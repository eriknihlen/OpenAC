//! The local player's movement input source: each frame the keys a window
//! holds (or nothing, headless) go through the scripted moves, which take
//! the frame while a move or a jump is held and hand it back once none is.
//!
//! Scripted movement itself is `ac_nav::motor`: the console, `/motor` and a
//! walk all drive it. This is where its output meets the body: the offset it
//! measures from where the moves began, the heading a turn lands on, and the
//! count of frames carrying the player's own movement, which ends a walk.

use ac_motion::move_to_math::{heading_from_yaw, yaw_from_heading};
use ac_nav::motor::{ScriptedMoveSample, ScriptedMoveSnapshot, ScriptedMovement};
use ac_physics::Vec3;
use ac_physics::movement::{MovementInput, PlayerMovementController, PlayerState};

/// Metres between landblock corners.
const LANDBLOCK_SIZE: f32 = 192.0;

/// Scripted moves and the frames of the player's own movement.
#[derive(Debug, Default)]
pub struct ScriptedInput {
    scripted: ScriptedMovement,
    /// Where the body stood (cell, landblock-local point) when the moves
    /// under way began.
    start: Option<(u32, Vec3)>,
    player_movement_input_frames: i64,
}

/// Whether a frame's input is the player's own, asking the character to
/// move or turn, rather than a held command.
fn is_player_movement(input: &MovementInput) -> bool {
    !input.is_persistent_command
        && (input.forward
            || input.backward
            || input.strafe_left
            || input.strafe_right
            || input.turn_left
            || input.turn_right)
}

/// How far one landblock's corner lies from another's, metres.
fn block_offset(from_cell: u32, to_cell: u32) -> Vec3 {
    let x = |cell: u32| ((cell >> 24) & 0xFF) as f32;
    let y = |cell: u32| ((cell >> 16) & 0xFF) as f32;
    Vec3::new(
        (x(to_cell) - x(from_cell)) * LANDBLOCK_SIZE,
        (y(to_cell) - y(from_cell)) * LANDBLOCK_SIZE,
        0.0,
    )
}

impl ScriptedInput {
    /// No moves, no frames counted.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The scripted moves.
    #[must_use]
    pub fn scripted(&self) -> &ScriptedMovement {
        &self.scripted
    }

    /// The scripted moves, to begin or stop one.
    pub fn scripted_mut(&mut self) -> &mut ScriptedMovement {
        &mut self.scripted
    }

    /// The latest move on each channel and the latest jump.
    #[must_use]
    pub fn snapshot(&self) -> ScriptedMoveSnapshot {
        self.scripted.snapshot()
    }

    /// How many frames carried the player's own movement input, whether or
    /// not a scripted move was running: a walk that sees it grow knows the
    /// player took the character.
    #[must_use]
    pub fn player_movement_input_frames(&self) -> i64 {
        self.player_movement_input_frames
    }

    /// Every move and the jump end as lost (the character left the world or
    /// entered portal space).
    pub fn lose(&mut self) {
        self.scripted.lose();
        self.start = None;
    }

    /// Advances the scripted moves by one frame and returns the input the
    /// body acts on instead of `frame`, or `None` when nothing scripted is
    /// held. A turn that has just passed its angle sets the body's heading
    /// to land on it. The player's own movement is counted either way.
    pub fn capture(
        &mut self,
        frame: &MovementInput,
        controller: Option<&mut PlayerMovementController>,
    ) -> Option<MovementInput> {
        let manual = is_player_movement(frame);
        if manual {
            self.player_movement_input_frames += 1;
        }
        if !self.scripted.is_active() {
            self.start = None;
            return None;
        }
        let input = match controller {
            None => {
                self.scripted.lose();
                None
            }
            Some(controller) => {
                let now = controller.current_cell_position();
                let (start_cell, start_origin) = *self
                    .start
                    .get_or_insert((now.obj_cell_id, now.frame.origin));
                let offset =
                    block_offset(start_cell, now.obj_cell_id) + now.frame.origin - start_origin;
                let output = self.scripted.advance(&ScriptedMoveSample {
                    offset_from_start: offset,
                    heading_degrees: heading_from_yaw(controller.yaw()),
                    time_seconds: f64::from(controller.sim_time_seconds()),
                    in_portal_space: controller.state == PlayerState::PortalSpace,
                    manual_movement_held: manual,
                });
                if let Some(landed) = output.heading {
                    controller.set_yaw(yaw_from_heading(landed));
                }
                output.input
            }
        };
        if !self.scripted.is_active() {
            self.start = None;
        }
        input
    }
}

