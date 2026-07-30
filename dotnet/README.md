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
src/PavlovBot.Host    composition root (not started)
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
| everything else | not started |

Total: **86 xUnit tests**, and **403 differential scenarios** agreeing with the running
JS across evasion, roster writes, factions and the penal code.

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
dotnet run --project tools/DiffCheck -- cases.json js-reference.json
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
