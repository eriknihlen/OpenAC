//! Emitter descriptions: the dat's particle emitter record turned into the
//! values the simulation reads, and the registry that resolves and caches
//! them by id.

use std::collections::HashMap;
use std::fmt;
use std::sync::Arc;

use ac_dat::objects::{GfxObjFlags, GfxObjInfo, ParticleEmitter as DatParticleEmitter};
use glam::Vec3;

use crate::dats::VfxDats;

/// How a particle moves: which of the emitter's `a`, `b`, `c` vectors are
/// velocity, acceleration or spin, and whether they turn with the emitter.
/// Values are the dat's.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[repr(i32)]
#[allow(missing_docs)]
pub enum ParticleType {
    #[default]
    Unknown = 0,
    Still = 1,
    LocalVelocity = 2,
    ParabolicLVGA = 3,
    ParabolicLVGAGR = 4,
    Swarm = 5,
    Explode = 6,
    Implode = 7,
    ParabolicLVLA = 8,
    ParabolicLVLALR = 9,
    ParabolicGVGA = 10,
    ParabolicGVGAGR = 11,
    GlobalVelocity = 12,
}

impl ParticleType {
    /// The type for a stored value; anything outside 1..=12 is `Unknown`.
    #[must_use]
    pub fn from_raw(raw: i32) -> Self {
        match raw {
            1 => Self::Still,
            2 => Self::LocalVelocity,
            3 => Self::ParabolicLVGA,
            4 => Self::ParabolicLVGAGR,
            5 => Self::Swarm,
            6 => Self::Explode,
            7 => Self::Implode,
            8 => Self::ParabolicLVLA,
            9 => Self::ParabolicLVLALR,
            10 => Self::ParabolicGVGA,
            11 => Self::ParabolicGVGAGR,
            12 => Self::GlobalVelocity,
            _ => Self::Unknown,
        }
    }

    /// The three "rotating" types, whose particles also spin by `c` per
    /// second (the renderer turns them).
    #[must_use]
    pub fn spins(self) -> bool {
        matches!(
            self,
            Self::ParabolicLVGAGR | Self::ParabolicLVLALR | Self::ParabolicGVGAGR
        )
    }
}

/// What paces emission. Values are the dat's.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum EmitterKind {
    /// Never emits.
    Unknown,
    /// One particle each time `birthrate` seconds have passed.
    #[default]
    BirthratePerSec,
    /// One particle each time the emitter has moved `birthrate` metres.
    BirthratePerMeter,
}

impl EmitterKind {
    /// The kind for a stored value; anything but 1 or 2 is `Unknown`.
    #[must_use]
    pub fn from_raw(raw: i32) -> Self {
        match raw {
            1 => Self::BirthratePerSec,
            2 => Self::BirthratePerMeter,
            _ => Self::Unknown,
        }
    }
}

/// Emitter flag bits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct EmitterFlags(pub u32);

impl EmitterFlags {
    /// Drawn with additive blending when the particle has no material of
    /// its own.
    pub const ADDITIVE: u32 = 0x01;
    /// Drawn as a camera-facing quad.
    pub const BILLBOARD: u32 = 0x02;
    /// Faces the camera.
    pub const FACE_CAMERA: u32 = 0x04;
    /// Particles stay attached to the emitter as it moves (the dat's
    /// "parent local" flag).
    pub const ATTACH_LOCAL: u32 = 0x08;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u32) -> bool {
        self.0 & bits == bits
    }
}

