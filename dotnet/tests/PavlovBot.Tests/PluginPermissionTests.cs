using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Plugins;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// What a plugin can reach, and what it is refused.
/// </summary>
/// <remarks>
/// A PERMISSION MODEL THAT IS ONLY DECLARED IS NOT A PERMISSION MODEL. Before this, a plugin
/// received the host's whole IServiceProvider in InitializeAsync, so a Permissions field would
/// have been documentation that nothing consulted - a plugin declaring "I read player data"
/// could resolve BanService and ban people. These pin that it is now enforced at resolve time.
///
/// WHAT IS NOT CLAIMED: this does not contain a HOSTILE plugin, and nothing running in-process
/// could. A determined plugin can reflect, walk static state, or load its own copy of
/// anything. The gate stops honest mistakes and casual overreach, and it stops a manifest from
/// being a lie. The defence against a hostile plugin is not installing it.
/// </remarks>
public class PluginPermissionTests
{
    /// <summary>Stands in for the host container, and records what was asked for.</summary>
    private sealed class HostServices : IServiceProvider
    {
        public List<Type> Asked { get; } = [];

        public object? GetService(Type serviceType)
        {
            Asked.Add(serviceType);
            // Any non-null answer will do: the tests are about whether the ASK gets through.
            return new object();
        }
    }

    private static ScopedPluginServices Scoped(PluginPermission granted, HostServices? host = null) =>
        new(host ?? new HostServices(), "TestPlugin", granted, NullLogger.Instance);

    /// <summary>A declared scope resolves.</summary>
    [Fact]
    public void ADeclaredScopeIsAllowedThrough()
    {
        var host = new HostServices();
        var scoped = Scoped(PluginPermission.ModerationWrite, host);

        var service = scoped.GetService(typeof(PavlovBot.Host.Moderation.BanService));

        Assert.NotNull(service);
        Assert.Contains(typeof(PavlovBot.Host.Moderation.BanService), host.Asked);
    }

    /// <summary>
    /// AN UNDECLARED SCOPE IS REFUSED, and the host is never even asked.
    /// </summary>
    /// <remarks>
    /// The central property. Asserting that the host was not consulted matters as much as the
    /// null: a gate that resolves the service and then discards it has already constructed it,
    /// and for a singleton that means the plugin's request had side effects.
    /// </remarks>
    [Fact]
    public void AnUndeclaredScopeIsRefusedWithoutTouchingTheHost()
    {
        var host = new HostServices();
        var scoped = Scoped(PluginPermission.PlayerRead, host);

        var service = scoped.GetService(typeof(PavlovBot.Host.Moderation.BanService));

        Assert.Null(service);
        Assert.Empty(host.Asked);
    }

    /// <summary>Declaring nothing gets nothing. Forgetting to declare fails closed.</summary>
    [Fact]
    public void NoDeclaredScopesMeansNoServices()
    {
        var scoped = Scoped(PluginPermission.None);

        Assert.Null(scoped.GetService(typeof(PavlovBot.Host.Moderation.BanService)));
        Assert.Null(scoped.GetService(typeof(PavlovBot.Host.Logs.IpTrackingService)));
        Assert.Null(scoped.GetService(typeof(PavlovBot.Host.Rcon.RconRegistry)));
    }

    /// <summary>
    /// A type nobody has classified is refused, not passed through.
    /// </summary>
    /// <remarks>
    /// An allow-list that defaults to allow is not an allow-list. Without this, a service added
    /// next year would be reachable by every plugin ever written without anybody deciding it
    /// should be - and the decision would never be noticed as missing.
    /// </remarks>
    [Fact]
    public void AnUnclassifiedServiceIsRefusedEvenWithEveryScope()
    {
        var everything = Enum.GetValues<PluginPermission>().Aggregate(PluginPermission.None, (a, b) => a | b);
        var scoped = Scoped(everything);

        Assert.Null(scoped.GetService(typeof(IServiceScopeFactory)));
        Assert.Null(scoped.GetService(typeof(PluginHost)));
    }

    /// <summary>Loggers and the clock need no scope, so nobody over-declares to get one.</summary>
    /// <remarks>
    /// If writing a log line required a scope, every plugin author would ask for one they do
    /// not need - which is the habit that makes a permission model worthless.
    /// </remarks>
    [Fact]
    public void LoggingAndTimeAreAlwaysAvailable()
    {
        var scoped = Scoped(PluginPermission.None);

        Assert.NotNull(scoped.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory)));
        Assert.NotNull(scoped.GetService(typeof(Microsoft.Extensions.Logging.ILogger<PluginHost>)));
        Assert.NotNull(scoped.GetService(typeof(TimeProvider)));
    }

    /// <summary>Network access is separate from ordinary player access.</summary>
    /// <remarks>
    /// The same split /player enforces. A plugin that reads playtime should not thereby read
    /// addresses, and this is the test that fails if the two are ever folded into one scope.
    /// </remarks>
    [Fact]
    public void PlayerReadDoesNotGrantNetworkAccess()
    {
        var scoped = Scoped(PluginPermission.PlayerRead);

        Assert.NotNull(scoped.GetService(typeof(PavlovBot.Host.Intelligence.PlayerIntelligenceService)));
        Assert.Null(scoped.GetService(typeof(PavlovBot.Host.Logs.IpTrackingService)));
    }

    /// <summary>Reading the economy does not let a plugin move money.</summary>
    [Fact]
    public void EconomyReadDoesNotGrantEconomyWrite()
    {
        var scoped = Scoped(PluginPermission.EconomyRead);

        Assert.NotNull(scoped.GetService(typeof(PavlovBot.Core.Economy.IBalanceStore)));
        Assert.Null(scoped.GetService(typeof(PavlovBot.Core.Economy.Ledger)));
    }

    // ---- compatibility ----

    private sealed class Stub(string? minimum) : IPlugin
    {
        public string Name => "Stub";
        public string Version => "1.0.0";
        public string Description => "test";
        public string? MinimumBotVersion => minimum;
    }

    [Fact]
    public void APluginNeedingANewerBotIsRefused()
    {
        var problem = PluginHost.Incompatible(new Stub("2.0.0"), "1.4.0");

        Assert.NotNull(problem);
        Assert.Contains("2.0.0", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void APluginWithinRangeIsAccepted()
    {
        Assert.Null(PluginHost.Incompatible(new Stub("1.0.0"), "1.4.0"));
        Assert.Null(PluginHost.Incompatible(new Stub("1.4.0"), "1.4.0"));
    }

    /// <summary>Build metadata is not part of the comparison.</summary>
    [Fact]
    public void ACommitSuffixDoesNotBreakTheComparison()
    {
        Assert.Null(PluginHost.Incompatible(new Stub("1.0.0"), "1.4.0+abc1234"));
    }

    /// <summary>
    /// A plugin with no opinion, or a bot with no version, loads.
    /// </summary>
    /// <remarks>
    /// FAILS OPEN, deliberately, and it is the one place here that does. A bot built without
    /// version stamping must not become a bot that silently loads no plugins - that failure
    /// looks identical to "the plugin directory is wrong" and would waste an afternoon.
    /// </remarks>
    [Fact]
    public void MissingVersionInformationDoesNotBlockLoading()
    {
        Assert.Null(PluginHost.Incompatible(new Stub(null), "1.0.0"));
        Assert.Null(PluginHost.Incompatible(new Stub("1.0.0"), null));
        Assert.Null(PluginHost.Incompatible(new Stub("not-a-version"), "1.0.0"));
    }
}
