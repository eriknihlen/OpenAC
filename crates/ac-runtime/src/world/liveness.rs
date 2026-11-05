//! Objects that left the player's surroundings are forgotten 25 seconds
//! later. The game client drops an object that long after the cell holding
//! it is released, and takes it off the list again when the cell comes
//! back; the server keeps the same 25-second clock and sends the object
//! again when the player returns, without a delete. A client that never
//! expired anything would keep every corpse, item and creature it saw.
//!
//! Outdoors the surroundings are the player's landblock and its eight
//! neighbours. Inside a sealed dungeon cell (an indoor cell that does not
//! see outside) they are the player's cell and the cells on its visible
//! list. An object held by a parent or wielder in the world leaves and
//! returns with its holder; a holder that is gone, or not in the world
//! itself, keeps what it held (a deleted holder only detaches it).
//!
//! Objects outside the world (inside a container) are on a destruction
//! queue with the same 25-second deadline, fed where the game client feeds
//! it: the contents of a container the player stops looking at (a container
//! nested in it keeps its list until it is destroyed itself), the contents
//! of a destroyed container, an object the server moves into a container
//! the player neither owns, has open, nor trades with, and what the trade
//! partner's items hold when they leave the trade window. A create for the
//! object, a move into the pack, the open container or the trade, an offer
//! by the trade partner and a vendor's list take it off again. The server
//! destroys container contents without a message, so without the queue
//! every corpse item ever looked at would be kept for the whole session.

use std::collections::{HashMap, HashSet};

use ac_core::items::{EquipMask, MoveOrigin, RemovalReason, TableEvent};

use super::{ObjectKey, World};

/// Seconds an object out of the surroundings lives on.
pub const DESTRUCTION_TIMEOUT_SECONDS: f64 = 25.0;

/// The surroundings outdoors: landblocks within this many of the player's,
/// in both directions (a 3 x 3 square).
pub const VISIBLE_LANDBLOCK_RADIUS: i32 = 1;

/// The checks run at most once per this many seconds.
const MAINTENANCE_INTERVAL_SECONDS: f64 = 1.0;

/// One object as one check sees it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LivenessSample {
    /// The object.
    pub key: ObjectKey,
    /// Its server id.
    pub guid: u32,
    /// It is in the surroundings.
    pub visible: bool,
    /// A parent, container or wielder holds it.
    pub retained: bool,
}

/// An object whose time ran out.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ExpiryCandidate {
    /// The object.
    pub key: ObjectKey,
    /// Its server id.
    pub guid: u32,
    /// The incarnation to delete.
    pub generation: u16,
}

/// The deadlines of objects out of the surroundings.
#[derive(Debug, Default)]
pub struct LivenessTracker {
    deadlines: HashMap<ObjectKey, f64>,
}

impl LivenessTracker {
    /// How many objects have a deadline.
    #[must_use]
    pub fn deadline_count(&self) -> usize {
        self.deadlines.len()
    }

    /// One check at `now` (seconds) over every object: one in the
    /// surroundings or held loses its deadline, one out of them gets one 25
    /// seconds out, and one whose deadline has come is due (and loses it).
    /// Deadlines of objects no longer sampled are dropped.
    pub fn tick(&mut self, now: f64, samples: &[LivenessSample]) -> Vec<ExpiryCandidate> {
        let mut due = Vec::new();
        let mut present = HashSet::new();
        for sample in samples {
            present.insert(sample.key);
            if sample.visible || sample.retained {
                self.deadlines.remove(&sample.key);
                continue;
            }
            let Some(&expires_at) = self.deadlines.get(&sample.key) else {
                self.deadlines
                    .insert(sample.key, now + DESTRUCTION_TIMEOUT_SECONDS);
                continue;
            };
            if expires_at > now {
                continue;
            }
            due.push(ExpiryCandidate {
                key: sample.key,
                guid: sample.guid,
                generation: sample.key.incarnation,
            });
            self.deadlines.remove(&sample.key);
        }
        self.deadlines.retain(|key, _| present.contains(key));
        due
    }

    /// Forget every deadline.
    pub fn clear(&mut self) {
        self.deadlines.clear();
    }
}

/// Whether two cells' landblocks are within one landblock of each other in
/// both directions (only the landblock halves of the ids matter).
#[must_use]
pub fn is_within_visible_landblocks(player_cell: u32, entity_cell: u32) -> bool {
    let x = |cell: u32| i32::from((cell >> 24) as u8);
    let y = |cell: u32| i32::from((cell >> 16) as u8);
    let dx = (x(player_cell) - x(entity_cell)).abs();
    let dy = (y(player_cell) - y(entity_cell)).abs();
    dx.max(dy) <= VISIBLE_LANDBLOCK_RADIUS
}

/// The indoor cell the session's physics has the player in.
#[derive(Debug, Clone, Copy)]
pub struct IndoorCell<'a> {
    /// Its full id.
    pub id: u32,
    /// It sees outside.
    pub seen_outside: bool,
    /// The cells visible from it (full ids).
    pub visible_cells: &'a [u32],
}

/// The cells whose objects stay alive.
#[derive(Debug, Default)]
pub struct VisibleCellSet {
    sealed_cells: HashSet<u32>,
    sealed_cell_id: u32,
    player_cell_id: u32,
    is_sealed_dungeon: bool,
}

impl VisibleCellSet {
    /// Whether the player is in a sealed dungeon cell.
    #[must_use]
    pub fn is_sealed_dungeon(&self) -> bool {
        self.is_sealed_dungeon
    }

