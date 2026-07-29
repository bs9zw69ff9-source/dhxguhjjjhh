/* ---------------- plugin template ----------------
   Copy this directory to plugins/<yourname>/ and edit. The leading underscore is what
   keeps THIS one from loading, so the template can live next to real plugins without
   ever running - remove it when you copy.

   The contract, in full. Every hook is optional; implement only what you need.

     name              required, unique. Also the key used by dependsOn and by
                       PLUGINS_DISABLED.
     version           x.y.z. Reported by /health and the plugin listing.
     description       one line, shown in listings.
     dependsOn         names of plugins that must initialise and start BEFORE this one.
                       Cycles and unknown names are reported by name at load.
     enabledByDefault  false means it only loads if explicitly enabled.
     create(ctx)       factory. Receives the injected context and returns the instance.

   Lifecycle, in call order:

     initialize()      build internal state. Throwing here SKIPS this plugin and leaves
                       the rest of the bot running - it is never fatal.
     registerCommands()  -> { "commandname": handler }   merged into the dispatcher
     registerEvents()    -> { "eventName": handler }     collected per event name
     start()           begin doing work: timers, listeners, connections.
     stop()            release everything start()/initialize() acquired. Called in
                       REVERSE dependency order on shutdown, and a throwing stop() does
                       not prevent the remaining plugins from stopping.

   Rules that keep plugins isolated:
     - take everything from ctx; never require("../../index.js")
     - never reach for module-level mutable state from elsewhere
     - unref() every timer, so a plugin can never hold the process open
     - anything acquired in start() is released in stop(), no exceptions */
"use strict";

module.exports = {
  name: "example",
  version: "0.1.0",
  description: "Template showing the plugin contract. Rename before use.",
  dependsOn: [],
  enabledByDefault: false,

  create(ctx) {
    const { logger, metrics, errors } = ctx;
    let timer = null;

    return {
      async initialize() {
        logger?.debug?.("Plugins", "example initialised");
      },

      registerCommands() {
        return {
          // "mycommand": async (interaction, name) => interaction.reply("hello"),
        };
      },

      registerEvents() {
        return {
          // onPlayerJoin: async ({ name }) => logger.info("Example", `${name} joined`),
        };
      },

      async start() {
        timer = setInterval(() => {
          try { metrics?.increment("example_ticks_total"); }
          catch (err) { errors?.record(err, { category: "example", subsystem: "example" }); }
        }, 60_000);
        if (timer.unref) timer.unref();
      },

      async stop() {
        if (timer) { clearInterval(timer); timer = null; }
      },
    };
  },
};
