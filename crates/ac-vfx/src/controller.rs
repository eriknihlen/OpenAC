//! What plays for whom. The server names objects by guid and may send a
//! "play script" before the object exists here; such packets wait, in
//! arrival order, until the object is ready, then replay once. A ready
//! object outside any cell gets no new scripts (and its queue pauses);
//! a frozen one pauses too. Typed plays go through the object's physics
//! script table; the default-script hooks use the object's profile.

use std::collections::{HashMap, HashSet, VecDeque};

use ac_dat::objects::{AnimationHook, HookData};
use glam::Vec3;

use crate::poses::EffectPoseSource;
use crate::profile::EntityEffectProfile;
use crate::script::PhysicsScriptRunner;
use crate::script_table::PhysicsScriptTableResolver;

/// A live object's state as far as effects care.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LiveRootState {
    /// Registered, placed in the world, visible and in a cell.
    pub in_cell: bool,
    /// Its physics state is frozen.
    pub frozen: bool,
    /// Its world position (used when it has no published pose).
    pub position: Vec3,
}

/// What the effects code asks the presentation about objects, by local id.
pub trait EffectOwnerQueries {
    /// The live object with this local id; `None` for anything else.
    fn live_root(&self, local_id: u32) -> Option<LiveRootState>;

    /// The object an equipped child is attached to.
    fn parent_of_attached_child(&self, local_id: u32) -> Option<u32> {
        let _ = local_id;
        None
    }

    /// The child attached at `part_index` of `parent`.
    fn child_at_part(&self, parent: u32, part_index: u32) -> Option<u32> {
        let _ = (parent, part_index);
        None
    }
}

/// Something the presentation must do (sound, bookkeeping).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum EffectEvent {
    /// Play the server's sound `sound_type` (a sound-table slot) at
    /// `volume` from `owner` at `position`.
    ServerSound {
        /// Local id.
        owner: u32,
        /// Where, world.
        position: Vec3,
        /// Sound-table slot.
        sound_type: u32,
        /// Wire volume.
        volume: f32,
    },
    /// The owner left: release what is held for it (its sound table).
    OwnerUnregistered {
        /// Local id.
        owner: u32,
    },
    /// The owner's sound table is now this one (or none).
    SoundTableChanged {
        /// Local id.
        owner: u32,
        /// Sound table id.
        sound_table: Option<u32>,
    },
}

/// The pieces a controller call works with.
pub struct EffectContext<'a> {
    /// The script runner.
    pub runner: &'a mut PhysicsScriptRunner,
    /// Owners' poses.
    pub poses: &'a dyn EffectPoseSource,
    /// Owners' live state.
    pub queries: &'a dyn EffectOwnerQueries,
}

impl std::fmt::Debug for EffectContext<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("EffectContext")
            .field("runner", &self.runner)
            .finish_non_exhaustive()
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
enum PendingEffect {
    Direct {
        script_did: u32,
    },
    Typed {
        raw_script_type: u32,
        intensity: f32,
    },
    Sound {
        sound_type: u32,
        volume: f32,
    },
}

#[derive(Debug, Clone)]
struct ReadyOwner {
    local_id: u32,
    profile: EntityEffectProfile,
    initial_presentation_barrier: bool,
}

/// Routes server effect messages and script-calling hooks to the runner.
#[derive(Debug)]
pub struct EntityEffectController {
    tables: PhysicsScriptTableResolver,
    ready: HashMap<u32, ReadyOwner>,
    guid_by_local: HashMap<u32, u32>,
    pending: HashMap<u32, VecDeque<PendingEffect>>,
    static_owners: HashMap<u32, EntityEffectProfile>,
    synthetic_owners: HashSet<u32>,
    dirty_set: HashSet<u32>,
    dirty_order: Vec<u32>,
    events: Vec<EffectEvent>,
    diagnostics: Vec<String>,
    last_pose_refresh_owner_visit_count: usize,
}

impl EntityEffectController {
    /// A controller resolving typed plays through `tables`.
    #[must_use]
    pub fn new(tables: PhysicsScriptTableResolver) -> Self {
        Self {
            tables,
            ready: HashMap::new(),
            guid_by_local: HashMap::new(),
            pending: HashMap::new(),
            static_owners: HashMap::new(),
            synthetic_owners: HashSet::new(),
            dirty_set: HashSet::new(),
            dirty_order: Vec::new(),
            events: Vec::new(),
            diagnostics: Vec::new(),
            last_pose_refresh_owner_visit_count: 0,
        }
    }

