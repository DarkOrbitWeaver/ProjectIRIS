using System.Text.RegularExpressions;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Voice;

/// <summary>
/// The speech-economy floor: her written answer lives on screen, her voice carries verdicts.
///
/// Three jobs it owns, all born from one real afternoon of waste. She read a whole music
/// library out loud — 161 characters, 5.7 s of synthesis, 9.4 s of playback — and she once
/// answered "Resumed." without a single tool receipt behind it.
///
///   1. TrimForSpeech keeps the first sentence (the verdict) and drops list-shaped
///      sentences: inventories belong on screen, where they are instant and skimmable.
///   2. LooksLikeUnbackedClaim spots the "Resumed." shape, so the brain can demand a
///      receipt before the turn is allowed to end. This is the class the honesty retry
///      consults; the OS polices the model, not the other way round.
///
/// It never invents words, never appends filler, and never touches the written reply.
/// </summary>
public static partial class SpeechPlanner
{
    /// <summary>
    /// Speech pays time: roughly 35 ms per character of synthesis and playback each.
    /// The records of 20:15 read like a radio show — four segments, nine seconds of
    /// voice for one request — which is why her voice carries one verdict sentence,
    /// plus at most one short follow-on, inside this ceiling.
    ///
    /// The 140-character economy stays as decided with the boss. The companion
    /// SpeechBudgetCharacters setting (default 240) is removed rather than honored:
    /// TrimForSpeech clamps to this constant anyway, so the setting only ever lied —
    /// it promised to allow more than the voice would ever say (2026-10-27 audit).
    /// </summary>
    public const int SpeechEconomyCharacters = 140;

    /// <summary>The tag-along sentence must be short or it stays on screen only.</summary>
    private const int TagAlongMaximumCharacters = 60;

