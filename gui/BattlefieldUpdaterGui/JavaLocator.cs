using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace BattlefieldUpdaterGui;

/// <summary>
/// Finds a working javaw.exe without assuming it's on PATH. Minecraft launchers routinely
/// bundle their own private JRE and never touch the system PATH at all (PrismLauncher does
/// this, and so do the official Minecraft Launcher, TLauncher, CurseForge/Overwolf, ...) - a
/// player can have Java "installed" and working for the game itself while a naive `where
/// java` check still fails. Hit this directly: a Battlefield player had Java (Minecraft ran
/// fine) but the old .bat updater still reported "Java not found" because PrismLauncher's
/// copy isn't on PATH.
///
/// Deliberately launcher-agnostic - not every player uses PrismLauncher, so this tries PATH
/// first, then the exact launcher config (if the chosen folder happens to be a
/// PrismLauncher/MultiMC-style instance), then a best-effort scan of every launcher's known
/// bundled-runtime location, then the Windows registry as a last resort before giving up.
/// </summary>
internal static class JavaLocator
{
    public static string? Find(string instancePath)
    {
        foreach (string name in new[] { "javaw", "java" })
        {
            if (CanRun(name)) return name;
        }

        string? fromConfig = FindFromLauncherConfig(instancePath);
        if (fromConfig != null) return fromConfig;

        foreach (string root in CandidateRuntimeRoots())
        {
            string? found = FindJavawUnder(root);
            if (found != null) return found;
        }

        return FindFromRegistry();
    }

    private static bool CanRun(string exeName)
    {
        try
        {
            var psi = new ProcessStartInfo(exeName, "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- exact launcher config

    /// <summary>
    /// PrismLauncher/MultiMC/PolyMC store the exact Java path they use to launch Minecraft in
    /// a plain-text .cfg file - per-instance first (most specific), then the launcher's global
    /// default. Only applies if the chosen folder actually has that layout; harmless no-op
    /// (just returns null) for any other launcher.
    /// </summary>
    private static string? FindFromLauncherConfig(string instancePath)
    {
        try
        {
            // instancePath = <launcher root>/instances/<Name>/minecraft
            DirectoryInfo? instanceDir = Directory.GetParent(instancePath);       // .../instances/<Name>
            DirectoryInfo? instancesDir = instanceDir?.Parent;                    // .../instances
            DirectoryInfo? launcherRoot = instancesDir?.Parent;                   // launcher root

            if (instanceDir != null)
            {
                string? perInstance = ReadJavaPathFromCfg(Path.Combine(instanceDir.FullName, "instance.cfg"));
                if (perInstance != null) return perInstance;
            }

            if (launcherRoot != null)
            {
                foreach (string cfgName in new[] { "prismlauncher.cfg", "multimc.cfg", "polymc.cfg" })
                {
                    string? global = ReadJavaPathFromCfg(Path.Combine(launcherRoot.FullName, cfgName));
                    if (global != null) return global;
                }
            }
        }
        catch
        {
            // Best-effort: an unusual folder layout just means "not this kind of launcher".
        }
        return null;
    }

    private static string? ReadJavaPathFromCfg(string cfgPath)
    {
        if (!File.Exists(cfgPath)) return null;
        foreach (string line in File.ReadLines(cfgPath))
        {
            if (!line.StartsWith("JavaPath=", StringComparison.OrdinalIgnoreCase)) continue;
            string path = line["JavaPath=".Length..].Trim().Replace('/', '\\');
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // ---------------------------------------------------------------- known bundled-runtime folders

    private static IEnumerable<string> CandidateRuntimeRoots()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // PrismLauncher and its MultiMC-family relatives.
        yield return Path.Combine(appData, "PrismLauncher", "java");
        yield return Path.Combine(appData, "MultiMC", "java");
        yield return Path.Combine(appData, "PolyMC", "java");

        // Mojang's official launcher bundles its own runtime too, in more than one possible spot
        // depending on install method (classic installer vs Microsoft Store).
        yield return Path.Combine(appData, ".minecraft", "runtime");
        yield return Path.Combine(programFiles, "Minecraft Launcher", "runtime");
        yield return Path.Combine(programFilesX86, "Minecraft Launcher", "runtime");
        yield return Path.Combine(localAppData, "Packages",
            "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime");

        // TLauncher - very common in the CIS/Russian-speaking Minecraft community this pack is
        // aimed at, and it bundles its own runtime the same way.
        yield return Path.Combine(appData, ".tlauncher", "java");
        yield return Path.Combine(appData, ".tlauncher", "java-runtime");

        // CurseForge app (Overwolf-based).
        yield return Path.Combine(localAppData, "Programs", "curseforge");
    }

    private static string? FindJavawUnder(string root)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateFiles(root, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            // Permission errors etc. on an unexpected folder shape - just means "not found here".
            return null;
        }
    }

    // ---------------------------------------------------------------- registry (standalone JDK/JRE installs)

    private static string? FindFromRegistry()
    {
        string[] keyPaths =
        {
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\Eclipse Adoptium\JDK",
            @"SOFTWARE\Eclipse Adoptium\JRE",
            @"SOFTWARE\Microsoft\JDK",
            @"SOFTWARE\Azul Systems\Zulu",
        };

        foreach (string keyPath in keyPaths)
        {
            try
            {
                using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(keyPath);
                if (baseKey == null) continue;
                foreach (string versionName in baseKey.GetSubKeyNames())
                {
                    using RegistryKey? versionKey = baseKey.OpenSubKey(versionName);
                    if (versionKey?.GetValue("JavaHome") is not string home) continue;
                    string exe = Path.Combine(home, "bin", "javaw.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch
            {
                // Some keys need elevation or simply don't exist on this machine - keep looking.
            }
        }
        return null;
    }
}
