using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using TopIsland.Models;

namespace TopIsland.Services;

public sealed class ThemeService
{
    private readonly MaterialYouThemeService _materialYou = new();

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

    public void Apply(AppThemeMode mode, SurfaceMaterial material, bool useExternalBlur = false)
    {
        var light = ResolveLight(mode);
        var resources = Application.Current.Resources;
        var accent = ReadWindowsAccentColor() ?? Color.FromRgb(86, 141, 255);

        if (material == SurfaceMaterial.MaterialCopy)
        {
            ApplyMaterialYou(resources, _materialYou.Create(accent, light));
            return;
        }

        var solidBlack = material == SurfaceMaterial.Solid;
        resources["SurfaceBrush"] = new SolidColorBrush(GetSurfaceColor(light, material, hover: false, useExternalBlur));
        resources["SurfaceHoverBrush"] = new SolidColorBrush(GetSurfaceColor(light, material, hover: true, useExternalBlur));
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
        resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(245, 245, 247));
        resources["PrimaryContainerBrush"] = Brushes.Transparent;
        resources["OnPrimaryContainerBrush"] = new SolidColorBrush(solidBlack || !light ? Colors.White : Colors.Black);
        resources["SecondaryContainerBrush"] = Brushes.Transparent;
        resources["OnSecondaryContainerBrush"] = new SolidColorBrush(solidBlack || !light ? Colors.White : Colors.Black);
        resources["SurfaceContainerLowBrush"] = new SolidColorBrush(GetSurfaceColor(light, material, hover: false, useExternalBlur));
        resources["SurfaceContainerHighBrush"] = new SolidColorBrush(GetSurfaceColor(light, material, hover: true, useExternalBlur));
        resources["OutlineBrush"] = new SolidColorBrush(solidBlack || !light ? Color.FromRgb(99, 99, 102) : Color.FromRgb(142, 142, 147));
        resources["ErrorBrush"] = new SolidColorBrush(Color.FromRgb(255, 69, 58));
    }

    private static void ApplyMaterialYou(ResourceDictionary resources, MaterialYouPalette palette)
    {
        resources["SurfaceBrush"] = new SolidColorBrush(palette.Surface);
        resources["SurfaceHoverBrush"] = new SolidColorBrush(palette.SurfaceHover);
        resources["PrimaryTextBrush"] = new SolidColorBrush(palette.PrimaryText);
        resources["SecondaryTextBrush"] = new SolidColorBrush(palette.SecondaryText);
        resources["TertiaryTextBrush"] = new SolidColorBrush(palette.TertiaryText);
        resources["SurfaceBorderBrush"] = new SolidColorBrush(palette.Border);
        resources["DividerBrush"] = new SolidColorBrush(palette.Divider);
        resources["ControlHoverBrush"] = new SolidColorBrush(palette.ControlHover);
        resources["ControlPressedBrush"] = new SolidColorBrush(palette.ControlPressed);
        resources["AccentBrush"] = new SolidColorBrush(palette.Primary);
        resources["PrimaryContainerBrush"] = new SolidColorBrush(palette.PrimaryContainer);
        resources["OnPrimaryContainerBrush"] = new SolidColorBrush(palette.OnPrimaryContainer);
        resources["SecondaryContainerBrush"] = new SolidColorBrush(palette.SecondaryContainer);
        resources["OnSecondaryContainerBrush"] = new SolidColorBrush(palette.OnSecondaryContainer);
        resources["SurfaceContainerLowBrush"] = new SolidColorBrush(palette.SurfaceContainerLow);
        resources["SurfaceContainerHighBrush"] = new SolidColorBrush(palette.SurfaceContainerHigh);
        resources["OutlineBrush"] = new SolidColorBrush(palette.Outline);
        resources["ErrorBrush"] = new SolidColorBrush(palette.Error);
    }

    private static Color GetSurfaceColor(bool light, SurfaceMaterial material, bool hover, bool useExternalBlur)
    {
        if (useExternalBlur)
        {
            return material switch
            {
                SurfaceMaterial.Acrylic => light
                    ? Color.FromArgb((byte)(hover ? 96 : 78), 252, 252, 253)
                    : Color.FromArgb((byte)(hover ? 98 : 82), 10, 10, 12),
                SurfaceMaterial.Glass => light
                    ? Color.FromArgb((byte)(hover ? 88 : 72), 252, 252, 253)
                    : Color.FromArgb((byte)(hover ? 92 : 76), 12, 12, 14),
                _ => GetSurfaceColor(light, material, hover, useExternalBlur: false)
            };
        }

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
            _ => Colors.Black
        };
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
