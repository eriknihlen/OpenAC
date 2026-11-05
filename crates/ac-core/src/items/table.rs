//! The object table: every object the server has described this session,
//! the contents list of each container, and the equipment list of each
//! wielder.
//!
//! The table raises no callbacks. Every mutator appends what happened to an
//! event list the caller passes in, in the order it happened; the runtime
//! turns those into its change stream and feeds the ones other item state
//! follows (the inventory transaction) back in.
//!
//! Containment is held twice, as in the game: each object records its own
//! container, slot, wielder and worn location, and each container keeps an
//! ordered contents list. A contents list can be a server snapshot (a chest
//! being viewed) whose members' own records name no container. Within a
//! contents list, packs and plain items are numbered separately.

use std::collections::{BTreeMap, BTreeSet, HashMap};

use super::{
    ClientArmorProfile, ClientObject, ClientWeaponProfile, EquipMask, HouseRestrictionRecord,
    ItemType, ObjectIdentity, PropertyBundle, PublicWeenieFlags, WeenieData, pk_status,
};
use crate::properties::{
    PropertyBool, PropertyDataId, PropertyFloat, PropertyInstanceId, PropertyInt,
};

/// One entry of a contents list: the object and its container type (0 an
/// item, 1 a pack, 2 a spell focus).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ContainerContentEntry {
    pub guid: u32,
    pub container_type: u32,
}

/// One worn or wielded object from the character description.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct EquipmentManifestEntry {
    pub guid: u32,
    pub equip_location: EquipMask,
    pub priority: u32,
}

/// Where an object is: its container and slot, or its wielder and worn
/// location. The default (all zero) means nowhere known.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ClientObjectPlacement {
    pub container_id: u32,
    pub container_slot: i32,
    pub wielder_id: u32,
    pub equip_location: EquipMask,
}

impl ClientObjectPlacement {
    /// The placement an object records.
    #[must_use]
    pub fn of(item: &ClientObject) -> Self {
        Self {
            container_id: item.container_id,
            container_slot: item.container_slot,
            wielder_id: item.wielder_id,
            equip_location: item.currently_equipped_location,
        }
    }

    /// In a container at a slot, not wielded.
    #[must_use]
    pub fn in_container(container_id: u32, container_slot: i32) -> Self {
        Self {
            container_id,
            container_slot,
            wielder_id: 0,
            equip_location: EquipMask::NONE,
        }
    }
}

/// Why a placement changed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MoveOrigin {
    /// The client moved it ahead of the server's answer.
    LocalProjection,
    /// The server refused a move and the client put the object back.
    ServerRollbackProjection,
    /// The server said where the object is.
    AuthoritativeResponse,
}

/// A placement change.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ObjectMove {
    pub item_id: u32,
    /// The moved record; `None` when the server placed an object the table
    /// does not hold (yet).
    pub identity: Option<ObjectIdentity>,
    pub previous: ClientObjectPlacement,
    pub current: ClientObjectPlacement,
    pub origin: MoveOrigin,
}

/// Why an object left the table.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RemovalReason {
    /// Destroyed, dropped, picked up by someone else, ...
    Ordinary,
    /// Deleted by the server for exactly this incarnation.
    LogicalDelete,
    /// Replaced by a newer incarnation of the same guid.
    GenerationReplacement,
}

/// The server refused an inventory move.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MoveRequestFailure {
    pub item_id: u32,
    pub weenie_error: u32,
    /// The client had moved the object ahead and put it back.
    pub rolled_back: bool,
}

/// What a table mutation did, in order.
#[derive(Debug, Clone, PartialEq)]
pub enum TableEvent {
    /// A new object was installed.
    Added {
        object_id: u32,
        identity: ObjectIdentity,
    },
    /// An object's description or properties changed.
    Updated {
        object_id: u32,
        identity: ObjectIdentity,
    },
    /// An object's placement changed (or the server placed one the table
    /// does not hold).
    Moved(ObjectMove),
    /// A container's contents list changed as a whole (replaced, dropped,
    /// or an object left it).
    ContainerContentsReplaced(u32),
    /// The client stopped viewing a container: the list the view held,
    /// raised just before the list is dropped (the game client queues those
    /// objects for destruction here). `nested` marks a container inside the
    /// one the player closed; its list stays with that container until the
    /// container itself goes away.
    ContentsViewEnded {
        container_id: u32,
        contents: Vec<u32>,
        nested: bool,
    },
    /// A refused move was put back.
    MoveRolledBack {
        object_id: u32,
        identity: ObjectIdentity,
    },
    /// The server confirmed a wield.
    WieldConfirmed(u32),
    /// The server refused an inventory move.
    MoveRequestFailed(MoveRequestFailure),
    /// An object left the table; the removed record is carried along.
    Removed {
        object: Box<ClientObject>,
        reason: RemovalReason,
        /// The incarnation, for logical deletes and replacements; 0 otherwise.
        generation: u16,
    },
    /// The server answered a stack-size request (a merge, split, give or
    /// drop), as opposed to any other update.
    StackSizeUpdated {
        object_id: u32,
        identity: ObjectIdentity,
    },
    /// Every object and pending move was dropped.
    Cleared,
}

/// One successful appraisal's results, applied together.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct AppraisalUpdate {
    pub properties: PropertyBundle,
    pub spell_ids: Vec<u32>,
    /// When the answer arrived, in seconds; negative or non-finite leaves
    /// the last appraisal time alone.
    pub received_at_seconds: f64,
    /// `None` clears a profile from an earlier appraisal.
    pub weapon_profile: Option<ClientWeaponProfile>,
    /// `None` clears a profile from an earlier appraisal.
    pub armor_profile: Option<ClientArmorProfile>,
}

type ContentsIndex = BTreeMap<u32, Vec<u32>>;
type Objects = BTreeMap<u32, ClientObject>;

/// Every object of the session, by guid.
#[derive(Debug, Default)]
pub struct ClientObjectTable {
    objects: Objects,
    container_index: ContentsIndex,
    /// Containers whose contents list is the server's own listing (the
    /// inventory manifest or a contents view), not one built up locally.
    described_containers: BTreeSet<u32>,
    equipment_index: ContentsIndex,
    /// Item → (placement before its first unconfirmed local move, moves
    /// still unconfirmed).
    pending_moves: HashMap<u32, (ClientObjectPlacement, u32)>,
    /// Placements the server confirmed for objects not yet described.
    pending_unresolved: HashMap<u32, (ClientObjectPlacement, Option<u32>)>,
    next_identity: u64,
    mutation_revision: u64,
    /// How many times the table was cleared (a session ended, or a copy
    /// was replaced by a snapshot).
    clears: u64,
}

fn emit(revision: &mut u64, events: &mut Vec<TableEvent>, event: TableEvent) {
    if matches!(
        event,
        TableEvent::Added { .. }
            | TableEvent::Updated { .. }
            | TableEvent::Moved(_)
            | TableEvent::Removed { .. }
            | TableEvent::Cleared
    ) {
        *revision = revision.wrapping_add(1);
    }
    events.push(event);
}

fn is_container_list_member(item: &ClientObject) -> bool {
    item.container_type_hint != 0
        || item.item_type.intersects(ItemType::CONTAINER)
        || item.items_capacity > 0
}

fn equipment_owner(placement: &ClientObjectPlacement) -> u32 {
    if placement.equip_location == EquipMask::NONE {
        0
    } else if placement.wielder_id != 0 {
        placement.wielder_id
    } else {
        placement.container_id
    }
}

