namespace CompanionOS.Core.Brain;

/// <summary>
/// "Done" means read back, not written.
///
/// A tool returning success only proves the call returned — not that the world
/// changed. That gap is where a confident agent invents outcomes: the playlist write
/// returned, so it says the song is in the playlist, and nobody ever looked. This
/// ledger closes it structurally instead of asking a 4B model to be careful.
///
/// Two ways a mutation becomes verified:
///   1. the module verified its own write and said so (ToolResult.SelfVerified),
///      which is what a read-after-write inside the module means;
///   2. a later READ tool from the same module succeeded in the same turn, which is
///      the agent checking its own work.
///
/// Anything left unverified is reported as unverified. The OS never rewrites her
/// sentence to say something happened; it adds the one fact she could not know —
/// that the change was not confirmed.
/// </summary>
public sealed class MutationVerificationLedger
{
    private readonly List<Mutation> _mutations = new();

    /// <summary>Records a successful mutating call awaiting confirmation.</summary>
    public void RecordMutation(string toolName, bool selfVerified)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return;
        }

        _mutations.Add(new Mutation(toolName.Trim(), ModuleOf(toolName), selfVerified));
    }

    /// <summary>
    /// Records a successful read. It confirms earlier mutations from the same module
    /// only — reading the browser proves nothing about a playlist write.
    /// </summary>
    public void RecordVerificationRead(string toolName)
    {
        string module = ModuleOf(toolName);
        for (int index = 0; index < _mutations.Count; index++)
        {
            if (_mutations[index].Module.Equals(module, StringComparison.OrdinalIgnoreCase))
            {
                _mutations[index] = _mutations[index] with { Verified = true };
            }
        }
    }

    /// <summary>Tool names whose effect was never confirmed this turn.</summary>
    public IReadOnlyList<string> UnverifiedTools => _mutations
        .Where(mutation => !mutation.Verified)
        .Select(mutation => mutation.ToolName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool HasUnverifiedMutations => UnverifiedTools.Count > 0;

    /// <summary>
    /// One short, factual clause for the receipts and the turn report. Empty when
    /// every change was confirmed, so a clean turn stays clean.
    /// </summary>
    public string DescribeUnverified()
    {
        IReadOnlyList<string> unverified = UnverifiedTools;
        if (unverified.Count == 0)
        {
            return string.Empty;
        }

        string names = string.Join(", ", unverified.Take(3).Select(FriendlyName));
        return unverified.Count == 1
            ? $"Heads up: \"{names}\" went through, but I could not read the result back to confirm it."
            : $"Heads up: {names} went through, but I could not read the results back to confirm them.";
    }

    /// <summary>
    /// Internal tool identifiers are protocol, not conversation. A user once read
    /// "plugin_telegram_personal_telegram_send_messag_1a7d79a2ed" in chat (2026-10-31
    /// screenshot) — strip the call-id suffix and plugin prefix, and speak in words.
    /// </summary>
    internal static string FriendlyName(string toolName)
    {
        string name = toolName.Trim();

        int lastUnderscore = name.LastIndexOf('_');
        if (lastUnderscore > 0 && name.Length - lastUnderscore - 1 >= 6 &&
            name[(lastUnderscore + 1)..].All(Uri.IsHexDigit))
        {
            name = name[..lastUnderscore];
        }

        const string pluginPrefix = "plugin_";
        if (name.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[pluginPrefix.Length..];
        }

        return name.Replace('_', ' ');
    }

    private static string ModuleOf(string toolName)
    {
        string name = toolName.Trim();
        int separator = name.IndexOf('_');
        return separator > 0 ? name[..separator] : name;
    }

    private readonly record struct Mutation(string ToolName, string Module, bool Verified);
}
