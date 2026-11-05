//! Whether a spell may be cast on an object, and the refusal text when not.

use super::metadata::SpellMetadata;
use crate::items::{ItemType, PublicWeenieFlags};

/// Target-mask bits that let a spell land on the caster.
const SPECIAL_TARGET_MASK: u32 = 0x0000_8107;

/// The facts about a candidate target the check reads.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SpellTargetCandidate<'a> {
    /// Object id.
    pub object_id: u32,
    /// Stack size.
    pub stack_size: i32,
    /// Item type.
    pub item_type: ItemType,
    /// Display name.
    pub name: &'a str,
    /// Public weenie bit field (`None` when not sent).
    pub public_weenie_bitfield: Option<u32>,
    /// The pet's owner (0 when not a pet).
    pub pet_owner_id: u32,
}

/// Why a spell cannot be cast on a target.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SpellTargetRefusal {
    /// The target is the caster and the spell has no self bit.
    SelfTarget,
    /// The target is a stack of items.
    Stack,
    /// The target's kind does not suit the spell.
    WrongTarget {
        /// The target's name.
        name: String,
    },
}

impl SpellTargetRefusal {
    /// The message the game prints.
    #[must_use]
    pub fn message(&self) -> String {
        match self {
            Self::SelfTarget => "You cannot cast this spell upon yourself.".to_owned(),
            Self::Stack => "Cannot cast spell on a stack of items.".to_owned(),
            Self::WrongTarget { name } => format!("This spell cannot be cast on {name}."),
        }
    }
}

/// True when the caster may be the target: a self-targeted or untargeted
/// spell, or one whose mask carries a self bit.
#[must_use]
pub fn can_target_self(spell: &SpellMetadata) -> bool {
    spell.is_self_targeted() || spell.is_untargeted || spell.target_mask & SPECIAL_TARGET_MASK != 0
}

/// Checks a target for a spell, in the game's order: self, stack, the
/// item-type mask, then a player-or-attackable non-pet.
///
/// # Errors
/// The first refusal that applies.
pub fn evaluate(
    local_player_id: u32,
    target: &SpellTargetCandidate<'_>,
    spell: &SpellMetadata,
) -> Result<(), SpellTargetRefusal> {
    let mask = spell.target_mask;
    let special = mask & SPECIAL_TARGET_MASK;
    if target.object_id == local_player_id && special == 0 {
        return Err(SpellTargetRefusal::SelfTarget);
    }
    if target.stack_size > 1 {
        return Err(SpellTargetRefusal::Stack);
    }
    let wrong = || SpellTargetRefusal::WrongTarget {
        name: target.name.to_owned(),
    };
    if target.item_type.bits() & mask == 0 && special == 0 {
        return Err(wrong());
    }
    let flags = PublicWeenieFlags(target.public_weenie_bitfield.unwrap_or(0));
    let player_or_attackable =
        flags.intersects(PublicWeenieFlags::PLAYER | PublicWeenieFlags::ATTACKABLE);
    if !player_or_attackable || target.pet_owner_id != 0 {
        return Err(wrong());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn creature_spell(mask: u32) -> SpellMetadata {
        SpellMetadata {
            target_mask: mask,
            ..SpellMetadata::new(1, "Test", 0)
        }
    }

    fn monster(id: u32) -> SpellTargetCandidate<'static> {
        SpellTargetCandidate {
            object_id: id,
            stack_size: 1,
            item_type: ItemType::CREATURE,
            name: "Drudge",
            public_weenie_bitfield: Some(PublicWeenieFlags::ATTACKABLE.bits()),
            pet_owner_id: 0,
        }
    }

    #[test]
    fn attackable_creature_is_accepted() {
        assert_eq!(evaluate(5, &monster(9), &creature_spell(0x10)), Ok(()));
    }

    #[test]
    fn self_needs_a_self_bit() {
        let spell = creature_spell(0x10);
        assert_eq!(
            evaluate(9, &monster(9), &spell),
            Err(SpellTargetRefusal::SelfTarget)
        );
        assert_eq!(
            SpellTargetRefusal::SelfTarget.message(),
            "You cannot cast this spell upon yourself."
        );
        assert!(!can_target_self(&spell));
        assert!(can_target_self(&creature_spell(0x10 | 0x8000)));
    }

    #[test]
    fn stacks_are_refused() {
        let target = SpellTargetCandidate {
            stack_size: 2,
            ..monster(9)
        };
        assert_eq!(
            evaluate(5, &target, &creature_spell(0x10)),
            Err(SpellTargetRefusal::Stack)
        );
    }

    #[test]
    fn wrong_kind_and_pets_are_refused_by_name() {
        let door = SpellTargetCandidate {
            item_type: ItemType::MISC,
            name: "Door",
            ..monster(9)
        };
        let refusal = evaluate(5, &door, &creature_spell(0x10)).expect_err("door");
        assert_eq!(refusal.message(), "This spell cannot be cast on Door.");

        let pet = SpellTargetCandidate {
            pet_owner_id: 5,
            ..monster(9)
        };
        assert!(evaluate(5, &pet, &creature_spell(0x10)).is_err());

        let npc = SpellTargetCandidate {
            public_weenie_bitfield: None,
            ..monster(9)
        };
        assert!(evaluate(5, &npc, &creature_spell(0x10)).is_err());
    }
}