fn remove_from_other_container_indexes(
    index: &mut ContentsIndex,
    item_id: u32,
    except_container_id: u32,
) -> Vec<u32> {
    let mut changed = Vec::new();
    for (&container_id, members) in index.iter_mut() {
        if container_id == except_container_id {
            continue;
        }
        if let Some(position) = members.iter().position(|&id| id == item_id) {
            members.remove(position);
            changed.push(container_id);
        }
    }
    changed
}

fn renumber_container_category(
    objects: &mut Objects,
    members: &[u32],
    is_container: bool,
    preserved: Option<(u32, i32)>,
) {
    let mut slot = 0;
    for id in members {
        if let Some(member) = objects.get_mut(id)
            && is_container_list_member(member) == is_container
        {
            member.container_slot = match preserved {
                Some((preserved_id, preserved_slot)) if preserved_id == *id => preserved_slot,
                _ => slot,
            };
            slot += 1;
        }
    }
}

fn remove_from_all_container_indexes(
    index: &mut ContentsIndex,
    objects: &mut Objects,
    item_id: u32,
    destination_container_id: u32,
    was_container: bool,
) -> Vec<u32> {
    let mut changed = Vec::new();
    for (&container_id, members) in index.iter_mut() {
        let Some(position) = members.iter().position(|&id| id == item_id) else {
            continue;
        };
        members.remove(position);
        renumber_container_category(objects, members, was_container, None);
        if container_id != destination_container_id {
            changed.push(container_id);
        }
    }
    changed
}

/// Inserts an object into its container's list the way the game adds
/// contents: at the requested position among its own category (packs or
/// items), or after the category's last member when the slot is unknown.
fn insert_container_member(
    index: &mut ContentsIndex,
    objects: &mut Objects,
    item_id: u32,
    requested_slot: i32,
) {
    let Some(item) = objects.get(&item_id) else {
        return;
    };
    let container_id = item.container_id;
    let is_container = is_container_list_member(item);
    let members = index.entry(container_id).or_default();
    let mut category_count = 0;
    let mut insertion_index = members.len();
    let requested_index = if requested_slot < 0 {
        i32::MAX
    } else {
        requested_slot
    };
    for (i, id) in members.iter().enumerate() {
        let Some(existing) = objects.get(id) else {
            continue;
        };
        if is_container_list_member(existing) != is_container {
            continue;
        }
        if category_count == requested_index {
            insertion_index = i;
            break;
        }
        category_count += 1;
        insertion_index = i + 1;
    }
    members.insert(insertion_index, item_id);
    let published_slot = if requested_slot < 0 {
        category_count
    } else {
        requested_slot
    };
    renumber_container_category(
        objects,
        members,
        is_container,
        Some((item_id, published_slot)),
    );
}

/// Puts an object into its container's list (leaving the old one) and
/// re-sorts the list by slot, keeping the current order among equal slots;
/// an unknown slot sorts last.
fn reindex(index: &mut ContentsIndex, objects: &Objects, item_id: u32, old_container_id: u32) {
    let Some(item) = objects.get(&item_id) else {
        return;
    };
    let container_id = item.container_id;
    if old_container_id != container_id
        && old_container_id != 0
        && let Some(old) = index.get_mut(&old_container_id)
    {
        old.retain(|&id| id != item_id);
    }
    if container_id != 0 {
        let list = index.entry(container_id).or_default();
        if !list.contains(&item_id) {
            list.push(item_id);
        }
        list.sort_by_key(|id| match objects.get(id) {
            Some(member) if member.container_slot >= 0 => member.container_slot,
            _ => i32::MAX,
        });
    }
}

fn renumber_container(objects: &mut Objects, list: &[u32]) {
    for (slot, id) in list.iter().enumerate() {
        if let Some(member) = objects.get_mut(id) {
            member.container_slot = slot as i32;
        }
    }
}

fn update_equipment_index(
    index: &mut ContentsIndex,
    item_id: u32,
    previous: &ClientObjectPlacement,
    current: &ClientObjectPlacement,
) {
    if previous == current {
        return;
    }
    let previous_owner = equipment_owner(previous);
    if previous_owner != 0
        && let Some(list) = index.get_mut(&previous_owner)
    {
        list.retain(|&id| id != item_id);
        if list.is_empty() {
            index.remove(&previous_owner);
        }
    }
    let current_owner = equipment_owner(current);
    if current_owner == 0 {
        return;
    }
    let list = index.entry(current_owner).or_default();
    list.retain(|&id| id != item_id);
    list.insert(0, item_id);
}

fn remove_equipment_index_membership(index: &mut ContentsIndex, item_id: u32) {
    index.retain(|_, placements| {
        placements.retain(|&id| id != item_id);
        !placements.is_empty()
    });
}

fn publish_container_contents_changes(changed: &[u32], events: &mut Vec<TableEvent>) {
    let mut published = BTreeSet::new();
    for &container_id in changed {
        if published.insert(container_id) {
            events.push(TableEvent::ContainerContentsReplaced(container_id));
        }
    }
}

impl ClientObjectTable {
    /// An empty table.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Bumped by every added, updated, moved and removed object, by a
    /// clear, and by every change to a house's owner, monarch or
    /// restrictions; physics uses it to know when its cached view of these
    /// objects is stale.
    #[must_use]
    pub fn mutation_revision(&self) -> u64 {
        self.mutation_revision
    }

    /// How many times the table was cleared: a holder of state about its
    /// objects (an open pack, say) knows from a change that the objects it
    /// named are gone.
    #[must_use]
    pub fn clear_count(&self) -> u64 {
        self.clears
    }

    /// How many objects the table holds.
    #[must_use]
    pub fn object_count(&self) -> usize {
        self.objects.len()
    }

    /// How many containers have a contents list.
    #[must_use]
    pub fn container_projection_count(&self) -> usize {
        self.container_index.len()
    }

    /// How many wielders have an equipment list.
    #[must_use]
    pub fn equipment_owner_count(&self) -> usize {
        self.equipment_index.len()
    }

    /// How many objects have unconfirmed local moves.
    #[must_use]
    pub fn pending_move_count(&self) -> usize {
        self.pending_moves.len()
    }

    /// Every object, by ascending guid.
    pub fn objects(&self) -> impl Iterator<Item = &ClientObject> {
        self.objects.values()
    }

    /// The object with this guid.
    #[must_use]
    pub fn get(&self, object_id: u32) -> Option<&ClientObject> {
        self.objects.get(&object_id)
    }

    /// The object with this guid, to change a field that does not affect
    /// placement (trade or sell state, burden, ...); follow with
    /// [`notify_object_updated`](Self::notify_object_updated).
    pub fn get_mut(&mut self, object_id: u32) -> Option<&mut ClientObject> {
        self.objects.get_mut(&object_id)
    }

    /// A container's contents list, in order; empty when it has none.
    #[must_use]
    pub fn get_contents(&self, container_id: u32) -> &[u32] {
        self.container_index
            .get(&container_id)
            .map_or(&[], Vec::as_slice)
    }

    /// The container's item list alone, in order: its contents minus the
    /// packs and foci, which live in the separate container list. A
    /// character's own items, without anything inside its side packs.
    #[must_use]
    pub fn get_contained_items(&self, container_id: u32) -> Vec<u32> {
        self.get_contents(container_id)
            .iter()
            .copied()
            .filter(|id| {
                self.objects
                    .get(id)
                    .is_some_and(|member| !is_container_list_member(member))
            })
            .collect()
    }

    /// A container's contents list when it has one (an opened container
    /// that holds nothing has an empty list; one never listed has none).
    #[must_use]
    pub fn contents_list(&self, container_id: u32) -> Option<&[u32]> {
        self.container_index.get(&container_id).map(Vec::as_slice)
    }

