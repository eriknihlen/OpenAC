//! The small fixed-layout object messages: delete, pickup, parent, physics
//! state, velocity and the player's teleport notice. Each reads its fields
//! after the opcode and ignores any bytes after them.

use ac_bits::Reader;

use crate::{Opcode, Result, Vector3, expect_opcode};

/// An object is destroyed ([`Opcode::DeleteObject`]): one exact
/// incarnation of it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DeleteObject {
    /// The object.
    pub guid: u32,
    /// The incarnation being destroyed.
    pub instance_sequence: u16,
}

impl DeleteObject {
    /// Parse the body: guid, instance counter.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::DeleteObject)?;
        Ok(Self {
            guid: r.u32()?,
            instance_sequence: r.u16()?,
        })
    }
}

/// An object leaves the world into a container ([`Opcode::PickupEvent`]).
/// Unlike a delete, the object itself lives on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PickupEvent {
    /// The object.
    pub guid: u32,
    /// The object's incarnation.
    pub instance_sequence: u16,
    /// Position update counter.
    pub position_sequence: u16,
}

impl PickupEvent {
    /// Parse the body: guid, instance counter, position counter.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::PickupEvent)?;
        Ok(Self {
            guid: r.u32()?,
            instance_sequence: r.u16()?,
            position_sequence: r.u16()?,
        })
    }
}

/// An object is attached to a parent, such as a wielded weapon
/// ([`Opcode::ParentEvent`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ParentEvent {
    /// The parent.
    pub parent_guid: u32,
    /// The attached object.
    pub child_guid: u32,
    /// Attachment point on the parent.
    pub parent_location: u32,
    /// Placement of the child.
    pub placement_id: u32,
    /// The parent's incarnation.
    pub parent_instance_sequence: u16,
    /// The child's position update counter.
    pub child_position_sequence: u16,
}

impl ParentEvent {
    /// Parse the body: parent, child, location, placement, then the two
    /// counters.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::ParentEvent)?;
        Ok(Self {
            parent_guid: r.u32()?,
            child_guid: r.u32()?,
            parent_location: r.u32()?,
            placement_id: r.u32()?,
            parent_instance_sequence: r.u16()?,
            child_position_sequence: r.u16()?,
        })
    }
}

/// An object's physics state bits changed ([`Opcode::SetState`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SetState {
    /// The object.
    pub guid: u32,
    /// The new physics state bits.
    pub physics_state: u32,
    /// The object's incarnation.
    pub instance_sequence: u16,
    /// State update counter.
    pub state_sequence: u16,
}

impl SetState {
    /// Parse the body: guid, state, instance counter, state counter.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::SetState)?;
        Ok(Self {
            guid: r.u32()?,
            physics_state: r.u32()?,
            instance_sequence: r.u16()?,
            state_sequence: r.u16()?,
        })
    }
}

/// An object's velocity and spin changed ([`Opcode::VectorUpdate`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct VectorUpdate {
    /// The object.
    pub guid: u32,
    /// Velocity, meters per second.
    pub velocity: Vector3,
    /// Angular velocity, radians per second.
    pub omega: Vector3,
    /// The object's incarnation.
    pub instance_sequence: u16,
    /// Velocity update counter.
    pub vector_sequence: u16,
}

impl VectorUpdate {
    /// Parse the body: guid, velocity, omega, instance counter, vector
    /// counter.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::VectorUpdate)?;
        Ok(Self {
            guid: r.u32()?,
            velocity: Vector3::read(&mut r)?,
            omega: Vector3::read(&mut r)?,
            instance_sequence: r.u16()?,
            vector_sequence: r.u16()?,
        })
    }
}

/// The server is moving the player through a portal
/// ([`Opcode::PlayerTeleport`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlayerTeleport {
    /// Teleport counter.
    pub teleport_sequence: u16,
}

impl PlayerTeleport {
    /// Parse the body: the teleport counter.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::PlayerTeleport)?;
        Ok(Self {
            teleport_sequence: r.u16()?,
        })
    }
}

#[cfg(test)]
mod tests {
    use ac_bits::Writer;

    use super::*;
    use crate::ProtoError;
    use crate::test_util::{assert_every_prefix_fails, words};

    fn wrong_opcode<T: core::fmt::Debug>(parse: impl Fn(&[u8]) -> Result<T>, len: usize) {
        let mut body = vec![0u8; len];
        body[..4].copy_from_slice(&0xDEAD_BEEFu32.to_le_bytes());
        assert!(matches!(parse(&body), Err(ProtoError::WrongOpcode { .. })));
    }

    #[test]
    fn delete_object() {
        // OpenAC tests/AcDream.Core.Net.Tests/Messages/DeleteObjectTests.cs.
        wrong_opcode(DeleteObject::parse, 12);
        assert!(DeleteObject::parse(&[]).is_err());
        assert!(DeleteObject::parse(&[0; 9]).is_err());
        let mut w = Writer::new();
        w.u32(0xF747);
        w.u32(0x8000_0439);
        w.u16(0x1234);
        assert_every_prefix_fails(w.as_slice(), DeleteObject::parse);
        w.u16(0);
        assert_eq!(
            DeleteObject::parse(w.as_slice()),
            Ok(DeleteObject {
                guid: 0x8000_0439,
                instance_sequence: 0x1234
            })
        );
    }

