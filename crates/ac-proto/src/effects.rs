//! Effects the server asks the client to play: sounds, physics scripts by
//! id or by type, and environment changes.

use ac_bits::Reader;

use crate::{Opcode, ProtoError, Result};

/// An object plays a sound ([`Opcode::SoundEvent`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SoundEvent {
    /// The object.
    pub guid: u32,
    /// Sound type.
    pub sound_type: u32,
    /// Volume as sent, not clamped.
    pub volume: f32,
}

impl SoundEvent {
    /// Parse the body: opcode, object, sound type, volume. Bytes after the
    /// volume are ignored.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        crate::expect_opcode(&mut r, Opcode::SoundEvent)?;
        Ok(Self {
            guid: r.u32()?,
            sound_type: r.u32()?,
            volume: r.f32()?,
        })
    }
}

/// An object plays a physics script by data id ([`Opcode::PlayScript`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlayScript {
    /// The object.
    pub guid: u32,
    /// The script's data id (zero is kept).
    pub script_id: u32,
}

impl PlayScript {
    /// Body size: opcode, object, script.
    pub const WIRE_SIZE: usize = 12;

    /// Parse the body, which must be exactly [`PlayScript::WIRE_SIZE`]
    /// bytes.
    pub fn parse(body: &[u8]) -> Result<Self> {
        check_exact(body, Self::WIRE_SIZE, "play script")?;
        let mut r = Reader::new(body);
        crate::expect_opcode(&mut r, Opcode::PlayScript)?;
        Ok(Self {
            guid: r.u32()?,
            script_id: r.u32()?,
        })
    }
}

/// An object plays the physics script of a type at an intensity
/// ([`Opcode::PlayScriptType`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlayScriptType {
    /// The object.
    pub guid: u32,
    /// Script type.
    pub script_type: u32,
    /// Intensity, kept as sent (NaN included).
    pub intensity: f32,
}

impl PlayScriptType {
    /// Body size: opcode, object, type, intensity.
    pub const WIRE_SIZE: usize = 16;

    /// Parse the body, which must be exactly [`PlayScriptType::WIRE_SIZE`]
    /// bytes.
    pub fn parse(body: &[u8]) -> Result<Self> {
        check_exact(body, Self::WIRE_SIZE, "play script type")?;
        let mut r = Reader::new(body);
        crate::expect_opcode(&mut r, Opcode::PlayScriptType)?;
        Ok(Self {
            guid: r.u32()?,
            script_type: r.u32()?,
            intensity: r.f32()?,
        })
    }
}

/// The server changed the environment: a fog preset or a sound cue
/// ([`Opcode::AdminEnvirons`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AdminEnvirons {
    /// The environment change type.
    pub environ_type: u32,
}

impl AdminEnvirons {
    /// Parse the body: opcode, change type.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        crate::expect_opcode(&mut r, Opcode::AdminEnvirons)?;
        Ok(Self {
            environ_type: r.u32()?,
        })
    }
}

