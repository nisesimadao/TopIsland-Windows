using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using System.Windows.Threading;
using SharpGen.Runtime;
using TopIsland.Models;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TopIsland.Services;

internal readonly record struct MotionCompositionFrame(
    double Width,
    double Height,
    double Top,
    double ShapeProgress,
    double RevealProgress,
    double SurfaceOpacity,
    double ShadowOpacity);

internal sealed record MotionCompositionBitmap(
    byte[] Pixels,
    int PixelWidth,
    int PixelHeight,
    int Stride,
    double X,
    double Y,
    double Width,
    double Height);

internal sealed record MotionCompositionBackdrop(
    MotionCompositionBitmap Source,
    float Sigma,
    MediaColor Tint);

internal sealed record MotionCompositionContent(
    MotionCompositionBitmap Compact,
    MotionCompositionBitmap Expanded,
    double TopRightX,
    double BottomStartY,
    double BottomMiddleX,
    double BottomRightX);
internal sealed record MotionCompositionRequest(
    MonitorDescriptor Monitor,
    IslandStyle Style,
    double HostWidthDip,
    double HostHeightDip,
    double HostTopDip,
    MotionCompositionFrame From,
    MotionCompositionFrame To,
    int DurationMs,
    MotionCompositionContent? Content,
    MotionCompositionBackdrop? Backdrop,
    MediaColor FillColor,
    MediaColor BorderColor,
    Action Presented,
    Action Completed,
    Action Failed);

