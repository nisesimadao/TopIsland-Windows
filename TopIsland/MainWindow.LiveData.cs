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

    private bool NeedsSecondaryPolling()
    {
        if (!IsVisible)
        {
            return false;
        }

        var baseWidth = ResolveBaseSurfaceWidth(GetTargetScreenWidthDip());
        return _state == SurfaceState.Expanded
               || baseWidth >= 1000
               || (_state == SurfaceState.Peek && baseWidth >= 500);
    }

    private void UpdateSecondaryPollingState(bool refreshImmediately = false)
    {
        var shouldPoll = NeedsSecondaryPolling();
        var isPolling = _notificationTimer.IsEnabled || _discordTimer.IsEnabled;

        if (!shouldPoll)
        {
            _notificationTimer.Stop();
            _discordTimer.Stop();
            return;
        }

        if (!isPolling)
        {
            _notificationTimer.Start();
            _discordTimer.Start();
            refreshImmediately = true;
        }

        if (refreshImmediately)
        {
            _ = RefreshSecondaryLiveDataAsync();
        }
    }

    private async Task RefreshSecondaryLiveDataAsync()
    {
        await Task.WhenAll(
            RefreshNotificationsAsync(waitForTurn: false),
            RefreshDiscordVoiceAsync(waitForTurn: false));
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
            NotificationColumn.Width = hasVisibleNotifications ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            NotificationStripGroup.Visibility = hasVisibleNotifications ? Visibility.Visible : Visibility.Collapsed;
            NotificationSeparator.Visibility = hasVisibleNotifications ? Visibility.Visible : Visibility.Collapsed;

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
            var serverText = string.IsNullOrWhiteSpace(voice.ServerName)
                ? participantText
                : $"{voice.ServerName} \u00B7 {participantText}";
            // Keep the always-visible voice summary privacy-safe. Exact participant
            // names remain available to the service for future opt-in/detail UI,
            // but the surface shows only the server and observed participant count.
            DiscordVoiceDetailText.Text = serverText;
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

    public bool NotificationsAllowed => (_notificationService ??= new NotificationService()).IsAccessAllowed();

    public async Task EnableNotificationsAsync()
    {
        _ = await (_notificationService ??= new NotificationService()).RequestAccessAsync();
        await RefreshNotificationsAsync();
        if (NeedsSecondaryPolling())
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

            ApplyArtwork(artwork, isMedia);
            MediaProgressBar.Value = media.Progress;
            MediaProgressBar.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
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
        ExpandedClockText.Text = now.ToString("HH:mm");
        ExpandedDateText.Text = now.ToString("M/d ddd");

        var stats = _statsService.Sample();
        CompactCpuMeter.Value = stats.CpuPercent;
        CompactRamMeter.Value = stats.RamPercent;
        ExpandedSystemText.Text = $"CPU {stats.CpuPercent:0}%   GPU {stats.GpuPercent:0}%   RAM {stats.RamPercent:0}%";
        CpuRadialMeter.Value = stats.CpuPercent;
        GpuRadialMeter.Value = stats.GpuPercent;
        RamRadialMeter.Value = stats.RamPercent;
        ExpandedNetworkText.Text = $"\u2193 {stats.DownloadMbps:0.0}   \u2191 {stats.UploadMbps:0.0} Mbps";
        if (_state == SurfaceState.Expanded)
        {
            _hardwareTelemetryService ??= new HardwareTelemetryService();
            var hardware = _hardwareTelemetryService.Sample();
            var thermal = new List<string>();
            if (hardware.CpuTemperatureC is double cpuTemp) thermal.Add($"CPU {cpuTemp:0}\u00B0C");
            if (hardware.GpuTemperatureC is double gpuTemp) thermal.Add($"GPU {gpuTemp:0}\u00B0C");
            thermal.Add(hardware.PowerMode);
            HardwareDetailText.Text = string.Join(" \u00B7 ", thermal);
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
            StorageProgressBar.Value = Math.Clamp((double)used / stats.StorageTotalBytes, 0, 1);
        }
        else
        {
            StorageText.Text = "Unavailable";
            StorageProgressBar.Value = 0;
        }

        BatteryStripGroup.Visibility = stats.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        BatterySeparator.Visibility = stats.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        BatteryColumn.Width = stats.HasBattery ? new GridLength(120) : new GridLength(0);
        if (stats.HasBattery)
        {
            BatteryText.Text = $"{stats.BatteryPercent:0}%";
            BatteryStateText.Text = stats.BatteryCharging ? "Charging" : "On battery";
        }

        var downloads = _downloadMonitorService.Sample();
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
        DownloadColumn.MinWidth = hasActiveDownloads ? 200 : 0;
        DownloadColumn.Width = hasActiveDownloads ? new GridLength(0.95, GridUnitType.Star) : new GridLength(0);
        DownloadLeadingSeparatorColumn.Width = hasActiveDownloads ? new GridLength(1) : new GridLength(0);
        var visibility = hasActiveDownloads ? Visibility.Visible : Visibility.Collapsed;
        DownloadGroup.Visibility = visibility;
        DownloadLeadingSeparator.Visibility = visibility;
    }

    private void UpdateDiscordColumnVisibility(bool isConnected)
    {
        DiscordVoiceColumn.MinWidth = isConnected ? 250 : 0;
        DiscordVoiceColumn.Width = isConnected ? new GridLength(1.15, GridUnitType.Star) : new GridLength(0);
        CommunicationSeparatorColumn.Width = isConnected ? new GridLength(1) : new GridLength(0);
        DiscordVoiceGroup.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        CommunicationSeparator.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatMediaTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
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
