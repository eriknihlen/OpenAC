use ac_bits::Reader;

use crate::{Opcode, Position, Quaternion, Result, Vector3, expect_opcode};

/// Position flag: a velocity follows the orientation.
pub const POSITION_HAS_VELOCITY: u32 = 0x01;
/// Position flag: a placement id follows.
pub const POSITION_HAS_PLACEMENT_ID: u32 = 0x02;
/// Position flag: the object is on the ground.
pub const POSITION_IS_GROUNDED: u32 = 0x04;
/// Position flag: the orientation's w is omitted (zero).
pub const POSITION_ORIENTATION_HAS_NO_W: u32 = 0x08;
/// Position flag: the orientation's x is omitted (zero).
pub const POSITION_ORIENTATION_HAS_NO_X: u32 = 0x10;
/// Position flag: the orientation's y is omitted (zero).
pub const POSITION_ORIENTATION_HAS_NO_Y: u32 = 0x20;
/// Position flag: the orientation's z is omitted (zero).
pub const POSITION_ORIENTATION_HAS_NO_Z: u32 = 0x40;

/// An object moved ([`Opcode::UpdatePosition`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct UpdatePosition {
    /// The object.
    pub guid: u32,
    /// Flags (`POSITION_*`).
    pub flags: u32,
    /// The new position; omitted orientation components are zero.
    pub position: Position,
    /// Velocity, meters per second.
    pub velocity: Option<Vector3>,
    /// Placement id.
    pub placement_id: Option<u32>,
    /// The object's incarnation.
    pub instance_sequence: u16,
    /// Position update counter.
    pub position_sequence: u16,
    /// Teleport counter.
    pub teleport_sequence: u16,
    /// Forced-position counter.
    pub force_position_sequence: u16,
}

impl UpdatePosition {
    /// True when the object is on the ground.
    #[must_use]
    pub fn is_grounded(&self) -> bool {
        self.flags & POSITION_IS_GROUNDED != 0
    }

