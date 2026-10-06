using System.Text.Json;

namespace MaceLauncher.Core;

public sealed class LauncherSettings
{
    public const int LowestMemoryMb = 3000;
    public const int MemoryStepMb = 250;
    public const int SmallestWidth = 640;
    public const int SmallestHeight = 480;

    private const int HighestMemoryMb = 16000;
    private const int SystemReserveMb = 1000;
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string Nick { get; set; } = "";
    public int MaxMemoryMb { get; set; } = 4000;
    public int MinMemoryMb { get; set; } = 512;
    public bool FullScreen { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public string JavaArguments { get; set; } = "";

    public static int SystemMemoryMb => (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576);

    public static int MemoryCeilingMb =>
        Math.Clamp((SystemMemoryMb - SystemReserveMb) / MemoryStepMb * MemoryStepMb, LowestMemoryMb, HighestMemoryMb);

    public static string[] SplitArguments(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static LauncherSettings Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Format));
    }
}
