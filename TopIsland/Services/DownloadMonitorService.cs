using System.IO;
using System.Runtime.InteropServices;

namespace TopIsland.Services;

public sealed record DownloadItemSnapshot(string Name, long Bytes, double MegabytesPerSecond);
public sealed record DownloadSnapshot(IReadOnlyList<DownloadItemSnapshot> Items)
{
    public int ActiveCount => Items.Count;
    public bool HasActive => Items.Count > 0;
    public DownloadItemSnapshot? Primary => Items.FirstOrDefault();
    public string PrimaryName => Primary?.Name ?? string.Empty;
    public long PrimaryBytes => Primary?.Bytes ?? 0;
    public double PrimaryMegabytesPerSecond => Primary?.MegabytesPerSecond ?? 0;
}

public sealed class DownloadMonitorService
{
    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".opdownload"
    };

    private static readonly TimeSpan IdleScanInterval = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ActivityGrace = TimeSpan.FromSeconds(12);
    private static readonly DownloadSnapshot EmptySnapshot = new(Array.Empty<DownloadItemSnapshot>());

    private readonly string _downloadsFolder = ResolveDownloadsFolder();
    private readonly Dictionary<string, (long Length, DateTime SampledAt)> _previous = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastDirectoryScan = DateTime.MinValue;
    private DownloadSnapshot _cached = EmptySnapshot;

    public DownloadSnapshot Sample()
    {
        var now = DateTime.UtcNow;

        // A full Downloads-folder enumeration is unnecessary every second when
        // nothing is active. Once a partial file is found we return to the
        // caller's normal 1-second cadence so transfer-speed estimates stay useful.
        if (!_cached.HasActive && now - _lastDirectoryScan < IdleScanInterval)
        {
            return _cached;
        }

        try
        {
            _lastDirectoryScan = now;
            if (!Directory.Exists(_downloadsFolder))
            {
                _cached = EmptySnapshot;
                return _cached;
            }

            var files = new DirectoryInfo(_downloadsFolder)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(IsPartialDownload)
                .Where(file => HasRecentActivity(file.LastWriteTimeUtc, now))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(4)
                .ToArray();

            if (files.Length == 0)
            {
                _previous.Clear();
                _cached = EmptySnapshot;
                return _cached;
            }

            var activePaths = files.Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _previous.Keys.Where(path => !activePaths.Contains(path)).ToArray())
            {
                _previous.Remove(stale);
            }

            var items = new List<DownloadItemSnapshot>(files.Length);
            foreach (var file in files)
            {
                double bytesPerSecond = 0;
                if (_previous.TryGetValue(file.FullName, out var previous))
                {
                    var seconds = Math.Max((now - previous.SampledAt).TotalSeconds, 0.05);
                    bytesPerSecond = Math.Max(0, file.Length - previous.Length) / seconds;
                }

                items.Add(new DownloadItemSnapshot(
                    CleanDisplayName(file.Name),
                    file.Length,
                    bytesPerSecond / 1_000_000.0));
                _previous[file.FullName] = (file.Length, now);
            }

            _cached = new DownloadSnapshot(items);
            return _cached;
        }
        catch
        {
            // Keep a previously observed active item for one sample instead of
            // flashing the Downloads module off because of a transient file lock.
            return _cached;
        }
    }

    private static bool IsPartialDownload(FileInfo file) =>
        PartialExtensions.Contains(file.Extension)
        || file.Name.EndsWith(".tmp.crdownload", StringComparison.OrdinalIgnoreCase);

    internal static bool HasRecentActivity(DateTime lastWriteUtc, DateTime nowUtc) =>
        nowUtc >= lastWriteUtc && nowUtc - lastWriteUtc <= ActivityGrace;

    internal static string CleanDisplayName(string name)
    {
        const string compoundChromeExtension = ".tmp.crdownload";
        if (name.EndsWith(compoundChromeExtension, StringComparison.OrdinalIgnoreCase))
        {
            return name[..^compoundChromeExtension.Length];
        }

        foreach (var extension in PartialExtensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }

        return name;
    }

    private static string ResolveDownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pathPtr) == 0 && pathPtr != IntPtr.Zero)
        {
            try
            {
                return Marshal.PtrToStringUni(pathPtr)
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint flags,
        IntPtr token,
        out IntPtr path);
}
