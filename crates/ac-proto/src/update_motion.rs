use ac_bits::Reader;

use crate::{MovementData, Opcode, Result, expect_opcode};

/// An object's motion changed ([`Opcode::UpdateMotion`]).
#[derive(Debug, Clone, PartialEq)]
pub struct UpdateMotion {
    /// The object.
    pub guid: u32,
    /// The object's incarnation.
    pub instance_sequence: u16,
    /// Movement update counter.
    pub movement_sequence: u16,
    /// Server-controlled movement era.
    pub server_control_sequence: u16,
    /// True when the motion was started by the object's own client.
    pub is_autonomous: bool,
    /// What the object is doing.
    pub movement: MovementData,
}

impl UpdateMotion {
    /// Parse the body: guid, instance counter, then the movement header
    /// (movement and server-control counters, autonomy byte, padding to 4)
    /// and the movement data. Bytes after the data are ignored.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::UpdateMotion)?;
        let guid = r.u32()?;
        let instance_sequence = r.u16()?;
        let movement_sequence = r.u16()?;
        let server_control_sequence = r.u16()?;
        let is_autonomous = r.u8()? != 0;
        r.align(4)?;
        let movement = MovementData::read(&mut r)?;
        Ok(Self {
            guid,
            instance_sequence,
            movement_sequence,
            server_control_sequence,
            is_autonomous,
            movement,
        })
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/UpdateMotionTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::assert_every_prefix_fails;
    use crate::{Movement, MovementData, ProtoError, Vector3};

    /// Opcode, guid, instance counter, then the movement header with every
    /// counter and the autonomy byte zero, then the movement-data header.
    fn header(guid: u32, movement_type: u8, motion_flags: u8, style: u16) -> Writer {
        let mut w = Writer::new();
        w.u32(0xF74C);
        w.u32(guid);
        w.u16(0);
        w.pad(6);
        w.u8(movement_type);
        w.u8(motion_flags);
        w.u16(style);
        w
    }

    fn parse_ok(w: &Writer) -> UpdateMotion {
        assert_every_prefix_fails(w.as_slice(), UpdateMotion::parse);
        UpdateMotion::parse(w.as_slice()).unwrap()
    }

    fn state(m: &MovementData) -> &crate::InterpretedMotionState {
        m.interpreted().expect("interpreted state")
    }

    #[test]
    fn rejects_wrong_opcode_and_truncation() {
        let mut body = vec![0u8; 32];
        body[..4].copy_from_slice(&0xDEAD_BEEFu32.to_le_bytes());
        assert!(matches!(
            UpdateMotion::parse(&body),
            Err(ProtoError::WrongOpcode { .. })
        ));
        assert!(UpdateMotion::parse(&[0; 3]).is_err());
        assert!(UpdateMotion::parse(&[]).is_err());
    }

    #[test]
    fn stance_only_when_forward_command_flag_unset() {
        let mut w = header(0x1234_5678, 0, 0, 0x0042);
        w.u32(0x1);
        w.u16(0x0005);
        w.align(4);
        let m = parse_ok(&w);
        assert_eq!(m.guid, 0x1234_5678);
        assert_eq!(m.movement.stance(), 0x0005);
        assert_eq!(state(&m.movement).forward_command, None);
    }

    #[test]
    fn stance_and_forward_command() {
        let mut w = header(0xABCD_EF01, 0, 0, 0);
        w.u32(0x3);
        w.u16(0x000D);
        w.u16(0x0007);
        let m = parse_ok(&w);
        assert_eq!(m.movement.stance(), 0x000D);
        assert_eq!(state(&m.movement).forward_command, Some(0x0007));
    }

    #[test]
    fn no_flags_keeps_outer_stance() {
        let mut w = header(0x5555_5555, 0, 0, 0x00AA);
        w.u32(0);
        let m = parse_ok(&w);
        assert_eq!(m.movement.stance(), 0x00AA);
        assert_eq!(state(&m.movement).forward_command, None);
    }

