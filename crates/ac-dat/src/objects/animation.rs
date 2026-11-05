use ac_bits::Reader;

use super::DatObject;
use super::read::list;
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::{Frame, Vector3, read_bool32};

type Result<T> = core::result::Result<T, DecodeError>;

/// Base id of model objects; packed model ids are stored relative to it.
const GFX_OBJ_BASE: u32 = 0x0100_0000;

/// Animation flag bits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct AnimationFlags(pub u32);

impl AnimationFlags {
    /// A whole-object frame per animation frame precedes the part frames.
    pub const POS_FRAMES: u32 = 0x1;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u32) -> bool {
        self.0 & bits == bits
    }
}

/// A keyframed animation: per frame, a placement for every part plus the
/// hooks fired on that frame. File id `0x03......`.
#[derive(Debug, Clone, PartialEq)]
pub struct Animation {
    /// File id.
    pub id: u32,
    /// Flags.
    pub flags: AnimationFlags,
    /// Number of parts each frame places.
    pub num_parts: u32,
    /// Movement of the whole object per frame; empty without
    /// [`AnimationFlags::POS_FRAMES`].
    pub pos_frames: Vec<Frame>,
    /// Part placements and hooks per frame.
    pub part_frames: Vec<AnimationFrame>,
}

impl DatObject for Animation {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let flags = AnimationFlags(r.u32()?);
        let num_parts = r.u32()?;
        let num_frames = u64::from(r.u32()?);
        let pos_frames = if flags.contains(AnimationFlags::POS_FRAMES) {
            list(r, num_frames, Frame::unpack)?
        } else {
            Vec::new()
        };
        let part_frames = list(r, num_frames, |r| AnimationFrame::unpack(r, num_parts))?;
        Ok(Self {
            id,
            flags,
            num_parts,
            pos_frames,
            part_frames,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// One frame of part placements plus the hooks fired on it.
#[derive(Debug, Clone, PartialEq)]
pub struct AnimationFrame {
    /// One frame per part.
    pub frames: Vec<Frame>,
    /// Hooks, in file order.
    pub hooks: Vec<AnimationHook>,
}

impl AnimationFrame {
    /// Decode a frame for an object with `num_parts` parts (the count is
    /// not stored with the frame).
    pub(crate) fn unpack(r: &mut Reader<'_>, num_parts: u32) -> Result<Self> {
        let mut frames = Vec::new();
        for _ in 0..num_parts {
            frames.push(Frame::unpack(r)?);
        }
        let num_hooks = r.u32()?;
        let mut hooks = Vec::new();
        for _ in 0..num_hooks {
            hooks.push(AnimationHook::unpack(r)?);
        }
        Ok(Self { frames, hooks })
    }
}

/// An event attached to an animation frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AnimationHook {
    /// Playback direction the hook fires in: 0 both, 1 forward,
    /// `0xFFFFFFFF` backward.
    pub direction: u32,
    /// The hook's kind and data.
    pub data: HookData,
}

/// Hook kinds and their data. The stored type number is in each variant's doc.
#[allow(missing_docs)]
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum HookData {
    /// 0: does nothing.
    NoOp,
    /// 1: play a sound.
    Sound { id: u32 },
    /// 2: play a sound from the object's sound table.
    SoundTable { sound: u32 },
    /// 3: an attack lands.
    Attack(AttackCone),
    /// 4: the animation is done.
    AnimationDone,
    /// 5: swap a part's model.
    ReplaceObject { part_index: u8, part_id: u32 },
    /// 6: become (non-)ethereal.
    Ethereal { ethereal: bool },
    /// 7: fade one part.
    TransparentPart {
        part_index: u32,
        start: f32,
        end: f32,
        time: f32,
    },
    /// 8: change luminosity.
    Luminous { start: f32, end: f32, time: f32 },
    /// 9: change one part's luminosity.
    LuminousPart {
        part_index: u32,
        start: f32,
        end: f32,
        time: f32,
    },
    /// 10: change diffuse lighting.
    Diffuse { start: f32, end: f32, time: f32 },
    /// 11: change one part's diffuse lighting.
    DiffusePart {
        part_index: u32,
        start: f32,
        end: f32,
        time: f32,
    },
    /// 12: scale the object.
    Scale { end: f32, time: f32 },
    /// 13: start a particle emitter.
    CreateParticle {
        emitter_info_id: u32,
        part_index: u32,
        offset: Frame,
        emitter_id: u32,
    },
    /// 14: destroy a particle emitter.
    DestroyParticle { emitter_id: u32 },
    /// 15: stop a particle emitter.
    StopParticle { emitter_id: u32 },
    /// 16: hide or show the object.
    NoDraw { no_draw: bool },
    /// 17: run the default script.
    DefaultScript,
    /// 18: run the default script on one part.
    DefaultScriptPart { part_index: u32 },
    /// 19: call a physics effect script.
    CallPes { pes: u32, pause: f32 },
    /// 20: fade the object.
    Transparent { start: f32, end: f32, time: f32 },
    /// 21: play a sound with tweaks.
    SoundTweaked {
        sound_id: u32,
        priority: f32,
        probability: f32,
        volume: f32,
    },
    /// 22: set spin.
    SetOmega { axis: Vector3 },
    /// 23: scroll textures.
    TextureVelocity { u_speed: f32, v_speed: f32 },
    /// 24: scroll one part's textures.
    TextureVelocityPart {
        part_index: u32,
        u_speed: f32,
        v_speed: f32,
    },
    /// 25: lights on or off.
    SetLight { lights_on: bool },
    /// 26: start a blocking particle emitter (same fields as 13).
    CreateBlockingParticle {
        emitter_info_id: u32,
        part_index: u32,
        offset: Frame,
        emitter_id: u32,
    },
    /// Any other type: no fields follow, and the hook does nothing.
    Unknown { hook_type: u32 },
}

impl AnimationHook {
    /// Decode a hook: type, direction, the type's fields, then padding to
    /// 4 bytes from the object start. A type outside 0..=26 has no fields.
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let hook_type = r.u32()?;
        let direction = r.u32()?;
        let data = match hook_type {
            0 => HookData::NoOp,
            1 => HookData::Sound { id: r.u32()? },
            2 => HookData::SoundTable { sound: r.u32()? },
            3 => HookData::Attack(AttackCone::unpack(r)?),
            4 => HookData::AnimationDone,
            5 => HookData::ReplaceObject {
                part_index: r.u8()?,
                part_id: r.data_id_of_known_type(GFX_OBJ_BASE)?,
            },
            6 => HookData::Ethereal {
                ethereal: read_bool32(r)?,
            },
            7 => HookData::TransparentPart {
                part_index: r.u32()?,
                start: r.f32()?,
                end: r.f32()?,
                time: r.f32()?,
            },
            8 => HookData::Luminous {
                start: r.f32()?,
                end: r.f32()?,
                time: r.f32()?,
            },
            9 => HookData::LuminousPart {
                part_index: r.u32()?,
                start: r.f32()?,
                end: r.f32()?,
                time: r.f32()?,
            },
            10 => HookData::Diffuse {
                start: r.f32()?,
                end: r.f32()?,
                time: r.f32()?,
            },
            11 => HookData::DiffusePart {
                part_index: r.u32()?,
                start: r.f32()?,
                end: r.f32()?,
                time: r.f32()?,
            },
            12 => HookData::Scale {
                end: r.f32()?,
                time: r.f32()?,
            },
            13 => HookData::CreateParticle {
                emitter_info_id: r.u32()?,
                part_index: r.u32()?,
                offset: Frame::unpack(r)?,
                emitter_id: r.u32()?,
            },
            14 => HookData::DestroyParticle {
                emitter_id: r.u32()?,
            },
            15 => HookData::StopParticle {
                emitter_id: r.u32()?,
            },
            16 => HookData::NoDraw {
                no_draw: read_bool32(r)?,
            },
            17 => HookData::DefaultScript,
            18 => HookData::DefaultScriptPart {
                part_index: r.u32()?,
            },
            19 => HookData::CallPes {
                pes: r.u32()?,
                pause: r.f32()?,
            },
            20 => HookData::Transparent {
                start: r.f32()?,
                end: r.f32()?,
                time: r.f32()?,
            },
            21 => HookData::SoundTweaked {
                sound_id: r.u32()?,
                priority: r.f32()?,
                probability: r.f32()?,
                volume: r.f32()?,
            },
            22 => HookData::SetOmega {
                axis: Vector3::unpack(r)?,
            },
            23 => HookData::TextureVelocity {
                u_speed: r.f32()?,
                v_speed: r.f32()?,
            },
            24 => HookData::TextureVelocityPart {
                part_index: r.u32()?,
                u_speed: r.f32()?,
                v_speed: r.f32()?,
            },
            25 => HookData::SetLight {
                lights_on: read_bool32(r)?,
            },
            26 => HookData::CreateBlockingParticle {
                emitter_info_id: r.u32()?,
                part_index: r.u32()?,
                offset: Frame::unpack(r)?,
                emitter_id: r.u32()?,
            },
            other => HookData::Unknown { hook_type: other },
        };
        r.align(4)?;
        Ok(Self { direction, data })
    }
}

/// The area an attack hook covers.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct AttackCone {
    /// Part the cone is attached to.
    pub part_index: u32,
    /// Left edge, x.
    pub left_x: f32,
    /// Left edge, y.
    pub left_y: f32,
    /// Right edge, x.
    pub right_x: f32,
    /// Right edge, y.
    pub right_y: f32,
    /// Radius.
    pub radius: f32,
    /// Height.
    pub height: f32,
}

