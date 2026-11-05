//! The particle simulation: emitters spawn particles by their description's
//! rules, particles move along their type's path, grow, fade and die. One
//! [`ParticleSystem`] holds every live emitter of a session's presentation.
//!
//! Units: seconds, metres; sizes are scale factors on the particle model,
//! alpha is opacity 0..1.

use std::cell::Cell;
use std::collections::{BTreeMap, BTreeSet, HashSet};
use std::fmt;
use std::sync::Arc;

use glam::{Quat, Vec3};

use crate::desc::{
    EmitterDesc, EmitterDescFailure, EmitterDescRegistry, EmitterFlags, EmitterKind, ParticleType,
};
use crate::random::RandomSource;

/// Identifies a live emitter. Handles start at 1, grow monotonically and
/// are never reused, so their order is spawn order.
pub type EmitterHandle = i32;

/// The render stage an emitter draws in.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum ParticleRenderPass {
    /// With the world.
    #[default]
    Scene,
    /// With the sky, before the world.
    SkyPreScene,
    /// With the sky, after the world.
    SkyPostScene,
}

impl ParticleRenderPass {
    const COUNT: usize = 3;

    fn index(self) -> usize {
        match self {
            Self::Scene => 0,
            Self::SkyPreScene => 1,
            Self::SkyPostScene => 2,
        }
    }
}

/// Which unattached emitters a scoped render copy admits, by the kind of
/// cell they are in.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum UnattachedEmitterCellScope {
    /// Every one.
    #[default]
    Any,
    /// Outdoor land cells (low word `1..0x100`).
    OutdoorCells,
    /// Interior cells (low word `>= 0x100`).
    InteriorCells,
}

/// Who decides whether an emitter may be drawn.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ParticleVisibilityPolicy {
    /// The world view: owner cell in view and owner within the emitter's
    /// degrade distance of the viewer.
    #[default]
    World,
    /// An examination preview: always eligible.
    Examination,
    /// Owned by a dedicated render pass (the sky): always eligible.
    PassOwned,
}

/// One particle. Positions are world metres.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Particle {
    /// The emitter's anchor when the particle was born.
    pub emission_origin: Vec3,
    /// The emitter's orientation when the particle was born.
    pub spawn_rotation: Quat,
    /// Current position.
    pub position: Vec3,
    /// The first motion vector (kept for inspection).
    pub velocity: Vec3,
    /// Spawn offset from the origin, world space.
    pub offset: Vec3,
    /// First motion vector, resolved at birth.
    pub a: Vec3,
    /// Second motion vector, resolved at birth.
    pub b: Vec3,
    /// Third motion vector, resolved at birth.
    pub c: Vec3,
    /// System time at birth.
    pub spawned_at: f32,
    /// The emitter's frozen time at birth.
    pub frozen_time_at_spawn: f32,
    /// Life, seconds.
    pub lifetime: f32,
    /// Age, seconds.
    pub age: f32,
    /// Scale at birth.
    pub start_size: f32,
    /// Scale at death.
    pub end_size: f32,
    /// Opacity at birth.
    pub start_alpha: f32,
    /// Opacity at death.
    pub end_alpha: f32,
    /// Current colour and opacity, ARGB.
    pub color_argb: u32,
    /// Current scale.
    pub size: f32,
    /// Current quad rotation.
    pub rotation: f32,
    /// Alive.
    pub alive: bool,
}

/// One live emitter and its particle slots. Read it through
/// [`ParticleSystem`]; every change goes through the system so its indexes
/// stay right.
#[derive(Debug, Clone)]
pub struct ParticleEmitter {
    /// Handle.
    pub handle: EmitterHandle,
    /// Description.
    pub desc: Arc<EmitterDesc>,
    /// Where new particles are born (and where attached ones follow), world.
    pub anchor_pos: Vec3,
    /// The owner's root position, for the degrade-distance test.
    pub owner_position: Vec3,
    /// The owner's cell (0: none).
    pub owner_cell_id: u32,
    /// Emitter orientation, world.
    pub anchor_rot: Quat,
    /// The object it is attached to (0: none).
    pub attached_object_id: u32,
    /// Who decides whether it may be drawn.
    pub visibility_policy: ParticleVisibilityPolicy,
    /// The presentation shows it.
    pub presentation_visible: bool,
    /// It advances when the system ticks.
    pub simulation_enabled: bool,
    /// The last view test passed.
    pub view_eligible: bool,
    /// The last tick ran the degraded branch.
    pub degraded_out: bool,
    /// The owner part it is attached to (-1: the root).
    pub attached_part_index: i32,
    /// Particle slots, `max_particles` of them.
    pub particles: Vec<Particle>,
    /// Render stage.
    pub render_pass: ParticleRenderPass,
    /// Particles counted alive (a degraded finite emitter counts emissions
    /// it did not draw).
    pub active_count: i32,
    /// Fractional emissions owed by the per-second rate path.
    pub emitted_accumulator: f32,
    /// System time it started.
    pub started_at: f32,
    /// System time of the last emission.
    pub last_emit_time: f32,
    /// Time spent degraded (infinite emitters freeze their particles' age).
    pub frozen_time: f32,
    /// Anchor at the last emission.
    pub last_emit_offset: Vec3,
    /// Particles emitted in all.
    pub total_emitted: i32,
    /// Stopped emitting; retires when its last particle dies.
    pub finished: bool,
}

