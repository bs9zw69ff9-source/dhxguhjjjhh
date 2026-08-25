using Microsoft.Extensions.Configuration;
using PavlovBot.Host.Configuration;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The .env parser must agree with dotenv 16 on the same file. A divergence here does not
/// look like a parsing bug - it looks like a wrong password, a truncated webhook URL, or a
/// token with a stray quote in it, and it costs an hour to trace back to this file.
/// </summary>
public class DotEnvParsingTests
{
    private static string? Parse(string line) => DotEnvConfigurationProvider.Parse(line).GetValueOrDefault("KEY");

    [Fact]
    public void PlainAssignmentsBind()
    {
        Assert.Equal("value", Parse("KEY=value"));
        Assert.Equal("value", Parse("  KEY = value  "));
        Assert.Equal("value", Parse("export KEY=value"));
        Assert.Equal("value", Parse("KEY: value"));
    }

    [Fact]
    public void QuotesAreStripped()
    {
        Assert.Equal("value", Parse("KEY=\"value\""));
        Assert.Equal("value", Parse("KEY='value'"));
        Assert.Equal("value", Parse("KEY=`value`"));
    }

    [Fact]
    public void EscapesExpandInsideDoubleQuotesOnly()
    {
        // dotenv's rule, not ours. A single-quoted value is literal.
        Assert.Equal("a\nb", Parse("KEY=\"a\\nb\""));
        Assert.Equal("a\\nb", Parse("KEY='a\\nb'"));
    }

    [Fact]
    public void OnlyNewlineAndCarriageReturnAreEscapes()
    {
        /* dotenv expands \n and \r and NOTHING else - a backslash-quote stays literal.
           This port unescaped \" until the differential harness caught it, which would
           have silently changed the value of any token containing one. */
        Assert.Equal("escaped \\\" quote", Parse("KEY=\"escaped \\\" quote\""));
        Assert.Equal("a\\tb", Parse("KEY=\"a\\tb\""));
    }

    [Fact]
    public void AnUnquotedValueEndsAtTheFirstHash()
    {
        /* This is dotenv's inline-comment rule, and it is a trap worth having a test for:
           a password containing a hash MUST be quoted or it is silently truncated. The
           test exists to pin the behaviour, not to endorse it - diverging would make the
           same file mean different things to the two bots. */
        Assert.Equal("value", Parse("KEY=value # trailing comment"));
        Assert.Equal("pa#ss", Parse("KEY=\"pa#ss\""));
    }

    [Fact]
    public void ValuesKeepInternalEqualsSigns()
    {
        // Base64 tokens and connection strings routinely end in '='.
        Assert.Equal("a=b=c", DotEnvConfigurationProvider.Parse("KEY=a=b=c").GetValueOrDefault("KEY"));
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        var data = DotEnvConfigurationProvider.Parse("# a comment\n\nKEY=value\n\n# another\n");
        Assert.Single(data);
        Assert.Equal("value", data["KEY"]);
    }

    [Fact]
    public void AnEmptyValueIsAnEmptyString_NotAMissingKey()
    {
        // "set to nothing" and "not set" are different states, and code downstream of this
        // branches on which one it is.
        var data = DotEnvConfigurationProvider.Parse("KEY=\nOTHER=x");
        Assert.True(data.ContainsKey("KEY"));
        Assert.Equal("", data["KEY"]);
    }

    [Fact]
    public void KeysAreCaseInsensitive_MatchingIConfiguration()
    {
        var data = DotEnvConfigurationProvider.Parse("Discord_Token=abc");
        Assert.Equal("abc", data.GetValueOrDefault("DISCORD_TOKEN"));
    }

    [Fact]
    public void CrlfFilesParse()
    {
        var data = DotEnvConfigurationProvider.Parse("A=1\r\nB=2\r\n");
        Assert.Equal("1", data["A"]);
        Assert.Equal("2", data["B"]);
    }

    /// <summary>
    /// A key repeated later in the file wins.
    /// </summary>
    /// <remarks>
    /// RELIED ON WHEN SETTING UP A SECOND BOT. Its .env starts as a COPY of the first bot's,
    /// so it inherits the token, the channels and the other install's paths - and the safe way
    /// to correct that is to APPEND overrides at the end rather than hand-edit thirty lines
    /// and hope none were missed.
    ///
    /// dotenv behaves this way and so does this parser, but it was never pinned, so a change
    /// to first-wins would silently leave a second bot running on the first bot's token -
    /// two gateways on one identity, answering every command twice.
    /// </remarks>
    [Fact]
    public void ALaterValueOverridesAnEarlierOneForTheSameKey()
    {
        var parsed = DotEnvConfigurationProvider.Parse(
            """
            DISCORD_TOKEN=the-first-bots-token
            GUILD_ID=111

            # appended overrides
            DISCORD_TOKEN=the-second-bots-token
            GUILD_ID=222
            """);

        Assert.Equal("the-second-bots-token", parsed["DISCORD_TOKEN"]);
        Assert.Equal("222", parsed["GUILD_ID"]);
    }

