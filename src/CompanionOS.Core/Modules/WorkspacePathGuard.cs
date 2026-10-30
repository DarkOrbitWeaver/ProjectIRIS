namespace CompanionOS.Core.Modules;

/// <summary>
/// The one gate every workspace file operation passes through.
///
/// The workspace is the only folder Iris may write to freely, and "IRIS://notes/x.md"
/// is only a friendly label for a path inside it — never a real mounted drive. That
/// promise holds exactly as long as no relative path, drive-qualified path, alternate
/// data stream, or symlink can resolve outside the root. This guard is pure (no I/O
/// except the final symlink check) so the containment rules are unit-testable on
/// their own.
/// </summary>
public static class WorkspacePathGuard
{
    /// <summary>The virtual scheme shown to the user. Presentation only.</summary>
    public const string VirtualScheme = "IRIS://";

    /// <summary>
    /// File extensions the workspace tools will create or edit. Text-shaped only:
    /// the workspace is notes, lists, and task summaries, not an executable drop.
    /// </summary>
    private static readonly HashSet<string> WritableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".csv", ".log", ".yaml", ".yml"
    };

    /// <summary>
    /// Resolves a user- or model-supplied relative path against the workspace root.
    /// Returns false with a human reason when the path is empty, rooted, escapes via
    /// "..", names an alternate data stream, or — for existing files — is a
    /// link/reparse point that resolves outside the root.
    /// </summary>
    public static bool TryResolve(string workspaceRoot, string requestedPath, out string fullPath, out string reason)
    {
        fullPath = string.Empty;

        string cleaned = (requestedPath ?? string.Empty).Trim();
        if (cleaned.StartsWith(VirtualScheme, StringComparison.OrdinalIgnoreCase))
        {
            // Only the scheme's own separators may be trimmed ("IRIS:///notes" is
            // sloppy but unambiguous). A bare leading slash WITHOUT the scheme is an
            // absolute path and stays one — trimming it here once turned
            // "/etc/passwd" into a relative path that resolved inside the root.
            cleaned = cleaned[VirtualScheme.Length..].TrimStart('/', '\\');
        }

        cleaned = cleaned.Replace('\\', '/');
        if (cleaned.Length == 0)
        {
            reason = "The path is empty. Give a file path inside the workspace, like notes/today.md.";
            return false;
        }

        if (cleaned.StartsWith('/') || Path.IsPathRooted(cleaned) ||
            cleaned.Length >= 2 && cleaned[1] == ':')
        {
            reason = "Absolute and drive-qualified paths are not allowed; the workspace only takes relative paths.";
            return false;
        }

        // ':' anywhere after the (already rejected) drive position is an NTFS
        // alternate data stream attempt ("notes.md:hidden") — refuse it outright.
        if (cleaned.Contains(':'))
        {
            reason = "The path contains ':', which is not allowed inside the workspace.";
            return false;
        }

        if (!TryValidateRoot(workspaceRoot, out string rootFull, out reason))
        {
            return false;
        }

        string candidate = Path.GetFullPath(Path.Combine(rootFull, cleaned));
        string rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            reason = "That path points outside the workspace. Everything must stay inside the workspace folder.";
            return false;
        }

        // A link inside the root that resolves outside it is still an escape. Checked
        // for every existing segment under the root, not just the leaf.
        string? walk = candidate;
        while (walk is not null &&
               (walk.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
                walk.Equals(rootFull, StringComparison.OrdinalIgnoreCase)))
        {
            if (File.Exists(walk) || Directory.Exists(walk))
            {
                FileSystemInfo info = Directory.Exists(walk)
                    ? new DirectoryInfo(walk)
                    : new FileInfo(walk);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "That path goes through a link, which could leave the workspace. Links are not followed.";
                    return false;
                }
            }

            walk = Path.GetDirectoryName(walk);
        }

        fullPath = candidate;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Confirms that the workspace exists and that neither it nor any ancestor is a
    /// reparse point. Checking the root itself matters: a symlinked workspace makes
    /// every apparently-contained child resolve outside the intended folder.
    /// </summary>
    public static bool TryValidateRoot(string workspaceRoot, out string fullRoot, out string reason)
    {
        fullRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            reason = "The workspace folder is not configured.";
            return false;
        }

        try
        {
            fullRoot = Path.GetFullPath(workspaceRoot);
            if (!Directory.Exists(fullRoot))
            {
                reason = "The workspace folder does not exist.";
                return false;
            }

            DirectoryInfo? ancestor = new(fullRoot);
            while (ancestor is not null)
            {
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "The workspace path goes through a link or junction, so it cannot be safely contained.";
                    fullRoot = string.Empty;
                    return false;
                }

                ancestor = ancestor.Parent;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            reason = $"The workspace folder could not be validated: {exception.Message}";
            fullRoot = string.Empty;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>True when the workspace tools may create or overwrite this file type.</summary>
    public static bool IsWritableFileType(string fullPath) =>
        WritableExtensions.Contains(Path.GetExtension(fullPath));

    /// <summary>The friendly label for a contained path: "IRIS://notes/today.md".</summary>
    public static string ToVirtualPath(string workspaceRoot, string fullPath)
    {
        string rootFull = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
        string relative = Path.GetRelativePath(rootFull, fullPath).Replace('\\', '/');
        return VirtualScheme + relative;
    }
}
