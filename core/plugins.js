/* ---------------- core/plugins: discovery, dependency ordering, lifecycle ----------------
   A plugin is a directory (or file) under plugins/ exporting a factory:

     module.exports = {
       name: "moderation",
       version: "1.0.0",
       description: "bans, kicks, ban evasion",
       dependsOn: ["rcon", "database"],
       enabledByDefault: true,
       create(ctx) {
         return {
           async initialize() {},        // build state; no side effects outside the plugin
           registerCommands() { return {} },   // name -> handler
           registerEvents()   { return {} },   // event -> handler
           async start() {},             // begin doing work
           async stop()  {},             // release everything acquired in start/initialize
         };
       },
     };

   Every hook is optional. Ordering is topological over `dependsOn`, so a plugin can rely
   on its dependencies having been initialised and started first, and teardown runs in
   reverse for the same reason.

   Isolation is the point: a plugin that throws during load is recorded and SKIPPED, and
   the rest of the bot still comes up. One broken feature must never be a dead bot. */
"use strict";

const fs = require("fs");
const path = require("path");

const SEMVER_ISH = /^\d+\.\d+\.\d+/;

/* Topological order over plugin dependencies. Unknown or disabled dependencies are
   surfaced explicitly rather than silently reordering into something that will fail at
   runtime in a way nobody can trace back to here. */
function orderPlugins(manifests) {
  const byName = new Map(manifests.map(m => [m.name, m]));
  const state = new Map();
  const out = [];
  const path_ = [];
  const visit = (name) => {
    const s = state.get(name) ?? 0;
    if (s === 2) return;
    if (s === 1) throw new Error(`circular plugin dependency: ${[...path_, name].join(" -> ")}`);
    const m = byName.get(name);
    if (!m) throw new Error(`plugin "${path_[path_.length - 1] ?? "?"}" depends on "${name}", which is not loaded or is disabled`);
    state.set(name, 1); path_.push(name);
    for (const d of m.dependsOn ?? []) visit(d);
    path_.pop(); state.set(name, 2);
    out.push(m);
  };
  for (const m of manifests) visit(m.name);
  return out;
}

function validateManifest(mod, source) {
  const errs = [];
  if (!mod || typeof mod !== "object") errs.push("module does not export an object");
  else {
    if (!mod.name || typeof mod.name !== "string") errs.push("missing a string `name`");
    if (mod.version && !SEMVER_ISH.test(String(mod.version))) errs.push(`version "${mod.version}" is not x.y.z`);
    if (mod.dependsOn && !Array.isArray(mod.dependsOn)) errs.push("`dependsOn` must be an array");
    if (typeof mod.create !== "function") errs.push("missing a `create(ctx)` factory");
  }
  return errs.length ? new Error(`invalid plugin at ${source}: ${errs.join("; ")}`) : null;
}