    /// <summary>
    /// Trims an answer for the voice only. The first sentence always survives — a reply
    /// that says nothing is worse than one that says too much. One short second sentence
    /// may ride along ("Sure boss. Playing it now."), and only while both fit the ceiling;
    /// everything else — inventories, third sentences, long offers — stays on screen.
    /// </summary>
    public static string TrimForSpeech(string? answerText, int budgetCharacters)
    {
        string text = (answerText ?? string.Empty).Trim();
        int effectiveBudget = Math.Min(budgetCharacters, SpeechEconomyCharacters);
        if (text.Length == 0 || effectiveBudget <= 0)
        {
            return text;
        }

        IReadOnlyList<string> sentences = SpeechTextSanitizer.SplitIntoSentences(text, 4);
        if (sentences.Count == 0)
        {
            return text;
        }

        List<string> kept = new(sentences.Count);
        int used = 0;
        for (int index = 0; index < sentences.Count; index++)
        {
            string sentence = sentences[index].Trim();
            if (sentence.Length == 0)
            {
                continue;
            }

            bool enumerated = IsEnumerationShape(sentence);
            if (kept.Count == 0)
            {
                // The verdict speaks even when it arrived wrapped inside a list.
                string opener = enumerated ? FirstClause(sentence, Math.Max(60, effectiveBudget / 2)) : sentence;
                kept.Add(opener);
                used += opener.Length;
                continue;
            }

            bool roomForTagAlong = kept.Count == 1 && sentence.Length <= TagAlongMaximumCharacters &&
                                   used + 1 + sentence.Length <= effectiveBudget;
            if (enumerated || !roomForTagAlong)
            {
                continue;
            }

            kept.Add(sentence);
            used += sentence.Length + 1;
        }

        if (kept.Count == sentences.Count)
        {
            return text;
        }

        string trimmed = string.Join(" ", kept);
        IrisEvent.Log("Voice", "speech.brevity_trimmed", LogLevel.Trace,
            data: new Dictionary<string, string?>
            {
                ["sentences"] = sentences.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["spoken"] = kept.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["chars_before"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["chars_after"] = trimmed.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        return trimmed;
    }

    /// <summary>
    /// The collapsed-reply shape this run caught in her records: handed a receipt check,
    /// the 4B brain answered a whole action request with exactly "Boss." and stopped.
    /// A reply that is empty, or nothing but address words and backchannel ("yes",
    /// "okay", "thanks"), is not an answer to a request for work — the brain treats it
    /// as a collapse and answers from the receipts instead.
    /// </summary>
    private static readonly HashSet<string> CollapsedReplyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "boss", "hey", "hi", "hello", "iris", "yes", "no", "yeah", "nah", "ok", "okay",
        "sure", "k", "kk", "thanks", "wdym"
    };

    /// <summary>True when a whole reply is nothing but address words and backchannel.</summary>
    public static bool LooksLikeCollapsedReply(string? answerText)
    {
        string text = (answerText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return true;
        }

        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (string raw in words)
        {
            if (!CollapsedReplyWords.Contains(raw.TrimEnd('.', '!', '?', ',', ':', ';', '…')))
            {
                return false;
            }
        }

        return words.Length > 0;
    }

    /// <summary>Lists read like catalogue pages: many commas, semicolons or inline bullets.</summary>
    private static bool IsEnumerationShape(string sentence)
    {
        int commas = 0;
        int semicolons = 0;
        foreach (char character in sentence)
        {
            if (character == ',') commas++;
            else if (character == ';') semicolons++;
        }

        if (commas >= 4 || semicolons >= 2)
        {
            return true;
        }

        return EnumerationMarkerPattern().Matches(sentence).Count >= 2;
    }

    /// <summary>Keeps the opening clause of a list-shaped first sentence, capped in length.</summary>
    private static string FirstClause(string sentence, int maximumCharacters)
    {
        int cut = sentence.IndexOfAny([',', ';']);
        string clause = cut > 12 ? sentence[..cut].Trim() : string.Empty;
        if (clause.Length == 0)
        {
            clause = sentence;
            if (clause.Length > maximumCharacters)
            {
                clause = clause[..maximumCharacters].TrimEnd() + "…";
            }
        }

        return clause;
    }

    [GeneratedRegex(@"(?:^|[\s])(?:[-•*]\s|\d{1,2}[\.\)]\s)", RegexOptions.CultureInvariant)]
    private static partial Regex EnumerationMarkerPattern();

    // The one classifier left in the planner, per the boss's ruling: regex judges the
    // REQUEST (an input, like form validation) — never her words. Every language cop
    // built on her output eventually punished a truthful sentence ("the video is
    // paused", "an ASMR playlist playing…"). The brain's gates stay on receipts and
    // the collapse shape alone; both are facts, not sentence shapes.
    /// <summary>
    /// A clear order aimed at her hands — the "play a song from our playlist" family.
    /// The turn contract uses it to scope the receipt check to turns where work is
    /// actually asked for; informational requests are never gated.
    ///
    /// Verbs are authoritative. The media-noun family only escalates a sentence when
    /// it is not question-shaped: "what song is playing?" used to pass as an order on
    /// the bare word "song" and drew a receipt-check nudge demanding she act on a
    /// question that has no tool (2026-10-27 audit).
    ///
    /// A one-letter-typoed verb still counts (the boss's "please ressume" slid past
    /// the gate unanswered). This is form validation on the REQUEST — five letters or
    /// more, one edit away from a known verb — not a judgment on any of her words.
    /// </summary>
    public static bool IsActionRequest(string? userText)
    {
        string text = (userText ?? string.Empty).Trim();
        if (text.Length < 4)
        {
            return false;
        }

        if (ActionVerbPattern().IsMatch(text))
        {
            return true;
        }

        if (MediaNounPattern().IsMatch(text) && !QuestionShapePattern().IsMatch(text))
        {
            return true;
        }

        return ContainsOneEditAwayVerb(text);
    }

    private static readonly string[] ActionVerbs =
    {
        "play", "pause", "resume", "unpause", "shuffle", "skip", "next", "previous", "open", "launch",
        "mute", "unmute", "volume", "timer", "alarm", "note", "download", "find", "search"
    };

    /// <summary>True when some token is one edit away from a known action verb. The
    /// length floor keeps short generic words ("fine", "none") from matching by luck.</summary>
    private static bool ContainsOneEditAwayVerb(string text)
    {
        int start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && !char.IsLetter(text[start]))
            {
                start++;
            }

            int end = start;
            while (end < text.Length && char.IsLetter(text[end]))
            {
                end++;
            }

            if (end - start >= 5)
            {
                string token = text[start..end].ToLowerInvariant();
                foreach (string verb in ActionVerbs)
                {
                    if (Math.Abs(verb.Length - token.Length) <= 1 && IsWithinOneEdit(token, verb))
                    {
                        return true;
                    }
                }
            }

            start = end;
        }

        return false;
    }

    private static bool IsWithinOneEdit(string token, string verb)
    {
        if (token.Equals(verb, StringComparison.Ordinal))
        {
            return true;
        }

        // Deletion or insertion.
        if (token.Length == verb.Length - 1)
        {
            for (int skip = 0; skip < verb.Length; skip++)
            {
                if (string.Concat(verb[..skip], verb[(skip + 1)..]).Equals(token, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        // Insertion into the verb.
        if (token.Length == verb.Length + 1)
        {
            for (int skip = 0; skip < token.Length; skip++)
            {
                if (string.Concat(token[..skip], token[(skip + 1)..]).Equals(verb, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        // Substitution: same length, exactly one differing letter.
        if (token.Length == verb.Length)
        {
            int differences = 0;
            for (int index = 0; index < token.Length; index++)
            {
                if (token[index] != verb[index] && ++differences > 1)
                {
                    return false;
                }
            }

            return differences == 1;
        }

        return false;
    }

    [GeneratedRegex(
        @"\b(play|pause|resume|unpause|shuffle|skip|next|previous|open|launch|mute|unmute|volume|timer|alarm|note|download|find|search)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActionVerbPattern();

    [GeneratedRegex(
        @"\b(song|track|playlist|music|video|yt|youtube|game|window|app|folder|file)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaNounPattern();

    [GeneratedRegex(
        @"^\s*(what|which|who|when|where|why|how|is|are|was|were|do|does|did|can|could|should|would|will|shall|any)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionShapePattern();
}
