using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TopIsland.Services;

public sealed record SystemStats(
    double CpuPercent,
    double GpuPercent,
    double RamPercent,
    double DownloadMbps,
    double UploadMbps,
    bool HasBattery,
    double BatteryPercent,
    bool BatteryCharging,
    long StorageFreeBytes,
    long StorageTotalBytes);

public sealed class SystemStatsService : IDisposable
{
    private readonly GpuStatsService _gpu = new();
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private long _lastReceived;
    private long _lastSent;
    private DateTime _lastNetworkSample = DateTime.UtcNow;
    private DateTime _lastStorageSample = DateTime.MinValue;
    private long _storageFree;
    private long _storageTotal;
    private bool _initialized;

    public SystemStats Sample()
    {
        var cpu = ReadCpu();
        var gpu = _gpu.SamplePercent();
        var ram = ReadRam();
        var (down, up) = ReadNetwork();
        var (hasBattery, battery, charging) = ReadBattery();
        var (storageFree, storageTotal) = ReadStorage();
        return new SystemStats(cpu, gpu, ram, down, up, hasBattery, battery, charging, storageFree, storageTotal);
    }

    private double ReadCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            return 0;
        }

        var idle = ToUInt64(idleFt);
        var kernel = ToUInt64(kernelFt);
        var user = ToUInt64(userFt);

        if (!_initialized)
        {
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _initialized = true;
            return 0;
        }

        var idleDelta = idle - _lastIdle;
        var kernelDelta = kernel - _lastKernel;
        var userDelta = user - _lastUser;
        var total = kernelDelta + userDelta;

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        return total == 0 ? 0 : Math.Clamp((1.0 - (double)idleDelta / total) * 100.0, 0, 100);
    }

    private static double ReadRam()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : 0;
    }

    private (double down, double up) ReadNetwork()
    {
        long received = 0;
        long sent = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                var stats = nic.GetIPv4Statistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
            catch
            {
                // Ignore adapters that do not expose counters.
            }
        }

        var now = DateTime.UtcNow;
        var seconds = Math.Max((now - _lastNetworkSample).TotalSeconds, 0.001);
        var down = _lastReceived == 0 ? 0 : Math.Max(0, received - _lastReceived) * 8.0 / 1_000_000.0 / seconds;
        var up = _lastSent == 0 ? 0 : Math.Max(0, sent - _lastSent) * 8.0 / 1_000_000.0 / seconds;

        _lastReceived = received;
        _lastSent = sent;
        _lastNetworkSample = now;
        return (down, up);
    }

    private static (bool hasBattery, double percent, bool charging) ReadBattery()
    {
        try
        {
            var power = System.Windows.Forms.SystemInformation.PowerStatus;
            var noBattery = power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery);
            if (noBattery || power.BatteryLifePercent < 0)
            {
                return (false, 0, false);
            }

            return (
                true,
                Math.Clamp(power.BatteryLifePercent * 100.0, 0, 100),
                power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging));
        }
        catch
        {
            return (false, 0, false);
        }
    }

    private (long free, long total) ReadStorage()
    {
        if (DateTime.UtcNow - _lastStorageSample < TimeSpan.FromSeconds(10) && _storageTotal > 0)
        {
            return (_storageFree, _storageTotal);
        }

        try
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            var drive = DriveInfo.GetDrives().FirstOrDefault(d =>
                d.IsReady && string.Equals(d.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase));
            if (drive is not null)
            {
                _storageFree = drive.AvailableFreeSpace;
                _storageTotal = drive.TotalSize;
            }
        }
        catch
        {
            _storageFree = 0;
            _storageTotal = 0;
        }

        _lastStorageSample = DateTime.UtcNow;
        return (_storageFree, _storageTotal);
    }

    public void Dispose()
    {
        _gpu.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ulong ToUInt64(FileTime ft) => ((ulong)ft.HighDateTime << 32) | ft.LowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
