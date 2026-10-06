namespace MaceLauncher.Core;

public sealed record LauncherPaths(string Root)
{
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MACE");

    public string Game => Path.Combine(Root, "game");
    public string Instance => Path.Combine(Root, "instance");
    public string Removed => Path.Combine(Root, "removed");
    public string Cache => Path.Combine(Root, "cache");
    public string Settings => Path.Combine(Root, "launcher.json");
    public string Index => Path.Combine(Root, "files.json");
    public string Log => Path.Combine(Root, "launcher.log");

    public string InInstance(string relative)
    {
        var root = Path.GetFullPath(Instance) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (full.Length <= root.Length || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Путь вне папки игры: {relative}");
        }
        return full;
    }
}
