//! The chat verbs plugins register, one table per session. A verb belongs
//! to one plugin; names match ignoring case; unloading a plugin releases all
//! of its verbs.

use ac_plugin_abi::verbs::{already_registered, normalize_verb, split_command_line};
use ac_plugin_abi::{CommandCall, CommandDef};

/// Verbs the client itself answers on the same path (navigation, the motor,
/// the session status, the plugin manager): a plugin cannot take them.
pub const RESERVED_VERBS: [&str; 4] = ["nav", "motor", "status", "plugin"];

#[derive(Debug, Clone)]
struct Registration {
    owner: String,
    verb: String,
    /// The verb and its aliases, lower-cased.
    names: Vec<String>,
    description: String,
}

/// The session's plugin verbs.
#[derive(Debug, Clone, Default)]
pub struct CommandRegistry {
    registrations: Vec<Registration>,
}

impl CommandRegistry {
    /// An empty table.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Gives `owner` a verb (and its aliases).
    ///
    /// # Errors
    /// A malformed name, repeated aliases, or a name already taken (by any
    /// plugin, or reserved by the client); the table is unchanged.
    pub fn register(&mut self, owner: &str, definition: &CommandDef) -> Result<(), String> {
        let verb = normalize_verb(&definition.verb)?;
        let mut names = vec![verb.to_lowercase()];
        for alias in &definition.aliases {
            names.push(normalize_verb(alias)?.to_lowercase());
        }
        let mut unique = names.clone();
        unique.sort_unstable();
        unique.dedup();
        if unique.len() != names.len() {
            return Err("Command aliases must be unique.".to_owned());
        }
        if let Some(taken) = names.iter().find(|name| self.claims(name)) {
            return Err(already_registered(taken));
        }
        self.registrations.push(Registration {
            owner: owner.to_owned(),
            verb,
            names,
            description: definition.description.clone(),
        });
        Ok(())
    }

    /// Removes `owner`'s verb named `verb`; false when it has none.
    pub fn unregister(&mut self, owner: &str, verb: &str) -> bool {
        let wanted = verb.trim().trim_start_matches(['/', '@']).to_lowercase();
        let before = self.registrations.len();
        self.registrations
            .retain(|r| !(r.owner == owner && r.names[0] == wanted));
        self.registrations.len() != before
    }

    /// Removes every verb `owner` holds; returns how many.
    pub fn release(&mut self, owner: &str) -> usize {
        let before = self.registrations.len();
        self.registrations.retain(|r| r.owner != owner);
        before - self.registrations.len()
    }

    /// Whether a verb (with or without its `/` or `@`) is taken, by a plugin
    /// or by the client.
    #[must_use]
    pub fn claims(&self, verb: &str) -> bool {
        let wanted = verb.trim().trim_start_matches(['/', '@']).to_lowercase();
        !wanted.is_empty()
            && (RESERVED_VERBS.contains(&wanted.as_str())
                || self.registrations.iter().any(|r| r.names.contains(&wanted)))
    }

    /// Whether a plugin holds the verb a typed line names (the client's
    /// reserved verbs are not plugin verbs).
    #[must_use]
    pub fn is_plugin_line(&self, raw: &str) -> bool {
        self.resolve(raw).is_some()
    }

    /// The plugin and call a typed line names, or `None` when it names no
    /// plugin verb.
    #[must_use]
    pub fn resolve(&self, raw: &str) -> Option<(String, CommandCall)> {
        let (trimmed, verb, arguments) = split_command_line(raw)?;
        let wanted = verb.to_lowercase();
        let registration = self
            .registrations
            .iter()
            .find(|r| r.names.contains(&wanted))?;
        Some((
            registration.owner.clone(),
            CommandCall {
                verb: registration.verb.clone(),
                arguments: arguments.to_owned(),
                raw: trimmed.to_owned(),
            },
        ))
    }

    /// `/verb - description` for every verb, ordered by verb ignoring case,
    /// one per line.
    #[must_use]
    pub fn help(&self) -> String {
        let mut lines: Vec<(&str, &str)> = self
            .registrations
            .iter()
            .map(|r| (r.verb.as_str(), r.description.as_str()))
            .collect();
        lines.sort_by_key(|(verb, _)| verb.to_lowercase());
        lines
            .iter()
            .map(|(verb, description)| format!("/{verb} - {description}"))
            .collect::<Vec<_>>()
            .join("\n")
    }

    /// Every verb as registered (verb, aliases, description), in
    /// registration order (OpenAC `Definitions`).
    #[must_use]
    pub fn definitions(&self) -> Vec<CommandDef> {
        self.registrations
            .iter()
            .map(|r| CommandDef {
                verb: r.verb.clone(),
                aliases: r.names[1..].to_vec(),
                description: r.description.clone(),
            })
            .collect()
    }

