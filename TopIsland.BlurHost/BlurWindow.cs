using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TopIsland.BlurHost;

internal sealed class BlurWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const uint WsExNoActivate = 0x08000000;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int WmDestroy = 0x0002;
    private const int WmNcHitTest = 0x0084;
    private const uint WmQuit = 0x0012;
    private const uint PmRemove = 0x0001;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;
    private const uint WaitObject0 = 0;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const int HtTransparent = -1;
    private const string RevealProgressProperty = "TopIsland.BlurRevealProgress";
    private const string ShapeProgressProperty = "TopIsland.BlurShapeProgress";
    private const string SurfaceWidthProperty = "TopIsland.BlurSurfaceWidth";
    private const string SurfaceHeightProperty = "TopIsland.BlurSurfaceHeight";
    private const string SurfaceOffsetXProperty = "TopIsland.BlurSurfaceOffsetX";
    private const string SurfaceOffsetYProperty = "TopIsland.BlurSurfaceOffsetY";
    private const string MotionSnapshotProperty = "TopIsland.BlurMotionSnapshot";

    private static readonly string WindowClass = $"TopIsland.BlurHost.{Environment.ProcessId}";
    private static readonly WndProcDelegate WndProcThunk = WndProc;
    private static readonly Dictionary<IntPtr, BlurWindow> Instances = new();

    private readonly IntPtr _target;
    private readonly string _settingsPath;
    private SkiaBlurRenderer? _renderer;
    private bool _nativeAcrylic;

    private IntPtr _hwnd;
    private BlurSettings _settings = new();
    private DateTime _lastSettingsReadUtc;
    private DateTime _lastSettingsWriteUtc;
    private long _lastFrameTick;
    private long _lastBackdropTick;
    private long _lastGeometryChangeTick;
    private NativeRect _lastTargetRect;
    private bool _hasLastTargetRect;
    private int _lastSurfaceWidth;
    private int _lastSurfaceHeight;
    private int _lastSurfaceOffsetX;
    private int _lastSurfaceOffsetY;
    private bool _visible;
    private bool _disposed;
    private bool _highResolutionTimerActive;
    private IntPtr _renderTimer;
    private bool _wasMotionSnapshotActive;

    public BlurWindow(IntPtr target)
    {
        _target = target;
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TopIsland",
            "settings.json");

        RegisterWindowClass();
        _hwnd = CreateWindowEx(
            WsExTopMost | WsExTransparent | WsExToolWindow | WsExNoActivate,
            WindowClass,
            "TopIsland Blur Host",
            WsPopup,
            0, 0, 1, 1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        Instances[_hwnd] = this;
        // Static Acrylic/Glass stays on the proven layered Skia renderer. The
        // native-acrylic experiment used a full rectangular HWND and could flash
        // black while the DirectComposition motion surface took over.
        _nativeAcrylic = false;
        EnableLayeredFallbackStyle();
        _renderer = new SkiaBlurRenderer(_hwnd);
        _highResolutionTimerActive = TimeBeginPeriod(1) == 0;
        _renderTimer = CreateMotionTimer();
        Update(force: true);
    }

    public void Run()
    {
        if (_renderTimer == IntPtr.Zero)
        {
            while (GetMessage(out var fallbackMessage, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref fallbackMessage);
                DispatchMessage(ref fallbackMessage);
                Update();
            }
            return;
        }

        var handles = new[] { _renderTimer };
        while (!_disposed)
        {
            var wait = MsgWaitForMultipleObjectsEx(
                1,
                handles,
                Infinite,
                QsAllInput,
                MwmoInputAvailable);

            if (wait == WaitObject0)
            {
                Update();
                continue;
            }

            if (wait == WaitObject0 + 1)
            {
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PmRemove))
                {
                    if (msg.message == WmQuit)
                    {
                        return;
                    }
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                continue;
            }

            if (wait == WaitFailed)
            {
                return;
            }
        }
    }

    private static IntPtr CreateMotionTimer()
    {
        var timer = CreateWaitableTimerEx(
            IntPtr.Zero,
            null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);
        if (timer == IntPtr.Zero)
        {
            timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, TimerAllAccess);
        }
        if (timer == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // Negative due time is relative, in 100 ns units. The 4 ms periodic tick
        // gives a 120 Hz display more than one opportunity per refresh without
        // relying on USER/WM_TIMER, whose effective minimum is too coarse here.
        long dueTime = -10_000;
        if (!SetWaitableTimer(timer, ref dueTime, 4, IntPtr.Zero, IntPtr.Zero, false))
        {
            CloseHandle(timer);
            return IntPtr.Zero;
        }
        return timer;
    }

    private void Update(bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        if (!IsWindow(_target))
        {
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            {
                DestroyWindow(_hwnd);
            }
            return;
        }

        ReadSettingsIfNeeded(force);
        var motionSnapshotActive = GetProp(_target, MotionSnapshotProperty) != IntPtr.Zero;
        if (motionSnapshotActive)
        {
            _wasMotionSnapshotActive = true;
            Hide();
            return;
        }
        if (_wasMotionSnapshotActive)
        {
            _wasMotionSnapshotActive = false;
            _lastBackdropTick = 0;
            _renderer?.InvalidateBackdrop();
        }

        var revealProgress = ReadRevealProgress();
        var shouldShow = IsWindowVisible(_target)
                         && _settings.Material is 2 or 3
                         && revealProgress > 0.001;
        if (!shouldShow || !GetWindowRect(_target, out var rect))
        {
            Hide();
            return;
        }

        var hostWidth = Math.Max(1, rect.Right - rect.Left);
        var hostHeight = Math.Max(1, rect.Bottom - rect.Top);
        var dpi = Math.Max(96, (int)GetDpiForWindow(_target));
        var scale = dpi / 96.0;
        var legacyExpanded = hostHeight / scale > 120;
        var shapeProgress = ReadProgress(ShapeProgressProperty, legacyExpanded ? 1.0 : 0.0);
        var expanded = shapeProgress > 0.5;

        var surfaceWidthDip = ReadMetric(SurfaceWidthProperty, hostWidth / scale);
        var surfaceHeightDip = ReadMetric(SurfaceHeightProperty, hostHeight / scale);
        var surfaceOffsetXDip = ReadMetric(SurfaceOffsetXProperty, 0);
        var surfaceOffsetYDip = ReadMetric(SurfaceOffsetYProperty, 0);
        var width = Math.Clamp((int)Math.Round(surfaceWidthDip * scale), 1, hostWidth);
        var height = Math.Clamp((int)Math.Round(surfaceHeightDip * scale), 1, hostHeight);
        var offsetX = Math.Clamp((int)Math.Round(surfaceOffsetXDip * scale), 0, Math.Max(0, hostWidth - width));
        var offsetY = Math.Clamp((int)Math.Round(surfaceOffsetYDip * scale), 0, Math.Max(0, hostHeight - height));
        var screenX = rect.Left + offsetX;
        var screenY = rect.Top + offsetY;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var geometryChanged = !_hasLastTargetRect
                              || !RectsEqual(rect, _lastTargetRect)
                              || width != _lastSurfaceWidth
                              || height != _lastSurfaceHeight
                              || offsetX != _lastSurfaceOffsetX
                              || offsetY != _lastSurfaceOffsetY;
        if (geometryChanged)
        {
            _lastTargetRect = rect;
            _hasLastTargetRect = true;
            _lastSurfaceWidth = width;
            _lastSurfaceHeight = height;
            _lastSurfaceOffsetX = offsetX;
            _lastSurfaceOffsetY = offsetY;
            _lastGeometryChangeTick = now;
        }

        // Geometry/reveal is cheap and follows the display cadence. Desktop
        // capture + Gaussian blur is deliberately decoupled and cached: redoing
        // that expensive filter on every shape frame starved the foreground WPF
        // compositor and capped both processes near 60 fps.
        var recentlyMoving = _lastGeometryChangeTick != 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(_lastGeometryChangeTick, now).TotalMilliseconds < 280;
        var revealInMotion = revealProgress > 0.001 && revealProgress < 0.999;
        var shapeInMotion = shapeProgress > 0.001 && shapeProgress < 0.999;
        var motionInProgress = recentlyMoving || revealInMotion || shapeInMotion;
        var composeIntervalMs = motionInProgress
            ? 10.5
            : width > 2600 || height > 600
                ? 66
                : expanded ? 50 : 33;
        // During a ~200 ms shell morph keep the already blurred backdrop frozen.
        // Re-capturing the desktop and running Gaussian blur every 32 ms caused a
        // periodic 25-30 ms stall in the foreground compositor. The spatial blur
        // quality is unchanged; only the low-frequency backdrop sample is held
        // during the brief motion and refreshed immediately once motion settles.
        var backdropIntervalMs = motionInProgress ? 1000.0 : composeIntervalMs;
        var frameElapsedMs = _lastFrameTick == 0
            ? double.PositiveInfinity
            : System.Diagnostics.Stopwatch.GetElapsedTime(_lastFrameTick, now).TotalMilliseconds;
        if (force || frameElapsedMs >= composeIntervalMs)
        {
            if (_nativeAcrylic)
            {
                if (RenderNativeAcrylic(rect, hostWidth, hostHeight, offsetX, offsetY, width, height, scale, _settings.Style, shapeProgress, revealProgress))
                {
                    _lastFrameTick = now;
                    SetWindowPos(
                        _hwnd,
                        _target,
                        rect.Left, rect.Top, hostWidth, hostHeight,
                        SwpNoActivate | SwpShowWindow);
                    _visible = true;
                }
                return;
            }

            var backdropElapsedMs = _lastBackdropTick == 0
                ? double.PositiveInfinity
                : System.Diagnostics.Stopwatch.GetElapsedTime(_lastBackdropTick, now).TotalMilliseconds;
            var refreshBackdrop = force || backdropElapsedMs >= backdropIntervalMs;
            var options = CreateFrameOptions(_settings.Material, _settings.Theme, scale);
            if (_renderer is not null && _renderer.Render(
                rect.Left,
                rect.Top,
                hostWidth,
                hostHeight,
                offsetX,
                offsetY,
                width,
                height,
                scale,
                _settings.Style,
                shapeProgress,
                revealProgress,
                options,
                refreshBackdrop))
            {
                if (refreshBackdrop) _lastBackdropTick = now;
                _lastFrameTick = now;
                // Keep the helper directly below TopIsland in the topmost z-band.
                SetWindowPos(
                    _hwnd,
                    _target,
                    0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
                _visible = true;
            }
        }
    }


    private bool TryEnableNativeAcrylic()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return false;
        }

        try
        {
            var policy = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                // Keep native tint almost transparent. TopIsland's WPF surface
                // provides the material-specific tint/border above this blur.
                GradientColor = 0x01000000,
                AnimationId = 0
            };
            var size = Marshal.SizeOf<AccentPolicy>();
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, memory, false);
                var data = new WindowCompositionAttribData
                {
                    Attribute = WcaAccentPolicy,
                    Data = memory,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(_hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }
        catch
        {
            return false;
        }
    }

    private void DisableNativeAcrylic()
    {
        if (!_nativeAcrylic || _hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var policy = new AccentPolicy { AccentState = AccentDisabled };
            var size = Marshal.SizeOf<AccentPolicy>();
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, memory, false);
                var data = new WindowCompositionAttribData
                {
                    Attribute = WcaAccentPolicy,
                    Data = memory,
                    SizeOfData = size
                };
                _ = SetWindowCompositionAttribute(_hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }
        catch { }
    }

    private void EnableLayeredFallbackStyle()
    {
        var style = GetWindowLongPtr(_hwnd, -20).ToInt64();
        style |= WsExLayered;
        _ = SetWindowLongPtr(_hwnd, -20, new IntPtr(style));
    }

    private bool RenderNativeAcrylic(
        NativeRect hostRect,
        int hostWidth,
        int hostHeight,
        int offsetX,
        int offsetY,
        int width,
        int height,
        double scale,
        int style,
        double shapeProgress,
        double revealProgress)
    {
        if (revealProgress <= 0.001)
        {
            Hide();
            return true;
        }

        var points = BuildNativeRegionPoints(
            offsetX, offsetY, width, height, scale, style, shapeProgress, revealProgress);
        if (points.Length < 3)
        {
            return false;
        }

        var region = CreatePolygonRgn(points, points.Length, 1);
        if (region == IntPtr.Zero)
        {
            return false;
        }

        // On success Windows owns the region handle. On failure we release it.
        if (SetWindowRgn(_hwnd, region, true) == 0)
        {
            DeleteObject(region);
            return false;
        }
        return true;
    }

    private static NativeRegionPoint[] BuildNativeRegionPoints(
        int offsetX,
        int offsetY,
        int width,
        int height,
        double scale,
        int style,
        double shapeProgress,
        double revealProgress)
    {
        const int curveSteps = 10;
        var points = new List<NativeRegionPoint>(64);
        var pad = 16.0 * scale;

        if (style != 1)
        {
            var left = offsetX + pad;
            var top = offsetY + pad;
            var right = offsetX + Math.Max(pad + 1, width - pad);
            var bottom = offsetY + Math.Max(pad + 1, height - pad);
            var surfaceHeight = bottom - top;
            var compactRadius = surfaceHeight / 2.0;
            var expandedRadius = Math.Min(28.0 * scale, compactRadius);
            var radius = compactRadius + (expandedRadius - compactRadius) * Math.Clamp(shapeProgress, 0, 1);
            AddRoundedRect(points, left, top, right, bottom, radius, curveSteps);
            return points.ToArray();
        }

        revealProgress = Math.Clamp(revealProgress, 0, 1);
        var leftN = offsetX + pad;
        var rightN = offsetX + Math.Max(pad + 1, width - pad);
        var topN = (double)offsetY;
        var fullBottom = offsetY + Math.Max(1.0, height - pad);
        var bottomN = offsetY + Math.Max(0.1, (fullBottom - offsetY) * revealProgress);
        var shoulderScale = Math.Pow(revealProgress, 1.18);
        shapeProgress = Math.Clamp(shapeProgress, 0, 1);
        var topRadius = (6.0 + (19.0 - 6.0) * shapeProgress) * scale * shoulderScale;
        var bottomRadius = (14.0 + (24.0 - 14.0) * shapeProgress) * scale * shoulderScale;
        topRadius = Math.Min(topRadius, Math.Max(0.1, (bottomN - topN) / 2.0));
        bottomRadius = Math.Min(bottomRadius, Math.Max(0.1, (bottomN - topN) / 2.0));
        bottomRadius = Math.Min(bottomRadius, Math.Max(0.1, (rightN - leftN) / 4.0));

        AddPoint(points, leftN, topN);
        AddQuadratic(points, leftN, topN, leftN + topRadius, topN, leftN + topRadius, topN + topRadius, curveSteps);
        AddPoint(points, leftN + topRadius, bottomN - bottomRadius);
        AddQuadratic(points, leftN + topRadius, bottomN - bottomRadius, leftN + topRadius, bottomN, leftN + topRadius + bottomRadius, bottomN, curveSteps);
        AddPoint(points, rightN - topRadius - bottomRadius, bottomN);
        AddQuadratic(points, rightN - topRadius - bottomRadius, bottomN, rightN - topRadius, bottomN, rightN - topRadius, bottomN - bottomRadius, curveSteps);
        AddPoint(points, rightN - topRadius, topN + topRadius);
        AddQuadratic(points, rightN - topRadius, topN + topRadius, rightN - topRadius, topN, rightN, topN, curveSteps);
        return points.ToArray();
    }

    private static void AddRoundedRect(List<NativeRegionPoint> points, double left, double top, double right, double bottom, double radius, int steps)
    {
        AddArc(points, left + radius, top + radius, radius, Math.PI, Math.PI * 1.5, steps);
        AddArc(points, right - radius, top + radius, radius, Math.PI * 1.5, Math.PI * 2.0, steps);
        AddArc(points, right - radius, bottom - radius, radius, 0, Math.PI * 0.5, steps);
        AddArc(points, left + radius, bottom - radius, radius, Math.PI * 0.5, Math.PI, steps);
    }

    private static void AddArc(List<NativeRegionPoint> points, double cx, double cy, double radius, double start, double end, int steps)
    {
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var angle = start + (end - start) * t;
            AddPoint(points, cx + Math.Cos(angle) * radius, cy + Math.Sin(angle) * radius);
        }
    }

    private static void AddQuadratic(List<NativeRegionPoint> points, double x0, double y0, double cx, double cy, double x1, double y1, int steps)
    {
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            var inv = 1.0 - t;
            AddPoint(points,
                inv * inv * x0 + 2 * inv * t * cx + t * t * x1,
                inv * inv * y0 + 2 * inv * t * cy + t * t * y1);
        }
    }

    private static void AddPoint(List<NativeRegionPoint> points, double x, double y)
        => points.Add(new NativeRegionPoint((int)Math.Round(x), (int)Math.Round(y)));


    private double ReadRevealProgress() => ReadProgress(RevealProgressProperty, 1.0);

    private double ReadMetric(string propertyName, double fallback)
    {
        var value = GetProp(_target, propertyName).ToInt64();
        return value <= 0 ? fallback : Math.Max(0, (value - 1) / 100.0);
    }

    private double ReadProgress(string propertyName, double fallback)
    {
        var value = GetProp(_target, propertyName).ToInt64();
        if (value <= 0)
        {
            return fallback;
        }

        return Math.Clamp((value - 1) / 1000.0, 0, 1);
    }

    private static bool RectsEqual(NativeRect left, NativeRect right) =>
        left.Left == right.Left && left.Top == right.Top && left.Right == right.Right && left.Bottom == right.Bottom;

    private static BlurFrameOptions CreateFrameOptions(int material, int theme, double scale)
    {
        var light = IsLightTheme(theme);
        return material switch
        {
            3 => new BlurFrameOptions(
                BlurSigma: (float)(13 * scale),
                TintAlpha: light ? (byte)20 : (byte)24,
                TintR: light ? (byte)250 : (byte)8,
                TintG: light ? (byte)251 : (byte)9,
                TintB: light ? (byte)253 : (byte)12),
            _ => new BlurFrameOptions(
                BlurSigma: (float)(19 * scale),
                TintAlpha: light ? (byte)28 : (byte)34,
                TintR: light ? (byte)248 : (byte)6,
                TintG: light ? (byte)249 : (byte)7,
                TintB: light ? (byte)251 : (byte)10)
        };
    }

    private void ReadSettingsIfNeeded(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastSettingsReadUtc).TotalMilliseconds < 200)
        {
            return;
        }
        _lastSettingsReadUtc = now;

        try
        {
            var write = File.Exists(_settingsPath)
                ? File.GetLastWriteTimeUtc(_settingsPath)
                : DateTime.MinValue;
            if (!force && write == _lastSettingsWriteUtc)
            {
                return;
            }
            _lastSettingsWriteUtc = write;
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            var json = File.ReadAllText(_settingsPath);
            _settings = JsonSerializer.Deserialize<BlurSettings>(json) ?? _settings;
        }
        catch
        {
            // Keep the last valid settings.
        }
    }

    private void Hide()
    {
        if (_visible)
        {
            ShowWindow(_hwnd, SwHide);
            _visible = false;
        }
    }

    private static bool IsLightTheme(int mode)
    {
        if (mode == 1) return true;
        if (mode == 2) return false;
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

    private static void RegisterWindowClass()
    {
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcThunk),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClass
        };

        var atom = RegisterClassEx(ref wc);
        if (atom == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmNcHitTest)
        {
            return new IntPtr(HtTransparent);
        }
        if (msg == WmDestroy)
        {
            Instances.Remove(hwnd);
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisableNativeAcrylic();
        _renderer?.Dispose();
        if (_renderTimer != IntPtr.Zero)
        {
            CancelWaitableTimer(_renderTimer);
            CloseHandle(_renderTimer);
            _renderTimer = IntPtr.Zero;
        }
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            DestroyWindow(_hwnd);
        }
        if (_highResolutionTimerActive)
        {
            _ = TimeEndPeriod(1);
            _highResolutionTimerActive = false;
        }
    }

    private sealed class BlurSettings
    {
        public int Style { get; set; }
        public int Theme { get; set; }
        public int Material { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRegionPoint
    {
        public NativeRegionPoint(int x, int y) { X = x; Y = y; }
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public NativePoint pt;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hwnd, string name);
    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? timerName, uint flags, uint desiredAccess);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")]
    private static extern bool CancelWaitableTimer(IntPtr timer);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePolygonRgn(NativeRegionPoint[] points, int count, int fillMode);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong(hwnd, index));
    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong(hwnd, index, value.ToInt32()));
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);
}
