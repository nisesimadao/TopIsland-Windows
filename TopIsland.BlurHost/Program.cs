using System.Diagnostics;

if (!OperatingSystem.IsWindows())
{
    return;
}

var target = IntPtr.Zero;
for (var i = 0; i < args.Length - 1; i++)
{
    if (!args[i].Equals("--target", StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    var value = args[i + 1];
    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        if (long.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out var parsed))
        {
            target = new IntPtr(parsed);
        }
    }
    else if (long.TryParse(value, out var parsed))
    {
        target = new IntPtr(parsed);
    }
}

if (target == IntPtr.Zero)
{
    var process = Process.GetProcessesByName("TopIsland").FirstOrDefault();
    target = process?.MainWindowHandle ?? IntPtr.Zero;
}

if (target == IntPtr.Zero)
{
    return;
}

using var host = new TopIsland.BlurHost.BlurWindow(target);
host.Run();
