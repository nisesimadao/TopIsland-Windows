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
        // Start the grace period immediately so stats/media work cannot delay the
        // beginning of a close by hundreds of milliseconds. Native resize can
        // synthesize MouseLeave, but the pointer safety timer will see that the
        // cursor is still inside, cancel the grace timer and resume polling.
        _pointerWasInside = false;
        HandlePointerLeft();
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
        if (_motionPollingSuspended && !_windowTransitionActive)
        {
            ResumeMotionSensitivePolling();
        }

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
        // Do not let a stats/media refresh steal the dispatcher while we are
        // waiting to start the close animation. If the pointer comes back during
        // the grace period, HandlePointerEntered resumes the sources immediately.
        SuspendMotionSensitivePolling();
        // Transparent/no-activate HWNDs occasionally miss or duplicate WPF mouse
        // events while resizing. The pointer tracker above supplies the missing
        // edge; this grace period absorbs transient leave events during morphs.
        _collapseTimer.Interval = _state == SurfaceState.Expanded
            ? TimeSpan.FromMilliseconds(180)
            : TimeSpan.FromMilliseconds(90);
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
            ResumeMotionSensitivePolling();
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
        var geometry = SurfacePath.Data;
        var bounds = geometry.Bounds;
        if (!bounds.Contains(local))
        {
            return false;
        }

        // The expensive path containment test is only useful around rounded or
        // inverse-radius edges. Most pointer samples sit well inside the surface.
        const double edgeBand = 7.0;
        var inner = new Rect(
            bounds.X + edgeBand,
            bounds.Y + edgeBand,
            Math.Max(0, bounds.Width - edgeBand * 2),
            Math.Max(0, bounds.Height - edgeBand * 2));
        return inner.Contains(local) || geometry.FillContains(local);
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
            SurfaceState.Expanded => 228,
            SurfaceState.Idle => 190,
            SurfaceState.Peek => 168,
            _ => 152
        };

        var edgeHidden = _settings.RevealOnTopEdge && _state == SurfaceState.Idle;
        if (!immediate && _settings.Style == IslandStyle.Notch && !edgeHidden && _edgeRevealProgress < 0.92)
        {
            duration = Math.Max(duration, 215);
        }

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

        var shadowOpacity = edgeHidden ? 0.0 : _state switch
        {
            SurfaceState.Idle => _settings.Style == IslandStyle.Notch ? 0.0 : 0.16,
            SurfaceState.Expanded => _settings.Style == IslandStyle.Notch ? 0.34 : 0.30,
            _ => _settings.Style == IslandStyle.Notch ? 0.26 : 0.24
        };

        const double shadowBlur = 10.0;
        ConfigureContentMargins();

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
        CompactBar.Opacity = 1;
        ExpandedPanel.Opacity = 1;

        if (_state == SurfaceState.Expanded)
        {
            CompactBar.Visibility = Visibility.Hidden;
            CompactTranslate.Y = -8;
            _compactContentClip.Rect = Rect.Empty;

            ExpandedPanel.Visibility = Visibility.Visible;
            ExpandedTranslate.Y = 0;
            ExpandedTopTranslate.Y = 0;
            ExpandedBottomTranslate.Y = 0;
            _expandedContentClip.Rect = new Rect(
                0, 0,
                Math.Max(1, ExpandedPanel.Width),
                Math.Max(1, ExpandedPanel.Height));
        }
        else
        {
            CompactBar.Visibility = Visibility.Visible;
            CompactTranslate.Y = 0;
            _compactContentClip.Rect = new Rect(
                0, 0,
                Math.Max(1, CompactBar.Width),
                Math.Max(1, CompactBar.Height));

            ExpandedPanel.Visibility = Visibility.Hidden;
            ExpandedTranslate.Y = 0;
            ExpandedTopTranslate.Y = 10;
            ExpandedBottomTranslate.Y = 16;
            _expandedContentClip.Rect = Rect.Empty;
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

    private void PrepareContentLayout(double fromWidth, double fromHeight, double toWidth, double toHeight, double fromShape, double toShape)
    {
        // Keep endpoint layouts fixed throughout the morph. In particular, never
        // use an in-between shell width as the expanded content width when motion
        // reverses. That was the source of the close-time padding/reflow jump.
        var compactOuterWidth = toShape <= fromShape + 0.001
            ? toWidth
            : !double.IsNaN(CompactBar.Width) && CompactBar.Width > 1
                ? CompactBar.Width + CompactBar.Margin.Left + CompactBar.Margin.Right
                : fromWidth;

        var expandedLayout = ResolveLayout(SurfaceState.Expanded);
        CompactBar.Width = Math.Max(1, compactOuterWidth - CompactBar.Margin.Left - CompactBar.Margin.Right);
        CompactBar.HorizontalAlignment = HorizontalAlignment.Center;

        ExpandedPanel.Width = Math.Max(1, expandedLayout.Width - ExpandedPanel.Margin.Left - ExpandedPanel.Margin.Right);
        ExpandedPanel.Height = Math.Max(1, expandedLayout.Height - ExpandedPanel.Margin.Top - ExpandedPanel.Margin.Bottom);
        ExpandedPanel.HorizontalAlignment = HorizontalAlignment.Center;
        ExpandedPanel.VerticalAlignment = VerticalAlignment.Top;

        CompactBar.Opacity = 1;
        ExpandedPanel.Opacity = 1;
        ApplyContentMotionFrame();
    }

    private void ApplyContentMotionFrame()
    {
        var expansion = Math.Clamp(_shapeExpansionProgress, 0.0, 1.0);

        // No opacity choreography. Compact retracts upward first, then the
        // expanded layout is uncovered top-to-bottom. The exact same function is
        // used in reverse during collapse, so interrupted motion cannot leave a
        // stale content state or padding arrangement behind.
        var compactExit = MotionProfile.EaseRange(expansion, 0.04, 0.30);
        var expandedReveal = MotionProfile.EaseRange(expansion, 0.30, 0.94);
        var topEnter = MotionProfile.EaseRange(expansion, 0.30, 0.72);
        var bottomEnter = MotionProfile.EaseRange(expansion, 0.52, 0.94);

        var compactWidth = Math.Max(1, CompactBar.Width);
        var compactHeight = Math.Max(1, CompactBar.Height);
        var compactVisibleHeight = compactHeight * (1.0 - compactExit);
        CompactTranslate.Y = -9.0 * compactExit;
        _compactContentClip.Rect = compactVisibleHeight <= 0.15
            ? Rect.Empty
            : new Rect(0, 0, compactWidth, compactVisibleHeight);
        CompactBar.Visibility = compactVisibleHeight <= 0.15
            ? Visibility.Hidden
            : Visibility.Visible;

        var expandedWidth = Math.Max(1, ExpandedPanel.Width);
        var expandedHeight = Math.Max(1, ExpandedPanel.Height);
        var expandedVisibleHeight = expandedHeight * expandedReveal;
        ExpandedTranslate.Y = 0;
        ExpandedTopTranslate.Y = 10.0 * (1.0 - topEnter);
        ExpandedBottomTranslate.Y = 16.0 * (1.0 - bottomEnter);
        _expandedContentClip.Rect = expandedVisibleHeight <= 0.15
            ? Rect.Empty
            : new Rect(0, 0, expandedWidth, expandedVisibleHeight);
        ExpandedPanel.Visibility = expandedVisibleHeight <= 0.15
            ? Visibility.Hidden
            : Visibility.Visible;
    }

    private void SuspendMotionSensitivePolling()
    {
        if (_motionPollingSuspended)
        {
            return;
        }

        _motionPollingSuspended = true;
        _resumeStatsAfterMotion = _statsTimer.IsEnabled;
        _resumeMediaAfterMotion = _mediaTimer.IsEnabled;
        _resumeThemeAfterMotion = _themeTimer.IsEnabled;

        _secondaryResumeTimer.Stop();
        _statsTimer.Stop();
        _mediaTimer.Stop();
        _themeTimer.Stop();
        _notificationTimer.Stop();
        _discordTimer.Stop();
    }

    private void ResumeMotionSensitivePolling()
    {
        if (!_motionPollingSuspended)
        {
            return;
        }

        _motionPollingSuspended = false;
        if (!IsLoaded || !IsVisible)
        {
            return;
        }

        if (_resumeStatsAfterMotion) _statsTimer.Start();
        if (_resumeMediaAfterMotion) _mediaTimer.Start();
        if (_resumeThemeAfterMotion) _themeTimer.Start();
        _resumeStatsAfterMotion = _resumeMediaAfterMotion = _resumeThemeAfterMotion = false;

        // Discord UI Automation and notification enumeration can be much more
        // expensive than the visible shell motion. Only start them after the
        // surface has stayed settled for a moment; leaving the surface cancels
        // this timer in SuspendMotionSensitivePolling().
        _secondaryResumeTimer.Stop();
        _secondaryResumeTimer.Start();
    }

    private void SecondaryResumeTimer_Tick(object? sender, EventArgs e)
    {
        _secondaryResumeTimer.Stop();
        if (_windowTransitionActive || _collapseTimer.IsEnabled)
        {
            return;
        }

        UpdateSecondaryPollingState();
    }

    private void AnimateSurface(
        double targetWidth,
        double targetHeight,
        double targetTop,
        int durationMs,
        double targetReveal,
        double targetSurfaceOpacity,
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
        var fromShadowBlur = SurfaceShadow.BlurRadius;
        var fromShadowOpacity = SurfaceShadow.Opacity;

        StopWindowTransition();

        _windowFromWidth = fromWidth;
        _windowFromHeight = fromHeight;
        _windowFromTopDip = fromTop;
        _windowFromShapeProgress = fromShape;
        _windowFromRevealProgress = fromReveal;
        _windowFromSurfaceOpacity = fromSurfaceOpacity;
        _windowFromShadowBlur = fromShadowBlur;
        _windowFromShadowOpacity = fromShadowOpacity;

        _windowToWidth = targetWidth;
        _windowToHeight = targetHeight;
        _windowToTopDip = targetTop;
        _windowToShapeProgress = targetShapeProgress;
        _windowToRevealProgress = targetReveal;
        _windowToSurfaceOpacity = targetSurfaceOpacity;
        _windowToShadowBlur = targetShadowBlur;
        _windowToShadowOpacity = targetShadowOpacity;

        var visualDelta = new[]
        {
            Math.Abs(_windowToShapeProgress - _windowFromShapeProgress),
            Math.Abs(_windowToRevealProgress - _windowFromRevealProgress),
            Math.Abs(_windowToSurfaceOpacity - _windowFromSurfaceOpacity),
            Math.Abs(_windowToShadowOpacity - _windowFromShadowOpacity)
        }.Max();

        _windowTransitionDurationMs = MotionProfile.ScaleDuration(
            durationMs,
            _windowToWidth - _windowFromWidth,
            _windowToHeight - _windowFromHeight,
            visualDelta);

        PrepareContentLayout(
            _windowFromWidth, _windowFromHeight,
            _windowToWidth, _windowToHeight,
            _windowFromShapeProgress, _windowToShapeProgress);

        if (durationMs <= 0 || _hwnd == IntPtr.Zero || _currentMonitor is null)
        {
            ApplySurfaceMotionFrame(1.0);
            _windowTransitionActive = false;
            Width = targetWidth;
            Height = targetHeight;
            ReconcileContentWithState();
            UpdateCompactDensity();
            ResumeMotionSensitivePolling();
            UpdateSecondaryPollingState();
            return;
        }

        if (_backdropResult.NativeApplied && _settings.Material != SurfaceMaterial.Solid)
        {
            _windowRegionService.Reset(this);
        }

        SuspendMotionSensitivePolling();
        _windowTransitionStartedTimestamp = Stopwatch.GetTimestamp();
        _windowTransitionActive = true;
        if (!_highResolutionMotionTimerActive)
        {
            _highResolutionMotionTimerActive = TimeBeginPeriod(1) == 0;
        }
        _motionTimer.Stop();
        _motionTimer.Start();
    }

    private void MotionTimer_Tick(object? sender, EventArgs e)
    {
        if (!_windowTransitionActive || _currentMonitor is null || _hwnd == IntPtr.Zero)
        {
            StopWindowTransition();
            ResumeMotionSensitivePolling();
            return;
        }

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

        var blurRevealProgress = _settings.Style == IslandStyle.Notch
            ? _edgeRevealProgress
            : surfaceOpacity;
        BlurHostService.SetRevealProgress(_hwnd, blurRevealProgress);
        BlurHostService.SetShapeProgress(_hwnd, _shapeExpansionProgress);

        // Content motion is geometry/translation only. Never interpolate opacity.
        ApplyContentMotionFrame();

        SurfaceShadow.BlurRadius = Lerp(_windowFromShadowBlur, _windowToShadowBlur, amount);
        SurfaceShadow.Opacity = Lerp(_windowFromShadowOpacity, _windowToShadowOpacity, amount);

        SetCurrentValue(WidthProperty, width);
        SetCurrentValue(HeightProperty, height);
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
            ResumeMotionSensitivePolling();
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

        // Native resize can synthesize leave/enter events while a morph is in
        // flight. Re-sample the actual cursor on the final geometry so the next
        // real leave can never be lost because _pointerWasInside is stale.
        var pointerInside = IsPointerInteractionActive();
        _pointerWasInside = pointerInside;
        if (!pointerInside && _state is SurfaceState.Hover or SurfaceState.Peek or SurfaceState.Expanded)
        {
            HandlePointerLeft();
        }
        else
        {
            ResumeMotionSensitivePolling();
        }
    }

    private void StopWindowTransition()
    {
        _motionTimer.Stop();
        if (_highResolutionMotionTimerActive)
        {
            _ = TimeEndPeriod(1);
            _highResolutionMotionTimerActive = false;
        }
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