fn check_exact(body: &[u8], size: usize, what: &'static str) -> Result<()> {
    if body.len() == size {
        Ok(())
    } else {
        Err(ProtoError::InvalidLength {
            what,
            actual: body.len(),
        })
    }
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `SoundEventTests.cs` and `PlayScriptGoldenTests.cs`, and the
    //! environment branch of `WorldSession.ProcessDatagram`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    fn sound(opcode: u32, guid: u32, sound_type: u32, volume: f32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(opcode);
        w.u32(guid);
        w.u32(sound_type);
        w.f32(volume);
        w.into_vec()
    }

    #[test]
    fn sound_event() {
        let body = sound(0xF750, 0x8000_1234, 0x03, 0.5);
        assert_eq!(
            SoundEvent::parse(&body),
            Ok(SoundEvent {
                guid: 0x8000_1234,
                sound_type: 3,
                volume: 0.5
            })
        );
        for sound_type in [0x30, 0x51, 0x8F, 0x91, 0x97, 0xCC] {
            assert_eq!(
                SoundEvent::parse(&sound(0xF750, 1, sound_type, 1.0))
                    .unwrap()
                    .sound_type,
                sound_type
            );
        }
        for volume in [0.0, 1.0, 2.5] {
            assert_eq!(
                SoundEvent::parse(&sound(0xF750, 1, 1, volume))
                    .unwrap()
                    .volume,
                volume
            );
        }
        let mut padded = sound(0xF750, 0xAA, 0x37, 0.25);
        padded.extend([0; 8]);
        assert_eq!(SoundEvent::parse(&padded).unwrap().sound_type, 0x37);
        assert!(SoundEvent::parse(&sound(0xF751, 1, 1, 1.0)).is_err());
        assert_every_prefix_fails(&body, SoundEvent::parse);
    }

    #[test]
    fn play_script_by_id() {
        for (guid, script) in [
            (0x5000_0001, 0x0D00_0001),
            (0x7C95_B01A, 0),
            (u32::MAX, u32::MAX),
        ] {
            let body = words(&[0xF754, guid, script]);
            assert_eq!(body.len(), PlayScript::WIRE_SIZE);
            assert_eq!(
                PlayScript::parse(&body),
                Ok(PlayScript {
                    guid,
                    script_id: script
                })
            );
        }
        assert!(PlayScript::parse(&words(&[0xF755, 1, 2])).is_err());
        let mut long = words(&[0xF754, 1, 2]);
        long.push(0);
        assert!(matches!(
            PlayScript::parse(&long),
            Err(ProtoError::InvalidLength { .. })
        ));
        assert_every_prefix_fails(&words(&[0xF754, 1, 2]), PlayScript::parse);
    }

    #[test]
    fn play_script_by_type() {
        for (guid, script_type, intensity) in [
            (0x5000_0001, 0x21, 1.0f32),
            (0x7C95_B01A, 0x83, 0.25),
            (0x8001_14C0, 0x01, 0.0),
            (0xA9B4_0001, 0xDEAD_BEEF, -3.5),
        ] {
            let body = sound(0xF755, guid, script_type, intensity);
            assert_eq!(
                PlayScriptType::parse(&body),
                Ok(PlayScriptType {
                    guid,
                    script_type,
                    intensity
                })
            );
        }
        let nan = PlayScriptType::parse(&sound(0xF755, 1, 0x21, f32::NAN)).unwrap();
        assert!(nan.intensity.is_nan());
        assert!(PlayScriptType::parse(&sound(0xF754, 1, 2, 1.0)).is_err());
        assert_every_prefix_fails(&sound(0xF755, 1, 2, 1.0), PlayScriptType::parse);
    }

    // OpenAC `ProjectileVfxPacketFixtures.DirectF754`, byte for byte.
    const DIRECT_F754: [u8; 12] = [
        0x54, 0xF7, 0x00, 0x00, 0x42, 0x00, 0x00, 0x50, 0x99, 0x00, 0x00, 0x33,
    ];

    // OpenAC `ProjectileVfxPacketFixtures.TypedF755UnknownNaN`: an unknown
    // script type and a NaN intensity with a payload.
    const TYPED_F755_UNKNOWN_NAN: [u8; 16] = [
        0x55, 0xF7, 0x00, 0x00, 0x42, 0x00, 0x00, 0x50, 0xA5, 0xA5, 0xA5, 0xA5, 0x34, 0x12, 0xC0,
        0x7F,
    ];

    /// OpenAC `ProjectileVfxPacketFixtureTests.DirectF754_PreservesExactTwelveByteWireShape`.
    #[test]
    fn direct_f754_preserves_the_exact_twelve_byte_wire_shape() {
        assert_eq!(
            PlayScript::parse(&DIRECT_F754),
            Ok(PlayScript {
                guid: 0x5000_0042,
                script_id: 0x3300_0099
            })
        );
        assert!(PlayScript::parse(&DIRECT_F754[..11]).is_err());
        let mut long = DIRECT_F754.to_vec();
        long.push(0);
        assert!(PlayScript::parse(&long).is_err());
    }

    /// OpenAC `ProjectileVfxPacketFixtureTests.TypedF755_PreservesUnknownTypeAndNaNPayloadBits`
    /// and `TypedF755_PreservesInfiniteIntensityBits`.
    #[test]
    fn typed_f755_keeps_an_unknown_type_and_the_intensity_bits() {
        let parsed = PlayScriptType::parse(&TYPED_F755_UNKNOWN_NAN).unwrap();
        assert_eq!(parsed.guid, 0x5000_0042);
        assert_eq!(parsed.script_type, 0xA5A5_A5A5);
        assert_eq!(parsed.intensity.to_bits(), 0x7FC0_1234);
        assert!(PlayScriptType::parse(&TYPED_F755_UNKNOWN_NAN[..15]).is_err());
        let mut long = TYPED_F755_UNKNOWN_NAN.to_vec();
        long.push(0);
        assert!(PlayScriptType::parse(&long).is_err());
        for bits in [0x7F80_0000u32, 0xFF80_0000] {
            let mut packet = TYPED_F755_UNKNOWN_NAN;
            packet[12..].copy_from_slice(&bits.to_le_bytes());
            assert_eq!(
                PlayScriptType::parse(&packet).unwrap().intensity.to_bits(),
                bits
            );
        }
    }

    #[test]
    fn admin_environs() {
        assert_eq!(
            AdminEnvirons::parse(&words(&[0xEA60, 0x65])),
            Ok(AdminEnvirons { environ_type: 0x65 })
        );
        assert_every_prefix_fails(&words(&[0xEA60, 0x65]), AdminEnvirons::parse);
    }
}
