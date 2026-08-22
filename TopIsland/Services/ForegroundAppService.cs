using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TopIsland.Services;

public sealed record ForegroundAppSnapshot(
    string Title,
    string ProcessName,
    int ProcessId,
    long WorkingSetBytes,
    double CpuPercent,
    int ThreadCount,
    DateTimeOffset? StartedAt,
    byte[]? IconPng);

public sealed class ForegroundAppService
{
    private string? _lastIconPath;
    private byte[]? _lastIconPng;
    private int _lastCpuProcessId;
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastCpuSampleAt;

    public ForegroundAppSnapshot Sample()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return Empty();
            }

            var titleLength = GetWindowTextLength(hwnd);
            var titleBuffer = new StringBuilder(Math.Max(1, titleLength + 1));
            _ = GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity);
            _ = GetWindowThreadProcessId(hwnd, out var processId);

            var processName = "Windows";
            var workingSetBytes = 0L;
            var cpuPercent = 0d;
            var threadCount = 0;
            DateTimeOffset? startedAt = null;
            byte[]? icon = null;
            if (processId != 0)
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
                workingSetBytes = Math.Max(0, process.WorkingSet64);
                cpuPercent = SampleCpuPercent(process);
                try { threadCount = process.Threads.Count; } catch { threadCount = 0; }
                try { startedAt = process.StartTime; } catch { startedAt = null; }
                icon = TryGetIcon(process);
            }

            var title = titleBuffer.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = processName;
            }

            if (title.Length > 72)
            {
                title = title[..71] + "…";
            }

            return new ForegroundAppSnapshot(
                title,
                processName,
                (int)processId,
                workingSetBytes,
                cpuPercent,
                threadCount,
                startedAt,
                icon);
        }
        catch
        {
            return Empty();
        }
    }

    private double SampleCpuPercent(Process process)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cpu = process.TotalProcessorTime;
            var pid = process.Id;
            var percent = 0d;
            if (_lastCpuProcessId == pid && _lastCpuSampleAt != default)
            {
                var elapsedMs = (now - _lastCpuSampleAt).TotalMilliseconds;
                var cpuMs = (cpu - _lastCpuTime).TotalMilliseconds;
                if (elapsedMs > 20 && cpuMs >= 0)
                {
                    percent = Math.Clamp(cpuMs / elapsedMs / Math.Max(1, Environment.ProcessorCount) * 100d, 0, 100);
                }
            }

            _lastCpuProcessId = pid;
            _lastCpuTime = cpu;
            _lastCpuSampleAt = now;
            return percent;
        }
        catch
        {
            return 0;
        }
    }

    private byte[]? TryGetIcon(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (string.Equals(path, _lastIconPath, StringComparison.OrdinalIgnoreCase))
            {
                return _lastIconPng;
            }

            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                _lastIconPath = path;
                _lastIconPng = null;
                return null;
            }

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            _lastIconPath = path;
            _lastIconPng = stream.ToArray();
            return _lastIconPng;
        }
        catch
        {
            return null;
        }
    }

    private static ForegroundAppSnapshot Empty() => new(
        "Desktop",
        "Windows",
        0,
        0,
        0,
        0,
        null,
        null);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
