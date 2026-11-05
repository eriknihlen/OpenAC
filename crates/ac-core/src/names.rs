//! Object names as the player sees them: singular or plural by stack size,
//! the hover caption with its count, and creature-kind names.

use std::borrow::Cow;
use std::collections::{HashMap, HashSet};

/// The naming inputs of one object, as the server described it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ObjectNaming<'a> {
    /// The singular name.
    pub name: &'a str,
    /// The plural name; empty when the server sent none.
    pub plural_name: &'a str,
    /// Items in the stack (1 for anything that does not stack).
    pub stack_size: i32,
}

impl<'a> ObjectNaming<'a> {
    /// The singular name for one item, the plural for a stack. Without a
    /// plural name from the server, the plural is the singular plus `s`, or
    /// plus `es` when it already ends in a lowercase `s`.
    #[must_use]
    pub fn appropriate_name(&self) -> Cow<'a, str> {
        if self.stack_size <= 1 {
            return Cow::Borrowed(self.name);
        }
        if !self.plural_name.is_empty() {
            return Cow::Borrowed(self.plural_name);
        }
        if self.name.is_empty() {
            return Cow::Borrowed(self.name);
        }
        let suffix = if self.name.ends_with('s') { "es" } else { "s" };
        Cow::Owned(format!("{}{suffix}", self.name))
    }

    /// The hover caption: the name, with the count in front when the stack
    /// holds more than one ("12 Arrows"). `composed_name` is the fully
    /// decorated name when the caller has built one (a material prefix needs
    /// data this crate does not read); `None` or empty uses
    /// [`appropriate_name`](Self::appropriate_name).
    #[must_use]
    pub fn tooltip_display_name(&self, composed_name: Option<&'a str>) -> Cow<'a, str> {
        let name = match composed_name {
            Some(composed) if !composed.is_empty() => Cow::Borrowed(composed),
            _ => self.appropriate_name(),
        };
        if self.stack_size > 1 {
            Cow::Owned(format!("{} {name}", self.stack_size))
        } else {
            name
        }
    }
}

/// The dat id of the first enum mapping in the creature-name chain.
pub const CREATURE_NAME_MAPPER_DID: u32 = 0x2200_000E;

/// One enum mapping as read from the dat: id → text, plus the id of the
/// mapping it extends (0 for none).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct EnumMapping {
    pub base_enum_map: u32,
    pub id_to_string: Vec<(u32, String)>,
}

/// Display names of creature kinds (the creature-type property), read from
/// a chain of enum mappings where each names the one it extends. A name in
/// an earlier mapping wins over the one it was layered on. Underscores in
/// the stored text become spaces.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CreatureDisplayNames {
    names: HashMap<u32, String>,
}

impl CreatureDisplayNames {
    /// Names from an already-built table.
    #[must_use]
    pub fn new(names: HashMap<u32, String>) -> Self {
        Self { names }
    }

    /// A table that names nothing, for a client without content.
    #[must_use]
    pub fn empty() -> Self {
        Self::default()
    }

    /// Every name, by creature kind.
    #[must_use]
    pub fn names(&self) -> &HashMap<u32, String> {
        &self.names
    }

    /// Walks the chain from [`CREATURE_NAME_MAPPER_DID`], asking `get` for
    /// each mapping. Stops at a base id of 0, at a mapping `get` cannot
    /// supply, or at an id already visited (a cycle).
    pub fn load(mut get: impl FnMut(u32) -> Option<EnumMapping>) -> Self {
        let mut names = HashMap::new();
        let mut visited = HashSet::new();
        let mut did = CREATURE_NAME_MAPPER_DID;
        while did != 0 && visited.insert(did) {
            let Some(mapping) = get(did) else {
                break;
            };
            for (id, text) in mapping.id_to_string {
                names.entry(id).or_insert_with(|| text.replace('_', " "));
            }
            did = mapping.base_enum_map;
        }
        Self { names }
    }

