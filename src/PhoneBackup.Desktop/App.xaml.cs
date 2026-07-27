using System.Windows;

namespace PhoneBackup.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var preferences = DesktopSettingsStore.Load();
        LocalizationManager.Initialize(preferences.Language);
        ThemeManager.Initialize(preferences.Theme);
        DispatcherUnhandledException += (_, args) =>
        {
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
