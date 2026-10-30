using System.Text;
using CompanionOS.Core.Modules;

namespace CompanionOS.Core.Modules.BuiltIn;

/// <summary>
/// Her own folder, made discoverable.
///
/// The gap this closes (2026-10 journals): asked "what is in your workspace?", she
/// answered from generic capability text instead of looking, and "where did you save
/// it?" got a guess. These tools are the evidence: a real listing, a real search, a
/// real read — plus bounded writes for notes and durable task summaries.
///
/// Boundaries, stated once and enforced in code:
///  - Every path goes through <see cref="WorkspacePathGuard"/>. No "..", no absolute
///    paths, no alternate data streams, no links out of the root.
///  - "IRIS://" is a friendly label for workspace-relative paths, not a mounted
///    drive. The overview always shows the real Windows folder next to it.
///  - Writes are text-shaped files only and ride ToolTier.Scoped: allowed inside her
///    own folder, always logged, never silent about what was written where.
///  - Task summaries live in tasks/ as dated markdown — episodic history the user
///    can open, separate from the semantic memory graph.
/// </summary>
public sealed class WorkspaceModule : IAssistantModule
{
    private const int MaximumReadCharacters = 24_000;
    private const int MaximumWriteCharacters = 64_000;
    private const int MaximumSearchHits = 20;

    private ModuleContext? _context;

    public string Id => "workspace";

    public string DisplayName => "Workspace";

    public string Purpose => "Her own files: notes, task summaries, lists — all inside one visible folder.";

    public string CapabilityDescription =>
        "workspace files notes save note read note folder iris drive where did you save list files " +
        "search files task summary project notes what is in your workspace open your folder";

    public IReadOnlyList<ModuleTool> Tools { get; } = new List<ModuleTool>
    {
        new(
            "workspace_overview",
            "Show where the workspace folder actually is on disk and what it contains: folders, file counts, " +
            "and the most recently changed files. Call this FIRST for questions like 'what is in your workspace?', " +
            "'where do you keep your files?' or 'where did you save it?' — answer from this evidence, never from memory.",
            ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""",
            "workspace", "your files", "your folder", "where did you save", "iris drive", "overview")
        { Access = ToolAccess.Reads },

        new(
            "workspace_list",
            "List files inside the workspace, optionally under one subfolder (e.g. 'notes' or 'tasks'). " +
            "Returns names, sizes and modified times.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"folder":{"type":"string","description":"Optional subfolder, e.g. 'notes'. Empty lists the root."}},"required":[]}""",
            "list files", "folder", "notes", "files", "show files")
        { Access = ToolAccess.Reads },

        new(
            "workspace_search",
            "Search workspace file names and text content for a word or phrase. Returns matching files with a " +
            "one-line snippet. Use this to find a note or task summary when the exact file name is unknown.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"query":{"type":"string","description":"Word or phrase to find."}},"required":["query"]}""",
            "search", "find note", "find file", "look up", "which file")
        { Access = ToolAccess.Reads },

        new(
            "workspace_read",
            "Read one text file from the workspace by its relative path (e.g. 'notes/today.md' or " +
            "'IRIS://tasks/2026-02-01-downloads.md'). Large files return the head with an honest truncation notice.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"path":{"type":"string","description":"Relative path inside the workspace."}},"required":["path"]}""",
            "read note", "open note", "read file", "show note", "contents")
        { Access = ToolAccess.Reads },

        new(
            "workspace_write",
            "Create or update one text file inside the workspace (markdown, txt, json, csv, yaml). " +
            "mode=overwrite replaces the file, mode=append adds to its end. Only for content the user asked to " +
            "save; never dump raw tool output here.",
            ToolTier.Scoped,
            """{"type":"object","properties":{"path":{"type":"string","description":"Relative path like notes/ideas.md."},"content":{"type":"string","description":"The text to write."},"mode":{"type":"string","enum":["overwrite","append"],"description":"Default overwrite."}},"required":["path","content"]}""",
            "save note", "write note", "create file", "take a note", "note that down", "update note")
        { Access = ToolAccess.Acts, PermissionScope = "workspace.write" },

        new(
            "workspace_save_task_summary",
            "Save a durable summary of a finished or stopped task into tasks/: what was asked, what verifiably " +
            "happened, where any files ended up, and what remains uncertain. Write outcomes you have receipts for, " +
            "never hopes. This is what later answers 'did that task finish?' honestly.",
            ToolTier.Scoped,
            """{"type":"object","properties":{"title":{"type":"string","description":"Short slug-friendly title, e.g. 'youtube download fix'."},"outcome":{"type":"string","enum":["success","partial","failed","cancelled","uncertain"],"description":"The honest terminal state."},"summary":{"type":"string","description":"What was attempted, what succeeded, file paths or links, what is uncertain."}},"required":["title","outcome","summary"]}""",
            "task summary", "task done", "record outcome", "what happened with", "did that finish")
        { Access = ToolAccess.Acts, PermissionScope = "workspace.write" }
    };

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        Directory.CreateDirectory(context.WorkspaceDirectory);
        Directory.CreateDirectory(Path.Combine(context.WorkspaceDirectory, "notes"));
        Directory.CreateDirectory(Path.Combine(context.WorkspaceDirectory, "tasks"));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync() => Task.CompletedTask;

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (_context is null)
        {
            return Task.FromResult(ToolResult.Fail("The workspace module is not initialized."));
        }