    /// The plugin holding the verb (or alias) `verb`, and the verb as
    /// registered.
    #[must_use]
    pub fn owner_of(&self, verb: &str) -> Option<(&str, &str)> {
        let wanted = verb.trim().trim_start_matches(['/', '@']).to_lowercase();
        self.registrations
            .iter()
            .find(|r| r.names.contains(&wanted))
            .map(|r| (r.owner.as_str(), r.verb.as_str()))
    }

    /// Every registered name (verbs and aliases), lower case.
    #[must_use]
    pub fn names(&self) -> Vec<String> {
        self.registrations
            .iter()
            .flat_map(|r| r.names.iter().cloned())
            .collect()
    }

    /// The verbs `owner` holds, in registration order.
    #[must_use]
    pub fn verbs_of(&self, owner: &str) -> Vec<String> {
        self.registrations
            .iter()
            .filter(|r| r.owner == owner)
            .map(|r| r.verb.clone())
            .collect()
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Core.Tests/Plugins/PluginCommandRegistryTests.cs`.

    use super::*;

    fn def(verb: &str, aliases: &[&str], description: &str) -> CommandDef {
        CommandDef {
            verb: verb.to_owned(),
            aliases: aliases.iter().map(|a| (*a).to_owned()).collect(),
            description: description.to_owned(),
        }
    }

    #[test]
    fn a_registered_verb_handles_both_prefixes_case_insensitively() {
        let mut registry = CommandRegistry::new();
        registry.register("a", &def("vt", &[], "")).unwrap();
        let (owner, call) = registry.resolve("/VT start").unwrap();
        assert_eq!(owner, "a");
        assert_eq!(call.arguments, "start");
        assert_eq!(call.raw, "/VT start");
        let (_, call) = registry.resolve(" @vt   opt get EnableCombat ").unwrap();
        assert_eq!(call.arguments, "opt get EnableCombat");
    }

    #[test]
    fn a_removed_verb_no_longer_consumes_the_line() {
        let mut registry = CommandRegistry::new();
        registry.register("a", &def("vt", &[], "")).unwrap();
        assert!(registry.unregister("a", "vt"));
        assert!(registry.resolve("/vt start").is_none());
        assert!(registry.resolve("hello").is_none());
    }

    #[test]
    fn a_duplicate_verb_is_refused_without_replacing_the_owner() {
        let mut registry = CommandRegistry::new();
        registry.register("a", &def("vt", &[], "")).unwrap();
        assert_eq!(
            registry.register("b", &def("VT", &[], "")),
            Err("Plugin command 'vt' is already registered.".to_owned())
        );
        assert_eq!(registry.resolve("/vt").unwrap().0, "a");
    }

    #[test]
    fn aliases_and_help() {
        let mut registry = CommandRegistry::new();
        registry
            .register("a", &def("go", &["g"], "Navigate to a destination"))
            .unwrap();
        let (_, call) = registry.resolve("/g \"North Road\"").unwrap();
        assert_eq!(call.verb, "go");
        assert_eq!(call.arguments, "\"North Road\"");
        assert_eq!(registry.help(), "/go - Navigate to a destination");
        assert_eq!(
            registry.definitions(),
            [def("go", &["g"], "Navigate to a destination")]
        );
        assert_eq!(registry.owner_of("/G"), Some(("a", "go")));
        assert_eq!(registry.owner_of("north"), None);
        assert_eq!(
            registry.register("b", &def("x", &["y", "Y"], "")),
            Err("Command aliases must be unique.".to_owned())
        );
    }

    #[test]
    fn the_clients_own_verbs_are_reserved() {
        let mut registry = CommandRegistry::new();
        for verb in RESERVED_VERBS {
            assert_eq!(
                registry.register("a", &def(verb, &[], "")),
                Err(already_registered(verb))
            );
            assert!(registry.claims(&format!("/{verb}")));
            assert!(!registry.is_plugin_line(&format!("/{verb} x")));
        }
    }

    #[test]
    fn releasing_an_owner_frees_every_verb_it_held() {
        let mut registry = CommandRegistry::new();
        registry.register("a", &def("one", &["uno"], "")).unwrap();
        registry.register("a", &def("two", &[], "")).unwrap();
        registry.register("b", &def("three", &[], "")).unwrap();
        assert_eq!(registry.verbs_of("a"), vec!["one", "two"]);
        assert_eq!(registry.release("a"), 2);
        assert!(!registry.claims("uno"));
        assert!(registry.claims("three"));
        registry.register("c", &def("one", &[], "")).unwrap();
    }

    #[test]
    fn malformed_verbs_are_refused() {
        let mut registry = CommandRegistry::new();
        assert!(registry.register("a", &def("two words", &[], "")).is_err());
        assert!(registry.register("a", &def("", &[], "")).is_err());
        assert!(
            registry
                .register("a", &def(&"x".repeat(33), &[], ""))
                .is_err()
        );
    }
}
