using System.Text;
using CompanionOS.Core.Memory;
using CompanionOS.Core.Modules;

namespace CompanionOS.Core.Modules.BuiltIn;

/// <summary>
/// The "learn me" hands: an explicit, user-controlled personal profile for tastes,
/// favorite songs and playlists.
///
/// The contract, enforced here and in <see cref="PersonalProfileStore"/>:
///  - What the user STATED or CONFIRMED is durable. What Iris merely noticed is a
///    tentative suggestion that must be asked about — it is never reported as truth.
///  - A rejected suggestion stays rejected; it is refused on re-suggest.
///  - A single play, download, search or open tab never becomes "the user likes this".
///  - "Favorite", "downloaded", "in my library", "in a playlist" and "recently
///    played" are different facts; playlist membership is recorded only after a
///    verified playlist write, never inferred from a download.
///  - No continuous monitoring: these tools run inside a user turn, on request.
/// </summary>
public sealed class ProfileModule : IAssistantModule
{
    private ModuleContext? _context;
    private PersonalProfileStore? _store;

    public string Id => "profile";

    public string DisplayName => "Personal Profile";

    public string Purpose => "What she knows about your tastes — inspectable, correctable, deletable.";

    public string CapabilityDescription =>
        "favorite song favorite artist my taste what do i like remember i like preferences profile " +
        "what do you know about me forget preference my favorites what do i usually listen to";

