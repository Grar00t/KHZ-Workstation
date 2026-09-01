using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KHZ.Mcp.Server.JsonRpc;
using KHZ.Tools;
using KHZ.Tools.Safety;
using KHZ.Tools.Tools;

namespace KHZ.Mcp.Server;

/// <summary>
/// Entry point for the local KHZ MCP server.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// khz-mcp-server --root &lt;workspace path&gt; [--allow-writes] [--read-only]
/// </code>
/// <para>
/// The write posture is the important design decision. An MCP server started by
/// a host has no user interface, so it cannot obtain informed consent for a
/// mutation. Rather than silently self-authorising, mutating tools are refused
/// unless a human explicitly launched the process with <c>--allow-writes</c>,
/// which is a one-time, auditable act recorded in the host's server
/// configuration file.
/// </para>
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var transport = new JsonRpcTransport();

        try
        {
            var readOnly = args.Contains("--read-only", StringComparer.OrdinalIgnoreCase);
            var allowWrites = !readOnly
                              && args.Contains("--allow-writes", StringComparer.OrdinalIgnoreCase);

            var root = ResolveRoot(ReadOption(args, "--root"));

            var context = new ToolContext(
                ContextId: "mcp:" + Hashes.Sha256(
                    Hashes.StrictUtf8.GetBytes(root.ToUpperInvariant()))[..16],
                RootPath: root,
                Confirmations: allowWrites
                    ? new PreAuthorizedConfirmationBroker(ToolRisk.Execute)
                    : DenyAllConfirmationBroker.Instance,
                Audit: new DelegateToolAuditSink(line => transport.Log("audit " + line)),
                Shell: new PowerShellRunner());

            var router = readOnly
                ? KhzToolCatalog.CreateReadOnlyRouter()
                : KhzToolCatalog.CreateRouter();

            transport.Log(
                "starting: root=" + root
                + " tools=" + router.Descriptors.Count
                + " writes=" + (allowWrites ? "allowed" : "denied")
                + " protocol=" + McpServer.ProtocolVersion);

            using var shutdown = new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };

            var server = new McpServer(transport, router, context, allowWrites);
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);

            return 0;
        }
        catch (DirectoryNotFoundException exception)
        {
            transport.Log("fatal: " + exception.Message);
            return 2;
        }
        catch (ArgumentException exception)
        {
            transport.Log("fatal: " + exception.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    private static string ResolveRoot(string? requested)
    {
        var candidate = requested
                        ?? Environment.GetEnvironmentVariable("KHZ_ROOT")
                        ?? Directory.GetCurrentDirectory();

        return WorkspaceGuard.ResolveRoot(candidate);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException(
                    "Option " + name + " requires a value.");
            }

            return args[index + 1];
        }

        return null;
    }
}
