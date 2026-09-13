using System.Windows;

namespace KHZ.AssetRegister;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.LoadAndApply();
        base.OnStartup(e);
    }
}