        string root = _context.WorkspaceDirectory;
        return Task.FromResult(invocation.Tool.Name switch
        {
            "workspace_overview" => Overview(root),
            "workspace_list" => ListFiles(root, invocation.GetString("folder")),
            "workspace_search" => Search(root, invocation.GetString("query")),
            "workspace_read" => ReadFile(root, invocation.GetString("path")),
            "workspace_write" => WriteFile(root, invocation.GetString("path"), invocation.GetString("content"),
                invocation.GetString("mode")),
            "workspace_save_task_summary" => SaveTaskSummary(root, invocation.GetString("title"),
                invocation.GetString("outcome"), invocation.GetString("summary")),
            _ => ToolResult.Fail($"Unknown workspace tool '{invocation.Tool.Name}'.")
        });
    }

    private static ToolResult Overview(string root)
    {
        Directory.CreateDirectory(root);
        if (!WorkspacePathGuard.TryValidateRoot(root, out string safeRoot, out string reason))
        {
            return ToolResult.Fail(reason);
        }

        var folders = Directory.EnumerateDirectories(safeRoot)
            .Where(IsSafeWorkspaceEntry)
            .Select(folder => $"{Path.GetFileName(folder)}/ ({EnumerateWorkspaceFiles(folder).Count()} files)")
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var recent = EnumerateWorkspaceFiles(safeRoot)
            .Select(file => new FileInfo(file))
            .OrderByDescending(info => info.LastWriteTime)
            .Take(8)
            .Select(info => $"{WorkspacePathGuard.ToVirtualPath(root, info.FullName)} ({info.Length} bytes, {info.LastWriteTime:yyyy-MM-dd HH:mm})")
            .ToList();

        StringBuilder text = new();
        text.AppendLine($"Workspace root (real folder): {root}");
        text.AppendLine("Virtual label: IRIS:// — a friendly name for this folder, not a mounted drive.");
        text.AppendLine(folders.Count == 0 ? "Folders: none yet." : "Folders: " + string.Join(", ", folders));
        text.AppendLine(recent.Count == 0
            ? "No files yet. workspace_write creates the first one."
            : "Most recently changed: " + string.Join("; ", recent));
        return ToolResult.Ok(text.ToString().TrimEnd());
    }

    private static ToolResult ListFiles(string root, string folder)
    {
        if (!WorkspacePathGuard.TryValidateRoot(root, out string safeRoot, out string rootReason))
        {
            return ToolResult.Fail(rootReason);
        }

        // Listing the root itself is always legal; only subfolders go through the guard.
        string target = string.IsNullOrWhiteSpace(folder) || folder.Trim() == "." ? string.Empty : folder;
        string fullPath;
        if (target.Length == 0)
        {
            fullPath = safeRoot;
        }
        else if (!WorkspacePathGuard.TryResolve(root, target, out fullPath, out string reason))
        {
            return ToolResult.Fail(reason);
        }

        string shownName = target.Length == 0 ? "the workspace root" : $"'{target}'";
        if (!Directory.Exists(fullPath))
        {
            return ToolResult.Fail($"There is no folder {shownName} in the workspace. workspace_overview shows what exists.");
        }

        var entries = Directory.EnumerateFileSystemEntries(fullPath)
            .Where(IsSafeWorkspaceEntry)
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .Select(entry => Directory.Exists(entry)
                ? Path.GetFileName(entry) + "/"
                : $"{WorkspacePathGuard.ToVirtualPath(root, entry)} ({new FileInfo(entry).Length} bytes)")
            .ToList();
        return ToolResult.Ok(entries.Count == 0
            ? $"{shownName} is empty."
            : $"{entries.Count} entries: " + string.Join("; ", entries));
    }

    private static ToolResult Search(string root, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolResult.Fail("Give a word or phrase to search for.");
        }

        if (!WorkspacePathGuard.TryValidateRoot(root, out string safeRoot, out string reason))
        {
            return ToolResult.Fail(reason);
        }

        List<string> hits = new();
        foreach (string file in EnumerateWorkspaceFiles(safeRoot))
        {
            if (hits.Count >= MaximumSearchHits)
            {
                hits.Add("… more matches exist; narrow the query.");
                break;
            }

            string name = Path.GetFileName(file);
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(WorkspacePathGuard.ToVirtualPath(root, file) + " (name match)");
                continue;
            }

            if (!WorkspacePathGuard.IsWritableFileType(file) || new FileInfo(file).Length > 512 * 1024)
            {
                continue; // Only text-shaped, sane-sized files are content-searched.
            }

            string content;
            try
            {
                content = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (IOException)
            {
                continue;
            }

            int index = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            int start = Math.Max(0, index - 40);
            int length = Math.Min(content.Length - start, 120);
            string snippet = content.Substring(start, length).Replace('\r', ' ').Replace('\n', ' ').Trim();
            hits.Add($"{WorkspacePathGuard.ToVirtualPath(root, file)}: …{snippet}…");
        }

        return ToolResult.Ok(hits.Count == 0
            ? $"No workspace file matches '{query}'. That is the verified answer — do not invent one."
            : $"{hits.Count} match(es): " + string.Join(" | ", hits));
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool IsSafeWorkspaceEntry(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static ToolResult ReadFile(string root, string path)
    {
        if (!WorkspacePathGuard.TryResolve(root, path, out string fullPath, out string reason))
        {
            return ToolResult.Fail(reason);
        }

        if (!File.Exists(fullPath))
        {
            return ToolResult.Fail($"There is no file at {WorkspacePathGuard.VirtualScheme}{path.TrimStart('/')}. " +
                                   "workspace_list or workspace_search finds the real name.");
        }

        string content;
        try
        {
            content = File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Fail($"Could not read the file: {exception.Message}");
        }

        bool truncated = content.Length > MaximumReadCharacters;
        if (truncated)
        {
            content = content[..MaximumReadCharacters];
        }

        return ToolResult.Ok($"{WorkspacePathGuard.ToVirtualPath(root, fullPath)}:\n{content}" +
                             (truncated ? "\n[Truncated: the file continues past this point.]" : string.Empty));
    }

    private static ToolResult WriteFile(string root, string path, string content, string mode)
    {
        if (!WorkspacePathGuard.TryResolve(root, path, out string fullPath, out string reason))
        {
            return ToolResult.Fail(reason);
        }

        if (!WorkspacePathGuard.IsWritableFileType(fullPath))
        {
            return ToolResult.Fail("Only text files (.md, .txt, .json, .csv, .yaml, .log) can be written in the workspace.");
        }

        if (content.Length > MaximumWriteCharacters)
        {
            return ToolResult.Fail($"That content is too large for one note ({content.Length} characters). Split it.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            bool append = string.Equals(mode, "append", StringComparison.OrdinalIgnoreCase);
            if (append)
            {
                File.AppendAllText(fullPath, content.EndsWith('\n') ? content : content + "\n", Encoding.UTF8);
            }
            else
            {
                File.WriteAllText(fullPath, content, Encoding.UTF8);
            }

            string virtualPath = WorkspacePathGuard.ToVirtualPath(root, fullPath);
            return ToolResult.Ok($"{(append ? "Appended to" : "Wrote")} {virtualPath} " +
                                 $"({new FileInfo(fullPath).Length} bytes on disk at {fullPath}).");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Fail($"The write failed: {exception.Message}");
        }
    }

    private static ToolResult SaveTaskSummary(string root, string title, string outcome, string summary)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
        {
            return ToolResult.Fail("A task summary needs a title and the summary text.");
        }

        string state = outcome.ToLowerInvariant() switch
        {
            "success" or "partial" or "failed" or "cancelled" or "uncertain" => outcome.ToLowerInvariant(),
            _ => "uncertain"
        };

        string slug = new string(title.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (slug.Length > 48)
        {
            slug = slug[..48].Trim('-');
        }

        string relative = $"tasks/{DateTime.Now:yyyy-MM-dd}-{slug}.md";
        string body = $"# {title}\n\n- outcome: {state}\n- recorded: {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}\n\n{summary.Trim()}\n";
        ToolResult written = WriteFile(root, relative, body, "overwrite");
        return written.Succeeded
            ? ToolResult.Ok($"Task summary saved with outcome '{state}'. {written.ModelText}")
            : written; // The write failure carries the real reason; never replace it.
    }
}
