using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MaceLauncher.Core;

namespace MaceLauncher;

public partial class MainWindow : Window
{
    private const int EarlyExitMs = 10_000;
    private static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(5);

    private readonly LauncherPaths paths;
    private readonly IReadOnlyList<string> sources;
    private readonly LauncherSettings settings;
    private NickKey? key;
    private CancellationTokenSource? work;
    private bool closeRequested;

    public MainWindow(LauncherPaths paths, IReadOnlyList<string> sources)
    {
        this.paths = paths;
        this.sources = sources;
        InitializeComponent();
        Log.Open(paths.Log);
        settings = LauncherSettings.Load(paths.Settings);
        key = NickKey.Load(paths.Secret);
        NickBox.Text = settings.Nick;
        ShowPasswordState();
        VersionText.Text = "v" + LauncherInfo.Version;
        Loaded += async (_, _) => await UpdateLauncherAsync();
    }

    private async Task UpdateLauncherAsync()
    {
        var source = new CancellationTokenSource();
        work = source;
        SetBusy(true);
        var restart = false;
        try
        {
            using var http = LauncherInfo.CreateHttp();
            source.CancelAfter(UpdateCheckTimeout);
            var manifest = await new PackSync(paths, http, sources).LoadManifestAsync(source.Token);
            source.CancelAfter(Timeout.InfiniteTimeSpan);
            if (!SelfUpdate.IsNewer(manifest)) return;
            var version = manifest.Launcher!.Version;
            ShowProgress("Обновление лаунчера", "версия " + version, 0);
            var downloader = new Downloader(http, sources.Concat(manifest.Sources));
            await SelfUpdate.InstallAsync(manifest, downloader, fraction => ShowProgress("Обновление лаунчера", "версия " + version, fraction),
                source.Token);
            restart = true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException
                                   or OperationCanceledException)
        {
            Log.Info("обновление лаунчера: " + ex.Message);
            if (ex is UnauthorizedAccessException) ShowStatus("Лаунчер не смог обновиться: нет прав на его папку", "ErrorBrush");
        }
        finally
        {
            work = null;
            source.Dispose();
            SetBusy(false);
        }
        if (!restart) return;
        ((App)Application.Current).ReleaseSingleInstance();
        SelfUpdate.Restart(Environment.GetCommandLineArgs().Skip(1));
        Close();
    }

    private string TypedPassword => ShowPasswordBox.IsChecked == true ? PasswordText.Text : PasswordField.Password;

    private async void Play_Click(object sender, RoutedEventArgs e) => await RunAsync(launch: true, verifyAll: false);

    private async void Verify_Click(object sender, RoutedEventArgs e) => await RunAsync(launch: false, verifyAll: true);

