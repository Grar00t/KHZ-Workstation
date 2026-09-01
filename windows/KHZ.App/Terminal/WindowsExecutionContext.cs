using System.Security.Principal;

namespace KHZ.App.Terminal;

internal static class WindowsExecutionContext
{
    public static bool IsElevated()
    {
        using var identity =
            WindowsIdentity.GetCurrent();

        var principal =
            new WindowsPrincipal(
                identity);

        return principal.IsInRole(
            WindowsBuiltInRole.Administrator);
    }
}
