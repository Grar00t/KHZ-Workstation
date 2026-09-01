using KHZ.App.Chat;

namespace KHZ.App.Views;

public partial class ChatView
{
    internal void Shutdown()
    {
        if (_disposed)
            return;

        _disposed = true;
        LocalAiSessionGate.Shared.Changed -= SessionGate_Changed;
        _runtime.StateChanged -= Runtime_StateChanged;
        _requestCancellation?.Cancel();

        try
        {
            _runtime.DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Window shutdown continues even if the child process already exited.
        }

        _client.Dispose();
    }
}
