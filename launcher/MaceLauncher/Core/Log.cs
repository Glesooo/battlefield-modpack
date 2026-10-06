namespace MaceLauncher.Core;

public static class Log
{
    private static readonly object Gate = new();
    private static string? file;

    public static void Open(string path)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path)) File.Move(path, Path.ChangeExtension(path, ".old.log"), true);
                file = path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                file = null;
            }
        }
        Info($"M.A.C.E launcher {LauncherInfo.Version}");
    }

    public static void Info(string message)
    {
        lock (Gate)
        {
            if (file == null) return;
            try
            {
                File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
