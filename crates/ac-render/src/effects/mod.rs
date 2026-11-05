//! The window's effects: particles, physics scripts and what starts them,
//! presented from the session's objects and the dat statics around the
//! player.
//!
//! One [`WindowEffects`] per attached window holds an `ac-vfx`
//! [`VfxWorld`]: built when the window first reads the dats and dropped
//! with the window. Server objects become effect owners when they show up
//! (their Setup's default script starts, the server's physics description
//! names their script table), statics of the near landblocks and their
//! cells with a default script or a default animation become dat owners
//! when their landblock arrives, and the sky objects' scripts follow the
//! time of day. Each frame, after the animations advanced and before the
//! frame is walked, the window publishes every owner's root and part poses,
//! hands over the hooks the animations fired, runs the frame
//! ([`WindowEffects::frame`]) and draws the particles the walk reaches.
//! Server "play script" and sound messages are queued until that frame.
//! Nothing here survives a detach; the next attach restarts every default
//! script (server plays that arrived meanwhile are not replayed).

pub mod ledger;
pub mod routing;

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use ac_core::physics::PhysicsState;
use ac_core::sky::DayGroupData;
use ac_dat::DatError;
use ac_dat::objects::{
    AnimationHook, GfxObj, GfxObjDegradeInfo, HookData, ParticleEmitter as DatParticleEmitter,
    PhysicsScriptTable, Setup, Surface,
};
use ac_runtime::world::ObjectView;
use ac_vfx::{
    ActivationInfo, EffectEvent, EffectOwnerQueries, EffectPoseSource, EntityEffectProfile,
    LiveRootState, NetworkEffectDescription, ParticleGfxCache, ParticleSystem, VfxDats, VfxEvent,
    VfxWorld,
};
use glam::{Mat4, Quat, Vec3};

use crate::animation::pose::rest_poses;
use crate::animation::source::AnimationSource;
use crate::animation::{AnimationPresenter, FiredHook, HookOwner, StaticKey};
use crate::assets::StaticInstance;
use crate::content::Content;
use crate::scene::Scene;
use ledger::ParticleResourceLedger;

/// The process's dats as the effects read them.
struct ContentVfxDats(Arc<Content>);

impl VfxDats for ContentVfxDats {
    fn particle_emitter(&self, id: u32) -> Result<Option<Arc<DatParticleEmitter>>, DatError> {
        VfxDats::particle_emitter(&self.0.dats, id)
    }
    fn gfx_obj(&self, id: u32) -> Result<Option<Arc<GfxObj>>, DatError> {
        VfxDats::gfx_obj(&self.0.dats, id)
    }
    fn gfx_obj_degrade_info(&self, id: u32) -> Result<Option<Arc<GfxObjDegradeInfo>>, DatError> {
        VfxDats::gfx_obj_degrade_info(&self.0.dats, id)
    }
    fn surface(&self, id: u32) -> Result<Option<Arc<Surface>>, DatError> {
        VfxDats::surface(&self.0.dats, id)
    }
    fn physics_script_table(&self, id: u32) -> Result<Option<Arc<PhysicsScriptTable>>, DatError> {
        VfxDats::physics_script_table(&self.0.dats, id)
    }
    fn portal_bytes(&self, id: u32) -> Result<Option<Vec<u8>>, DatError> {
        VfxDats::portal_bytes(&self.0.dats, id)
    }
}

/// A server message the effects present.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum ServerEffect {
    /// Play script `script_id` on object `guid`.
    PlayScript {
        /// The object.
        guid: u32,
        /// The script.
        script_id: u32,
    },
    /// Play the script of `script_type` at `intensity` on object `guid`.
    PlayScriptType {
        /// The object.
        guid: u32,
        /// Script type.
        script_type: u32,
        /// Intensity.
        intensity: f32,
    },
    /// Object `guid` plays sound-table slot `sound_type` at `volume`.
    Sound {
        /// The object.
        guid: u32,
        /// Sound-table slot.
        sound_type: u32,
        /// Wire volume.
        volume: f32,
    },
}

