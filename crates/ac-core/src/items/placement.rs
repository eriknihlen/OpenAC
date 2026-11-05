//! Putting an item into a container: whether the move is legal, which pack
//! an item picked up goes into, and the notice when it cannot go anywhere.

use std::collections::{BTreeMap, BTreeSet};

use super::{ClientObject, ClientObjectTable, EquipMask, ItemType};

/// Why an item may not go into a container.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlacementRejection {
    /// The item is unknown.
    InvalidItem,
    /// The item is the player.
    CannotMovePlayer,
    /// The item is a creature.
    CannotMoveCreature,
    /// The item is in the trade window.
    SourceBeingTraded,
    /// The destination is unknown or not a container.
    InvalidDestination,
    /// The destination is in the trade window.
    DestinationBeingTraded,
    /// The destination is the item or inside it.
    RecursiveContainment,
    /// No free item slot.
    ItemCapacityFull,
    /// No free container slot.
    ContainerCapacityFull,
    /// The destination is a hook the item does not fit: the item names no
    /// hook kind the hook has, or the hook does not take its type.
    WrongHookType,
    /// The destination is a hook of a house nobody owns.
    HookHouseNotOwned,
}

/// The container-type value of a spell focus.
const FOCUS_CONTAINER_TYPE: u32 = 2;

/// Whether the object takes a container slot rather than an item slot. A
/// spell focus does, although it holds nothing; ask
/// [`is_focus`] before putting anything into one.
#[must_use]
pub fn is_container(item: &ClientObject) -> bool {
    item.container_type_hint != 0
        || item.item_type.intersects(ItemType::CONTAINER)
        || item.items_capacity != 0
        || item.containers_capacity != 0
}

/// A spell focus: counted as a container, but never somewhere an item can
/// go (the server refuses any request naming one as the destination).
#[must_use]
pub fn is_focus(item: &ClientObject) -> bool {
    item.container_type_hint == FOCUS_CONTAINER_TYPE
}

fn is_container_id(objects: &ClientObjectTable, id: u32) -> bool {
    objects.get(id).is_some_and(is_container)
}

/// Whether `item_id` may go into `destination_id`: a known item that is
/// not the player, not a creature and not being traded; a destination
/// that is the player or a known container not being traded, and not the
/// item or inside it; for a hook, a house with an owner and an item that
/// fits the hook; and a free slot of the right kind unless the item is
/// already directly inside (a reorder). A plain item already inside counts
/// only when `whole_stack` moves: part of a stack needs a slot of its own.
pub fn evaluate_container_placement(
    objects: &ClientObjectTable,
    item_id: u32,
    destination_id: u32,
    player_id: u32,
    whole_stack: bool,
) -> Result<(), PlacementRejection> {
    let item = match objects.get(item_id) {
        Some(item) if item_id != 0 => item,
        _ => return Err(PlacementRejection::InvalidItem),
    };
    if item_id == player_id {
        return Err(PlacementRejection::CannotMovePlayer);
    }
    if item.item_type.intersects(ItemType::CREATURE) {
        return Err(PlacementRejection::CannotMoveCreature);
    }
    if item.trade_state == 1 {
        return Err(PlacementRejection::SourceBeingTraded);
    }
    let destination = objects.get(destination_id);
    let invalid = match destination {
        _ if destination_id == 0 => true,
        None => destination_id != player_id,
        Some(destination) => !is_container(destination) && destination_id != player_id,
    };
    if invalid {
        return Err(PlacementRejection::InvalidDestination);
    }
    if destination.is_some_and(|destination| destination.trade_state == 1) {
        return Err(PlacementRejection::DestinationBeingTraded);
    }
    if item_id == destination_id || is_contained_by(objects, destination_id, item_id) {
        return Err(PlacementRejection::RecursiveContainment);
    }
    if let Some(hook) = destination.filter(|d| d.is_hook()) {
        // Only the owner of the house may use its hooks; a hook of a house
        // nobody owns takes nothing.
        if matches!(hook.house_owner_id(), None | Some(0)) {
            return Err(PlacementRejection::HookHouseNotOwned);
        }
        if !fits_hook(item, hook) {
            return Err(PlacementRejection::WrongHookType);
        }
    }

    let already_directly_contained = item.container_id == destination_id;
    let moving_container = is_container(item);
    let capacity = destination.map_or(0, |destination| {
        if moving_container {
            destination.containers_capacity
        } else {
            destination.items_capacity
        }
    });
    let occupied = objects
        .get_contents(destination_id)
        .iter()
        .filter(|&&child| {
            objects
                .get(child)
                .is_some_and(|child| is_container(child) == moving_container)
        })
        .count();
    // Part of a stack takes a slot of its own, even beside the rest of it.
    let reorder = already_directly_contained && (moving_container || whole_stack);
    // Item slots: only -1 is unbounded, and 0 is none (a description
    // carries a capacity only when it is not zero). An undescribed player
    // has no known capacity and takes anything.
    let limited = match destination {
        None => false,
        Some(_) if moving_container => capacity > 0,
        Some(_) => capacity != -1,
    };
    if !reorder && limited && occupied >= usize::try_from(capacity).unwrap_or(0) {
        return Err(if moving_container {
            PlacementRejection::ContainerCapacityFull
        } else {
            PlacementRejection::ItemCapacityFull
        });
    }
    Ok(())
}

/// An item names the kinds of hook it hangs on (floor, wall, ceiling, yard,
/// roof) and a hook names the item types it takes. An item that names no
/// hook kind fits none.
fn fits_hook(item: &ClientObject, hook: &ClientObject) -> bool {
    item.hook_type & hook.hook_type != 0 && hook.hook_item_types & item.item_type.bits() != 0
}

