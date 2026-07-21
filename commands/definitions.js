/* ---------------- commands/definitions: every slash-command builder (registration payload) ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./commands/definitions")(ctx). */
module.exports = function(ctx) {
  const {
  ALL_FACTIONS, FACTION_BOT, FACTION_RANKS, PermissionFlagsBits, SlashCommandBuilder,
  PUNISH_CHOICES, MENUS,
  } = ctx;

// ---- slash command definitions ----
function serverOption(o) {
  return o.setName("server").setDescription("Which server to target").setRequired(true)
    .addChoices({ name: "Server 1", value: "server1" }, { name: "Server 2", value: "server2" }, { name: "Server 3", value: "server3" }, { name: "All", value: "both" });
}

const factionChoices = ALL_FACTIONS.map(f => ({ name: f, value: f }));
// Add the faction list as choices, but ONLY when there are factions - Discord
// rejects an empty choices array, so with zero factions the option falls back to
// autocomplete (which returns the current list, empty for now) and stays valid.
const facChoices = (o) => factionChoices.length ? o.addChoices(...factionChoices) : o.setAutocomplete(true);

const ALL_RANK_NAMES = [...new Set(
  Object.values(FACTION_RANKS).flatMap(cfg => cfg.order)
)].map(r => ({ name: r, value: r }));


const commands = [
  new SlashCommandBuilder().setName("help").setDescription("Show all commands and your current access level"),
  new SlashCommandBuilder().setName("serverinfo").setDescription("Server info: map, mode, player count"),
  new SlashCommandBuilder().setName("kick")
    .setDescription("Kick a player from the server")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID or username").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for kick"))
    .addUserOption(o => o.setName("discord_user").setDescription("Discord account to DM the punishment details to")),
  new SlashCommandBuilder().setName("flush")
    .setDescription("Randomly kick one online player from a server")
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("staffactivity")
    .setDescription("Admin - All moderation actions taken by a staff member")
    .addUserOption(o => o.setName("staff").setDescription("Staff member to audit").setRequired(true)),
  new SlashCommandBuilder().setName("staffleaderboard")
    .setDescription("Admin - Rank staff by moderation actions (bans/kicks/mutes)")
    .addStringOption(o => o.setName("period").setDescription("Time window (default: all time)")
      .addChoices(
        { name: "All time",     value: "all" },
        { name: "Last 30 days", value: "30d" },
        { name: "Last 7 days",  value: "7d" },
        { name: "Last 24 hours", value: "24h" },
      )),
  new SlashCommandBuilder().setName("tempban")
    .setDescription("Ban a player - the punishment sets the duration (Hard R = permanent; Other = custom date)")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID or username").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("reason").setDescription("Punishment - sets the ban length automatically").setRequired(true).addChoices(...PUNISH_CHOICES))
    .addStringOption(o => o.setName("date").setDescription("Only for 'Other': unban date YYYY-MM-DD (lifts 12pm Eastern that day)").setRequired(false))
    .addUserOption(o => o.setName("discord_user").setDescription("Discord account to DM the punishment details to")),
  new SlashCommandBuilder().setName("unban")
    .setDescription("Lift a player's ban")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID to pardon").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("checkban")
    .setDescription("Check if a player is currently banned")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("permban")
    .setDescription("Admin - Permanently ban a player")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("reason").setDescription("Grounds").setRequired(true).addChoices(...PUNISH_CHOICES))
    .addStringOption(o => o.setName("notes").setDescription("Additional context"))
    .addUserOption(o => o.setName("discord_user").setDescription("Discord account to DM the punishment details to")),
  new SlashCommandBuilder().setName("cleartempbans").setDescription("Admin - Clear all temporary bans (confirmation required)"),
  new SlashCommandBuilder().setName("donator")
    .setDescription("Admin - Manage the donator whitelist file")
    .addSubcommand(s => s.setName("add")
      .setDescription("Add a player to the donator file")
      .addStringOption(o => o.setName("playerid").setDescription("Player ID or username").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("remove")
      .setDescription("Remove a player from the donator file")
      .addStringOption(o => o.setName("playerid").setDescription("Player ID or username").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("List all players in the donator file")),
  new SlashCommandBuilder().setName("setroles")
    .setDescription("Admin - Configure role permissions")
    .addRoleOption(o => o.setName("mod_role").setDescription("Moderator role"))
    .addRoleOption(o => o.setName("admin_role").setDescription("Admin role"))
    .addRoleOption(o => o.setName("whitelist_leader_role").setDescription("Whitelist Leader role (manages every whitelist)"))
    .addRoleOption(o => o.setName("police_role").setDescription("Police Officer role (manages warrants)"))
    .addRoleOption(o => o.setName("gambino_role").setDescription("Gambino role (manages the Gambino whitelist)"))
    .addRoleOption(o => o.setName("colombo_role").setDescription("Colombo role (manages the Colombo whitelist)")),
  new SlashCommandBuilder().setName("warrant")
    .setDescription("Police - Manage player warrants")
    .addSubcommand(s => s.setName("give")
      .setDescription("Put out a warrant for a player (reason required)")
      .addStringOption(o => o.setName("playerid").setDescription("Player ID or username").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("reason").setDescription("Reason for the warrant").setRequired(true)))
    .addSubcommand(s => s.setName("remove")
      .setDescription("Clear a player's warrant (a number clears just that one; omit to clear all)")
      .addStringOption(o => o.setName("playerid").setDescription("Player ID or username").setRequired(true).setAutocomplete(true))
      .addIntegerOption(o => o.setName("number").setDescription("Which warrant to clear (from /warrant check); omit to clear all").setRequired(false).setMinValue(1)))
    .addSubcommand(s => s.setName("check")
      .setDescription("Check a player's warrant, or list every active warrant")
      .addStringOption(o => o.setName("playerid").setDescription("Player ID (leave blank to list all)").setRequired(false).setAutocomplete(true))),
  new SlashCommandBuilder().setName("announce")
    .setDescription("Mod - Broadcast a message via RCON Notify")
    .addStringOption(o => o.setName("message").setDescription("Message to broadcast (max 200 chars)").setRequired(true))
    .addStringOption(o => o.setName("target").setDescription("Who to notify: a specific player, or All").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("givemenu")
    .setDescription("Admin - Grant RCON menu access to a player")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("menu").setDescription("Menu to grant").setRequired(true)
      .addChoices(...MENUS.map(m => ({ name: m.name, value: m.value })))),
  new SlashCommandBuilder().setName("stripmenu")
    .setDescription("Admin - Revoke ALL RCON menu access from a player")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("stripmenuall")
    .setDescription("Owner - Clear ALL menu access from every player (both servers)"),
  new SlashCommandBuilder().setName("configure")
    .setDescription("Owner menu"),
  new SlashCommandBuilder().setName("clearallbans")
    .setDescription("Owner - Unban everyone (clears blacklist.txt on both servers)"),
  new SlashCommandBuilder().setName("setrconroles")
    .setDescription("Admin - Set the Discord roles that grant each RCON menu (self-service panel)")
    .setDefaultMemberPermissions(PermissionFlagsBits.Administrator)
    .addRoleOption(o => o.setName("high_staff_role").setDescription("Role that grants the High Staff menu"))
    .addRoleOption(o => o.setName("staff_role").setDescription("Role that grants the Staff menu"))
    .addRoleOption(o => o.setName("whitelist_role").setDescription("Role that grants the Whitelist menu")),
  /* ── WHITELIST ─────────────────────────────────────────── */
  new SlashCommandBuilder()
    .setName("whitelist")
    .setDescription("Manage whitelists, ranks, and rosters")
    .addSubcommand(s => s.setName("add")
      .setDescription("Add a player to a whitelist")
      .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
      .addStringOption(o => facChoices(o.setName("whitelist").setDescription("Whitelist name").setRequired(true)))
      .addStringOption(o => o.setName("rank").setDescription("Starting rank (whitelist-specific, default is lowest rank)").setAutocomplete(true)))
    .addSubcommand(s => s.setName("remove")
      .setDescription("Remove a player from a whitelist")
      .addStringOption(o => facChoices(o.setName("whitelist").setDescription("Whitelist name").setRequired(true)))
      .addStringOption(o => o.setName("playerid").setDescription("Player ID (pick the whitelist first)").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("List all members of a whitelist with their ranks")
      .addStringOption(o => facChoices(o.setName("whitelist").setDescription("Whitelist name").setRequired(true))))
    .addSubcommand(s => s.setName("playtime")
      .setDescription("Whitelisted members' playtime, highest to lowest")
      .addStringOption(o => facChoices(o.setName("whitelist").setDescription("Whitelist name").setRequired(true))))
    .addSubcommand(s => s.setName("wipe")
      .setDescription("Owner - Reset a whitelist (or all of them), clearing every member and rank")
      .addStringOption(o => facChoices(o.setName("whitelist").setDescription("Whitelist to wipe (omit to wipe ALL)")))),

  new SlashCommandBuilder().setName("manual")
    .setDescription("Admin - Send a raw RCON command")
    .addStringOption(o => o.setName("command").setDescription("Raw RCON signal").setRequired(true)),
  new SlashCommandBuilder().setName("givecaps")
    .setDescription("Give credits to a player (whitelist leader / mod command)")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
    .addIntegerOption(o => o.setName("amount").setDescription("Credits to give").setRequired(true).setMinValue(1).setMaxValue(10000))
    .addStringOption(o => o.setName("reason").setDescription("Reason (shown in logs)")),
  new SlashCommandBuilder().setName("adjustcaps")
    .setDescription("Admin - Manually add or subtract credits from a player's ledger")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
    .addIntegerOption(o => o.setName("amount").setDescription("Credits to add (positive) or subtract (negative)").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for adjustment (logged)")),
  // Owner-only deep inspection (gated in the handler; not listed in /help). Discord
  // requires a non-empty description - a zero-width one gets the whole PUT rejected.
  new SlashCommandBuilder().setName("health")
    .setDescription("Owner: bot health - uptime, server reachability, recent errors"),
  new SlashCommandBuilder().setName("inspect")
    .setDescription("Owner: full record for a player")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID or username").setRequired(true).setAutocomplete(true)),
  // Owner-only manual OS-firewall (ufw) control - block/unblock an IP by hand,
  // independent of any ban. Gated in the handler; requires UFW_BLOCK=1.
  new SlashCommandBuilder().setName("firewall")
    .setDescription("Owner: manage the server firewall block list")
    .addSubcommand(s => s.setName("block")
      .setDescription("Add an address to the firewall block list")
      .addStringOption(o => o.setName("ip").setDescription("Address to block").setRequired(true)))
    .addSubcommand(s => s.setName("unblock")
      .setDescription("Remove an address from the firewall block list")
      .addStringOption(o => o.setName("ip").setDescription("Address to unblock").setRequired(true)))
    .addSubcommand(s => s.setName("status")
      .setDescription("Show the firewall block list")),
].map(c => c.toJSON());

// Partition: faction commands live on the faction bot when it's configured.
const FACTION_COMMAND_NAMES = new Set(["whitelist"]);
const mainCommands    = FACTION_BOT ? commands.filter(c => !FACTION_COMMAND_NAMES.has(c.name)) : commands;
const factionCommands = commands.filter(c => FACTION_COMMAND_NAMES.has(c.name));


  return { ALL_RANK_NAMES, commands, factionCommands, mainCommands };
};
