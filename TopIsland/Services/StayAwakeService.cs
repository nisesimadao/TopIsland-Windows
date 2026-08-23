using System.Runtime.InteropServices;

namespace TopIsland.Services;

/// <summary>Owns TopIsland's optional system-sleep execution requirement.</summary>
public sealed class StayAwakeService : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    /// <summary>Gets whether this process currently requests that the system remain awake.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Changes the execution requirement without forcing the display to remain on.</summary>
    /// <param name="enabled">Whether TopIsland should prevent automatic system sleep.</param>
    /// <returns><see langword="true"/> when Windows accepted the requested state.</returns>
    public bool SetEnabled(bool enabled)
    {
        var flags = enabled ? EsContinuous | EsSystemRequired : EsContinuous;
        if (SetThreadExecutionState(flags) == 0)
        {
            return false;
        }

        IsEnabled = enabled;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ = SetEnabled(false);
        GC.SuppressFinalize(this);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint executionState);
}
