using System.Security.Cryptography;
using System.Text.Json;

namespace MaceLauncher.Core;

public sealed class FileIndex
{
    public sealed record Entry(long Size, long Modified, string Sha256);

    private readonly string file;
    private readonly Dictionary<string, Entry> entries;
    private readonly object gate = new();

    private FileIndex(string file, Dictionary<string, Entry> entries)
    {
        this.file = file;
        this.entries = new Dictionary<string, Entry>(entries, StringComparer.OrdinalIgnoreCase);
    }

    public static FileIndex Load(string file)
    {
        try
        {
            var loaded = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) : null;
            return new FileIndex(file, loaded ?? []);
        }
        catch (Exception e) when (e is JsonException or IOException or ArgumentException)
        {
            return new FileIndex(file, []);
        }
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    public async Task<string?> HashAsync(string relative, string full, bool trustCache, CancellationToken ct)
    {
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            Forget(relative);
            return null;
        }
        var size = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;
        lock (gate)
        {
            if (trustCache && entries.TryGetValue(relative, out var known) && known.Size == size && known.Modified == modified)
            {
                return known.Sha256;
            }
        }
        var sha = await Sha256Async(full, ct);
        lock (gate) entries[relative] = new Entry(size, modified, sha);
        return sha;
    }

    public void Record(string relative, string full, string sha)
    {
        var info = new FileInfo(full);
        lock (gate) entries[relative] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, sha);
    }

    public bool Forget(string relative)
    {
        lock (gate) return entries.Remove(relative);
    }

    public void Save()
    {
        try
        {
            lock (gate) File.WriteAllText(file, JsonSerializer.Serialize(entries));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info("не удалось сохранить files.json: " + e.Message);
        }
    }
}
