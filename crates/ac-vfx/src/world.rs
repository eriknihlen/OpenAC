//! Everything put together for one presentation: the particle system, the
//! hook sink, the script runner, the effect controller, the pose registry,
//! the visibility controller and the sky scripts. The presentation feeds it
//! (server messages, animation hooks, poses, the view) and each frame it
//! advances and hands out what to draw and what to play.

use std::collections::HashSet;
use std::fmt;
use std::sync::Arc;

use ac_core::sky::DayGroupData;
use ac_dat::objects::{AnimationHook, HookData};
use glam::{Mat4, Quat, Vec3};

use crate::controller::{EffectContext, EffectEvent, EffectOwnerQueries, EntityEffectController};
use crate::dats::VfxDats;
use crate::desc::EmitterDescRegistry;
use crate::hook_sink::ParticleHookSink;
use crate::particles::{EmitterHandle, ParticleSystem};
use crate::poses::{EffectPoseRegistry, EffectPoseSource, PoseError, root_matrix};
use crate::profile::EntityEffectProfile;
use crate::random::DotNetRandom;
use crate::script::{PhysicsScriptRunner, ScriptHookSink, ScriptTimeError};
use crate::script_table::PhysicsScriptTableResolver;
use crate::sky::{SkyPesFrameController, SkyTargets};
use crate::visibility::ParticleVisibilityController;

/// Something the presentation must act on.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum VfxEvent {
    /// A physics script fired a hook the effects code does not handle
    /// (sound, sound table, tweaked sound, light, fade, scale, ...): route
    /// it to those sinks, at `position`.
    ScriptHook {
        /// Owner local id.
        owner: u32,
        /// The owner's anchor, world.
        position: Vec3,
        /// The hook.
        hook: AnimationHook,
    },
    /// From the effect controller (server sounds, sound tables, owner
    /// release).
    Effect(EffectEvent),
    /// A sky owner stopped: stop its sounds.
    SkyOwnerStopped {
        /// Owner local id.
        owner: u32,
    },
}

/// An object whose effects start: its pose and Setup script data.
#[derive(Debug, Clone, PartialEq)]
pub struct ActivationInfo {
    /// Local id.
    pub local_id: u32,
    /// A dat object (no server guid).
    pub is_dat_static: bool,
    /// Position, world.
    pub position: Vec3,
    /// Rotation, world.
    pub rotation: Quat,
    /// Its cell.
    pub cell_id: u32,
    /// The Setup's default script (0: none).
    pub script_id: u32,
    /// Each Setup part's pose relative to the root (resting placement,
    /// object scale applied).
    pub part_transforms: Vec<Mat4>,
    /// Which parts draw (all when absent).
    pub part_availability: Option<Vec<bool>>,
    /// A dat object's profile (its Setup's tables).
    pub effect_profile: Option<EntityEffectProfile>,
}

/// A dat object id activated twice.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DuplicateStaticOwner(pub u32);

impl fmt::Display for DuplicateStaticOwner {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "Dat-static owner 0x{:08X} is already active; static ids must be unique.",
            self.0
        )
    }
}

impl std::error::Error for DuplicateStaticOwner {}

/// The effects of one presentation.
pub struct VfxWorld {
    particles: ParticleSystem,
    sink: ParticleHookSink,
    scripts: PhysicsScriptRunner,
    effects: EntityEffectController,
    poses: EffectPoseRegistry,
    visibility: ParticleVisibilityController,
    sky: SkyPesFrameController,
    static_owners: HashSet<u32>,
    events: Vec<VfxEvent>,
    retired: Vec<EmitterHandle>,
    diagnostics: Vec<String>,
}

impl fmt::Debug for VfxWorld {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("VfxWorld")
            .field("particles", &self.particles)
            .field("scripts", &self.scripts)
            .finish_non_exhaustive()
    }
}

