using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KHZ.Tools.Safety;
using KHZ.Tools.Tools;

namespace KHZ.Tools.Office;

/// <summary>
/// Converts an Office document to PDF using a headless LibreOffice process.
/// </summary>
/// <remarks>
/// This is the counterpart of the Python OnlyOffice engine's unimplemented
/// <c>convert_to_pdf</c>. LibreOffice is used because it is the only engine in
/// the KHZ engine registry with a documented, non-interactive conversion CLI;
/// OnlyOffice Desktop Editors exposes no supported headless conversion entry
/// point, which is exactly why the Python path raised instead of guessing.
/// <para>
/// The converter is an external dependency: when <c>soffice.exe</c> cannot be
/// resolved the tool fails with a precise, actionable error rather than
/// reporting a silent no-op.
/// </para>
/// </remarks>
public sealed class ConvertToPdfTool : IKhzTool
{
    /// <summary>Explicit override for the LibreOffice executable path.</summary>
    public const string ExecutableVariable = "KHZ_SOFFICE";

    public ToolDescriptor Descriptor { get; } = new(
        Name: "office_convert_to_pdf",
        Title: "Convert document to PDF",
        Description: "Converts a .docx, .xlsx, or .pptx file in the workspace to PDF using a "
                     + "headless LibreOffice process. Requires LibreOffice on the machine "
                     + "(override the path with KHZ_SOFFICE). The PDF is written next to the "
                     + "source or into an optional workspace-relative output folder. Requires "
                     + "user confirmation.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative .docx, .xlsx, or .pptx path." },
            "output_directory": { "type": "string", "description": "Optional workspace-relative output folder." },
            "timeout_seconds": { "type": "integer", "description": "10 to 600. Defaults to 180." }
          },
          "required": ["path"],
          "additionalProperties": false
        }
        """,
        Risk: ToolRisk.Write,
        RequiresConfirmation: true);

    public async Task<JsonNodeResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var source = context.Resolve(ToolArgs.RequireString(arguments, "path"));
        var extension = Path.GetExtension(source).ToLowerInvariant();

        if (extension is not (".docx" or ".xlsx" or ".pptx"))
        {
            throw new ToolFailureException(
                "unsupported_file_type",
                "Only .docx, .xlsx, and .pptx are supported. Received: " + extension);
        }

        if (!File.Exists(source))
        {
            throw new ToolFailureException(
                "file_not_found",
                "File not found: " + context.Relative(source));
        }

        var outputDirectory = context.Resolve(
            ToolArgs.OptionalString(arguments, "output_directory")
            ?? Path.GetDirectoryName(context.Relative(source))
            ?? ".");

        if (!Directory.Exists(outputDirectory))
        {
            throw new ToolFailureException(
                "directory_not_found",
                "Output directory not found: " + context.Relative(outputDirectory));
        }

        var timeoutSeconds = ToolArgs.OptionalInt(arguments, "timeout_seconds", 180, 10, 600);
        var executable = ResolveExecutable();

        if (executable is null)
        {
            throw new ToolFailureException(
                "converter_not_found",
                "LibreOffice (soffice.exe) was not found. Install LibreOffice or set "
                + ExecutableVariable + " to its full path. OnlyOffice Desktop Editors provides "
                + "no supported headless conversion entry point and cannot be substituted.");
        }

        var target = Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(source) + ".pdf");

        var overwrite = File.Exists(target);

        await ToolRouter.RequireConfirmationAsync(
            context,
            new ConfirmationRequest(
                ToolName: Descriptor.Name,
                Risk: ToolRisk.Write,
                Title: "Convert a document to PDF",
                Target: context.Relative(target),
                Summary: "Convert " + context.Relative(source) + " to PDF using headless "
                         + "LibreOffice.",
                Warnings: overwrite
                    ? ["An existing PDF at the destination will be overwritten."]
                    : null),
            cancellationToken).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = outputDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--norestore");
        startInfo.ArgumentList.Add("--invisible");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add("pdf");
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add(source);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token,
            cancellationToken);

        string stderr;

        try
        {
            stderr = await process.StandardError.ReadToEndAsync(linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            throw new ToolFailureException(
                "conversion_timeout",
                "Conversion exceeded " + timeoutSeconds + " seconds and was terminated. A "
                + "LibreOffice instance already running in the user session can block headless "
                + "conversion; close it and retry.");
        }

        if (process.ExitCode != 0 || !File.Exists(target))
        {
            throw new ToolFailureException(
                "conversion_failed",
                "Conversion failed with exit code " + process.ExitCode + ". "
                + OfficeGuard.Cap(stderr.Trim(), 2000));
        }

        var sha = Hashes.Sha256OfFile(target);
        var length = new FileInfo(target).Length;

        var json = ToolArgs.Serialize(new
        {
            source = context.Relative(source),
            output = context.Relative(target),
            status = "converted",
            overwritten = overwrite,
            sizeBytes = length,
            sha256 = sha
        });

        return new JsonNodeResult(
            json,
            Target: "redacted",
            AuditDetails: new
            {
                pathCaptured = false,
                userConfirmed = true,
                aiUsed = true,
                converter = "libreoffice-headless",
                sizeBytes = length,
                afterSha256 = sha
            });
    }

    private static string? ResolveExecutable()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable(ExecutableVariable),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "LibreOffice", "program", "soffice.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "LibreOffice", "program", "soffice.exe"),
            "/usr/bin/soffice",
            "/usr/bin/libreoffice"
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                var full = Path.GetFullPath(candidate.Trim());

                if (File.Exists(full))
                    return full;
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