fn is_contained_by(
    objects: &ClientObjectTable,
    candidate_id: u32,
    possible_ancestor_id: u32,
) -> bool {
    let mut visited = BTreeSet::new();
    let mut current = candidate_id;
    while current != 0 && visited.insert(current) {
        if current == possible_ancestor_id {
            return true;
        }
        current = objects.get(current).map_or(0, |item| item.container_id);
    }
    false
}

const CANNOT_PLACE_ON_HOOK: &str = "That item cannot be placed on the hook.";

/// The notice the client prints itself for a refused placement. The
/// player's own inventory is called by the player object's name (the
/// "Backpack" notices).
#[must_use]
pub fn placement_rejection_text(
    rejection: PlacementRejection,
    item: Option<&ClientObject>,
    destination: Option<&ClientObject>,
    player_id: u32,
) -> String {
    let item_name = item.map_or_else(|| "item".into(), |item| item.appropriate_name());
    let destination_name = destination.map_or_else(
        || "container".into(),
        |destination| destination.appropriate_name(),
    );
    let destination_is_player = destination.is_some_and(|d| d.object_id() == player_id);
    match rejection {
        PlacementRejection::InvalidItem => "That item is not valid!".into(),
        PlacementRejection::CannotMovePlayer => {
            "You cannot place yourself within another object!".into()
        }
        PlacementRejection::CannotMoveCreature => "You cannot pick up creatures!".into(),
        PlacementRejection::SourceBeingTraded => format!("The {item_name} is being traded"),
        PlacementRejection::InvalidDestination => "The destination container is not valid!".into(),
        PlacementRejection::DestinationBeingTraded => {
            format!("The {destination_name} is being traded")
        }
        PlacementRejection::RecursiveContainment => {
            "You cannot place an object within itself!".into()
        }
        PlacementRejection::ItemCapacityFull if destination_is_player => {
            "Backpack is completely full!".into()
        }
        PlacementRejection::ItemCapacityFull => {
            format!("The {destination_name} is completely full!")
        }
        PlacementRejection::ContainerCapacityFull if destination_is_player => {
            format!("{destination_name} can carry no more containers!")
        }
        PlacementRejection::ContainerCapacityFull => {
            format!("The {destination_name} can fit no more containers!")
        }
        PlacementRejection::WrongHookType => CANNOT_PLACE_ON_HOOK.into(),
        PlacementRejection::HookHouseNotOwned => {
            format!("{CANNOT_PLACE_ON_HOOK} You must own the house to manipulate the hook.")
        }
    }
}

/// Whether `container_id` can take `item_id`: the container must be the
/// player or in the player's inventory, not a spell focus and not being
/// traded. A plain item needs a free item slot (-1 is unbounded, 0 means
/// none, as a capacity the description left out); one already inside fits
/// only when `whole_stack` moves, since part of a stack needs a slot of its
/// own. A pack needs a free container slot (-1 is unbounded, 0 means none;
/// one already inside fits). An undescribed player object fits anything.
#[must_use]
pub fn will_item_fit_in_container(
    objects: &ClientObjectTable,
    item_id: u32,
    container_id: u32,
    player_id: u32,
    whole_stack: bool,
) -> bool {
    if container_id == 0 || item_id == container_id {
        return false;
    }
    let Some(container) = objects.get(container_id) else {
        return container_id == player_id;
    };
    if is_focus(container) {
        return false;
    }
    let owned_by_player = container_id == player_id
        || (player_id != 0 && objects.is_owned_by_object(container_id, player_id));
    if !owned_by_player || container.trade_state == 1 {
        return false;
    }

    let contents = objects.get_contents(container_id);
    let moving_container = is_container_id(objects, item_id);
    let capacity = if moving_container {
        if container.containers_capacity < 0 {
            return true;
        }
        container.containers_capacity
    } else {
        if container.items_capacity == -1 {
            return true;
        }
        container.items_capacity
    };
    let inside_counts = moving_container || whole_stack;
    let mut occupied = 0;
    for &id in contents {
        if id == item_id && inside_counts {
            return true;
        }
        if is_container_id(objects, id) == moving_container {
            occupied += 1;
        }
    }
    occupied < capacity
}

/// How many item slots `container_id` has free: its item capacity less the
/// plain items in it (packs take container slots). -1, unbounded, stays
/// -1; an overfull container comes out negative. The hover over a pack's
/// icon reads only this: any value but 0 takes a plain item.
#[must_use]
pub fn empty_item_slots(objects: &ClientObjectTable, container_id: u32) -> i32 {
    let Some(container) = objects.get(container_id) else {
        return 0;
    };
    let capacity = container.items_capacity;
    if capacity == -1 {
        return capacity;
    }
    let items = objects
        .get_contents(container_id)
        .iter()
        .filter(|&&id| !is_container_id(objects, id))
        .count();
    capacity.saturating_sub(i32::try_from(items).unwrap_or(i32::MAX))
}

