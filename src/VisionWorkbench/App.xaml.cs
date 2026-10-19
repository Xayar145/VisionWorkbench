using System.Windows;
using VisionWorkbench.Services;

namespace VisionWorkbench;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--check"))
        {
            Shutdown(await SmokeChecks.Run(e.Args));
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
        if (e.Args.Contains("--ui-smoke")) Shutdown(await SmokeChecks.Run(e.Args));
    }
}