    #[test]
    fn forward_speed_when_speed_flag_set() {
        let mut w = header(0x1A2B_3C4D, 0, 0, 0);
        w.u32(0x7);
        w.u16(0x003D);
        w.u16(0x0007);
        w.f32(1.5);
        let m = parse_ok(&w);
        let s = state(&m.movement);
        assert_eq!(m.movement.stance(), 0x003D);
        assert_eq!(s.forward_command, Some(0x0007));
        assert_eq!(s.forward_speed, Some(1.5));
    }

    #[test]
    fn commands_list_wave() {
        let mut w = header(0xDEAD_BEEF, 0, 0, 0);
        w.u32(0x83); // flags 0x3, one action
        w.u16(0x003D);
        w.u16(0x0003);
        w.u16(0x0087);
        w.u16(0x0001);
        w.f32(1.0);
        let m = parse_ok(&w);
        let s = state(&m.movement);
        assert_eq!(m.movement.stance(), 0x003D);
        assert_eq!(s.forward_command, Some(0x0003));
        assert_eq!(
            s.commands,
            [crate::MotionItem {
                command: 0x0087,
                packed_sequence: 1,
                speed: 1.0
            }]
        );
    }

    #[test]
    fn action_count_is_five_bits() {
        // Retail masks the count with 0x1F: bit 12 of the word is not part
        // of it, so 0x1080 means one action, not 33.
        let mut w = header(1, 0, 0, 0);
        w.u32(0x1080);
        w.u16(0x0087);
        w.u16(1);
        w.f32(1.0);
        let m = parse_ok(&w);
        assert_eq!(state(&m.movement).commands.len(), 1);
    }

    #[test]
    fn truncated_move_to_payload_is_an_error() {
        // OpenAC's HandlesNonInvalidMovementType_GracefullyReturnsOuterStance
        // keeps the header of a move-to with no payload; here the message is
        // rejected.
        let w = header(0x9999_9999, 7, 0, 0x00CC);
        assert!(UpdateMotion::parse(w.as_slice()).is_err());
    }

    #[test]
    fn move_to_position_speed_and_run_rate() {
        let mut w = header(0x8000_1234, 7, 0, 0x003D);
        w.u32(0xA8B4_000E);
        for v in [10.0, 20.0, 30.0] {
            w.f32(v);
        }
        let flags = 0x1 | 0x2 | 0x200;
        w.u32(flags);
        for v in [0.6, 0.0, f32::MAX, 1.25, 15.0, 90.0, 1.5] {
            w.f32(v);
        }
        let m = parse_ok(&w);
        let mv = &m.movement;
        assert_eq!(mv.movement_type, 7);
        assert!(mv.is_server_controlled_move_to());
        assert_eq!(mv.stance(), 0x003D);
        assert!(mv.interpreted().is_none());
        assert!(mv.move_to_can_run());
        assert!(mv.move_towards());
        assert_eq!(
            mv.movement,
            Movement::MoveToPosition {
                path: crate::MoveToPath {
                    origin_cell_id: 0xA8B4_000E,
                    origin: Vector3 {
                        x: 10.0,
                        y: 20.0,
                        z: 30.0
                    },
                    bitfield: flags,
                    distance_to_object: 0.6,
                    min_distance: 0.0,
                    fail_distance: f32::MAX,
                    speed: 1.25,
                    walk_run_threshold: 15.0,
                    desired_heading: 90.0,
                    run_rate: 1.5,
                }
            }
        );
    }

    #[test]
    fn attack_high_1_as_action_forward_command() {
        let mut w = header(0x8000_03B5, 0, 0, 0x003C);
        w.u32(0x06);
        w.u16(0x0062);
        w.f32(1.25);
        w.align(4);
        let m = parse_ok(&w);
        assert_eq!(m.movement.movement_type, 0);
        assert!(!m.movement.is_server_controlled_move_to());
        let s = state(&m.movement);
        assert_eq!(s.forward_command, Some(0x0062));
        assert_eq!(s.forward_speed, Some(1.25));
    }

