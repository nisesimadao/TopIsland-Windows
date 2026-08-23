using System.Runtime.InteropServices;

namespace TopIsland.Services;

/// <summary>Represents a Windows power scheme that TopIsland can safely expose as a compact control.</summary>
public sealed record PowerModeOption(Guid Scheme, string Label);

/// <summary>Describes the active power scheme and the usable schemes discovered on this PC.</summary>
public sealed record PowerModeSnapshot(bool Available, string ActiveLabel, IReadOnlyList<PowerModeOption> Options)
{
    /// <summary>Gets the unavailable state used when Windows does not expose controllable schemes.</summary>
    public static PowerModeSnapshot Unavailable { get; } = new(false, "Unavailable", Array.Empty<PowerModeOption>());
}

/// <summary>Enumerates and changes the current user's recognized Windows power schemes.</summary>
public sealed class PowerModeService
{
    private const uint AccessScheme = 16;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorMoreData = 234;
    private static readonly IReadOnlyDictionary<Guid, string> KnownSchemes = new Dictionary<Guid, string>
    {
        [new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")] = "Efficiency",
        [new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")] = "Balanced",
        [new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")] = "Performance",
        [new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")] = "Performance"
    };

    /// <summary>Reads the current scheme and the recognized schemes available to the current user.</summary>
    /// <returns>A snapshot that is unavailable when the native power APIs cannot be used.</returns>
    public PowerModeSnapshot Sample()
    {
        var options = EnumerateKnownSchemes();
        if (options.Count == 0 || !TryGetActiveScheme(out var active))
        {
            return PowerModeSnapshot.Unavailable;
        }

        var activeLabel = options.FirstOrDefault(option => option.Scheme == active)?.Label ?? "Custom";
        return new PowerModeSnapshot(true, activeLabel, options);
    }

    /// <summary>Activates the next discovered scheme and verifies the change.</summary>
    /// <returns>The updated state, or unavailable when Windows did not accept the change.</returns>
    public PowerModeSnapshot Cycle()
    {
        var snapshot = Sample();
        if (!snapshot.Available || snapshot.Options.Count < 2 || !TryGetActiveScheme(out var active))
        {
            return snapshot;
        }

        var index = snapshot.Options.ToList().FindIndex(option => option.Scheme == active);
        var next = snapshot.Options[(Math.Max(index, -1) + 1) % snapshot.Options.Count];
        var nextScheme = next.Scheme;
        return PowerSetActiveScheme(IntPtr.Zero, ref nextScheme) == 0 ? Sample() : snapshot;
    }

    private static List<PowerModeOption> EnumerateKnownSchemes()
    {
        var options = new List<PowerModeOption>();
        for (uint index = 0; ; index++)
        {
            uint size = 0;
            var result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, IntPtr.Zero, ref size);
            if (result == ErrorNoMoreItems) break;
            if (result is not 0 and not ErrorMoreData || size != Marshal.SizeOf<Guid>()) continue;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size);
                if (result != 0) continue;
                var scheme = Marshal.PtrToStructure<Guid>(buffer);
                if (KnownSchemes.TryGetValue(scheme, out var label) && options.All(option => option.Scheme != scheme))
                {
                    options.Add(new PowerModeOption(scheme, label));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return options;
    }

    private static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out pointer) != 0 || pointer == IntPtr.Zero) return false;
            scheme = Marshal.PtrToStructure<Guid>(pointer);
            return true;
        }
        finally
        {
            if (pointer != IntPtr.Zero) _ = LocalFree(pointer);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid, uint accessFlags, uint index, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