    /// Every contents list, by container guid.
    pub fn contents_lists(&self) -> impl Iterator<Item = (u32, &[u32])> {
        self.container_index
            .iter()
            .map(|(&container, list)| (container, list.as_slice()))
    }

    /// Set a container's contents list (`None` drops it) exactly as another
    /// table holds it: for a copy of a table kept in step with it. The
    /// listed objects are left as they are and no event is raised.
    pub fn set_contents_list(&mut self, container_id: u32, list: Option<Vec<u32>>) {
        match list {
            Some(list) => {
                self.container_index.insert(container_id, list);
            }
            None => {
                self.container_index.remove(&container_id);
                self.described_containers.remove(&container_id);
            }
        }
    }

    /// The server's listings cover `owner_id` and every pack nested in it,
    /// and no local move is waiting for the server: only then does an item
    /// missing from the owner's inventory mean it is no longer owned.
    #[must_use]
    pub fn has_complete_inventory(&self, owner_id: u32) -> bool {
        if owner_id == 0 || !self.pending_moves.is_empty() {
            return false;
        }
        let mut pending = vec![owner_id];
        let mut visited = BTreeSet::new();
        while let Some(container) = pending.pop() {
            if !visited.insert(container) || !self.described_containers.contains(&container) {
                return false;
            }
            let Some(contents) = self.container_index.get(&container) else {
                return false;
            };
            for id in contents {
                let Some(item) = self.objects.get(id) else {
                    return false;
                };
                // A focus takes a container slot but holds nothing, and the
                // server never sends a listing for one.
                if is_container_list_member(item) && !super::placement::is_focus(item) {
                    pending.push(*id);
                }
            }
        }
        true
    }

    /// What `wielder_id` wears and wields, most recently placed first.
    #[must_use]
    pub fn get_equipped_by(&self, wielder_id: u32) -> Vec<&ClientObject> {
        self.equipment_index
            .get(&wielder_id)
            .map(|placements| {
                placements
                    .iter()
                    .filter_map(|id| self.objects.get(id))
                    .filter(|item| {
                        item.currently_equipped_location != EquipMask::NONE
                            && (item.wielder_id == wielder_id || item.container_id == wielder_id)
                    })
                    .collect()
            })
            .unwrap_or_default()
    }

    /// Whether `object_id` belongs to `owner_id`: it is the owner, sits
    /// directly in it or is wielded by it, sits in one of the owner's
    /// packs, or is on the owner's equipment list.
    #[must_use]
    pub fn is_owned_by_object(&self, object_id: u32, owner_id: u32) -> bool {
        if owner_id == 0 {
            return false;
        }
        let Some(item) = self.objects.get(&object_id) else {
            return false;
        };
        if item.object_id() == owner_id
            || item.container_id == owner_id
            || item.wielder_id == owner_id
        {
            return true;
        }
        if !self.objects.contains_key(&owner_id) {
            return false;
        }
        if item.container_id != 0
            && self
                .container_index
                .get(&owner_id)
                .is_some_and(|contents| contents.contains(&item.container_id))
        {
            return true;
        }
        self.equipment_index
            .get(&owner_id)
            .is_some_and(|placements| placements.contains(&item.object_id()))
    }

    /// The total burden of everything `owner_guid` wields or carries,
    /// following containers up to eight levels deep.
    #[must_use]
    pub fn sum_carried_burden(&self, owner_guid: u32) -> i32 {
        self.objects
            .values()
            .filter(|item| self.is_carried_by(item, owner_guid))
            .fold(0i32, |total, item| total.wrapping_add(item.burden))
    }

    fn is_carried_by(&self, item: &ClientObject, owner_guid: u32) -> bool {
        if item.wielder_id == owner_guid {
            return true;
        }
        let mut container = item.container_id;
        for _ in 0..8 {
            if container == 0 {
                break;
            }
            if container == owner_guid {
                return true;
            }
            container = self.objects.get(&container).map_or(0, |c| c.container_id);
        }
        false
    }

    fn next_identity(&mut self) -> ObjectIdentity {
        self.next_identity += 1;
        ObjectIdentity(self.next_identity)
    }

    /// Installs a fresh record for `object_id` if none exists; returns
    /// whether one existed.
    fn ensure(&mut self, object_id: u32) -> bool {
        ensure_record(&mut self.objects, &mut self.next_identity, object_id).1
    }

    fn identity_of(&self, object_id: u32) -> ObjectIdentity {
        self.objects
            .get(&object_id)
            .map_or_else(ObjectIdentity::default, ClientObject::identity)
    }

    fn emit_added_or_updated(
        &mut self,
        object_id: u32,
        existed: bool,
        events: &mut Vec<TableEvent>,
    ) {
        let identity = self.identity_of(object_id);
        let event = if existed {
            TableEvent::Updated {
                object_id,
                identity,
            }
        } else {
            TableEvent::Added {
                object_id,
                identity,
            }
        };
        emit(&mut self.mutation_revision, events, event);
    }

    fn emit_updated(&mut self, object_id: u32, events: &mut Vec<TableEvent>) {
        self.emit_added_or_updated(object_id, true, events);
    }

    /// Installs `item` as a new record (with a new identity), replacing any
    /// record with its guid. Its placement updates the equipment lists but
    /// not the contents lists.
    pub fn add_or_update(
        &mut self,
        mut item: ClientObject,
        events: &mut Vec<TableEvent>,
    ) -> ObjectIdentity {
        let object_id = item.object_id();
        let prior = self.objects.get(&object_id);
        let existed = prior.is_some();
        let previous = prior.map(ClientObjectPlacement::of).unwrap_or_default();
        let identity = self.next_identity();
        item.set_identity(identity);
        let current = ClientObjectPlacement::of(&item);
        self.objects.insert(object_id, item);
        update_equipment_index(&mut self.equipment_index, object_id, &previous, &current);
        self.emit_added_or_updated(object_id, existed, events);
        identity
    }

    /// Moves an object locally (no server answer involved): into
    /// `new_container_id` at `new_slot` (-1 for unknown), worn at
    /// `new_equip_location`; the wielder stays.
    pub fn move_item(
        &mut self,
        item_id: u32,
        new_container_id: u32,
        new_slot: i32,
        new_equip_location: EquipMask,
        container_type_hint: Option<u32>,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get(&item_id) else {
            return false;
        };
        let current = ClientObjectPlacement {
            container_id: new_container_id,
            container_slot: new_slot,
            wielder_id: item.wielder_id,
            equip_location: new_equip_location,
        };
        self.apply_placement(
            item_id,
            current,
            container_type_hint,
            MoveOrigin::LocalProjection,
            false,
            events,
        )
    }

    /// The server says where an object is now. A container placement
    /// inserts it into the container's list as the game does.
    pub fn apply_server_move(
        &mut self,
        item_id: u32,
        placement: ClientObjectPlacement,
        container_type_hint: Option<u32>,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        server_showed_object(item);
        self.apply_placement(
            item_id,
            placement,
            container_type_hint,
            MoveOrigin::AuthoritativeResponse,
            true,
            events,
        )
    }

