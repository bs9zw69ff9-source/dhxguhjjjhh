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
| everything else | not started |

## Running

```bash
dotnet test
dotnet run --project tools/DiffCheck -- cases.json js-reference.json
```