impl VfxWorld {
    /// Effects reading the dats, with random numbers seeded by `seed`.
    #[must_use]
    pub fn new(dats: Arc<dyn VfxDats>, seed: i32) -> Self {
        Self::from_parts(
            ParticleSystem::new(
                EmitterDescRegistry::with_dats(Arc::clone(&dats)),
                Box::new(DotNetRandom::new(seed)),
            ),
            PhysicsScriptRunner::with_dats(
                Arc::clone(&dats),
                Box::new(DotNetRandom::new(seed.wrapping_add(1))),
            ),
            PhysicsScriptTableResolver::with_dats(dats),
        )
    }

    /// Effects from ready-made parts (tests, tools).
    #[must_use]
    pub fn from_parts(
        particles: ParticleSystem,
        scripts: PhysicsScriptRunner,
        tables: PhysicsScriptTableResolver,
    ) -> Self {
        Self {
            particles,
            sink: ParticleHookSink::new(),
            scripts,
            effects: EntityEffectController::new(tables),
            poses: EffectPoseRegistry::new(),
            visibility: ParticleVisibilityController::new(),
            sky: SkyPesFrameController::new(),
            static_owners: HashSet::new(),
            events: Vec::new(),
            retired: Vec::new(),
            diagnostics: Vec::new(),
        }
    }

    /// The particles (to build draw lists from).
    #[must_use]
    pub fn particles(&self) -> &ParticleSystem {
        &self.particles
    }

    /// The script runner.
    #[must_use]
    pub fn scripts(&self) -> &PhysicsScriptRunner {
        &self.scripts
    }

    /// The effect controller.
    #[must_use]
    pub fn effects(&self) -> &EntityEffectController {
        &self.effects
    }

    /// The particle hook sink.
    #[must_use]
    pub fn sink(&self) -> &ParticleHookSink {
        &self.sink
    }

    /// Owners' poses.
    #[must_use]
    pub fn poses(&self) -> &EffectPoseRegistry {
        &self.poses
    }

    /// The view collector (the renderer reports drawn cells to it).
    pub fn visibility_mut(&mut self) -> &mut ParticleVisibilityController {
        &mut self.visibility
    }

    /// Events since the last call.
    pub fn take_events(&mut self) -> Vec<VfxEvent> {
        self.collect();
        std::mem::take(&mut self.events)
    }

    /// Emitters that died since the last call (the renderer releases what
    /// it holds for them).
    pub fn take_retired_emitters(&mut self) -> Vec<EmitterHandle> {
        self.collect();
        std::mem::take(&mut self.retired)
    }

    /// Diagnostics of every part since the last call.
    pub fn take_diagnostics(&mut self) -> Vec<String> {
        self.collect();
        std::mem::take(&mut self.diagnostics)
    }

    // ---- poses ----

    /// Publish an owner's whole pose (see [`EffectPoseRegistry::publish`]).
    pub fn publish_pose(
        &mut self,
        owner: u32,
        root: Mat4,
        parts: &[Mat4],
        cell_id: u32,
        availability: Option<&[bool]>,
    ) -> Result<(), PoseError> {
        let result = self
            .poses
            .publish(owner, root, parts, cell_id, availability);
        self.forward_pose_changes();
        result
    }

    /// Move an owner's root (and cell).
    pub fn update_root(&mut self, owner: u32, root: Mat4, cell_id: u32) -> bool {
        let known = self.poses.update_root(owner, root, cell_id);
        self.forward_pose_changes();
        known
    }

    /// Forget an owner's pose.
    pub fn remove_pose(&mut self, owner: u32) -> bool {
        let known = self.poses.remove(owner);
        self.forward_pose_changes();
        known
    }

    fn forward_pose_changes(&mut self) {
        for owner in self.poses.take_changed_owners() {
            self.sink.on_pose_changed(owner);
            self.effects.on_pose_changed(owner);
        }
    }

    // ---- entity lifetime (the Setup default script) ----

