//! World state of one session: every object the server described, the local
//! player's own state, and the stream of changes a window follows.
//!
//! Two records per object, as in OpenAC: the physics record (`WorldObject`:
//! position, motion, appearance, behind the update-counter gate) and the item
//! record in `ac_core`'s object table (description, properties,
//! containment). A physics update that is not new enough changes nothing
//! and records nothing; item updates have no counters and always apply.

mod attachments;
mod items;
pub mod liveness;
mod object;
mod player;
mod presentation;
mod time;
pub mod transit;

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use ac_core::items::{ClientObject, ClientObjectTable, TableEvent};
use ac_core::physics::{
    CreateDisposition, PhysicsTimestampGate, PhysicsTimestamps, PositionDisposition, is_newer,
    is_valid_position,
};
use ac_core::player::{LocalPlayerState, SkillFormulaBonusResolver};
use ac_core::spells::{SpellTable, Spellbook};
use ac_proto::inventory::{
    InventoryServerSaveFailed, PutObjInContainer, ViewContents, WieldObject, parse_inventory_word,
};
use ac_proto::{
    CreateObject, DeleteObject, GameEventEnvelope, GameEventType, Inbound, ObjDescEvent,
    ParentEvent, PhysicsMovement, PickupEvent, PlayerDescription, ProtoError, SetState,
    UpdateMotion, UpdatePosition, Vector3, VectorUpdate,
};

pub use attachments::{AwaitingParent, ParentAttachments, ParentRelation};
pub use items::{ItemChange, appraisal_update, property_bundle, restriction_record, weenie_data};
pub use object::{ObjectKey, WorldObject};
pub use player::PlayerState;
pub use presentation::{
    AppliedMotion, AttackBarView, BodyFacts, EffectFacts, MotionView, ObjectDelta, ObjectView,
    PlayerView, RadarFacts, StepBlend, VitalReading, WorldSnapshot, accepted_motion_state,
    motion_line, motion_log_stamp, rest_placement,
};
pub use time::GameClock;
use transit::TeleportDestination;

/// Something in the world changed. Changes name objects by key (physics)
/// or guid (items); read the current values from the [`World`] when
/// handling one.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WorldChange {
    /// A new object appeared.
    Spawned(ObjectKey),
    /// An object was re-created as a new incarnation; its old state is gone.
    Respawned(ObjectKey),
    /// An object's description was re-sent without changing incarnation.
    Refreshed(ObjectKey),
    /// An object was destroyed.
    Removed(ObjectKey),
    /// An object moved.
    Moved {
        /// The object.
        key: ObjectKey,
        /// The move was a teleport.
        teleported: bool,
        /// The server forced the local player here.
        forced: bool,
    },
    /// An object's motion changed.
    MotionChanged(ObjectKey),
    /// An object's physics state bits changed.
    StateChanged(ObjectKey),
    /// An object's velocity changed.
    VectorChanged(ObjectKey),
    /// An object's appearance changed.
    AppearanceChanged(ObjectKey),
    /// An object was attached to a parent.
    Parented(ObjectKey),
    /// An object left the world into a container.
    PickedUp(ObjectKey),
    /// The item table changed.
    Item(ItemChange),
    /// The local player's own state changed.
    PlayerChanged,
}

/// The world as one session sees it.
#[derive(Debug, Default)]
pub struct World {
    objects: HashMap<u32, WorldObject>,
    local_ids: HashMap<u32, u32>,
    guids_by_local_id: HashMap<u32, u32>,
    next_local_id: u32,
    player_guid: Option<u32>,
    player: Option<PlayerState>,
    /// What a newly described player's numbers start from: the spellbook
    /// (with the spell table) and the skill formula resolver.
    player_rules: LocalPlayerState,
    items: ClientObjectTable,
    /// Which object hangs from which.
    attachments: ParentAttachments,
    table_events: Vec<TableEvent>,
    /// What the destruction queue follows, oldest first (the liveness
    /// controller drains it).
    queue_notes: Vec<liveness::QueueNote>,
    changes: Vec<WorldChange>,
    /// Motion states objects took up since the last take, oldest first.
    applied_motions: Vec<AppliedMotion>,
    parse_failures: BTreeMap<u32, u64>,
    clock: GameClock,
    /// Positions accepted for the local player since the last take, with
    /// whether each advanced the teleport counter.
    player_moves: Vec<(TeleportDestination, bool)>,
    /// The server's last fog override (AdminEnvirons 0..=6, 0 clears it);
    /// `None` until one arrives.
    environ_override: Option<u32>,
}

impl World {
    /// An empty world.
    #[must_use]
    pub fn new() -> Self {
        Self {
            next_local_id: 1,
            // Enchantments apply from the first description on, with or
            // without the spell table.
            player_rules: LocalPlayerState::new(Some(Spellbook::new(None))),
            ..Self::default()
        }
    }

