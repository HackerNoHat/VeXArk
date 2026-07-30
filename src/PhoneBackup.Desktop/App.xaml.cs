using System.Windows;

namespace PhoneBackup.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DesktopDiagnostics.Log(
            "info",
            "desktop",
            "startup",
            $"{AppRuntimeProfile.ProductName} is starting",
            fields: new Dictionary<string, object?>
            {
                ["channel"] = AppRuntimeProfile.Channel,
                ["version"] = AppRuntimeProfile.VersionName,
                ["buildId"] = AppRuntimeProfile.BuildId,
                ["agentPackage"] = AppRuntimeProfile.AgentPackage,
                ["agentPort"] = AppRuntimeProfile.AgentPort
            });
        var preferences = DesktopSettingsStore.Load();
        LocalizationManager.Initialize(preferences.Language);
        ThemeManager.Initialize(preferences.Theme);
        DispatcherUnhandledException += (_, args) =>
        {
            DesktopDiagnostics.Log(
                "error",
                "desktop",
                "unhandled_exception",
                "Unhandled dispatcher exception",
                error: args.Exception);
            MessageBox.Show(
                LocalizationManager.T(args.Exception.Message),
                LocalizationManager.T("VeXArk — ошибка"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        RuntimeBootstrap.EnsureExtracted();
        RestoreCoordinator.CleanupStaleStaging();
        base.OnStartup(e);
    }
}