/// <summary>
/// Short-lived animation renderer that bypasses WPF's per-pixel layered-window
/// bitmap round-trip. The settled UI remains WPF; only the 150-230 ms shell morph
/// is handed to a Direct2D -> DirectComposition surface on a dedicated render
/// thread. This keeps the hot animation path GPU-resident and refresh-rate driven.
/// </summary>
internal sealed class MotionCompositionHost : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExNoRedirectionBitmap = 0x00200000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint PmRemove = 0x0001;

    private static readonly string WindowClass = $"TopIsland.MotionComposition.{Environment.ProcessId}";
    private static readonly WndProcDelegate WndProcThunk = WndProc;
    private static int _classRegistered;

    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentQueue<MotionCompositionRequest> _commands = new();
    private readonly AutoResetEvent _commandEvent = new(false);
    private readonly object _stateGate = new();
    private readonly Thread _thread;

    private bool _disposed;
    private bool _stopRequested;
    private volatile bool _faulted;
    private MotionCompositionFrame _currentFrame;
    private bool _hasCurrentFrame;

    private IntPtr _hwnd;
    private ID3D11Device? _d3d;
    private IDXGIDevice? _dxgi;
    private ID2D1Device? _d2d;
    private ID2D1Factory1? _d2dFactory;
    private IDCompositionDevice? _dcompBase;
    private IDCompositionDevice2? _dcomp;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private IDCompositionSurface? _surface;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private MotionCompositionContent? _loadedContent;
    private ID2D1Bitmap1? _compactBitmap;
    private ID2D1Bitmap1? _expandedBitmap;
    private MotionCompositionBackdrop? _loadedBackdrop;
    private ID2D1Bitmap1? _backdropBitmap;
    private ID2D1BitmapBrush1? _backdropBrush;
    private ID2D1Layer? _contentLayer;

    public MotionCompositionHost(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(RenderThreadMain)
        {
            IsBackground = true,
            Name = "TopIsland DirectComposition Motion"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool IsUsable => !_disposed && !_faulted;

    public void Prewarm(MonitorDescriptor monitor, double hostWidthDip, double hostHeightDip, double hostTopDip)
    {
        if (!IsUsable) return;
        _commands.Enqueue(new MotionCompositionRequest(
            monitor, IslandStyle.Notch,
            hostWidthDip, hostHeightDip, hostTopDip,
            default, default, -2,
            null, null,
            System.Windows.Media.Colors.Transparent,
            System.Windows.Media.Colors.Transparent,
            static () => { }, static () => { }, static () => { }));
        _commandEvent.Set();
    }

    public bool TryGetCurrentFrame(out MotionCompositionFrame frame)
    {
        lock (_stateGate)
        {
            frame = _currentFrame;
            return _hasCurrentFrame;
        }
    }

    public void Start(MotionCompositionRequest request)
    {
        if (_disposed || _faulted)
        {
            _dispatcher.BeginInvoke(request.Failed, DispatcherPriority.Send);
            return;
        }

        // Only the newest target matters. An interrupted motion starts from the
        // frame most recently committed by this compositor.
        while (_commands.TryDequeue(out _))
        {
        }
        _commands.Enqueue(request);
        _commandEvent.Set();
    }

    public void Hide()
    {
        if (_disposed)
        {
            return;
        }

        _commands.Enqueue(new MotionCompositionRequest(
            new MonitorDescriptor(IntPtr.Zero, string.Empty, 0, 0, 1, 1, 100, false),
            IslandStyle.Notch,
            1, 1, 0,
            default,
            default,
            -1,
            null,
            null,
            System.Windows.Media.Colors.Transparent,
            System.Windows.Media.Colors.Transparent,
            static () => { },
            static () => { },
            static () => { }));
        _commandEvent.Set();
    }

    private void RenderThreadMain()
    {
        MotionCompositionRequest? active = null;
        long started = 0;
        long nextFrame = 0;
        var targetFrameTicks = Stopwatch.Frequency / 120.0;

        var highResolutionTimerActive = TimeBeginPeriod(1) == 0;
        try
        {
            while (!_stopRequested)
            {
                PumpMessages();

                if (_commands.TryDequeue(out var command))
                {
                    if (command.DurationMs == -2)
                    {
                        EnsureDeviceAndWindow(command);
                        HideNativeWindow();
                        continue;
                    }
                    if (command.DurationMs < 0)
                    {
                        HideNativeWindow();
                        active = null;
                        continue;
                    }

                    active = command;
                    EnsureDeviceAndWindow(command);
                    targetFrameTicks = Stopwatch.Frequency / ResolveTargetRefreshHz(command.Monitor.DeviceName);
                    RenderFrame(command, command.From);
                    lock (_stateGate)
                    {
                        _currentFrame = command.From;
                        _hasCurrentFrame = true;
                    }
                    ShowNativeWindow();
                    started = Stopwatch.GetTimestamp();
                    nextFrame = started;
                    _dispatcher.BeginInvoke(command.Presented, DispatcherPriority.Send);
                }

                if (active is null)
                {
                    _commandEvent.WaitOne(25);
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                if (now < nextFrame)
                {
                    var remainingMs = (nextFrame - now) * 1000.0 / Stopwatch.Frequency;
                    if (remainingMs > 1.5)
                    {
                        _commandEvent.WaitOne(Math.Max(1, (int)Math.Floor(remainingMs - 0.5)));
                    }
                    else
                    {
                        Thread.SpinWait(80);
                    }
                    continue;
                }

                // If a newer target arrived, consume it immediately rather than
                // spending time on a frame that will never be presented.
                if (!_commands.IsEmpty)
                {
                    continue;
                }

                var elapsedMs = Stopwatch.GetElapsedTime(started, now).TotalMilliseconds;
                var t = Math.Clamp(elapsedMs / Math.Max(1, active.DurationMs), 0, 1);
                var amount = MotionProfile.Ease(t);
                var frame = Lerp(active.From, active.To, amount);
                RenderFrame(active, frame);

                lock (_stateGate)
                {
                    _currentFrame = frame;
                    _hasCurrentFrame = true;
                }

                if (t >= 1)
                {
                    var completed = active.Completed;
                    active = null;
                    _dispatcher.BeginInvoke(completed, DispatcherPriority.Send);
                    continue;
                }

                // Absolute scheduling prevents one slow frame from permanently
                // shifting the cadence. Drop missed slots instead of queueing them.
                var frameIndex = Math.Floor((now - started) / targetFrameTicks) + 1;
                nextFrame = started + (long)Math.Round(frameIndex * targetFrameTicks);
            }
        }
        catch (Exception ex)
        {
            _faulted = true;
            if (active is not null)
            {
                _dispatcher.BeginInvoke(active.Failed, DispatcherPriority.Send);
            }
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "TopIsland-composition-error.txt"), ex.ToString());
            }
            catch
            {
            }
        }
        finally
        {
            if (highResolutionTimerActive)
            {
                _ = TimeEndPeriod(1);
            }
            ReleaseResources();
        }
    }

    private void EnsureDeviceAndWindow(MotionCompositionRequest request)
    {
        RegisterWindowClass();

        var width = Math.Max(1, (int)Math.Ceiling(request.HostWidthDip * request.Monitor.Scale));
        var height = Math.Max(1, (int)Math.Ceiling(request.HostHeightDip * request.Monitor.Scale));
        var x = request.Monitor.Left + (request.Monitor.PixelWidth - width) / 2;
        var y = request.Monitor.Top + (int)Math.Round(request.HostTopDip * request.Monitor.Scale);

        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = CreateWindowEx(
                WsExTopMost | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExNoRedirectionBitmap,
                WindowClass,
                "TopIsland Motion Composition",
                WsPopup,
                x, y, width, height,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            }
        }

        _ = SetWindowPos(_hwnd, new IntPtr(-1), x, y, width, height, SwpNoActivate);

        _d3d ??= D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_0]);
        _dxgi ??= _d3d.QueryInterface<IDXGIDevice>();
        _d2d ??= D2D1.D2D1CreateDevice(_dxgi, null);
        _d2dFactory ??= _d2d.Factory.QueryInterface<ID2D1Factory1>();
        if (_dcompBase is null)
        {
            _dcompBase = DComp.DCompositionCreateDevice2<IDCompositionDevice>(_d2d);
            _dcomp = _dcompBase.QueryInterface<IDCompositionDevice2>();
            _dcompBase.CreateTargetForHwnd(_hwnd, true, out _target).CheckError();
            _visual = _dcompBase.CreateVisual();
            _target.SetRoot(_visual).CheckError();
        }

        if (_surface is null || _surfaceWidth != width || _surfaceHeight != height)
        {
            _surface?.Dispose();
            _surface = _dcomp!.CreateSurface((uint)width, (uint)height, Format.B8G8R8A8_UNorm, Vortice.DXGI.AlphaMode.Premultiplied);
            _surfaceWidth = width;
            _surfaceHeight = height;
            _visual!.SetContent(_surface).CheckError();
            _dcompBase!.Commit().CheckError();
        }
    }

    private void RenderFrame(MotionCompositionRequest request, MotionCompositionFrame frame)
    {
        if (_surface is null || _dcompBase is null || _d2dFactory is null)
        {
            return;
        }

        using var dc = _surface.BeginDraw<ID2D1DeviceContext>(null, out var updateOffset);
        dc.Transform = Matrix3x2.CreateTranslation(updateOffset.X, updateOffset.Y);
        dc.UnitMode = UnitMode.Pixels;
        dc.Clear(new Color4(0, 0, 0, 0));
        EnsureContentBitmaps(dc, request.Content);
        EnsureBackdropBitmap(dc, request.Backdrop, request.HostWidthDip, request.HostHeightDip, request.Monitor.Scale);

        var scale = (float)request.Monitor.Scale;
        var hostWidthDip = request.HostWidthDip;
        var offsetX = (float)((hostWidthDip - frame.Width) * 0.5 * request.Monitor.Scale);
        var offsetY = (float)((frame.Top - request.HostTopDip) * request.Monitor.Scale);
        var widthPx = (float)(frame.Width * request.Monitor.Scale);
        var heightPx = (float)(frame.Height * request.Monitor.Scale);

        using var geometry = CreateGeometry(
            _d2dFactory,
            request.Style,
            widthPx,
            heightPx,
            (float)frame.ShapeProgress,
            (float)frame.RevealProgress,
            offsetX,
            offsetY,
            scale);

        if (geometry is not null)
        {
            if (frame.ShadowOpacity > 0.001)
            {
                using var shadowOuter = dc.CreateSolidColorBrush(new Color4(0, 0, 0, (float)(frame.ShadowOpacity * 0.10)));
                using var shadowInner = dc.CreateSolidColorBrush(new Color4(0, 0, 0, (float)(frame.ShadowOpacity * 0.18)));
                dc.DrawGeometry(geometry, shadowOuter, 9f * scale);
                dc.DrawGeometry(geometry, shadowInner, 4f * scale);
            }

            if (_backdropBrush is not null)
            {
                dc.FillGeometry(geometry, _backdropBrush);
            }

            var fill = request.FillColor;
            using var fillBrush = dc.CreateSolidColorBrush(new Color4(
                fill.R / 255f,
                fill.G / 255f,
                fill.B / 255f,
                (fill.A / 255f) * (float)frame.SurfaceOpacity));
            dc.FillGeometry(geometry, fillBrush);

            if (request.BorderColor.A > 0)
            {
                var border = request.BorderColor;
                using var borderBrush = dc.CreateSolidColorBrush(new Color4(
                    border.R / 255f,
                    border.G / 255f,
                    border.B / 255f,
                    (border.A / 255f) * (float)frame.SurfaceOpacity));
                dc.DrawGeometry(geometry, borderBrush, Math.Max(1f, scale));
            }
        }

        if (geometry is not null && request.Content is not null)
        {
            _contentLayer ??= dc.CreateLayer(null);
            var layerParameters = new LayerParameters1
            {
                ContentBounds = new Vortice.RawRectF(0, 0, _surfaceWidth, _surfaceHeight),
                GeometricMask = geometry,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1f,
                OpacityBrush = null,
                LayerOptions = LayerOptions1.None
            };
            dc.PushLayer(ref layerParameters, _contentLayer);
            DrawContent(dc, request, frame, scale, offsetY);
            dc.PopLayer();
        }
        _surface.EndDraw().CheckError();
        _dcompBase.Commit().CheckError();
    }

    private void EnsureContentBitmaps(ID2D1DeviceContext dc, MotionCompositionContent? content)
    {
        if (ReferenceEquals(_loadedContent, content))
        {
            return;
        }

        _compactBitmap?.Dispose();
        _expandedBitmap?.Dispose();
        _compactBitmap = null;
        _expandedBitmap = null;
        _loadedContent = content;
        if (content is null)
        {
            return;
        }

        _compactBitmap = CreateBitmap(dc, content.Compact);
        _expandedBitmap = CreateBitmap(dc, content.Expanded);
    }

    private static ID2D1Bitmap1 CreateBitmap(
        ID2D1DeviceContext dc,
        MotionCompositionBitmap bitmap,
        Vortice.DCommon.AlphaMode alphaMode = Vortice.DCommon.AlphaMode.Premultiplied)
    {
        var handle = GCHandle.Alloc(bitmap.Pixels, GCHandleType.Pinned);
        try
        {
            var properties = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, alphaMode),
                96,
                96);
            return dc.CreateBitmap(
                new SizeI(bitmap.PixelWidth, bitmap.PixelHeight),
                handle.AddrOfPinnedObject(),
                (uint)bitmap.Stride,
                properties);
        }
        finally
        {
            handle.Free();
        }
    }

    private void EnsureBackdropBitmap(
        ID2D1DeviceContext dc,
        MotionCompositionBackdrop? backdrop,
        double hostWidthDip,
        double hostHeightDip,
        double monitorScale)
    {
        if (ReferenceEquals(_loadedBackdrop, backdrop))
        {
            return;
        }

        _backdropBrush?.Dispose();
        _backdropBitmap?.Dispose();
        _backdropBrush = null;
        _backdropBitmap = null;
        _loadedBackdrop = backdrop;
        if (backdrop is null)
        {
            return;
        }

        // MotionBackdropSnapshotService already produced a premultiplied,
        // material-correct Acrylic/Glass frame. Keep the DirectComposition hot
        // path to a single GPU texture upload + brush; do not re-blur or reinterpret
        // alpha here. The extra GPU-effect pass caused black frames on some drivers.
        _backdropBitmap = CreateBitmap(dc, backdrop.Source);
        var hostWidthPx = Math.Max(1f, (float)(hostWidthDip * monitorScale));
        var hostHeightPx = Math.Max(1f, (float)(hostHeightDip * monitorScale));
        var scaleX = hostWidthPx / Math.Max(1, backdrop.Source.PixelWidth);
        var scaleY = hostHeightPx / Math.Max(1, backdrop.Source.PixelHeight);
        var bitmapProps = new BitmapBrushProperties1(
            ExtendMode.Clamp,
            ExtendMode.Clamp,
            Vortice.Direct2D1.InterpolationMode.Linear);
        var brushProps = new BrushProperties(1f, Matrix3x2.CreateScale(scaleX, scaleY));
        _backdropBrush = dc.CreateBitmapBrush(_backdropBitmap, bitmapProps, brushProps);
    }
    private void DrawContent(
        ID2D1DeviceContext dc,
        MotionCompositionRequest request,
        MotionCompositionFrame frame,
        float scale,
        float surfaceOffsetY)
    {
        var content = request.Content;
        if (content is null || _compactBitmap is null || _expandedBitmap is null)
        {
            return;
        }

        var expansion = Math.Clamp(frame.ShapeProgress, 0, 1);
        var compactExit = MotionProfile.CompactExit(expansion);
        var collapsing = request.To.ShapeProgress + 0.001 < request.From.ShapeProgress;
        var collapseProgress = collapsing
            ? Math.Clamp(
                (request.From.ShapeProgress - frame.ShapeProgress) /
                Math.Max(0.001, request.From.ShapeProgress - request.To.ShapeProgress),
                0.0,
                1.0)
            : 0.0;

        var compactOpacity = collapsing
            ? MotionProfile.EaseRange(collapseProgress, 0.48, 0.88)
            : 1.0;
        var compact = content.Compact;
        var compactY = compact.Y - MotionProfile.CompactTravelY * compactExit;
        var compactDest = new Vortice.RawRectF(
            (float)(compact.X * scale),
            (float)(compactY * scale + surfaceOffsetY),
            (float)((compact.X + compact.Width) * scale),
            (float)((compactY + compact.Height) * scale + surfaceOffsetY));
        if (compactOpacity > 0.001)
        {
            dc.DrawBitmap(_compactBitmap, (Vortice.RawRectF?)compactDest, (float)compactOpacity,
                Vortice.Direct2D1.InterpolationMode.Linear, null, null);
        }

        var expanded = content.Expanded;
        var width = expanded.Width;
        var height = expanded.Height;
        var topRightX = Math.Clamp(content.TopRightX, 1, Math.Max(1, width - 1));
        var bottomStartY = Math.Clamp(content.BottomStartY, 1, Math.Max(1, height - 1));
        var bottomMiddleX = Math.Clamp(content.BottomMiddleX, 1, Math.Max(1, width - 2));
        var bottomRightX = Math.Clamp(content.BottomRightX, bottomMiddleX + 1, Math.Max(bottomMiddleX + 1, width - 1));
        var bottomHeight = height - bottomStartY;

        if (collapsing)
        {
            // During collapse the shell contracts around its own centre. Keep text/cards
            // at their original size, but move each region centre with that contraction.
            // Previously the expanded bitmap stayed in endpoint coordinates and the shell
            // simply clipped across it, which made the contents look completely stationary.
            var expandedFrame = request.From.ShapeProgress >= request.To.ShapeProgress
                ? request.From
                : request.To;
            // Stop compressing before regions collide. The remaining transition is
            // handed to the compact snapshot with a short crossfade.
            var widthRatio = Math.Max(0.70,
                Math.Clamp(frame.Width / Math.Max(1.0, expandedFrame.Width), 0.0, 1.0));
            var heightRatio = Math.Max(0.68,
                Math.Clamp(frame.Height / Math.Max(1.0, expandedFrame.Height), 0.0, 1.0));
            var expandedOpacity = 1.0 - MotionProfile.EaseRange(collapseProgress, 0.42, 0.78);
            var hostCenterX = request.HostWidthDip * 0.5;
            var expandedTopInHost = expandedFrame.Top - request.HostTopDip;
            var currentTopInHost = frame.Top - request.HostTopDip;

            DrawCollapsingExpandedRegion(dc, expanded,
                0, 0, topRightX, bottomStartY,
                hostCenterX, expandedTopInHost, currentTopInHost,
                widthRatio, heightRatio, expandedOpacity, scale, surfaceOffsetY);

            DrawCollapsingExpandedRegion(dc, expanded,
                topRightX, 0, width - topRightX, bottomStartY,
                hostCenterX, expandedTopInHost, currentTopInHost,
                widthRatio, heightRatio, expandedOpacity, scale, surfaceOffsetY);

            DrawCollapsingExpandedRegion(dc, expanded,
                0, bottomStartY, bottomMiddleX, bottomHeight,
                hostCenterX, expandedTopInHost, currentTopInHost,
                widthRatio, heightRatio, expandedOpacity, scale, surfaceOffsetY);

            DrawCollapsingExpandedRegion(dc, expanded,
                bottomMiddleX, bottomStartY, bottomRightX - bottomMiddleX, bottomHeight,
                hostCenterX, expandedTopInHost, currentTopInHost,
                widthRatio, heightRatio, expandedOpacity, scale, surfaceOffsetY);

            DrawCollapsingExpandedRegion(dc, expanded,
                bottomRightX, bottomStartY, width - bottomRightX, bottomHeight,
                hostCenterX, expandedTopInHost, currentTopInHost,
                widthRatio, heightRatio, expandedOpacity, scale, surfaceOffsetY);
            return;
        }

        var topLeftEnter = MotionProfile.ExpandedTopLeftEnter(expansion);
        var topRightEnter = MotionProfile.ExpandedTopRightEnter(expansion);
        var bottomLeftEnter = MotionProfile.ExpandedBottomLeftEnter(expansion);
        var bottomMiddleEnter = MotionProfile.ExpandedBottomMiddleEnter(expansion);
        var bottomRightEnter = MotionProfile.ExpandedBottomRightEnter(expansion);

        DrawExpandedRegion(dc, expanded,
            0, 0, topRightX, bottomStartY,
            +64.0 * (1.0 - topLeftEnter),
            +40.0 * (1.0 - topLeftEnter),
            scale, surfaceOffsetY);

        DrawExpandedRegion(dc, expanded,
            topRightX, 0, width - topRightX, bottomStartY,
            -64.0 * (1.0 - topRightEnter),
            +42.0 * (1.0 - topRightEnter),
            scale, surfaceOffsetY);

        DrawExpandedRegion(dc, expanded,
            0, bottomStartY, bottomMiddleX, bottomHeight,
            +56.0 * (1.0 - bottomLeftEnter),
            +62.0 * (1.0 - bottomLeftEnter),
            scale, surfaceOffsetY);

        DrawExpandedRegion(dc, expanded,
            bottomMiddleX, bottomStartY, bottomRightX - bottomMiddleX, bottomHeight,
            0,
            +72.0 * (1.0 - bottomMiddleEnter),
            scale, surfaceOffsetY);

        DrawExpandedRegion(dc, expanded,
            bottomRightX, bottomStartY, width - bottomRightX, bottomHeight,
            -56.0 * (1.0 - bottomRightEnter),
            +62.0 * (1.0 - bottomRightEnter),
            scale, surfaceOffsetY);
    }

    private void DrawCollapsingExpandedRegion(
        ID2D1DeviceContext dc,
        MotionCompositionBitmap bitmap,
        double x,
        double y,
        double width,
        double height,
        double hostCenterX,
        double expandedTopInHost,
        double currentTopInHost,
        double widthRatio,
        double heightRatio,
        double opacity,
        float scale,
        float surfaceOffsetY)
    {
        var regionCenterX = bitmap.X + x + width * 0.5;
        var regionCenterY = bitmap.Y + y + height * 0.5;
        var desiredCenterX = hostCenterX + (regionCenterX - hostCenterX) * widthRatio;
        var relativeCenterY = regionCenterY - expandedTopInHost;
        var desiredCenterY = currentTopInHost + relativeCenterY * heightRatio;

        DrawExpandedRegion(
            dc,
            bitmap,
            x,
            y,
            width,
            height,
            desiredCenterX - regionCenterX,
            desiredCenterY - regionCenterY,
            scale,
            surfaceOffsetY,
            opacity);
    }

    private void DrawExpandedRegion(
        ID2D1DeviceContext dc,
        MotionCompositionBitmap bitmap,
        double x,
        double y,
        double width,
        double height,
        double translateX,
        double translateY,
        float scale,
        float surfaceOffsetY,
        double opacity = 1.0)
    {
        if (_expandedBitmap is null || width <= 0.1 || height <= 0.1 || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return;
        }

        var sourceLeft = (float)(x / bitmap.Width * bitmap.PixelWidth);
        var sourceTop = (float)(y / bitmap.Height * bitmap.PixelHeight);
        var sourceRight = (float)((x + width) / bitmap.Width * bitmap.PixelWidth);
        var sourceBottom = (float)((y + height) / bitmap.Height * bitmap.PixelHeight);
        var source = new Vortice.RawRectF(sourceLeft, sourceTop, sourceRight, sourceBottom);

        var destLeft = bitmap.X + x + translateX;
        var destTop = bitmap.Y + y + translateY;
        var dest = new Vortice.RawRectF(
            (float)(destLeft * scale),
            (float)(destTop * scale + surfaceOffsetY),
            (float)((destLeft + width) * scale),
            (float)((destTop + height) * scale + surfaceOffsetY));
        if (opacity <= 0.001)
        {
            return;
        }
        dc.DrawBitmap(_expandedBitmap, (Vortice.RawRectF?)dest, (float)Math.Clamp(opacity, 0.0, 1.0),
            Vortice.Direct2D1.InterpolationMode.Linear, (Vortice.RawRectF?)source, null);
    }
    private static ID2D1Geometry? CreateGeometry(
        ID2D1Factory1 factory,
        IslandStyle style,
        float width,
        float height,
        float shapeProgress,
        float revealProgress,
        float offsetX,
        float offsetY,
        float scale)
    {
        var pad = 16f * scale;
        if (style == IslandStyle.DynamicIsland)
        {
            var left = offsetX + pad;
            var top = offsetY + pad;
            var right = offsetX + Math.Max(pad + 1, width - pad);
            var bottom = offsetY + Math.Max(pad + 1, height - pad);
            var surfaceHeight = bottom - top;
            var compactRadius = surfaceHeight * 0.5f;
            var expandedRadius = Math.Min(28f * scale, compactRadius);
            var radius = compactRadius + (expandedRadius - compactRadius) * shapeProgress;
            return factory.CreateRoundedRectangleGeometry(new RoundedRectangle(
                new Vortice.RawRectF(left, top, right, bottom),
                radius,
                radius));
        }

        revealProgress = Math.Clamp(revealProgress, 0, 1);
        if (revealProgress <= 0.0001f)
        {
            return null;
        }

        var leftN = offsetX + pad;
        var rightN = offsetX + Math.Max(pad + 1, width - pad);
        var topN = offsetY;
        var fullBottom = Math.Max(1f, height - pad);
        var bottomN = offsetY + Math.Max(0.1f, fullBottom * revealProgress);
        var shoulderScale = (float)Math.Pow(revealProgress, 1.18);
        var topRadius = (6f + 13f * shapeProgress) * scale * shoulderScale;
        var bottomRadius = (14f + 10f * shapeProgress) * scale * shoulderScale;
        var visibleHeight = Math.Max(0.1f, bottomN - topN);
        topRadius = Math.Min(topRadius, visibleHeight * 0.5f);
        bottomRadius = Math.Min(bottomRadius, visibleHeight * 0.5f);
        bottomRadius = Math.Min(bottomRadius, Math.Max(0.1f, (rightN - leftN) * 0.25f));

        var path = factory.CreatePathGeometry();
        using var sink = path.Open();
        sink.SetFillMode(Vortice.Direct2D1.FillMode.Winding);
        sink.BeginFigure(new Vector2(leftN, topN), FigureBegin.Filled);
        sink.AddQuadraticBezier(new Vortice.Direct2D1.QuadraticBezierSegment { Point1 = new Vector2(leftN + topRadius, topN), Point2 = new Vector2(leftN + topRadius, topN + topRadius) });
        sink.AddLine(new Vector2(leftN + topRadius, bottomN - bottomRadius));
        sink.AddQuadraticBezier(new Vortice.Direct2D1.QuadraticBezierSegment { Point1 = new Vector2(leftN + topRadius, bottomN), Point2 = new Vector2(leftN + topRadius + bottomRadius, bottomN) });
        sink.AddLine(new Vector2(rightN - topRadius - bottomRadius, bottomN));
        sink.AddQuadraticBezier(new Vortice.Direct2D1.QuadraticBezierSegment { Point1 = new Vector2(rightN - topRadius, bottomN), Point2 = new Vector2(rightN - topRadius, bottomN - bottomRadius) });
        sink.AddLine(new Vector2(rightN - topRadius, topN + topRadius));
        sink.AddQuadraticBezier(new Vortice.Direct2D1.QuadraticBezierSegment { Point1 = new Vector2(rightN - topRadius, topN), Point2 = new Vector2(rightN, topN) });
        sink.AddLine(new Vector2(leftN, topN));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return path;
    }

    private void ShowNativeWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            _ = ShowWindow(_hwnd, SwShowNoActivate);
            _ = SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | SwpNoActivate | SwpShowWindow);
        }
    }

    private void HideNativeWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            _ = ShowWindow(_hwnd, SwHide);
        }
    }

    private static void RegisterWindowClass()
    {
        if (Interlocked.CompareExchange(ref _classRegistered, 1, 0) != 0)
        {
            return;
        }

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
            Interlocked.Exchange(ref _classRegistered, 0);
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PmRemove))
        {
            _ = TranslateMessage(ref msg);
            _ = DispatchMessage(ref msg);
        }
    }

    private static double ResolveTargetRefreshHz(string deviceName)
    {
        try
        {
            var mode = new NativeDevMode
            {
                DeviceName = string.Empty,
                FormName = string.Empty,
                Size = (short)Marshal.SizeOf<NativeDevMode>()
            };
            if (!string.IsNullOrWhiteSpace(deviceName)
                && EnumDisplaySettings(deviceName, -1, ref mode)
                && mode.DisplayFrequency is >= 50 and <= 360)
            {
                // Motion is authored for up to 120 Hz. Higher-refresh panels still
                // get a stable 120 Hz animation without burning extra render work.
                return Math.Clamp(mode.DisplayFrequency, 60, 120);
            }
        }
        catch
        {
        }
        return 120;
    }
    private static MotionCompositionFrame Lerp(MotionCompositionFrame from, MotionCompositionFrame to, double t) => new(
        Lerp(from.Width, to.Width, t),
        Lerp(from.Height, to.Height, t),
        Lerp(from.Top, to.Top, t),
        Lerp(from.ShapeProgress, to.ShapeProgress, t),
        Lerp(from.RevealProgress, to.RevealProgress, t),
        Lerp(from.SurfaceOpacity, to.SurfaceOpacity, t),
        Lerp(from.ShadowOpacity, to.ShadowOpacity, t));

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private void ReleaseResources()
    {
        HideNativeWindow();
        _contentLayer?.Dispose();
        _backdropBrush?.Dispose();
        _backdropBitmap?.Dispose();
        _compactBitmap?.Dispose();
        _expandedBitmap?.Dispose();
        _contentLayer = null;
        _backdropBrush = null;
        _backdropBitmap = null;
        _loadedBackdrop = null;
        _loadedContent = null;
        _surface?.Dispose();
        _visual?.Dispose();
        _target?.Dispose();
        _dcomp?.Dispose();
        _dcompBase?.Dispose();
        _d2dFactory?.Dispose();
        _d2d?.Dispose();
        _dxgi?.Dispose();
        _d3d?.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            _ = DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stopRequested = true;
        _commandEvent.Set();
        if (!_thread.Join(1200))
        {
            // Background thread cannot block process shutdown.
        }
        _commandEvent.Dispose();
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProc(hwnd, msg, wParam, lParam);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int ICMMethod;
        public int ICMIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref NativeDevMode mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}
