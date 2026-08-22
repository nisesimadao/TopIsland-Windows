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
    private void ThemeTimer_Tick(object? sender, EventArgs e)
    {
        var wantsBlur = RequiresLiveBlur(_settings.Material);
        if (!wantsBlur && _blurHostService.IsRunning)
        {
            _blurHostService.Stop();
        }
        var blurAvailable = wantsBlur && _blurHostService.Start(_hwnd);
        if (blurAvailable != _externalBlurAvailable)
        {
            _externalBlurAvailable = blurAvailable;
            _themeService.Apply(_settings.Theme, _settings.Material, _externalBlurAvailable);
        }

        if (_settings.Theme != AppThemeMode.System)
        {
            return;
        }

        var current = _themeService.IsSystemLightTheme();
        if (current == _lastSystemLight)
        {
            return;
        }

        _lastSystemLight = current;
        _themeService.Apply(AppThemeMode.System, _settings.Material, _externalBlurAvailable);
        ApplyBackdropMaterial();
    }

    private void RefreshTargetMonitor(bool force)
    {
        MonitorDescriptor target;
        try
        {
            target = _monitorService.Resolve(_settings);
        }
        catch
        {
            return;
        }

        var changed = _currentMonitor is null
                      || _currentMonitor.Handle != target.Handle
                      || _currentMonitor.ScalePercent != target.ScalePercent
                      || _currentMonitor.PixelWidth != target.PixelWidth
                      || _currentMonitor.PixelHeight != target.PixelHeight;

        if (!force && !changed)
        {
            return;
        }

        if (!force && _settings.MonitorMode == MonitorMode.FollowActiveApp && IsLoaded && IsVisible)
        {
            TransitionToMonitor(target);
            return;
        }

        _currentMonitor = target;
        PositionOnCurrentMonitor();
        Dispatcher.BeginInvoke(() => ApplyState(immediate: true), DispatcherPriority.Loaded);
    }

    private void TransitionToMonitor(MonitorDescriptor target)
    {
        var transitionSerial = ++_monitorTransitionSerial;
        // Restart from the currently rendered opacity. A newer monitor request
        // invalidates every callback from the older transition, preventing a
        // stale fade completion from jumping the island back to an old display.
        var renderedOpacity = Root.Opacity;
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = renderedOpacity;
        var fadeOut = new DoubleAnimation(renderedOpacity, 0, TimeSpan.FromMilliseconds(75))
        {
            EasingFunction = CreateMotionEasing(),
            FillBehavior = FillBehavior.Stop
        };
        fadeOut.Completed += (_, _) =>
        {
            if (transitionSerial != _monitorTransitionSerial)
            {
                return;
            }

            Root.BeginAnimation(OpacityProperty, null);
            Root.Opacity = 0;
            _currentMonitor = target;
            PositionOnCurrentMonitor();
            ApplyState(immediate: true);

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = CreateMotionEasing(),
                FillBehavior = FillBehavior.Stop
            };
            fadeIn.Completed += (_, _) =>
            {
                if (transitionSerial != _monitorTransitionSerial)
                {
                    return;
                }

                Root.BeginAnimation(OpacityProperty, null);
                Root.Opacity = 1;
            };
            Root.BeginAnimation(OpacityProperty, fadeIn);
        };
        Root.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void PositionOnCurrentMonitor()
    {
        if (_hwnd == IntPtr.Zero || _currentMonitor is null || _windowTransitionActive)
        {
            return;
        }

        _currentTopDip = _targetTopDip;
        _monitorService.PositionWindow(_hwnd, _currentMonitor, _currentTopDip);
    }

    private void SetDynamicIsland_Click(object sender, RoutedEventArgs e) => SetStyle(IslandStyle.DynamicIsland);
    private void SetNotch_Click(object sender, RoutedEventArgs e) => SetStyle(IslandStyle.Notch);

    private void SetAuthenticWidth_Click(object sender, RoutedEventArgs e) => SetWidth(WidthPreset.Authentic);
    private void SetCompactWidth_Click(object sender, RoutedEventArgs e) => SetWidth(WidthPreset.Compact);
    private void SetStandardWidth_Click(object sender, RoutedEventArgs e) => SetWidth(WidthPreset.Standard);
    private void SetWideWidth_Click(object sender, RoutedEventArgs e) => SetWidth(WidthPreset.Wide);
    private void SetFullWidth_Click(object sender, RoutedEventArgs e) => SetWidth(WidthPreset.FullWidth);

    private void SetSolidMaterial_Click(object sender, RoutedEventArgs e) => SetMaterial(SurfaceMaterial.Solid);
    private void SetMicaMaterial_Click(object sender, RoutedEventArgs e) => SetMaterial(SurfaceMaterial.Mica);
    private void SetAcrylicMaterial_Click(object sender, RoutedEventArgs e) => SetMaterial(SurfaceMaterial.Acrylic);
    private void SetGlassMaterial_Click(object sender, RoutedEventArgs e) => SetMaterial(SurfaceMaterial.Glass);
    private void SetMaterialCopy_Click(object sender, RoutedEventArgs e) => SetMaterial(SurfaceMaterial.MaterialCopy);

    private void SetSystemTheme_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.System);
    private void SetDarkTheme_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.Dark);
    private void SetLightTheme_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.Light);
    private void ToggleTopEdgeReveal_Click(object sender, RoutedEventArgs e) =>
        SetTopEdgeReveal(!_settings.RevealOnTopEdge);

    public void SetTopEdgeReveal(bool enabled)
    {
        _settings.RevealOnTopEdge = enabled;
        SaveAndRefresh();
    }

    public void SetStyle(IslandStyle style)
    {
        _settings.Style = style;
        if (style == IslandStyle.Notch)
        {
            _settings.Material = SurfaceMaterial.Solid;
            _themeService.Apply(_settings.Theme, _settings.Material, _externalBlurAvailable);
            ApplyBackdropMaterial();
        }
        SaveAndRefresh();
    }

    public void SetWidth(WidthPreset width)
    {
        _settings.WidthPreset = width;
        SaveAndRefresh();
    }

    public void SetMaterial(SurfaceMaterial material)
    {
        _settings.Material = material;
        if (RequiresLiveBlur(material))
        {
            _externalBlurAvailable = _blurHostService.Start(_hwnd);
        }
        else
        {
            _blurHostService.Stop();
            _externalBlurAvailable = false;
        }
        _themeService.Apply(_settings.Theme, _settings.Material, _externalBlurAvailable);
        ApplyBackdropMaterial();
        SaveAndRefresh();
    }

    private static bool RequiresLiveBlur(SurfaceMaterial material) =>
        material is SurfaceMaterial.Acrylic or SurfaceMaterial.Glass;

    public void SetTheme(AppThemeMode theme)
    {
        _settings.Theme = theme;
        _themeService.Apply(_settings.Theme, _settings.Material, _externalBlurAvailable);
        ApplyBackdropMaterial();
        SaveAndRefresh();
    }

    public void SetMonitor(MonitorMode mode, string? deviceName)
    {
        _settings.MonitorMode = mode;
        _settings.MonitorDeviceName = mode == MonitorMode.Fixed ? deviceName : null;
        _settingsStore.Save(_settings);
        RefreshTargetMonitor(force: true);
        SyncSettingsUi();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetStartWithWindows(bool enabled)
    {
        _settings.StartWithWindows = enabled;
        _startupService.SetEnabled(enabled);
        _settingsStore.Save(_settings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleVisibilityFromTray()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            ApplyState(immediate: true);
            PositionOnCurrentMonitor();
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ExpandFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }
        _state = SurfaceState.Expanded;
        ApplyState();
    }

    private void FullSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Root.ContextMenu is null)
        {
            return;
        }

        Root.ContextMenu.PlacementTarget = FullSettingsButton;
        Root.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        Root.ContextMenu.IsOpen = true;
    }
    private void ExitButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void SaveAndRefresh()
    {
        _settingsStore.Save(_settings);
        SyncSettingsUi();
        ApplyState();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncSettingsUi()
    {
        DynamicIslandMenuItem.IsChecked = _settings.Style == IslandStyle.DynamicIsland;
        NotchMenuItem.IsChecked = _settings.Style == IslandStyle.Notch;

        AuthenticWidthMenuItem.IsChecked = _settings.WidthPreset == WidthPreset.Authentic;
        CompactWidthMenuItem.IsChecked = _settings.WidthPreset == WidthPreset.Compact;
        StandardWidthMenuItem.IsChecked = _settings.WidthPreset == WidthPreset.Standard;
        WideWidthMenuItem.IsChecked = _settings.WidthPreset == WidthPreset.Wide;
        FullWidthMenuItem.IsChecked = _settings.WidthPreset == WidthPreset.FullWidth;

        SolidMaterialMenuItem.IsChecked = _settings.Material == SurfaceMaterial.Solid;
        MicaMaterialMenuItem.IsChecked = _settings.Material == SurfaceMaterial.Mica;
        AcrylicMaterialMenuItem.IsChecked = _settings.Material == SurfaceMaterial.Acrylic;
        GlassMaterialMenuItem.IsChecked = _settings.Material == SurfaceMaterial.Glass;
        MaterialCopyMenuItem.IsChecked = _settings.Material == SurfaceMaterial.MaterialCopy;
        var materialYou = _settings.Material == SurfaceMaterial.MaterialCopy;
        MaterialYouTopContainer.Visibility = materialYou ? Visibility.Visible : Visibility.Collapsed;
        MaterialYouBottomContainer.Visibility = materialYou ? Visibility.Visible : Visibility.Collapsed;
        LightThemeMenuItem.IsChecked = _settings.Theme == AppThemeMode.Light;
        TopEdgeRevealMenuItem.IsChecked = _settings.RevealOnTopEdge;

        UpdateCompactDensity();
    }

    private void UpdateCompactDensity()
    {
        // During compact -> expanded cross-fade, changing visibility inside the
        // outgoing compact tree causes a re-measure/pop before its opacity has
        // reached zero. Keep that tree frozen until the coordinated motion ends.
        if (_windowTransitionActive && _state == SurfaceState.Expanded)
        {
            return;
        }

        var baseWidth = ResolveBaseSurfaceWidth(GetTargetScreenWidthDip());
        var hoverOrPeek = _state is SurfaceState.Hover or SurfaceState.Peek;
        var peek = _state == SurfaceState.Peek;

        CompactMediaText.Visibility = baseWidth >= 235 ? Visibility.Visible : Visibility.Collapsed;
        MediaSubtitleText.Visibility = baseWidth >= 520 || (peek && baseWidth >= 300)
            ? Visibility.Visible
            : Visibility.Collapsed;

        CompactTransportPanel.Visibility = _hasMediaSession && (baseWidth >= 500 || (peek && baseWidth >= 420))
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactDateText.Visibility = baseWidth >= 500 ? Visibility.Visible : Visibility.Collapsed;

        var compactSurface = _state != SurfaceState.Expanded;
        var showRamMeter = compactSurface && baseWidth >= 300;
        CompactMetersHost.Width = showRamMeter ? 88 : 43;
        CompactMetersHost.Margin = baseWidth < 300 ? new Thickness(3, 0, 0, 0) : new Thickness(6, 0, 0, 0);
        CompactMetersPanel.Visibility = compactSurface ? Visibility.Visible : Visibility.Collapsed;
        CompactCpuMeter.Visibility = compactSurface ? Visibility.Visible : Visibility.Collapsed;
        CompactRamMeter.Visibility = showRamMeter ? Visibility.Visible : Visibility.Collapsed;

        FullWidthInfoPanel.Visibility = baseWidth >= 1400 ? Visibility.Visible : Visibility.Collapsed;
        FullSettingsButton.Visibility = baseWidth >= 1400 ? Visibility.Visible : Visibility.Collapsed;

        var showIndicators = baseWidth >= 520 || (peek && baseWidth >= 420);
        CompactIndicatorsPanel.Visibility = showIndicators ? Visibility.Visible : Visibility.Collapsed;
        CompactNotificationIndicator.Visibility = showIndicators && _notificationCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactDownloadIndicator.Visibility = showIndicators && _activeDownloadCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        var showContextualActivity = baseWidth < 1400 && hoverOrPeek && (baseWidth >= 520 || (peek && baseWidth >= 480));
        var showFocusControl = showContextualActivity && _focusIsRunning;
        CompactFocusGroup.Visibility = showFocusControl ? Visibility.Visible : Visibility.Collapsed;
        CompactActivityText.Visibility = showContextualActivity
            && !showFocusControl
            && !string.IsNullOrWhiteSpace(CompactActivityText.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyBackdropMaterial()
    {
        _backdropResult = _backdropService.Apply(this, _settings.Material, _settings.Theme, _themeService);
        UpdateGeometry();
    }
}