    /// Packets waiting for their owner.
    #[must_use]
    pub fn pending_packet_count(&self) -> usize {
        self.pending.values().map(VecDeque::len).sum()
    }

    /// Ready live owners plus dat owners.
    #[must_use]
    pub fn ready_owner_count(&self) -> usize {
        self.ready.len() + self.static_owners.len()
    }

    /// Owners the last pose refresh visited.
    #[must_use]
    pub fn last_pose_refresh_owner_visit_count(&self) -> usize {
        self.last_pose_refresh_owner_visit_count
    }

    /// Events for the presentation, since the last call.
    pub fn take_events(&mut self) -> Vec<EffectEvent> {
        std::mem::take(&mut self.events)
    }

    /// Messages about plays that could not happen, since the last call.
    pub fn take_diagnostics(&mut self) -> Vec<String> {
        std::mem::take(&mut self.diagnostics)
    }

    /// The server's "play script" for object `guid`.
    pub fn handle_direct(&mut self, ctx: &mut EffectContext<'_>, guid: u32, script_did: u32) {
        self.handle(ctx, guid, PendingEffect::Direct { script_did });
    }

    /// The server's "play script type" for object `guid`.
    pub fn handle_typed(
        &mut self,
        ctx: &mut EffectContext<'_>,
        guid: u32,
        raw_script_type: u32,
        intensity: f32,
    ) {
        self.handle(
            ctx,
            guid,
            PendingEffect::Typed {
                raw_script_type,
                intensity,
            },
        );
    }

    /// The server's sound event for object `guid`; it shares the packet
    /// queue with the scripts.
    pub fn handle_sound(
        &mut self,
        ctx: &mut EffectContext<'_>,
        guid: u32,
        sound_type: u32,
        volume: f32,
    ) {
        self.handle(ctx, guid, PendingEffect::Sound { sound_type, volume });
    }

    fn handle(&mut self, ctx: &mut EffectContext<'_>, guid: u32, effect: PendingEffect) {
        if guid == 0 {
            return;
        }
        if let Some(local) = self.ready_local_id(guid) {
            self.refresh_live_anchor(ctx, local);
            if self.can_start_owner(ctx.queries, local) {
                self.execute(ctx, local, effect);
            } else if self.is_waiting_for_initial_presentation(guid) {
                self.enqueue(guid, effect);
            }
            // A ready owner outside any cell drops the packet.
            return;
        }
        self.enqueue(guid, effect);
    }

    /// Object `guid` (local id `local_id`) is ready: registered, with its
    /// resources and profile. Its waiting packets replay now unless
    /// `awaiting_initial_placement` (the first placement has not been
    /// presented yet; see [`Self::on_presentation_bound`]).
    pub fn on_live_entity_ready(
        &mut self,
        ctx: &mut EffectContext<'_>,
        guid: u32,
        local_id: u32,
        profile: EntityEffectProfile,
        awaiting_initial_placement: bool,
    ) {
        self.prepare_live_entity_owner(ctx, guid, local_id, profile, awaiting_initial_placement);
        self.replay_pending_for_live_entity(ctx, guid);
    }

    /// [`Self::on_live_entity_ready`] without the replay.
    pub fn prepare_live_entity_owner(
        &mut self,
        ctx: &mut EffectContext<'_>,
        guid: u32,
        local_id: u32,
        profile: EntityEffectProfile,
        awaiting_initial_placement: bool,
    ) {
        let sound_table = profile.current_sound_table_did();
        if let Some(previous) = self.ready.insert(
            guid,
            ReadyOwner {
                local_id,
                profile,
                initial_presentation_barrier: awaiting_initial_placement,
            },
        ) && previous.local_id != local_id
            && self.guid_by_local.get(&previous.local_id) == Some(&guid)
        {
            self.guid_by_local.remove(&previous.local_id);
        }
        self.guid_by_local.insert(local_id, guid);
        self.refresh_live_anchor(ctx, local_id);
        self.events.push(EffectEvent::SoundTableChanged {
            owner: local_id,
            sound_table,
        });
    }

