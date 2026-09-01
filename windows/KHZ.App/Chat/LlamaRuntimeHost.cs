using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KHZ.App.Chat;

internal sealed class LlamaRuntimeHost : IAsyncDisposable
{
    private const int MaxLogCharacters = 128 * 1024;
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly object _sync = new();
    private readonly StringBuilder _log = new();
    private Process? _process;
    private Uri? _endpoint;
    private LocalAiSettings? _settings;
    private LocalAiRuntimeSnapshot _snapshot =
        new(
            LocalAiRuntimeStatus.Stopped,
            Endpoint: null,
            ModelLabel: null,
            ProcessId: null,
            Detail: "Stopped");

    internal event EventHandler<LocalAiRuntimeSnapshot>? StateChanged;

    internal LocalAiRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return _snapshot;
        }
    }

    internal string RecentLog
    {
        get
        {
            lock (_sync)
                return _log.ToString();
        }
    }

    internal async Task<Uri> EnsureStartedAsync(
        LocalAiSettings requestedSettings,
        CancellationToken cancellationToken = default)
    {
        var settings = requestedSettings
            .ResolveEffective()
            .ValidateForUse();

        Process? existing;
        Uri? existingEndpoint;
        LocalAiSettings? existingSettings;

        lock (_sync)
        {
            existing = _process;
            existingEndpoint = _endpoint;
            existingSettings = _settings;
        }

        if (existing is not null
            && !existing.HasExited
            && existingEndpoint is not null
            && Equals(existingSettings, settings)
            && await IsHealthyAsync(
                existingEndpoint,
                cancellationToken))
        {
            return existingEndpoint;
        }

        await StopAsync();

        var port = ReserveLoopbackPort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/");

        SetSnapshot(
            new LocalAiRuntimeSnapshot(
                LocalAiRuntimeStatus.Starting,
                endpoint.ToString(),
                settings.ModelLabel,
                ProcessId: null,
                Detail: "Starting local model runtime"));

        var startInfo = new ProcessStartInfo
        {
            FileName = settings.RuntimeExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory =
                Path.GetDirectoryName(settings.RuntimeExecutable)
                ?? AppContext.BaseDirectory
        };

        Add(startInfo, "--host", "127.0.0.1");
        Add(startInfo, "--port", port.ToString());
        Add(startInfo, "--model", settings.ModelPath);
        Add(startInfo, "--ctx-size", settings.ContextSize.ToString());
        Add(startInfo, "--n-gpu-layers", settings.GpuLayers);
        startInfo.ArgumentList.Add("--offline");
        startInfo.ArgumentList.Add("--jinja");
        startInfo.ArgumentList.Add("--log-colors");
        startInfo.ArgumentList.Add("off");

        if (!string.IsNullOrWhiteSpace(settings.AdapterPath))
            Add(startInfo, "--lora", settings.AdapterPath!);

        if (!string.IsNullOrWhiteSpace(settings.ChatTemplatePath))
            Add(startInfo, "--chat-template-file", settings.ChatTemplatePath!);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        process.Exited += (_, _) => HandleProcessExit(process, settings.ModelLabel);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "llama-server could not be started.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            process.Dispose();
            SetSnapshot(
                new LocalAiRuntimeSnapshot(
                    LocalAiRuntimeStatus.Failed,
                    Endpoint: null,
                    settings.ModelLabel,
                    ProcessId: null,
                    Detail: "Local model runtime failed to start"));
            throw;
        }

        lock (_sync)
        {
            _process = process;
            _endpoint = endpoint;
            _settings = settings;
        }

        SetSnapshot(
            new LocalAiRuntimeSnapshot(
                LocalAiRuntimeStatus.Starting,
                endpoint.ToString(),
                settings.ModelLabel,
                process.Id,
                "Loading model"));

        var deadline =
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(4);

        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        "llama-server exited while loading the model. "
                        + TailLog());
                }

                if (await IsHealthyAsync(endpoint, cancellationToken))
                {
                    SetSnapshot(
                        new LocalAiRuntimeSnapshot(
                            LocalAiRuntimeStatus.Ready,
                            endpoint.ToString(),
                            settings.ModelLabel,
                            process.Id,
                            "Ready · loopback only"));
                    return endpoint;
                }

                await Task.Delay(500, cancellationToken);
            }

            throw new TimeoutException(
                "Timed out waiting for llama-server to become ready. "
                + TailLog());
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    internal async Task StopAsync()
    {
        Process? process;

        lock (_sync)
        {
            process = _process;
            _process = null;
            _endpoint = null;
            _settings = null;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            try
            {
                if (!process.HasExited)
                    await process.WaitForExitAsync();
            }
            catch
            {
            }

            process.Dispose();
        }

        SetSnapshot(
            new LocalAiRuntimeSnapshot(
                LocalAiRuntimeStatus.Stopped,
                Endpoint: null,
                ModelLabel: null,
                ProcessId: null,
                Detail: "Stopped"));
    }

    private void HandleProcessExit(
        Process process,
        string modelLabel)
    {
        int? processId = null;
        int? exitCode = null;
        try
        {
            processId = process.Id;
            exitCode = process.ExitCode;
        }
        catch
        {
        }

        lock (_sync)
        {
            if (!ReferenceEquals(_process, process))
                return;

            _process = null;
            _endpoint = null;
            _settings = null;
        }

        SetSnapshot(
            new LocalAiRuntimeSnapshot(
                LocalAiRuntimeStatus.Failed,
                Endpoint: null,
                modelLabel,
                processId,
                $"Runtime exited with code {exitCode?.ToString() ?? "unknown"}"));
    }

    private async Task<bool> IsHealthyAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(
                new Uri(endpoint, "health"),
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        lock (_sync)
        {
            _log.AppendLine(line);
            if (_log.Length > MaxLogCharacters)
            {
                _log.Remove(
                    0,
                    _log.Length - MaxLogCharacters);
            }
        }
    }

    private string TailLog()
    {
        lock (_sync)
        {
            if (_log.Length <= 2000)
                return _log.ToString().Trim();
            return _log.ToString(
                    _log.Length - 2000,
                    2000)
                .Trim();
        }
    }

    private void SetSnapshot(LocalAiRuntimeSnapshot snapshot)
    {
        lock (_sync)
            _snapshot = snapshot;

        StateChanged?.Invoke(this, snapshot);
    }

    private static void Add(
        ProcessStartInfo startInfo,
        string name,
        string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static int ReserveLoopbackPort()
    {
        using var listener =
            new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
