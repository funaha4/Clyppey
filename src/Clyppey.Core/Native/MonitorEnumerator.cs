using System.Runtime.InteropServices;

namespace Clyppey.Core.Native;

public sealed record MonitorInfo(
    int Index,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary,
    string DeviceName)
{
    public string Label =>
        $"モニター {Index + 1}  ·  {Width}×{Height}" + (IsPrimary ? "  (プライマリ)" : string.Empty);
}

/// <summary>
/// Lists the capture targets. The index is the DXGI output index that ffmpeg's
/// "ddagrab=output_idx=" expects, obtained from DXGI itself so it cannot drift from what
/// Windows calls the display. The UI additionally shows a live thumbnail per index.
/// </summary>
public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var gdi = EnumerateGdiMonitors();
        var dxgi = DxgiOutputs.Enumerate();

        if (dxgi.Count > 0)
        {
            return dxgi
                .Where(o => o.Attached)
                .Select(o => new MonitorInfo(
                    o.Index,
                    o.Left,
                    o.Top,
                    o.Width,
                    o.Height,
                    gdi.Any(g => g.Left == o.Left && g.Top == o.Top &&
                                 g.Width == o.Width && g.Height == o.Height && g.IsPrimary),
                    gdi.FirstOrDefault(g => g.Left == o.Left && g.Top == o.Top)?.DeviceName ?? string.Empty))
                .ToList();
        }

        // Fallback: GDI order. Index may not match DXGI exactly, so the UI preview matters.
        return gdi.Select((m, i) => m with { Index = i }).ToList();
    }

    private static List<MonitorInfo> EnumerateGdiMonitors()
    {
        var list = new List<MonitorInfo>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
                {
                    var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
                    var primary = false;
                    var name = string.Empty;
                    if (GetMonitorInfo(hMonitor, ref info))
                    {
                        primary = (info.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
                        name = info.szDevice;
                    }

                    list.Add(new MonitorInfo(
                        list.Count,
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        Math.Max(0, info.rcMonitor.Right - info.rcMonitor.Left),
                        Math.Max(0, info.rcMonitor.Bottom - info.rcMonitor.Top),
                        primary,
                        name));

                    return true;
                }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"モニターの列挙に失敗: {ex.Message}");
        }

        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);
}
