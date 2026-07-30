using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Services;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host;

/// <summary>
/// The composition root, and the only place in the program that constructs anything.
/// </summary>
/// <remarks>
/// This is the same discipline the Node bot arrived at the hard way - index.js builds the
/// context and every other module is a function of it, so nothing imports the root and the
/// wiring is checkable in one place. .NET gives it a name and a container, but the rule is
/// unchanged: if a type reaches for a global, it cannot be tested and it cannot be replaced.
///
/// Startup order matters and is deliberate:
///   1. configuration, then VALIDATE AND EXIT if it is wrong - before anything opens a
///      socket, so a misconfigured bot fails in a second with a list of what to fix rather
///      than a null reference three subsystems deep
///   2. monitoring, so /health is answering while the rest is still coming up
///   3. background services
///   4. the Discord gateway LAST, so the first interaction cannot arrive before the things
///      it depends on exist
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        // .env first, real environment second: an environment variable must win, which is
        // both what dotenv does and what makes a container override work.
        builder.Configuration
            .AddDotEnvFile(Path.Combine(Directory.GetCurrentDirectory(), ".env"))
            .AddEnvironmentVariables();

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
            options.UseUtcTimestamp = false;
        });
        builder.Logging.SetMinimumLevel(ParseLogLevel(builder.Configuration["LOG_LEVEL"]));

        var options = BotOptions.Bind(builder.Configuration);

        var problems = options.Validate();
        if (problems.Count > 0)
        {
            // Every problem at once. Fixing an environment one crash at a time, with a
            // restart between each, turns a five-minute deploy into an hour.
            await Console.Error.WriteLineAsync("Configuration is not usable:").ConfigureAwait(false);
            foreach (var problem in problems)
                await Console.Error.WriteLineAsync($"  - {problem}").ConfigureAwait(false);
            return 78;   // EX_CONFIG
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(options.Monitoring);

        builder.Services.AddSingleton(new MetricsRegistry());
        builder.Services.AddSingleton(new HealthRegistry());

        builder.Services.AddSingleton<IKeyValueBackend>(_ => new FileKeyValueBackend(options.DataDirectory));
        builder.Services.AddSingleton<IJsonCodec, SystemTextJsonCodec>();
        builder.Services.AddSingleton<SerializedStore>();

        builder.Services.AddSingleton<RconRegistry>();
        builder.Services.AddSingleton(sp => new ServiceRegistry(
            sp.GetRequiredService<MetricsRegistry>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ServiceRegistry>()));

        builder.Services.AddSingleton<ISlashCommand, ServerInfoCommand>();
        builder.Services.AddSingleton<DiscordGateway>();

        // Hosted services start in registration order and stop in reverse, which is exactly
        // the order this needs: monitoring up first and down last, gateway up last so no
        // interaction can arrive before its dependencies exist.
        builder.Services.AddSingleton(sp => new MonitoringServer(
            sp.GetRequiredService<MonitoringOptions>(),
            sp.GetRequiredService<MetricsRegistry>(),
            sp.GetRequiredService<HealthRegistry>(),
            sp.GetRequiredService<ILogger<MonitoringServer>>(),
            readiness: () => sp.GetRequiredService<DiscordGateway>().IsReady));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringServer>());
        builder.Services.AddHostedService<BackgroundServiceHost>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordGateway>());

        var host = builder.Build();

        RegisterProcessHealth(host.Services.GetRequiredService<HealthRegistry>(), options);

        var logger = host.Services.GetRequiredService<ILogger<IHost>>();
        logger.LogInformation("Starting with {Servers} RCON server(s), data in {DataDirectory}",
            options.Servers.Count, options.DataDirectory);

        /* --selftest builds the entire object graph, reports what it cost, and exits without
           connecting to anything. It is a deploy smoke test - it proves the configuration
           parses and every dependency resolves, which are the two things that fail at 2am -
           and it is the only honest way to measure this process against the Node bot, since
           neither side is connected to Discord when measured this way. */
        if (args.Contains("--selftest", StringComparer.Ordinal))
        {
            SelfTest(host, options, started);
            return 0;
        }

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void SelfTest(IHost host, BotOptions options, long started)
    {
        // Touching the gateway is the point: constructing DiscordSocketClient is where the
        // memory goes, and a graph that resolves everything EXCEPT the expensive part would
        // be a flattering measurement rather than a useful one.
        var gateway = host.Services.GetRequiredService<DiscordGateway>();
        _ = host.Services.GetRequiredService<RconRegistry>();
        _ = host.Services.GetRequiredService<SerializedStore>();
        _ = host.Services.GetRequiredService<MonitoringServer>();
        var commands = host.Services.GetServices<ISlashCommand>().ToList();

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        using var process = System.Diagnostics.Process.GetCurrentProcess();

        Console.WriteLine($"selftest: graph built in {elapsed.TotalMilliseconds:0}ms");
        Console.WriteLine($"selftest: {options.Servers.Count} RCON server(s), {commands.Count} command(s): {string.Join(", ", commands.Select(c => "/" + c.Name))}");
        Console.WriteLine($"selftest: resident {process.WorkingSet64 / 1024.0 / 1024:0.0} MB, managed heap {GC.GetTotalMemory(false) / 1024.0 / 1024:0.0} MB");
        Console.WriteLine($"selftest: gateway ready={gateway.IsReady} (not connected - this is construction only)");
    }

    /// <summary>Checks that describe the process itself rather than any one subsystem.</summary>
    private static void RegisterProcessHealth(HealthRegistry health, BotOptions options)
    {
        health.Register("filesystem", _ =>
        {
            /* Actually WRITE something. "The directory exists" is not the question - a
               read-only mount, a full disk and a permissions change all pass that check and
               all lose every write the bot makes. */
            try
            {
                Directory.CreateDirectory(options.DataDirectory);
                var probe = Path.Combine(options.DataDirectory, ".health-probe");
                File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                File.Delete(probe);
                return Task.FromResult(HealthResult.Healthy(options.DataDirectory));
            }
            catch (Exception ex)
            {
                return Task.FromResult(HealthResult.Unhealthy($"{options.DataDirectory} is not writable: {ex.Message}"));
            }
        });

        health.Register("timezone", _ =>
        {
            /* Every timestamp the bot prints is Eastern. If the tzdata is missing - the
               usual cause is InvariantGlobalization slipping back on, or a distroless base
               image - every ban expiry the bot displays is silently wrong by up to five
               hours, and nothing else notices. */
            try
            {
                var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                return Task.FromResult(HealthResult.Healthy(
                    TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, eastern).ToString("yyyy-MM-dd HH:mm zzz",
                        System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch (TimeZoneNotFoundException ex)
            {
                return Task.FromResult(HealthResult.Unhealthy($"America/New_York unavailable: {ex.Message}"));
            }
        });
    }

    private static LogLevel ParseLogLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information,
    };
}
