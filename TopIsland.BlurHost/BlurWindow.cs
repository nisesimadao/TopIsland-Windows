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
    private const uint WsExNoActivate = 0x08000000;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int WmDestroy = 0x0002;
    private const int WmTimer = 0x0113;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const string RevealProgressProperty = "TopIsland.BlurRevealProgress";
    private const string ShapeProgressProperty = "TopIsland.BlurShapeProgress";

    private static readonly string WindowClass = $"TopIsland.BlurHost.{Environment.ProcessId}";
    private static readonly WndProcDelegate WndProcThunk = WndProc;
    private static readonly Dictionary<IntPtr, BlurWindow> Instances = new();

    private readonly IntPtr _target;
    private readonly string _settingsPath;
    private readonly SkiaBlurRenderer _renderer;

    private IntPtr _hwnd;
    private BlurSettings _settings = new();
    private DateTime _lastSettingsReadUtc;
    private DateTime _lastSettingsWriteUtc;
    private long _lastFrameTick;
    private long _lastGeometryChangeTick;
    private NativeRect _lastTargetRect;
    private bool _hasLastTargetRect;
    private bool _visible;
    private bool _disposed;
    private bool _highResolutionTimerActive;

    public BlurWindow(IntPtr target)
    {
        _target = target;
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TopIsland",
            "settings.json");

        RegisterWindowClass();
        _hwnd = CreateWindowEx(
            WsExTopMost | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate,
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
        _renderer = new SkiaBlurRenderer(_hwnd);
        _highResolutionTimerActive = TimeBeginPeriod(1) == 0;
        SetTimer(_hwnd, new UIntPtr(1), 4, IntPtr.Zero);
        Update(force: true);
    }

    public void Run()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
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
        var revealProgress = ReadRevealProgress();
        var shouldShow = IsWindowVisible(_target)
                         && _settings.Material is 2 or 3
                         && revealProgress > 0.001;
        if (!shouldShow || !GetWindowRect(_target, out var rect))
        {
            Hide();
            return;
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var dpi = Math.Max(96, (int)GetDpiForWindow(_target));
        var scale = dpi / 96.0;
        var legacyExpanded = height / scale > 120;
        var shapeProgress = ReadProgress(ShapeProgressProperty, legacyExpanded ? 1.0 : 0.0);
        var expanded = shapeProgress > 0.5;

        var now = Environment.TickCount64;
        var geometryChanged = !_hasLastTargetRect || !RectsEqual(rect, _lastTargetRect);
        if (geometryChanged)
        {
            _lastTargetRect = rect;
            _hasLastTargetRect = true;
            _lastGeometryChangeTick = now;
        }

        // Match the foreground surface at ~60 Hz while the shell is moving or
        // revealing. The previous 33 ms cap made glass/acrylic visibly update at
        // half the rate of the WPF animation even when the foreground itself was
        // rendering smoothly. Once the shell settles, back off to save GPU/CPU.
        var recentlyMoving = now - _lastGeometryChangeTick < 280;
        var revealInMotion = revealProgress > 0.001 && revealProgress < 0.999;
        var shapeInMotion = shapeProgress > 0.001 && shapeProgress < 0.999;
        var intervalMs = recentlyMoving || revealInMotion || shapeInMotion
            ? 4
            : width > 2600 || height > 600
                ? 66
                : expanded ? 50 : 33;
        if (force || now - _lastFrameTick >= intervalMs)
        {
            var options = CreateFrameOptions(_settings.Material, _settings.Theme, scale);
            if (_renderer.Render(
                rect.Left,
                rect.Top,
                width,
                height,
                scale,
                _settings.Style,
                shapeProgress,
                revealProgress,
                options))
            {
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


    private double ReadRevealProgress() => ReadProgress(RevealProgressProperty, 1.0);

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
        if (msg == WmTimer && Instances.TryGetValue(hwnd, out var instance))
        {
            instance.Update();
            return IntPtr.Zero;
        }
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
        _renderer.Dispose();
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            KillTimer(_hwnd, new UIntPtr(1));
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
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapse, IntPtr timerFunc);
    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);
}
