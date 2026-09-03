using KHZ.App.Workspaces;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KHZ.App.Chat;

internal sealed record LocalAiSettings(
    string ModelLabel,
    string RuntimeExecutable,
    string ModelPath,
    string? AdapterPath,
    string? ChatTemplatePath,
    int ContextSize,
    string GpuLayers,
    bool ToolsEnabled,
    bool HideReasoning)
{
    internal const int DefaultContextSize = 8192;

    internal static LocalAiSettings Default()
        => new(
            ModelLabel: "OLMo 3",
            RuntimeExecutable: string.Empty,
            ModelPath: string.Empty,
            AdapterPath: null,
            ChatTemplatePath: null,
            ContextSize: DefaultContextSize,
            GpuLayers: "auto",
            ToolsEnabled: true,
            HideReasoning: true);

    internal LocalAiSettings ResolveEffective()
    {
        var runtime = FirstExistingFile(
            RuntimeExecutable,
            Environment.GetEnvironmentVariable("KHZ_LLAMA_SERVER"),
            Path.Combine(
                AppContext.BaseDirectory,
                "Runtime",
                "llama",
                "llama-server.exe"),
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "KHZ",
                "runtime",
                "llama",
                "llama-server.exe"));

        var model = FirstExistingFile(
            ModelPath,
            Environment.GetEnvironmentVariable("KHZ_MODEL_PATH"));

        var adapter = FirstExistingFile(
            AdapterPath,
            Environment.GetEnvironmentVariable("KHZ_ADAPTER_PATH"));

        var template = FirstExistingFile(
            ChatTemplatePath,
            Environment.GetEnvironmentVariable("KHZ_CHAT_TEMPLATE"));

        return this with
        {
            RuntimeExecutable = runtime ?? RuntimeExecutable,
            ModelPath = model ?? ModelPath,
            AdapterPath = adapter ?? AdapterPath,
            ChatTemplatePath = template ?? ChatTemplatePath
        };
    }

    internal LocalAiSettings ValidateForUse()
    {
        var label = (ModelLabel ?? string.Empty).Trim();
        if (label.Length is < 1 or > 120)
        {
            throw new ArgumentException(
                "Model label must contain 1 to 120 characters.",
                nameof(ModelLabel));
        }

        var runtime = RequireExistingFile(
            RuntimeExecutable,
            "llama-server executable");

        var model = RequireExistingFile(
            ModelPath,
            "GGUF model");

        var adapter = OptionalExistingFile(
            AdapterPath,
            "LoRA adapter");

        var template = OptionalExistingFile(
            ChatTemplatePath,
            "chat template");

        if (ContextSize is < 512 or > 131072)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ContextSize),
                "Context size must be between 512 and 131072 tokens.");
        }

        var gpuLayers = NormalizeGpuLayers(GpuLayers);

        return this with
        {
            ModelLabel = label,
            RuntimeExecutable = runtime,
            ModelPath = model,
            AdapterPath = adapter,
            ChatTemplatePath = template,
            GpuLayers = gpuLayers
        };
    }

    private static string? FirstExistingFile(
        params string?[] candidates)
    {
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
            catch
            {
            }
        }

        return null;
    }

    private static string RequireExistingFile(
        string value,
        string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{label} path is required.");

        var full = Path.GetFullPath(value.Trim());
        if (!File.Exists(full))
            throw new FileNotFoundException($"{label} was not found.", full);
        return full;
    }

    private static string? OptionalExistingFile(
        string? value,
        string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var full = Path.GetFullPath(value.Trim());
        if (!File.Exists(full))
            throw new FileNotFoundException($"{label} was not found.", full);
        return full;
    }

    private static string NormalizeGpuLayers(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (value is "auto" or "all")
            return value;

        if (int.TryParse(value, out var count)
            && count is >= 0 and <= 1000)
        {
            return count.ToString();
        }

        throw new ArgumentException(
            "GPU layers must be 'auto', 'all', or an integer from 0 to 1000.",
            nameof(value));
    }
}

internal sealed record ChatConversation(
    string ConversationId,
    string ContextId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record ChatMessage(
    long Sequence,
    string ConversationId,
    string Role,
    string Content,
    string? ToolName,
    string? ToolCallId,
    string? ToolArgumentsJson,
    DateTimeOffset CreatedAt);

internal sealed record ChatContext(
    string ContextId,
    string DisplayName,
    string RootPath,
    string? WorkspaceId)
{
    internal static ChatContext Create(
        string currentDirectory,
        WorkspaceContext? workspace)
    {
        if (workspace is not null)
        {
            return new ChatContext(
                ContextId: "workspace:" + workspace.Info.WorkspaceId,
                DisplayName: "Workspace: " + workspace.Info.Name,
                RootPath: workspace.Info.Root,
                WorkspaceId: workspace.Info.WorkspaceId);
        }

        var root = Path.GetFullPath(currentDirectory);
        var normalized =
            root.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();

        var digest = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();

        return new ChatContext(
            ContextId: "folder:" + digest,
            DisplayName: "Folder mode",
            RootPath: root,
            WorkspaceId: null);
    }
}

internal sealed record ChatToolDefinition(
    string Name,
    string Description,
    string ParametersJson,
    bool RequiresConfirmation);

internal sealed record ChatToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

internal sealed record ChatCompletionResult(
    string Content,
    ChatToolCall? ToolCall,
    string FinishReason);

internal enum LocalAiRuntimeStatus
{
    Stopped,
    Starting,
    Ready,
    Failed
}

internal sealed record LocalAiRuntimeSnapshot(
    LocalAiRuntimeStatus Status,
    string? Endpoint,
    string? ModelLabel,
    int? ProcessId,
    string Detail);