/// Everything the simulation reads about one emitter. Sizes are scale
/// factors, alphas are opacity 0..1, times are seconds, distances metres.
#[derive(Debug, Clone, PartialEq)]
pub struct EmitterDesc {
    /// Beyond this distance from the viewer the emitter degrades out.
    pub max_degrade_distance: f32,
    /// Dat id (`0x32......`).
    pub dat_id: u32,
    /// How particles move.
    pub particle_type: ParticleType,
    /// What paces emission.
    pub emitter_kind: EmitterKind,
    /// Flag bits.
    pub flags: EmitterFlags,
    /// A surface drawn directly on a quad instead of the particle model
    /// (0: use the model). Never set from the dat.
    pub texture_surface_id: u32,
    /// Particle model.
    pub gfx_obj_id: u32,
    /// Particle model for hardware drawing (the one retail draws).
    pub hw_gfx_obj_id: u32,
    /// Emission interval, seconds (or metres for distance pacing).
    pub birthrate: f32,
    /// Particles per second for descriptions without a birthrate.
    pub emit_rate: f32,
    /// Most particles alive at once.
    pub max_particles: i32,
    /// Particles spawned when the emitter starts.
    pub initial_particles: i32,
    /// Particles emitted in all (0: unlimited).
    pub total_particles: i32,
    /// Shortest particle life.
    pub lifetime_min: f32,
    /// Longest particle life.
    pub lifetime_max: f32,
    /// Particle life.
    pub lifespan: f32,
    /// Random spread of the particle life.
    pub lifespan_rand: f32,
    /// Delay before emission starts.
    pub start_delay: f32,
    /// Emitter life (0: unlimited).
    pub total_duration: f32,
    /// Direction the spawn offset is taken perpendicular to.
    pub offset_dir: Vec3,
    /// Smallest spawn offset.
    pub min_offset: f32,
    /// Largest spawn offset.
    pub max_offset: f32,
    /// Spawn disk radius (the largest offset).
    pub spawn_disk_radius: f32,
    /// Fallback first vector when `a` is zero.
    pub initial_velocity: Vec3,
    /// Random spread added to `initial_velocity`.
    pub velocity_jitter: f32,
    /// Fallback second vector when `b` is zero.
    pub gravity: Vec3,
    /// First motion vector.
    pub a: Vec3,
    /// Smallest scale of `a`.
    pub min_a: f32,
    /// Largest scale of `a`.
    pub max_a: f32,
    /// Second motion vector.
    pub b: Vec3,
    /// Smallest scale of `b`.
    pub min_b: f32,
    /// Largest scale of `b`.
    pub max_b: f32,
    /// Third motion vector.
    pub c: Vec3,
    /// Smallest scale of `c`.
    pub min_c: f32,
    /// Largest scale of `c`.
    pub max_c: f32,
    /// Colour at birth, ARGB (alpha unused).
    pub start_color_argb: u32,
    /// Colour at death, ARGB (alpha unused).
    pub end_color_argb: u32,
    /// Opacity at birth.
    pub start_alpha: f32,
    /// Opacity at death.
    pub end_alpha: f32,
    /// Scale at birth.
    pub start_size: f32,
    /// Scale at death.
    pub end_size: f32,
    /// Random spread of the scale.
    pub scale_rand: f32,
    /// Random spread of the opacity.
    pub trans_rand: f32,
    /// Quad rotation at birth (never set from the dat).
    pub start_rotation: f32,
    /// Quad rotation at death (never set from the dat).
    pub end_rotation: f32,
}

impl Default for EmitterDesc {
    fn default() -> Self {
        Self {
            max_degrade_distance: DEFAULT_DEGRADE_DISTANCE,
            dat_id: 0,
            particle_type: ParticleType::Unknown,
            emitter_kind: EmitterKind::BirthratePerSec,
            flags: EmitterFlags(0),
            texture_surface_id: 0,
            gfx_obj_id: 0,
            hw_gfx_obj_id: 0,
            birthrate: 0.0,
            emit_rate: 0.0,
            max_particles: 0,
            initial_particles: 0,
            total_particles: 0,
            lifetime_min: 0.0,
            lifetime_max: 0.0,
            lifespan: 0.0,
            lifespan_rand: 0.0,
            start_delay: 0.0,
            total_duration: 0.0,
            offset_dir: Vec3::Z,
            min_offset: 0.0,
            max_offset: 0.0,
            spawn_disk_radius: 0.0,
            initial_velocity: Vec3::ZERO,
            velocity_jitter: 0.0,
            gravity: Vec3::new(0.0, 0.0, -9.8),
            a: Vec3::ZERO,
            min_a: 1.0,
            max_a: 1.0,
            b: Vec3::ZERO,
            min_b: 1.0,
            max_b: 1.0,
            c: Vec3::ZERO,
            min_c: 1.0,
            max_c: 1.0,
            start_color_argb: 0xFFFF_FFFF,
            end_color_argb: 0xFFFF_FFFF,
            start_alpha: 1.0,
            end_alpha: 0.0,
            start_size: 0.5,
            end_size: 0.5,
            scale_rand: 0.0,
            trans_rand: 0.0,
            start_rotation: 0.0,
            end_rotation: 0.0,
        }
    }
}

