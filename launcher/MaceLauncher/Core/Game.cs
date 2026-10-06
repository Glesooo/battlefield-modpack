using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
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
            var version = await new ForgeInstaller(launcher, http).Install(manifest.Minecraft, manifest.Forge, new ForgeInstallOptions
            {
                FileProgress = files,
                ByteProgress = bytes,
                InstallerOutput = new Progress<string>(line => Remember(output, line)),
                SkipIfAlreadyInstalled = true,
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

    public async Task<Process> StartAsync(string version, string nick, LauncherSettings settings, CancellationToken ct,
        string? server = null)
    {
        var process = await BuildAsync(version, nick, settings, ct, server);
        process.Start();
        Log.Info($"игра запущена, PID {process.Id}");
        return process;
    }

    public async Task<Process> BuildAsync(string version, string nick, LauncherSettings settings, CancellationToken ct,
        string? server = null)
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
