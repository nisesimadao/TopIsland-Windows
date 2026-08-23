using SkiaSharp;
using System.Runtime.InteropServices;

namespace TopIsland.BlurHost;

internal readonly record struct BlurFrameOptions(
    float BlurSigma,
    byte TintAlpha,
    byte TintR,
    byte TintG,
    byte TintB);

/// <summary>
/// Layered-window renderer with two cadences:
/// - desktop capture + Gaussian blur refreshes a cached full-host backdrop;
/// - shape/size/reveal composition samples that cache at the display cadence.
/// This keeps the expensive filter out of most animation frames without lowering
/// the blur resolution or the geometric animation frame rate.
/// </summary>
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

    private int _capacityWidth;
    private int _capacityHeight;
    private bool _disposed;
    private bool _backdropValid;

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
    private double _cachedShapeProgress = -1;
    private int _cachedShapeWidth = -1;
    private int _cachedShapeHeight = -1;
    private double _cachedScale = -1;
    private double _cachedRevealProgress = -1;
    private BlurFrameOptions _cachedOptions;

    public SkiaBlurRenderer(IntPtr hostHwnd)
    {
        _hostHwnd = hostHwnd;
    }

    public void InvalidateBackdrop() => _backdropValid = false;

    public bool Render(
        int hostScreenX,
        int hostScreenY,
        int hostWidth,
        int hostHeight,
        int surfaceOffsetX,
        int surfaceOffsetY,
        int surfaceWidth,
        int surfaceHeight,
        double scale,
        int style,
        double shapeProgress,
        double revealProgress,
        BlurFrameOptions options,
        bool refreshBackdrop)
    {
        if (_disposed || hostWidth <= 0 || hostHeight <= 0 || surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return false;
        }

        EnsureBuffers(hostWidth, hostHeight);
        EnsureSkiaResources(scale, options);
        if (refreshBackdrop || !_backdropValid)
        {
            if (!RefreshBackdrop(hostScreenX, hostScreenY, hostWidth, hostHeight))
            {
                return false;
            }
        }
        EnsureShapePath(surfaceWidth, surfaceHeight, scale, style, shapeProgress, revealProgress);
        if (_outputCanvas is null || _blurSkia is null || _shapePath is null || _tintPaint is null)
        {
            return false;
        }

        var workingLeft = (float)(surfaceOffsetX * BlurWorkingScale);
        var workingTop = (float)(surfaceOffsetY * BlurWorkingScale);
        var workingRight = (float)((surfaceOffsetX + surfaceWidth) * BlurWorkingScale);
        var workingBottom = (float)((surfaceOffsetY + surfaceHeight) * BlurWorkingScale);
        var blurSource = new SKRect(workingLeft, workingTop, workingRight, workingBottom);
        var localSurface = new SKRect(0, 0, surfaceWidth, surfaceHeight);
        var hostRect = new SKRect(0, 0, hostWidth, hostHeight);

        // Keep the layered HWND at the full host envelope. Resizing/moving a
        // layered window every shape frame was itself paced near 60 Hz. Clearing
        // the fixed transparent canvas and moving only pixels in Skia avoids that
        // native-window churn while keeping the transparent area click-through.
        _outputCanvas.Save();
        _outputCanvas.ClipRect(hostRect, SKClipOperation.Intersect, antialias: false);
        _outputCanvas.DrawColor(SKColors.Transparent, SKBlendMode.Src);
        _outputCanvas.Translate(surfaceOffsetX, surfaceOffsetY);
        _outputCanvas.ClipPath(_shapePath, SKClipOperation.Intersect, antialias: true);
        _outputCanvas.DrawBitmap(
            _blurSkia,
            blurSource,
            localSurface,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

        if (options.TintAlpha > 0)
        {
            _outputCanvas.DrawRect(localSurface, _tintPaint);
        }

        _outputCanvas.Restore();
        _outputCanvas.Flush();
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var dst = new NativePoint(hostScreenX, hostScreenY);
            var size = new NativeSize(hostWidth, hostHeight);
            var src = new NativePoint(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = style == 1
                    ? (byte)255
                    : (byte)Math.Round(255 * Math.Clamp(revealProgress, 0, 1)),
                AlphaFormat = AcSrcAlpha
            };
            var presented = UpdateLayeredWindow(
                _hostHwnd,
                screenDc,
                ref dst,
                ref size,
                _outputDc,
                ref src,
                0,
                ref blend,
                UlwAlpha);
            return presented;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
    private bool RefreshBackdrop(int screenX, int screenY, int width, int height)
    {
        if (_sourceSkia is null || _blurCanvas is null || _blurSkia is null || _blurPaint is null)
        {
            return false;
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!BitBlt(_captureDc, 0, 0, width, height, screenDc, screenX, screenY, SrcCopy))
            {
                return false;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        var blurWidth = Math.Max(1, (int)Math.Ceiling(width * BlurWorkingScale));
        var blurHeight = Math.Max(1, (int)Math.Ceiling(height * BlurWorkingScale));
        _blurCanvas.Save();
        _blurCanvas.ClipRect(new SKRect(0, 0, blurWidth, blurHeight));
        _blurCanvas.DrawColor(SKColors.Transparent, SKBlendMode.Src);
        _blurCanvas.DrawBitmap(
            _sourceSkia,
            new SKRect(0, 0, width, height),
            new SKRect(0, 0, blurWidth, blurHeight),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            _blurPaint);
        _blurCanvas.Restore();
        _blurCanvas.Flush();
        _backdropValid = true;
        return true;
    }

    private void EnsureSkiaResources(double scale, BlurFrameOptions options)
    {
        if (_sourceSkia is null || _outputSkia is null || _blurSkia is null || _outputCanvas is null || _blurCanvas is null)
        {
            _sourceSkia = new SKBitmap();
            _outputSkia = new SKBitmap();
            var sourceInfo = new SKImageInfo(_capacityWidth, _capacityHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var outputInfo = new SKImageInfo(_capacityWidth, _capacityHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            if (!_sourceSkia.InstallPixels(sourceInfo, _captureBits, _capacityWidth * 4) ||
                !_outputSkia.InstallPixels(outputInfo, _outputBits, _capacityWidth * 4))
            {
                throw new InvalidOperationException("Could not attach Skia bitmaps to blur buffers.");
            }

            var blurWidth = Math.Max(1, (int)Math.Ceiling(_capacityWidth * BlurWorkingScale));
            var blurHeight = Math.Max(1, (int)Math.Ceiling(_capacityHeight * BlurWorkingScale));
            _blurSkia = new SKBitmap(new SKImageInfo(blurWidth, blurHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            _outputCanvas = new SKCanvas(_outputSkia);
            _blurCanvas = new SKCanvas(_blurSkia);
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
            _backdropValid = false;
        }

        _tintPaint ??= new SKPaint { IsAntialias = true };
        _tintPaint.Color = new SKColor(options.TintR, options.TintG, options.TintB, options.TintAlpha);
        _cachedScale = scale;
    }

    private void EnsureShapePath(
        int width,
        int height,
        double scale,
        int style,
        double shapeProgress,
        double revealProgress)
    {
        var shapeChanged = Math.Abs(_cachedShapeProgress - shapeProgress) > 0.001;
        var revealChangesShape = style == 1 && Math.Abs(_cachedRevealProgress - revealProgress) > 0.001;
        if (_shapePath is not null
            && _cachedStyle == style
            && _cachedShapeWidth == width
            && _cachedShapeHeight == height
            && Math.Abs(_cachedScale - scale) <= 0.001
            && !shapeChanged
            && !revealChangesShape)
        {
            return;
        }

        _shapePath?.Dispose();
        _shapePath = CreateShapePath(width, height, scale, style, shapeProgress, revealProgress);
        _cachedStyle = style;
        _cachedShapeProgress = shapeProgress;
        _cachedShapeWidth = width;
        _cachedShapeHeight = height;
        _cachedScale = scale;
        _cachedRevealProgress = revealProgress;
    }

    private static SKPath CreateShapePath(int width, int height, double scale, int style, double shapeProgress, double revealProgress)
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
            shapeProgress = Math.Clamp(shapeProgress, 0, 1);
            var compactRadius = surfaceHeight / 2f;
            var expandedRadius = Math.Min((float)(28 * scale), compactRadius);
            var radius = compactRadius + (expandedRadius - compactRadius) * (float)shapeProgress;
            builder.AddRoundRect(
                new SKRoundRect(new SKRect(left, top, right, bottom), radius, radius),
                SKPathDirection.Clockwise);
            return builder.Detach();
        }

        revealProgress = Math.Clamp(revealProgress, 0, 1);
        if (revealProgress <= 0.001)
        {
            return builder.Detach();
        }

        var notchLeft = pad;
        var notchRight = Math.Max(notchLeft + 1, width - pad);
        var notchTop = 0f;
        var fullNotchBottom = Math.Max(1f, height - pad);
        var notchBottom = Math.Max(0.01f, (float)(fullNotchBottom * revealProgress));
        shapeProgress = Math.Clamp(shapeProgress, 0, 1);
        var shoulderScale = Math.Pow(revealProgress, 1.18);
        var topRadius = (float)((6.0 + (19.0 - 6.0) * shapeProgress) * scale * shoulderScale);
        var bottomRadius = (float)((14.0 + (24.0 - 14.0) * shapeProgress) * scale * shoulderScale);
        topRadius = Math.Min(topRadius, notchBottom / 2f);
        bottomRadius = Math.Min(bottomRadius, notchBottom / 2f);
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
        if (width <= _capacityWidth && height <= _capacityHeight && _captureDc != IntPtr.Zero && _outputDc != IntPtr.Zero)
        {
            return;
        }

        var capacityWidth = GrowCapacity(_capacityWidth, width, 1920);
        var capacityHeight = GrowCapacity(_capacityHeight, height, 640);
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

            _captureBitmap = CreateTopDownDib(screenDc, capacityWidth, capacityHeight, out _captureBits);
            _outputBitmap = CreateTopDownDib(screenDc, capacityWidth, capacityHeight, out _outputBits);
            if (_captureBitmap == IntPtr.Zero || _outputBitmap == IntPtr.Zero ||
                _captureBits == IntPtr.Zero || _outputBits == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection failed.");
            }

            _captureOld = SelectObject(_captureDc, _captureBitmap);
            _outputOld = SelectObject(_outputDc, _outputBitmap);
            _capacityWidth = capacityWidth;
            _capacityHeight = capacityHeight;
            _backdropValid = false;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static int GrowCapacity(int current, int required, int minimum)
    {
        var capacity = Math.Max(current, minimum);
        while (capacity < required)
        {
            capacity = checked((int)Math.Ceiling(capacity * 1.5));
        }
        return capacity;
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

        if (_captureDc != IntPtr.Zero && _captureOld != IntPtr.Zero) SelectObject(_captureDc, _captureOld);
        if (_outputDc != IntPtr.Zero && _outputOld != IntPtr.Zero) SelectObject(_outputDc, _outputOld);
        if (_captureBitmap != IntPtr.Zero) DeleteObject(_captureBitmap);
        if (_outputBitmap != IntPtr.Zero) DeleteObject(_outputBitmap);
        if (_captureDc != IntPtr.Zero) DeleteDC(_captureDc);
        if (_outputDc != IntPtr.Zero) DeleteDC(_outputDc);

        _captureDc = _captureBitmap = _captureOld = _captureBits = IntPtr.Zero;
        _outputDc = _outputBitmap = _outputOld = _outputBits = IntPtr.Zero;
        _capacityWidth = _capacityHeight = 0;
        _backdropValid = false;
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
        _cachedShapeProgress = -1;
        _cachedShapeWidth = -1;
        _cachedShapeHeight = -1;
        _cachedScale = -1;
        _cachedRevealProgress = -1;
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
