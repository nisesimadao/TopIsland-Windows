using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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
    private readonly StartupService _startupService = new();
    private readonly DownloadMonitorService _downloadMonitorService = new();
    private readonly FocusTimerService _focusTimerService = new();
    private readonly NotificationService _notificationService = new();
    private readonly MonitorService _monitorService;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private readonly DispatcherTimer _themeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _peekTimer = new();
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private AppSettings _settings = new();
    private SurfaceState _state = SurfaceState.Idle;
    private bool _lastSystemLight;
    private OverlayWindowBehavior? _overlayBehavior;
    private BackdropApplyResult _backdropResult = new(false, false, "Unavailable");
    private byte[]? _lastArtworkBytes;
    private MonitorDescriptor? _currentMonitor;
    private IntPtr _hwnd;
    private double _targetTopDip;
    private int _notificationCount;
    private bool _monitorTransitioning;

    public event EventHandler? SettingsChanged;

    public AppSettings Settings => _settings;

    public MainWindow(MonitorService monitorService)
    {
        _monitorService = monitorService;
        InitializeComponent();

        _settings = _settingsStore.Load();
        _peekTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.HoverPeekDelayMs, 120, 2000));
        _peekTimer.Tick += PeekTimer_Tick;
        _collapseTimer.Tick += CollapseTimer_Tick;
        _statsTimer.Tick += StatsTimer_Tick;
        _mediaTimer.Tick += MediaTimer_Tick;
        _themeTimer.Tick += ThemeTimer_Tick;
        _notificationTimer.Tick += NotificationTimer_Tick;
        SizeChanged += (_, _) =>
        {
            UpdateGeometry();
            PositionOnCurrentMonitor();
        };
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _overlayBehavior = new OverlayWindowBehavior(this, point => SurfacePath.Data?.FillContains(point) == true);
        _overlayBehavior.DisplayEnvironmentChanged += OverlayBehavior_DisplayEnvironmentChanged;
        _overlayBehavior.Attach();

        _currentMonitor = _monitorService.Resolve(_settings);
        ApplyBackdropMaterial();
        PositionOnCurrentMonitor();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_overlayBehavior is not null)
        {
            _overlayBehavior.DisplayEnvironmentChanged -= OverlayBehavior_DisplayEnvironmentChanged;
            _overlayBehavior.Detach();
        }
        _notificationTimer.Stop();
        _statsService.Dispose();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _themeService.Apply(_settings.Theme, _settings.Material);
        _lastSystemLight = _themeService.IsSystemLightTheme();
        if (_settings.StartWithWindows)
        {
            _startupService.SetEnabled(true);
        }

        RefreshTargetMonitor(force: true);
        SyncSettingsUi();

        _state = _settings.StartExpanded ? SurfaceState.Expanded : SurfaceState.Idle;
        ApplyState(immediate: true);
        UpdateLiveData();
        _statsTimer.Start();
        _themeTimer.Start();

        await _mediaService.InitializeAsync();
        await RefreshMediaAsync();
        await RefreshNotificationsAsync();
        _mediaTimer.Start();
        _notificationTimer.Start();
    }

    private void OverlayBehavior_DisplayEnvironmentChanged(object? sender, EventArgs e)
    {
        RefreshTargetMonitor(force: true);
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
        AnimateWindow(target.Width, target.Height, target.Top, duration);
        UpdateGeometry();
    }

    private (double Width, double Height, double Top) ResolveLayout(SurfaceState state)
    {
        var screenWidth = GetTargetScreenWidthDip();
        var baseWidth = ResolveBaseSurfaceWidth(screenWidth);
        var maxWidth = Math.Max(185, screenWidth - _settings.SideMargin * 2);
        double surfaceWidth;
        double windowHeight;

        switch (state)
        {
            case SurfaceState.Hover:
                surfaceWidth = baseWidth * 1.015;
                windowHeight = _settings.Style == IslandStyle.Notch ? 53 : 69;
                break;
            case SurfaceState.Peek:
                surfaceWidth = baseWidth * 1.035;
                windowHeight = _settings.Style == IslandStyle.Notch ? 58 : 73;
                break;
            case SurfaceState.Expanded:
                surfaceWidth = _settings.WidthPreset == WidthPreset.FullWidth
                    ? maxWidth
                    : Math.Min(Math.Max(baseWidth, 960), maxWidth);
                windowHeight = _settings.Style == IslandStyle.Notch ? 216 : 264;
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

    private double GetTargetScreenWidthDip()
    {
        if (_currentMonitor is not null)
        {
            return _currentMonitor.DipWidth;
        }

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

    private void AnimateWindow(double targetWidth, double targetHeight, double targetTop, int durationMs)
    {
        _targetTopDip = targetTop;
        PositionOnCurrentMonitor();

        if (durationMs <= 0)
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = targetWidth;
            Height = targetHeight;
            UpdateGeometry();
            PositionOnCurrentMonitor();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(WidthProperty, CreateAnimation(ActualWidth > 0 ? ActualWidth : Width, targetWidth, durationMs, ease));
        BeginAnimation(HeightProperty, CreateAnimation(ActualHeight > 0 ? ActualHeight : Height, targetHeight, durationMs, ease));
    }

    private static DoubleAnimation CreateAnimation(double from, double to, int durationMs, IEasingFunction easing)
    {
        return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
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

    private void StatsTimer_Tick(object? sender, EventArgs e)
    {
        UpdateLiveData();
        RefreshTargetMonitor(force: false);
    }

    private async void MediaTimer_Tick(object? sender, EventArgs e) => await RefreshMediaAsync();

    private async void NotificationTimer_Tick(object? sender, EventArgs e) => await RefreshNotificationsAsync();

    private async Task RefreshNotificationsAsync()
    {
        var snapshot = await _notificationService.SampleAsync();
        var show = snapshot.AccessAllowed;
        _notificationCount = show ? snapshot.Count : 0;
        NotificationColumn.Width = show ? new GridLength(190) : new GridLength(0);
        NotificationStripGroup.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NotificationSeparator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (!show)
        {
            NotificationPrimaryText.Text = string.Empty;
            NotificationDetailText.Text = string.Empty;
            return;
        }

        if (snapshot.HasNotifications)
        {
            NotificationPrimaryText.Text = $"{snapshot.AppName}  ·  {snapshot.Count}";
            NotificationDetailText.Text = snapshot.Text;
        }
        else
        {
            NotificationPrimaryText.Text = "No notifications";
            NotificationDetailText.Text = string.Empty;
        }
    }

    public bool NotificationsAllowed => _notificationService.IsAccessAllowed();

    public async Task EnableNotificationsAsync()
    {
        _ = await _notificationService.RequestAccessAsync();
        await RefreshNotificationsAsync();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }


    private async Task RefreshMediaAsync()
    {
        var media = await _mediaService.SampleAsync();
        byte[]? artwork;
        bool isMedia;

        if (media.HasSession)
        {
            isMedia = true;
            artwork = media.Artwork;
            MediaTitleText.Text = media.Title;
            MediaSubtitleText.Text = media.Subtitle;
            ExpandedMediaTitle.Text = media.Title;
            ExpandedMediaSubtitle.Text = string.IsNullOrWhiteSpace(media.SourceApp)
                ? media.Subtitle
                : $"{media.Subtitle} \u00B7 {media.SourceApp}";
        }
        else
        {
            isMedia = false;
            var foreground = _foregroundAppService.Sample();
            artwork = foreground.IconPng;
            MediaTitleText.Text = foreground.Title;
            MediaSubtitleText.Text = foreground.ProcessName;
            ExpandedMediaTitle.Text = foreground.Title;
            ExpandedMediaSubtitle.Text = foreground.ProcessName;
        }

        ApplyArtwork(artwork, isMedia);
        MediaProgressBar.Value = media.Progress;
        MediaProgressBar.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
        MediaControlsPanel.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
        PreviousMediaButton.IsEnabled = media.HasSession;
        PlayPauseMediaButton.IsEnabled = media.HasSession;
        NextMediaButton.IsEnabled = media.HasSession;
        PlayIconViewbox.Visibility = media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIconViewbox.Visibility = media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyArtwork(byte[]? bytes, bool isMedia)
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

                MediaArtworkBorder.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                ExpandedArtworkBorder.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                MediaGlyphPath.Visibility = Visibility.Collapsed;
                ExpandedArtworkGlyphPath.Visibility = Visibility.Collapsed;
                return;
            }
            catch
            {
                // Fall through to the vector placeholder.
            }
        }

        var fallback = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        fallback.Freeze();
        MediaArtworkBorder.Background = fallback;
        ExpandedArtworkBorder.Background = fallback;
        var fallbackGeometry = (Geometry)FindResource(isMedia ? "MediaFallbackGeometry" : "ActiveAppFallbackGeometry");
        MediaGlyphPath.Data = fallbackGeometry;
        ExpandedArtworkGlyphPath.Data = fallbackGeometry;
        MediaGlyphPath.Visibility = Visibility.Visible;
        ExpandedArtworkGlyphPath.Visibility = Visibility.Visible;
    }

    private void FocusToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _focusTimerService.Toggle();
        UpdateLiveData();
    }

    public void StartFocusTimer(int minutes)
    {
        _focusTimerService.Start(TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 180)));
        UpdateLiveData();
    }

    public void ToggleFocusTimer()
    {
        _focusTimerService.Toggle();
        UpdateLiveData();
    }

    public void ResetFocusTimer()
    {
        _focusTimerService.Reset();
        UpdateLiveData();
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
        CompactStatsText.Text = $"CPU {stats.CpuPercent:0}%   GPU {stats.GpuPercent:0}%   RAM {stats.RamPercent:0}%";
        ExpandedSystemText.Text = $"CPU {stats.CpuPercent:0}%   GPU {stats.GpuPercent:0}%   RAM {stats.RamPercent:0}%";
        ExpandedNetworkText.Text = $"\u2193 {stats.DownloadMbps:0.0}   \u2191 {stats.UploadMbps:0.0} Mbps";

        if (stats.StorageTotalBytes > 0)
        {
            var used = Math.Max(0, stats.StorageTotalBytes - stats.StorageFreeBytes);
            StorageText.Text = $"{FormatBytes(stats.StorageFreeBytes)} free / {FormatBytes(stats.StorageTotalBytes)}";
            StorageProgressBar.Value = Math.Clamp((double)used / stats.StorageTotalBytes, 0, 1);
        }
        else
        {
            StorageText.Text = "Unavailable";
            StorageProgressBar.Value = 0;
        }

        BatteryStripGroup.Visibility = stats.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        BatterySeparator.Visibility = stats.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        BatteryColumn.Width = stats.HasBattery ? new GridLength(120) : new GridLength(0);
        if (stats.HasBattery)
        {
            BatteryText.Text = $"{stats.BatteryPercent:0}%";
            BatteryStateText.Text = stats.BatteryCharging ? "Charging" : "On battery";
        }

        var downloads = _downloadMonitorService.Sample();
        if (downloads.HasActive)
        {
            DownloadPrimaryText.Text = downloads.PrimaryName;
            DownloadDetailText.Text = downloads.PrimaryMegabytesPerSecond > 0.05
                ? $"{FormatBytes(downloads.PrimaryBytes)}  ·  {downloads.PrimaryMegabytesPerSecond:0.0} MB/s  ·  {downloads.ActiveCount} active"
                : $"{FormatBytes(downloads.PrimaryBytes)}  ·  {downloads.ActiveCount} active";
        }
        else
        {
            DownloadPrimaryText.Text = "No active downloads";
            DownloadDetailText.Text = string.Empty;
        }

        var focus = _focusTimerService.Snapshot();
        FocusTimerText.Text = focus.Display;
        FocusPlayIconViewbox.Visibility = focus.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        FocusPauseIconViewbox.Visibility = focus.IsRunning ? Visibility.Visible : Visibility.Collapsed;

        var activity = new List<string>();
        if (focus.IsRunning)
        {
            activity.Add($"Focus {focus.Display}");
        }
        if (downloads.HasActive)
        {
            activity.Add($"DL {downloads.ActiveCount}");
        }
        if (_notificationCount > 0)
        {
            activity.Add($"N {_notificationCount}");
        }
        var compactBaseWidth = ResolveBaseSurfaceWidth(GetTargetScreenWidthDip());
        if (compactBaseWidth >= 1400)
        {
            activity.Add($"NET ↓{stats.DownloadMbps:0.0} ↑{stats.UploadMbps:0.0}");
            if (stats.StorageFreeBytes > 0)
            {
                activity.Add($"SSD {FormatBytes(stats.StorageFreeBytes)}");
            }
            if (stats.HasBattery)
            {
                activity.Add($"BAT {stats.BatteryPercent:0}%");
            }
        }
        CompactActivityText.Text = string.Join("  ·  ", activity);
        UpdateCompactDensity();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "--";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
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

    private void RefreshTargetMonitor(bool force)
    {
        MonitorDescriptor target;
        try
        {
            target = _monitorService.Resolve(_settings);
        }
        catch
        {
            return;
        }

        var changed = _currentMonitor is null
                      || _currentMonitor.Handle != target.Handle
                      || _currentMonitor.ScalePercent != target.ScalePercent
                      || _currentMonitor.PixelWidth != target.PixelWidth
                      || _currentMonitor.PixelHeight != target.PixelHeight;

        if (!force && !changed)
        {
            return;
        }

        if (!force && _settings.MonitorMode == MonitorMode.FollowActiveApp && IsLoaded && IsVisible)
        {
            TransitionToMonitor(target);
            return;
        }

        _currentMonitor = target;
        PositionOnCurrentMonitor();
        Dispatcher.BeginInvoke(() => ApplyState(immediate: true), DispatcherPriority.Loaded);
    }

    private void TransitionToMonitor(MonitorDescriptor target)
    {
        if (_monitorTransitioning)
        {
            _currentMonitor = target;
            PositionOnCurrentMonitor();
            return;
        }

        _monitorTransitioning = true;
        var fadeOut = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(80))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            _currentMonitor = target;
            PositionOnCurrentMonitor();
            ApplyState(immediate: true);

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeIn.Completed += (_, _) =>
            {
                Root.BeginAnimation(OpacityProperty, null);
                Root.Opacity = 1;
                _monitorTransitioning = false;
            };
            Root.BeginAnimation(OpacityProperty, fadeIn);
        };
        Root.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void PositionOnCurrentMonitor()
    {
        if (_hwnd == IntPtr.Zero || _currentMonitor is null)
        {
            return;
        }

        _monitorService.PositionWindow(_hwnd, _currentMonitor, _targetTopDip);
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

    public void SetStyle(IslandStyle style)
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

    public void SetWidth(WidthPreset width)
    {
        _settings.WidthPreset = width;
        SaveAndRefresh();
    }

    public void SetMaterial(SurfaceMaterial material)
    {
        _settings.Material = material;
        _themeService.Apply(_settings.Theme, _settings.Material);
        ApplyBackdropMaterial();
        SaveAndRefresh();
    }

    public void SetTheme(AppThemeMode theme)
    {
        _settings.Theme = theme;
        _themeService.Apply(_settings.Theme, _settings.Material);
        ApplyBackdropMaterial();
        SaveAndRefresh();
    }

    public void SetMonitor(MonitorMode mode, string? deviceName)
    {
        _settings.MonitorMode = mode;
        _settings.MonitorDeviceName = mode == MonitorMode.Fixed ? deviceName : null;
        _settingsStore.Save(_settings);
        RefreshTargetMonitor(force: true);
        SyncSettingsUi();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetStartWithWindows(bool enabled)
    {
        _settings.StartWithWindows = enabled;
        _startupService.SetEnabled(enabled);
        _settingsStore.Save(_settings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleVisibilityFromTray()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            ApplyState(immediate: true);
            PositionOnCurrentMonitor();
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ExpandFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }
        _state = SurfaceState.Expanded;
        ApplyState();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void SaveAndRefresh()
    {
        _settingsStore.Save(_settings);
        SyncSettingsUi();
        ApplyState();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
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
        var baseWidth = ResolveBaseSurfaceWidth(GetTargetScreenWidthDip());
        var peek = _state == SurfaceState.Peek;

        CompactMediaText.Visibility = baseWidth >= 240 ? Visibility.Visible : Visibility.Collapsed;
        MediaSubtitleText.Visibility = baseWidth >= 600 || (peek && baseWidth >= 300)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactStatsText.Visibility = baseWidth >= 760 || (peek && baseWidth >= 430)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactActivityText.Visibility = !string.IsNullOrWhiteSpace(CompactActivityText.Text)
            && (baseWidth >= 1000 || (peek && baseWidth >= 700))
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyBackdropMaterial()
    {
        _backdropResult = _backdropService.Apply(this, _settings.Material, _settings.Theme, _themeService);
        UpdateGeometry();
    }
}