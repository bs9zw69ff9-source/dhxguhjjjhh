using System.Reflection;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Plugins;

/// <summary>
/// A unit of optional functionality, loaded from an assembly dropped into <c>plugins/</c>.
/// </summary>
/// <remarks>
/// Every hook is optional. A plugin can add commands, do background work, or both.
/// </remarks>
public interface IPlugin
{
    string Name { get; }
    string Version { get; }
    string Description { get; }

    /// <summary>Plugins that must be started first. Ordering is topological over this.</summary>
    IReadOnlyList<string> DependsOn => [];

    /// <summary>Build state. No side effects outside the plugin.</summary>
    Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Begin doing work.</summary>
    Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Release everything acquired in initialize or start.</summary>
    Task StopAsync() => Task.CompletedTask;
}

public enum PluginState { Discovered, Initialized, Running, Failed, Stopped }

public sealed record PluginStatus(string Name, string Version, PluginState State, string? Error);

/// <summary>
/// Discovering, ordering and running plugins.
/// </summary>
/// <remarks>
/// ISOLATION IS THE POINT. A plugin that throws while loading, initialising or starting is
/// recorded and SKIPPED, and the rest of the bot still comes up. One broken optional feature
/// must never be a dead bot - which is the whole reason it is a plugin and not a class in
/// the host.
///
/// The isolation has a real limit and it should be said plainly: plugins load into THIS
/// process and this AssemblyLoadContext. A plugin cannot be unloaded, and one that corrupts
/// shared state or deadlocks takes the bot with it. What this protects against is the common
/// case - a plugin that is broken on its own terms - not a hostile one. <b>Only load
/// assemblies you trust.</b>
/// </remarks>
public sealed class PluginHost(IServiceProvider services, ILogger<PluginHost> logger)
{
    private readonly List<(IPlugin Plugin, PluginRecord Record)> _plugins = [];

    private sealed class PluginRecord
    {
        public PluginState State = PluginState.Discovered;
        public string? Error;
    }

    /// <summary>
    /// Start order honouring <see cref="IPlugin.DependsOn"/>.
    /// </summary>
    /// <remarks>
    /// Throws on a cycle NAMING the participants, and on a dependency that is not loaded -
    /// reported rather than silently reordered into something that fails at runtime in a way
    /// nobody can trace back to here.
    /// </remarks>
    public static IReadOnlyList<IPlugin> Order(IReadOnlyCollection<IPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        var byName = plugins.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<IPlugin>();
        var path = new List<string>();

        void Visit(string name, string? requiredBy)
        {
            if (state.TryGetValue(name, out var s))
            {
                if (s == 2) return;
                throw new InvalidOperationException(
                    $"circular plugin dependency: {string.Join(" -> ", path.Append(name))}");
            }
            if (!byName.TryGetValue(name, out var plugin))
                throw new InvalidOperationException(
                    $"plugin \"{requiredBy ?? "?"}\" depends on \"{name}\", which is not loaded or is disabled");

            state[name] = 1;
            path.Add(name);
            foreach (var dependency in plugin.DependsOn) Visit(dependency, name);
            path.RemoveAt(path.Count - 1);
            state[name] = 2;
            ordered.Add(plugin);
        }

        foreach (var plugin in plugins) Visit(plugin.Name, null);
        return ordered;
    }

    /// <summary>
    /// Load every <c>*.dll</c> in <paramref name="directory"/> that exports a plugin.
    /// </summary>
    /// <param name="enabled">
    /// Names to load, or null for all of them. An explicit list is how a plugin that is
    /// crashing gets disabled without deleting the file.
    /// </param>
    public IReadOnlyList<IPlugin> Discover(string directory, IReadOnlyCollection<string>? enabled = null)
    {
        if (!Directory.Exists(directory))
        {
            logger.LogDebug("No plugins directory at {Directory}", directory);
            return [];
        }

        var found = new List<IPlugin>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(file);

                var types = assembly.GetTypes()
                    .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

                foreach (var type in types)
                {
                    if (Activator.CreateInstance(type) is not IPlugin plugin) continue;

                    if (enabled is not null && !enabled.Contains(plugin.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        logger.LogInformation("Plugin {Name} found but not enabled - skipping", plugin.Name);
                        continue;
                    }

                    found.Add(plugin);
                    logger.LogInformation("Discovered plugin {Name} v{Version} - {Description}",
                        plugin.Name, plugin.Version, plugin.Description);
                }
            }
            catch (Exception ex)
            {
                /* A plugin that will not even LOAD is recorded and skipped. ReflectionTypeLoadException
                   in particular means it was built against a different version of something,
                   which is a plugin problem, not a bot problem. */
                logger.LogError(ex, "Could not load plugin assembly {File}", Path.GetFileName(file));
            }
        }

