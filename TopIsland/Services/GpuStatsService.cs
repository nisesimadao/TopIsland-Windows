using System.Runtime.InteropServices;

namespace TopIsland.Services;

public sealed class GpuStatsService : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;

    private IntPtr _query;
    private IntPtr _counter;
    private IntPtr _buffer;
    private uint _bufferCapacity;
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

            uint requiredSize = 0;
            uint itemCount = 0;
            var status = PdhGetFormattedCounterArray(
                _counter,
                PdhFmtDouble,
                ref requiredSize,
                ref itemCount,
                IntPtr.Zero);
            if (status != PdhMoreData || requiredSize == 0 || itemCount == 0)
            {
                return 0;
            }

            EnsureBuffer(requiredSize);
            var availableSize = _bufferCapacity;
            status = PdhGetFormattedCounterArray(
                _counter,
                PdhFmtDouble,
                ref availableSize,
                ref itemCount,
                _buffer);
            if (status != ErrorSuccess)
            {
                return 0;
            }

            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItemDouble>();
            double total3d = 0;
            double totalAny = 0;
            for (var i = 0; i < itemCount; i++)
            {
                var itemPtr = IntPtr.Add(_buffer, i * itemSize);
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
        catch
        {
            return 0;
        }
    }

    private void EnsureBuffer(uint requiredSize)
    {
        if (_buffer != IntPtr.Zero && _bufferCapacity >= requiredSize)
        {
            return;
        }

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
        }

        // Keep a little spare capacity because the set of GPU engine instances can
        // fluctuate as applications create/destroy contexts.
        _bufferCapacity = Math.Max(requiredSize, requiredSize + requiredSize / 4);
        _buffer = Marshal.AllocHGlobal(checked((int)_bufferCapacity));
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferCapacity = 0;
        }

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
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