    /// Follow the player: `physics_cell` is the indoor cell the physics has
    /// the player in (none outdoors or when unknown), `player_cell_id` the
    /// player's cell as the world records it. The physics cell can lag a
    /// landblock behind while the destination loads; nothing is judged per
    /// cell until the two name the same landblock.
    pub fn update(&mut self, physics_cell: Option<IndoorCell<'_>>, player_cell_id: u32) {
        self.player_cell_id = player_cell_id;
        let sealed = physics_cell
            .filter(|c| !c.seen_outside && (c.id & 0xFFFF_0000) == (player_cell_id & 0xFFFF_0000));
        let Some(cell) = sealed else {
            self.is_sealed_dungeon = false;
            self.sealed_cell_id = 0;
            self.sealed_cells.clear();
            return;
        };
        self.is_sealed_dungeon = true;
        if self.sealed_cell_id == cell.id {
            return;
        }
        self.sealed_cell_id = cell.id;
        self.sealed_cells.clear();
        self.sealed_cells.insert(cell.id);
        self.sealed_cells.extend(cell.visible_cells.iter().copied());
    }

    /// Whether an object in `entity_cell` is in the surroundings.
    #[must_use]
    pub fn contains(&self, entity_cell: u32) -> bool {
        if self.is_sealed_dungeon {
            self.sealed_cells.contains(&entity_cell)
        } else {
            is_within_visible_landblocks(self.player_cell_id, entity_cell)
        }
    }
}

/// Something the destruction queue follows, in the order it happened.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum QueueNote {
    /// The client stopped viewing a container; `nested` for a container
    /// inside the one the player closed.
    ViewEnded {
        /// The container.
        container: u32,
        /// The list the view held.
        contents: Vec<u32>,
        /// Inside the container the player closed.
        nested: bool,
    },
    /// An object left the item table; `replaced` when a newer incarnation
    /// took its place.
    Removed {
        /// The object.
        guid: u32,
        /// A newer incarnation replaced it.
        replaced: bool,
    },
    /// The server put a known object into a container.
    MovedIntoContainer(u32),
    /// A create for the object landed (new, or described again).
    Spawned(u32),
    /// The item table was emptied.
    Cleared,
    /// The trade partner put an item into the trade.
    PartnerItemAdded(u32),
    /// The partner's items left the trade window (taken back, or the trade
    /// reset or closed).
    PartnerItemsReleased(Vec<u32>),
    /// The vendor's window lists these items.
    VendorListed(Vec<u32>),
}

impl QueueNote {
    /// The note an item table event makes, if the queue follows it.
    #[must_use]
    pub fn of(event: &TableEvent) -> Option<Self> {
        Some(match event {
            TableEvent::ContentsViewEnded {
                container_id,
                contents,
                nested,
            } => Self::ViewEnded {
                container: *container_id,
                contents: contents.clone(),
                nested: *nested,
            },
            TableEvent::Removed { object, reason, .. } => Self::Removed {
                guid: object.object_id(),
                replaced: *reason == RemovalReason::GenerationReplacement,
            },
            TableEvent::Moved(m)
                if m.origin == MoveOrigin::AuthoritativeResponse
                    && m.current.container_id != 0
                    && m.identity.is_some() =>
            {
                Self::MovedIntoContainer(m.item_id)
            }
            TableEvent::Cleared => Self::Cleared,
            _ => return None,
        })
    }
}

/// Where an object stays although it is in a container the player does
/// not own: the viewed ground container and the open trade.
#[derive(Debug, Clone, Copy, Default)]
pub struct KeptPlaces<'a> {
    /// The ground container the player has open; 0 for none.
    pub ground_container: u32,
    /// The trade partner while a trade is open; 0 for none.
    pub trade_partner: u32,
    /// What the partner put into the trade.
    pub partner_items: &'a [u32],
}

/// Containers nest one level in practice; the bound only guards a
/// malformed list that names its own ancestor.
const MAX_NESTING: u32 = 8;

/// Runs the checks over the world, once a second, and keeps the
/// destruction queue.
#[derive(Debug, Default)]
pub struct LivenessController {
    tracker: LivenessTracker,
    visible: VisibleCellSet,
    next_maintenance_at: f64,
    /// Objects outside the world and when they go.
    queued: HashMap<u32, f64>,
    /// The viewed lists of containers nested in one the player closed: the
    /// game client keeps such a list on its container until that container
    /// is destroyed, and only then queues what it still holds.
    nested_lists: HashMap<u32, Vec<u32>>,
    /// The time of the last pass (the queue's deadlines count from it).
    now: f64,
}

impl LivenessController {
    /// The deadlines kept.
    #[must_use]
    pub fn tracker(&self) -> &LivenessTracker {
        &self.tracker
    }

    /// How many objects outside the world wait on their deadline.
    #[must_use]
    pub fn queued_object_count(&self) -> usize {
        self.queued.len()
    }

    /// The visibility check alone at `now` (session seconds) when a second
    /// has passed since the last: the objects whose time ran out, still
    /// current in `world`. Nothing while the player has no place.
    pub fn tick(
        &mut self,
        now: f64,
        world: &World,
        physics_cell: Option<IndoorCell<'_>>,
    ) -> Vec<ExpiryCandidate> {
        self.now = now;
        if now < self.next_maintenance_at {
            return Vec::new();
        }
        self.next_maintenance_at = now + MAINTENANCE_INTERVAL_SECONDS;
        self.expired(now, world, physics_cell)
    }

