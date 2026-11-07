//! The barber: the server opens the window with the character's look
//! ([`crate::GameEventType::START_BARBER`]) and the client answers with the
//! look chosen ([`build_finish_barber`]). Both carry the same sixteen
//! words in the same order.

use ac_bits::{Reader, Writer};

use crate::wire::action;
use crate::{GameActionType, Result};

/// A look as the barber messages carry it: the appearance data ids (0:
/// none), the body setup and the two option words.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct BarberLook {
    /// The base palette.
    pub base_palette: u32,
    /// The head part (the hair style's head).
    pub head_object: u32,
    /// The hair texture now on the head.
    pub hair_texture: u32,
    /// The texture the hair texture replaces.
    pub default_hair_texture: u32,
    /// The eyes texture.
    pub eyes_texture: u32,
    /// The texture the eyes texture replaces.
    pub default_eyes_texture: u32,
    /// The nose texture.
    pub nose_texture: u32,
    /// The texture the nose texture replaces.
    pub default_nose_texture: u32,
    /// The mouth texture.
    pub mouth_texture: u32,
    /// The texture the mouth texture replaces.
    pub default_mouth_texture: u32,
    /// The skin palette.
    pub skin_palette: u32,
    /// The hair palette.
    pub hair_palette: u32,
    /// The eyes palette.
    pub eyes_palette: u32,
    /// The body setup.
    pub setup: u32,
    /// The first option: an Empyrean walks on the ground instead of
    /// floating (non-zero).
    pub option1: i32,
    /// The second option (always 0 from the client).
    pub option2: i32,
}

impl BarberLook {
    /// The size of the look on the wire.
    pub const WIRE_SIZE: usize = 64;

    /// Parse the start-barber event payload: the fourteen ids, then the two
    /// option words.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            base_palette: r.u32()?,
            head_object: r.u32()?,
            hair_texture: r.u32()?,
            default_hair_texture: r.u32()?,
            eyes_texture: r.u32()?,
            default_eyes_texture: r.u32()?,
            nose_texture: r.u32()?,
            default_nose_texture: r.u32()?,
            mouth_texture: r.u32()?,
            default_mouth_texture: r.u32()?,
            skin_palette: r.u32()?,
            hair_palette: r.u32()?,
            eyes_palette: r.u32()?,
            setup: r.u32()?,
            option1: r.i32()?,
            option2: r.i32()?,
        })
    }

    fn write(&self, w: &mut Writer) {
        for id in [
            self.base_palette,
            self.head_object,
            self.hair_texture,
            self.default_hair_texture,
            self.eyes_texture,
            self.default_eyes_texture,
            self.nose_texture,
            self.default_nose_texture,
            self.mouth_texture,
            self.default_mouth_texture,
            self.skin_palette,
            self.hair_palette,
            self.eyes_palette,
            self.setup,
        ] {
            w.u32(id);
        }
        w.i32(self.option1);
        w.i32(self.option2);
    }
}

/// The look chosen at the barber, sent on the UI queue.
#[must_use]
pub fn build_finish_barber(sequence: u32, look: &BarberLook) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::FINISH_BARBER);
    look.write(&mut w);
    w.into_vec()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    /// A look with every word different, in wire order (the barber
    /// messages' field order: base palette, head, hair texture and its
    /// default, eyes, nose and mouth textures each with its default, skin,
    /// hair and eyes palettes, setup, the two options).
    fn wire() -> Vec<u32> {
        vec![
            0x0400_007E,
            0x0100_4000,
            0x0500_1111,
            0x0500_1112,
            0x0500_2221,
            0x0500_2222,
            0x0500_3331,
            0x0500_3332,
            0x0500_4441,
            0x0500_4442,
            0x0400_1001,
            0x0400_2002,
            0x0400_3003,
            0x0200_0001,
            1,
            0,
        ]
    }

    fn look() -> BarberLook {
        BarberLook {
            base_palette: 0x0400_007E,
            head_object: 0x0100_4000,
            hair_texture: 0x0500_1111,
            default_hair_texture: 0x0500_1112,
            eyes_texture: 0x0500_2221,
            default_eyes_texture: 0x0500_2222,
            nose_texture: 0x0500_3331,
            default_nose_texture: 0x0500_3332,
            mouth_texture: 0x0500_4441,
            default_mouth_texture: 0x0500_4442,
            skin_palette: 0x0400_1001,
            hair_palette: 0x0400_2002,
            eyes_palette: 0x0400_3003,
            setup: 0x0200_0001,
            option1: 1,
            option2: 0,
        }
    }

    #[test]
    fn the_start_event_reads_sixteen_words_in_order() {
        let bytes = words(&wire());
        assert_eq!(bytes.len(), BarberLook::WIRE_SIZE);
        assert_eq!(BarberLook::parse(&bytes), Ok(look()));
        assert_every_prefix_fails(&bytes, BarberLook::parse);
    }

    #[test]
    fn finish_writes_the_envelope_then_the_same_sixteen_words() {
        let mut expected = vec![0xF7B1, 9, 0x0311];
        expected.extend(wire());
        let body = build_finish_barber(9, &look());
        assert_eq!(body.len(), 12 + BarberLook::WIRE_SIZE);
        assert_eq!(body, words(&expected));
    }

    #[test]
    fn a_look_round_trips() {
        let body = build_finish_barber(1, &look());
        assert_eq!(BarberLook::parse(&body[12..]), Ok(look()));
    }
}
