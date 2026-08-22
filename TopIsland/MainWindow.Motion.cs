using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TopIsland.Controls;
using TopIsland.Models;
using TopIsland.Services;

namespace TopIsland;

public partial class MainWindow
{
    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        _pointerWasInside = true;
        HandlePointerEntered();
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        // A native resize can synthesize MouseLeave even while the real pointer
        // is still over the island. Never collapse directly from this event; the
        // 16 ms pointer tracker validates the actual screen-space cursor first.
        _peekTimer.Stop();
    }

    private void PointerTimer_Tick(object? sender, EventArgs e)
    {
        var inside = IsPointerInteractionActive();
        if (inside == _pointerWasInside)
        {
            return;
        }

        _pointerWasInside = inside;
        if (inside)
        {
            HandlePointerEntered();
        }
        else
        {
            HandlePointerLeft();
        }
    }

    private void HandlePointerEntered()
    {
        _collapseTimer.Stop();
        if (_state == SurfaceState.Expanded)
        {
            return;
        }

        if (_state != SurfaceState.Hover && _state != SurfaceState.Peek)
        {
            _state = SurfaceState.Hover;
            ApplyState();
        }

        if (_settings.EnableHoverPeek && _state == SurfaceState.Hover)
        {
            _peekTimer.Stop();
            _peekTimer.Start();
        }
    }

    private void HandlePointerLeft()
    {
        _peekTimer.Stop();
        _collapseTimer.Stop();
        // Transparent/no-activate HWNDs occasionally miss or duplicate WPF mouse
        // events while resizing. The pointer tracker above supplies the missing
        // edge; this grace period absorbs transient leave events during morphs.
        _collapseTimer.Interval = _state == SurfaceState.Expanded
            ? TimeSpan.FromMilliseconds(260)
            : TimeSpan.FromMilliseconds(100);
        _collapseTimer.Start();
    }

    private void PeekTimer_Tick(object? sender, EventArgs e)
    {
        _peekTimer.Stop();
        if (_state != SurfaceState.Hover || !IsPointerInteractionActive())
        {
            return;
        }

        _state = SurfaceState.Peek;
        ApplyState();
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();

        // ContextMenu lives in a separate HWND, so the real cursor is naturally
        // outside the island while the user is choosing a setting. Keep the
        // surface stable until that popup closes.
        if (Root.ContextMenu?.IsOpen == true)
        {
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(160);
            _collapseTimer.Start();
            return;
        }

        if (IsPointerInteractionActive())
        {
            return;
        }

        if (_state is SurfaceState.Hover or SurfaceState.Peek or SurfaceState.Expanded)
        {
            _state = SurfaceState.Idle;
            ApplyState();
        }
    }

    private bool IsCursorInsideSurface()
    {
        if (_hwnd == IntPtr.Zero || SurfacePath.Data is null || !GetCursorPos(out var cursor))
        {
            return IsMouseOver;
        }

        if (!ScreenToClient(_hwnd, ref cursor))
        {
            return IsMouseOver;
        }

        var source = HwndSource.FromHwnd(_hwnd);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var local = transform.Transform(new Point(cursor.X, cursor.Y));
        return SurfacePath.Data.FillContains(local);
    }

    private bool IsPointerInteractionActive()
    {
        if (_edgeRevealVisualHidden)
        {
            return IsCursorInsideTopRevealZone();
        }

        return IsCursorInsideSurface()
               || (_settings.RevealOnTopEdge && _state != SurfaceState.Expanded && IsCursorInsideTopRevealZone());
    }

    private bool IsCursorInsideTopRevealZone()
    {
        if (!_settings.RevealOnTopEdge || _currentMonitor is null || !GetCursorPos(out var cursor))
        {
            return false;
        }

        var baseWidth = ResolveBaseSurfaceWidth(_currentMonitor.DipWidth);
        return TopEdgeRevealCalculator.Contains(_currentMonitor, baseWidth, cursor.X, cursor.Y);
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
            SurfaceState.Expanded => 235,
            SurfaceState.Idle => 205,
            SurfaceState.Peek => 180,
            _ => 165
        };

        var edgeHidden = _settings.RevealOnTopEdge && _state == SurfaceState.Idle;
        if (!immediate && _settings.Style == IslandStyle.Notch && !edgeHidden && _edgeRevealProgress < 0.92)
        {
            // Top-edge reveal is intentionally a little more deliberate than a
            // normal hover resize, but every property still shares one timeline.
            duration = Math.Max(duration, 225);
        }

        // Do not start/stop secondary data sources here. A newly completed
        // notification/Discord query can change column visibility while the HWND
        // is mid-resize, which reads as a dropped frame. Polling is reconciled on
        // the final motion frame instead.

        // Both content trees stay alive during a transition. Their opacities are
        // driven from the exact same frame progress as the shell, eliminating the
        // delayed async swap that used to visibly pop midway through a resize.
        CompactBar.Visibility = Visibility.Visible;
        ExpandedPanel.Visibility = Visibility.Visible;

        SurfacePath.SetResourceReference(System.Windows.Shapes.Path.FillProperty,
            _state is SurfaceState.Hover or SurfaceState.Peek ? "SurfaceHoverBrush" : "SurfaceBrush");

        _edgeRevealVisualHidden = edgeHidden;
        Root.IsHitTestVisible = !edgeHidden;

        var targetReveal = _settings.Style == IslandStyle.Notch
            ? edgeHidden ? 0.0 : 1.0
            : 1.0;
        var targetSurfaceOpacity = _settings.Style == IslandStyle.Notch
            ? 1.0
            : edgeHidden ? 0.0 : 1.0;
        var targetCompactOpacity = _state == SurfaceState.Expanded ? 0.0 : 1.0;
        var targetExpandedOpacity = _state == SurfaceState.Expanded ? 1.0 : 0.0;
        var targetExpandedTranslate = _state == SurfaceState.Expanded ? 0.0 : -4.0;

        var shadowOpacity = edgeHidden ? 0.0 : _state switch
        {
            SurfaceState.Idle => _settings.Style == IslandStyle.Notch ? 0.0 : 0.16,
            SurfaceState.Expanded => _settings.Style == IslandStyle.Notch ? 0.34 : 0.30,
            _ => _settings.Style == IslandStyle.Notch ? 0.26 : 0.24
        };
        // Animating DropShadowEffect.BlurRadius forces an expensive effect
        // re-rasterization every frame for almost no perceptual benefit. Keep the
        // kernel stable and animate only shadow opacity on the shared timeline.
        const double shadowBlur = 10.0;

        ConfigureContentMargins();
        // When expanding, keep the currently visible compact composition intact
        // until it has fully cross-faded out. On collapse, configure compact
        // density before it fades in so no items pop into existence halfway.
        if (_state != SurfaceState.Expanded)
        {
            UpdateCompactDensity();
        }
        AnimateSurface(
            target.Width,
            target.Height,
            target.Top,
            duration,
            targetReveal,
            targetSurfaceOpacity,
            targetCompactOpacity,
            targetExpandedOpacity,
            targetExpandedTranslate,
            shadowBlur,
            shadowOpacity);
    }

    private void TransitionGuardTimer_Tick(object? sender, EventArgs e)
    {
        if (_hwnd == IntPtr.Zero || _currentMonitor is null)
        {
            return;
        }

        if (_windowTransitionActive)
        {
            var elapsed = Stopwatch.GetElapsedTime(_windowTransitionStartedTimestamp).TotalMilliseconds;
            if (elapsed > _windowTransitionDurationMs + 500)
            {
                CompleteWindowTransition();
            }
            return;
        }

        var target = ResolveLayout(_state);
        var actualWidth = ActualWidth > 1 ? ActualWidth : Width;
        var actualHeight = ActualHeight > 1 ? ActualHeight : Height;
        if (Math.Abs(actualWidth - target.Width) > 2.5 || Math.Abs(actualHeight - target.Height) > 2.5)
        {
            // Recovery only. Normal completion already lands every property on
            // the same final frame, so the guard must not periodically overwrite
            // content opacity/position while another visual is settling.
            ApplyState(immediate: true);
        }
    }

    private void ReconcileContentWithState()
    {
        if (_state == SurfaceState.Expanded)
        {
            ExpandedPanel.Visibility = Visibility.Visible;
            ExpandedPanel.Opacity = 1;
            ExpandedTranslate.Y = 0;
            CompactBar.Visibility = Visibility.Hidden;
            CompactBar.Opacity = 0;
        }
        else
        {
            ExpandedPanel.Visibility = Visibility.Hidden;
            ExpandedPanel.Opacity = 0;
            ExpandedTranslate.Y = -4;
            CompactBar.Visibility = Visibility.Visible;
            CompactBar.Opacity = 1;
        }
    }

    private (double Width, double Height, double Top) ResolveLayout(SurfaceState state)
    {
        var layout = IslandLayoutCalculator.Resolve(_settings, state, GetTargetScreenWidthDip());
        return (layout.Width, layout.Height, layout.Top);
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

    private double ResolveBaseSurfaceWidth(double screenWidth) =>
        IslandLayoutCalculator.ResolveBaseSurfaceWidth(_settings, screenWidth);

    private void AnimateSurface(
        double targetWidth,
        double targetHeight,
        double targetTop,
        int durationMs,
        double targetReveal,
        double targetSurfaceOpacity,
        double targetCompactOpacity,
        double targetExpandedOpacity,
        double targetExpandedTranslate,
        double targetShadowBlur,
        double targetShadowOpacity)
    {
        var targetShapeProgress = _state == SurfaceState.Expanded ? 1.0 : 0.0;
        _targetTopDip = targetTop;

        var fromWidth = _renderedWidth > 1
            ? _renderedWidth
            : ActualWidth > 1 ? ActualWidth : Width;
        var fromHeight = _renderedHeight > 1
            ? _renderedHeight
            : ActualHeight > 1 ? ActualHeight : Height;
        var fromTop = _currentTopDip;
        var fromShape = _shapeExpansionProgress;
        var fromReveal = _edgeRevealProgress;
        var fromSurfaceOpacity = SurfacePath.Opacity;
        var fromCompactOpacity = CompactBar.Opacity;
        var fromExpandedOpacity = ExpandedPanel.Opacity;
        var fromExpandedTranslate = ExpandedTranslate.Y;
        var fromShadowBlur = SurfaceShadow.BlurRadius;
        var fromShadowOpacity = SurfaceShadow.Opacity;

        StopWindowTransition();

        _windowFromWidth = fromWidth;
        _windowFromHeight = fromHeight;
        _windowFromTopDip = fromTop;
        _windowFromShapeProgress = fromShape;
        _windowFromRevealProgress = fromReveal;
        _windowFromSurfaceOpacity = fromSurfaceOpacity;
        _windowFromCompactOpacity = fromCompactOpacity;
        _windowFromExpandedOpacity = fromExpandedOpacity;
        _windowFromExpandedTranslate = fromExpandedTranslate;
        _windowFromShadowBlur = fromShadowBlur;
        _windowFromShadowOpacity = fromShadowOpacity;

        _windowToWidth = targetWidth;
        _windowToHeight = targetHeight;
        _windowToTopDip = targetTop;
        _windowToShapeProgress = targetShapeProgress;
        _windowToRevealProgress = targetReveal;
        _windowToSurfaceOpacity = targetSurfaceOpacity;
        _windowToCompactOpacity = targetCompactOpacity;
        _windowToExpandedOpacity = targetExpandedOpacity;
        _windowToExpandedTranslate = targetExpandedTranslate;
        _windowToShadowBlur = targetShadowBlur;
        _windowToShadowOpacity = targetShadowOpacity;

        var visualDelta = new[]
        {
            Math.Abs(_windowToShapeProgress - _windowFromShapeProgress),
            Math.Abs(_windowToRevealProgress - _windowFromRevealProgress),
            Math.Abs(_windowToSurfaceOpacity - _windowFromSurfaceOpacity),
            Math.Abs(_windowToCompactOpacity - _windowFromCompactOpacity),
            Math.Abs(_windowToExpandedOpacity - _windowFromExpandedOpacity),
            Math.Abs(_windowToShadowOpacity - _windowFromShadowOpacity)
        }.Max();

        _windowTransitionDurationMs = MotionProfile.ScaleDuration(
            durationMs,
            _windowToWidth - _windowFromWidth,
            _windowToHeight - _windowFromHeight,
            visualDelta);

        if (durationMs <= 0 || _hwnd == IntPtr.Zero || _currentMonitor is null)
        {
            ApplySurfaceMotionFrame(1.0);
            _windowTransitionActive = false;
            Width = targetWidth;
            Height = targetHeight;
            ReconcileContentWithState();
            UpdateCompactDensity();
            UpdateSecondaryPollingState();
            return;
        }

        if (_backdropResult.NativeApplied && _settings.Material != SurfaceMaterial.Solid)
        {
            _windowRegionService.Reset(this);
        }

        _windowTransitionStartedTimestamp = Stopwatch.GetTimestamp();
        _windowTransitionActive = true;
        // Rendering is a static event. Defensively remove first so interrupted
        // transitions can never accumulate duplicate callbacks.
        CompositionTarget.Rendering -= WindowTransition_Rendering;
        CompositionTarget.Rendering += WindowTransition_Rendering;
    }

    private void WindowTransition_Rendering(object? sender, EventArgs e)
    {
        if (!_windowTransitionActive || _currentMonitor is null || _hwnd == IntPtr.Zero)
        {
            StopWindowTransition();
            return;
        }

        // Layout/size changes can pump WPF messages. Never allow a rendering
        // callback to enter itself; one physical display frame gets one motion
        // update, full stop.
        if (_surfaceMotionFrameInProgress)
        {
            return;
        }

        _surfaceMotionFrameInProgress = true;
        try
        {
            var elapsedMs = Stopwatch.GetElapsedTime(_windowTransitionStartedTimestamp).TotalMilliseconds;
            var t = Math.Clamp(elapsedMs / Math.Max(1, _windowTransitionDurationMs), 0, 1);
            ApplySurfaceMotionFrame(MotionProfile.Ease(t));

            if (t >= 1)
            {
                CompleteWindowTransition();
            }
        }
        finally
        {
            _surfaceMotionFrameInProgress = false;
        }
    }

    private void ApplySurfaceMotionFrame(double amount)
    {
        var width = Lerp(_windowFromWidth, _windowToWidth, amount);
        var height = Lerp(_windowFromHeight, _windowToHeight, amount);
        _renderedWidth = width;
        _renderedHeight = height;
        _currentTopDip = Lerp(_windowFromTopDip, _windowToTopDip, amount);
        _shapeExpansionProgress = Lerp(_windowFromShapeProgress, _windowToShapeProgress, amount);
        _edgeRevealProgress = Lerp(_windowFromRevealProgress, _windowToRevealProgress, amount);

        var surfaceOpacity = Lerp(_windowFromSurfaceOpacity, _windowToSurfaceOpacity, amount);
        SurfacePath.Opacity = surfaceOpacity;
        ContentHost.Opacity = surfaceOpacity;
        var isCleanExpand = _windowFromCompactOpacity > 0.98
                            && _windowToCompactOpacity < 0.02
                            && _windowFromExpandedOpacity < 0.02
                            && _windowToExpandedOpacity > 0.98;
        var isCleanCollapse = _windowFromCompactOpacity < 0.02
                              && _windowToCompactOpacity > 0.98
                              && _windowFromExpandedOpacity > 0.98
                              && _windowToExpandedOpacity < 0.02;

        if (isCleanExpand)
        {
            // One master timeline with a soft gamma cross-fade. Both trees are
            // present around the midpoint, but neither reaches 50% opacity there,
            // avoiding both the old double-image and the temporary blank band.
            CompactBar.Opacity = Math.Pow(1.0 - amount, 1.45);
            ExpandedPanel.Opacity = Math.Pow(amount, 1.45);
        }
        else if (isCleanCollapse)
        {
            ExpandedPanel.Opacity = Math.Pow(1.0 - amount, 1.45);
            CompactBar.Opacity = Math.Pow(amount, 1.45);
        }
        else
        {
            // Interrupted/reversed transitions continue from the exact rendered
            // values rather than snapping back onto a canned cross-fade curve.
            CompactBar.Opacity = Lerp(_windowFromCompactOpacity, _windowToCompactOpacity, amount);
            ExpandedPanel.Opacity = Lerp(_windowFromExpandedOpacity, _windowToExpandedOpacity, amount);
        }

        ExpandedTranslate.Y = Lerp(_windowFromExpandedTranslate, _windowToExpandedTranslate, amount);
        SurfaceShadow.BlurRadius = Lerp(_windowFromShadowBlur, _windowToShadowBlur, amount);
        SurfaceShadow.Opacity = Lerp(_windowFromShadowOpacity, _windowToShadowOpacity, amount);

        // Let WPF own the size change so its measure/arrange pass and the HWND
        // resize stay in the same pipeline. Native SetWindowPos used to resize
        // the HWND immediately while WPF content was still on the previous
        // layout for one frame, producing the visible "blank enlarged shell".
        SetCurrentValue(WidthProperty, width);
        SetCurrentValue(HeightProperty, height);
        // Do not call UpdateLayout() here. Rendering can be re-entered by a
        // forced layout pass, which previously created nested motion frames,
        // tanked FPS, and let stale heights overwrite the expanded surface. WPF
        // will coalesce this invalidation into its normal layout/render pipeline.
        if (_hwnd != IntPtr.Zero && _currentMonitor is not null)
        {
            _monitorService.PositionWindow(_hwnd, _currentMonitor, _currentTopDip);
        }

        UpdateGeometry(width, height);
    }

    private void CompleteWindowTransition()
    {
        if (_currentMonitor is null || _hwnd == IntPtr.Zero)
        {
            StopWindowTransition();
            return;
        }

        ApplySurfaceMotionFrame(1.0);
        StopWindowTransition();
        _renderedWidth = _windowToWidth;
        _renderedHeight = _windowToHeight;
        _currentTopDip = _windowToTopDip;
        _shapeExpansionProgress = _windowToShapeProgress;
        _edgeRevealProgress = _windowToRevealProgress;

        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        Width = _windowToWidth;
        Height = _windowToHeight;
        _monitorService.PositionAndSizeWindow(_hwnd, _currentMonitor, _currentTopDip, _windowToWidth, _windowToHeight);
        UpdateGeometry(_windowToWidth, _windowToHeight);
        ReconcileContentWithState();
        UpdateCompactDensity();
        UpdateSecondaryPollingState();
    }

    private void StopWindowTransition()
    {
        // Always detach. A stale/duplicate subscription must not survive merely
        // because the active flag was already cleared by an interrupted frame.
        CompositionTarget.Rendering -= WindowTransition_Rendering;
        _windowTransitionActive = false;
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;

    private static IEasingFunction CreateMotionEasing() => MotionProfile.CreateWpfEasing();

    private void ConfigureContentMargins()
    {
        if (_settings.Style == IslandStyle.Notch)
        {
            CompactBar.Margin = new Thickness(30, 2, 30, 0);
            // The notch surface ends ShadowPadding (16 DIP) above the window
            // bottom. Add that padding to the window-side bottom margin so the
            // content is actually 20 DIP from both visible surface edges.
            ExpandedPanel.Margin = new Thickness(47, 20, 47, 36);
        }
        else
        {
            CompactBar.Margin = new Thickness(30, 18, 30, 0);
            ExpandedPanel.Margin = new Thickness(36);
        }
    }

    private void UpdateGeometry(double? widthOverride = null, double? heightOverride = null)
    {
        var geometryWidth = widthOverride ?? ActualWidth;
        var geometryHeight = heightOverride ?? ActualHeight;
        if (geometryWidth <= 1 || geometryHeight <= 1)
        {
            return;
        }

        var geometry = IslandGeometryFactory.Create(
            _settings.Style,
            new Size(geometryWidth, geometryHeight),
            _shapeExpansionProgress,
            _settings.Style == IslandStyle.Notch ? _edgeRevealProgress : 1.0);
        SurfacePath.Data = geometry;
        ContentHost.Clip = geometry;

        if (!_windowTransitionActive)
        {
            if (_backdropResult.NativeApplied && _settings.Material != SurfaceMaterial.Solid)
            {
                _windowRegionService.Apply(this, geometry);
            }
            else
            {
                _windowRegionService.Reset(this);
            }
        }
    }

}
