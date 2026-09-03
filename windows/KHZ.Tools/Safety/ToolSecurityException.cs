using System;

namespace KHZ.Tools.Safety;

/// <summary>
/// Raised when a tool request violates a KHZ boundary (path escape, reparse
/// traversal, internal metadata access, blocked command).
/// </summary>
/// <remarks>
/// This is deliberately a distinct type: hosts must be able to distinguish a
/// boundary violation from an ordinary I/O failure, because the two require
/// different audit results.
/// </remarks>
public sealed class ToolSecurityException : Exception
{
    public ToolSecurityException(string code, string message)
        : base(message)
        => Code = code;

    /// <summary>Stable machine-readable reason code.</summary>
    public string Code { get; }
}
