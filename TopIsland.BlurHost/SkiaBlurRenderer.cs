using SkiaSharp;
using System.Runtime.InteropServices;

namespace TopIsland.BlurHost;

internal readonly record struct BlurFrameOptions(
    float BlurSigma,
    byte TintAlpha,
    byte TintR,
    byte TintG,
    byte TintB);

internal sealed class SkiaBlurRenderer : IDisposable
{
    private const float BlurWorkingScale = 0.50f;
    private const uint SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    private readonly IntPtr _hostHwnd;

    private IntPtr _captureDc;
    private IntPtr _captureBitmap;
    private IntPtr _captureOld;
    private IntPtr _captureBits;

    private IntPtr _outputDc;
    private IntPtr _outputBitmap;
    private IntPtr _outputOld;
    private IntPtr _outputBits;

    private int _width;
    private int _height;
    private bool _disposed;

    private SKBitmap? _sourceSkia;
    private SKBitmap? _outputSkia;
    private SKCanvas? _outputCanvas;
    private SKBitmap? _blurSkia;
    private SKCanvas? _blurCanvas;
    private SKPath? _shapePath;
    private SKImageFilter? _blurFilter;
    private SKPaint? _blurPaint;
    private SKPaint? _tintPaint;
    private int _cachedStyle = int.MinValue;
    private bool _cachedExpanded;
    private double _cachedScale = -1;
    private BlurFrameOptions _cachedOptions;

    public SkiaBlurRenderer(IntPtr hostHwnd)
    {
        _hostHwnd = hostHwnd;
    }

    public bool Render(
        int screenX,
        int screenY,
        int width,
        int height,
        double scale,
        int style,
        bool expanded,
        BlurFrameOptions options)
    {
        if (_disposed || width <= 0 || height <= 0)
        {
            return false;
        }

        EnsureBuffers(width, height);

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // SRCCOPY excludes the layered TopIsland and BlurHost windows, so this
            // buffer is the real desktop/application content underneath the surface.
            if (!BitBlt(_captureDc, 0, 0, width, height, screenDc, screenX, screenY, SrcCopy))
            {
                return false;
            }

            EnsureSkiaResources(width, height, scale, style, expanded, options);
            if (_sourceSkia is null || _outputCanvas is null || _blurSkia is null ||
                _blurCanvas is null || _shapePath is null || _blurPaint is null || _tintPaint is null)
            {
                return false;
            }

            // Backdrop blur runs on a half-resolution working surface. A Gaussian
            // blur discards the high-frequency detail that downsampling removes,
            // cutting filter cost sharply without changing the apparent radius.
            _blurCanvas.Clear(SKColors.Transparent);
            var blurDestination = new SKRect(0, 0, _blurSkia.Width, _blurSkia.Height);
            _blurCanvas.DrawBitmap(_sourceSkia, blurDestination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), _blurPaint);
            _blurCanvas.Flush();

