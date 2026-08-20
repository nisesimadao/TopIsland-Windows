using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using TopIsland.Models;

namespace TopIsland.Services;

public sealed class ThemeService
{
    public bool IsSystemLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return value is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }

    public bool ResolveLight(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => true,
        AppThemeMode.Dark => false,
        _ => IsSystemLightTheme()
    };

    public void Apply(AppThemeMode mode, SurfaceMaterial material)
    {
        var light = ResolveLight(mode);
        var resources = Application.Current.Resources;
        var accent = ReadWindowsAccentColor() ?? Color.FromRgb(86, 141, 255);
        var solidBlack = material == SurfaceMaterial.Solid;

        resources["SurfaceBrush"] = new SolidColorBrush(GetSurfaceColor(light, material, hover: false, accent));
        resources["SurfaceHoverBrush"] = new SolidColorBrush(GetSurfaceColor(light, material, hover: true, accent));
        resources["PrimaryTextBrush"] = new SolidColorBrush(solidBlack || !light
            ? Color.FromRgb(245, 245, 247)
            : Color.FromRgb(24, 24, 27));
        resources["SecondaryTextBrush"] = new SolidColorBrush(solidBlack || !light
            ? Color.FromRgb(151, 151, 157)
            : Color.FromRgb(99, 99, 102));
        resources["TertiaryTextBrush"] = new SolidColorBrush(solidBlack || !light
            ? Color.FromRgb(99, 99, 102)
            : Color.FromRgb(142, 142, 147));
        resources["SurfaceBorderBrush"] = new SolidColorBrush(solidBlack
            ? Colors.Transparent
            : Color.FromArgb(light ? (byte)38 : (byte)30, 255, 255, 255));
        resources["DividerBrush"] = new SolidColorBrush(solidBlack || !light
            ? Color.FromArgb(38, 255, 255, 255)
            : Color.FromArgb(28, 0, 0, 0));
        resources["ControlHoverBrush"] = new SolidColorBrush(solidBlack || !light
            ? Color.FromArgb(36, 255, 255, 255)
            : Color.FromArgb(24, 0, 0, 0));
        resources["ControlPressedBrush"] = new SolidColorBrush(solidBlack || !light
            ? Color.FromArgb(54, 255, 255, 255)
            : Color.FromArgb(38, 0, 0, 0));
        resources["AccentBrush"] = new SolidColorBrush(material == SurfaceMaterial.MaterialCopy ? accent : Color.FromRgb(245, 245, 247));
    }

    private static Color GetSurfaceColor(bool light, SurfaceMaterial material, bool hover, Color accent)
    {
        return material switch
        {
            SurfaceMaterial.Solid => Color.FromArgb(255, 0, 0, 0),
            SurfaceMaterial.Mica => light
                ? Color.FromArgb((byte)(hover ? 246 : 240), 243, 243, 245)
                : Color.FromArgb((byte)(hover ? 246 : 240), 30, 30, 32),
            SurfaceMaterial.Acrylic => light
                ? Color.FromArgb((byte)(hover ? 226 : 214), 246, 246, 248)
                : Color.FromArgb((byte)(hover ? 226 : 214), 24, 24, 27),
            SurfaceMaterial.Glass => light
                ? Color.FromArgb((byte)(hover ? 202 : 188), 250, 250, 252)
                : Color.FromArgb((byte)(hover ? 204 : 190), 18, 18, 20),
            SurfaceMaterial.MaterialCopy => Tint(
                light ? Color.FromRgb(244, 244, 246) : Color.FromRgb(24, 24, 27),
                accent,
                light ? 0.08 : 0.13,
                hover ? (byte)238 : (byte)228),
            _ => Colors.Black
        };
    }

    private static Color Tint(Color baseColor, Color accent, double amount, byte alpha)
    {
        return Color.FromArgb(
            alpha,
            (byte)Math.Round(baseColor.R + (accent.R - baseColor.R) * amount),
            (byte)Math.Round(baseColor.G + (accent.G - baseColor.G) * amount),
            (byte)Math.Round(baseColor.B + (accent.B - baseColor.B) * amount));
    }

    private static Color? ReadWindowsAccentColor()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "ColorizationColor", null);
            if (value is int i)
            {
                var raw = unchecked((uint)i);
                return Color.FromRgb((byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
            }
        }
        catch
        {
        }

        return null;
    }
}
