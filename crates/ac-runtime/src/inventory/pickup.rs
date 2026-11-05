//! What a person may pick up: a pickup of something that is not theirs to
//! lift is refused before anything is sent, in the game's own words (a
//! creature; something stuck in place; something another character
//! wields; anything else that is not a loose object). Every pickup route
//! asks, the keys, a click, a use that means a pickup and a plugin's.

use ac_core::items::{EquipMask, ItemType, PublicWeenieFlags};

use crate::world::World;

/// A creature cannot be lifted.
pub const CANNOT_PICK_UP_CREATURES: &str = "You cannot pick up creatures!";

/// The pick-up key with nothing selected.
pub const NOTHING_SELECTED: &str = "Nothing selected";

/// The give key with no creature to give to.
pub const GIVE_NEEDS_A_CREATURE: &str =
    "You must select a creature or a character to give that to.\n";

/// The drop key on something the player does not carry.
pub const DROP_NEEDS_IT_CARRIED: &str = "You must pick that up first";

/// An object that is not loose cannot be lifted.
#[must_use]
pub fn cant_be_picked_up(name: &str) -> String {
    format!("The {name} can't be picked up!")
}

/// An object stuck in place cannot be lifted.
#[must_use]
pub fn cannot_be_picked_up(name: &str) -> String {
    format!("The {name} cannot be picked up!")
}

/// What another character wields cannot be lifted.
#[must_use]
pub fn being_wielded_by_someone_else(name: &str) -> String {
    format!("The {name} is being wielded by someone else!")
}

/// How an object is named in a refusal: its name, else its id in hex.
#[must_use]
pub fn describe(world: &World, guid: u32) -> String {
    world
        .item(guid)
        .map(|i| i.name.as_str())
        .filter(|n| !n.trim().is_empty())
        .map_or_else(|| format!("0x{guid:08X}"), str::to_owned)
}

/// A creature in the world other than the player that is not lying dead.
#[must_use]
pub fn is_creature(world: &World, guid: u32) -> bool {
    let Some(object) = world.object(guid) else {
        return false;
    };
    if world.player_guid() == Some(guid)
        || object
            .movement()
            .is_some_and(crate::gameplay::combat::is_current_motion_dead)
    {
        return false;
    }
    world
        .item(guid)
        .is_some_and(|i| i.item_type.intersects(ItemType::CREATURE))
}

/// An object the server described that is stuck in place.
fn is_stuck(world: &World, guid: u32) -> bool {
    world.object(guid).is_some()
        && world
            .item(guid)
            .is_some_and(|i| i.flags().intersects(PublicWeenieFlags::STUCK))
}

/// Worn or wielded, held by no pack.
fn is_wielded(world: &World, guid: u32) -> bool {
    world
        .item(guid)
        .is_some_and(|i| i.container_id == 0 && i.currently_equipped_location != EquipMask::NONE)
}

/// A described object that may be lifted into a pack: no list of kinds,
/// anything that is not stuck in place and is not a container of other
/// containers (a chest, a corpse).
fn is_pickupable(world: &World, guid: u32) -> bool {
    world.object(guid).is_some()
        && !is_stuck(world, guid)
        && world.item(guid).map_or(0, |i| i.containers_capacity) == 0
}

/// Why `guid` may not be picked up (`owned`: the player carries or wields
/// it), as the player is told; `None` when it may.
#[must_use]
pub fn refusal(world: &World, guid: u32, owned: bool) -> Option<String> {
    if is_creature(world, guid) {
        return Some(CANNOT_PICK_UP_CREATURES.to_owned());
    }
    if is_stuck(world, guid) {
        return Some(cannot_be_picked_up(&describe(world, guid)));
    }
    if is_wielded(world, guid) && !owned {
        return Some(being_wielded_by_someone_else(&describe(world, guid)));
    }
    if is_pickupable(world, guid) {
        return None;
    }
    Some(cant_be_picked_up(&describe(world, guid)))
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden strings: OpenAC `tests/AcDream.Core.Tests/Ui/RetailMessagesTests.cs`.
    #[test]
    fn the_refusals_are_the_retail_literals() {
        assert_eq!(
            cant_be_picked_up("Holtburg"),
            "The Holtburg can't be picked up!"
        );
        assert_eq!(CANNOT_PICK_UP_CREATURES, "You cannot pick up creatures!");
        assert_eq!(
            cannot_be_picked_up("Holtburg"),
            "The Holtburg cannot be picked up!"
        );
        assert_eq!(
            being_wielded_by_someone_else("Holtburg"),
            "The Holtburg is being wielded by someone else!"
        );
    }
}
