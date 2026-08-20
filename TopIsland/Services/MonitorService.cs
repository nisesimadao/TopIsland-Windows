using System.Runtime.InteropServices;
using TopIsland.Models;

namespace TopIsland.Services;

public sealed record MonitorDescriptor(
    IntPtr Handle,
    string DeviceName,
    int Left,
    int Top,
    int Right,
    int Bottom,
    int ScalePercent,
    bool IsPrimary)
{
    public int PixelWidth => Right - Left;
    public int PixelHeight => Bottom - Top;
    public double Scale => ScalePercent / 100.0;
    public double DipWidth => PixelWidth / Scale;
    public double DipHeight => PixelHeight / Scale;
    public string DisplayLabel => $"{PixelWidth}×{PixelHeight}  {ScalePercent}%";
}

public sealed class MonitorService
{
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const int MonitorInfoPrimary = 1;
    private const int EnumCurrentSettings = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

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
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int ICMMethod;
        public int ICMIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var result = new List<MonitorDescriptor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var descriptor = Describe(monitor);
            if (descriptor is not null)
            {
                result.Add(descriptor);
            }
            return true;
        }, IntPtr.Zero);

        return result
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Left)
            .ThenBy(m => m.Top)
            .ToArray();
    }

    public MonitorDescriptor GetPrimary()
    {
        var monitor = MonitorFromPoint(new NativePoint(0, 0), MonitorDefaultToPrimary);
        return Describe(monitor) ?? GetMonitors().First();
    }

    public MonitorDescriptor GetForegroundMonitor()
    {
        var hwnd = GetForegroundWindow();
        return hwnd == IntPtr.Zero ? GetPrimary() : GetForWindow(hwnd);
    }

    public MonitorDescriptor GetForWindow(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        return Describe(monitor) ?? GetPrimary();
    }

    public MonitorDescriptor Resolve(AppSettings settings)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("Windows did not report any displays.");
        }

        return settings.MonitorMode switch
        {
            MonitorMode.FollowActiveApp => GetForegroundMonitor(),
            MonitorMode.Fixed when !string.IsNullOrWhiteSpace(settings.MonitorDeviceName)
                => monitors.FirstOrDefault(m => string.Equals(m.DeviceName, settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
                   ?? monitors.FirstOrDefault(m => m.IsPrimary)
                   ?? monitors[0],
            _ => monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]
        };
    }

    public void PositionWindow(IntPtr hwnd, MonitorDescriptor monitor, double topDip)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var width = rect.Right - rect.Left;
        var x = monitor.Left + (monitor.PixelWidth - width) / 2;
        var y = monitor.Top + (int)Math.Round(topDip * monitor.Scale);
        _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private MonitorDescriptor? Describe(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfoEx
        {
            Size = Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty
        };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        var left = info.Monitor.Left;
        var top = info.Monitor.Top;
        var right = info.Monitor.Right;
        var bottom = info.Monitor.Bottom;

        var mode = new DevMode
        {
            DeviceName = string.Empty,
            FormName = string.Empty,
            Size = (short)Marshal.SizeOf<DevMode>()
        };
        if (EnumDisplaySettings(info.DeviceName, EnumCurrentSettings, ref mode))
        {
            left = mode.PositionX;
            top = mode.PositionY;
            right = left + mode.PelsWidth;
            bottom = top + mode.PelsHeight;
        }

        var scalePercent = 100;
        try
        {
            if (GetScaleFactorForMonitor(monitor, out var scale) == 0 && scale >= 100)
            {
                scalePercent = scale;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return new MonitorDescriptor(
            monitor,
            info.DeviceName,
            left,
            top,
            right,
            bottom,
            scalePercent,
            (info.Flags & MonitorInfoPrimary) != 0);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode mode);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
}