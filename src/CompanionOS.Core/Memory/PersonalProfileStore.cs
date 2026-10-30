using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompanionOS.Core.Memory;

/// <summary>How a profile entry came to exist. The difference IS the feature:
/// a stated preference is truth, a tentative pattern is a question waiting for the
/// user, and a one-time choice must never quietly become "the user likes this".</summary>
public enum ProfileProvenance
{
    /// <summary>The user said it directly ("I love Yates XV").</summary>
    UserStated,

    /// <summary>Iris suggested it and the user explicitly confirmed.</summary>
    UserConfirmed,

    /// <summary>A pattern Iris noticed. NOT durable truth until confirmed.</summary>
    InferredTentative,

    /// <summary>A single play/download/search. Context, not preference.</summary>
    OneTimeChoice,

    /// <summary>Backed by a verified tool action (e.g. a playlist add that was re-read).</summary>
    VerifiedAction
}

/// <summary>One preference: about music, creators, styles, or anything the user teaches her.</summary>
public sealed class ProfilePreference
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..12];
    public string Category { get; set; } = "other"; // artist, song, album, genre, playlist, creator, video_style, other
    public string Subject { get; set; } = string.Empty;
    public string Stance { get; set; } = "likes"; // likes, dislikes, prefers, avoids
    public ProfileProvenance Provenance { get; set; } = ProfileProvenance.UserStated;
    public double? Confidence { get; set; } // Only meaningful for InferredTentative.
    public string Evidence { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastReinforcedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Set when a newer preference contradicts this one. History is kept,
    /// not rewritten: tastes change and the old entry says when they did.</summary>
    public string? SupersededBy { get; set; }
}

/// <summary>One song she knows about, with only verifiable metadata.</summary>
public sealed class ProfileSong
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..12];
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Stance { get; set; } = "mentioned"; // favorite, like, dislike, mentioned
    public List<string> Aliases { get; set; } = new();
    public string Url { get; set; } = string.Empty;
    public string UrlKind { get; set; } = string.Empty; // official, live, cover, remix, lyric, unknown
    public string LocalPath { get; set; } = string.Empty;
    public List<string> Playlists { get; set; } = new(); // Only playlists the user intentionally added it to.
    public ProfileProvenance Provenance { get; set; } = ProfileProvenance.UserStated;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastReinforcedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>A suggestion the user rejected. Kept so it is never re-suggested as truth.</summary>
public sealed record RejectedSuggestion(string Subject, string Category, DateTimeOffset RejectedAt);