/// The container to ask the server for when `item_id` goes into the
/// inventory: the named `target_id` if it fits, else the root (the main
/// pack), else the first of the root's packs with room, in slot order.
/// When nothing has room the error is the capacity refusal, whose notice
/// names the root. `whole_stack` is false when only part of a stack moves.
pub fn choose_container(
    objects: &ClientObjectTable,
    item_id: u32,
    root_id: u32,
    target_id: u32,
    player_id: u32,
    whole_stack: bool,
) -> Result<u32, PlacementRejection> {
    let fits =
        |container| will_item_fit_in_container(objects, item_id, container, player_id, whole_stack);
    if target_id != 0 && fits(target_id) {
        return Ok(target_id);
    }
    if root_id != 0 && fits(root_id) {
        return Ok(root_id);
    }
    if root_id != 0 {
        let mut packs: Vec<&ClientObject> = objects
            .get_contents(root_id)
            .iter()
            .filter_map(|&id| objects.get(id))
            .filter(|contained| is_container(contained))
            .collect();
        packs.sort_by_key(|pack| pack.container_slot);
        if let Some(pack) = packs.iter().find(|pack| fits(pack.object_id())) {
            return Ok(pack.object_id());
        }
    }
    Err(if is_container_id(objects, item_id) {
        PlacementRejection::ContainerCapacityFull
    } else {
        PlacementRejection::ItemCapacityFull
    })
}

/// The slot a drop onto a container's item list asks the server for, or
/// `None` when the drop leaves the item where it already is and nothing is
/// sent.
///
/// The dropped item takes the place of the item under the pointer, which
/// moves one slot on, so it always lands to the left of the item it was
/// dropped on; a drop at or past `item_count` (an empty cell after the last
/// item) appends. The server takes a moved item out of its old slot before
/// inserting it, so an item moving later in its own list asks for one slot
/// less. A split leaves the source stack in place and takes nothing off.
///
/// `source_index` is the dragged item's index in this list, `None` when it
/// comes from anywhere else (another pack, the paperdoll, the ground).
#[must_use]
pub fn item_list_drop_placement(
    drop_index: i32,
    item_count: usize,
    source_index: Option<usize>,
    whole_stack: bool,
) -> Option<i32> {
    let count = i32::try_from(item_count).unwrap_or(i32::MAX);
    let mut placement = drop_index.clamp(0, count);
    let Some(source) = source_index.and_then(|s| i32::try_from(s).ok()) else {
        return Some(placement);
    };
    if placement == source {
        return None; // dropped on itself
    }
    if !whole_stack {
        return Some(placement);
    }
    if placement == source + 1 {
        return None; // already left of the item it was dropped on
    }
    if source < placement {
        placement -= 1; // its old slot closes up first
    }
    Some(placement)
}

/// Whether an item window shows the object among a container's packs
/// rather than its loose items: it says it is a container, or it holds
/// items.
#[must_use]
pub fn is_pack(item: &ClientObject) -> bool {
    item.container_type_hint != 0
        || item.item_type.intersects(ItemType::CONTAINER)
        || item.items_capacity > 0
}

/// A container's loose items (everything directly in it that is not a
/// pack), in its order: what its item list shows, and what a placement in
/// that list counts.
#[must_use]
pub fn loose_contents(objects: &ClientObjectTable, container_id: u32) -> Vec<u32> {
    objects
        .get_contents(container_id)
        .iter()
        .copied()
        .filter(|&id| !objects.get(id).is_some_and(is_pack))
        .collect()
}

/// An object's place in its container's contents list, counted among its
/// own kind: a loose item among the loose items, a pack (or focus) among
/// the packs, the split [`is_pack`] and [`loose_contents`] use. The game
/// keeps the two kinds in separate lists and reads an item's place off
/// its list, so this is the number it shows for the item, whatever slot
/// the server last named. -1 when the object is in no container, is worn
/// or wielded (the game takes it out of the list; the table may still name
/// the wielder as its container) or its container's list does not hold
/// it; 0 when its container has no list at all (never listed, or its view
/// closed), as the game counts it. [`ContainerPlaces`] answers the same
/// for many items with one walk of each list.
#[must_use]
pub fn place_in_container_list(objects: &ClientObjectTable, item_id: u32) -> i32 {
    objects.get(item_id).map_or(-1, |item| {
        place_by_rule(objects, item, |contents| {
            let mut places = PlaceCounter::default();
            contents
                .iter()
                .find_map(|&id| places.next(objects, id).filter(|_| id == item_id))
        })
    })
}

/// Worn or wielded: on the character, in no container's list.
fn is_worn(item: &ClientObject) -> bool {
    item.wielder_id != 0 || item.currently_equipped_location != EquipMask::NONE
}

/// The rule around the list: no container or worn is -1, a container with
/// no list is 0, else `find` reads the place off the container's list.
fn place_by_rule(
    objects: &ClientObjectTable,
    item: &ClientObject,
    find: impl FnOnce(&[u32]) -> Option<i32>,
) -> i32 {
    if item.container_id == 0 || is_worn(item) {
        return -1;
    }
    objects
        .contents_list(item.container_id)
        .map_or(0, |contents| find(contents).unwrap_or(-1))
}

/// Counts a contents list's members as one walks it, loose items and packs
/// apart, skipping worn ones.
#[derive(Debug, Default, Clone, Copy)]
pub struct PlaceCounter {
    loose: i32,
    packs: i32,
}

impl PlaceCounter {
    /// The place of the list's next member, `id`; `None` for a worn one,
    /// which takes no place.
    pub fn next(&mut self, objects: &ClientObjectTable, id: u32) -> Option<i32> {
        let member = objects.get(id);
        if member.is_some_and(is_worn) {
            return None;
        }
        let count = if member.is_some_and(is_pack) {
            &mut self.packs
        } else {
            &mut self.loose
        };
        let place = *count;
        *count = count.saturating_add(1);
        Some(place)
    }
}

/// Every listed object's place in its own container's list, from one walk
/// of each list: [`place_in_container_list`] for a whole table at once.
#[derive(Debug, Default)]
pub struct ContainerPlaces {
    places: BTreeMap<u32, i32>,
}

