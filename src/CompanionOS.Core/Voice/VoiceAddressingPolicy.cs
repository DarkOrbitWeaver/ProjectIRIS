namespace CompanionOS.Core.Voice;

public enum VoiceAddressingOutcome
{
    OpenConversationRequest,
    WakeRequest,
    CalledWithoutRequest,
    WakeMiss
}

/// <summary>The addressing result for one transcript.</summary>
public sealed record VoiceAddressingDecision(VoiceAddressingOutcome Outcome, string RequestText);

/// <summary>Decides whether a transcript belongs to Iris without consulting mutable live state.</summary>
public static class VoiceAddressingPolicy
{
    public static VoiceAddressingDecision Decide(string transcript, bool requiresWakePhrase,
        bool conversationWasOpen, WakePhraseMatcher wakeMatcher)
    {
        ArgumentNullException.ThrowIfNull(wakeMatcher);
        // An open microphone or recent wake must not claim nearby conversation as
        // addressed speech. If wake phrase protection is enabled, every request must
        // begin with Iris, including follow-ups.
        if (!requiresWakePhrase)
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