    /// An object's effects start: publish its pose, register a dat object's
    /// profile, play its Setup's default script.
    pub fn activate_entity(&mut self, info: ActivationInfo) -> Result<(), DuplicateStaticOwner> {
        let owner = info.local_id;
        if owner == 0 {
            return Ok(());
        }
        if info.is_dat_static && !self.static_owners.insert(owner) {
            return Err(DuplicateStaticOwner(owner));
        }
        if let Err(error) = self.poses.publish(
            owner,
            root_matrix(info.position, info.rotation),
            &info.part_transforms,
            info.cell_id,
            info.part_availability.as_deref(),
        ) {
            self.diagnostics.push(error.to_string());
        }
        self.forward_pose_changes();
        if info.is_dat_static
            && let Some(profile) = info.effect_profile
        {
            self.effects.on_dat_static_entity_ready(
                &mut self.scripts,
                owner,
                info.position,
                profile,
            );
        }
        if info.script_id != 0 {
            self.scripts.play(info.script_id, owner, info.position);
        }
        Ok(())
    }

    /// An object's effects end: its scripts stop, its emitters go at once,
    /// its pose is forgotten.
    pub fn deactivate_entity(&mut self, local_id: u32) {
        if local_id == 0 {
            return;
        }
        self.scripts.stop_all_for_entity(local_id);
        self.sink
            .stop_all_for_entity(&mut self.particles, local_id, false);
        self.poses.remove(local_id);
        self.forward_pose_changes();
        if self.static_owners.remove(&local_id) {
            self.effects
                .on_dat_static_entity_removed(&mut self.scripts, local_id);
        }
    }

    /// Hide or show an object's emitters without ending them.
    pub fn set_entity_presentation_visible(&mut self, owner: u32, visible: bool) {
        self.sink
            .set_entity_presentation_visible(&mut self.particles, owner, visible);
    }

    /// Mark an object as an examination preview.
    pub fn set_entity_examination_object(&mut self, owner: u32, is_examination_object: bool) {
        self.sink
            .set_entity_examination_object(&mut self.particles, owner, is_examination_object);
    }

    // ---- live objects (server) ----

    /// Live object `guid` is ready (see
    /// [`EntityEffectController::on_live_entity_ready`]).
    pub fn on_live_entity_ready(
        &mut self,
        queries: &dyn EffectOwnerQueries,
        guid: u32,
        local_id: u32,
        profile: EntityEffectProfile,
        awaiting_initial_placement: bool,
    ) {
        let mut ctx = EffectContext {
            runner: &mut self.scripts,
            poses: &self.poses,
            queries,
        };
        self.effects.on_live_entity_ready(
            &mut ctx,
            guid,
            local_id,
            profile,
            awaiting_initial_placement,
        );
    }

    /// [`Self::on_live_entity_ready`] without the replay (see
    /// [`EntityEffectController::prepare_live_entity_owner`]): the caller
    /// presents the object's state in between.
    pub fn prepare_live_entity_owner(
        &mut self,
        queries: &dyn EffectOwnerQueries,
        guid: u32,
        local_id: u32,
        profile: EntityEffectProfile,
        awaiting_initial_placement: bool,
    ) {
        let mut ctx = EffectContext {
            runner: &mut self.scripts,
            poses: &self.poses,
            queries,
        };
        self.effects.prepare_live_entity_owner(
            &mut ctx,
            guid,
            local_id,
            profile,
            awaiting_initial_placement,
        );
    }

    /// Replay what waited for ready object `guid` (see
    /// [`EntityEffectController::replay_pending_for_live_entity`]).
    pub fn replay_pending_for_live_entity(
        &mut self,
        queries: &dyn EffectOwnerQueries,
        guid: u32,
    ) -> bool {
        let mut ctx = EffectContext {
            runner: &mut self.scripts,
            poses: &self.poses,
            queries,
        };
        self.effects.replay_pending_for_live_entity(&mut ctx, guid)
    }

    /// Live object `guid`'s first placement is presented.
    pub fn on_presentation_bound(&mut self, queries: &dyn EffectOwnerQueries, guid: u32) -> bool {
        let mut ctx = EffectContext {
            runner: &mut self.scripts,
            poses: &self.poses,
            queries,
        };
        self.effects.on_presentation_bound(&mut ctx, guid)
    }

    /// The server re-described live object `guid`.
    pub fn on_live_entity_description_changed(
        &mut self,
        guid: u32,
        profile: EntityEffectProfile,
    ) -> bool {
        self.effects
            .on_live_entity_description_changed(guid, profile)
    }