impl ContainerPlaces {
    /// Walks every contents list once.
    #[must_use]
    pub fn of(objects: &ClientObjectTable) -> Self {
        let mut places = BTreeMap::new();
        for (container, contents) in objects.contents_lists() {
            let mut counter = PlaceCounter::default();
            for &id in contents {
                if let Some(place) = counter.next(objects, id)
                    && objects.get(id).is_some_and(|m| m.container_id == container)
                {
                    places.insert(id, place);
                }
            }
        }
        Self { places }
    }

    /// The item's place, as [`place_in_container_list`] gives it.
    #[must_use]
    pub fn place(&self, objects: &ClientObjectTable, item: &ClientObject) -> i32 {
        place_by_rule(objects, item, |_| {
            self.places.get(&item.object_id()).copied()
        })
    }
}

/// Whether sending the whole of the loose item at `source_index` (of
/// `item_count`) to `placement`, the slot as sent, leaves it where it is:
/// the drop an item list resolves to [`item_list_drop_placement`]'s `None`.
/// The server takes the item out of its slot before inserting it, so the
/// drop that sends `placement` is on that slot when it lies before the
/// item, and on the next one otherwise.
#[must_use]
pub fn placement_leaves_item_in_place(
    placement: i32,
    item_count: usize,
    source_index: usize,
) -> bool {
    let source = i32::try_from(source_index).unwrap_or(i32::MAX);
    let drop_index = if placement < source {
        placement
    } else {
        placement.saturating_add(1)
    };
    item_list_drop_placement(drop_index, item_count, Some(source_index), true).is_none()
}

