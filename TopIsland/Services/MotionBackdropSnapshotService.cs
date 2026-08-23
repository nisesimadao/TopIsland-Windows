using SkiaSharp;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using TopIsland.Models;

namespace TopIsland.Services;

internal sealed record MotionBackdropRawCapture(
    byte[] Pixels,
    int PixelWidth,
    int PixelHeight,
    int Stride,
    float Sigma,
    MediaColor Tint);


/// <summary>
/// Captures the desktop underneath TopIsland and produces a spatially identical
/// Acrylic/Glass blur snapshot for the short shell morph. Keeping that bitmap in
/// the WPF visual tree lets shape/clip animation run at the display cadence while
/// the companion layered BlurHost is paused, avoiding DWM contention between two
/// independently presented layered windows.
/// </summary>
public sealed class MotionBackdropSnapshotService : IDisposable
{
    private const float BlurWorkingScale = 0.50f;
    private const uint SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

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

    private SKBitmap? _source;
    private SKBitmap? _output;
    private SKBitmap? _working;
    private SKCanvas? _outputCanvas;
    private SKCanvas? _workingCanvas;
    private SKImageFilter? _filter;
    private SKPaint? _blurPaint;
    private SKPaint? _tintPaint;
    private float _cachedSigma = -1;
    private bool _disposed;

    internal MotionBackdropRawCapture? CaptureRaw(IntPtr targetHwnd, SurfaceMaterial material, bool light)
    {
        if (_disposed || targetHwnd == IntPtr.Zero || material is not (SurfaceMaterial.Acrylic or SurfaceMaterial.Glass))
        {
            return null;
        }

        if (!GetWindowRect(targetHwnd, out var rect))
        {
            return null;
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        EnsureBuffers(width, height);
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            if (!BitBlt(_captureDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SrcCopy))
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        var dpi = Math.Max(96, (int)GetDpiForWindow(targetHwnd));
        var scale = dpi / 96.0;
        var (sigma, tint) = ResolveMaterial(material, light, scale);
        var stride = checked(width * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * height));
        var sourceStride = checked(_capacityWidth * 4);
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(IntPtr.Add(_captureBits, y * sourceStride), pixels, y * stride, stride);
        }

