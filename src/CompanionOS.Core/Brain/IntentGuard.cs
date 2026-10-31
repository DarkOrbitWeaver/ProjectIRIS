namespace CompanionOS.Core.Brain;

/// <summary>
/// The action classes a request can actually grant. They are deliberately ordered by
/// consequence: inspecting costs nothing, sending or deleting cannot be undone.
/// </summary>
public enum RequestedAction
{
    /// <summary>Read-only: status, listing, "what/where/which", inspection.</summary>
    Inspect,

    /// <summary>Search or resolve a candidate without acting on it.</summary>
    Find,

    /// <summary>Open or navigate to something already identified.</summary>
    Open,

    /// <summary>Start playback of something that already exists.</summary>
    Play,

    /// <summary>Fetch a file from the network to disk.</summary>
    Download,

    /// <summary>Index an existing local file into the music library.</summary>
    ImportToLibrary,

    /// <summary>Write to a saved playlist.</summary>
    PlaylistWrite,

    /// <summary>Send a message or media to another person.</summary>
    Send,

    /// <summary>Remove or overwrite user data.</summary>
    Delete
}

/// <summary>
/// Verb fidelity, enforced in code instead of hoped for in a prompt.
///
/// The failure this closes is the classic over-acting agent: the user says "play
/// this" and the agent downloads it, or says "download it" and the agent also edits
/// a playlist. A 4B model conflates those verbs constantly, and a system prompt only
/// asks it not to.
///
/// This judges the REQUEST, which is an input — never her sentences. That boundary is
/// deliberate and matches the existing rule in this codebase: every pattern ever built
/// on her OUTPUT eventually punished a true statement, so no regex is allowed to score
/// her words. Scoring what the user asked for is a different thing entirely.
///
/// Grants accumulate across the turns of one task, so a clarification ("the second
/// one") never loses the download the user asked for two turns earlier. The guard is
/// additive to permissions, never a replacement: an allowed intent still passes
/// through the sandbox, confirmation and audit exactly as before.
/// </summary>
public static class IntentGuard
{
    /// <summary>Actions whose cost justifies refusing an unrequested escalation.</summary>
    private static readonly RequestedAction[] ConsequentialActions =
    {
        RequestedAction.Download, RequestedAction.ImportToLibrary,
        RequestedAction.PlaylistWrite, RequestedAction.Send, RequestedAction.Delete
    };

    /// <summary>Reads the action classes one utterance actually grants.</summary>
    public static IReadOnlySet<RequestedAction> Parse(string? userText)
    {
        HashSet<RequestedAction> granted = new();
        string text = " " + Normalize(userText) + " ";
        if (text.Trim().Length == 0)
        {
            return granted;
        }

        if (ContainsAny(text, "download", "save the video", "save that video", "grab the file", "rip ", "yt-dlp"))
        {
            granted.Add(RequestedAction.Download);
        }

        if (ContainsAny(text, "add to my library", "add it to my library", "add to the library",
                "import", "index this", "add to library"))
        {
            granted.Add(RequestedAction.ImportToLibrary);
        }

        if (ContainsAny(text, "playlist"))
        {
            // "play my playlist" is playback; only add/create/remove wording writes.
            if (ContainsAny(text, "add", "put", "create", "make", "new playlist", "remove", "delete"))
            {
                granted.Add(RequestedAction.PlaylistWrite);
                // Adding a specific local file to a playlist implies indexing it first;
                // that prerequisite is granted so a legitimate chain is never blocked.
                granted.Add(RequestedAction.ImportToLibrary);
            }
            else
            {
                granted.Add(RequestedAction.Play);
            }
        }

        if (ContainsAny(text, "send", "message ", "text ", "dm ", "forward", "post ", "email"))
        {
            granted.Add(RequestedAction.Send);
        }

        if (ContainsAny(text, "delete", "erase", "remove the file", "wipe", "uninstall"))
        {
            granted.Add(RequestedAction.Delete);
        }

        if (ContainsAny(text, "play", "put on", "resume", "start the song", "listen"))
        {
            granted.Add(RequestedAction.Play);
        }

        if (ContainsAny(text, "open", "launch", "go to", "navigate", "pull up"))
        {
            granted.Add(RequestedAction.Open);
        }

        if (ContainsAny(text, "find", "search", "look for", "look up", "which", "locate"))
        {
            granted.Add(RequestedAction.Find);
        }

        granted.Add(RequestedAction.Inspect);
        return granted;
    }

    /// <summary>
    /// Unions the grants of every user utterance that belongs to the same task, so a
    /// short follow-up answer keeps the original verb. Without this, "the second one"
    /// would silently revoke the download the user explicitly asked for.
    /// </summary>
    public static IReadOnlySet<RequestedAction> ParseAll(IEnumerable<string> utterances)
    {
        HashSet<RequestedAction> granted = new();
        foreach (string utterance in utterances)
        {
            granted.UnionWith(Parse(utterance));
        }

        return granted;
    }

    /// <summary>
    /// The action class a tool belongs to, read from its name. Unknown tools return
    /// null and are never blocked: an unrecognised plugin must keep working, and the
    /// permission tier is still its real gate.
    /// </summary>
    public static RequestedAction? ClassifyTool(string toolName)
    {
        string name = Normalize(toolName).Replace(' ', '_');
        if (name.Length == 0)
        {
            return null;
        }

        if (name.Contains("download", StringComparison.Ordinal)) return RequestedAction.Download;
        if (name.Contains("import", StringComparison.Ordinal)) return RequestedAction.ImportToLibrary;
        if (name.Contains("playlist", StringComparison.Ordinal)) return RequestedAction.PlaylistWrite;
        if (name.Contains("send", StringComparison.Ordinal)) return RequestedAction.Send;
        if (name.Contains("delete", StringComparison.Ordinal) ||
            name.Contains("uninstall", StringComparison.Ordinal)) return RequestedAction.Delete;
        return null;
    }

    /// <summary>
    /// True when this call would do more than the user asked for. The refusal text is
    /// written for the model: it names the gap and the one question worth asking,
    /// instead of silently doing the bigger thing.
    ///
    /// <paramref name="isMutatingCall"/> comes from the tool's declared access, so a
    /// read-only playlist inspection is never mistaken for a playlist write.
    /// </summary>
    public static bool IsUnrequestedEscalation(IReadOnlySet<RequestedAction> granted, string toolName,
        bool isMutatingCall, out string refusal)
    {
        refusal = string.Empty;
        if (!isMutatingCall)
        {
            return false;
        }

        RequestedAction? required = ClassifyTool(toolName);
        if (required is null || !ConsequentialActions.Contains(required.Value) || granted.Contains(required.Value))
        {
            return false;
        }

        string asked = granted.Contains(RequestedAction.Play) ? "play it"
            : granted.Contains(RequestedAction.Open) ? "open it"
            : granted.Contains(RequestedAction.Find) ? "find it"
            : "look something up";
        string wouldDo = required.Value switch
        {
            RequestedAction.Download => "download a file",
            RequestedAction.ImportToLibrary => "import a file into the music library",
            RequestedAction.PlaylistWrite => "change a saved playlist",
            RequestedAction.Send => "send a message to someone",
            _ => "delete user data"
        };

        refusal = $"I did not run {toolName}: the user asked me to {asked}, not to {wouldDo}. " +
                  "Those are separate actions. Do the requested one, and ask in one short question whether " +
                  $"they also want me to {wouldDo}.";
        return true;
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));

    private static string Normalize(string? text) => string.Join(' ',
        (text ?? string.Empty).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