impl ParticleEmitter {
    fn is_renderable(&self) -> bool {
        self.presentation_visible && self.view_eligible
    }
}

/// Where and how a new emitter is placed.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SpawnOptions {
    /// Emitter orientation.
    pub rotation: Quat,
    /// The object it is attached to (0: none).
    pub attached_object_id: u32,
    /// The owner part it is attached to (-1: the root).
    pub attached_part_index: i32,
    /// Render stage.
    pub render_pass: ParticleRenderPass,
    /// Who decides whether it may be drawn.
    pub visibility_policy: ParticleVisibilityPolicy,
}

impl Default for SpawnOptions {
    fn default() -> Self {
        Self {
            rotation: Quat::IDENTITY,
            attached_object_id: 0,
            attached_part_index: -1,
            render_pass: ParticleRenderPass::Scene,
            visibility_policy: ParticleVisibilityPolicy::World,
        }
    }
}

#[derive(Debug, Default)]
struct Bucket {
    logical_count: i32,
    renderable: BTreeSet<EmitterHandle>,
}

/// Emitter buckets by owner or cell id. The renderer asks for every land
/// cell it walks each frame, so the lookup takes the fast hash (ids are not
/// attacker-chosen; the maps are only looked up, never iterated).
type Buckets = rustc_hash::FxHashMap<u32, Bucket>;

/// The render worksets: renderable emitters per pass, unattached ones per
/// pass, and per owner and per cell (each with a logical count).
#[derive(Debug, Default)]
struct RenderIndexes {
    renderable: [BTreeSet<EmitterHandle>; ParticleRenderPass::COUNT],
    unattached: [BTreeSet<EmitterHandle>; ParticleRenderPass::COUNT],
    owners: [Buckets; ParticleRenderPass::COUNT],
    cells: [Buckets; ParticleRenderPass::COUNT],
}

impl RenderIndexes {
    fn add(&mut self, em: &ParticleEmitter) {
        let pass = em.render_pass.index();
        if em.attached_object_id != 0 {
            self.owners[pass]
                .entry(em.attached_object_id)
                .or_default()
                .logical_count += 1;
        }
        self.cells[pass]
            .entry(em.owner_cell_id)
            .or_default()
            .logical_count += 1;

        if em.is_renderable() {
            self.renderable[pass].insert(em.handle);
            if em.attached_object_id == 0 {
                self.unattached[pass].insert(em.handle);
            } else if let Some(bucket) = self.owners[pass].get_mut(&em.attached_object_id) {
                bucket.renderable.insert(em.handle);
            }
            if let Some(bucket) = self.cells[pass].get_mut(&em.owner_cell_id) {
                bucket.renderable.insert(em.handle);
            }
        }
    }

    fn refresh(&mut self, em: &ParticleEmitter, was_renderable: bool) {
        let is_renderable = em.is_renderable();
        if was_renderable == is_renderable {
            return;
        }
        let pass = em.render_pass.index();
        let set = |s: &mut BTreeSet<EmitterHandle>| {
            if is_renderable {
                s.insert(em.handle);
            } else {
                s.remove(&em.handle);
            }
        };
        set(&mut self.renderable[pass]);
        if em.attached_object_id == 0 {
            set(&mut self.unattached[pass]);
        } else if let Some(bucket) = self.owners[pass].get_mut(&em.attached_object_id) {
            set(&mut bucket.renderable);
        }
        if let Some(bucket) = self.cells[pass].get_mut(&em.owner_cell_id) {
            set(&mut bucket.renderable);
        }
    }

    fn remove(&mut self, em: &ParticleEmitter) {
        let pass = em.render_pass.index();
        self.renderable[pass].remove(&em.handle);
        if em.attached_object_id == 0 {
            self.unattached[pass].remove(&em.handle);
        } else {
            release(&mut self.owners[pass], em.attached_object_id, em.handle);
        }
        release(&mut self.cells[pass], em.owner_cell_id, em.handle);
    }

    fn move_cell(&mut self, em: &ParticleEmitter, new_cell: u32) {
        let pass = em.render_pass.index();
        release(&mut self.cells[pass], em.owner_cell_id, em.handle);
        let bucket = self.cells[pass].entry(new_cell).or_default();
        bucket.logical_count += 1;
        if em.is_renderable() {
            bucket.renderable.insert(em.handle);
        }
    }
}

fn release(buckets: &mut Buckets, key: u32, handle: EmitterHandle) {
    if let Some(bucket) = buckets.get_mut(&key) {
        bucket.renderable.remove(&handle);
        bucket.logical_count -= 1;
        if bucket.logical_count == 0 {
            buckets.remove(&key);
        }
    }
}

/// Every live emitter of one presentation, and the clock they share.
pub struct ParticleSystem {
    registry: EmitterDescRegistry,
    rng: Box<dyn RandomSource>,
    emitters: BTreeMap<EmitterHandle, ParticleEmitter>,
    simulation: BTreeSet<EmitterHandle>,
    world_simulation: BTreeSet<EmitterHandle>,
    indexes: RenderIndexes,
    tick_snapshot: Vec<EmitterHandle>,
    simulation_version: u64,
    tick_snapshot_version: Option<u64>,
    simulation_snapshot_rebuilds: usize,
    next_handle: EmitterHandle,
    time: f32,
    active_particle_count: i32,
    last_tick_emitter_visit_count: usize,
    last_retail_view_emitter_visit_count: usize,
    last_render_scope_emitter_visit_count: Cell<usize>,
    died: Vec<EmitterHandle>,
}

