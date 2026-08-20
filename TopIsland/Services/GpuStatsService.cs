using System.Runtime.InteropServices;

namespace TopIsland.Services;

public sealed class GpuStatsService : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;

    private IntPtr _query;
    private IntPtr _counter;
    private bool _ready;

    public GpuStatsService()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != ErrorSuccess || _query == IntPtr.Zero)
            {
                return;
            }

            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != ErrorSuccess)
            {
                Dispose();
                return;
            }

            _ = PdhCollectQueryData(_query);
            _ready = true;
        }
        catch
        {
            Dispose();
        }
    }

    public double SamplePercent()
    {
        if (!_ready || _query == IntPtr.Zero || _counter == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            if (PdhCollectQueryData(_query) != ErrorSuccess)
            {
                return 0;
            }

            uint bufferSize = 0;
            uint itemCount = 0;
            var status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status != PdhMoreData || bufferSize == 0 || itemCount == 0)
            {
                return 0;
            }

            var buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref bufferSize, ref itemCount, buffer);
                if (status != ErrorSuccess)
                {
                    return 0;
                }

                var itemSize = Marshal.SizeOf<PdhFmtCounterValueItemDouble>();
                double total3d = 0;
                double totalAny = 0;
                for (var i = 0; i < itemCount; i++)
                {
                    var itemPtr = IntPtr.Add(buffer, i * itemSize);
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItemDouble>(itemPtr);
                    if (item.Name == IntPtr.Zero || item.Value.CStatus != ErrorSuccess)
                    {
                        continue;
                    }

                    var name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                    var value = Math.Max(0, item.Value.DoubleValue);
                    totalAny += value;
                    if (name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        total3d += value;
                    }
                }

                var result = total3d > 0 ? total3d : totalAny;
                return Math.Clamp(result, 0, 100);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            _ = PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
        }
        _ready = false;
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueDouble
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItemDouble
    {
        public IntPtr Name;
        public PdhFmtCounterValueDouble Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
