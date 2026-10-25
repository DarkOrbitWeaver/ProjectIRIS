using CompanionOS.Core.Common;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;
using System.Text.Json;
using NAudio.Wave;

namespace CompanionOS.Core.Modules.BuiltIn;

/// <summary>What the music panel in the interface shows. Plain data, no UI code.</summary>
public sealed class NowPlayingModel : ObservableObject
{
    private string _title = "Nothing playing";
    private string _status = "Stopped";
    private double _progress;
    private string _positionText = "0:00";
    private int _trackCount;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>0.0 to 1.0 through the current track.</summary>
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public string PositionText
    {
        get => _positionText;
        set => SetProperty(ref _positionText, value);
    }

    public int TrackCount
    {
        get => _trackCount;
        set => SetProperty(ref _trackCount, value);
    }
}

/// <summary>
/// Her own music player.
///
/// It plays whatever is in the Music folder inside her workspace, so nothing is
/// hard-coded and the user controls the library by dropping files in one place.
/// She can start, pause, skip, stop and set the volume by voice, and every action is
/// a tier-1 reflex: music must never need permission, and it must keep working while
/// her thinking brain is unloaded.
/// </summary>
public sealed class MusicPlayerModule : IAssistantModule, IModuleSurfaceProvider, IDisposable
{
    // Formats Windows can normally decode through NAudio without a third-party codec.
    // Unsupported files are skipped rather than poisoning an otherwise good scan.
    private static readonly string[] SupportedExtensions =
        { ".mp3", ".wav", ".aiff", ".aif", ".flac", ".m4a", ".aac", ".wma" };

    private readonly NowPlayingModel _nowPlaying = new();
    private readonly List<string> _library = new();
    private readonly object _playerLock = new();

    private ModuleContext? _context;
    private WaveOutEvent? _outputDevice;
    private AudioFileReader? _audioReader;
    private Timer? _progressTimer;
    private int _currentIndex = -1;
    private bool _isDisposed;
    private DateTimeOffset _lastLibraryRefresh = DateTimeOffset.MinValue;

    // A library can be a USB drive or a many-thousand-file archive. Rewalking it for
    // every play/list command is not "real time"; it is an invisible disk spike. An
    // explicit Refresh command (or adding/removing a folder) always bypasses this
    // small cache, while normal playback stays instant.
    private static readonly TimeSpan MinimumAutomaticLibraryRefresh = TimeSpan.FromMinutes(2);

    /// <summary>Playlist playback. When a playlist starts she owns this queue, and the
    /// track-end handler walks through it; any manual play/skip/stop clears it, so
    /// ordinary double-click-style browsing never gets hijacked by yesterday's list.</summary>
    private readonly List<string> _queue = new();
    private int _queueIndex = -1;

    /// <summary>Bumped on every commanded stop. A natural track-end advance checks it
    /// before continuing, so stop always wins over an automatic next that is already
    /// in flight on another thread.</summary>
    private int _playbackGeneration;

    // The current place in the deliberately conversational folder browser. It is session-only:
    // remembering a user's last folder forever would be surprising, while retaining it for the
    // next "go into that one" is exactly how a human-guided browse should feel.
    private string _browseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string Id => "music";

    public string DisplayName => "Music Player";

    public string Purpose =>
        "Plays and controls music from your real folders, with playlists.";

    public string CapabilityDescription =>
        "music song track play pause resume stop skip next previous volume playlist queue " +
        "library browse folder navigate go here find music add folder put something on play something chill " +
        "add to playlist make a playlist";