    #[test]
    fn sequence_numbers_and_autonomy_flag() {
        let mut w = Writer::new();
        w.u32(0xF74C);
        w.u32(0x5000_1234);
        w.u16(0x0102);
        w.u16(0x0304);
        w.u16(0x0506);
        w.u8(1);
        w.pad(1);
        w.bytes(&[0, 0]);
        w.u16(0x003D);
        w.u32(0);
        let m = parse_ok(&w);
        assert_eq!(m.instance_sequence, 0x0102);
        assert_eq!(m.movement_sequence, 0x0304);
        assert_eq!(m.server_control_sequence, 0x0506);
        assert!(m.is_autonomous);
    }

    #[test]
    fn move_to_object_target_guid_and_origin() {
        let mut w = header(0x8000_4321, 6, 0, 0x003D);
        w.u32(0x8000_1234);
        w.u32(0xA8B4_000E);
        for v in [5.0, 6.0, 7.0] {
            w.f32(v);
        }
        w.u32(0x1 | 0x2 | 0x200);
        for v in [0.6, 0.0, f32::MAX, 1.0, 15.0, 1.57, 1.25] {
            w.f32(v);
        }
        assert_eq!(w.len(), 72);
        let m = parse_ok(&w);
        assert_eq!(m.movement.movement_type, 6);
        assert!(m.movement.is_server_controlled_move_to());
        let Movement::MoveToObject { target, path } = m.movement.movement else {
            panic!("expected a move to an object");
        };
        assert_eq!(target, 0x8000_1234);
        assert_eq!(path.origin_cell_id, 0xA8B4_000E);
        assert_eq!(
            path.origin,
            Vector3 {
                x: 5.0,
                y: 6.0,
                z: 7.0
            }
        );
        assert_eq!(path.run_rate, 1.25);
    }

    #[test]
    fn turn_to_object_guid_wire_heading_and_params() {
        let mut w = header(0x8000_5678, 8, 0, 0x003D);
        w.u32(0x8000_9999);
        w.f32(42.0);
        let flags = 0x1 | 0x2 | 0x200;
        w.u32(flags);
        w.f32(1.5);
        w.f32(199.0);
        assert_eq!(w.len(), 40);
        let m = parse_ok(&w);
        assert!(m.movement.is_server_controlled_turn_to());
        assert!(!m.movement.is_server_controlled_move_to());
        assert_eq!(
            m.movement.movement,
            Movement::TurnToObject {
                target: 0x8000_9999,
                wire_heading: 42.0,
                params: crate::TurnToParams {
                    bitfield: flags,
                    speed: 1.5,
                    desired_heading: 199.0
                }
            }
        );
    }

    #[test]
    fn turn_to_object_unresolvable_fallback_keeps_both_headings() {
        let mut w = header(0x8000_AAAA, 8, 0, 0);
        w.u32(0xDEAD_BEEF);
        w.f32(270.0);
        w.u32(0x1);
        w.f32(1.0);
        w.f32(0.0);
        let m = parse_ok(&w);
        let Movement::TurnToObject {
            target,
            wire_heading,
            params,
        } = m.movement.movement
        else {
            panic!("expected a turn to an object");
        };
        assert_eq!(target, 0xDEAD_BEEF);
        assert_eq!(wire_heading, 270.0);
        assert_eq!(params.desired_heading, 0.0);
    }

    #[test]
    fn turn_to_heading_three_word_form_only() {
        let flags = 0x2 | 0x800;
        let mut w = header(0x8000_BBBB, 9, 0, 0x003D);
        w.u32(flags);
        w.f32(2.0);
        w.f32(315.0);
        assert_eq!(w.len(), 32);
        let m = parse_ok(&w);
        assert!(m.movement.is_server_controlled_turn_to());
        assert_eq!(
            m.movement.movement,
            Movement::TurnToHeading {
                params: crate::TurnToParams {
                    bitfield: flags,
                    speed: 2.0,
                    desired_heading: 315.0
                }
            }
        );
    }