    /// Forget everything the server told this session (a new generation
    /// starts); the player rules from the game tables stay, and so does the
    /// game clock: the server's time of day does not change with the
    /// character, and a character entering again sees the synced sky at
    /// once instead of the offline clock until the next time sync (OpenAC
    /// keeps its world time for the runtime's whole life).
    pub fn reset_session(&mut self) {
        let rules = std::mem::take(&mut self.player_rules);
        let clock = std::mem::take(&mut self.clock);
        let environ_override = self.environ_override;
        *self = Self::new();
        self.player_rules = rules;
        self.clock = clock;
        // The environment is the runtime's for its whole life, as the clock
        // is (OpenAC's runtime environment state is one per runtime).
        self.environ_override = environ_override;
        // Whoever follows the changes (a window's copy of the items) hears
        // that every item is gone, as from a cleared table.
        self.changes.push(WorldChange::Item(ItemChange::Cleared));
    }

    /// The server's last fog override as sent (`None` until one arrives);
    /// a window that attaches later applies it.
    #[must_use]
    pub fn environ_override(&self) -> Option<u32> {
        self.environ_override
    }

    /// Forget the game clock's sync: the session time it was measured
    /// against ended with its connection.
    pub fn forget_clock(&mut self) {
        self.clock = GameClock::default();
    }

    /// Take the positions accepted for the local player since the last call,
    /// each with whether its teleport counter advanced.
    pub fn take_player_moves(&mut self) -> Vec<(TeleportDestination, bool)> {
        std::mem::take(&mut self.player_moves)
    }

    /// Install the spell table and the skill formula resolver the player's
    /// numbers use. A player already described gets a fresh spellbook (the
    /// next description fills it).
    pub fn install_player_rules(
        &mut self,
        spells: Arc<SpellTable>,
        resolver: Option<SkillFormulaBonusResolver>,
    ) {
        let mut rules = LocalPlayerState::new(Some(Spellbook::new(Some(spells))));
        rules.set_skill_formula_bonus_resolver(resolver);
        if let Some(player) = &mut self.player {
            *player = PlayerState::new(rules.clone());
        }
        self.player_rules = rules;
    }

    /// The local player's spellbook, once the description arrived.
    #[must_use]
    pub fn spellbook(&self) -> Option<&Spellbook> {
        self.player.as_ref().and_then(PlayerState::spellbook)
    }

    /// Change the local player's spellbook (learned spells, enchantments);
    /// the player's numbers read the change. `None` before the description.
    pub fn update_spellbook<R>(&mut self, update: impl FnOnce(&mut Spellbook) -> R) -> Option<R> {
        let result = update(self.player.as_mut()?.spellbook_mut()?);
        self.changes.push(WorldChange::PlayerChanged);
        Some(result)
    }

    /// Set the local player's object id (the character entering the world).
    pub fn set_player(&mut self, guid: u32) {
        self.player_guid = Some(guid);
    }

    /// The local player's object id.
    #[must_use]
    pub fn player_guid(&self) -> Option<u32> {
        self.player_guid
    }

    /// The local player's own state, once its description arrived.
    #[must_use]
    pub fn player(&self) -> Option<&PlayerState> {
        self.player.as_ref()
    }

    /// An object's physics record by server id.
    #[must_use]
    pub fn object(&self, guid: u32) -> Option<&WorldObject> {
        self.objects.get(&guid)
    }

    /// An object by its presentation local id.
    #[must_use]
    pub fn object_by_local_id(&self, local_id: u32) -> Option<&WorldObject> {
        self.guids_by_local_id
            .get(&local_id)
            .and_then(|guid| self.objects.get(guid))
    }

    /// The server id a presentation local id stood for (kept after the
    /// object leaves, so a removal can still be named).
    #[must_use]
    pub fn guid_of_local_id(&self, local_id: u32) -> Option<u32> {
        self.guids_by_local_id.get(&local_id).copied()
    }

    /// Every known object's physics record, in no particular order.
    pub fn objects(&self) -> impl Iterator<Item = &WorldObject> {
        self.objects.values()
    }

    /// How many objects have a physics record.
    #[must_use]
    pub fn object_count(&self) -> usize {
        self.objects.len()
    }

    /// The item table: every described object's description, properties
    /// and containment.
    #[must_use]
    pub fn items(&self) -> &ClientObjectTable {
        &self.items
    }

    /// An object's item record.
    #[must_use]
    pub fn item(&self, guid: u32) -> Option<&ClientObject> {
        self.items.get(guid)
    }

    /// An object's name, if the server described one.
    #[must_use]
    pub fn name(&self, guid: u32) -> Option<&str> {
        self.items
            .get(guid)
            .map(|item| item.name.as_str())
            .filter(|name| !name.is_empty())
    }

    /// Messages of a world kind that failed to parse, by opcode. Nonzero
    /// counts mean the parser and the server disagree; they are diagnostic.
    #[must_use]
    pub fn parse_failures(&self) -> &BTreeMap<u32, u64> {
        &self.parse_failures
    }