    /// One pass at `now`: the queue follows what happened, and when a
    /// second has passed since the last check the queued objects whose
    /// deadline came are destroyed and the objects out of the surroundings
    /// for 25 seconds expire, both through the world's delete. Returns
    /// every object of the world removed so.
    pub fn pass(
        &mut self,
        now: f64,
        world: &mut World,
        physics_cell: Option<IndoorCell<'_>>,
        kept: KeptPlaces<'_>,
    ) -> Vec<ExpiryCandidate> {
        // What happened since the last pass happened at its time.
        self.follow(world, kept);
        self.now = now;
        if now < self.next_maintenance_at {
            return Vec::new();
        }
        self.next_maintenance_at = now + MAINTENANCE_INTERVAL_SECONDS;
        let mut removed = self.destroy_queued(now, world, kept);
        for candidate in self.expired(now, world, physics_cell) {
            world.expire(candidate);
            if world
                .object(candidate.guid)
                .is_none_or(|o| o.key() != candidate.key)
            {
                removed.push(candidate);
            }
        }
        self.follow(world, kept);
        removed
    }

    /// Follow what the world noted since the last call, oldest first.
    pub fn follow(&mut self, world: &mut World, kept: KeptPlaces<'_>) {
        for note in world.take_queue_notes() {
            self.follow_note(note, world, kept);
        }
    }

    fn follow_note(&mut self, note: QueueNote, world: &World, kept: KeptPlaces<'_>) {
        let player = world.player_guid().unwrap_or(0);
        match note {
            QueueNote::ViewEnded {
                container,
                contents,
                nested,
            } => {
                if nested {
                    self.nested_lists.insert(container, contents);
                    return;
                }
                for guid in contents {
                    self.enqueue(guid, player);
                }
            }
            QueueNote::Removed { guid, replaced } => {
                self.queued.remove(&guid);
                if replaced {
                    return;
                }
                // What the destroyed container still holds, and is not in
                // the world itself, follows it 25 seconds later.
                let contents = self.viewed_contents(world, guid).to_vec();
                self.nested_lists.remove(&guid);
                for member in contents {
                    if world.item(member).is_some_and(|i| i.container_id != guid) {
                        continue;
                    }
                    if world
                        .object(member)
                        .and_then(|o| o.position())
                        .is_some_and(|p| p.cell_id != 0)
                    {
                        continue;
                    }
                    self.enqueue(member, player);
                }
            }
            QueueNote::MovedIntoContainer(guid) => {
                if is_kept_where_it_is(world, guid, player, kept) {
                    self.queued.remove(&guid);
                    self.dequeue_contents(world, guid, 0);
                } else {
                    self.enqueue_contents(world, guid, player);
                    self.enqueue(guid, player);
                }
            }
            QueueNote::Spawned(guid) => {
                self.queued.remove(&guid);
                for member in self.viewed_contents(world, guid).to_vec() {
                    self.queued.remove(&member);
                }
            }
            QueueNote::Cleared => {
                self.queued.clear();
                self.nested_lists.clear();
            }
            QueueNote::PartnerItemAdded(guid) => {
                // It and what it holds stay.
                self.queued.remove(&guid);
                self.dequeue_contents(world, guid, 0);
            }
            QueueNote::PartnerItemsReleased(items) => {
                // What they hold is queued; the items themselves stay where
                // the server put them.
                for guid in items {
                    let Some(item) = world.item(guid) else {
                        continue;
                    };
                    if world.items().is_owned_by_object(guid, player)
                        || item.wielder_id != 0
                        || item.currently_equipped_location != EquipMask::NONE
                    {
                        continue;
                    }
                    self.enqueue_contents(world, guid, player);
                }
            }
            QueueNote::VendorListed(items) => {
                for guid in items {
                    self.queued.remove(&guid);
                }
            }
        }
    }

    /// The queued objects whose deadline came are destroyed, oldest guid
    /// first; a destroyed container queues what it still holds.
    fn destroy_queued(
        &mut self,
        now: f64,
        world: &mut World,
        kept: KeptPlaces<'_>,
    ) -> Vec<ExpiryCandidate> {
        let mut due: Vec<u32> = self
            .queued
            .iter()
            .filter(|&(_, &at)| at <= now)
            .map(|(&guid, _)| guid)
            .collect();
        due.sort_unstable();
        let mut removed = Vec::new();
        for guid in due {
            if self.queued.remove(&guid).is_none() || world.player_guid() == Some(guid) {
                continue;
            }
            if let Some(key) = world.object(guid).map(super::WorldObject::key) {
                let candidate = ExpiryCandidate {
                    key,
                    guid,
                    generation: key.incarnation,
                };
                world.expire(candidate);
                removed.push(candidate);
            } else {
                // Known only from a container's contents list, never created.
                world.remove_item(guid);
            }
            self.follow(world, kept);
        }
        removed
    }

    /// The objects out of the surroundings whose time ran out.
    fn expired(
        &mut self,
        now: f64,
        world: &World,
        physics_cell: Option<IndoorCell<'_>>,
    ) -> Vec<ExpiryCandidate> {
        let Some(player_guid) = world.player_guid() else {
            return Vec::new();
        };
        let Some(player_cell) = world
            .object(player_guid)
            .and_then(|p| p.position())
            .map(|p| p.cell_id)
            .filter(|cell| *cell != 0)
        else {
            return Vec::new();
        };
        self.visible.update(physics_cell, player_cell);
        let cell_of = |guid: u32| {
            world
                .object(guid)
                .and_then(|o| o.position())
                .map_or(0, |p| p.cell_id)
        };
        let samples: Vec<LivenessSample> = world
            .objects()
            .filter(|o| o.guid() != player_guid)
            .filter_map(|o| {
                let guid = o.guid();
                let sample = |visible, retained| LivenessSample {
                    key: o.key(),
                    guid,
                    visible,
                    retained,
                };
                // Placement is read from the item table, which server moves
                // keep current, never from the create.
                let placed = world.item(guid);
                if placed.is_some_and(|i| i.container_id != 0) {
                    // Container contents are on the queue instead.
                    return Some(sample(true, true));
                }
                if let Some(holder) = holder_of(world, o, placed) {
                    // A held object goes and comes with its holder; a holder
                    // that is gone, or not in the world itself, keeps it.
                    let holder_cell = cell_of(holder);
                    return Some(if holder != player_guid && holder_cell != 0 {
                        sample(self.visible.contains(holder_cell), false)
                    } else {
                        sample(true, true)
                    });
                }
                let cell = cell_of(guid);
                (cell != 0).then(|| sample(self.visible.contains(cell), false))
            })
            .collect();
        self.tracker
            .tick(now, &samples)
            .into_iter()
            .filter(|c| world.object(c.guid).is_some_and(|o| o.key() == c.key))
            .collect()
    }