    public IReadOnlyList<ModuleTool> Tools { get; } = new List<ModuleTool>
    {
        new(
            "play_music",
            "Play a track from her music library. Can take a description of what to play.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"query":{"type":"string","description":"Track, mood or artist. Leave empty to resume."}},"required":[]}""",
            "play music", "song", "track", "put on"),

        new("pause_music", "Pause the currently playing music.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "pause"),

        new("resume_music", "Resume the paused track.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "resume", "continue"),

        new("next_track", "Skip to the next track in the library.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "next", "skip"),

        new("shuffle_music", "Shuffle her music library and play the first track. Next and previous keep following the shuffled order.",
            ToolTier.Reflex, """{"type":"object","properties":{},"required":[]}""",
            "shuffle", "random songs", "mix it up"),

        new("stop_music", "Stop playback completely.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "stop music", "silence"),

        new(
            "set_music_volume",
            "Set the music volume.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"level":{"type":"integer","description":"0 to 100"}},"required":["level"]}""",
            "volume", "louder", "quieter"),

        new("list_music", "List the tracks available in her music library.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "what music", "library", "playlist"),

        new(
            "add_music_folder",
            "Add an existing folder anywhere on this PC to the music library and scan it. Ask for confirmation first. An empty path means the folder most recently opened with browse_music_folder.",
            ToolTier.SystemCritical,
            """{"type":"object","properties":{"path":{"type":"string","description":"Existing folder containing music, for example D:\\Music. Empty uses the currently browsed folder."}},"required":[]}""",
            "add music folder", "music folder", "my music is", "scan folder", "find my music"),

        new(
            "browse_music_folder",
            "Safely list folders in a location while helping the user navigate to their music. Use an empty path for the current location, 'up' for its parent, or a shown child folder name. Ask permission before browsing a new location.",
            ToolTier.SystemCritical,
            """{"type":"object","properties":{"path":{"type":"string","description":"Absolute folder, shown child-folder name, 'up', or empty for the current location"}},"required":[]}""",
            "browse music", "open folder", "go here", "go into", "find folder", "where is my music"),

        new(
            "remove_music_folder",
            "Stop scanning one configured music folder. This never deletes its files.",
            ToolTier.SystemCritical,
            """{"type":"object","properties":{"path":{"type":"string","description":"Configured music folder to remove"}},"required":["path"]}""",
            "remove music folder", "forget music folder", "stop scanning folder"),

        new("refresh_music_library", "Re-scan configured music folders and report playable tracks.",
            ToolTier.Scoped, """{"type":"object","properties":{},"required":[]}""",
            "refresh music", "rescan music", "scan music", "update library"),

        new("show_music_overlay", "Show the small always-on-top music controls while music is playing.",
            ToolTier.Reflex, """{"type":"object","properties":{},"required":[]}""",
            "show music player", "show music overlay", "show player", "expand music"),

        new("hide_music_overlay", "Hide the small music controls without stopping music. The player can be shown again at any time.",
            ToolTier.Reflex, """{"type":"object","properties":{},"required":[]}""",
            "hide music player", "hide music overlay", "minimize music", "minimize player"),

        new(
            "duck_music",
            "Lower the music while she speaks, then restore it. Used automatically when she talks.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"ducked":{"type":"boolean"}},"required":["ducked"]}""",
            "duck", "lower volume while talking"),

        new("previous_track", "Go back to the previous track.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "previous", "last song", "back"),

        new("list_playlists", "List her saved playlists and their sizes.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "playlists", "what playlists"),

        new(
            "create_playlist",
            "Create an empty playlist, or create one and immediately add tracks that match a query.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"name":{"type":"string"},"query":{"type":"string","description":"Optional: add matching tracks right away"}},"required":["name"]}""",
            "new playlist", "create playlist", "make playlist"),

        new(
            "add_to_playlist",
            "Add tracks that match a query to a saved playlist.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"name":{"type":"string"},"query":{"type":"string","description":"Part of the track name"}},"required":["name","query"]}""",
            "add song", "add to playlist", "add track"),

        new(
            "play_playlist",
            "Play a saved playlist from the start.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
            "play playlist", "play my")
    };

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;

        // The drop-in folder is one of her library roots; create it so there is
        // always somewhere obvious to hand her files.
        Directory.CreateDirectory(Path.Combine(context.WorkspaceDirectory, "Music"));
        RefreshLibrary(force: true);

        _progressTimer = new Timer(_ => UpdateProgress(), null, 500, 500);

        // The player must survive a restart of the interface: no state is kept in the
        // audio device, so simply re-scanning the folder is enough.
        _nowPlaying.TrackCount = _library.Count;
        return Task.CompletedTask;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        switch (invocation.Tool.Name)
        {
            case "play_music":
                return Task.FromResult(Play(invocation.GetString("query")));

            case "pause_music":
                lock (_playerLock)
                {
                    if (_outputDevice is null)
                    {
                        return Task.FromResult(ToolResult.Fail("Nothing is playing."));
                    }

                    _outputDevice.Pause();
                    _nowPlaying.Status = "Paused";
                    return Task.FromResult(ToolResult.Ok("Music paused.", "Paused."));
                }

            case "resume_music":
                lock (_playerLock)
                {
                    if (_outputDevice is null)
                    {
                        return Task.FromResult(Play(string.Empty));
                    }

                    _outputDevice.Play();
                    _nowPlaying.Status = "Playing";
                    return Task.FromResult(ToolResult.Ok("Music resumed.", "Back on."));
                }

            case "next_track":
                return Task.FromResult(PlayNext());

            case "shuffle_music":
                return Task.FromResult(ShuffleMusic());

            case "stop_music":
                StopPlayback();
                return Task.FromResult(ToolResult.Ok("Music stopped.", "Stopped."));

            case "set_music_volume":
            {
                int level = Math.Clamp(invocation.GetInt32("level", 60), 0, 100);
                lock (_playerLock)
                {
                    if (_audioReader is not null)
                    {
                        _audioReader.Volume = level / 100f;
                    }
                }

                return Task.FromResult(ToolResult.Ok($"Music volume set to {level} percent."));
            }

            case "list_music":
            {
                RefreshLibrary();
                if (_library.Count == 0)
                {
                    return Task.FromResult(ToolResult.Ok(
                        "No audio files yet. Name your music folders in settings, or drop files " +
                        "into the workspace Music folder."));
                }

                string listing = string.Join("; ", _library.Take(20).Select(Path.GetFileNameWithoutExtension));
                return Task.FromResult(ToolResult.Ok($"{_library.Count} tracks: {listing}",
                    displayText: listing));
            }

            case "add_music_folder":
                return Task.FromResult(AddMusicFolder(invocation.GetString("path")));

            case "browse_music_folder":
                return Task.FromResult(BrowseMusicFolder(invocation.GetString("path")));

            case "remove_music_folder":
                return Task.FromResult(RemoveMusicFolder(invocation.GetString("path")));

            case "refresh_music_library":
                RefreshLibrary(force: true);
                return Task.FromResult(ToolResult.Ok(
                    $"Music library refreshed. I found {_library.Count} playable track(s) in {LibraryDirectories().Count} folder(s).",
                    spokenText: _library.Count == 0 ? "I couldn't find playable music there yet." : "Music library refreshed."));

            case "show_music_overlay":
                return Task.FromResult(SetMusicOverlayVisible(true));

            case "hide_music_overlay":
                return Task.FromResult(SetMusicOverlayVisible(false));

            case "duck_music":
            {
                bool shouldDuck = invocation.GetBoolean("ducked", true);
                lock (_playerLock)
                {
                    if (_audioReader is not null)
                    {
                        double target = shouldDuck
                            ? Math.Max(0.05, _context?.Settings.Voice.MusicDuckLevel ?? 0.25)
                            : 1.0;
                        _audioReader.Volume = (float)target;
                    }
                }

                return Task.FromResult(ToolResult.Ok(shouldDuck ? "Music lowered." : "Music restored."));
            }

            case "previous_track":
                return Task.FromResult(PlayPrevious());

            case "list_playlists":
                return Task.FromResult(ListPlaylists());

            case "create_playlist":
                return Task.FromResult(CreatePlaylist(invocation.GetString("name"),
                    invocation.GetString("query")));

            case "add_to_playlist":
                return Task.FromResult(AddToPlaylist(invocation.GetString("name"),
                    invocation.GetString("query")));

            case "play_playlist":
                return Task.FromResult(PlayPlaylist(invocation.GetString("name")));

            default:
                return Task.FromResult(ToolResult.Fail($"Unknown music tool '{invocation.Tool.Name}'."));
        }
    }

    public IReadOnlyList<ModuleSurface> GetSurfaces() => new[]
    {
        new ModuleSurface
        {
            Id = "music.nowplaying",
            Title = "Now Playing",
            Slot = ShellSlot.Sidebar,
            Model = _nowPlaying
        }
    };

    public Task ShutdownAsync()
    {
        StopPlayback();
        _progressTimer?.Dispose();
        _progressTimer = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopPlayback();
        _progressTimer?.Dispose();
    }

    /// <summary>The real places music comes from: the user's own folders plus her
    /// drop-in workspace folder. Nothing is copied; she plays in place.</summary>
    private List<string> LibraryDirectories()
    {
        List<string> directories = new();

        foreach (string directory in _context?.Settings.Music.SearchDirectories ?? new List<string>())
        {
            string expanded = Environment.ExpandEnvironmentVariables(directory);
            if (directory.StartsWith('~'))
            {
                expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    directory[1..].TrimStart('/', '\\'));
            }

            if (!string.IsNullOrWhiteSpace(expanded) && Directory.Exists(expanded) &&
                !directories.Contains(expanded, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(expanded);
            }
        }

        string dropIns = Path.Combine(_context?.WorkspaceDirectory ?? Path.GetTempPath(), "Music");
        if (!directories.Contains(dropIns, StringComparer.OrdinalIgnoreCase))
        {
            directories.Add(dropIns);
        }

        return directories;
    }

    /// <summary>Adds a real, existing folder to the persistent library. This only
    /// grants read access to that one directory; it never copies, moves or deletes the
    /// owner's files.</summary>
    private ToolResult AddMusicFolder(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            requestedPath = _browseDirectory;
        }

        string path = ExpandDirectory(requestedPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolResult.Fail("Tell me the folder path to add, for example D:\\Music.");
        }

        if (!Directory.Exists(path))
        {
            return ToolResult.Fail($"I can't find '{path}'. Check that the drive is connected, then give me the folder path again.");
        }

        List<string> directories = _context?.Settings.Music.SearchDirectories ?? new List<string>();
        if (!directories.Any(existing => string.Equals(ExpandDirectory(existing), path,
                StringComparison.OrdinalIgnoreCase)))
        {
            directories.Add(path);
            _context?.PersistSettings();
        }

        RefreshLibrary(force: true);
        int foundHere = _library.Count(candidate => candidate.StartsWith(path, StringComparison.OrdinalIgnoreCase));
        return ToolResult.Ok(
            $"Added '{path}' and found {foundHere} playable track(s) there. The library now has {_library.Count} track(s).",
            spokenText: foundHere == 0
                ? "I added the folder, but I couldn't find playable tracks there yet."
                : $"Found {foundHere} tracks. Would you like me to play something?");
    }

    /// <summary>
    /// Lists only direct child folders (never recursively scans a drive) so a human can guide
    /// Iris one step at a time without an invisible disk spike. The selected location is kept
    /// only in this running session and is used by a later confirmed add_music_folder call.
    /// </summary>
    private ToolResult BrowseMusicFolder(string requestedLocation)
    {
        string path = ResolveBrowseDirectory(requestedLocation);
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return ToolResult.Fail(
                $"I can't open '{requestedLocation}'. Tell me another folder or say up to return to the parent folder.");
        }

        try
        {
            List<string> children = Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList()!;
            int directTracks = Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly)
                .Count(file => SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));

            _browseDirectory = path;
            string folderList = children.Count == 0 ? "no child folders" : string.Join(", ", children);
            string suffix = children.Count == 40 ? " (showing the first 40)" : string.Empty;
            string response = $"Opened '{path}'. I see {folderList}{suffix}; {directTracks} playable audio file(s) directly here. " +
                "Tell me which folder to open next, say up, or confirm that I should add this folder.";
            return ToolResult.Ok(response, spokenText: children.Count == 0
                ? "I opened it. There are no folders inside. Would you like me to add this folder?"
                : $"Opened it. I see {children.Count} folders. Which one should I open?");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail($"Windows denied access to '{path}'. Choose another folder or check its permissions.");
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Music", $"Could not browse '{path}': {exception.Message}");
            return ToolResult.Fail($"I couldn't browse '{path}'. The drive may be unavailable or the folder may have changed.");
        }
    }

    private string ResolveBrowseDirectory(string? requestedLocation)
    {
        string request = (requestedLocation ?? string.Empty).Trim().Trim('"');
        if (request.Length == 0 || request.Equals("here", StringComparison.OrdinalIgnoreCase))
        {
            return _browseDirectory;
        }

        if (request.Equals("up", StringComparison.OrdinalIgnoreCase) || request == "..")
        {
            return Directory.GetParent(_browseDirectory)?.FullName ?? _browseDirectory;
        }

        string alias = request.ToLowerInvariant();
        string? knownFolder = alias switch
        {
            "music" or "my music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(knownFolder))
        {
            return knownFolder;
        }

        string absoluteOrRelative = ExpandDirectory(request);
        if (Path.IsPathRooted(request) || Directory.Exists(absoluteOrRelative))
        {
            return absoluteOrRelative;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(_browseDirectory, request));
        }
        catch
        {
            return request;
        }
    }

    private ToolResult RemoveMusicFolder(string requestedPath)
    {
        string path = ExpandDirectory(requestedPath);
        List<string> directories = _context?.Settings.Music.SearchDirectories ?? new List<string>();
        int removed = directories.RemoveAll(existing => string.Equals(ExpandDirectory(existing), path,
            StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return ToolResult.Fail("That folder is not in the music library. No files were changed.");
        }

        _context?.PersistSettings();
        RefreshLibrary(force: true);
        return ToolResult.Ok($"Stopped scanning '{path}'. No files were deleted.",
            spokenText: "I removed that folder from my library. I did not delete anything.");
    }

    private ToolResult SetMusicOverlayVisible(bool visible)
    {
        if (_context is null)
        {
            return ToolResult.Fail("The music controls are not ready yet.");
        }

        _context.Settings.Interface.ShowMusicOverlay = visible;
        _context.PersistSettings();
        return ToolResult.Ok(visible ? "Music overlay shown." : "Music overlay hidden; playback continues.",
            spokenText: visible ? "Music controls are back." : "Music controls hidden. The music keeps playing.");
    }

    private static string ExpandDirectory(string? value)
    {
        string input = (value ?? string.Empty).Trim().Trim('"');
        if (input.Length == 0)
        {
            return string.Empty;
        }

        string expanded = Environment.ExpandEnvironmentVariables(input);
        if (expanded.StartsWith('~'))
        {
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[1..].TrimStart('/', '\\'));
        }

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception)
        {
            return expanded;
        }
    }

    /// <summary>Playlists persist as plain JSON next to her workspace: readable, copyable,
    /// never a database her owner cannot open.</summary>
    private string PlaylistsDirectory =>
        Path.Combine(_context?.WorkspaceDirectory ?? Path.GetTempPath(), "playlists");

    private ToolResult Play(string query)
    {
        RefreshLibrary();
        if (_library.Count == 0)
        {
            return ToolResult.Fail(
                "I found no audio files. Name the folders that hold your music in settings " +
                "(MusicSearchDirectories), or drop files into my workspace Music folder.");
        }

        int chosenIndex = string.IsNullOrWhiteSpace(query)
            ? (_currentIndex >= 0 ? _currentIndex : 0)
            : FindBestMatch(query);

        if (chosenIndex < 0)
        {
            return ToolResult.Fail(
                $"I couldn't find a track matching '{query}'. I left the current playback unchanged.");
        }

        if (!StartTrack(chosenIndex))
        {
            return ToolResult.Fail("I found that track, but Windows could not play it. Check the file and selected audio output.");
        }

        string title = Path.GetFileNameWithoutExtension(_library[chosenIndex]);
        return ToolResult.Ok($"Playing {title}.", $"Playing {title}.", title);
    }

    private ToolResult PlayNext()
    {
        if (_queue.Count > 0 && _queueIndex >= 0)
        {
            int queueNext = _queueIndex + 1;
            if (queueNext >= _queue.Count)
            {
                return ToolResult.Fail("That was the last track in this queue.");
            }

            if (!StartTrackPath(_queue[queueNext]))
            {
                return ToolResult.Fail("I found the next queued track, but Windows could not play it.");
            }

            _queueIndex = queueNext;
            string queuedTitle = Path.GetFileNameWithoutExtension(_queue[queueNext]);
            return ToolResult.Ok($"Playing {queuedTitle}.", $"Next up, {queuedTitle}.", queuedTitle);
        }

        if (_library.Count == 0)
        {
            return ToolResult.Fail("I have no music to play yet.");
        }

        int nextIndex = (_currentIndex + 1) % _library.Count;
        if (!StartTrack(nextIndex))
        {
            return ToolResult.Fail("I found the next track, but Windows could not play it. Check the file and selected audio output.");
        }

        string title = Path.GetFileNameWithoutExtension(_library[nextIndex]);
        return ToolResult.Ok($"Playing {title}.", $"Next up, {title}.", title);
    }

    private ToolResult PlayPrevious()
    {
        if (_queue.Count > 0 && _queueIndex >= 0)
        {
            int queuePrevious = Math.Max(0, _queueIndex - 1);
            if (!StartTrackPath(_queue[queuePrevious]))
            {
                return ToolResult.Fail("I found the previous queued track, but Windows could not play it.");
            }

            _queueIndex = queuePrevious;
            string queuedTitle = Path.GetFileNameWithoutExtension(_queue[queuePrevious]);
            return ToolResult.Ok($"Playing {queuedTitle}.", $"Back to {queuedTitle}.", queuedTitle);
        }

        if (_library.Count == 0)
        {
            return ToolResult.Fail("I have no music to play yet.");
        }

        int previousIndex = _currentIndex <= 0 ? _library.Count - 1 : _currentIndex - 1;
        if (_currentIndex < 0)
        {
            previousIndex = 0;
        }

        if (!StartTrack(previousIndex))
        {
            return ToolResult.Fail("I found the previous track, but Windows could not play it. Check the file and selected audio output.");
        }

        string title = Path.GetFileNameWithoutExtension(_library[previousIndex]);
        return ToolResult.Ok($"Playing {title}.", $"Back to {title}.", title);
    }

    private ToolResult ListPlaylists()
    {
        IReadOnlyList<string> names = PlaylistNames();
        if (names.Count == 0)
        {
            return ToolResult.Ok("No playlists yet. Ask me to make one and add songs to it.");
        }

        string lines = string.Join("\n", names.Select(name =>
        {
            List<string> tracks = LoadPlaylist(name);
            return $"{name} — {tracks.Count} track(s)";
        }));

        return ToolResult.Ok(lines, displayText: lines);
    }

    private ToolResult ShuffleMusic()
    {
        RefreshLibrary();
        if (_library.Count == 0)
        {
            return ToolResult.Fail("I have no music to shuffle yet. Add a folder with your tracks first.");
        }

        ClearQueue();
        _queue.AddRange(_library);
        for (int index = _queue.Count - 1; index > 0; index--)
        {
            int swapIndex = Random.Shared.Next(index + 1);
            (_queue[index], _queue[swapIndex]) = (_queue[swapIndex], _queue[index]);
        }

        for (int index = 0; index < _queue.Count; index++)
        {
            if (!StartTrackPath(_queue[index]))
            {
                continue;
            }

            _queueIndex = index;
            string title = Path.GetFileNameWithoutExtension(_queue[index]);
            return ToolResult.Ok($"Shuffling the library. Playing {title} first.",
                $"Shuffling your music. {title} is first.", title);
        }

        ClearQueue();
        return ToolResult.Fail("I found audio files, but Windows could not play any of them.");
    }

    private ToolResult CreatePlaylist(string name, string query)
    {
        string safeName = SafeName(name);
        if (safeName.Length == 0)
        {
            return ToolResult.Fail("A playlist needs a name.");
        }

        if (PlaylistExists(safeName))
        {
            return ToolResult.Fail($"A playlist called '{name}' already exists.");
        }

        List<string> tracks = new();
        if (!string.IsNullOrWhiteSpace(query))
        {
            RefreshLibrary();
            tracks = ResolveQuery(query).ToList();
        }

        SavePlaylist(safeName, tracks);
        return ToolResult.Ok(tracks.Count == 0
                ? $"Playlist '{name}' is created and empty."
                : $"Playlist '{name}' created with {tracks.Count} track(s).",
            spokenText: $"Playlist '{name}' is ready.");
    }

    private ToolResult AddToPlaylist(string name, string query)
    {
        string safeName = SafeName(name);
        if (safeName.Length == 0)
        {
            return ToolResult.Fail("Which playlist should I add to? Give me its name.");
        }

        if (!PlaylistExists(safeName))
        {
            return ToolResult.Fail($"There is no playlist called '{name}' yet. Ask me to create it first.");
        }

        RefreshLibrary();
        List<string> added = ResolveQuery(query).ToList();
        if (added.Count == 0)
        {
            return ToolResult.Fail($"Nothing in the library matches '{query}'.");
        }

        List<string> tracks = LoadPlaylist(safeName);
        int existing = tracks.Count;
        tracks.AddRange(added.Where(track => !tracks.Contains(track, StringComparer.OrdinalIgnoreCase)));
        SavePlaylist(safeName, tracks);

        return ToolResult.Ok(
            $"Added {tracks.Count - existing} new track(s) to '{name}' (now {tracks.Count} total).",
            spokenText: $"Added to '{name}'.");
    }

    private ToolResult PlayPlaylist(string name)
    {
        string safeName = SafeName(name);
        if (safeName.Length == 0)
        {
            return ToolResult.Fail("Which playlist should I play? Give me its name.");
        }

        if (!PlaylistExists(safeName))
        {
            return ToolResult.Fail($"There is no playlist called '{name}'.");
        }

        List<string> tracks = LoadPlaylist(safeName).Where(File.Exists).ToList();
        if (tracks.Count == 0)
        {
            return ToolResult.Fail($"'{name}' has no playable tracks left on disk.");
        }

        SavePlaylist(safeName, tracks);
        _queue.Clear();
        _queue.AddRange(tracks);
        _queueIndex = 0;
        if (!StartTrackPath(_queue[0]))
        {
            _queue.Clear();
            _queueIndex = -1;
            return ToolResult.Fail("The playlist is saved, but Windows could not play its first track.");
        }
        string first = Path.GetFileNameWithoutExtension(_queue[0]);
        return ToolResult.Ok($"Playing playlist '{name}': {tracks.Count} track(s), starting with {first}.",
            spokenText: $"Playing playlist {name}.");
    }

    private IReadOnlyList<string> PlaylistNames()
    {
        if (!Directory.Exists(PlaylistsDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(PlaylistsDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private bool PlaylistExists(string safeName) =>
        File.Exists(Path.Combine(PlaylistsDirectory, safeName + ".json"));

    private List<string> LoadPlaylist(string safeName)
    {
        string path = Path.Combine(PlaylistsDirectory, safeName + ".json");
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>()
                : new List<string>();
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Music", $"Playlist '{safeName}' could not be read: {exception.Message}");
            return new List<string>();
        }
    }

    private void SavePlaylist(string safeName, List<string> tracks)
    {
        Directory.CreateDirectory(PlaylistsDirectory);
        string path = Path.Combine(PlaylistsDirectory, safeName + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(tracks, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static string SafeName(string? name)
    {
        string invalid = new(Path.GetInvalidFileNameChars());
        string safe = new((name ?? string.Empty).Trim().Where(c => !invalid.Contains(c)).ToArray());
        return safe.Length == 0 ? string.Empty : safe;
    }

    /// <summary>Every library track that matches a free-text query, best first.</summary>
    private IEnumerable<string> ResolveQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        return _library
            .Select(path => (Path: path, Score: MatchScore(query, path)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .Select(match => match.Path);
    }

    private static int MatchScore(string query, string filePath)
    {
        IReadOnlyList<string> queryWords = Tokenize(query);
        IReadOnlyList<string> fileWords = Tokenize(Path.GetFileNameWithoutExtension(filePath));
        return queryWords.Count(word =>
            fileWords.Any(fileWord => fileWord.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    private bool StartTrack(int index)
    {
        if (index < 0 || index >= _library.Count)
        {
            return false;
        }

        // Manual play always leaves playlist mode: the queue belongs to a playlist
        // until the user touches playback themselves.
        ClearQueue();
        return StartTrackPath(_library[index]);
    }

    /// <summary>Starts one track by full path. Playlist queues hold paths directly, so
    /// a rescan of the library can never break which song comes next.</summary>
    private bool StartTrackPath(string filePath)
    {
        lock (_playerLock)
        {
            ReleasePlayer();

            try
            {
                _audioReader = new AudioFileReader(filePath);
                _outputDevice = new WaveOutEvent
                {
                    // Music honours the same output selector as Iris' speech. -1 is
                    // the Windows default, which survives device changes gracefully.
                    DeviceNumber = _context?.Settings.Voice.OutputDeviceNumber ?? -1
                };
                _outputDevice.PlaybackStopped += OnPlaybackStopped;
                _outputDevice.Init(_audioReader);
                _outputDevice.Play();

                _currentIndex = _library.FindIndex(candidate =>
                    candidate.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                _nowPlaying.Title = Path.GetFileNameWithoutExtension(filePath);
                _nowPlaying.Status = "Playing";
                return true;
            }
            catch (Exception exception)
            {
                ReleasePlayer();
                _nowPlaying.Status = "Stopped";
                _context?.Events.PublishNotice($"That track would not play: {exception.Message}",
                    NoticeSeverity.Warning);
                return false;
            }
        }
    }

    private void ClearQueue()
    {
        _queue.Clear();
        _queueIndex = -1;
        _playbackGeneration++;
    }

    private int FindBestMatch(string query)
    {
        IReadOnlyList<string> queryWords = Tokenize(query);
        int bestIndex = 0;
        int bestScore = 0;

        for (int index = 0; index < _library.Count; index++)
        {
            IReadOnlyList<string> fileWords = Tokenize(Path.GetFileNameWithoutExtension(_library[index]));
            int score = queryWords.Count(word => fileWords.Any(fileWord =>
                fileWord.Contains(word, StringComparison.OrdinalIgnoreCase)));

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestScore == 0 ? -1 : bestIndex;
    }

    private static IReadOnlyList<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(new[] { ' ', '-', '_', '.', ',' }, StringSplitOptions.RemoveEmptyEntries);

    private void RefreshLibrary(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _lastLibraryRefresh < MinimumAutomaticLibraryRefresh)
        {
            return;
        }

        List<string> found = new();
        HashSet<string> knownPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in LibraryDirectories())
        {
            try
            {
                foreach (string filePath in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    string extension = Path.GetExtension(filePath).ToLowerInvariant();
                    if (SupportedExtensions.Contains(extension) &&
                        knownPaths.Add(filePath))
                    {
                        found.Add(filePath);
                    }
                }
            }
            catch (Exception exception)
            {
                CompanionLog.Trace("Music", $"Skipping a music folder during refresh: {exception.Message}");
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        _library.Clear();
        _library.AddRange(found);
        _nowPlaying.TrackCount = _library.Count;
        _lastLibraryRefresh = DateTimeOffset.UtcNow;
    }

    private void UpdateProgress()
    {
        try
        {
            lock (_playerLock)
            {
                if (_audioReader is null || _outputDevice is null)
                {
                    return;
                }

                TimeSpan position = _audioReader.CurrentTime;
                TimeSpan total = _audioReader.TotalTime;

                _nowPlaying.PositionText = $"{position:m\\:ss} / {total:m\\:ss}";
                _nowPlaying.Progress = total.TotalSeconds > 0
                    ? Math.Clamp(position.TotalSeconds / total.TotalSeconds, 0, 1)
                    : 0;
                _nowPlaying.Status = _outputDevice.PlaybackState == PlaybackState.Playing ? "Playing" : "Paused";
            }
        }
        catch
        {
            // A device change mid-song must not break the timer.
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs eventArgs)
    {
        // Replacing/stopping a device also raises PlaybackStopped. Only the device
        // that is still current may advance a queue; an old output completing late
        // must never skip the newly selected track.
        lock (_playerLock)
        {
            if (!ReferenceEquals(sender, _outputDevice))
            {
                return;
            }
        }

        if (eventArgs.Exception is not null)
        {
            _nowPlaying.Status = "Stopped";
            return;
        }

        // The device is being disposed while this event is still in flight, so the
        // queue advance happens on another thread and only if no stop was commanded
        // in the meantime.
        int generation = _playbackGeneration;
        _ = Task.Run(() =>
        {
            if (_playbackGeneration == generation)
            {
                ContinueQueue();
            }
        });
    }

    /// <summary>Plays the next playlist entry when one ends naturally. When the queue
    /// is exhausted she stops and waits — a companion that keeps playing into an empty
    /// room is worse than one that knows a playlist finished.</summary>
    private void ContinueQueue()
    {
        if (_queue.Count == 0 || _queueIndex < 0)
        {
            return;
        }

        _queueIndex++;
        if (_queueIndex >= _queue.Count)
        {
            ClearQueue();
            _nowPlaying.Status = "Stopped";
            _nowPlaying.Progress = 0;
            _nowPlaying.PositionText = "0:00";
            return;
        }

        StartTrackPath(_queue[_queueIndex]);
    }

    private void StopPlayback()
    {
        lock (_playerLock)
        {
            ClearQueue();
            ReleasePlayer();
            _nowPlaying.Status = "Stopped";
            _nowPlaying.Progress = 0;
            _nowPlaying.PositionText = "0:00";
        }
    }

    private void ReleasePlayer()
    {
        if (_outputDevice is not null)
        {
            try
            {
                _outputDevice.PlaybackStopped -= OnPlaybackStopped;
                _outputDevice.Stop();
                _outputDevice.Dispose();
            }
            catch
            {
                // Device already gone.
            }

            _outputDevice = null;
        }

        try
        {
            _audioReader?.Dispose();
        }
        catch
        {
            // Same here.
        }

        _audioReader = null;
    }
}