/// A sound the effects ask the window's audio to play.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum EffectSound {
    /// A script's sound hook, from object `guid` (0 for a dat static) at
    /// `position` (drawing frame).
    Hook {
        /// The owner's server id.
        guid: u32,
        /// Where.
        position: Vec3,
        /// The hook.
        hook: HookData,
    },
    /// The server's sound event for object `guid`.
    Server {
        /// The owner's server id.
        guid: u32,
        /// Where.
        position: Vec3,
        /// Sound-table slot.
        sound_type: u32,
        /// Wire volume.
        volume: f32,
    },
}

/// A part fade a script asked for, for the animation's fades.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ScriptPartFade {
    /// Whose part.
    pub owner: HookOwner,
    /// The part.
    pub part: u32,
    /// Translucency at the start.
    pub start: f32,
    /// Translucency at the end.
    pub end: f32,
    /// Seconds.
    pub time: f32,
}

/// One drawn object's pose this frame: its root (no scale) and, when it
/// animates or is held, its parts' frames (object scale in their offsets).
#[derive(Debug, Clone, PartialEq)]
pub struct OwnerPose {
    /// Local id.
    pub local: u32,
    /// Root, drawing frame.
    pub root: Mat4,
    /// Part frames relative to the root; `None` keeps the resting ones.
    pub parts: Option<Vec<Mat4>>,
    /// Its cell.
    pub cell: u32,
}

/// What one frame of effects needs from the window.
pub struct EffectsFrame<'a> {
    /// Seconds since the last frame.
    pub dt: f32,
    /// The session's objects.
    pub scene: Option<&'a Scene>,
    /// A session position (cell, cell-local metres) in the drawing frame.
    pub to_frame: &'a dyn Fn(u32, Vec3) -> Vec3,
    /// Drawn objects' poses (after their animation advanced).
    pub poses: &'a [OwnerPose],
    /// The animations (statics' poses).
    pub animations: &'a AnimationPresenter,
    /// A landblock's (`0xXXYY0000`) offset in the drawing frame.
    pub landblock_offset: &'a dyn Fn(u32) -> Vec3,
    /// Whether a cell's landblock is in the window's resident world (its
    /// load arrived). An object outside it neither draws its emitters nor
    /// runs its scripts.
    pub resident: &'a dyn Fn(u32) -> bool,
    /// The hooks the animations fired this frame.
    pub hooks: Vec<FiredHook>,
    /// The time of day (0..1) and the active day group, once the sky is
    /// known.
    pub sky: Option<(f32, Option<&'a DayGroupData>)>,
    /// The sky's effects run (the player is outdoors).
    pub sky_active: bool,
    /// The camera, drawing frame.
    pub camera: Vec3,
    /// Setups.
    pub source: &'a dyn AnimationSource,
    /// The particle range setting: how many times its authored degrade
    /// distance an emitter is drawn out to.
    pub particle_range: f32,
}

impl std::fmt::Debug for EffectsFrame<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("EffectsFrame")
            .field("dt", &self.dt)
            .field("poses", &self.poses.len())
            .field("hooks", &self.hooks.len())
            .field("sky_active", &self.sky_active)
            .finish_non_exhaustive()
    }
}

/// The script type an object plays when it comes back from hidden.
const UNHIDE_SCRIPT_TYPE: u32 = 0x75;
/// The script type an object plays when it goes hidden.
const HIDDEN_SCRIPT_TYPE: u32 = 0x76;

/// A server object as an effect owner.
#[derive(Debug, Clone, PartialEq)]
struct LiveOwner {
    guid: u32,
    incarnation: u16,
    setup_id: u32,
    description: NetworkEffectDescription,
    hidden: bool,
    /// It stands in the window's resident world; outside it its emitters
    /// hide (a hidden object's emitters still draw).
    resident: bool,
    /// Its first placement has been presented.
    bound: bool,
}

/// A dat static as an effect owner.
#[derive(Debug, Clone)]
struct StaticOwner {
    id: u32,
    /// Landblock-local placement (scale, rotation, translation).
    transform: Mat4,
    cell: u32,
    animated: bool,
    /// Its animated pose is published every frame: it has a default
    /// script, or its animation fired an effect hook. Until then nothing
    /// hangs from its parts (most animating statics never fire one).
    follows: bool,
}

