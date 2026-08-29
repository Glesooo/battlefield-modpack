using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BattlefieldUpdaterGui;

public partial class MainWindow : Window
{
    private const string PackUrl = "https://raw.githubusercontent.com/Glesooo/battlefield-modpack/main/pack.toml";
    private const string BootstrapUrl = "https://github.com/packwiz/packwiz-installer-bootstrap/releases/download/v0.0.3/packwiz-installer-bootstrap.jar";

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BattlefieldUpdater");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "path.txt");

    // Same regex packwiz-installer's own console output uses: "(123/7563) some file name ...".
    private static readonly Regex ProgressLine = new(@"^\((\d+)/(\d+)\)\s*(.*)$", RegexOptions.Compiled);

    private Process? _process;
    private bool _finishedOk;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        string? instance = LoadSavedPath();
        if (instance == null)
        {
            instance = PickFolder();
            if (instance == null)
            {
                Close();
                return;
            }
            SavePath(instance);
        }

        await RunUpdateAsync(instance);
    }

    // ---------------------------------------------------------------- settings

    private static string? LoadSavedPath()
    {
        if (!File.Exists(SettingsFile)) return null;
        string saved = File.ReadAllText(SettingsFile).Trim();
        return Directory.Exists(saved) ? saved : null;
    }

    private static void SavePath(string path)
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(SettingsFile, path);
    }

    private string? PickFolder()
    {
        MessageBox.Show(
            "Выбери папку 'minecraft' сборки Battlefield (в ней лежат папки mods, config, saves).",
            "Battlefield Updater", MessageBoxButton.OK, MessageBoxImage.Information);

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка minecraft сборки Battlefield (с mods/config/saves)",
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        string? picked = PickFolder();
        if (picked == null) return;
        SavePath(picked);
        await RunUpdateAsync(picked);
    }

    // ---------------------------------------------------------------- steps

    private void SetStep(int index, bool error = false)
    {
        var borders = new[] { Step1Border, Step2Border, Step3Border, Step4Border };
        var icons = new[] { Step1Icon, Step2Icon, Step3Icon, Step4Icon };
        for (int i = 0; i < borders.Length; i++)
        {
            if (i < index)
            {
                borders[i].Background = Brushes.Transparent;
                icons[i].Text = "✓";
                icons[i].Foreground = (Brush)FindResource("TextFaintBrush");
                borders[i].SetValue(TextElementForegroundProperty, (Brush)FindResource("TextFaintBrush"));
            }
            else if (i == index)
            {
                borders[i].Background = error ? new SolidColorBrush(Color.FromArgb(0x30, 0xE0, 0x57, 0x6B))
                    : new SolidColorBrush(Color.FromArgb(0x25, 0xF0, 0x62, 0x9E));
                icons[i].Text = error ? "!" : "●";
                var accent = error ? (Brush)FindResource("ErrorBrush") : (Brush)FindResource("AccentBrightBrush");
                icons[i].Foreground = accent;
                borders[i].SetValue(TextElementForegroundProperty, accent);
            }
            else
            {
                borders[i].Background = Brushes.Transparent;
                borders[i].SetValue(TextElementForegroundProperty, (Brush)FindResource("TextFaintBrush"));
            }
        }
    }

    private static readonly DependencyProperty TextElementForegroundProperty =
        System.Windows.Documents.TextElement.ForegroundProperty;

    // ---------------------------------------------------------------- update flow

    private async Task RunUpdateAsync(string instancePath)
    {
        CancelButton.Visibility = Visibility.Visible;
        ActionButton.IsEnabled = false;
        ActionButton.Content = "...";
        MainProgressBar.Maximum = 100;
        MainProgressBar.Value = 0;
        ProgressPercentText.Text = "";
        CurrentFileText.Text = "";
        _finishedOk = false;

        TitleText.Text = "Проверка обновлений...";
        SubtitleText.Text = instancePath;
        ProgressLabel.Text = "Подключение...";
        SetStep(0);

        if (!IsJavaAvailable())
        {
            ShowError("Java не найдена на этом компьютере.",
                "Установи Java (она и так нужна для Minecraft) и запусти апдейтер снова.");
            return;
        }

        string bootstrapPath = Path.Combine(instancePath, "battlefield-installer-bootstrap.jar");
        if (!File.Exists(bootstrapPath))
        {
            ProgressLabel.Text = "Скачивание апдейтера...";
            try
            {
                using var http = new HttpClient();
                byte[] data = await http.GetByteArrayAsync(BootstrapUrl);
                await File.WriteAllBytesAsync(bootstrapPath, data);
            }
            catch (Exception ex)
            {
                ShowError("Не удалось скачать апдейтер.", ex.Message);
                return;
            }
        }

        SetStep(1);
        TitleText.Text = "Загрузка обновлений...";
        SubtitleText.Text = "Скачиваются изменившиеся файлы";
        ProgressLabel.Text = "Проверка файлов...";

        var psi = new ProcessStartInfo
        {
            FileName = "java",
            ArgumentList = { "-jar", bootstrapPath, PackUrl },
            WorkingDirectory = instancePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var lastLines = new List<string>();

        _process.OutputDataReceived += (_, args) => HandleLine(args.Data, lastLines);
        _process.ErrorDataReceived += (_, args) => HandleLine(args.Data, lastLines);

        try
        {
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            // packwiz-installer can prompt for a confirmation on stdin in some versions; feeding
            // a newline immediately means "accept default" instead of hanging forever with no
            // console attached for the player to type into (CreateNoWindow hides it).
            await _process.StandardInput.WriteLineAsync();
            await _process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            ShowError("Не удалось запустить обновление.", ex.Message);
            return;
        }

        if (_process.ExitCode == 0)
        {
            _finishedOk = true;
            SetStep(3);
            TitleText.Text = "Готово!";
            SubtitleText.Text = "Сборка обновлена до последней версии";
            ProgressLabel.Text = "Всё установлено";
            ProgressPercentText.Text = "100%";
            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = 100;
            CurrentFileText.Text = "";
            CancelButton.Visibility = Visibility.Collapsed;
            ActionButton.Content = "Готово";
            ActionButton.IsEnabled = true;
        }
        else
        {
            string details = string.Join(Environment.NewLine, lastLines.TakeLast(6));
            ShowError($"Апдейтер сообщил о проблеме (код {_process.ExitCode}).", details);
        }
    }

    private void HandleLine(string? line, List<string> lastLines)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lastLines.Add(line);
        if (lastLines.Count > 50) lastLines.RemoveAt(0);

        var match = ProgressLine.Match(line);
        Dispatcher.Invoke(() =>
        {
            if (match.Success)
            {
                SetStep(2);
                TitleText.Text = "Установка обновлений...";
                SubtitleText.Text = "Применяются изменения в сборке";
                int current = int.Parse(match.Groups[1].Value);
                int total = int.Parse(match.Groups[2].Value);
                string what = match.Groups[3].Value;

                MainProgressBar.IsIndeterminate = false;
                MainProgressBar.Maximum = total;
                MainProgressBar.Value = current;
                int pct = total > 0 ? (int)(current * 100.0 / total) : 0;
                ProgressPercentText.Text = $"{pct}%";
                ProgressLabel.Text = $"Файл {current} из {total}";
                CurrentFileText.Text = what;
            }
            else if (line.Contains("Finished successfully"))
            {
                ProgressLabel.Text = "Завершение...";
            }
        });
    }

    private static bool IsJavaAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("java", "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p != null && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void ShowError(string title, string details)
    {
        SetStep(2, error: true);
        TitleText.Text = title;
        SubtitleText.Text = "Попробуй ещё раз или обратись к автору сборки";
        CurrentFileText.Text = details;
        MainProgressBar.IsIndeterminate = false;
        MainProgressBar.Value = 0;
        ProgressPercentText.Text = "";
        ProgressLabel.Text = "Ошибка";
        CancelButton.Visibility = Visibility.Collapsed;
        ActionButton.Content = "Повторить";
        ActionButton.IsEnabled = true;
    }

    // ---------------------------------------------------------------- chrome + buttons

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch { /* best-effort - the process may have already exited on its own */ }
        Close();
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_finishedOk)
        {
            Close();
            return;
        }
        // "Повторить" after an error - re-run against the same saved folder.
        string? instance = LoadSavedPath();
        if (instance != null) await RunUpdateAsync(instance);
    }
}
