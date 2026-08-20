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

    public void Apply(AppThemeMode mode)
    {
        var light = ResolveLight(mode);
        var resources = Application.Current.Resources;

        resources["SurfaceBrush"] = new SolidColorBrush(light
            ? Color.FromArgb(238, 247, 248, 250)
            : Color.FromArgb(240, 16, 17, 20));
        resources["SurfaceHoverBrush"] = new SolidColorBrush(light
            ? Color.FromArgb(246, 252, 252, 253)
            : Color.FromArgb(246, 24, 25, 30));
        resources["CardBrush"] = new SolidColorBrush(light
            ? Color.FromArgb(180, 255, 255, 255)
            : Color.FromArgb(150, 40, 42, 49));
        resources["PrimaryTextBrush"] = new SolidColorBrush(light ? Color.FromRgb(18, 20, 24) : Color.FromRgb(246, 247, 249));
        resources["SecondaryTextBrush"] = new SolidColorBrush(light ? Color.FromRgb(88, 92, 101) : Color.FromRgb(170, 174, 184));
        resources["SurfaceBorderBrush"] = new SolidColorBrush(light
            ? Color.FromArgb(150, 255, 255, 255)
            : Color.FromArgb(70, 255, 255, 255));
        resources["DividerBrush"] = new SolidColorBrush(light
            ? Color.FromArgb(35, 0, 0, 0)
            : Color.FromArgb(32, 255, 255, 255));
        resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(86, 141, 255));
        resources["SuccessBrush"] = new SolidColorBrush(Color.FromRgb(81, 201, 122));
    }
}