    #[test]
    fn pickup_event() {
        // OpenAC tests/AcDream.Core.Net.Tests/Messages/PickupEventTests.cs.
        wrong_opcode(PickupEvent::parse, 12);
        assert!(PickupEvent::parse(&[]).is_err());
        assert!(PickupEvent::parse(&[0; 11]).is_err());
        let mut w = Writer::new();
        w.u32(0xF74A);
        w.u32(0x8000_0727);
        w.u16(0x1234);
        w.u16(0x5678);
        assert_every_prefix_fails(w.as_slice(), PickupEvent::parse);
        assert_eq!(
            PickupEvent::parse(w.as_slice()),
            Ok(PickupEvent {
                guid: 0x8000_0727,
                instance_sequence: 0x1234,
                position_sequence: 0x5678
            })
        );
    }

    #[test]
    fn parent_event_retail_wire_order() {
        // OpenAC tests/AcDream.Core.Net.Tests/Messages/ParentEventTests.cs.
        let mut w = Writer::new();
        w.u32(0xF749);
        w.u32(0x5000_0001);
        w.u32(0x6000_0002);
        w.u32(2);
        w.u32(3);
        w.u16(0x1234);
        w.u16(0x5678);
        assert_every_prefix_fails(w.as_slice(), ParentEvent::parse);
        assert_eq!(
            ParentEvent::parse(w.as_slice()),
            Ok(ParentEvent {
                parent_guid: 0x5000_0001,
                child_guid: 0x6000_0002,
                parent_location: 2,
                placement_id: 3,
                parent_instance_sequence: 0x1234,
                child_position_sequence: 0x5678
            })
        );
        assert!(ParentEvent::parse(&[0; 23]).is_err());
        let mut body = w.into_vec();
        body[..4].copy_from_slice(&0xF748u32.to_le_bytes());
        assert!(matches!(
            ParentEvent::parse(&body),
            Err(ProtoError::WrongOpcode { .. })
        ));
    }

    #[test]
    fn set_state() {
        // OpenAC tests/AcDream.Core.Net.Tests/Messages/SetStateTests.cs.
        let mut w = Writer::new();
        w.u32(0xF74B);
        w.u32(0x000F_4244);
        w.u32(0x0000_0004);
        w.u16(355);
        w.u16(42);
        assert_every_prefix_fails(w.as_slice(), SetState::parse);
        assert_eq!(
            SetState::parse(w.as_slice()),
            Ok(SetState {
                guid: 0x000F_4244,
                physics_state: 0x4,
                instance_sequence: 355,
                state_sequence: 42
            })
        );
        assert!(SetState::parse(&[0; 10]).is_err());
        let mut body = vec![0u8; 16];
        body[..4].copy_from_slice(&0xF74Cu32.to_le_bytes());
        assert!(matches!(
            SetState::parse(&body),
            Err(ProtoError::WrongOpcode { .. })
        ));
    }

    /// The server's writer for the vector update: guid, velocity, omega,
    /// then the two counters.
    fn golden_vector(
        guid: u32,
        velocity: [f32; 3],
        omega: [f32; 3],
        inst: u16,
        vec: u16,
    ) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xF74E);
        w.u32(guid);
        for v in velocity.into_iter().chain(omega) {
            w.f32(v);
        }
        w.u16(inst);
        w.u16(vec);
        w.into_vec()
    }

    #[test]
    fn vector_update_golden_bytes() {
        // OpenAC tests/AcDream.Core.Net.Tests/Messages/VectorUpdateGoldenTests.cs.
        type Case = (u32, [f32; 3], [f32; 3], u16, u16);
        let cases: [Case; 4] = [
            (0x5000_0001, [0.0, 0.0, 6.1], [0.0; 3], 355, 42),
            (0x7C95_B01A, [2.94, -1.25, 0.0], [0.0, 0.0, 1.5], 1, 2),
            (0x8001_14C0, [0.0; 3], [0.0; 3], 0, 0),
            (
                0xA9B4_0001,
                [-1.0, -2.0, -3.0],
                [-4.0, -5.0, -6.0],
                65535,
                65534,
            ),
        ];
        for (guid, v, o, inst, vec) in cases {
            let body = golden_vector(guid, v, o, inst, vec);
            assert_eq!(body.len(), 36);
            assert_every_prefix_fails(&body, VectorUpdate::parse);
            let p = VectorUpdate::parse(&body).unwrap();
            assert_eq!(p.guid, guid);
            assert_eq!(
                p.velocity,
                Vector3 {
                    x: v[0],
                    y: v[1],
                    z: v[2]
                }
            );
            assert_eq!(
                p.omega,
                Vector3 {
                    x: o[0],
                    y: o[1],
                    z: o[2]
                }
            );
            assert_eq!((p.instance_sequence, p.vector_sequence), (inst, vec));
        }
        let mut body = golden_vector(1, [0.0; 3], [0.0; 3], 0, 0);
        body[..4].copy_from_slice(&0xF74Cu32.to_le_bytes());
        assert!(matches!(
            VectorUpdate::parse(&body),
            Err(ProtoError::WrongOpcode { .. })
        ));
    }

    #[test]
    fn player_teleport() {
        // OpenAC src/AcDream.Core.Net/WorldSession.cs: a body of at least
        // six bytes carries the teleport counter after the opcode.
        let body = [0x51, 0xF7, 0, 0, 0x34, 0x12];
        assert_every_prefix_fails(&body, PlayerTeleport::parse);
        assert_eq!(
            PlayerTeleport::parse(&words(&[0xF751, 0x0000_1234])),
            Ok(PlayerTeleport {
                teleport_sequence: 0x1234
            })
        );
    }
}
