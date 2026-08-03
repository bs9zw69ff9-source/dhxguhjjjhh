using Discord;

namespace PavlovBot.Tests;

/// <summary>
/// The two Discord user shapes <see cref="PavlovBot.Host.Discord.Access"/> has to tell apart.
/// </summary>
/// <remarks>
/// <see cref="FakeUser"/> is somebody whose GUILD MEMBERSHIP DID NOT RESOLVE - a DM, an
/// uncached guild, a user-installed app context. That state is the whole reason these exist:
/// it used to make every role lookup answer false, which read as "holds nothing" and refused
/// an owner a moderator command.
///
/// Everything the access checks do not read throws rather than returning a default. A fake
/// that quietly answers null for an unimplemented member turns "the test exercises this" into
/// something you have to verify by reading; a throw fails loudly the moment a check starts
/// depending on something this does not model.
/// </remarks>
internal class FakeUser(ulong id) : IUser
{
    public ulong Id { get; } = id;

    public string Username => "fake";

    // ---- not modelled ----

    string IUser.Discriminator => throw new NotSupportedException();
    ushort IUser.DiscriminatorValue => throw new NotSupportedException();
    string IUser.AvatarId => throw new NotSupportedException();
    string? IUser.GlobalName => throw new NotSupportedException();
    string? IUser.AvatarDecorationHash => throw new NotSupportedException();
    ulong? IUser.AvatarDecorationSkuId => throw new NotSupportedException();
    bool IUser.IsBot => false;
    bool IUser.IsWebhook => false;
    UserProperties? IUser.PublicFlags => throw new NotSupportedException();
    PrimaryGuild? IUser.PrimaryGuild => throw new NotSupportedException();
    DateTimeOffset ISnowflakeEntity.CreatedAt => throw new NotSupportedException();
    string IMentionable.Mention => throw new NotSupportedException();

    UserStatus IPresence.Status => throw new NotSupportedException();
    IReadOnlyCollection<ClientType> IPresence.ActiveClients => throw new NotSupportedException();
    IReadOnlyCollection<IActivity> IPresence.Activities => throw new NotSupportedException();

    Task<IDMChannel> IUser.CreateDMChannelAsync(RequestOptions? options) => throw new NotSupportedException();
    string IUser.GetAvatarUrl(ImageFormat format, ushort size) => throw new NotSupportedException();
    string IUser.GetDefaultAvatarUrl() => throw new NotSupportedException();
    string IUser.GetDisplayAvatarUrl(ImageFormat format, ushort size) => throw new NotSupportedException();
    string IUser.GetAvatarDecorationUrl() => throw new NotSupportedException();
}

/// <summary>A user whose guild membership DID resolve, with a role list and permissions.</summary>
internal sealed class FakeMember(ulong id, params ulong[] roles) : FakeUser(id), IGuildUser
{
    /// <summary>Discord's own Administrator permission, which the access checks honour.</summary>
    public bool Administrator { get; init; }

    public IReadOnlyCollection<ulong> RoleIds { get; } = roles;

    public GuildPermissions GuildPermissions => new(administrator: Administrator);

    // ---- not modelled ----

    IGuild IGuildUser.Guild => throw new NotSupportedException();
    ulong IGuildUser.GuildId => throw new NotSupportedException();
    string? IGuildUser.DisplayName => throw new NotSupportedException();
    string? IGuildUser.Nickname => throw new NotSupportedException();
    string? IGuildUser.DisplayAvatarId => throw new NotSupportedException();
    string? IGuildUser.GuildAvatarId => throw new NotSupportedException();
    string? IGuildUser.GuildBannerHash => throw new NotSupportedException();
    GuildUserFlags IGuildUser.Flags => throw new NotSupportedException();
    int IGuildUser.Hierarchy => throw new NotSupportedException();
    bool? IGuildUser.IsPending => throw new NotSupportedException();
    DateTimeOffset? IGuildUser.JoinedAt => throw new NotSupportedException();
    DateTimeOffset? IGuildUser.PremiumSince => throw new NotSupportedException();
    DateTimeOffset? IGuildUser.TimedOutUntil => throw new NotSupportedException();

    bool IVoiceState.IsDeafened => throw new NotSupportedException();
    bool IVoiceState.IsMuted => throw new NotSupportedException();
    bool IVoiceState.IsSelfDeafened => throw new NotSupportedException();
    bool IVoiceState.IsSelfMuted => throw new NotSupportedException();
    bool IVoiceState.IsSuppressed => throw new NotSupportedException();
    bool IVoiceState.IsStreaming => throw new NotSupportedException();
    bool IVoiceState.IsVideoing => throw new NotSupportedException();
    DateTimeOffset? IVoiceState.RequestToSpeakTimestamp => throw new NotSupportedException();
    IVoiceChannel IVoiceState.VoiceChannel => throw new NotSupportedException();
    ulong? IVoiceState.VoiceChannelId => throw new NotSupportedException();
    string IVoiceState.VoiceSessionId => throw new NotSupportedException();

    ChannelPermissions IGuildUser.GetPermissions(IGuildChannel channel) => throw new NotSupportedException();
    string IGuildUser.GetGuildAvatarUrl(ImageFormat format, ushort size) => throw new NotSupportedException();
    string IGuildUser.GetGuildBannerUrl(ImageFormat format, ushort size) => throw new NotSupportedException();

    Task IGuildUser.AddRoleAsync(ulong roleId, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.AddRoleAsync(IRole role, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.AddRolesAsync(IEnumerable<ulong> roleIds, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.AddRolesAsync(IEnumerable<IRole> roles, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.RemoveRoleAsync(ulong roleId, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.RemoveRoleAsync(IRole role, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.RemoveRolesAsync(IEnumerable<ulong> roleIds, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.RemoveRolesAsync(IEnumerable<IRole> roles, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.KickAsync(string? reason, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.ModifyAsync(Action<GuildUserProperties> func, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.SetTimeOutAsync(TimeSpan span, RequestOptions? options) => throw new NotSupportedException();
    Task IGuildUser.RemoveTimeOutAsync(RequestOptions? options) => throw new NotSupportedException();
}
