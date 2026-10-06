using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MaceLauncher.Core;

public sealed class Downloader
{
    public const string PartSuffix = ".mace-part";

    private const int AttemptsPerSource = 2;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly HttpClient http;
    private readonly List<string> sources;
    private readonly object gate = new();
    private long received;

    public Downloader(HttpClient http, IEnumerable<string> sources)
    {
        this.http = http;
        this.sources = sources.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public long Received => Interlocked.Read(ref received);

    public async Task<T> ReadAsync<T>(string asset, Func<string, T> parse, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var source in Snapshot())
        {
            try
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(StallTimeout);
                var (stream, _) = await OpenAsync(source, asset, 0, stall.Token);
                await using (stream)
                using (var reader = new StreamReader(stream))
                {
                    return parse(await reader.ReadToEndAsync(stall.Token));
                }
            }
            catch (Exception e) when (IsRecoverable(e, ct) || e is JsonException or InvalidDataException)
            {
                Log.Info($"{asset}: {source}: {e.Message}");
                last = e;
            }
        }
        if (last is InvalidDataException) throw last;
        throw new IOException($"Не удалось получить {asset} ни с одного адреса: {last?.Message}", last);
    }

    public async Task FetchAsync(string asset, string sha256, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + PartSuffix;
        await DownloadAsync(asset, sha256, part, ct);
        FileOps.Replace(part, destination);
    }

    private async Task DownloadAsync(string asset, string sha256, string part, CancellationToken ct)
    {
        Exception? last = null;
        var lastSource = "";
        foreach (var source in Snapshot())
        {
            lastSource = source;
            for (var attempt = 0; attempt < AttemptsPerSource; attempt++)
            {
                if (attempt > 0) await Task.Delay(RetryDelay, ct);
                long counted = 0;
                try
                {
                    var actual = await CopyAsync(source, asset, part, bytes =>
                    {
                        counted += bytes;
                        Interlocked.Add(ref received, bytes);
                    }, ct);
                    if (actual == sha256) return;
                    File.Delete(part);
                    last = new InvalidDataException("контрольная сумма не совпала");
                }
                catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    if (File.Exists(part) && await FileIndex.Sha256Async(part, ct) == sha256)
                    {
                        Interlocked.Add(ref received, new FileInfo(part).Length);
                        return;
                    }
                    File.Delete(part);
                    last = e;
                }
                catch (Exception e) when (IsMissing(e))
                {
                    Log.Info($"{asset}: нет на {source}");
                    last = e;
                    break;
                }
                catch (Exception e) when (IsRecoverable(e, ct))
                {
                    last = e;
                    if (e is not HttpRequestException { StatusCode: not null }) Demote(source);
                }
                Interlocked.Add(ref received, -counted);
                Log.Info($"{asset}: попытка {attempt + 1} с {source} не удалась: {last?.Message}");
            }
        }
        throw new IOException($"Не удалось скачать {asset} (последний адрес: {lastSource}): {last?.Message}", last);
    }

    private async Task<string> CopyAsync(string source, string asset, string part, Action<long> count, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        var (input, resumed) = await OpenAsync(source, asset, offset, stall.Token);
        stall.CancelAfter(Timeout.InfiniteTimeSpan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        int read;
        await using (input)
        {
            if (resumed)
            {
                await using var existing = File.OpenRead(part);
                while ((read = await existing.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, read);
                count(offset);
            }
            await using var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 16, true);
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                if ((read = await input.ReadAsync(buffer, stall.Token)) == 0) break;
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                count(read);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<(Stream Stream, bool Resumed)> OpenAsync(string source, string asset, long offset, CancellationToken ct)
    {
        if (!IsWeb(source))
        {
            var file = File.OpenRead(Path.Combine(source, asset));
            file.Seek(offset, SeekOrigin.Begin);
            return (file, offset > 0);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(source), asset));
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"ответ сервера {(int)status}", null, status);
        }
        return (await response.Content.ReadAsStreamAsync(ct), response.StatusCode == HttpStatusCode.PartialContent);
    }

    private static bool IsRecoverable(Exception e, CancellationToken ct) =>
        !ct.IsCancellationRequested && e is HttpRequestException or IOException or OperationCanceledException;

    private static bool IsMissing(Exception e) =>
        e is FileNotFoundException or DirectoryNotFoundException
        || e is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden };

    private static bool IsWeb(string source) =>
        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string source)
    {
        if (!IsWeb(source)) return Path.GetFullPath(source);
        return source.EndsWith('/') ? source : source + "/";
    }

    private List<string> Snapshot()
    {
        lock (gate) return [.. sources];
    }

    private void Demote(string source)
    {
        lock (gate)
        {
            if (sources.Count > 1 && sources.Remove(source)) sources.Add(source);
        }
    }
}
