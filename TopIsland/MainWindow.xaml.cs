using System.IO;
using System.Runtime.InteropServices;
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
    private readonly MonitorService _monitorService;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private readonly DispatcherTimer _themeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _discordTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _peekTimer = new();
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _pointerTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
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
    private long _windowTransitionStartedTimestamp;
    private int _windowTransitionDurationMs;
    private double _windowFromWidth;
    private double _windowFromHeight;
    private double _windowFromTopDip;
    private double _windowFromShapeProgress;
    private double _windowFromRevealProgress;
    private double _windowFromSurfaceOpacity;
    private double _windowFromCompactOpacity;
    private double _windowFromExpandedOpacity;
    private double _windowFromExpandedTranslate;
    private double _windowFromShadowBlur;
    private double _windowFromShadowOpacity;
    private double _windowToWidth;
    private double _windowToHeight;
    private double _windowToTopDip;
    private double _windowToShapeProgress;
    private double _windowToRevealProgress;
    private double _windowToSurfaceOpacity;
    private double _windowToCompactOpacity;
    private double _windowToExpandedOpacity;
    private double _windowToExpandedTranslate;
    private double _windowToShadowBlur;
    private double _windowToShadowOpacity;
    private double _renderedWidth;
    private double _renderedHeight;
    private double _currentTopDip;
    private double _shapeExpansionProgress;
    private readonly DispatcherTimer _transitionGuardTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _pointerWasInside;
    private bool _edgeRevealVisualHidden;
    private double _edgeRevealProgress = 1.0;

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
        _pointerTimer.Tick += PointerTimer_Tick;
        _transitionGuardTimer.Tick += TransitionGuardTimer_Tick;
        _statsTimer.Tick += StatsTimer_Tick;
        _mediaTimer.Tick += MediaTimer_Tick;
        _themeTimer.Tick += ThemeTimer_Tick;
        _notificationTimer.Tick += NotificationTimer_Tick;
        _discordTimer.Tick += DiscordTimer_Tick;
        SizeChanged += (_, _) =>
        {
            // During a coordinated transition the render driver owns geometry,
            // size and position together. Recomputing from the delayed WPF
            // ActualWidth/ActualHeight here would put the clip one frame behind.
            if (!_windowTransitionActive)
            {
                UpdateGeometry();
                PositionOnCurrentMonitor();
            }
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
        _transitionGuardTimer.Stop();
        StopWindowTransition();
        _blurHostService.Dispose();
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
        UpdateLiveData();
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
}
