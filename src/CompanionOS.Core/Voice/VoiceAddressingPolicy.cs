namespace CompanionOS.Core.Voice;

public enum VoiceAddressingOutcome
{
    OpenConversationRequest,
    WakeRequest,
    CalledWithoutRequest,
    WakeMiss
}

/// <summary>The addressing result for one transcript, based on the conversation state when capture began.</summary>
public sealed record VoiceAddressingDecision(VoiceAddressingOutcome Outcome, string RequestText);

/// <summary>Decides whether a transcript belongs to Iris without consulting mutable live state.</summary>
public static class VoiceAddressingPolicy
{
    public static VoiceAddressingDecision Decide(string transcript, bool requiresWakePhrase,
        bool conversationWasOpen, WakePhraseMatcher wakeMatcher)
    {
        ArgumentNullException.ThrowIfNull(wakeMatcher);
        if (!requiresWakePhrase || conversationWasOpen)
        {
            return new VoiceAddressingDecision(VoiceAddressingOutcome.OpenConversationRequest, transcript.Trim());
        }

        if (!wakeMatcher.TryMatch(transcript, out string remainder))
        {
            return new VoiceAddressingDecision(VoiceAddressingOutcome.WakeMiss, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(remainder) || wakeMatcher.IsGreetingOnly(remainder))
        {
            return new VoiceAddressingDecision(VoiceAddressingOutcome.CalledWithoutRequest, string.Empty);
        }

        return new VoiceAddressingDecision(VoiceAddressingOutcome.WakeRequest, remainder);
    }
}
