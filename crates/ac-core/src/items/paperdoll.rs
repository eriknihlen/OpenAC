//! Which worn item the paperdoll shows (and selects) at a body location:
//! the outermost one, by layering priority.

use super::{ClientObject, ClientObjectTable, EquipMask};

/// The layering priority of `item` at `location`. Armor worn without an
/// explicit priority layers over clothing: when the covered part of the
/// location falls in the armor range, it counts as priority 0x7F.
fn effective_priority(item: &ClientObject, location: EquipMask) -> u32 {
    let covered = (item.currently_equipped_location & location).bits();
    if item.priority == 0 && (0x200..=0x4000).contains(&covered) {
        0x7F
    } else {
        item.priority
    }
}

fn worn_by(item: &ClientObject, player_id: u32) -> bool {
    item.wielder_id == player_id || item.container_id == player_id
}

/// The outermost object the player wears at `location`: the highest
/// priority wins and the first one found wins a tie. The player itself
/// when nothing covers the location; 0 when the player is unknown or the
/// location empty.
#[must_use]
pub fn upper_inventory_object(
    objects: &ClientObjectTable,
    player_id: u32,
    location: EquipMask,
) -> u32 {
    if player_id == 0 || location == EquipMask::NONE || objects.get(player_id).is_none() {
        return 0;
    }
    let mut winner: Option<(&ClientObject, u32)> = None;
    for candidate in objects.objects() {
        if !candidate.currently_equipped_location.intersects(location)
            || !worn_by(candidate, player_id)
        {
            continue;
        }
        let priority = effective_priority(candidate, location);
        if winner.is_none_or(|(_, best)| priority > best) {
            winner = Some((candidate, priority));
        }
    }
    winner.map_or(player_id, |(item, _)| item.object_id())
}

