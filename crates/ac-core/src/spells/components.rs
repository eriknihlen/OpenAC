//! Whether the player carries the components a spell needs, and which
//! recipe applies: the account's personalised formula, or the scarab-only
//! formula when components are switched off, the school is infused, or a
//! focus for the school sits directly in the player's pack.

use std::collections::{HashMap, HashSet};

use super::formula::{customize_for_account, inq_scarab_only_formula};
use crate::properties::{PropertyBool, PropertyInt};

/// How deep a container chain is followed when deciding ownership.
const MAX_CONTAINER_DEPTH: usize = 16;

/// A spell's authored formula.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SpellFormulaDefinition {
    /// Which account randomisation applies.
    pub formula_version: u32,
    /// Component ids (scarab first).
    pub component_ids: Vec<u32>,
    /// The spell's school (1 war .. 5 void).
    pub school: u32,
}

/// The facts about one known object that ownership needs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ComponentObject {
    /// Object id.
    pub object_id: u32,
    /// Weenie class id.
    pub weenie_class_id: u32,
    /// Containing object (0 when none).
    pub container_id: u32,
    /// Wielding object (0 when none).
    pub wielder_id: u32,
}

/// The view of the world the component check reads. The runtime implements
/// it over its object table.
pub trait SpellComponentWorld {
    /// The local player's id (0 before login).
    fn player_id(&self) -> u32;
    /// A bool property of the player; `None` when the player or the
    /// property is unknown.
    fn player_bool(&self, key: PropertyBool) -> Option<bool>;
    /// An int property of the player; `None` when the player or the
    /// property is unknown.
    fn player_int(&self, key: PropertyInt) -> Option<i32>;
    /// Every known object.
    fn objects(&self) -> impl Iterator<Item = ComponentObject> + '_;
    /// One object by id.
    fn object(&self, object_id: u32) -> Option<ComponentObject>;
}

/// The infusion augmentation that lets a school cast from scarabs alone.
#[must_use]
pub fn school_infusion_property(school: u32) -> Option<PropertyInt> {
    match school {
        1 => Some(PropertyInt::AUGMENTATION_INFUSED_WAR_MAGIC),
        2 => Some(PropertyInt::AUGMENTATION_INFUSED_LIFE_MAGIC),
        3 => Some(PropertyInt::AUGMENTATION_INFUSED_ITEM_MAGIC),
        4 => Some(PropertyInt::AUGMENTATION_INFUSED_CREATURE_MAGIC),
        5 => Some(PropertyInt::AUGMENTATION_INFUSED_VOID_MAGIC),
        _ => None,
    }
}

/// The component check, over dat-derived tables: spell formulas by spell
/// id, the component-to-weenie-class map, and each school's focus (magic
/// pack) weenie class.
#[derive(Debug, Clone, Default)]
pub struct SpellComponentRequirements {
    formulas: HashMap<u32, SpellFormulaDefinition>,
    wcid_by_scid: HashMap<u32, u32>,
    magic_pack_wcid_by_school: HashMap<u32, u32>,
}

impl SpellComponentRequirements {
    /// A check over these tables.
    #[must_use]
    pub fn new(
        formulas: HashMap<u32, SpellFormulaDefinition>,
        wcid_by_scid: HashMap<u32, u32>,
        magic_pack_wcid_by_school: HashMap<u32, u32>,
    ) -> Self {
        Self {
            formulas,
            wcid_by_scid,
            magic_pack_wcid_by_school,
        }
    }

    /// True when the player can cast the spell as far as components go:
    /// always when the server switched components off; never for a spell
    /// with no formula; otherwise when every component of the applicable
    /// formula maps to a weenie class the player owns.
    #[must_use]
    pub fn has_required_components(
        &self,
        world: &impl SpellComponentWorld,
        account_name: &str,
        spell_id: u32,
    ) -> bool {
        if world.player_bool(PropertyBool::SPELL_COMPONENTS_REQUIRED) == Some(false) {
            return true;
        }
        let Some(formula) = self.formulas.get(&spell_id) else {
            return false;
        };

        let player_id = world.player_id();
        let owned: HashSet<u32> = world
            .objects()
            .filter(|item| is_owned_by_player(world, item, player_id))
            .map(|item| item.weenie_class_id)
            .collect();
        self.appropriate_formula_for(world, formula, account_name)
            .into_iter()
            .filter(|&scid| scid != 0)
            .all(|scid| {
                self.wcid_by_scid
                    .get(&scid)
                    .is_some_and(|wcid| owned.contains(wcid))
            })
    }

