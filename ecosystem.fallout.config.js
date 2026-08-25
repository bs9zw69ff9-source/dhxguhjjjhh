/* pm2 config for the THEMED SECOND BOT.
 *
 *   pm2 start ecosystem.fallout.config.js
 *   pm2 logs pavlov-bot-fallout
 *   pm2 restart ecosystem.fallout.config.js --update-env    (after a deploy)
 *
 * SAME BINARY, DIFFERENT WORKING DIRECTORY. There is one published output and one
 * repo; what makes this a second bot is `cwd`, because that is where the process
 * reads .env, factions.json and its data/. Copying the tree instead would mean
 * every fix landing twice, which on this repo's history means it lands once.
 *
 * WHY THIS FILE EXISTS AT ALL. The setup doc used to say:
 *
 *     pm2 start ./dotnet-out/PavlovBot --name pavlov-bot-fallout \
 *       --cwd /root/pavlov-bot-fallout --interpreter none
 *
 * That works, and it silently drops every tuning value in ecosystem.csharp.config.js.
 * The one that matters is kill_timeout: pm2's default is 1600ms and this bot's
 * shutdown drains in-flight RCON and finishes roster writes. A SIGKILL landing
 * mid-write to a FactionRoles file truncates a whitelist the game is reading.
 * An ad-hoc `pm2 start` is a data-loss risk, not a shortcut.
 *
 * DIFFERENT DISCORD APPLICATION, NOT A SECOND COPY OF THE FIRST. Both online at
 * once is correct and expected here - unlike the old Node/C# pair - because they
 * hold different tokens. Two processes on ONE token is the thing that answers
 * every command twice, and the check for that is the token in each .env.
 */
const path = require("path");

/* Overridable so the second bot does not have to live at one blessed path:
 *   FALLOUT_HOME=/srv/other pm2 start ecosystem.fallout.config.js
 * The default is what SECOND-BOT.md tells you to create. */
const HOME = process.env.FALLOUT_HOME || "/root/pavlov-bot-fallout";

module.exports = {
  apps: [{
    name: "pavlov-bot-fallout",

    // Absolute, because cwd is NOT the repo - a relative script path would be
    // resolved against the second bot's directory, which holds no binary.
    script: path.join(__dirname, "dotnet-out/PavlovBot"),

    // A native binary. Without this pm2 hands it to node, which fails with a
    // syntax error that looks nothing like the cause.
    interpreter: "none",

    // THE WHOLE OF WHAT MAKES THIS A SECOND BOT.
    cwd: HOME,

    instances: 1,
    exec_mode: "fork",     // never cluster: one bot, one gateway connection
    autorestart: true,

    // Back off rather than hammer Discord's login endpoint, which rate-limits
    // and can get a token temporarily blocked.
    min_uptime: "30s",
    max_restarts: 10,
    restart_delay: 5000,
    exp_backoff_restart_delay: 1000,

    // Real shutdown time: stop background services, drain RCON, log the gateway
    // out, finish any roster write. pm2's default would SIGKILL it mid-write.
    kill_timeout: 15000,

    max_memory_restart: "400M",

    env: {
      DOTNET_ENVIRONMENT: "Production",
      DOTNET_gcServer: "0",
    },

    time: true,
    merge_logs: true,

    // Under the SECOND bot's directory, so the two do not interleave into one
    // file - which is how you end up debugging the wrong bot's stack trace.
    out_file: path.join(HOME, "logs/pavlov-bot-fallout.out.log"),
    error_file: path.join(HOME, "logs/pavlov-bot-fallout.err.log"),
  }],
};