    #[test]
    fn turn_to_heading_bitfield_round_trips() {
        for bitfield in [0, 0x1 | 0x2, 0x10, 0x3FFFF] {
            let mut w = header(0x8000_1111, 9, 0, 0);
            w.u32(bitfield);
            w.f32(1.0);
            w.f32(0.0);
            let m = UpdateMotion::parse(w.as_slice()).unwrap();
            let Movement::TurnToHeading { params } = m.movement.movement else {
                panic!("expected a turn to a heading");
            };
            assert_eq!(params.bitfield, bitfield);
        }
    }

    #[test]
    fn move_to_position_path_keeps_all_seven_parameters() {
        let flags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x200 | 0x400;
        let mut w = header(0x8000_2222, 7, 0, 0x003D);
        w.u32(0xA8B4_000E);
        for v in [11.0, 22.0, 33.0] {
            w.f32(v);
        }
        w.u32(flags);
        for v in [0.6, 0.1, 50.0, 1.25, 15.0, 123.0, 2.75] {
            w.f32(v);
        }
        let m = parse_ok(&w);
        let p = m.movement.move_to_path().unwrap();
        assert_eq!(p.bitfield, flags);
        assert!(m.movement.can_charge());
        assert_eq!(
            (
                p.distance_to_object,
                p.min_distance,
                p.fail_distance,
                p.speed,
                p.walk_run_threshold,
                p.desired_heading,
                p.run_rate
            ),
            (0.6, 0.1, 50.0, 1.25, 15.0, 123.0, 2.75)
        );
    }

    #[test]
    fn sticky_guid_when_motion_flag_set() {
        let mut w = header(0x8000_3333, 0, 0x1, 0x003D);
        w.u32(0);
        w.u32(0x8000_4444);
        let m = parse_ok(&w);
        let Movement::Interpreted { sticky_object, .. } = m.movement.movement else {
            panic!("expected an interpreted state");
        };
        assert_eq!(sticky_object, Some(0x8000_4444));
    }

    #[test]
    fn no_sticky_guid_when_motion_flag_clear() {
        let mut w = header(0x8000_5555, 0, 0, 0x003D);
        w.u32(0);
        let m = parse_ok(&w);
        let Movement::Interpreted { sticky_object, .. } = m.movement.movement else {
            panic!("expected an interpreted state");
        };
        assert_eq!(sticky_object, None);
    }

    #[test]
    fn sticky_guid_follows_the_padded_state() {
        // OpenAC's ParsesStickyGuidTrailer_CursorHonesty puts the guid
        // straight after a 2-byte forward command. Retail and the server pad
        // the interpreted state to 4 bytes first, so the
        // guid sits after two padding bytes.
        let mut w = header(0x8000_6666, 0, 0x1, 0);
        w.u32(0x2);
        w.u16(0x0007);
        w.align(4);
        w.u32(0x8000_7777);
        let m = parse_ok(&w);
        assert_eq!(state(&m.movement).forward_command, Some(0x0007));
        let Movement::Interpreted { sticky_object, .. } = m.movement.movement else {
            panic!("expected an interpreted state");
        };
        assert_eq!(sticky_object, Some(0x8000_7777));
    }

    #[test]
    fn standing_long_jump_flag() {
        let mut w = header(1, 0, 0x2, 0);
        w.u32(0);
        assert!(parse_ok(&w).movement.standing_long_jump());
    }

    #[test]
    fn unknown_movement_type_reads_only_the_header() {
        let w = header(1, 3, 0, 0x003D);
        let m = parse_ok(&w);
        assert_eq!(m.movement.movement, Movement::Other);
        assert_eq!(m.movement.stance(), 0x003D);
    }
}