impl EmitterDesc {
    /// The description of dat emitter `emitter_id`.
    #[must_use]
    pub fn from_dat(emitter_id: u32, dat: &DatParticleEmitter, max_degrade_distance: f32) -> Self {
        let birthrate = (dat.birthrate as f32).max(0.0);
        let lifespan = (dat.lifespan as f32).max(0.0);
        let lifespan_rand = (dat.lifespan_rand as f32).abs();
        let lifetime_min = (lifespan - lifespan_rand).max(0.0);
        let lifetime_max = lifetime_min.max(lifespan + lifespan_rand);

        let mut flags = EmitterFlags::BILLBOARD | EmitterFlags::FACE_CAMERA;
        if dat.is_parent_local {
            flags |= EmitterFlags::ATTACH_LOCAL;
        }

        // The dat stores transparency; the simulation works in opacity.
        let start_opacity = 1.0 - dat.start_trans.clamp(0.0, 1.0);
        let end_opacity = 1.0 - dat.final_trans.clamp(0.0, 1.0);
        let emitter_kind = EmitterKind::from_raw(dat.emitter_type);
        let a = vec3(dat.a);
        let b = vec3(dat.b);

        Self {
            max_degrade_distance,
            dat_id: emitter_id,
            particle_type: ParticleType::from_raw(dat.particle_type),
            emitter_kind,
            flags: EmitterFlags(flags),
            gfx_obj_id: dat.gfx_obj_id,
            hw_gfx_obj_id: dat.hw_gfx_obj_id,
            birthrate,
            emit_rate: if emitter_kind == EmitterKind::BirthratePerSec && birthrate > 0.0 {
                1.0 / birthrate
            } else {
                0.0
            },
            max_particles: dat.max_particles.max(1),
            initial_particles: dat.initial_particles.max(0),
            total_particles: dat.total_particles.max(0),
            total_duration: (dat.total_seconds as f32).max(0.0),
            lifespan,
            lifespan_rand,
            lifetime_min,
            lifetime_max,
            offset_dir: vec3(dat.offset_dir),
            min_offset: dat.min_offset,
            max_offset: dat.max_offset,
            spawn_disk_radius: dat.max_offset,
            initial_velocity: a,
            gravity: b,
            a,
            min_a: dat.min_a,
            max_a: dat.max_a,
            b,
            min_b: dat.min_b,
            max_b: dat.max_b,
            c: vec3(dat.c),
            min_c: dat.min_c,
            max_c: dat.max_c,
            start_size: dat.start_scale,
            end_size: dat.final_scale,
            scale_rand: dat.scale_rand,
            start_alpha: start_opacity,
            end_alpha: end_opacity,
            trans_rand: dat.trans_rand,
            ..Self::default()
        }
    }
}

pub(crate) fn vec3(v: ac_dat::types::Vector3) -> Vec3 {
    Vec3::new(v.x, v.y, v.z)
}

/// The degrade distance of a particle model without a degrade table.
pub const DEFAULT_DEGRADE_DISTANCE: f32 = 100.0;

/// The distance at which a particle model degrades out, from its degrade
/// table: entry 0 for tables of one or two entries, else the second to last.
/// No table: [`DEFAULT_DEGRADE_DISTANCE`]. The authored value is kept as is
/// (negative, infinite or NaN included).
#[must_use]
pub fn particle_degrade_distance(entries: Option<&[GfxObjInfo]>) -> f32 {
    match entries {
        None | Some([]) => DEFAULT_DEGRADE_DISTANCE,
        Some(entries) => {
            let index = if entries.len() <= 2 {
                0
            } else {
                entries.len() - 2
            };
            entries[index].max_dist
        }
    }
}

/// Why an emitter id resolved to nothing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EmitterDescFailureKind {
    /// No such emitter (or it failed to decode).
    MissingEmitterInfo,
    /// The emitter names no hardware particle model; the game skips such an
    /// emitter and so do we (the software model is not substituted).
    InvalidHardwareGfxObjId,
    /// The hardware particle model it names is not in the dats.
    MissingHardwareGfxObj,
}

/// An emitter that could not be resolved, and the related id.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EmitterDescFailure {
    /// Why.
    pub kind: EmitterDescFailureKind,
    /// The emitter id, or the missing model's id.
    pub related_dat_id: u32,
}

type EmitterResolver = Box<dyn Fn(u32) -> Option<DatParticleEmitter> + Send + Sync>;

enum Resolver {
    None,
    Function(EmitterResolver),
    Dats(Arc<dyn VfxDats>),
}

/// Resolves emitter ids to descriptions, caching hits and misses.
pub struct EmitterDescRegistry {
    resolver: Resolver,
    by_id: HashMap<u32, Arc<EmitterDesc>>,
    failures: HashMap<u32, EmitterDescFailure>,
}

impl fmt::Debug for EmitterDescRegistry {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("EmitterDescRegistry")
            .field("count", &self.by_id.len())
            .field("failures", &self.failures.len())
            .finish_non_exhaustive()
    }
}

