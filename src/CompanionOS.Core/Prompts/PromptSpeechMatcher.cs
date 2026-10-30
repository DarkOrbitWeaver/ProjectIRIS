using System.Text;

namespace CompanionOS.Core.Prompts;

public enum UtteranceOutcome
{
    /// <summary>Nothing usable. The card keeps waiting and shows a hint.</summary>
    NotUnderstood,

    /// <summary>One option was picked.</summary>
    Answer,

    /// <summary>"Never mind", "cancel", "stop". Safe for every prompt.</summary>
    Cancel,

    /// <summary>"More" / "back" on a numbered list.</summary>
    Page,

    /// <summary>"Repeat that" / "what are the options".</summary>
    Repeat,

    /// <summary>A casual yes on a sensitive action. Ask for the clear phrase instead.</summary>
    NeedsExplicitApproval,

    /// <summary>More than one reading, or too much chatter. Ask again.</summary>
    Ambiguous
}

public readonly record struct UtteranceMatch(
    UtteranceOutcome Outcome,
    PromptOption? Option = null,
    int PageDelta = 0,
    string Hint = "");

/// <summary>
/// Turns what the user said (or typed) into an answer for the open prompt.
///
/// This is pure logic with no audio, UI or Windows dependency, so every rule below is
/// covered by a unit test. The rules are conservative on purpose, because speech
/// recognition is wrong sometimes and the TV is sometimes talking:
///
///   - A refusal is always accepted. Any negation means no, and no beats yes.
///   - A sensitive approval needs a clear phrase ("approve", "allow it", "confirm").
///     A bare "yes" / "okay" / "sure" gets a polite "say approve".
///   - "Always allow" can never be spoken. It needs a button press.
///   - Long chatter is never read as an answer.
///   - Two different numbers, or a number that disagrees with a name, ask again.
/// </summary>
public static class PromptSpeechMatcher
{
    private const int MaxWordsSensitive = 6;
    private const int MaxWordsNormal = 8;

    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal)
    {
        "no", "nope", "nah", "not", "dont", "never", "deny", "denied", "decline", "declined",
        "refuse", "reject", "negative", "wont", "cant", "shouldnt", "wouldnt", "disallow"
    };

    private static readonly string[] CancelPhrases =
    {
        "cancel", "never mind", "nevermind", "forget it", "abort", "dismiss", "stop", "skip it", "leave it"
    };

    private static readonly string[] RepeatPhrases =
    {
        "repeat", "say that again", "say it again", "say again", "come again", "read them again",
        "read that again", "read the options", "what are the options", "what are my options",
        "what were the options", "what was the question"
    };

    private static readonly HashSet<string> ExplicitApprovalWords = new(StringComparer.Ordinal)
    {
        "approve", "approved", "allow", "allowed", "confirm", "confirmed", "authorize", "authorise"
    };

    private static readonly HashSet<string> CasualApprovalWords = new(StringComparer.Ordinal)
    {
        "yes", "yeah", "yep", "yup", "yea", "sure", "okay", "ok", "fine", "proceed", "affirmative"
    };

    private static readonly string[] CasualApprovalPhrases =
    {
        "go ahead", "do it", "sounds good", "go for it", "please do", "that works"
    };

    private static readonly HashSet<string> NextWords = new(StringComparer.Ordinal) { "next", "more", "forward" };
    private static readonly HashSet<string> PreviousWords = new(StringComparer.Ordinal) { "previous", "back", "earlier" };

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
        ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40,
        ["fifty"] = 50
    };

    private static readonly Dictionary<string, int> Ordinals = new(StringComparer.Ordinal)
    {
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5, ["sixth"] = 6,
        ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9, ["tenth"] = 10
    };

    // Speech engines write these for numbers, but only right after "number" or "option".
    private static readonly Dictionary<string, int> Homophones = new(StringComparer.Ordinal)
    {
        ["to"] = 2, ["too"] = 2, ["for"] = 4, ["won"] = 1, ["tree"] = 3
    };

    private static readonly HashSet<string> NumberLeadIns = new(StringComparer.Ordinal)
    {
        "number", "option", "choice", "pick", "select", "item", "track", "tab", "no"
    };

    private static readonly HashSet<string> NameStopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "to", "in", "on", "for", "with", "by", "youtube", "official",
        "play", "pick", "choose", "select", "number", "option", "choice", "item", "one", "please",
        "want", "like", "i", "go", "me", "that", "this", "it", "lets", "download", "open", "use",
        "take", "get", "i'll", "ill", "my", "is", "its"
    };

    public static UtteranceMatch Match(IrisPrompt prompt, string? utterance)
    {
        List<string> tokens = Tokenize(utterance ?? string.Empty);
        if (tokens.Count == 0)
        {
            return new UtteranceMatch(UtteranceOutcome.NotUnderstood, Hint: DefaultHint(prompt));
        }

        string joined = " " + string.Join(' ', tokens) + " ";

        if (ContainsAnyPhrase(joined, RepeatPhrases))
        {
            return new UtteranceMatch(UtteranceOutcome.Repeat);
        }

        if (ContainsAnyPhrase(joined, CancelPhrases))
        {
            return new UtteranceMatch(UtteranceOutcome.Cancel, prompt.DenyOption);
        }

        if (prompt.Kind == PromptKind.Numbered && tokens.Count <= 3)
        {
            if (tokens.Any(NextWords.Contains))
            {
                return new UtteranceMatch(UtteranceOutcome.Page, PageDelta: 1);
            }

            if (tokens.Any(PreviousWords.Contains))
            {
                return new UtteranceMatch(UtteranceOutcome.Page, PageDelta: -1);
            }
        }

        if (prompt.Kind == PromptKind.Confirm)
        {
            UtteranceMatch? confirm = MatchConfirm(prompt, tokens, joined);
            if (confirm is not null)
            {
                return confirm.Value;
            }
        }

        UtteranceMatch selection = MatchSelection(prompt, tokens, joined);
        return selection;
    }

    // ------------------------------------------------------------------ confirm

    private static UtteranceMatch? MatchConfirm(IrisPrompt prompt, List<string> tokens, string joined)
    {
        PromptOption? deny = prompt.DenyOption;
        if (tokens.Any(Negations.Contains))
        {
            // No always wins. "No, don't approve it" is a refusal, never an approval.
            return new UtteranceMatch(UtteranceOutcome.Cancel, deny);
        }

        bool explicitApproval = tokens.Any(ExplicitApprovalWords.Contains);
        bool casualApproval = tokens.Any(CasualApprovalWords.Contains) ||
                              ContainsAnyPhrase(joined, CasualApprovalPhrases);
        if (!explicitApproval && !casualApproval)
        {
            return null;
        }

        PromptOption? approve = prompt.Options.FirstOrDefault(option =>
            option.Role == PromptOptionRole.Approve && option.VoiceAllowed);
        if (approve is null)
        {
            return new UtteranceMatch(UtteranceOutcome.Ambiguous,
                Hint: "That one needs a button press.");
        }

        if (tokens.Contains("always") || tokens.Contains("forever") || tokens.Contains("permanently"))
        {
            return new UtteranceMatch(UtteranceOutcome.Ambiguous,
                Hint: "By voice I can only allow it once. Say \"approve\", or press Always.");
        }

        bool sensitive = prompt.Risk == PromptRisk.Sensitive;
        int limit = sensitive ? MaxWordsSensitive : MaxWordsNormal;
        if (tokens.Count > limit)
        {
            return new UtteranceMatch(UtteranceOutcome.Ambiguous,
                Hint: sensitive
                    ? "That was a lot. Say just \"approve\" or \"deny\"."
                    : "Say just \"yes\" or \"no\".");
        }

        if (sensitive && !explicitApproval)
        {
            return new UtteranceMatch(UtteranceOutcome.NeedsExplicitApproval,
                Hint: "This one acts for you, so say \"approve\", or \"deny\".");
        }

        return new UtteranceMatch(UtteranceOutcome.Answer, approve);
    }

    // ---------------------------------------------------------------- selection

    private static UtteranceMatch MatchSelection(IrisPrompt prompt, List<string> tokens, string joined)
    {
        IReadOnlyList<PromptOption> options = prompt.Options;
        if (options.Count == 0)
        {
            return new UtteranceMatch(UtteranceOutcome.NotUnderstood, Hint: DefaultHint(prompt));
        }

        // 1. Explicit aliases ("audio only", "the video one").
        List<PromptOption> aliasHits = options.Where(option => option.Aliases is not null &&
            option.Aliases.Any(alias => joined.Contains(" " + string.Join(' ', Tokenize(alias)) + " ",
                StringComparison.Ordinal))).ToList();
        if (aliasHits.Count == 1)
        {
            return Pick(prompt, aliasHits[0]);
        }

        // 2. Numbers and ordinals ("two", "the second one", "number 3", "last").
        List<int> numbers = ExtractNumbers(tokens, options.Count);

        // 3. Words from the option text ("the lofi one").
        List<PromptOption> nameHits = FindByName(options, tokens, out bool nameTie);

        if (numbers.Count > 1)
        {
            return new UtteranceMatch(UtteranceOutcome.Ambiguous,
                Hint: "I heard more than one number. Say just one.");
        }

        if (numbers.Count == 1)
        {
            int number = numbers[0];
            if (number < 1 || number > options.Count)
            {
                return new UtteranceMatch(UtteranceOutcome.Ambiguous,
                    Hint: options.Count == 1
                        ? "I only have one option."
                        : $"I only have {options.Count} options. Say 1 to {options.Count}.");
            }

            PromptOption byNumber = options[number - 1];
            if (nameHits.Count == 1 && !ReferenceEquals(nameHits[0], byNumber))
            {
                return new UtteranceMatch(UtteranceOutcome.Ambiguous,
                    Hint: "The number and the name point at different options. Say just the number.");
            }

            return Pick(prompt, byNumber);
        }

        if (nameHits.Count == 1)
        {
            return Pick(prompt, nameHits[0]);
        }

        if (nameHits.Count > 1 || nameTie)
        {
            return new UtteranceMatch(UtteranceOutcome.Ambiguous,
                Hint: "That fits more than one. Say the number.");
        }

        return new UtteranceMatch(UtteranceOutcome.NotUnderstood, Hint: DefaultHint(prompt));
    }

    private static UtteranceMatch Pick(IrisPrompt prompt, PromptOption option)
    {
        if (!option.VoiceAllowed)
        {
            return new UtteranceMatch(UtteranceOutcome.Ambiguous, Hint: "That one needs a button press.");
        }

        return new UtteranceMatch(UtteranceOutcome.Answer, option);
    }

    private static List<int> ExtractNumbers(List<string> tokens, int optionCount)
    {
        List<int> found = new();
        for (int index = 0; index < tokens.Count; index++)
        {
            string token = tokens[index];
            string? previous = index > 0 ? tokens[index - 1] : null;
            string? next = index + 1 < tokens.Count ? tokens[index + 1] : null;

            if (int.TryParse(token, out int digits) && token.Length <= 3)
            {
                found.Add(digits);
                continue;
            }

            if (token == "last")
            {
                found.Add(optionCount);
                continue;
            }

            if (Ordinals.TryGetValue(token, out int ordinal))
            {
                found.Add(ordinal);
                continue;
            }

            if (NumberWords.TryGetValue(token, out int value))
            {
                // "the one with lofi", "the second one": "one" is a pronoun there.
                if (token == "one" &&
                    ((previous is not null && (Ordinals.ContainsKey(previous) ||
                        previous is "the" or "this" or "that" or "which" or "any" or "another" or "last")) ||
                     (next is not null && next is "with" or "that" or "by" or "from" or "of" or "in" or "about") ||
                     // "the lofi one", "the bohemian rhapsody one": a trailing "one" in a longer
                     // phrase is a pronoun, unless it follows "number", "option" or "pick".
                     (next is null && tokens.Count >= 3 &&
                      (previous is null || !NumberLeadIns.Contains(previous)))))
                {
                    continue;
                }

                // "twenty one" is 21.
                if (value >= 20 && next is not null && NumberWords.TryGetValue(next, out int ones) &&
                    ones is >= 1 and <= 9)
                {
                    found.Add(value + ones);
                    index++;
                    continue;
                }

                found.Add(value);
                continue;
            }

            if (Homophones.TryGetValue(token, out int homophone) &&
                (tokens.Count == 1 || (previous is not null && NumberLeadIns.Contains(previous))))
            {
                found.Add(homophone);
            }
        }

        return found.Distinct().ToList();
    }

    /// <summary>
    /// Finds options whose text contains every meaningful word the user said. The most
    /// specific one wins when it is clearly ahead; otherwise the caller asks again.
    /// </summary>
    private static List<PromptOption> FindByName(IReadOnlyList<PromptOption> options, List<string> tokens,
        out bool tie)
    {
        tie = false;
        List<string> said = tokens.Where(token => token.Length >= 2 && !NameStopWords.Contains(token) &&
                                                  !int.TryParse(token, out _) &&
                                                  !Ordinals.ContainsKey(token) &&
                                                  !NumberWords.ContainsKey(token) &&
                                                  token != "last")
            .ToList();
        if (said.Count == 0 || said.All(word => word.Length < 3))
        {
            return new List<PromptOption>();
        }

        List<(PromptOption Option, double Specificity)> hits = new();
        foreach (PromptOption option in options)
        {
            List<string> words = Tokenize(option.Label + " " + option.Detail)
                .Where(word => !NameStopWords.Contains(word)).ToList();
            if (words.Count == 0)
            {
                continue;
            }

            bool allSaidWordsAppear = said.All(word => words.Any(candidate =>
                candidate == word || (word.Length >= 4 && candidate.StartsWith(word, StringComparison.Ordinal))));
            if (!allSaidWordsAppear)
            {
                continue;
            }

            double specificity = (double)said.Count / words.Count;
            hits.Add((option, specificity));
        }

        if (hits.Count <= 1)
        {
            return hits.Select(hit => hit.Option).ToList();
        }

        List<(PromptOption Option, double Specificity)> ordered = hits.OrderByDescending(hit => hit.Specificity).ToList();
        if (ordered[0].Specificity - ordered[1].Specificity >= 0.15)
        {
            return new List<PromptOption> { ordered[0].Option };
        }

        tie = true;
        return ordered.Select(hit => hit.Option).ToList();
    }

    // ------------------------------------------------------------------ helpers

    public static string DefaultHint(IrisPrompt prompt) => prompt.Kind switch
    {
        PromptKind.Confirm => prompt.Risk == PromptRisk.Sensitive
            ? "Say \"approve\", or \"deny\"."
            : "Say \"yes\", or \"no\".",
        _ => prompt.Options.Count <= 1
            ? "Say the name, or \"cancel\"."
            : $"Say a number from 1 to {prompt.Options.Count}, or the name."
    };

    internal static List<string> Tokenize(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (character == '\'' || character == '\u2019')
            {
                // don't -> dont, so contractions compare as one word.
            }
            else
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static bool ContainsAnyPhrase(string paddedText, IEnumerable<string> phrases)
    {
        foreach (string phrase in phrases)
        {
            if (paddedText.Contains(" " + phrase + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
