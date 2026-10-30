using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CompanionOS.Core.Modules;

/// <summary>Whether a tool only looks, or changes something. Drives the permission icons.</summary>
public enum ToolAccess
{
    Reads,
    Acts
}

/// <summary>
/// How a tool call reads to a person: "Resuming your music" while it runs, "Resumed your
/// music" when it finishes, "Couldn't resume your music" when it fails. The model-facing
/// description stays for the model; this is what the chat and the overlay show.
/// </summary>
public sealed record ToolPresentation(string Doing, string Done, string Failed, string IconKey);

/// <summary>
/// The single place tool names become human sentences. Built-in tools are listed here;
/// plugins can override any of it from plugin.json ("label", "doneLabel", "icon").
/// A tool nobody described still gets a readable sentence instead of its raw description.
/// </summary>
public static class ToolPresentationCatalog
{
    private static readonly Regex QualifiedName =
        new("^plugin_[a-z0-9_]+?_([a-z0-9_]+)_[0-9a-f]{10}$", RegexOptions.Compiled);

    private static readonly Dictionary<string, Func<ArgReader, ToolPresentation>> Known = Build();

    /// <summary>Resolves the sentence for one call. Never throws.</summary>
    public static ToolPresentation Resolve(ModuleTool tool, JsonElement arguments)
    {
        try
        {
            if (tool.Presentation is not null)
            {
                return tool.Presentation;
            }

            ArgReader args = new(arguments);
            string key = tool.LocalName ?? tool.Name;
            if (Known.TryGetValue(key, out Func<ArgReader, ToolPresentation>? build))
            {
                return build(args);
            }

            if (Known.TryGetValue(tool.Name, out build))
            {
                return build(args);
            }

            return Humanize(tool);
        }
        catch (Exception)
        {
            return Humanize(tool);
        }
    }

    /// <summary>Reads for look-only tools, Acts for anything that changes something.</summary>
    public static ToolAccess AccessOf(ModuleTool tool)
    {
        if (tool.Access is { } declared)
        {
            return declared;
        }

        if (!string.IsNullOrWhiteSpace(tool.PermissionScope))
        {
            return tool.PermissionScope.EndsWith(".read", StringComparison.OrdinalIgnoreCase) ||
                   tool.PermissionScope.EndsWith(".view", StringComparison.OrdinalIgnoreCase)
                ? ToolAccess.Reads
                : ToolAccess.Acts;
        }

        return tool.Tier == ToolTier.Reflex ? ToolAccess.Reads : ToolAccess.Acts;
    }

    // ------------------------------------------------------------------ catalog