    /// The formula the player casts the spell with (empty for a spell with
    /// no formula).
    #[must_use]
    pub fn appropriate_formula(
        &self,
        world: &impl SpellComponentWorld,
        account_name: &str,
        spell_id: u32,
    ) -> Vec<u32> {
        self.formulas
            .get(&spell_id)
            .map(|formula| self.appropriate_formula_for(world, formula, account_name))
            .unwrap_or_default()
    }

    /// True when the player owns (anywhere in their packs, or wielded) an
    /// item of the component's weenie class.
    #[must_use]
    pub fn is_component_owned(
        &self,
        world: &impl SpellComponentWorld,
        spell_component_id: u32,
    ) -> bool {
        let Some(&wcid) = self.wcid_by_scid.get(&spell_component_id) else {
            return false;
        };
        let player_id = world.player_id();
        world
            .objects()
            .any(|item| item.weenie_class_id == wcid && is_owned_by_player(world, &item, player_id))
    }

    /// The formula the player casts `formula` with: the scarab-only recipe
    /// when components are switched off, the school is infused or the
    /// school's focus sits directly in the player's pack; otherwise the
    /// account's personalised recipe. For a caller that holds the spell's
    /// formula itself (a window reading the spellbook's copy).
    #[must_use]
    pub fn appropriate_formula_for(
        &self,
        world: &impl SpellComponentWorld,
        formula: &SpellFormulaDefinition,
        account_name: &str,
    ) -> Vec<u32> {
        let components_required = world
            .player_bool(PropertyBool::SPELL_COMPONENTS_REQUIRED)
            .unwrap_or(true);
        let infused = school_infusion_property(formula.school)
            .is_some_and(|key| world.player_int(key).unwrap_or(0) > 0);
        let player_id = world.player_id();
        // Only a focus directly in the player's main pack counts.
        let carries_magic_pack = self
            .magic_pack_wcid_by_school
            .get(&formula.school)
            .is_some_and(|&pack| {
                world
                    .objects()
                    .any(|item| item.container_id == player_id && item.weenie_class_id == pack)
            });
        if !components_required || infused || carries_magic_pack {
            inq_scarab_only_formula(&formula.component_ids)
        } else {
            customize_for_account(
                &formula.component_ids,
                formula.formula_version,
                account_name,
            )
        }
    }
}

