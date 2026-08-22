using LibreHardwareMonitor.Hardware;
using System.Runtime.InteropServices;

namespace TopIsland.Services;

public sealed record HardwareTelemetry(double? CpuTemperatureC, double? GpuTemperatureC, string PowerMode);

public sealed class HardwareTelemetryService : IDisposable
{
    private readonly Computer _computer;
    private DateTime _lastSample = DateTime.MinValue;
    private HardwareTelemetry _cached = new(null, null, "Unknown");

    public HardwareTelemetryService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };
        try { _computer.Open(); } catch { }
    }

    public HardwareTelemetry Sample()
    {
        if (DateTime.UtcNow - _lastSample < TimeSpan.FromSeconds(2))
        {
            return _cached;
        }

        double? cpu = null;
        double? gpu = null;
        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                foreach (var sub in hardware.SubHardware)
                {
                    sub.Update();
                }

                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    cpu = ReadBestTemperature(hardware, "Package", "Tctl", "Core Average");
                }
                else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                {
                    gpu = ReadBestTemperature(hardware, "GPU Core", "Core", "Hot Spot");
                }
            }
        }
        catch { }

        _cached = new HardwareTelemetry(cpu, gpu, ReadPowerMode());
        _lastSample = DateTime.UtcNow;
        return _cached;
    }

    private static double? ReadBestTemperature(IHardware hardware, params string[] preferredNames)
    {
        var sensors = hardware.Sensors
            .Where(sensor => sensor.SensorType == SensorType.Temperature && sensor.Value is >= 10 and <= 125)
            .ToArray();
        foreach (var preferred in preferredNames)
        {
            var match = sensors.FirstOrDefault(sensor => sensor.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (match?.Value is float value) return value;
        }
        return sensors.Select(sensor => sensor.Value).FirstOrDefault(value => value.HasValue);
    }

    private static string ReadPowerMode()
    {
        IntPtr schemePtr = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out schemePtr) != 0 || schemePtr == IntPtr.Zero)
            {
                return "Unknown";
            }
            var guid = Marshal.PtrToStructure<Guid>(schemePtr);
            if (guid == new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")) return "High performance";
            if (guid == new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")) return "Balanced";
            if (guid == new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")) return "Power saver";
            if (guid == new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")) return "Ultimate performance";
            return "Custom power plan";
        }
        catch { return "Unknown"; }
        finally { if (schemePtr != IntPtr.Zero) LocalFree(schemePtr); }
    }

    public void Dispose()
    {
        try { _computer.Close(); } catch { }
        GC.SuppressFinalize(this);
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
