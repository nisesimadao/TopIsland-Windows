using System.IO;
using System.Runtime.InteropServices;

namespace TopIsland.Services;

public sealed record DownloadSnapshot(int ActiveCount, string PrimaryName, long PrimaryBytes, double PrimaryMegabytesPerSecond)
{
    public bool HasActive => ActiveCount > 0;
}

public sealed class DownloadMonitorService
{
    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".opdownload"
    };

    private readonly string _downloadsFolder;
    private readonly Dictionary<string, (long Length, DateTime SampledAt)> _previous = new(StringComparer.OrdinalIgnoreCase);

    public DownloadMonitorService()
    {
        _downloadsFolder = ResolveDownloadsFolder();
    }

    public DownloadSnapshot Sample()
    {
        try
        {
            if (!Directory.Exists(_downloadsFolder))
            {
                return Empty();
            }

            var now = DateTime.UtcNow;
            var files = new DirectoryInfo(_downloadsFolder)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(IsPartialDownload)
                .Where(file => now - file.LastWriteTimeUtc < TimeSpan.FromMinutes(10))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(8)
                .ToArray();

            if (files.Length == 0)
            {
                _previous.Clear();
                return Empty();
            }

            var activePaths = files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _previous.Keys.Where(path => !activePaths.Contains(path)).ToArray())
            {
                _previous.Remove(stale);
            }

            var primary = files[0];
            double bytesPerSecond = 0;
            if (_previous.TryGetValue(primary.FullName, out var previous))
            {
                var seconds = Math.Max((now - previous.SampledAt).TotalSeconds, 0.05);
                bytesPerSecond = Math.Max(0, primary.Length - previous.Length) / seconds;
            }

            foreach (var file in files)
            {
                _previous[file.FullName] = (file.Length, now);
            }

            return new DownloadSnapshot(
                files.Length,
                CleanDisplayName(primary.Name),
                primary.Length,
                bytesPerSecond / 1_000_000.0);
        }
        catch
        {
            return Empty();
        }
    }

    private static bool IsPartialDownload(FileInfo file)
    {
        if (PartialExtensions.Contains(file.Extension))
        {
            return true;
        }

        return file.Name.EndsWith(".tmp.crdownload", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanDisplayName(string name)
    {
        foreach (var extension in PartialExtensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }
        return name;
    }

    private static DownloadSnapshot Empty() => new(0, string.Empty, 0, 0);

    private static string ResolveDownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pathPtr) == 0 && pathPtr != IntPtr.Zero)
        {
            try
            {
                return Marshal.PtrToStringUni(pathPtr) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
