//! One Discord account, one in-game name, forever (C# `Core/Moderation/MenuLink.cs`).
//!
//! Stops menu access being cycled onto alt accounts. A member binds to exactly one in-game name
//! the first time they claim a menu, and THE BINDING OUTLIVES THE MENU: releasing the menu frees
//! the access, never the name. Only an admin can break a binding (`/unlinkname`).

/// What a claim should do.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MenuClaimAction {
    /// Proceed with the grant.
    Grant,
    /// This Discord account is bound to a different in-game name. Refuse.
    Locked,
    /// That in-game name belongs to a different Discord account. Refuse.
    Taken,
    /// They re-entered their own name while holding a menu: strip it.
    Release,
}

/// The decision, with the detail each refusal reports.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MenuClaimDecision {
    pub action: MenuClaimAction,
    /// The name they are bound to, on [`MenuClaimAction::Locked`].
    pub bound_name: Option<String>,
    /// The account that owns the name, on [`MenuClaimAction::Taken`].
    pub owner_id: Option<String>,
}

impl MenuClaimDecision {
    fn plain(action: MenuClaimAction) -> Self {
        Self {
            action,
            bound_name: None,
            owner_id: None,
        }
    }
}

/// Namespace for the rule, mirroring the C# static class.
#[derive(Debug)]
pub struct MenuLink;

impl MenuLink {
    /// Trimmed and lower-cased, the form names are compared in.
    #[must_use]
    pub fn normalise(value: Option<&str>) -> String {
        value.unwrap_or("").trim().to_lowercase()
    }

    /// Decide a claim.
    ///
    /// * `bound_name`: the name this account is already bound to.
    /// * `requested_name`: the name they just typed.
    /// * `owner_id`: the Discord id already bound to `requested_name`.
    /// * `holds_menu`: whether they currently hold a granted menu.
    #[must_use]
    pub fn decide(
        bound_name: Option<&str>,
        requested_name: Option<&str>,
        owner_id: Option<&str>,
        self_id: &str,
        holds_menu: bool,
    ) -> MenuClaimDecision {
        let want = Self::normalise(requested_name);
        let bound = Self::normalise(bound_name);

        /* CHECKED FIRST, and the order is the whole rule. It must hold with no menu held, which
        is precisely the state somebody would engineer (release, then re-claim as an alt). */
        if !bound.is_empty() && want != bound {
            return MenuClaimDecision {
                action: MenuClaimAction::Locked,
                bound_name: bound_name.map(str::to_owned),
                owner_id: None,
            };
        }

        // The reverse direction: two Discord accounts must not converge on one in-game name.
        if let Some(owner) = owner_id {
            if owner != self_id {
                return MenuClaimDecision {
                    action: MenuClaimAction::Taken,
                    bound_name: None,
                    owner_id: Some(owner.to_owned()),
                };
            }
        }

        if holds_menu && !bound.is_empty() && want == bound {
            return MenuClaimDecision::plain(MenuClaimAction::Release);
        }
        MenuClaimDecision::plain(MenuClaimAction::Grant)
    }
}
