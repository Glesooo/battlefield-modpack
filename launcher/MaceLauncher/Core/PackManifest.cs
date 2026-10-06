using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaceLauncher.Core;

public sealed record PackManifest(
    string Revision,
    int Format,
    string Name,
    string Minecraft,
    string Forge,
    int Java,
    IReadOnlyList<string> Sources,
    IReadOnlyDictionary<string, string> Folders,
    IReadOnlyList<PackFile> Files,
    PackBundle Bundle)
{
    public const int SupportedFormat = 1;
    public const string Strict = "strict";
    public const string StrictZip = "strict_zip";
    public const string Additive = "additive";
    public const string Managed = "managed";
    public const string FirstInstall = "first_install";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static PackManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<PackManifest>(json, Options)
            ?? throw new InvalidDataException("Пустой список сборки");
        if (manifest.Format != SupportedFormat)
        {
            throw new InvalidDataException("Список сборки новее лаунчера: обновите лаунчер");
        }
        ManifestRules.Check(manifest);
        return manifest;
    }
}

public sealed record PackFile(string Path, long Size, string Sha256, string Asset);

public sealed record PackBundle(string Asset, long Size, string Sha256, IReadOnlyList<BundleFile> Files);

public sealed record BundleFile(string Path, string Sha256, string Mode);

public sealed record SyncReport(int Downloaded, long Bytes, IReadOnlyList<string> Moved, int ConfigsWritten);

public static partial class ManifestRules
{
    public static void Check(PackManifest manifest)
    {
        Require(manifest.Sources is not null && manifest.Folders is not null && manifest.Files is not null
            && manifest.Bundle?.Files is not null, "нет обязательных разделов");
        Require(!string.IsNullOrWhiteSpace(manifest.Minecraft) && !string.IsNullOrWhiteSpace(manifest.Forge),
            "не указаны версии игры");

        foreach (var source in manifest.Sources)
        {
            Require(Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
                $"адрес источника {source}");
        }
        foreach (var (folder, policy) in manifest.Folders)
        {
            Require(Folder().IsMatch(folder), $"имя папки {folder}");
            Require(policy is PackManifest.Strict or PackManifest.StrictZip or PackManifest.Additive,
                $"правило папки {folder}");
        }
        foreach (var file in manifest.Files)
        {
            Require(file is not null, "пустая запись файла");
            Require(IsSafePath(file.Path) && file.Size >= 0, $"файл {file.Path}");
            Require(IsAsset(file.Asset) && IsSha(file.Sha256), $"имя или сумма файла {file.Path}");
        }
        Require(IsAsset(manifest.Bundle.Asset) && IsSha(manifest.Bundle.Sha256), "архив конфигов");
        foreach (var file in manifest.Bundle.Files)
        {
            Require(file is not null, "пустая запись конфига");
            Require(IsSafePath(file.Path) && IsSha(file.Sha256), $"конфиг {file.Path}");
            Require(file.Mode is PackManifest.Managed or PackManifest.FirstInstall, $"режим конфига {file.Path}");
        }
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool ok, string what)
    {
        if (!ok) throw new InvalidDataException("Список сборки повреждён: " + what);
    }

    private static bool IsSafePath(string? path) =>
        !string.IsNullOrEmpty(path) && SafePath().IsMatch(path)
        && path.Split('/').All(segment => segment is not ("." or "..") && segment == segment.Trim() && !segment.EndsWith('.'));

    private static bool IsAsset(string? asset) => asset is not null && Asset().IsMatch(asset);

    private static bool IsSha(string? sha) => sha is not null && Sha().IsMatch(sha);

    [GeneratedRegex(@"^[^\\/:*?""<>|\x00-\x1f]+(/[^\\/:*?""<>|\x00-\x1f]+)*\z")]
    private static partial Regex SafePath();

    [GeneratedRegex(@"^[0-9a-f]{16}-[A-Za-z0-9._-]+\z")]
    private static partial Regex Asset();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex Sha();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z")]
    private static partial Regex Folder();
}