    fn viewed_contents<'w>(&'w self, world: &'w World, container: u32) -> &'w [u32] {
        let viewed = world.items().get_contents(container);
        if !viewed.is_empty() {
            return viewed;
        }
        self.nested_lists
            .get(&container)
            .map_or(viewed, Vec::as_slice)
    }

    fn enqueue(&mut self, guid: u32, player: u32) {
        if guid == 0 || guid == player {
            return;
        }
        self.queued
            .insert(guid, self.now + DESTRUCTION_TIMEOUT_SECONDS);
    }

    fn enqueue_contents(&mut self, world: &World, container: u32, player: u32) {
        for member in self.viewed_contents(world, container).to_vec() {
            self.enqueue(member, player);
        }
    }

    fn dequeue_contents(&mut self, world: &World, container: u32, depth: u32) {
        if depth > MAX_NESTING {
            return;
        }
        for member in self.viewed_contents(world, container).to_vec() {
            self.queued.remove(&member);
            self.dequeue_contents(world, member, depth + 1);
        }
    }

    /// Forget everything (a new session generation).
    pub fn clear(&mut self) {
        *self = Self::default();
    }
}

/// Owned by the player, inside the container the player has open, or part
/// of the open trade.
fn is_kept_where_it_is(world: &World, guid: u32, player: u32, kept: KeptPlaces<'_>) -> bool {
    let items = world.items();
    if player != 0 && items.is_owned_by_object(guid, player) {
        return true;
    }
    if kept.ground_container != 0 && items.is_owned_by_object(guid, kept.ground_container) {
        return true;
    }
    kept.trade_partner != 0
        && (items.is_owned_by_object(guid, kept.trade_partner)
            || kept.partner_items.contains(&guid))
}

/// The object this one hangs from in the world: its physics parent, else
/// its wielder, else the parent it was created on.
fn holder_of(
    world: &World,
    object: &super::WorldObject,
    placed: Option<&ac_core::items::ClientObject>,
) -> Option<u32> {
    if let Some((parent, _)) = world.attachments().committed_parent(object.guid()) {
        return Some(parent);
    }
    let wielder = placed.map_or(0, |i| i.wielder_id);
    if wielder != 0 {
        return Some(wielder);
    }
    object.parent().map(|p| p.guid).filter(|&g| g != 0)
}

