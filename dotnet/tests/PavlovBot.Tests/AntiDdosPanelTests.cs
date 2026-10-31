using Discord;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>The Anti-DDoS menu in /configure: within Discord's limits, and wired to real actions.</summary>
public class AntiDdosPanelTests
{
    /// <summary>Every subcommand <see cref="AntiDdosCommand.RunAsync"/> knows.</summary>
    private static readonly HashSet<string> Subcommands =
    [
        "status", "traffic", "history", "top", "detections", "bans", "crowdsec", "storage", "export", "block", "unban", "logging",
        "allow add", "allow remove", "allow list", "threshold set", "threshold list",
    ];

    private static readonly HashSet<string> ToolActions = ["tools:status", "tools:install", "tools:analyze", "tools:unblock"];

    [Fact]
    public void TheMenuFitsInOneDiscordSelect()
    {
        var actions = AntiDdosPanel.Actions;

        Assert.InRange(actions.Count, 1, 25);
        Assert.Equal(actions.Count, actions.Select(a => a.Key).Distinct().Count());
        Assert.All(actions, a =>
        {
            Assert.InRange(a.Label.Length, 1, 100);
            Assert.InRange(a.Description.Length, 1, 100);
            Assert.InRange(a.Key.Length, 1, 100);
            Assert.True(Emoji.TryParse(a.Emoji, out _), $"{a.Key}: {a.Emoji} is not an emoji Discord accepts");
        });
        Assert.True(ComponentId.Encode(AntiDdosPanel.Id, "menu").Length <= 100);
    }

    [Fact]
    public void EveryEntryRunsARealAction()
    {
        Assert.All(AntiDdosPanel.Actions, a => Assert.True(Subcommands.Contains(a.Run) || ToolActions.Contains(a.Run), $"{a.Key} runs unknown {a.Run}"));

        // Everything the slash command can change is reachable from the panel too.
        var runs = AntiDdosPanel.Actions.Select(a => a.Run).ToHashSet();
        foreach (var change in new[] { "block", "unban", "logging", "allow add", "allow remove", "threshold set", "export" })
            Assert.Contains(change, runs);
        Assert.Subset(ToolActions, runs.Where(ToolActions.Contains).ToHashSet());
        Assert.Equal(ToolActions, runs.Where(ToolActions.Contains).ToHashSet());
    }

    [Fact]
    public void ModalsStayWithinDiscordsLimits()
    {
        foreach (var action in AntiDdosPanel.Actions)
        {
            var modal = AntiDdosPanel.Modal(action);
            var inputs = (action.Inputs?.Count ?? 0) + (action.Confirm is null ? 0 : 1);
            if (inputs == 0)
            {
                Assert.Null(modal);
                continue;
            }

            Assert.NotNull(modal);
            Assert.InRange(inputs, 1, 5);
            Assert.InRange(modal!.Title.Length, 1, 45);
            Assert.True(modal.CustomId.Length <= 100);
            Assert.All(action.Inputs ?? [], i =>
            {
                Assert.InRange(i.Label.Length, 1, 45);
                Assert.InRange(i.Placeholder.Length, 0, 100);
            });
        }
    }

    [Fact]
    public void ModalTextBecomesTheOptionsTheSlashCommandWouldGet()
    {
        var lookup = AntiDdosPanel.Find("lookup")!;
        Assert.Null(AntiDdosPanel.Arguments(lookup, new Dictionary<string, string> { ["ip"] = " 203.0.113.5 ", ["minutes"] = "90" }, out var args));
        Assert.Equal("203.0.113.5", args["ip"]);
        Assert.Equal(90L, args["minutes"]);   // integers as long, as Discord passes them

        // Optional and left empty: absent, so the command's own default applies.
        Assert.Null(AntiDdosPanel.Arguments(lookup, new Dictionary<string, string> { ["ip"] = "203.0.113.5", ["minutes"] = "" }, out args));
        Assert.False(args.ContainsKey("minutes"));

        // Fixed options ride along.
        Assert.Null(AntiDdosPanel.Arguments(AntiDdosPanel.Find("incident")!, new Dictionary<string, string> { ["minutes"] = "45" }, out args));
        Assert.Equal(("incident", 45L), ((string)args["mode"], (long)args["minutes"]));
        Assert.Null(AntiDdosPanel.Arguments(AntiDdosPanel.Find("traffic")!, new Dictionary<string, string>(), out args));
        Assert.Equal(60L, args["minutes"]);
    }

    [Theory]
    [InlineData("block", "ip", "")]                  // required and empty
    [InlineData("threshold", "value", "lots")]       // not a number
    public void BadModalTextIsRefusedBeforeAnythingRuns(string action, string field, string value)
    {
        var fields = new Dictionary<string, string> { ["ip"] = "203.0.113.5", ["duration"] = "4h", ["reason"] = "x", ["name"] = "per_ip_pps", ["value"] = "1000" };
        fields[field] = value;

        Assert.NotNull(AntiDdosPanel.Arguments(AntiDdosPanel.Find(action)!, fields, out _));
    }

    [Fact]
    public void TheMenuDoesNotCollideWithTheOtherPanels()
    {
        Assert.NotEqual(ConfigPanel.Id, AntiDdosPanel.Id);
        Assert.NotEqual(MenuPanel.Id, AntiDdosPanel.Id);
        Assert.Equal(AntiDdosPanel.Actions.Count, new AntiDdosPanel(null!, null!, null!, null!).Menu().Options.Count);
    }

    [Fact]
    public void OnlyTheActionsThatLetAttackersBackInAskForAConfirmation() =>
        Assert.Equal(["unblockall"], AntiDdosPanel.Actions.Where(a => a.Confirm is not null).Select(a => a.Key));
}
