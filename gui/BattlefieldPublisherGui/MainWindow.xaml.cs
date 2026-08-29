using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BattlefieldPublisherGui;

public partial class MainWindow : Window
{
    // Hardcoded on purpose - this tool only ever runs on Gleso's own machine against his one
    // repo, unlike BattlefieldUpdaterGui which has to adapt to any player's chosen folder.
    // Same "own local tool, own machine" reasoning publish.ps1 itself already uses for its
    // -InstancePath default.
    private const string ModpackDistDir = @"C:\Users\Gleso\Desktop\Project\Minecraft\Work\ModpackDist";
    private const string Repo = "Glesooo/battlefield-modpack";

    private Process? _process;

    public MainWindow()
    {
        InitializeComponent();
        RepoText.Text = Repo;
    }

    private void AppendLine(string line)
    {
        LogText.AppendText(line + Environment.NewLine);
        LogScroll.ScrollToEnd();
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        LogText.Clear();
        ActionButton.IsEnabled = false;
        ActionButton.Content = "Публикация...";
        CancelButton.Visibility = Visibility.Visible;
        StatusText.Text = "Публикация...";
        StatusText.Foreground = (Brush)FindResource("TextDimBrush");
        TitleText.Text = "Публикация обновления";

        string publishScript = Path.Combine(ModpackDistDir, "publish.ps1");
        if (!File.Exists(publishScript))
        {
            AppendLine($"Не найден publish.ps1 по пути: {publishScript}");
            Finish(success: false);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", publishScript,
                "-Repo", Repo,
            },
            WorkingDirectory = ModpackDistDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Defensive: gh/packwiz are added to PATH by their installers, but a GUI app launched
        // straight from Explorer can end up with a stale PATH snapshot right after a fresh
        // install (no logoff/logon yet) - the exact issue hit repeatedly while building this
        // tool. Prepending known install locations costs nothing if they're already on PATH.
        string ghDir = @"C:\Program Files\GitHub CLI";
        string packwizDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "go", "bin");
        psi.Environment["PATH"] = $"{ghDir};{packwizDir};{psi.Environment["PATH"]}";

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) => OnLine(args.Data);
        _process.ErrorDataReceived += (_, args) => OnLine(args.Data);

        try
        {
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            await _process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            AppendLine($"Не удалось запустить publish.ps1: {ex.Message}");
            Finish(success: false);
            return;
        }

        Finish(success: _process.ExitCode == 0);
    }

    private void OnLine(string? line)
    {
        if (line == null) return;
        Dispatcher.Invoke(() => AppendLine(line));
    }

    private void Finish(bool success)
    {
        CancelButton.Visibility = Visibility.Collapsed;
        ActionButton.IsEnabled = true;
        ActionButton.Content = "Опубликовать";

        if (success)
        {
            TitleText.Text = "Готово!";
            StatusText.Text = "Обновление опубликовано";
            StatusText.Foreground = (Brush)FindResource("OkBrush");
        }
        else
        {
            TitleText.Text = "Ошибка публикации";
            StatusText.Text = "Что-то пошло не так - подробности в логе выше";
            StatusText.Foreground = (Brush)FindResource("ErrorBrush");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch { /* best-effort - the process may have already exited on its own */ }
        AppendLine("--- отменено ---");
        Finish(success: false);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
