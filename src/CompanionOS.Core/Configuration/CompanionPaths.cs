namespace CompanionOS.Core.Configuration;

/// <summary>
/// Every path she needs, resolved in exactly one place.
///
/// Why this exists: a companion app that hardcodes "C:\Users\me\Desktop" works on one machine
/// and breaks on the next. Nothing in this codebase builds a path by hand — it asks here.
///
/// Layout under the data root (default: %LOCALAPPDATA%\CompanionOS):
///   config\     settings.json, persona.json, permissions.json
///   models\     brain.gguf, brain-mmproj.gguf, whisper-base.bin
///   runtimes\   llama.cpp\win-cuda-x64\llama-server.exe
///   cache\      rendered reflex phrases, thumbnails, downloads
///   memory\     brain.db (+ WAL files), journal.jsonl, export\
///   modules\    drop-in plugins, one folder per module
///   workspace\  her own notes, downloaded music, scratch files
///   logs\       companion.log
/// </summary>
public static class CompanionPaths
{
    /// <summary>Set this environment variable to move everything somewhere else, like a big drive.</summary>
    public const string HomeVariable = "COMPANIONOS_HOME";

    private static string _dataRoot = ResolveDefaultRoot();

    /// <summary>The one folder everything lives in. Shown in settings, opened by the UI.</summary>
    public static string DataRoot => _dataRoot;

    /// <summary>Short alias, used by the paths below.</summary>
    public static string Root => _dataRoot;

    public static string ConfigDirectory => Path.Combine(_dataRoot, "config");
    public static string ModelsDirectory => Path.Combine(_dataRoot, "models");
    public static string RuntimesDirectory => Path.Combine(_dataRoot, "runtimes");

    /// <summary>Anything regenerable: rendered reflex phrases, thumbnails, downloads.</summary>
    public static string CacheDirectory => Path.Combine(_dataRoot, "cache");
    public static string MemoryDirectory => Path.Combine(_dataRoot, "memory");
    public static string ModulesDirectory => Path.Combine(_dataRoot, "modules");
    public static string WorkspaceDirectory => Path.Combine(_dataRoot, "workspace");
    public static string LogsDirectory => Path.Combine(_dataRoot, "logs");
    public static string ExportDirectory => Path.Combine(MemoryDirectory, "export");

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public static string PersonaFile => Path.Combine(ConfigDirectory, "persona.json");
    public static string PermissionsFile => Path.Combine(ConfigDirectory, "permissions.json");
    public static string BrainDatabaseFile => Path.Combine(MemoryDirectory, "brain.db");
    public static string JournalFile => Path.Combine(MemoryDirectory, "journal.jsonl");

    /// <summary>Full path of a file in the models folder. Accepts an absolute path unchanged.</summary>
    public static string Model(string fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return ModelsDirectory;
        }

        return Path.IsPathRooted(fileNameOrPath)
            ? fileNameOrPath
            : Path.Combine(ModelsDirectory, fileNameOrPath);
    }

    public static string Runtime(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return RuntimesDirectory;
        }

        return Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(RuntimesDirectory, relativePath);
    }

    /// <summary>Used at startup and by tests. Also recreates the folder tree.</summary>
    public static void OverrideRoot(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return;
        }

        _dataRoot = Path.GetFullPath(dataRoot);
        EnsureCreated();
    }

    /// <summary>Creates the folder tree. Safe to call as often as you like.</summary>
    public static void EnsureCreated()
    {
        foreach (string directory in new[]
                 {
                     DataRoot, ConfigDirectory, ModelsDirectory, RuntimesDirectory, CacheDirectory,
                     MemoryDirectory, ModulesDirectory, WorkspaceDirectory, LogsDirectory, ExportDirectory
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string ResolveDefaultRoot()
    {
        string? environmentRoot = Environment.GetEnvironmentVariable(HomeVariable);
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return Path.GetFullPath(environmentRoot);
        }

        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            // Extremely unlikely, but a fallback beats an exception during startup.
            localApplicationData = AppContext.BaseDirectory;
        }

        return Path.Combine(localApplicationData, "CompanionOS");
    }
}
