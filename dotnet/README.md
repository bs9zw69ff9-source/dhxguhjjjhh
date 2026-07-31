# PavlovBot (.NET port)

Staged port of the Node bot to .NET 9. **The Node bot in the repository root is still
the production one** - nothing here is deployed yet.

## Why the port

Two goals drove it: smaller memory/startup footprint, and a single self-contained
deployable. A third benefit showed up on contact: a large share of the bugs found while
hardening the Node bot were **tri-state bugs**, where `null` ("nobody could answer") was
handled as `false` ("the answer is no"). `<Nullable>enable</Nullable>` with
`TreatWarningsAsErrors` turns most of that class into compile failures.

## Layout

```
src/PavlovBot.Core    pure domain logic - no Discord, no sockets, no filesystem
src/PavlovBot.Rcon    Pavlov RCON client
src/PavlovBot.Host    composition root, configuration, observability, Discord
tests/PavlovBot.Tests xUnit
tools/DiffCheck       differential harness: replays cases through both implementations
```

`Core` and `Rcon` are marked `IsAotCompatible` so the parts that can be trimmed are kept
honest as the port grows.

## Porting method

Each module goes across in this order, and does not count as ported until the last step
passes:

1. **Port the tests first.** They encode behaviour learned the hard way - `Ban` takes a
   UniqueId not a name, a bare `SetPin` clears the pin, a lone shared ASN must never
   report. That knowledge is the asset; the code is replaceable.
2. Port the implementation until xUnit is green.
3. **Differential-test against the running JS.** Passing the new tests only proves the
   port is self-consistent. `tools/DiffCheck` replays identical inputs through both and
   compares verdicts, which is what proves it agrees with production.

## Status

| Module | State |
|---|---|
| `Evasion` (ban-evasion scoring) | ported - 15 tests, 12/12 differential cases agree |
| `Rcon` (client, coalescing, audit) | ported - 13 tests against a fake Pavlov listener |
| `Data.SerializedStore` | ported - 8 tests |
| `Data.RosterWriteGuard` | ported - 10 tests, 8/8 differential cases agree |
| `Factions` (registry + membership rules) | ported - 24 tests, 3/3 factions and 52/52 rank scenarios agree |
| `Penal` (59 charges + booking maths) | ported - 16 tests, 331/331 differential scenarios agree |
| `Host` (config, DI, services, monitoring, gateway) | ported - 39/39 `.env` lines parse identically |
| `Text` / `Time` / `StaffHierarchy` / `MenuLink` / `Verification` | ported |
| `PeakTracker`, `Ledger` | ported |
| storage (SQLite + dataset registry + JSON export) | ported |
| ban rules and enforcement (sweep, reconcile, expiry) | ported |
| VPN screening (6 detectors, two-tier consensus) | ported |
| log pipeline (parsing, correlation, tailing, IP tracking) | ported |
| roster service + faction commands | ported |
| feeds, money log, auto-posting boards | ported |
| boards (playtime, most wanted, player list, staff, dashboard) | ported |
| menu grants + the permanent name binding | ported |
| autocomplete, role config, donators, rank suspensions | ported |
| modsave ban-list import/export | ported |
| plugin system | ported |
| commands | 25 |

Total: **463 xUnit tests**, and **442 differential scenarios** agreeing with the running
JS across evasion, roster writes, factions, the penal code and `.env` parsing.

### RCON: what changed versus Node

The Node client opened a fresh TCP connection and MD5 handshake **per command**, which
the Pavlov RCON reference explicitly advises against - Pavlov services RCON on the game
thread, so every connection is work taken from the tick.

Measured over 10 cycles of the bot's real pattern (player list, dashboard, leaderboards,
ban sweep, health check and cache refresh all asking within the same second):

| | connections opened |
|---|---|
| Node, before read coalescing | ~6 per cycle |
| Node, after read coalescing | ~2 per cycle |
| .NET, persistent session | **1 in total** |

Three behaviours carried across from the Node work, all covered by tests: a mutation is
never cached and clears the cached roster; a failure is never cached as an answer; retry
backoff uses full jitter. Two are new: the session is reused and rebuilt transparently
when dropped, and commands are paced 100ms apart as the docs advise.

## Running

```bash
dotnet test

# The host. --selftest builds the whole object graph, reports what it cost and exits
# without connecting to anything: a deploy smoke test that proves the configuration
# parses and every dependency resolves.
dotnet run --project src/PavlovBot.Host -- --selftest
dotnet run --project src/PavlovBot.Host

# Differential stages are selected by flag and are independent of each other.
dotnet run --project tools/DiffCheck -- --evasion cases.json js-reference.json
dotnet run --project tools/DiffCheck -- --dotenv dotenv-cases.json
```