function createPluginManager({ logger = null, metrics = null, errors = null,
                               dir = null, isEnabled = () => true } = {}) {
  const loaded = new Map();     // name -> { manifest, instance, state, loadMs }
  const failed = new Map();     // name/source -> reason

  const manager = {
    /** Discover plugin modules on disk. Missing directory is not an error - a
        deployment with no plugins is a valid deployment. */
    discover(pluginDir = dir) {
      if (!pluginDir || !fs.existsSync(pluginDir)) return [];
      const found = [];
      for (const entry of fs.readdirSync(pluginDir, { withFileTypes: true })) {
        if (entry.name.startsWith("_") || entry.name.startsWith(".")) continue;   // _foo = intentionally skipped
        const full = path.join(pluginDir, entry.name);
        const target = entry.isDirectory() ? path.join(full, "index.js") : full;
        if (!target.endsWith(".js") || !fs.existsSync(target)) continue;
        found.push(target);
      }
      return found.sort();
    },

    /** Register an already-required manifest (used by tests and for built-ins). */
    register(mod, source = "(inline)") {
      const bad = validateManifest(mod, source);
      if (bad) { failed.set(source, bad.message); errors?.record(bad, { category: "plugin_invalid", subsystem: "plugins" }); return null; }
      if (loaded.has(mod.name)) { failed.set(mod.name, "duplicate plugin name"); return null; }
      if (!isEnabled(mod.name, mod)) { logger?.debug?.("Plugins", `${mod.name} is disabled by configuration`); return null; }
      loaded.set(mod.name, { manifest: mod, instance: null, state: "registered", loadMs: 0, source });
      return mod;
    },

    /** require() every discovered plugin. A throwing module is skipped, not fatal. */
    load(pluginDir = dir) {
      for (const file of manager.discover(pluginDir)) {
        try { manager.register(require(file), file); }
        catch (err) {
          failed.set(file, String(err.message));
          errors?.record(err, { category: "plugin_load", subsystem: "plugins", context: { file } });
          logger?.warn?.("Plugins", `failed to load ${path.basename(path.dirname(file))}: ${err.message}`);
        }
      }
      return manager;
    },

    /** initialize() + collect commands/events, in dependency order. */
    async initializeAll(ctx) {
      const ordered = orderPlugins([...loaded.values()].map(e => e.manifest));
      const commands = {}, events = {};
      for (const manifest of ordered) {
        const entry = loaded.get(manifest.name);
        const t0 = process.hrtime.bigint();
        try {
          const instance = manifest.create(ctx) ?? {};
          entry.instance = instance;
          if (instance.initialize) await instance.initialize();
          if (instance.registerCommands) Object.assign(commands, instance.registerCommands() ?? {});
          if (instance.registerEvents) {
            for (const [evt, fn] of Object.entries(instance.registerEvents() ?? {})) {
              (events[evt] ||= []).push({ plugin: manifest.name, fn });
            }
          }
          entry.state = "initialized";
          entry.loadMs = Number(process.hrtime.bigint() - t0) / 1e6;
          metrics?.observe("plugin_load_duration_ms", entry.loadMs, { plugin: manifest.name }, "Plugin initialisation time");
        } catch (err) {
          entry.state = "failed";
          failed.set(manifest.name, String(err.message));
          errors?.record(err, { category: "plugin_init", subsystem: manifest.name });
          logger?.warn?.("Plugins", `${manifest.name} failed to initialise - continuing without it: ${err.message}`);
        }
      }
      return { commands, events };
    },

    async startAll() {
      for (const manifest of orderPlugins([...loaded.values()].map(e => e.manifest))) {
        const entry = loaded.get(manifest.name);
        if (entry.state !== "initialized") continue;
        try {
          if (entry.instance.start) await entry.instance.start();
          entry.state = "started";
          metrics?.gauge("plugin_up", 1, { plugin: manifest.name }, "1 when a plugin is started");
        } catch (err) {
          entry.state = "failed";
          errors?.record(err, { category: "plugin_start", subsystem: manifest.name });
          metrics?.gauge("plugin_up", 0, { plugin: manifest.name });
        }
      }
      logger?.info?.("Plugins", `${manager.started().length}/${loaded.size} plugin(s) started`);
    },

    /** Reverse order, and every stop() is attempted even if an earlier one throws -
        a leaked handle in one plugin must not strand the rest. */
    async stopAll() {
      const ordered = orderPlugins([...loaded.values()].map(e => e.manifest)).reverse();
      for (const manifest of ordered) {
        const entry = loaded.get(manifest.name);
        if (!entry?.instance?.stop) continue;
        try { await entry.instance.stop(); entry.state = "stopped"; }
        catch (err) { errors?.record(err, { category: "plugin_stop", subsystem: manifest.name }); }
        metrics?.gauge("plugin_up", 0, { plugin: manifest.name });
      }
    },

    list() {
      return [...loaded.values()].map(e => ({
        name: e.manifest.name,
        version: e.manifest.version ?? "0.0.0",
        description: e.manifest.description ?? null,
        dependsOn: e.manifest.dependsOn ?? [],
        state: e.state,
        loadMs: Number(e.loadMs.toFixed?.(2) ?? e.loadMs),
      }));
    },
    started() { return [...loaded.values()].filter(e => e.state === "started"); },
    get(name) { return loaded.get(name) ?? null; },
    failures() { return Object.fromEntries(failed); },
    count() { return loaded.size; },
  };
  return manager;
}

module.exports = { createPluginManager, orderPlugins, validateManifest };