    /// Take the changes recorded since the last call, oldest first.
    pub fn drain_changes(&mut self) -> std::vec::Drain<'_, WorldChange> {
        self.changes.drain(..)
    }

    /// Take the motion states objects took up since the last call, in the
    /// order they were taken up.
    pub fn drain_applied_motions(&mut self) -> std::vec::Drain<'_, AppliedMotion> {
        self.applied_motions.drain(..)
    }

    /// The session's physics took a server motion state up on the body of
    /// `guid` (published with [`set_live_motion`](Self::set_live_motion)
    /// first): record the object's motion as it stands now, in order.
    pub fn note_motion_applied(&mut self, guid: u32) {
        if let Some(object) = self.objects.get(&guid)
            && let Some(motion) = object.motion_view()
        {
            self.applied_motions.push(AppliedMotion {
                key: object.key,
                guid,
                motion,
                taken_up: true,
            });
        }
    }

    /// The session's physics passed the body of `guid` through `motion`
    /// on its way to taking a server state up (the movement under way
    /// interrupted first): record it, in order, for a window to follow.
    pub fn note_motion_between(&mut self, guid: u32, motion: MotionView) {
        if let Some(object) = self.objects.get(&guid) {
            self.applied_motions.push(AppliedMotion {
                key: object.key,
                guid,
                motion,
                taken_up: false,
            });
        }
    }

    /// A motion record was accepted for `guid`: when the session's physics
    /// does not move the object (it records its own), the object took the
    /// server's state up as it stands.
    fn note_motion_accepted(&mut self, guid: u32) {
        if self
            .objects
            .get(&guid)
            .is_some_and(|o| o.live_motion.is_none())
        {
            self.note_motion_applied(guid);
        }
    }

    /// How many changes are recorded and not yet drained (a mark for
    /// [`changes_since`](Self::changes_since)).
    #[must_use]
    pub fn change_count(&self) -> usize {
        self.changes.len()
    }

    /// The changes recorded after `mark` (a [`change_count`](Self::change_count)
    /// taken earlier in the same pass).
    #[must_use]
    pub fn changes_since(&self, mark: usize) -> &[WorldChange] {
        self.changes.get(mark..).unwrap_or(&[])
    }

    /// The session's physics has the object at `position` now: the change
    /// stream reports a move when the cell, the place (by a millimetre) or
    /// the facing changed. Ignored for an object not in the world.
    pub fn set_live_position(&mut self, guid: u32, position: ac_proto::Position) {
        let Some(object) = self.objects.get_mut(&guid) else {
            return;
        };
        if object.physics.position.is_none() {
            return;
        }
        let moved = object
            .live
            .is_none_or(|old| live_pose_changed(&old, &position));
        object.live = Some(position);
        if moved {
            self.changes.push(WorldChange::Moved {
                key: object.key,
                teleported: false,
                forced: false,
            });
        }
    }

    /// What a window blends to draw the object at its own frame's time,
    /// when the session's physics moves it in fixed steps (the local
    /// player: its body moves 30 steps a second while the screen redraws
    /// more often, so a window drawing the step itself shows the character
    /// and the camera holding still one frame and jumping the next). The
    /// places are origins in the cell of
    /// [`set_live_position`](Self::set_live_position)'s place, which
    /// gameplay, plugins and the server keep. The change stream reports a
    /// move when the blend draws differently ([`StepBlend::differs`]).
    /// Ignored for an object the physics does not place.
    pub fn set_step_blend(&mut self, guid: u32, blend: StepBlend) {
        let Some(object) = self.objects.get_mut(&guid) else {
            return;
        };
        if object.physics.position.is_none() || object.live.is_none() {
            return;
        }
        let moved = object.step_blend.is_none_or(|old| old.differs(&blend));
        if moved {
            object.step_blend = Some(blend);
            self.changes.push(WorldChange::Moved {
                key: object.key,
                teleported: false,
                forced: false,
            });
        }
    }

    /// The session's physics has the object doing `motion` now (its
    /// interpreter's axes and ground contact): the change stream reports a
    /// motion change when it differs. Ignored for an object not in the
    /// world.
    pub fn set_live_motion(&mut self, guid: u32, motion: MotionView) {
        let Some(object) = self.objects.get_mut(&guid) else {
            return;
        };
        if object.physics.position.is_none() {
            return;
        }
        if object.live_motion.as_ref() != Some(&motion) {
            object.live_motion = Some(motion);
            self.changes.push(WorldChange::MotionChanged(object.key));
        }
    }

    /// The session's physics found no clear spot for the object to enter
    /// the world at: it stays known (its records, its updates) but is not
    /// in the world, so a window does not draw it, for the rest of this
    /// incarnation (a new incarnation enters again).
    pub fn refuse_entry(&mut self, guid: u32) {
        let Some(object) = self.objects.get_mut(&guid) else {
            return;
        };
        if object.entry_refused {
            return;
        }
        object.entry_refused = true;
        object.live = None;
        object.step_blend = None;
        object.live_motion = None;
        self.changes.push(WorldChange::Moved {
            key: object.key,
            teleported: false,
            forced: false,
        });
    }

    /// The session's physics no longer simulates the object: its position
    /// and motion are the server's again.
    pub fn clear_live_position(&mut self, guid: u32) {
        if let Some(object) = self.objects.get_mut(&guid) {
            object.live_motion = None;
            object.step_blend = None;
        }
        if let Some(object) = self.objects.get_mut(&guid)
            && object.live.take().is_some()
        {
            self.changes.push(WorldChange::Moved {
                key: object.key,
                teleported: false,
                forced: false,
            });
        }
    }

    /// Take the item table's events since the last call, oldest first. The
    /// inventory owner follows them (its request in flight completes on
    /// the server's answer about that object).
    pub fn take_table_events(&mut self) -> Vec<TableEvent> {
        std::mem::take(&mut self.table_events)
    }

    /// Tell the destruction queue something only an owner outside the world
    /// knows (the trade partner's items, the vendor's list).
    pub fn note_destruction_queue(&mut self, note: liveness::QueueNote) {
        self.queue_notes.push(note);
    }

    /// Take what the destruction queue has not followed yet, oldest first.
    pub fn take_queue_notes(&mut self) -> Vec<liveness::QueueNote> {
        std::mem::take(&mut self.queue_notes)
    }

    /// Game time.
    #[must_use]
    pub fn clock(&self) -> &GameClock {
        &self.clock
    }

    /// Take a server time sync received at session time `now`.
    pub fn sync_time(&mut self, server_ticks: f64, now: std::time::Duration) {
        self.clock.sync_from_server(server_ticks, now);
    }

    /// Count a message that failed to parse.
    pub fn record_parse_failure(&mut self, opcode: u32) {
        *self.parse_failures.entry(opcode).or_default() += 1;
    }

    /// Apply one parsed server message. Messages that are not about world
    /// state are ignored, and so are the character description and
    /// appraisals, which the session parses once for every owner and hands
    /// to [`apply_player_description`](Self::apply_player_description) and
    /// [`apply_appraisal`](Self::apply_appraisal).
    pub fn apply(&mut self, message: &Inbound<'_>) {
        match message {
            Inbound::CreateObject(create) => self.apply_create(create),
            Inbound::UpdateObject(update) => self.apply_update_object(update),
            Inbound::DeleteObject(delete) => self.apply_delete(delete),
            Inbound::UpdatePosition(update) => self.apply_position(update),
            Inbound::UpdateMotion(update) => self.apply_motion(update),
            Inbound::VectorUpdate(update) => self.apply_vector(update),
            Inbound::SetState(update) => self.apply_state(update),
            Inbound::ObjDescEvent(update) => self.apply_obj_desc(update),
            Inbound::ParentEvent(update) => self.apply_parent(update),
            Inbound::PickupEvent(update) => self.apply_pickup(update),
            Inbound::PublicUpdatePropertyInt(u) => {
                self.with_items(|t, e| t.update_int_property(u.guid, u.property, u.value, e));
            }
            Inbound::PublicUpdatePropertyDataId(u) => {
                self.with_items(|t, e| t.update_data_id_property(u.guid, u.property, u.value, e));
            }
            Inbound::PublicUpdatePropertyInstanceId(u) => {
                self.with_items(|t, e| {
                    t.update_instance_id_property(u.guid, u.property, u.value, e)
                });
            }
            Inbound::PublicUpdatePropertyString(u) => {
                self.with_items(|t, e| t.update_string_property(u.guid, u.property, &u.value, e));
            }
            Inbound::PrivateUpdatePropertyString(u) => {
                let player = self.player_guid.unwrap_or(0);
                self.with_items(|t, e| t.update_string_property(player, u.property, &u.value, e));
            }
            Inbound::PublicUpdatePropertyBool(u) => {
                self.with_items(|t, e| t.update_bool_property(u.guid, u.property, u.value, e));
            }
            Inbound::PrivateUpdatePropertyBool(u) => {
                let player = self.player_guid.unwrap_or(0);
                self.with_items(|t, e| t.update_bool_property(player, u.property, u.value, e));
            }
            Inbound::PrivateUpdatePropertyInt(u) => {
                // The player's value lives in both projections; one
                // message updates both so they cannot drift.
                let player = self.player_guid.unwrap_or(0);
                self.with_items(|t, e| t.update_int_property(player, u.property, u.value, e));
                self.update_player(|s| s.apply_int(u.property, u.value));
            }
            Inbound::PrivateUpdatePropertyInt64(u) => {
                let player = self.player_guid.unwrap_or(0);
                self.with_items(|t, e| t.update_int64_property(player, u.property, u.value, e));
            }
            Inbound::PrivateUpdatePropertyDataId(u) => {
                let player = self.player_guid.unwrap_or(0);
                self.with_items(|t, e| t.update_data_id_property(player, u.property, u.value, e));
            }
            Inbound::PrivateUpdatePropertyInstanceId(u) => {
                let player = self.player_guid.unwrap_or(0);
                self.with_items(|t, e| {
                    t.update_instance_id_property(player, u.property, u.value, e)
                });
            }
            Inbound::PrivateUpdatePosition(u) => {
                self.update_player(|s| s.apply_position(u.position_type, u.position));
            }
            Inbound::PrivateUpdateVital(u) => self.update_player(|s| s.apply_vital(u)),
            Inbound::PrivateUpdateVitalCurrent(u) => {
                self.update_player(|s| s.apply_vital_current(u));
            }
            Inbound::PrivateUpdateAttribute(u) => self.update_player(|s| s.apply_attribute(u)),
            Inbound::PrivateUpdateSkill(u) => self.update_player(|s| s.apply_skill(u)),
            Inbound::SetStackSize(u) => {
                self.with_items(|t, e| t.update_stack_size(u.guid, u.stack_size, u.value, e));
            }
            Inbound::InventoryRemoveObject(u) => {
                // The object leaves the inventory view: the item record
                // goes; the physics record stays until the object is
                // deleted.
                self.with_items(|t, e| t.remove(u.guid, e));
            }
            // Values up to 6 are fog overrides and stay in force; the rest
            // are one-shot sound cues.
            Inbound::AdminEnvirons(change)
                if ac_core::sky::EnvironOverride::from_raw(change.environ_type).is_some() =>
            {
                self.environ_override = Some(change.environ_type);
            }
            Inbound::GameEvent(event) if self.apply_game_event(event).is_err() => {
                self.record_parse_failure(0xF7B0_0000 | event.event_type.0);
            }
            _ => {}
        }
    }

    fn apply_game_event(&mut self, event: &GameEventEnvelope<'_>) -> Result<(), ProtoError> {
        let payload = event.payload;
        match event.event_type {
            GameEventType::WIELD_OBJECT => self.apply_wield(&WieldObject::parse(payload)?),
            GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER => {
                self.apply_put_in_container(&PutObjInContainer::parse(payload)?);
            }
            GameEventType::INVENTORY_PUT_OBJECT_IN_3D => {
                self.apply_put_in_3d(parse_inventory_word(payload)?);
            }
            GameEventType::INVENTORY_SERVER_SAVE_FAILED => {
                self.apply_save_failed(&InventoryServerSaveFailed::parse(payload)?);
            }
            GameEventType::VIEW_CONTENTS => {
                self.apply_view_contents(&ViewContents::parse(payload)?);
            }
            GameEventType::CLOSE_GROUND_CONTAINER => {
                self.apply_close_ground_container(parse_inventory_word(payload)?);
            }
            GameEventType::HOUSE_UPDATE_RESTRICTIONS => {
                let update = ac_proto::house::HouseUpdateRestrictions::parse(payload)?;
                let record = restriction_record(&update.restrictions);
                self.apply_house_restrictions(update.sender_id, record);
            }
            _ => {}
        }
        Ok(())
    }

    fn is_player(&self, guid: u32) -> bool {
        self.player_guid == Some(guid)
    }

    fn local_id(&mut self, guid: u32) -> u32 {
        if let Some(id) = self.local_ids.get(&guid) {
            return *id;
        }
        let id = self.next_local_id;
        self.next_local_id += 1;
        self.local_ids.insert(guid, id);
        self.guids_by_local_id.insert(id, guid);
        id
    }

    fn apply_create(&mut self, create: &CreateObject) {
        let stamps = timestamps(create);
        let Some(existing) = self.objects.get_mut(&create.guid) else {
            let mut gate = PhysicsTimestampGate::new();
            gate.seed_for_create_object(stamps);
            self.insert(create, gate, false);
            self.ingest_description(create);
            self.queue_notes
                .push(liveness::QueueNote::Spawned(create.guid));
            self.attach_after_spawn(create);
            return;
        };
        match existing.gate.seed_for_create_object(stamps) {
            CreateDisposition::StaleGeneration => {}
            CreateDisposition::InitialGeneration | CreateDisposition::NewGeneration => {
                let gate = existing.gate.clone();
                // The old incarnation's relations end with it.
                self.attachments
                    .end_generation(create.guid, stamps.instance);
                self.insert(create, gate, true);
                self.replace_description(create, stamps.instance);
                self.queue_notes
                    .push(liveness::QueueNote::Spawned(create.guid));
                self.attach_after_spawn(create);
            }
            CreateDisposition::ExistingGeneration => {
                let key = existing.key;
                self.changes.push(WorldChange::Refreshed(key));
                self.ingest_description(create);
                self.queue_notes
                    .push(liveness::QueueNote::Spawned(create.guid));
                self.apply_same_generation_parts(create);
            }
        }
    }

    fn insert(&mut self, create: &CreateObject, gate: PhysicsTimestampGate, respawn: bool) {
        let key = ObjectKey {
            local_id: self.local_id(create.guid),
            incarnation: gate.stamps().instance,
        };
        self.objects
            .insert(create.guid, WorldObject::new(key, create, gate));
        self.changes.push(if respawn {
            WorldChange::Respawned(key)
        } else {
            WorldChange::Spawned(key)
        });
    }

    /// A same-incarnation create carries every timestamped part again; each
    /// goes through the same check its standalone update would.
    fn apply_same_generation_parts(&mut self, create: &CreateObject) {
        let ts = create.physics.timestamps;
        let guid = create.guid;
        self.apply_obj_desc(&ObjDescEvent {
            guid,
            model: create.model.clone(),
            instance_sequence: ts.instance,
            obj_desc_sequence: ts.obj_desc,
        });
        let physics = &create.physics;
        if let Some(parent) = physics.parent {
            if let Some(object) = self.objects.get_mut(&guid)
                && object
                    .gate
                    .try_accept_position_channel(ts.instance, ts.position)
            {
                let placement = physics.animation_frame.unwrap_or(0);
                object.apply_parent(parent.guid, parent.location_id, placement);
                self.changes.push(WorldChange::Parented(object.key));
                self.accept_create_parent(AwaitingParent {
                    parent_guid: parent.guid,
                    child_guid: guid,
                    parent_location: parent.location_id,
                    placement_id: placement,
                    child_position_sequence: ts.position,
                });
            }
        } else if let Some(position) = physics.position.filter(|p| p.cell_id != 0) {
            self.apply_position(&UpdatePosition {
                guid,
                flags: 0,
                position,
                velocity: physics.velocity,
                placement_id: physics.animation_frame,
                instance_sequence: ts.instance,
                position_sequence: ts.position,
                teleport_sequence: ts.teleport,
                force_position_sequence: ts.force_position,
            });
        } else {
            self.apply_pickup(&PickupEvent {
                guid,
                instance_sequence: ts.instance,
                position_sequence: ts.position,
            });
        }
        if let Some(PhysicsMovement::Present {
            raw,
            data,
            is_autonomous,
        }) = &physics.movement
            && !raw.is_empty()
            && let Some(object) = self.objects.get_mut(&guid)
            && object
                .gate
                .try_accept_movement(ts.instance, ts.movement, ts.server_controlled_move)
        {
            object.apply_movement(data, *is_autonomous);
            self.changes.push(WorldChange::MotionChanged(object.key));
            self.note_motion_accepted(guid);
        }
        self.apply_state(&SetState {
            guid,
            physics_state: physics.state,
            instance_sequence: ts.instance,
            state_sequence: ts.state,
        });
        self.apply_vector(&VectorUpdate {
            guid,
            velocity: physics.velocity.unwrap_or_default(),
            omega: physics.omega.unwrap_or_default(),
            instance_sequence: ts.instance,
            vector_sequence: ts.vector,
        });
    }

    fn apply_update_object(&mut self, update: &CreateObject) {
        // A re-sent description of an object held deletes it and creates it
        // again from the whole new description, whatever instance it
        // carries: its setup, physics state, parent and position (a house
        // hook given an item takes the item's setup and loses its no-draw
        // bit). The item record is taken as any description is taken (an
        // icon underlay a rend adds, a revealed Aetheria), keeping what was
        // learned about it; being created again, it takes the first place
        // in the container the description names (a revealed Aetheria
        // moves to its pack's top-left slot). One not held only refreshes
        // the item record, where it is; it never puts an object in the
        // world.
        let Some(existing) = self.objects.get_mut(&update.guid) else {
            self.ingest_description(update);
            return;
        };
        let stamps = timestamps(update);
        existing.gate.seed_for_replacement(stamps);
        let gate = existing.gate.clone();
        // The old incarnation's relations end with it.
        self.attachments
            .end_generation(update.guid, stamps.instance);
        self.insert(update, gate, true);
        self.ingest_recreated_description(update);
        self.queue_notes
            .push(liveness::QueueNote::Spawned(update.guid));
        self.attach_after_spawn(update);
    }

    fn apply_delete(&mut self, delete: &DeleteObject) {
        let is_player = self.is_player(delete.guid);
        let accepted = self.objects.get(&delete.guid).is_some_and(|o| {
            o.gate
                .try_accept_delete(delete.instance_sequence, is_player)
        });
        if accepted && let Some(object) = self.objects.remove(&delete.guid) {
            self.attachments
                .delete_generation(delete.guid, delete.instance_sequence);
            self.changes.push(WorldChange::Removed(object.key));
            self.with_items(|t, e| {
                t.remove_logical_generation(delete.guid, delete.instance_sequence, e)
            });
        }
    }

    fn apply_position(&mut self, update: &UpdatePosition) {
        let is_player = self.is_player(update.guid);
        let Some(object) = self.objects.get_mut(&update.guid) else {
            return;
        };
        let previous_teleport = object.gate.stamps().teleport;
        let disposition = object.gate.try_accept_position(
            update.instance_sequence,
            update.position_sequence,
            update.teleport_sequence,
            update.force_position_sequence,
            is_player,
        );
        // A position that is not a valid place (non-finite numbers, a bad
        // cell, a rotation that is not unit length) keeps its accepted
        // counters but does not move the object.
        let p = update.position;
        if !is_valid_position(
            p.cell_id,
            [p.origin.x, p.origin.y, p.origin.z],
            [
                p.orientation.w,
                p.orientation.x,
                p.orientation.y,
                p.orientation.z,
            ],
        ) {
            return;
        }
        let (forced, clear_parent, install_placement) = match disposition {
            PositionDisposition::Rejected => return,
            PositionDisposition::ForcePosition => (true, false, false),
            // An ordinary move unparents first; objects without animations
            // also take the placement frame before moving.
            PositionDisposition::Apply => (false, true, !object.has_animations()),
        };
        let teleported = !forced && is_newer(previous_teleport, update.teleport_sequence);
        if is_player && !forced {
            // A move the player accepted may be a teleport's destination.
            self.player_moves.push((
                TeleportDestination {
                    guid: update.guid,
                    instance_sequence: update.instance_sequence,
                    position_sequence: update.position_sequence,
                    teleport_sequence: update.teleport_sequence,
                    force_position_sequence: update.force_position_sequence,
                    position: update.position,
                },
                teleported,
            ));
        }
        let old_velocity = object.physics.velocity;
        let velocity = if forced {
            old_velocity
        } else if is_player {
            if teleported {
                Some(Vector3::default())
            } else {
                old_velocity
            }
        } else {
            Some(update.velocity.unwrap_or_default())
        };
        let mut position = update.position;
        if forced && let Some(current) = object.position().copied() {
            // A forced position keeps the heading the player has now (its
            // live body when the physics moves it).
            position.orientation = current.orientation;
        }
        object.apply_position(
            position,
            velocity,
            update.placement_id,
            install_placement,
            clear_parent,
        );
        self.changes.push(WorldChange::Moved {
            key: object.key,
            teleported,
            forced,
        });
        if clear_parent {
            self.attachments.end_child(update.guid);
        }
    }

    fn apply_motion(&mut self, update: &UpdateMotion) {
        if let Some(object) = self.objects.get_mut(&update.guid)
            && object.gate.try_accept_movement(
                update.instance_sequence,
                update.movement_sequence,
                update.server_control_sequence,
            )
        {
            object.apply_movement(&update.movement, update.is_autonomous);
            self.changes.push(WorldChange::MotionChanged(object.key));
            self.note_motion_accepted(update.guid);
        }
    }

    fn apply_vector(&mut self, update: &VectorUpdate) {
        if let Some(object) = self.objects.get_mut(&update.guid)
            && object
                .gate
                .try_accept_vector(update.instance_sequence, update.vector_sequence)
        {
            object.physics.velocity = Some(update.velocity);
            object.physics.omega = Some(update.omega);
            self.changes.push(WorldChange::VectorChanged(object.key));
        }
    }

    fn apply_state(&mut self, update: &SetState) {
        if let Some(object) = self.objects.get_mut(&update.guid)
            && object
                .gate
                .try_accept_state(update.instance_sequence, update.state_sequence)
        {
            object.physics.state = update.physics_state;
            self.changes.push(WorldChange::StateChanged(object.key));
        }
    }

    fn apply_obj_desc(&mut self, update: &ObjDescEvent) {
        if let Some(object) = self.objects.get_mut(&update.guid)
            && object
                .gate
                .try_accept_obj_desc(update.instance_sequence, update.obj_desc_sequence)
        {
            object.apply_model(&update.model);
            self.changes
                .push(WorldChange::AppearanceChanged(object.key));
        }
    }

    /// A parent event waits behind the child's earlier ones until its
    /// parent incarnation is live and the child exists.
    fn apply_parent(&mut self, update: &ParentEvent) {
        self.attachments.enqueue(update);
        self.resolve_attachment(update.child_guid);
    }

    /// Try the child's waiting parent events; the one accepted attaches it.
    fn resolve_attachment(&mut self, child_guid: u32) {
        let mut view = AttachView {
            objects: &mut self.objects,
        };
        let Some(relation) = self.attachments.resolve(child_guid, &mut view) else {
            return;
        };
        self.attachments.commit(relation);
        if let Some(child) = self.objects.get_mut(&child_guid) {
            child.apply_parent(
                relation.parent_guid,
                relation.parent_location,
                relation.placement_id,
            );
            self.changes.push(WorldChange::Parented(child.key));
        }
    }

    /// A relation a create carried: bound to the parent's live incarnation
    /// when the parent is known, else held until it appears.
    fn accept_create_parent(&mut self, request: AwaitingParent) {
        match self.objects.get(&request.parent_guid) {
            Some(parent) => self.attachments.accept_create_relation(ParentRelation {
                parent_guid: request.parent_guid,
                child_guid: request.child_guid,
                parent_location: request.parent_location,
                placement_id: request.placement_id,
                parent_instance: parent.gate.stamps().instance,
                child_position_sequence: request.child_position_sequence,
            }),
            None => self.attachments.defer_until_parent_known(request),
        }
    }

    /// A new object (or incarnation) is in the world: the relation its
    /// create carried, the parent events that waited for it as child, and
    /// the relations of children that waited for it as parent.
    fn attach_after_spawn(&mut self, create: &CreateObject) {
        let guid = create.guid;
        let physics = &create.physics;
        if let Some(parent) = physics.parent {
            self.accept_create_parent(AwaitingParent {
                parent_guid: parent.guid,
                child_guid: guid,
                parent_location: parent.location_id,
                placement_id: physics.animation_frame.unwrap_or(0),
                child_position_sequence: physics.timestamps.position,
            });
        }
        self.resolve_attachment(guid);
        for child in self.attachments.children_unresolved_for(guid) {
            self.resolve_attachment(child);
        }
        let Some(instance) = self.objects.get(&guid).map(|o| o.gate.stamps().instance) else {
            return;
        };
        for request in self.attachments.take_awaiting(guid) {
            self.attachments.accept_create_relation(ParentRelation {
                parent_guid: guid,
                child_guid: request.child_guid,
                parent_location: request.parent_location,
                placement_id: request.placement_id,
                parent_instance: instance,
                child_position_sequence: request.child_position_sequence,
            });
        }
    }

    /// Which object hangs from which.
    #[must_use]
    pub fn attachments(&self) -> &ParentAttachments {
        &self.attachments
    }

    fn apply_pickup(&mut self, update: &PickupEvent) {
        if let Some(object) = self.objects.get_mut(&update.guid)
            && object
                .gate
                .try_accept_position_channel(update.instance_sequence, update.position_sequence)
        {
            object.apply_pickup();
            self.changes.push(WorldChange::PickedUp(object.key));
            self.attachments.end_child(update.guid);
        }
    }

    fn update_player(&mut self, update: impl FnOnce(&mut PlayerState)) {
        if let Some(player) = self.player.as_mut() {
            update(player);
            self.changes.push(WorldChange::PlayerChanged);
        }
    }

    /// The character description: the player's numbers and saved positions,
    /// its properties, and its inventory and equipment lists.
    pub fn apply_player_description(&mut self, description: &PlayerDescription) {
        self.apply_description_items(description);
        let rules = &self.player_rules;
        self.player
            .get_or_insert_with(|| PlayerState::new(rules.clone()))
            .apply_description(description);
        self.changes.push(WorldChange::PlayerChanged);
    }
}

