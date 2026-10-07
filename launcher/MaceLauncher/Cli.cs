using MaceLauncher.Core;

namespace MaceLauncher;

public static class Cli
{
    private const string KnownPrivateKey =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgEMqQI6AEPuvrDXeK+TGZ7WsKTLYJ2DoyYY/ut8BacByhRANCAATCOLuACFvx" +
        "FsoNuQM8cU2r9lxDPQ2B0vHYYrIzXu6/N4A64T8lB9WarQWFmtLmZtw9lHFwMmNvbPpDF32Z1V0v";
    private const string KnownPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwji7gAhb8RbKDbkDPHFNq/ZcQz0NgdLx2GKyM17uvzeAOuE/JQfVmq0FhZrS5mbcPZRxcDJj" +
        "b2z6Qxd9mdVdLw==";

    public static async Task<int> RunAsync(string[] args)
    {
        var paths = Paths(args);
        Log.Open(paths.Log);
        if (args.Contains("--selftest")) return SelfTest();

        try
        {
            using var http = LauncherInfo.CreateHttp();
            var sync = new PackSync(paths, http, Sources(args));
            var manifest = await sync.LoadManifestAsync(CancellationToken.None);
            await RunStepsAsync(args, paths, http, sync, manifest);
            return 0;
        }
        catch (Exception e)
        {
            Log.Info("ОШИБКА: " + e);
            return 1;
        }
    }

    private static async Task RunStepsAsync(string[] args, LauncherPaths paths, HttpClient http, PackSync sync,
        PackManifest manifest)
    {
        var progress = new StageLogger();
        if (args.Contains("--sync"))
        {
            var report = await sync.RunAsync(manifest, args.Contains("--verify"), progress, CancellationToken.None);
            Log.Info($"синхронизация: скачано {report.Downloaded} ({report.Bytes / 1048576.0:F1} МБ), " +
                     $"убрано {report.Moved.Count}, конфигов {report.ConfigsWritten}");
        }
        var launch = args.Contains("--launch");
        var printOnly = args.Contains("--print-launch");
        if (!args.Contains("--install") && !launch && !printOnly) return;

        var game = new Game(paths, http);
        var version = await game.InstallAsync(manifest, progress, CancellationToken.None);
        if (!launch && !printOnly) return;

        var settings = LauncherSettings.Load(paths.Settings);
        var nick = Value(args, "--nick") ?? settings.Nick;
        if (!OfflineProfile.IsValidNick(nick)) throw new ArgumentException($"Неверный ник: {nick}");
        var key = Key(args, paths, nick);
        var server = Value(args, "--join");
        if (printOnly) await game.BuildAsync(version, nick, key, settings, CancellationToken.None, server);
        else await game.StartAsync(version, nick, key, settings, CancellationToken.None, server);
    }

    private static NickKey? Key(string[] args, LauncherPaths paths, string nick)
    {
        var password = Value(args, "--password");
        if (password == null)
        {
            var saved = NickKey.Load(paths.Secret);
            return saved?.IsFor(nick) == true ? saved : null;
        }
        if (!NickKey.IsValidPassword(password)) throw new ArgumentException("Пароль слишком короткий");
        return NickKey.Derive(nick, password);
    }

    public static LauncherPaths Paths(string[] args) => new(Value(args, "--dir") ?? LauncherPaths.DefaultRoot);

    public static IReadOnlyList<string> Sources(string[] args)
    {
        var sources = Values(args, "--source");
        return sources.Count > 0 ? sources : LauncherInfo.Sources;
    }

    private static int SelfTest()
    {
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add(what);
        }

        Check(OfflineProfile.Uuid("Gleso") == "9cfa60c82fc135eeb837cb98cd12c0d3", "offline uuid");
        Check(OfflineProfile.IsValidNick("Gleso_1") && !OfflineProfile.IsValidNick("ab") && !OfflineProfile.IsValidNick("Глесо"), "nick rules");
        var paths = new LauncherPaths(@"C:\mace-selftest");
        Check(paths.InInstance("mods/a.jar").EndsWith(@"instance\mods\a.jar"), "path inside");
        try
        {
            paths.InInstance("../launcher.json");
            failures.Add("path escape not rejected");
        }
        catch (InvalidDataException)
        {
        }
        Check(Rejects(() => paths.InInstance("")), "instance root not rejected");
        CheckNickKey(Check);

