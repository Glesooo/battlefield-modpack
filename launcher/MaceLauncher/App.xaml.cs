using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaceLauncher.Core;

namespace MaceLauncher;

public partial class App : Application
{
    private Mutex? single;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        var screenshot = Array.IndexOf(e.Args, "--screenshot");
        if (screenshot >= 0 && screenshot + 1 < e.Args.Length)
        {
            SaveScreenshot(e.Args, e.Args[screenshot + 1]);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--headless"))
        {
            Shutdown(await Cli.RunAsync(e.Args));
            return;
        }
        single = new Mutex(true, @"Local\MACE-Launcher", out var first);
        if (!first)
        {
            MessageBox.Show("Лаунчер M.A.C.E уже запущен.", "M.A.C.E", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        new MainWindow(Cli.Paths(e.Args), Cli.Sources(e.Args)).Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Info("НЕОБРАБОТАННАЯ ОШИБКА: " + e.Exception);
        MessageBox.Show(e.Exception.Message + Environment.NewLine + "Подробности в launcher.log", "M.A.C.E",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void SaveScreenshot(string[] args, string file)
    {
        var window = new MainWindow(Cli.Paths(args), Cli.Sources(args));
        var content = (FrameworkElement)window.Content;
        var size = new Size(window.Width, window.Height);
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
        window.Close();
    }
}