/// What the effects ask about live objects (`EffectOwnerQueries`), rebuilt
/// from the scene each frame.
#[derive(Debug, Default)]
pub(crate) struct OwnerFacts {
    roots: HashMap<u32, LiveRootState>,
    parents: HashMap<u32, u32>,
    children: HashMap<(u32, u32), u32>,
}

impl EffectOwnerQueries for OwnerFacts {
    fn live_root(&self, local_id: u32) -> Option<LiveRootState> {
        self.roots.get(&local_id).copied()
    }

    fn parent_of_attached_child(&self, local_id: u32) -> Option<u32> {
        self.parents.get(&local_id).copied()
    }

    fn child_at_part(&self, parent: u32, part_index: u32) -> Option<u32> {
        self.children.get(&(parent, part_index)).copied()
    }
}

/// The `cell` of a building shell's [`StaticKey`] (shells are not in the
/// statics list the animation keys index).
pub const SHELL_CELL: u32 = u32::MAX;

/// Dat static owner ids start here, above every presentation local id the
/// session hands out and below the sky owners (`0xF0000000`).
const FIRST_STATIC_OWNER: u32 = 0x8000_0000;
const LAST_STATIC_OWNER: u32 = 0xEFFF_FFFF;

/// How many distinct diagnostics are printed before the rest are dropped.
const DIAGNOSTIC_LIMIT: usize = 200;

/// The window's effects. See the module documentation.
pub struct WindowEffects {
    vfx: VfxWorld,
    gfx: ParticleGfxCache,
    clock: f64,
    facts: OwnerFacts,
    live: HashMap<u32, LiveOwner>,
    guid_to_local: HashMap<u32, u32>,
    statics: HashMap<StaticKey, StaticOwner>,
    static_keys: HashMap<u32, StaticKey>,
    next_static: u32,
    server: Vec<ServerEffect>,
    sounds: Vec<EffectSound>,
    fades: Vec<ScriptPartFade>,
    lights: Vec<(u32, bool)>,
    ledger: ParticleResourceLedger,
    printed: HashSet<String>,
}

impl std::fmt::Debug for WindowEffects {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WindowEffects")
            .field("vfx", &self.vfx)
            .field("live", &self.live.len())
            .field("statics", &self.statics.len())
            .finish_non_exhaustive()
    }
}

impl WindowEffects {
    /// Effects reading the process's dats, random numbers seeded by
    /// `seed`.
    #[must_use]
    pub fn new(content: Arc<Content>, seed: i32) -> Self {
        let dats: Arc<dyn VfxDats> = Arc::new(ContentVfxDats(content));
        Self::with_dats(dats, seed)
    }

    /// Effects reading `dats`.
    #[must_use]
    pub fn with_dats(dats: Arc<dyn VfxDats>, seed: i32) -> Self {
        Self::from_world(
            VfxWorld::new(Arc::clone(&dats), seed),
            ParticleGfxCache::new(Some(dats)),
        )
    }

    /// Effects around a ready-made world (tests, tools).
    #[must_use]
    pub fn from_world(vfx: VfxWorld, gfx: ParticleGfxCache) -> Self {
        Self {
            vfx,
            gfx,
            clock: 0.0,
            facts: OwnerFacts::default(),
            live: HashMap::new(),
            guid_to_local: HashMap::new(),
            statics: HashMap::new(),
            static_keys: HashMap::new(),
            next_static: FIRST_STATIC_OWNER,
            server: Vec::new(),
            sounds: Vec::new(),
            fades: Vec::new(),
            lights: Vec::new(),
            ledger: ParticleResourceLedger::new(),
            printed: HashSet::new(),
        }
    }

    /// The effects world.
    #[must_use]
    pub fn vfx(&self) -> &VfxWorld {
        &self.vfx
    }

    /// The effects world, to report the frame's view to.
    pub fn vfx_mut(&mut self) -> &mut VfxWorld {
        &mut self.vfx
    }

    /// The particles and the particle-model cache, to build draw lists.
    pub fn draw_sources(
        &mut self,
    ) -> (
        &ParticleSystem,
        &mut ParticleGfxCache,
        &mut ParticleResourceLedger,
    ) {
        (self.vfx.particles(), &mut self.gfx, &mut self.ledger)
    }

