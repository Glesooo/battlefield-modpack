using System.IO.Compression;
using System.Runtime.ExceptionServices;

namespace MaceLauncher.Core;

public sealed class PackSync(LauncherPaths paths, HttpClient http, IReadOnlyList<string> bootstrapSources)
{
    private const int ParallelDownloads = 4;
    private const string ManifestAsset = "manifest.json";
    private const string BundlePattern = "*-base.zip*";

    public async Task<PackManifest> LoadManifestAsync(CancellationToken ct)
    {
        var manifest = await new Downloader(http, bootstrapSources).ReadAsync(ManifestAsset, PackManifest.Parse, ct);
        Log.Info($"сборка {manifest.Name} {manifest.Revision}: {manifest.Files.Count} файлов");
        return manifest;
    }

    public async Task<SyncReport> RunAsync(PackManifest manifest, bool verifyAll, IProgress<LauncherProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Instance);
        var index = FileIndex.Load(paths.Index);
        try
        {
            var moved = RemoveForeignFiles(manifest, index);
            var outdated = await FindOutdatedAsync(manifest, index, verifyAll, progress, ct);
            var total = outdated.Sum(file => file.Size);
            Log.Info($"скачать {outdated.Count} файлов, {total / 1048576.0:F1} МБ; убрано чужих: {moved.Count}");

            var downloader = new Downloader(http, bootstrapSources.Concat(manifest.Sources));
            await DownloadAsync(outdated, total, index, downloader, progress, ct);
            var written = await ApplyBundleAsync(manifest, index, downloader, verifyAll, ct);
            return new SyncReport(outdated.Count, total, moved, written);
        }
        finally
        {
            index.Save();
        }
    }

    private async Task<List<PackFile>> FindOutdatedAsync(PackManifest manifest, FileIndex index, bool verifyAll,
        IProgress<LauncherProgress>? progress, CancellationToken ct)
    {
        var outdated = new List<PackFile>();
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var file = manifest.Files[i];
            if (await index.HashAsync(file.Path, paths.InInstance(file.Path), !verifyAll, ct) != file.Sha256)
            {
                outdated.Add(file);
            }
            progress?.Report(new("Проверка файлов", file.Path, (i + 1.0) / manifest.Files.Count));
        }
        return outdated;
    }

    private async Task DownloadAsync(IReadOnlyList<PackFile> files, long total, FileIndex index, Downloader downloader,
        IProgress<LauncherProgress>? progress, CancellationToken ct)
    {
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(ParallelDownloads);
        Exception? failure = null;
        var downloads = Task.WhenAll(files.Select(async file =>
        {
            try
            {
                await gate.WaitAsync(failFast.Token);
                try
                {
                    var full = paths.InInstance(file.Path);
                    await downloader.FetchAsync(file.Asset, file.Sha256, full, failFast.Token);
                    index.Record(file.Path, full, file.Sha256);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception e)
            {
                if (Interlocked.CompareExchange(ref failure, e, null) == null) failFast.Cancel();
            }
        }));
        while (!downloads.IsCompleted)
        {
            var done = Math.Clamp(downloader.Received, 0, total);
            progress?.Report(new("Загрузка сборки", $"{done / 1048576.0:F0} из {total / 1048576.0:F0} МБ",
                total == 0 ? 1 : (double)done / total));
            await Task.WhenAny(downloads, Task.Delay(250, CancellationToken.None));
        }
        await downloads;
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private List<string> RemoveForeignFiles(PackManifest manifest, FileIndex index)
    {
        var moved = new List<string>();
        var stamp = Path.Combine(paths.Removed, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        foreach (var (folder, policy) in manifest.Folders)
        {
            if (policy is not (PackManifest.Strict or PackManifest.StrictZip)) continue;
            var directory = new DirectoryInfo(paths.InInstance(folder));
            if (!directory.Exists) continue;
            var prefix = folder + "/";
            var allowed = manifest.Files.Where(file => file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Path[prefix.Length..].Split('/')[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in directory.EnumerateFileSystemInfos().ToList())
            {
                if (allowed.Contains(entry.Name)) continue;
                if (entry.Name.EndsWith(Downloader.PartSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    if (!allowed.Contains(entry.Name[..^Downloader.PartSuffix.Length])) FileOps.Delete(entry.FullName);
                    continue;
                }
                if (policy == PackManifest.StrictZip
                    && (entry is DirectoryInfo || !entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))) continue;

                var relative = prefix + entry.Name;
                if (entry is FileInfo && index.Forget(relative))
                {
                    FileOps.Delete(entry.FullName);
                    Log.Info($"удалён файл прошлой версии сборки {relative}");
                    continue;
                }
                var target = Path.Combine(stamp, folder, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                FileOps.Guard(entry.FullName, () =>
                {
                    if (entry is DirectoryInfo nested) nested.MoveTo(target);
                    else ((FileInfo)entry).MoveTo(target);
                });
                moved.Add(relative);
                Log.Info($"убран чужой файл {relative} -> {target}");
            }
        }
        return moved;
    }

    private async Task<int> ApplyBundleAsync(PackManifest manifest, FileIndex index, Downloader downloader, bool verifyAll,
        CancellationToken ct)
    {
        var needed = new List<BundleFile>();
        foreach (var file in manifest.Bundle.Files)
        {
            var full = paths.InInstance(file.Path);
            var missing = file.Mode == PackManifest.FirstInstall
                ? !File.Exists(full)
                : await index.HashAsync(file.Path, full, !verifyAll, ct) != file.Sha256;
            if (missing) needed.Add(file);
        }
        if (needed.Count == 0) return 0;

        Directory.CreateDirectory(paths.Cache);
        var zip = Path.Combine(paths.Cache, manifest.Bundle.Asset);
        if (!File.Exists(zip) || await FileIndex.Sha256Async(zip, ct) != manifest.Bundle.Sha256)
        {
            await downloader.FetchAsync(manifest.Bundle.Asset, manifest.Bundle.Sha256, zip, ct);
        }
        DeleteOldBundles(zip);

        using var archive = ZipFile.OpenRead(zip);
        foreach (var file in needed)
        {
            var entry = archive.GetEntry(file.Path) ?? throw new InvalidDataException($"В архиве конфигов нет {file.Path}");
            var full = paths.InInstance(file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            FileOps.Guard(full, () =>
            {
                FileOps.MakeWritable(full);
                entry.ExtractToFile(full, true);
            });
            if (file.Mode != PackManifest.FirstInstall) index.Record(file.Path, full, file.Sha256);
        }
        Log.Info($"конфигов записано: {needed.Count}");
        return needed.Count;
    }

    private void DeleteOldBundles(string current)
    {
        foreach (var old in Directory.EnumerateFiles(paths.Cache, BundlePattern))
        {
            if (old.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
            }
        }
    }
}
