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
    /// <summary>Few enough words to be a prompt answer ("approve", "deny", "the second one").</summary>
    private const int MaximumPromptAnswerWords = 6;

    public static VoiceAddressingDecision Decide(string transcript, bool requiresWakePhrase,
        bool conversationWasOpen, WakePhraseMatcher wakeMatcher, bool promptOpen = false)
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
            // The permission card says "Say 'approve', or 'deny'" — honoring that cannot
            // require "Iris, approve". While a question is open, a SHORT utterance is
            // offered to the prompt layer, whose conservative matcher (negation beats
            // yes, sensitive actions need the explicit word, chatter is never an answer)
            // remains the actual safety gate. Long speech still needs the wake phrase.
            if (promptOpen)
            {
                string candidate = transcript.Trim();
                int words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (words > 0 && words <= MaximumPromptAnswerWords)
                {
                    return new VoiceAddressingDecision(VoiceAddressingOutcome.OpenConversationRequest, candidate);
                }
            }

            return new VoiceAddressingDecision(VoiceAddressingOutcome.WakeMiss, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(remainder) || wakeMatcher.IsGreetingOnly(remainder))
        {
            return new VoiceAddressingDecision(VoiceAddressingOutcome.CalledWithoutRequest, string.Empty);
        }

        return new VoiceAddressingDecision(VoiceAddressingOutcome.WakeRequest, remainder);
    }
}
