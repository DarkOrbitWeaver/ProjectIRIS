using System.Diagnostics;
using System.Security;

namespace CompanionOS.Modules;

/// <summary>A bounded scan result; partial scans state why they stopped.</summary>
public sealed record MusicLibraryScanResult(
    IReadOnlyList<string> Tracks,
    int DirectoriesVisited,
    int EntriesVisited,
    int UnavailableItems,
    int LinksSkipped,
    bool WasLimited,
    string? LimitReason,
    TimeSpan Duration);

public readonly record struct MusicLibraryScanProgress(int DirectoriesVisited, int EntriesVisited, int TracksFound);

/// <summary>
/// Walks user-selected music roots with explicit resource limits. It never follows directory
/// junctions/symlinks, stops promptly between filesystem entries when cancelled, and returns
/// partial results with a reason instead of hiding an unbounded drive scan.
/// </summary>
public static class MusicLibraryScanner
{
    public const int DefaultMaximumRoots = 64;
    public const int DefaultMaximumDirectories = 2_000;
    public const int DefaultMaximumEntries = 50_000;
    public const int DefaultMaximumTracks = 10_000;
    public const int DefaultMaximumDepth = 24;
    public static readonly TimeSpan DefaultTimeBudget = TimeSpan.FromSeconds(4);

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".aiff", ".aif", ".flac", ".m4a", ".aac", ".wma"
    };

    public static bool IsSupportedAudioFile(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    public static MusicLibraryScanResult Scan(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default,
        TimeSpan? timeBudget = null,
        int maximumRoots = DefaultMaximumRoots,
        int maximumDirectories = DefaultMaximumDirectories,
        int maximumEntries = DefaultMaximumEntries,
        int maximumTracks = DefaultMaximumTracks,
        int maximumDepth = DefaultMaximumDepth,
        Action<MusicLibraryScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRoots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDirectories, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTracks, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDepth);

        TimeSpan budget = timeBudget ?? DefaultTimeBudget;
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeBudget));
        }

        Stopwatch clock = Stopwatch.StartNew();
        Queue<(string Path, int Depth)> pending = new();
        HashSet<string> queuedDirectories = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> knownTracks = new(StringComparer.OrdinalIgnoreCase);
        List<string> tracks = new();
        int directoriesVisited = 0;
        int entriesVisited = 0;
        int unavailableDirectories = 0;
        int linksSkipped = 0;
        int rootsVisited = 0;
        bool wasLimited = false;
        bool rootsLimited = false;
        string? rootLimitReason = null;
        string? limitReason = null;

        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= budget)
            {
                rootsLimited = true;
                rootLimitReason = $"time budget of {budget.TotalSeconds:0.#} seconds";
                break;
            }
            if (rootsVisited >= maximumRoots)
            {
                rootsLimited = true;
                rootLimitReason = $"music-folder root limit of {maximumRoots}";
                break;
            }
            rootsVisited++;
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            try
            {
                string fullPath = Path.GetFullPath(root);
                bool exists = Directory.Exists(fullPath);
                if (exists && queuedDirectories.Add(fullPath))
                {
                    pending.Enqueue((fullPath, 0));
                }
                else if (!exists)
                {
                    unavailableDirectories++;
                }
            }
            catch (Exception exception) when (IsFilesystemException(exception))
            {
                unavailableDirectories++;
            }
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= budget)
            {
                wasLimited = true;
                limitReason = $"time budget of {budget.TotalSeconds:0.#} seconds";
                break;
            }
            if (directoriesVisited >= maximumDirectories)
            {
                wasLimited = true;
                limitReason = $"folder limit of {maximumDirectories:N0}";
                break;
            }

            (string directory, int depth) = pending.Dequeue();
            try
            {
                FileAttributes directoryAttributes = File.GetAttributes(directory);
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    linksSkipped++;
                    continue;
                }

                directoriesVisited++;
                using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directory,
                    "*", SearchOption.TopDirectoryOnly).GetEnumerator();
                while (entries.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock.Elapsed >= budget)
                    {
                        wasLimited = true;
                        limitReason = $"time budget of {budget.TotalSeconds:0.#} seconds";
                        break;
                    }
                    if (entriesVisited >= maximumEntries)
                    {
                        wasLimited = true;
                        limitReason = $"entry limit of {maximumEntries:N0}";
                        break;
                    }

                    string path = entries.Current;
                    entriesVisited++;
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            linksSkipped++;
                            continue;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (depth >= maximumDepth)
                            {
                                wasLimited = true;
                                limitReason = $"folder depth limit of {maximumDepth}";
                                continue;
                            }

                            if (queuedDirectories.Add(path))
                            {
                                pending.Enqueue((path, depth + 1));
                            }
                        }
                        else if (IsSupportedAudioFile(path) && knownTracks.Add(path))
                        {
                            if (tracks.Count >= maximumTracks)
                            {
                                wasLimited = true;
                                limitReason = $"track limit of {maximumTracks:N0}";
                                break;
                            }

                            tracks.Add(path);
                        }
                    }
                    catch (Exception exception) when (IsFilesystemException(exception))
                    {
                        unavailableDirectories++;
                    }
                }
            }
            catch (Exception exception) when (IsFilesystemException(exception))
            {
                unavailableDirectories++;
            }

            if (directoriesVisited % 32 == 0 || wasLimited)
            {
                progress?.Invoke(new MusicLibraryScanProgress(directoriesVisited, entriesVisited, tracks.Count));
            }

            if (wasLimited)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (rootsLimited && !wasLimited)
        {
            wasLimited = true;
            limitReason = rootLimitReason;
        }
        tracks.Sort(StringComparer.OrdinalIgnoreCase);
        progress?.Invoke(new MusicLibraryScanProgress(directoriesVisited, entriesVisited, tracks.Count));
        return new MusicLibraryScanResult(tracks, directoriesVisited, entriesVisited,
            unavailableDirectories, linksSkipped, wasLimited, limitReason, clock.Elapsed);
    }

    private static bool IsFilesystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException;
}