    /// The resources live emitters hold.
    #[must_use]
    pub fn ledger(&self) -> &ParticleResourceLedger {
        &self.ledger
    }

    /// Queue a server message for the next frame.
    pub fn queue_server(&mut self, effect: ServerEffect) {
        self.server.push(effect);
    }

    /// Sounds the effects asked for since the last call.
    pub fn take_sounds(&mut self) -> Vec<EffectSound> {
        std::mem::take(&mut self.sounds)
    }

    /// Part fades scripts asked for since the last call.
    pub fn take_part_fades(&mut self) -> Vec<ScriptPartFade> {
        std::mem::take(&mut self.fades)
    }

    /// Server objects (by local id) whose scripts switched their lights on
    /// or off since the last call, in order.
    pub fn take_light_switches(&mut self) -> Vec<(u32, bool)> {
        std::mem::take(&mut self.lights)
    }

    /// Server objects and dat statics that are effect owners.
    #[must_use]
    pub fn owner_counts(&self) -> (usize, usize) {
        (self.live.len(), self.statics.len())
    }

    // ── Dat statics ───────────────────────────────────────────────────

    /// A near landblock (`0xXXYY0000`) arrived: its statics, building
    /// shells and cells' statics (`statics`, each with its key: cell 0 for
    /// the landblock's own, [`SHELL_CELL`] for shells) whose Setup has a
    /// default script, or that animate (`animated`), become effect owners.
    /// `offset` places the landblock in the drawing frame.
    pub fn register_statics<'s>(
        &mut self,
        landblock: u32,
        offset: Vec3,
        statics: impl Iterator<Item = (StaticKey, &'s StaticInstance)>,
        animated: impl Fn(StaticKey) -> bool,
        source: &dyn AnimationSource,
    ) {
        for (key, s) in statics {
            if s.model_id >> 24 != 0x02 || self.statics.contains_key(&key) {
                continue;
            }
            let Some(setup) = source.setup(s.model_id) else {
                continue;
            };
            let is_animated = animated(key);
            if setup.default_script == 0 && !is_animated {
                continue;
            }
            let local = s.transform.w_axis.truncate();
            let cell_id = if key.cell != 0 && key.cell != SHELL_CELL {
                key.cell
            } else {
                landblock | outdoor_cell_index(local)
            };
            let Some(id) = self.allocate_static_id() else {
                return;
            };
            let (scale, rotation, _) = s.transform.to_scale_rotation_translation();
            let info = ActivationInfo {
                local_id: id,
                is_dat_static: true,
                position: offset + local,
                rotation,
                cell_id,
                script_id: setup.default_script,
                part_transforms: rigid_parts(&setup, None, scale.x),
                part_availability: None,
                effect_profile: Some(EntityEffectProfile::create_dat_static(&setup)),
            };
            if let Err(error) = self.vfx.activate_entity(info) {
                self.print(&error.to_string());
                continue;
            }
            self.static_keys.insert(id, key);
            self.statics.insert(
                key,
                StaticOwner {
                    id,
                    transform: s.transform,
                    cell: cell_id,
                    animated: is_animated,
                    follows: setup.default_script != 0,
                },
            );
        }
    }

    /// A landblock (`0xXXYY0000`) left: its statics' effects end.
    pub fn drop_statics(&mut self, landblock: u32) {
        let gone: Vec<StaticKey> = self
            .statics
            .keys()
            .copied()
            .filter(|k| k.landblock == landblock)
            .collect();
        for key in gone {
            if let Some(owner) = self.statics.remove(&key) {
                self.static_keys.remove(&owner.id);
                self.vfx.deactivate_entity(owner.id);
            }
        }
    }

    /// The drawing frame moved: place every static owner again.
    pub fn origin_moved(&mut self, landblock_offset: &dyn Fn(u32) -> Vec3) {
        for (key, owner) in &self.statics {
            let (_, rotation, translation) = owner.transform.to_scale_rotation_translation();
            let root = Mat4::from_rotation_translation(
                rotation,
                landblock_offset(key.landblock) + translation,
            );
            self.vfx.update_root(owner.id, root, owner.cell);
        }
    }