        return new MotionBackdropRawCapture(
            pixels, width, height, stride, sigma,
            MediaColor.FromArgb(tint.Alpha, tint.Red, tint.Green, tint.Blue));
    }

    public BitmapSource? Capture(IntPtr targetHwnd, SurfaceMaterial material, bool light)
    {
        if (_disposed || targetHwnd == IntPtr.Zero || material is not (SurfaceMaterial.Acrylic or SurfaceMaterial.Glass))
        {
            return null;
        }

        if (!GetWindowRect(targetHwnd, out var rect))
        {
            return null;
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        EnsureBuffers(width, height);
        EnsureSkia();
        if (_source is null || _output is null || _working is null || _outputCanvas is null || _workingCanvas is null)
        {
            return null;
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            // Layered windows are excluded from this SRCCOPY path, yielding the
            // application/desktop pixels actually underneath TopIsland.
            if (!BitBlt(_captureDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SrcCopy))
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        var dpi = Math.Max(96, (int)GetDpiForWindow(targetHwnd));
        var scale = dpi / 96.0;
        var (sigma, tint) = ResolveMaterial(material, light, scale);
        EnsurePaints(sigma, tint);
        if (_blurPaint is null || _tintPaint is null)
        {
            return null;
        }

        var workingWidth = Math.Max(1, (int)Math.Ceiling(width * BlurWorkingScale));
        var workingHeight = Math.Max(1, (int)Math.Ceiling(height * BlurWorkingScale));
        _workingCanvas.Save();
        _workingCanvas.ClipRect(new SKRect(0, 0, workingWidth, workingHeight));
        _workingCanvas.DrawColor(SKColors.Transparent, SKBlendMode.Src);
        _workingCanvas.DrawBitmap(
            _source,
            new SKRect(0, 0, width, height),
            new SKRect(0, 0, workingWidth, workingHeight),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            _blurPaint);
        // Tint on the blur working surface and hand that directly to WPF. Blur
        // has already removed the high-frequency information, so upsampling it
        // into another full-resolution CPU bitmap adds no visible detail while
        // quadrupling the texture upload. WPF/GPU performs the final bilinear
        // expansion when painting the fixed host-sized ImageBrush.
        _workingCanvas.DrawRect(0, 0, workingWidth, workingHeight, _tintPaint);
        _workingCanvas.Restore();
        _workingCanvas.Flush();

        var stride = _working.RowBytes;
        var bufferSize = checked(stride * workingHeight);
        var bitmap = BitmapSource.Create(
            workingWidth,
            workingHeight,
            dpi * BlurWorkingScale,
            dpi * BlurWorkingScale,
            PixelFormats.Bgra32,
            null,
            _working.GetPixels(),
            bufferSize,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static (float Sigma, SKColor Tint) ResolveMaterial(SurfaceMaterial material, bool light, double scale) =>
        material switch
        {
            SurfaceMaterial.Glass => (
                (float)(13 * scale),
                light ? new SKColor(250, 251, 253, 20) : new SKColor(8, 9, 12, 24)),
            _ => (
                (float)(19 * scale),
                light ? new SKColor(248, 249, 251, 28) : new SKColor(6, 7, 10, 34))
        };

    private void EnsurePaints(float sigma, SKColor tint)
    {
        if (_blurPaint is null || Math.Abs(_cachedSigma - sigma) > 0.01f)
        {
            _blurPaint?.Dispose();
            _filter?.Dispose();
            var workingSigma = Math.Max(0.1f, sigma * BlurWorkingScale);
            _filter = SKImageFilter.CreateBlur(workingSigma, workingSigma, SKShaderTileMode.Clamp);
            _blurPaint = new SKPaint { IsAntialias = true, ImageFilter = _filter };
            _cachedSigma = sigma;
        }
        _tintPaint ??= new SKPaint { IsAntialias = false };
        _tintPaint.Color = tint;
    }

    private void EnsureBuffers(int width, int height)
    {
        if (width <= _capacityWidth && height <= _capacityHeight && _captureDc != IntPtr.Zero && _outputDc != IntPtr.Zero)
        {
            return;
        }

        var capacityWidth = GrowCapacity(_capacityWidth, width, 1280);
        var capacityHeight = GrowCapacity(_capacityHeight, height, 480);
        ReleaseBuffers();

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC failed while preparing motion backdrop.");
        }
        try
        {
            _captureDc = CreateCompatibleDC(screenDc);
            _outputDc = CreateCompatibleDC(screenDc);
            _captureBitmap = CreateTopDownDib(screenDc, capacityWidth, capacityHeight, out _captureBits);
            _outputBitmap = CreateTopDownDib(screenDc, capacityWidth, capacityHeight, out _outputBits);
            if (_captureDc == IntPtr.Zero || _outputDc == IntPtr.Zero || _captureBitmap == IntPtr.Zero || _outputBitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not allocate motion backdrop buffers.");
            }
            _captureOld = SelectObject(_captureDc, _captureBitmap);
            _outputOld = SelectObject(_outputDc, _outputBitmap);
            _capacityWidth = capacityWidth;
            _capacityHeight = capacityHeight;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void EnsureSkia()
    {
        if (_source is not null)
        {
            return;
        }

        _source = new SKBitmap();
        _output = new SKBitmap();
        var sourceInfo = new SKImageInfo(_capacityWidth, _capacityHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var outputInfo = new SKImageInfo(_capacityWidth, _capacityHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (!_source.InstallPixels(sourceInfo, _captureBits, _capacityWidth * 4)
            || !_output.InstallPixels(outputInfo, _outputBits, _capacityWidth * 4))
        {
            throw new InvalidOperationException("Could not attach Skia motion backdrop buffers.");
        }

        var workingWidth = Math.Max(1, (int)Math.Ceiling(_capacityWidth * BlurWorkingScale));
        var workingHeight = Math.Max(1, (int)Math.Ceiling(_capacityHeight * BlurWorkingScale));
        _working = new SKBitmap(new SKImageInfo(workingWidth, workingHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        _outputCanvas = new SKCanvas(_output);
        _workingCanvas = new SKCanvas(_working);
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
        _outputCanvas?.Dispose();
        _workingCanvas?.Dispose();
        _source?.Dispose();
        _output?.Dispose();
        _working?.Dispose();
        _outputCanvas = null;
        _workingCanvas = null;
        _source = null;
        _output = null;
        _working = null;

        if (_captureDc != IntPtr.Zero && _captureOld != IntPtr.Zero) SelectObject(_captureDc, _captureOld);
        if (_outputDc != IntPtr.Zero && _outputOld != IntPtr.Zero) SelectObject(_outputDc, _outputOld);
        if (_captureBitmap != IntPtr.Zero) DeleteObject(_captureBitmap);
        if (_outputBitmap != IntPtr.Zero) DeleteObject(_outputBitmap);
        if (_captureDc != IntPtr.Zero) DeleteDC(_captureDc);
        if (_outputDc != IntPtr.Zero) DeleteDC(_outputDc);
        _captureDc = _captureBitmap = _captureOld = _captureBits = IntPtr.Zero;
        _outputDc = _outputBitmap = _outputOld = _outputBits = IntPtr.Zero;
        _capacityWidth = _capacityHeight = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseBuffers();
        _blurPaint?.Dispose();
        _filter?.Dispose();
        _tintPaint?.Dispose();
        _blurPaint = null;
        _filter = null;
        _tintPaint = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount;
        public uint Compression; public uint SizeImage; public int XPelsPerMeter; public int YPelsPerMeter;
        public uint ClrUsed; public uint ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destDc, int x, int y, int width, int height, IntPtr sourceDc, int sourceX, int sourceY, uint rasterOp);
}