    /// The object's first placement is presented: open the barrier and
    /// replay its waiting packets in arrival order.
    pub fn on_presentation_bound(&mut self, ctx: &mut EffectContext<'_>, guid: u32) -> bool {
        let Some(owner) = self.ready.get_mut(&guid) else {
            return false;
        };
        owner.initial_presentation_barrier = false;
        let local = owner.local_id;
        self.refresh_live_anchor(ctx, local);
        self.try_replay_pending(ctx, guid, local);
        true
    }

    /// Replay object `guid`'s waiting packets if it is ready.
    pub fn replay_pending_for_live_entity(
        &mut self,
        ctx: &mut EffectContext<'_>,
        guid: u32,
    ) -> bool {
        let Some(local) = self.ready_local_id(guid) else {
            return false;
        };
        self.try_replay_pending(ctx, guid, local);
        true
    }

    /// The server re-described ready object `guid`.
    pub fn on_live_entity_description_changed(
        &mut self,
        guid: u32,
        profile: EntityEffectProfile,
    ) -> bool {
        let Some(owner) = self.ready.get_mut(&guid) else {
            return false;
        };
        let sound_table = profile.current_sound_table_did();
        owner.profile = profile;
        let owner = owner.local_id;
        self.events
            .push(EffectEvent::SoundTableChanged { owner, sound_table });
        true
    }

    /// A dat object (no guid) at `position` is ready.
    pub fn on_dat_static_entity_ready(
        &mut self,
        runner: &mut PhysicsScriptRunner,
        local_id: u32,
        position: Vec3,
        profile: EntityEffectProfile,
    ) {
        if local_id == 0 {
            return;
        }
        let sound_table = profile.current_sound_table_did();
        self.static_owners.insert(local_id, profile);
        runner.set_owner_anchor(local_id, position);
        self.events.push(EffectEvent::SoundTableChanged {
            owner: local_id,
            sound_table,
        });
    }

    /// A dat object went away.
    pub fn on_dat_static_entity_removed(
        &mut self,
        runner: &mut PhysicsScriptRunner,
        local_id: u32,
    ) {
        if self.static_owners.remove(&local_id).is_none() {
            return;
        }
        runner.stop_all_for_entity(local_id);
        self.events
            .push(EffectEvent::OwnerUnregistered { owner: local_id });
    }

    /// A presentation-only owner (a sky object) whose scripts always
    /// advance.
    pub fn register_synthetic_owner(&mut self, owner: u32) {
        if owner != 0 {
            self.synthetic_owners.insert(owner);
        }
    }

    /// Forget a presentation-only owner.
    pub fn unregister_synthetic_owner(&mut self, owner: u32) {
        self.synthetic_owners.remove(&owner);
    }

    /// Object `guid` was unregistered. `local_id` is its local id if it had
    /// one; `is_current_generation` is false when a newer object with the
    /// same guid already replaced it (its packets are kept for the newer
    /// one).
    pub fn on_live_entity_unregistered(
        &mut self,
        runner: &mut PhysicsScriptRunner,
        guid: u32,
        local_id: Option<u32>,
        is_current_generation: bool,
    ) {
        if is_current_generation {
            self.pending.remove(&guid);
        }
        let Some(local) = local_id else {
            return;
        };
        if self.ready.get(&guid).is_some_and(|r| r.local_id == local) {
            self.ready.remove(&guid);
            // A dirty mark belongs to the departed incarnation.
            if self.dirty_set.remove(&guid) {
                self.dirty_order.retain(|g| *g != guid);
            }
        }
        if self.guid_by_local.get(&local) == Some(&guid) {
            self.guid_by_local.remove(&local);
        }
        runner.stop_all_for_entity(local);
        self.events
            .push(EffectEvent::OwnerUnregistered { owner: local });
    }

    /// The server deleted an object that never arrived: drop its packets.
    pub fn forget_unknown_owner(&mut self, guid: u32) {
        self.pending.remove(&guid);
    }

    /// The session's network state reset: every live owner goes, dat
    /// owners stay.
    pub fn clear_network_state(&mut self, runner: &mut PhysicsScriptRunner) {
        for owner in self.ready.values() {
            runner.stop_all_for_entity(owner.local_id);
            self.events.push(EffectEvent::OwnerUnregistered {
                owner: owner.local_id,
            });
        }
        self.ready.clear();
        self.guid_by_local.clear();
        self.pending.clear();
        self.dirty_set.clear();
        self.dirty_order.clear();
    }

