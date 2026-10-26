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
    /// </summary>
    private const int MaximumSpeechCharacters = 140;

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
        int effectiveBudget = Math.Min(budgetCharacters, MaximumSpeechCharacters);
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
    /// True when the answer announces work she never did, or promises work that never
    /// ran. Three agent shapes count, and specifically NOT the world describing itself:
    ///
    ///   1. First-person work words near the subject — "I'll open it", "I just paused it",
    ///      "let me check the library" (a turn-ending promise is a broken one).
    ///   2. Bare action announcements at sentence start — "Resumed.", "Done.",
    ///      "Playing Machine Girl."
    ///   3. Past completions anywhere — "Alright boss, playlist resumed."
    ///
    /// Media-state sentences — "You are watching an ASMR playlist playing some stuff",
    /// "the video is paused" — are summaries of reality, not claims of work. The run of
    /// 20:25 replaced her *correct* "what am I watching" answer with the receipts
    /// because the old regex met the word "playing" inside a video title. Information
    /// needs no tool receipts; claiming she acted does.
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

            if (SelfWorkPattern().IsMatch(lean) ||
                SentenceStartClaimPattern().IsMatch(lean) ||
                PastCompletionPattern().IsMatch(lean))
            {
                return true;
            }
        }

        return false;
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

    // First-person work: the subject is her hands ("i", "i'll", "let me") and an action
    // verb follows within a short window. The window stays tight so chatter that merely
    // mentions verbs ("I think the song you're playing is nice") never matches.
    [GeneratedRegex(
        @"\b(?:i\b|im\b|ill\b|ive\b|i'd\b|id\b|i am\b|i was\b|i will\b|i'll\b|i've\b|i just\b|let me\b|let's\b|we're\b|we will\b|we'll\b|going to\b)" +
        @"[^.!?]{0,16}?\b(resume|resumed|play|playing|played|pause|paused|start|started|stop|stopped|open|opened|" +
        @"close|clos|save|saved|download|downloaded|send|sent|lock|locked|unlock|unlocked|mute|muted|unmute|" +
        @"unmuted|launch|launched|create|created|add|added|remove|removed|delete|deleted|check|look|find|fetch|grab|bring|did)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelfWorkPattern();

    // "Resumed.", "Done.", "Playing Machine Girl, boss." — an announcement that starts
    // with the action itself is her voice claiming the deed.
    [GeneratedRegex(
        @"(?:^|[?!.]\s)\s*(resumed|playing|played|paused|started|stopped|added|created|deleted|removed|opened|closed|saved|downloaded|sent|muted|unmuted|locked|unlocked|launched|done)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SentenceStartClaimPattern();

    // Past completions anywhere, but only the verbs whose deed is hers by nature. The
    // media participles (playing, paused, started, stopped) stay out: "the song is
    // paused" describes the world. "You ..." keeps diary-recall chatter out: "you
    // created that playlist last Friday" is memory, not a claim she acted just now.
    [GeneratedRegex(
        @"(?<!\byou(?:\s+are)?\s)\b(resumed|added|created|removed|deleted|saved|downloaded|launched|unlocked|locked|muted|unmuted)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PastCompletionPattern();

    /// <summary>
    /// A clear order aimed at her hands — the "play a song from our playlist" family.
    /// The brain uses it for the 20:20 stall: hands routed, none run, and she asked
    /// permission for an order instead of acting.
    /// </summary>
    public static bool IsActionRequest(string? userText)
    {
        string text = (userText ?? string.Empty).Trim();
        if (text.Length < 4)
        {
            return false;
        }

        return ActionRequestPattern().IsMatch(text);
    }

    [GeneratedRegex(
        @"\b(play|pause|resume|shuffle|skip|next|previous|open|launch|mute|unmute|volume|timer|alarm|note|download|find|search)\b|\b(song|track|playlist|music|video|yt|youtube|game|window|app|folder|file)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActionRequestPattern();

    /// <summary>
    /// The offer-question shape that substitutes for the action: "want me to play that?",
    /// "shall I open it?", "the ASMR one you're watching?". On a turn whose hands are
    /// routed, asking permission for an unambiguous order is narrator-voice, not work.
    /// </summary>
    public static bool LooksLikeOfferInsteadOfAction(string? answerText)
    {
        string text = (answerText ?? string.Empty).Trim();
        if (text.Length < 8)
        {
            return false;
        }

        return OfferPattern().IsMatch(text);
    }

    [GeneratedRegex(
        @"\b(do you want|you want|want me|shall i|should i|could i|can i|may i|i can|would you like|what about|how about|if you want|if you'd like|which one|which playlist|which song|want that|or maybe)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OfferPattern();
}
