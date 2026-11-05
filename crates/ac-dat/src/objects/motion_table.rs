use ac_bits::Reader;

use super::DatObject;
use super::read::{self, keyed, list};
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::Vector3;

type Result<T> = core::result::Result<T, DecodeError>;

/// How a creature moves: the animations for each motion in each stance,
/// the overlays (modifiers) and the transitions between motions. File id
/// `0x09......`.
///
/// Motion keys combine a stance and a motion: `(stance << 16) | (motion &
/// 0xFFFFFF)`, kept as stored. Tables are in file order.
#[derive(Debug, Clone, PartialEq)]
pub struct MotionTable {
    /// File id.
    pub id: u32,
    /// The stance a creature starts in.
    pub default_style: u32,
    /// Default motion per stance: `(stance, motion)`.
    pub style_defaults: Vec<(u32, u32)>,
    /// Looping motions, keyed by stance and motion.
    pub cycles: Vec<(u32, MotionData)>,
    /// Motions layered over a cycle, keyed by stance and motion.
    pub modifiers: Vec<(u32, MotionData)>,
    /// Transitions: keyed by stance and starting motion, then by the
    /// motion being transitioned to.
    pub links: Vec<(u32, Vec<(u32, MotionData)>)>,
}

impl DatObject for MotionTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let default_style = r.u32()?;
        let count = u64::from(r.u32()?);
        let style_defaults = keyed(r, "style defaults", count, read::u32, read::u32)?;
        let count = u64::from(r.u32()?);
        let cycles = keyed(r, "cycles", count, read::u32, MotionData::unpack)?;
        let count = u64::from(r.u32()?);
        let modifiers = keyed(r, "modifiers", count, read::u32, MotionData::unpack)?;
        let count = u64::from(r.u32()?);
        let links = keyed(r, "links", count, read::u32, |r| {
            let count = u64::from(r.u32()?);
            keyed(r, "link targets", count, read::u32, MotionData::unpack)
        })?;
        Ok(Self {
            id,
            default_style,
            style_defaults,
            cycles,
            modifiers,
            links,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// Motion data flag bits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct MotionDataFlags(pub u8);

impl MotionDataFlags {
    /// A velocity follows the animations.
    pub const HAS_VELOCITY: u8 = 0x1;
    /// An angular velocity follows.
    pub const HAS_OMEGA: u8 = 0x2;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u8) -> bool {
        self.0 & bits == bits
    }
}

/// One motion: the animations it plays in order and how it moves the
/// object.
#[derive(Debug, Clone, PartialEq)]
pub struct MotionData {
    /// Extra bits stored with the motion.
    pub bitfield: u8,
    /// Which optional fields are present.
    pub flags: MotionDataFlags,
    /// Animations, played in order.
    pub anims: Vec<AnimData>,
    /// Velocity, metres per second; zero without
    /// [`MotionDataFlags::HAS_VELOCITY`].
    pub velocity: Vector3,
    /// Angular velocity, radians per second; zero without
    /// [`MotionDataFlags::HAS_OMEGA`].
    pub omega: Vector3,
}

impl MotionData {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let num_anims = r.u8()?;
        let bitfield = r.u8()?;
        let flags = MotionDataFlags(r.u8()?);
        r.align(4)?;
        let anims = list(r, u64::from(num_anims), AnimData::unpack)?;
        let velocity = if flags.contains(MotionDataFlags::HAS_VELOCITY) {
            Vector3::unpack(r)?
        } else {
            Vector3::default()
        };
        let omega = if flags.contains(MotionDataFlags::HAS_OMEGA) {
            Vector3::unpack(r)?
        } else {
            Vector3::default()
        };
        Ok(Self {
            bitfield,
            flags,
            anims,
            velocity,
            omega,
        })
    }
}

