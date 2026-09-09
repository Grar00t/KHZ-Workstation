using System.Windows;
using Majorsilence.Reporting.Rdl;

namespace KHZ.Reporting.Spike;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        RdlEngineConfig.RdlEngineConfigInit();
        base.OnStartup(e);
    }
}
