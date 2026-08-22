using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TopIsland.Services;

public sealed class BlurHostService : IDisposable
{
    // HWND property shared with TopIsland.BlurHost. Encode 0..1 as 1..1001 so
    // GetProp == 0 can continue to mean "property unavailable / legacy host".
    internal const string RevealProgressProperty = "TopIsland.BlurRevealProgress";

    private Process? _process;

    public bool IsAvailable => ResolveExecutablePath() is not null;
    public bool IsRunning => _process is { HasExited: false };

    public bool Start(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero)
        {
            return false;
        }

        if (IsRunning)
        {
            return true;
        }

        var executable = ResolveExecutablePath();
        if (executable is null)
        {
            return false;
        }

        try
        {
            _process?.Dispose();
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"--target {targetHwnd.ToInt64()}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
            });
            return _process is not null;
        }
        catch
        {
            _process?.Dispose();
            _process = null;
            return false;
        }
    }


    public static void SetRevealProgress(IntPtr targetHwnd, double progress)
    {
        if (targetHwnd == IntPtr.Zero)
        {
            return;
        }

        var encoded = 1 + (int)Math.Round(Math.Clamp(progress, 0, 1) * 1000);
        _ = SetProp(targetHwnd, RevealProgressProperty, new IntPtr(encoded));
    }

    public static void ClearRevealProgress(IntPtr targetHwnd)
    {
        if (targetHwnd != IntPtr.Zero)
        {
            _ = RemoveProp(targetHwnd, RevealProgressProperty);
        }
    }

    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(800);
            }
        }
        catch
        {
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static string? ResolveExecutablePath()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "BlurHost", "TopIsland.BlurHost.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        // Development layouts. Match the parent app's configuration first; a
        // Release TopIsland must not silently launch a stale Debug BlurHost (and
        // vice versa), otherwise IPC/protocol changes appear to be ignored.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var preferredConfiguration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        var fallbackConfiguration = preferredConfiguration == "Debug" ? "Release" : "Debug";
        var candidates = new[]
        {
            Path.Combine(root, "TopIsland.BlurHost", "bin", preferredConfiguration, "net10.0-windows10.0.19041.0", "win-x64", "TopIsland.BlurHost.exe"),
            Path.Combine(root, "TopIsland.BlurHost", "bin", "x64", preferredConfiguration, "net10.0-windows10.0.19041.0", "win-x64", "TopIsland.BlurHost.exe"),
            Path.Combine(root, "TopIsland.BlurHost", "bin", fallbackConfiguration, "net10.0-windows10.0.19041.0", "win-x64", "TopIsland.BlurHost.exe"),
            Path.Combine(root, "TopIsland.BlurHost", "bin", "x64", fallbackConfiguration, "net10.0-windows10.0.19041.0", "win-x64", "TopIsland.BlurHost.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(IntPtr hwnd, string name, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr hwnd, string name);
}