    fn allocate_static_id(&mut self) -> Option<u32> {
        // Ids are not reused while a window lives; the range holds about
        // 1.8 billion statics.
        if self.next_static > LAST_STATIC_OWNER {
            return None;
        }
        let id = self.next_static;
        self.next_static += 1;
        Some(id)
    }

    /// The root pose the window last published for the live object `guid`
    /// (drawing frame, no scale): a held object's hangs from its holder
    /// (OpenAC `EffectPoses.TryGetRootPose`, which the selection query reads
    /// for attached objects).
    #[must_use]
    pub fn published_root_pose(&self, guid: u32) -> Option<Mat4> {
        let local = *self.guid_to_local.get(&guid)?;
        self.vfx.poses().root_pose(local)
    }

    /// The live object `guid` stands in the window's resident world (OpenAC
    /// `GpuWorldState.IsLiveEntityProjectionResident`).
    #[must_use]
    pub fn is_live_resident(&self, guid: u32) -> bool {
        self.guid_to_local
            .get(&guid)
            .and_then(|local| self.live.get(local))
            .is_some_and(|owner| owner.resident)
    }

    // ── The frame ─────────────────────────────────────────────────────

    /// One frame: the clock, the objects, the server's messages, the
    /// poses, the animation hooks, the sky, the simulation and what it
    /// asks the window to do. See the module documentation.
    pub fn frame(&mut self, input: EffectsFrame<'_>) {
        let dt = if input.dt.is_finite() && input.dt > 0.0 {
            input.dt
        } else {
            0.0
        };
        self.clock += f64::from(dt);
        if let Err(error) = self.vfx.publish_time(self.clock) {
            self.print(&error.to_string());
        }
        if let Some(scene) = input.scene {
            self.sync_objects(scene, input.to_frame, input.resident, input.source);
        }
        for effect in std::mem::take(&mut self.server) {
            self.server_effect(effect);
        }
        for fired in &input.hooks {
            if let HookOwner::Static(key) = fired.owner
                && let Some(owner) = self.statics.get_mut(&key)
            {
                owner.follows = true;
            }
        }
        self.publish_poses(&input);
        for fired in &input.hooks {
            let owner = match fired.owner {
                HookOwner::Object(local) if self.live.contains_key(&local) => local,
                HookOwner::Static(key) => match self.statics.get(&key) {
                    Some(owner) => owner.id,
                    None => continue,
                },
                HookOwner::Object(_) => continue,
            };
            self.vfx
                .on_animation_hook(&self.facts, owner, fired.position, &fired.hook);
        }
        let (day_fraction, day_group) = input.sky.unwrap_or((0.0, None));
        self.vfx.update_sky(
            day_fraction,
            day_group,
            input.camera,
            input.sky_active && input.sky.is_some(),
            input
                .scene
                .and_then(Scene::player_view)
                .is_some_and(|v| v.disable_most_weather_effects),
        );
        if let Err(error) = self
            .vfx
            .tick(&self.facts, dt, self.clock, input.particle_range)
        {
            self.print(&error.to_string());
        }
        for event in self.vfx.take_events() {
            self.route_event(event);
        }
        for handle in self.vfx.take_retired_emitters() {
            self.ledger.retire(handle);
        }
        for message in self.vfx.take_diagnostics() {
            self.print(&message);
        }
        for message in self.gfx.take_diagnostics() {
            self.print(&message);
        }
    }