## Data layer notes

**SerializedStore** is the read-modify-write queue. Every mutation the bot makes has that
shape - load the ban list, add one, save it - and running two concurrently against one
dataset means the second silently overwrites the first. That surfaces days later as a ban
that "didn't take". Locking is per key, so unrelated datasets never wait on each other.

Two properties carried across deliberately:

- **A failed update must not poison the queue.** In JS the promise chain was kept alive
  with a trailing catch. Here the semaphore is released in a `finally`, so one thrown
  mutator cannot wedge every later write to that dataset.
- **Reads are isolated.** JS needed an explicit `structuredClone` per read; here it falls
  out of deserialising per read, so a caller mutating what it read cannot corrupt shared
  state.

A mutator returning `null` is a **veto**, not an error - it is how "insufficient funds"
or "already present" is expressed, and nothing is written.

**RosterWriteGuard** refuses a write that would delete more than 5 existing entries. The
reasoning matters more than the number: an empty roster file is perfectly VALID to Pavlov,
so a parsing bug producing an empty list would silently strip a faction mid-round with no
error anywhere. The check is therefore on the *size of the change*, not the shape of the
data - a write removing twenty entries is not a big operation, it is a bug that already
happened. "Cannot read the current file" is treated as its own refusal rather than as
"empty", because conflating those turns a transient permission error into a wipe.

## Faction notes

Ranks are a **list**, lowest to highest, so promotion is "move one index up" and demotion
"move one index down" - one piece of logic walking a shape rather than a method per rank.
Adding a rank is a line of data.

The membership rules exist because **the storage cannot express them**. Rosters are plain
text the game reads live; nothing stops a name appearing in six files at once. So one
faction per player, one rank, at most one sub-class, and the per-rank caps are all
enforced at the boundary before anything is written, and all of it is pure - current
membership is passed in, so every rule tests without a filesystem.

Two behaviours worth keeping in mind when reading it:

- **A demotion into a full rank is refused too.** Overflow is overflow regardless of
  direction; checking only the promote path is a real bug in this shape of code.
- **An unrecognised current rank is treated as the lowest**, not rejected. A member can
  end up off-ladder if a rank was renamed underneath them, and letting a promotion fix
  that is more useful than stranding them.

## Penal code notes

The 59-row charge table was **generated from the running JS**, not retyped. 59 rows of
bail figures typed by hand is 59 chances to fat-finger a number nobody notices until a
player is charged the wrong amount. The generator emitted the C# initialisers directly
from `penal/codes.js`.

`rate` - the multiplier behind `/bail increase|decrease` - is applied and rounded **per
charge**, never to the total. Otherwise a booking's bail would not equal the sum of the
figures shown for its individual charges, and a receipt whose lines do not add up is
worse than no receipt. The differential suite covers this explicitly: at rate 1.5,
`PC 100 + PC 102` is 38 + 23 = 61, not 60.

Two special cases behave differently on purpose:

- **Execution replaces** the sentence and the bail entirely, even when charged alongside
  ordinary offences. The underlying minute total is still computed and available.
- **Variable annotates** rather than replaces - "2 min + based on the associated charge".

## Host notes

The host is the composition root and the only place in the program that constructs
anything - the same discipline `index.js` arrived at the hard way, with a container
attached. Startup order is deliberate: configuration is **validated and the process exits**
before anything opens a socket, then monitoring comes up so `/health` is answering while
the rest is still starting, then background services, and the Discord gateway **last** so
the first interaction cannot arrive before the things it depends on exist.

Configuration reports **every** problem at once, not the first. Fixing a bot's environment
one crash at a time, with a restart between each, is how a five-minute deploy becomes an
hour.

### The .env parser, and why it is differentially tested

Both bots read one `.env` during a migration, so the parser matches dotenv 16's grammar
rather than inventing a cleaner one. A divergence here does not present as a parsing bug -
it presents as a wrong password or a truncated webhook URL.

The harness earned its place immediately: this port unescaped `\"` inside double quotes,
and dotenv does not. Only `\n` and `\r` are expanded. Nothing in the unit tests would have
caught it, and the symptom would have been a token that silently differed by one character.

The other rule worth knowing: an **unquoted** value ends at the first `#`. A value
containing a hash must be quoted, or dotenv truncates it - and so, deliberately, does this.

### Services: one deliberate difference from Node