        var sha = new string('a', 64);
        var valid = $$$"""
            {"revision":"r","format":1,"name":"n","minecraft":"1.20.1","forge":"47.4.10","java":17,
             "sources":["https://example.org/pack/"],"folders":{"mods":"strict"},
             "files":[{"path":"mods/a.jar","size":1,"sha256":"{{{sha}}}","asset":"0123456789abcdef-a.jar"}],
             "bundle":{"asset":"0123456789abcdef-base.zip","size":1,"sha256":"{{{sha}}}",
                       "files":[{"path":"config/a.toml","sha256":"{{{sha}}}","mode":"managed"}]}}
            """;
        Check(!Rejects(() => PackManifest.Parse(valid)), "valid manifest rejected");
        var broken = new Dictionary<string, string>
        {
            ["empty folder name"] = valid.Replace("\"mods\":\"strict\"", "\"\":\"strict\""),
            ["nested folder name"] = valid.Replace("\"mods\":\"strict\"", "\"mods/x\":\"strict\""),
            ["unknown folder rule"] = valid.Replace("\"mods\":\"strict\"", "\"mods\":\"wipe\""),
            ["path escape"] = valid.Replace("mods/a.jar", "../a.jar"),
            ["absolute path"] = valid.Replace("mods/a.jar", "C:/Windows/a.jar"),
            ["asset escape"] = valid.Replace("0123456789abcdef-a.jar", "../../evil.jar"),
            ["bundle asset escape"] = valid.Replace("0123456789abcdef-base.zip", "C:/evil.zip"),
            ["network share source"] = valid.Replace("https://example.org/pack/", @"\\\\host\\share"),
            ["http source"] = valid.Replace("https://", "http://"),
            ["unknown config mode"] = valid.Replace("\"managed\"", "\"anything\""),
            ["missing bundle"] = valid.Replace("\"bundle\"", "\"bundl\""),
            ["bad checksum"] = valid.Replace(sha, "xyz"),
            ["newer format"] = valid.Replace("\"format\":1", "\"format\":2"),
        };
        foreach (var (name, json) in broken)
        {
            Check(Rejects(() => PackManifest.Parse(json)), name + " not rejected");
        }

        Log.Info(failures.Count == 0 ? "selftest: ok" : "selftest FAILED: " + string.Join(", ", failures));
        return failures.Count == 0 ? 0 : 1;
    }

    private static void CheckNickKey(Action<bool, string> check)
    {
        var key = NickKey.Derive("gleso", "correct horse");
        check(key.PrivateKey == KnownPrivateKey && key.PublicKey == KnownPublicKey, "nick key known answer");
        check(NickKey.Derive("GLESO", "correct horse").PublicKey == key.PublicKey, "nick key depends on the nick's case");
        check(NickKey.Derive("gleso", "correct horsf").PublicKey != key.PublicKey, "nick key ignores the password");
        check(NickKey.Derive("glesa", "correct horse").PublicKey != key.PublicKey, "nick key ignores the nick");
        check(key.IsFor("Gleso") && !key.IsFor("Glesa"), "nick key owner");
        check(NickKey.IsValidPassword("123456") && !NickKey.IsValidPassword("12345"), "password length rule");

        var file = Path.Combine(Path.GetTempPath(), $"mace-selftest-{Environment.ProcessId}.bin");
        key.Save(file);
        var saved = NickKey.Load(file);
        check(saved?.PrivateKey == key.PrivateKey && saved.PublicKey == key.PublicKey && saved.IsFor("gleso"),
            "nick key storage");
        check(!System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file)).Contains(key.PublicKey),
            "nick key stored in the open");
        File.WriteAllText(file, "junk");
        check(NickKey.Load(file) == null, "damaged nick key file accepted");
        File.Delete(file);
    }

    private static bool Rejects(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> Values(string[] args, string name) =>
        args.Select((arg, i) => (arg, i)).Where(x => x.arg == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToList();

    private sealed class StageLogger : IProgress<LauncherProgress>
    {
        private string stage = "";
        private int decile = -1;

        public void Report(LauncherProgress value)
        {
            var current = double.IsFinite(value.Fraction) ? (int)(Math.Clamp(value.Fraction, 0, 1) * 10) : 0;
            if (value.Stage == stage && current == decile) return;
            stage = value.Stage;
            decile = current;
            Log.Info($"{value.Stage}: {current * 10}% {value.Detail}");
        }
    }
}