    /// Refresh the script anchor (and replay waiting packets) of every
    /// owner marked dirty since the last call.
    pub fn refresh_live_owner_poses(&mut self, ctx: &mut EffectContext<'_>) {
        self.last_pose_refresh_owner_visit_count = 0;
        let guids = std::mem::take(&mut self.dirty_order);
        self.dirty_set.clear();
        for guid in guids {
            let Some(local) = self.ready_local_id(guid) else {
                continue;
            };
            self.last_pose_refresh_owner_visit_count += 1;
            self.refresh_live_anchor(ctx, local);
            self.try_replay_pending(ctx, guid, local);
        }
    }

    /// Ready object `guid` moved (or became visible again).
    pub fn mark_live_owner_pose_dirty(&mut self, guid: u32) {
        if self.ready.contains_key(&guid) && self.dirty_set.insert(guid) {
            self.dirty_order.push(guid);
        }
    }

    /// The pose of local owner `local_id` changed.
    pub fn on_pose_changed(&mut self, local_id: u32) {
        if let Some(&guid) = self.guid_by_local.get(&local_id) {
            self.mark_live_owner_pose_dirty(guid);
        }
    }

    /// Queue script `script_did` on `owner` if it may start scripts.
    pub fn play_direct(
        &mut self,
        ctx: &mut EffectContext<'_>,
        owner: u32,
        script_did: u32,
    ) -> bool {
        if !self.can_start_owner(ctx.queries, owner) {
            return false;
        }
        ctx.runner.play_direct(owner, script_did)
    }

    /// Queue the script `owner`'s table names for the type and intensity.
    pub fn play_typed(
        &mut self,
        ctx: &mut EffectContext<'_>,
        owner: u32,
        raw_script_type: u32,
        intensity: f32,
    ) -> bool {
        if !self.can_start_owner(ctx.queries, owner) {
            return false;
        }
        self.resolve_and_queue_typed(ctx, owner, raw_script_type, intensity)
    }

    /// [`Self::play_typed`] for an owner going hidden: it queues even when
    /// the owner is outside any cell (the queue waits).
    pub fn play_typed_from_hidden_transition(
        &mut self,
        ctx: &mut EffectContext<'_>,
        owner: u32,
        raw_script_type: u32,
        intensity: f32,
    ) -> bool {
        self.resolve_and_queue_typed(ctx, owner, raw_script_type, intensity)
    }

    /// Play `owner`'s default script (by its profile's type and
    /// intensity).
    pub fn play_default(&mut self, ctx: &mut EffectContext<'_>, owner: u32) -> bool {
        if !self.can_start_owner(ctx.queries, owner) {
            return false;
        }
        let Some(profile) = self.profile(owner) else {
            return false;
        };
        let (raw, intensity) = (
            profile.raw_default_script_type(),
            profile.default_script_intensity(),
        );
        self.play_typed(ctx, owner, raw, intensity)
    }

    /// A hook `owner` fired at `world_position`: script calls and default
    /// scripts start here.
    pub fn on_hook(
        &mut self,
        ctx: &mut EffectContext<'_>,
        owner: u32,
        world_position: Vec3,
        hook: &AnimationHook,
    ) {
        ctx.runner.set_owner_anchor(owner, world_position);
        match hook.data {
            HookData::CallPes { pes, pause } if self.can_start_owner(ctx.queries, owner) => {
                ctx.runner.schedule_call_pes(owner, pes, pause);
            }
            HookData::DefaultScript => {
                self.play_default(ctx, owner);
            }
            HookData::DefaultScriptPart { part_index } => {
                if let Some(child) = ctx.queries.child_at_part(owner, part_index) {
                    self.play_default(ctx, child);
                }
            }
            _ => {}
        }
    }

    /// Whether `owner`'s scripts may advance: dat and synthetic owners
    /// always; a live object (or an attached child's parent) when it is in
    /// a cell and not frozen.
    #[must_use]
    pub fn can_advance_owner(&self, queries: &dyn EffectOwnerQueries, owner: u32) -> bool {
        if self.static_owners.contains_key(&owner) || self.synthetic_owners.contains(&owner) {
            return true;
        }
        let root = queries.parent_of_attached_child(owner).unwrap_or(owner);
        queries
            .live_root(root)
            .is_some_and(|s| s.in_cell && !s.frozen)
    }

