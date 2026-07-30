using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhoneBackup.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = AppRuntimeProfile.ProductName;
        DataContext = new MainViewModel();
        Loaded += async (_, _) =>
        {
            LocalizationManager.Apply(this);
            await ((MainViewModel)DataContext).RefreshAsync();
        };
    }

    private void RepositoryPassword_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is PasswordBox passwordBox)
            viewModel.RepositoryPassword = passwordBox.Password;
    }

    private void NewRepositoryPassword_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is PasswordBox passwordBox)
            viewModel.NewRepositoryPassword = passwordBox.Password;
    }

    private void RecoveryInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is PasswordBox passwordBox)
            viewModel.RecoveryInput = passwordBox.Password;
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
