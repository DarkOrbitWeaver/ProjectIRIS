using CompanionOS.Core.Brain;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// A tool returning success proves the call returned, not that the world changed.
/// These pin the difference between "written" and "verified", which is where a
/// confident agent invents an outcome nobody checked.
/// </summary>
public sealed class MutationVerificationLedgerTests
{
    [Fact]
    public void AWriteWithNoReadBackIsReportedAsUnconfirmed()
    {
        MutationVerificationLedger ledger = new();
        ledger.RecordMutation("music_playlists", selfVerified: false);

        Assert.True(ledger.HasUnverifiedMutations);
        Assert.Contains("music_playlists", ledger.UnverifiedTools);
        Assert.Contains("not read back", ledger.DescribeUnverified(), StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleThatReReadsItsOwnWriteNeedsNothingElse()
    {
        MutationVerificationLedger ledger = new();
        ledger.RecordMutation("music_playlists", selfVerified: true);

        Assert.False(ledger.HasUnverifiedMutations);
        Assert.Equal(string.Empty, ledger.DescribeUnverified());
    }

    [Fact]
    public void ALaterReadFromTheSameModuleConfirmsTheWrite()
    {
        MutationVerificationLedger ledger = new();
        ledger.RecordMutation("music_playlists", selfVerified: false);
        ledger.RecordVerificationRead("music_library");

        Assert.False(ledger.HasUnverifiedMutations);
    }

    [Fact]
    public void AReadFromADifferentModuleProvesNothing()
    {
        MutationVerificationLedger ledger = new();
        ledger.RecordMutation("telegram_send_message", selfVerified: false);
        ledger.RecordVerificationRead("browser_tabs");

        Assert.True(ledger.HasUnverifiedMutations);
        Assert.Contains("telegram_send_message", ledger.UnverifiedTools);
    }

    [Fact]
    public void ATurnWithNoWritesSaysNothingAtAll()
    {
        MutationVerificationLedger ledger = new();
        ledger.RecordVerificationRead("music_library");

        Assert.False(ledger.HasUnverifiedMutations);
        Assert.Equal(string.Empty, ledger.DescribeUnverified());
    }

    [Fact]
    public void SeveralUnverifiedWritesAreNamedTogetherInOneClause()
    {
        MutationVerificationLedger ledger = new();
        ledger.RecordMutation("music_playlists", false);
        ledger.RecordMutation("telegram_send_message", false);

        string described = ledger.DescribeUnverified();
        Assert.Contains("music_playlists", described, StringComparison.Ordinal);
        Assert.Contains("telegram_send_message", described, StringComparison.Ordinal);
        Assert.Contains("were not read back", described, StringComparison.Ordinal);
    }
}