impl Default for EmitterDescRegistry {
    fn default() -> Self {
        Self::new()
    }
}

impl EmitterDescRegistry {
    /// A registry that knows only what is registered.
    #[must_use]
    pub fn new() -> Self {
        Self {
            resolver: Resolver::None,
            by_id: HashMap::new(),
            failures: HashMap::new(),
        }
    }

    /// A registry that reads unknown ids through `resolve`; every emitter
    /// gets the default degrade distance.
    #[must_use]
    pub fn with_resolver(
        resolve: impl Fn(u32) -> Option<DatParticleEmitter> + Send + Sync + 'static,
    ) -> Self {
        Self {
            resolver: Resolver::Function(Box::new(resolve)),
            ..Self::new()
        }
    }

    /// A registry that reads unknown ids from the dats, with the degrade
    /// distance of each emitter's hardware particle model.
    #[must_use]
    pub fn with_dats(dats: Arc<dyn VfxDats>) -> Self {
        Self {
            resolver: Resolver::Dats(dats),
            ..Self::new()
        }
    }

    /// Add (or replace) a description; it clears a cached failure.
    pub fn register(&mut self, desc: EmitterDesc) -> Arc<EmitterDesc> {
        let id = desc.dat_id;
        let desc = Arc::new(desc);
        self.by_id.insert(id, Arc::clone(&desc));
        self.failures.remove(&id);
        desc
    }

    /// The description of `emitter_id`, resolving and caching it on first
    /// use. A failure is cached too and never retried.
    pub fn get(&mut self, emitter_id: u32) -> Result<Arc<EmitterDesc>, EmitterDescFailure> {
        if let Some(desc) = self.by_id.get(&emitter_id) {
            return Ok(Arc::clone(desc));
        }
        if let Some(failure) = self.failures.get(&emitter_id) {
            return Err(*failure);
        }

        let resolved = match &self.resolver {
            Resolver::None => None,
            Resolver::Function(resolve) => {
                resolve(emitter_id).map(|dat| Ok((dat, DEFAULT_DEGRADE_DISTANCE)))
            }
            // A decode failure reads as a missing emitter, as in OpenAC.
            Resolver::Dats(dats) => dats.particle_emitter(emitter_id).ok().flatten().map(|dat| {
                max_degrade_distance(dats.as_ref(), &dat).map(|distance| ((*dat), distance))
            }),
        };
        let failure = match resolved {
            Some(Ok((dat, distance))) => {
                let desc = Arc::new(EmitterDesc::from_dat(emitter_id, &dat, distance));
                self.by_id.insert(emitter_id, Arc::clone(&desc));
                return Ok(desc);
            }
            Some(Err(failure)) => failure,
            None => EmitterDescFailure {
                kind: EmitterDescFailureKind::MissingEmitterInfo,
                related_dat_id: emitter_id,
            },
        };
        self.failures.insert(emitter_id, failure);
        Err(failure)
    }

    /// Descriptions held.
    #[must_use]
    pub fn count(&self) -> usize {
        self.by_id.len()
    }

    /// Failures held.
    #[must_use]
    pub fn failure_count(&self) -> usize {
        self.failures.len()
    }
}

/// The degrade distance of the emitter's hardware particle model; an
/// emitter without one (or whose model is missing) fails, as the game's
/// emitter set-up does.
fn max_degrade_distance(
    dats: &dyn VfxDats,
    emitter: &DatParticleEmitter,
) -> Result<f32, EmitterDescFailure> {
    let gfx_obj_id = emitter.hw_gfx_obj_id;
    if gfx_obj_id == 0 {
        return Err(EmitterDescFailure {
            kind: EmitterDescFailureKind::InvalidHardwareGfxObjId,
            related_dat_id: 0,
        });
    }
    let Some(gfx) = dats.gfx_obj(gfx_obj_id).ok().flatten() else {
        return Err(EmitterDescFailure {
            kind: EmitterDescFailureKind::MissingHardwareGfxObj,
            related_dat_id: gfx_obj_id,
        });
    };
    if !gfx.flags.contains(GfxObjFlags::HAS_DID_DEGRADE) || gfx.did_degrade == 0 {
        return Ok(DEFAULT_DEGRADE_DISTANCE);
    }
    let degrade = dats.gfx_obj_degrade_info(gfx.did_degrade).ok().flatten();
    Ok(particle_degrade_distance(
        degrade.as_deref().map(|d| d.degrades.as_slice()),
    ))
}