    fn apply_placement(
        &mut self,
        item_id: u32,
        current: ClientObjectPlacement,
        container_type_hint: Option<u32>,
        origin: MoveOrigin,
        retail_container_insert: bool,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get(&item_id) else {
            return false;
        };
        let previous = ClientObjectPlacement::of(item);
        let previous_was_container = is_container_list_member(item);
        let changed = if retail_container_insert {
            remove_from_all_container_indexes(
                &mut self.container_index,
                &mut self.objects,
                item_id,
                current.container_id,
                previous_was_container,
            )
        } else {
            remove_from_other_container_indexes(
                &mut self.container_index,
                item_id,
                current.container_id,
            )
        };
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        item.container_id = current.container_id;
        item.container_slot = current.container_slot;
        item.wielder_id = current.wielder_id;
        item.currently_equipped_location = current.equip_location;
        if let Some(hint) = container_type_hint {
            item.container_type_hint = hint;
        }
        if retail_container_insert && current.container_id != 0 {
            insert_container_member(
                &mut self.container_index,
                &mut self.objects,
                item_id,
                current.container_slot,
            );
        } else {
            reindex(
                &mut self.container_index,
                &self.objects,
                item_id,
                previous.container_id,
            );
        }
        self.publish_placement_change(item_id, previous, origin, events);
        publish_container_contents_changes(&changed, events);
        true
    }

    fn publish_placement_change(
        &mut self,
        item_id: u32,
        previous: ClientObjectPlacement,
        origin: MoveOrigin,
        events: &mut Vec<TableEvent>,
    ) {
        let Some(item) = self.objects.get(&item_id) else {
            return;
        };
        let current = ClientObjectPlacement::of(item);
        let identity = item.identity();
        update_equipment_index(&mut self.equipment_index, item_id, &previous, &current);
        emit(
            &mut self.mutation_revision,
            events,
            TableEvent::Moved(ObjectMove {
                item_id,
                identity: Some(identity),
                previous,
                current,
                origin,
            }),
        );
    }

    /// The server's answer to a move request: confirms one outstanding
    /// local move and applies the placement. For an object not described
    /// yet, the placement is kept and applied when it arrives; the move
    /// is still announced (without a record) and `false` returned.
    pub fn apply_confirmed_server_move(
        &mut self,
        item_id: u32,
        placement: ClientObjectPlacement,
        container_type_hint: Option<u32>,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        self.confirm_move(item_id);
        if self.apply_server_move(item_id, placement, container_type_hint, events) {
            return true;
        }
        if item_id != 0 && placement.container_id != 0 && !self.objects.contains_key(&item_id) {
            self.pending_unresolved
                .insert(item_id, (placement, container_type_hint));
        }
        emit(
            &mut self.mutation_revision,
            events,
            TableEvent::Moved(ObjectMove {
                item_id,
                identity: None,
                previous: ClientObjectPlacement::default(),
                current: placement,
                origin: MoveOrigin::AuthoritativeResponse,
            }),
        );
        false
    }

    /// The server's answer to a wield request: confirms one outstanding
    /// local move and wields the object (slot 0, no container).
    pub fn apply_confirmed_server_wield(
        &mut self,
        item_id: u32,
        wielder_id: u32,
        equip_location: EquipMask,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        self.confirm_move(item_id);
        let placement = ClientObjectPlacement {
            container_id: 0,
            container_slot: 0,
            wielder_id,
            equip_location,
        };
        let Some(item) = self.objects.get_mut(&item_id) else {
            emit(
                &mut self.mutation_revision,
                events,
                TableEvent::Moved(ObjectMove {
                    item_id,
                    identity: None,
                    previous: ClientObjectPlacement::default(),
                    current: placement,
                    origin: MoveOrigin::AuthoritativeResponse,
                }),
            );
            events.push(TableEvent::WieldConfirmed(item_id));
            return false;
        };
        server_showed_object(item);
        if !self.apply_placement(
            item_id,
            placement,
            None,
            MoveOrigin::AuthoritativeResponse,
            false,
            events,
        ) {
            return false;
        }
        events.push(TableEvent::WieldConfirmed(item_id));
        true
    }

    fn record_pending(&mut self, item_id: u32) {
        let Some(item) = self.objects.get(&item_id) else {
            return;
        };
        let placement = ClientObjectPlacement::of(item);
        self.pending_moves
            .entry(item_id)
            .and_modify(|(_, outstanding)| *outstanding += 1)
            .or_insert((placement, 1));
    }

    /// Moves an object into a container ahead of the server's answer,
    /// remembering where it was so a refusal can put it back. The source
    /// and destination lists are renumbered without gaps.
    pub fn move_item_optimistic(
        &mut self,
        item_id: u32,
        new_container_id: u32,
        new_slot: i32,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        if !self.objects.contains_key(&item_id) {
            return false;
        }
        self.record_pending(item_id);
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        let previous = ClientObjectPlacement::of(item);
        let old_container = previous.container_id;
        item.container_id = new_container_id;
        item.currently_equipped_location = EquipMask::NONE;
        let changed = remove_from_other_container_indexes(
            &mut self.container_index,
            item_id,
            new_container_id,
        );

        if old_container != 0
            && old_container != new_container_id
            && let Some(source) = self.container_index.get_mut(&old_container)
        {
            source.retain(|&id| id != item_id);
            let source = source.clone();
            renumber_container(&mut self.objects, &source);
        }

        if new_container_id != 0 {
            let destination = self.container_index.entry(new_container_id).or_default();
            destination.retain(|&id| id != item_id);
            let index = if new_slot < 0 || new_slot as usize > destination.len() {
                destination.len()
            } else {
                new_slot as usize
            };
            destination.insert(index, item_id);
            let destination = destination.clone();
            renumber_container(&mut self.objects, &destination);
        } else if let Some(item) = self.objects.get_mut(&item_id) {
            item.container_slot = new_slot;
        }

        self.publish_placement_change(item_id, previous, MoveOrigin::LocalProjection, events);
        publish_container_contents_changes(&changed, events);
        true
    }

    /// Wields an object ahead of the server's answer (it moves onto the
    /// wielder at `equip_mask`), remembering where it was.
    pub fn wield_item_optimistic(
        &mut self,
        item_id: u32,
        wielder_guid: u32,
        equip_mask: EquipMask,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        if !self.objects.contains_key(&item_id) {
            return false;
        }
        self.record_pending(item_id);
        self.move_item(item_id, wielder_guid, -1, equip_mask, None, events)
    }

    /// The server accepted one outstanding local move of the object.
    pub fn confirm_move(&mut self, item_id: u32) {
        if let Some((_, outstanding)) = self.pending_moves.get_mut(&item_id) {
            if *outstanding <= 1 {
                self.pending_moves.remove(&item_id);
            } else {
                *outstanding -= 1;
            }
        }
    }

    /// Puts an object back where it was before its first unconfirmed local
    /// move; false when it has none.
    pub fn rollback_move(&mut self, item_id: u32, events: &mut Vec<TableEvent>) -> bool {
        let Some((placement, _)) = self.pending_moves.remove(&item_id) else {
            return false;
        };
        if !self.apply_placement(
            item_id,
            placement,
            None,
            MoveOrigin::ServerRollbackProjection,
            false,
            events,
        ) {
            return false;
        }
        let identity = self.identity_of(item_id);
        events.push(TableEvent::MoveRolledBack {
            object_id: item_id,
            identity,
        });
        true
    }

    /// The server refused a move: roll it back if it was made locally, and
    /// announce the failure either way. Returns whether it rolled back.
    pub fn reject_move(
        &mut self,
        item_id: u32,
        weenie_error: u32,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let rolled_back = self.rollback_move(item_id, events);
        events.push(TableEvent::MoveRequestFailed(MoveRequestFailure {
            item_id,
            weenie_error,
            rolled_back,
        }));
        rolled_back
    }

    /// Drops an object the server removed (destroyed, dropped into the
    /// world, taken by someone else, ...).
    pub fn remove(&mut self, item_id: u32, events: &mut Vec<TableEvent>) -> bool {
        self.remove_core(item_id, RemovalReason::Ordinary, 0, events)
    }

