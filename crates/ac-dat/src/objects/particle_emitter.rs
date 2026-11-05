use ac_bits::Reader;

use super::DatObject;
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::{Vector3, read_bool32};

type Result<T> = core::result::Result<T, DecodeError>;

/// How a particle emitter spawns and moves its particles. File id
/// `0x32......`.
///
/// `a`, `b` and `c` are the three motion vectors the particle type
/// combines (for example velocity, acceleration, spin); each is scaled by
/// a random factor between its minimum and maximum.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ParticleEmitter {
    /// File id.
    pub id: u32,
    /// A stored value of unknown use.
    pub unknown: u32,
    /// What paces emission (time, distance, ...).
    pub emitter_type: i32,
    /// How particles move.
    pub particle_type: i32,
    /// Particle model id.
    pub gfx_obj_id: u32,
    /// Particle model id for hardware rendering.
    pub hw_gfx_obj_id: u32,
    /// Emission interval, seconds (or metres for distance pacing).
    pub birthrate: f64,
    /// Most particles alive at once.
    pub max_particles: i32,
    /// Particles spawned at once on start.
    pub initial_particles: i32,
    /// Particles spawned in all (0: unlimited).
    pub total_particles: i32,
    /// Emitter lifetime, seconds (0: unlimited).
    pub total_seconds: f64,
    /// Particle lifetime, seconds.
    pub lifespan: f64,
    /// Random spread of the particle lifetime, seconds.
    pub lifespan_rand: f64,
    /// Direction of the spawn offset.
    pub offset_dir: Vector3,
    /// Smallest spawn offset, metres.
    pub min_offset: f32,
    /// Largest spawn offset, metres.
    pub max_offset: f32,
    /// First motion vector.
    pub a: Vector3,
    /// Smallest scale of `a`.
    pub min_a: f32,
    /// Largest scale of `a`.
    pub max_a: f32,
    /// Second motion vector.
    pub b: Vector3,
    /// Smallest scale of `b`.
    pub min_b: f32,
    /// Largest scale of `b`.
    pub max_b: f32,
    /// Third motion vector.
    pub c: Vector3,
    /// Smallest scale of `c`.
    pub min_c: f32,
    /// Largest scale of `c`.
    pub max_c: f32,
    /// Particle scale at birth.
    pub start_scale: f32,
    /// Particle scale at death.
    pub final_scale: f32,
    /// Random spread of the scale.
    pub scale_rand: f32,
    /// Particle transparency at birth.
    pub start_trans: f32,
    /// Particle transparency at death.
    pub final_trans: f32,
    /// Random spread of the transparency.
    pub trans_rand: f32,
    /// Particles move with the parent object after spawning.
    pub is_parent_local: bool,
}

impl DatObject for ParticleEmitter {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            id: r.u32()?,
            unknown: r.u32()?,
            emitter_type: r.i32()?,
            particle_type: r.i32()?,
            gfx_obj_id: r.u32()?,
            hw_gfx_obj_id: r.u32()?,
            birthrate: r.f64()?,
            max_particles: r.i32()?,
            initial_particles: r.i32()?,
            total_particles: r.i32()?,
            total_seconds: r.f64()?,
            lifespan: r.f64()?,
            lifespan_rand: r.f64()?,
            offset_dir: Vector3::unpack(r)?,
            min_offset: r.f32()?,
            max_offset: r.f32()?,
            a: Vector3::unpack(r)?,
            min_a: r.f32()?,
            max_a: r.f32()?,
            b: Vector3::unpack(r)?,
            min_b: r.f32()?,
            max_b: r.f32()?,
            c: Vector3::unpack(r)?,
            min_c: r.f32()?,
            max_c: r.f32()?,
            start_scale: r.f32()?,
            final_scale: r.f32()?,
            scale_rand: r.f32()?,
            start_trans: r.f32()?,
            final_trans: r.f32()?,
            trans_rand: r.f32()?,
            is_parent_local: read_bool32(r)?,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn unpacks_every_field_in_order() {
        let mut w = Writer::new();
        w.u32(0x3200_0004);
        w.u32(0);
        w.i32(2);
        w.i32(3);
        w.u32(0x0100_0FB3);
        w.u32(0x0100_0FC4);
        w.f64(0.1);
        w.i32(20);
        w.i32(1);
        w.i32(0);
        w.f64(10.0);
        w.f64(1.0);
        w.f64(0.5);
        // offset dir, min, max; a, b, c with min/max; 6 scale/trans values.
        let floats: Vec<f32> = (1..=26).map(|i| i as f32).collect();
        for v in floats {
            w.f32(v);
        }
        w.u32(1);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let p = ParticleEmitter::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!((p.emitter_type, p.particle_type), (2, 3));
        assert_eq!(p.hw_gfx_obj_id, 0x0100_0FC4);
        assert_eq!(p.birthrate, 0.1);
        assert_eq!(p.max_particles, 20);
        assert_eq!(p.lifespan_rand, 0.5);
        assert_eq!(p.offset_dir.z, 3.0);
        assert_eq!(p.max_offset, 5.0);
        assert_eq!(p.a.x, 6.0);
        assert_eq!(p.max_a, 10.0);
        assert_eq!(p.b.z, 13.0);
        assert_eq!(p.c.x, 16.0);
        assert_eq!(p.max_c, 20.0);
        assert_eq!(p.start_scale, 21.0);
        assert_eq!(p.trans_rand, 26.0);
        assert!(p.is_parent_local);
        for len in 0..bytes.len() {
            assert!(ParticleEmitter::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
