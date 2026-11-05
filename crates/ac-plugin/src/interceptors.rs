//! The chat input interceptors plugins register: asked, in registration
//! order across every plugin, about each line the player sends (after the
//! client's own commands, before plugin verbs and the server), and the
//! first that does not pass decides (OpenAC `IPluginChat.
//! RegisterInputInterceptor`, `ChatCommandRouter`).

/// The most interceptors one plugin may have installed at once (OpenAC
/// `IPluginChat.MaximumInputInterceptors`).
pub const MAXIMUM_INTERCEPTORS_PER_PLUGIN: usize = 16;

/// How many times a line may be rewritten before the last text is sent as
/// it stands (OpenAC `ChatCommandRouter.MaximumRewritePasses`).
pub const MAXIMUM_REWRITE_PASSES: u32 = 8;

/// The registered interceptors, oldest first.
#[derive(Debug, Default)]
pub struct InterceptorRegistry {
    entries: Vec<(String, u32)>,
    next: u32,
}

impl InterceptorRegistry {
    /// Registers one for plugin `owner`: its handle (never 0), or why not.
    ///
    /// # Errors
    /// The plugin already has [`MAXIMUM_INTERCEPTORS_PER_PLUGIN`].
    pub fn register(&mut self, owner: &str) -> Result<u32, String> {
        let installed = self
            .entries
            .iter()
            .filter(|(o, _)| o.eq_ignore_ascii_case(owner))
            .count();
        if installed >= MAXIMUM_INTERCEPTORS_PER_PLUGIN {
            return Err(format!(
                "a plugin may have at most {MAXIMUM_INTERCEPTORS_PER_PLUGIN} input interceptors"
            ));
        }
        self.next = self.next.wrapping_add(1).max(1);
        self.entries.push((owner.to_owned(), self.next));
        Ok(self.next)
    }

    /// Removes one of `owner`'s; false when it has no such handle.
    pub fn unregister(&mut self, owner: &str, handle: u32) -> bool {
        let before = self.entries.len();
        self.entries
            .retain(|(o, h)| !(o.eq_ignore_ascii_case(owner) && *h == handle));
        self.entries.len() != before
    }

    /// Everything `owner` registered goes.
    pub fn release(&mut self, owner: &str) {
        self.entries.retain(|(o, _)| !o.eq_ignore_ascii_case(owner));
    }

    /// Whether any is registered.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// (owner, handle), in registration order.
    #[must_use]
    pub fn ordered(&self) -> Vec<(String, u32)> {
        self.entries.clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // OpenAC `IPluginChat.MaximumInputInterceptors`: the seventeenth is
    // refused; removing one makes room; release drops a plugin's own.
    #[test]
    fn a_plugin_may_hold_sixteen_and_release_drops_only_its_own() {
        let mut registry = InterceptorRegistry::default();
        let first = registry.register("a").expect("room");
        for _ in 1..MAXIMUM_INTERCEPTORS_PER_PLUGIN {
            registry.register("a").expect("room");
        }
        assert!(registry.register("a").is_err());
        let other = registry.register("b").expect("its own room");
        assert!(!registry.unregister("b", first));
        assert!(registry.unregister("a", first));
        assert!(registry.register("a").is_ok());
        registry.release("a");
        assert_eq!(registry.ordered(), [("b".to_owned(), other)]);
    }
}

/// The chat filters plugins register: asked, in registration order across
/// every plugin, about each line before the transcript keeps it (and each
/// status notice); the first that drops it decides (OpenAC
/// `ChatSuppressionFilters`, `IPluginChat.RegisterFilter`). There is no cap
/// (OpenAC has none).
#[derive(Debug, Default)]
pub struct FilterRegistry {
    entries: Vec<(String, u32)>,
    next: u32,
}

impl FilterRegistry {
    /// Registers one for plugin `owner`, at the end of the order: its
    /// handle (never 0).
    pub fn register(&mut self, owner: &str) -> u32 {
        self.next = self.next.wrapping_add(1).max(1);
        self.entries.push((owner.to_owned(), self.next));
        self.next
    }

    /// Removes one of `owner`'s; false when it has no such handle.
    pub fn unregister(&mut self, owner: &str, handle: u32) -> bool {
        let before = self.entries.len();
        self.entries
            .retain(|(o, h)| !(o.eq_ignore_ascii_case(owner) && *h == handle));
        self.entries.len() != before
    }

    /// Everything `owner` registered goes.
    pub fn release(&mut self, owner: &str) {
        self.entries.retain(|(o, _)| !o.eq_ignore_ascii_case(owner));
    }

    /// Whether any is registered.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// (owner, handle), in registration order.
    #[must_use]
    pub fn ordered(&self) -> Vec<(String, u32)> {
        self.entries.clone()
    }
}

#[cfg(test)]
mod filter_tests {
    use super::*;

    // OpenAC `ScopedPluginChatTests.UnloadingAPluginRemovesEveryFilterAndChat
    // SubscriptionItLeft` and `DisposingOneFilterRegistrationLeavesTheOthers`;
    // `ChatSuppressionFilterTests.FiltersRunInRegistrationOrder...` (the
    // order is registration order across plugins).
    #[test]
    fn filters_keep_registration_order_and_go_with_their_plugin() {
        let mut registry = FilterRegistry::default();
        let first = registry.register("example.plugin");
        let other = registry.register("other.plugin");
        let second = registry.register("example.plugin");
        assert_eq!(
            registry.ordered(),
            [
                ("example.plugin".to_owned(), first),
                ("other.plugin".to_owned(), other),
                ("example.plugin".to_owned(), second),
            ]
        );
        assert!(registry.unregister("example.plugin", first));
        assert!(!registry.unregister("example.plugin", first));
        assert!(!registry.unregister("example.plugin", other));
        registry.release("EXAMPLE.plugin");
        assert_eq!(registry.ordered(), [("other.plugin".to_owned(), other)]);
        registry.release("other.plugin");
        assert!(registry.is_empty());
    }
}
