using Microsoft.UI.Xaml;
using System.IO;

namespace OpenClaw.SetupEngine.UI;

public partial class App : Application
{
    public static SetupWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new SetupWindow();
            MainWindow.BringToFrontForSetupLaunch();
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OpenClawTray");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "setup-ui-crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex}\n");
            }
            catch
            {
            }

            throw;
        }
    }
}
