using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CompletePage : Page
{
    private static readonly Regex s_urlRegex = new(@"https?://[^\s)]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private string? _logPath;
    private string? _gatewayUrl;
    private bool _success;
    private bool _autoLaunchTray;
    private bool _trayLaunchStarted;

    public CompletePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is CompletePageArgs args)
        {
            _logPath = args.LogPath;
            _gatewayUrl = args.GatewayUrl;

            if (args.Success)
            {
                _success = true;
                _autoLaunchTray = args.AutoLaunchTray;
                SuccessIcon.Visibility = Visibility.Visible;
                FailureIcon.Visibility = Visibility.Collapsed;
                TitleText.Text = "All set!";
                SubtitleText.Text = $"OpenClaw is ready. Opening Windows tray for {DisplayGatewayUrl(_gatewayUrl)}...";
                ErrorCard.Visibility = Visibility.Collapsed;
                LaunchButton.Content = "Close Setup";
                HelpLink.Visibility = Visibility.Collapsed;
            }
            else
            {
                _success = false;
                _autoLaunchTray = false;
                var errorMessage = args.ErrorMessage ?? "Unknown error";
                var helpUrl = ExtractHelpUrl(errorMessage);

                SuccessIcon.Visibility = Visibility.Collapsed;
                FailureIcon.Visibility = Visibility.Visible;
                TitleText.Text = "Setup failed";
                SubtitleText.Text = helpUrl is null
                    ? args.ErrorMessage ?? "An error occurred during setup"
                    : "Follow the steps below to resolve the setup issue and retry.";
                NodeModeBanner.Visibility = Visibility.Collapsed;
                StartupRow.Visibility = Visibility.Collapsed;
                LaunchButton.Content = "Close";

                // Show error card with details and log link
                ErrorCard.Visibility = Visibility.Visible;
                ErrorText.Text = errorMessage;
                if (helpUrl != null)
                {
                    HelpLink.Content = errorMessage.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                        ? "Update WSL →"
                        : "Open help link →";
                    HelpLink.NavigateUri = helpUrl;
                    HelpLink.Visibility = Visibility.Visible;
                }
                else
                {
                    HelpLink.Visibility = Visibility.Collapsed;
                }
                if (args.LogPath != null)
                {
                    var displayPath = LogFileLauncher.ResolveRealPath(args.LogPath);
                    ViewLogLink.Content = $"View full log → {displayPath}";
                    ToolTipService.SetToolTip(ViewLogLink, displayPath);
                    ViewLogLink.Visibility = Visibility.Visible;
                }
                else
                    ViewLogLink.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static Uri? ExtractHelpUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = s_urlRegex.Match(text);
        if (!match.Success)
            return null;

        return Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? uri : null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Style the Node Mode banner with amber/brown background
        var isDark = ActualTheme == ElementTheme.Dark;
        NodeModeBanner.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x4A, 0x3D, 0x10) // dark amber
            : Color.FromArgb(255, 0xF5, 0xE6, 0xB8)); // light amber

        // Default startup toggle to off (user can enable)
        StartupToggle.IsOn = false;

        if (_autoLaunchTray && !_trayLaunchStarted)
        {
            _trayLaunchStarted = true;
            _ = LaunchTrayAfterSuccessAsync();
        }
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        // Register startup if toggled on
        if (StartupToggle.Visibility == Visibility.Visible && StartupToggle.IsOn)
            RegisterStartup();

        if (_success && !_trayLaunchStarted)
            LaunchTray();
        App.MainWindow?.Close();
    }

    private async Task LaunchTrayAfterSuccessAsync()
    {
        await Task.Delay(900);
        try
        {
            LaunchTray();
            SubtitleText.Text = $"Windows tray opened and connecting to {DisplayGatewayUrl(_gatewayUrl)}.";
        }
        catch (Exception ex)
        {
            SubtitleText.Text = $"Setup complete, but Windows tray did not open automatically: {ex.Message}";
        }
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        LogFileLauncher.RevealInExplorer(_logPath);
    }

    internal static void LaunchTray()
    {
        // Kill any existing tray instances so fresh one picks up new gateway
        foreach (var proc in Process.GetProcessesByName("OpenClaw.Tray.WinUI"))
        {
            try { proc.Kill(); } catch { }
        }

        // Brief pause for process cleanup
        Thread.Sleep(1000);

        // Launch via protocol deep link — opens tray and navigates to chat
        try
        {
            Process.Start(new ProcessStartInfo("openclaw://chat") { UseShellExecute = true });
            return;
        }
        catch
        {
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray", "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenClawTray", "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "OpenClaw.Tray.WinUI.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.exe"),
        };

        var trayPath = candidates.FirstOrDefault(File.Exists);
        if (trayPath == null)
            throw new FileNotFoundException("OpenClaw.Tray.WinUI.exe was not found.");

        Process.Start(new ProcessStartInfo(trayPath, "openclaw://chat") { UseShellExecute = true });
    }

    private static void RegisterStartup()
    {
        try
        {
            var trayPath = TrayExecutableResolver.Resolve();
            if (trayPath == null) return;

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.SetValue("OpenClawTray", $"\"{Path.GetFullPath(trayPath)}\"");
        }
        catch { /* best effort */ }
    }

    private static string DisplayGatewayUrl(string? gatewayUrl)
        => string.IsNullOrWhiteSpace(gatewayUrl) ? "the local gateway" : gatewayUrl;
}
