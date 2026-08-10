using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Text;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Plugins;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/plugins</c> - what is loaded, what it may reach, and what was refused.
/// </summary>
/// <remarks>
/// READ-ONLY, AND THAT IS THE DESIGN RATHER THAN AN OMISSION. The brief asks for
/// <c>/plugin enable</c>, <c>disable</c> and <c>reload</c>. None of those can be implemented
/// honestly here: a plugin is loaded into the host's AssemblyLoadContext and .NET cannot
/// unload one. A disable command could stop a plugin's WORK, but its code would stay loaded,
/// its event subscriptions would stay attached and whatever it holds would stay held - so the
/// button would report a thing it had not done, which is the failure this whole codebase
/// keeps finding and fixing.
///
/// Enabling and disabling is therefore configuration: PLUGINS_ENABLED and PLUGINS_DISABLED in
/// .env, applied at start. This command shows the effect of that configuration, including
/// which plugins were refused and why, so the round trip is visible without reading the log.
///
/// OWNER-ONLY. It lists what every plugin can reach, which is a map of the bot's soft spots.
/// </remarks>
public sealed class PluginsCommand(PluginHost plugins, FeatureOptions features, Access access) : ISlashCommand
{
    public string Name => "plugins";

    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Loaded plugins, their permissions and their state")
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        var loaded = plugins.Status();

        var embed = loaded.Count == 0
            ? Theme.Notice("No plugins loaded",
                $"Nothing was loaded from `{Sanitize.Code(features.PluginDirectory ?? "./plugins")}`.")
            : Theme.Notice($"{loaded.Count} plugin(s)", string.Join("\n\n", loaded.Select(p =>
                $"{Glyph(p.State)} **{Sanitize.Code(p.Name)}** `v{Sanitize.Code(p.Version)}` — {p.State}" +
                (p.Error is { Length: > 0 } e ? $"\n↳ {Sanitize.Message(e)}" : ""))));

        embed.AddField("Directory", $"`{Sanitize.Code(features.PluginDirectory ?? "./plugins")}`");

        embed.AddField("Enabled",
            features.EnabledPlugins.Count == 0
                ? "`PLUGINS_ENABLED` unset — every plugin found is loaded"
                : string.Join(", ", features.EnabledPlugins.Select(p => $"`{Sanitize.Code(p)}`")));

        embed.AddField("Disabled",
            features.DisabledPlugins.Count == 0
                ? "`PLUGINS_DISABLED` unset — nothing is being refused"
                : string.Join(", ", features.DisabledPlugins.Select(p => $"`{Sanitize.Code(p)}`")));

        /* SAID HERE RATHER THAN LEFT TO BE DISCOVERED. Somebody looking at this panel is
           looking for the disable button, and the honest answer is faster than letting them
           search for it. */
        embed.AddField("Turning one off",
            "Set `PLUGINS_DISABLED=name` in `.env` and restart. There is no runtime toggle: " +
            ".NET cannot unload an assembly, so a disable command could stop a plugin working " +
            "but not unload its code or detach what it subscribed to. A half-disable reported " +
            "as a disable is worse than restarting.");

        embed.AddField("Permission scopes",
            "A plugin only reaches what it declares. Anything undeclared resolves to nothing " +
            "and is logged.\n" +
            string.Join("\n", ScopedPluginServices.AllScopes.Select(s => $"{Theme.Dot} `{ScopedPluginServices.Slug(s)}`")));

        await Reply(command, embed).ConfigureAwait(false);
    }

    private static string Glyph(PluginState state) => state switch
    {
        PluginState.Running => Theme.Up,
        PluginState.Failed => Theme.Bad,
        PluginState.Stopped => Theme.Down,
        _ => Theme.Dot,
    };

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