impl fmt::Debug for ParticleSystem {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ParticleSystem")
            .field("emitters", &self.emitters.len())
            .field("time", &self.time)
            .field("active_particles", &self.active_particle_count)
            .finish_non_exhaustive()
    }
}

impl ParticleSystem {
    /// An empty system resolving emitter ids through `registry` and drawing
    /// random numbers from `rng`.
    #[must_use]
    pub fn new(registry: EmitterDescRegistry, rng: Box<dyn RandomSource>) -> Self {
        Self {
            registry,
            rng,
            emitters: BTreeMap::new(),
            simulation: BTreeSet::new(),
            world_simulation: BTreeSet::new(),
            indexes: RenderIndexes::default(),
            tick_snapshot: Vec::new(),
            simulation_version: 0,
            tick_snapshot_version: None,
            simulation_snapshot_rebuilds: 0,
            next_handle: 1,
            time: 0.0,
            active_particle_count: 0,
            last_tick_emitter_visit_count: 0,
            last_retail_view_emitter_visit_count: 0,
            last_render_scope_emitter_visit_count: Cell::new(0),
            died: Vec::new(),
        }
    }

    /// The description registry.
    pub fn registry_mut(&mut self) -> &mut EmitterDescRegistry {
        &mut self.registry
    }

    /// Live emitters.
    #[must_use]
    pub fn active_emitter_count(&self) -> usize {
        self.emitters.len()
    }

    /// Particles alive after the last tick.
    #[must_use]
    pub fn active_particle_count(&self) -> i32 {
        self.active_particle_count
    }

    /// The shared clock, seconds.
    #[must_use]
    pub fn time(&self) -> f32 {
        self.time
    }

    /// Emitters the last tick advanced.
    #[must_use]
    pub fn last_tick_emitter_visit_count(&self) -> usize {
        self.last_tick_emitter_visit_count
    }

    /// Emitters the last view test visited.
    #[must_use]
    pub fn last_retail_view_emitter_visit_count(&self) -> usize {
        self.last_retail_view_emitter_visit_count
    }

    /// Emitters the last scoped render copy visited.
    #[must_use]
    pub fn last_render_scope_emitter_visit_count(&self) -> usize {
        self.last_render_scope_emitter_visit_count.get()
    }

    /// Times the tick rebuilt its snapshot of the simulation set (only when
    /// the set's membership moved).
    #[must_use]
    pub fn simulation_snapshot_rebuilds(&self) -> usize {
        self.simulation_snapshot_rebuilds
    }

    /// The handles the last tick iterated, in order.
    #[must_use]
    pub fn simulation_tick_order(&self) -> &[EmitterHandle] {
        &self.tick_snapshot
    }

    /// Start an emitter at `anchor` with description `desc`; its initial
    /// particles spawn at once.
    pub fn spawn_emitter(
        &mut self,
        desc: Arc<EmitterDesc>,
        anchor: Vec3,
        options: SpawnOptions,
    ) -> EmitterHandle {
        let handle = self.next_handle;
        self.next_handle += 1;
        let slots = desc.max_particles.max(1) as usize;
        let mut emitter = ParticleEmitter {
            handle,
            desc: Arc::clone(&desc),
            anchor_pos: anchor,
            owner_position: anchor,
            owner_cell_id: 0,
            anchor_rot: options.rotation,
            attached_object_id: options.attached_object_id,
            visibility_policy: options.visibility_policy,
            presentation_visible: true,
            simulation_enabled: true,
            view_eligible: true,
            degraded_out: false,
            attached_part_index: options.attached_part_index,
            particles: vec![Particle::default(); slots],
            render_pass: options.render_pass,
            active_count: 0,
            emitted_accumulator: 0.0,
            started_at: self.time,
            last_emit_time: self.time,
            frozen_time: 0.0,
            last_emit_offset: anchor,
            total_emitted: 0,
            finished: false,
        };
        for _ in 0..desc.initial_particles {
            spawn_one(&mut emitter, self.time, self.rng.as_mut());
        }
        self.indexes.add(&emitter);
        self.emitters.insert(handle, emitter);
        self.add_simulation(handle);
        if options.visibility_policy == ParticleVisibilityPolicy::World {
            self.world_simulation.insert(handle);
        }
        handle
    }

    /// Start an emitter by dat id; fails (spawning nothing) when the id does
    /// not resolve.
    pub fn spawn_emitter_by_id(
        &mut self,
        emitter_id: u32,
        anchor: Vec3,
        options: SpawnOptions,
    ) -> Result<EmitterHandle, EmitterDescFailure> {
        let desc = self.registry.get(emitter_id)?;
        Ok(self.spawn_emitter(desc, anchor, options))
    }

    /// Stop an emitter. With `fade_out` it stops emitting and retires when
    /// its last particle dies; without, it and its particles go at once.
    pub fn stop_emitter(&mut self, handle: EmitterHandle, fade_out: bool) {
        let Some(em) = self.emitters.get_mut(&handle) else {
            return;
        };
        em.finished = true;
        if !fade_out {
            for p in &mut em.particles {
                p.alive = false;
            }
            em.active_count = 0;
            self.remove_emitter(handle);
        }
    }

