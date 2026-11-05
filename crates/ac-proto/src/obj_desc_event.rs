use ac_bits::Reader;

use crate::{ModelData, Opcode, ProtoError, Result, expect_opcode};

/// An object's appearance changed ([`Opcode::ObjDescEvent`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ObjDescEvent {
    /// The object.
    pub guid: u32,
    /// The new appearance.
    pub model: ModelData,
    /// The object's incarnation.
    pub instance_sequence: u16,
    /// Appearance update counter.
    pub obj_desc_sequence: u16,
}

impl ObjDescEvent {
    /// Parse the body: guid, appearance block, then exactly the two
    /// counters. Any other length after the appearance block is an error.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::ObjDescEvent)?;
        let guid = r.u32()?;
        let model = ModelData::read(&mut r)?;
        if r.remaining() != 4 {
            return Err(ProtoError::InvalidLength {
                what: "appearance change",
                actual: body.len(),
            });
        }
        Ok(Self {
            guid,
            model,
            instance_sequence: r.u16()?,
            obj_desc_sequence: r.u16()?,
        })
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/ObjDescEventTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::model_data::tests::packed_of_known_type;
    use crate::model_data::{PALETTE_ID_TYPE, SURFACE_TEXTURE_ID_TYPE};
    use crate::test_util::assert_every_prefix_fails;

    #[test]
    fn rejects_wrong_opcode_and_truncation() {
        let mut body = vec![0u8; 16];
        body[..4].copy_from_slice(&0xF745u32.to_le_bytes());
        assert!(matches!(
            ObjDescEvent::parse(&body),
            Err(ProtoError::WrongOpcode { .. })
        ));
        assert!(ObjDescEvent::parse(&[0; 3]).is_err());
    }

    #[test]
    fn synthesized_body_guid_and_model_data() {
        let mut w = Writer::new();
        w.u32(0xF625);
        w.u32(0x5000_0001);
        w.bytes(&[0x11, 3, 4, 0]);
        packed_of_known_type(&mut w, 0x0400_007E, PALETTE_ID_TYPE);
        for (id, offset, length) in [
            (0x0400_1FE3, 24, 8),
            (0x0400_02BA, 0, 24),
            (0x0400_02BC, 32, 8),
        ] {
            packed_of_known_type(&mut w, id, PALETTE_ID_TYPE);
            w.u8(offset);
            w.u8(length);
        }
        for part in 0..4u8 {
            w.u8(part);
            packed_of_known_type(
                &mut w,
                0x0500_0100 + u32::from(part),
                SURFACE_TEXTURE_ID_TYPE,
            );
            packed_of_known_type(
                &mut w,
                0x0500_0200 + u32::from(part),
                SURFACE_TEXTURE_ID_TYPE,
            );
        }
        w.align(4);
        w.u16(0x5678);
        w.u16(0xDEF0);
        let body = w.into_vec();

        let p = ObjDescEvent::parse(&body).unwrap();
        assert_eq!(p.guid, 0x5000_0001);
        assert_eq!(p.instance_sequence, 0x5678);
        assert_eq!(p.obj_desc_sequence, 0xDEF0);
        let md = &p.model;
        assert_eq!(md.base_palette_id, Some(0x0400_007E));
        assert_eq!(md.sub_palettes.len(), 3);
        assert_eq!(md.sub_palettes[0].sub_palette_id, 0x0400_1FE3);
        assert_eq!(
            (md.sub_palettes[0].offset, md.sub_palettes[0].length),
            (24, 8)
        );
        assert_eq!(md.sub_palettes[1].sub_palette_id, 0x0400_02BA);
        assert_eq!(
            (md.sub_palettes[1].offset, md.sub_palettes[1].length),
            (0, 24)
        );
        assert_eq!(md.texture_changes.len(), 4);
        assert_eq!(md.texture_changes[0].part_index, 0);
        assert_eq!(md.texture_changes[0].old_texture, 0x0500_0100);
        assert_eq!(md.texture_changes[0].new_texture, 0x0500_0200);
        assert_eq!(md.texture_changes[3].part_index, 3);
        assert!(md.anim_part_changes.is_empty());

        assert_every_prefix_fails(&body, ObjDescEvent::parse);
        let mut longer = body.clone();
        longer.push(0);
        assert_eq!(
            ObjDescEvent::parse(&longer),
            Err(ProtoError::InvalidLength {
                what: "appearance change",
                actual: longer.len()
            })
        );
    }
}
