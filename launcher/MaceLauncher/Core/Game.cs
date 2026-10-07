using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;

namespace MaceLauncher.Core;

public sealed class Game(LauncherPaths paths, HttpClient http)
{
    private const string OfflineUserType = "legacy";
    private static readonly string[] JvmArguments = ["-Xss1024k"];

    public async Task<string> InstallAsync(PackManifest manifest, IProgress<LauncherProgress>? progress, CancellationToken ct)
    {
        var launcher = CreateLauncher();
        var stage = "Установка Minecraft";
        var name = "";
        var files = new Progress<InstallerProgressChangedEventArgs>(e => name = e.Name ?? "");
        var bytes = new Progress<ByteProgress>(b => progress?.Report(new(stage, name, b.ToRatio())));

        await launcher.InstallAsync(manifest.Minecraft, files, bytes, ct);
        stage = "Установка Forge";
        var output = new Queue<string>();
        try
        {
            var version = await InstallForgeAsync(launcher, manifest, new ForgeInstallOptions
            {
                FileProgress = files,
                ByteProgress = bytes,
                InstallerOutput = new Progress<string>(line => Remember(output, line)),
                CancellationToken = ct,
            });
            Log.Info($"игра установлена: {version}");
            return version;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            lock (output) Log.Info("последний вывод установщика Forge:" + Environment.NewLine + string.Join(Environment.NewLine, output));
            throw;
        }
    }

    private async Task<string> InstallForgeAsync(MinecraftLauncher launcher, PackManifest manifest, ForgeInstallOptions options)
    {
        var ct = options.CancellationToken;
        var expected = $"{manifest.Minecraft}-forge-{manifest.Forge}";
        if (await IsInstalledAsync(launcher, expected, ct)) return expected;

        var forge = (await new ForgeVersionLoader(http).GetForgeVersions(manifest.Minecraft))
            .FirstOrDefault(v => v.ForgeVersionName == manifest.Forge)
            ?? throw new InvalidOperationException($"Forge {manifest.Forge} для Minecraft {manifest.Minecraft} не найден.");
        var installer = new ForgeInstallerVersionMapper().CreateInstaller(forge);
        if (await IsInstalledAsync(launcher, installer.VersionName, ct)) return installer.VersionName;

        var minecraft = await launcher.GetVersionAsync(manifest.Minecraft, ct);
        options.JavaPath = launcher.GetJavaPath(minecraft)
            ?? throw new InvalidOperationException("Не найдена Java для установки Forge.");
        await installer.Install(launcher.MinecraftPath, launcher.GameInstaller, options);
        await launcher.GetAllVersionsAsync(ct);
        return installer.VersionName;
    }

    private static async Task<bool> IsInstalledAsync(MinecraftLauncher launcher, string version, CancellationToken ct)
    {
        try
        {
            await launcher.GetVersionAsync(version, ct);
            return true;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    public async Task<Process> StartAsync(string version, string nick, NickKey? key, LauncherSettings settings,
        CancellationToken ct, string? server = null)
    {
        var process = await BuildAsync(version, nick, key, settings, ct, server);
        process.Start();
        Log.Info($"игра запущена, PID {process.Id}");
        return process;
    }

    public async Task<Process> BuildAsync(string version, string nick, NickKey? key, LauncherSettings settings,
        CancellationToken ct, string? server = null)
    {
        var session = MSession.CreateOfflineSession(nick);
        session.UUID = OfflineProfile.Uuid(nick);
        session.UserType = OfflineUserType;
        var options = new MLaunchOption
        {
            Session = session,
            MaximumRamMb = settings.MaxMemoryMb,
            MinimumRamMb = Math.Min(settings.MinMemoryMb, settings.MaxMemoryMb),
            ExtraJvmArguments = JvmArguments.Concat(LauncherSettings.SplitArguments(settings.JavaArguments))
                .Select(a => new MArgument(a)).ToList(),
            FullScreen = settings.FullScreen,
            ScreenWidth = settings.WindowWidth,
            ScreenHeight = settings.WindowHeight,
            GameLauncherName = "M.A.C.E",
            GameLauncherVersion = LauncherInfo.Version,
        };
        if (server != null) options.ServerIp = server;
        var process = await CreateLauncher().InstallAndBuildProcessAsync(version, options, ct);
        Log.Info($"команда запуска: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
        Log.Info("ключ ника: " + (key?.PublicKey ?? "нет"));
        if (key != null)
        {
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.Environment[NickKey.PrivateVariable] = key.PrivateKey;
            process.StartInfo.Environment[NickKey.PublicVariable] = key.PublicKey;
        }
        return process;
    }

    private static void Remember(Queue<string> output, string line)
    {
        lock (output)
        {
            output.Enqueue(line);
            if (output.Count > 200) output.Dequeue();
        }
    }

    private MinecraftLauncher CreateLauncher()
    {
        var path = new MinecraftPath(paths.Instance)
        {
            Library = Path.Combine(paths.Game, "libraries"),
            Versions = Path.Combine(paths.Game, "versions"),
            Assets = Path.Combine(paths.Game, "assets"),
            Runtime = Path.Combine(paths.Game, "runtime"),
            Resource = Path.Combine(paths.Game, "resources"),
        };
        path.CreateDirs();
        return new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(path, http));
    }
}