    private static Dictionary<string, Func<ArgReader, ToolPresentation>> Build()
    {
        Dictionary<string, Func<ArgReader, ToolPresentation>> map = new(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string doing, string done, string failed, string icon) =>
            map[name] = _ => new ToolPresentation(doing, done, failed, icon);

        // Core senses
        Add("remember", "Remembering that", "Remembered that", "Couldn't save that memory", "memory");
        Add("recall_memory", "Thinking back", "Looked through my memory", "Couldn't search my memory", "memory");
        Add("forget_memory", "Forgetting that", "Forgot that", "Couldn't forget that", "memory");
        Add("correct_memory", "Fixing a memory", "Fixed a memory", "Couldn't fix that memory", "memory");
        Add("look_at_screen", "Looking at your screen", "Looked at your screen", "Couldn't see your screen", "eye");
        Add("describe_situation", "Checking what's going on", "Checked what's going on", "Couldn't check that", "status");
        Add("search_capabilities", "Checking what I can do", "Checked what I can do", "Couldn't check that", "status");
        Add("learn_game", "Learning a game", "Learned a game", "Couldn't learn that game", "status");
        Add("ask_user", "Asking you", "Asked you", "Couldn't ask you", "question");
        Add("background_task_status", "Checking background tasks", "Checked background tasks",
            "Couldn't check background tasks", "activity");
        Add("cancel_background_task", "Stopping a background task", "Stopped a background task",
            "Couldn't stop that task", "stop");

        map["control_windows_media"] = args => args.Str("action").ToLowerInvariant() switch
        {
            "pause" => new ToolPresentation("Pausing your media", "Paused your media", "Couldn't pause your media", "pause"),
            _ => new ToolPresentation("Resuming your media", "Resumed your media", "Couldn't resume your media", "play")
        };

        map["set_operating_mode"] = args => args.Str("mode").ToLowerInvariant() switch
        {
            "sleeping" or "sleep" => new ToolPresentation("Going to sleep", "Went to sleep", "Couldn't go to sleep", "sleep"),
            "gaming" => new ToolPresentation("Switching to gaming mode", "Gaming mode is on", "Couldn't switch modes", "status"),
            _ => new ToolPresentation("Waking up", "I'm awake", "Couldn't wake up", "status")
        };

        map["control_microphone"] = args =>
            args.Str("action").ToLowerInvariant() is "close" or "off" or "mute" or "stop"
                ? new ToolPresentation("Closing the microphone", "Closed the microphone", "Couldn't close the microphone", "mic")
                : new ToolPresentation("Opening the microphone", "Opened the microphone", "Couldn't open the microphone", "mic");

        // Music
        map["music_player"] = args =>
        {
            string query = args.Short("query");
            return args.Str("action").ToLowerInvariant() switch
            {
                "play" when query.Length > 0 => new ToolPresentation($"Playing {query}", $"Started {query}", $"Couldn't play {query}", "play"),
                "play" => new ToolPresentation("Starting your music", "Started your music", "Couldn't start your music", "play"),
                "pause" => new ToolPresentation("Pausing your music", "Paused your music", "Couldn't pause your music", "pause"),
                "resume" => new ToolPresentation("Resuming your music", "Resumed your music", "Couldn't resume your music", "play"),
                "stop" => new ToolPresentation("Stopping the music", "Stopped the music", "Couldn't stop the music", "stop"),
                "next" => new ToolPresentation("Skipping ahead", "Skipped to the next song", "Couldn't skip the song", "next"),
                "previous" => new ToolPresentation("Going back", "Went back a song", "Couldn't go back", "previous"),
                "shuffle" => new ToolPresentation("Shuffling your music", "Shuffled your music", "Couldn't shuffle", "music"),
                "set_volume" => new ToolPresentation($"Setting volume to {args.Str("level")}%", $"Volume is now {args.Str("level")}%",
                    "Couldn't change the volume", "music"),
                "show_controls" => new ToolPresentation("Showing the player", "Showing the player", "Couldn't show the player", "music"),
                "hide_controls" => new ToolPresentation("Hiding the player", "Hid the player", "Couldn't hide the player", "music"),
                _ => new ToolPresentation("Adjusting the music", "Adjusted the music", "Couldn't adjust the music", "music")
            };
        };

        map["music_library"] = args => args.Str("action").ToLowerInvariant() == "refresh"
            ? new ToolPresentation("Scanning your music folders", "Scanned your music folders", "Couldn't scan your music", "music")
            : new ToolPresentation("Looking through your library", "Looked through your library", "Couldn't read your library", "music");

        map["music_library_folders"] = args => args.Str("action").ToLowerInvariant() switch
        {
            "add" => new ToolPresentation("Adding a music folder", "Added a music folder", "Couldn't add that folder", "folder"),
            "remove" => new ToolPresentation("Removing a music folder", "Removed a music folder", "Couldn't remove that folder", "folder"),
            _ => new ToolPresentation("Looking through folders", "Looked through your folders", "Couldn't open that folder", "folder")
        };

        map["music_playlists"] = args =>
        {
            string name = args.Short("name");
            string target = name.Length > 0 ? name : "a playlist";
            return args.Str("action").ToLowerInvariant() switch
            {
                "create" => new ToolPresentation($"Making {target}", $"Made {target}", $"Couldn't make {target}", "music"),
                "add" => new ToolPresentation($"Adding songs to {target}", $"Added songs to {target}", $"Couldn't add to {target}", "music"),
                "play" => new ToolPresentation($"Playing {target}", $"Started {target}", $"Couldn't play {target}", "play"),
                _ => new ToolPresentation("Checking your playlists", "Checked your playlists", "Couldn't read your playlists", "music")
            };
        };

        // System
        map["set_timer"] = args =>
        {
            string minutes = args.Str("minutes");
            string span = minutes.Length > 0 ? $"{minutes} min " : string.Empty;
            return new ToolPresentation($"Setting a {span}timer", $"Set a {span}timer", "Couldn't set the timer", "timer");
        };
        Add("list_timers", "Checking your timers", "Checked your timers", "Couldn't check your timers", "timer");
        Add("cancel_timers", "Cancelling your timers", "Cancelled your timers", "Couldn't cancel the timers", "timer");
        Add("cancel_timer", "Cancelling a timer", "Cancelled a timer", "Couldn't cancel that timer", "timer");
        Add("snooze_timer", "Snoozing the timer", "Snoozed the timer", "Couldn't snooze the timer", "timer");
        Add("show_timer_overlay", "Showing the timer", "Showing the timer", "Couldn't show the timer", "timer");
        Add("hide_timer_overlay", "Hiding the timer", "Hid the timer", "Couldn't hide the timer", "timer");
        Add("write_note", "Saving a note", "Saved a note", "Couldn't save that note", "note");
        Add("read_notes", "Reading your notes", "Read your notes", "Couldn't read your notes", "note");
        Add("system_status", "Checking your PC", "Checked your PC", "Couldn't check your PC", "status");

        map["open_application"] = args =>
        {
            string name = args.Short("name");
            string target = name.Length > 0 ? name : "an app";
            return new ToolPresentation($"Opening {target}", $"Opened {target}", $"Couldn't open {target}", "app");
        };

        map["find_files"] = args =>
        {
            string query = args.Short("query");
            string target = query.Length > 0 ? $"\u201C{query}\u201D" : "files";
            return new ToolPresentation($"Looking for {target}", $"Searched for {target}", $"Couldn't search for {target}", "folder");
        };

        // Browser plugin (local tool names)
        Add("browser_pair", "Pairing your browser", "Paired your browser", "Couldn't pair your browser", "web");
        Add("browser_status", "Checking the browser", "Checked the browser", "Couldn't reach the browser", "web");
        Add("browser_search", "Searching the web", "Searched the web", "Couldn't search the web", "web");
        Add("browser_read_page", "Reading the page", "Read the page", "Couldn't read the page", "web");
        Add("browser_focus", "Highlighting the page", "Highlighted the page", "Couldn't highlight the page", "web");
        Add("browser_attend", "Looking at the page", "Looked at the page", "Couldn't look at the page", "web");
        Add("browser_scroll", "Scrolling the page", "Scrolled the page", "Couldn't scroll the page", "web");
        Add("browser_navigate", "Opening a page", "Opened a page", "Couldn't open that page", "web");
        Add("browser_click", "Clicking on the page", "Clicked on the page", "Couldn't click there", "web");
        Add("browser_type", "Typing on the page", "Typed on the page", "Couldn't type there", "web");
        Add("browser_tabs", "Listing your tabs", "Listed your tabs", "Couldn't list your tabs", "web");
        Add("browser_switch_tab", "Switching tabs", "Switched tabs", "Couldn't switch tabs", "web");

        return map;
    }

    private static ToolPresentation Humanize(ModuleTool tool)
    {
        string name = tool.LocalName ?? tool.Name;
        Match match = QualifiedName.Match(name);
        if (match.Success)
        {
            name = match.Groups[1].Value;
        }

        StringBuilder words = new();
        foreach (string part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(part);
        }

        string phrase = words.Length == 0 ? "that" : words.ToString();
        return new ToolPresentation($"Working on {phrase}", $"Finished {phrase}", $"Couldn't finish {phrase}", "body");
    }

    /// <summary>Safe, short reads of tool arguments for building a sentence.</summary>
    public readonly struct ArgReader
    {
        private readonly JsonElement _arguments;

        public ArgReader(JsonElement arguments) => _arguments = arguments;

        public string Str(string name)
        {
            if (_arguments.ValueKind == JsonValueKind.Object && _arguments.TryGetProperty(name, out JsonElement value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
            }

            return string.Empty;
        }

        /// <summary>One line, at most 40 characters, so a long argument never wrecks a card.</summary>
        public string Short(string name)
        {
            string text = Str(name).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length > 40 ? text[..39].TrimEnd() + "\u2026" : text;
        }
    }
}
