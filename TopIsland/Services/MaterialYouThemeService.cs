using Material3.Core;
using System.Windows.Media;

namespace TopIsland.Services;

public readonly record struct MaterialYouPalette(
    Color Surface,
    Color SurfaceHover,
    Color SurfaceContainerLow,
    Color SurfaceContainerHigh,
    Color PrimaryText,
    Color SecondaryText,
    Color TertiaryText,
    Color Border,
    Color Divider,
    Color ControlHover,
    Color ControlPressed,
    Color Primary,
    Color OnPrimary,
    Color PrimaryContainer,
    Color OnPrimaryContainer,
    Color SecondaryContainer,
    Color OnSecondaryContainer,
    Color Outline,
    Color Error);

public sealed class MaterialYouThemeService
{
    public MaterialYouPalette Create(Color sourceColor, bool light)
    {
        var seed = Argb.FromArgb(sourceColor.R, sourceColor.G, sourceColor.B);
        var theme = MaterialTheme.FromSeed(seed, SchemeVariant.TonalSpot);
        var scheme = light ? theme.LightScheme : theme.DarkScheme;

        var surface = ToColor(scheme.Surface);
        var onSurface = ToColor(scheme.OnSurface);
        var onSurfaceVariant = ToColor(scheme.OnSurfaceVariant);
        var outline = ToColor(scheme.Outline);
        var outlineVariant = ToColor(scheme.OutlineVariant);

        return new MaterialYouPalette(
            Surface: surface,
            SurfaceHover: ToColor(scheme.SurfaceContainerLow),
            SurfaceContainerLow: ToColor(scheme.SurfaceContainerLow),
            SurfaceContainerHigh: ToColor(scheme.SurfaceContainerHigh),
            PrimaryText: onSurface,
            SecondaryText: onSurfaceVariant,
            TertiaryText: WithAlpha(outline, 220),
            Border: WithAlpha(outlineVariant, light ? (byte)110 : (byte)90),
            Divider: WithAlpha(outlineVariant, light ? (byte)125 : (byte)110),
            ControlHover: WithAlpha(onSurface, 20),
            ControlPressed: WithAlpha(onSurface, 31),
            Primary: ToColor(scheme.Primary),
            OnPrimary: ToColor(scheme.OnPrimary),
            PrimaryContainer: ToColor(scheme.PrimaryContainer),
            OnPrimaryContainer: ToColor(scheme.OnPrimaryContainer),
            SecondaryContainer: ToColor(scheme.SecondaryContainer),
            OnSecondaryContainer: ToColor(scheme.OnSecondaryContainer),
            Outline: outline,
            Error: ToColor(scheme.Error));
    }

    private static Color ToColor(Argb argb) => Color.FromArgb(argb.A, argb.R, argb.G, argb.B);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Overlay(Color background, Color foreground, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(background.R + (foreground.R - background.R) * opacity),
            (byte)Math.Round(background.G + (foreground.G - background.G) * opacity),
            (byte)Math.Round(background.B + (foreground.B - background.B) * opacity));
    }
}