    /// Move an emitter's anchor (and orientation). Attached particles follow
    /// it on the next tick; released ones keep their birth origin.
    pub fn update_emitter_anchor(
        &mut self,
        handle: EmitterHandle,
        anchor: Vec3,
        rot: Option<Quat>,
    ) {
        let Some(em) = self.emitters.get_mut(&handle) else {
            return;
        };
        em.anchor_pos = anchor;
        if !em.simulation_enabled {
            em.last_emit_offset = anchor;
        }
        if let Some(rot) = rot {
            em.anchor_rot = rot;
        }
    }

    /// Set the owner's root position used by the degrade-distance test.
    pub fn update_emitter_owner_position(&mut self, handle: EmitterHandle, owner_position: Vec3) {
        if let Some(em) = self.emitters.get_mut(&handle) {
            em.owner_position = owner_position;
        }
    }

    /// Set the owner's cell.
    pub fn update_emitter_owner_cell(&mut self, handle: EmitterHandle, owner_cell_id: u32) {
        let Some(em) = self.emitters.get_mut(&handle) else {
            return;
        };
        if em.owner_cell_id == owner_cell_id {
            return;
        }
        self.indexes.move_cell(em, owner_cell_id);
        em.owner_cell_id = owner_cell_id;
    }

    /// Change who decides whether the emitter may be drawn.
    pub fn set_emitter_visibility_policy(
        &mut self,
        handle: EmitterHandle,
        policy: ParticleVisibilityPolicy,
    ) {
        let Some(em) = self.emitters.get_mut(&handle) else {
            return;
        };
        if em.visibility_policy == policy {
            return;
        }
        let was_renderable = em.is_renderable();
        if em.visibility_policy == ParticleVisibilityPolicy::World {
            self.world_simulation.remove(&handle);
        }
        em.visibility_policy = policy;
        if policy == ParticleVisibilityPolicy::World {
            if em.simulation_enabled {
                self.world_simulation.insert(handle);
            }
        } else {
            // Examination and dedicated-pass emitters bypass the world view.
            em.view_eligible = true;
        }
        self.indexes.refresh(em, was_renderable);
    }

    /// Apply the world view to every world emitter that simulates: its
    /// owner's cell must be in view (an interior cell always is; an outdoor
    /// cell when it is in `visible_landscape_cell_ids`) and the owner within
    /// the emitter's degrade distance (times `range_multiplier`) of the
    /// viewer. Without a completed view nothing is eligible.
    pub fn apply_retail_view(
        &mut self,
        viewer_position: Vec3,
        visible_landscape_cell_ids: &HashSet<u32>,
        has_completed_view: bool,
        range_multiplier: f32,
    ) {
        let range_multiplier = if range_multiplier.is_finite() && range_multiplier > 0.0 {
            range_multiplier
        } else {
            1.0
        };
        self.last_retail_view_emitter_visit_count = 0;
        for handle in &self.world_simulation {
            let Some(em) = self.emitters.get_mut(handle) else {
                continue;
            };
            self.last_retail_view_emitter_visit_count += 1;
            let was_renderable = em.is_renderable();
            if !has_completed_view {
                em.view_eligible = false;
                self.indexes.refresh(em, was_renderable);
                continue;
            }
            let max_distance = em.desc.max_degrade_distance * range_multiplier;
            let distance = retail_distance(em.owner_position, viewer_position);
            let cell_low = em.owner_cell_id & 0xFFFF;
            let cell_in_view = cell_low >= 0x0100
                || (cell_low > 0 && visible_landscape_cell_ids.contains(&em.owner_cell_id));
            // NaN on either side passes, as the game's comparison does; a
            // negative range never does.
            em.view_eligible = em.owner_cell_id != 0
                && cell_in_view
                && (distance.is_nan() || max_distance.is_nan() || distance <= max_distance);
            self.indexes.refresh(em, was_renderable);
        }
    }

    /// Show or hide an emitter without touching its lifetime.
    pub fn set_emitter_presentation_visible(&mut self, handle: EmitterHandle, visible: bool) {
        let Some(em) = self.emitters.get_mut(&handle) else {
            return;
        };
        if em.presentation_visible == visible {
            return;
        }
        let was_renderable = em.is_renderable();
        em.presentation_visible = visible;
        self.indexes.refresh(em, was_renderable);
    }

    /// Pause or resume an emitter's simulation. A paused world emitter
    /// loses its view eligibility until the next view test.
    pub fn set_emitter_simulation_enabled(&mut self, handle: EmitterHandle, enabled: bool) {
        let Some(em) = self.emitters.get_mut(&handle) else {
            return;
        };
        if em.simulation_enabled == enabled {
            return;
        }
        if !enabled {
            let was_renderable = em.is_renderable();
            em.simulation_enabled = false;
            let world = em.visibility_policy == ParticleVisibilityPolicy::World;
            if world {
                em.view_eligible = false;
                self.indexes.refresh(em, was_renderable);
            }
            self.remove_simulation(handle);
            self.world_simulation.remove(&handle);
            return;
        }

        if em.desc.birthrate <= 0.0 && em.desc.emit_rate > 0.0 {
            // No catch-up burst for the paused interval.
            em.last_emit_time = self.time;
            em.emitted_accumulator = 0.0;
        }
        em.simulation_enabled = true;
        if em.visibility_policy == ParticleVisibilityPolicy::World {
            self.world_simulation.insert(handle);
        } else {
            em.view_eligible = true;
        }
        self.add_simulation(handle);
    }