/// A span of one animation and the rate to play it at.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AnimData {
    /// Animation id (`0x03......`).
    pub anim_id: u32,
    /// First frame.
    pub low_frame: i32,
    /// Last frame; -1 means the animation's last frame.
    pub high_frame: i32,
    /// Frames per second; negative plays backwards.
    pub framerate: f32,
}

impl AnimData {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            anim_id: r.u32()?,
            low_frame: r.i32()?,
            high_frame: r.i32()?,
            framerate: r.f32()?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn motion_data(w: &mut Writer, anims: u8, flags: u8) {
        w.u8(anims);
        w.u8(0);
        w.u8(flags);
        w.align(4);
        for i in 0..anims {
            w.u32(0x0300_0001 + u32::from(i));
            w.i32(0);
            w.i32(-1);
            w.f32(30.0);
        }
        if flags & MotionDataFlags::HAS_VELOCITY != 0 {
            for v in [0.0f32, 4.0, 0.0] {
                w.f32(v);
            }
        }
        if flags & MotionDataFlags::HAS_OMEGA != 0 {
            for v in [0.0f32, 0.0, -1.5] {
                w.f32(v);
            }
        }
    }

    fn sample() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x0900_0001);
        w.u32(0x8000_003D);
        w.u32(1);
        w.u32(0x8000_003D);
        w.u32(0x4100_0003);
        // Cycles: 2.
        w.u32(2);
        w.u32(0x003D_0003);
        motion_data(&mut w, 1, 0);
        w.u32(0x003D_0005);
        motion_data(&mut w, 2, MotionDataFlags::HAS_VELOCITY);
        // Modifiers: 1.
        w.u32(1);
        w.u32(0x0046_000D);
        motion_data(&mut w, 0, MotionDataFlags::HAS_OMEGA);
        // Links: 1 with 2 targets.
        w.u32(1);
        w.u32(0x003D_0003);
        w.u32(2);
        w.u32(0x4100_0003);
        motion_data(&mut w, 1, 0);
        w.u32(0x4500_0005);
        motion_data(&mut w, 1, 3);
        w.into_vec()
    }

    #[test]
    fn unpacks_every_table() {
        let bytes = sample();
        let r = &mut Reader::new(&bytes);
        let t = MotionTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(t.default_style, 0x8000_003D);
        assert_eq!(t.style_defaults, vec![(0x8000_003D, 0x4100_0003)]);
        assert_eq!(t.cycles.len(), 2);
        let (key, run) = &t.cycles[1];
        assert_eq!(*key, 0x003D_0005);
        assert_eq!(run.anims.len(), 2);
        assert_eq!(run.anims[1].anim_id, 0x0300_0002);
        assert_eq!(run.anims[1].high_frame, -1);
        assert_eq!(run.velocity.y, 4.0);
        assert_eq!(run.omega, Vector3::default());
        assert_eq!(t.modifiers[0].1.omega.z, -1.5);
        assert!(t.modifiers[0].1.anims.is_empty());
        assert_eq!(t.links[0].0, 0x003D_0003);
        let targets = &t.links[0].1;
        assert_eq!(targets.len(), 2);
        assert_eq!(targets[1].0, 0x4500_0005);
        assert!(targets[1].1.flags.contains(3));
    }

    #[test]
    fn truncated_table_is_an_error_at_every_length() {
        let bytes = sample();
        for len in 0..bytes.len() {
            assert!(
                MotionTable::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                "len {len}"
            );
        }
    }

    #[test]
    fn duplicate_cycle_is_an_error() {
        let mut bytes = sample();
        // Second cycle key sits after the header (20 bytes), the count (4),
        // the first key (4) and its 20-byte motion.
        let second = 20 + 4 + 4 + 20;
        bytes[second..second + 4].copy_from_slice(&0x003D_0003u32.to_le_bytes());
        assert!(matches!(
            MotionTable::unpack(&mut Reader::new(&bytes)),
            Err(DecodeError::DuplicateKey {
                table: "cycles",
                ..
            })
        ));
    }
}
