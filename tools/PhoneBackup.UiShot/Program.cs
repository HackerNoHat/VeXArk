using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhoneBackup.Desktop;

internal static class Program
{
    private static int Main(string[] args)
    {
        var result = 1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = CaptureAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
        return result;
    }

    private static async Task<int> CaptureAsync(string[] args)
    {
        if (args.Length is < 1 or > 4)
        {
            Console.Error.WriteLine(
                "Usage: PhoneBackup.UiShot <output.png> [page] [system|light|dark|oled] [en|ru]");
            return 2;
        }

        var app = new App();
        app.InitializeComponent();
        LocalizationManager.Initialize(args.Length >= 4 ? args[3] : "en");
        var window = new MainWindow
        {
            Width = 1280,
            Height = 820,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20_000,
            Top = -20_000,
            ShowInTaskbar = false
        };
        window.Show();
        ThemeManager.Initialize(args.Length >= 3 ? args[2] : "dark");
        LocalizationManager.Apply(window);
        if (args.Length >= 2 && window.DataContext is MainViewModel viewModel)
        {
            var command = args[1] switch
            {
                "backup" => viewModel.ShowBackupCommand,
                "media" => viewModel.ShowMediaCommand,
                "restore" => viewModel.ShowRestoreCommand,
                "history" => viewModel.ShowHistoryCommand,
                "settings" => viewModel.ShowSettingsCommand,
                _ => viewModel.ShowDevicesCommand
            };
            command.Execute(null);
        }
        PumpDispatcher(TimeSpan.FromSeconds(2));
        ThemeManager.Apply(args.Length >= 3 ? args[2] : "dark");
        LocalizationManager.ApplyLanguage(args.Length >= 4 ? args[3] : "en");
        if (window.DataContext is MainViewModel localizedViewModel)
            localizedViewModel.RefreshLocalization();
        PumpDispatcher(TimeSpan.FromMilliseconds(150));
        window.UpdateLayout();

        var scale = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scale.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scale.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            scale.PixelsPerInchX,
            scale.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var output = new FileStream(
                         Path.GetFullPath(args[0]),
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None))
        {
            encoder.Save(output);
        }
        window.Close();
        return 0;
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher.CurrentDispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
