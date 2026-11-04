//! The services plugins offer one another ([`ac_plugin_abi::services`]):
//! one entry per scoped id (`<plugin id>/<name>`, compared ignoring case),
//! listed by id, released when the plugin unloads.
//!
//! Every offer and withdrawal is noted as a change for the session to tell
//! the plugins ([`ServiceRegistry::take_changes`]). What the plugins have
//! been told so far ([`ServiceRegistry::told`]) is what a plugin that starts
//! is told first: the changes not yet told follow, so it ends up knowing
//! exactly what is on offer, each change once.

use ac_plugin_abi::services::{
    MAX_DESCRIPTION, MAX_SERVICES, ServiceChange, ServiceError, ServiceInfo, ServiceRegistration,
    is_valid_name,
};

#[derive(Debug, Clone)]
struct Entry {
    info: ServiceInfo,
    handle: u32,
}

/// The session's services.
#[derive(Debug, Default)]
pub struct ServiceRegistry {
    entries: Vec<Entry>,
    next_handle: u32,
    /// Offers and withdrawals not yet handed to the session.
    changes: Vec<ServiceChange>,
    /// The services the plugins have been told are on offer.
    told: Vec<ServiceChange>,
}

fn same(a: &str, b: &str) -> bool {
    a.eq_ignore_ascii_case(b)
}

impl ServiceRegistry {
    /// Plugin `owner` offers a service; its handle for it.
    ///
    /// # Errors
    /// The name is not a service name, the description is longer than
    /// [`MAX_DESCRIPTION`], `owner` already offers one by that name, or it
    /// offers [`MAX_SERVICES`] already.
    pub fn register(
        &mut self,
        owner: &str,
        registration: &ServiceRegistration,
    ) -> Result<u32, ServiceError> {
        if !is_valid_name(&registration.name) {
            return Err(ServiceError::InvalidName);
        }
        if registration.description.len() > MAX_DESCRIPTION {
            return Err(ServiceError::DescriptionTooLarge(
                u32::try_from(MAX_DESCRIPTION).unwrap_or(u32::MAX),
            ));
        }
        let id = format!("{owner}/{}", registration.name);
        if self.find_entry(&id).is_some() {
            return Err(ServiceError::AlreadyRegistered);
        }
        if self
            .entries
            .iter()
            .filter(|e| same(&e.info.owner, owner))
            .count()
            >= MAX_SERVICES
        {
            return Err(ServiceError::TooMany(
                u32::try_from(MAX_SERVICES).unwrap_or(u32::MAX),
            ));
        }
        self.next_handle += 1;
        let info = ServiceInfo {
            id,
            owner: owner.to_owned(),
            description: registration.description.clone(),
        };
        self.changes.push(change(&info, true));
        self.entries.push(Entry {
            info,
            handle: self.next_handle,
        });
        Ok(self.next_handle)
    }

    /// Plugin `owner` withdraws one of its services; false for a handle it
    /// does not hold.
    pub fn unregister(&mut self, owner: &str, handle: u32) -> bool {
        let Some(at) = self
            .entries
            .iter()
            .position(|e| e.handle == handle && same(&e.info.owner, owner))
        else {
            return false;
        };
        let entry = self.entries.remove(at);
        self.changes.push(change(&entry.info, false));
        true
    }

    /// Everything plugin `owner` offered goes.
    pub fn release(&mut self, owner: &str) {
        let (gone, kept) = std::mem::take(&mut self.entries)
            .into_iter()
            .partition(|e| same(&e.info.owner, owner));
        self.entries = kept;
        let gone: Vec<Entry> = gone;
        self.changes
            .extend(gone.iter().map(|e| change(&e.info, false)));
    }

    /// Every service on offer, by id (ignoring case).
    #[must_use]
    pub fn available(&self) -> Vec<ServiceInfo> {
        let mut list: Vec<ServiceInfo> = self.entries.iter().map(|e| e.info.clone()).collect();
        list.sort_by_key(|info| info.id.to_lowercase());
        list
    }

    fn find_entry(&self, id: &str) -> Option<&Entry> {
        self.entries.iter().find(|e| same(&e.info.id, id.trim()))
    }

    /// The plugin and handle answering scoped id `id`.
    #[must_use]
    pub fn find(&self, id: &str) -> Option<(String, u32)> {
        self.find_entry(id)
            .map(|e| (e.info.owner.clone(), e.handle))
    }

    /// The offers and withdrawals since the last take, oldest first, for
    /// the session to tell the plugins.
    pub fn take_changes(&mut self) -> Vec<ServiceChange> {
        std::mem::take(&mut self.changes)
    }

    /// The plugins were told `change`.
    pub fn told_of(&mut self, change: &ServiceChange) {
        self.told.retain(|c| !same(&c.id, &change.id));
        if change.available {
            self.told.push(change.clone());
        }
    }

