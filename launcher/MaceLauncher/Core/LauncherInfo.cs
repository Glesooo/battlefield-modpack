namespace MaceLauncher.Core;

public static class LauncherInfo
{
    public static readonly IReadOnlyList<string> Sources =
        ["https://github.com/Glesooo/battlefield-modpack/releases/download/pack/"];

    public static string Version { get; } =
        typeof(LauncherInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MACE-Launcher/{Version}");
        return http;
    }
}

public sealed record LauncherProgress(string Stage, string Detail, double Fraction);
