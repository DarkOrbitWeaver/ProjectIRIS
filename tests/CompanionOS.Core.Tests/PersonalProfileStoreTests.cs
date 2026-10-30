using CompanionOS.Core.Memory;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// The consent and continuity contract of the personal profile:
/// stated favorites survive a restart, inferences never become truth without
/// confirmation, rejections stick, and a verified action records facts without
/// inventing liking.
/// </summary>
public sealed class PersonalProfileStoreTests : IDisposable
{
    private readonly string _directory;

    public PersonalProfileStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "iris-profile-" + Guid.NewGuid().ToString("n")[..8]);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AStatedFavoriteSongSurvivesARestart()
    {
        PersonalProfileStore store = new(_directory);
        store.UpsertSong("Midnight Signal", "Yates XV", "favorite", ProfileProvenance.UserStated,
            url: "https://www.youtube.com/watch?v=abc123", urlKind: "official", alias: "my late night song");

        // A new store over the same folder is the restart.
        PersonalProfileStore reopened = new(_directory);
        var matches = reopened.FindSongs("late night song");
        Assert.Single(matches);
        Assert.Equal("Midnight Signal", matches[0].Title);
        Assert.Equal("favorite", matches[0].Stance);
        Assert.Equal("https://www.youtube.com/watch?v=abc123", matches[0].Url);
        Assert.Equal(ProfileProvenance.UserStated, matches[0].Provenance);
    }

    [Fact]
    public void AnInferenceIsNotDurableTruthUntilConfirmed()
    {
        PersonalProfileStore store = new(_directory);
        Assert.True(store.TrySuggestPreference("artist", "Yates XV", "likes", 0.7,
            "three saved tracks, two replays", out _));

        var durable = store.ListPreferences()
            .Where(p => p.Provenance is ProfileProvenance.UserStated or ProfileProvenance.UserConfirmed);
        Assert.Empty(durable);

        Assert.True(store.ConfirmSuggestion("Yates XV"));
        Assert.Contains(store.ListPreferences(), p =>
            p.Subject == "Yates XV" && p.Provenance == ProfileProvenance.UserConfirmed);
    }

    [Fact]
    public void SavingAnInferredPreferenceDirectlyIsRefused()
    {
        PersonalProfileStore store = new(_directory);
        Assert.Throws<InvalidOperationException>(() =>
            store.SavePreference("artist", "SomeBand", "likes", ProfileProvenance.InferredTentative, "watched once"));
        Assert.Throws<InvalidOperationException>(() =>
            store.SavePreference("song", "SomeSong", "likes", ProfileProvenance.OneTimeChoice, "played once"));
    }

    [Fact]
    public void ARejectedSuggestionDoesNotResurface()
    {
        PersonalProfileStore store = new(_directory);
        Assert.True(store.TrySuggestPreference("genre", "phonk", "likes", 0.6, "two downloads", out _));
        Assert.True(store.RejectSuggestion("phonk"));

        Assert.False(store.TrySuggestPreference("genre", "phonk", "likes", 0.9, "another download", out string refusal));
        Assert.Contains("rejected", refusal);

        // And the rejection survives a restart too.
        PersonalProfileStore reopened = new(_directory);
        Assert.False(reopened.TrySuggestPreference("genre", "phonk", "likes", 0.9, "again", out _));
        Assert.True(reopened.IsRejected("phonk"));
    }

    [Fact]
    public void AnExplicitStatementOverridesAnOldRejection()
    {
        PersonalProfileStore store = new(_directory);
        store.TrySuggestPreference("genre", "phonk", "likes", 0.6, "evidence", out _);
        store.RejectSuggestion("phonk");

        // The user now SAYS they like it; their newest explicit word wins.
        store.SavePreference("genre", "phonk", "likes", ProfileProvenance.UserStated, "user: I like phonk now");
        Assert.False(store.IsRejected("phonk"));
    }

    [Fact]
    public void AChangedPreferenceSupersedesInsteadOfErasingHistory()
    {
        PersonalProfileStore store = new(_directory);
        store.SavePreference("artist", "DJ Example", "likes", ProfileProvenance.UserStated, "said in chat");
        store.SavePreference("artist", "DJ Example", "dislikes", ProfileProvenance.UserStated, "changed their mind");

        var all = store.ListPreferences(includeSuperseded: true)
            .Where(p => p.Subject == "DJ Example").ToList();
        Assert.Equal(2, all.Count);
        Assert.Single(all, p => p.SupersededBy is not null && p.Stance == "likes");
        Assert.Single(store.ListPreferences(), p => p.Subject == "DJ Example"); // Current view: one truth.
    }

    [Fact]
    public void PausedLearningRefusesNewSuggestionsButKeepsExistingData()
    {
        PersonalProfileStore store = new(_directory);
        store.SavePreference("artist", "Kept Artist", "likes", ProfileProvenance.UserStated, "stated");
        store.SetLearningPaused(true);

        Assert.False(store.TrySuggestPreference("artist", "New Band", "likes", 0.8, "evidence", out string refusal));
        Assert.Contains("paused", refusal);
        Assert.Contains(store.ListPreferences(), p => p.Subject == "Kept Artist");
    }

    [Fact]
    public void AVerifiedActionRecordsFactsButCannotInventLiking()
    {
        PersonalProfileStore store = new(_directory);
        // A download/play may record the song as 'mentioned' with verified metadata…
        var song = store.UpsertSong("Random Track", "Someone", "mentioned", ProfileProvenance.OneTimeChoice,
            localPath: @"C:\Music\random.mp3");
        Assert.Equal("mentioned", song.Stance);

        // …but neither that action nor a verified operation can silently rate it.
        Assert.Throws<InvalidOperationException>(() =>
            store.UpsertSong("Random Track", "Someone", "favorite", ProfileProvenance.OneTimeChoice));
        Assert.Throws<InvalidOperationException>(() =>
            store.UpsertSong("Random Track", "Someone", "favorite", ProfileProvenance.VerifiedAction));
    }

    [Fact]
    public void PlaylistMembershipIsOnlyRecordedForVerifiedAddsAndSurvivesRestart()
    {
        PersonalProfileStore store = new(_directory);
        Assert.False(store.RecordPlaylistMembership("Unknown Song", "", "Favorites")); // Not remembered: refused.

        store.UpsertSong("Midnight Signal", "Yates XV", "favorite", ProfileProvenance.UserStated);
        Assert.True(store.RecordPlaylistMembership("Midnight Signal", "Yates XV", "Favorites"));

        PersonalProfileStore reopened = new(_directory);
        Assert.Contains("Favorites", reopened.FindSongs("Midnight Signal")[0].Playlists);
    }

    [Fact]
    public void ForgettingASubjectRemovesItCompletely()
    {
        PersonalProfileStore store = new(_directory);
        store.SavePreference("artist", "Gone Band", "likes", ProfileProvenance.UserStated, "stated");
        store.UpsertSong("Gone Band", "", "mentioned", ProfileProvenance.UserStated);

        Assert.Equal(2, store.Forget("Gone Band"));
        Assert.DoesNotContain(store.ListPreferences(), p => p.Subject == "Gone Band");
        Assert.Empty(store.FindSongs("Gone Band"));
    }

    [Fact]
    public void ACorruptProfileFileIsPreservedNotOverwrittenSilently()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "profile.json");
        File.WriteAllText(path, "{ this is not json");

        PersonalProfileStore store = new(_directory);
        Assert.Empty(store.ListPreferences());
        Assert.True(File.Exists(path + ".corrupt.bak"));
    }
}