    /// The services the plugins have been told are on offer, in the order
    /// they were told: what a plugin that starts hears first.
    #[must_use]
    pub fn told(&self) -> &[ServiceChange] {
        &self.told
    }
}

fn change(info: &ServiceInfo, available: bool) -> ServiceChange {
    ServiceChange {
        id: info.id.clone(),
        owner: info.owner.clone(),
        available,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn offer(name: &str) -> ServiceRegistration {
        ServiceRegistration {
            name: name.to_owned(),
            description: format!("does {name}"),
        }
    }

    #[test]
    fn a_service_is_found_ignoring_case_and_goes_when_withdrawn() {
        let mut registry = ServiceRegistry::default();
        let handle = registry.register("openac.nav", &offer("path")).unwrap();
        assert_eq!(
            registry.find("OPENAC.NAV/path"),
            Some(("openac.nav".to_owned(), handle))
        );
        assert_eq!(
            registry.register("openac.nav", &offer("path")),
            Err(ServiceError::AlreadyRegistered)
        );
        assert!(!registry.unregister("openac.vision", handle));
        assert!(registry.unregister("openac.nav", handle));
        assert!(!registry.unregister("openac.nav", handle));
        assert_eq!(registry.find("openac.nav/path"), None);
        let changes: Vec<(String, bool)> = registry
            .take_changes()
            .into_iter()
            .map(|c| (c.id, c.available))
            .collect();
        assert_eq!(
            changes,
            [
                ("openac.nav/path".to_owned(), true),
                ("openac.nav/path".to_owned(), false)
            ]
        );
        assert!(registry.take_changes().is_empty());
    }

    #[test]
    fn names_and_counts_are_limited() {
        let mut registry = ServiceRegistry::default();
        for bad in ["", "Path", "a/b", &"x".repeat(65)] {
            assert_eq!(
                registry.register("p", &offer(bad)),
                Err(ServiceError::InvalidName),
                "{bad}"
            );
        }
        for n in 0..MAX_SERVICES {
            registry.register("p", &offer(&format!("s{n}"))).unwrap();
        }
        assert_eq!(
            registry.register("p", &offer("one-more")),
            Err(ServiceError::TooMany(32))
        );
        // The limit is per plugin.
        assert!(registry.register("q", &offer("one-more")).is_ok());
    }

    #[test]
    fn descriptions_are_limited() {
        let mut registry = ServiceRegistry::default();
        let described = |name: &str, len: usize| ServiceRegistration {
            name: name.to_owned(),
            description: "d".repeat(len),
        };
        assert!(
            registry
                .register("p", &described("at", MAX_DESCRIPTION))
                .is_ok()
        );
        assert_eq!(
            registry.register("p", &described("over", MAX_DESCRIPTION + 1)),
            Err(ServiceError::DescriptionTooLarge(1024))
        );
        assert_eq!(registry.find("p/over"), None);
    }

    #[test]
    fn services_list_by_id_and_go_with_their_plugin() {
        let mut registry = ServiceRegistry::default();
        registry.register("b.plugin", &offer("x")).unwrap();
        registry.register("a.plugin", &offer("z")).unwrap();
        // The same plugin, its id cased another way.
        registry.register("A.plugin", &offer("y")).unwrap();
        let ids: Vec<String> = registry.available().into_iter().map(|i| i.id).collect();
        assert_eq!(ids, ["A.plugin/y", "a.plugin/z", "b.plugin/x"]);
        registry.take_changes();
        registry.release("A.PLUGIN");
        let ids: Vec<String> = registry.available().into_iter().map(|i| i.id).collect();
        assert_eq!(ids, ["b.plugin/x"]);
        let gone: Vec<(String, bool)> = registry
            .take_changes()
            .into_iter()
            .map(|c| (c.id, c.available))
            .collect();
        assert_eq!(
            gone,
            [
                ("a.plugin/z".to_owned(), false),
                ("A.plugin/y".to_owned(), false)
            ]
        );
    }

    #[test]
    fn what_the_plugins_were_told_follows_the_changes_told() {
        let mut registry = ServiceRegistry::default();
        let nav = registry.register("nav", &offer("path")).unwrap();
        registry.register("vision", &offer("see")).unwrap();
        // Nothing is told until the session tells it.
        assert!(registry.told().is_empty());
        for change in registry.take_changes() {
            registry.told_of(&change);
        }
        assert_eq!(
            registry
                .told()
                .iter()
                .map(|c| c.id.as_str())
                .collect::<Vec<_>>(),
            ["nav/path", "vision/see"]
        );
        registry.unregister("nav", nav);
        assert_eq!(registry.told().len(), 2);
        for change in registry.take_changes() {
            registry.told_of(&change);
        }
        assert_eq!(
            registry
                .told()
                .iter()
                .map(|c| c.id.as_str())
                .collect::<Vec<_>>(),
            ["vision/see"]
        );
    }
}
