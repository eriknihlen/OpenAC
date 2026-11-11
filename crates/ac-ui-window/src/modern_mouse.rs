//! Modern mouse turning (an OpenAC option, off by default) on the window's
//! side.
//!
//! The right button pressed over the world (not over the interface)
//! starts a drag and a modern mouse look in the session
//! ([`MovementCommand::BeginModernMouseLook`]). The window holds the cursor
//! for it, so the drag goes on wherever the pointer would have crossed the
//! interface, and the mouse turns the chase camera's view around the
//! character at once ([`crate::camera::ChaseCamera::hold_view`]). The view
//! never waits for the character: the session turns the character toward
//! the view's yaw ([`MovementCommand::MouseLookFacing`]) with the ordinary
//! turn motion at the full turn rate and stops it exactly on the view.
//! This side only says where the view looks; the session alone moves the
//! character.
//!
//! Letting go of the button gives the cursor back. The view stays where it
//! points while the character finishes its turn; then the ordinary camera
//! follows the character again, keeping what is left of the swing (none
//! once the character caught up), so nothing jumps. A turn key, leaving the
//! chase camera, the option going off or the window losing the focus end
//! it at once; a new press during the finishing turn takes the view up
//! where it is.
//!
//! With "Both Mouse Buttons Run Forward" on, holding the left button as
//! well during a drag runs forward; letting go of either stops; the
//! back-up key cancels it until the buttons are pressed again.

use ac_runtime::movement::MovementCommand;
use ac_ui::input::chord::mouse;
use ac_ui::input::dispatcher::InputDevices;

use super::GameplayOutput;
use crate::camera::{CameraController, CameraMode, MOUSE_ORBIT_SCALE, wrap_angle};

/// One pixel of sideways drag turns the view this many radians, times the
/// mouse-look sensitivity.
pub const YAW_RADIANS_PER_PIXEL: f32 = 0.004;

/// The character faces the view once they are this close (radians): the
/// session lands the turn exactly on the view, so only rounding is left.
pub const FACING_SETTLED_RADIANS: f32 = 1e-3;

/// A finishing turn that has not moved the character for this long is
/// over even if the character could not reach the view (it was not
/// allowed to turn).
pub const SETTLE_GIVE_UP_SECONDS: f32 = 1.0;

#[derive(Debug, Clone, Copy, PartialEq)]
enum Phase {
    Idle,
    /// The right button is down: the mouse turns the view.
    Dragging,
    /// The button came up: the character finishes turning to the view.
    Settling {
        quiet_for: f32,
        last_yaw: f32,
    },
}

/// The modern mouse turning state of one window.
#[derive(Debug, Clone, PartialEq)]
pub struct ModernMouse {
    phase: Phase,
    /// The view yaw the session was last told.
    sent: Option<f32>,
    /// The first raw motion after the cursor was taken is dropped (taking
    /// the cursor can report a jump).
    ignore_next_motion: bool,
    forward: bool,
    forward_cancelled: bool,
    /// The drag turned the view or a button went down during it: the
    /// world clicks of the buttons held now select nothing.
    swallow_clicks: bool,
}

impl Default for ModernMouse {
    fn default() -> Self {
        Self {
            phase: Phase::Idle,
            sent: None,
            ignore_next_motion: false,
            forward: false,
            forward_cancelled: false,
            swallow_clicks: false,
        }
    }
}

impl ModernMouse {
    /// A drag is on: the window holds the cursor.
    #[must_use]
    pub fn dragging(&self) -> bool {
        self.phase == Phase::Dragging
    }

    /// A drag or its finishing turn is on.
    #[must_use]
    pub fn active(&self) -> bool {
        self.phase != Phase::Idle
    }

    /// Both buttons run forward now.
    #[must_use]
    pub fn forward(&self) -> bool {
        self.forward
    }

    /// World clicks are swallowed: the drag turned the view, or a button
    /// went down during it (its click would pick under a hidden cursor).
    /// A right-button click that turned nothing still examines.
    #[must_use]
    pub fn swallows_clicks(&self) -> bool {
        self.swallow_clicks
    }

    /// The right button went down over the world (`over_ui` when the
    /// interface had the pointer, which starts nothing); `mouse_look`
    /// when instant mouse look is on (it keeps the mouse).
    pub fn press(
        &mut self,
        cameras: Option<&mut CameraController>,
        over_ui: bool,
        mouse_look: bool,
        out: &mut Vec<GameplayOutput>,
    ) {
        let Some(cameras) = cameras else {
            return;
        };
        if over_ui
            || mouse_look
            || self.dragging()
            || !cameras.chase.tuning.modern_mouse_turning
            || cameras.mode() != CameraMode::Chase
        {
            return;
        }
        if self.phase == Phase::Idle {
            // The view starts at the character's facing plus the swing.
            cameras.chase.hold_view();
            out.push(GameplayOutput::Movement(
                MovementCommand::BeginModernMouseLook,
            ));
            self.sent = None;
        }
        // During a finishing turn the view is taken up where it is and the
        // turn simply carries on.
        self.phase = Phase::Dragging;
        self.ignore_next_motion = true;
        self.send_view(cameras, out);
        out.push(GameplayOutput::GrabCursor);
    }

