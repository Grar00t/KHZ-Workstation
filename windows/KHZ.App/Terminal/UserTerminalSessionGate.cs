namespace KHZ.App.Terminal;

internal sealed class UserTerminalSessionGate
{
    public bool IsEnabled { get; private set; }

    public void Enable()
        => IsEnabled = true;

    public void Disable()
        => IsEnabled = false;
}