#[cfg(test)]
mod drop_placement_tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/ItemListDropPlacementTests.cs`.

    use super::item_list_drop_placement as resolve;

    #[test]
    fn whole_item_reorder() {
        for (drop, count, source, expected) in [
            (0, 4, 3, 0), // moving earlier takes the hovered slot
            (1, 4, 3, 1),
            (3, 5, 0, 2), // moving later asks for one less: lands left of the target
            (2, 4, 0, 1),
            (99, 4, 0, 3), // past the last item appends
            (99, 4, 2, 3),
        ] {
            assert_eq!(resolve(drop, count, Some(source), true), Some(expected));
        }
    }

    #[test]
    fn a_drop_that_leaves_the_item_in_place_sends_nothing() {
        for (drop, count, source) in [(2, 4, 2), (3, 4, 2), (99, 4, 3)] {
            assert_eq!(resolve(drop, count, Some(source), true), None);
        }
    }

    #[test]
    fn a_split_keeps_the_source_in_place_so_nothing_is_taken_off() {
        assert_eq!(resolve(3, 4, Some(2), false), Some(3));
        assert_eq!(resolve(99, 4, Some(0), false), Some(4));
        assert_eq!(resolve(2, 4, Some(2), false), None);
    }

    #[test]
    fn an_arrival_from_elsewhere_takes_the_hovered_slot() {
        for (drop, count, expected) in [(0, 3, 0), (2, 3, 2), (7, 3, 3), (0, 0, 0), (-1, 3, 0)] {
            assert_eq!(resolve(drop, count, None, true), Some(expected));
        }
    }

    // A placement as sent leaves the item in place exactly when every drop
    // that would send it is one the list resolves to nothing: its own slot,
    // or past the end for the last item.
    #[test]
    fn a_placement_as_sent_leaves_the_item_in_place_only_at_its_own_slot() {
        use super::placement_leaves_item_in_place as stays;
        for (placement, count, source) in [(2, 4, 2), (0, 4, 0), (-1, 4, 0), (3, 4, 3), (9, 4, 3)] {
            assert!(
                stays(placement, count, source),
                "{placement} {count} {source}"
            );
        }
        for (placement, count, source) in [(1, 4, 2), (3, 4, 2), (0, 4, 3), (1, 4, 0), (9, 4, 2)] {
            assert!(
                !stays(placement, count, source),
                "{placement} {count} {source}"
            );
            // The drop that sends it is resolved to that very placement.
            let drop = if placement < i32::try_from(source).unwrap() {
                placement
            } else {
                placement + 1
            };
            assert_eq!(
                resolve(drop, count, Some(source), true),
                Some(placement.clamp(0, i32::try_from(count).unwrap() - 1))
            );
        }
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Core.Tests/Items/InventoryContainerPlacementPolicyTests.cs`
    //! and `InventoryPlacementSearchTests.cs`.

    use super::*;
    use crate::items::{EquipMask, TableEvent};

    const PLAYER: u32 = 0x5000_0001;
    const SIDE_PACK_A: u32 = 0x6000_0010;
    const SIDE_PACK_B: u32 = 0x6000_0011;

    struct World {
        t: ClientObjectTable,
        ev: Vec<TableEvent>,
    }

    impl World {
        fn new() -> Self {
            Self {
                t: ClientObjectTable::new(),
                ev: Vec::new(),
            }
        }

        fn add(&mut self, item: ClientObject) {
            self.t.add_or_update(item, &mut self.ev);
        }

        fn mv(&mut self, id: u32, container: u32, slot: i32) {
            self.t
                .move_item(id, container, slot, EquipMask::NONE, None, &mut self.ev);
        }
    }

    fn object(id: u32, name: &str, item_type: ItemType) -> ClientObject {
        let mut item = ClientObject::new(id);
        item.name = name.into();
        item.item_type = item_type;
        item
    }

    #[test]
    fn full_item_capacity_rejects_new_item_but_allows_reorder() {
        let mut w = World::new();
        let mut player = object(PLAYER, "Player", ItemType::NONE);
        player.items_capacity = 1;
        player.containers_capacity = 7;
        w.add(player);
        w.add(ClientObject::new(2));
        w.mv(2, PLAYER, 0);
        w.add(ClientObject::new(3));

        assert_eq!(
            evaluate_container_placement(&w.t, 3, PLAYER, PLAYER, true),
            Err(PlacementRejection::ItemCapacityFull)
        );
        assert_eq!(
            evaluate_container_placement(&w.t, 2, PLAYER, PLAYER, true),
            Ok(())
        );
    }

    #[test]
    fn container_cycle_and_trade_are_rejected() {
        let mut w = World::new();
        for id in [10, 11] {
            let mut bag = object(id, "Bag", ItemType::CONTAINER);
            bag.items_capacity = 24;
            w.add(bag);
        }
        w.mv(11, 10, 0);
        assert_eq!(
            evaluate_container_placement(&w.t, 10, 11, PLAYER, true),
            Err(PlacementRejection::RecursiveContainment)
        );
        w.t.get_mut(10).expect("present").trade_state = 1;
        assert_eq!(
            evaluate_container_placement(&w.t, 10, PLAYER, PLAYER, true),
            Err(PlacementRejection::SourceBeingTraded)
        );
    }

    const FLOOR_HOOK: u32 = 1;
    const WALL_HOOK: u32 = 2;
    const HOUSE_OWNER: u32 = 0x5000_0002;

    fn hooked_house(hook_type: u32, hook_item_types: u32, house_owner: Option<u32>) -> World {
        let mut w = World::new();
        let mut hook = object(2, "Hook", ItemType::NONE);
        hook.items_capacity = 1;
        hook.hook_type = hook_type;
        hook.hook_item_types = hook_item_types;
        hook.house_owner_id = house_owner;
        w.add(hook);
        w
    }

    fn sword() -> ClientObject {
        let mut sword = object(3, "Long Sword", ItemType::MELEE_WEAPON);
        sword.hook_type = WALL_HOOK;
        sword
    }

    #[test]
    fn item_goes_only_on_the_hook_types_it_fits() {
        for (hook_type, expected) in [
            (WALL_HOOK, Ok(())),
            (FLOOR_HOOK, Err(PlacementRejection::WrongHookType)),
        ] {
            let mut w = hooked_house(hook_type, u32::MAX, Some(HOUSE_OWNER));
            w.add(sword());
            assert_eq!(
                evaluate_container_placement(&w.t, 3, 2, PLAYER, true),
                expected
            );
        }
    }

    #[test]
    fn item_with_no_hook_type_fits_no_hook() {
        let mut w = hooked_house(WALL_HOOK, u32::MAX, Some(HOUSE_OWNER));
        w.add(object(3, "Coat", ItemType::ARMOR));
        assert_eq!(
            evaluate_container_placement(&w.t, 3, 2, PLAYER, true),
            Err(PlacementRejection::WrongHookType)
        );
    }

    #[test]
    fn hook_refuses_item_types_it_does_not_take() {
        let mut w = hooked_house(WALL_HOOK, ItemType::ARMOR.bits(), Some(HOUSE_OWNER));
        w.add(sword());
        assert_eq!(
            evaluate_container_placement(&w.t, 3, 2, PLAYER, true),
            Err(PlacementRejection::WrongHookType)
        );
    }

    #[test]
    fn hook_of_an_unowned_house_refuses_even_a_fitting_item() {
        for owner in [None, Some(0)] {
            let mut w = hooked_house(WALL_HOOK, u32::MAX, owner);
            w.add(sword());
            assert_eq!(
                evaluate_container_placement(&w.t, 3, 2, PLAYER, true),
                Err(PlacementRejection::HookHouseNotOwned),
                "{owner:?}"
            );
        }
    }

    #[test]
    fn hook_refusal_messages() {
        let coat = object(3, "Coat", ItemType::NONE);
        let hook = object(2, "Wall Hook", ItemType::NONE);
        assert_eq!(
            placement_rejection_text(
                PlacementRejection::WrongHookType,
                Some(&coat),
                Some(&hook),
                PLAYER
            ),
            "That item cannot be placed on the hook."
        );
        assert_eq!(
            placement_rejection_text(
                PlacementRejection::HookHouseNotOwned,
                Some(&coat),
                Some(&hook),
                PLAYER
            ),
            "That item cannot be placed on the hook. You must own the house to manipulate the hook."
        );
    }

    #[test]
    fn full_message_matches_container_type_branches() {
        let player = object(PLAYER, "Backpack", ItemType::NONE);
        let bag = object(2, "Pack", ItemType::NONE);
        assert_eq!(
            placement_rejection_text(
                PlacementRejection::ItemCapacityFull,
                None,
                Some(&player),
                PLAYER
            ),
            "Backpack is completely full!"
        );
        assert_eq!(
            placement_rejection_text(
                PlacementRejection::ContainerCapacityFull,
                None,
                Some(&bag),
                PLAYER
            ),
            "The Pack can fit no more containers!"
        );
    }

    fn player_with_packs(main_capacity: i32, containers_capacity: i32) -> World {
        let mut w = World::new();
        let mut player = object(PLAYER, "Tester", ItemType::CREATURE);
        player.items_capacity = main_capacity;
        player.containers_capacity = containers_capacity;
        w.add(player);
        for (guid, name, slot) in [(SIDE_PACK_A, "Pack A", 0), (SIDE_PACK_B, "Pack B", 1)] {
            let mut pack = object(guid, name, ItemType::CONTAINER);
            pack.items_capacity = 2;
            w.add(pack);
            w.mv(guid, PLAYER, slot);
        }
        w
    }

    fn add_loose(w: &mut World, guid: u32, container: u32) -> u32 {
        w.add(object(guid, "Item", ItemType::MISC));
        let slot = w.t.get_contents(container).len() as i32;
        w.mv(guid, container, slot);
        guid
    }

    fn world_item(w: &mut World) -> u32 {
        w.add(object(0x7000_0001, "Loot", ItemType::MISC));
        0x7000_0001
    }

    #[test]
    fn named_container_with_room_is_used_as_is() {
        let mut w = player_with_packs(3, 2);
        let loot = world_item(&mut w);
        assert_eq!(
            choose_container(&w.t, loot, PLAYER, SIDE_PACK_B, PLAYER, true),
            Ok(SIDE_PACK_B)
        );
    }

    #[test]
    fn full_main_pack_falls_through_to_the_first_side_pack_with_room() {
        let mut w = player_with_packs(1, 2);
        add_loose(&mut w, 0x7000_0100, PLAYER);
        add_loose(&mut w, 0x7000_0101, SIDE_PACK_A);
        add_loose(&mut w, 0x7000_0102, SIDE_PACK_A);
        let loot = world_item(&mut w);
        assert_eq!(
            choose_container(&w.t, loot, PLAYER, PLAYER, PLAYER, true),
            Ok(SIDE_PACK_B)
        );
    }

    #[test]
    fn empty_item_slots_count_plain_items_only() {
        let mut w = player_with_packs(3, 2);
        add_loose(&mut w, 0x7000_0100, PLAYER);
        assert_eq!(
            empty_item_slots(&w.t, PLAYER),
            2,
            "the packs take no item slot"
        );
        add_loose(&mut w, 0x7000_0101, SIDE_PACK_A);
        add_loose(&mut w, 0x7000_0102, SIDE_PACK_A);
        assert_eq!(empty_item_slots(&w.t, SIDE_PACK_A), 0);
        add_loose(&mut w, 0x7000_0103, SIDE_PACK_A);
        assert_eq!(empty_item_slots(&w.t, SIDE_PACK_A), -1, "overfull");
        w.t.get_mut(SIDE_PACK_B).expect("present").items_capacity = -1;
        assert_eq!(empty_item_slots(&w.t, SIDE_PACK_B), -1, "unbounded");
        assert_eq!(empty_item_slots(&w.t, 0x7FFF_0000), 0, "unknown");
    }

    #[test]
    fn a_focus_is_never_chosen_as_a_placement_destination() {
        let mut w = player_with_packs(1, 2);
        add_loose(&mut w, 0x7000_0100, PLAYER);
        add_loose(&mut w, 0x7000_0101, SIDE_PACK_A);
        add_loose(&mut w, 0x7000_0102, SIDE_PACK_A);
        add_loose(&mut w, 0x7000_0103, SIDE_PACK_B);
        add_loose(&mut w, 0x7000_0104, SIDE_PACK_B);
        let mut focus = object(0x6000_0020, "Foci of Verdancy", ItemType::MISC);
        focus.container_type_hint = 2;
        w.add(focus);
        w.mv(0x6000_0020, PLAYER, 2);
        let loot = world_item(&mut w);
        assert_eq!(
            choose_container(&w.t, loot, PLAYER, PLAYER, PLAYER, true),
            Err(PlacementRejection::ItemCapacityFull)
        );
    }

    #[test]
    fn no_target_named_tries_the_main_pack_first() {
        let mut w = player_with_packs(3, 2);
        let loot = world_item(&mut w);
        assert_eq!(
            choose_container(&w.t, loot, PLAYER, 0, PLAYER, true),
            Ok(PLAYER)
        );
    }

    #[test]
    fn everything_full_refuses_with_the_backpack_notice() {
        let mut w = player_with_packs(1, 2);
        add_loose(&mut w, 0x7000_0100, PLAYER);
        add_loose(&mut w, 0x7000_0101, SIDE_PACK_A);
        add_loose(&mut w, 0x7000_0102, SIDE_PACK_A);
        add_loose(&mut w, 0x7000_0103, SIDE_PACK_B);
        add_loose(&mut w, 0x7000_0104, SIDE_PACK_B);
        let loot = world_item(&mut w);
        let refusal = choose_container(&w.t, loot, PLAYER, PLAYER, PLAYER, true);
        assert_eq!(refusal, Err(PlacementRejection::ItemCapacityFull));
        assert_eq!(
            placement_rejection_text(
                PlacementRejection::ItemCapacityFull,
                w.t.get(loot),
                w.t.get(PLAYER),
                PLAYER
            ),
            "Backpack is completely full!"
        );
    }

    #[test]
    fn a_pack_needs_a_container_slot_and_the_notice_names_the_player() {
        let mut w = player_with_packs(3, 2);
        let mut pack = object(0x6000_0020, "New Pack", ItemType::CONTAINER);
        pack.items_capacity = 4;
        w.add(pack);
        assert_eq!(
            choose_container(&w.t, 0x6000_0020, PLAYER, PLAYER, PLAYER, true),
            Err(PlacementRejection::ContainerCapacityFull)
        );
        let expected = format!(
            "{} can carry no more containers!",
            w.t.get(PLAYER).expect("player").appropriate_name()
        );
        assert_eq!(
            placement_rejection_text(
                PlacementRejection::ContainerCapacityFull,
                w.t.get(0x6000_0020),
                w.t.get(PLAYER),
                PLAYER
            ),
            expected
        );
    }

    #[test]
    fn an_item_already_in_the_container_fits_even_when_it_is_full() {
        let mut w = player_with_packs(1, 2);
        let inside = add_loose(&mut w, 0x7000_0100, PLAYER);
        assert!(will_item_fit_in_container(
            &w.t, inside, PLAYER, PLAYER, true
        ));
    }

    #[test]
    fn part_of_a_stack_already_in_a_full_pack_needs_a_slot_of_its_own() {
        // Only the whole stack counts as already fitting where it is.
        let mut w = player_with_packs(1, 2);
        let inside = add_loose(&mut w, 0x7000_0100, PLAYER);
        assert!(will_item_fit_in_container(
            &w.t, inside, PLAYER, PLAYER, true
        ));
        assert!(!will_item_fit_in_container(
            &w.t, inside, PLAYER, PLAYER, false
        ));
        assert_eq!(
            evaluate_container_placement(&w.t, inside, PLAYER, PLAYER, true),
            Ok(())
        );
        assert_eq!(
            evaluate_container_placement(&w.t, inside, PLAYER, PLAYER, false),
            Err(PlacementRejection::ItemCapacityFull)
        );
        assert_eq!(
            choose_container(&w.t, inside, PLAYER, PLAYER, PLAYER, false),
            Ok(SIDE_PACK_A)
        );
    }

    #[test]
    fn an_item_capacity_of_zero_has_no_room() {
        // Only -1 is unbounded; a pack that carries no item slots is
        // passed over by the search.
        let mut w = player_with_packs(1, 2);
        add_loose(&mut w, 0x7000_0100, PLAYER);
        let mut none = object(SIDE_PACK_A, "Pack A", ItemType::CONTAINER);
        none.items_capacity = 0;
        w.add(none);
        w.mv(SIDE_PACK_A, PLAYER, 0);
        let loot = world_item(&mut w);
        assert!(!will_item_fit_in_container(
            &w.t,
            loot,
            SIDE_PACK_A,
            PLAYER,
            true
        ));
        assert_eq!(
            choose_container(&w.t, loot, PLAYER, PLAYER, PLAYER, true),
            Ok(SIDE_PACK_B)
        );
    }

    #[test]
    fn the_legality_check_reads_an_item_capacity_of_zero_as_no_room() {
        let mut w = player_with_packs(3, 2);
        let mut none = object(SIDE_PACK_A, "Pack A", ItemType::CONTAINER);
        none.items_capacity = 0;
        w.add(none);
        w.mv(SIDE_PACK_A, PLAYER, 0);
        let loot = world_item(&mut w);
        assert_eq!(
            evaluate_container_placement(&w.t, loot, SIDE_PACK_A, PLAYER, true),
            Err(PlacementRejection::ItemCapacityFull)
        );
        // -1 is unbounded; an undescribed player takes anything.
        w.t.get_mut(SIDE_PACK_A).expect("present").items_capacity = -1;
        assert_eq!(
            evaluate_container_placement(&w.t, loot, SIDE_PACK_A, PLAYER, true),
            Ok(())
        );
        let mut lone = World::new();
        lone.add(object(0x7000_0001, "Loot", ItemType::MISC));
        assert_eq!(
            evaluate_container_placement(&lone.t, 0x7000_0001, PLAYER, PLAYER, true),
            Ok(())
        );
    }

    #[test]
    fn unbounded_capacity_always_fits() {
        let mut w = player_with_packs(-1, 2);
        for i in 0..10 {
            add_loose(&mut w, 0x7000_0200 + i, PLAYER);
        }
        let loot = world_item(&mut w);
        assert!(will_item_fit_in_container(&w.t, loot, PLAYER, PLAYER, true));
    }

    #[test]
    fn a_container_not_owned_by_the_player_never_fits() {
        let mut w = player_with_packs(3, 2);
        let mut chest = object(0x6000_0030, "Chest", ItemType::CONTAINER);
        chest.items_capacity = 10;
        w.add(chest);
        let loot = world_item(&mut w);
        assert!(!will_item_fit_in_container(
            &w.t,
            loot,
            0x6000_0030,
            PLAYER,
            true
        ));
    }
}

