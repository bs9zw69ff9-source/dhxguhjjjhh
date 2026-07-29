/* ---------------- core/container: dependency injection ----------------
   The bot already injects dependencies - every module is `module.exports = (ctx) => {}`
   and index.js is the sole composition root, which is why nothing imports index.js and
   there are no circular requires. This formalises that pattern rather than replacing it:
   a container that resolves lazily, memoises singletons, and detects cycles by name
   instead of by stack overflow.

   `ctx()` produces the same flat object the existing modules already expect, so the two
   styles interoperate and migration can happen one module at a time. */
"use strict";

function createContainer({ logger = null } = {}) {
  const factories = new Map();   // name -> { factory, singleton }
  const instances = new Map();   // name -> resolved value
  const resolving = [];          // resolution path, for cycle reporting

  const container = {
    /** Register a lazily-constructed dependency. The factory receives the container. */
    register(name, factory, { singleton = true } = {}) {
      if (typeof factory !== "function") throw new Error(`factory for "${name}" must be a function`);
      factories.set(name, { factory, singleton });
      return container;
    },
    /** Register an already-built value (config objects, the discord client, ...). */
    value(name, val) { instances.set(name, val); return container; },

    has(name) { return instances.has(name) || factories.has(name); },

    resolve(name) {
      if (instances.has(name)) return instances.get(name);
      const entry = factories.get(name);
      if (!entry) throw new Error(`nothing registered for "${name}"${resolving.length ? ` (resolving ${resolving.join(" -> ")})` : ""}`);
      if (resolving.includes(name)) throw new Error(`circular dependency: ${[...resolving, name].join(" -> ")}`);
      resolving.push(name);
      try {
        const val = entry.factory(container);
        if (entry.singleton) instances.set(name, val);
        return val;
      } finally { resolving.pop(); }
    },

    /** Resolve several at once into a plain object. */
    pick(...names) {
      const out = {};
      for (const n of names.flat()) out[n] = container.resolve(n);
      return out;
    },

    /** Everything registered, as one flat object - the `ctx` shape existing modules take.
        Resolution failures are reported, not thrown: a broken optional dependency should
        not prevent the rest of the context from being built. */
    ctx(extra = {}) {
      const out = {};
      for (const name of container.names()) {
        try { out[name] = container.resolve(name); }
        catch (err) { logger?.warn?.("Container", `could not resolve "${name}": ${err.message}`); }
      }
      return Object.assign(out, extra);
    },

    names() { return [...new Set([...instances.keys(), ...factories.keys()])].sort(); },
    reset() { instances.clear(); factories.clear(); resolving.length = 0; },
  };
  return container;
}

module.exports = { createContainer };