/// True when the item is wielded by the player or sits in the player's
/// packs, following containers up to sixteen levels.
fn is_owned_by_player(
    world: &impl SpellComponentWorld,
    item: &ComponentObject,
    player_id: u32,
) -> bool {
    if player_id == 0 {
        return false;
    }
    let mut current = *item;
    for _ in 0..MAX_CONTAINER_DEPTH {
        if current.wielder_id == player_id || current.container_id == player_id {
            return true;
        }
        if current.container_id == 0 {
            return false;
        }
        match world.object(current.container_id) {
            Some(parent) => current = parent,
            None => return false,
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;

    // Golden behavior from OpenAC
    // `tests/AcDream.Core.Tests/Spells/SpellComponentRequirementServiceTests.cs`.

    #[derive(Default)]
    struct World {
        player_id: u32,
        bools: BTreeMap<u32, bool>,
        ints: BTreeMap<u32, i32>,
        objects: Vec<ComponentObject>,
    }

    impl SpellComponentWorld for World {
        fn player_id(&self) -> u32 {
            self.player_id
        }
        fn player_bool(&self, key: PropertyBool) -> Option<bool> {
            self.bools.get(&key.0).copied()
        }
        fn player_int(&self, key: PropertyInt) -> Option<i32> {
            self.ints.get(&key.0).copied()
        }
        fn objects(&self) -> impl Iterator<Item = ComponentObject> + '_ {
            self.objects.iter().copied()
        }
        fn object(&self, object_id: u32) -> Option<ComponentObject> {
            self.objects
                .iter()
                .copied()
                .find(|o| o.object_id == object_id)
        }
    }

    fn item(object_id: u32, weenie_class_id: u32, container_id: u32) -> ComponentObject {
        ComponentObject {
            object_id,
            weenie_class_id,
            container_id,
            wielder_id: 0,
        }
    }

    fn world() -> World {
        World {
            player_id: 1,
            objects: vec![item(1, 0, 0)],
            ..World::default()
        }
    }

    fn service(components: &[u32], map: &[(u32, u32)]) -> SpellComponentRequirements {
        SpellComponentRequirements::new(
            HashMap::from([(
                50,
                SpellFormulaDefinition {
                    formula_version: 0,
                    component_ids: components.to_vec(),
                    school: 0,
                },
            )]),
            map.iter().copied().collect(),
            HashMap::new(),
        )
    }

    #[test]
    fn requires_every_component_mapped_to_an_owned_class() {
        let mut w = world();
        w.objects.push(item(2, 100, 1));
        let s = service(&[10, 11], &[(10, 100), (11, 101)]);
        assert!(!s.has_required_components(&w, "testaccount", 50));
        w.objects.push(item(3, 101, 1));
        assert!(s.has_required_components(&w, "testaccount", 50));
    }

    #[test]
    fn server_disabled_components_bypass_and_use_modern_formula() {
        let mut w = world();
        w.bools
            .insert(PropertyBool::SPELL_COMPONENTS_REQUIRED.0, false);
        let s = service(&[1, 63, 10], &[]);
        assert!(s.has_required_components(&w, "testaccount", 50));
        assert_eq!(s.appropriate_formula(&w, "testaccount", 50), [1, 0xBC]);
    }

    #[test]
    fn personalised_formula_uses_exact_account_spelling() {
        let formula = [6, 63, 10, 64, 20, 30, 65, 40];
        let canonical = customize_for_account(&formula, 3, "CanonicalAccount");
        let mut w = world();
        let map: HashMap<u32, u32> = canonical
            .iter()
            .filter(|&&scid| scid != 0)
            .map(|&scid| (scid, scid + 1000))
            .collect();
        for &wcid in map.values() {
            w.objects.push(item(wcid + 10_000, wcid, 1));
        }
        let s = SpellComponentRequirements::new(
            HashMap::from([(
                50,
                SpellFormulaDefinition {
                    formula_version: 3,
                    component_ids: formula.to_vec(),
                    school: 0,
                },
            )]),
            map,
            HashMap::new(),
        );
        assert!(s.has_required_components(&w, "CanonicalAccount", 50));
        assert!(!s.has_required_components(&w, "canonicalaccount", 50));
    }

    #[test]
    fn infused_school_or_directly_carried_focus_uses_scarab_only_formula() {
        for (infused, carries_focus) in [(true, false), (false, true)] {
            let mut w = world();
            if infused {
                w.ints.insert(0x129, 1);
            }
            if carries_focus {
                w.objects.push(item(2, 900, 1));
            }
            // War I scarab-only formula is component 1 plus one prismatic taper.
            w.objects.push(item(3, 101, 1));
            w.objects.push(item(4, 188, 1));
            let s = SpellComponentRequirements::new(
                HashMap::from([(
                    50,
                    SpellFormulaDefinition {
                        formula_version: 0,
                        component_ids: vec![1, 63, 10],
                        school: 1,
                    },
                )]),
                HashMap::from([(1, 101), (0xBC, 188)]),
                HashMap::from([(1, 900)]),
            );
            assert!(s.has_required_components(&w, "testaccount", 50));
        }
    }

    #[test]
    fn focus_nested_inside_a_pack_does_not_count() {
        let mut w = world();
        w.objects.push(item(2, 700, 1));
        w.objects.push(item(3, 900, 2));
        let s = SpellComponentRequirements::new(
            HashMap::from([(
                50,
                SpellFormulaDefinition {
                    formula_version: 0,
                    component_ids: vec![1, 63],
                    school: 1,
                },
            )]),
            HashMap::new(),
            HashMap::from([(1, 900)]),
        );
        assert!(!s.has_required_components(&w, "testaccount", 50));
    }

    #[test]
    fn examination_formula_preserves_order_and_reports_nested_ownership() {
        let mut w = world();
        w.objects.push(item(2, 0, 1));
        w.objects.push(item(3, 101, 2));
        let s = service(&[10, 11, 10], &[(10, 101), (11, 102)]);
        assert_eq!(s.appropriate_formula(&w, "testaccount", 50), [10, 11, 10]);
        assert!(s.is_component_owned(&w, 10));
        assert!(!s.is_component_owned(&w, 11));
    }

    #[test]
    fn infusion_properties() {
        assert_eq!(school_infusion_property(1).map(|p| p.0), Some(0x129));
        assert_eq!(school_infusion_property(2).map(|p| p.0), Some(0x128));
        assert_eq!(school_infusion_property(3).map(|p| p.0), Some(0x127));
        assert_eq!(school_infusion_property(4).map(|p| p.0), Some(0x126));
        assert_eq!(school_infusion_property(5).map(|p| p.0), Some(0x148));
        assert_eq!(school_infusion_property(0), None);
    }
}
