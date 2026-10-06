using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MaceLauncher.Core;

public static class FileHolders
{
    private const int MoreData = 234;
    private const int SessionKeyLength = 33;

    public static IReadOnlyList<string> Of(string[] paths)
    {
        if (RmStartSession(out var session, 0, new StringBuilder(SessionKeyLength)) != 0) return [];
        try
        {
            var existing = paths.Where(File.Exists).ToArray();
            if (existing.Length == 0) return [];
            if (RmRegisterResources(session, (uint)existing.Length, existing, 0, IntPtr.Zero, 0, null) != 0) return [];
            uint count = 0;
            uint reasons = 0;
            if (RmGetList(session, out var needed, ref count, null, ref reasons) != MoreData) return [];
            var found = new ProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, found, ref reasons) != 0) return [];
            return found.Take((int)count)
                .Where(info => info.Process.Id != Environment.ProcessId)
                .Select(Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static string Name(ProcessInfo info)
    {
        try
        {
            return Process.GetProcessById(info.Process.Id).ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return info.AppName;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int Id;
        public long StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public int ApplicationType;
        public uint Status;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint appCount,
        IntPtr apps, uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[]? found, ref uint rebootReasons);
}
