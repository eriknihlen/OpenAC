//! A movement the server drives: it names a thing to walk to (or a place),
//! how close to stop and how fast, or a thing or heading to turn to, and the
//! body carries it out on its own.
//!
//! For the local character this is not a nicety: asked to use something out
//! of reach, the server sends the character to it and waits; a character
//! that never walks is told fifteen seconds later that the use is done,
//! with nothing done. A thing nothing simulates is followed through the
//! same minimal stand-in body a walk the character starts gets (where it
//! is, standing still); only when it has no body here at all (unknown,
//! hidden, held, or outside the resident landblocks) does the walk fall
//! back to the place the order names, as the original client does.

use ac_motion::{MovementParameters, MovementType};
use ac_physics::movement::MovementStruct;
use ac_physics::{CellPosition, Quat, Vec3};
use ac_proto::{Movement, MovementData};

/// The width and height of a thing's body, metres (the gap that ends a walk
/// is measured between two cylinders).
pub type BodyShape = (f32, f32);

/// What one movement asks the body to do (`None` when it asks for no
/// server-driven movement, or the frame to measure its place in is not
/// established), with the run rate it carries.
///
/// `target_body` answers a thing's body shape, `None` when it has none to
/// follow; `frame_offset` the corner of a cell's landblock in the body's
/// frame, `None` when there is no frame; `cell_id` is the body's cell.
pub fn resolve(
    data: &MovementData,
    target_body: impl Fn(u32) -> Option<BodyShape>,
    frame_offset: impl Fn(u32) -> Option<Vec3>,
    cell_id: u32,
) -> Option<(MovementStruct, Option<f32>)> {
    match &data.movement {
        Movement::MoveToObject { path, .. } | Movement::MoveToPosition { path } => {
            let params = MovementParameters::from_wire(
                path.bitfield,
                path.distance_to_object,
                path.min_distance,
                path.fail_distance,
                path.speed,
                path.walk_run_threshold,
                path.desired_heading,
            );
            let run_rate = Some(path.run_rate);
            if let Movement::MoveToObject { target, .. } = &data.movement
                && let Some((radius, height)) = target_body(*target)
            {
                return Some((
                    MovementStruct {
                        kind: MovementType::MoveToObject,
                        object_id: *target,
                        top_level_id: *target,
                        radius,
                        height,
                        params: Some(params),
                        ..MovementStruct::default()
                    },
                    run_rate,
                ));
            }
            let offset = frame_offset(path.origin_cell_id)?;
            let o = path.origin;
            Some((
                MovementStruct {
                    kind: MovementType::MoveToPosition,
                    pos: CellPosition::new(
                        cell_id,
                        Vec3::new(o.x, o.y, o.z) + offset,
                        Quat::IDENTITY,
                    ),
                    params: Some(params),
                    ..MovementStruct::default()
                },
                run_rate,
            ))
        }
        Movement::TurnToObject {
            target,
            wire_heading,
            params,
        } => {
            let mut p = MovementParameters::from_wire_turn_to(
                params.bitfield,
                params.speed,
                params.desired_heading,
            );
            if target_body(*target).is_some() {
                return Some((
                    MovementStruct {
                        kind: MovementType::TurnToObject,
                        object_id: *target,
                        top_level_id: *target,
                        params: Some(p),
                        ..MovementStruct::default()
                    },
                    None,
                ));
            }
            // A thing with no body here: turn to the heading the order
            // names for that case.
            p.desired_heading = *wire_heading;
            Some((
                MovementStruct {
                    kind: MovementType::TurnToHeading,
                    params: Some(p),
                    ..MovementStruct::default()
                },
                None,
            ))
        }
        Movement::TurnToHeading { params } => Some((
            MovementStruct {
                kind: MovementType::TurnToHeading,
                params: Some(MovementParameters::from_wire_turn_to(
                    params.bitfield,
                    params.speed,
                    params.desired_heading,
                )),
                ..MovementStruct::default()
            },
            None,
        )),
        Movement::Interpreted { .. } | Movement::Other => None,
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Runtime.Tests/Session/RuntimeServerControlledLocalMovementTests.cs`.
    //! The wire here carries one record per movement type, so the case
    //! where an ordinary motion carries move-to records becomes: an
    //! ordinary motion asks for nothing.

    use ac_proto::{InterpretedMotionState, MoveToPath, TurnToParams, Vector3};

    use super::*;

    const LOCAL_CELL: u32 = 0xA9B4_0029;
    const TARGET: u32 = 0x8000_0DAD;

    fn walk_path() -> MoveToPath {
        MoveToPath {
            origin_cell_id: LOCAL_CELL,
            origin: Vector3 {
                x: 133.6,
                y: 22.4,
                z: 94.0,
            },
            bitfield: 0x403,
            distance_to_object: 1.8,
            min_distance: 0.0,
            fail_distance: 0.0,
            speed: 1.0,
            walk_run_threshold: 15.0,
            desired_heading: 0.0,
            run_rate: 1.75,
        }
    }

    fn order(movement_type: u8, movement: Movement) -> MovementData {
        MovementData {
            movement_type,
            motion_flags: 0,
            current_style: 0x3D,
            movement,
        }
    }

    fn face() -> Movement {
        Movement::TurnToObject {
            target: TARGET,
            wire_heading: 90.0,
            params: TurnToParams {
                bitfield: 0x3,
                speed: 1.0,
                desired_heading: 0.0,
            },
        }
    }

    fn resolve_with(
        data: &MovementData,
        body: Option<BodyShape>,
        offset: Option<Vec3>,
    ) -> Option<(MovementStruct, Option<f32>)> {
        resolve(data, |_| body, |_| offset, LOCAL_CELL)
    }

    const BODY: Option<BodyShape> = Some((0.4, 1.3));

    #[test]
    fn an_order_to_walk_to_something_with_a_body_follows_that_body() {
        let data = order(
            6,
            Movement::MoveToObject {
                target: TARGET,
                path: walk_path(),
            },
        );
        let (request, run_rate) = resolve_with(&data, BODY, Some(Vec3::ZERO)).unwrap();
        assert_eq!(request.kind, MovementType::MoveToObject);
        assert_eq!(request.object_id, TARGET);
        assert_eq!(request.top_level_id, TARGET);
        assert_eq!(request.radius, 0.4);
        assert_eq!(request.height, 1.3);
        assert_eq!(request.params.unwrap().distance_to_object, 1.8);
        assert_eq!(run_rate, Some(1.75));
    }

    #[test]
    fn an_order_naming_something_with_no_body_walks_to_the_place_instead() {
        let data = order(
            6,
            Movement::MoveToObject {
                target: TARGET,
                path: walk_path(),
            },
        );
        let (request, _) = resolve_with(&data, None, Some(Vec3::new(192.0, -192.0, 0.0))).unwrap();
        assert_eq!(request.kind, MovementType::MoveToPosition);
        assert_eq!(request.pos.obj_cell_id, LOCAL_CELL);
        let o = request.pos.frame.origin;
        assert!((o.x - (133.6 + 192.0)).abs() < 1e-3);
        assert!((o.y - (22.4 - 192.0)).abs() < 1e-3);
        assert!((o.z - 94.0).abs() < 1e-3);
        assert_eq!(request.params.unwrap().distance_to_object, 1.8);
    }

    #[test]
    fn an_ordinary_motion_asks_for_nothing_and_the_kind_decides() {
        let ordinary = order(
            0,
            Movement::Interpreted {
                state: InterpretedMotionState::default(),
                sticky_object: None,
            },
        );
        assert!(resolve_with(&ordinary, BODY, Some(Vec3::ZERO)).is_none());
        let faced = resolve_with(&order(8, face()), BODY, Some(Vec3::ZERO)).unwrap();
        assert_eq!(faced.0.kind, MovementType::TurnToObject);
    }

    #[test]
    fn an_order_that_names_a_place_walks_there_even_when_a_body_is_at_hand() {
        let data = order(7, Movement::MoveToPosition { path: walk_path() });
        let (request, _) = resolve_with(&data, BODY, Some(Vec3::ZERO)).unwrap();
        assert_eq!(request.kind, MovementType::MoveToPosition);
        assert!((request.pos.frame.origin.x - 133.6).abs() < 1e-3);
    }

    #[test]
    fn an_order_whose_frame_is_not_established_starts_nothing() {
        let data = order(
            6,
            Movement::MoveToObject {
                target: TARGET,
                path: walk_path(),
            },
        );
        assert!(resolve_with(&data, None, None).is_none());
    }

    #[test]
    fn an_order_to_face_something_with_a_body_turns_to_it() {
        let (request, _) = resolve_with(&order(8, face()), BODY, None).unwrap();
        assert_eq!(request.kind, MovementType::TurnToObject);
        assert_eq!(request.object_id, TARGET);
    }

    #[test]
    fn an_order_to_face_something_with_no_body_turns_to_the_heading_it_names() {
        let (request, _) = resolve_with(&order(8, face()), None, None).unwrap();
        assert_eq!(request.kind, MovementType::TurnToHeading);
        assert_eq!(request.params.unwrap().desired_heading, 90.0);
    }
}
