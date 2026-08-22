using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TopIsland.Controls;
using TopIsland.Interop;
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
        // 40 ms pointer tracker validates the actual screen-space cursor first.
        _peekTimer.Stop();
    }

    private void PointerTimer_Tick(object? sender, EventArgs e)
    {
        var inside = IsCursorInsideSurface();
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
            : TimeSpan.FromMilliseconds(55);
        _collapseTimer.Start();
    }

    private void PeekTimer_Tick(object? sender, EventArgs e)
    {
        _peekTimer.Stop();
        if (_state != SurfaceState.Hover || !IsCursorInsideSurface())
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

        if (IsCursorInsideSurface())
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

    private void CompactBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _peekTimer.Stop();
        _state = _state == SurfaceState.Expanded ? SurfaceState.Idle : SurfaceState.Expanded;
        ApplyState();
    }

    private void ApplyState(bool immediate = false)
    {
        var transitionSerial = ++_visualTransitionSerial;
        var target = ResolveLayout(_state);
        var duration = immediate ? 0 : _state switch
        {
            SurfaceState.Expanded => 200,
            SurfaceState.Idle => 165,
            SurfaceState.Peek => 130,
            _ => 95
        };

        UpdateSecondaryPollingState();

        if (_state == SurfaceState.Expanded)
        {
            if (immediate)
            {
                CompactBar.Visibility = Visibility.Hidden;
                CompactBar.Opacity = 0;
                ExpandedTranslate.Y = 0;
            }
            else
            {
                AnimateOpacity(CompactBar, 0, 50, 0, hideOnComplete: true);
                ExpandedTranslate.Y = -4;
            }

            ExpandedPanel.Visibility = Visibility.Visible;
            AnimateOpacity(ExpandedPanel, 1, immediate ? 0 : 135, immediate ? 0 : 20);
            AnimateTranslate(ExpandedTranslate, 0, immediate ? 0 : 150, immediate ? 0 : 12);
        }
        else
        {
            if (immediate || _shapeExpansionProgress < 0.35)
            {
                ApplyCompactContent(immediate);
            }
            else
            {
                // Keep expanded content while the large shell begins to contract.
                // This prevents a large window with tiny compact content if a native
                // resize is interrupted. The serial makes stale delayed swaps harmless.
                _ = SwapToCompactContentAfterDelayAsync(transitionSerial, Math.Max(40, (int)(duration * 0.34)));
            }
        }

        SurfacePath.SetResourceReference(System.Windows.Shapes.Path.FillProperty,
            _state is SurfaceState.Hover or SurfaceState.Peek ? "SurfaceHoverBrush" : "SurfaceBrush");

        var shadowOpacity = _state switch
        {
            SurfaceState.Idle => _settings.Style == IslandStyle.Notch ? 0.0 : 0.16,
            SurfaceState.Expanded => _settings.Style == IslandStyle.Notch ? 0.34 : 0.30,
            _ => _settings.Style == IslandStyle.Notch ? 0.26 : 0.24
        };
        var shadowBlur = _state switch
        {
            SurfaceState.Idle => 8.0,
            SurfaceState.Expanded => 12.0,
            _ => 10.0
        };
        AnimateShadow(shadowBlur, shadowOpacity, immediate);

        ConfigureContentMargins();
        UpdateCompactDensity();
        AnimateWindow(target.Width, target.Height, target.Top, duration);
        UpdateGeometry();
    }

    private void ApplyCompactContent(bool immediate)
    {
        AnimateOpacity(ExpandedPanel, 0, immediate ? 0 : 65, 0, hideOnComplete: true);
        AnimateTranslate(ExpandedTranslate, -4, immediate ? 0 : 75, 0);
        CompactBar.Visibility = Visibility.Visible;
        if (immediate)
        {
            CompactBar.BeginAnimation(OpacityProperty, null);
            CompactBar.Opacity = 1;
        }
        else
        {
            AnimateOpacity(CompactBar, 1, 90, 0);
        }
    }

    private async Task SwapToCompactContentAfterDelayAsync(long transitionSerial, int delayMs)
    {
        await Task.Delay(delayMs);
        if (transitionSerial != _visualTransitionSerial || _state == SurfaceState.Expanded)
        {
            return;
        }
        await Dispatcher.InvokeAsync(() => ApplyCompactContent(immediate: false));
    }

    private void TransitionGuardTimer_Tick(object? sender, EventArgs e)
    {
        if (_hwnd == IntPtr.Zero || _currentMonitor is null)
        {
            return;
        }

        if (_windowTransitionActive)
        {
            var elapsed = (DateTime.UtcNow - _windowTransitionStartedAt).TotalMilliseconds;
            if (elapsed > _windowTransitionDurationMs + 250)
            {
                CompleteWindowTransition();
            }
            return;
        }

        var target = ResolveLayout(_state);
        var actualWidth = ActualWidth > 1 ? ActualWidth : Width;
        var actualHeight = ActualHeight > 1 ? ActualHeight : Height;
        if (Math.Abs(actualWidth - target.Width) > 1.5 || Math.Abs(actualHeight - target.Height) > 1.5)
        {
            // A native resize should never leave a half-transition as a stable state.
            _targetTopDip = target.Top;
            _currentTopDip = target.Top;
            _shapeExpansionProgress = _state == SurfaceState.Expanded ? 1 : 0;
            Width = target.Width;
            Height = target.Height;
            _monitorService.PositionAndSizeWindow(_hwnd, _currentMonitor, target.Top, target.Width, target.Height);
            UpdateGeometry();
        }

        ReconcileContentWithState();
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

    private void AnimateWindow(double targetWidth, double targetHeight, double targetTop, int durationMs)
    {
        var targetShapeProgress = _state == SurfaceState.Expanded ? 1.0 : 0.0;
        _targetTopDip = targetTop;

        var renderedState = CaptureRenderedWindowState();
        StopWindowTransition();

        if (durationMs <= 0 || _hwnd == IntPtr.Zero || _currentMonitor is null)
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            _shapeExpansionProgress = targetShapeProgress;
            _currentTopDip = targetTop;
            Width = targetWidth;
            Height = targetHeight;
            if (_hwnd != IntPtr.Zero && _currentMonitor is not null)
            {
                _monitorService.PositionAndSizeWindow(_hwnd, _currentMonitor, targetTop, targetWidth, targetHeight);
            }
            UpdateGeometry();
            return;
        }

        _windowFromWidth = renderedState.Width;
        _windowFromHeight = renderedState.Height;
        _windowFromTopDip = renderedState.Top;
        _windowFromShapeProgress = renderedState.ShapeProgress;
        _windowToWidth = targetWidth;
        _windowToHeight = targetHeight;
        _windowToTopDip = targetTop;
        _windowToShapeProgress = targetShapeProgress;
        // Short reversals should not restart a full-length animation. Scale the
        // duration by the remaining distance while keeping normal transitions
        // comfortably readable.
        var widthDelta = _windowToWidth - _windowFromWidth;
        var heightDelta = _windowToHeight - _windowFromHeight;
        _windowTransitionDurationMs = MotionProfile.ScaleDuration(durationMs, widthDelta, heightDelta);
        _windowTransitionStartedAt = DateTime.UtcNow;
        if (_backdropResult.NativeApplied && _settings.Material != SurfaceMaterial.Solid)
        {
            _windowRegionService.Reset(this);
        }
        _windowTransitionActive = true;
        CompositionTarget.Rendering += WindowTransition_Rendering;
    }

    private (double Width, double Height, double Top, double ShapeProgress) CaptureRenderedWindowState()
    {
        if (!_windowTransitionActive || _windowTransitionDurationMs <= 0)
        {
            return (
                ActualWidth > 1 ? ActualWidth : Width,
                ActualHeight > 1 ? ActualHeight : Height,
                _currentTopDip,
                _shapeExpansionProgress);
        }

        var elapsedMs = (DateTime.UtcNow - _windowTransitionStartedAt).TotalMilliseconds;
        var progress = Math.Clamp(elapsedMs / _windowTransitionDurationMs, 0, 1);
        var eased = MotionProfile.Ease(progress);
        return (
            Lerp(_windowFromWidth, _windowToWidth, eased),
            Lerp(_windowFromHeight, _windowToHeight, eased),
            Lerp(_windowFromTopDip, _windowToTopDip, eased),
            Lerp(_windowFromShapeProgress, _windowToShapeProgress, eased));
    }

    private void WindowTransition_Rendering(object? sender, EventArgs e)
    {
        if (!_windowTransitionActive || _currentMonitor is null || _hwnd == IntPtr.Zero)
        {
            StopWindowTransition();
            return;
        }

        var elapsedMs = (DateTime.UtcNow - _windowTransitionStartedAt).TotalMilliseconds;
        var t = Math.Clamp(elapsedMs / _windowTransitionDurationMs, 0, 1);
        // The shell and WPF content share the same responsive ease-out curve.
        // This avoids the sluggish first half of symmetric ease-in-out motion.
        var eased = MotionProfile.Ease(t);
        var width = Lerp(_windowFromWidth, _windowToWidth, eased);
        var height = Lerp(_windowFromHeight, _windowToHeight, eased);
        _currentTopDip = Lerp(_windowFromTopDip, _windowToTopDip, eased);
        _shapeExpansionProgress = Lerp(_windowFromShapeProgress, _windowToShapeProgress, eased);

        _monitorService.PositionAndSizeWindow(_hwnd, _currentMonitor, _currentTopDip, width, height);

        if (t < 1)
        {
            return;
        }

        CompleteWindowTransition();
    }

    private void CompleteWindowTransition()
    {
        if (_currentMonitor is null || _hwnd == IntPtr.Zero)
        {
            StopWindowTransition();
            return;
        }

        StopWindowTransition();
        _currentTopDip = _windowToTopDip;
        _shapeExpansionProgress = _windowToShapeProgress;
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        Width = _windowToWidth;
        Height = _windowToHeight;
        _monitorService.PositionAndSizeWindow(_hwnd, _currentMonitor, _currentTopDip, _windowToWidth, _windowToHeight);
        UpdateGeometry();
        ReconcileContentWithState();
    }

    private void StopWindowTransition()
    {
        if (!_windowTransitionActive)
        {
            return;
        }

        CompositionTarget.Rendering -= WindowTransition_Rendering;
        _windowTransitionActive = false;
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;

    private static IEasingFunction CreateMotionEasing() => MotionProfile.CreateWpfEasing();

    private static void AnimateOpacity(UIElement element, double to, int durationMs, int delayMs, bool hideOnComplete = false)
    {
        var from = element.Opacity;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = from;
        if (durationMs <= 0)
        {
            element.Opacity = to;
            if (hideOnComplete && to <= 0) element.Visibility = Visibility.Hidden;
            return;
        }

        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = CreateMotionEasing(),
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = to;
            if (hideOnComplete && to <= 0.01) element.Visibility = Visibility.Hidden;
        };
        element.BeginAnimation(OpacityProperty, animation);
    }

    private static void AnimateTranslate(TranslateTransform transform, double to, int durationMs, int delayMs)
    {
        var from = transform.Y;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = from;
        if (durationMs <= 0)
        {
            transform.Y = to;
            return;
        }

        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = CreateMotionEasing(),
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = to;
        };
        transform.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private void AnimateShadow(double blur, double opacity, bool immediate)
    {
        var fromBlur = SurfaceShadow.BlurRadius;
        var fromOpacity = SurfaceShadow.Opacity;
        SurfaceShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        SurfaceShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        SurfaceShadow.BlurRadius = fromBlur;
        SurfaceShadow.Opacity = fromOpacity;

        if (immediate)
        {
            SurfaceShadow.BlurRadius = blur;
            SurfaceShadow.Opacity = opacity;
            return;
        }

        var blurAnimation = new DoubleAnimation(fromBlur, blur, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = CreateMotionEasing(),
            FillBehavior = FillBehavior.Stop
        };
        blurAnimation.Completed += (_, _) =>
        {
            SurfaceShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            SurfaceShadow.BlurRadius = blur;
        };

        var opacityAnimation = new DoubleAnimation(fromOpacity, opacity, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = CreateMotionEasing(),
            FillBehavior = FillBehavior.Stop
        };
        opacityAnimation.Completed += (_, _) =>
        {
            SurfaceShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            SurfaceShadow.Opacity = opacity;
        };

        SurfaceShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnimation);
        SurfaceShadow.BeginAnimation(DropShadowEffect.OpacityProperty, opacityAnimation);
    }

    private void ConfigureContentMargins()
    {
        if (_settings.Style == IslandStyle.Notch)
        {
            CompactBar.Margin = new Thickness(30, 2, 30, 0);
            ExpandedPanel.Margin = new Thickness(47, 20, 47, 20);
        }
        else
        {
            CompactBar.Margin = new Thickness(30, 18, 30, 0);
            ExpandedPanel.Margin = new Thickness(36);
        }
    }

    private void UpdateGeometry()
    {
        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var geometry = IslandGeometryFactory.Create(
            _settings.Style,
            new Size(ActualWidth, ActualHeight),
            _shapeExpansionProgress);
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