        return found;
    }

    /// <summary>Initialise and start every plugin, in dependency order.</summary>
    public async Task StartAllAsync(IReadOnlyCollection<IPlugin> plugins, CancellationToken ct = default)
    {
        IReadOnlyList<IPlugin> ordered;
        try
        {
            ordered = Order(plugins);
        }
        catch (InvalidOperationException ex)
        {
            // A bad dependency graph disables the whole plugin SYSTEM, not the bot. Starting
            // an arbitrary subset would be worse - a plugin would run without what it needs.
            logger.LogError(ex, "Plugins are not loadable. No plugin will run.");
            return;
        }

        foreach (var plugin in ordered)
        {
            var record = new PluginRecord();
            _plugins.Add((plugin, record));

            try
            {
                await plugin.InitializeAsync(services, ct).ConfigureAwait(false);
                record.State = PluginState.Initialized;

                await plugin.StartAsync(ct).ConfigureAwait(false);
                record.State = PluginState.Running;

                logger.LogInformation("Plugin {Name} running", plugin.Name);
            }
            catch (Exception ex)
            {
                record.State = PluginState.Failed;
                record.Error = ex.Message;
                logger.LogError(ex, "Plugin {Name} failed to start - it is disabled, the bot continues", plugin.Name);
            }
        }
    }

    /// <summary>Stop every plugin in REVERSE dependency order.</summary>
    public async Task StopAllAsync()
    {
        foreach (var (plugin, record) in Enumerable.Reverse(_plugins))
        {
            if (record.State != PluginState.Running) continue;

            try
            {
                await plugin.StopAsync().ConfigureAwait(false);
                record.State = PluginState.Stopped;
            }
            catch (Exception ex)
            {
                // A plugin that cannot stop cleanly must not block shutdown of the rest.
                logger.LogWarning(ex, "Plugin {Name} did not stop cleanly", plugin.Name);
            }
        }
    }

    public IReadOnlyList<PluginStatus> Status() =>
        _plugins.Select(p => new PluginStatus(p.Plugin.Name, p.Plugin.Version, p.Record.State, p.Record.Error)).ToList();
}

/// <summary>Runs the plugin system from the host lifetime.</summary>
public sealed class PluginHostedService(
    PluginHost plugins, Configuration.FeatureOptions features,
    Observability.HealthRegistry health, ILogger<PluginHostedService> logger) : Microsoft.Extensions.Hosting.IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var directory = features.PluginDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "plugins");
        var discovered = plugins.Discover(directory, features.EnabledPlugins.Count > 0 ? features.EnabledPlugins : null);

        if (discovered.Count == 0)
        {
            logger.LogDebug("No plugins loaded");
            return;
        }

        await plugins.StartAllAsync(discovered, cancellationToken).ConfigureAwait(false);

        /* NON-CRITICAL health. A failed plugin is a feature being dark, not an outage, and
           returning unhealthy here would restart-loop a bot whose only problem is one
           optional extra. */
        health.Register("plugins", _ =>
        {
            var status = plugins.Status();
            var failed = status.Where(p => p.State == PluginState.Failed).ToList();

            return Task.FromResult(failed.Count == 0
                ? Observability.HealthResult.Healthy($"{status.Count} loaded")
                : Observability.HealthResult.Degraded(
                    string.Join("; ", failed.Select(p => $"{p.Name}: {p.Error}"))));
        }, critical: false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => plugins.StopAllAsync();
}
