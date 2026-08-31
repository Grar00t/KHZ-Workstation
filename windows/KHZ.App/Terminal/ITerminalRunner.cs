using System.Threading;
using System.Threading.Tasks;

namespace KHZ.App.Terminal;

internal interface ITerminalRunner
{
    Task<TerminalExecutionResult> ExecuteAsync(
        TerminalExecutionRequest request,
        CancellationToken cancellationToken = default);
}
