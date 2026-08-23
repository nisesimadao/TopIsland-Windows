using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TopIsland.Controls;
using TopIsland.Interop;
using TopIsland.Models;
using TopIsland.Services;

namespace TopIsland;

public partial class MainWindow : Window
{
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
    private NotificationService? _notificationService;
    private DiscordVoiceService? _discordVoiceService;
    private HardwareTelemetryService? _hardwareTelemetryService;
    private AudioStatusService? _audioStatusService;
    private AudioStatusSnapshot _lastAudioStatus = AudioStatusSnapshot.Unavailable;
    private readonly BlurHostService _blurHostService = new();
    private readonly MotionBackdropSnapshotService _motionBackdropSnapshotService = new();
    private readonly MotionCompositionHost _motionCompositionHost;
    private readonly MonitorService _monitorService;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private readonly DispatcherTimer _themeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _discordTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _peekTimer = new();
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // MouseEnter remains immediate. This timer is only a safety net for the
    // transparent/no-activate HWND, so it does not need to run every display frame.
    private readonly DispatcherTimer _pointerTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly DispatcherTimer _secondaryResumeTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(650)
    };
    private readonly DispatcherTimer _backdropHandoffTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(42)
    };
    private readonly DispatcherTimer _compositionHandoffTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(24)
    };
    private readonly SemaphoreSlim _mediaRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _notificationRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _discordRefreshGate = new(1, 1);

    private AppSettings _settings = new();
    private SurfaceState _state = SurfaceState.Idle;
    private bool _lastSystemLight;
    private OverlayWindowBehavior? _overlayBehavior;
    private BackdropApplyResult _backdropResult = new(false, false, "Unavailable");
    private byte[]? _lastArtworkBytes;
    private bool? _lastArtworkIsMedia;
    private MonitorDescriptor? _currentMonitor;
    private IntPtr _hwnd;
    private double _targetTopDip;
    private int _notificationCount;
    private int _activeDownloadCount;
    private bool _hasMediaSession;
    private bool _focusIsRunning;
    private long _monitorTransitionSerial;
    private bool _externalBlurAvailable;
    private bool _windowTransitionActive;
    private bool _surfaceMotionFrameInProgress;
    private bool _motionPollingSuspended;
    private bool _resumeStatsAfterMotion;
    private bool _resumeMediaAfterMotion;
    private bool _resumeThemeAfterMotion;
    private readonly RectangleGeometry _compactContentClip = new();
    private readonly RectangleGeometry _expandedContentClip = new();
    private readonly RectangleGeometry _surfaceContentClip = new();
    private readonly RuntimeIslandGeometry _runtimeGeometry = new();
    private bool _motionHostConfigured;
    private bool _motionHostUpdating;
    private double _motionHostWidth;
    private double _motionHostHeight;
    private double _motionHostTopDip;
    private double _renderedShadowOpacity;
    private bool _motionBackdropActive;
    private long _windowTransitionStartedTimestamp;
    private int _windowTransitionDurationMs;
    private double _windowFromWidth;
    private double _windowFromHeight;
    private double _windowFromTopDip;
    private double _windowFromShapeProgress;
    private double _windowFromRevealProgress;
    private double _windowFromSurfaceOpacity;
    private double _windowFromShadowOpacity;
    private double _windowToWidth;
    private double _windowToHeight;
    private double _windowToTopDip;
    private double _windowToShapeProgress;
    private double _windowToRevealProgress;
    private double _windowToSurfaceOpacity;
    private double _windowToShadowOpacity;
    private double _renderedWidth;
    private double _renderedHeight;
    private double _currentTopDip;
    private double _shapeExpansionProgress;
    private readonly DispatcherTimer _transitionGuardTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _pointerWasInside;
    private bool _edgeRevealVisualHidden;
    private double _edgeRevealProgress = 1.0;
    private TimeSpan _lastMotionRenderingTime = TimeSpan.MinValue;
    private bool _compositionSurfaceActive;
    private bool _compositionBackdropActive;
    private MotionCompositionContent? _cachedMotionCompositionContent;
    private string? _cachedMotionCompositionContentKey;
    private bool _motionCompositionContentCacheRefreshing;

    public event EventHandler? SettingsChanged;

    public AppSettings Settings => _settings;

    public MainWindow(MonitorService monitorService)
    {
        _monitorService = monitorService;
        InitializeComponent();
        _motionCompositionHost = new MotionCompositionHost(Dispatcher);

        _settings = _settingsStore.Load();
        _peekTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.HoverPeekDelayMs, 120, 2000));
        _peekTimer.Tick += PeekTimer_Tick;
        _collapseTimer.Tick += CollapseTimer_Tick;
        _pointerTimer.Tick += PointerTimer_Tick;
        _secondaryResumeTimer.Tick += SecondaryResumeTimer_Tick;
        _backdropHandoffTimer.Tick += BackdropHandoffTimer_Tick;
        _compositionHandoffTimer.Tick += CompositionHandoffTimer_Tick;
        _transitionGuardTimer.Tick += TransitionGuardTimer_Tick;
        CompactBar.Clip = _compactContentClip;
        ExpandedPanel.Clip = _expandedContentClip;
        ContentHost.Clip = _surfaceContentClip;
        _statsTimer.Tick += StatsTimer_Tick;
        _mediaTimer.Tick += MediaTimer_Tick;
        _themeTimer.Tick += ThemeTimer_Tick;
        _notificationTimer.Tick += NotificationTimer_Tick;
        _discordTimer.Tick += DiscordTimer_Tick;
        SizeChanged += (_, _) =>
        {
            if (_motionHostUpdating || _windowTransitionActive)
            {
                return;
            }

            UpdateGeometry(
                _renderedWidth > 1 ? _renderedWidth : null,
                _renderedHeight > 1 ? _renderedHeight : null);
            PositionOnCurrentMonitor();
        };
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _overlayBehavior = new OverlayWindowBehavior(this, point => !_edgeRevealVisualHidden && SurfacePath.Data?.FillContains(point) == true);
        _overlayBehavior.DisplayEnvironmentChanged += OverlayBehavior_DisplayEnvironmentChanged;
        _overlayBehavior.Attach();

        _currentMonitor = _monitorService.Resolve(_settings);
        BlurHostService.SetRevealProgress(_hwnd, 1.0);
        BlurHostService.SetShapeProgress(_hwnd, _shapeExpansionProgress);
        _externalBlurAvailable = RequiresLiveBlur(_settings.Material) && _blurHostService.Start(_hwnd);
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
        _statsTimer.Stop();
        _mediaTimer.Stop();
        _themeTimer.Stop();
        _notificationTimer.Stop();
        _discordTimer.Stop();
        _peekTimer.Stop();
        _collapseTimer.Stop();
        _pointerTimer.Stop();
        _secondaryResumeTimer.Stop();
        _backdropHandoffTimer.Stop();
        _compositionHandoffTimer.Stop();
        _transitionGuardTimer.Stop();
        StopWindowTransition();
        BlurHostService.ClearRevealProgress(_hwnd);
        BlurHostService.ClearShapeProgress(_hwnd);
        BlurHostService.ClearSurfaceBounds(_hwnd);
        BlurHostService.ClearMotionSnapshotActive(_hwnd);
        _blurHostService.Dispose();
        _motionBackdropSnapshotService.Dispose();
        _motionCompositionHost.Dispose();
        _statsService.Dispose();
        _hardwareTelemetryService?.Dispose();
        _hardwareTelemetryService = null;
        _audioStatusService?.Dispose();
        _audioStatusService = null;
        _lastAudioStatus = AudioStatusSnapshot.Unavailable;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _themeService.Apply(_settings.Theme, _settings.Material, _externalBlurAvailable);
        _lastSystemLight = _themeService.IsSystemLightTheme();
        if (_settings.StartWithWindows)
        {
            _startupService.SetEnabled(true);
        }

        RefreshTargetMonitor(force: true);
        SyncSettingsUi();

        _state = _settings.StartExpanded ? SurfaceState.Expanded : SurfaceState.Idle;
        ApplyState(immediate: true);
        PrewarmMotionCompositionHost();
        UpdateLiveData();
        _ = Dispatcher.BeginInvoke(RefreshMotionCompositionContentCache, DispatcherPriority.Background);
        _statsTimer.Start();
        _pointerWasInside = IsPointerInteractionActive();
        _pointerTimer.Start();
        if (_pointerWasInside)
        {
            HandlePointerEntered();
        }
        _transitionGuardTimer.Start();
        _themeTimer.Start();

        await _mediaService.InitializeAsync();
        await RefreshMediaAsync();
        _ = Dispatcher.BeginInvoke(RefreshMotionCompositionContentCache, DispatcherPriority.Background);
        _mediaTimer.Start();
        UpdateSecondaryPollingState(refreshImmediately: true);

    }
    private void OverlayBehavior_DisplayEnvironmentChanged(object? sender, EventArgs e)
    {
        RefreshTargetMonitor(force: true);
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