    /// Drops exactly this incarnation of an object.
    pub fn remove_logical_generation(
        &mut self,
        item_id: u32,
        generation: u16,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        self.remove_core(item_id, RemovalReason::LogicalDelete, generation, events)
    }

    fn remove_core(
        &mut self,
        item_id: u32,
        reason: RemovalReason,
        generation: u16,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.remove(&item_id) else {
            return false;
        };
        self.described_containers.remove(&item_id);
        let changed = remove_from_other_container_indexes(&mut self.container_index, item_id, 0);
        update_equipment_index(
            &mut self.equipment_index,
            item_id,
            &ClientObjectPlacement::of(&item),
            &ClientObjectPlacement::default(),
        );
        // A recycled guid must not be rolled back to this object's place.
        self.pending_moves.remove(&item_id);
        emit(
            &mut self.mutation_revision,
            events,
            TableEvent::Removed {
                object: Box::new(item),
                reason,
                generation,
            },
        );
        publish_container_contents_changes(&changed, events);
        true
    }

    /// A newer incarnation of an object: the old record is removed (as a
    /// replacement) and, when `install_replacement` holds, the new
    /// description installed as a fresh record.
    pub fn replace_generation(
        &mut self,
        data: &WeenieData,
        generation: u16,
        install_replacement: bool,
        events: &mut Vec<TableEvent>,
    ) -> Option<ObjectIdentity> {
        self.remove_core(
            data.guid,
            RemovalReason::GenerationReplacement,
            generation,
            events,
        );
        if !install_replacement {
            return None;
        }
        Some(self.ingest(data, events))
    }

    /// Announces a client-side change made through
    /// [`get_mut`](Self::get_mut) (a sale or trade marker).
    pub fn notify_object_updated(&mut self, item_id: u32, events: &mut Vec<TableEvent>) -> bool {
        if !self.objects.contains_key(&item_id) {
            return false;
        }
        self.emit_updated(item_id, events);
        true
    }

    /// Merges a property bundle into an object.
    pub fn update_properties(
        &mut self,
        item_id: u32,
        incoming: &PropertyBundle,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        server_showed_object(item);
        merge_properties(item, incoming);
        apply_cooldown_properties(item, incoming);
        self.emit_updated(item_id, events);
        true
    }

    /// Applies one successful appraisal at once: properties, spell list,
    /// and the weapon and armor profiles (each replaced, or cleared when
    /// the answer carried none).
    pub fn update_appraisal(
        &mut self,
        item_id: u32,
        appraisal: &AppraisalUpdate,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        merge_properties(item, &appraisal.properties);
        item.appraisal_answered = true;
        item.last_appraisal_unsuccessful = false;
        item.appraised_spell_ids = appraisal.spell_ids.clone();
        item.weapon_profile = appraisal.weapon_profile;
        item.armor_profile = appraisal.armor_profile;
        let seconds = appraisal.received_at_seconds;
        if seconds.is_finite() && seconds >= 0.0 {
            let milliseconds = (seconds * 1000.0).round() as i64;
            item.last_appraisal_time_ms = milliseconds as i32;
        }
        apply_cooldown_properties(item, &appraisal.properties);
        self.emit_updated(item_id, events);
        true
    }

    /// Records that the server answered an appraisal it could not make, at
    /// `now_seconds` of session time. The answer carries nothing to keep,
    /// so nothing else about the object changes; every such answer is
    /// stamped, a repeat refusal included, but only a change is announced.
    pub fn record_unsuccessful_appraisal(
        &mut self,
        item_id: u32,
        now_seconds: f64,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        item.last_appraisal_unsuccessful_at_seconds = now_seconds;
        if item.appraisal_answered && item.last_appraisal_unsuccessful {
            return true;
        }
        item.appraisal_answered = true;
        item.last_appraisal_unsuccessful = true;
        self.emit_updated(item_id, events);
        true
    }

    /// Merges a property bundle into an object, creating the object when
    /// the table does not hold it.
    pub fn upsert_properties(
        &mut self,
        guid: u32,
        incoming: &PropertyBundle,
        events: &mut Vec<TableEvent>,
    ) {
        let existed = self.ensure(guid);
        if let Some(item) = self.objects.get_mut(&guid) {
            server_showed_object(item);
            merge_properties(item, incoming);
            apply_cooldown_properties(item, incoming);
        }
        self.emit_added_or_updated(guid, existed, events);
    }

    /// Applies one int property update. The ones the table also keeps as
    /// fields (pack capacities, icon effects, cooldown group, worn location,
    /// hook, structure,
    /// player-killer status) are mirrored there.
    pub fn update_int_property(
        &mut self,
        item_id: u32,
        property_id: u32,
        value: i32,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        server_showed_object(item);
        let previous = ClientObjectPlacement::of(item);
        item.properties.ints.insert(property_id, value);
        let bits = value as u32;
        match PropertyInt(property_id) {
            PropertyInt::ITEMS_CAPACITY => item.items_capacity = value,
            PropertyInt::CONTAINERS_CAPACITY => item.containers_capacity = value,
            PropertyInt::UI_EFFECTS => item.effects = bits,
            PropertyInt::SHARED_COOLDOWN => item.cooldown_id = Some(bits),
            PropertyInt::CURRENT_WIELDED_LOCATION => {
                item.currently_equipped_location = EquipMask(bits);
            }
            PropertyInt::HOOK_TYPE => item.hook_type = bits,
            PropertyInt::HOOK_ITEM_TYPE => item.hook_item_types = bits,
            PropertyInt::MAX_STRUCTURE => item.max_structure = value,
            PropertyInt::STRUCTURE => item.structure = value,
            PropertyInt::PLAYER_KILLER_STATUS => {
                let bitfield = item.public_weenie_bitfield.unwrap_or_default().bits();
                item.public_weenie_bitfield =
                    Some(PublicWeenieFlags(pk_status::apply(bitfield, value)));
            }
            _ => {}
        }
        if property_id == PropertyInt::CURRENT_WIELDED_LOCATION.0 {
            let current = ClientObjectPlacement::of(item);
            update_equipment_index(&mut self.equipment_index, item_id, &previous, &current);
        }
        self.emit_updated(item_id, events);
        true
    }

    /// Applies one dat-id property update; the three icon ids are mirrored
    /// into their fields.
    pub fn update_data_id_property(
        &mut self,
        item_id: u32,
        property_id: u32,
        value: u32,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        server_showed_object(item);
        item.properties.data_ids.insert(property_id, value);
        match PropertyDataId(property_id) {
            PropertyDataId::ICON => item.icon_id = value,
            PropertyDataId::ICON_OVERLAY => item.icon_overlay_id = value,
            PropertyDataId::ICON_UNDERLAY => item.icon_underlay_id = value,
            _ => {}
        }
        self.emit_updated(item_id, events);
        true
    }

    /// Applies one string property update. The value goes into the property
    /// bag only: the shown name changes only when the object is created or
    /// updated, even when this is the name property.
    pub fn update_string_property(
        &mut self,
        item_id: u32,
        property_id: u32,
        value: &str,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        server_showed_object(item);
        item.properties
            .strings
            .insert(property_id, value.to_owned());
        self.emit_updated(item_id, events);
        true
    }