#[cfg(test)]
mod place_in_list_tests {
    //! An item's place in its container's list, as the game counts it: its
    //! index among its own kind (loose items, or packs) in that list.

    use super::{ContainerPlaces, place_in_container_list};

    /// The item's place, asserting the whole-table walk agrees with the
    /// single lookup.
    fn place(t: &ClientObjectTable, id: u32) -> i32 {
        let single = place_in_container_list(t, id);
        let walked = t
            .get(id)
            .map_or(-1, |item| ContainerPlaces::of(t).place(t, item));
        assert_eq!(single, walked, "{id:#x}");
        single
    }
    use crate::items::{
        ClientObjectTable, ContainerContentEntry, EquipMask, ItemType, TableEvent, WeenieData,
    };

    const PLAYER: u32 = 0x5000_0001;
    const PACK: u32 = 0x6000_0010;

    fn entry(guid: u32, container_type: u32) -> ContainerContentEntry {
        ContainerContentEntry {
            guid,
            container_type,
        }
    }

    fn in_container(guid: u32, container: u32) -> WeenieData {
        WeenieData {
            guid,
            name: Some("Gem".into()),
            item_type: Some(ItemType::GEM),
            container_id: Some(container),
            ..WeenieData::default()
        }
    }

    fn pack_in(guid: u32, container: u32) -> WeenieData {
        WeenieData {
            item_type: Some(ItemType::CONTAINER),
            items_capacity: Some(24),
            ..in_container(guid, container)
        }
    }