#[cfg(test)]
mod tests {
    //! The four cases of OpenAC
    //! `tests/AcDream.Runtime.Tests/Gameplay/RuntimeScriptedMovementTests.cs`
    //! that go through the movement state's input source
    //! (`RuntimeScriptedMovementInputSource`); the rest are in
    //! `crates/ac-nav/tests/motor.rs`.

    use super::*;
    use ac_nav::motor::{MoveDirection, MovePace, MoveRequest, ScriptedMoveState};
    use ac_physics::PhysicsEngine;
    use ac_physics::movement::MovementEnv;

    fn controller() -> PlayerMovementController {
        let mut engine = PhysicsEngine::new();
        let view = super::super::remote::BodiesView::default();
        let mut controller = PlayerMovementController::new();
        let mut env = MovementEnv::new(&mut engine, &view);
        let at = Vec3::new(96.0, 96.0, 50.0);
        controller.seed_placement(&mut env, at, 0x0001, at);
        controller
    }

    fn forward() -> MovementInput {
        MovementInput {
            forward: true,
            ..MovementInput::default()
        }
    }

    #[test]
    fn the_frames_own_input_passes_through_when_nothing_is_scripted() {
        let mut source = ScriptedInput::new();
        assert_eq!(source.capture(&forward(), None), None);
        assert_eq!(source.player_movement_input_frames(), 1);
    }

    #[test]
    fn a_scripted_move_takes_the_frame_until_the_player_moves() {
        let mut controller = controller();
        let mut source = ScriptedInput::new();
        assert!(source.scripted_mut().begin(MoveRequest::new(
            MoveDirection::StrafeLeft,
            MovePace::Run,
            0.0
        )));
        let held = source
            .capture(&MovementInput::default(), Some(&mut controller))
            .expect("the move takes the frame");
        assert!(held.strafe_left && held.is_persistent_command);

        source.capture(&forward(), Some(&mut controller));
        assert_eq!(
            source.snapshot().strafe.state,
            ScriptedMoveState::Interrupted
        );
    }

    #[test]
    fn a_turn_lands_the_body_on_its_angle() {
        let mut controller = controller();
        let mut source = ScriptedInput::new();
        controller.set_yaw(yaw_from_heading(10.0));
        assert!(source.scripted_mut().begin(MoveRequest::new(
            MoveDirection::TurnRight,
            MovePace::Run,
            20.0
        )));
        source.capture(&MovementInput::default(), Some(&mut controller));
        controller.set_yaw(yaw_from_heading(34.0));

        let input = source.capture(&MovementInput::default(), Some(&mut controller));
        assert!(input.is_some_and(|i| !i.turn_right));
        assert_eq!(source.snapshot().turn.state, ScriptedMoveState::Completed);
        let heading = heading_from_yaw(controller.yaw());
        assert!((heading - 30.0).abs() < 0.01, "heading {heading}");
    }

    #[test]
    fn a_turn_ending_changes_the_snapshot() {
        // OpenAC's case checks the movement state's revision; the runtime
        // here has no revision counter, and the snapshot it is derived from
        // is what a host compares.
        let mut controller = controller();
        let mut source = ScriptedInput::new();
        controller.set_yaw(yaw_from_heading(0.0));
        assert!(source.scripted_mut().begin(MoveRequest::new(
            MoveDirection::TurnRight,
            MovePace::Run,
            5.0
        )));
        source.capture(&MovementInput::default(), Some(&mut controller));
        let before = source.snapshot();
        controller.set_yaw(yaw_from_heading(9.0));
        source.capture(&MovementInput::default(), Some(&mut controller));

        assert_eq!(source.snapshot().turn.state, ScriptedMoveState::Completed);
        assert_ne!(source.snapshot(), before);
    }
}
