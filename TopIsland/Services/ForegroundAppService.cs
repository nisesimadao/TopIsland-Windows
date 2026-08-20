using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TopIsland.Services;

public sealed record ForegroundAppSnapshot(string Title, string ProcessName);

public sealed class ForegroundAppService
{
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

            var processName = processId == 0
                ? "Windows"
                : Process.GetProcessById((int)processId).ProcessName;

            var title = titleBuffer.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = processName;
            }

            if (title.Length > 72)
            {
                title = title[..71] + "…";
            }

            return new ForegroundAppSnapshot(title, processName);
        }
        catch
        {
            return Empty();
        }
    }

    private static ForegroundAppSnapshot Empty() => new("Desktop", "Windows");

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
