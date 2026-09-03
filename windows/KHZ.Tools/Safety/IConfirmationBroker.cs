using System.Threading;
using System.Threading.Tasks;

namespace KHZ.Tools.Safety;

/// <summary>Risk class of a tool, used to pick the confirmation path.</summary>
public enum ToolRisk
{
    /// <summary>Observation only. No state change.</summary>
    Read = 0,

    /// <summary>Mutates workspace content. Requires confirmation.</summary>
    Write = 1,

    /// <summary>Executes arbitrary code. Requires per-call confirmation.</summary>
    Execute = 2
}

/// <summary>A concrete action awaiting human authorisation.</summary>
/// <param name="ToolName">Tool requesting authorisation.</param>
/// <param name="Risk">Risk class.</param>
/// <param name="Title">Short headline for the prompt.</param>
/// <param name="Target">Workspace-relative target, or the working directory.</param>
/// <param name="Summary">One-line description of the effect.</param>
/// <param name="Before">Exact current content being replaced, when applicable.</param>
/// <param name="After">Exact proposed content, when applicable.</param>
/// <param name="Warnings">Risk flags the host must surface verbatim.</param>
public sealed record ConfirmationRequest(
    string ToolName,
    ToolRisk Risk,
    string Title,
    string Target,
    string Summary,
    string? Before = null,
    string? After = null,
    string[]? Warnings = null);

/// <summary>
/// Transport-neutral authorisation gate. A model tool call is a *request*; only
/// a broker returning <c>true</c> constitutes authority to mutate or execute.
/// </summary>
public interface IConfirmationBroker
{
    Task<bool> ConfirmAsync(
        ConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default for any host that cannot render a prompt. Denying is the only safe
/// behaviour: an unattended process must never self-authorise a mutation.
/// </summary>
public sealed class DenyAllConfirmationBroker : IConfirmationBroker
{
    public static readonly DenyAllConfirmationBroker Instance = new();

    public Task<bool> ConfirmAsync(
        ConfirmationRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>
/// Blanket pre-authorisation. Only legitimate when a human has explicitly opted
/// in for the current process (for example an MCP host started with
/// <c>--allow-writes</c>), and never a default.
/// </summary>
public sealed class PreAuthorizedConfirmationBroker : IConfirmationBroker
{
    private readonly ToolRisk _maxRisk;

    public PreAuthorizedConfirmationBroker(ToolRisk maxRisk)
        => _maxRisk = maxRisk;

    public Task<bool> ConfirmAsync(
        ConfirmationRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(request.Risk <= _maxRisk);
}
