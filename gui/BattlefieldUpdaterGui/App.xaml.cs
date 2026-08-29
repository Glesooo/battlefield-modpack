using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace BattlefieldUpdaterGui;

public partial class App : Application
{
    // Without this, any unhandled exception silently kills the whole process - a WPF app has no
    // console, so the player just sees the window vanish with zero information. Log it and show
    // it instead, so "the updater just crashes" turns into an actual bug report.
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BattlefieldUpdater", "crash.log");

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportCrash(args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportCrash(e.Exception);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) ReportCrash(ex);
    }

    private static void ReportCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* best-effort logging - never let the crash reporter itself crash the app */ }

        MessageBox.Show(
            $"Апдейтер столкнулся с неожиданной ошибкой:\n\n{ex.Message}\n\nПодробности сохранены в:\n{CrashLogPath}",
            "M.A.C.E Updater - ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