/// Straight-line distance between two places, meters. Landblocks are 192 m
/// squares numbered by the top two bytes of the cell id (x then y); an
/// origin is the offset inside its landblock.
#[must_use]
pub fn distance(a: &ac_proto::Position, b: &ac_proto::Position) -> f32 {
    const LANDBLOCK_METERS: f32 = 192.0;
    let global = |p: &ac_proto::Position| {
        let x = f32::from((p.cell_id >> 24) as u8) * LANDBLOCK_METERS + p.origin.x;
        let y = f32::from((p.cell_id >> 16) as u8) * LANDBLOCK_METERS + p.origin.y;
        (x, y, p.origin.z)
    };
    let (ax, ay, az) = global(a);
    let (bx, by, bz) = global(b);
    ((ax - bx).powi(2) + (ay - by).powi(2) + (az - bz).powi(2)).sqrt()
}

/// A simulated pose worth telling a window about: another cell, a move of
/// more than a millimetre, or a turn.
fn live_pose_changed(old: &ac_proto::Position, new: &ac_proto::Position) -> bool {
    let (a, b) = (old.origin, new.origin);
    let moved = (a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2) > 1e-6;
    let (p, q) = (old.orientation, new.orientation);
    let dot = (p.w * q.w + p.x * q.x + p.y * q.y + p.z * q.z).abs();
    old.cell_id != new.cell_id || moved || dot < 0.999_999
}

