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
    private bool _visible;
    private bool _disposed;

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
        SetTimer(_hwnd, new UIntPtr(1), 16, IntPtr.Zero);
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
        var shouldShow = IsWindowVisible(_target) && _settings.Material is 2 or 3 or 4;
        if (!shouldShow || !GetWindowRect(_target, out var rect))
        {
            Hide();
            return;
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var dpi = Math.Max(96, (int)GetDpiForWindow(_target));
        var scale = dpi / 96.0;
        var expanded = height / scale > 120;

        var intervalMs = width > 2600 || height > 600 ? 50 : 33;
        var now = Environment.TickCount64;
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
                expanded,
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
            4 => CreateMaterialCopyOptions(light, scale),
            _ => new BlurFrameOptions(
                BlurSigma: (float)(19 * scale),
                TintAlpha: light ? (byte)28 : (byte)34,
                TintR: light ? (byte)248 : (byte)6,
                TintG: light ? (byte)249 : (byte)7,
                TintB: light ? (byte)251 : (byte)10)
        };
    }

    private static BlurFrameOptions CreateMaterialCopyOptions(bool light, double scale)
    {
        var accent = ReadAccent();
        return new BlurFrameOptions(
            BlurSigma: (float)(16 * scale),
            TintAlpha: light ? (byte)24 : (byte)30,
            TintR: accent.R,
            TintG: accent.G,
            TintB: accent.B);
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

    private static (byte R, byte G, byte B) ReadAccent()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
                "ColorizationColor",
                null);
            if (value is int i)
            {
                var raw = unchecked((uint)i);
                return ((byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
            }
        }
        catch
        {
        }
        return (70, 110, 210);
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