    /// Records one object-id property update. Placement has its own
    /// messages, so this stores the value, and a house's owner (which a
    /// purchase or an abandon announces this way) also moves the owner the
    /// entry gate reads; 0 means no owner. The monarch (which swearing or
    /// breaking allegiance announces this way) moves the object's monarch,
    /// the one its description carries; 0 means none.
    pub fn update_instance_id_property(
        &mut self,
        item_id: u32,
        property_id: u32,
        value: u32,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        server_showed_object(item);
        item.properties.instance_ids.insert(property_id, value);
        let mut authority_changes = 0;
        let id = (value != 0).then_some(value);
        if PropertyInstanceId(property_id) == PropertyInstanceId::HOUSE_OWNER
            && item.house_owner_id != id
        {
            item.house_owner_id = id;
            authority_changes = 1;
        }
        if PropertyInstanceId(property_id) == PropertyInstanceId::MONARCH && item.monarch_id != id {
            item.monarch_id = id;
            authority_changes = 1;
        }
        self.mutation_revision = self.mutation_revision.wrapping_add(authority_changes);
        self.emit_updated(item_id, events);
        true
    }

    /// Records one true/false property update. Some of these are also a bit
    /// of the public description, which is what everything else reads
    /// (whether the object can be picked or shown on the radar, whether a
    /// container can be opened and how many slots its window shows), so the
    /// bit moves with the bool. Locked is the inverse of openable: a key or
    /// a lockpick unlocking a chest makes it openable.
    pub fn update_bool_property(
        &mut self,
        item_id: u32,
        property_id: u32,
        value: bool,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        server_showed_object(item);
        item.properties.bools.insert(property_id, value);
        if let Some((bit, inverted)) = description_bit_of(PropertyBool(property_id)) {
            let mut bitfield = item.public_weenie_bitfield.unwrap_or_default();
            bitfield.set(bit, value != inverted);
            item.public_weenie_bitfield = Some(bitfield);
        }
        self.emit_updated(item_id, events);
        true
    }

