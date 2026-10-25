using CompanionOS.Core.Diagnostics;
using Windows.Media.Control;

namespace CompanionOS.Core.Perception;

/// <summary>What the machine is playing, whoever is playing it.</summary>
public sealed class MediaSessionSnapshot
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string AlbumTitle { get; init; } = string.Empty;

    /// <summary>Application identifier reported by Windows, e.g. "Spotify.exe".</summary>
    public string ApplicationId { get; init; } = string.Empty;

    public string PlaybackStatus { get; init; } = string.Empty;
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }

    public bool HasContent => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// Windows' own media transport controls, which is how Iris knows what Spotify,
/// Chrome, VLC or any other player is doing without touching a single app.
///
/// Every call is defensive: protected sessions and players that do not publish
/// metadata simply produce an empty snapshot instead of an error.
/// </summary>
public sealed class MediaSessionSensor
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    /// <summary>Connects to the Windows media manager once, and reconnects if it was lost.</summary>
    public async Task<bool> EnsureConnectedAsync()
    {
        if (_manager is not null)
        {
            return true;
        }

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _manager is not null;
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Senses", $"Media session manager unavailable: {exception.Message}");
            _manager = null;
            return false;
        }
    }

    /// <summary>Reads the current session, or null when nothing is playing.</summary>
    public async Task<MediaSessionSnapshot?> ReadAsync()
    {
        if (!await EnsureConnectedAsync().ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            GlobalSystemMediaTransportControlsSession? session = _manager!.GetCurrentSession();
            if (session is null)
            {
                return null;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            return new MediaSessionSnapshot
            {
                Title = properties?.Title ?? string.Empty,
                Artist = properties?.Artist ?? string.Empty,
                AlbumTitle = properties?.AlbumTitle ?? string.Empty,
                ApplicationId = session.SourceAppUserModelId ?? string.Empty,
                PlaybackStatus = playbackInfo?.PlaybackStatus.ToString() ?? "Unknown",
                Position = timeline?.Position ?? TimeSpan.Zero,
                Duration = timeline?.EndTime ?? TimeSpan.Zero
            };
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Senses", $"Media session unreadable: {exception.Message}");
            return null;
        }
    }

    /// <summary>Pauses or resumes whatever is playing. Used by her music controls.</summary>
    public async Task<bool> TryTogglePlaybackAsync()
    {
        if (!await EnsureConnectedAsync().ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            GlobalSystemMediaTransportControlsSession? session = _manager!.GetCurrentSession();
            if (session is null)
            {
                return false;
            }

            var status = session.GetPlaybackInfo()?.PlaybackStatus;
            return status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                ? await session.TryPauseAsync()
                : await session.TryPlayAsync();
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Senses", $"Media control failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Pauses only when the active Windows media session is currently playing.</summary>
    public async Task<bool> TryPauseAsync()
    {
        if (!await EnsureConnectedAsync().ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            GlobalSystemMediaTransportControlsSession? session = _manager!.GetCurrentSession();
            return session is not null &&
                   session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                   await session.TryPauseAsync();
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Senses", $"Media pause failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Resumes only when the active Windows media session is currently paused.</summary>
    public async Task<bool> TryPlayAsync()
    {
        if (!await EnsureConnectedAsync().ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            GlobalSystemMediaTransportControlsSession? session = _manager!.GetCurrentSession();
            return session is not null &&
                   session.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                   await session.TryPlayAsync();
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Senses", $"Media resume failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Skips to the next track in the active session.</summary>
    public async Task<bool> TrySkipNextAsync()
    {
        if (!await EnsureConnectedAsync().ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            GlobalSystemMediaTransportControlsSession? session = _manager!.GetCurrentSession();
            if (session is null)
            {
                return false;
            }

            return await session.TrySkipNextAsync();
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Senses", $"Media skip failed: {exception.Message}");
            return false;
        }
    }
}
