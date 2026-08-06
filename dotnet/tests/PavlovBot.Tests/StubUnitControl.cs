using PavlovBot.Host.Servers;

namespace PavlovBot.Tests;

/// <summary>
/// A unit control with a fixed answer, so tests do not assert about the host.
/// </summary>
/// <remarks>
/// The alternative - a real <see cref="ServiceControl"/> pointed at units that do not exist -
/// answers Unknown on a box without systemd and Inactive on one with it. Tests written
/// against the first behaviour are green in a container and red on CI, for a difference that
/// is not a defect. This makes the answer part of the test rather than part of the machine.
/// </remarks>
internal sealed class StubUnitControl(IReadOnlyList<string> units, UnitState state) : IUnitControl
{
    public IReadOnlyList<string> Units { get; } = units;

    public Task<UnitState> StateAsync(string unit, CancellationToken ct = default) =>
        Task.FromResult(state);

    public Task<UnitResult> RunAsync(string unit, UnitAction action, CancellationToken ct = default) =>
        Task.FromResult(new UnitResult(true, unit, "stub"));
}