    /// Follow the scene's objects: new ones become owners, changed ones
    /// follow, gone ones end. Also rebuilds the owner facts.
    fn sync_objects(
        &mut self,
        scene: &Scene,
        to_frame: &dyn Fn(u32, Vec3) -> Vec3,
        is_resident: &dyn Fn(u32) -> bool,
        source: &dyn AnimationSource,
    ) {
        // Places first: a held object stands where its parent does.
        let by_guid: HashMap<u32, &ObjectView> = scene.objects().map(|o| (o.guid, o)).collect();
        let place = |view: &ObjectView| -> Option<(Vec3, Quat, u32)> {
            let mut current = view;
            for _ in 0..4 {
                if let Some(p) = current.position {
                    let o = p.orientation;
                    return Some((
                        to_frame(p.cell_id, Vec3::new(p.origin.x, p.origin.y, p.origin.z)),
                        Quat::from_xyzw(o.x, o.y, o.z, o.w),
                        p.cell_id,
                    ));
                }
                current = by_guid.get(&current.parent?.guid)?;
            }
            None
        };
        let mut facts = OwnerFacts::default();
        let mut wanted = Vec::new();
        for view in scene.objects() {
            let Some(setup_id) = view.setup_id else {
                continue;
            };
            if view.position.is_none() && view.parent.is_none() {
                continue;
            }
            let local = view.key.local_id;
            let hidden = PhysicsState::from(view.state).contains(PhysicsState::HIDDEN);
            let frozen = PhysicsState::from(view.state).contains(PhysicsState::FROZEN);
            let placed = place(view);
            // Residency is the place's landblock (a held object's is its
            // parent's). Hiding does not take an object out of its cell:
            // its scripts run and its emitters draw while it is hidden.
            let resident = placed.is_some_and(|(_, _, cell)| cell != 0 && is_resident(cell));
            facts.roots.insert(
                local,
                LiveRootState {
                    in_cell: view.position.is_some_and(|p| p.cell_id != 0) && resident,
                    frozen,
                    position: placed.map_or(Vec3::ZERO, |(p, _, _)| p),
                },
            );
            if let Some(parent) = view.parent
                && let Some(parent_view) = by_guid.get(&parent.guid)
            {
                let parent_local = parent_view.key.local_id;
                facts.parents.insert(local, parent_local);
                if let Some(part) = parent_view
                    .setup_id
                    .and_then(|id| source.setup(id))
                    .and_then(|s| {
                        s.holding_locations
                            .iter()
                            .find(|(k, _)| *k as u32 == parent.location_id)
                            .map(|(_, l)| l.part_id)
                    })
                    .and_then(|p| u32::try_from(p).ok())
                {
                    facts.children.insert((parent_local, part), local);
                }
            }
            wanted.push((view, setup_id, hidden, resident, placed));
        }
        self.facts = facts;

        let mut seen = HashSet::new();
        for (view, setup_id, hidden, resident, placed) in wanted {
            let local = view.key.local_id;
            seen.insert(local);
            let description = NetworkEffectDescription {
                physics_script_table_id: view.effects.physics_script_table_id,
                sound_table_id: view.sound_table_id,
                default_script_type: view.effects.default_script_type,
                default_script_intensity: view.effects.default_script_intensity,
            };
            if let Some(owner) = self.live.get(&local)
                && owner.incarnation == view.key.incarnation
                && owner.setup_id == setup_id
                && owner.guid == view.guid
            {
                self.follow(local, view, description, (hidden, resident), placed, source);
                continue;
            }
            if self.live.contains_key(&local) {
                self.end_live(local);
            }
            let Some(setup) = source.setup(setup_id) else {
                continue;
            };
            let (position, rotation, cell) = placed.unwrap_or((Vec3::ZERO, Quat::IDENTITY, 0));
            let scale = view.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
            let info = ActivationInfo {
                local_id: local,
                is_dat_static: false,
                position,
                rotation,
                cell_id: cell,
                script_id: setup.default_script,
                part_transforms: rigid_parts(&setup, Some(view.rest_placement), scale),
                part_availability: None,
                effect_profile: None,
            };
            if let Err(error) = self.vfx.activate_entity(info) {
                self.print(&error.to_string());
                continue;
            }
            let awaiting_placement = view.position.is_none() && view.parent.is_none();
            self.vfx.prepare_live_entity_owner(
                &self.facts,
                view.guid,
                local,
                EntityEffectProfile::create_live(&setup, &description),
                awaiting_placement,
            );
            // A new object starts from the constructor's visible state, so
            // one described hidden has just gone hidden and plays its Hidden
            // script, before what waited for it replays. The server creates
            // a character logging in hidden: this raises the purple portal
            // bubbles it stands in until the server shows it.
            if hidden {
                self.vfx.play_typed_from_hidden_transition(
                    &self.facts,
                    local,
                    HIDDEN_SCRIPT_TYPE,
                    1.0,
                );
            }
            self.vfx
                .replay_pending_for_live_entity(&self.facts, view.guid);
            if !resident {
                self.vfx.set_entity_presentation_visible(local, false);
            }
            self.guid_to_local.insert(view.guid, local);
            self.live.insert(
                local,
                LiveOwner {
                    guid: view.guid,
                    incarnation: view.key.incarnation,
                    setup_id,
                    description,
                    hidden,
                    resident,
                    bound: !awaiting_placement,
                },
            );
        }
        let gone: Vec<u32> = self
            .live
            .keys()
            .copied()
            .filter(|local| !seen.contains(local))
            .collect();
        for local in gone {
            self.end_live(local);
        }
    }