    /// True while `handle` is a live emitter.
    #[must_use]
    pub fn is_emitter_alive(&self, handle: EmitterHandle) -> bool {
        self.emitters.contains_key(&handle)
    }

    /// One emitter.
    #[must_use]
    pub fn emitter(&self, handle: EmitterHandle) -> Option<&ParticleEmitter> {
        self.emitters.get(&handle)
    }

    /// The emitters that died since the last call (finished, or stopped
    /// without fade), in the order they died. Each handle appears once.
    pub fn take_died_emitters(&mut self) -> Vec<EmitterHandle> {
        std::mem::take(&mut self.died)
    }

    fn add_simulation(&mut self, handle: EmitterHandle) {
        if self.simulation.insert(handle) {
            self.simulation_version += 1;
        }
    }

    fn remove_simulation(&mut self, handle: EmitterHandle) {
        if self.simulation.remove(&handle) {
            self.simulation_version += 1;
        }
    }

    /// Advance every simulating emitter by `dt` seconds. A non-positive or
    /// non-finite `dt` does nothing.
    pub fn tick(&mut self, dt: f32) {
        if !(dt > 0.0 && dt.is_finite()) {
            return;
        }
        self.time += dt;
        self.active_particle_count = 0;
        self.last_tick_emitter_visit_count = 0;

        if self.tick_snapshot_version != Some(self.simulation_version) {
            self.tick_snapshot.clear();
            self.tick_snapshot.extend(self.simulation.iter().copied());
            self.tick_snapshot_version = Some(self.simulation_version);
            self.simulation_snapshot_rebuilds += 1;
        }

        let snapshot = std::mem::take(&mut self.tick_snapshot);
        let time = self.time;
        for &handle in &snapshot {
            let Some(em) = self.emitters.get_mut(&handle) else {
                continue;
            };
            if !em.simulation_enabled {
                continue;
            }
            self.last_tick_emitter_visit_count += 1;
            let was_finished = em.finished;

            if em.view_eligible {
                em.degraded_out = false;
                advance_emitter(em, time, self.rng.as_mut());
            } else {
                em.degraded_out = true;
                advance_degraded_emitter(em, time, dt);
            }
            let live = em.active_count;
            self.active_particle_count += live;

            if em.desc.total_duration > 0.0 && (time - em.started_at) > em.desc.total_duration {
                em.finished = true;
            }
            if em.desc.total_particles > 0 && em.total_emitted >= em.desc.total_particles {
                em.finished = true;
            }
            // The tick that first stops an emitter keeps it; only a later
            // tick of an already stopped, empty emitter retires it.
            if em.finished && live == 0 && was_finished {
                self.remove_emitter(handle);
            }
        }
        self.tick_snapshot = snapshot;
    }

    fn remove_emitter(&mut self, handle: EmitterHandle) {
        let Some(em) = self.emitters.remove(&handle) else {
            return;
        };
        self.remove_simulation(handle);
        self.world_simulation.remove(&handle);
        self.indexes.remove(&em);
        self.died.push(handle);
    }

    /// Every live emitter, in spawn order.
    pub fn emitters(&self) -> impl Iterator<Item = &ParticleEmitter> {
        self.emitters.values()
    }

    /// Every live particle as (emitter, slot), in spawn order.
    pub fn live_particles(&self) -> impl Iterator<Item = (&ParticleEmitter, usize)> {
        self.emitters.values().flat_map(|em| {
            em.particles
                .iter()
                .enumerate()
                .filter(|(_, p)| p.alive)
                .map(move |(i, _)| (em, i))
        })
    }

    /// The emitters of `pass` that may be drawn (shown and view eligible),
    /// in spawn order.
    pub fn renderable_emitters(
        &self,
        pass: ParticleRenderPass,
    ) -> impl Iterator<Item = &ParticleEmitter> {
        self.indexes.renderable[pass.index()]
            .iter()
            .filter_map(|h| self.emitters.get(h))
    }

    /// The handles of the drawable emitters of `pass` attached to `owners`
    /// (minus `excluded_owners`), plus the unattached ones in
    /// `unattached_scope` when `include_unattached`, in spawn order. Visits
    /// only those. Look the emitters up with [`Self::emitter`].
    pub fn copy_renderable_emitters_for_owners(
        &self,
        pass: ParticleRenderPass,
        owners: &HashSet<u32>,
        include_unattached: bool,
        destination: &mut Vec<EmitterHandle>,
        excluded_owners: Option<&HashSet<u32>>,
        unattached_scope: UnattachedEmitterCellScope,
    ) {
        destination.clear();
        let mut visits = 0;
        let pass_index = pass.index();
        if include_unattached {
            for handle in &self.indexes.unattached[pass_index] {
                visits += 1;
                if let Some(em) = self.emitters.get(handle)
                    && matches_unattached_scope(em, unattached_scope)
                {
                    destination.push(*handle);
                }
            }
        }
        for owner in owners {
            if excluded_owners.is_some_and(|ex| ex.contains(owner)) {
                continue;
            }
            let Some(bucket) = self.indexes.owners[pass_index].get(owner) else {
                continue;
            };
            visits += bucket.renderable.len();
            destination.extend(bucket.renderable.iter().copied());
        }
        destination.sort_unstable();
        self.last_render_scope_emitter_visit_count.set(visits);
    }