    /// The name of creature kind `creature_type`; empty for 0, negative or
    /// unknown kinds.
    #[must_use]
    pub fn resolve(&self, creature_type: i32) -> &str {
        u32::try_from(creature_type)
            .ok()
            .filter(|&id| id > 0)
            .and_then(|id| self.names.get(&id))
            .map_or("", String::as_str)
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/ClientObjectNameTests.cs`
    //! and `tests/AcDream.App.Tests/UI/Layout/CreatureAppraisalRowsTests.cs`
    //! (`CreatureNameResolverUsesLoadedRetailMappingAndSafeFallback`).

    use super::*;

    fn naming<'a>(name: &'a str, plural_name: &'a str, stack_size: i32) -> ObjectNaming<'a> {
        ObjectNaming {
            name,
            plural_name,
            stack_size,
        }
    }

    #[test]
    fn appropriate_name_uses_wire_plural_for_stack() {
        assert_eq!(
            naming("Pyreal Scarab", "Pyreal Scarabs", 2).appropriate_name(),
            "Pyreal Scarabs"
        );
    }

    #[test]
    fn appropriate_name_without_wire_plural_uses_suffix_fallback() {
        for (singular, expected) in [
            ("Arrow", "Arrows"),
            ("Pyreal", "Pyreals"),
            ("Compass", "Compasses"),
        ] {
            assert_eq!(naming(singular, "", 2).appropriate_name(), expected);
        }
    }

    #[test]
    fn appropriate_name_for_single_item_remains_singular() {
        assert_eq!(
            naming("Pyreal Scarab", "Pyreal Scarabs", 1).appropriate_name(),
            "Pyreal Scarab"
        );
    }

    #[test]
    fn appropriate_name_edge_cases() {
        assert_eq!(naming("", "", 5).appropriate_name(), "");
        // Only a lowercase s takes "es".
        assert_eq!(naming("BOSS", "", 2).appropriate_name(), "BOSSs");
        assert_eq!(naming("Arrow", "", 0).appropriate_name(), "Arrow");
    }

    #[test]
    fn tooltip_puts_the_count_first_for_a_stack() {
        assert_eq!(
            naming("Arrow", "", 12).tooltip_display_name(None),
            "12 Arrows"
        );
        assert_eq!(naming("Arrow", "", 1).tooltip_display_name(None), "Arrow");
        assert_eq!(
            naming("Arrow", "", 3).tooltip_display_name(Some("Iron Arrows")),
            "3 Iron Arrows"
        );
        assert_eq!(
            naming("Arrow", "", 3).tooltip_display_name(Some("")),
            "3 Arrows"
        );
    }

    #[test]
    fn creature_name_resolver_uses_loaded_mapping_and_safe_fallback() {
        let names = CreatureDisplayNames::new(HashMap::from([(77, "Ghost".to_owned())]));
        assert_eq!(names.resolve(77), "Ghost");
        assert_eq!(names.resolve(0), "");
        assert_eq!(names.resolve(999), "");
        assert_eq!(names.resolve(-77), "");
    }

    #[test]
    fn load_walks_the_chain_first_name_wins_and_stops_on_cycles() {
        let mut asked = Vec::new();
        let names = CreatureDisplayNames::load(|did| {
            asked.push(did);
            match did {
                CREATURE_NAME_MAPPER_DID => Some(EnumMapping {
                    base_enum_map: 0x2200_0001,
                    id_to_string: vec![(1, "Olthoi_Soldier".to_owned())],
                }),
                0x2200_0001 => Some(EnumMapping {
                    base_enum_map: CREATURE_NAME_MAPPER_DID,
                    id_to_string: vec![(1, "Overridden".to_owned()), (2, "Drudge".to_owned())],
                }),
                _ => None,
            }
        });
        assert_eq!(asked, [CREATURE_NAME_MAPPER_DID, 0x2200_0001]);
        assert_eq!(names.resolve(1), "Olthoi Soldier");
        assert_eq!(names.resolve(2), "Drudge");
        assert_eq!(CreatureDisplayNames::empty().resolve(1), "");
    }
}