Node used a timer plus a `busy` flag and counted the ticks it skipped. Here each service is
a loop over a `PeriodicTimer`, so re-entrancy is impossible **by construction** rather than
by a flag somebody has to remember to check. Missed ticks collapse into one instead of
being counted, so the skip counter is replaced by an **overrun** counter: ticks that took
longer than their own interval. That is the signal you actually alert on, and unlike a skip
count it cannot be wrong.

`runOnStart` stays **off by default**, and that default is load-bearing. Services start
before the gateway connects, so an immediate tick on anything that posts to Discord fires
with no connection - and re-fires on every supervisor restart. The Node bot shipped a
leaderboard regression exactly this way.

### Monitoring

`HttpListener`, not Kestrel. Four endpoints that return text do not justify pulling the
ASP.NET Core framework reference into a process whose whole reason for existing is a
smaller footprint. Loopback by default, optional bearer token, and disabled entirely
unless `METRICS_PORT` is set - no port, no listener, no attack surface.

`/healthz` stays open even when a token is configured: an orchestrator has to be able to
restart a bot whose configuration is broken, and a wrong metrics token is exactly that
case. And only **unhealthy** returns 503 - "degraded" must not take a bot out of a load
balancer because one optional detector is down.

One tri-state carried forward: `METRICS_PORT=0` is **set but unusable**, and is reported as
such. The Node bot read a configured 0 as "no port" through a truthiness check and the
metrics endpoint silently never came up.

## AOT: what actually happened

The port set out to publish with Native AOT. **It cannot, and the reason is Discord.Net.**

`Discord.Net.Rest` and `Discord.Net.WebSocket` both build their gateway and command models
by reflection and produce `IL2104` trim warnings, so neither trimming nor AOT can be
recommended without exercising a live gateway - and a reflection failure under trimming
does not show up at startup, it shows up the first time a particular payload arrives.

The constraint is contained rather than accepted everywhere: `PavlovBot.Core` and
`PavlovBot.Rcon` stay marked `IsAotCompatible`, and the RCON protocol layer parses with
`JsonDocument` rather than a serialiser specifically to keep them that way. Only the host
gives up AOT.

The shipping recommendation is therefore **self-contained + ReadyToRun, untrimmed**.

## Measured, honestly

Both sides measured at the same point - **entire object graph constructed, Discord client
created, nothing connected** - which is what `--selftest` exists for. Median of three runs
on the same machine.

| | Node (`index.js`, ~30 commands) | .NET host (25 commands) |
|---|---|---|
| construction | 333 ms | **~290 ms** |
| resident | 101.6 MB | **65.1 MB** |
| heap | 23.5 MB | **1.2 MB** |

Published size:

| | |
|---|---|
| self-contained + ReadyToRun | 90 MB |
| framework-dependent | 14 MB |
| trimmed (**not recommended**, see above) | 52 MB |
| Node: `node_modules` | 57 MB (plus a ~119 MB `node` binary) |

**This is still not a finished comparison.** The .NET host has 22 commands against roughly
thirty, and a few features behind them are not ported. But the earlier prediction now has
evidence: going from 1 command to 22, plus SQLite, the log pipeline, VPN screening, the
boards and the feeds, moved resident from 56.8 MB to 63.4 MB and the managed heap from
0.7 MB to 1.0 MB.
Feature count and memory are not tracking together, because the features are code, and code
lands in mapped R2R images rather than on the heap.

## What is deliberately NOT ported

Three things from the Node bot were left out on purpose rather than missed:

- **`/manual <command>`** - an arbitrary RCON passthrough. Every command it could send now
  has a typed equivalent that validates its arguments, and a passthrough is a way to send
  the unvalidated version of one.
- **`/firewall`** - the OS firewall is an owner-managed manual concern. Automating `ufw`
  from a bot means a false-positive ban can cut off a whole household or a shared NAT, and
  nothing in the bot would know to undo it.
- **RCON+ log auditing** - dropped at your request ("just forget rcon logs"). The line
  parser still recognises those lines; nothing consumes them.

Also not ported: `/setrconroles` (menu ROLE mapping - the grants themselves are done),
`/flush`, `/staffactivity` as a command (the board exists), and `/stats`.

## The claim this port can and cannot make

**Nothing here has run against a live Pavlov server or a real Discord gateway.** The whole
port is verified by 463 tests, 442 differential scenarios agreeing with the running JS, and
`--selftest` - which is a different and weaker claim than "it works".

**The Node bot in the repository root is still the production one.** Switching over needs a
staging server, a real gateway, and a period running both against the same `bot.db`.
