// ModAction is declared in PavlovBot.Host.Discord (Boards.cs). It is a moderation domain
// record living in a presentation file, which is worth moving - but the type is persisted
// under Datasets.ModLog and shared with the Node bot, so it is left alone here rather than
// bundled into an unrelated change.
using PavlovBot.Host.Discord;

namespace PavlovBot.Host.Moderation;

/// <summary>
/// Somewhere a staff action is published as it happens.
/// </summary>
/// <remarks>
/// AN INTERFACE BECAUSE OF A DEPENDENCY CYCLE, not for its own sake. Posting to a channel by
/// id needs the gateway; the gateway is constructed from every <c>ISlashCommand</c>; and
/// nearly every command needs <see cref="AuditLog"/>. Injecting a channel poster into the
/// audit log directly closes that loop and the container cannot resolve it - which the
/// <c>--selftest</c> gate catches at deploy time, but only after somebody has written it.
///
/// So the audit log states what it needs and the implementation is attached afterwards, in
/// the composition root, once the gateway exists. The audit log keeps working with no sink
/// at all, which is what every test and every unconfigured deployment does.
/// </remarks>
public interface IStaffLogSink
{
    /// <summary>
    /// Publish one action. MUST NOT THROW.
    /// </summary>
    /// <remarks>
    /// The caller has already written the audit record by this point and cannot un-write it.
    /// A sink that throws would turn a cosmetic delivery problem into a lost ban.
    /// </remarks>
    Task PostAsync(ModAction action, CancellationToken ct = default);
}