/// The objects as parent-relation resolving sees them: a relation is
/// accepted when the child's position counter takes it.
struct AttachView<'a> {
    objects: &'a mut HashMap<u32, WorldObject>,
}

impl attachments::ResolveWorld for AttachView<'_> {
    fn is_known(&self, guid: u32) -> bool {
        self.objects.contains_key(&guid)
    }

    fn instance_of(&self, guid: u32) -> Option<u16> {
        self.objects.get(&guid).map(|o| o.gate.stamps().instance)
    }

    fn accept(&mut self, relation: &ParentRelation) -> bool {
        self.objects
            .get_mut(&relation.child_guid)
            .is_some_and(|child| {
                let instance = child.gate.stamps().instance;
                child
                    .gate
                    .try_accept_position_channel(instance, relation.child_position_sequence)
            })
    }
}

fn timestamps(create: &CreateObject) -> PhysicsTimestamps {
    let ts = create.physics.timestamps;
    PhysicsTimestamps {
        position: ts.position,
        movement: ts.movement,
        state: ts.state,
        vector: ts.vector,
        teleport: ts.teleport,
        server_controlled_move: ts.server_controlled_move,
        force_position: ts.force_position,
        obj_desc: ts.obj_desc,
        instance: ts.instance,
    }
}

#[cfg(test)]
pub(crate) mod tests;

#[cfg(test)]
mod entity_tests;

#[cfg(test)]
mod attachment_tests;