    private void NickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PlayButton.IsEnabled) Play_Click(sender, e);
    }

    private void NickBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ShowPasswordState();

    private bool HasKey(string nick) => key?.IsFor(nick) == true;

    private void ShowPasswordState()
    {
        var saved = HasKey(NickBox.Text.Trim());
        PasswordSaved.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        PasswordEntry.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PasswordReset_Click(object sender, RoutedEventArgs e)
    {
        key = null;
        ShowPasswordState();
    }

    private void ShowPasswordBox_Changed(object sender, RoutedEventArgs e)
    {
        var shown = ShowPasswordBox.IsChecked == true;
        if (shown) PasswordText.Text = PasswordField.Password;
        else PasswordField.Password = PasswordText.Text;
        PasswordText.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        PasswordField.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RememberKeyAsync(string nick, string password, CancellationToken token)
    {
        ShowProgress("Подготовка ключа ника", "", 0);
        key = await Task.Run(() => NickKey.Derive(nick, password), token);
        key.Save(paths.Secret);
        PasswordField.Clear();
        PasswordText.Clear();
        ShowPasswordState();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(paths.Instance);

    private void OpenSubfolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(paths.InInstance((string)((FrameworkElement)sender).Tag));

    private void OpenLauncherLog_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{paths.Log}\"") { UseShellExecute = true });

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        MemorySlider.Minimum = LauncherSettings.LowestMemoryMb;
        MemorySlider.Maximum = LauncherSettings.MemoryCeilingMb;
        MemorySlider.TickFrequency = LauncherSettings.MemoryStepMb;
        MemoryHint.Text = $"На этом ПК {LauncherSettings.SystemMemoryMb} МБ. Сборке нужно не меньше " +
                          $"{LauncherSettings.LowestMemoryMb} МБ, обычно хватает 4000.";
        ShowSettings(settings);
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void ShowSettings(LauncherSettings shown)
    {
        MemorySlider.Value = Math.Clamp(shown.MaxMemoryMb, MemorySlider.Minimum, MemorySlider.Maximum);
        FullScreenBox.IsChecked = shown.FullScreen;
        WidthBox.Text = shown.WindowWidth > 0 ? shown.WindowWidth.ToString() : "";
        HeightBox.Text = shown.WindowHeight > 0 ? shown.WindowHeight.ToString() : "";
        JavaBox.Text = shown.JavaArguments;
        ShowSettingsMessage("", "ErrorBrush");
    }

    private void MemorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        MemoryValue.Text = $"{e.NewValue:F0} МБ";

    private void FullScreenBox_Changed(object sender, RoutedEventArgs e)
    {
        var windowed = FullScreenBox.IsChecked != true;
        WindowSizeRow.IsEnabled = windowed;
        WindowSizeRow.Opacity = windowed ? 1 : 0.4;
    }

    private void SettingsDefaults_Click(object sender, RoutedEventArgs e) => ShowSettings(new LauncherSettings());

    private void SettingsCancel_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadWindowSize(out var width, out var height))
        {
            ShowSettingsMessage($"Размер окна: два числа не меньше {LauncherSettings.SmallestWidth}×" +
                                $"{LauncherSettings.SmallestHeight} или оба поля пустые.", "ErrorBrush");
            return;
        }
        var java = JavaBox.Text.Trim();
        if (LauncherSettings.SplitArguments(java).Any(argument => !argument.StartsWith('-')))
        {
            ShowSettingsMessage("Каждый параметр Java должен начинаться с «-».", "ErrorBrush");
            return;
        }
        settings.MaxMemoryMb = (int)MemorySlider.Value;
        settings.FullScreen = FullScreenBox.IsChecked == true;
        settings.WindowWidth = width;
        settings.WindowHeight = height;
        settings.JavaArguments = java;
        try
        {
            settings.Save(paths.Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowSettingsMessage("Не удалось сохранить настройки: " + ex.Message, "ErrorBrush");
            return;
        }
        SettingsPanel.Visibility = Visibility.Collapsed;
    }

    private bool TryReadWindowSize(out int width, out int height)
    {
        width = height = 0;
        if (WidthBox.Text.Trim().Length == 0 && HeightBox.Text.Trim().Length == 0) return true;
        return int.TryParse(WidthBox.Text.Trim(), out width) && int.TryParse(HeightBox.Text.Trim(), out height)
               && width >= LauncherSettings.SmallestWidth && height >= LauncherSettings.SmallestHeight;
    }

    private async Task RunAsync(bool launch, bool verifyAll)
    {
        var nick = NickBox.Text.Trim();
        if (launch && !OfflineProfile.IsValidNick(nick))
        {
            ShowStatus("Ник: от 3 до 16 символов — латинские буквы, цифры и _", "ErrorBrush");
            return;
        }
        var password = TypedPassword;
        if (launch && !HasKey(nick) && !NickKey.IsValidPassword(password))
        {
            ShowStatus($"Пароль: не короче {NickKey.ShortestPassword} символов", "ErrorBrush");
            return;
        }
        var source = new CancellationTokenSource();
        var token = source.Token;
        work = source;
        SetBusy(true);
        var launched = false;
        try
        {
            settings.Nick = nick;
            settings.Save(paths.Settings);
            if (launch && !HasKey(nick)) await RememberKeyAsync(nick, password, token);
            using var http = LauncherInfo.CreateHttp();
            var progress = new Progress<LauncherProgress>(p => ShowProgress(p.Stage, p.Detail, p.Fraction));
            var sync = new PackSync(paths, http, sources);
            ShowProgress("Получение списка сборки", "", 0);
            var manifest = await Task.Run(() => sync.LoadManifestAsync(token), token);
            var report = await Task.Run(() => sync.RunAsync(manifest, verifyAll, progress, token), token);
            if (report.Moved.Count > 0) ReportRemoved(report.Moved);
            if (launch && !settings.MigrationOffered) OfferMigration(manifest);
            if (!launch)
            {
                work = null;
                ShowStatus("Файлы сборки в порядке", "OkBrush");
                return;
            }
            var game = new Game(paths, http);
            var version = await Task.Run(() => game.InstallAsync(manifest, progress, token), token);
            var process = await Task.Run(() => game.StartAsync(version, nick, key, settings, token), token);
            work = null;
            ShowStatus("Игра запускается…", "TextBrush");
            if (await Task.Run(() => process.WaitForExit(EarlyExitMs)) && process.ExitCode != 0)
            {
                ShowStatus($"Игра закрылась с ошибкой (код {process.ExitCode}). Журнал: instance\\logs\\latest.log", "ErrorBrush");
                return;
            }
            launched = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            work = null;
            ShowStatus("Отменено", "ErrorBrush");
        }
        catch (Exception ex)
        {
            work = null;
            Log.Info("ОШИБКА: " + ex);
            ShowStatus(Describe(ex), "ErrorBrush");
        }
        finally
        {
            work = null;
            source.Dispose();
            SetBusy(false);
            if (launched || closeRequested) Close();
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        FileBusyException or InvalidDataException => ex.Message,
        OperationCanceledException => "Сервер не отвечает. Проверьте интернет и попробуйте снова.",
        _ => ex.Message + " Подробности в launcher.log",
    };

    private void OfferMigration(PackManifest manifest)
    {
        settings.MigrationOffered = true;
        settings.Save(paths.Settings);
        var old = Migration.FindOldFolder(paths);
        if (old == null) return;
        var answer = MessageBox.Show(this,
            $"Нашлась прежняя папка игры:{Environment.NewLine}{old}{Environment.NewLine}{Environment.NewLine}" +
            "Перенести оттуда управление, свои настройки графики и модов и свои шейдеры?",
            "M.A.C.E", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Migration.Copy(old, paths, manifest);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Прежняя папка игры: в ней лежат options.txt и папка mods" };
        if (dialog.ShowDialog(this) != true) return;
        var folder = Migration.ResolveGameFolder(dialog.FolderName);
        if (folder == null)
        {
            ShowSettingsMessage("В этой папке нет игры: нужны options.txt и папка mods.", "ErrorBrush");
            return;
        }
        ImportButton.IsEnabled = false;
        try
        {
            using var http = LauncherInfo.CreateHttp();
            var manifest = await new PackSync(paths, http, sources).LoadManifestAsync(CancellationToken.None);
            var report = await Task.Run(() => Migration.Copy(folder, paths, manifest));
            ShowSettingsMessage($"Перенесено: настроек {report.Settings}, шейдеров {report.Shaders}.", "OkBrush");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Info("перенос настроек: " + ex);
            ShowSettingsMessage("Не удалось перенести: " + ex.Message, "ErrorBrush");
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private void ShowSettingsMessage(string text, string brush)
    {
        SettingsError.Text = text;
        SettingsError.Foreground = (Brush)FindResource(brush);
    }

    private void ReportRemoved(IReadOnlyList<string> moved)
    {
        var list = string.Join(Environment.NewLine, moved.Take(12));
        var more = moved.Count > 12 ? $"{Environment.NewLine}и ещё {moved.Count - 12}" : "";
        MessageBox.Show(this,
            $"Эти файлы не входят в сборку и убраны в папку removed:{Environment.NewLine}{Environment.NewLine}{list}{more}",
            "M.A.C.E", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowProgress(string stage, string detail, double fraction)
    {
        if (work is not { IsCancellationRequested: false }) return;
        var value = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        StageText.Text = stage;
        StageText.Foreground = (Brush)FindResource("TextBrush");
        DetailText.Text = detail;
        Progress.Value = value;
        PercentText.Text = $"{value * 100:F0}%";
    }

    private void ShowStatus(string text, string brush)
    {
        StageText.Text = text;
        StageText.Foreground = (Brush)FindResource(brush);
        DetailText.Text = "";
        PercentText.Text = "";
    }

    private void SetBusy(bool busy)
    {
        PlayButton.IsEnabled = !busy;
        VerifyButton.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
        NickBox.IsEnabled = !busy;
        PasswordEntry.IsEnabled = !busy;
        PasswordSaved.IsEnabled = !busy;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (work is not { IsCancellationRequested: false }) return;
        e.Cancel = true;
        closeRequested = true;
        work.Cancel();
        ShowStatus("Останавливаю…", "TextBrush");
    }
}
