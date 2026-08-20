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
    private bool _initializing = true;
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
        _initializing = false;
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
        var duration = immediate ? 0 : (_state == SurfaceState.Expanded ? 300 : 170);
        var heightDelay = !immediate && _settings.Style == IslandStyle.Notch && _state == SurfaceState.Expanded ? 40 : 0;

        var baseWidthForState = ResolveBaseSurfaceWidth(SystemParameters.PrimaryScreenWidth);
        NetworkCompact.Visibility = baseWidthForState >= 980 || _state is SurfaceState.Peek or SurfaceState.Expanded
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_state == SurfaceState.Expanded)
        {
            ExpandedPanel.Visibility = Visibility.Visible;
            AnimateOpacity(ExpandedPanel, 1, immediate ? 0 : 210, immediate ? 0 : 80);
        }
        else
        {
            AnimateOpacity(ExpandedPanel, 0, immediate ? 0 : 120, 0, hideOnComplete: true);
        }

        SurfacePath.SetResourceReference(System.Windows.Shapes.Path.FillProperty,
            _state is SurfaceState.Hover or SurfaceState.Peek ? "SurfaceHoverBrush" : "SurfaceBrush");
        var nativeBackdrop = _backdropResult.NativeApplied && _settings.Material != SurfaceMaterial.Solid;
        AnimateShadow(_state == SurfaceState.Idle ? 22 : 30, nativeBackdrop ? 0 : (_state == SurfaceState.Idle ? 0.28 : 0.42), immediate);

        ConfigureContentMargins();
        AnimateWindow(target.Width, target.Height, target.Top, duration, heightDelay);
        UpdateGeometry();
    }

    private (double Width, double Height, double Top) ResolveLayout(SurfaceState state)
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var baseWidth = ResolveBaseSurfaceWidth(screenWidth);
        double surfaceWidth;
        double windowHeight;

        switch (state)
        {
            case SurfaceState.Hover:
                surfaceWidth = baseWidth * (_settings.Style == IslandStyle.Notch ? 1.13 : 1.055);
                windowHeight = _settings.Style == IslandStyle.Notch ? 70 : 84;
                break;
            case SurfaceState.Peek:
                surfaceWidth = baseWidth * (_settings.Style == IslandStyle.Notch ? 1.18 : 1.11);
                windowHeight = _settings.Style == IslandStyle.Notch ? 76 : 90;
                break;
            case SurfaceState.Expanded:
                surfaceWidth = Math.Min(Math.Max(baseWidth * 1.38, 820), screenWidth - _settings.SideMargin * 2);
                windowHeight = 398;
                break;
            default:
                surfaceWidth = baseWidth;
                windowHeight = _settings.Style == IslandStyle.Notch ? 62 : 76;
                break;
        }

        surfaceWidth = Math.Clamp(surfaceWidth, 220, Math.Max(220, screenWidth - _settings.SideMargin * 2));
        var windowWidth = surfaceWidth + IslandGeometryFactory.ShadowPadding * 2;
        var top = _settings.Style == IslandStyle.Notch ? 0 : state switch
        {
            SurfaceState.Hover => 10,
            SurfaceState.Peek => 11,
            _ => 8
        };
        return (windowWidth, windowHeight, top);
    }

    private double ResolveBaseSurfaceWidth(double screenWidth)
    {
        return _settings.WidthPreset switch
        {
            WidthPreset.Authentic => 300,
            WidthPreset.Compact => 390,
            WidthPreset.Standard => 560,
            WidthPreset.Wide => screenWidth * 0.64,
            WidthPreset.FullWidth => screenWidth - _settings.SideMargin * 2,
            WidthPreset.Custom => _settings.CustomWidth,
            _ => 560
        };
    }

    private void AnimateWindow(double targetWidth, double targetHeight, double targetTop, int durationMs, int heightDelayMs)
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
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
            new DoubleAnimation(SurfaceShadow.BlurRadius, blur, TimeSpan.FromMilliseconds(230))
            {
                EasingFunction = ease
            });
        SurfaceShadow.BeginAnimation(
            DropShadowEffect.OpacityProperty,
            new DoubleAnimation(SurfaceShadow.Opacity, opacity, TimeSpan.FromMilliseconds(230))
            {
                EasingFunction = ease
            });
    }
    private void ConfigureContentMargins()
    {
        if (_settings.Style == IslandStyle.Notch)
        {
            CompactBar.Margin = new Thickness(66, 5, 66, 0);
            ExpandedPanel.Margin = new Thickness(72, 72, 72, 20);
        }
        else
        {
            CompactBar.Margin = new Thickness(38, 16, 38, 0);
            ExpandedPanel.Margin = new Thickness(40, 78, 40, 20);
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
            MediaGlyphText.Text = "♪";
            ExpandedArtworkGlyph.Text = "♪";
            MediaSectionLabel.Text = "Now playing";
            MediaTitleText.Text = media.Title;
            MediaSubtitleText.Text = media.Subtitle;
            ExpandedMediaTitle.Text = media.Title;
            ExpandedMediaSubtitle.Text = string.IsNullOrWhiteSpace(media.SourceApp)
                ? media.Subtitle
                : $"{media.Subtitle} · {media.SourceApp}";
        }
        else
        {
            var foreground = _foregroundAppService.Sample();
            MediaGlyphText.Text = "▣";
            ExpandedArtworkGlyph.Text = "▣";
            MediaSectionLabel.Text = "Active app";
            MediaTitleText.Text = foreground.Title;
            MediaSubtitleText.Text = foreground.ProcessName;
            ExpandedMediaTitle.Text = foreground.Title;
            ExpandedMediaSubtitle.Text = $"Active window · {foreground.ProcessName}";
        }

        ApplyArtwork(media.Artwork);
        MediaProgressBar.Value = media.Progress;
        PreviousMediaButton.IsEnabled = media.HasSession;
        PlayPauseMediaButton.IsEnabled = media.HasSession;
        NextMediaButton.IsEnabled = media.HasSession;
        PlayPauseMediaButton.Content = media.IsPlaying ? "Ⅱ" : "▶";
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
                MediaGlyphText.Visibility = Visibility.Collapsed;
                ExpandedArtworkGlyph.Visibility = Visibility.Collapsed;
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
        MediaGlyphText.Visibility = Visibility.Visible;
        ExpandedArtworkGlyph.Visibility = Visibility.Visible;
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
        DateText.Text = now.ToString("M/d ddd");

        var stats = _statsService.Sample();
        CpuText.Text = $"{stats.CpuPercent:0}%";
        RamText.Text = $"{stats.RamPercent:0}%";
        NetworkText.Text = $"↓ {stats.DownloadMbps:0.0}  ↑ {stats.UploadMbps:0.0}";
        ExpandedCpuText.Text = $"{stats.CpuPercent:0}%";
        ExpandedRamText.Text = $"{stats.RamPercent:0}%";
        ExpandedNetworkText.Text = $"↓ {stats.DownloadMbps:0.0} Mbps   ↑ {stats.UploadMbps:0.0} Mbps";

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText.Text = uptime.TotalDays >= 1
            ? $"Uptime {(int)uptime.TotalDays}d {uptime.Hours:00}:{uptime.Minutes:00}"
            : $"Uptime {uptime.Hours:00}:{uptime.Minutes:00}";
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

    private void StyleToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Style = _settings.Style == IslandStyle.DynamicIsland ? IslandStyle.Notch : IslandStyle.DynamicIsland;
        SaveAndRefresh();
    }

    private void MaterialButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Material = _settings.Material switch
        {
            SurfaceMaterial.Solid => SurfaceMaterial.Mica,
            SurfaceMaterial.Mica => SurfaceMaterial.Acrylic,
            SurfaceMaterial.Acrylic => SurfaceMaterial.AppleGlass,
            SurfaceMaterial.AppleGlass => SurfaceMaterial.MaterialCopy,
            _ => SurfaceMaterial.Solid
        };

        _themeService.Apply(_settings.Theme, _settings.Material);
        ApplyBackdropMaterial();
        SaveAndRefresh();
    }
    private void WidthPresetButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.WidthPreset = _settings.WidthPreset switch
        {
            WidthPreset.Authentic => WidthPreset.Compact,
            WidthPreset.Compact => WidthPreset.Standard,
            WidthPreset.Standard => WidthPreset.Wide,
            WidthPreset.Wide => WidthPreset.FullWidth,
            WidthPreset.FullWidth => WidthPreset.Custom,
            _ => WidthPreset.Authentic
        };
        SaveAndRefresh();
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = _settings.Theme switch
        {
            AppThemeMode.System => AppThemeMode.Dark,
            AppThemeMode.Dark => AppThemeMode.Light,
            _ => AppThemeMode.System
        };
        _themeService.Apply(_settings.Theme, _settings.Material);
        ApplyBackdropMaterial();
        SaveAndRefresh();
    }

    private void CustomWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.CustomWidth = e.NewValue;
        _settings.WidthPreset = WidthPreset.Custom;
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
        StyleToggleButton.Content = _settings.Style == IslandStyle.DynamicIsland ? "Dynamic Island" : "Notch";
        MaterialButton.Content = MaterialDisplayName(_settings.Material);
        UpdateMaterialStatusText();
        WidthPresetButton.Content = _settings.WidthPreset switch
        {
            WidthPreset.FullWidth => "Full width",
            _ => _settings.WidthPreset.ToString()
        };
        ThemeButton.Content = $"Theme: {_settings.Theme}";
        CustomWidthSlider.Maximum = Math.Max(220, SystemParameters.PrimaryScreenWidth - _settings.SideMargin * 2);
        CustomWidthSlider.Value = Math.Clamp(_settings.CustomWidth, CustomWidthSlider.Minimum, CustomWidthSlider.Maximum);

        var baseWidth = ResolveBaseSurfaceWidth(SystemParameters.PrimaryScreenWidth);
        var authenticDensity = baseWidth < 360;
        var compactDensity = baseWidth < 470;
        var distributedDensity = baseWidth >= 980;
        StatsCompact.Visibility = authenticDensity ? Visibility.Collapsed : Visibility.Visible;
        DateText.Visibility = compactDensity ? Visibility.Collapsed : Visibility.Visible;

        MediaColumn.Width = distributedDensity ? new GridLength(2.2, GridUnitType.Star) : new GridLength(1.35, GridUnitType.Star);
        ClockColumn.Width = distributedDensity ? new GridLength(1.0, GridUnitType.Star) : GridLength.Auto;
        StatsColumn.Width = distributedDensity ? new GridLength(1.1, GridUnitType.Star) : GridLength.Auto;
        NetworkColumn.Width = distributedDensity ? new GridLength(1.2, GridUnitType.Star) : GridLength.Auto;
        StatusColumn.Width = GridLength.Auto;
        NetworkCompact.Visibility = distributedDensity || _state is SurfaceState.Peek or SurfaceState.Expanded
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
    private void ApplyBackdropMaterial()
    {
        _backdropResult = _backdropService.Apply(this, _settings.Material, _settings.Theme, _themeService);
        UpdateGeometry();
        UpdateMaterialStatusText();
    }

    private void UpdateMaterialStatusText()
    {
        if (!IsLoaded && PresentationSource.FromVisual(this) is null)
        {
            return;
        }

        if (_settings.Material == SurfaceMaterial.Solid)
        {
            MaterialNativeText.Text = "Native backdrop: off";
            return;
        }

        MaterialNativeText.Text = _backdropResult.NativeApplied
            ? $"Native: {_backdropResult.NativeKind} + TopIsland tint"
            : $"Shape-safe: {MaterialDisplayName(_settings.Material)}";
    }

    private static string MaterialDisplayName(SurfaceMaterial material) => material switch
    {
        SurfaceMaterial.AppleGlass => "Apple Glass",
        SurfaceMaterial.MaterialCopy => "Material Copy",
        _ => material.ToString()
    };
}
