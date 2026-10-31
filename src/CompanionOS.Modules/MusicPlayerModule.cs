using CompanionOS.Core.Common;
using CompanionOS.Core.Configuration;
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
    private MusicLibraryImportIndex? _importIndex;
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
        "downloaded song find download import file add to library add to playlist make a playlist";

    public IReadOnlyList<ModuleTool> Tools { get; } = new List<ModuleTool>
    {
        // One player, one library, one folder page, one playlist tool. Twenty
        // single-verb tools (play_music, pause_music, resume_music, …) used to share a
        // name family that read as near-identical to a small model and ate 16 slots of
        // the 64-tool offer cap besides. Each remaining tool now has a purpose no other
        // one covers, and every action lives behind one enum the schema enforces.
        new(
            "music_player",
            "HER OWN music player: the tracks indexed from her music folders. Stops, pauses, resumes, skips, shuffles, sets the volume, ducks while she speaks, and shows/hides its overlay controls. " +
            "A 'song', 'anything', 'a track' ask means action=play with NO query — the library picks; do not pass a mood filter. " +
            "This is not the right hand for saved playlists (\"a song from my playlist\" belongs to music_playlists) nor for Windows system media like a browser YouTube video (that is control_windows_media).",
            ToolTier.Reflex,
            """{"type":"object","properties":{"action":{"type":"string","enum":["play","pause","resume","stop","next","previous","shuffle","set_volume","duck","show_controls","hide_controls"],"description":"What to do"},"query":{"type":"string","description":"Only for action=play: track or artist name. Leave empty to play whatever the library serves."},"level":{"type":"integer","description":"Only for action=set_volume: 0 to 100"},"ducked":{"type":"boolean","description":"Only for action=duck: true lowers the volume while she speaks, false restores it."}},"required":["action"]}""",
            "play music", "play", "song", "track", "put on", "pause", "resume", "skip", "next", "previous",
            "shuffle", "stop music", "volume", "louder", "quieter"),

        new(
            "music_library",
            "Her own music library: action=list shows the indexed track titles, action=refresh re-scans her music folders and reports what is playable.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"action":{"type":"string","enum":["list","refresh"],"description":"What to do with the library"},"limit":{"type":"integer","description":"Only for action=list: how many titles to show, 1-200 (default 20)."}},"required":["action"]}""",
            "what music", "library", "list tracks", "refresh music", "rescan music", "scan music", "update library"),

        new(
            "music_find_download",
            "Search only the known download/music locations for an existing audio file whose filename or nearby folder matches the query. " +
            "This is a bounded, read-only search of Iris cache downloads, workspace Music/Downloads, and the user's standard Downloads folder; " +
            "it does not search the whole PC. If several files match, return all plausible paths and ask which one. Do not import or add anything.",
            ToolTier.SystemCritical,
            """{"type":"object","properties":{"query":{"type":"string","description":"Song title, artist, or filename phrase."}},"required":["query"]}""",
            "find downloaded song", "search downloads", "where did the download go", "find downloaded music", "downloaded track")
        { Access = ToolAccess.Reads, AlwaysConfirm = true, PermissionScope = "music.downloads.read", TimeoutSeconds = 45 },

        new(
            "music_import_file",
            "Index one exact existing audio file in the music library without moving, copying, renaming, or modifying it. " +
            "The user must have selected/provided the exact absolute path; this asks before reading an external file. " +
            "After import, the path is remembered across restarts and can be added to a playlist as a separate step.",
            ToolTier.SystemCritical,
            """{"type":"object","properties":{"path":{"type":"string","description":"Exact absolute path to the existing audio file."}},"required":["path"]}""",
            "import song", "add to my library", "index this audio file", "import downloaded song", "add downloaded song to library")
        { Access = ToolAccess.Acts, AlwaysConfirm = true, PermissionScope = "music.import", TimeoutSeconds = 30 },

        new(
            "music_library_folders",
            "Music library folders: action=add indexes an existing folder and scans it (with confirmation), action=browse safely lists the folders in one location so the user can walk the tree, action=remove stops indexing a configured folder — this never deletes files. An empty path uses the folder most recently browsed.",
            ToolTier.SystemCritical,
            """{"type":"object","properties":{"action":{"type":"string","enum":["add","browse","remove"],"description":"What to do with music folders"},"path":{"type":"string","description":"For add: an existing folder such as D:\\Music. For browse: an absolute folder, a shown child-folder name, or 'up'. For remove: a configured music folder. Empty uses the currently browsed folder."}},"required":["action"]}""",
            "add music folder", "music folder", "browse music", "open folder", "go into", "remove music folder",
            "where is my music"),

        new(
            "music_playlists",
            "Her saved playlists: action=list shows names and sizes; action=inspect reads back the actual tracks; " +
            "action=create makes one (name required; optional query adds matching indexed tracks); action=add appends indexed tracks matching query; " +
            "action=add_file appends one exact file_path already indexed in the library; action=play starts one from the beginning. " +
            "An add is not complete until the playlist is read back and the requested path is present. A download is not a library or playlist entry.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"action":{"type":"string","enum":["list","inspect","create","add","add_file","play"],"description":"What to do with playlists"},"name":{"type":"string","description":"Playlist name for inspect/create/add/add_file/play."},"query":{"type":"string","description":"For create/add: part of a track name; matching indexed tracks join the playlist."},"file_path":{"type":"string","description":"For add_file: exact absolute path already imported/indexed in the music library."}},"required":["action"]}""",
            "playlist", "play my", "play playlist", "new playlist", "create playlist", "add song", "add to playlist")
    };

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _importIndex = new MusicLibraryImportIndex(context.WorkspaceDirectory);

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
            case "music_player":
                return await InvokePlayerActionAsync(invocation, cancellationToken).ConfigureAwait(false);
            case "music_library":
                return await InvokeLibraryActionAsync(invocation, cancellationToken).ConfigureAwait(false);
            case "music_find_download":
                return await Task.Run(() => FindDownloadFiles(invocation.GetString("query"), cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            case "music_import_file":
                return ImportExistingFile(invocation.GetString("path"));
            case "music_library_folders":
                return await InvokeFolderActionAsync(invocation, cancellationToken).ConfigureAwait(false);
            case "music_playlists":
                return await InvokePlaylistActionAsync(invocation, cancellationToken).ConfigureAwait(false);
            default:
                return ToolResult.Fail($"Unknown music tool '{invocation.Tool.Name}'.");
        }
    }

    private async Task<ToolResult> InvokePlayerActionAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        string action = invocation.GetString("action").Trim().ToLowerInvariant();
        switch (action)
        {
            case "play":
                await RefreshLibraryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return Play(invocation.GetString("query"));
            case "pause":
                lock (_playerLock)
                {
                    if (_outputDevice is null) return ToolResult.Fail("Nothing is playing.");
                    _outputDevice.Pause();
                    _nowPlaying.Status = "Paused";
                    return ToolResult.Ok("Music paused.", "Paused.");
                }
            case "resume":
                lock (_playerLock)
                {
                    if (_outputDevice is null) return Play(string.Empty);
                    _outputDevice.Play();
                    _nowPlaying.Status = "Playing";
                    return ToolResult.Ok("Music resumed.", "Back on.");
                }
            case "next":
                return PlayNext();
            case "previous":
                return PlayPrevious();
            case "shuffle":
                await RefreshLibraryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return ShuffleMusic();
            case "stop":
                StopPlayback();
                return ToolResult.Ok("Music stopped.", "Stopped.");
            case "set_volume":
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
            case "duck":
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
            case "show_controls":
                return SetMusicOverlayVisible(true);
            case "hide_controls":
                return SetMusicOverlayVisible(false);
            default:
                return ToolResult.Fail(
                    $"Unknown music player action '{action}'. Call again with exactly one of: play, pause, resume, " +
                    "stop, next, previous, shuffle, set_volume (needs level), duck (needs ducked), show_controls, " +
                    "hide_controls.");
        }
    }

    private async Task<ToolResult> InvokeLibraryActionAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        string action = invocation.GetString("action").Trim().ToLowerInvariant();
        switch (action)
        {
            case "list":
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
            case "refresh":
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
            default:
                return ToolResult.Fail(
                    $"Unknown music library action '{action}'. Call again with action list or refresh.");
        }
    }

    private async Task<ToolResult> InvokeFolderActionAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        string action = invocation.GetString("action").Trim().ToLowerInvariant();
        string path = invocation.GetString("path");
        switch (action)
        {
            case "add":
                return await AddMusicFolderAsync(path, cancellationToken).ConfigureAwait(false);
            case "browse":
                return await Task.Run(() => BrowseMusicFolder(path), cancellationToken)
                    .ConfigureAwait(false);
            case "remove":
                return await RemoveMusicFolderAsync(path, cancellationToken).ConfigureAwait(false);
            default:
                return ToolResult.Fail(
                    $"Unknown music folder action '{action}'. Call again with action add, browse or remove.");
        }
    }

    private async Task<ToolResult> InvokePlaylistActionAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        string action = invocation.GetString("action").Trim().ToLowerInvariant();
        string name = invocation.GetString("name");
        string query = invocation.GetString("query");
        switch (action)
        {
            case "list":
                return ListPlaylists();
            case "inspect":
                return InspectPlaylist(name);
            case "create":
                if (!string.IsNullOrWhiteSpace(query))
                    await RefreshLibraryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return CreatePlaylist(name, query);
            case "add":
                await RefreshLibraryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return AddToPlaylist(name, query);
            case "add_file":
                return AddExactFileToPlaylist(name, invocation.GetString("file_path"));
            case "play":
                return PlayPlaylist(name);
            default:
                return ToolResult.Fail(
                    $"Unknown playlist action '{action}'. Use list, inspect, create, add, add_file or play.");
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
        string action = arguments.ValueKind == JsonValueKind.Object &&
                        arguments.TryGetProperty("action", out JsonElement actionValue) &&
                        actionValue.ValueKind == JsonValueKind.String
            ? actionValue.GetString() ?? string.Empty
            : string.Empty;

        if (tool.Name == "music_import_file")
        {
            string target = SanitizePermissionTarget(requestedPath);
            return $"index the existing audio file '{target}' by reference; do not move, copy, rename or modify it";
        }

        if (tool.Name == "music_find_download")
        {
            return "read-only bounded search of Iris cache downloads, workspace Music/Downloads, and the standard user Downloads folder";
        }

        if (tool.Name == "music_playlists" && action == "add_file")
        {
            string target = arguments.ValueKind == JsonValueKind.Object &&
                            arguments.TryGetProperty("file_path", out JsonElement fileValue)
                ? SanitizePermissionTarget(fileValue.ValueKind == JsonValueKind.String ? fileValue.GetString() ?? string.Empty : fileValue.ToString())
                : "the selected indexed audio file";
            return $"add the already indexed file '{target}' to the named playlist and verify it by reading the playlist back";
        }

        if (tool.Name == "music_library_folders")
        {
            string target = action switch
            {
                "browse" => ResolveBrowseDirectory(requestedPath),
                "add" => string.IsNullOrWhiteSpace(requestedPath)
                    ? _browseDirectory
                    : ExpandDirectory(requestedPath),
                "remove" => ExpandDirectory(requestedPath),
                _ => string.Empty
            };
            string safeTarget = SanitizePermissionTarget(target);

            return action switch
            {
                "browse" => $"read the folder names and supported-audio count inside '{safeTarget}' (read-only)",
                "add" => $"add '{safeTarget}' to the music library and index its audio files; no source files will be changed",
                "remove" => $"stop indexing '{safeTarget}' in the music library; no files will be deleted",
                _ => tool.Description.TrimEnd('.').ToLowerInvariant()
            };
        }

        return tool.Description.TrimEnd('.').ToLowerInvariant();
    }

    private ToolResult ImportExistingFile(string requestedPath)
    {
        if (_importIndex is null)
        {
            return ToolResult.Fail("The music library is not initialized; no file was imported.");
        }

        if (!_importIndex.Add(requestedPath, out string fullPath, out string reason))
        {
            return ToolResult.Fail(reason);
        }

        if (!File.Exists(fullPath))
        {
            return ToolResult.Fail("The file disappeared before the library could verify it; nothing was added to the in-memory library.");
        }

        if (!_library.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            _library.Add(fullPath);
            if (_library.Count > MaximumCachedTracks)
            {
                _library.RemoveAt(_library.Count - 1);
            }
        }

        _nowPlaying.TrackCount = _library.Count;
        string title = Path.GetFileNameWithoutExtension(fullPath);
        return ToolResult.Ok(
            $"Imported '{title}' into the library by path reference. Verified file exists at {fullPath}; " +
            "the original file was not moved, copied, renamed or modified. It can now be added to a playlist as a separate step.");
    }

    private ToolResult FindDownloadFiles(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolResult.Fail("Give me a song title, artist or filename phrase to search for.");
        }
        if (_context is null)
        {
            return ToolResult.Fail("The music module is not initialized.");
        }

        string userDownloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string[] roots =
        {
            Path.Combine(_context.CacheDirectory, "downloads"),
            Path.Combine(_context.WorkspaceDirectory, "Downloads"),
            Path.Combine(_context.WorkspaceDirectory, "Music"),
            userDownloads
        };

        MusicLibraryScanResult scan = MusicLibraryScanner.Scan(
            roots,
            cancellationToken,
            timeBudget: TimeSpan.FromSeconds(4),
            maximumRoots: 8,
            maximumDirectories: 600,
            maximumEntries: 6_000,
            maximumTracks: 1_200,
            maximumDepth: 12);

        List<string> matches = scan.Tracks
            .Select(path => (Path: path, Score: MatchScore(query, path)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(match => match.Path)
            .ToList();
        string scope = string.Join("; ", roots.Select(path => $"'{path}'"));
        string partial = scan.WasLimited ? $" Search was partial: it reached the {scan.LimitReason}." : string.Empty;
        if (matches.Count == 0)
        {
            return ToolResult.Ok(
                $"No matching audio file was found. Scope checked: {scope}.{partial} " +
                "No file was imported or changed. If it is in another folder, ask the user to name or browse that folder.");
        }

        return ToolResult.Ok(
            $"Found {matches.Count} plausible existing audio file(s) in the checked locations (up to 12 shown): " +
            string.Join(" | ", matches.Select(path => $"{Path.GetFileNameWithoutExtension(path)} — {path}")) +
            $". Scope checked: {scope}.{partial} These are candidates only; do not randomly import or add one when ambiguous.");
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

        // The read-mutate-persist used to ride directly on the shared settings: a module
        // thread added to the live list the interface can serialize down the line
        // (2026-10-27 audit finding 96). The gate closes the window; a duplicate folder
        // is a no-op visit.
        bool alreadyListed = false;
        if (_context is not null)
        {
            bool changed = _context.MutateSettings(settings =>
            {
                List<string> directories = settings.Music.SearchDirectories;
                if (directories.Any(existing => string.Equals(ExpandDirectory(existing), path,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    alreadyListed = true;
                    return false;
                }

                directories.Add(path);
                return true;
            });

            if (!changed && !alreadyListed)
            {
                return ToolResult.Fail("The music folder could not be recorded. Nothing was changed.");
            }
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
        int removed = 0;
        if (_context is not null)
        {
            // Same gate as the add: RemoveAll on the live shared list now happens inside
            // the store's window, not from a module thread (2026-10-27 audit finding 96).
            _context.MutateSettings(settings =>
            {
                removed = settings.Music.SearchDirectories.RemoveAll(existing =>
                    string.Equals(ExpandDirectory(existing), path, StringComparison.OrdinalIgnoreCase));
                return removed > 0;
            });
        }

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

        _context.MutateSettings(settings =>
        {
            if (settings.Interface.ShowMusicOverlay == visible)
            {
                return false;
            }

            settings.Interface.ShowMusicOverlay = visible;
            return true;
        });
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

    private ToolResult InspectPlaylist(string name)
    {
        string safeName = SafeName(name);
        if (safeName.Length == 0)
        {
            return ToolResult.Fail("Name the playlist to inspect.");
        }
        if (!PlaylistExists(safeName))
        {
            return MissingPlaylistResult(name, "Inspect one of the real playlists listed above.");
        }

        List<string>? tracks = LoadPlaylist(safeName);
        if (tracks is null)
        {
            return ToolResult.Fail($"Playlist '{name}' exists but could not be read; I cannot verify its contents.");
        }

        string body = tracks.Count == 0
            ? $"Playlist '{name}' is empty (verified by reading its saved file)."
            : $"Playlist '{name}' has {tracks.Count} track(s), read from its saved file: " +
              string.Join("; ", tracks.Select(path => $"{Path.GetFileNameWithoutExtension(path)} — {path}"));
        return ToolResult.Ok(body);
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

        try
        {
            SavePlaylist(safeName, tracks);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Uncertain($"Playlist '{name}' may or may not have been created: {exception.Message}");
        }

        List<string>? verified = LoadPlaylist(safeName);
        if (verified is null || verified.Count != tracks.Count ||
            !tracks.All(track => verified.Contains(track, StringComparer.OrdinalIgnoreCase)))
        {
            return ToolResult.Uncertain($"Playlist '{name}' was written, but reading it back did not verify the requested contents. Check it before retrying.");
        }

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
        List<string> tracks = ResolveQuery(query).ToList();
        if (tracks.Count == 0)
        {
            return ToolResult.Fail($"Nothing in the library matches '{query}'.");
        }
        return AddTracksToPlaylist(name, tracks);
    }

    private ToolResult AddExactFileToPlaylist(string name, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath) || !Path.IsPathRooted(requestedPath))
        {
            return ToolResult.Fail("Give the exact absolute path of the imported file.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ToolResult.Fail($"That file path is invalid: {exception.Message}");
        }

        if (!File.Exists(fullPath) || !MusicLibraryScanner.IsSupportedAudioFile(fullPath))
        {
            return ToolResult.Fail("That audio file is missing or unsupported. Nothing was added.");
        }
        if (!_library.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            return ToolResult.Fail(
                "That file is not indexed in the music library yet. Import the exact existing file first; " +
                "a download alone is not a library entry.");
        }

        return AddTracksToPlaylist(name, new[] { fullPath });
    }

    private ToolResult AddTracksToPlaylist(string name, IReadOnlyList<string> requestedTracks)
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

        List<string>? storedTracks = LoadPlaylist(safeName);
        if (storedTracks is null)
        {
            return ToolResult.Fail(
                $"The playlist file for '{name}' exists but is unreadable, so I will not touch it: " +
                "appending into a broken file would have erased what was already inside. " +
                "Delete the playlist and create it again.");
        }

        List<string> expectedTracks = storedTracks.ToList();
        int existing = expectedTracks.Count;
        expectedTracks.AddRange(requestedTracks.Where(track =>
            !expectedTracks.Contains(track, StringComparer.OrdinalIgnoreCase)));

        try
        {
            SavePlaylist(safeName, expectedTracks);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Uncertain(
                $"The playlist write may have happened, but its result was not confirmed: {exception.Message}");
        }

        // Read-after-write is the only success receipt. In particular, a downloaded
        // file being present in the library never counts as playlist membership.
        List<string>? verifiedTracks = LoadPlaylist(safeName);
        if (verifiedTracks is null ||
            !requestedTracks.All(track => verifiedTracks.Contains(track, StringComparer.OrdinalIgnoreCase)))
        {
            return ToolResult.Uncertain(
                $"The playlist file was written, but reading it back did not confirm every requested track. " +
                "Check the playlist before retrying; I will not claim the add succeeded.");
        }

        int newlyAdded = verifiedTracks.Count - existing;
        string proof = string.Join("; ", requestedTracks.Select(path => Path.GetFileNameWithoutExtension(path)));
        return new ToolResult
        {
            Succeeded = true,
            // Read-after-write actually happened above, so this write is proven, not assumed.
            SelfVerified = true,
            ModelText = $"Playlist '{name}' read back from disk and verified {requestedTracks.Count} requested track(s): {proof}. " +
                        $"{newlyAdded} new track(s) added; playlist now has {verifiedTracks.Count}.",
            SpokenText = newlyAdded == 0
                ? $"That track was already in '{name}'. I verified it."
                : $"Added to '{name}' and verified it is there."
        };
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

            // The scanner covers configured folders; explicit imports are exact path
            // references to user-selected files outside those roots. Merge them without
            // copying/moving sources, and keep their existence/link validation separate.
            IReadOnlyList<string> imported = _importIndex?.Load() ?? Array.Empty<string>();
            List<string> discoveries = scan.Tracks
                .Concat(imported)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            scan = scan with { Tracks = discoveries };

            // If the scan hit a bound, preserve the usable previous index and merge the
            // discoveries. Removing a root excludes its paths only after a scan succeeds; cancellation
            // therefore leaves the previous index untouched.
            _lastScan = scan;
            if (scan.WasLimited)
            {
                IEnumerable<string> retained = _library.Where(track => excludedRoots is null ||
                    !excludedRoots.Any(root => IsWithinDirectory(root, track)));
                _library = retained.Concat(discoveries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumCachedTracks)
                    .ToList();
            }
            else
            {
                _library = discoveries.Take(MaximumCachedTracks).ToList();
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
