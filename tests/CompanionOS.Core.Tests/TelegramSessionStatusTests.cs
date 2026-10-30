using CompanionOS.Core.Modules;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// The Telegram card's truth table. The fixed failure: a session.dpapi file on disk
/// was presented as "Telegram account linked" even when Telegram had revoked the
/// session — a local file is not proof of current authorization.
/// </summary>
public sealed class TelegramSessionStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoSessionFileMeansNotLinked()
    {
        Assert.Equal(TelegramLinkState.NotLinked,
            TelegramSessionStatus.Resolve(false, null, null, Now));
    }

    [Fact]
    public void AFileAloneIsNeverReportedAsConnected()
    {
        TelegramLinkState state = TelegramSessionStatus.Resolve(true, null, null, Now);
        Assert.Equal(TelegramLinkState.SessionFilePresent, state);

        string card = TelegramSessionStatus.DescribeForCard(state);
        Assert.DoesNotContain("linked", card, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connected", card, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not verified", card, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFreshSuccessfulCheckIsConnected()
    {
        Assert.Equal(TelegramLinkState.VerifiedConnected,
            TelegramSessionStatus.Resolve(true, Now.AddMinutes(-2), true, Now));
    }

    [Fact]
    public void AFreshFailedCheckIsSurfacedAsRevokedOrUnreachable()
    {
        TelegramLinkState state = TelegramSessionStatus.Resolve(true, Now.AddMinutes(-1), false, Now);
        Assert.Equal(TelegramLinkState.VerificationFailed, state);
        Assert.Contains("revoked", TelegramSessionStatus.DescribeForCard(state, "network timeout"));
        Assert.Contains("network timeout", TelegramSessionStatus.DescribeForCard(state, "network timeout"));
    }

    [Fact]
    public void AStaleSuccessfulCheckFallsBackToUnverifiedInsteadOfStaleConnected()
    {
        // 2 hours old news must never read as "connected" on the card.
        Assert.Equal(TelegramLinkState.SessionFilePresent,
            TelegramSessionStatus.Resolve(true, Now.AddHours(-2), true, Now));
    }

    [Theory]
    [InlineData(TelegramLinkState.NotLinked, "Connect account")]
    [InlineData(TelegramLinkState.SessionFilePresent, "Manage account")]
    [InlineData(TelegramLinkState.VerifiedConnected, "Manage account")]
    [InlineData(TelegramLinkState.VerificationFailed, "Reconnect account")]
    public void ButtonLabelsMatchTheState(TelegramLinkState state, string expected)
    {
        Assert.Equal(expected, TelegramSessionStatus.ButtonLabel(state));
    }

    [Fact]
    public void EveryStateProducesANonEmptyActionableLine()
    {
        foreach (TelegramLinkState state in Enum.GetValues<TelegramLinkState>())
        {
            Assert.False(string.IsNullOrWhiteSpace(TelegramSessionStatus.DescribeForCard(state)));
        }
    }
}
