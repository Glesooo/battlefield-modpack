using System.Diagnostics;

namespace MaceLauncher.Core;

public static class SelfUpdate
{
    private const string NewSuffix = ".new";
    private const string OldSuffix = ".old";

    public static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("Не найден файл лаунчера");

    public static bool IsNewer(PackManifest manifest) =>
        manifest.Launcher is { } release && Version.Parse(release.Version) > Version.Parse(LauncherInfo.Version);

    public static async Task InstallAsync(PackManifest manifest, Downloader downloader, Action<double>? progress,
        CancellationToken ct)
    {
        var release = manifest.Launcher ?? throw new InvalidOperationException("В списке сборки нет лаунчера");
        var exe = Executable;
        var fresh = exe + NewSuffix;
        var download = downloader.FetchAsync(release.Asset, release.Sha256, fresh, ct);
        while (!download.IsCompleted)
        {
            progress?.Invoke(Math.Clamp(downloader.Received / (double)release.Size, 0, 1));
            await Task.WhenAny(download, Task.Delay(200, ct));
        }
        await download;
        File.Move(exe, exe + OldSuffix, true);
        File.Move(fresh, exe);
        Log.Info($"лаунчер обновлён до {release.Version}");
    }

    public static void Restart(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process.Start(start);
    }

    public static void CleanUp()
    {
        var old = Executable + OldSuffix;
        try
        {
            if (File.Exists(old)) File.Delete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info("старый файл лаунчера ещё занят: " + e.Message);
        }
    }
}
