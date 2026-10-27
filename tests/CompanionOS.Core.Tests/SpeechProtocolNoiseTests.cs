using CompanionOS.Core.Voice;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// The chat-junk regression: a small brain imitates the bracket shapes it finds in its
/// own context, so "[ mood:calm ]" and "[ list_music ]" once printed as her words. The
/// strip is display hygiene for protocol WE sanctioned — tags and exact tool names
/// inside brackets — never a judgment of her sentences.
/// </summary>
public class SpeechProtocolNoiseTests
{
    [Fact]
    public void SpacedMoodTagIsStrippedForChatAndFace()
    {
        string answer = "[ mood:calm ]\nBoss.\nHow's the day treating you?";
        Assert.Equal("Boss.\nHow's the day treating you?",
            SpeechTextSanitizer.StripProtocolNoise(answer, Array.Empty<string>()));
        // The face reader tolerates the same spacing the stripper does.
        Assert.Equal("calm", SpeechTextSanitizer.ReadControlTag(answer, "mood"));
    }

    [Fact]
    public void BracketedKnownToolNameIsStrippedWhenItIsDeclared()
    {
        string answer = "Boss. How's the day treating you?\n[ list_music ]";
        Assert.Equal("Boss. How's the day treating you?",
            SpeechTextSanitizer.StripProtocolNoise(answer, ["list_music", "play_music"]));
    }

    [Fact]
    public void UnknownBracketTextSurvivesUntouched()
    {
        string answer = "I keep my notes in [sturdy] order.";
        Assert.Equal(answer, SpeechTextSanitizer.StripProtocolNoise(answer, ["list_music"]));
        // Markdown links and citations are her words, not protocol.
        string linked = "Read [the manual](https://example.com) first.";
        Assert.Equal(linked, SpeechTextSanitizer.StripProtocolNoise(linked, ["list_music"]));
    }

    [Fact]
    public void StrippingIsCaseInsensitiveOnNamesButLeavesOtherBrackets()
    {
        string answer = "[LIST_MUSIC] and [ track player ] stay, [play_music] goes.";
        Assert.Equal("and [ track player ] stay, goes.",
            SpeechTextSanitizer.StripProtocolNoise(answer, ["list_music", "play_music"]));
    }

    [Fact]
    public void OldSquareBracketReceiptsAreStrippedAsProtocolJunk()
    {
        // History from before the parenthesis fix carries "[receipts: ...]" — that
        // shape is the mimicry source, and a "-word: content" bracket is protocol by
        // construction, so the control-tag strip removes it wherever it lingers.
        string answer = "Played it. [receipts: play_music: done]";
        Assert.Equal("Played it.", SpeechTextSanitizer.StripProtocolNoise(answer, ["play_music"]));
    }
}
