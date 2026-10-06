namespace MaceLauncher.Core;

public static class FileOps
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int Attempts = 5;
    private const int RetryDelayMs = 300;

    public static void Replace(string from, string to) => Guard([to, from], () =>
    {
        MakeWritable(to);
        File.Move(from, to, true);
    });

    public static void Delete(string path) => Guard(path, () =>
    {
        MakeWritable(path);
        File.Delete(path);
    });

    public static void MakeWritable(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    public static void Guard(string path, Action action) => Guard([path], action);

    private static void Guard(string[] paths, Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException { HResult: SharingViolation })
            {
                if (attempt == Attempts) throw new FileBusyException(paths[0], FileHolders.Of(paths), e);
                Thread.Sleep(RetryDelayMs);
            }
        }
    }
}

public sealed class FileBusyException(string path, IReadOnlyList<string> holders, Exception inner)
    : IOException(Describe(Path.GetFileName(path), holders), inner)
{
    private static string Describe(string file, IReadOnlyList<string> holders) => holders.Count == 0
        ? $"Файл занят или недоступен: {file}. Закройте игру и попробуйте снова."
        : $"Файл {file} занят программой {string.Join(", ", holders)}. Закройте её и попробуйте снова.";
}