impl World {
    /// Forget an object whose time ran out: the same delete a server
    /// delete of that incarnation performs (physics and item record).
    pub fn expire(&mut self, candidate: ExpiryCandidate) {
        if self
            .object(candidate.guid)
            .is_none_or(|o| o.key() != candidate.key)
        {
            return;
        }
        self.apply(&ac_proto::Inbound::DeleteObject(ac_proto::DeleteObject {
            guid: candidate.guid,
            instance_sequence: candidate.generation,
        }));
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Runtime.Tests/Entities/RuntimeEntityLivenessTests.cs`
    //! and `tests/AcDream.Headless.Tests/HeadlessEntityLivenessTests.cs`.

    use super::*;
    use crate::world::tests::{create, position, stamps};
    use ac_proto::{Inbound, PublicWeenieDesc};

    const PLAYER: u32 = 0x5000_000A;

    fn key(local_id: u32, incarnation: u16) -> ObjectKey {
        ObjectKey {
            local_id,
            incarnation,
        }
    }

    fn sample(id: u32, generation: u16, visible: bool, retained: bool) -> LivenessSample {
        LivenessSample {
            key: key(id, generation),
            guid: id,
            visible,
            retained,
        }
    }

    #[test]
    fn an_object_out_of_range_expires_after_twenty_five_seconds() {
        let mut tracker = LivenessTracker::default();
        let samples = [sample(0x7000_0001, 4, false, false)];
        assert!(tracker.tick(10.0, &samples).is_empty());
        assert!(tracker.tick(34.999, &samples).is_empty());
        assert_eq!(
            tracker.tick(35.0, &samples),
            [ExpiryCandidate {
                key: key(0x7000_0001, 4),
                guid: 0x7000_0001,
                generation: 4,
            }]
        );
        assert_eq!(tracker.deadline_count(), 0);
    }

    #[test]
    fn coming_back_into_range_cancels_the_deadline() {
        let mut tracker = LivenessTracker::default();
        assert!(tracker.tick(0.0, &[sample(1, 1, false, false)]).is_empty());
        assert!(tracker.tick(20.0, &[sample(1, 1, true, false)]).is_empty());
        assert!(tracker.tick(40.0, &[sample(1, 1, false, false)]).is_empty());
        assert!(tracker.tick(64.9, &[sample(1, 1, false, false)]).is_empty());
        assert_eq!(tracker.tick(65.0, &[sample(1, 1, false, false)]).len(), 1);
    }

    #[test]
    fn being_held_cancels_the_deadline() {
        let mut tracker = LivenessTracker::default();
        assert!(tracker.tick(0.0, &[sample(1, 1, false, false)]).is_empty());
        assert!(tracker.tick(30.0, &[sample(1, 1, false, true)]).is_empty());
        assert_eq!(tracker.deadline_count(), 0);
    }

    #[test]
    fn a_reused_guid_gets_a_deadline_for_its_new_generation() {
        let mut tracker = LivenessTracker::default();
        assert!(tracker.tick(0.0, &[sample(1, 1, false, false)]).is_empty());
        assert!(tracker.tick(24.0, &[sample(1, 2, false, false)]).is_empty());
        assert!(tracker.tick(25.0, &[sample(1, 2, false, false)]).is_empty());
        assert_eq!(
            tracker.tick(49.0, &[sample(1, 2, false, false)]),
            [ExpiryCandidate {
                key: key(1, 2),
                guid: 1,
                generation: 2,
            }]
        );
    }

    #[test]
    fn a_removed_object_drops_its_deadline() {
        let mut tracker = LivenessTracker::default();
        assert!(tracker.tick(0.0, &[sample(1, 1, false, false)]).is_empty());
        assert!(tracker.tick(30.0, &[]).is_empty());
        assert_eq!(tracker.deadline_count(), 0);
    }

    #[test]
    fn the_surroundings_are_the_players_landblock_and_its_eight_neighbours() {
        let player = 0x3032_0001;
        assert!(is_within_visible_landblocks(player, 0x3032_00A7));
        assert!(is_within_visible_landblocks(player, 0x3132_0001));
        assert!(is_within_visible_landblocks(player, 0x2F31_0001));
        assert!(is_within_visible_landblocks(player, 0x3133_00FF));
        assert!(!is_within_visible_landblocks(player, 0x3232_0001));
        assert!(!is_within_visible_landblocks(player, 0x3034_0001));
        assert!(!is_within_visible_landblocks(player, 0x2E30_0001));
    }

    #[test]
    fn distance_inside_the_neighbourhood_does_not_matter() {
        // The far corner of a diagonal neighbour is about 543 m away.
        assert!(is_within_visible_landblocks(0x3032_0001, 0x3133_0001));
    }

    fn dungeon(id: u32, visible: &[u32]) -> IndoorCell<'_> {
        IndoorCell {
            id,
            seen_outside: false,
            visible_cells: visible,
        }
    }

    #[test]
    fn a_sealed_dungeon_keeps_the_players_cell_and_its_visible_cells() {
        let mut set = VisibleCellSet::default();
        set.update(
            Some(dungeon(0x01D9_0102, &[0x01D9_0103, 0x01D9_0110])),
            0x01D9_0102,
        );
        assert!(set.is_sealed_dungeon());
        assert!(set.contains(0x01D9_0102));
        assert!(set.contains(0x01D9_0103));
        assert!(set.contains(0x01D9_0110));
    }

    #[test]
    fn a_sealed_dungeon_drops_cells_of_its_landblock_off_the_visible_list() {
        let mut set = VisibleCellSet::default();
        set.update(Some(dungeon(0x01D9_0102, &[0x01D9_0103])), 0x01D9_0102);
        assert!(!set.contains(0x01D9_0140));
        assert!(!set.contains(0x01D9_FFFF));
        // The outdoor terrain above the dungeon is released as well.
        assert!(!set.contains(0x01D9_0001));
        assert!(!set.contains(0x01DA_0102));
    }

    #[test]
    fn moving_to_another_dungeon_cell_replaces_the_visible_list() {
        let mut set = VisibleCellSet::default();
        set.update(Some(dungeon(0x01D9_0102, &[0x01D9_0103])), 0x01D9_0102);
        assert!(set.contains(0x01D9_0103));
        set.update(Some(dungeon(0x01D9_0120, &[0x01D9_0121])), 0x01D9_0120);
        assert!(set.contains(0x01D9_0120));
        assert!(set.contains(0x01D9_0121));
        assert!(!set.contains(0x01D9_0102));
        assert!(!set.contains(0x01D9_0103));
    }

    #[test]
    fn a_dungeon_cell_uses_its_own_id_when_the_worlds_cell_lags_behind() {
        let mut set = VisibleCellSet::default();
        set.update(Some(dungeon(0x01D9_0120, &[0x01D9_0121])), 0x01D9_0102);
        assert!(set.contains(0x01D9_0120));
        assert!(!set.contains(0x01D9_0102));
    }

    #[test]
    fn a_dungeon_cell_of_another_landblock_is_not_judged_per_cell() {
        let mut set = VisibleCellSet::default();
        set.update(Some(dungeon(0x01D9_0120, &[0x01D9_0121])), 0x0A9B_0102);
        assert!(!set.is_sealed_dungeon());
        assert!(set.contains(0x0A9B_0140));
        assert!(set.contains(0x0A9B_0001));
        assert!(!set.contains(0x01D9_0120));
    }

    #[test]
    fn an_indoor_cell_seen_from_outside_keeps_the_landblock_neighbourhood() {
        let mut set = VisibleCellSet::default();
        let visible = [0x3032_0103];
        set.update(
            Some(IndoorCell {
                id: 0x3032_0102,
                seen_outside: true,
                visible_cells: &visible,
            }),
            0x3032_0102,
        );
        assert!(!set.is_sealed_dungeon());
        assert!(set.contains(0x3032_0140));
        assert!(set.contains(0x3032_0001));
        assert!(set.contains(0x3133_00FF));
        assert!(!set.contains(0x3232_0001));
    }

    #[test]
    fn an_unknown_player_cell_keeps_the_landblock_neighbourhood() {
        let mut set = VisibleCellSet::default();
        set.update(None, 0x3032_0001);
        assert!(!set.is_sealed_dungeon());
        assert!(set.contains(0x3032_00A7));
        assert!(set.contains(0x3133_0001));
        assert!(!set.contains(0x3232_0001));
    }

    #[test]
    fn leaving_the_dungeon_forgets_its_visible_list() {
        let mut set = VisibleCellSet::default();
        set.update(Some(dungeon(0x01D9_0102, &[0x01D9_0103])), 0x01D9_0102);
        set.update(None, 0xA9B4_0001);
        assert!(!set.is_sealed_dungeon());
        assert!(!set.contains(0x01D9_0103));
        assert!(set.contains(0xA9B4_0020));
    }

    /// The fixture's objects: incarnation 7, as `RuntimeEntityLivenessTests.Spawn`.
    fn register(world: &mut World, guid: u32, cell: u32) {
        world.apply(&create(guid, stamps(7, 1), Some(position(cell, 51.0))));
    }

    fn world() -> World {
        let mut world = World::new();
        world.set_player(PLAYER);
        register(&mut world, PLAYER, 0x3032_0001);
        world
    }

    #[test]
    fn an_object_outside_the_neighbourhood_is_due_after_twenty_five_seconds() {
        let mut world = world();
        register(&mut world, 0x7000_0001, 0xA9B4_0001);
        register(&mut world, 0x7000_0002, 0x3133_0020);
        let mut controller = LivenessController::default();
        assert!(controller.tick(0.0, &world, None).is_empty());
        assert!(controller.tick(24.0, &world, None).is_empty());
        let due = controller.tick(26.0, &world, None);
        assert_eq!(due.len(), 1);
        assert_eq!(due[0].guid, 0x7000_0001);
        assert_eq!(due[0].generation, 7);
    }

    #[test]
    fn an_object_in_a_container_never_expires() {
        let mut world = world();
        let Inbound::CreateObject(mut item) =
            create(0x7000_0001, stamps(7, 1), Some(position(0xA9B4_0001, 51.0)))
        else {
            unreachable!()
        };
        item.weenie = Some(PublicWeenieDesc {
            container_id: Some(PLAYER),
            ..PublicWeenieDesc::default()
        });
        world.apply(&Inbound::CreateObject(item));
        let mut controller = LivenessController::default();
        assert!(controller.tick(0.0, &world, None).is_empty());
        assert!(controller.tick(30.0, &world, None).is_empty());
    }

    #[test]
    fn expiry_deletes_the_object_with_no_window_present() {
        // TheCanonicalSinkDeletesTheObjectWithNoPresentationPresent.
        let mut world = world();
        register(&mut world, 0x7000_0001, 0xA9B4_0001);
        register(&mut world, 0x7000_0002, 0x3133_0020);
        assert_eq!(world.object_count(), 3);
        let mut controller = LivenessController::default();
        controller.tick(0.0, &world, None);
        for candidate in controller.tick(26.0, &world, None) {
            world.expire(candidate);
        }
        assert_eq!(world.object_count(), 2);
        assert!(world.object(0x7000_0001).is_none());
        assert!(world.item(0x7000_0001).is_none());
        assert!(world.object(0x7000_0002).is_some());
        assert!(
            world
                .drain_changes()
                .any(|c| matches!(c, crate::world::WorldChange::Removed(_)))
        );
    }

    #[test]
    fn a_sealed_dungeon_cell_expires_the_dungeons_other_cells() {
        let mut world = World::new();
        world.set_player(PLAYER);
        register(&mut world, PLAYER, 0x01D9_0102);
        register(&mut world, 0x7000_0001, 0x01D9_0140);
        register(&mut world, 0x7000_0002, 0x01D9_0103);
        let visible = [0x01D9_0103];
        let cell = || Some(dungeon(0x01D9_0102, &visible));
        let mut controller = LivenessController::default();
        controller.tick(0.0, &world, cell());
        let due = controller.tick(26.0, &world, cell());
        assert_eq!(
            due.iter().map(|c| c.guid).collect::<Vec<_>>(),
            [0x7000_0001]
        );
    }

    #[test]
    fn checks_run_once_a_second_from_the_session_clock() {
        // HeadlessEntityLivenessTests: ticked once a second from 1 s, the
        // object outside the neighbourhood is still there after 24 ticks and
        // gone after 26; the neighbour and the player stay.
        let mut world = world();
        register(&mut world, 0x7000_0001, 0xA9B4_0001);
        register(&mut world, 0x7000_0002, 0x3133_0020);
        let mut controller = LivenessController::default();
        for second in 1..=24 {
            for candidate in controller.tick(f64::from(second), &world, None) {
                world.expire(candidate);
            }
        }
        assert_eq!(world.object_count(), 3);
        for second in 25..=26 {
            for candidate in controller.tick(f64::from(second), &world, None) {
                world.expire(candidate);
            }
        }
        assert!(world.object(0x7000_0001).is_none());
        assert!(world.object(0x7000_0002).is_some());
        assert!(world.object(PLAYER).is_some());
    }

    // Objects outside the world (OpenAC `RuntimeEntityLivenessTests`, the
    // destruction-queue cases of 2026-09-24). The server destroys a
    // corpse's contents (and a creature's wielded items) without a message;
    // the game client drops them itself 25 seconds after it stops looking
    // at them, or after their container goes away.

    const CORPSE: u32 = 0x8000_1000;
    const PACK: u32 = 0x8000_1100;
    const PARTNER: u32 = 0x5000_0B00;

    /// A create with no place in the world, in `container` or wielded by
    /// `wielder` (incarnation 7, as `ContainedSpawn`).
    fn contained(world: &mut World, guid: u32, container: Option<u32>, wielder: Option<u32>) {
        let Inbound::CreateObject(mut item) = create(guid, stamps(7, 1), None) else {
            unreachable!()
        };
        item.weenie = Some(PublicWeenieDesc {
            name: "item".into(),
            container_id: container,
            wielder_id: wielder,
            ..PublicWeenieDesc::default()
        });
        world.apply(&Inbound::CreateObject(item));
    }

    fn view(world: &mut World, container: u32, items: &[u32]) {
        world.apply_view_contents(&ac_proto::inventory::ViewContents {
            container_guid: container,
            items: items
                .iter()
                .map(|&guid| ac_proto::inventory::ContentsEntry {
                    guid,
                    container_type: 0,
                })
                .collect(),
        });
    }

    /// The server put `item` into `container`.
    fn put(world: &mut World, item: u32, container: u32) {
        world.apply_put_in_container(&ac_proto::inventory::PutObjInContainer {
            item_guid: item,
            container_guid: container,
            placement: 0,
            container_type: 0,
        });
    }

    fn delete(world: &mut World, guid: u32) {
        world.apply(&Inbound::DeleteObject(ac_proto::DeleteObject {
            guid,
            instance_sequence: 7,
        }));
    }

    /// The fixture's world with the destruction queue, as `ContentsWorld`.
    struct Contents {
        world: World,
        controller: LivenessController,
        partner: u32,
        partner_items: Vec<u32>,
    }

    impl Contents {
        fn new() -> Self {
            Self {
                world: world(),
                controller: LivenessController::default(),
                partner: 0,
                partner_items: Vec::new(),
            }
        }

        fn tick(&mut self, now: f64) {
            let kept = KeptPlaces {
                ground_container: 0,
                trade_partner: self.partner,
                partner_items: &self.partner_items,
            };
            self.controller.pass(now, &mut self.world, None, kept);
        }

        /// What a session pass does after each message.
        fn follow(&mut self) -> usize {
            let kept = KeptPlaces {
                ground_container: 0,
                trade_partner: self.partner,
                partner_items: &self.partner_items,
            };
            self.controller.follow(&mut self.world, kept);
            self.controller.queued_object_count()
        }

        /// A corpse whose contents the player is viewing.
        fn corpse(&mut self, guid: u32, items: &[u32]) {
            contained(&mut self.world, guid, None, None);
            for &item in items {
                contained(&mut self.world, item, Some(guid), None);
            }
            view(&mut self.world, guid, items);
        }

        fn gone(&self, guid: u32) -> bool {
            self.world.item(guid).is_none()
        }
    }

    #[test]
    fn closing_a_corpse_destroys_its_contents_twenty_five_seconds_later() {
        let mut f = Contents::new();
        f.corpse(CORPSE, &[0x8000_1001, 0x8000_1002]);
        f.tick(1.0);
        f.world.stop_viewing_contents_tree(CORPSE);
        f.tick(25.5);
        assert!(!f.gone(0x8000_1001));
        f.tick(26.5);
        assert!(f.gone(0x8000_1001));
        assert!(f.gone(0x8000_1002));
        assert!(f.world.object(0x8000_1001).is_none());
        assert!(!f.gone(CORPSE));
    }

    #[test]
    fn an_item_looted_into_the_pack_leaves_the_queue() {
        let mut f = Contents::new();
        f.corpse(CORPSE, &[0x8000_1001, 0x8000_1002]);
        f.world.stop_viewing_contents_tree(CORPSE);
        put(&mut f.world, 0x8000_1001, PLAYER);
        f.tick(1.0);
        f.tick(30.0);
        assert!(!f.gone(0x8000_1001));
        assert!(f.gone(0x8000_1002));
    }

    #[test]
    fn a_create_for_a_queued_item_takes_it_off_the_queue() {
        // Opening the corpse again re-sends every item's create.
        let mut f = Contents::new();
        f.corpse(CORPSE, &[0x8000_1001]);
        f.world.stop_viewing_contents_tree(CORPSE);
        assert_eq!(f.follow(), 1);
        contained(&mut f.world, 0x8000_1001, Some(CORPSE), None);
        f.tick(1.0);
        f.tick(30.0);
        assert_eq!(f.controller.queued_object_count(), 0);
        assert!(!f.gone(0x8000_1001));
    }

    #[test]
    fn a_destroyed_corpse_takes_what_it_still_held_with_it_twenty_five_seconds_later() {
        let mut f = Contents::new();
        f.corpse(CORPSE, &[0x8000_1001, 0x8000_1002]);
        f.tick(1.0);
        delete(&mut f.world, CORPSE);
        f.tick(25.5);
        assert!(!f.gone(0x8000_1001));
        f.tick(26.5);
        assert!(f.gone(0x8000_1001));
        assert!(f.gone(0x8000_1002));
    }

    #[test]
    fn a_looted_corpses_items_do_not_accumulate() {
        // A bot looting all night: every corpse is opened, one item taken,
        // the corpse closed and later destroyed by the server. What the
        // client holds must come back to the player and what it looted.
        let mut f = Contents::new();
        let baseline = f.world.items().objects().count();
        let mut now = 0.0;
        for corpse in 0..40u32 {
            let corpse_guid = 0x8001_0000 + corpse * 0x10;
            let items: Vec<u32> = (1..=6).map(|i| corpse_guid + i).collect();
            f.corpse(corpse_guid, &items);
            put(&mut f.world, items[0], PLAYER);
            f.world.stop_viewing_contents_tree(corpse_guid);
            now += 2.0;
            f.tick(now);
            delete(&mut f.world, corpse_guid);
        }
        f.tick(now + 26.0);
        assert_eq!(f.world.items().objects().count(), baseline + 40);
        assert_eq!(f.world.object_count(), 1 + 40);
        assert_eq!(f.controller.queued_object_count(), 0);
    }

    /// A viewed corpse holding one item and a viewed pack of two.
    fn corpse_with_a_pack() -> Contents {
        let mut f = Contents::new();
        f.corpse(CORPSE, &[0x8000_1001, PACK]);
        contained(&mut f.world, 0x8000_1101, Some(PACK), None);
        contained(&mut f.world, 0x8000_1102, Some(PACK), None);
        view(&mut f.world, PACK, &[0x8000_1101, 0x8000_1102]);
        f
    }

    #[test]
    fn a_nested_packs_items_go_twenty_five_seconds_after_the_pack() {
        let mut f = corpse_with_a_pack();
        f.tick(1.0);
        f.world.stop_viewing_contents_tree(CORPSE);
        f.tick(26.5);
        assert!(f.gone(PACK));
        assert!(f.gone(0x8000_1001));
        assert!(!f.gone(0x8000_1101));
        f.tick(51.0);
        assert!(!f.gone(0x8000_1101));
        f.tick(52.0);
        assert!(f.gone(0x8000_1101));
        assert!(f.gone(0x8000_1102));
    }

    #[test]
    fn a_pack_looted_from_a_closed_corpse_keeps_its_items() {
        let mut f = corpse_with_a_pack();
        f.tick(1.0);
        f.world.stop_viewing_contents_tree(CORPSE);
        put(&mut f.world, PACK, PLAYER);
        f.tick(30.0);
        f.tick(60.0);
        assert!(!f.gone(PACK));
        assert!(!f.gone(0x8000_1101));
        assert!(!f.gone(0x8000_1102));
    }

    #[test]
    fn an_item_the_partner_offers_leaves_the_queue_and_the_trade_end_queues_what_it_holds() {
        let mut f = Contents::new();
        f.tick(1.0);
        contained(&mut f.world, 0x8000_2001, Some(PARTNER), None);
        contained(&mut f.world, 0x8000_2002, Some(0x8000_2001), None);
        view(&mut f.world, 0x8000_2001, &[0x8000_2002]);
        // Moved into the partner's pack while no trade was open: queued.
        put(&mut f.world, 0x8000_2001, PARTNER);
        assert_eq!(f.follow(), 2);

        f.partner = PARTNER;
        f.partner_items = vec![0x8000_2001];
        f.world
            .note_destruction_queue(QueueNote::PartnerItemAdded(0x8000_2001));
        assert_eq!(f.follow(), 0);

        f.tick(20.0);
        // The trade resets: the partner's items leave the window.
        f.partner_items.clear();
        f.world
            .note_destruction_queue(QueueNote::PartnerItemsReleased(vec![0x8000_2001]));
        f.tick(46.0);
        assert!(!f.gone(0x8000_2001));
        assert!(f.gone(0x8000_2002));
    }

    #[test]
    fn a_vendors_window_takes_its_items_off_the_queue() {
        // An item sold to a vendor moves into the vendor, a container the
        // player does not own; the vendor's refreshed list shows it again.
        const VENDOR: u32 = 0x8000_3000;
        let mut f = Contents::new();
        f.tick(1.0);
        contained(&mut f.world, 0x8000_3001, Some(PLAYER), None);
        put(&mut f.world, 0x8000_3001, VENDOR);
        assert_eq!(f.follow(), 1);
        f.world
            .note_destruction_queue(QueueNote::VendorListed(vec![0x8000_3001]));
        f.tick(30.0);
        assert_eq!(f.controller.queued_object_count(), 0);
        assert!(!f.gone(0x8000_3001));
    }

    #[test]
    fn a_wielded_object_leaves_visibility_with_its_holder() {
        let mut world = world();
        register(&mut world, 0x7000_0001, 0xA9B4_0001);
        contained(&mut world, 0x7000_0002, None, Some(0x7000_0001));
        let mut controller = LivenessController::default();
        controller.tick(0.0, &world, None);
        let mut due: Vec<u32> = controller
            .tick(26.0, &world, None)
            .iter()
            .map(|c| c.guid)
            .collect();
        due.sort_unstable();
        assert_eq!(due, [0x7000_0001, 0x7000_0002]);
    }

    #[test]
    fn a_looted_weapon_stays_in_the_pack_when_its_old_wielder_leaves() {
        // The create named a wielder; the loot moved it into the pack. Only
        // the live placement may decide, or a recycled wielder id leaving
        // view would take the weapon out of the player's pack.
        let mut world = world();
        register(&mut world, 0x7000_0001, 0xA9B4_0001);
        contained(&mut world, 0x7000_0002, None, Some(0x7000_0001));
        put(&mut world, 0x7000_0002, PLAYER);
        let mut controller = LivenessController::default();
        controller.tick(0.0, &world, None);
        let due = controller.tick(26.0, &world, None);
        assert_eq!(
            due.iter().map(|c| c.guid).collect::<Vec<_>>(),
            [0x7000_0001]
        );
    }

    #[test]
    fn an_object_whose_holder_is_gone_is_not_expired_by_visibility() {
        // A deleted creature only detaches what it held.
        let mut world = world();
        contained(&mut world, 0x7000_0002, None, Some(0x7000_0001));
        let mut controller = LivenessController::default();
        controller.tick(0.0, &world, None);
        assert!(controller.tick(30.0, &world, None).is_empty());
    }
}
