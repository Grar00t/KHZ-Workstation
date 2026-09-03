using System;

namespace KHZ.Tools.Safety;

/// <summary>
/// Append-only activity recorder. Shaped to match the existing KHZ activity
/// store so the WPF host can adapt it without a schema change.
/// </summary>
/// <remarks>
/// Audit records deliberately carry lengths, hashes, and decisions rather than
/// payloads: the log proves that an action happened and was authorised without
/// becoming a second copy of the user's content or of raw command text.
/// </remarks>
public interface IToolAuditSink
{
    void Record(
        string category,
        string action,
        string target,
        string result,
        object? details = null);
}

/// <summary>Discards events. For tests and for hosts with no audit store.</summary>
public sealed class NullToolAuditSink : IToolAuditSink
{
    public static readonly NullToolAuditSink Instance = new();

    public void Record(
        string category,
        string action,
        string target,
        string result,
        object? details = null)
    {
    }
}

/// <summary>Writes one JSON line per event to a delegate (stderr, file, ...).</summary>
public sealed class DelegateToolAuditSink : IToolAuditSink
{
    private readonly Action<string> _write;

    public DelegateToolAuditSink(Action<string> write)
        => _write = write;

    public void Record(
        string category,
        string action,
        string target,
        string result,
        object? details = null)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.UtcNow.ToString("O"),
            category,
            action,
            target,
            result,
            details
        });

        _write(payload);
    }
}
