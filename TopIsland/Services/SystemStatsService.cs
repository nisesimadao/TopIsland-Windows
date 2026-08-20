using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TopIsland.Services;

public sealed record SystemStats(double CpuPercent, double RamPercent, double DownloadMbps, double UploadMbps);

public sealed class SystemStatsService
{
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private long _lastReceived;
    private long _lastSent;
    private DateTime _lastNetworkSample = DateTime.UtcNow;
    private bool _initialized;

    public SystemStats Sample()
    {
        var cpu = ReadCpu();
        var ram = ReadRam();
        var (down, up) = ReadNetwork();
        return new SystemStats(cpu, ram, down, up);
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
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : 0;
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

    private static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