    /// Live object `guid` left (see
    /// [`EntityEffectController::on_live_entity_unregistered`]); call
    /// [`Self::deactivate_entity`] for its local id as well.
    pub fn on_live_entity_unregistered(
        &mut self,
        guid: u32,
        local_id: Option<u32>,
        is_current_generation: bool,
    ) {
        self.effects.on_live_entity_unregistered(
            &mut self.scripts,
            guid,
            local_id,
            is_current_generation,
        );
    }

    /// The server deleted an object that never arrived.
    pub fn forget_unknown_owner(&mut self, guid: u32) {
        self.effects.forget_unknown_owner(guid);
    }

    /// Live object `guid` moved or became visible again.
    pub fn mark_live_owner_pose_dirty(&mut self, guid: u32) {
        self.effects.mark_live_owner_pose_dirty(guid);
    }

    /// The session's network state reset.
    pub fn clear_network_state(&mut self) {
        self.effects.clear_network_state(&mut self.scripts);
    }

    /// Server "play script" (0xF754).
    pub fn handle_play_script(
        &mut self,
        queries: &dyn EffectOwnerQueries,
        guid: u32,
        script_did: u32,
    ) {
        let mut ctx = EffectContext {
            runner: &mut self.scripts,
            poses: &self.poses,
            queries,
        };
        self.effects.handle_direct(&mut ctx, guid, script_did);
    }

    /// Server "play script type" (0xF755).
    pub fn handle_play_script_type(
        &mut self,
        queries: &dyn EffectOwnerQueries,
        guid: u32,
        raw_script_type: u32,
        intensity: f32,
    ) {
        let mut ctx = EffectContext {
            runner: &mut self.scripts,
            poses: &self.poses,
            queries,
        };
        self.effects
            .handle_typed(&mut ctx, guid, raw_script_type, intensity);
    }

    /// Server sound (0xF750); it queues with the script packets.
    pub fn handle_sound(
        &mut self,
        queries: &dyn EffectOwnerQueries,
        guid: u32,
        sound_type: u32,
        volume: f32,
    ) {
        let mut ctx = EffectContext {
            runner: &mut self.scripts,
            poses: &self.poses,
            queries,
        };
        self.effects
            .handle_sound(&mut ctx, guid, sound_type, volume);
    }

    /// Play `owner`'s typed script while it goes hidden.
    pub fn play_typed_from_hidden_transition(
        &mut self,
        queries: &dyn EffectOwnerQueries,
        owner: u32,
        raw_script_type: u32,
        intensity: f32,
    ) -> bool {
        let mut ctx = EffectContext {
            runner: &mut self.scripts,
            poses: &self.poses,
            queries,
        };
        self.effects
            .play_typed_from_hidden_transition(&mut ctx, owner, raw_script_type, intensity)
    }

    // ---- hooks and frames ----

    /// Publish the frame's script clock before animation hooks are
    /// delivered.
    pub fn publish_time(&mut self, script_time: f64) -> Result<(), ScriptTimeError> {
        self.scripts.publish_time(script_time)
    }

    /// An animation hook `owner` fired at `world_position` (from the
    /// sequencer): particle hooks and script calls are handled here.
    pub fn on_animation_hook(
        &mut self,
        queries: &dyn EffectOwnerQueries,
        owner: u32,
        world_position: Vec3,
        hook: &AnimationHook,
    ) {
        let mut router = HookRouter {
            particles: &mut self.particles,
            sink: &mut self.sink,
            effects: &mut self.effects,
            poses: &self.poses,
            queries,
            events: &mut self.events,
            forward_other_hooks: false,
        };
        router.route(&mut self.scripts, owner, world_position, hook);
    }

