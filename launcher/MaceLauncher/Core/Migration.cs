namespace MaceLauncher.Core;

public sealed record MigrationReport(int Settings, int Shaders);

public static class Migration
{
    private const string Shaderpacks = "shaderpacks";
    private static readonly string[] PersonalFiles =
    [
        "options.txt", "config/battlefieldhud-client.toml", "config/embeddium-options.json", "config/oculus.properties",
    ];

    public static string? FindOldFolder(LauncherPaths paths) =>
        Candidates().FirstOrDefault(folder => IsGameFolder(folder) && !SameFolder(folder, paths.Instance));

    public static bool IsGameFolder(string folder) =>
        File.Exists(Path.Combine(folder, "options.txt")) && Directory.Exists(Path.Combine(folder, "mods"));

    public static string? ResolveGameFolder(string picked) =>
        new[] { picked, Path.Combine(picked, "minecraft"), Path.Combine(picked, ".minecraft") }.FirstOrDefault(IsGameFolder);

    public static MigrationReport Copy(string from, LauncherPaths paths, PackManifest manifest)
    {
        var personal = manifest.Bundle.Files.Where(file => file.Mode == PackManifest.FirstInstall)
            .Select(file => file.Path).Concat(PersonalFiles).Distinct();
        var settings = 0;
        foreach (var relative in personal)
        {
            var source = Path.Combine(from, relative);
            if (!File.Exists(source)) continue;
            var target = paths.InInstance(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            FileOps.Guard(target, () =>
            {
                FileOps.MakeWritable(target);
                File.Copy(source, target, true);
            });
            settings++;
        }
        var shaders = CopyShaders(Path.Combine(from, Shaderpacks), paths.InInstance(Shaderpacks));
        Log.Info($"перенос из {from}: настроек {settings}, шейдеров {shaders}");
        return new(settings, shaders);
    }

    private static IEnumerable<string> Candidates()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var saved = Path.Combine(appData, "BattlefieldUpdater", "path.txt");
        if (File.Exists(saved)) yield return File.ReadAllText(saved).Trim();
        yield return Path.Combine(appData, "PrismLauncher", "instances", "M.A.C.E", "minecraft");
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static int CopyShaders(string from, string to)
    {
        if (!Directory.Exists(from)) return 0;
        Directory.CreateDirectory(to);
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            File.Copy(file, target);
            copied++;
        }
        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            var target = Path.Combine(to, Path.GetFileName(folder));
            if (Directory.Exists(target)) continue;
            CopyFolder(folder, target);
            copied++;
        }
        return copied;
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var folder in Directory.EnumerateDirectories(from)) CopyFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
    }
}