    /// A known owner's view changed (or not): its hidden flag and whether
    /// it stands in the resident world come in as `(hidden, resident)`.
    fn follow(
        &mut self,
        local: u32,
        view: &ObjectView,
        description: NetworkEffectDescription,
        (hidden, resident): (bool, bool),
        placed: Option<(Vec3, Quat, u32)>,
        source: &dyn AnimationSource,
    ) {
        let Some(owner) = self.live.get_mut(&local) else {
            return;
        };
        let guid = owner.guid;
        let description_changed = owner.description != description;
        let hidden_changed = owner.hidden != hidden;
        let resident_changed = owner.resident != resident;
        let first_placement = !owner.bound && view.position.is_some();
        owner.description = description;
        owner.hidden = hidden;
        owner.resident = resident;
        owner.bound |= first_placement;
        if description_changed && let Some(setup) = source.setup(view.setup_id.unwrap_or(0)) {
            self.vfx.on_live_entity_description_changed(
                guid,
                EntityEffectProfile::create_live(&setup, &description),
            );
        }
        if hidden_changed {
            // Going hidden plays the owner's Hidden script and coming back
            // its UnHide script, as the game does: for a character the
            // first raises the portal bubbles and the second stops them.
            let script_type = if hidden {
                HIDDEN_SCRIPT_TYPE
            } else {
                UNHIDE_SCRIPT_TYPE
            };
            self.vfx
                .play_typed_from_hidden_transition(&self.facts, local, script_type, 1.0);
        }
        if resident_changed {
            // Only leaving the resident world hides the emitters: a hidden
            // character's portal bubbles stay drawn.
            self.vfx.set_entity_presentation_visible(local, resident);
        }
        if let Some((position, rotation, cell)) = placed
            && self
                .vfx
                .poses()
                .root_pose(local)
                .is_none_or(|r| r.w_axis.truncate() != position)
        {
            self.vfx.update_root(
                local,
                Mat4::from_rotation_translation(rotation, position),
                cell,
            );
            self.vfx.mark_live_owner_pose_dirty(guid);
        }
        if first_placement {
            self.vfx.on_presentation_bound(&self.facts, guid);
        }
    }

    fn end_live(&mut self, local: u32) {
        let Some(owner) = self.live.remove(&local) else {
            return;
        };
        if self.guid_to_local.get(&owner.guid) == Some(&local) {
            self.guid_to_local.remove(&owner.guid);
        }
        self.vfx
            .on_live_entity_unregistered(owner.guid, Some(local), true);
        self.vfx.deactivate_entity(local);
    }

    fn server_effect(&mut self, effect: ServerEffect) {
        match effect {
            ServerEffect::PlayScript { guid, script_id } => {
                self.vfx.handle_play_script(&self.facts, guid, script_id);
            }
            ServerEffect::PlayScriptType {
                guid,
                script_type,
                intensity,
            } => {
                self.vfx
                    .handle_play_script_type(&self.facts, guid, script_type, intensity);
            }
            ServerEffect::Sound {
                guid,
                sound_type,
                volume,
            } => {
                self.vfx.handle_sound(&self.facts, guid, sound_type, volume);
            }
        }
    }

