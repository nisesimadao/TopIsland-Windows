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

        resources["SurfaceBrush"] = CreateSurfaceBrush(light, material, hover: false, accent);
        resources["SurfaceHoverBrush"] = CreateSurfaceBrush(light, material, hover: true, accent);
        resources["CardBrush"] = CreateCardBrush(light, material, accent);
        resources["PrimaryTextBrush"] = new SolidColorBrush(light ? Color.FromRgb(18, 20, 24) : Color.FromRgb(246, 247, 249));
        resources["SecondaryTextBrush"] = new SolidColorBrush(light ? Color.FromRgb(88, 92, 101) : Color.FromRgb(170, 174, 184));
        resources["SurfaceBorderBrush"] = new SolidColorBrush(GetBorderColor(light, material));
        resources["DividerBrush"] = new SolidColorBrush(light
            ? Color.FromArgb(38, 0, 0, 0)
            : Color.FromArgb(34, 255, 255, 255));
        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["SuccessBrush"] = new SolidColorBrush(Color.FromRgb(81, 201, 122));
        resources["ButtonBrush"] = new SolidColorBrush(light
            ? Color.FromArgb(26, 0, 0, 0)
            : Color.FromArgb(22, 255, 255, 255));
        resources["ButtonHoverBrush"] = new SolidColorBrush(light
            ? Color.FromArgb(42, 0, 0, 0)
            : Color.FromArgb(42, 255, 255, 255));
        resources["MaterialSheenBrush"] = CreateSheenBrush(light, material, accent);
    }

    private static Brush CreateSurfaceBrush(bool light, SurfaceMaterial material, bool hover, Color accent)
    {
        return material switch
        {
            SurfaceMaterial.Mica => new SolidColorBrush(light
                ? Color.FromArgb((byte)(hover ? 224 : 214), 245, 246, 248)
                : Color.FromArgb((byte)(hover ? 226 : 214), 22, 23, 28)),
            SurfaceMaterial.Acrylic => new SolidColorBrush(light
                ? Color.FromArgb((byte)(hover ? 205 : 188), 250, 251, 253)
                : Color.FromArgb((byte)(hover ? 208 : 188), 18, 19, 24)),
            SurfaceMaterial.AppleGlass => CreateAppleGlass(light, hover),
            SurfaceMaterial.MaterialCopy => CreateMaterialCopy(light, hover, accent),
            _ => new SolidColorBrush(light
                ? Color.FromArgb((byte)(hover ? 246 : 238), 247, 248, 250)
                : Color.FromArgb((byte)(hover ? 246 : 240), 16, 17, 20))
        };
    }

    private static Brush CreateCardBrush(bool light, SurfaceMaterial material, Color accent)
    {
        return material switch
        {
            SurfaceMaterial.Acrylic => new SolidColorBrush(light
                ? Color.FromArgb(126, 255, 255, 255)
                : Color.FromArgb(104, 52, 54, 62)),
            SurfaceMaterial.AppleGlass => new LinearGradientBrush(
                light
                    ? Color.FromArgb(154, 255, 255, 255)
                    : Color.FromArgb(88, 255, 255, 255),
                light
                    ? Color.FromArgb(104, 244, 246, 250)
                    : Color.FromArgb(72, 30, 32, 39),
                new Point(0, 0),
                new Point(1, 1)),
            SurfaceMaterial.MaterialCopy => new SolidColorBrush(MixWithAlpha(
                light ? Color.FromRgb(248, 249, 252) : Color.FromRgb(30, 31, 37),
                accent,
                light ? 0.07 : 0.15,
                light ? (byte)160 : (byte)132)),
            SurfaceMaterial.Mica => new SolidColorBrush(light
                ? Color.FromArgb(155, 255, 255, 255)
                : Color.FromArgb(128, 43, 45, 52)),
            _ => new SolidColorBrush(light
                ? Color.FromArgb(180, 255, 255, 255)
                : Color.FromArgb(150, 40, 42, 49))
        };
    }

    private static Brush CreateAppleGlass(bool light, bool hover)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        if (light)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(hover ? 218 : 202), 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(hover ? 188 : 170), 239, 243, 250), 0.52));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(hover ? 205 : 188), 255, 255, 255), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(hover ? 198 : 178), 32, 34, 42), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(hover ? 176 : 156), 15, 16, 21), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(hover ? 190 : 170), 37, 39, 47), 1));
        }
        return brush;
    }

    private static Brush CreateMaterialCopy(bool light, bool hover, Color accent)
    {
        var baseColor = light ? Color.FromRgb(247, 248, 251) : Color.FromRgb(24, 25, 30);
        var tint = Mix(baseColor, accent, light ? 0.10 : 0.20);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0.8)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(hover ? 222 : 204), tint.R, tint.G, tint.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(hover ? 205 : 187), baseColor.R, baseColor.G, baseColor.B), 0.65));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(hover ? 212 : 194), tint.R, tint.G, tint.B), 1));
        return brush;
    }

    private static Brush CreateSheenBrush(bool light, SurfaceMaterial material, Color accent)
    {
        if (material is SurfaceMaterial.Solid or SurfaceMaterial.Mica)
        {
            return Brushes.Transparent;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.05, 0),
            EndPoint = new Point(0.95, 1)
        };
        var highlight = material == SurfaceMaterial.MaterialCopy ? accent : Colors.White;
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(light ? 38 : 28), highlight.R, highlight.G, highlight.B), 0));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.38));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(light ? 18 : 12), 255, 255, 255), 1));
        return brush;
    }

    private static Color GetBorderColor(bool light, SurfaceMaterial material)
    {
        var alpha = material switch
        {
            SurfaceMaterial.AppleGlass => light ? 205 : 100,
            SurfaceMaterial.Acrylic => light ? 175 : 82,
            SurfaceMaterial.MaterialCopy => light ? 165 : 82,
            _ => light ? 150 : 70
        };
        return Color.FromArgb((byte)alpha, 255, 255, 255);
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

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static Color MixWithAlpha(Color a, Color b, double t, byte alpha)
    {
        var mixed = Mix(a, b, t);
        return Color.FromArgb(alpha, mixed.R, mixed.G, mixed.B);
    }
}