    /// Start, keep or stop the sky objects' scripts (see
    /// [`SkyPesFrameController::update`]).
    pub fn update_sky(
        &mut self,
        day_fraction: f32,
        day_group: Option<&DayGroupData>,
        camera_world_position: Vec3,
        sky_active: bool,
        disable_most_weather_effects: bool,
    ) {
        let mut stopped = Vec::new();
        let mut targets = SkyTargets {
            scripts: &mut self.scripts,
            sink: &mut self.sink,
            particles: &mut self.particles,
            poses: &mut self.poses,
            effects: Some(&mut self.effects),
            stopped_owners: &mut stopped,
            diagnostics: &mut self.diagnostics,
        };
        self.sky.update(
            &mut targets,
            day_fraction,
            day_group,
            camera_world_position,
            sky_active,
            disable_most_weather_effects,
        );
        self.events.extend(
            stopped
                .into_iter()
                .map(|owner| VfxEvent::SkyOwnerStopped { owner }),
        );
        self.forward_pose_changes();
    }

    /// One frame: refresh script anchors and attached emitters from the
    /// poses, apply the last completed view (times `range_multiplier`),
    /// advance the particles by `dt` seconds and the scripts to
    /// `script_time`.
    pub fn tick(
        &mut self,
        queries: &dyn EffectOwnerQueries,
        dt: f32,
        script_time: f64,
        range_multiplier: f32,
    ) -> Result<(), ScriptTimeError> {
        self.forward_pose_changes();
        {
            let mut ctx = EffectContext {
                runner: &mut self.scripts,
                poses: &self.poses,
                queries,
            };
            self.effects.refresh_live_owner_poses(&mut ctx);
        }
        self.sink
            .refresh_attached_emitters(&mut self.particles, &self.poses);
        self.visibility.apply(&mut self.particles, range_multiplier);
        self.particles.tick(dt);
        let mut router = HookRouter {
            particles: &mut self.particles,
            sink: &mut self.sink,
            effects: &mut self.effects,
            poses: &self.poses,
            queries,
            events: &mut self.events,
            forward_other_hooks: true,
        };
        let result = self.scripts.tick(script_time, &mut router);
        self.collect();
        result
    }

    fn collect(&mut self) {
        self.retired
            .extend(self.sink.take_retired_emitters(&mut self.particles));
        self.events
            .extend(self.effects.take_events().into_iter().map(VfxEvent::Effect));
        self.diagnostics.extend(self.sink.take_diagnostics());
        self.diagnostics.extend(self.scripts.take_diagnostics());
        self.diagnostics.extend(self.effects.take_diagnostics());
    }
}

/// Sends a hook to the particle sink and the effect controller, and (for
/// script hooks) everything else to the presentation.
struct HookRouter<'a> {
    particles: &'a mut ParticleSystem,
    sink: &'a mut ParticleHookSink,
    effects: &'a mut EntityEffectController,
    poses: &'a EffectPoseRegistry,
    queries: &'a dyn EffectOwnerQueries,
    events: &'a mut Vec<VfxEvent>,
    forward_other_hooks: bool,
}

impl HookRouter<'_> {
    fn route(
        &mut self,
        runner: &mut PhysicsScriptRunner,
        owner: u32,
        position: Vec3,
        hook: &AnimationHook,
    ) {
        self.sink.on_hook(
            self.particles,
            self.poses as &dyn EffectPoseSource,
            owner,
            hook,
        );
        let mut ctx = EffectContext {
            runner,
            poses: self.poses,
            queries: self.queries,
        };
        self.effects.on_hook(&mut ctx, owner, position, hook);
        let handled_here = matches!(
            hook.data,
            HookData::CreateParticle { .. }
                | HookData::CreateBlockingParticle { .. }
                | HookData::DestroyParticle { .. }
                | HookData::StopParticle { .. }
                | HookData::CallPes { .. }
                | HookData::DefaultScript
                | HookData::DefaultScriptPart { .. }
        );
        if self.forward_other_hooks && !handled_here {
            self.events.push(VfxEvent::ScriptHook {
                owner,
                position,
                hook: *hook,
            });
        }
    }
}

impl ScriptHookSink for HookRouter<'_> {
    fn on_script_hook(
        &mut self,
        runner: &mut PhysicsScriptRunner,
        owner: u32,
        anchor: Vec3,
        hook: &AnimationHook,
    ) {
        self.route(runner, owner, anchor, hook);
    }

    fn can_advance_owner(&self, owner: u32) -> bool {
        self.effects.can_advance_owner(self.queries, owner)
    }
}

#[cfg(test)]
mod tests;