    // The login shape: a side pack's listing, then each item's description.
    // The descriptions name the pack but no slot, so the table keeps none;
    // the place comes from the list.
    #[test]
    fn a_side_pack_item_listed_before_its_description_has_its_list_place() {
        let mut t = ClientObjectTable::new();
        let mut ev: Vec<TableEvent> = Vec::new();
        t.initialize_inventory_manifest(PLAYER, &[entry(PACK, 1)], &mut ev);
        t.replace_contents(PACK, &[entry(0xA01, 0), entry(0xA02, 0)], &mut ev);
        t.ingest(&pack_in(PACK, PLAYER), &mut ev);
        t.ingest(&in_container(0xA02, PACK), &mut ev);
        t.ingest(&in_container(0xA01, PACK), &mut ev);
        assert_eq!(t.get(0xA01).unwrap().container_slot, -1);
        assert_eq!((place(&t, 0xA01), place(&t, 0xA02)), (0, 1));
    }

    // Packs and loose items are counted apart: a pack inside a pack is
    // counted among the packs, and the items after it do not count it.
    #[test]
    fn a_pack_inside_a_pack_counts_among_the_packs() {
        let mut t = ClientObjectTable::new();
        let mut ev: Vec<TableEvent> = Vec::new();
        t.replace_contents(
            PACK,
            &[
                entry(0xA01, 0),
                entry(0xB01, 1),
                entry(0xA02, 0),
                entry(0xB02, 1),
            ],
            &mut ev,
        );
        for guid in [0xA01, 0xA02] {
            t.ingest(&in_container(guid, PACK), &mut ev);
        }
        for guid in [0xB01, 0xB02] {
            t.ingest(&pack_in(guid, PACK), &mut ev);
        }
        assert_eq!((place(&t, 0xA01), place(&t, 0xA02)), (0, 1));
        assert_eq!((place(&t, 0xB01), place(&t, 0xB02)), (0, 1));
    }

