using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TopIsland.Services;

public sealed record MediaSnapshot(
    bool HasSession,
    string Title,
    string Subtitle,
    string SourceApp,
    bool IsPlaying,
    double Progress,
    byte[]? Artwork);

public sealed class MediaSessionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _activeSession;
    private string? _lastArtworkKey;
    private byte[]? _lastArtwork;

    public async Task<bool> InitializeAsync()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _manager is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<MediaSnapshot> SampleAsync()
    {
        try
        {
            if (_manager is null && !await InitializeAsync())
            {
                ClearActiveSession();
                return Empty();
            }

            var selection = await FindMediaSessionAsync();
            if (selection is null)
            {
                ClearActiveSession();
                return Empty();
            }

            var (session, properties) = selection.Value;
            _activeSession = session;

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var title = properties.Title;
            var subtitle = !string.IsNullOrWhiteSpace(properties.Artist)
                ? properties.Artist
                : !string.IsNullOrWhiteSpace(properties.AlbumTitle)
                    ? properties.AlbumTitle
                    : FriendlySource(session.SourceAppUserModelId);

            var endMs = timeline.EndTime.TotalMilliseconds;
            var progress = endMs > 0
                ? Math.Clamp(timeline.Position.TotalMilliseconds / endMs, 0, 1)
                : 0;

            var artworkKey = $"{session.SourceAppUserModelId}|{properties.Title}|{properties.Artist}|{properties.AlbumTitle}";
            byte[]? artwork;
            if (string.Equals(_lastArtworkKey, artworkKey, StringComparison.Ordinal))
            {
                artwork = _lastArtwork;
            }
            else
            {
                artwork = await ReadArtworkAsync(properties.Thumbnail);
                _lastArtworkKey = artworkKey;
                _lastArtwork = artwork;
            }

            return new MediaSnapshot(
                true,
                title,
                subtitle,
                FriendlySource(session.SourceAppUserModelId),
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                progress,
                artwork);
        }
        catch
        {
            ClearActiveSession();
            return Empty();
        }
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        try
        {
            var session = _activeSession;
            if (session is null)
            {
                return false;
            }

            return session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                ? await session.TryPauseAsync()
                : await session.TryPlayAsync();
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PreviousAsync()
    {
        try
        {
            return _activeSession is not null && await _activeSession.TrySkipPreviousAsync();
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> NextAsync()
    {
        try
        {
            return _activeSession is not null && await _activeSession.TrySkipNextAsync();
        }
        catch
        {
            return false;
        }
    }

    private async Task<(GlobalSystemMediaTransportControlsSession Session, GlobalSystemMediaTransportControlsSessionMediaProperties Properties)?> FindMediaSessionAsync()
    {
        if (_manager is null)
        {
            return null;
        }

        var current = _manager.GetCurrentSession();
        var sessions = _manager.GetSessions();
        var ordered = new List<GlobalSystemMediaTransportControlsSession>();

        if (current is not null)
        {
            ordered.Add(current);
        }

        ordered.AddRange(sessions
            .Where(s => !ReferenceEquals(s, current))
            .OrderByDescending(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));

        foreach (var session in ordered)
        {
            var playback = session.GetPlaybackInfo().PlaybackStatus;
            if (playback is not GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                and not GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
            {
                continue;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties is null || !LooksLikeUserMedia(session.SourceAppUserModelId, properties))
            {
                continue;
            }

            return (session, properties);
        }

        return null;
    }

    private static bool LooksLikeUserMedia(string source, GlobalSystemMediaTransportControlsSessionMediaProperties properties)
    {
        if (string.IsNullOrWhiteSpace(properties.Title))
        {
            return false;
        }

        var hasRichMetadata = !string.IsNullOrWhiteSpace(properties.Artist)
            || !string.IsNullOrWhiteSpace(properties.AlbumTitle);
        if (hasRichMetadata)
        {
            return true;
        }

        var id = source.ToLowerInvariant();
        string[] knownMediaSources =
        [
            "spotify", "chrome", "msedge", "firefox", "zunemusic", "mediaplayer",
            "vlc", "foobar", "mpv", "potplayer", "musicbee", "aimp"
        ];

        return knownMediaSources.Any(id.Contains);
    }

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync();
            var requested = (uint)Math.Min(stream.Size, 2_000_000UL);
            if (requested == 0)
            {
                return null;
            }

            using var reader = new DataReader(stream);
            var loaded = await reader.LoadAsync(requested);
            if (loaded == 0)
            {
                return null;
            }

            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            reader.DetachStream();
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private void ClearActiveSession()
    {
        _activeSession = null;
        _lastArtworkKey = null;
        _lastArtwork = null;
    }

    private static MediaSnapshot Empty() => new(false, "TopIsland", "Windows top surface", string.Empty, false, 0, null);

    private static string FriendlySource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "Media";
        }

        if (source.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        if (source.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }

        if (source.Contains("MSEdge", StringComparison.OrdinalIgnoreCase) || source.Contains("Edge", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Edge";
        }

        if (source.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
        {
            return "Firefox";
        }

        if (source.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase) || source.Contains("MediaPlayer", StringComparison.OrdinalIgnoreCase))
        {
            return "Media Player";
        }

        var tail = source.Split('!').LastOrDefault() ?? source;
        return tail.Length > 28 ? tail[..28] + "…" : tail;
    }
}
