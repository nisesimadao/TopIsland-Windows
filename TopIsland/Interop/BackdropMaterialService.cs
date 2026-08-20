using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TopIsland.Models;
using TopIsland.Services;

namespace TopIsland.Interop;

public sealed record BackdropApplyResult(bool NativeSupported, bool NativeApplied, string NativeKind);

public sealed class BackdropMaterialService
{
    private const int DwmaUseImmersiveDarkMode = 20;
    private const int DwmaSystemBackdropType = 38;

    private enum DwmSystemBackdropType
    {
        Auto = 0,
        None = 1,
        MainWindow = 2,
        TransientWindow = 3,
        TabbedWindow = 4
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public BackdropApplyResult Apply(Window window, SurfaceMaterial material, AppThemeMode theme, ThemeService themeService)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return new BackdropApplyResult(false, false, "Unavailable");
        }

        var dark = themeService.ResolveLight(theme) ? 0 : 1;
        _ = DwmSetWindowAttribute(hwnd, DwmaUseImmersiveDarkMode, ref dark, sizeof(int));

        var nativeKind = material switch
        {
            SurfaceMaterial.Mica => DwmSystemBackdropType.MainWindow,
            SurfaceMaterial.Acrylic => DwmSystemBackdropType.TransientWindow,
            SurfaceMaterial.Glass => DwmSystemBackdropType.TransientWindow,
            SurfaceMaterial.MaterialCopy => DwmSystemBackdropType.TabbedWindow,
            _ => DwmSystemBackdropType.None
        };

        var supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        if (!supported)
        {
            return new BackdropApplyResult(false, false, NativeName(nativeKind));
        }

        // WPF transparency is required for TopIsland's true pill / inverse-radius notch.
        // DWM system backdrops are window-wide, so on a layered transparent WPF window
        // they paint the shadow padding as an opaque rectangle. Keep the native API off
        // in this mode and use the material-specific TopIsland palette instead.
        if (window.AllowsTransparency)
        {
            var none = (int)DwmSystemBackdropType.None;
            _ = DwmSetWindowAttribute(hwnd, DwmaSystemBackdropType, ref none, sizeof(int));
            return new BackdropApplyResult(true, false, NativeName(nativeKind));
        }

        var value = (int)nativeKind;
        var hr = DwmSetWindowAttribute(hwnd, DwmaSystemBackdropType, ref value, sizeof(int));
        return new BackdropApplyResult(true, hr >= 0, NativeName(nativeKind));
    }

    private static string NativeName(DwmSystemBackdropType kind) => kind switch
    {
        DwmSystemBackdropType.MainWindow => "Mica",
        DwmSystemBackdropType.TransientWindow => "Desktop Acrylic",
        DwmSystemBackdropType.TabbedWindow => "Mica Alt",
        DwmSystemBackdropType.None => "None",
        _ => "Auto"
    };
}