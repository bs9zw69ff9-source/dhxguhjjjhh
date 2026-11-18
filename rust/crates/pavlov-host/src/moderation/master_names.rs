//! The names automation never acts on (C# `Moderation/MasterNames.cs`): the master owners,
//! the never-ban list, a master owner's pardons, and the short exemption after a lift.

use std::sync::Arc;
use std::time::Duration;

use chrono::TimeDelta;
use futures_util::future::BoxFuture;
use pavlov_core::data::{SerializedStore, StoreError};
use pavlov_core::json::Stamp;
use pavlov_core::security::{OwnerGuard, OwnerGuardError};
use pavlov_core::text::case::{IgnoreCaseMap, IgnoreCaseSet, eq_ignore_case};
use pavlov_core::time::Clock;
use pavlov_storage::datasets::{AUTOBAN_EXEMPT, MASTER_PARDONS, NEVER_BAN};

use super::bans::MasterNames;

const DEFAULT_EXEMPTION: Duration = Duration::from_secs(60 * 60);

type Stamps = IgnoreCaseMap<Stamp>;

/// The stored lists.
pub struct StoredMasterNames {
    masters: IgnoreCaseSet,
    store: Arc<SerializedStore>,
    clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for StoredMasterNames {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StoredMasterNames")
            .field("masters", &self.masters)
            .finish_non_exhaustive()
    }
}

impl StoredMasterNames {
    /// The configured masters with the built-in one unioned in and asserted: losing it would
    /// let the bot's own auto-ban ban the owner off their own server.
    ///
    /// # Errors
    /// When the built-in identity cannot be verified.
    pub fn new<'a>(
        master_names: impl IntoIterator<Item = &'a str>,
        store: Arc<SerializedStore>,
        clock: Arc<dyn Clock>,
    ) -> Result<Self, OwnerGuardError> {
        Ok(Self {
            masters: OwnerGuard::with_built_in_masters(master_names)?,
            store,
            clock,
        })
    }

    #[must_use]
    pub fn masters(&self) -> &IgnoreCaseSet {
        &self.masters
    }

    fn read(&self, key: &str) -> Stamps {
        self.store.read(key, Stamps::default()).unwrap_or_else(|error| {
            tracing::warn!(dataset = key, %error, "could not read a protection list - treating it as empty");
            Stamps::default()
        })
    }

    async fn change(
        &self,
        key: &str,
        mutate: impl FnOnce(Stamps) -> Option<Stamps>,
    ) -> Result<bool, StoreError> {
        Ok(self.store.update(key, Stamps::default, mutate).await?.ok)
    }

    #[must_use]
    pub fn pardons(&self) -> Stamps {
        self.read(MASTER_PARDONS)
    }

    #[must_use]
    pub fn is_pardoned(&self, name: &str) -> bool {
        self.pardons().contains_key(name.trim())
    }

    /// A master owner's unban: protected from every automated path until banned again.
    ///
    /// # Errors
    /// When the store fails.
    pub async fn pardon(&self, name: &str) -> Result<bool, StoreError> {
        let now = self.clock.now();
        self.change(MASTER_PARDONS, |mut p| {
            p.insert(name.trim(), Stamp(now));
            Some(p)
        })
        .await
    }

    /// # Errors
    /// When the store fails.
    pub async fn clear_pardon(&self, name: &str) -> Result<bool, StoreError> {
        self.change(MASTER_PARDONS, |mut p| p.remove(name.trim()).map(|_| p))
            .await
    }

    /// The never-ban list.
    #[must_use]
    pub fn protected(&self) -> Stamps {
        self.read(NEVER_BAN)
    }

    /// # Errors
    /// When the store fails.
    pub async fn protect(&self, name: &str) -> Result<bool, StoreError> {
        let now = self.clock.now();
        self.change(NEVER_BAN, |mut p| {
            p.insert(name.trim(), Stamp(now));
            Some(p)
        })
        .await
    }

    /// # Errors
    /// When the store fails.
    pub async fn unprotect(&self, name: &str) -> Result<bool, StoreError> {
        self.change(NEVER_BAN, |mut p| {
            p.remove(name.trim());
            Some(p)
        })
        .await
    }

    /// # Errors
    /// When the store fails.
    pub async fn remove_exemption(&self, name: &str) -> Result<bool, StoreError> {
        self.change(AUTOBAN_EXEMPT, |mut e| {
            e.remove(name.trim());
            Some(e)
        })
        .await
    }
}

impl MasterNames for StoredMasterNames {
    fn is_master(&self, name: &str) -> bool {
        let trimmed = name.trim();
        // At the point of use too: emptying the set is not enough to unprotect the account.
        eq_ignore_case(trimmed, OwnerGuard::MASTER_NAME) || self.masters.contains(trimmed)
    }

    fn is_exempt(&self, name: &str) -> bool {
        self.read(AUTOBAN_EXEMPT)
            .get(name.trim())
            .is_some_and(|until| until.0 > self.clock.now())
    }

    fn is_protected(&self, name: &str) -> bool {
        self.protected().contains_key(name.trim()) || self.is_pardoned(name)
    }

    fn exempt<'a>(&'a self, name: &'a str, duration: Option<Duration>) -> BoxFuture<'a, ()> {
        Box::pin(async move {
            let span = TimeDelta::from_std(duration.unwrap_or(DEFAULT_EXEMPTION))
                .unwrap_or(TimeDelta::hours(1));
            let until = self.clock.now() + span;
            if let Err(error) = self
                .change(AUTOBAN_EXEMPT, |mut e| {
                    e.insert(name.trim(), Stamp(until));
                    Some(e)
                })
                .await
            {
                tracing::error!(name, %error, "could not record the auto-ban exemption");
            }
        })
    }
}
