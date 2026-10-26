using System.Text;
using CompanionOS.Core.Brain;

namespace CompanionOS.Core.Voice;

/// <summary>
/// Decides whether a sentence contains her name, and returns what was said after it.
///
/// The user asked for greetings to be natural ("hey Iris", "wsp Iris", "yo Iris,
/// you there?") rather than one hard-coded trigger. So the matcher does three things:
///
///   1. compares every word against her name with a one-letter tolerance, which
///      absorbs speech-to-text slips like "irys" or "iris's";
///   2. accepts the name anywhere in the first few words, because people rarely
///      start with the name itself;
///   3. treats a bare name as a complete wake ("Iris?"), and everything after the
///      name as the request.
///
/// The greeting words themselves are data in the persona file, so a new greeting
/// never requires a code change.
/// </summary>
public sealed class WakePhraseMatcher
{
    private string _wakeName;
    private HashSet<string> _greetingWords;
    private List<string> _wakeAliases;

    public WakePhraseMatcher(Persona persona)
    {
        _wakeName = Normalize(persona.WakeName.Length > 0 ? persona.WakeName : persona.Name);
        _greetingWords = persona.GreetingWords
            .Select(Normalize)
            .Where(word => word.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        _wakeAliases = NormalizePhrases(persona.WakeAliases);
    }

    /// <summary>
    /// Re-reads her name, greetings and aliases. Called when the persona changes at
    /// runtime, so a renamed assistant starts answering to the new name immediately.
    /// </summary>
    public void Refresh(Persona persona)
    {
        _wakeName = Normalize(persona.WakeName.Length > 0 ? persona.WakeName : persona.Name);
        _greetingWords = persona.GreetingWords
            .Select(Normalize)
            .Where(word => word.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        _wakeAliases = NormalizePhrases(persona.WakeAliases);
    }

    /// <summary>The name she listens for, lower case.</summary>
    public string WakeName => _wakeName;

    /// <summary>The trained extra spellings of her name, lower case.</summary>
    public IReadOnlyList<string> WakeAliases => _wakeAliases;

    private static List<string> NormalizePhrases(IEnumerable<string?> phrases) => phrases
        .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
        .Select(phrase => string.Join(' ', phrase!.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(word => word.Length > 0)))
        .Where(phrase => phrase.Length > 0 && phrase.Length >= 3)
        .Distinct(StringComparer.Ordinal)
        .Take(24)
        .ToList();

    /// <summary>
    /// Returns true when the transcript addresses her.
    /// <paramref name="remainder"/> receives the request without the greeting and name;
    /// it is empty when the user only called her.
    /// </summary>
    public bool TryMatch(string? transcript, out string remainder)
    {
        remainder = string.Empty;
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        List<string> words = new();
        List<string> spokenWords = new();
        foreach (string rawWord in transcript.Split(' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalised = Normalize(rawWord);
            if (normalised.Length == 0)
            {
                continue;
            }

            words.Add(normalised);
            spokenWords.Add(rawWord);
        }

        if (words.Count == 0)
        {
            // An empty transcript never wakes her.
            return false;
        }

        // Look at the first handful of words only; her name in the middle of a long
        // sentence is usually the user talking about her, not to her.
        int searchWindow = Math.Min(words.Count, 4);

        for (int index = 0; index < searchWindow; index++)
        {
            // First the name, then one-word and two-word trained aliases: engines hear
            // "Iris" as anything from "I race" to "on this", and some of those
            // mishearings span two words. Everything after the matched phrase is the
            // request, and a bare call ("Iris?") is a complete wake.
            if (IsNameMatch(words[index]))
            {
                remainder = JoinSpoken(spokenWords, index + 1);
                if (remainder.Length > 0 && IsGreetingOnly(remainder))
                {
                    remainder = string.Empty;
                }

                return true;
            }

            for (int span = 1; span <= 2; span++)
            {
                if (span == 2 && index + 1 >= words.Count)
                {
                    break;
                }

                string phrase = span == 1 ? words[index] : $"{words[index]} {words[index + 1]}";
                if (!IsWakePhraseMatch(phrase))
                {
                    continue;
                }

                remainder = JoinSpoken(spokenWords, index + span);
                if (remainder.Length > 0 && IsGreetingOnly(remainder))
                {
                    remainder = string.Empty;
                }

                return true;
            }
        }

        // No name found: she stays quiet. A greeting alone is not enough, because
        // people say "hey" to each other all day long.
        return false;
    }

    private static string JoinSpoken(IReadOnlyList<string> spokenWords, int fromIndex) =>
        string.Join(" ", spokenWords.Skip(fromIndex)).Trim();

    /// <summary>True when the (possibly two-word) phrase is her name or a trained alias.</summary>
    private bool IsWakePhraseMatch(string phrase)
    {
        if (IsNameMatch(phrase))
        {
            return true;
        }

        foreach (string alias in _wakeAliases)
        {
            if (phrase == alias)
            {
                return true;
            }

            int distance = LevenshteinDistance(phrase, alias);
            if (distance <= (alias.Length >= 8 ? 2 : 1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the text is only a greeting, e.g. "hey there".</summary>
    public bool IsGreetingOnly(string text)
    {
        List<string> words = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(word => word.Length > 0)
            .ToList();

        return words.Count > 0 && words.All(word => _greetingWords.Contains(word));
    }

    /// <summary>Compares one word against her name, tolerating a single letter slip.</summary>
    public bool IsNameMatch(string word)
    {
        if (word == _wakeName)
        {
            return true;
        }

        // "iris's", "irises" and similar belong to her too.
        if (word.StartsWith(_wakeName, StringComparison.Ordinal) && word.Length <= _wakeName.Length + 2)
        {
            return true;
        }

        return LevenshteinDistance(word, _wakeName) <= 1;
    }

    /// <summary>Classic edit distance, used only on short single words.</summary>
    public static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        if (Math.Abs(left.Length - right.Length) > 2)
        {
            return 99; // clearly different; skip the table
        }

        int[] previousRow = new int[right.Length + 1];
        int[] currentRow = new int[right.Length + 1];

        for (int column = 0; column <= right.Length; column++)
        {
            previousRow[column] = column;
        }

        for (int row = 1; row <= left.Length; row++)
        {
            currentRow[0] = row;

            for (int column = 1; column <= right.Length; column++)
            {
                int substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                currentRow[column] = Math.Min(
                    Math.Min(currentRow[column - 1] + 1, previousRow[column] + 1),
                    previousRow[column - 1] + substitutionCost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[right.Length];
    }

    /// <summary>Lower case, letters and digits only.</summary>
    private static string Normalize(string? word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return string.Empty;
        }

        StringBuilder builder = new(word.Length);
        foreach (char character in word.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character == '\'')
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim('\'');
    }
}