    // In no list: not in a container at all (wielded), or named in a
    // container whose list no longer holds it.
    #[test]
    fn an_item_in_no_list_has_no_place() {
        let mut t = ClientObjectTable::new();
        let mut ev: Vec<TableEvent> = Vec::new();
        t.ingest(
            &WeenieData {
                wielder_id: Some(PLAYER),
                current_wielded_location: Some(EquipMask::MELEE_WEAPON),
                ..in_container(0xA03, 0)
            },
            &mut ev,
        );
        assert_eq!(place(&t, 0xA03), -1);
        t.ingest(&in_container(0xA04, 0), &mut ev);
        t.move_item(0xA04, PACK, 0, EquipMask::NONE, None, &mut ev);
        t.replace_contents(PACK, &[], &mut ev);
        let kept = t.get(0xA04).unwrap();
        assert_eq!((kept.container_id, kept.container_slot), (PACK, 0));
        assert_eq!(place(&t, 0xA04), -1);
        assert_eq!(place(&t, 0xDEAD), -1);
    }

    // A worn item is in no container's list, even where the table names
    // the wielder as its container and lists it there; nor does it take a
    // place from the items listed after it.
    #[test]
    fn a_worn_item_has_no_place_and_takes_none() {
        let mut t = ClientObjectTable::new();
        let mut ev: Vec<TableEvent> = Vec::new();
        t.ingest(&in_container(0xA05, 0), &mut ev);
        t.move_item(0xA05, PLAYER, 0, EquipMask::CHEST_WEAR, None, &mut ev);
        t.ingest(&in_container(0xA01, PLAYER), &mut ev);
        t.ingest(
            &WeenieData {
                wielder_id: Some(PLAYER),
                ..in_container(0xA06, PLAYER)
            },
            &mut ev,
        );
        let listed = t.get_contents(PLAYER);
        assert!(listed.contains(&0xA05) && listed.contains(&0xA06));
        assert!(
            listed.iter().position(|&id| id == 0xA05) < listed.iter().position(|&id| id == 0xA01)
        );
        assert_eq!((place(&t, 0xA05), place(&t, 0xA06)), (-1, -1));
        assert_eq!(place(&t, 0xA01), 0);
    }

    // A container with no contents list at all (never listed, or its view
    // closed, which drops the list) puts what names it at place 0, as the
    // game does; only a list that does not hold the item gives -1.
    #[test]
    fn an_item_whose_container_has_no_list_is_at_place_zero() {
        let mut t = ClientObjectTable::new();
        let mut ev: Vec<TableEvent> = Vec::new();
        t.replace_contents(PACK, &[entry(0xA01, 0), entry(0xA02, 0)], &mut ev);
        t.ingest(&in_container(0xA01, PACK), &mut ev);
        t.ingest(&in_container(0xA02, PACK), &mut ev);
        assert_eq!(place(&t, 0xA02), 1);
        t.stop_viewing_contents(PACK, &mut ev);
        assert!(t.contents_list(PACK).is_none());
        assert_eq!(t.get(0xA02).unwrap().container_id, PACK);
        assert_eq!((place(&t, 0xA01), place(&t, 0xA02)), (0, 0));
    }

    // The main pack as the character description lists it (loose items
    // first, then the packs): the loose items' places are the slots the
    // table already keeps; the packs are counted among the packs.
    #[test]
    fn main_pack_items_keep_the_numbers_the_table_keeps() {
        let mut t = ClientObjectTable::new();
        let mut ev: Vec<TableEvent> = Vec::new();
        t.initialize_inventory_manifest(
            PLAYER,
            &[entry(0xA01, 0), entry(0xA02, 0), entry(PACK, 1)],
            &mut ev,
        );
        t.ingest(&in_container(0xA02, PLAYER), &mut ev);
        t.ingest(&in_container(0xA01, PLAYER), &mut ev);
        t.ingest(&pack_in(PACK, PLAYER), &mut ev);
        for guid in [0xA01, 0xA02] {
            assert_eq!(place(&t, guid), t.get(guid).unwrap().container_slot);
        }
        assert_eq!((place(&t, 0xA01), place(&t, 0xA02)), (0, 1));
        assert_eq!(place(&t, PACK), 0);
    }
}