    /// True when cell `cell_id` holds a drawable emitter of `pass`.
    #[must_use]
    pub fn has_renderable_emitters_in_cell(&self, pass: ParticleRenderPass, cell_id: u32) -> bool {
        self.indexes.cells[pass.index()]
            .get(&cell_id)
            .is_some_and(|b| !b.renderable.is_empty())
    }

    /// The handles of the drawable emitters of `pass` whose owner cell is
    /// `cell_id`, in spawn order.
    pub fn copy_renderable_emitters_in_cell(
        &self,
        pass: ParticleRenderPass,
        cell_id: u32,
        destination: &mut Vec<EmitterHandle>,
    ) {
        destination.clear();
        let Some(bucket) = self.indexes.cells[pass.index()].get(&cell_id) else {
            self.last_render_scope_emitter_visit_count.set(0);
            return;
        };
        self.last_render_scope_emitter_visit_count
            .set(bucket.renderable.len());
        destination.extend(bucket.renderable.iter().copied());
    }
}

fn matches_unattached_scope(em: &ParticleEmitter, scope: UnattachedEmitterCellScope) -> bool {
    let low = em.owner_cell_id & 0xFFFF;
    match scope {
        UnattachedEmitterCellScope::Any => true,
        UnattachedEmitterCellScope::OutdoorCells => low != 0 && low < 0x0100,
        UnattachedEmitterCellScope::InteriorCells => low >= 0x0100,
    }
}

fn advance_emitter(em: &mut ParticleEmitter, time: f32, rng: &mut dyn RandomSource) {
    let desc = Arc::clone(&em.desc);
    let attach_local = desc.flags.contains(EmitterFlags::ATTACH_LOCAL);
    let anchor = em.anchor_pos;
    for p in &mut em.particles {
        if !p.alive {
            continue;
        }
        p.age = time - p.spawned_at - (em.frozen_time - p.frozen_time_at_spawn);
        if p.lifetime <= 0.0 || p.age >= p.lifetime {
            p.alive = false;
            em.active_count -= 1;
            continue;
        }
        p.position = compute_position(&desc, attach_local, anchor, p);
        let t_life = (p.age / p.lifetime).clamp(0.0, 1.0);
        p.size = lerp(p.start_size, p.end_size, t_life);
        p.rotation = lerp(desc.start_rotation, desc.end_rotation, t_life);
        let alpha = lerp(p.start_alpha, p.end_alpha, t_life);
        p.color_argb = color32(alpha, desc.start_color_argb, desc.end_color_argb, t_life);
    }

    if em.finished || time < em.started_at + desc.start_delay {
        return;
    }
    while should_emit_particle(em, time) {
        if !spawn_one(em, time, rng) {
            break;
        }
    }
    if desc.birthrate <= 0.0 && desc.emit_rate > 0.0 {
        let dt = time - em.last_emit_time;
        em.emitted_accumulator += dt * desc.emit_rate;
        em.last_emit_time = time;
        while em.emitted_accumulator >= 1.0 {
            em.emitted_accumulator -= 1.0;
            if !spawn_one(em, time, rng) {
                break;
            }
        }
    }
}

/// The degraded branch: an infinite emitter freezes (its particles stop
/// ageing, nothing is emitted); a finite one ages and retires particles and
/// records at most one due emission without making a drawable particle.
fn advance_degraded_emitter(em: &mut ParticleEmitter, time: f32, dt: f32) {
    let infinite = em.desc.total_particles == 0 && em.desc.total_duration == 0.0;
    if infinite {
        em.frozen_time += dt;
        if em.desc.birthrate <= 0.0 && em.desc.emit_rate > 0.0 {
            em.last_emit_time = time;
            em.emitted_accumulator = 0.0;
        }
        return;
    }

    for p in &mut em.particles {
        if !p.alive {
            continue;
        }
        p.age = time - p.spawned_at;
        if p.lifetime <= 0.0 || p.age >= p.lifetime {
            p.alive = false;
            em.active_count -= 1;
        }
    }

    if !em.finished && should_emit_particle(em, time) {
        em.active_count += 1;
        em.total_emitted += 1;
        em.last_emit_time = time;
        em.last_emit_offset = em.anchor_pos;
    }
}

fn should_emit_particle(em: &ParticleEmitter, time: f32) -> bool {
    let desc = &em.desc;
    if desc.total_particles > 0 && em.total_emitted >= desc.total_particles {
        return false;
    }
    if em.active_count >= desc.max_particles {
        return false;
    }
    if desc.birthrate <= 0.0 {
        return false;
    }
    match desc.emitter_kind {
        EmitterKind::BirthratePerSec => (time - em.last_emit_time) > desc.birthrate,
        EmitterKind::BirthratePerMeter => {
            em.anchor_pos.distance_squared(em.last_emit_offset) > desc.birthrate * desc.birthrate
        }
        EmitterKind::Unknown => false,
    }
}