    fn can_start_owner(&self, queries: &dyn EffectOwnerQueries, owner: u32) -> bool {
        if self.static_owners.contains_key(&owner) || self.synthetic_owners.contains(&owner) {
            return true;
        }
        let root = queries.parent_of_attached_child(owner).unwrap_or(owner);
        queries.live_root(root).is_some_and(|s| s.in_cell)
    }

    fn ready_local_id(&self, guid: u32) -> Option<u32> {
        self.ready.get(&guid).map(|r| r.local_id)
    }

    fn is_waiting_for_initial_presentation(&self, guid: u32) -> bool {
        self.ready
            .get(&guid)
            .is_some_and(|r| r.initial_presentation_barrier)
    }

    fn profile(&self, owner: u32) -> Option<&EntityEffectProfile> {
        if let Some(guid) = self.guid_by_local.get(&owner)
            && let Some(ready) = self.ready.get(guid)
            && ready.local_id == owner
        {
            return Some(&ready.profile);
        }
        self.static_owners.get(&owner)
    }

    fn resolve_and_queue_typed(
        &mut self,
        ctx: &mut EffectContext<'_>,
        owner: u32,
        raw_script_type: u32,
        intensity: f32,
    ) -> bool {
        let Some(table_did) = self
            .profile(owner)
            .and_then(EntityEffectProfile::current_physics_script_table_did)
        else {
            self.diagnostics.push(format!(
                "No PhysicsScriptTable for owner 0x{owner:08X}, type 0x{raw_script_type:08X}."
            ));
            return false;
        };
        match self.tables.resolve(table_did, raw_script_type, intensity) {
            Ok(Some(script_did)) => ctx.runner.play_direct(owner, script_did),
            Ok(None) => {
                self.diagnostics.push(format!(
                    "No typed PhysicsScript for owner 0x{owner:08X}, table 0x{table_did:08X}, \
                     type 0x{raw_script_type:08X}, intensity {intensity}."
                ));
                false
            }
            Err(error) => {
                self.diagnostics.push(format!(
                    "No typed PhysicsScript for owner 0x{owner:08X}, table 0x{table_did:08X}, \
                     type 0x{raw_script_type:08X}, intensity {intensity}. Load failed: {error}"
                ));
                false
            }
        }
    }

    fn refresh_live_anchor(&mut self, ctx: &mut EffectContext<'_>, local: u32) {
        let anchor = ctx
            .poses
            .root_pose(local)
            .map(|root| root.w_axis.truncate())
            .or_else(|| ctx.queries.live_root(local).map(|s| s.position));
        if let Some(anchor) = anchor {
            ctx.runner.set_owner_anchor(local, anchor);
        }
    }

    fn enqueue(&mut self, guid: u32, effect: PendingEffect) {
        self.pending.entry(guid).or_default().push_back(effect);
    }

    fn try_replay_pending(&mut self, ctx: &mut EffectContext<'_>, guid: u32, local: u32) {
        if !self.can_start_owner(ctx.queries, local) {
            return;
        }
        let Some(pending) = self.pending.remove(&guid) else {
            return;
        };
        for effect in pending {
            self.execute(ctx, local, effect);
        }
    }

    fn execute(&mut self, ctx: &mut EffectContext<'_>, local: u32, effect: PendingEffect) {
        match effect {
            PendingEffect::Direct { script_did } => {
                self.play_direct(ctx, local, script_did);
            }
            PendingEffect::Typed {
                raw_script_type,
                intensity,
            } => {
                self.play_typed(ctx, local, raw_script_type, intensity);
            }
            PendingEffect::Sound { sound_type, volume } => {
                if self.can_start_owner(ctx.queries, local) {
                    let position = ctx
                        .poses
                        .root_pose(local)
                        .map_or(Vec3::ZERO, |root| root.w_axis.truncate());
                    self.events.push(EffectEvent::ServerSound {
                        owner: local,
                        position,
                        sound_type,
                        volume,
                    });
                }
            }
        }
    }
}

#[cfg(test)]
pub(crate) mod tests;
