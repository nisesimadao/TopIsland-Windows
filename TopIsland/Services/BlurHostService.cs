using System.Diagnostics;
using System.IO;

namespace TopIsland.Services;

public sealed class BlurHostService : IDisposable
{
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

        // Development layouts. Keep these outside the shipped runtime path.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var candidates = new[]
        {
            Path.Combine(root, "TopIsland.BlurHost", "bin", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "TopIsland.BlurHost.exe"),
            Path.Combine(root, "TopIsland.BlurHost", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64", "TopIsland.BlurHost.exe"),
            Path.Combine(root, "TopIsland.BlurHost", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "TopIsland.BlurHost.exe"),
            Path.Combine(root, "TopIsland.BlurHost", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", "win-x64", "TopIsland.BlurHost.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose() => Stop();
}

