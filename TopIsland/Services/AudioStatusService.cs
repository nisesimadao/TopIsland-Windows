using NAudio.CoreAudioApi;

namespace TopIsland.Services;

public sealed record AudioStatusSnapshot(bool Available, string DeviceName, double VolumePercent, bool Muted)
{
    public static AudioStatusSnapshot Unavailable { get; } = new(false, string.Empty, 0, false);
}

public sealed class AudioStatusService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private DateTimeOffset _lastSampleAt;
    private AudioStatusSnapshot _lastSnapshot = AudioStatusSnapshot.Unavailable;

    public AudioStatusSnapshot Sample()
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastSampleAt).TotalMilliseconds < 1200)
        {
            return _lastSnapshot;
        }

        _lastSampleAt = now;
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var name = ShortenDeviceName(device.FriendlyName);
            var volume = Math.Clamp(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100d, 0, 100);
            _lastSnapshot = new AudioStatusSnapshot(true, name, volume, device.AudioEndpointVolume.Mute);
        }
        catch
        {
            _lastSnapshot = AudioStatusSnapshot.Unavailable;
        }

        return _lastSnapshot;
    }

    /// <summary>Sets the default render endpoint's master volume.</summary>
    /// <param name="volumePercent">The requested volume, from 0 through 100.</param>
    /// <returns>The current endpoint state after the change, or unavailable when Windows rejects it.</returns>
    public AudioStatusSnapshot SetVolume(double volumePercent)
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(volumePercent / 100d, 0d, 1d);
            return ReadCurrent(device);
        }
        catch
        {
            return _lastSnapshot = AudioStatusSnapshot.Unavailable;
        }
    }

    /// <summary>Toggles mute on the default render endpoint.</summary>
    /// <returns>The current endpoint state after the change, or unavailable when Windows rejects it.</returns>
    public AudioStatusSnapshot ToggleMute()
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
            return ReadCurrent(device);
        }
        catch
        {
            return _lastSnapshot = AudioStatusSnapshot.Unavailable;
        }
    }

    public void Dispose() => _enumerator.Dispose();

    private AudioStatusSnapshot ReadCurrent(MMDevice device)
    {
        _lastSampleAt = DateTimeOffset.UtcNow;
        return _lastSnapshot = new AudioStatusSnapshot(
            true,
            ShortenDeviceName(device.FriendlyName),
            Math.Clamp(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100d, 0, 100),
            device.AudioEndpointVolume.Mute);
    }

    private static string ShortenDeviceName(string name)
    {
        var value = name?.Trim() ?? string.Empty;
        if (value.Length <= 34)
        {
            return value;
        }

        var parenthesis = value.IndexOf(" (", StringComparison.Ordinal);
        if (parenthesis > 6)
        {
            value = value[..parenthesis];
        }

        return value.Length <= 34 ? value : value[..33] + "…";
    }
}
