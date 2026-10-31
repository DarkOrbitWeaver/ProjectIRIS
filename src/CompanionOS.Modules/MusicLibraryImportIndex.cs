using System.Security;
using System.Text;
using System.Text.Json;

namespace CompanionOS.Modules;

/// <summary>
/// Persistent references to audio files explicitly imported from outside configured
/// music roots. The source files are never copied, moved, renamed or modified; this
/// file stores only absolute path references so they can be indexed after restart.
/// </summary>
public sealed class MusicLibraryImportIndex
{
    private readonly string _indexPath;
    private readonly object _gate = new();

    public MusicLibraryImportIndex(string workspaceDirectory)
    {
        string musicDirectory = Path.Combine(workspaceDirectory, "Music");
        Directory.CreateDirectory(musicDirectory);
        _indexPath = Path.Combine(musicDirectory, "imported-files.json");
    }

    public string IndexPath => _indexPath;

    /// <summary>Reads durable path references, filtering missing, unsupported and
    /// reparse-point paths without touching any source file.</summary>
    public IReadOnlyList<string> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_indexPath)) return Array.Empty<string>();
            List<string> stored;
            try
            {
                stored = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_indexPath, Encoding.UTF8)) ?? new();
            }
            catch (JsonException exception)
            {
                string backup = _indexPath + ".corrupt.bak";
                File.Copy(_indexPath, backup, overwrite: true);
                throw new InvalidDataException($"The music import index is invalid and was preserved at {backup}.", exception);
            }

            return stored
                .Where(TryValidateExistingAudioFile)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>
    /// Adds one explicitly named existing audio file. The caller must have obtained
    /// the normal SystemCritical permission first. No directory walk occurs here.
    /// </summary>
    public bool Add(string requestedPath, out string fullPath, out string reason)
    {
        fullPath = string.Empty;
        string candidate = (requestedPath ?? string.Empty).Trim();
        if (candidate.Length == 0 || !Path.IsPathRooted(candidate))
        {
            reason = "Give the exact absolute path to an existing audio file. Relative paths are not resolved against Iris's working directory.";
            return false;
        }

        try
        {
            candidate = Path.GetFullPath(candidate);
            if (!TryValidateExistingAudioFile(candidate))
            {
                reason = "That file is missing, unsupported, or passes through a link. Supported audio files are mp3, wav, aiff, flac, m4a, aac and wma.";
                return false;
            }

            lock (_gate)
            {
                List<string> paths = Load().ToList();
                bool alreadyListed = paths.Contains(candidate, StringComparer.OrdinalIgnoreCase);
                if (!alreadyListed)
                {
                    paths.Add(candidate);
                    Write(paths);
                }
            }

            fullPath = candidate;
            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or SecurityException or NotSupportedException)
        {
            reason = $"The import index could not be updated: {exception.Message}";
            return false;
        }
    }

    private static bool TryValidateExistingAudioFile(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || !MusicLibraryScanner.IsSupportedAudioFile(fullPath)) return false;

            string? current = fullPath;
            while (!string.IsNullOrWhiteSpace(current))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                string? parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    private void Write(List<string> paths)
    {
        string temporary = _indexPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(paths.OrderBy(path => path,
            StringComparer.OrdinalIgnoreCase), new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, _indexPath, overwrite: true);
    }
}
