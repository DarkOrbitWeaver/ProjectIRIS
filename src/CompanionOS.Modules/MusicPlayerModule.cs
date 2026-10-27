using CompanionOS.Core.Common;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;
using CompanionOS.Modules;
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
    private bool _isScanning;
    private string _libraryStatus = "Library scan has not run yet";

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

    public bool IsScanning
    {
        get => _isScanning;
        set => SetProperty(ref _isScanning, value);
    }

    public string LibraryStatus
    {
        get => _libraryStatus;
        set => SetProperty(ref _libraryStatus, value);
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
public sealed class MusicPlayerModule : IAssistantModule, IModuleSurfaceProvider, IModuleActionDescriber, IDisposable
{
    private const int MaximumCachedTracks = MusicLibraryScanner.DefaultMaximumTracks * 2;
    private const int MaximumBrowseFolders = 40;
    private const int MaximumBrowseFilesToInspect = 500;
    // Formats Windows can normally decode through NAudio without a third-party codec.
    // Unsupported files are skipped rather than poisoning an otherwise good scan.
    private readonly NowPlayingModel _nowPlaying = new();
    private List<string> _library = new();
    private readonly SemaphoreSlim _libraryScanGate = new(1, 1);
    private readonly CancellationTokenSource _moduleLifetime = new();
    private readonly object _playerLock = new();

    private ModuleContext? _context;
    private WaveOutEvent? _outputDevice;
    private AudioFileReader? _audioReader;
    private Timer? _progressTimer;
    private int _currentIndex = -1;
    private bool _isDisposed;
    private DateTimeOffset _lastLibraryRefresh = DateTimeOffset.MinValue;
    private MusicLibraryScanResult? _lastScan;

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

    /// <summary>
    /// The user's chosen volume, remembered across track changes and ducks. Each
    /// track is a new <see cref="AudioFileReader"/> with its own 1.0 volume, so the
    /// level used to die at every song boundary and the duck restore used to reset
    /// to full blast — "quieter" lasted one song (2026-10-27 audit).
    /// </summary>
    private float _currentVolume = 1.0f;
    private bool _isDucked;

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
            "Play a track from her music library. \"A song\", \"anything\", \"any track\" and similar vague asks mean: call this WITHOUT a query and let the library pick; do not invent mood filters.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"query":{"type":"string","description":"Track or artist name. Optional — leave empty to play whatever the library serves."}},"required":[]}""",
            "play music", "play", "song", "track", "put on"),

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

        new("list_music", "List the tracks available in her music library. Pass limit for more than the default first 20.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"limit":{"type":"integer","description":"How many titles to show, 1-200 (default 20)"}},"required":[]}""",
            "what music", "library", "playlist", "player"),

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
        _ = InitializeLibraryIndexAsync(cancellationToken);

        _progressTimer = new Timer(_ => UpdateProgress(), null, 500, 500);

        // The player must survive a restart of the interface: no state is kept in the
        // audio device, so simply re-scanning the folder is enough.
        return Task.CompletedTask;
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        switch (invocation.Tool.Name)
        {
            case "play_music":
                await RefreshLibraryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return Play(invocation.GetString("query"));
            case "pause_music":
                lock (_playerLock)
                {
                    if (_outputDevice is null) return ToolResult.Fail("Nothing is playing.");
                    _outputDevice.Pause();
                    _nowPlaying.Status = "Paused";
                    return ToolResult.Ok("Music paused.", "Paused.");
                }
            case "resume_music":
                lock (_playerLock)
                {
                    if (_outputDevice is null) return Play(string.Empty);
                    _outputDevice.Play();
                    _nowPlaying.Status = "Playing";
                    return ToolResult.Ok("Music resumed.", "Back on.");
                }
            case "next_track":
                return PlayNext();
            case "shuffle_music":
                await RefreshLibraryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return ShuffleMusic();
            case "stop_music":
                StopPlayback();
                return ToolResult.Ok("Music stopped.", "Stopped.");
            case "set_music_volume":
            {
                int level = Math.Clamp(invocation.GetInt32("level", 60), 0, 100);
                lock (_playerLock)
                {
                    // The chosen level is remembered, not just applied: the next track
                    // spawns a fresh reader and previously spat it back to 100% while
                    // still reporting success.
                    _currentVolume = level / 100f;
                    _isDucked = false;
                    if (_audioReader is not null) _audioReader.Volume = _currentVolume;
                }
                return ToolResult.Ok($"Music volume set to {level} percent.");
            }
            case "list_music":
            {
                MusicLibraryScanResult scan = await RefreshLibraryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (_library.Count == 0)
                    return ToolResult.Ok(scan.WasLimited
                        ? $"The bounded scan has not found tracks yet; it reached the {scan.LimitReason}. Browse a smaller folder to continue."
                        : "No audio files yet. Name your music folders in settings, or drop files into the workspace Music folder.");
                // A hard 20-title page on a many-thousand library was an evidence gap
                // with no way deeper; limit (to 200) opens the window (2026-10-27 audit).
                int limit = Math.Clamp(invocation.GetInt32("limit", 20), 1, 200);
                string listing = string.Join("; ", _library.Take(limit).Select(Path.GetFileNameWithoutExtension));
                return ToolResult.Ok(
                    $"{_library.Count} indexed track(s)" +
                    $"{(scan.WasLimited ? $" (partial: {scan.LimitReason})" : string.Empty)}; " +
                    $"showing first {Math.Min(limit, _library.Count)}: {listing}",
                    displayText: listing);
            }
            case "add_music_folder":
                return await AddMusicFolderAsync(invocation.GetString("path"), cancellationToken).ConfigureAwait(false);
            case "browse_music_folder":
                return await Task.Run(() => BrowseMusicFolder(invocation.GetString("path")), cancellationToken)
                    .ConfigureAwait(false);
            case "remove_music_folder":
                return await RemoveMusicFolderAsync(invocation.GetString("path"), cancellationToken).ConfigureAwait(false);
            case "refresh_music_library":
            {
                IModuleTaskHandle? card = _context?.Ui.BeginTask(new ModuleTaskSpec
                {
                    Title = "Indexing music library",
                    IconKey = "music",
                    Detail = "Looking through her music folders"
                }, cancellationToken);

                MusicLibraryScanResult scan;
                try
                {
                    scan = await RefreshLibraryAsync(force: true, cancellationToken,
                        onProgress: card is null
                            ? null
                            : progress => card.Step(
                                $"{progress.TracksFound:N0} tracks in {progress.DirectoriesVisited:N0} folders"))
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    card?.Cancel("Scan stopped; the previous library stays put");
                    throw;
                }
                catch (Exception)
                {
                    card?.Fail("The scan did not finish; her previous index stays put");
                    throw;
                }

                card?.Step(_library.Count == 0
                    ? "No playable audio turned up"
                    : $"{_library.Count:N0} tracks are ready to play");
                card?.Complete(_library.Count == 0
                    ? "Found nothing playable yet"
                    : $"Indexed {_library.Count:N0} tracks");
                return ToolResult.Ok(DescribeScan(scan),
                    spokenText: _library.Count == 0 ? "I couldn't find playable music there yet." : ScanSpokenSummary(scan));
            }
            case "show_music_overlay":
                return SetMusicOverlayVisible(true);
            case "hide_music_overlay":
                return SetMusicOverlayVisible(false);
            case "duck_music":
            {
                bool shouldDuck = invocation.GetBoolean("ducked", true);
                lock (_playerLock)
                {
                    _isDucked = shouldDuck;
                    if (_audioReader is not null)
                    {
                        // Restore remembers the user's level, not full blast: the old
                        // hard 1.0 erased a chosen 40% after every spoken reply.
                        double target = shouldDuck
                            ? Math.Max(0.05, _context?.Settings.Voice.MusicDuckLevel ?? 0.25)
                            : _currentVolume;
                        _audioReader.Volume = (float)target;
                    }
                }
                return ToolResult.Ok(shouldDuck ? "Music lowered." : "Music restored.");
            }
            case "previous_track":
                return PlayPrevious();
            case "list_playlists":
                return ListPlaylists();
            case "create_playlist":
            {
                string query = invocation.GetString("query");
                if (!string.IsNullOrWhiteSpace(query))
                    await RefreshLibraryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return CreatePlaylist(invocation.GetString("name"), query);
            }
            case "add_to_playlist":
                await RefreshLibraryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return AddToPlaylist(invocation.GetString("name"), invocation.GetString("query"));
            case "play_playlist":
                return PlayPlaylist(invocation.GetString("name"));
            default:
                return ToolResult.Fail($"Unknown music tool '{invocation.Tool.Name}'.");
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

    public string DescribeAction(ModuleTool tool, JsonElement arguments)
    {
        string requestedPath = arguments.ValueKind == JsonValueKind.Object &&
                               arguments.TryGetProperty("path", out JsonElement pathValue)
            ? pathValue.ValueKind == JsonValueKind.String ? pathValue.GetString() ?? string.Empty : pathValue.ToString()
            : string.Empty;
        string target = tool.Name switch
        {
            "browse_music_folder" => ResolveBrowseDirectory(requestedPath),
            "add_music_folder" => string.IsNullOrWhiteSpace(requestedPath)
                ? _browseDirectory
                : ExpandDirectory(requestedPath),
            "remove_music_folder" => ExpandDirectory(requestedPath),
            _ => string.Empty
        };
        string safeTarget = SanitizePermissionTarget(target);

        return tool.Name switch
        {
            "browse_music_folder" => $"read the folder names and supported-audio count inside '{safeTarget}' (read-only)",
            "add_music_folder" => $"add '{safeTarget}' to the music library and index its audio files; no source files will be changed",
            "remove_music_folder" => $"stop indexing '{safeTarget}' in the music library; no files will be deleted",
            _ => tool.Description.TrimEnd('.').ToLowerInvariant()
        };
    }

    private static string SanitizePermissionTarget(string path)
    {
        char[] safe = path.Where(character => !char.IsControl(character) && character != '\'')
            .Take(240).ToArray();
        return safe.Length == 0 ? "the selected music folder" : new string(safe);
    }

    public Task ShutdownAsync()
    {
        _moduleLifetime.Cancel();
        StopPlayback();
        _progressTimer?.Dispose();
        _progressTimer = null;
        return Task.CompletedTask;
    }

    private async Task InitializeLibraryIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshLibraryAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Runtime shutdown owns this cancellation; the module will initialize again next launch.
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Music", "Initial music index did not finish; the player remains available.", exception);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _moduleLifetime.Cancel();
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

            if (!string.IsNullOrWhiteSpace(expanded) &&
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
    private async Task<ToolResult> AddMusicFolderAsync(string requestedPath, CancellationToken cancellationToken)
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

        if (!await Task.Run(() => Directory.Exists(path), cancellationToken).ConfigureAwait(false))
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

        MusicLibraryScanResult scan = await RefreshLibraryAsync(force: true, cancellationToken,
            preferredRoot: path).ConfigureAwait(false);
        int foundHere = _library.Count(candidate => IsWithinDirectory(path, candidate));
        return ToolResult.Ok(
            $"Added '{path}'. The scan found {foundHere} playable track(s) there; the library now has {_library.Count}. {ScanLimitSuffix(scan)}",
            spokenText: scan.WasLimited
                ? foundHere == 0
                    ? "I added the folder, but my scan limit was reached before I could check it fully. Want me to browse a smaller folder?"
                    : $"I found at least {foundHere} tracks before the scan limit. Would you like me to play one?"
                : foundHere == 0
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
            List<string> children = new();
            foreach (string childPath in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
                         .Take(MaximumBrowseFolders + 1))
            {
                try
                {
                    if ((File.GetAttributes(childPath) & FileAttributes.ReparsePoint) == 0)
                        children.Add(Path.GetFileName(childPath));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // This child disappeared or became inaccessible while being listed.
                }
            }
            children = children.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumBrowseFolders).ToList();

            int directTracks = 0;
            int entriesChecked = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                         .Take(MaximumBrowseFilesToInspect + 1))
            {
                entriesChecked++;
                if (MusicLibraryScanner.IsSupportedAudioFile(file)) directTracks++;
            }
            bool trackCountIsPartial = entriesChecked > MaximumBrowseFilesToInspect;

            _browseDirectory = path;
            string folderList = children.Count == 0 ? "no child folders" : string.Join(", ", children);
            string suffix = children.Count == MaximumBrowseFolders ? $" (showing up to {MaximumBrowseFolders})" : string.Empty;
            string trackCount = trackCountIsPartial ? $"at least {directTracks}" : directTracks.ToString();
            string response = $"Opened '{path}'. I see {folderList}{suffix}; {trackCount} playable audio file(s) directly here. " +
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

        string expandedRequest = Environment.ExpandEnvironmentVariables(request);
        if (Path.IsPathRooted(expandedRequest) || expandedRequest.StartsWith('~'))
        {
            return ExpandDirectory(expandedRequest);
        }

        try
        {
            return Path.GetFullPath(Path.Combine(_browseDirectory, expandedRequest));
        }
        catch
        {
            return request;
        }
    }

    private async Task<ToolResult> RemoveMusicFolderAsync(string requestedPath, CancellationToken cancellationToken)
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
        await RefreshLibraryAsync(force: true, cancellationToken, new[] { path }).ConfigureAwait(false);
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

    private static bool IsWithinDirectory(string root, string candidate)
    {
        try
        {
            string relative = Path.GetRelativePath(root, candidate);
            return !Path.IsPathRooted(relative) && relative != ".." &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Playlists persist as plain JSON next to her workspace: readable, copyable,
    /// never a database her owner cannot open.</summary>
    private string PlaylistsDirectory =>
        Path.Combine(_context?.WorkspaceDirectory ?? Path.GetTempPath(), "playlists");

    private ToolResult Play(string query)
    {
        if (_library.Count == 0)
        {
            return ToolResult.Fail(
                (_lastScan?.WasLimited == true
                    ? $"My bounded scan stopped at the {_lastScan.LimitReason} before it found tracks. Browse a smaller folder. "
                    : "I found no audio files. ") +
                "Name the folders that hold your music in settings " +
                "(MusicSearchDirectories), or drop files into my workspace Music folder.");
        }

        // An empty query IS the "anything"/"something" ask: the tool description
        // promises a library pick, and the old code replayed the current track —
        // which felt like nothing happened at all. A random pick is the honest read.
        int chosenIndex = string.IsNullOrWhiteSpace(query)
            ? Random.Shared.Next(_library.Count)
            : FindBestMatch(query);

        if (chosenIndex < 0)
        {
            // Evidence instead of a bare no: the closest real titles (file and folder
            // names), so the mind can match dictation noise against reality itself.
            IEnumerable<string> scored = ResolveQuery(query);
            List<string> candidates = scored.Take(5).ToList();
            if (candidates.Count == 0)
            {
                candidates = _library.OrderBy(candidate => Random.Shared.Next())
                    .Take(5).Select(track => Path.GetFileNameWithoutExtension(track)!).ToList();
                return ToolResult.Fail(
                    $"I couldn't find a track matching '{query}' in {_library.Count} indexed tracks. " +
                    (!string.IsNullOrWhiteSpace(query)
                        ? "Here is some of what is here instead: " + string.Join("; ", candidates) + ". "
                        : string.Empty) +
                    (_lastScan?.WasLimited == true ? "The index is partial; browse a smaller folder to search more. " : string.Empty));
            }

            return ToolResult.Fail(
                $"There is no exact '{query}' in my library, but these look closest: " +
                $"{string.Join("; ", candidates)}. Name one to play it." +
                (_lastScan?.WasLimited == true ? " (The index is partial; browse a smaller folder to search more.)" : string.Empty));
        }

        if (!StartTrack(chosenIndex))
        {
            return ToolResult.Fail(
                _audioReader is not null
                    ? "I could not switch to that track; what was playing still is. Check the file."
                    : "I found that track, but Windows could not play it. Check the file and selected audio output.");
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
            List<string>? tracks = LoadPlaylist(name);
            return $"{name} — {(tracks is null ? "unreadable file" : $"{tracks.Count} track(s)")}";
        }));

        return ToolResult.Ok(lines, displayText: lines);
    }

    private ToolResult ShuffleMusic()
    {
        if (_library.Count == 0)
        {
            return ToolResult.Fail(_lastScan?.WasLimited == true
                ? $"My bounded scan stopped at the {_lastScan.LimitReason} before it found tracks. Browse a smaller folder."
                : "I have no music to shuffle yet. Add a folder with your tracks first.");
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
        bool partialLibrary = _lastScan?.WasLimited == true;
        return ToolResult.Ok(partialLibrary
                ? $"Shuffling the indexed tracks. Playing {title} first; the library scan is partial."
                : $"Shuffling the library. Playing {title} first.",
            partialLibrary
                ? $"Shuffling the tracks I found so far. {title} is first."
                : $"Shuffling your music. {title} is first.", title);
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
            tracks = ResolveQuery(query).ToList();
        }

        SavePlaylist(safeName, tracks);
        bool partialLibrary = !string.IsNullOrWhiteSpace(query) && _lastScan?.WasLimited == true;
        return ToolResult.Ok(tracks.Count == 0
                ? partialLibrary
                    ? $"Playlist '{name}' is empty so far; the library index is partial."
                    : $"Playlist '{name}' is created and empty."
                : $"Playlist '{name}' created with {tracks.Count} track(s){(partialLibrary ? " from the partial library index" : string.Empty)}.",
            spokenText: partialLibrary
                ? $"Playlist '{name}' is ready with the tracks found so far."
                : $"Playlist '{name}' is ready.");
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
            return MissingPlaylistResult(name,
                "Add to the closest real spelling, or create the missing playlist first.");
        }

        List<string> added = ResolveQuery(query).ToList();
        if (added.Count == 0)
        {
            return ToolResult.Fail($"Nothing in the library matches '{query}'.");
        }

        List<string>? storedTracks = LoadPlaylist(safeName);
        if (storedTracks is null)
        {
            return ToolResult.Fail(
                $"The playlist file for '{name}' exists but is unreadable, so I will not touch it: " +
                "appending into a broken file would have erased what was already inside. " +
                "Delete the playlist and create it again.");
        }

        List<string> tracks = storedTracks;
        int existing = tracks.Count;
        tracks.AddRange(added.Where(track => !tracks.Contains(track, StringComparer.OrdinalIgnoreCase)));
        SavePlaylist(safeName, tracks);

        return ToolResult.Ok(
            $"Added {tracks.Count - existing} new track(s) to '{name}' (now {tracks.Count} total)" +
            (_lastScan?.WasLimited == true ? " from the partial library index." : "."),
            spokenText: _lastScan?.WasLimited == true
                ? $"I added the matching tracks I found so far to '{name}'."
                : $"Added to '{name}'.");
    }

    /// <summary>
    /// The refusal that carries its own evidence: the model sees the real playlist
    /// names and reasons out the match itself — dictation noise ("hollowlight" for
    /// "Hollow Light") is resolved by the brain, not by matching code. The tool hands
    /// over reality; the mind infers.
    /// </summary>
    private ToolResult MissingPlaylistResult(string name, string guidance)
    {
        IReadOnlyList<string> saved = PlaylistNames();
        string evidence = saved.Count == 0
            ? "There are no saved playlists yet."
            : $" Saved playlists: {string.Join(", ", saved.Take(12))}" +
              (saved.Count > 12 ? $" … and {saved.Count - 12} more" : string.Empty) + ".";
        return ToolResult.Fail($"There is no playlist called '{name}'.{evidence} {guidance}");
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
            return MissingPlaylistResult(name,
                "Read the spellings yourself and play the closest real one; create the missing playlist only if nothing is close.");
        }

        List<string>? storedTracks = LoadPlaylist(safeName);
        if (storedTracks is null)
        {
            return ToolResult.Fail(
                $"The playlist file for '{name}' exists but its JSON is unreadable, so nothing was loaded. " +
                "Delete that playlist and re-create it, or fix the file directly.");
        }

        List<string> tracks = RecoverPlaylistTracks(storedTracks);
        if (tracks.Count == 0)
        {
            return ToolResult.Fail($"'{name}' has no playable tracks left on disk.");
        }

        // The healed list is written back, so the rescue only ever has to happen once.
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

    /// <summary>
    /// Playlist files store absolute paths, which die when a folder moves or the
    /// workspace changes underneath them — that is exactly what killed 'hollowlight'
    /// ("no playable tracks left on disk"). Recovery walks every dead entry through
    /// three chances: the moved workspace location, then the same file name against
    /// the current library index. Only tracks that survive nowhere are truly gone.
    /// </summary>
    private List<string> RecoverPlaylistTracks(List<string> stored)
    {
        List<string> recovered = new(stored.Count);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in stored)
        {
            string? resolved = null;
            if (File.Exists(entry))
            {
                resolved = entry;
            }
            else
            {
                string directory = _context?.WorkspaceDirectory ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    // A recorded path that slid deeper into the workspace, e.g. after the
                    // workspace root was moved: re-find it from this tree.
                    string anchored = Path.IsPathRooted(entry)
                        ? entry
                        : Path.Combine(directory, entry);
                    if (File.Exists(anchored))
                    {
                        resolved = anchored;
                    }
                    else
                    {
                        string fileName = Path.GetFileName(entry);
                        if (fileName.Length > 0 && File.Exists(Path.Combine(directory, fileName)))
                        {
                            resolved = Path.Combine(directory, fileName);
                        }
                    }
                }

                // Last chance: the same song under the same name somewhere in today's index.
                resolved ??= ResolveQuery(Path.GetFileNameWithoutExtension(entry)).FirstOrDefault();
            }

            if (resolved is not null && !seen.Contains(resolved))
            {
                seen.Add(resolved);
                recovered.Add(resolved);
            }
        }

        return recovered;
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

    /// <summary>Reads a playlist. A missing file is an empty list (nothing saved
    /// yet); a file that EXISTS but cannot be parsed returns NULL, so callers say
    /// "the playlist data is broken" instead of blaming the disk — the old swallow
    /// re-surfaced as "no playable tracks left on disk" for corrupt JSON
    /// (2026-10-27 audit).</summary>
    private List<string>? LoadPlaylist(string safeName)
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
            return null;
        }
    }

    private void SavePlaylist(string safeName, List<string> tracks)
    {
        Directory.CreateDirectory(PlaylistsDirectory);
        string path = Path.Combine(PlaylistsDirectory, safeName + ".json");
        string json = JsonSerializer.Serialize(tracks, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        // Write-then-move: a crash or power loss mid-write previously shredded the
        // file in place, which is how playlists ended up as the corrupt JSON the
        // loader had to defend against. The temp file only replaces the real one
        // after it serializes and closes cleanly.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
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

    /// <summary>Word evidence across file name AND folder names: "play Iron Maiden"
    /// has to find tracks inside an 'Iron Maiden - Fear of the Dark' folder even when
    /// no file name says so (2026-10-27 audit). Only the two deepest folders count —
    /// distant grandparents (C:\, Users) would make everything match everyone.</summary>
    private static int MatchScore(string query, string filePath)
    {
        IReadOnlyList<string> queryWords = Tokenize(query);
        if (queryWords.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<string> fileWords = Tokenize(Path.GetFileNameWithoutExtension(filePath));
        IReadOnlyList<string> folderWords = Tokenize(Path.GetFileName(Path.GetDirectoryName(filePath)));
        IReadOnlyList<string> parentFolderWords =
            Tokenize(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(filePath))));
        IReadOnlyList<string> candidateWords =
            fileWords.Concat(folderWords).Concat(parentFolderWords).ToList();

        return queryWords.Count(word =>
            candidateWords.Any(candidate => candidate.Contains(word, StringComparison.OrdinalIgnoreCase)));
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
            // Validate the new file BEFORE destroying the playing one. The old order
            // tore the working player down first, so one bad file killed the song that
            // was playing and left silence with no rollback (2026-10-27 audit).
            AudioFileReader? newReader;
            try
            {
                newReader = new AudioFileReader(filePath);
            }
            catch (Exception exception)
            {
                _context?.Events.PublishNotice(
                    $"That track would not play: {exception.Message}. What was playing was left alone.",
                    NoticeSeverity.Warning);
                return false;
            }

            WaveOutEvent? previousDevice = _outputDevice;
            AudioFileReader? previousReader = _audioReader;
            if (previousDevice is not null)
            {
                // Detach first so the dying device's Stopped event cannot drive the
                // queue instead of us; the handler also checks the sender, this just
                // removes the race window.
                try
                {
                    previousDevice.PlaybackStopped -= OnPlaybackStopped;
                    previousDevice.Stop();
                    previousDevice.Dispose();
                }
                catch
                {
                    // The old device was already dying; the new one still starts.
                }
            }

            try
            {
                previousReader?.Dispose();
                _audioReader = newReader;
                newReader.Volume = _isDucked
                    ? (float)Math.Max(0.05, _context?.Settings.Voice.MusicDuckLevel ?? 0.25)
                    : _currentVolume;
                _outputDevice = new WaveOutEvent
                {
                    // Music honours the same output selector as Iris' speech. -1 is
                    // the Windows default, which survives device changes gracefully.
                    DeviceNumber = _context?.Settings.Voice.OutputDeviceNumber ?? -1
                };
                _outputDevice.PlaybackStopped += OnPlaybackStopped;
                _outputDevice.Init(newReader);
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
        // One ranked search, reused from ResolveQuery: a zero-hit query must come back
        // as -1 (a genuine miss, reported to the model with candidates), not as index
        // zero — the old default played the first track while claiming a match.
        string? best = _library
            .Select(path => (Path: path, Score: MatchScore(query, path)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .Select(match => match.Path)
            .FirstOrDefault();

        return best is null
            ? -1
            : _library.FindIndex(candidate => candidate.Equals(best, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(new[] { ' ', '-', '_', '.', ',' }, StringSplitOptions.RemoveEmptyEntries);

    private async Task<MusicLibraryScanResult> RefreshLibraryAsync(
        bool force = false, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? excludedRoots = null, string? preferredRoot = null,
        Action<MusicLibraryScanProgress>? onProgress = null)
    {
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _moduleLifetime.Token);
        cancellationToken = linkedCancellation.Token;

        await _libraryScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && DateTimeOffset.UtcNow - _lastLibraryRefresh < MinimumAutomaticLibraryRefresh)
            {
                return _lastScan ?? new MusicLibraryScanResult(_library.ToArray(), 0, 0, 0, 0, false, null, TimeSpan.Zero);
            }

            _nowPlaying.IsScanning = true;
            _nowPlaying.LibraryStatus = "Scanning selected music folders…";
            Action<MusicLibraryScanProgress> reportProgress = progress =>
            {
                _nowPlaying.LibraryStatus = $"Scanning: {progress.TracksFound:N0} tracks in {progress.DirectoriesVisited:N0} folders";
                onProgress?.Invoke(progress);
            };
            List<string> roots = LibraryDirectories();
            if (!string.IsNullOrWhiteSpace(preferredRoot))
            {
                roots.RemoveAll(root => string.Equals(ExpandDirectory(root), preferredRoot,
                    StringComparison.OrdinalIgnoreCase));
                roots.Insert(0, preferredRoot);
            }
            MusicLibraryScanResult scan = await Task.Run(() => MusicLibraryScanner.Scan(
                roots, cancellationToken,
                timeBudget: TimeSpan.FromSeconds(Math.Clamp(
                    _context?.Settings.Music.ScanTimeBudgetSeconds ?? 15, 1, 600)),
                progress: reportProgress), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // If the scan hit a bound, preserve the usable previous index and merge the
            // discoveries. A huge new folder must not make familiar tracks disappear.
            // Removing a root excludes its paths only after a scan succeeds; cancellation
            // therefore leaves the previous index untouched.
            _lastScan = scan;
            if (scan.WasLimited)
            {
                IEnumerable<string> retained = _library.Where(track => excludedRoots is null ||
                    !excludedRoots.Any(root => IsWithinDirectory(root, track)));
                _library = retained.Concat(scan.Tracks)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumCachedTracks)
                    .ToList();
            }
            else
            {
                _library = scan.Tracks.ToList();
            }
            _nowPlaying.TrackCount = _library.Count;
            _lastLibraryRefresh = DateTimeOffset.UtcNow;
            _nowPlaying.LibraryStatus = ScanStatus(scan, _library.Count);
            LogLibraryScan(scan, roots.Count, cancelled: false);
            return scan;
        }
        catch (OperationCanceledException)
        {
            _nowPlaying.LibraryStatus = "Scan cancelled; previous library kept";
            LogLibraryScan(null, LibraryDirectories().Count, cancelled: true);
            throw;
        }
        catch (Exception exception)
        {
            _nowPlaying.LibraryStatus = "Scan failed; previous library kept";
            CompanionLog.Warning("Music", "Library scan failed; keeping the previous index.", exception);
            IrisEvent.Log("Music", "library.scan.failed", LogLevel.Warning,
                message: $"{exception.GetType().Name}: {exception.Message}");
            throw;
        }
        finally
        {
            _nowPlaying.IsScanning = false;
            _libraryScanGate.Release();
        }
    }

    private static string ScanStatus(MusicLibraryScanResult scan, int indexedTrackCount)
    {
        if (scan.WasLimited)
            return $"Partial list: {indexedTrackCount:N0} indexed tracks; reached the {scan.LimitReason}";
        if (scan.UnavailableItems > 0)
            return $"Ready: {scan.Tracks.Count:N0} tracks; {scan.UnavailableItems:N0} files or folders unavailable";
        if (scan.LinksSkipped > 0)
            return $"Ready: {scan.Tracks.Count:N0} tracks; skipped {scan.LinksSkipped:N0} linked items";
        return $"Ready: {scan.Tracks.Count:N0} tracks";
    }

    private static string ScanLimitSuffix(MusicLibraryScanResult scan) => scan.WasLimited
        ? $"The scan stopped at its {scan.LimitReason}; browse a smaller folder to see more."
        : scan.UnavailableItems > 0
            ? $"{scan.UnavailableItems:N0} files or folders could not be read."
            : scan.LinksSkipped > 0 ? $"{scan.LinksSkipped:N0} links were skipped for safety." : string.Empty;

    private static string DescribeScan(MusicLibraryScanResult scan) =>
        $"Music library scan found {scan.Tracks.Count:N0} playable track(s) in " +
        $"{scan.DirectoriesVisited:N0} folder(s) ({scan.Duration.TotalSeconds:0.0}s). " +
        (scan.WasLimited ? $"Partial results: reached the {scan.LimitReason}. " : string.Empty) +
        (scan.UnavailableItems > 0 ? $"{scan.UnavailableItems:N0} files or folders were unavailable. " : string.Empty) +
        (scan.LinksSkipped > 0 ? $"Skipped {scan.LinksSkipped:N0} linked items for safety." : "Scan complete.");

    private static string ScanSpokenSummary(MusicLibraryScanResult scan) => scan.WasLimited
        ? $"I found {scan.Tracks.Count:N0} tracks before the scan limit. A smaller folder will help me find more."
        : scan.UnavailableItems > 0
            ? $"I found {scan.Tracks.Count:N0} tracks. Some files or folders were unavailable."
            : scan.LinksSkipped > 0
                ? $"I found {scan.Tracks.Count:N0} tracks and skipped linked folders for safety."
                : $"I found {scan.Tracks.Count:N0} tracks.";

    private static void LogLibraryScan(MusicLibraryScanResult? scan, int roots, bool cancelled)
    {
        bool limited = scan?.WasLimited == true;
        IrisEvent.Log("Music", cancelled ? "library.scan.cancelled" : "library.scan.completed",
            cancelled || limited || (scan?.UnavailableItems ?? 0) > 0 || (scan?.LinksSkipped ?? 0) > 0
                ? LogLevel.Warning : LogLevel.Info,
            message: cancelled ? "Scan cancelled; the previous index remains active." : scan?.LimitReason,
            durationMs: scan?.Duration.TotalMilliseconds,
            data: new Dictionary<string, string?>
            {
                ["roots"] = roots.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["directories"] = (scan?.DirectoriesVisited ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["entries"] = (scan?.EntriesVisited ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["tracks"] = (scan?.Tracks.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unavailable_items"] = (scan?.UnavailableItems ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["links_skipped"] = (scan?.LinksSkipped ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["limited"] = limited.ToString()
            });
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
            // A decode or driver fault is not the end of the queue. Skip forward and
            // SAY what happened: the old handler froze silently mid-playlist and the
            // silence looked exactly like her ignoring the request (2026-10-27 audit).
            string fault = eventArgs.Exception.GetBaseException().Message;
            CompanionLog.Warning("Music", $"Playback fault: {fault}; skipping forward.", eventArgs.Exception);
            _nowPlaying.Status = "Skipped after a playback fault";
            _context?.Events.PublishNotice(
                $"That track failed to play ({fault}); skipping to the next one if there is one.",
                NoticeSeverity.Warning);
        }

        // The device is being disposed while this event is still in flight, so the
        // queue advance happens on another thread and only if no stop was commanded
        // in the meantime. (ContinueQueue re-checks everything under the player lock;
        // a commanded stop still wins.)
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
    /// <remarks>
    /// The whole advance happens under the player lock (StartTrackPath re-enters it —
    /// Monitor is re-entrant on the same thread). That closes the last stay of the
    /// stop-vs-auto-next race: a commanded stop that lands mid-advance empties the
    /// queue under the same lock, so "stop" can no longer lose to an advance that
    /// had already passed its generation check (2026-10-27 audit).
    /// </remarks>
    private void ContinueQueue()
    {
        lock (_playerLock)
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