fn spawn_one(em: &mut ParticleEmitter, time: f32, rng: &mut dyn RandomSource) -> bool {
    let Some(slot) = em.particles.iter().position(|p| !p.alive) else {
        return false;
    };
    let desc = Arc::clone(&em.desc);
    let mut particle = Particle {
        alive: true,
        spawned_at: time,
        frozen_time_at_spawn: em.frozen_time,
        lifetime: random_lifespan(&desc, rng),
        emission_origin: em.anchor_pos,
        spawn_rotation: em.anchor_rot,
        ..Particle::default()
    };

    let local_offset = random_offset(&desc, rng);
    let mut local_a = random_vector(desc.a, desc.min_a, desc.max_a, rng);
    let mut local_b = random_vector(desc.b, desc.min_b, desc.max_b, rng);
    let local_c = random_vector(desc.c, desc.min_c, desc.max_c, rng);

    if local_a == Vec3::ZERO && desc.initial_velocity != Vec3::ZERO {
        local_a = desc.initial_velocity;
        if desc.velocity_jitter > 0.0 {
            let x = random_centered(desc.velocity_jitter, rng);
            let y = random_centered(desc.velocity_jitter, rng);
            let z = random_centered(desc.velocity_jitter, rng);
            local_a += Vec3::new(x, y, z);
        }
    }
    if local_b == Vec3::ZERO && desc.gravity != Vec3::ZERO {
        local_b = desc.gravity;
    }

    init_particle_vectors(
        &desc,
        em.anchor_rot,
        &mut particle,
        local_offset,
        local_a,
        local_b,
        local_c,
        rng,
    );

    particle.velocity = particle.a;
    particle.start_size = random_scale(desc.start_size, desc.scale_rand, rng);
    particle.end_size = random_scale(desc.end_size, desc.scale_rand, rng);
    particle.start_alpha = random_trans(desc.start_alpha, desc.trans_rand, rng);
    particle.end_alpha = random_trans(desc.end_alpha, desc.trans_rand, rng);
    particle.size = particle.start_size;
    particle.color_argb = color32(
        particle.start_alpha,
        desc.start_color_argb,
        desc.end_color_argb,
        0.0,
    );
    let attach_local = desc.flags.contains(EmitterFlags::ATTACH_LOCAL);
    particle.position = compute_position(&desc, attach_local, em.anchor_pos, &particle);

    em.particles[slot] = particle;
    em.total_emitted += 1;
    em.active_count += 1;
    em.last_emit_time = time;
    em.last_emit_offset = em.anchor_pos;
    true
}

/// Where a particle is at its age: the origin (the live anchor when
/// attached, else the birth anchor) plus offset plus its type's motion.
fn compute_position(desc: &EmitterDesc, attach_local: bool, anchor: Vec3, p: &Particle) -> Vec3 {
    let t = p.age;
    let origin = if attach_local {
        anchor
    } else {
        p.emission_origin
    };
    let (offset, a, b, c) = (p.offset, p.a, p.b, p.c);
    match desc.particle_type {
        ParticleType::Still => origin + offset,
        ParticleType::LocalVelocity | ParticleType::GlobalVelocity => origin + offset + t * a,
        ParticleType::ParabolicLVGA
        | ParticleType::ParabolicLVLA
        | ParticleType::ParabolicGVGA
        | ParticleType::ParabolicLVGAGR
        | ParticleType::ParabolicLVLALR
        | ParticleType::ParabolicGVGAGR => origin + offset + t * a + 0.5 * t * t * b,
        ParticleType::Swarm => {
            origin
                + offset
                + t * a
                + Vec3::new(
                    (t * b.x).cos() * c.x,
                    (t * b.y).sin() * c.y,
                    (t * b.z).cos() * c.z,
                )
        }
        ParticleType::Explode => {
            origin
                + offset
                + Vec3::new(
                    (t * b.x + c.x * a.x) * t,
                    (t * b.y + c.y * a.x) * t,
                    (t * b.z + c.z * a.x + a.z) * t,
                )
        }
        ParticleType::Implode => origin + offset + (a.x * t).cos() * c + t * t * b,
        ParticleType::Unknown => origin + offset + t * a,
    }
}

/// Resolve the particle's vectors into world space once, at birth: the
/// "local" vectors of each type turn with the emitter's orientation.
#[allow(clippy::too_many_arguments)]
fn init_particle_vectors(
    desc: &EmitterDesc,
    anchor_rot: Quat,
    p: &mut Particle,
    local_offset: Vec3,
    local_a: Vec3,
    local_b: Vec3,
    local_c: Vec3,
    rng: &mut dyn RandomSource,
) {
    let to_world = |v: Vec3| {
        if anchor_rot == Quat::IDENTITY {
            v
        } else {
            anchor_rot * v
        }
    };
    p.offset = to_world(local_offset);
    p.a = local_a;
    p.b = local_b;
    p.c = local_c;

    match desc.particle_type {
        ParticleType::LocalVelocity | ParticleType::ParabolicLVGA | ParticleType::Swarm => {
            p.a = to_world(local_a);
        }
        ParticleType::ParabolicLVLA => {
            p.a = to_world(local_a);
            p.b = to_world(local_b);
        }
        ParticleType::ParabolicLVGAGR => {
            p.a = to_world(local_a);
            p.c = local_c;
        }
        ParticleType::Explode => {
            p.a = local_a;
            p.b = local_b;
            p.c = random_explode_direction(local_c, rng);
        }
        ParticleType::Implode => {
            p.a = local_a;
            p.b = local_b;
            p.offset *= local_c;
            p.c = p.offset;
        }
        ParticleType::ParabolicLVLALR => {
            p.a = to_world(local_a);
            p.b = to_world(local_b);
            p.c = to_world(local_c);
        }
        ParticleType::ParabolicGVGAGR => {
            p.c = local_c;
        }
        ParticleType::Unknown
        | ParticleType::Still
        | ParticleType::ParabolicGVGA
        | ParticleType::GlobalVelocity => {}
    }
}