            _outputCanvas.Clear(SKColors.Transparent);
            _outputCanvas.Save();
            _outputCanvas.ClipPath(_shapePath, SKClipOperation.Intersect, antialias: true);
            var outputDestination = new SKRect(0, 0, width, height);
            _outputCanvas.DrawBitmap(_blurSkia, outputDestination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

            if (options.TintAlpha > 0)
            {
                _outputCanvas.DrawRect(0, 0, width, height, _tintPaint);
            }

            _outputCanvas.Restore();
            _outputCanvas.Flush();

            var dst = new NativePoint(screenX, screenY);
            var size = new NativeSize(width, height);
            var src = new NativePoint(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };

            return UpdateLayeredWindow(
                _hostHwnd,
                screenDc,
                ref dst,
                ref size,
                _outputDc,
                ref src,
                0,
                ref blend,
                UlwAlpha);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void EnsureSkiaResources(
        int width,
        int height,
        double scale,
        int style,
        bool expanded,
        BlurFrameOptions options)
    {
        if (_sourceSkia is null || _outputSkia is null || _blurSkia is null || _outputCanvas is null || _blurCanvas is null)
        {
            _sourceSkia = new SKBitmap();
            _outputSkia = new SKBitmap();
            var sourceInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var outputInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            if (!_sourceSkia.InstallPixels(sourceInfo, _captureBits, width * 4) ||
                !_outputSkia.InstallPixels(outputInfo, _outputBits, width * 4))
            {
                throw new InvalidOperationException("Could not attach Skia bitmaps to blur buffers.");
            }

            var blurWidth = Math.Max(1, (int)Math.Ceiling(width * BlurWorkingScale));
            var blurHeight = Math.Max(1, (int)Math.Ceiling(height * BlurWorkingScale));
            _blurSkia = new SKBitmap(new SKImageInfo(blurWidth, blurHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            _outputCanvas = new SKCanvas(_outputSkia);
            _blurCanvas = new SKCanvas(_blurSkia);
        }

        if (_shapePath is null || _cachedStyle != style || _cachedExpanded != expanded || Math.Abs(_cachedScale - scale) > 0.001)
        {
            _shapePath?.Dispose();
            _shapePath = CreateShapePath(width, height, scale, style, expanded);
            _cachedStyle = style;
            _cachedExpanded = expanded;
            _cachedScale = scale;
        }

        if (_blurPaint is null || !_cachedOptions.Equals(options))
        {
            _blurPaint?.Dispose();
            _blurFilter?.Dispose();
            var workingSigma = Math.Max(0.1f, options.BlurSigma * BlurWorkingScale);
            _blurFilter = SKImageFilter.CreateBlur(workingSigma, workingSigma, SKShaderTileMode.Clamp);
            _blurPaint = new SKPaint
            {
                IsAntialias = true,
                ImageFilter = _blurFilter
            };
            _cachedOptions = options;
        }

        _tintPaint ??= new SKPaint { IsAntialias = true };
        _tintPaint.Color = new SKColor(options.TintR, options.TintG, options.TintB, options.TintAlpha);
    }

    private static SKPath CreateShapePath(int width, int height, double scale, int style, bool expanded)
    {
        using var builder = new SKPathBuilder();
        var pad = (float)(16 * scale);

        if (style != 1)
        {
            var left = pad;
            var top = pad;
            var right = Math.Max(left + 1, width - pad);
            var bottom = Math.Max(top + 1, height - pad);
            var surfaceHeight = bottom - top;
            var radius = expanded
                ? Math.Min((float)(28 * scale), surfaceHeight / 2f)
                : surfaceHeight / 2f;
            builder.AddRoundRect(
                new SKRoundRect(new SKRect(left, top, right, bottom), radius, radius),
                SKPathDirection.Clockwise);
            return builder.Detach();
        }

        var notchLeft = pad;
        var notchRight = Math.Max(notchLeft + 1, width - pad);
        var notchTop = 0f;
        var notchBottom = Math.Max(1f, height - pad);
        var topRadius = (float)((expanded ? 19.0 : 6.0) * scale);
        var bottomRadius = (float)((expanded ? 24.0 : 14.0) * scale);
        bottomRadius = Math.Min(bottomRadius, Math.Max(1f, (notchRight - notchLeft) / 4f));

        builder.MoveTo(notchLeft, notchTop);
        builder.QuadTo(notchLeft + topRadius, notchTop, notchLeft + topRadius, notchTop + topRadius);
        builder.LineTo(notchLeft + topRadius, notchBottom - bottomRadius);
        builder.QuadTo(notchLeft + topRadius, notchBottom, notchLeft + topRadius + bottomRadius, notchBottom);
        builder.LineTo(notchRight - topRadius - bottomRadius, notchBottom);
        builder.QuadTo(notchRight - topRadius, notchBottom, notchRight - topRadius, notchBottom - bottomRadius);
        builder.LineTo(notchRight - topRadius, notchTop + topRadius);
        builder.QuadTo(notchRight - topRadius, notchTop, notchRight, notchTop);
        builder.LineTo(notchLeft, notchTop);
        builder.Close();
        return builder.Detach();
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_width == width && _height == height && _captureDc != IntPtr.Zero && _outputDc != IntPtr.Zero)
        {
            return;
        }

        ReleaseBuffers();

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC failed while creating blur buffers.");
        }

        try
        {
            _captureDc = CreateCompatibleDC(screenDc);
            _outputDc = CreateCompatibleDC(screenDc);
            if (_captureDc == IntPtr.Zero || _outputDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed.");
            }

            _captureBitmap = CreateTopDownDib(screenDc, width, height, out _captureBits);
            _outputBitmap = CreateTopDownDib(screenDc, width, height, out _outputBits);
            if (_captureBitmap == IntPtr.Zero || _outputBitmap == IntPtr.Zero ||
                _captureBits == IntPtr.Zero || _outputBits == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection failed.");
            }

            _captureOld = SelectObject(_captureDc, _captureBitmap);
            _outputOld = SelectObject(_outputDc, _outputBitmap);
            _width = width;
            _height = height;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static IntPtr CreateTopDownDib(IntPtr dc, int width, int height, out IntPtr bits)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
                SizeImage = (uint)checked(width * height * 4)
            }
        };
        return CreateDIBSection(dc, ref info, DibRgbColors, out bits, IntPtr.Zero, 0);
    }

    private void ReleaseBuffers()
    {
        ReleaseSkiaResources();

        if (_captureDc != IntPtr.Zero && _captureOld != IntPtr.Zero)
        {
            SelectObject(_captureDc, _captureOld);
        }
        if (_outputDc != IntPtr.Zero && _outputOld != IntPtr.Zero)
        {
            SelectObject(_outputDc, _outputOld);
        }
        if (_captureBitmap != IntPtr.Zero) DeleteObject(_captureBitmap);
        if (_outputBitmap != IntPtr.Zero) DeleteObject(_outputBitmap);
        if (_captureDc != IntPtr.Zero) DeleteDC(_captureDc);
        if (_outputDc != IntPtr.Zero) DeleteDC(_outputDc);

        _captureDc = _captureBitmap = _captureOld = _captureBits = IntPtr.Zero;
        _outputDc = _outputBitmap = _outputOld = _outputBits = IntPtr.Zero;
        _width = _height = 0;
    }

    private void ReleaseSkiaResources()
    {
        _outputCanvas?.Dispose();
        _blurCanvas?.Dispose();
        _sourceSkia?.Dispose();
        _outputSkia?.Dispose();
        _blurSkia?.Dispose();
        _shapePath?.Dispose();
        _blurPaint?.Dispose();
        _blurFilter?.Dispose();
        _tintPaint?.Dispose();

        _outputCanvas = null;
        _blurCanvas = null;
        _sourceSkia = null;
        _outputSkia = null;
        _blurSkia = null;
        _shapePath = null;
        _blurPaint = null;
        _blurFilter = null;
        _tintPaint = null;
        _cachedStyle = int.MinValue;
        _cachedExpanded = false;
        _cachedScale = -1;
        _cachedOptions = default;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseBuffers();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y) { X = x; Y = y; }
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public NativeSize(int cx, int cy) { Cx = cx; Cy = cy; }
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint dst, ref NativeSize size, IntPtr hdcSrc, ref NativePoint src, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destDc, int x, int y, int width, int height, IntPtr sourceDc, int sourceX, int sourceY, uint rasterOp);
}
