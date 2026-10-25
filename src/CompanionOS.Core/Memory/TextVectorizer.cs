using System.Globalization;
using System.Text;

namespace CompanionOS.Core.Memory;

/// <summary>
/// Turns text into a fixed-size numeric fingerprint so memories, modules and
/// tools can be compared by meaning-ish similarity without downloading an
/// embedding model.
///
/// How it works, in plain words: every word (and every pair of neighbouring words)
/// is hashed into one of 384 slots, with a stable sign, then the vector is scaled
/// to length 1. Two texts that share words end up pointing in similar directions,
/// so their cosine similarity is high. This is the classic "hashing trick": cheap,
/// deterministic, zero downloads, and good enough for short requests like
/// "play something calm" versus "find me a video".
///
/// If you later want true semantic embeddings (a small ONNX model), replace only
/// <see cref="Embed"/> — the rest of the memory engine is agnostic.
/// </summary>
public static class TextVectorizer
{
    /// <summary>Fingerprint length. 384 matches the usual small embedding models.</summary>
    public const int Dimensions = 384;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "but", "of", "to", "in", "on", "for", "with",
        "is", "are", "was", "were", "be", "been", "it", "this", "that", "these", "those",
        "i", "you", "me", "my", "your", "we", "us", "he", "she", "they", "them",
        "do", "does", "did", "can", "could", "would", "should", "will", "just", "please"
    };

    /// <summary>Creates the unit-length fingerprint of a piece of text.</summary>
    public static float[] Embed(string? text)
    {
        float[] vector = new float[Dimensions];
        if (string.IsNullOrWhiteSpace(text))
        {
            return vector;
        }

        IReadOnlyList<string> tokens = Tokenize(text);
        for (int index = 0; index < tokens.Count; index++)
        {
            AddToken(vector, tokens[index], 1.0f);

            // Word pairs capture short phrases such as "night drive" or "play music".
            if (index + 1 < tokens.Count)
            {
                AddToken(vector, tokens[index] + "_" + tokens[index + 1], 0.6f);
            }
        }

        Normalize(vector);
        return vector;
    }

    /// <summary>Splits text into lower-case words, dropping punctuation and filler words.</summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        List<string> tokens = new();
        StringBuilder currentWord = new();

        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character) || character == '\'')
            {
                currentWord.Append(char.ToLowerInvariant(character));
                continue;
            }

            FlushWord(currentWord, tokens);
        }

        FlushWord(currentWord, tokens);
        return tokens;
    }

    /// <summary>Cosine similarity of two fingerprints, from -1 to 1.</summary>
    public static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length)
        {
            return 0;
        }

        double dot = 0;
        double leftLength = 0;
        double rightLength = 0;

        for (int index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftLength += left[index] * left[index];
            rightLength += right[index] * right[index];
        }

        if (leftLength <= double.Epsilon || rightLength <= double.Epsilon)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftLength) * Math.Sqrt(rightLength));
    }

    /// <summary>Converts a float vector to bytes for storage in SQLite.</summary>
    public static byte[] ToBytes(float[] vector)
    {
        byte[] bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Reads a vector back from SQLite. Tolerates a damaged or empty blob.</summary>
    public static float[] FromBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < sizeof(float))
        {
            return new float[Dimensions];
        }

        float[] vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }

    /// <summary>Case and spacing insensitive form used to recognise the same concept twice.</summary>
    public static string NormalizeForComparison(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new(text.Length);
        bool previousWasSpace = false;

        foreach (char character in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>Human-friendly display form: first letter capitalised, underscores removed.</summary>
    public static string ToDisplayTitle(string? text)
    {
        string cleaned = (text ?? string.Empty).Replace('_', ' ').Trim();
        if (cleaned.Length == 0)
        {
            return "Untitled";
        }

        return char.ToUpper(cleaned[0], CultureInfo.InvariantCulture) + cleaned[1..];
    }

    private static void FlushWord(StringBuilder currentWord, List<string> tokens)
    {
        if (currentWord.Length == 0)
        {
            return;
        }

        string word = currentWord.ToString();
        currentWord.Clear();

        if (word.Length < 2 || StopWords.Contains(word))
        {
            return;
        }

        tokens.Add(word);
    }

    private static void AddToken(float[] vector, string token, float weight)
    {
        uint hash = Fnv1a(token);
        int bucket = (int)(hash % Dimensions);
        float sign = ((hash >> 16) & 1) == 0 ? 1f : -1f;
        vector[bucket] += sign * weight;
    }

    private static uint Fnv1a(string token)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        uint hash = offsetBasis;
        foreach (char character in token)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }

    private static void Normalize(float[] vector)
    {
        double length = 0;
        foreach (float value in vector)
        {
            length += value * value;
        }

        if (length <= double.Epsilon)
        {
            return;
        }

        float scale = (float)(1.0 / Math.Sqrt(length));
        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] *= scale;
        }
    }
}
