namespace CompanionOS.Core.Memory;

/// <summary>
/// The four layers of her memory. She is the same person in all of them;
/// the layer only says what kind of knowledge a node holds.
/// </summary>
public enum MemoryLayer
{
    /// <summary>Who she is: persona, voice, boundaries, the relationship.</summary>
    Core = 0,

    /// <summary>Things that happened, with a time.</summary>
    Episodic = 1,

    /// <summary>Facts about the world and about the user.</summary>
    Semantic = 2,

    /// <summary>Rules and how-to knowledge: "when X, do Y".</summary>
    Procedural = 3
}

/// <summary>
/// The direction and the emotional charge of a connection.
/// Two nodes can therefore be linked *and* in conflict at the same time,
/// which is exactly how a person holds "fire cooks food" and "fire burns".
/// </summary>
public enum SynapsePolarity
{
    /// <summary>Abstraction upward: dagger IS_A knife.</summary>
    Inward,

    /// <summary>Capability outward: flame USED_FOR cooking.</summary>
    Outward,

    /// <summary>Danger or contradiction: knife CANNOT_BE a toy.</summary>
    Inhibitory,

    /// <summary>Preference: the user DISLIKES loud bass.</summary>
    Affective,

    /// <summary>Ordering in time: wind down BEFORE sleep.</summary>
    Temporal
}

/// <summary>Every legal relationship between two memories.</summary>
public enum SynapseType
{
    IsA,
    BelongsTo,
    UsedFor,
    Causes,
    CombinesWith,
    ConflictsWith,
    DangerIf,
    CannotBe,
    Likes,
    Dislikes,
    Prefers,
    Before,
    After,
    During,
    RelatedTo,
    Supersedes
}

/// <summary>Helpers that keep the polarity rules in one place.</summary>
public static class SynapseTypes
{
    /// <summary>Which polarity a relationship belongs to.</summary>
    public static SynapsePolarity PolarityOf(SynapseType type) => type switch
    {
        SynapseType.IsA or SynapseType.BelongsTo => SynapsePolarity.Inward,
        SynapseType.UsedFor or SynapseType.Causes or SynapseType.CombinesWith or SynapseType.RelatedTo =>
            SynapsePolarity.Outward,
        SynapseType.ConflictsWith or SynapseType.DangerIf or SynapseType.CannotBe or SynapseType.Supersedes => SynapsePolarity.Inhibitory,
        SynapseType.Likes or SynapseType.Dislikes or SynapseType.Prefers => SynapsePolarity.Affective,
        _ => SynapsePolarity.Temporal
    };

    /// <summary>
    /// Reads a relationship written by a model ("is_a", "is a", "dislikes", "used for").
    /// Unknown words become RelatedTo rather than throwing, because a 4B model will
    /// occasionally invent a verb and that should not lose the memory.
    /// </summary>
    public static SynapseType Parse(string? writtenRelation)
    {
        string normalised = (writtenRelation ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace(" ", "_")
            .Replace("-", "_");

        return normalised switch
        {
            "is_a" or "isa" or "is" or "kind_of" => SynapseType.IsA,
            "belongs_to" or "part_of" or "belongs" => SynapseType.BelongsTo,
            "used_for" or "used_to" or "for" or "purpose" => SynapseType.UsedFor,
            "causes" or "makes" or "leads_to" => SynapseType.Causes,
            "combines_with" or "goes_with" or "pairs_with" => SynapseType.CombinesWith,
            "conflicts_with" or "contradicts" or "clashes_with" => SynapseType.ConflictsWith,
            "danger_if" or "dangerous_if" or "unsafe_if" or "harmful_if" => SynapseType.DangerIf,
            "cannot_be" or "must_not_be" or "is_not" => SynapseType.CannotBe,
            "likes" or "loves" or "enjoys" => SynapseType.Likes,
            "dislikes" or "hates" or "avoids" => SynapseType.Dislikes,
            "prefers" or "favourite" or "favorite" => SynapseType.Prefers,
            "before" => SynapseType.Before,
            "after" => SynapseType.After,
            "during" => SynapseType.During,
            "supersedes" or "replaces" => SynapseType.Supersedes,
            _ => SynapseType.RelatedTo
        };
    }

    /// <summary>Spoken form used when memory is translated back into plain English.</summary>
    public static string ToWords(SynapseType type) => type switch
    {
        SynapseType.IsA => "is a",
        SynapseType.BelongsTo => "belongs to",
        SynapseType.UsedFor => "is used for",
        SynapseType.Causes => "causes",
        SynapseType.CombinesWith => "goes with",
        SynapseType.ConflictsWith => "conflicts with",
        SynapseType.DangerIf => "is dangerous if",
        SynapseType.CannotBe => "must never be",
        SynapseType.Likes => "likes",
        SynapseType.Dislikes => "dislikes",
        SynapseType.Prefers => "prefers",
        SynapseType.Before => "comes before",
        SynapseType.After => "comes after",
        SynapseType.During => "happens during",
        SynapseType.Supersedes => "supersedes",
        _ => "relates to"
    };
}

/// <summary>One memory. A person, a fact, a rule, a moment.</summary>
public sealed class MemoryNode
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public required string NormalizedTitle { get; set; }
    public string Content { get; set; } = string.Empty;
    public MemoryLayer Layer { get; set; }
    public double Strength { get; set; } = 0.75;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; set; }
    public int UseCount { get; set; }
    public float[] Vector { get; set; } = new float[TextVectorizer.Dimensions];
    public string Provenance { get; set; } = string.Empty;

    /// <summary>Pinned nodes (her core self) never fade and are never merged away.</summary>
    public bool Pinned { get; set; }

    /// <summary>Fresh nodes glow in the graph view.</summary>
    public bool IsRecent => LastUsedAt.HasValue && LastUsedAt.Value > DateTimeOffset.Now.AddMinutes(-5);
}

/// <summary>One connection between two memories, with a strength that fades.</summary>
public sealed class MemorySynapse
{
    public required string Id { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public required SynapseType Type { get; init; }
    public double Strength { get; set; } = 0.6;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; set; }
    public int UseCount { get; set; }
    public string Provenance { get; set; } = string.Empty;

    public SynapsePolarity Polarity => SynapseTypes.PolarityOf(Type);
}

/// <summary>Result of one spreading-activation pass, before it becomes prompt text.</summary>
public sealed class ActivationResult
{
    public IReadOnlyList<(MemoryNode Node, double Score)> Activated { get; init; } =
        Array.Empty<(MemoryNode, double)>();

    public IReadOnlyList<MemorySynapse> ActivatedSynapses { get; init; } = Array.Empty<MemorySynapse>();

    public IReadOnlyList<MemoryNode> Seeds { get; init; } = Array.Empty<MemoryNode>();

    /// <summary>The strongest known parent edge used to activate each non-seed node.</summary>
    public IReadOnlyDictionary<string, MemoryActivationPath> ActivationPaths { get; init; } =
        new Dictionary<string, MemoryActivationPath>(StringComparer.Ordinal);

    /// <summary>True when nothing was recalled, so the prompt can simply omit the block.</summary>
    public bool IsEmpty => Activated.Count == 0;
}

/// <summary>One edge in the strongest spread-activation explanation for a memory.</summary>
public sealed record MemoryActivationPath(string ParentNodeId, string SynapseId);
