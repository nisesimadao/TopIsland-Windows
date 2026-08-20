using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TopIsland.Controls;
using TopIsland.Interop;
using TopIsland.Models;
using TopIsland.Services;

namespace TopIsland;

public partial class MainWindow : Window
{
    private enum SurfaceState
    {
        Idle,
        Hover,
        Peek,
        Expanded
    }

    private readonly SettingsStore _settingsStore = new();
    private readonly ThemeService _themeService = new();
    private readonly SystemStatsService _statsService = new();
    private readonly MediaSessionService _mediaService = new();
    private readonly ForegroundAppService _foregroundAppService = new();
    private readonly BackdropMaterialService _backdropService = new();
    private readonly WindowRegionService _windowRegionService = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private readonly DispatcherTimer _themeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _peekTimer = new();
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private AppSettings _settings = new();
    private SurfaceState _state = SurfaceState.Idle;
    private bool _lastSystemLight;
    private OverlayWindowBehavior? _overlayBehavior;
    private BackdropApplyResult _backdropResult = new(false, false, "Unavailable");
    private byte[]? _lastArtworkBytes;

    public MainWindow()
    {
        InitializeComponent();

        _settings = _settingsStore.Load();
        _peekTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.HoverPeekDelayMs, 120, 2000));
        _peekTimer.Tick += PeekTimer_Tick;
        _collapseTimer.Tick += CollapseTimer_Tick;
        _statsTimer.Tick += StatsTimer_Tick;
        _mediaTimer.Tick += MediaTimer_Tick;
        _themeTimer.Tick += ThemeTimer_Tick;
        SizeChanged += (_, _) => UpdateGeometry();
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += (_, _) => _overlayBehavior?.Detach();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _overlayBehavior = new OverlayWindowBehavior(this, point => SurfacePath.Data?.FillContains(point) == true);
        _overlayBehavior.Attach();
        ApplyBackdropMaterial();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _themeService.Apply(_settings.Theme, _settings.Material);
        _lastSystemLight = _themeService.IsSystemLightTheme();
        SyncSettingsUi();

        _state = _settings.StartExpanded ? SurfaceState.Expanded : SurfaceState.Idle;
        ApplyState(immediate: true);
        UpdateLiveData();
        _statsTimer.Start();
        _themeTimer.Start();

        await _mediaService.InitializeAsync();
        await RefreshMediaAsync();
        _mediaTimer.Start();
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_state == SurfaceState.Expanded)
        {
            return;
        }

        _state = SurfaceState.Hover;
        ApplyState();

        if (_settings.EnableHoverPeek)
        {
            _peekTimer.Stop();
            _peekTimer.Start();
        }
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        _peekTimer.Stop();
        if (_state == SurfaceState.Expanded)
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
            return;
        }

        _state = SurfaceState.Idle;
        ApplyState();
    }

    private void PeekTimer_Tick(object? sender, EventArgs e)
    {
        _peekTimer.Stop();
        if (_state != SurfaceState.Hover || !IsMouseOver)
        {
            return;
        }

        _state = SurfaceState.Peek;
        ApplyState();
    }


    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (_state == SurfaceState.Expanded && !IsMouseOver)
        {
            _state = SurfaceState.Idle;
            ApplyState();
        }
    }

    private void CompactBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _peekTimer.Stop();
        _state = _state == SurfaceState.Expanded ? SurfaceState.Idle : SurfaceState.Expanded;
        ApplyState();
    }

    private void ApplyState(bool immediate = false)
    {
        var target = ResolveLayout(_state);
        var duration = immediate ? 0 : _state switch
        {
            SurfaceState.Expanded => 260,
            SurfaceState.Idle => 200,
            SurfaceState.Peek => 175,
            _ => 145
        };

        if (_state == SurfaceState.Expanded)
        {
            if (immediate)
            {
                CompactBar.Visibility = Visibility.Hidden;
                CompactBar.Opacity = 0;
                ExpandedTranslate.Y = 0;
            }
            else
            {
                AnimateOpacity(CompactBar, 0, 70, 0, hideOnComplete: true);
                ExpandedTranslate.Y = -4;
            }

            ExpandedPanel.Visibility = Visibility.Visible;
            AnimateOpacity(ExpandedPanel, 1, immediate ? 0 : 175, immediate ? 0 : 55);
            AnimateTranslate(ExpandedTranslate, 0, immediate ? 0 : 220, immediate ? 0 : 35);
        }
        else
        {
            AnimateOpacity(ExpandedPanel, 0, immediate ? 0 : 90, 0, hideOnComplete: true);
            AnimateTranslate(ExpandedTranslate, -4, immediate ? 0 : 100, 0);

            CompactBar.Visibility = Visibility.Visible;
            if (immediate)
            {
                CompactBar.Opacity = 1;
            }
            else
            {
                AnimateOpacity(CompactBar, 1, 130, 45);
            }
        }

        SurfacePath.SetResourceReference(System.Windows.Shapes.Path.FillProperty,
            _state is SurfaceState.Hover or SurfaceState.Peek ? "SurfaceHoverBrush" : "SurfaceBrush");

        var shadowOpacity = _state switch
        {
            SurfaceState.Idle => _settings.Style == IslandStyle.Notch ? 0.0 : 0.16,
            SurfaceState.Expanded => _settings.Style == IslandStyle.Notch ? 0.34 : 0.30,
            _ => _settings.Style == IslandStyle.Notch ? 0.26 : 0.24
        };
        var shadowBlur = _state switch
        {
            SurfaceState.Idle => 8.0,
            SurfaceState.Expanded => 12.0,
            _ => 10.0
        };
        AnimateShadow(shadowBlur, shadowOpacity, immediate);

        ConfigureContentMargins();
        UpdateCompactDensity();
        AnimateWindow(target.Width, target.Height, target.Top, duration, 0);
        UpdateGeometry();
    }

    private (double Width, double Height, double Top) ResolveLayout(SurfaceState state)
    {
        var screenWidth = GetPrimaryScreenWidthDip();
        var baseWidth = ResolveBaseSurfaceWidth(screenWidth);
        var maxWidth = Math.Max(185, screenWidth - _settings.SideMargin * 2);
        double surfaceWidth;
        double windowHeight;

        switch (state)
        {
            case SurfaceState.Hover:
                surfaceWidth = baseWidth * 1.02;
                windowHeight = _settings.Style == IslandStyle.Notch ? 54 : 70;
                break;
            case SurfaceState.Peek:
                surfaceWidth = baseWidth * 1.04;
                windowHeight = _settings.Style == IslandStyle.Notch ? 58 : 72;
                break;
            case SurfaceState.Expanded:
                surfaceWidth = _settings.WidthPreset == WidthPreset.FullWidth
                    ? maxWidth
                    : Math.Min(Math.Max(baseWidth, 640), maxWidth);
                windowHeight = _settings.Style == IslandStyle.Notch ? 176 : 192;
                break;
            default:
                surfaceWidth = baseWidth;
                windowHeight = _settings.Style == IslandStyle.Notch ? 52 : 68;
                break;
        }

        var minWidth = _settings.Style == IslandStyle.Notch ? 185 : 220;
        surfaceWidth = Math.Clamp(surfaceWidth, minWidth, maxWidth);
        var windowWidth = surfaceWidth + IslandGeometryFactory.ShadowPadding * 2;
        var top = _settings.Style == IslandStyle.Notch ? 0 : state switch
        {
            SurfaceState.Hover or SurfaceState.Peek => 10,
            _ => 8
        };
        return (windowWidth, windowHeight, top);
    }

    private double GetPrimaryScreenWidthDip()
    {
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return SystemParameters.PrimaryScreenWidth * transform.M11;
    }

    private double ResolveBaseSurfaceWidth(double screenWidth)
    {
        return _settings.WidthPreset switch
        {
            WidthPreset.Authentic => _settings.Style == IslandStyle.Notch ? 185 : 260,
            WidthPreset.Compact => 320,
            WidthPreset.Standard => 560,
            WidthPreset.Wide => screenWidth * 0.64,
            WidthPreset.FullWidth => screenWidth - _settings.SideMargin * 2,
            WidthPreset.Custom => _settings.CustomWidth,
            _ => 560
        };
    }

    private void AnimateWindow(double targetWidth, double targetHeight, double targetTop, int durationMs, int heightDelayMs)
    {
        var screenWidth = GetPrimaryScreenWidthDip();
        var targetLeft = (screenWidth - targetWidth) / 2.0;

        if (durationMs <= 0)
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Width = targetWidth;
            Height = targetHeight;
            Left = targetLeft;
            Top = targetTop;
            UpdateGeometry();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(WidthProperty, CreateAnimation(ActualWidth > 0 ? ActualWidth : Width, targetWidth, durationMs, 0, ease));
        BeginAnimation(LeftProperty, CreateAnimation(double.IsNaN(Left) ? targetLeft : Left, targetLeft, durationMs, 0, ease));
        BeginAnimation(TopProperty, CreateAnimation(double.IsNaN(Top) ? targetTop : Top, targetTop, durationMs, 0, ease));
        BeginAnimation(HeightProperty, CreateAnimation(ActualHeight > 0 ? ActualHeight : Height, targetHeight, durationMs, heightDelayMs, ease));
    }

    private static DoubleAnimation CreateAnimation(double from, double to, int durationMs, int delayMs, IEasingFunction easing)
    {
        return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
    }

    private static void AnimateOpacity(UIElement element, double to, int durationMs, int delayMs, bool hideOnComplete = false)
    {
        if (durationMs <= 0)
        {
            element.Opacity = to;
            if (hideOnComplete && to <= 0)
            {
                element.Visibility = Visibility.Hidden;
            }
            return;
        }

        var animation = new DoubleAnimation(element.Opacity, to, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        if (hideOnComplete)
        {
            animation.Completed += (_, _) =>
            {
                if (element.Opacity <= 0.01 || to <= 0)
                {
                    element.Visibility = Visibility.Hidden;
                }
            };
        }
        element.BeginAnimation(OpacityProperty, animation);
    }

    private static void AnimateTranslate(TranslateTransform transform, double to, int durationMs, int delayMs)
    {
        if (durationMs <= 0)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = to;
            return;
        }

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(transform.Y, to, TimeSpan.FromMilliseconds(durationMs))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void AnimateShadow(double blur, double opacity, bool immediate)
    {
        if (immediate)
        {
            SurfaceShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            SurfaceShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            SurfaceShadow.BlurRadius = blur;
            SurfaceShadow.Opacity = opacity;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SurfaceShadow.BeginAnimation(
            DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(SurfaceShadow.BlurRadius, blur, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = ease
            });
        SurfaceShadow.BeginAnimation(
            DropShadowEffect.OpacityProperty,
            new DoubleAnimation(SurfaceShadow.Opacity, opacity, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = ease
            });
    }
    private void ConfigureContentMargins()
    {
        if (_settings.Style == IslandStyle.Notch)
        {
            CompactBar.Margin = new Thickness(30, 2, 30, 0);
            ExpandedPanel.Margin = new Thickness(47, 20, 47, 20);
        }
        else
        {
            CompactBar.Margin = new Thickness(30, 18, 30, 0);
            ExpandedPanel.Margin = new Thickness(36);
        }
    }

    private void UpdateGeometry()
    {
        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var geometry = IslandGeometryFactory.Create(
            _settings.Style,
            new Size(ActualWidth, ActualHeight),
            _state == SurfaceState.Expanded);
        SurfacePath.Data = geometry;
        ContentHost.Clip = geometry;

        if (_backdropResult.NativeApplied && _settings.Material != SurfaceMaterial.Solid)
        {
            _windowRegionService.Apply(this, geometry);
        }
        else
        {
            _windowRegionService.Reset(this);
        }
    }

    private void StatsTimer_Tick(object? sender, EventArgs e) => UpdateLiveData();

    private async void MediaTimer_Tick(object? sender, EventArgs e) => await RefreshMediaAsync();

    private async Task RefreshMediaAsync()
    {
        var media = await _mediaService.SampleAsync();
        if (media.HasSession)
        {
            MediaTitleText.Text = media.Title;
            MediaSubtitleText.Text = media.Subtitle;
            ExpandedMediaTitle.Text = media.Title;
            ExpandedMediaSubtitle.Text = string.IsNullOrWhiteSpace(media.SourceApp)
                ? media.Subtitle
                : $"{media.Subtitle} \u00B7 {media.SourceApp}";
        }
        else
        {
            var foreground = _foregroundAppService.Sample();
            MediaTitleText.Text = foreground.Title;
            MediaSubtitleText.Text = foreground.ProcessName;
            ExpandedMediaTitle.Text = foreground.Title;
            ExpandedMediaSubtitle.Text = foreground.ProcessName;
        }

        ApplyArtwork(media.Artwork);
        MediaProgressBar.Value = media.Progress;
        MediaProgressBar.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
        MediaControlsPanel.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
        PreviousMediaButton.IsEnabled = media.HasSession;
        PlayPauseMediaButton.IsEnabled = media.HasSession;
        NextMediaButton.IsEnabled = media.HasSession;
        PlayIconViewbox.Visibility = media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIconViewbox.Visibility = media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyArtwork(byte[]? bytes)
    {
        if (ReferenceEquals(_lastArtworkBytes, bytes))
        {
            return;
        }

        _lastArtworkBytes = bytes;
        if (bytes is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();

                var compactBrush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                var expandedBrush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                MediaArtworkBorder.Background = compactBrush;
                ExpandedArtworkBorder.Background = expandedBrush;
                MediaGlyphPath.Visibility = Visibility.Collapsed;
                ExpandedArtworkGlyphPath.Visibility = Visibility.Collapsed;
                return;
            }
            catch
            {
                // Fall through to the lightweight glyph placeholder.
            }
        }

        var fallback = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        fallback.Freeze();
        MediaArtworkBorder.Background = fallback;
        ExpandedArtworkBorder.Background = fallback;
        MediaGlyphPath.Visibility = Visibility.Visible;
        ExpandedArtworkGlyphPath.Visibility = Visibility.Visible;
    }

    private async void PreviousMediaButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.PreviousAsync();
        await RefreshMediaAsync();
    }

    private async void PlayPauseMediaButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.TogglePlayPauseAsync();
        await RefreshMediaAsync();
    }

    private async void NextMediaButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.NextAsync();
        await RefreshMediaAsync();
    }


    private void UpdateLiveData()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        ExpandedClockText.Text = now.ToString("HH:mm");
        ExpandedDateText.Text = now.ToString("M/d ddd");

        var stats = _statsService.Sample();
        CompactStatsText.Text = $"CPU {stats.CpuPercent:0}%   RAM {stats.RamPercent:0}%";
        ExpandedSystemText.Text = $"CPU {stats.CpuPercent:0}%   RAM {stats.RamPercent:0}%";
        ExpandedNetworkText.Text = $"\u2193 {stats.DownloadMbps:0.0}   \u2191 {stats.UploadMbps:0.0} Mbps";
    }

    private void ThemeTimer_Tick(object? sender, EventArgs e)
    {
        if (_settings.Theme != AppThemeMode.System)
        {
            return;
        }

        var current = _themeService.IsSystemLightTheme();
        if (current == _lastSystemLight)
        {
            return;
        }

        _lastSystemLight = current;
        _themeService.Apply(AppThemeMode.System, _settings.Material);
        ApplyBackdropMaterial();
    }

    private void SetDynamicIsland_Click(object sender, RoutedEventArgs e) => SetStyle(IslandStyle.DynamicIsland);
    private void SetNotch_Click(object sender, RoutedEventArgs e) => SetStyle(IslandStyle.Notch);

    private void SetAuthenticWidth_Click(object sender, RoutedEventArgs e) => SetWidth(WidthPreset.Authentic);
    private void SetCompactWidth_Click(object sender, RoutedEventArgs e) => SetWidth(WidthPreset.Compact);
    private void SetStandardWidth_Click(object sender, RoutedEventArgs e) => SetWidth(WidthPreset.Standard);
    private void SetWideWidth_Click(object sender, RoutedEventArgs e) => SetWidth(WidthPreset.Wide);
    private void SetFullWidth_Click(object sender, RoutedEventArgs e) => SetWidth(WidthPreset.FullWidth);

    private void SetSolidMaterial_Click(object sender, RoutedEventArgs e) => SetMaterial(SurfaceMaterial.Solid);
    private void SetMicaMaterial_Click(object sender, RoutedEventArgs e) => SetMaterial(SurfaceMaterial.Mica);
    private void SetAcrylicMaterial_Click(object sender, RoutedEventArgs e) => SetMaterial(SurfaceMaterial.Acrylic);
    private void SetGlassMaterial_Click(object sender, RoutedEventArgs e) => SetMaterial(SurfaceMaterial.Glass);
    private void SetMaterialCopy_Click(object sender, RoutedEventArgs e) => SetMaterial(SurfaceMaterial.MaterialCopy);

    private void SetSystemTheme_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.System);
    private void SetDarkTheme_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.Dark);
    private void SetLightTheme_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.Light);

    private void SetStyle(IslandStyle style)
    {
        _settings.Style = style;
        if (style == IslandStyle.Notch)
        {
            _settings.Material = SurfaceMaterial.Solid;
            _themeService.Apply(_settings.Theme, _settings.Material);
            ApplyBackdropMaterial();
        }
        SaveAndRefresh();
    }

    private void SetWidth(WidthPreset width)
    {
        _settings.WidthPreset = width;
        SaveAndRefresh();
    }

    private void SetMaterial(SurfaceMaterial material)
    {
        _settings.Material = material;
        _themeService.Apply(_settings.Theme, _settings.Material);
        ApplyBackdropMaterial();
        SaveAndRefresh();
    }

    private void SetTheme(AppThemeMode theme)
    {
        _settings.Theme = theme;
        _themeService.Apply(_settings.Theme, _settings.Material);
        ApplyBackdropMaterial();
        SaveAndRefresh();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void SaveAndRefresh()
    {
        _settingsStore.Save(_settings);
        SyncSettingsUi();
        ApplyState();
    }

    private void SyncSettingsUi()
    {
        DynamicIslandMenuItem.IsChecked = _settings.Style == IslandStyle.DynamicIsland;
        NotchMenuItem.IsChecked = _settings.Style == IslandStyle.Notch;

        AuthenticWidthMenuItem.IsChecked = _settings.WidthPreset == WidthPreset.Authentic;
        CompactWidthMenuItem.IsChecked = _settings.WidthPreset == WidthPreset.Compact;
        StandardWidthMenuItem.IsChecked = _settings.WidthPreset == WidthPreset.Standard;
        WideWidthMenuItem.IsChecked = _settings.WidthPreset == WidthPreset.Wide;
        FullWidthMenuItem.IsChecked = _settings.WidthPreset == WidthPreset.FullWidth;

        SolidMaterialMenuItem.IsChecked = _settings.Material == SurfaceMaterial.Solid;
        MicaMaterialMenuItem.IsChecked = _settings.Material == SurfaceMaterial.Mica;
        AcrylicMaterialMenuItem.IsChecked = _settings.Material == SurfaceMaterial.Acrylic;
        GlassMaterialMenuItem.IsChecked = _settings.Material == SurfaceMaterial.Glass;
        MaterialCopyMenuItem.IsChecked = _settings.Material == SurfaceMaterial.MaterialCopy;

        SystemThemeMenuItem.IsChecked = _settings.Theme == AppThemeMode.System;
        DarkThemeMenuItem.IsChecked = _settings.Theme == AppThemeMode.Dark;
        LightThemeMenuItem.IsChecked = _settings.Theme == AppThemeMode.Light;

        UpdateCompactDensity();
    }

    private void UpdateCompactDensity()
    {
        var baseWidth = ResolveBaseSurfaceWidth(GetPrimaryScreenWidthDip());
        var peek = _state == SurfaceState.Peek;

        // Prefer removing detail to compressing every element. This keeps the
        // compact surface readable instead of turning it into a tiny dashboard.
        CompactMediaText.Visibility = baseWidth >= 240 ? Visibility.Visible : Visibility.Collapsed;
        MediaSubtitleText.Visibility = baseWidth >= (peek ? 300 : 360) ? Visibility.Visible : Visibility.Collapsed;
        CompactStatsText.Visibility = baseWidth >= (peek ? 430 : 520) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyBackdropMaterial()
    {
        _backdropResult = _backdropService.Apply(this, _settings.Material, _settings.Theme, _themeService);
        UpdateGeometry();
    }

}
