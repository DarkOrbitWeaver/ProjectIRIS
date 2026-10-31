using CompanionOS.Core.Brain;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// Verb fidelity. "find", "open", "play", "download", "add to my library" and
/// "add to my playlist" are different requests, and the small model conflates them
/// constantly. The guard judges the REQUEST, which is an input — her sentences are
/// still never scored by a pattern.
/// </summary>
public sealed class IntentGuardTests
{
    [Fact]
    public void PlayingSomethingDoesNotGrantDownloadingIt()
    {
        IReadOnlySet<RequestedAction> granted = IntentGuard.Parse("play that Yates XV song");

        Assert.Contains(RequestedAction.Play, granted);
        Assert.DoesNotContain(RequestedAction.Download, granted);
        Assert.True(IntentGuard.IsUnrequestedEscalation(granted, "youtube_download", true, out string refusal));
        Assert.Contains("not to download a file", refusal, StringComparison.Ordinal);
        Assert.Contains("ask", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlayingSomethingDoesNotGrantARewriteOfAPlaylist()
    {
        IReadOnlySet<RequestedAction> granted = IntentGuard.Parse("play my favourite song");
        Assert.True(IntentGuard.IsUnrequestedEscalation(granted, "music_playlists", true, out _));
    }

    [Fact]
    public void AReadOnlyCallIsNeverBlockedEvenWhenItsNameLooksLikeAWrite()
    {
        IReadOnlySet<RequestedAction> granted = IntentGuard.Parse("what is in my playlist?");
        // Inspecting a playlist is a read; only a mutating call can be an escalation.
        Assert.False(IntentGuard.IsUnrequestedEscalation(granted, "music_playlists", false, out _));
    }

    [Fact]
    public void AnExplicitDownloadRequestIsAllowedThrough()
    {
        IReadOnlySet<RequestedAction> granted = IntentGuard.Parse("download that video for me");

        Assert.Contains(RequestedAction.Download, granted);
        Assert.False(IntentGuard.IsUnrequestedEscalation(granted, "youtube_download", true, out _));
    }

    [Fact]
    public void AClarificationTurnKeepsTheVerbTheUserGaveEarlier()
    {
        // "download it" in turn one, "the second one" in turn two. Parsing only the
        // latest turn would refuse the very action the user asked for.
        IReadOnlySet<RequestedAction> latestOnly = IntentGuard.Parse("the second one");
        Assert.True(IntentGuard.IsUnrequestedEscalation(latestOnly, "youtube_download", true, out _));

        IReadOnlySet<RequestedAction> acrossTask =
            IntentGuard.ParseAll(new[] { "download that song", "the second one" });
        Assert.False(IntentGuard.IsUnrequestedEscalation(acrossTask, "youtube_download", true, out _));
    }

    [Fact]
    public void AddingToAPlaylistGrantsTheLibraryImportItRequires()
    {
        IReadOnlySet<RequestedAction> granted = IntentGuard.Parse("add that song to my favourites playlist");

        Assert.Contains(RequestedAction.PlaylistWrite, granted);
        // Indexing the file is the prerequisite of the requested add, not an escalation.
        Assert.False(IntentGuard.IsUnrequestedEscalation(granted, "music_import_file", true, out _));
        Assert.False(IntentGuard.IsUnrequestedEscalation(granted, "music_playlists", true, out _));
        // It still does not grant a download.
        Assert.True(IntentGuard.IsUnrequestedEscalation(granted, "youtube_download", true, out _));
    }

    [Fact]
    public void PlayingAPlaylistIsPlaybackNotAPlaylistWrite()
    {
        IReadOnlySet<RequestedAction> granted = IntentGuard.Parse("play my night drive playlist");

        Assert.Contains(RequestedAction.Play, granted);
        Assert.DoesNotContain(RequestedAction.PlaylistWrite, granted);
    }

    [Fact]
    public void AskingToFindSomethingNeverGrantsSending()
    {
        IReadOnlySet<RequestedAction> granted = IntentGuard.Parse("find that photo of the build");

        Assert.Contains(RequestedAction.Find, granted);
        Assert.True(IntentGuard.IsUnrequestedEscalation(granted, "telegram_send_media", true, out string refusal));
        Assert.Contains("send a message", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownToolIsNeverBlockedBecauseItsTierIsStillItsRealGate()
    {
        IReadOnlySet<RequestedAction> granted = IntentGuard.Parse("play something");
        Assert.False(IntentGuard.IsUnrequestedEscalation(granted, "some_plugin_action", true, out _));
        Assert.Null(IntentGuard.ClassifyTool("some_plugin_action"));
    }

    [Theory]
    [InlineData("youtube_download", RequestedAction.Download)]
    [InlineData("music_import_file", RequestedAction.ImportToLibrary)]
    [InlineData("music_playlists", RequestedAction.PlaylistWrite)]
    [InlineData("telegram_send_message", RequestedAction.Send)]
    [InlineData("delete_file", RequestedAction.Delete)]
    public void ToolsAreClassifiedByTheActionTheyActuallyPerform(string toolName, RequestedAction expected)
    {
        Assert.Equal(expected, IntentGuard.ClassifyTool(toolName));
    }
}
