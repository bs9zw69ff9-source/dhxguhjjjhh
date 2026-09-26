using PavlovBot.Host.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The rule the whole feature turns on: a deny must go IN FRONT of the port allow rules, or
/// ufw's first-match evaluation lets the "blocked" address straight through the game port.
/// </summary>
public class FirewallArgvTests
{
    [Fact]
    public void AsRootUfwIsCalledDirectly()
    {
        var (file, argv) = UfwFirewall.Invocation(["status", "numbered"], privileged: true);

        Assert.Equal("ufw", file);
        Assert.Equal(["status", "numbered"], argv);
    }

    [Fact]
    public void UnprivilegedUfwGoesThroughNonInteractiveSudo()
    {
        // The bot is meant to run unprivileged; -n makes a missing grant fail instead of hang.
        var (file, argv) = UfwFirewall.Invocation(["insert", "1", "deny", "from", "1.2.3.4"], privileged: false);

        Assert.Equal("sudo", file);
        Assert.Equal(["-n", "ufw", "insert", "1", "deny", "from", "1.2.3.4"], argv);
    }

    [Fact]
    public void TheSudoAdviceNamesOnlyUfw()
    {
        Assert.Contains("pavlov ALL=(root) NOPASSWD: /usr/sbin/ufw", UfwFirewall.SudoersAdvice("pavlov"), StringComparison.Ordinal);
    }

    [Fact]
    public void ADenyIsInsertedAtRuleOne_NotAppended()
    {
        // insert 1 puts the deny ahead of `allow <gameport>`; a bare `deny from` lands after
        // it and never fires. This is the bug that let a blocked address keep connecting.
        Assert.Equal(["insert", "1", "deny", "from", "1.2.3.4"], UfwFirewall.InsertDenyArgv("1.2.3.4"));
    }

    [Fact]
    public void TheEmptyRulesetFallbackAppends()
    {
        // Only reached when `insert 1` fails because there are no rules at all - nothing to
        // sit in front of, so a plain deny is equivalent.
        Assert.Equal(["deny", "from", "1.2.3.4"], UfwFirewall.AppendDenyArgv("1.2.3.4"));
    }

    [Fact]
    public void ADeleteMatchesByRuleSpec_NotPosition()
    {
        // Delete by specification removes the rule wherever it sits, so unblocking works
        // regardless of where the deny ended up.
        Assert.Equal(["delete", "deny", "from", "1.2.3.4"], UfwFirewall.DeleteDenyArgv("1.2.3.4"));
    }
}
