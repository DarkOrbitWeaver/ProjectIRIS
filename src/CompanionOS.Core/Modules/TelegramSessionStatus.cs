namespace CompanionOS.Core.Modules;

/// <summary>What the UI may truthfully claim about the Telegram account.</summary>
public enum TelegramLinkState
{
    /// <summary>No encrypted session file exists; the account was never linked here.</summary>
    NotLinked,

    /// <summary>A session file exists, but nothing has verified it this session.
    /// A file on disk is NOT proof of current authorization — Telegram may have
    /// revoked the session from another device.</summary>
    SessionFilePresent,

    /// <summary>A live status check succeeded recently: authorized and reachable.</summary>
    VerifiedConnected,

    /// <summary>A live status check failed: the session is revoked, expired, or the
    /// network/module is down. The detail says which, when known.</summary>
    VerificationFailed
}

/// <summary>
/// The one honest mapping from raw facts to the Modules card text. It exists as a
/// pure function because the old card computed "linked" from File.Exists alone and
/// told the user "Telegram account linked" for sessions Telegram had long revoked
/// (2026-10 journals). The UI supplies the facts; this decides the words.
/// </summary>
public static class TelegramSessionStatus
{
    /// <summary>How long a successful live check is trusted before it counts as stale.</summary>
    public static readonly TimeSpan VerificationTrustWindow = TimeSpan.FromMinutes(10);

    public static TelegramLinkState Resolve(bool sessionFileExists, DateTimeOffset? lastVerifiedAt,
        bool? lastVerificationSucceeded, DateTimeOffset now)
    {
        if (!sessionFileExists)
        {
            return TelegramLinkState.NotLinked;
        }

        if (lastVerifiedAt is null || now - lastVerifiedAt.Value > VerificationTrustWindow)
        {
            return TelegramLinkState.SessionFilePresent; // Never promote stale news to "connected".
        }

        return lastVerificationSucceeded == true
            ? TelegramLinkState.VerifiedConnected
            : TelegramLinkState.VerificationFailed;
    }

    /// <summary>The card's status line: truthful, and always with the next action.</summary>
    public static string DescribeForCard(TelegramLinkState state, string? failureDetail = null) => state switch
    {
        TelegramLinkState.NotLinked =>
            "Telegram account not linked. Connect to scan the login QR.",
        TelegramLinkState.SessionFilePresent =>
            "Encrypted session file present — not verified this session. Ask Iris to \"check Telegram status\" to confirm it still works.",
        TelegramLinkState.VerifiedConnected =>
            "Telegram verified and connected.",
        TelegramLinkState.VerificationFailed =>
            "Telegram session check failed" +
            (string.IsNullOrWhiteSpace(failureDetail) ? "" : $" ({failureDetail})") +
            ". The session may be revoked — reconnect to scan a new QR.",
        _ => "Telegram status unknown."
    };

    /// <summary>The card's button label for each state.</summary>
    public static string ButtonLabel(TelegramLinkState state) => state switch
    {
        TelegramLinkState.NotLinked => "Connect account",
        TelegramLinkState.VerificationFailed => "Reconnect account",
        _ => "Manage account"
    };
}
