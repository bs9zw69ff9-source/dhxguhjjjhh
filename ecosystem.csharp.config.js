/* pm2 config for the C# bot.
 *
 * The app name is DELIBERATELY DIFFERENT from the Node bot's `pavlov-bot`, so the
 * two can never be confused for one another by a `pm2 restart`. They are separate
 * apps and only one of them should be online at a time — see the warning below.
 *
 *   pm2 start ecosystem.csharp.config.js
 *   pm2 logs pavlov-bot-cs
 *   pm2 stop pavlov-bot-cs
 *
 * ────────────────────────────────────────────────────────────────────────────
 * DO NOT RUN BOTH BOTS AGAINST THE SAME DISCORD APPLICATION AT ONCE.
 *
 * They would both answer every slash command, issue every ban twice, and post
 * every feed line twice. `scripts/deploy-csharp.sh` refuses to start this one
 * while `pavlov-bot` is online, which is the only automated protection there is.
 * ──────────────────────────────────────────────────────────────────────────── */
module.exports = {
  apps: [{
    name: "pavlov-bot-cs",

    // A native binary, not a script. Without `interpreter: "none"` pm2 hands it
    // to node, which fails with a syntax error that looks nothing like the cause.
    script: "./dotnet-out/PavlovBot",
    interpreter: "none",

    /* The working directory decides where the bot reads .env and where it puts
       data/. Pointing it at the repo root is what makes it share configuration
       — and bot.db — with the Node bot. */
    cwd: __dirname,

    instances: 1,
    exec_mode: "fork",     // never cluster: one bot, one gateway connection
    autorestart: true,

    /* A crash loop should back off rather than hammer Discord's login endpoint,
       which rate-limits and can get a token temporarily blocked. */
    min_uptime: "30s",
    max_restarts: 10,
    restart_delay: 5000,
    exp_backoff_restart_delay: 1000,

    /* Shutdown needs real time: the host stops every background service, drains
       in-flight RCON, and logs the gateway out. pm2's default 1600ms would SIGKILL
       it mid-write. */
    kill_timeout: 15000,

    /* Generous, and a backstop rather than a plan. The bot idles around 65 MB;
       if it is at 400 MB something is leaking and a restart buys time to look. */
    max_memory_restart: "400M",

    env: {
      DOTNET_ENVIRONMENT: "Production",

      // Workstation GC. This is one bot on a shared game host, and server GC
      // would reserve a heap per core for a workload that mostly waits on sockets.
      DOTNET_gcServer: "0",
    },

    // Timestamped, merged, and NOT rotated by pm2 — use pm2-logrotate for that.
    time: true,
    merge_logs: true,
    out_file: "./logs/pavlov-bot-cs.out.log",
    error_file: "./logs/pavlov-bot-cs.err.log",
  }],
};