impl AttackCone {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            part_index: r.u32()?,
            left_x: r.f32()?,
            left_y: r.f32()?,
            right_x: r.f32()?,
            right_y: r.f32()?,
            radius: r.f32()?,
            height: r.f32()?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn hook(hook_type: u32, direction: u32, body: &[u8]) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(hook_type);
        w.u32(direction);
        w.bytes(body);
        w.into_vec()
    }

    #[test]
    fn hooks_read_type_direction_and_body() {
        let h = AnimationHook::unpack(&mut Reader::new(&hook(1, 1, &0x0A00_0010u32.to_le_bytes())))
            .unwrap();
        assert_eq!(h.direction, 1);
        assert_eq!(h.data, HookData::Sound { id: 0x0A00_0010 });

        let h = AnimationHook::unpack(&mut Reader::new(&hook(4, 0, &[]))).unwrap();
        assert_eq!(h.data, HookData::AnimationDone);

        let h = AnimationHook::unpack(&mut Reader::new(&hook(6, 0, &[2, 0, 0, 0]))).unwrap();
        assert_eq!(h.data, HookData::Ethereal { ethereal: true });
    }

    #[test]
    fn replace_object_uses_a_byte_part_index_and_a_packed_model_id() {
        // Part 1, packed id 0x0BB4 -> model 0x01000BB4, one byte of padding.
        let r = &mut Reader::new(&[5, 0, 0, 0, 0, 0, 0, 0, 1, 0xB4, 0x0B, 0, 0xEE]);
        let h = AnimationHook::unpack(r).unwrap();
        assert_eq!(
            h.data,
            HookData::ReplaceObject {
                part_index: 1,
                part_id: 0x0100_0BB4
            }
        );
        assert_eq!(r.remaining(), 1);

        // The long id form: 5 bytes of fields, padded to 8.
        let r = &mut Reader::new(&[5, 0, 0, 0, 0, 0, 0, 0, 2, 0x01, 0x80, 0x34, 0x12, 0, 0, 0]);
        let h = AnimationHook::unpack(r).unwrap();
        assert_eq!(
            h.data,
            HookData::ReplaceObject {
                part_index: 2,
                part_id: 0x0101_1234
            }
        );
        assert_eq!(r.remaining(), 0);
    }

    #[test]
    fn every_known_type_consumes_its_size() {
        // Body sizes per type, worked from the field lists (padded to 4).
        let sizes = [
            (0, 0),
            (1, 4),
            (2, 4),
            (3, 28),
            (4, 0),
            (5, 4),
            (6, 4),
            (7, 16),
            (8, 12),
            (9, 16),
            (10, 12),
            (11, 16),
            (12, 8),
            (13, 40),
            (14, 4),
            (15, 4),
            (16, 4),
            (17, 0),
            (18, 4),
            (19, 8),
            (20, 12),
            (21, 16),
            (22, 12),
            (23, 8),
            (24, 12),
            (25, 4),
            (26, 40),
        ];
        for (hook_type, size) in sizes {
            let bytes = hook(hook_type, 0, &vec![0; size]);
            let mut r = Reader::new(&bytes);
            AnimationHook::unpack(&mut r).unwrap();
            assert_eq!(r.remaining(), 0, "type {hook_type}");
            if size > 0 {
                assert!(
                    AnimationHook::unpack(&mut Reader::new(&bytes[..bytes.len() - 1])).is_err(),
                    "type {hook_type}"
                );
            }
        }
    }

    #[test]
    fn unknown_hook_type_has_no_fields() {
        for hook_type in [27, 0xFFFF_FFFF] {
            let bytes = hook(hook_type, 1, &[0; 8]);
            let r = &mut Reader::new(&bytes);
            let h = AnimationHook::unpack(r).unwrap();
            assert_eq!(h.data, HookData::Unknown { hook_type });
            assert_eq!(h.direction, 1);
            assert_eq!(r.remaining(), 8);
        }
        let h = AnimationHook::unpack(&mut Reader::new(&hook(0, 0, &[]))).unwrap();
        assert_eq!(h.data, HookData::NoOp);
    }

    fn animation_bytes(flags: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x0300_0001);
        w.u32(flags);
        w.u32(2); // parts
        w.u32(2); // frames
        if flags & AnimationFlags::POS_FRAMES != 0 {
            for x in [1.0f32, 2.0] {
                for v in [x, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0] {
                    w.f32(v);
                }
            }
        }
        for frame in 0..2 {
            for part in 0..2 {
                for v in [frame as f32, part as f32, 0.5, 1.0, 0.0, 0.0, 0.0] {
                    w.f32(v);
                }
            }
            w.u32(frame); // hooks: none on frame 0, one on frame 1
            if frame == 1 {
                w.bytes(&hook(2, 1, &1u32.to_le_bytes()));
            }
        }
        w.into_vec()
    }

    #[test]
    fn animation_with_and_without_position_frames() {
        for flags in [0, AnimationFlags::POS_FRAMES | 0x2] {
            let bytes = animation_bytes(flags);
            let r = &mut Reader::new(&bytes);
            let a = Animation::unpack(r).unwrap();
            assert_eq!(r.remaining(), 0);
            assert_eq!(a.num_parts, 2);
            assert_eq!(a.part_frames.len(), 2);
            assert_eq!(a.part_frames[1].frames[1].origin.y, 1.0);
            assert_eq!(
                a.part_frames[1].hooks[0].data,
                HookData::SoundTable { sound: 1 }
            );
            if flags == 0 {
                assert!(a.pos_frames.is_empty());
            } else {
                assert_eq!(a.pos_frames[1].origin.x, 2.0);
            }
            for len in 0..bytes.len() {
                assert!(
                    Animation::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                    "len {len}"
                );
            }
        }
    }

    #[test]
    fn animation_frame_reads_one_frame_per_part_then_hooks() {
        let mut w = Writer::new();
        for part in 0..2 {
            for v in [part as f32, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0] {
                w.f32(v);
            }
        }
        w.u32(1);
        w.bytes(&hook(17, 0, &[]));
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let f = AnimationFrame::unpack(&mut r, 2).unwrap();
        assert_eq!(f.frames.len(), 2);
        assert_eq!(f.frames[1].origin.x, 1.0);
        assert_eq!(f.hooks[0].data, HookData::DefaultScript);
        assert_eq!(r.remaining(), 0);
        assert!(AnimationFrame::unpack(&mut Reader::new(&bytes[..bytes.len() - 1]), 2).is_err());
        assert!(AnimationFrame::unpack(&mut Reader::new(&bytes), 3).is_err());
    }
}
