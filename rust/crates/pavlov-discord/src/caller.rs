//! Who is asking (C# `IUser` / `IGuildUser`), reduced to what the bot reads.

/// A caller's membership in one guild, as the interaction (or the staff guild) showed it.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct MemberView {
    pub guild_id: u64,
    pub role_ids: Vec<u64>,
    /// Discord's own Administrator permission in that guild.
    pub administrator: bool,
}

/// A caller. `member` is `None` when no guild membership could be resolved: a DM, an
/// uncached guild, or a user-installed app context.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Caller {
    pub id: u64,
    pub member: Option<MemberView>,
}

impl Caller {
    /// A bare user with no member.
    #[must_use]
    pub fn user(id: u64) -> Self {
        Self { id, member: None }
    }

    /// A member of `guild_id` holding `roles`.
    #[must_use]
    pub fn member(id: u64, guild_id: u64, roles: &[u64]) -> Self {
        Self {
            id,
            member: Some(MemberView {
                guild_id,
                role_ids: roles.to_vec(),
                administrator: false,
            }),
        }
    }
}
