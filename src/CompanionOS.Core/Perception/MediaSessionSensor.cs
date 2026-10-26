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

/// <summary>Separates a successful "nothing is playing" read from a failed Windows query.</summary>
public sealed record MediaSessionReadResult(bool IsAvailable, MediaSessionSnapshot? Session, string? Error = null);

/// <summary>The media sensing and pause/resume operations used by the perception engine.</summary>
public interface IMediaSessionSensor
{
    Task<MediaSessionReadResult> ReadWithStatusAsync();
    Task<bool> TryPauseAsync();
    Task<bool> TryPlayAsync();
}

/// <summary>
/// Windows' own media transport controls, which is how Iris knows what Spotify,
/// Chrome, VLC or any other player is doing without touching a single app.
///
/// Reads distinguish a successful empty session from a failed Windows query, so Iris
/// can clear or qualify old track information instead of presenting it as current.
/// </summary>
public sealed class MediaSessionSensor : IMediaSessionSensor
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

    /// <summary>Reads the current session and preserves whether Windows answered successfully.</summary>
    public async Task<MediaSessionReadResult> ReadWithStatusAsync()
    {
        if (!await EnsureConnectedAsync().ConfigureAwait(false))
        {
            return new MediaSessionReadResult(false, null, "Windows media session service is unavailable.");
        }

        try
        {
            GlobalSystemMediaTransportControlsSession? session = _manager!.GetCurrentSession();
            if (session is null)
            {
                return new MediaSessionReadResult(true, null);
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            MediaSessionSnapshot snapshot = new()
            {
                Title = properties?.Title ?? string.Empty,
                Artist = properties?.Artist ?? string.Empty,
                AlbumTitle = properties?.AlbumTitle ?? string.Empty,
                ApplicationId = session.SourceAppUserModelId ?? string.Empty,
                PlaybackStatus = playbackInfo?.PlaybackStatus.ToString() ?? "Unknown",
                Position = timeline?.Position ?? TimeSpan.Zero,
                Duration = timeline?.EndTime ?? TimeSpan.Zero
            };
            return new MediaSessionReadResult(true, snapshot);
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Senses", $"Media session unreadable: {exception.Message}");
            _manager = null;
            return new MediaSessionReadResult(false, null, "The active Windows media session could not be read.");
        }
    }

    /// <summary>Compatibility helper; null means no readable active session or an unavailable sensor.</summary>
    public async Task<MediaSessionSnapshot?> ReadAsync() =>
        (await ReadWithStatusAsync().ConfigureAwait(false)).Session;

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