fn random_explode_direction(local_c: Vec3, rng: &mut dyn RandomSource) -> Vec3 {
    let pi = std::f32::consts::PI;
    let yaw = random_range(-pi, pi, rng);
    let pitch = random_range(-pi, pi, rng);
    let cos_pitch = pitch.cos();
    let c = Vec3::new(
        yaw.cos() * local_c.x * cos_pitch,
        yaw.sin() * local_c.y * cos_pitch,
        pitch.sin() * local_c.z,
    );
    let length = c.length();
    if length < 1e-8 {
        Vec3::ZERO
    } else {
        c / length
    }
}

/// The straight-line distance, computed without overflow for coordinates
/// near the float limit.
pub(crate) fn retail_distance(a: Vec3, b: Vec3) -> f32 {
    let d = a - b;
    let scale = d.x.abs().max(d.y.abs().max(d.z.abs()));
    if scale.is_nan() || scale.is_infinite() || scale == 0.0 {
        return scale;
    }
    let d = d / scale;
    scale * (d.x * d.x + d.y * d.y + d.z * d.z).sqrt()
}

fn random_lifespan(desc: &EmitterDesc, rng: &mut dyn RandomSource) -> f32 {
    let lifespan = if desc.lifespan > 0.0 {
        desc.lifespan
    } else {
        (desc.lifetime_min + desc.lifetime_max) * 0.5
    };
    let rand = if desc.lifespan_rand > 0.0 {
        desc.lifespan_rand
    } else {
        (desc.lifetime_max - desc.lifetime_min).abs() * 0.5
    };
    let mut value = lifespan + random_centered(rand, rng);
    if value <= 0.0 && desc.lifetime_max > 0.0 {
        value = lerp(desc.lifetime_min, desc.lifetime_max, rng.next_unit() as f32);
    }
    value.max(0.0)
}

fn random_offset(desc: &EmitterDesc, rng: &mut dyn RandomSource) -> Vec3 {
    let min = desc.min_offset.min(desc.max_offset);
    let max = desc.min_offset.max(desc.max_offset);
    if max <= 0.0 {
        return Vec3::ZERO;
    }
    let axis = normalize_or_zero(desc.offset_dir);
    let x = random_centered(1.0, rng);
    let y = random_centered(1.0, rng);
    let z = random_centered(1.0, rng);
    let mut v = Vec3::new(x, y, z);
    if axis != Vec3::ZERO {
        v -= axis * v.dot(axis);
    }
    v = if v.length_squared() < 1e-8 {
        if axis != Vec3::ZERO {
            perpendicular(axis)
        } else {
            Vec3::X
        }
    } else {
        v / v.length()
    };
    v * lerp(min, max, rng.next_unit() as f32)
}

fn random_vector(direction: Vec3, min: f32, max: f32, rng: &mut dyn RandomSource) -> Vec3 {
    if direction == Vec3::ZERO {
        return Vec3::ZERO;
    }
    let (min, max) = if max < min { (max, min) } else { (min, max) };
    direction * lerp(min, max, rng.next_unit() as f32)
}

fn random_scale(base: f32, rand: f32, rng: &mut dyn RandomSource) -> f32 {
    (base + random_centered(rand, rng)).clamp(0.1, 10.0)
}

fn random_trans(base: f32, rand: f32, rng: &mut dyn RandomSource) -> f32 {
    (base + random_centered(rand, rng)).clamp(0.0, 1.0)
}

fn random_centered(half_width: f32, rng: &mut dyn RandomSource) -> f32 {
    ((rng.next_unit() as f32) - 0.5) * 2.0 * half_width
}

fn random_range(min: f32, max: f32, rng: &mut dyn RandomSource) -> f32 {
    lerp(min, max, rng.next_unit() as f32)
}

fn lerp(a: f32, b: f32, t: f32) -> f32 {
    a + (b - a) * t
}

fn normalize_or_zero(v: Vec3) -> Vec3 {
    if v.length_squared() > 1e-8 {
        v / v.length()
    } else {
        Vec3::ZERO
    }
}

fn perpendicular(v: Vec3) -> Vec3 {
    let basis = if v.x.abs() < 0.9 { Vec3::X } else { Vec3::Y };
    let c = v.cross(basis);
    c / c.length()
}

/// The particle's colour: RGB from start to end colour at `t`, alpha from
/// `alpha` (opacity 0..1).
fn color32(alpha: f32, start_argb: u32, end_argb: u32, t: f32) -> u32 {
    let channel = |shift: u32| {
        let s = ((start_argb >> shift) & 0xFF) as i32;
        let e = ((end_argb >> shift) & 0xFF) as i32;
        (s as f32 + (e - s) as f32 * t).clamp(0.0, 255.0) as u8
    };
    let r = channel(16);
    let g = channel(8);
    let b = channel(0);
    let a = (alpha * 255.0).clamp(0.0, 255.0) as u8;
    (u32::from(a) << 24) | (u32::from(r) << 16) | (u32::from(g) << 8) | u32::from(b)
}

#[cfg(test)]
mod tests;