    /// The right button came up: the cursor comes back, the character
    /// finishes turning to the view.
    pub fn release(&mut self, cameras: Option<&CameraController>, out: &mut Vec<GameplayOutput>) {
        if !self.dragging() {
            return;
        }
        self.phase = Phase::Settling {
            quiet_for: 0.0,
            last_yaw: cameras.map_or(0.0, |c| c.chase.subject_yaw()),
        };
        self.forward = false;
        self.ignore_next_motion = false;
        out.push(GameplayOutput::ReleaseCursor);
    }

    /// A button went down (during a drag its click is swallowed).
    pub fn button_pressed(&mut self) {
        if self.dragging() {
            self.swallow_clicks = true;
        }
    }

    /// Raw mouse motion during the drag turns the view (sideways) and
    /// raises or lowers it (up and down), by the mouse-look sensitivity.
    pub fn motion(&mut self, dx: f32, dy: f32, cameras: Option<&mut CameraController>) {
        let Some(cameras) = cameras else {
            return;
        };
        if !self.dragging() || !dx.is_finite() || !dy.is_finite() {
            return;
        }
        if std::mem::take(&mut self.ignore_next_motion) {
            return;
        }
        if dx != 0.0 || dy != 0.0 {
            self.swallow_clicks = true;
        }
        let tuning = cameras.chase.tuning;
        let sensitivity = tuning.mouse_look_sensitivity;
        let invert = if tuning.invert_mouse_look_y_axis {
            -1.0
        } else {
            1.0
        };
        // Moving the mouse right turns the view right (clockwise).
        cameras
            .chase
            .turn_held_view(-dx * YAW_RADIANS_PER_PIXEL * sensitivity);
        if dy != 0.0 {
            cameras
                .chase
                .adjust_pitch(invert * dy * sensitivity * MOUSE_ORBIT_SCALE);
        }
    }

    /// End at once: the view is let go where it is, the session's modern
    /// mouse look ends, the cursor comes back.
    pub fn end(&mut self, cameras: Option<&mut CameraController>, out: &mut Vec<GameplayOutput>) {
        if self.phase == Phase::Idle {
            return;
        }
        if self.dragging() {
            out.push(GameplayOutput::ReleaseCursor);
        }
        if let Some(cameras) = cameras {
            cameras.chase.release_view();
        }
        out.push(GameplayOutput::Movement(MovementCommand::EndMouseLook));
        self.phase = Phase::Idle;
        self.sent = None;
        self.ignore_next_motion = false;
        self.forward = false;
        self.forward_cancelled = false;
    }

    /// The view's yaw to the session when it changed.
    fn send_view(&mut self, cameras: &CameraController, out: &mut Vec<GameplayOutput>) {
        let Some(view) = cameras.chase.held_view_yaw() else {
            return;
        };
        if self.sent != Some(view) {
            self.sent = Some(view);
            out.push(GameplayOutput::Movement(MovementCommand::MouseLookFacing(
                view,
            )));
        }
    }

    /// One frame of `dt` seconds: ends the drag when the option, the
    /// chase camera or the held view went away; finishes the turn after a
    /// release; sends the view's yaw when it changed; works out both
    /// buttons' run (`backward` is the back-up key held, `keyboard_turn` a
    /// turn key held). Returns whether both buttons run forward.
    pub fn tick(
        &mut self,
        dt: f32,
        devices: &dyn InputDevices,
        cameras: Option<&mut CameraController>,
        backward: bool,
        keyboard_turn: bool,
        out: &mut Vec<GameplayOutput>,
    ) -> bool {
        if !devices.is_button_held(mouse::LEFT) && !devices.is_button_held(mouse::RIGHT) {
            self.swallow_clicks = false;
        }
        let Some(cameras) = cameras else {
            self.end(None, out);
            return false;
        };
        if self.phase == Phase::Idle {
            // A view left held (the drag ended without a camera to hand).
            if cameras.chase.held_view_yaw().is_some() {
                cameras.chase.release_view();
            }
            return false;
        }
        if !cameras.chase.tuning.modern_mouse_turning
            || cameras.mode() != CameraMode::Chase
            || cameras.chase.held_view_yaw().is_none()
        {
            self.end(Some(cameras), out);
            return false;
        }
        if self.dragging() && !devices.is_button_held(mouse::RIGHT) {
            // The release went elsewhere (the window lost it).
            self.release(Some(&*cameras), out);
        }
        if let Phase::Settling {
            quiet_for,
            last_yaw,
        } = &mut self.phase
        {
            let yaw = cameras.chase.subject_yaw();
            if wrap_angle(yaw - *last_yaw).abs() > 1e-5 {
                *quiet_for = 0.0;
            } else {
                *quiet_for += dt;
            }
            *last_yaw = yaw;
            let faces_view = cameras
                .chase
                .held_view_yaw()
                .is_some_and(|view| wrap_angle(view - yaw).abs() <= FACING_SETTLED_RADIANS);
            if keyboard_turn || faces_view || *quiet_for >= SETTLE_GIVE_UP_SECONDS {
                self.end(Some(cameras), out);
                return false;
            }
        }
        self.send_view(cameras, out);
        let both = self.dragging()
            && cameras.chase.tuning.both_mouse_buttons_run_forward
            && devices.is_button_held(mouse::LEFT)
            && devices.is_button_held(mouse::RIGHT);
        if !both {
            self.forward_cancelled = false;
        } else if backward {
            self.forward_cancelled = true;
        }
        self.forward = both && !self.forward_cancelled;
        self.forward
    }
}
