//! The loot classifiers plugins register for one another (OpenAC
//! `PluginLootClassifierRegistry`, scoped per plugin as OpenAC's scoped host
//! does): one entry per scoped id (`<plugin id>/<id>`, compared ignoring
//! case), listed by name then id, released when the plugin unloads.

use ac_plugin_abi::items::{ClassifierInfo, ClassifierRegistration};

#[derive(Debug, Clone)]
struct Entry {
    info: ClassifierInfo,
    owner: String,
    handle: u32,
}

/// The session's classifiers.
#[derive(Debug, Default)]
pub struct ClassifierRegistry {
    entries: Vec<Entry>,
    next_handle: u32,
}

impl ClassifierRegistry {
    /// Plugin `owner` registers a classifier; its handle for it.
    ///
    /// # Errors
    /// A blank id or name, or a scoped id already registered (OpenAC's
    /// message).
    pub fn register(
        &mut self,
        owner: &str,
        registration: &ClassifierRegistration,
    ) -> Result<u32, String> {
        let id = registration.id.trim();
        let name = registration.display_name.trim();
        if id.is_empty() {
            return Err("A loot classifier needs an id.".to_owned());
        }
        if name.is_empty() {
            return Err("A loot classifier needs a display name.".to_owned());
        }
        let scoped = format!("{owner}/{id}");
        if self.find_entry(&scoped).is_some() {
            return Err(format!("Loot classifier '{scoped}' is already registered."));
        }
        self.next_handle += 1;
        self.entries.push(Entry {
            info: ClassifierInfo {
                id: scoped,
                display_name: name.to_owned(),
            },
            owner: owner.to_owned(),
            handle: self.next_handle,
        });
        Ok(self.next_handle)
    }

    /// Plugin `owner` removes one of its classifiers.
    pub fn unregister(&mut self, owner: &str, handle: u32) -> bool {
        let before = self.entries.len();
        self.entries
            .retain(|e| !(e.handle == handle && e.owner.eq_ignore_ascii_case(owner)));
        self.entries.len() != before
    }

    /// Everything plugin `owner` registered goes.
    pub fn release(&mut self, owner: &str) {
        self.entries
            .retain(|e| !e.owner.eq_ignore_ascii_case(owner));
    }

    /// Every classifier, by name then id (ignoring case).
    #[must_use]
    pub fn available(&self) -> Vec<ClassifierInfo> {
        let mut list: Vec<ClassifierInfo> = self.entries.iter().map(|e| e.info.clone()).collect();
        list.sort_by(|a, b| {
            a.display_name
                .to_lowercase()
                .cmp(&b.display_name.to_lowercase())
                .then_with(|| a.id.to_lowercase().cmp(&b.id.to_lowercase()))
        });
        list
    }

    fn find_entry(&self, scoped_id: &str) -> Option<&Entry> {
        let wanted = scoped_id.trim();
        if wanted.is_empty() {
            return None;
        }
        self.entries
            .iter()
            .find(|e| e.info.id.eq_ignore_ascii_case(wanted))
    }

    /// The plugin and handle answering a scoped id.
    #[must_use]
    pub fn find(&self, scoped_id: &str) -> Option<(String, u32)> {
        self.find_entry(scoped_id)
            .map(|e| (e.owner.clone(), e.handle))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn registration(id: &str, name: &str) -> ClassifierRegistration {
        ClassifierRegistration {
            id: id.to_owned(),
            display_name: name.to_owned(),
        }
    }

    // OpenAC `PluginLootClassifierRegistryTests.
    // RegistrationClassifiesAndDisposalRemovesEntry`: the id is found
    // ignoring case and is gone once removed.
    #[test]
    fn registration_is_found_ignoring_case_and_removal_drops_it() {
        let mut registry = ClassifierRegistry::default();
        let handle = registry
            .register("test", &registration("rules", "Test Rules"))
            .unwrap();
        assert_eq!(registry.available()[0].id, "test/rules");
        assert_eq!(
            registry.find("TEST/RULES"),
            Some(("test".to_owned(), handle))
        );
        assert_eq!(
            registry.register("test", &registration(" rules ", "Again")),
            Err("Loot classifier 'test/rules' is already registered.".to_owned())
        );
        assert!(!registry.unregister("other", handle));
        assert!(registry.unregister("test", handle));
        assert!(registry.available().is_empty());
        assert_eq!(registry.find("test/rules"), None);
    }

    #[test]
    fn classifiers_list_by_name_and_go_with_their_plugin() {
        let mut registry = ClassifierRegistry::default();
        registry
            .register("b.plugin", &registration("x", "beta"))
            .unwrap();
        registry
            .register("a.plugin", &registration("y", "Alpha"))
            .unwrap();
        registry
            .register("a.plugin", &registration("z", "alpha"))
            .unwrap();
        let ids: Vec<String> = registry.available().into_iter().map(|i| i.id).collect();
        assert_eq!(ids, ["a.plugin/y", "a.plugin/z", "b.plugin/x"]);
        assert!(
            registry
                .register("a.plugin", &registration(" ", "n"))
                .is_err()
        );
        assert!(
            registry
                .register("a.plugin", &registration("n", ""))
                .is_err()
        );
        registry.release("A.PLUGIN");
        assert_eq!(registry.available().len(), 1);
    }
}
