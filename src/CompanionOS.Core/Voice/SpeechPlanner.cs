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
    /// Trims an answer for the voice only. The first sentence always survives — a reply
    /// that says nothing is worse than one that says too much. Sentences that look like
    /// inventories (commas, semicolons, bullets, numbered lists) never reach the ears,
    /// and the budget caps how much extra prose may tag along after the verdict.
    /// </summary>
    public static string TrimForSpeech(string? answerText, int budgetCharacters)
    {
        string text = (answerText ?? string.Empty).Trim();
        if (text.Length == 0 || budgetCharacters <= 0)
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
                string opener = enumerated ? FirstClause(sentence, Math.Max(60, budgetCharacters / 2)) : sentence;
                kept.Add(opener);
                used += opener.Length;
                continue;
            }

            if (enumerated || used + 1 + sentence.Length > budgetCharacters)
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
    /// True when the answer announces completed work ("Resumed.", "Playing Machine Girl.",
    /// "Added it to the playlist.") in a sentence that is not a question. The brain runs
    /// this against a turn that produced zero tool receipts; a match earns one retry with
    /// a receipt check, because announcing work that never ran is the one lie a companion
    /// cannot tell.
    /// </summary>
    public static bool LooksLikeUnbackedClaim(string? answerText)
    {
        if (string.IsNullOrWhiteSpace(answerText))
        {
            return false;
        }

        foreach (string sentence in SpeechTextSanitizer.SplitIntoSentences(answerText, 4))
        {
            string lean = sentence.Trim();
            if (lean.Length == 0 || lean.EndsWith('?'))
            {
                continue;
            }

            if (ClaimPattern().IsMatch(lean))
            {
                return true;
            }
        }

        return false;
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

    [GeneratedRegex(
        @"\b(resumed|playing|played|paused|started|stopped|added|created|deleted|removed|opened|closed|saved|downloaded|sent|muted|unmuted|locked|unlocked|launched|done)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaimPattern();
}
