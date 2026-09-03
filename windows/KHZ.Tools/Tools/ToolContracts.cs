using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KHZ.Tools.Safety;

namespace KHZ.Tools.Tools;

/// <summary>Static description of a tool, as advertised to a model or MCP host.</summary>
/// <param name="Name">Stable tool identifier.</param>
/// <param name="Title">Short human-readable title.</param>
/// <param name="Description">Behaviour, limits, and failure modes.</param>
/// <param name="ParametersJson">JSON Schema for the arguments object.</param>
/// <param name="Risk">Risk class.</param>
/// <param name="RequiresConfirmation">Human authorisation required per call.</param>
public sealed record ToolDescriptor(
    string Name,
    string Title,
    string Description,
    string ParametersJson,
    ToolRisk Risk,
    bool RequiresConfirmation)
{
    public bool ReadOnly => Risk == ToolRisk.Read;
}

/// <summary>Everything a tool is allowed to touch during one invocation.</summary>
/// <param name="ContextId">Workspace or folder identity, for audit correlation.</param>
/// <param name="RootPath">Canonical workspace root. The hard boundary.</param>
/// <param name="Confirmations">Authorisation gate.</param>
/// <param name="Audit">Append-only activity sink.</param>
/// <param name="Shell">Shell execution backend.</param>
public sealed record ToolContext(
    string ContextId,
    string RootPath,
    IConfirmationBroker Confirmations,
    IToolAuditSink Audit,
    IShellRunner Shell)
{
    public static ToolContext ForRoot(
        string root,
        IConfirmationBroker? confirmations = null,
        IToolAuditSink? audit = null,
        IShellRunner? shell = null)
    {
        var canonical = WorkspaceGuard.ResolveRoot(root);

        return new ToolContext(
            ContextId: "root:" + Hashes.Sha256(Hashes.StrictUtf8.GetBytes(canonical.ToUpperInvariant()))[..16],
            RootPath: canonical,
            Confirmations: confirmations ?? DenyAllConfirmationBroker.Instance,
            Audit: audit ?? NullToolAuditSink.Instance,
            Shell: shell ?? new PowerShellRunner());
    }

    /// <summary>Resolves a model-supplied relative path inside the boundary.</summary>
    public string Resolve(string? relativePath)
        => WorkspaceGuard.Resolve(RootPath, relativePath);

    /// <summary>Workspace-relative form, safe to echo back to the model.</summary>
    public string Relative(string absolutePath)
        => WorkspaceGuard.Relative(RootPath, absolutePath);
}

/// <summary>A single executable capability.</summary>
public interface IKhzTool
{
    ToolDescriptor Descriptor { get; }

    /// <summary>
    /// Executes the tool and returns a JSON document. Implementations throw
    /// <see cref="ToolFailureException"/> for expected, model-correctable
    /// failures and <see cref="ToolSecurityException"/> for boundary violations.
    /// </summary>
    Task<JsonNodeResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Serialised tool output plus the audit facts the router should record.</summary>
/// <param name="Json">Result payload returned to the model.</param>
/// <param name="Target">Audit target (relative path, or a redaction marker).</param>
/// <param name="AuditDetails">Non-content facts: lengths, hashes, decisions.</param>
public sealed record JsonNodeResult(
    string Json,
    string Target,
    object? AuditDetails = null);

/// <summary>An expected failure the model can reasonably correct and retry.</summary>
public sealed class ToolFailureException : Exception
{
    public ToolFailureException(string code, string message)
        : base(message)
        => Code = code;

    public string Code { get; }
}

/// <summary>Raised when the human declines a confirmation prompt.</summary>
public sealed class ToolDeniedException : Exception
{
    public ToolDeniedException(string message = "The action was declined by the user.")
        : base(message)
    {
    }
}

/// <summary>Argument reading helpers with explicit, model-friendly errors.</summary>
public static class ToolArgs
{
    public static string RequireString(JsonElement args, string name)
    {
        var value = OptionalString(args, name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ToolFailureException(
                "missing_argument",
                "Argument '" + name + "' is required and must be a non-empty string.");
        }

        return value;
    }

    public static string? OptionalString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new ToolFailureException(
                "invalid_argument",
                "Argument '" + name + "' must be a string.")
        };
    }

    public static int OptionalInt(JsonElement args, string name, int fallback, int min, int max)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        var parsed = property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var number) => number,
            _ => throw new ToolFailureException(
                "invalid_argument",
                "Argument '" + name + "' must be an integer.")
        };

        return Math.Clamp(parsed, min, max);
    }

    public static bool OptionalBool(JsonElement args, string name, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => fallback,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => fallback
        };
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Serialises a result object without escaping non-ASCII text.</summary>
    public static string Serialize(object value)
        => JsonSerializer.Serialize(value, Json);
}
