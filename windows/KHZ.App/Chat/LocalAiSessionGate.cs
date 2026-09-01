using System;

namespace KHZ.App.Chat;

internal sealed class LocalAiSessionGate
{
    internal static LocalAiSessionGate Shared { get; } = new();

    public bool IsEnabled { get; private set; }

    public event EventHandler? Changed;

    public void Enable()
    {
        if (IsEnabled)
            return;

        IsEnabled = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Disable()
    {
        if (!IsEnabled)
            return;

        IsEnabled = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