    fn publish_poses(&mut self, input: &EffectsFrame<'_>) {
        for pose in input.poses {
            if !self.live.contains_key(&pose.local) {
                continue;
            }
            let result = match &pose.parts {
                Some(parts) => self
                    .vfx
                    .publish_pose(pose.local, pose.root, parts, pose.cell, None),
                None => {
                    self.vfx.update_root(pose.local, pose.root, pose.cell);
                    Ok(())
                }
            };
            if let Err(error) = result {
                self.print(&error.to_string());
            }
        }
        for (key, owner) in &self.statics {
            if !owner.animated || !owner.follows {
                continue;
            }
            let Some(animation) = input.animations.static_animation(*key) else {
                continue;
            };
            let (scale, rotation, translation) = owner.transform.to_scale_rotation_translation();
            let root = Mat4::from_rotation_translation(
                animation.step.spin * rotation,
                (input.landblock_offset)(key.landblock) + translation,
            );
            let parts: Vec<Mat4> = animation
                .poses
                .iter()
                .map(|p| scale_offset(p.rigid, scale.x))
                .collect();
            let _ = self
                .vfx
                .publish_pose(owner.id, root, &parts, owner.cell, None);
        }
    }

    fn route_event(&mut self, event: VfxEvent) {
        match event {
            VfxEvent::ScriptHook {
                owner,
                position,
                hook,
            } => self.route_script_hook(owner, position, hook),
            VfxEvent::Effect(EffectEvent::ServerSound {
                owner,
                position,
                sound_type,
                volume,
            }) => {
                if let Some(live) = self.live.get(&owner) {
                    self.sounds.push(EffectSound::Server {
                        guid: live.guid,
                        position,
                        sound_type,
                        volume,
                    });
                }
            }
            // The window's audio follows the scene's sound tables itself;
            // a sky owner's sounds end with their voices.
            VfxEvent::Effect(
                EffectEvent::SoundTableChanged { .. } | EffectEvent::OwnerUnregistered { .. },
            )
            | VfxEvent::SkyOwnerStopped { .. } => {}
        }
    }

    fn route_script_hook(&mut self, owner: u32, position: Vec3, hook: AnimationHook) {
        match hook.data {
            HookData::Sound { .. }
            | HookData::SoundTable { .. }
            | HookData::SoundTweaked { .. } => {
                let guid = self.live.get(&owner).map_or(0, |o| o.guid);
                self.sounds.push(EffectSound::Hook {
                    guid,
                    position,
                    hook: hook.data,
                });
            }
            HookData::TransparentPart {
                part_index,
                start,
                end,
                time,
            } => {
                let owner = if self.live.contains_key(&owner) {
                    Some(HookOwner::Object(owner))
                } else {
                    self.static_keys.get(&owner).map(|k| HookOwner::Static(*k))
                };
                if let Some(owner) = owner {
                    self.fades.push(ScriptPartFade {
                        owner,
                        part: part_index,
                        start,
                        end,
                        time,
                    });
                }
            }
            // A server object's lights switch; a dat static's lights are
            // not switched.
            HookData::SetLight { lights_on } if self.live.contains_key(&owner) => {
                self.lights.push((owner, lights_on));
            }
            // Whole-object fades, scale, luminosity, diffuse and texture
            // hooks are not drawn, as OpenAC.
            _ => {}
        }
    }

    fn print(&mut self, message: &str) {
        if self.printed.len() < DIAGNOSTIC_LIMIT && self.printed.insert(message.to_owned()) {
            eprintln!("vfx: {message}");
        }
    }
}

/// The outdoor land cell (`1..=64`) of a landblock-local position.
fn outdoor_cell_index(local: Vec3) -> u32 {
    let cell = |v: f32| ((v / 24.0).floor().max(0.0) as u32).min(7);
    cell(local.x) * 8 + cell(local.y) + 1
}

/// A Setup's resting part frames with the object's scale in their offsets.
fn rigid_parts(setup: &Setup, placement: Option<u32>, scale: f32) -> Vec<Mat4> {
    rest_poses(setup, placement, scale)
        .into_iter()
        .map(|p| p.rigid)
        .collect()
}

/// A part frame (posed at scale 1) with its offset scaled by `scale`.
fn scale_offset(part: Mat4, scale: f32) -> Mat4 {
    if (scale - 1.0).abs() < f32::EPSILON {
        return part;
    }
    let mut out = part;
    out.w_axis = (part.w_axis.truncate() * scale).extend(1.0);
    out
}

#[cfg(test)]
mod tests;