    /// Records one 64-bit int property update.
    pub fn update_int64_property(
        &mut self,
        item_id: u32,
        property_id: u32,
        value: i64,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&item_id) else {
            return false;
        };
        server_showed_object(item);
        item.properties.int64s.insert(property_id, value);
        self.emit_updated(item_id, events);
        true
    }

    /// The server's answer to a stack request: new size and value.
    pub fn update_stack_size(
        &mut self,
        guid: u32,
        stack_size: i32,
        value: i32,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&guid) else {
            return false;
        };
        server_showed_object(item);
        item.stack_size = stack_size;
        item.value = value;
        let identity = item.identity();
        self.emit_updated(guid, events);
        events.push(TableEvent::StackSizeUpdated {
            object_id: guid,
            identity,
        });
        true
    }

    /// A house object's new entry restrictions.
    pub fn update_house_restrictions(
        &mut self,
        guid: u32,
        restrictions: HouseRestrictionRecord,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        let Some(item) = self.objects.get_mut(&guid) else {
            return false;
        };
        server_showed_object(item);
        if item.restrictions.as_ref() != Some(&restrictions) {
            item.restrictions = Some(restrictions);
            self.mutation_revision = self.mutation_revision.wrapping_add(1);
        }
        self.emit_updated(guid, events);
        true
    }

    /// Applies an object description (create or update): creates the
    /// object if new, otherwise patches only the fields the description
    /// carries (the icon effects always apply) and keeps its properties.
    /// A placement the server confirmed before the description arrived is
    /// applied now when the description puts the object in that same
    /// container (the placement adds its slot); a description that places
    /// it elsewhere (another container, a wielder) is newer than that
    /// placement, which is dropped.
    pub fn ingest(&mut self, data: &WeenieData, events: &mut Vec<TableEvent>) -> ObjectIdentity {
        let guid = data.guid;
        let (item, existed) = ensure_record(&mut self.objects, &mut self.next_identity, guid);
        server_showed_object(item);
        let old_container = item.container_id;
        let previous = ClientObjectPlacement::of(item);
        let authority_changes = patch_from_weenie(item, data);
        let current = ClientObjectPlacement::of(item);
        self.mutation_revision = self.mutation_revision.wrapping_add(authority_changes);

        let changed = remove_from_other_container_indexes(
            &mut self.container_index,
            guid,
            current.container_id,
        );
        reindex(
            &mut self.container_index,
            &self.objects,
            guid,
            old_container,
        );
        update_equipment_index(&mut self.equipment_index, guid, &previous, &current);
        self.emit_added_or_updated(guid, existed, events);
        publish_container_contents_changes(&changed, events);

        // A placement kept for a record removed earlier (the giver's
        // item, which the server named in the receiver's pack) must not be
        // laid over the description of where the object is now.
        if !existed
            && let Some((placement, hint)) = self.pending_unresolved.remove(&guid)
            && data.container_id == Some(placement.container_id)
        {
            self.apply_server_move(guid, placement, hint, events);
        }
        self.identity_of(guid)
    }

    /// A re-sent description of an object the client holds (a revealed
    /// Aetheria, a tinkered or tailored item). The game deletes the object
    /// and creates it again: deleting takes it out of its container's
    /// list, so the container the new description names has no place for
    /// it and puts it first among its own kind (items or packs). A
    /// re-sent item therefore lands in its pack's top-left slot. A
    /// description that names no container leaves the object where it is.
    pub fn ingest_recreated(
        &mut self,
        data: &WeenieData,
        events: &mut Vec<TableEvent>,
    ) -> ObjectIdentity {
        let identity = self.ingest(data, events);
        if let Some(container_id) = data.container_id.filter(|&id| id != 0) {
            self.apply_server_move(
                data.guid,
                ClientObjectPlacement::in_container(container_id, 0),
                None,
                events,
            );
        }
        identity
    }

    /// Records an object's membership from the character description: its
    /// container (when non-zero) and worn location (which clears the
    /// slot), plus container type and priority when given.
    pub fn record_membership(
        &mut self,
        guid: u32,
        container_id: u32,
        equip: EquipMask,
        container_type_hint: Option<u32>,
        priority: Option<u32>,
        events: &mut Vec<TableEvent>,
    ) -> ObjectIdentity {
        let (item, existed) = ensure_record(&mut self.objects, &mut self.next_identity, guid);
        server_showed_object(item);
        let old_container = item.container_id;
        let previous = ClientObjectPlacement::of(item);
        if container_id != 0 {
            item.container_id = container_id;
        }
        item.currently_equipped_location = equip;
        if equip != EquipMask::NONE {
            item.container_slot = -1;
        }
        if let Some(hint) = container_type_hint {
            item.container_type_hint = hint;
        }
        if let Some(priority) = priority {
            item.priority = priority;
        }
        let current = ClientObjectPlacement::of(item);
        let changed = remove_from_other_container_indexes(
            &mut self.container_index,
            guid,
            current.container_id,
        );
        reindex(
            &mut self.container_index,
            &self.objects,
            guid,
            old_container,
        );
        update_equipment_index(&mut self.equipment_index, guid, &previous, &current);
        self.emit_added_or_updated(guid, existed, events);
        publish_container_contents_changes(&changed, events);
        self.identity_of(guid)
    }

    /// The character description's equipment list for `wielder_id`, in
    /// order: each entry is worn as listed, and anything the wielder wore
    /// before that the list leaves out is taken off (and placed nowhere).
    pub fn initialize_equipment_manifest(
        &mut self,
        wielder_id: u32,
        entries: &[EquipmentManifestEntry],
        events: &mut Vec<TableEvent>,
    ) {
        if wielder_id == 0 {
            return;
        }
        let incoming: BTreeSet<u32> = entries.iter().map(|entry| entry.guid).collect();
        let mut ordered = Vec::with_capacity(entries.len());
        let mut notifications = Vec::new();
        let mut changed = Vec::new();

        let prior_equipment = self
            .equipment_index
            .get(&wielder_id)
            .cloned()
            .unwrap_or_default();
        for prior_id in prior_equipment {
            if incoming.contains(&prior_id) {
                continue;
            }
            let Some(prior) = self.objects.get_mut(&prior_id) else {
                continue;
            };
            let worn_by_wielder = prior.wielder_id == wielder_id
                || (prior.container_id == wielder_id
                    && prior.currently_equipped_location != EquipMask::NONE);
            if !worn_by_wielder {
                continue;
            }
            prior.container_id = 0;
            prior.container_slot = -1;
            prior.wielder_id = 0;
            prior.currently_equipped_location = EquipMask::NONE;
            prior.priority = 0;
            changed.extend(remove_from_other_container_indexes(
                &mut self.container_index,
                prior_id,
                0,
            ));
            notifications.push((prior_id, true));
        }

        for entry in entries {
            ordered.push(entry.guid);
            let (item, existed) =
                ensure_record(&mut self.objects, &mut self.next_identity, entry.guid);
            server_showed_object(item);
            let previous = ClientObjectPlacement::of(item);
            item.container_id = 0;
            item.container_slot = -1;
            item.wielder_id = wielder_id;
            item.currently_equipped_location = entry.equip_location;
            item.priority = entry.priority;
            changed.extend(remove_from_other_container_indexes(
                &mut self.container_index,
                entry.guid,
                0,
            ));
            reindex(
                &mut self.container_index,
                &self.objects,
                entry.guid,
                previous.container_id,
            );
            remove_equipment_index_membership(&mut self.equipment_index, entry.guid);
            notifications.push((entry.guid, existed));
        }

        self.equipment_index.insert(wielder_id, ordered);
        for (object_id, existed) in notifications {
            self.emit_added_or_updated(object_id, existed, events);
        }
        publish_container_contents_changes(&changed, events);
    }

    /// The character description's inventory list for `owner_id`, in
    /// order: each entry goes into the owner at its list position, and
    /// anything the owner held before that the list leaves out is placed
    /// nowhere.
    pub fn initialize_inventory_manifest(
        &mut self,
        owner_id: u32,
        entries: &[ContainerContentEntry],
        events: &mut Vec<TableEvent>,
    ) {
        if owner_id == 0 {
            return;
        }
        let incoming: BTreeSet<u32> = entries.iter().map(|entry| entry.guid).collect();
        let mut ordered = Vec::with_capacity(entries.len());
        let mut changed = Vec::new();
        let mut notifications = Vec::new();

        let prior_inventory = self
            .container_index
            .get(&owner_id)
            .cloned()
            .unwrap_or_default();
        for prior_id in prior_inventory {
            if incoming.contains(&prior_id) {
                continue;
            }
            let Some(prior) = self.objects.get_mut(&prior_id) else {
                continue;
            };
            if prior.container_id != owner_id {
                continue;
            }
            let previous = ClientObjectPlacement::of(prior);
            prior.container_id = 0;
            prior.container_slot = -1;
            prior.wielder_id = 0;
            prior.currently_equipped_location = EquipMask::NONE;
            prior.priority = 0;
            let current = ClientObjectPlacement::of(prior);
            update_equipment_index(&mut self.equipment_index, prior_id, &previous, &current);
            notifications.push((prior_id, true));
        }

        for (position, entry) in entries.iter().enumerate() {
            ordered.push(entry.guid);
            let (item, existed) =
                ensure_record(&mut self.objects, &mut self.next_identity, entry.guid);
            server_showed_object(item);
            let previous = ClientObjectPlacement::of(item);
            item.container_id = owner_id;
            item.container_slot = i32::try_from(position).unwrap_or(i32::MAX);
            item.wielder_id = 0;
            item.currently_equipped_location = EquipMask::NONE;
            item.container_type_hint = entry.container_type;
            let current = ClientObjectPlacement::of(item);
            changed.extend(remove_from_other_container_indexes(
                &mut self.container_index,
                entry.guid,
                owner_id,
            ));
            update_equipment_index(&mut self.equipment_index, entry.guid, &previous, &current);
            notifications.push((entry.guid, existed));
        }

        self.container_index.insert(owner_id, ordered);
        self.described_containers.insert(owner_id);
        for (object_id, existed) in notifications {
            self.emit_added_or_updated(object_id, existed, events);
        }
        events.push(TableEvent::ContainerContentsReplaced(owner_id));
        publish_container_contents_changes(&changed, events);
    }

    /// A container's contents as the server listed them (opening a chest,
    /// a corpse, a pack). The list replaces the container's list; the
    /// listed objects' own placements are left alone, unknown ones are
    /// created, and each records its container type.
    pub fn replace_contents(
        &mut self,
        container_id: u32,
        entries: &[ContainerContentEntry],
        events: &mut Vec<TableEvent>,
    ) {
        if container_id == 0 {
            return;
        }
        let mut ordered = Vec::with_capacity(entries.len());
        let mut added = Vec::new();
        let mut shown = Vec::new();
        for entry in entries {
            ordered.push(entry.guid);
            let (item, existed) =
                ensure_record(&mut self.objects, &mut self.next_identity, entry.guid);
            item.container_type_hint = entry.container_type;
            if !existed {
                added.push(entry.guid);
            } else if server_showed_object(item) {
                // A listing names only what the server has. An object it
                // names is otherwise not republished here, so one whose
                // refusal this listing overturns is, for readers watching
                // that flag.
                shown.push(entry.guid);
            }
        }
        self.container_index.insert(container_id, ordered);
        self.described_containers.insert(container_id);
        for object_id in added {
            self.emit_added_or_updated(object_id, false, events);
        }
        for object_id in shown {
            self.emit_updated(object_id, events);
        }
        events.push(TableEvent::ContainerContentsReplaced(container_id));
    }

    /// [`replace_contents`](Self::replace_contents) with every entry an
    /// ordinary item (container type 0).
    pub fn replace_contents_ids(
        &mut self,
        container_id: u32,
        guids: &[u32],
        events: &mut Vec<TableEvent>,
    ) {
        let entries: Vec<ContainerContentEntry> = guids
            .iter()
            .map(|&guid| ContainerContentEntry {
                guid,
                container_type: 0,
            })
            .collect();
        self.replace_contents(container_id, &entries, events);
    }

    /// Drops a container's contents list (the player stopped viewing it);
    /// the objects stay.
    pub fn stop_viewing_contents(
        &mut self,
        container_id: u32,
        events: &mut Vec<TableEvent>,
    ) -> bool {
        if container_id == 0 {
            return false;
        }
        let Some(contents) = self.container_index.remove(&container_id) else {
            return false;
        };
        self.described_containers.remove(&container_id);
        events.push(TableEvent::ContentsViewEnded {
            container_id,
            contents,
            nested: false,
        });
        events.push(TableEvent::ContainerContentsReplaced(container_id));
        true
    }

    /// Drops the contents list of a container and of every container listed
    /// inside it, recursively; returns how many lists were dropped.
    pub fn stop_viewing_contents_tree(
        &mut self,
        root_container_id: u32,
        events: &mut Vec<TableEvent>,
    ) -> usize {
        if root_container_id == 0 {
            return 0;
        }
        let mut pending = vec![root_container_id];
        let mut visited = BTreeSet::new();
        let mut removed = Vec::new();
        while let Some(container_id) = pending.pop() {
            if !visited.insert(container_id) {
                continue;
            }
            let Some(contents) = self.container_index.remove(&container_id) else {
                continue;
            };
            self.described_containers.remove(&container_id);
            pending.extend(
                contents
                    .iter()
                    .copied()
                    .filter(|child| self.container_index.contains_key(child)),
            );
            removed.push((container_id, contents));
        }
        let ids: Vec<u32> = removed.iter().map(|(id, _)| *id).collect();
        for (container_id, contents) in removed {
            events.push(TableEvent::ContentsViewEnded {
                container_id,
                contents,
                nested: container_id != root_container_id,
            });
        }
        for &container_id in &ids {
            events.push(TableEvent::ContainerContentsReplaced(container_id));
        }
        ids.len()
    }

    /// Drops every object, list and pending move (session end, teleport).
    pub fn clear(&mut self, events: &mut Vec<TableEvent>) {
        self.objects.clear();
        self.container_index.clear();
        self.described_containers.clear();
        self.equipment_index.clear();
        self.pending_moves.clear();
        self.pending_unresolved.clear();
        self.clears = self.clears.wrapping_add(1);
        emit(&mut self.mutation_revision, events, TableEvent::Cleared);
    }
}