    /// <summary>An appended EMPTY value blanks an inherited one.</summary>
    /// <remarks>
    /// How a copied .env turns a feature OFF for the clone - "PAYROLL_AMOUNT=" appended has to
    /// beat the inherited figure, or the second bot pays wages on the first bot's schedule.
    /// </remarks>
    [Fact]
    public void AnAppendedBlankValueClearsAnInheritedSetting()
    {
        var parsed = DotEnvConfigurationProvider.Parse(
            """
            PAYROLL_AMOUNT=500
            PAYROLL_AMOUNT=
            """);

        Assert.Equal("", parsed["PAYROLL_AMOUNT"]);
    }
}

public class BotOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static (string, string)[] Minimal =>
    [
        ("DISCORD_TOKEN", "t"), ("RCON_HOST_1", "10.0.0.1"), ("RCON_PORT_1", "9100"), ("RCON_PASSWORD_1", "p"),
    ];

    [Fact]
    public void ServersAreCollectedByIndex()
    {
        var options = BotOptions.Bind(Config(
            ("RCON_HOST_1", "a"), ("RCON_PORT_1", "1"), ("RCON_PASSWORD_1", "p1"),
            ("RCON_HOST_2", "b"), ("RCON_PORT_2", "2"), ("RCON_PASSWORD_2", "p2")));

        Assert.Equal(2, options.Servers.Count);
        Assert.Equal("server1", options.Servers[0].Name);
        Assert.Equal("b", options.Servers[1].Host);
    }

    [Fact]
    public void AGapInTheIndicesIsNotAStopSignal()
    {
        // Server 2 decommissioned, 1 and 3 still running. Stopping at the first gap would
        // silently drop a live server.
        var options = BotOptions.Bind(Config(
            ("RCON_HOST_1", "a"), ("RCON_PORT_1", "1"), ("RCON_PASSWORD_1", "p"),
            ("RCON_HOST_3", "c"), ("RCON_PORT_3", "3"), ("RCON_PASSWORD_3", "p")));

        Assert.Equal(2, options.Servers.Count);
        Assert.Equal("server3", options.Servers[1].Name);
    }

    [Fact]
    public void ValidateReportsEveryProblemAtOnce()
    {
        var problems = BotOptions.Bind(Config()).Validate();
        Assert.Contains(problems, p => p.Contains("DISCORD_TOKEN", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("RCON_HOST_1", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingPortOrPasswordIsNamedSpecifically()
    {
        var problems = BotOptions.Bind(Config(("DISCORD_TOKEN", "t"), ("RCON_HOST_2", "a"))).Validate();
        Assert.Contains(problems, p => p.Contains("RCON_PORT_2", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("RCON_PASSWORD_2", StringComparison.Ordinal));
    }

    [Fact]
    public void AValidConfigurationHasNoProblems()
    {
        Assert.Empty(BotOptions.Bind(Config(Minimal)).Validate());
    }

    [Fact]
    public void AnUnsetMetricsPortDisablesTheEndpoint()
    {
        Assert.False(BotOptions.Bind(Config(Minimal)).Monitoring.Enabled);
        Assert.Empty(BotOptions.Bind(Config(Minimal)).Monitoring.Validate());
    }

    [Fact]
    public void APortOfZeroIsRejected_NotSilentlyTreatedAsDisabled()
    {
        /* The exact tri-state bug the Node bot shipped: `if (!port)` read a configured 0
           as "no port", and the metrics endpoint never came up with nothing in the log to
           say why. Set-but-unusable must be LOUD. */
        var monitoring = BotOptions.Bind(Config([.. Minimal, ("METRICS_PORT", "0")])).Monitoring;
        Assert.True(monitoring.Enabled);
        Assert.NotEmpty(monitoring.Validate());
    }

    [Fact]
    public void MetricsDefaultsToLoopback()
    {
        Assert.Equal("127.0.0.1", BotOptions.Bind(Config(Minimal)).Monitoring.Host);
    }

    [Fact]
    public void ExposingMetricsWithoutATokenIsReported()
    {
        var monitoring = BotOptions.Bind(Config([.. Minimal, ("METRICS_PORT", "9109"), ("METRICS_HOST", "0.0.0.0")])).Monitoring;
        Assert.Contains(monitoring.Validate(), p => p.Contains("METRICS_TOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public void ExposingMetricsWithATokenIsFine()
    {
        var monitoring = BotOptions.Bind(Config([.. Minimal,
            ("METRICS_PORT", "9109"), ("METRICS_HOST", "0.0.0.0"), ("METRICS_TOKEN", "s")])).Monitoring;
        Assert.Empty(monitoring.Validate());
    }

    [Fact]
    public void TheReadCacheDurationIsConfigurableAndReachesEveryServer()
    {
        var options = BotOptions.Bind(Config([.. Minimal, ("RCON_READ_CACHE_MS", "500")]));
        Assert.All(options.Servers, s => Assert.Equal(TimeSpan.FromMilliseconds(500), s.ReadCacheDuration));
    }

    [Fact]
    public void AnUnparseableIntervalFallsBackRatherThanCrashing()
    {
        var options = BotOptions.Bind(Config([.. Minimal, ("RCON_READ_CACHE_MS", "soon")]));
        Assert.Equal(TimeSpan.FromMilliseconds(2500), options.Servers[0].ReadCacheDuration);
    }

}