    /// Parse the body: guid, flags, cell and origin, the orientation
    /// components the flags keep (w, x, y, z), velocity, placement, then
    /// the instance, position, teleport and forced-position counters.
    /// Bytes after the counters are ignored.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::UpdatePosition)?;
        let guid = r.u32()?;
        let flags = r.u32()?;
        let cell_id = r.u32()?;
        let origin = Vector3::read(&mut r)?;
        let component = |r: &mut Reader<'_>, omitted: u32| -> Result<f32> {
            Ok(if flags & omitted == 0 { r.f32()? } else { 0.0 })
        };
        let orientation = Quaternion {
            w: component(&mut r, POSITION_ORIENTATION_HAS_NO_W)?,
            x: component(&mut r, POSITION_ORIENTATION_HAS_NO_X)?,
            y: component(&mut r, POSITION_ORIENTATION_HAS_NO_Y)?,
            z: component(&mut r, POSITION_ORIENTATION_HAS_NO_Z)?,
        };
        let velocity = if flags & POSITION_HAS_VELOCITY != 0 {
            Some(Vector3::read(&mut r)?)
        } else {
            None
        };
        let placement_id = if flags & POSITION_HAS_PLACEMENT_ID != 0 {
            Some(r.u32()?)
        } else {
            None
        };
        Ok(Self {
            guid,
            flags,
            position: Position {
                cell_id,
                origin,
                orientation,
            },
            velocity,
            placement_id,
            instance_sequence: r.u16()?,
            position_sequence: r.u16()?,
            teleport_sequence: r.u16()?,
            force_position_sequence: r.u16()?,
        })
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/UpdatePositionTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::ProtoError;
    use crate::test_util::assert_every_prefix_fails;

    fn start(guid: u32, flags: u32, cell_id: u32, origin: [f32; 3]) -> Writer {
        let mut w = Writer::new();
        w.u32(0xF748);
        w.u32(guid);
        w.u32(flags);
        w.u32(cell_id);
        for v in origin {
            w.f32(v);
        }
        w
    }

    fn sequences(w: &mut Writer) {
        w.u16(0x1001);
        w.u16(0x2002);
        w.u16(0x3003);
        w.u16(0x4004);
    }

    fn parse_ok(w: &Writer) -> UpdatePosition {
        assert_every_prefix_fails(w.as_slice(), UpdatePosition::parse);
        UpdatePosition::parse(w.as_slice()).unwrap()
    }

    #[test]
    fn rejects_wrong_opcode_and_truncation() {
        let mut body = vec![0u8; 64];
        body[..4].copy_from_slice(&0xCAFE_BABEu32.to_le_bytes());
        assert!(matches!(
            UpdatePosition::parse(&body),
            Err(ProtoError::WrongOpcode { .. })
        ));
        assert!(UpdatePosition::parse(&[]).is_err());
        assert!(UpdatePosition::parse(&[0; 3]).is_err());
    }

    #[test]
    fn all_four_rotation_components_present() {
        let mut w = start(0x1234_5678, 0, 0xA9B4_001A, [10.0, 20.0, 30.0]);
        for v in [1.0, 0.0, 0.0, 0.0] {
            w.f32(v);
        }
        sequences(&mut w);
        let p = parse_ok(&w);
        assert_eq!(p.guid, 0x1234_5678);
        assert_eq!(p.position.cell_id, 0xA9B4_001A);
        assert_eq!(
            p.position.origin,
            Vector3 {
                x: 10.0,
                y: 20.0,
                z: 30.0
            }
        );
        assert_eq!(p.position.orientation, Quaternion::IDENTITY);
        assert_eq!(p.velocity, None);
        assert_eq!(p.placement_id, None);
        assert!(!p.is_grounded());
        assert_eq!(
            (
                p.instance_sequence,
                p.position_sequence,
                p.teleport_sequence,
                p.force_position_sequence
            ),
            (0x1001, 0x2002, 0x3003, 0x4004)
        );
    }

    #[test]
    fn missing_rotation_components_are_zero() {
        let mut w = start(
            0xDEAD_BEEF,
            0x10 | 0x20 | 0x40,
            0x01BB_0001,
            [1.0, 2.0, 3.0],
        );
        w.f32(0.707);
        sequences(&mut w);
        let p = parse_ok(&w);
        assert_eq!(
            p.position.orientation,
            Quaternion {
                w: 0.707,
                x: 0.0,
                y: 0.0,
                z: 0.0
            }
        );
    }

    #[test]
    fn velocity_flag() {
        let mut w = start(0xAABB_CCDD, 0x01, 0x0102_0003, [5.0, 6.0, 7.0]);
        for v in [1.0, 0.0, 0.0, 0.0, 100.0, 200.0, 300.0] {
            w.f32(v);
        }
        sequences(&mut w);
        let p = parse_ok(&w);
        assert_eq!(
            p.velocity,
            Some(Vector3 {
                x: 100.0,
                y: 200.0,
                z: 300.0
            })
        );
    }

    #[test]
    fn placement_id_flag() {
        let mut w = start(0x1111_1111, 0x02, 0x0000_0001, [0.0, 0.0, 0.0]);
        for v in [1.0, 0.0, 0.0, 0.0] {
            w.f32(v);
        }
        w.u32(0x65);
        sequences(&mut w);
        assert_eq!(parse_ok(&w).placement_id, Some(0x65));
    }

    #[test]
    fn velocity_and_placement_and_grounded() {
        let mut w = start(
            0x2222_2222,
            0x01 | 0x02 | 0x04,
            0x0405_0607,
            [10.0, 20.0, 30.0],
        );
        for v in [1.0, 0.0, 0.0, 0.0, 1.0, 2.0, 3.0] {
            w.f32(v);
        }
        w.u32(42);
        sequences(&mut w);
        let p = parse_ok(&w);
        assert!(p.velocity.is_some());
        assert_eq!(p.placement_id, Some(42));
        assert!(p.is_grounded());
    }
}
