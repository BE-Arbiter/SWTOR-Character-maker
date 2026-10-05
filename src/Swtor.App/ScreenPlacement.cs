using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;

namespace Swtor.App;

/// <summary>
/// Puts the window on a chosen monitor. Used by the SWTOR_SCREEN test option: "1" is the primary monitor,
/// "2" is the next one from left to right, and so on. Does nothing on other systems than Windows.
/// </summary>
internal static class ScreenPlacement
{
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Moves the window to the center of monitor number <paramref name="number"/> (1 based). Ignores bad numbers.</summary>
    public static void Apply(GameWindow window, int width, int height, int number)
    {
        if (!OperatingSystem.IsWindows() || number < 1) return;
        var monitors = new List<(Rect Area, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) monitors.Add((info.Monitor, (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);

        var ordered = monitors.OrderByDescending(m => m.Primary).ThenBy(m => m.Area.Left).ToList();
        if (number > ordered.Count) return;
        var area = ordered[number - 1].Area;
        int x = area.Left + Math.Max(0, (area.Right - area.Left - width) / 2);
        int y = area.Top + Math.Max(0, (area.Bottom - area.Top - height) / 2);
        window.Position = new Point(x, y);
    }
}