    public IReadOnlyList<ModuleTool> Tools { get; } = new List<ModuleTool>
    {
        new(
            "profile_show",
            "Show what she durably knows about the user's tastes: confirmed preferences with provenance and " +
            "dates, tentative patterns still awaiting the user's answer (clearly marked), favorite songs, and " +
            "paused/rejected state. Use for 'what do you know about me / my music?'. Never present a tentative " +
            "pattern as a known fact.",
            ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""",
            "what do you know", "my taste", "my preferences", "my profile", "about me")
        { Access = ToolAccess.Reads },

        new(
            "profile_save_preference",
            "Save a durable preference the user STATED directly this conversation ('I love Yates XV') or just " +
            "explicitly CONFIRMED after a suggestion. source must say which. Never use this for something merely " +
            "noticed — that goes through profile_suggest.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"category":{"type":"string","enum":["artist","song","album","genre","playlist","creator","video_style","other"]},"subject":{"type":"string","description":"What the preference is about."},"stance":{"type":"string","enum":["likes","dislikes","prefers","avoids"]},"source":{"type":"string","enum":["user_stated","user_confirmed"],"description":"user_stated: they said it. user_confirmed: they just said yes to a suggestion."},"evidence":{"type":"string","description":"Their words or the confirming exchange, one line."}},"required":["category","subject","stance","source"]}""",
            "remember i like", "i love", "i hate", "my favorite", "save preference")
        { Access = ToolAccess.Acts },

        new(
            "profile_suggest",
            "Record a TENTATIVE pattern noticed from evidence the user asked to be analyzed, then ask the user " +
            "about it in the answer ('Should I remember that you like X?'). It is saved as pending, not as truth. " +
            "Refused automatically if the user rejected it before or paused learning.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"category":{"type":"string","enum":["artist","song","album","genre","playlist","creator","video_style","other"]},"subject":{"type":"string"},"stance":{"type":"string","enum":["likes","dislikes","prefers","avoids"]},"confidence":{"type":"number","description":"0 to 1."},"evidence":{"type":"string","description":"The concrete evidence, e.g. 'three saved tracks and two replays this month'."}},"required":["category","subject","stance","evidence"]}""",
            "pattern", "noticed", "suggest preference", "should i remember")
        { Access = ToolAccess.Acts },

        new(
            "profile_resolve_suggestion",
            "Apply the user's answer to a pending suggestion: action=confirm makes it a durable confirmed " +
            "preference, action=reject removes it and remembers the rejection so it is not suggested again.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"subject":{"type":"string"},"action":{"type":"string","enum":["confirm","reject"]}},"required":["subject","action"]}""",
            "yes remember that", "no don't", "confirm", "reject suggestion")
        { Access = ToolAccess.Acts },

        new(
            "profile_forget",
            "User-controlled deletion and pausing: action=forget removes one subject (preferences and songs), " +
            "action=clear_category wipes a whole category, action=pause stops new pattern learning, " +
            "action=resume re-enables it.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"action":{"type":"string","enum":["forget","clear_category","pause","resume"]},"subject":{"type":"string","description":"For forget: the subject. For clear_category: the category name."}},"required":["action"]}""",
            "forget that i like", "clear my preferences", "stop learning", "pause learning", "delete preference")
        { Access = ToolAccess.Acts },

        new(
            "favorites_add_song",
            "Save or update one song the user told her about: title, artist, how they rate it (favorite/like/" +
            "dislike/mentioned), their own alias for it, a VERIFIED YouTube URL if one was actually opened or " +
            "checked this conversation (say what kind: official/live/cover/remix/lyric), and a verified local " +
            "file path if the file is known to exist. Never mark 'favorite' unless the user said or confirmed it.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"title":{"type":"string"},"artist":{"type":"string"},"album":{"type":"string"},"stance":{"type":"string","enum":["favorite","like","dislike","mentioned"]},"source":{"type":"string","enum":["user_stated","user_confirmed","verified_action"]},"url":{"type":"string","description":"Only a URL that was actually verified."},"url_kind":{"type":"string","enum":["official","live","cover","remix","lyric","unknown"]},"local_path":{"type":"string","description":"Only a path verified to exist."},"alias":{"type":"string","description":"The user's own phrase for this song."}},"required":["title","stance","source"]}""",
            "favorite song", "i love this song", "remember this song", "save this song", "that song i told you")
        { Access = ToolAccess.Acts },

        new(
            "favorites_find_song",
            "Look up remembered songs by title, artist or the user's alias. Returns stance, verified link and " +
            "kind, verified local path, playlists it was intentionally added to, and when it was last reinforced. " +
            "Call this FIRST for 'play/open my favorite …' or 'the song I told you about' — then open or play the " +
            "verified result with the right tool. If nothing matches, say so and search the web instead of guessing.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"query":{"type":"string","description":"Title, artist or alias fragment. Empty lists favorites."}},"required":[]}""",
            "my favorite song", "favorite", "that song", "what do i listen to", "my songs")
        { Access = ToolAccess.Reads },

        new(
            "favorites_record_playlist_add",
            "Record that a remembered song is now in a playlist — call ONLY after the playlist write succeeded " +
            "AND the playlist was read back showing the track. A download or play is never a playlist addition.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"title":{"type":"string"},"artist":{"type":"string"},"playlist":{"type":"string"}},"required":["title","playlist"]}""",
            "added to playlist", "playlist updated")
        { Access = ToolAccess.Acts }
    };

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _store = new PersonalProfileStore(Path.Combine(context.WorkspaceDirectory, "profile"));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync() => Task.CompletedTask;

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return Task.FromResult(ToolResult.Fail("The profile module is not initialized."));
        }

        try
        {
            return Task.FromResult(invocation.Tool.Name switch
            {
                "profile_show" => Show(),
                "profile_save_preference" => SavePreference(invocation),
                "profile_suggest" => Suggest(invocation),
                "profile_resolve_suggestion" => Resolve(invocation),
                "profile_forget" => Forget(invocation),
                "favorites_add_song" => AddSong(invocation),
                "favorites_find_song" => FindSong(invocation.GetString("query")),
                "favorites_record_playlist_add" => RecordPlaylistAdd(invocation),
                _ => ToolResult.Fail($"Unknown profile tool '{invocation.Tool.Name}'.")
            });
        }
        catch (InvalidOperationException guardrail)
        {
            // The store's honesty rules speak for themselves (e.g. "a play is not a
            // stated taste"); the refusal is the correct, visible outcome.
            return Task.FromResult(ToolResult.Fail(guardrail.Message));
        }
    }

    private ToolResult Show()
    {
        var preferences = _store!.ListPreferences();
        var pending = preferences.Where(p => p.Provenance == ProfileProvenance.InferredTentative).ToList();
        var durable = preferences.Where(p => p.Provenance is ProfileProvenance.UserStated
            or ProfileProvenance.UserConfirmed or ProfileProvenance.VerifiedAction).ToList();
        var songs = _store.ListSongs();

        StringBuilder text = new();
        text.AppendLine($"Profile file: {_store.FilePath}" +
                        (_store.LearningPaused ? " — LEARNING PAUSED by the user." : string.Empty));
        text.AppendLine(durable.Count == 0
            ? "Durable preferences: none saved yet."
            : "Durable preferences: " + string.Join("; ", durable.Take(20).Select(p =>
                $"{p.Subject} ({p.Category}, {p.Stance}, {Describe(p.Provenance)}, last reinforced {p.LastReinforcedAt:yyyy-MM-dd})")));
        if (pending.Count > 0)
        {
            text.AppendLine("Tentative patterns AWAITING the user's answer (not facts): " +
                            string.Join("; ", pending.Select(p =>
                                $"{p.Subject} ({p.Stance}, confidence {p.Confidence:0.0}, evidence: {p.Evidence})")));
        }

        text.AppendLine(songs.Count == 0
            ? "Songs: none remembered yet."
            : "Songs: " + string.Join("; ", songs.Take(20).Select(DescribeSong)));
        var rejected = _store.ListRejected();
        if (rejected.Count > 0)
        {
            text.AppendLine("Previously rejected (never re-suggest): " +
                            string.Join(", ", rejected.Select(r => r.Subject)));
        }

        return ToolResult.Ok(text.ToString().TrimEnd());
    }

    private ToolResult SavePreference(ToolInvocation invocation)
    {
        string source = invocation.GetString("source");
        ProfileProvenance provenance = source switch
        {
            "user_stated" => ProfileProvenance.UserStated,
            "user_confirmed" => ProfileProvenance.UserConfirmed,
            _ => ProfileProvenance.InferredTentative // The store will refuse it, loudly.
        };
        ProfilePreference saved = _store!.SavePreference(
            invocation.GetString("category"), invocation.GetString("subject"),
            invocation.GetString("stance"), provenance, invocation.GetString("evidence"));
        return ToolResult.Ok(
            $"Saved: {saved.Subject} — {saved.Stance} ({saved.Category}), provenance {Describe(saved.Provenance)}. " +
            "The user can inspect or forget this any time with profile_show / profile_forget.");
    }

    private ToolResult Suggest(ToolInvocation invocation)
    {
        string subject = invocation.GetString("subject");
        // Confidence is a fraction, read straight from the JSON; a missing or broken
        // value stays honest at "unsure" rather than inventing certainty.
        double confidence = 0.5;
        if (invocation.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object &&
            invocation.Arguments.TryGetProperty("confidence", out System.Text.Json.JsonElement raw) &&
            raw.ValueKind == System.Text.Json.JsonValueKind.Number &&
            raw.TryGetDouble(out double parsed))
        {
            confidence = parsed;
        }

        bool stored = _store!.TrySuggestPreference(invocation.GetString("category"), subject,
            invocation.GetString("stance"), confidence, invocation.GetString("evidence"), out string refusal);
        return stored
            ? ToolResult.Ok($"Noted as TENTATIVE: {subject}. Now ask the user plainly whether to remember it; " +
                            "it becomes a durable preference only after they confirm.")
            : ToolResult.Fail(refusal);
    }

    private ToolResult Resolve(ToolInvocation invocation)
    {
        string subject = invocation.GetString("subject");
        return invocation.GetString("action") switch
        {
            "confirm" => _store!.ConfirmSuggestion(subject)
                ? ToolResult.Ok($"Confirmed and saved durably: {subject}.")
                : ToolResult.Fail($"No pending suggestion named '{subject}'. profile_show lists what is pending."),
            "reject" => _store!.RejectSuggestion(subject)
                ? ToolResult.Ok($"Rejected: {subject}. It will not be suggested again.")
                : ToolResult.Fail($"Could not record the rejection for '{subject}'."),
            _ => ToolResult.Fail("action must be confirm or reject.")
        };
    }

    private ToolResult Forget(ToolInvocation invocation)
    {
        string subject = invocation.GetString("subject");
        switch (invocation.GetString("action"))
        {
            case "forget":
                int removed = _store!.Forget(subject);
                return removed > 0
                    ? ToolResult.Ok($"Forgot {removed} entr{(removed == 1 ? "y" : "ies")} about '{subject}'.")
                    : ToolResult.Fail($"Nothing in the profile matches '{subject}'. profile_show lists what exists.");
            case "clear_category":
                int cleared = _store!.ClearCategory(subject);
                return ToolResult.Ok($"Cleared {cleared} preference(s) in category '{subject}'.");
            case "pause":
                _store!.SetLearningPaused(true);
                return ToolResult.Ok("Preference learning is paused. Existing saved preferences stay until forgotten.");
            case "resume":
                _store!.SetLearningPaused(false);
                return ToolResult.Ok("Preference learning is back on, still suggestion-and-confirm only.");
            default:
                return ToolResult.Fail("action must be forget, clear_category, pause or resume.");
        }
    }

    private ToolResult AddSong(ToolInvocation invocation)
    {
        ProfileProvenance provenance = invocation.GetString("source") switch
        {
            "user_stated" => ProfileProvenance.UserStated,
            "user_confirmed" => ProfileProvenance.UserConfirmed,
            "verified_action" => ProfileProvenance.VerifiedAction,
            _ => ProfileProvenance.OneTimeChoice // The store refuses rating stances for this.
        };
        ProfileSong song = _store!.UpsertSong(
            invocation.GetString("title"), invocation.GetString("artist"), invocation.GetString("stance"),
            provenance, invocation.GetString("url"), invocation.GetString("url_kind"),
            invocation.GetString("local_path"), invocation.GetString("alias"), invocation.GetString("album"));
        return ToolResult.Ok($"Remembered: {DescribeSong(song)}. Stored in {_store.FilePath} — durable across restarts.");
    }

    private ToolResult FindSong(string query)
    {
        IReadOnlyList<ProfileSong> matches = string.IsNullOrWhiteSpace(query)
            ? _store!.ListSongs("favorite")
            : _store!.FindSongs(query);
        if (matches.Count == 0)
        {
            return ToolResult.Ok(string.IsNullOrWhiteSpace(query)
                ? "No favorite songs are saved yet. The user can tell me one and I will remember it."
                : $"No remembered song matches '{query}'. That is the verified answer — search the web/YouTube " +
                  "next if the user wants the song found, and never guess a link.");
        }

        return ToolResult.Ok($"{matches.Count} remembered song(s): " + string.Join(" | ", matches.Select(DescribeSong)));
    }

    private ToolResult RecordPlaylistAdd(ToolInvocation invocation)
    {
        string title = invocation.GetString("title");
        string playlist = invocation.GetString("playlist");
        return _store!.RecordPlaylistMembership(title, invocation.GetString("artist"), playlist)
            ? ToolResult.Ok($"Recorded: '{title}' is in playlist '{playlist}' (verified add).")
            : ToolResult.Fail($"'{title}' is not a remembered song yet. Save it with favorites_add_song first, " +
                              "then record the verified playlist addition.");
    }

    private static string Describe(ProfileProvenance provenance) => provenance switch
    {
        ProfileProvenance.UserStated => "stated by the user",
        ProfileProvenance.UserConfirmed => "confirmed by the user",
        ProfileProvenance.InferredTentative => "tentative, unconfirmed",
        ProfileProvenance.VerifiedAction => "verified action",
        _ => "one-time choice"
    };

    private static string DescribeSong(ProfileSong song)
    {
        StringBuilder text = new($"{song.Title}{(song.Artist.Length > 0 ? " — " + song.Artist : string.Empty)} [{song.Stance}, {Describe(song.Provenance)}]");
        if (song.Aliases.Count > 0) text.Append($" aliases: {string.Join("/", song.Aliases)}");
        if (song.Url.Length > 0) text.Append($" link: {song.Url} ({(song.UrlKind.Length > 0 ? song.UrlKind : "unknown kind")})");
        if (song.LocalPath.Length > 0) text.Append($" file: {song.LocalPath}");
        if (song.Playlists.Count > 0) text.Append($" playlists: {string.Join(", ", song.Playlists)}");
        return text.ToString();
    }
}
