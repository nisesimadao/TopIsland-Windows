using System.Globalization;
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
    private void StatsTimer_Tick(object? sender, EventArgs e)
    {
        UpdateLiveData();
        RefreshTargetMonitor(force: false);
        UpdateSecondaryPollingState();
    }

    private bool NeedsNotificationPolling()
    {
        if (!IsVisible) return false;
        var baseWidth = ResolveBaseSurfaceWidth(GetTargetScreenWidthDip());
        return _state == SurfaceState.Expanded
               || baseWidth >= 500
               || (_state == SurfaceState.Peek && baseWidth >= 360);
    }

    private bool NeedsDiscordPolling()
    {
        if (!IsVisible) return false;
        var baseWidth = ResolveBaseSurfaceWidth(GetTargetScreenWidthDip());
        return _state is SurfaceState.Hover or SurfaceState.Peek or SurfaceState.Expanded
               || baseWidth >= 1400;
    }

    private void UpdateSecondaryPollingState(bool refreshImmediately = false)
    {
        var needNotifications = NeedsNotificationPolling();
        var needDiscord = NeedsDiscordPolling();

        var startNotifications = needNotifications && !_notificationTimer.IsEnabled;
        var startDiscord = needDiscord && !_discordTimer.IsEnabled;

        if (needNotifications) _notificationTimer.Start(); else _notificationTimer.Stop();
        if (needDiscord) _discordTimer.Start(); else _discordTimer.Stop();

        if (refreshImmediately || startNotifications || startDiscord)
        {
            _ = RefreshSecondaryLiveDataAsync(needNotifications, needDiscord);
        }
    }

    private async Task RefreshSecondaryLiveDataAsync(bool refreshNotifications, bool refreshDiscord)
    {
        var tasks = new List<Task>(2);
        if (refreshNotifications) tasks.Add(RefreshNotificationsAsync(waitForTurn: false));
        if (refreshDiscord) tasks.Add(RefreshDiscordVoiceAsync(waitForTurn: false));
        if (tasks.Count > 0) await Task.WhenAll(tasks);
        UpdateLiveData();
    }

    private async void MediaTimer_Tick(object? sender, EventArgs e) => await RefreshMediaAsync(waitForTurn: false);

    private async void NotificationTimer_Tick(object? sender, EventArgs e) => await RefreshNotificationsAsync(waitForTurn: false);

    private async void DiscordTimer_Tick(object? sender, EventArgs e) => await RefreshDiscordVoiceAsync(waitForTurn: false);

    private async Task RefreshNotificationsAsync(bool waitForTurn = true)
    {
        if (!await EnterRefreshGateAsync(_notificationRefreshGate, waitForTurn))
        {
            return;
        }

        try
        {
            var snapshot = await (_notificationService ??= new NotificationService()).SampleAsync();
            var hasVisibleNotifications = snapshot.AccessAllowed && snapshot.HasNotifications;
            _notificationCount = snapshot.AccessAllowed ? snapshot.Count : 0;
            NotificationColumn.MinWidth = hasVisibleNotifications ? 260 : 0;
            NotificationColumn.Width = hasVisibleNotifications ? new GridLength(1.0, GridUnitType.Star) : new GridLength(0);
            NotificationSeparatorColumn.Width = hasVisibleNotifications ? new GridLength(1) : new GridLength(0);
            NotificationStripGroup.Visibility = hasVisibleNotifications ? Visibility.Visible : Visibility.Collapsed;
            NotificationSeparator.Visibility = hasVisibleNotifications ? Visibility.Visible : Visibility.Collapsed;
            CompactNotificationText.Text = _notificationCount.ToString();

            NotificationItem2Group.Visibility = Visibility.Collapsed;
            NotificationItem3Group.Visibility = Visibility.Collapsed;
            NotificationAge1Text.Text = string.Empty;
            NotificationAge2Text.Text = string.Empty;
            NotificationAge3Text.Text = string.Empty;

            if (!snapshot.AccessAllowed)
            {
                NotificationPrimaryText.Text = string.Empty;
                NotificationDetailText.Text = string.Empty;
                return;
            }

            if (!hasVisibleNotifications)
            {
                NotificationPrimaryText.Text = "No notifications";
                NotificationDetailText.Text = string.Empty;
                return;
            }

            var first = snapshot.Items[0];
            NotificationPrimaryText.Text = first.AppName;
            NotificationDetailText.Text = first.Text;
            NotificationAge1Text.Text = FormatNotificationAge(first.CreatedAt);

            if (snapshot.Items.Count > 1)
            {
                var second = snapshot.Items[1];
                NotificationItem2Group.Visibility = Visibility.Visible;
                NotificationPrimary2Text.Text = $"{second.AppName} \u00B7 {second.Text}";
                NotificationAge2Text.Text = FormatNotificationAge(second.CreatedAt);
            }
            if (snapshot.Items.Count > 2)
            {
                var third = snapshot.Items[2];
                NotificationItem3Group.Visibility = Visibility.Visible;
                NotificationPrimary3Text.Text = $"{third.AppName} \u00B7 {third.Text}";
                NotificationAge3Text.Text = FormatNotificationAge(third.CreatedAt);
            }
        }
        finally
        {
            _notificationRefreshGate.Release();
        }
    }

    private static string FormatNotificationAge(DateTimeOffset createdAt)
    {
        var age = DateTimeOffset.Now - createdAt;
        if (age.TotalMinutes < 1) return "now";
        if (age.TotalHours < 1) return $"{Math.Max(1, (int)age.TotalMinutes)}m";
        if (age.TotalDays < 1) return $"{Math.Max(1, (int)age.TotalHours)}h";
        return $"{Math.Max(1, (int)age.TotalDays)}d";
    }

    private async Task RefreshDiscordVoiceAsync(bool waitForTurn = true)
    {
        if (!await EnterRefreshGateAsync(_discordRefreshGate, waitForTurn))
        {
            return;
        }

        try
        {
            var service = _discordVoiceService ??= new DiscordVoiceService();
            await service.RefreshAsync();
            var voice = service.Current;

            if (!voice.IsRunning)
            {
                UpdateDiscordColumnVisibility(false);
                DiscordVoiceStateText.Text = "Discord";
                DiscordVoiceChannelText.Text = "Not running";
                DiscordVoiceDetailText.Text = "Discord is not open";
                DiscordVoiceStatusStrip.Visibility = Visibility.Collapsed;
                DiscordVoiceControlsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (!voice.IsConnected)
            {
                UpdateDiscordColumnVisibility(false);
                DiscordVoiceStateText.Text = "Discord";
                DiscordVoiceChannelText.Text = "Not in voice";
                DiscordVoiceDetailText.Text = string.IsNullOrWhiteSpace(voice.ServerName)
                    ? "No active voice connection"
                    : voice.ServerName;
                DiscordVoiceStatusStrip.Visibility = Visibility.Collapsed;
                DiscordVoiceControlsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateDiscordColumnVisibility(true);
            DiscordVoiceStateText.Text = "Discord";
            var channel = string.IsNullOrWhiteSpace(voice.ChannelName) ? "Voice channel" : voice.ChannelName;
            DiscordVoiceChannelText.Text = voice.IsMuted
                ? $"{channel} \u00B7 muted"
                : $"{channel} \u00B7 voice connected";
            DiscordVoiceChannelText.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty,
                "AccentBrush");

            var participantText = voice.ParticipantCount > 0
                ? $"{voice.ParticipantCount} in call"
                : "In call";
            // Keep the always-visible voice summary privacy-safe. Exact participant
            // and server names remain available to the service for future opt-in/detail UI,
            // but the surface shows only the observed participant count.
            DiscordVoiceDetailText.Text = participantText;
            DiscordVoiceStatusText.Text = voice.IsDeafened
                ? "Mic muted \u00B7 Audio muted"
                : voice.IsMuted
                    ? "Mic muted \u00B7 Audio on"
                    : "Mic live \u00B7 Audio on";
            DiscordVoiceStatusStrip.Visibility = Visibility.Visible;
            DiscordVoiceControlsPanel.Visibility = Visibility.Visible;
            DiscordMuteButton.IsEnabled = voice.CanToggleMute;
            DiscordDeafenButton.IsEnabled = voice.CanToggleDeafen;
            DiscordDisconnectButton.IsEnabled = voice.CanDisconnect;
            DiscordMuteButton.ToolTip = voice.IsMuted ? "Unmute microphone" : "Mute microphone";
            DiscordDeafenButton.ToolTip = voice.IsDeafened ? "Unmute speakers" : "Mute speakers";

            if (voice.IsMuted)
            {
                DiscordMuteButton.SetResourceReference(
                    System.Windows.Controls.Control.BackgroundProperty,
                    "SecondaryContainerBrush");
            }
            else
            {
                DiscordMuteButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            }

            if (voice.IsDeafened)
            {
                DiscordDeafenButton.SetResourceReference(
                    System.Windows.Controls.Control.BackgroundProperty,
                    "SecondaryContainerBrush");
            }
            else
            {
                DiscordDeafenButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            }
        }
        finally
        {
            _discordRefreshGate.Release();
        }
    }

    private async void DiscordMuteButton_Click(object sender, RoutedEventArgs e)
    {
        DiscordMuteButton.IsEnabled = false;
        _ = await (_discordVoiceService ??= new DiscordVoiceService()).ToggleMuteAsync();
        await RefreshDiscordVoiceAsync();
    }

    private async void DiscordDeafenButton_Click(object sender, RoutedEventArgs e)
    {
        DiscordDeafenButton.IsEnabled = false;
        _ = await (_discordVoiceService ??= new DiscordVoiceService()).ToggleDeafenAsync();
        await RefreshDiscordVoiceAsync();
    }

    private async void DiscordDisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        DiscordDisconnectButton.IsEnabled = false;
        _ = await (_discordVoiceService ??= new DiscordVoiceService()).DisconnectAsync();
        await RefreshDiscordVoiceAsync();
    }

    private async void ClearNotificationsButton_Click(object sender, RoutedEventArgs e)
    {
        var service = _notificationService ??= new NotificationService();
        if (service.ClearAll())
        {
            await RefreshNotificationsAsync();
            UpdateLiveData();
        }
    }

    public bool NotificationsAllowed => (_notificationService ??= new NotificationService()).IsAccessAllowed();

    public async Task EnableNotificationsAsync()
    {
        _ = await (_notificationService ??= new NotificationService()).RequestAccessAsync();
        await RefreshNotificationsAsync();
        if (NeedsNotificationPolling())
        {
            _notificationTimer.Start();
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }


    private async Task RefreshMediaAsync(bool waitForTurn = true)
    {
        if (!await EnterRefreshGateAsync(_mediaRefreshGate, waitForTurn))
        {
            return;
        }

        try
        {
            var media = await _mediaService.SampleAsync();
            _hasMediaSession = media.HasSession;
            byte[]? artwork;
            bool isMedia;

            if (media.HasSession)
            {
                isMedia = true;
                MediaSectionLabel.Text = "NOW PLAYING";
                artwork = media.Artwork;
                MediaTitleText.Text = media.Title;
                MediaSubtitleText.Text = media.Subtitle;
                ExpandedMediaTitle.Text = media.Title;
                ExpandedMediaSubtitle.Text = string.IsNullOrWhiteSpace(media.SourceApp)
                    ? media.Subtitle
                    : $"{media.Subtitle} \u00B7 {media.SourceApp}";
            }
            else
            {
                isMedia = false;
                MediaSectionLabel.Text = "ACTIVE APP";
                var foreground = _foregroundAppService.Sample();
                artwork = foreground.IconPng;
                MediaTitleText.Text = foreground.Title;
                MediaSubtitleText.Text = foreground.ProcessName;
                ExpandedMediaTitle.Text = foreground.Title;
                ExpandedMediaSubtitle.Text = foreground.ProcessName;
            }

            ContextColumn.MinWidth = isMedia ? 300 : 230;
            ContextColumn.Width = new GridLength(isMedia ? 1.10 : 0.80, GridUnitType.Star);
            OverviewColumn.MinWidth = isMedia ? 292 : 320;
            OverviewColumn.Width = new GridLength(isMedia ? 1.02 : 1.18, GridUnitType.Star);
            ExpandedArtworkBorder.Width = isMedia ? 100 : 68;
            ExpandedArtworkBorder.Height = isMedia ? 112 : 68;
            ExpandedArtworkBorder.CornerRadius = new CornerRadius(isMedia ? 14 : 14);
            ExpandedMediaTitle.FontSize = isMedia ? 16 : 14.5;
            ExpandedMediaTitle.TextWrapping = isMedia ? TextWrapping.NoWrap : TextWrapping.Wrap;
            ExpandedMediaTitle.MaxHeight = isMedia ? double.PositiveInfinity : 38;
            ApplyArtwork(artwork, isMedia);
            MediaSeekSlider.Value = media.Progress;
            MediaSeekSlider.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
            MediaPositionText.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
            MediaDurationText.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
            MediaPositionText.Text = FormatMediaTime(media.Position);
            MediaDurationText.Text = FormatMediaTime(media.Duration);
            MediaControlsPanel.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
            PreviousMediaButton.IsEnabled = media.HasSession;
            PlayPauseMediaButton.IsEnabled = media.HasSession;
            NextMediaButton.IsEnabled = media.HasSession;
            PlayIconViewbox.Visibility = media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
            PauseIconViewbox.Visibility = media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
            CompactPlayIconViewbox.Visibility = media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
            CompactPauseIconViewbox.Visibility = media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
            UpdateCompactDensity();
        }
        finally
        {
            _mediaRefreshGate.Release();
        }
    }

    private static async Task<bool> EnterRefreshGateAsync(SemaphoreSlim gate, bool waitForTurn)
    {
        if (waitForTurn)
        {
            await gate.WaitAsync();
            return true;
        }

        return await gate.WaitAsync(0);
    }

    private void ApplyArtwork(byte[]? bytes, bool isMedia)
    {
        if (ReferenceEquals(_lastArtworkBytes, bytes))
        {
            return;
        }

        _lastArtworkBytes = bytes;
        if (bytes is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();

                MediaArtworkBorder.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                ExpandedArtworkBorder.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                MediaGlyphPath.Visibility = Visibility.Collapsed;
                ExpandedArtworkGlyphPath.Visibility = Visibility.Collapsed;
                return;
            }
            catch
            {
                // Fall through to the vector placeholder.
            }
        }

        var fallback = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        fallback.Freeze();
        MediaArtworkBorder.Background = fallback;
        ExpandedArtworkBorder.Background = fallback;
        var fallbackGeometry = (Geometry)FindResource(isMedia ? "MediaFallbackGeometry" : "ActiveAppFallbackGeometry");
        MediaGlyphPath.Data = fallbackGeometry;
        ExpandedArtworkGlyphPath.Data = fallbackGeometry;
        MediaGlyphPath.Visibility = Visibility.Visible;
        ExpandedArtworkGlyphPath.Visibility = Visibility.Visible;
    }

    private void FocusToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _focusTimerService.Toggle();
        UpdateLiveData();
    }

    private void Break45Button_Click(object sender, RoutedEventArgs e)
    {
        _focusTimerService.Start(TimeSpan.FromMinutes(45));
        UpdateLiveData();
    }

    public void StartFocusTimer(int minutes)
    {
        _focusTimerService.Start(TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 180)));
        UpdateLiveData();
    }

    public void ToggleFocusTimer()
    {
        _focusTimerService.Toggle();
        UpdateLiveData();
    }

    public void ResetFocusTimer()
    {
        _focusTimerService.Reset();
        UpdateLiveData();
    }

    private async void MediaSeekSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_hasMediaSession) return;
        _ = await _mediaService.SeekAsync(MediaSeekSlider.Value);
        await RefreshMediaAsync();
    }

    private async void PreviousMediaButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.PreviousAsync();
        await RefreshMediaAsync();
    }

    private async void PlayPauseMediaButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.TogglePlayPauseAsync();
        await RefreshMediaAsync();
    }

    private async void NextMediaButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.NextAsync();
        await RefreshMediaAsync();
    }

    private void UpdateLiveData()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        CompactDateText.Text = now.ToString("M/d");
        ExpandedClockText.Text = now.ToString("HH:mm");
        ExpandedWeekdayText.Text = now.ToString("dddd", CultureInfo.InvariantCulture);
        ExpandedDateText.Text = now.ToString("MMM d", CultureInfo.InvariantCulture);

        var stats = _statsService.Sample();
        CompactCpuMeter.Value = stats.CpuPercent;
        CompactRamMeter.Value = stats.RamPercent;
        ExpandedSystemText.Text = $"CPU {stats.CpuPercent:0}%   GPU {stats.GpuPercent:0}%   RAM {stats.RamPercent:0}%";
        CpuRadialMeter.Value = stats.CpuPercent;
        GpuRadialMeter.Value = stats.GpuPercent;
        RamRadialMeter.Value = stats.RamPercent;
        ExpandedNetworkText.Text = $"\u2193 {stats.DownloadMbps:0.0}   \u2191 {stats.UploadMbps:0.0} Mbps";
        FullNetworkText.Text = $"\u2193 {stats.DownloadMbps:0.0}  \u2191 {stats.UploadMbps:0.0}";
        if (_state == SurfaceState.Expanded)
        {
            _hardwareTelemetryService ??= new HardwareTelemetryService();
            var hardware = _hardwareTelemetryService.Sample();
            var thermal = new List<string>();
            if (hardware.CpuTemperatureC is double cpuTemp) thermal.Add($"CPU {cpuTemp:0}\u00B0C");
            if (hardware.GpuTemperatureC is double gpuTemp) thermal.Add($"GPU {gpuTemp:0}\u00B0C");
            HardwareDetailText.Text = thermal.Count > 0
                ? string.Join(" \u00B7 ", thermal)
                : "Temperature unavailable";

            OverviewPowerText.Text = hardware.PowerMode;
            if (stats.HasBattery)
            {
                OverviewThermalLabel.Text = "BATTERY";
                OverviewThermalText.Text = stats.BatteryCharging
                    ? $"{stats.BatteryPercent:0}% · charging"
                    : $"{stats.BatteryPercent:0}%";
            }
            else
            {
                OverviewThermalLabel.Text = "THERMAL";
                OverviewThermalText.Text = hardware.GpuTemperatureC is double overviewGpuTemp
                    ? $"GPU {overviewGpuTemp:0}\u00B0C"
                    : hardware.CpuTemperatureC is double overviewCpuTemp
                        ? $"CPU {overviewCpuTemp:0}\u00B0C"
                        : "Unavailable";
            }
        }
        else if (_hardwareTelemetryService is not null)
        {
            _hardwareTelemetryService.Dispose();
            _hardwareTelemetryService = null;
        }

        if (stats.StorageTotalBytes > 0)
        {
            var used = Math.Max(0, stats.StorageTotalBytes - stats.StorageFreeBytes);
            StorageText.Text = $"{FormatBytes(stats.StorageFreeBytes)} free / {FormatBytes(stats.StorageTotalBytes)}";
            FullStorageText.Text = $"{FormatBytes(stats.StorageFreeBytes)} free";
            StorageProgressBar.Value = Math.Clamp((double)used / stats.StorageTotalBytes, 0, 1);
        }
        else
        {
            StorageText.Text = "Unavailable";
            FullStorageText.Text = "Unavailable";
            StorageProgressBar.Value = 0;
        }

        BatteryStripGroup.Visibility = stats.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        BatterySeparator.Visibility = Visibility.Collapsed;
        if (stats.HasBattery)
        {
            BatteryText.Text = $"{stats.BatteryPercent:0}%";
            BatteryStateText.Text = stats.BatteryCharging ? "Charging" : "On battery";
        }

        var session = _foregroundAppService.Sample();
        SessionProcessText.Text = session.ProcessName;
        var sessionDetails = new List<string>();
        sessionDetails.Add($"CPU {session.CpuPercent:0.0}%");
        if (session.WorkingSetBytes > 0) sessionDetails.Add(FormatBytes(session.WorkingSetBytes));
        if (session.StartedAt is DateTimeOffset startedAt) sessionDetails.Add($"Up {FormatSessionAge(DateTimeOffset.Now - startedAt)}");
        SessionDetailText.Text = sessionDetails.Count > 0
            ? string.Join(" \u00B7 ", sessionDetails)
            : "Foreground process details unavailable";

        var downloads = _downloadMonitorService.Sample();
        _activeDownloadCount = downloads.ActiveCount;
        CompactDownloadText.Text = _activeDownloadCount.ToString();
        FullDownloadText.Text = $"{_activeDownloadCount} active";
        var showFullDownload = _activeDownloadCount > 0;
        FullDownloadGroup.Visibility = showFullDownload ? Visibility.Visible : Visibility.Collapsed;
        FullDownloadColumn.Width = showFullDownload ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        FullDownloadSeparatorColumn.Width = showFullDownload ? new GridLength(1) : new GridLength(0);
        UpdateDownloadColumnVisibility(downloads.HasActive);
        DownloadActivityProgressBar.Visibility = downloads.HasActive ? Visibility.Visible : Visibility.Collapsed;

        DownloadItem2Group.Visibility = Visibility.Collapsed;
        if (downloads.HasActive && downloads.Primary is not null)
        {
            var primary = downloads.Primary;
            DownloadPrimaryText.Text = primary.Name;
            DownloadDetailText.Text = primary.MegabytesPerSecond > 0.05
                ? $"{FormatBytes(primary.Bytes)} \u00B7 {primary.MegabytesPerSecond:0.0} MB/s \u00B7 {downloads.ActiveCount} active"
                : $"{FormatBytes(primary.Bytes)} \u00B7 {downloads.ActiveCount} active";

            if (downloads.Items.Count > 1)
            {
                var secondary = downloads.Items[1];
                DownloadItem2Group.Visibility = Visibility.Visible;
                DownloadSecondaryText.Text = secondary.Name;
                DownloadSecondaryDetailText.Text = secondary.MegabytesPerSecond > 0.05
                    ? $"{FormatBytes(secondary.Bytes)} \u00B7 {secondary.MegabytesPerSecond:0.0} MB/s"
                    : FormatBytes(secondary.Bytes);
            }
        }
        else
        {
            DownloadPrimaryText.Text = "No active downloads";
            DownloadDetailText.Text = string.Empty;
        }

        var focus = _focusTimerService.Snapshot();
        FocusTimerLabelText.Text = focus.Duration >= TimeSpan.FromMinutes(40) ? "Break" : "Focus";
        FocusTimerText.Text = focus.Display;
        _focusIsRunning = focus.IsRunning;
        CompactFocusText.Text = $"Focus {focus.Display}";
        CompactFocusPlayIconViewbox.Visibility = focus.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        CompactFocusPauseIconViewbox.Visibility = focus.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        FullFocusText.Text = focus.Display;
        FocusPlayIconViewbox.Visibility = focus.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        FocusPauseIconViewbox.Visibility = focus.IsRunning ? Visibility.Visible : Visibility.Collapsed;

        var activity = new List<string>();
        if (focus.IsRunning)
        {
            activity.Add($"Focus {focus.Display}");
        }
        DownloadActivityProgressBar.Visibility = downloads.HasActive ? Visibility.Visible : Visibility.Collapsed;

        if (downloads.HasActive)
        {
            activity.Add($"DL {downloads.ActiveCount}");
        }
        var discordVoice = _discordVoiceService?.Current ?? DiscordVoiceSnapshot.Empty;
        FullDiscordGroup.Visibility = discordVoice.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        FullDiscordColumn.Width = discordVoice.IsConnected ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        FullDiscordSeparatorColumn.Width = discordVoice.IsConnected ? new GridLength(1) : new GridLength(0);
        FullDiscordText.Text = string.IsNullOrWhiteSpace(discordVoice.ChannelName) ? "Discord VC" : discordVoice.ChannelName;
        if (discordVoice.IsConnected)
        {
            activity.Add(string.IsNullOrWhiteSpace(discordVoice.ChannelName) ? "Discord VC" : $"VC {discordVoice.ChannelName}");
        }
        if (_notificationCount > 0)
        {
            activity.Add($"N {_notificationCount}");
        }
        var compactBaseWidth = ResolveBaseSurfaceWidth(GetTargetScreenWidthDip());
        if (compactBaseWidth >= 1400)
        {
            activity.Add($"NET \u2193{stats.DownloadMbps:0.0} \u2191{stats.UploadMbps:0.0}");
            if (stats.StorageFreeBytes > 0)
            {
                activity.Add($"SSD {FormatBytes(stats.StorageFreeBytes)}");
            }
            if (stats.HasBattery)
            {
                activity.Add($"BAT {stats.BatteryPercent:0}%");
            }
        }
        var visibleActivity = compactBaseWidth < 900 ? activity.Take(1) : activity;
        CompactActivityText.Text = string.Join("  \u00B7  ", visibleActivity);
        UpdateCompactDensity();
    }

    private void UpdateDownloadColumnVisibility(bool hasActiveDownloads)
    {
        DownloadColumn.MinWidth = hasActiveDownloads ? 300 : 230;
        DownloadColumn.Width = new GridLength(hasActiveDownloads ? 0.95 : 0.72, GridUnitType.Star);
        DownloadLeadingSeparatorColumn.Width = new GridLength(1);
        DownloadLeadingSeparator.Visibility = Visibility.Visible;
        DownloadGroup.Visibility = hasActiveDownloads ? Visibility.Visible : Visibility.Collapsed;
        SessionGroup.Visibility = hasActiveDownloads ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateDiscordColumnVisibility(bool isConnected)
    {
        DiscordVoiceGroup.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatMediaTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    private static string FormatSessionAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalDays >= 1) return $"{(int)age.TotalDays}d {age.Hours}h";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours}h {age.Minutes}m";
        return $"{Math.Max(0, (int)age.TotalMinutes)}m";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "--";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

}