/// [`upper_inventory_object`] for several locations in one pass; one
/// answer per location, in order.
#[must_use]
pub fn upper_inventory_objects(
    objects: &ClientObjectTable,
    player_id: u32,
    locations: &[EquipMask],
) -> Vec<u32> {
    if player_id == 0 || objects.get(player_id).is_none() {
        return vec![0; locations.len()];
    }
    let mut winners: Vec<Option<(u32, u32)>> = vec![None; locations.len()];
    for candidate in objects.objects() {
        if candidate.currently_equipped_location == EquipMask::NONE
            || !worn_by(candidate, player_id)
        {
            continue;
        }
        for (winner, &location) in winners.iter_mut().zip(locations) {
            if location == EquipMask::NONE
                || !candidate.currently_equipped_location.intersects(location)
            {
                continue;
            }
            let priority = effective_priority(candidate, location);
            if winner.is_none_or(|(_, best)| priority > best) {
                *winner = Some((candidate.object_id(), priority));
            }
        }
    }
    winners
        .iter()
        .zip(locations)
        .map(|(winner, &location)| {
            if location == EquipMask::NONE {
                0
            } else {
                winner.map_or(player_id, |(id, _)| id)
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/PaperdollSelectionPolicyTests.cs`.

    use super::*;
    use crate::items::{ItemType, TableEvent};

    const PLAYER: u32 = 0x5000_0001;

    fn table_with_player(ev: &mut Vec<TableEvent>) -> ClientObjectTable {
        let mut objects = ClientObjectTable::new();
        let mut player = ClientObject::new(PLAYER);
        player.item_type = ItemType::CREATURE;
        objects.add_or_update(player, ev);
        objects
    }

    fn add_equipped(
        objects: &mut ClientObjectTable,
        ev: &mut Vec<TableEvent>,
        id: u32,
        location: EquipMask,
        priority: u32,
    ) {
        let mut item = ClientObject::new(id);
        item.wielder_id = PLAYER;
        item.currently_equipped_location = location;
        item.priority = priority;
        objects.add_or_update(item, ev);
    }

    #[test]
    fn uncovered_valid_body_part_returns_player() {
        let mut ev = Vec::new();
        let objects = table_with_player(&mut ev);
        assert_eq!(
            upper_inventory_object(&objects, PLAYER, EquipMask::HEAD_WEAR),
            PLAYER
        );
    }

    #[test]
    fn default_priority_armor_beats_clothing_on_same_body_part() {
        let mut ev = Vec::new();
        let mut objects = table_with_player(&mut ev);
        add_equipped(
            &mut objects,
            &mut ev,
            0x5000_1001,
            EquipMask::CHEST_WEAR,
            20,
        );
        add_equipped(
            &mut objects,
            &mut ev,
            0x5000_1002,
            EquipMask::CHEST_ARMOR,
            0,
        );
        assert_eq!(
            upper_inventory_object(
                &objects,
                PLAYER,
                EquipMask::CHEST_WEAR | EquipMask::CHEST_ARMOR
            ),
            0x5000_1002
        );
    }

    #[test]
    fn higher_explicit_priority_wins_and_ties_keep_first() {
        let mut ev = Vec::new();
        let mut objects = table_with_player(&mut ev);
        add_equipped(&mut objects, &mut ev, 0x5000_1001, EquipMask::HEAD_WEAR, 12);
        add_equipped(&mut objects, &mut ev, 0x5000_1002, EquipMask::HEAD_WEAR, 12);
        add_equipped(&mut objects, &mut ev, 0x5000_1003, EquipMask::HEAD_WEAR, 13);
        assert_eq!(
            upper_inventory_object(&objects, PLAYER, EquipMask::HEAD_WEAR),
            0x5000_1003
        );
        objects.remove(0x5000_1003, &mut ev);
        assert_eq!(
            upper_inventory_object(&objects, PLAYER, EquipMask::HEAD_WEAR),
            0x5000_1001
        );
    }

    #[test]
    fn default_armor_priority_uses_covered_intersection_range_exactly() {
        let mut ev = Vec::new();
        let mut objects = table_with_player(&mut ev);
        add_equipped(
            &mut objects,
            &mut ev,
            0x5000_1001,
            EquipMask::LOWER_LEG_WEAR,
            1,
        );
        add_equipped(
            &mut objects,
            &mut ev,
            0x5000_1002,
            EquipMask::LOWER_LEG_WEAR | EquipMask::LOWER_LEG_ARMOR,
            0,
        );
        assert_eq!(
            upper_inventory_object(
                &objects,
                PLAYER,
                EquipMask::LOWER_LEG_WEAR | EquipMask::LOWER_LEG_ARMOR
            ),
            0x5000_1001
        );
    }

    #[test]
    fn invalid_body_mask_returns_zero() {
        let mut ev = Vec::new();
        let objects = table_with_player(&mut ev);
        assert_eq!(upper_inventory_object(&objects, PLAYER, EquipMask::NONE), 0);
    }

    #[test]
    fn batch_answers_match_single_location_answers() {
        let mut ev = Vec::new();
        let mut objects = table_with_player(&mut ev);
        add_equipped(
            &mut objects,
            &mut ev,
            0x5000_1001,
            EquipMask::CHEST_WEAR,
            20,
        );
        add_equipped(
            &mut objects,
            &mut ev,
            0x5000_1002,
            EquipMask::CHEST_ARMOR,
            0,
        );
        add_equipped(&mut objects, &mut ev, 0x5000_1003, EquipMask::HEAD_WEAR, 1);
        let locations = [
            EquipMask::CHEST_WEAR | EquipMask::CHEST_ARMOR,
            EquipMask::HEAD_WEAR,
            EquipMask::FOOT_WEAR,
            EquipMask::NONE,
        ];
        let expected: Vec<u32> = locations
            .iter()
            .map(|&location| upper_inventory_object(&objects, PLAYER, location))
            .collect();
        assert_eq!(
            upper_inventory_objects(&objects, PLAYER, &locations),
            expected
        );
    }
}