/// The record for `object_id`, installed with a fresh identity when the
/// table has none; also whether it existed.
fn ensure_record<'a>(
    objects: &'a mut Objects,
    next_identity: &mut u64,
    object_id: u32,
) -> (&'a mut ClientObject, bool) {
    let existed = objects.contains_key(&object_id);
    let item = objects.entry(object_id).or_insert_with(|| {
        *next_identity += 1;
        let mut item = ClientObject::new(object_id);
        item.set_identity(ObjectIdentity(*next_identity));
        item
    });
    (item, existed)
}

/// The server has just said something about the object other than an
/// appraisal refusal, which shows it still has the object: an earlier
/// refusal no longer stands. Whether it stood.
fn server_showed_object(item: &mut ClientObject) -> bool {
    if !item.last_appraisal_unsuccessful {
        return false;
    }
    item.last_appraisal_unsuccessful = false;
    true
}

/// The public description bit a true/false property stands for, if any,
/// and whether the bit is set when the property is false.
fn description_bit_of(property: PropertyBool) -> Option<(PublicWeenieFlags, bool)> {
    match property {
        PropertyBool::STUCK => Some((PublicWeenieFlags::STUCK, false)),
        PropertyBool::LOCKED => Some((PublicWeenieFlags::OPENABLE, true)),
        PropertyBool::INSCRIBABLE => Some((PublicWeenieFlags::INSCRIBABLE, false)),
        PropertyBool::UI_HIDDEN => Some((PublicWeenieFlags::UI_HIDDEN, false)),
        PropertyBool::IGNORE_HOUSE_BARRIERS => {
            Some((PublicWeenieFlags::IMMUNE_CELL_RESTRICTIONS, false))
        }
        PropertyBool::HIDDEN_ADMIN => Some((PublicWeenieFlags::HIDDEN_ADMIN, false)),
        _ => None,
    }
}

/// Merges a property bundle; the pack capacities it carries (an
/// augmentation raising them) are mirrored into their fields, and an
/// omitted capacity keeps its value.
fn merge_properties(item: &mut ClientObject, incoming: &PropertyBundle) {
    item.properties.merge(incoming);
    if let Some(&items) = incoming.ints.get(&PropertyInt::ITEMS_CAPACITY.0) {
        item.items_capacity = items;
    }
    if let Some(&packs) = incoming.ints.get(&PropertyInt::CONTAINERS_CAPACITY.0) {
        item.containers_capacity = packs;
    }
}

fn apply_cooldown_properties(item: &mut ClientObject, properties: &PropertyBundle) {
    if let Some(&cooldown_id) = properties.ints.get(&PropertyInt::SHARED_COOLDOWN.0) {
        item.cooldown_id = Some(cooldown_id as u32);
    }
    if let Some(&duration) = properties.floats.get(&PropertyFloat::COOLDOWN_DURATION.0) {
        item.cooldown_duration = Some(duration);
    }
}

/// Copies the fields a description carries onto the record; returns how
/// many of house owner, monarch and restrictions changed.
fn patch_from_weenie(item: &mut ClientObject, d: &WeenieData) -> u64 {
    let mut changes = 0;
    if let Some(name) = d.name.as_deref().filter(|name| !name.is_empty()) {
        item.name = name.to_owned();
    }
    if let Some(plural) = d.plural_name.as_deref().filter(|name| !name.is_empty()) {
        item.plural_name = plural.to_owned();
    }
    if let Some(item_type) = d.item_type {
        item.item_type = item_type;
    }
    if d.weenie_class_id != 0 {
        item.weenie_class_id = d.weenie_class_id;
    }
    if d.icon_id != 0 {
        item.icon_id = d.icon_id;
    }
    if d.icon_overlay_id != 0 {
        item.icon_overlay_id = d.icon_overlay_id;
    }
    if d.icon_underlay_id != 0 {
        item.icon_underlay_id = d.icon_underlay_id;
    }
    item.effects = d.effects;
    set_if(&mut item.value, d.value);
    set_if(&mut item.stack_size, d.stack_size);
    set_if(&mut item.stack_size_max, d.stack_size_max);
    set_if(&mut item.burden, d.burden);
    set_if(&mut item.container_id, d.container_id);
    set_if(&mut item.wielder_id, d.wielder_id);
    set_if(&mut item.valid_locations, d.valid_locations);
    set_if(
        &mut item.currently_equipped_location,
        d.current_wielded_location,
    );
    set_if(&mut item.priority, d.priority);
    set_some_if(&mut item.useability, d.useability);
    set_some_if(&mut item.target_type, d.target_type);
    set_some_if(&mut item.public_weenie_bitfield, d.public_weenie_bitfield);
    set_if(&mut item.pet_owner_id, d.pet_owner_id);
    set_some_if(&mut item.combat_use, d.combat_use);
    set_some_if(&mut item.ammo_type, d.ammo_type);
    set_some_if(&mut item.spell_id, d.spell_id);
    set_some_if(&mut item.cooldown_id, d.cooldown_id);
    set_some_if(&mut item.cooldown_duration, d.cooldown_duration);
    set_some_if(&mut item.radar_blip_color, d.radar_blip_color);
    set_some_if(&mut item.radar_behavior, d.radar_behavior);
    set_if(&mut item.items_capacity, d.items_capacity);
    set_if(&mut item.containers_capacity, d.containers_capacity);
    set_if(&mut item.hook_item_types, d.hook_item_types);
    set_if(&mut item.hook_type, d.hook_type);
    set_if(&mut item.structure, d.structure);
    set_if(&mut item.max_structure, d.max_structure);
    set_if(&mut item.workmanship, d.workmanship);
    set_some_if(&mut item.material_type, d.material_type);
    set_some_if(&mut item.header, d.header);
    if let Some(owner) = d.house_owner_id
        && item.house_owner_id != Some(owner)
    {
        item.house_owner_id = Some(owner);
        changes += 1;
    }
    if let Some(monarch) = d.monarch_id
        && item.monarch_id != Some(monarch)
    {
        item.monarch_id = Some(monarch);
        changes += 1;
    }
    if let Some(restrictions) = &d.restrictions
        && item.restrictions.as_ref() != Some(restrictions)
    {
        item.restrictions = Some(restrictions.clone());
        changes += 1;
    }
    changes
}

fn set_if<T>(field: &mut T, value: Option<T>) {
    if let Some(value) = value {
        *field = value;
    }
}

fn set_some_if<T>(field: &mut Option<T>, value: Option<T>) {
    if value.is_some() {
        *field = value;
    }
}

#[cfg(test)]
#[path = "table_tests.rs"]
mod tests;