/// <summary>
/// The explicit, inspectable "learn me" profile.
///
/// This is deliberately NOT the memory graph. The graph is associative intuition;
/// this file is a ledger the user can open, read, correct and delete, entry by
/// entry. Provenance is first-class: nothing inferred becomes durable truth without
/// the user's confirmation, rejected suggestions stay rejected, and a contradicted
/// preference is superseded — never silently erased, never silently kept current.
///
/// Storage is one JSON file inside the workspace profile folder, written atomically
/// (tmp + replace) so a crash can never leave half a profile. No secrets, no raw
/// transcripts, no browsing history belong in here.
/// </summary>
public sealed class PersonalProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private ProfileDocument _document = new();

    public PersonalProfileStore(string profileDirectory)
    {
        Directory.CreateDirectory(profileDirectory);
        _filePath = Path.Combine(profileDirectory, "profile.json");
        if (File.Exists(_filePath))
        {
            try
            {
                _document = JsonSerializer.Deserialize<ProfileDocument>(
                    File.ReadAllText(_filePath), SerializerOptions) ?? new ProfileDocument();
            }
            catch (JsonException)
            {
                // A corrupt profile must not take the module down; it is preserved
                // for inspection instead of being overwritten by an empty one.
                File.Copy(_filePath, _filePath + ".corrupt.bak", overwrite: true);
                _document = new ProfileDocument();
            }
        }
    }

    public string FilePath => _filePath;

    public bool LearningPaused
    {
        get { lock (_gate) { return _document.LearningPaused; } }
    }

    public void SetLearningPaused(bool paused)
    {
        lock (_gate)
        {
            _document.LearningPaused = paused;
            Persist();
        }
    }

    /// <summary>True when this subject was rejected before — do not re-suggest it.</summary>
    public bool IsRejected(string subject)
    {
        lock (_gate)
        {
            return _document.Rejected.Any(entry =>
                entry.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Saves a durable preference. Only UserStated, UserConfirmed and VerifiedAction
    /// provenance are accepted here; an inference must go through SuggestPreference
    /// and the user's explicit confirmation first. That rule is the privacy contract.
    /// </summary>
    public ProfilePreference SavePreference(string category, string subject, string stance,
        ProfileProvenance provenance, string evidence)
    {
        if (provenance is ProfileProvenance.InferredTentative or ProfileProvenance.OneTimeChoice)
        {
            throw new InvalidOperationException(
                "Inferred or one-time items are not durable preferences. Suggest it and wait for the user's confirmation.");
        }

        lock (_gate)
        {
            ProfilePreference? existing = _document.Preferences.FirstOrDefault(entry =>
                entry.SupersededBy is null &&
                entry.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase) &&
                entry.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

            if (existing is not null && existing.Stance.Equals(stance, StringComparison.OrdinalIgnoreCase))
            {
                existing.LastReinforcedAt = DateTimeOffset.Now;
                if (provenance == ProfileProvenance.UserStated)
                {
                    existing.Provenance = ProfileProvenance.UserStated; // A direct statement upgrades confirmation.
                }

                Persist();
                return existing;
            }

            ProfilePreference entry = new()
            {
                Category = category,
                Subject = subject,
                Stance = stance,
                Provenance = provenance,
                Evidence = evidence
            };

            if (existing is not null)
            {
                existing.SupersededBy = entry.Id; // Tastes changed; the history says when.
            }

            _document.Preferences.Add(entry);
            // A saved preference clears an old rejection of the same subject: the
            // user changed their mind, and the newest explicit word wins.
            _document.Rejected.RemoveAll(rejected =>
                rejected.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));
            Persist();
            return entry;
        }
    }

    /// <summary>
    /// Records a tentative pattern. It is stored so the suggestion survives a
    /// restart, but it is reported as a question, never as known truth.
    /// Returns false when learning is paused or the subject was already rejected.
    /// </summary>
    public bool TrySuggestPreference(string category, string subject, string stance, double confidence,
        string evidence, out string refusal)
    {
        lock (_gate)
        {
            if (_document.LearningPaused)
            {
                refusal = "Preference learning is paused; the user asked not to collect new patterns.";
                return false;
            }

            if (_document.Rejected.Any(entry => entry.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase)))
            {
                refusal = $"The user already rejected '{subject}' as a preference. Do not suggest it again.";
                return false;
            }

            ProfilePreference? open = _document.Preferences.FirstOrDefault(entry =>
                entry.SupersededBy is null &&
                entry.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));
            if (open is not null)
            {
                refusal = open.Provenance == ProfileProvenance.InferredTentative
                    ? $"'{subject}' is already waiting for the user's confirmation."
                    : $"'{subject}' is already saved ({open.Provenance}).";
                return false;
            }

            _document.Preferences.Add(new ProfilePreference
            {
                Category = category,
                Subject = subject,
                Stance = stance,
                Provenance = ProfileProvenance.InferredTentative,
                Confidence = Math.Clamp(confidence, 0, 1),
                Evidence = evidence
            });
            Persist();
            refusal = string.Empty;
            return true;
        }
    }

    /// <summary>Confirms a tentative suggestion: it becomes UserConfirmed truth.</summary>
    public bool ConfirmSuggestion(string subject)
    {
        lock (_gate)
        {
            ProfilePreference? entry = _document.Preferences.FirstOrDefault(candidate =>
                candidate.Provenance == ProfileProvenance.InferredTentative &&
                candidate.SupersededBy is null &&
                candidate.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return false;
            }

            entry.Provenance = ProfileProvenance.UserConfirmed;
            entry.LastReinforcedAt = DateTimeOffset.Now;
            Persist();
            return true;
        }
    }

    /// <summary>Rejects a suggestion and remembers the rejection itself.</summary>
    public bool RejectSuggestion(string subject)
    {
        lock (_gate)
        {
            ProfilePreference? entry = _document.Preferences.FirstOrDefault(candidate =>
                candidate.Provenance == ProfileProvenance.InferredTentative &&
                candidate.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));
            string category = entry?.Category ?? "other";
            if (entry is not null)
            {
                _document.Preferences.Remove(entry);
            }

            if (!_document.Rejected.Any(rejected =>
                    rejected.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase)))
            {
                _document.Rejected.Add(new RejectedSuggestion(subject, category, DateTimeOffset.Now));
            }

            Persist();
            return true;
        }
    }

    /// <summary>Forgets one subject entirely — preferences and any rejection record.</summary>
    public int Forget(string subject)
    {
        lock (_gate)
        {
            int removed = _document.Preferences.RemoveAll(entry =>
                entry.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));
            removed += _document.Songs.RemoveAll(song =>
                song.Title.Equals(subject, StringComparison.OrdinalIgnoreCase) ||
                song.Aliases.Any(alias => alias.Equals(subject, StringComparison.OrdinalIgnoreCase)));
            Persist();
            return removed;
        }
    }

    /// <summary>Clears every preference in one category. Songs are not touched.</summary>
    public int ClearCategory(string category)
    {
        lock (_gate)
        {
            int removed = _document.Preferences.RemoveAll(entry =>
                entry.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            Persist();
            return removed;
        }
    }

    /// <summary>Adds or reinforces a song. Same honesty rule: stance "favorite" needs
    /// user-stated/confirmed provenance; a verified action records facts (path,
    /// playlist membership) but never invents liking.</summary>
    public ProfileSong UpsertSong(string title, string artist, string stance, ProfileProvenance provenance,
        string url = "", string urlKind = "", string localPath = "", string? alias = null, string album = "")
    {
        if (stance is "favorite" or "like" or "dislike" &&
            provenance is not (ProfileProvenance.UserStated or ProfileProvenance.UserConfirmed))
        {
            throw new InvalidOperationException(
                "A play, download or search is not a stated taste. Keep stance 'mentioned' or ask the user first.");
        }

        lock (_gate)
        {
            ProfileSong? song = FindSongLocked(title, artist);
            if (song is null)
            {
                song = new ProfileSong { Title = title.Trim(), Artist = artist.Trim() };
                _document.Songs.Add(song);
            }

            song.Stance = stance;
            song.Provenance = provenance;
            song.LastReinforcedAt = DateTimeOffset.Now;
            if (!string.IsNullOrWhiteSpace(album)) song.Album = album.Trim();
            if (!string.IsNullOrWhiteSpace(url)) song.Url = url.Trim();
            if (!string.IsNullOrWhiteSpace(urlKind)) song.UrlKind = urlKind.Trim();
            if (!string.IsNullOrWhiteSpace(localPath)) song.LocalPath = localPath.Trim();
            if (!string.IsNullOrWhiteSpace(alias) &&
                !song.Aliases.Contains(alias!, StringComparer.OrdinalIgnoreCase))
            {
                song.Aliases.Add(alias!.Trim());
            }

            Persist();
            return song;
        }
    }

    /// <summary>Records a VERIFIED playlist membership — call only after the playlist
    /// was written and read back. Downloading is never playlist membership.</summary>
    public bool RecordPlaylistMembership(string title, string artist, string playlistName)
    {
        lock (_gate)
        {
            ProfileSong? song = FindSongLocked(title, artist);
            if (song is null)
            {
                return false;
            }

            if (!song.Playlists.Contains(playlistName, StringComparer.OrdinalIgnoreCase))
            {
                song.Playlists.Add(playlistName);
            }

            song.LastReinforcedAt = DateTimeOffset.Now;
            Persist();
            return true;
        }
    }

    /// <summary>Finds songs matching a free-text query against title, artist and aliases.</summary>
    public IReadOnlyList<ProfileSong> FindSongs(string query, int maximum = 5)
    {
        lock (_gate)
        {
            string needle = query.Trim();
            return _document.Songs
                .Where(song =>
                    song.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    song.Artist.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    song.Aliases.Any(a => a.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                                          needle.Contains(a, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(song => song.Stance == "favorite")
                .ThenByDescending(song => song.LastReinforcedAt)
                .Take(maximum)
                .ToList();
        }
    }

    public IReadOnlyList<ProfileSong> ListSongs(string? stance = null)
    {
        lock (_gate)
        {
            return _document.Songs
                .Where(song => stance is null || song.Stance.Equals(stance, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(song => song.LastReinforcedAt)
                .ToList();
        }
    }

    public IReadOnlyList<ProfilePreference> ListPreferences(bool includeSuperseded = false)
    {
        lock (_gate)
        {
            return _document.Preferences
                .Where(entry => includeSuperseded || entry.SupersededBy is null)
                .OrderByDescending(entry => entry.LastReinforcedAt)
                .ToList();
        }
    }

    public IReadOnlyList<RejectedSuggestion> ListRejected()
    {
        lock (_gate) { return _document.Rejected.ToList(); }
    }

    private ProfileSong? FindSongLocked(string title, string artist) =>
        _document.Songs.FirstOrDefault(song =>
            song.Title.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(song.Artist) ||
             song.Artist.Equals(artist.Trim(), StringComparison.OrdinalIgnoreCase)));

    private void Persist()
    {
        string temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_document, SerializerOptions));
        File.Move(temporary, _filePath, overwrite: true);
    }

    private sealed class ProfileDocument
    {
        public bool LearningPaused { get; set; }
        public List<ProfilePreference> Preferences { get; set; } = new();
        public List<ProfileSong> Songs { get; set; } = new();
        public List<RejectedSuggestion> Rejected { get; set; } = new();
    }
}
