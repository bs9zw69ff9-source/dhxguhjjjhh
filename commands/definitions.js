/* ---------------- commands/definitions: every slash-command builder (registration payload) ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./commands/definitions")(ctx). */
module.exports = function(ctx) {
  const {
  ALL_FACTIONS, FACTION_BOT, FACTION_RANKS, JACKPOT_MIN_BALANCE, PermissionFlagsBits, SlashCommandBuilder,
  PUNISH_CHOICES, MENUS,
  } = ctx;

// ---- slash command definitions ----
function serverOption(o) {
  return o.setName("server").setDescription("Which server to target").setRequired(true)
    .addChoices({ name: "Server 1", value: "server1" }, { name: "Server 2", value: "server2" }, { name: "Server 3", value: "server3" }, { name: "All", value: "both" });
}

const factionChoices = ALL_FACTIONS.map(f => ({ name: f, value: f }));

const ALL_RANK_NAMES = [...new Set(
  Object.values(FACTION_RANKS).flatMap(cfg => cfg.order)
)].map(r => ({ name: r, value: r }));


const commands = [
  new SlashCommandBuilder().setName("help").setDescription("Show all commands and your current access level"),
  new SlashCommandBuilder().setName("serverinfo").setDescription("Server info: map, mode, player count").addStringOption(serverOption),
  new SlashCommandBuilder().setName("kick")
    .setDescription("Kick a player from the server")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID or username").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption)
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
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("date").setDescription("Only for 'Other': unban date YYYY-MM-DD (lifts 12pm Eastern that day)").setRequired(false))
    .addUserOption(o => o.setName("discord_user").setDescription("Discord account to DM the punishment details to")),
  new SlashCommandBuilder().setName("unban")
    .setDescription("Lift a player's ban")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID to pardon").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("checkban")
    .setDescription("Check if a player is currently banned")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("permban")
    .setDescription("Admin - Permanently ban a player")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption)
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
    .addRoleOption(o => o.setName("faction_leader_role").setDescription("Faction Leader role")),
  new SlashCommandBuilder().setName("announce")
    .setDescription("Mod - Broadcast a message via RCON Notify")
    .addStringOption(o => o.setName("message").setDescription("Message to broadcast (max 200 chars)").setRequired(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("target").setDescription("Who to notify: a specific player, or All").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("givemenu")
    .setDescription("Admin - Grant RCON menu access to a player")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("menu").setDescription("Menu to grant").setRequired(true)
      .addChoices(...MENUS.map(m => ({ name: m.name, value: m.value })))),
  new SlashCommandBuilder().setName("stripmenu")
    .setDescription("Admin - Revoke ALL RCON menu access from a player")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("stripmenuall")
    .setDescription("Owner - Clear ALL menu access from every player (both servers)"),
  new SlashCommandBuilder().setName("configure")
    .setDescription("Owner menu"),
  new SlashCommandBuilder().setName("link")
    .setDescription("Link your Discord account to your Pavlov username")
    .addSubcommand(s => s.setName("add")
      .setDescription("Request to link YOUR Discord to your Pavlov username (staff approves)")
      .addStringOption(o => o.setName("pavlov").setDescription("Your exact Pavlov in-game username").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("remove")
      .setDescription("Mod - Remove a Discord account's link")
      .addUserOption(o => o.setName("discord_user").setDescription("The Discord account").setRequired(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("Mod - Show all Discord ↔ Pavlov links")),
  new SlashCommandBuilder().setName("clearallbans")
    .setDescription("Owner - Unban everyone (clears blacklist.txt on both servers)"),
  new SlashCommandBuilder().setName("setrconroles")
    .setDescription("Admin - Set the Discord roles that grant each RCON menu (self-service panel)")
    .setDefaultMemberPermissions(PermissionFlagsBits.Administrator)
    .addRoleOption(o => o.setName("high_staff_role").setDescription("Role that grants the High Staff menu"))
    .addRoleOption(o => o.setName("staff_role").setDescription("Role that grants the Staff menu"))
    .addRoleOption(o => o.setName("faction_role").setDescription("Role that grants the Faction menu")),
  /* ── FACTION ─────────────────────────────────────────── */
  new SlashCommandBuilder()
    .setName("faction")
    .setDescription("Manage faction whitelists, ranks, and rosters")
    .addSubcommand(s => s.setName("add")
      .setDescription("Add a player to a faction whitelist")
      .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("rank").setDescription("Starting rank (faction-specific, default is lowest rank)").setAutocomplete(true)))
    .addSubcommand(s => s.setName("remove")
      .setDescription("Remove a player from a faction whitelist")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("playerid").setDescription("Player ID (pick the faction first)").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("List all members of a faction with their ranks")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices)))
    .addSubcommand(s => s.setName("playtime")
      .setDescription("Whitelisted members' playtime, highest to lowest")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices)))
    .addSubcommand(s => s.setName("setrankcap")
      .setDescription("Admin - Set the per-rank member cap within a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("rank").setDescription("Rank to cap (faction-specific)").setRequired(true).setAutocomplete(true))
      .addIntegerOption(o => o.setName("cap").setDescription("Max members at this rank (0 = unlimited)").setRequired(true).setMinValue(0).setMaxValue(500)))
    .addSubcommand(s => s.setName("wipe")
      .setDescription("Owner - Reset a faction's whitelist (or all of them), clearing every member and rank")
      .addStringOption(o => o.setName("faction").setDescription("Faction to wipe (omit to wipe ALL factions)").addChoices(...factionChoices))),

  new SlashCommandBuilder().setName("manual")
    .setDescription("Admin - Send a raw RCON command")
    .addStringOption(o => o.setName("command").setDescription("Raw RCON signal").setRequired(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("givecaps")
    .setDescription("Give credits to a player (faction leader / mod command)")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
    .addIntegerOption(o => o.setName("amount").setDescription("Credits to give").setRequired(true).setMinValue(1).setMaxValue(10000))
    .addStringOption(o => o.setName("reason").setDescription("Reason (shown in logs)")),
  new SlashCommandBuilder().setName("adjustcaps")
    .setDescription("Admin - Manually add or subtract credits from a player's ledger")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true))
    .addIntegerOption(o => o.setName("amount").setDescription("Credits to add (positive) or subtract (negative)").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for adjustment (logged)")),
  new SlashCommandBuilder().setName("stats")
    .setDescription("Player dossier: playtime, factions, balance, and mod history")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID").setRequired(true).setAutocomplete(true)),
  // Owner-only deep inspection (gated in the handler; not listed in /help). Discord
  // requires a non-empty description - a zero-width one gets the whole PUT rejected.
  new SlashCommandBuilder().setName("inspect")
    .setDescription("Owner: full record for a player - IPs, VPN checks, alts, flags")
    .addStringOption(o => o.setName("playerid").setDescription("Player ID or username").setRequired(true).setAutocomplete(true)),
  // Owner-only manual OS-firewall (ufw) control - block/unblock an IP by hand,
  // independent of any ban. Gated in the handler; requires UFW_BLOCK=1.
  new SlashCommandBuilder().setName("firewall")
    .setDescription("Owner: block or unblock an IP at the OS firewall (ufw)")
    .addSubcommand(s => s.setName("block")
      .setDescription("Block an IP at the firewall - sudo ufw insert 1 deny from <ip>")
      .addStringOption(o => o.setName("ip").setDescription("IPv4 address to block").setRequired(true)))
    .addSubcommand(s => s.setName("unblock")
      .setDescription("Remove a firewall block for an IP - sudo ufw delete <rule>")
      .addStringOption(o => o.setName("ip").setDescription("IPv4 address to unblock").setRequired(true)))
    .addSubcommand(s => s.setName("status")
      .setDescription("Show every IP currently blocked at the firewall - sudo ufw status numbered")),
  new SlashCommandBuilder().setName("kd")
    .setDescription("Kill/death stats - a player's K/D, or the leaderboard")
    .addStringOption(o => o.setName("playerid").setDescription("Player (leave blank for the K/D leaderboard)").setRequired(false).setAutocomplete(true)),
  /* ── CASINO ─────────────────────────────────────────── */
  new SlashCommandBuilder().setName("slots")
    .setDescription("Pull the slot machine - wager credits for a shot at the jackpot")
    .addIntegerOption(o => o.setName("bet").setDescription("Credits to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000)),
  new SlashCommandBuilder().setName("coinflip")
    .setDescription("Call it - heads or tails, double or nothing")
    .addIntegerOption(o => o.setName("bet").setDescription("Credits to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000))
    .addStringOption(o => o.setName("call").setDescription("Your call").setRequired(true)
      .addChoices({ name: "Heads", value: "heads" }, { name: "Tails", value: "tails" })),
  new SlashCommandBuilder().setName("blackjack")
    .setDescription("Play a hand of blackjack against the dealer")
    .addIntegerOption(o => o.setName("bet").setDescription("Credits to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000)),
  new SlashCommandBuilder().setName("roulette")
    .setDescription("Place a bet on the wheel")
    .addIntegerOption(o => o.setName("bet").setDescription("Credits to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000))
    .addStringOption(o => o.setName("space").setDescription("Outside bet (ignored if a number is given)")
      .addChoices(
        { name: "Red (2x)",            value: "red"    },
        { name: "Black (2x)",          value: "black"  },
        { name: "Even (2x)",           value: "even"   },
        { name: "Odd (2x)",            value: "odd"    },
        { name: "1-18 / Low (2x)",     value: "low"    },
        { name: "19-36 / High (2x)",   value: "high"   },
        { name: "1st 12  1-12 (3x)",   value: "1st12"  },
        { name: "2nd 12  13-24 (3x)",  value: "2nd12"  },
        { name: "3rd 12  25-36 (3x)",  value: "3rd12"  },
      ))
    .addIntegerOption(o => o.setName("number").setDescription("Straight-up bet on a single number 0-36 (36x, overrides space)").setMinValue(0).setMaxValue(36)),
  new SlashCommandBuilder().setName("cockfight")
    .setDescription("Wager credits on a duel - challenge another player, or the house")
    .addIntegerOption(o => o.setName("bet").setDescription("Credits to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000))
    .addUserOption(o => o.setName("opponent").setDescription("Challenge this player instead of the house")),
  new SlashCommandBuilder().setName("russianroulette")
    .setDescription("Push your luck - pull the trigger for a rising multiplier, or cash out")
    .addIntegerOption(o => o.setName("bet").setDescription("Credits to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000)),
  new SlashCommandBuilder().setName("jackpot")
    .setDescription(`Bet your ENTIRE balance for a shot at the casino jackpot (min ${JACKPOT_MIN_BALANCE.toLocaleString()} credits)`),
  new SlashCommandBuilder().setName("casino")
    .setDescription("Admin - Configure the casino")
    .setDefaultMemberPermissions(PermissionFlagsBits.Administrator)
    .addSubcommand(s => s.setName("status").setDescription("Show the current casino config"))
    .addSubcommand(s => s.setName("toggle")
      .setDescription("Enable or disable gambling server-wide")
      .addBooleanOption(o => o.setName("enabled").setDescription("On or off").setRequired(true)))
    .addSubcommand(s => s.setName("setlimits")
      .setDescription("Set the min/max bet")
      .addIntegerOption(o => o.setName("min").setDescription("Minimum bet").setRequired(true).setMinValue(1))
      .addIntegerOption(o => o.setName("max").setDescription("Maximum bet").setRequired(true).setMinValue(1))),
].map(c => c.toJSON());

// Partition: faction commands live on the faction bot when it's configured.
const FACTION_COMMAND_NAMES = new Set(["faction"]);
const mainCommands    = FACTION_BOT ? commands.filter(c => !FACTION_COMMAND_NAMES.has(c.name)) : commands;
const factionCommands = commands.filter(c => FACTION_COMMAND_NAMES.has(c.name));


  return { ALL_RANK_NAMES, commands, factionCommands, mainCommands };
};
