using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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

        ConfigureContentMargins();
        EnsureMotionHost();

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
        if (Math.Abs(_renderedWidth - target.Width) > 2.5 || Math.Abs(_renderedHeight - target.Height) > 2.5)
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
            CompactBar.Visibility = Visibility.Visible;
            CompactBar.IsHitTestVisible = false;
            CompactTranslate.Y = -MotionProfile.CompactTravelY;
            _compactContentClip.Rect = Rect.Empty;

            ExpandedPanel.Visibility = Visibility.Visible;
            ExpandedPanel.IsHitTestVisible = true;
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
            CompactBar.IsHitTestVisible = true;
            CompactTranslate.Y = 0;
            _compactContentClip.Rect = new Rect(
                0, 0,
                Math.Max(1, CompactBar.Width),
                Math.Max(1, CompactBar.Height));

            ExpandedPanel.Visibility = Visibility.Visible;
            ExpandedPanel.IsHitTestVisible = false;
            ExpandedTranslate.Y = 0;
            ExpandedTopTranslate.Y = MotionProfile.ExpandedTopTravelY;
            ExpandedBottomTranslate.Y = MotionProfile.ExpandedBottomTravelY;
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

    private void EnsureMotionHost()
    {
        if (_currentMonitor is null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var screenWidth = GetTargetScreenWidthDip();
        var layouts = new[]
        {
            IslandLayoutCalculator.Resolve(_settings, SurfaceState.Idle, screenWidth),
            IslandLayoutCalculator.Resolve(_settings, SurfaceState.Hover, screenWidth),
            IslandLayoutCalculator.Resolve(_settings, SurfaceState.Peek, screenWidth),
            IslandLayoutCalculator.Resolve(_settings, SurfaceState.Expanded, screenWidth)
        };

        var hostWidth = layouts.Max(x => x.Width);
        var hostTop = layouts.Min(x => x.Top);
        var hostBottom = layouts.Max(x => x.Top + x.Height);
        var hostHeight = Math.Max(1, hostBottom - hostTop);
        var changed = !_motionHostConfigured
                      || Math.Abs(_motionHostWidth - hostWidth) > 0.5
                      || Math.Abs(_motionHostHeight - hostHeight) > 0.5
                      || Math.Abs(_motionHostTopDip - hostTop) > 0.5;

        _motionHostConfigured = true;
        _motionHostWidth = hostWidth;
        _motionHostHeight = hostHeight;
        _motionHostTopDip = hostTop;
        if (!changed)
        {
            return;
        }

        _motionHostUpdating = true;
        try
        {
            SetCurrentValue(WidthProperty, hostWidth);
            SetCurrentValue(HeightProperty, hostHeight);
            _monitorService.PositionAndSizeWindow(_hwnd, _currentMonitor, hostTop, hostWidth, hostHeight);
            UpdateLayout();
        }
        finally
        {
            _motionHostUpdating = false;
        }
    }

    private (double X, double Y) ResolveSurfaceOffset(double width, double topDip)
    {
        if (!_motionHostConfigured)
        {
            return (0, 0);
        }

        return (
            Math.Max(0, (_motionHostWidth - width) / 2.0),
            Math.Max(0, topDip - _motionHostTopDip));
    }

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
        var compactExit = MotionProfile.CompactExit(expansion);
        var topEnter = MotionProfile.ExpandedTopEnter(expansion);
        var bottomEnter = MotionProfile.ExpandedBottomEnter(expansion);

        var compactWidth = Math.Max(1, CompactBar.Width);
        var compactHeight = Math.Max(1, CompactBar.Height);
        var compactVisibleHeight = compactHeight * (1.0 - compactExit);
        CompactTranslate.Y = -MotionProfile.CompactTravelY * compactExit;
        _compactContentClip.Rect = compactVisibleHeight <= 0.15
            ? Rect.Empty
            : new Rect(0, 0, compactWidth, compactVisibleHeight);
        CompactBar.Visibility = Visibility.Visible;
        CompactBar.IsHitTestVisible = false;

        var expandedWidth = Math.Max(1, ExpandedPanel.Width);
        var expandedHeight = Math.Max(1, ExpandedPanel.Height);
        var expandedVisibleHeight = expandedHeight * Math.Clamp((expansion - 0.04) / 0.86, 0.0, 1.0);
        ExpandedTranslate.Y = 0;
        ExpandedTopTranslate.Y = MotionProfile.ExpandedTopTravelY * (1.0 - topEnter);
        ExpandedBottomTranslate.Y = MotionProfile.ExpandedBottomTravelY * (1.0 - bottomEnter);
        _expandedContentClip.Rect = expandedVisibleHeight <= 0.15
            ? Rect.Empty
            : new Rect(0, 0, expandedWidth, expandedVisibleHeight);
        ExpandedPanel.Visibility = Visibility.Visible;
        ExpandedPanel.IsHitTestVisible = false;
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

    private bool UsesExternalMotionBackdrop =>
        _externalBlurAvailable
        && _settings.Material is SurfaceMaterial.Acrylic or SurfaceMaterial.Glass;

    private void PrepareMotionBackdropSnapshot()
    {
        if (!UsesExternalMotionBackdrop || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _backdropHandoffTimer.Stop();
        if (_motionBackdropActive)
        {
            // A reversal during the handoff window can reuse the same spatially
            // correct blur snapshot without paying another desktop capture/filter.
            BlurHostService.SetMotionSnapshotActive(_hwnd, true);
            return;
        }

        try
        {
            var snapshot = _motionBackdropSnapshotService.Capture(
                _hwnd,
                _settings.Material,
                _themeService.ResolveLight(_settings.Theme));
            if (snapshot is null)
            {
                return;
            }

            BackdropSnapshotImage.Source = snapshot;
            BackdropSnapshotLayer.Width = Math.Max(1, _motionHostWidth);
            BackdropSnapshotLayer.Height = Math.Max(1, _motionHostHeight);
            BackdropSnapshotLayer.HorizontalAlignment = HorizontalAlignment.Left;
            BackdropSnapshotLayer.VerticalAlignment = VerticalAlignment.Top;

            // Clip once at the maximum endpoint. During motion this entire cached
            // layer is transformed by the GPU; the clip is not rebuilt per frame.
            var expandedLayout = ResolveLayout(SurfaceState.Expanded);
            var expandedOffset = ResolveSurfaceOffset(expandedLayout.Width, expandedLayout.Top);
            var expandedClip = IslandGeometryFactory.Create(
                _settings.Style,
                new Size(expandedLayout.Width, expandedLayout.Height),
                1.0,
                1.0);
            expandedClip = expandedClip.Clone();
            expandedClip.Transform = new TranslateTransform(expandedOffset.X, expandedOffset.Y);
            expandedClip.Freeze();
            BackdropSnapshotLayer.Clip = expandedClip;
            BackdropSnapshotLayer.RenderTransformOrigin = _settings.Style == IslandStyle.Notch
                ? new Point(0.5, 0)
                : new Point(0.5, 0.5);
            BackdropSnapshotScale.ScaleX = 1;
            BackdropSnapshotScale.ScaleY = 1;
            BackdropSnapshotTranslate.X = 0;
            BackdropSnapshotTranslate.Y = 0;
            BackdropSnapshotLayer.Visibility = Visibility.Visible;
            _motionBackdropActive = true;
            BlurHostService.SetMotionSnapshotActive(_hwnd, true);
        }
        catch
        {
            // The external BlurHost remains the safe fallback if desktop capture
            // is unavailable for a frame (secure desktop, driver reset, etc.).
            _motionBackdropActive = false;
        }
    }

    private void BeginMotionBackdropHandoff()
    {
        if (!_motionBackdropActive)
        {
            return;
        }

        BlurHostService.SetMotionSnapshotActive(_hwnd, false);
        _backdropHandoffTimer.Stop();
        _backdropHandoffTimer.Start();
    }

    private void BackdropHandoffTimer_Tick(object? sender, EventArgs e)
    {
        _backdropHandoffTimer.Stop();
        if (_windowTransitionActive)
        {
            return;
        }

        BackdropSnapshotLayer.Visibility = Visibility.Collapsed;
        BackdropSnapshotImage.Source = null;
        BackdropSnapshotLayer.Clip = null;
        _motionBackdropActive = false;
    }

    private void ClearMotionBackdropImmediately()
    {
        _backdropHandoffTimer.Stop();
        BlurHostService.SetMotionSnapshotActive(_hwnd, false);
        BackdropSnapshotLayer.Visibility = Visibility.Collapsed;
        BackdropSnapshotImage.Source = null;
        BackdropSnapshotLayer.Clip = null;
        _motionBackdropActive = false;
    }

    private void AnimateSurface(
        double targetWidth,
        double targetHeight,
        double targetTop,
        int durationMs,
        double targetReveal,
        double targetSurfaceOpacity,
        double targetShadowOpacity)
    {
        var targetShapeProgress = _state == SurfaceState.Expanded ? 1.0 : 0.0;
        _targetTopDip = targetTop;

        EnsureMotionHost();
        var fromWidth = _renderedWidth > 1 ? _renderedWidth : targetWidth;
        var fromHeight = _renderedHeight > 1 ? _renderedHeight : targetHeight;
        var fromTop = _currentTopDip;
        var fromShape = _shapeExpansionProgress;
        var fromReveal = _edgeRevealProgress;
        var fromSurfaceOpacity = SurfacePath.Opacity;
        var fromShadowOpacity = _renderedShadowOpacity;

        if (_compositionSurfaceActive && _motionCompositionHost.TryGetCurrentFrame(out var compositionFrame))
        {
            fromWidth = compositionFrame.Width;
            fromHeight = compositionFrame.Height;
            fromTop = compositionFrame.Top;
            fromShape = compositionFrame.ShapeProgress;
            fromReveal = compositionFrame.RevealProgress;
            fromSurfaceOpacity = compositionFrame.SurfaceOpacity;
            fromShadowOpacity = compositionFrame.ShadowOpacity;
        }

        StopWindowTransition();

        _windowFromWidth = fromWidth;
        _windowFromHeight = fromHeight;
        _windowFromTopDip = fromTop;
        _windowFromShapeProgress = fromShape;
        _windowFromRevealProgress = fromReveal;
        _windowFromSurfaceOpacity = fromSurfaceOpacity;
        _windowFromShadowOpacity = fromShadowOpacity;

        _windowToWidth = targetWidth;
        _windowToHeight = targetHeight;
        _windowToTopDip = targetTop;
        _windowToShapeProgress = targetShapeProgress;
        _windowToRevealProgress = targetReveal;
        _windowToSurfaceOpacity = targetSurfaceOpacity;
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
            ReconcileContentWithState();
            UpdateCompactDensity();
            ClearMotionBackdropImmediately();
            ApplyNativeRevealEndpointVisibility();
            ResumeMotionSensitivePolling();
            UpdateSecondaryPollingState();
            return;
        }

        if (_backdropResult.NativeApplied && _settings.Material != SurfaceMaterial.Solid)
        {
            _windowRegionService.Reset(this);
        }

        SuspendMotionSensitivePolling();
        if (_motionCompositionHost.IsUsable)
        {
            StartCompositionSurfaceTransition();
            return;
        }

        StartLegacySurfaceTransition();
    }

    private void StartLegacySurfaceTransition()
    {
        if (_hwnd != IntPtr.Zero)
        {
            _ = ShowWindow(_hwnd, 4);
        }
        PrepareMotionBackdropSnapshot();
        _windowTransitionStartedTimestamp = Stopwatch.GetTimestamp();
        _windowTransitionActive = true;
        CompositionTarget.Rendering -= WindowTransition_Rendering;
        CompositionTarget.Rendering += WindowTransition_Rendering;
    }

    private void PrewarmMotionCompositionHost()
    {
        if (_currentMonitor is null || !_motionCompositionHost.IsUsable)
        {
            return;
        }
        EnsureMotionHost();
        _motionCompositionHost.Prewarm(
            _currentMonitor,
            Math.Max(1, _motionHostWidth),
            Math.Max(1, _motionHostHeight),
            _motionHostTopDip);
    }

    private MotionCompositionContent? CaptureMotionCompositionContent()
    {
        var key = BuildMotionCompositionContentCacheKey();
        if (_cachedMotionCompositionContent is not null
            && string.Equals(_cachedMotionCompositionContentKey, key, StringComparison.Ordinal))
        {
            return _cachedMotionCompositionContent;
        }

        var snapshot = CreateMotionCompositionContentSnapshot();
        if (snapshot is not null)
        {
            _cachedMotionCompositionContent = snapshot;
            _cachedMotionCompositionContentKey = key;
        }
        return snapshot;
    }

    private void RefreshMotionCompositionContentCache()
    {
        if (_motionCompositionContentCacheRefreshing
            || !IsLoaded
            || _windowTransitionActive
            || !_edgeRevealVisualHidden
            || _currentMonitor is null)
        {
            return;
        }

        _motionCompositionContentCacheRefreshing = true;
        try
        {
            EnsureMotionHost();
            var snapshot = CreateMotionCompositionContentSnapshot();
            if (snapshot is not null)
            {
                _cachedMotionCompositionContent = snapshot;
                _cachedMotionCompositionContentKey = BuildMotionCompositionContentCacheKey();
            }
        }
        finally
        {
            _motionCompositionContentCacheRefreshing = false;
        }
    }

    private string BuildMotionCompositionContentCacheKey() =>
        $"{(int)_settings.Style}|{(int)_settings.Theme}|{(int)_settings.Material}|{(int)_settings.WidthPreset}|{_settings.CustomWidth:F1}|{_settings.SideMargin:F1}|{_currentMonitor?.DeviceName}|{_currentMonitor?.Scale:F3}|{_motionHostWidth:F1}|{_motionHostHeight:F1}|{CompactBar.Width:F1}|{ExpandedPanel.Width:F1}|{ExpandedPanel.Height:F1}";

    private MotionCompositionContent? CreateMotionCompositionContentSnapshot()
    {
        if (_currentMonitor is null || !IsLoaded) return null;
        var compactClip = CompactBar.Clip;
        var expandedClip = ExpandedPanel.Clip;
        var surfaceContentClip = ContentHost.Clip;
        var compactTranslateY = CompactTranslate.Y;
        var expandedTranslateY = ExpandedTranslate.Y;
        var expandedTopTranslateY = ExpandedTopTranslate.Y;
        var expandedBottomTranslateY = ExpandedBottomTranslate.Y;
        var contentHostTranslateY = ContentHostTranslate.Y;
        var contentHostOpacity = ContentHost.Opacity;
        var compactVisibility = CompactBar.Visibility;
        var expandedVisibility = ExpandedPanel.Visibility;
        var surfaceVisibility = SurfacePath.Visibility;
        var outerShadowVisibility = ShadowOuterPath.Visibility;
        var innerShadowVisibility = ShadowInnerPath.Visibility;
        var backdropVisibility = BackdropSnapshotLayer.Visibility;
        var compactOpacity = CompactBar.Opacity;
        var expandedOpacity = ExpandedPanel.Opacity;
        try
        {
            CompactBar.Clip = null;
            ExpandedPanel.Clip = null;
            ContentHost.Clip = null;
            CompactTranslate.Y = 0;
            ExpandedTranslate.Y = 0;
            ExpandedTopTranslate.Y = 0;
            ExpandedBottomTranslate.Y = 0;
            ContentHostTranslate.Y = 0;
            ContentHost.Opacity = 1;
            CompactBar.Opacity = 1;
            ExpandedPanel.Opacity = 1;
            SurfacePath.Visibility = Visibility.Collapsed;
            ShadowOuterPath.Visibility = Visibility.Collapsed;
            ShadowInnerPath.Visibility = Visibility.Collapsed;
            BackdropSnapshotLayer.Visibility = Visibility.Collapsed;

            // Render one endpoint at a time from the already-connected Root visual.
            // Rendering a detached/sub-element directly is unreliable on a layered
            // WPF HWND, while Root is guaranteed to have a live render target.
            ExpandedPanel.Visibility = Visibility.Collapsed;
            CompactBar.Visibility = Visibility.Visible;
            Root.UpdateLayout();
            var compactPoint = CompactBar.TranslatePoint(new Point(0, 0), Root);
            var compact = CaptureCompositionBitmapFromRoot(CompactBar, compactPoint.X, compactPoint.Y, _currentMonitor.Scale);

            CompactBar.Visibility = Visibility.Collapsed;
            ExpandedPanel.Visibility = Visibility.Visible;
            Root.UpdateLayout();
            var expandedPoint = ExpandedPanel.TranslatePoint(new Point(0, 0), Root);
            var topPoint = ExpandedTopContent.TranslatePoint(new Point(0, 0), ExpandedPanel);
            var bottomPoint = ExpandedBottomContent.TranslatePoint(new Point(0, 0), ExpandedPanel);
            var expanded = CaptureCompositionBitmapFromRoot(ExpandedPanel, expandedPoint.X, expandedPoint.Y, _currentMonitor.Scale);
            if (compact is null || expanded is null) return null;

            var topRightX = ExpandedTopContent.ColumnDefinitions.Count >= 2
                ? ExpandedTopContent.ColumnDefinitions[0].ActualWidth + ExpandedTopContent.ColumnDefinitions[1].ActualWidth
                : expanded.Width * 0.50;
            var bottomMiddleX = ExpandedBottomContent.ColumnDefinitions.Count >= 2
                ? ExpandedBottomContent.ColumnDefinitions[0].ActualWidth + ExpandedBottomContent.ColumnDefinitions[1].ActualWidth
                : expanded.Width * 0.28;
            var bottomRightX = ExpandedBottomContent.ColumnDefinitions.Count >= 4
                ? bottomMiddleX
                  + ExpandedBottomContent.ColumnDefinitions[2].ActualWidth
                  + ExpandedBottomContent.ColumnDefinitions[3].ActualWidth
                : expanded.Width * 0.62;

            return new MotionCompositionContent(
                compact,
                expanded,
                topRightX,
                bottomPoint.Y,
                bottomMiddleX,
                bottomRightX);
        }
        finally
        {
            CompactBar.Clip = compactClip;
            ExpandedPanel.Clip = expandedClip;
            ContentHost.Clip = surfaceContentClip;
            CompactTranslate.Y = compactTranslateY;
            ExpandedTranslate.Y = expandedTranslateY;
            ExpandedTopTranslate.Y = expandedTopTranslateY;
            ExpandedBottomTranslate.Y = expandedBottomTranslateY;
            ContentHostTranslate.Y = contentHostTranslateY;
            ContentHost.Opacity = contentHostOpacity;
            CompactBar.Visibility = compactVisibility;
            ExpandedPanel.Visibility = expandedVisibility;
            SurfacePath.Visibility = surfaceVisibility;
            ShadowOuterPath.Visibility = outerShadowVisibility;
            ShadowInnerPath.Visibility = innerShadowVisibility;
            BackdropSnapshotLayer.Visibility = backdropVisibility;
            CompactBar.Opacity = compactOpacity;
            ExpandedPanel.Opacity = expandedOpacity;
        }
    }

    private MotionCompositionBitmap? CaptureCompositionBitmapFromRoot(
        FrameworkElement element,
        double x,
        double y,
        double scale)
    {
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        var rootWidth = Root.ActualWidth;
        var rootHeight = Root.ActualHeight;
        if (width <= 1 || height <= 1 || rootWidth <= 1 || rootHeight <= 1) return null;

        scale = Math.Clamp(scale, 1.0, 3.0);
        var rootPixelWidth = Math.Max(1, (int)Math.Ceiling(rootWidth * scale));
        var rootPixelHeight = Math.Max(1, (int)Math.Ceiling(rootHeight * scale));
        var rootBitmap = new RenderTargetBitmap(
            rootPixelWidth,
            rootPixelHeight,
            96.0 * scale,
            96.0 * scale,
            PixelFormats.Pbgra32);
        rootBitmap.Render(Root);

        var left = Math.Clamp((int)Math.Floor(x * scale), 0, rootPixelWidth - 1);
        var top = Math.Clamp((int)Math.Floor(y * scale), 0, rootPixelHeight - 1);
        var pixelWidth = Math.Clamp((int)Math.Ceiling(width * scale), 1, rootPixelWidth - left);
        var pixelHeight = Math.Clamp((int)Math.Ceiling(height * scale), 1, rootPixelHeight - top);
        var crop = new CroppedBitmap(rootBitmap, new Int32Rect(left, top, pixelWidth, pixelHeight));

        var stride = checked(pixelWidth * 4);
        var pixels = new byte[checked(stride * pixelHeight)];
        crop.CopyPixels(pixels, stride, 0);
        return new MotionCompositionBitmap(pixels, pixelWidth, pixelHeight, stride, x, y, width, height);
    }

    private MotionCompositionBackdrop? CaptureMotionCompositionBackdrop()
    {
        if (_currentMonitor is null
            || _hwnd == IntPtr.Zero
            || _settings.Material is not (SurfaceMaterial.Acrylic or SurfaceMaterial.Glass))
        {
            return null;
        }

        // Use the proven Skia snapshot here. The blur is prepared once before the
        // short DirectComposition morph; the 120 Hz hot path only samples this
        // already-blurred bitmap. This avoids device-context/alpha ambiguity that
        // could turn the motion backdrop black on some drivers.
        var source = _motionBackdropSnapshotService.Capture(
            _hwnd,
            _settings.Material,
            _themeService.ResolveLight(_settings.Theme));
        if (source is null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return null;
        }

        var stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        var bitmap = new MotionCompositionBitmap(
            pixels,
            source.PixelWidth,
            source.PixelHeight,
            stride,
            0,
            0,
            Math.Max(1, _motionHostWidth),
            Math.Max(1, _motionHostHeight));
        return new MotionCompositionBackdrop(bitmap, 0, Colors.Transparent);
    }
    private void StartCompositionSurfaceTransition()
    {
        _compositionHandoffTimer.Stop();
        if (_currentMonitor is null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var fill = (SurfacePath.Fill as SolidColorBrush)?.Color ?? Colors.Black;
        var border = (SurfacePath.Stroke as SolidColorBrush)?.Color ?? Colors.Transparent;
        var from = new MotionCompositionFrame(
            _windowFromWidth,
            _windowFromHeight,
            _windowFromTopDip,
            _windowFromShapeProgress,
            _windowFromRevealProgress,
            _windowFromSurfaceOpacity,
            _windowFromShadowOpacity);
        var to = new MotionCompositionFrame(
            _windowToWidth,
            _windowToHeight,
            _windowToTopDip,
            _windowToShapeProgress,
            _windowToRevealProgress,
            _windowToSurfaceOpacity,
            _windowToShadowOpacity);
        var content = CaptureMotionCompositionContent();
        var backdrop = CaptureMotionCompositionBackdrop();
        _windowTransitionStartedTimestamp = Stopwatch.GetTimestamp();
        _windowTransitionActive = true;
        var compositionDuration = _windowTransitionDurationMs;
        _motionCompositionHost.Start(new MotionCompositionRequest(
            _currentMonitor,
            _settings.Style,
            Math.Max(1, _motionHostWidth),
            Math.Max(1, _motionHostHeight),
            _motionHostTopDip,
            from,
            to,
            compositionDuration,
            content,
            backdrop,
            fill,
            border,
            () =>
            {
                _compositionSurfaceActive = true;
                _compositionBackdropActive = backdrop is not null;
                if (_compositionBackdropActive)
                {
                    BlurHostService.SetMotionSnapshotActive(_hwnd, true);
                }
                if (_hwnd != IntPtr.Zero)
                {
                    _ = ShowWindow(_hwnd, 0);
                }
            },
            CompleteCompositionSurfaceTransition,
            FallbackCompositionSurfaceTransition));
    }

    private void CompleteCompositionSurfaceTransition()
    {
        if (_currentMonitor is null || _hwnd == IntPtr.Zero)
        {
            _compositionSurfaceActive = false;
            _motionCompositionHost.Hide();
            _windowTransitionActive = false;
            ResumeMotionSensitivePolling();
            return;
        }

        // WPF is hidden while these endpoint values are reconciled, so no layered
        // bitmap is presented during the expensive morph. The final live UI is
        // shown only after every property has landed on the same endpoint frame.
        ApplySurfaceMotionFrame(1.0);
        _renderedWidth = _windowToWidth;
        _renderedHeight = _windowToHeight;
        _currentTopDip = _windowToTopDip;
        _shapeExpansionProgress = _windowToShapeProgress;
        _edgeRevealProgress = _windowToRevealProgress;
        _windowTransitionActive = false;
        UpdateGeometry(_windowToWidth, _windowToHeight);
        ReconcileContentWithState();
        UpdateCompactDensity();

        if (_compositionBackdropActive)
        {
            BlurHostService.SetMotionSnapshotActive(_hwnd, false);
        }
        ApplyNativeRevealEndpointVisibility();

        // Keep the exact final GPU frame above WPF briefly while the settled
        // layered surface/backdrop catches up. This is a hold, never a fade.
        _compositionHandoffTimer.Stop();
        _compositionHandoffTimer.Interval = TimeSpan.FromMilliseconds(
            _compositionBackdropActive ? 34 : 18);
        _compositionHandoffTimer.Start();

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

    private void CompositionHandoffTimer_Tick(object? sender, EventArgs e)
    {
        _compositionHandoffTimer.Stop();
        _motionCompositionHost.Hide();
        _compositionSurfaceActive = false;
        _compositionBackdropActive = false;
        if (_edgeRevealVisualHidden)
        {
            Dispatcher.BeginInvoke(RefreshMotionCompositionContentCache, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void FallbackCompositionSurfaceTransition()
    {
        _compositionHandoffTimer.Stop();
        if (_hwnd == IntPtr.Zero || _currentMonitor is null)
        {
            _compositionSurfaceActive = false;
            _compositionBackdropActive = false;
            _windowTransitionActive = false;
            ResumeMotionSensitivePolling();
            return;
        }

        if (_compositionSurfaceActive && _motionCompositionHost.TryGetCurrentFrame(out var frame))
        {
            _renderedWidth = frame.Width;
            _renderedHeight = frame.Height;
            _currentTopDip = frame.Top;
            _shapeExpansionProgress = frame.ShapeProgress;
            _edgeRevealProgress = frame.RevealProgress;
            _renderedShadowOpacity = frame.ShadowOpacity;
            SurfacePath.Opacity = frame.SurfaceOpacity;
            ContentHost.Opacity = frame.SurfaceOpacity;
            ApplyContentMotionFrame();
            ApplySurfaceShadowOpacity(frame.ShadowOpacity);
            var offset = ResolveSurfaceOffset(frame.Width, frame.Top);
            ContentHostTranslate.Y = offset.Y;
            UpdateGeometry(frame.Width, frame.Height);

            _windowFromWidth = frame.Width;
            _windowFromHeight = frame.Height;
            _windowFromTopDip = frame.Top;
            _windowFromShapeProgress = frame.ShapeProgress;
            _windowFromRevealProgress = frame.RevealProgress;
            _windowFromSurfaceOpacity = frame.SurfaceOpacity;
            _windowFromShadowOpacity = frame.ShadowOpacity;
        }

        if (_compositionBackdropActive)
        {
            BlurHostService.SetMotionSnapshotActive(_hwnd, false);
        }
        ApplyNativeRevealEndpointVisibility();
        _motionCompositionHost.Hide();
        _compositionSurfaceActive = false;
        _compositionBackdropActive = false;
        _windowTransitionActive = false;

        // The fallback is deliberately shorter than a fresh transition because
        // the compositor may already have covered part of the distance.
        _windowTransitionDurationMs = Math.Max(80, _windowTransitionDurationMs / 2);
        StartLegacySurfaceTransition();
    }

    private void WindowTransition_Rendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastMotionRenderingTime)
            {
                return;
            }
            _lastMotionRenderingTime = rendering.RenderingTime;
        }
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

        var liveBlurActive = _externalBlurAvailable
                             && _settings.Material is SurfaceMaterial.Acrylic or SurfaceMaterial.Glass;
        if (liveBlurActive)
        {
            var blurRevealProgress = _settings.Style == IslandStyle.Notch
                ? _edgeRevealProgress
                : surfaceOpacity;
            BlurHostService.SetRevealProgress(_hwnd, blurRevealProgress);
            BlurHostService.SetShapeProgress(_hwnd, _shapeExpansionProgress);
        }

        // Content motion is geometry/translation only. Never interpolate opacity.
        ApplyContentMotionFrame();

        _renderedShadowOpacity = Lerp(_windowFromShadowOpacity, _windowToShadowOpacity, amount);
        ApplySurfaceShadowOpacity(_renderedShadowOpacity);

        var surfaceOffset = ResolveSurfaceOffset(width, _currentTopDip);
        ContentHostTranslate.Y = surfaceOffset.Y;
        if (_motionBackdropActive)
        {
            var expandedLayout = ResolveLayout(SurfaceState.Expanded);
            BackdropSnapshotScale.ScaleX = Math.Clamp(width / Math.Max(1, expandedLayout.Width), 0.01, 1.0);
            BackdropSnapshotScale.ScaleY = Math.Clamp(height / Math.Max(1, expandedLayout.Height), 0.01, 1.0);
            BackdropSnapshotTranslate.Y = _settings.Style == IslandStyle.Notch
                ? Math.Max(0, surfaceOffset.Y)
                : _currentTopDip - expandedLayout.Top;
        }
        if (liveBlurActive)
        {
            BlurHostService.SetSurfaceBounds(_hwnd, width, height, surfaceOffset.X, surfaceOffset.Y);
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

        var finalOffset = ResolveSurfaceOffset(_windowToWidth, _currentTopDip);
        if (_externalBlurAvailable && _settings.Material is SurfaceMaterial.Acrylic or SurfaceMaterial.Glass)
        {
            BlurHostService.SetSurfaceBounds(_hwnd, _windowToWidth, _windowToHeight, finalOffset.X, finalOffset.Y);
        }
        UpdateGeometry(_windowToWidth, _windowToHeight);
        ReconcileContentWithState();
        UpdateCompactDensity();
        BeginMotionBackdropHandoff();
        ApplyNativeRevealEndpointVisibility();

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
        CompositionTarget.Rendering -= WindowTransition_Rendering;
        _windowTransitionActive = false;
    }

    private void ApplySurfaceShadowOpacity(double opacity)
    {
        // Preserve a soft depth cue without DropShadowEffect. The latter forces a
        // full effect re-rasterization whenever the shell geometry changes.
        ShadowOuterPath.Opacity = Math.Clamp(opacity * 0.32, 0.0, 0.18);
        ShadowInnerPath.Opacity = Math.Clamp(opacity * 0.54, 0.0, 0.24);
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;

    private static IEasingFunction CreateMotionEasing() => MotionProfile.CreateWpfEasing();

    private void ApplyNativeRevealEndpointVisibility()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_edgeRevealVisualHidden)
        {
            _ = ShowWindow(_hwnd, 0);
            return;
        }

        // Do not resurrect a window that the user explicitly hid from the tray.
        if (Visibility == Visibility.Visible)
        {
            _ = ShowWindow(_hwnd, 4);
        }
    }

    private void UpdateSurfaceContentClip(double width, double height, double offsetX, double offsetY)
    {
        var pad = IslandGeometryFactory.ShadowPadding;
        if (_settings.Style == IslandStyle.Notch)
        {
            var visibleHeight = Math.Max(0, (height - pad) * Math.Clamp(_edgeRevealProgress, 0, 1));
            _surfaceContentClip.Rect = visibleHeight <= 0.01
                ? Rect.Empty
                : new Rect(
                    offsetX + pad,
                    offsetY,
                    Math.Max(0, width - pad * 2),
                    visibleHeight);
            return;
        }

        _surfaceContentClip.Rect = new Rect(
            offsetX + pad,
            offsetY + pad,
            Math.Max(0, width - pad * 2),
            Math.Max(0, height - pad * 2));
    }

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
        var geometryWidth = widthOverride ?? (_renderedWidth > 1 ? _renderedWidth : ResolveLayout(_state).Width);
        var geometryHeight = heightOverride ?? (_renderedHeight > 1 ? _renderedHeight : ResolveLayout(_state).Height);
        if (geometryWidth <= 1 || geometryHeight <= 1)
        {
            return;
        }

        var offset = ResolveSurfaceOffset(geometryWidth, _currentTopDip);
        var geometry = _runtimeGeometry.Update(
            _settings.Style,
            new Size(geometryWidth, geometryHeight),
            _shapeExpansionProgress,
            _settings.Style == IslandStyle.Notch ? _edgeRevealProgress : 1.0,
            offset.X,
            offset.Y);

        if (!ReferenceEquals(SurfacePath.Data, geometry))
        {
            SurfacePath.Data = geometry;
            ShadowOuterPath.Data = geometry;
            ShadowInnerPath.Data = geometry;
        }

        UpdateSurfaceContentClip(geometryWidth, geometryHeight, offset.X, offset.Y);

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
