using System.Text;
using System.Text.RegularExpressions;

namespace CompanionOS.Core.Voice;

/// <summary>
/// Cleans her written answer so it sounds right when spoken.
///
/// Two jobs:
///   * strip control tags such as [mood:happy] that steer her face but must never
///     be read aloud;
///   * remove the parts of Markdown that turn into noise in a voice (backticks,
///     asterisks, bullet dashes, links), because a small model loves to use them.
/// </summary>
public static partial class SpeechTextSanitizer
{
    /// <summary>Removes control tags and Markdown, keeping the actual words.</summary>
    public static string CleanForSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string cleaned = ControlTagPattern().Replace(text, string.Empty);
        cleaned = MarkdownLinkPattern().Replace(cleaned, "$1");
        cleaned = CodeFencePattern().Replace(cleaned, string.Empty);
        cleaned = InlineCodePattern().Replace(cleaned, "$1");
        cleaned = EmphasisPattern().Replace(cleaned, "$1");
        cleaned = LeadingBulletPattern().Replace(cleaned, string.Empty);
        cleaned = WhitespacePattern().Replace(cleaned, " ");
        return cleaned.Trim();
    }

    /// <summary>Removes control tags only, keeping formatting for the screen.</summary>
    public static string StripControlTags(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : ControlTagPattern().Replace(text, string.Empty).Trim();

    /// <summary>Reads the first control tag of a kind, e.g. the mood of a reply.</summary>
    public static string? ReadControlTag(string? text, string tagName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = Regex.Match(text, $@"\[\s*{Regex.Escape(tagName)}\s*:\s*([a-zA-Z\-]+)\s*\]");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// One strip, at the brain: control tags (<c>[mood:calm]</c>, spaced or not) and
    /// bracketed KNOWN tool names are machine channels, not words. A small model
    /// imitates bracket shapes it finds in its own context and prints them as prose
    /// ("[ list_music ]"); no hand runs, the junk lands on every screen and the
    /// receipt check then demands honest recovery in a second generation round.
    /// Stripping the channels WE sanctioned at one site — before chat, history and
    /// voice see the text — keeps every surface on the same clean words. This is
    /// hygiene for our own protocols, never a judgment of what she said: only an
    /// EXACT known tool name inside plain brackets is removed; any other bracket
    /// content is left exactly as she wrote it.
    /// </summary>
    public static string StripProtocolNoise(string? text, IEnumerable<string> knownToolNames)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string cleaned = ControlTagPattern().Replace(text, string.Empty);

        string[] known = knownToolNames as string[] ?? knownToolNames.ToArray();
        if (known.Length > 0)
        {
            cleaned = BracketedToolNamePattern().Replace(cleaned, match =>
                known.Any(name => name.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                    ? string.Empty
                    : match.Value);
        }

        cleaned = WhitespacePattern().Replace(cleaned, " ");
        return cleaned.Trim();
    }

    /// <summary>
    /// Splits text into speakable sentences. Short fragments are merged so she does
    /// not produce a clipped, robotic rhythm, and very long sentences are cut so the
    /// first audio can start before the whole answer is finished.
    /// </summary>
    public static IReadOnlyList<string> SplitIntoSentences(string? text, int minimumCharacters = 12)
    {
        string cleaned = CleanForSpeech(text);
        if (cleaned.Length == 0)
        {
            return Array.Empty<string>();
        }

        List<string> sentences = new();
        StringBuilder current = new();

        foreach (char character in cleaned)
        {
            current.Append(character);
            bool isBoundary = character is '.' or '!' or '?' or '…' or '\n' or ';' or ':';

            if (!isBoundary)
            {
                continue;
            }

            string candidate = current.ToString().Trim();
            current.Clear();

            if (candidate.Length == 0)
            {
                continue;
            }

            if (candidate.Length < minimumCharacters && sentences.Count > 0)
            {
                sentences[^1] = sentences[^1] + " " + candidate;
            }
            else
            {
                sentences.Add(candidate);
            }
        }

        string tail = current.ToString().Trim();
        if (tail.Length > 0)
        {
            if (sentences.Count > 0 && tail.Length < minimumCharacters)
            {
                sentences[^1] = sentences[^1] + " " + tail;
            }
            else
            {
                sentences.Add(tail);
            }
        }

        return sentences;
    }

    [GeneratedRegex(@"\[\s*[a-zA-Z_\-]+\s*:\s*[^\]]*\]")]
    private static partial Regex ControlTagPattern();

    [GeneratedRegex(@"\[\s*([a-zA-Z0-9_\-]+)\s*\]")]
    private static partial Regex BracketedToolNamePattern();

    [GeneratedRegex(@"\[([^\]]+)\]\([^\)]+\)")]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"```[a-zA-Z]*")]
    private static partial Regex CodeFencePattern();

    [GeneratedRegex(@"`([^`]*)`")]
    private static partial Regex InlineCodePattern();

    [GeneratedRegex(@"\*{1,3}([^\*]+)\*{1,3}")]
    private static partial Regex EmphasisPattern();

    [GeneratedRegex(@"(?m)^\s*[-•*]\s+")]
    private static partial Regex LeadingBulletPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespacePattern();
}
