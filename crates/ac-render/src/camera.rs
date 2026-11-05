//! Cameras: where the eye is, where it looks, and how wide it sees.
//!
//! The world is z-up, right-handed, in meters. A camera produces a view
//! matrix (world to eye) and a projection matrix (eye to the screen's clip
//! space, depth 0 at the near plane and 1 at the far plane, the convention
//! wgpu uses).
//!
//! Two cameras: the chase camera follows the player from behind and above
//! (the game's normal view); the orbit camera circles a fixed point and is
//! used when there is no player to follow.

use glam::{Mat4, Quat, Vec3};

use crate::camera_collision::CameraCollisionProbe;

/// The game's default field-of-view setting: 90 degrees, in radians.
pub const DEFAULT_GAME_FOV_RADIANS: f32 = 1.570_796_4;

/// Subtracted from the aspect ratio before it divides the game field of
/// view (see [`applied_vertical_fov`]).
pub const ASPECT_BIAS: f32 = 0.1;

/// Distance from the eye to the near clipping plane: 0.1 m. Anything closer
/// is not drawn.
pub const NEAR_PLANE: f32 = 0.1;

/// Distance from the eye to the far clipping plane: 5 km.
pub const FAR_PLANE: f32 = 5000.0;

/// The vertical field of view the game applies for a field-of-view setting
/// and a window shape: `game_fov / (aspect - 0.1)`, radians. Wider windows
/// get a smaller vertical angle so the horizontal view stays near 80-90
/// degrees. `None` when the result is not strictly between 0 and pi
/// (180 degrees) or the window is too narrow (`aspect <= 0.1`).
#[must_use]
pub fn applied_vertical_fov(game_fov_radians: f32, aspect: f32) -> Option<f32> {
    let divisor = aspect - ASPECT_BIAS;
    if divisor <= 0.0 {
        return None;
    }
    let fov_y = game_fov_radians / divisor;
    (fov_y > 0.0 && fov_y < std::f32::consts::PI).then_some(fov_y)
}

/// The vertical field of view at the default setting on a 16:9 window.
#[must_use]
pub fn default_applied_fov_y() -> f32 {
    applied_vertical_fov(DEFAULT_GAME_FOV_RADIANS, 16.0 / 9.0)
        .expect("the default field of view is valid at 16:9")
}

/// The lens shared by every camera: the field-of-view setting, the window
/// shape, and the vertical angle they give.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Lens {
    game_fov: f32,
    aspect: f32,
    fov_y: f32,
}

impl Default for Lens {
    fn default() -> Self {
        Self {
            game_fov: DEFAULT_GAME_FOV_RADIANS,
            aspect: 16.0 / 9.0,
            fov_y: default_applied_fov_y(),
        }
    }
}

impl Lens {
    /// The field-of-view setting, radians.
    #[must_use]
    pub fn game_fov(&self) -> f32 {
        self.game_fov
    }

    /// Width over height of the window.
    #[must_use]
    pub fn aspect(&self) -> f32 {
        self.aspect
    }

    /// The vertical field of view in use, radians.
    #[must_use]
    pub fn fov_y(&self) -> f32 {
        self.fov_y
    }

    /// Follow a new window shape. The aspect always changes; the vertical
    /// angle changes only when the formula accepts the pair, otherwise the
    /// previous angle stays.
    pub fn set_aspect(&mut self, aspect: f32) {
        self.aspect = aspect;
        self.apply();
    }

    /// Change the field-of-view setting at the current window shape.
    pub fn set_game_fov(&mut self, game_fov_radians: f32) {
        self.game_fov = game_fov_radians;
        self.apply();
    }

    fn apply(&mut self) {
        if let Some(fov_y) = applied_vertical_fov(self.game_fov, self.aspect) {
            self.fov_y = fov_y;
        }
    }

    /// The projection matrix: right-handed, depth 0..1, near 0.1 m, far 5 km.
    #[must_use]
    pub fn projection(&self) -> Mat4 {
        glam::camera::rh::proj::directx::perspective(self.fov_y, self.aspect, NEAR_PLANE, FAR_PLANE)
    }
}

/// The direction an orientation faces in the ground plane, as an angle from
/// +x (east) toward +y (north), radians in -pi..=pi. An object's forward
/// axis is its local +y.
#[must_use]
pub fn yaw_of(orientation: Quat) -> f32 {
    let forward = orientation * Vec3::Y;
    forward.y.atan2(forward.x)
}

/// `angle` wrapped to (-π, π], radians.
#[must_use]
pub fn wrap_angle(angle: f32) -> f32 {
    use std::f32::consts::{PI, TAU};
    let wrapped = (angle + PI).rem_euclid(TAU) - PI;
    if wrapped <= -PI {
        wrapped + TAU
    } else {
        wrapped
    }
}

/// Translation and rotation smoothing of the chase camera: each frame the
/// eye moves `stiffness * dt * 10` of the way to where it should be. The
/// player's "camera stiffness" setting replaces it ([`ChaseTuning`]).
pub const CHASE_STIFFNESS: f32 = 0.45;

/// One pixel of a right-button orbit drag turns the boom this many
/// adjustment units (times the mouse-look sensitivity).
pub const MOUSE_ORBIT_SCALE: f32 = 0.066_666_67;

/// Mouse movement within this many seconds of the last is averaged with
/// it.
pub const MOUSE_LOW_PASS_WINDOW_SECONDS: f32 = 0.25;

/// The player's camera settings as the chase camera uses them.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ChaseTuning {
    /// Translation and rotation smoothing.
    pub stiffness: f32,
    /// Adjustment units per second of a held camera key.
    pub adjustment_speed: f32,
    /// Mouse-look sensitivity.
    pub mouse_look_sensitivity: f32,
    /// Moving the mouse up lowers the view.
    pub invert_mouse_look_y_axis: bool,
    /// The camera heads along the slope the player moves on.
    pub align_to_slope: bool,
    /// Modern mouse turning: a right-button drag over the world turns the
    /// view around the character, and the character turns to face it.
    pub modern_mouse_turning: bool,
    /// With modern mouse turning, both buttons held run forward.
    pub both_mouse_buttons_run_forward: bool,
}

impl Default for ChaseTuning {
    fn default() -> Self {
        Self::from(&ac_host::settings::CameraTurningSettings::default())
    }
}

impl From<&ac_host::settings::CameraTurningSettings> for ChaseTuning {
    fn from(s: &ac_host::settings::CameraTurningSettings) -> Self {
        Self {
            stiffness: s.stiffness,
            adjustment_speed: s.adjustment_speed,
            mouse_look_sensitivity: s.mouse_look_sensitivity,
            invert_mouse_look_y_axis: s.invert_mouse_look_y_axis,
            align_to_slope: s.align_to_slope,
            modern_mouse_turning: s.modern_mouse_turning,
            both_mouse_buttons_run_forward: s.both_mouse_buttons_run_forward,
        }
    }
}

/// The chase camera keys held this frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ChaseAdjustment {
    /// Move toward the player (keypad minus).
    pub zoom_in: bool,
    /// Move away (keypad plus).
    pub zoom_out: bool,
    /// Raise the boom (keypad 8).
    pub raise: bool,
    /// Lower it (keypad 2).
    pub lower: bool,
    /// Swing it to the player's left (keypad 4).
    pub rotate_left: bool,
    /// Swing it to the right (keypad 6).
    pub rotate_right: bool,
}

/// One axis of the mouse smoothing: within the window the sample is
/// averaged with the last output; the result blends that with the raw
/// sample by `weight`.
#[must_use]
pub fn filter_mouse_axis(
    raw: f32,
    weight: f32,
    now_seconds: f32,
    last_delta: &mut f32,
    last_time_seconds: &mut f32,
    window_seconds: f32,
) -> f32 {
    let average = if now_seconds - *last_time_seconds < window_seconds {
        (*last_delta + raw) * 0.5
    } else {
        raw
    };
    let output = raw * (1.0 - weight) + average * weight;
    *last_delta = output;
    *last_time_seconds = now_seconds;
    output
}

/// Below this many meters of eye movement (and [`ROTATION_SNAP_EPSILON`] of
/// turn) the chase camera holds still instead of creeping.
pub const TRANSLATION_SNAP_EPSILON: f32 = 0.0002 * 2.0;

/// Below this change of the unit look direction the chase camera holds still.
pub const ROTATION_SNAP_EPSILON: f32 = 0.0002;

/// One zoom step scales the boom length by 20 %.
pub const OFFSET_SCALE_PER_ADJUSTMENT: f32 = 0.2;

/// One raise/lower/rotate step turns the boom 8 degrees.
pub const OFFSET_ANGLE_RADIANS: f32 = 8.0 * std::f32::consts::PI / 180.0;

/// Zooming in stops at a boom of 0.5 m.
pub const MINIMUM_OFFSET_LENGTH: f32 = 0.5;

/// The boom's sideways and backward reach is capped at 10 m each.
pub const MAXIMUM_HORIZONTAL_COMPONENT: f32 = 10.0;

/// The boom reaches at most 450 m up.
pub const MAXIMUM_VERTICAL_COMPONENT: f32 = 450.0;

/// The boom reaches at most 1.8 m below the pivot.
pub const MINIMUM_VERTICAL_COMPONENT: f32 = -1.8;

/// The default view before its scale: 2.5 m behind and 0.75 m above the
/// pivot.
const DEFAULT_BACK: f32 = 2.5;
const DEFAULT_UP: f32 = 0.75;
/// In the game world the default view is scaled by 1.1, so it sits 2.75 m
/// behind and 0.825 m above the pivot (a 2.87 m boom). Only the default
/// view is scaled: the look-down, map and first-person views are not.
pub const DEFAULT_VIEW_SCALE: f32 = 1.1;

/// The default view's boom, `(back, up)` in meters, scale applied.
#[must_use]
pub fn default_view_offset() -> (f32, f32) {
    (
        DEFAULT_BACK * DEFAULT_VIEW_SCALE,
        DEFAULT_UP * DEFAULT_VIEW_SCALE,
    )
}

/// The look-down view's boom: 2 m back.
const LOOK_DOWN_BACK: f32 = 2.0;
/// The map view's boom: 450 m back.
const MAP_BACK: f32 = 450.0;
/// The first-person eye: 0.18 m ahead of the pivot.
pub const FIRST_PERSON_FORWARD: f32 = 0.18;
/// Leaving the first-person view puts the boom 0.6 m back, 0.5 m up.
const LEAVE_HEAD_BACK: f32 = 0.6;
const LEAVE_HEAD_UP: f32 = 0.5;
/// Raising or lowering the first-person look moves its target direction
/// by 0.2 per unit, up to ±0.8.
const IN_HEAD_DIRECTION_STEP: f32 = 0.2;
const IN_HEAD_DIRECTION_LIMIT: f32 = 0.8;
/// The look-down and map views look along this direction (local frame:
/// x right, y forward, z up).
const LOOK_DOWN_DIRECTION: Vec3 = Vec3::new(0.0, 0.5, -1.8);

/// Below this a velocity, normal or projection is treated as zero for the
/// slope tilt.
pub const MIN_TILT_COMPONENT: f32 = 2e-4;
/// The airborne heading plane flattens the velocity's vertical part to a
/// tenth.
pub const AIRBORNE_PLANE_VERTICAL_SCALE: f32 = 0.1;

/// What the chase camera follows this frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ChaseSubject {
    /// The player's feet (drawing frame).
    pub position: Vec3,
    /// The player's facing, radians from +x.
    pub yaw: f32,
    /// The velocity of the body's last step, metres per second.
    pub velocity: Vec3,
    /// The body touches a surface.
    pub in_contact: bool,
    /// The contact plane's normal.
    pub contact_normal: Vec3,
    /// The point the camera tracks (a combat target), if any.
    pub tracked_target: Option<Vec3>,
}

impl ChaseSubject {
    /// A body standing still on flat ground.
    #[must_use]
    pub fn standing(position: Vec3, yaw: f32) -> Self {
        Self {
            position,
            yaw,
            velocity: Vec3::ZERO,
            in_contact: true,
            contact_normal: Vec3::Z,
            tracked_target: None,
        }
    }
}

/// The camera that follows the player: it sits on a boom behind and above a
/// pivot 1.5 m over the player's feet and looks at the pivot, easing toward
/// its target each frame. It heads along the slope the player moves on
/// (when the option is on and the boom is behind the player), and has the
/// game's first-person, look-down and map views.
#[derive(Debug, Clone, PartialEq)]
pub struct ChaseCamera {
    /// Boom length, meters.
    pub distance: f32,
    /// Boom elevation above the horizontal, radians.
    pub pitch: f32,
    /// Boom rotation around the pivot relative to the player's facing,
    /// radians.
    pub yaw_offset: f32,
    /// Height of the pivot above the player's feet, meters.
    pub pivot_height: f32,
    /// The player's camera settings.
    pub tuning: ChaseTuning,
    looking_down: bool,
    map_mode: bool,
    in_head: bool,
    saved: SavedView,
    target_direction_local: Option<Vec3>,
    velocity_ring: [Vec3; 5],
    velocity_count: usize,
    /// Where the eye was published last frame (after collision): the
    /// easing starts from here.
    eye: Vec3,
    forward: Vec3,
    initialised: bool,
    viewer_cell: Option<u32>,
    player_translucency: f32,
    last_mouse_delta: (f32, f32),
    last_filter_time: f32,
    /// Rotates asked for in the first-person view since the frame last
    /// took them (they turn the character).
    pending_first_person_rotate: f32,
    /// Modern mouse turning holds the view: the yaw it looks along (radians
    /// from +x, counter-clockwise), whatever the character faces.
    held_view: Option<f32>,
    /// The character's facing at the last update.
    subject_yaw: f32,
}

/// One first-person rotate turns the character this many compass degrees.
pub const FIRST_PERSON_TURN_STEP_DEGREES: f32 = 8.0;

/// The view the look-down and map views return to.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
struct SavedView {
    in_head: bool,
    distance: f32,
    pitch: f32,
    yaw_offset: f32,
    target_direction_local: Option<Vec3>,
}

impl Default for ChaseCamera {
    fn default() -> Self {
        let (back, up) = default_view_offset();
        Self {
            distance: back.hypot(up),
            pitch: up.atan2(back),
            yaw_offset: 0.0,
            pivot_height: 1.5,
            tuning: ChaseTuning::default(),
            looking_down: false,
            map_mode: false,
            in_head: false,
            saved: SavedView::default(),
            target_direction_local: None,
            velocity_ring: [Vec3::ZERO; 5],
            velocity_count: 0,
            eye: Vec3::ZERO,
            forward: Vec3::X,
            initialised: false,
            viewer_cell: None,
            player_translucency: 0.0,
            last_mouse_delta: (0.0, 0.0),
            last_filter_time: 0.0,
            pending_first_person_rotate: 0.0,
            held_view: None,
            subject_yaw: 0.0,
        }
    }
}

/// What the chase camera's eye collides with this frame: the probe, the
/// player's cell and the player's object id (which the sweep ignores).
#[derive(Clone, Copy)]
pub struct ChaseCollision<'a> {
    /// The sweep.
    pub probe: &'a dyn CameraCollisionProbe,
    /// The player's cell.
    pub cell_id: u32,
    /// The player's object id.
    pub self_id: u32,
}

impl std::fmt::Debug for ChaseCollision<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ChaseCollision")
            .field("cell_id", &format_args!("{:#010x}", self.cell_id))
            .field("self_id", &format_args!("{:#010x}", self.self_id))
            .finish_non_exhaustive()
    }
}

impl ChaseCamera {
    /// Follow `subject`, `dt` seconds after the previous update. The first
    /// update places the eye at its target at once. With `collision`, the
    /// eased eye is swept from the pivot and stops where the sweep stops;
    /// the sweep's cell is then the viewer cell ([`ChaseCamera::viewer_cell`]).
    pub fn update(
        &mut self,
        subject: &ChaseSubject,
        dt: f32,
        collision: Option<ChaseCollision<'_>>,
    ) {
        let pivot = subject.position + Vec3::new(0.0, 0.0, self.pivot_height);
        self.subject_yaw = subject.yaw;
        // A held view looks where it was turned, whatever the character
        // faces: the swing is what lies between them, and neither a combat
        // target nor the slope turns it.
        let held = self.held_view;
        if let Some(view) = held {
            self.yaw_offset = wrap_angle(view - subject.yaw);
        }
        let tracked = if held.is_some() {
            None
        } else {
            compute_tracked_heading(pivot, subject.tracked_target)
        };

        // Look-down, map mode and a wide orbit see the flat facing; the
        // head view always tilts.
        let alignment = held.is_none()
            && self.tuning.align_to_slope
            && !self.looking_down
            && !self.map_mode
            && (self.in_head
                || (tracked.is_none()
                    && is_orbit_within_alignment_threshold(
                        self.distance,
                        self.pitch,
                        self.yaw_offset,
                    )));
        // The velocity ring only advances on updates that align to the
        // plane.
        if alignment {
            push_velocity(
                &mut self.velocity_ring,
                &mut self.velocity_count,
                subject.velocity,
            );
        }
        let average = average_velocity(&self.velocity_ring, self.velocity_count);

        // Look-down and map mode bake the orbit into the heading; a held
        // view in the head looks along the view.
        let heading_yaw = match held {
            Some(view) if self.in_head => view,
            _ if !self.in_head && self.target_direction_local.is_some() => {
                subject.yaw + self.yaw_offset
            }
            _ => subject.yaw,
        };
        let heading = tracked.unwrap_or_else(|| {
            compute_heading(
                average,
                heading_yaw,
                subject.in_contact,
                subject.contact_normal,
                alignment,
            )
        });

        let (target_eye, target_forward) = if self.in_head {
            compute_in_head_pose(pivot, heading, self.target_direction_local)
        } else if let Some(local) = self.target_direction_local {
            compute_target_direction_pose(pivot, heading, self.distance, self.pitch, local)
        } else {
            desired_pose(pivot, heading, self.distance, self.pitch, self.yaw_offset)
        };

        // A held view sits rigidly where the mouse put it; in the head the
        // look still eases.
        let sought_eye = if self.initialised && (held.is_none() || self.in_head) {
            // The game's camera stiffness sets only the rotation in first
            // person.
            let t_alpha = if self.in_head {
                1.0
            } else {
                damping_alpha(self.tuning.stiffness, dt)
            };
            let r_alpha = damping_alpha(self.tuning.stiffness, dt);
            let candidate_eye = self.eye.lerp(target_eye, t_alpha);
            let candidate_forward = self.forward.lerp(target_forward, r_alpha).normalize();
            let (eye, forward, _) =
                convergence_snap(self.eye, self.forward, candidate_eye, candidate_forward);
            self.forward = forward;
            eye
        } else {
            self.forward = target_forward;
            self.initialised = true;
            target_eye
        };

        self.eye = sought_eye;
        self.viewer_cell = None;
        if let Some(c) = collision {
            let swept =
                c.probe
                    .sweep_eye(pivot, sought_eye, c.cell_id, c.self_id, subject.position);
            self.eye = swept.eye;
            // A failed sweep (no cell) leaves the eye on the player and the
            // frame rooted in the player's cell.
            self.viewer_cell = Some(if swept.viewer_cell == 0 {
                c.cell_id
            } else {
                swept.viewer_cell
            });
        }
        self.player_translucency = compute_translucency(self.eye.distance(pivot));
    }

    /// After a teleport: the eye starts on the player looking along its
    /// facing and re-extends from there; the slope history is dropped.
    pub fn reset_viewer_to_player(&mut self, position: Vec3, yaw: f32) {
        let forward = Vec3::new(yaw.cos(), yaw.sin(), 0.0);
        self.eye = position;
        self.forward = forward;
        self.initialised = true;
        self.velocity_ring = [Vec3::ZERO; 5];
        self.velocity_count = 0;
        self.viewer_cell = None;
        self.player_translucency = 0.0;
    }

    /// The cell the last collided update left the eye in; `None` when the
    /// last update had no collision.
    #[must_use]
    pub fn viewer_cell(&self) -> Option<u32> {
        self.viewer_cell
    }

    /// How see-through the player is drawn: 1 with the eye within 0.2 m of
    /// the pivot (first person), 0 from 0.45 m out.
    #[must_use]
    pub fn player_translucency(&self) -> f32 {
        self.player_translucency
    }

    /// The first-person view is on.
    #[must_use]
    pub fn is_in_head(&self) -> bool {
        self.in_head
    }

    /// The look-down view is on.
    #[must_use]
    pub fn is_looking_down(&self) -> bool {
        self.looking_down
    }

    /// The map view is on.
    #[must_use]
    pub fn is_map_mode(&self) -> bool {
        self.map_mode
    }

    /// Restart the easing: the next update jumps straight to the target.
    pub fn reset(&mut self) {
        self.initialised = false;
    }

    /// Move the eye by `delta` without easing: the drawing frame's origin
    /// moved, so everything shifted and the camera shifts with it.
    pub fn translate(&mut self, delta: Vec3) {
        self.eye += delta;
    }

    /// Where the eye is.
    #[must_use]
    pub fn eye(&self) -> Vec3 {
        self.eye
    }

    /// The look direction.
    #[must_use]
    pub fn forward(&self) -> Vec3 {
        self.forward
    }

    /// The view matrix.
    #[must_use]
    pub fn view(&self) -> Mat4 {
        glam::camera::rh::view::look_at_mat4(self.eye, self.eye + self.forward, Vec3::Z)
    }

    /// Zoom: positive moves out. Each unit scales the boom by 20 %; a step
    /// that would break a boom limit is refused whole. Zooming out leaves
    /// the first-person view (0.6 m back, 0.5 m up); any adjustment leaves
    /// the look-down view.
    #[expect(
        clippy::neg_cmp_op_on_partial_ord,
        reason = "a NaN must fail the check, as it does in the game"
    )]
    pub fn adjust_distance(&mut self, adjustment: f32) {
        self.exit_look_down_for_adjustment();
        if !adjustment.is_finite() || adjustment == 0.0 {
            return;
        }
        if self.in_head && adjustment > 0.0 {
            self.in_head = false;
            self.target_direction_local = None;
            self.set_viewer_offset(LEAVE_HEAD_BACK, LEAVE_HEAD_UP);
            return;
        }
        let scale = 1.0 + adjustment * OFFSET_SCALE_PER_ADJUSTMENT;
        if !(scale > 0.0) || !scale.is_finite() {
            return;
        }
        let candidate = self.distance * scale;
        if adjustment < 0.0 && !(candidate > MINIMUM_OFFSET_LENGTH) {
            return;
        }
        self.try_write_offset(candidate, self.pitch, false);
    }

    /// Raise (positive) or lower the boom by 8 degrees per unit, keeping its
    /// length; refused whole past a limit. In first person it raises or
    /// lowers the look instead.
    pub fn adjust_pitch(&mut self, adjustment: f32) {
        self.exit_look_down_for_adjustment();
        if !adjustment.is_finite() || adjustment == 0.0 {
            return;
        }
        if self.in_head {
            let mut direction = self.target_direction_local.unwrap_or(Vec3::Y);
            direction.z = (direction.z + adjustment * IN_HEAD_DIRECTION_STEP)
                .clamp(-IN_HEAD_DIRECTION_LIMIT, IN_HEAD_DIRECTION_LIMIT);
            self.target_direction_local = Some(direction);
            return;
        }
        let candidate = self.pitch + adjustment * OFFSET_ANGLE_RADIANS;
        self.try_write_offset(self.distance, candidate, true);
    }

    /// Swing the boom around the pivot by 8 degrees per unit. In the
    /// first-person view the rotate turns the character instead and the
    /// view stays first person: it is collected for the frame to take
    /// ([`Self::take_first_person_turn`]).
    pub fn adjust_yaw(&mut self, adjustment: f32) {
        self.exit_look_down_for_adjustment();
        if !adjustment.is_finite() || adjustment == 0.0 {
            return;
        }
        // The mouse owns a held view: the orbit keys leave it alone.
        if self.held_view.is_some() {
            return;
        }
        if self.in_head {
            self.pending_first_person_rotate += adjustment;
            return;
        }
        self.yaw_offset += adjustment * OFFSET_ANGLE_RADIANS;
    }

    /// The player's camera settings. Turning modern mouse turning on or off
    /// puts the swing back to zero (behind the character).
    pub fn set_tuning(&mut self, tuning: ChaseTuning) {
        if tuning.modern_mouse_turning != self.tuning.modern_mouse_turning {
            self.yaw_offset = 0.0;
        }
        self.tuning = tuning;
    }

    /// Modern mouse turning takes the view: from now it looks along the
    /// yaw it looks along now (the character's facing and the swing), and
    /// it stays there while the character turns, until it is turned
    /// ([`Self::turn_held_view`]) or let go ([`Self::release_view`]). It
    /// follows the character rigidly while held (no easing), so it turns
    /// with the mouse at once; in the first-person view the look still
    /// eases. The orbit keys leave a held view alone, and neither a combat
    /// target nor the slope turns it.
    pub fn hold_view(&mut self) {
        if self.held_view.is_none() {
            let swing = if self.in_head { 0.0 } else { self.yaw_offset };
            self.held_view = Some(wrap_angle(self.subject_yaw + swing));
        }
    }

    /// Turn the held view by `radians` (positive turns it left).
    pub fn turn_held_view(&mut self, radians: f32) {
        if let Some(view) = self.held_view.as_mut()
            && radians.is_finite()
        {
            *view = wrap_angle(*view + radians);
        }
    }

    /// The yaw the held view looks along, while one is held.
    #[must_use]
    pub fn held_view_yaw(&self) -> Option<f32> {
        self.held_view
    }

    /// Let go of the held view: the camera stays where it is, as a swing
    /// from the character's facing, and follows the character again.
    pub fn release_view(&mut self) {
        if let Some(view) = self.held_view.take()
            && !self.in_head
        {
            self.yaw_offset = wrap_angle(view - self.subject_yaw);
        }
    }

    /// The character's facing at the last update (radians from +x,
    /// counter-clockwise).
    #[must_use]
    pub fn subject_yaw(&self) -> f32 {
        self.subject_yaw
    }

    /// The rotate collected in the first-person view since the last call,
    /// as a compass-heading step in degrees: a rotate left (negative
    /// adjustment) is +8, a rotate right -8. `None` when nothing is
    /// pending or the view is no longer first person.
    pub fn take_first_person_turn(&mut self) -> Option<f32> {
        let pending = std::mem::take(&mut self.pending_first_person_rotate);
        let step = if pending < 0.0 {
            FIRST_PERSON_TURN_STEP_DEGREES
        } else {
            -FIRST_PERSON_TURN_STEP_DEGREES
        };
        (self.in_head && pending != 0.0 && pending.is_finite()).then_some(step)
    }

    /// The camera keys held for `dt` seconds: each moves the boom by the
    /// adjustment speed times `dt` units (zoom 20 % a unit, raise and
    /// swing 8 degrees a unit).
    pub fn adjust_held(&mut self, keys: ChaseAdjustment, dt: f32) {
        let adjustment = self.tuning.adjustment_speed * dt;
        if keys.zoom_in {
            self.adjust_distance(-adjustment);
        }
        if keys.zoom_out {
            self.adjust_distance(adjustment);
        }
        if keys.raise {
            self.adjust_pitch(adjustment);
        }
        if keys.lower {
            self.adjust_pitch(-adjustment);
        }
        if keys.rotate_left {
            self.adjust_yaw(-adjustment);
        }
        if keys.rotate_right {
            self.adjust_yaw(adjustment);
        }
    }

    /// The mouse moved `dx`, `dy` pixels at `now_seconds` with the right
    /// button held: smoothed, scaled by the sensitivity, the boom swings
    /// and tilts (the vertical sense flipped when inverted).
    pub fn orbit_drag(&mut self, dx: f32, dy: f32, now_seconds: f32) {
        let (x, y) = self.filter_mouse_delta(dx, dy, 0.5, now_seconds);
        let invert = if self.tuning.invert_mouse_look_y_axis {
            -1.0
        } else {
            1.0
        };
        let sensitivity = self.tuning.mouse_look_sensitivity;
        self.adjust_yaw(-invert * x * sensitivity * MOUSE_ORBIT_SCALE);
        self.adjust_pitch(invert * y * sensitivity * MOUSE_ORBIT_SCALE);
    }

    /// A character entered the world: the camera starts over as it did at
    /// the first entry, in the default view (2.75 m back, 0.825 m up, no
    /// swing, not looking down, no map, not first person) with its eye
    /// placed afresh on the next update. The player's camera settings stay.
    pub fn reset_for_world_entry(&mut self) {
        *self = Self {
            tuning: self.tuning,
            ..Self::default()
        };
    }

    /// Back to the default view: 2.75 m back, 0.825 m up, no swing.
    pub fn set_default_view(&mut self) {
        self.looking_down = false;
        self.map_mode = false;
        self.in_head = false;
        self.target_direction_local = None;
        self.yaw_offset = 0.0;
        self.pivot_height = 1.5;
        // A held view comes back behind the character too.
        if self.held_view.is_some() {
            self.held_view = Some(self.subject_yaw);
        }
        let (back, up) = default_view_offset();
        self.set_viewer_offset(back, up);
    }

    /// The first-person view: the eye 0.18 m ahead of the pivot, looking
    /// along the heading.
    pub fn set_first_person_view(&mut self) {
        self.looking_down = false;
        self.map_mode = false;
        self.in_head = true;
        self.target_direction_local = None;
        self.yaw_offset = 0.0;
        if self.held_view.is_some() {
            self.held_view = Some(self.subject_yaw);
        }
        self.distance = FIRST_PERSON_FORWARD;
        self.pitch = 0.0;
        self.initialised = false;
    }

    /// The look-down view on or off (off returns to the view before it).
    pub fn toggle_look_down_view(&mut self) {
        if self.looking_down {
            self.restore_look_down_view();
            return;
        }
        self.save_look_down_view();
        self.looking_down = true;
        self.map_mode = false;
        self.in_head = false;
        self.target_direction_local = Some(LOOK_DOWN_DIRECTION);
        self.set_viewer_offset(LOOK_DOWN_BACK, DEFAULT_UP);
    }

    /// The map view (high overhead) on or off.
    pub fn toggle_map_mode_view(&mut self) {
        if self.map_mode {
            self.restore_look_down_view();
            return;
        }
        if !self.looking_down {
            self.save_look_down_view();
        }
        self.looking_down = true;
        self.map_mode = true;
        self.in_head = false;
        self.target_direction_local = Some(LOOK_DOWN_DIRECTION);
        self.set_viewer_offset(MAP_BACK, DEFAULT_UP);
    }

    fn save_look_down_view(&mut self) {
        self.saved = SavedView {
            in_head: self.in_head,
            distance: self.distance,
            pitch: self.pitch,
            yaw_offset: self.yaw_offset,
            target_direction_local: self.target_direction_local,
        };
    }

    fn restore_look_down_view(&mut self) {
        self.distance = self.saved.distance;
        self.pitch = self.saved.pitch;
        self.yaw_offset = self.saved.yaw_offset;
        self.target_direction_local = self.saved.target_direction_local;
        self.in_head = self.saved.in_head;
        self.looking_down = false;
        self.map_mode = false;
    }

    fn exit_look_down_for_adjustment(&mut self) {
        if self.looking_down {
            self.restore_look_down_view();
        }
    }

    fn set_viewer_offset(&mut self, back: f32, up: f32) {
        self.distance = back.hypot(up);
        self.pitch = up.atan2(back);
    }

    /// Smooth a raw mouse motion: averaged with the previous sample when it
    /// came within [`MOUSE_LOW_PASS_WINDOW_SECONDS`], blended by `weight`
    /// (the y axis always averages).
    pub fn filter_mouse_delta(
        &mut self,
        raw_x: f32,
        raw_y: f32,
        weight: f32,
        now: f32,
    ) -> (f32, f32) {
        let x = filter_mouse_axis(
            raw_x,
            weight,
            now,
            &mut self.last_mouse_delta.0,
            &mut self.last_filter_time,
            MOUSE_LOW_PASS_WINDOW_SECONDS,
        );
        let mut y_time = self.last_filter_time - 1.0;
        let y = filter_mouse_axis(
            raw_y,
            weight,
            now,
            &mut self.last_mouse_delta.1,
            &mut y_time,
            MOUSE_LOW_PASS_WINDOW_SECONDS,
        );
        (x, y)
    }

    #[expect(
        clippy::neg_cmp_op_on_partial_ord,
        reason = "a NaN must fail every check, as it does in the game"
    )]
    fn try_write_offset(
        &mut self,
        candidate_distance: f32,
        candidate_pitch: f32,
        preserve_horizontal_sign: bool,
    ) -> bool {
        if !candidate_distance.is_finite()
            || !candidate_pitch.is_finite()
            || !(candidate_distance > 0.0)
        {
            return false;
        }
        let current_horizontal = self.distance * self.pitch.cos();
        let candidate_horizontal = candidate_distance * candidate_pitch.cos();
        if preserve_horizontal_sign
            && ((current_horizontal > 0.0 && candidate_horizontal < 0.0)
                || (current_horizontal < 0.0 && candidate_horizontal > 0.0))
        {
            return false;
        }
        let x = candidate_horizontal * self.yaw_offset.sin();
        let y = -candidate_horizontal * self.yaw_offset.cos();
        let z = candidate_distance * candidate_pitch.sin();
        if !(x.abs() < MAXIMUM_HORIZONTAL_COMPONENT)
            || !(y.abs() < MAXIMUM_HORIZONTAL_COMPONENT)
            || !(z < MAXIMUM_VERTICAL_COMPONENT)
            || !(z > MINIMUM_VERTICAL_COMPONENT)
        {
            return false;
        }
        self.distance = candidate_distance;
        self.pitch = candidate_pitch;
        true
    }
}

/// The camera's heading: the player's flat facing, tilted into the plane
/// it moves on when slope alignment applies (the contact plane on the
/// ground; in the air, a plane built from the average velocity with its
/// vertical part flattened to a tenth). A velocity too small or along a
/// world axis does not tilt.
#[must_use]
pub fn compute_heading(
    average_velocity: Vec3,
    yaw: f32,
    in_contact: bool,
    contact_normal: Vec3,
    align_to_slope: bool,
) -> Vec3 {
    let base = Vec3::new(yaw.cos(), yaw.sin(), 0.0);
    if !align_to_slope {
        return base;
    }
    let length = average_velocity.length();
    if length < MIN_TILT_COMPONENT {
        return base;
    }
    let norm = average_velocity / length;
    if norm.x.abs() < MIN_TILT_COMPONENT || norm.y.abs() < MIN_TILT_COMPONENT {
        return base;
    }
    let normal = if in_contact {
        if contact_normal.length() >= MIN_TILT_COMPONENT {
            contact_normal.normalize()
        } else {
            contact_normal
        }
    } else {
        let t = Vec3::new(norm.x, norm.y, AIRBORNE_PLANE_VERTICAL_SCALE * norm.z);
        if t.length() >= MIN_TILT_COMPONENT {
            ac_physics::frame_math::set_vector_heading(Quat::IDENTITY, t) * Vec3::Z
        } else {
            Vec3::Z
        }
    };
    let projected = base - normal * base.dot(normal);
    if projected.length() < MIN_TILT_COMPONENT {
        return base;
    }
    projected.normalize()
}

/// The boom stays behind the player with at most one metre of side
/// offset (slope alignment applies only then).
#[must_use]
pub fn is_orbit_within_alignment_threshold(distance: f32, pitch: f32, yaw_offset: f32) -> bool {
    let horizontal = distance * pitch.cos();
    horizontal * yaw_offset.sin().abs() <= 1.0 && yaw_offset.cos() >= 0.0
}

/// The heading toward a tracked point from the pivot (`None` without a
/// point or on it).
#[must_use]
pub fn compute_tracked_heading(pivot: Vec3, tracked_target: Option<Vec3>) -> Option<Vec3> {
    let toward = tracked_target? - pivot;
    (toward.length_squared() >= 1e-8).then(|| toward.normalize())
}

/// The first-person pose: the eye 0.18 m ahead of the pivot, looking along
/// the heading (or the raised/lowered local direction).
#[must_use]
pub fn compute_in_head_pose(
    pivot: Vec3,
    heading: Vec3,
    target_direction_local: Option<Vec3>,
) -> (Vec3, Vec3) {
    let heading_forward = heading.normalize();
    let target_forward = match target_direction_local {
        Some(local) => {
            let (forward, right, up) = basis(heading_forward);
            (forward * local.y - right * local.x + up * local.z).normalize()
        }
        None => heading_forward,
    };
    (
        pivot + heading_forward * FIRST_PERSON_FORWARD,
        target_forward,
    )
}

/// The look-down and map pose: looking along the local direction, the eye
/// on the boom behind and above that look.
#[must_use]
pub fn compute_target_direction_pose(
    pivot: Vec3,
    heading: Vec3,
    distance: f32,
    pitch: f32,
    target_direction_local: Vec3,
) -> (Vec3, Vec3) {
    let (forward, right, up) = basis(heading);
    let target_forward = (forward * target_direction_local.y - right * target_direction_local.x
        + up * target_direction_local.z)
        .normalize();
    let (_, _, target_up) = basis(target_forward);
    let back = distance * pitch.cos();
    let rise = distance * pitch.sin();
    (
        pivot - target_forward * back + target_up * rise,
        target_forward,
    )
}

/// Append a velocity sample to the five-sample ring (the oldest drops out).
pub fn push_velocity(ring: &mut [Vec3; 5], count: &mut usize, sample: Vec3) {
    ring.copy_within(1.., 0);
    ring[4] = sample;
    if *count < 5 {
        *count += 1;
    }
}

/// The average of the newest `count` samples.
#[must_use]
pub fn average_velocity(ring: &[Vec3; 5], count: usize) -> Vec3 {
    if count == 0 {
        return Vec3::ZERO;
    }
    ring[5 - count..].iter().copied().sum::<Vec3>() / count as f32
}

/// How see-through the player is with the eye `distance` from the pivot:
/// 0 from 0.45 m, 1 within 0.2 m, linear between.
#[must_use]
pub fn compute_translucency(distance: f32) -> f32 {
    const FAR: f32 = 0.45;
    const NEAR: f32 = 0.20;
    if distance >= FAR {
        0.0
    } else if distance <= NEAR {
        1.0
    } else {
        1.0 - (NEAR - distance) / (NEAR - FAR)
    }
}

/// An orthonormal frame around a heading: forward, right, up (right-handed,
/// z-up). A near-vertical heading takes +x as its secondary axis.
#[must_use]
pub fn basis(heading: Vec3) -> (Vec3, Vec3, Vec3) {
    let forward = heading.normalize();
    let right = if forward.z.abs() > 0.99 {
        forward.cross(Vec3::X).normalize()
    } else {
        forward.cross(Vec3::Z).normalize()
    };
    let up = right.cross(forward);
    (forward, right, up)
}

/// Where the chase camera wants to be: on a boom of `distance` meters
/// raised `pitch` radians, swung `yaw_offset` radians, behind the pivot
/// along `heading`; looking at the pivot. Returns (eye, look direction).
#[must_use]
pub fn desired_pose(
    pivot: Vec3,
    heading: Vec3,
    distance: f32,
    pitch: f32,
    yaw_offset: f32,
) -> (Vec3, Vec3) {
    let (forward, right, up) = basis(heading);
    let boom = (forward * yaw_offset.cos() - right * yaw_offset.sin()).normalize();
    let horizontal = distance * pitch.cos();
    let vertical = distance * pitch.sin();
    let eye = pivot - boom * horizontal + up * vertical;
    (eye, (pivot - eye).normalize())
}

/// How far toward its target the camera moves this frame (0..=1).
#[must_use]
pub fn damping_alpha(stiffness: f32, dt: f32) -> f32 {
    (stiffness * dt * 10.0).clamp(0.0, 1.0)
}

/// Holds the camera exactly still once both its move and its turn are below
/// the snap thresholds, so an eased camera does not creep forever. Returns
/// (eye, forward, held).
#[must_use]
pub fn convergence_snap(
    eye: Vec3,
    forward: Vec3,
    candidate_eye: Vec3,
    candidate_forward: Vec3,
) -> (Vec3, Vec3, bool) {
    let still = candidate_eye.distance(eye) < TRANSLATION_SNAP_EPSILON
        && candidate_forward.distance(forward) < ROTATION_SNAP_EPSILON;
    if still {
        (eye, forward, true)
    } else {
        (candidate_eye, candidate_forward, false)
    }
}

/// A camera circling a target point: used when there is no player.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct OrbitCamera {
    /// The point looked at.
    pub target: Vec3,
    /// Distance from the target, meters.
    pub distance: f32,
    /// Angle around the target from +x toward +y, radians.
    pub yaw: f32,
    /// Elevation above the horizontal, radians.
    pub pitch: f32,
}

impl Default for OrbitCamera {
    fn default() -> Self {
        Self {
            target: Vec3::new(96.0, 96.0, 0.0),
            distance: 300.0,
            yaw: std::f32::consts::FRAC_PI_4,
            pitch: std::f32::consts::FRAC_PI_6,
        }
    }
}

impl OrbitCamera {
    /// Where the eye is.
    #[must_use]
    pub fn eye(&self) -> Vec3 {
        self.target
            + Vec3::new(
                self.distance * self.pitch.cos() * self.yaw.cos(),
                self.distance * self.pitch.cos() * self.yaw.sin(),
                self.distance * self.pitch.sin(),
            )
    }

    /// The view matrix.
    #[must_use]
    pub fn view(&self) -> Mat4 {
        glam::camera::rh::view::look_at_mat4(self.eye(), self.target, Vec3::Z)
    }

    /// Turn with the mouse while the left button is held: `dx`, `dy` in
    /// pixels. Elevation stays between 0.1 and 1.5 radians (about 6 and 86
    /// degrees).
    pub fn drag(&mut self, dx: f32, dy: f32, sensitivity: f32) {
        self.yaw -= dx * 0.005 * sensitivity;
        self.pitch = (self.pitch + dy * 0.005 * sensitivity).clamp(0.1, 1.5);
    }
}

/// Elevation limit of the fly camera: about 89 degrees up or down.
pub const FLY_PITCH_LIMIT: f32 = 1.5533;

/// A free camera for looking around: moves with the movement keys in the
/// horizontal plane (and straight up and down), turns with the mouse.
#[derive(Debug, Clone, PartialEq)]
pub struct FlyCamera {
    /// Where the eye is.
    pub position: Vec3,
    /// Heading from +x toward +y, radians (pi/2 faces north, +y).
    pub yaw: f32,
    /// Elevation, radians (negative looks down).
    pub pitch: f32,
    /// Normal speed, meters per second.
    pub move_speed: f32,
    /// Speed while boosting, meters per second.
    pub boost_speed: f32,
    /// Radians per pixel of mouse movement.
    pub mouse_sensitivity: f32,
}

impl Default for FlyCamera {
    fn default() -> Self {
        Self {
            position: Vec3::new(96.0, 96.0, 150.0),
            yaw: std::f32::consts::FRAC_PI_2,
            pitch: -0.3,
            move_speed: 12.0,
            boost_speed: 50.0,
            mouse_sensitivity: 0.003,
        }
    }
}

/// Which movement keys are held for the fly camera.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct FlyInput {
    /// Forward along the heading.
    pub forward: bool,
    /// Left (sideways).
    pub left: bool,
    /// Backward.
    pub backward: bool,
    /// Right (sideways).
    pub right: bool,
    /// Straight up.
    pub up: bool,
    /// Straight down.
    pub down: bool,
    /// Move at boost speed.
    pub boost: bool,
}

impl FlyCamera {
    /// Where the camera looks.
    #[must_use]
    pub fn forward(&self) -> Vec3 {
        let cp = self.pitch.cos();
        Vec3::new(cp * self.yaw.cos(), cp * self.yaw.sin(), self.pitch.sin())
    }

    /// The view matrix.
    #[must_use]
    pub fn view(&self) -> Mat4 {
        glam::camera::rh::view::look_at_mat4(self.position, self.position + self.forward(), Vec3::Z)
    }

    /// Move for `dt` seconds with the keys in `input`. Forward and sideways
    /// stay horizontal whatever the pitch, so flying forward never dives
    /// into the ground.
    pub fn update(&mut self, dt: f32, input: FlyInput) {
        let speed = if input.boost {
            self.boost_speed
        } else {
            self.move_speed
        };
        let step = speed * dt;
        let flat_forward = Vec3::new(self.yaw.cos(), self.yaw.sin(), 0.0);
        let right = Vec3::new(self.yaw.sin(), -self.yaw.cos(), 0.0);
        if input.forward {
            self.position += flat_forward * step;
        }
        if input.backward {
            self.position -= flat_forward * step;
        }
        if input.left {
            self.position -= right * step;
        }
        if input.right {
            self.position += right * step;
        }
        if input.up {
            self.position += Vec3::Z * step;
        }
        if input.down {
            self.position -= Vec3::Z * step;
        }
    }

    /// Turn by a mouse movement of `dx`, `dy` pixels: right turns right,
    /// down looks down.
    pub fn look(&mut self, dx: f32, dy: f32) {
        self.yaw -= dx * self.mouse_sensitivity;
        self.pitch =
            (self.pitch - dy * self.mouse_sensitivity).clamp(-FLY_PITCH_LIMIT, FLY_PITCH_LIMIT);
    }
}

/// What the window knows, this frame, about whether the camera may start
/// following the player.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PlayerModeFacts {
    /// A character is in the world.
    pub live_in_world: bool,
    /// The character's object is present, with a position.
    pub player_present: bool,
    /// The session's physics moves the character's body (its movement
    /// controller is in charge of it).
    pub controller_ready: bool,
    /// The world around the character is shown: the login reveal is over.
    pub world_ready: bool,
    /// How many times a character has entered the world in what the
    /// window has seen of the session: a new count is a new entry (a log
    /// in after a log out), even when no frame saw the character gone.
    pub world_entry: u64,
}

/// The one-shot trigger that puts the camera into player mode on its own
/// once the character can be followed: armed when a character enters the
/// world, it fires on the first frame every condition holds, then
/// disarms. Toggling player mode or the fly camera by hand cancels it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PlayerModeAutoEntry {
    armed: bool,
}

impl PlayerModeAutoEntry {
    /// Whether the trigger is waiting to fire.
    #[must_use]
    pub fn is_armed(&self) -> bool {
        self.armed
    }

    /// Arm the trigger (again: no change).
    pub fn arm(&mut self) {
        self.armed = true;
    }

    /// Disarm without firing.
    pub fn cancel(&mut self) {
        self.armed = false;
    }

    /// One frame's check. Armed with player mode already on (entered some
    /// other way), it retires; armed with every condition met, it disarms
    /// and answers `true`: the caller enters player mode. Otherwise
    /// nothing changes.
    pub fn try_enter(&mut self, facts: &PlayerModeFacts, player_mode_active: bool) -> bool {
        if !self.armed {
            return false;
        }
        if player_mode_active {
            self.armed = false;
            return false;
        }
        if !facts.live_in_world
            || !facts.player_present
            || !facts.controller_ready
            || !facts.world_ready
        {
            return false;
        }
        self.armed = false;
        true
    }
}

/// Which camera the window looks through.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CameraMode {
    /// Circling a point (no player to follow).
    Orbit,
    /// Flying freely.
    Fly,
    /// Following the player.
    Chase,
}

/// The window's cameras and which one is active.
#[derive(Debug, Clone)]
pub struct CameraController {
    /// The orbit camera.
    pub orbit: OrbitCamera,
    /// The fly camera.
    pub fly: FlyCamera,
    /// The chase camera.
    pub chase: ChaseCamera,
    mode: CameraMode,
    facts: PlayerModeFacts,
    auto_entry: PlayerModeAutoEntry,
}

impl CameraController {
    /// Cameras in orbit mode, the orbit set from the start-up overrides.
    #[must_use]
    pub fn new(orbit: OrbitCamera) -> Self {
        Self {
            orbit,
            fly: FlyCamera::default(),
            chase: ChaseCamera::default(),
            mode: CameraMode::Orbit,
            facts: PlayerModeFacts::default(),
            auto_entry: PlayerModeAutoEntry::default(),
        }
    }

    /// The active camera.
    #[must_use]
    pub fn mode(&self) -> CameraMode {
        self.mode
    }

    /// The overhead map view is what the window shows: the chase camera is
    /// in charge with its map view on. Its eye is hundreds of metres up, so
    /// the frame turns distance fog off and keeps buildings at full detail.
    #[must_use]
    pub fn is_overhead_view(&self) -> bool {
        self.mode == CameraMode::Chase && self.chase.is_map_mode()
    }

    /// The player-mode trigger.
    #[must_use]
    pub fn auto_entry(&self) -> PlayerModeAutoEntry {
        self.auto_entry
    }

    /// This frame's facts about the player. A character entering the world
    /// (or one already in it when the window attaches) puts the chase
    /// camera back in its default view, as the game does when it creates
    /// the player, and arms the player-mode trigger, which puts the chase
    /// camera in charge once the character can be followed; a character
    /// leaving the world cancels it and falls back to orbiting. Fly mode is
    /// left alone.
    pub fn follow_player(&mut self, facts: PlayerModeFacts) {
        let was_in_world = self.facts.live_in_world;
        let entered =
            facts.live_in_world && (!was_in_world || facts.world_entry != self.facts.world_entry);
        self.facts = facts;
        if entered {
            // Every entry starts the chase camera over in the default view,
            // whatever the last character left it at.
            self.chase.reset_for_world_entry();
            self.auto_entry.arm();
        } else if !facts.live_in_world && was_in_world {
            self.auto_entry.cancel();
            if self.mode == CameraMode::Chase {
                self.mode = CameraMode::Orbit;
            }
        }
        if self
            .auto_entry
            .try_enter(&facts, self.mode == CameraMode::Chase)
        {
            self.mode = CameraMode::Chase;
        }
    }

    /// The character is going into portal space. For a login, player mode
    /// is entered first if it is not on (the character must be present).
    /// Once the session's physics moves the body, portal space is taken and
    /// the trigger is no longer needed; until then the trigger stays armed
    /// and the answer is `false`.
    pub fn enter_portal_space(&mut self, login: bool) -> bool {
        if login && self.mode != CameraMode::Chase {
            if !self.facts.player_present || !self.facts.controller_ready {
                return false;
            }
            self.mode = CameraMode::Chase;
        }
        if !self.facts.controller_ready {
            return false;
        }
        self.auto_entry.cancel();
        true
    }

    /// The fly toggle: leaving fly mode returns to the chase camera when
    /// there is a player, else to orbiting; entering it resumes the fly
    /// camera where it was left (first time: 150 m above the middle of the
    /// player's landblock). It cancels the player-mode trigger.
    pub fn toggle_fly(&mut self) {
        self.auto_entry.cancel();
        self.mode = match self.mode {
            CameraMode::Fly if self.facts.player_present => CameraMode::Chase,
            CameraMode::Fly => CameraMode::Orbit,
            _ => CameraMode::Fly,
        };
    }

    /// The player mode toggle (only with a character in the world): from
    /// orbit or fly to the chase camera when there is a player, and from
    /// the chase camera back to orbiting. It cancels the player-mode
    /// trigger.
    pub fn toggle_player_mode(&mut self) {
        if !self.facts.live_in_world {
            return;
        }
        self.auto_entry.cancel();
        self.mode = match self.mode {
            CameraMode::Chase => CameraMode::Orbit,
            _ if self.facts.player_present => CameraMode::Chase,
            other => other,
        };
    }

    /// The active camera's view matrix and eye.
    #[must_use]
    pub fn view_and_eye(&self) -> (Mat4, Vec3) {
        match self.mode {
            CameraMode::Orbit => (self.orbit.view(), self.orbit.eye()),
            CameraMode::Fly => (self.fly.view(), self.fly.position),
            CameraMode::Chase => (self.chase.view(), self.chase.eye()),
        }
    }

    /// Zoom: positive moves out (the chase boom, or the orbit distance).
    pub fn zoom(&mut self, steps: f32) {
        self.chase.adjust_distance(steps);
        self.orbit.distance = (self.orbit.distance * (1.0 + 0.2 * steps)).clamp(5.0, 2000.0);
    }

    /// The drawing frame's origin moved by `delta`: move every camera's
    /// position with it.
    pub fn translate(&mut self, delta: Vec3) {
        self.chase.translate(delta);
        self.fly.position += delta;
        self.orbit.target += delta;
    }
}

/// Orbit camera start-up overrides: distance in meters, yaw and pitch in
/// degrees, each `None` when not given or out of range.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct OrbitOverrides {
    /// Distance from the target, meters (positive).
    pub distance_meters: Option<f32>,
    /// Angle around the target, degrees.
    pub yaw_degrees: Option<f32>,
    /// Elevation, degrees (-89 to 89).
    pub pitch_degrees: Option<f32>,
}

impl OrbitOverrides {
    /// Parse the three values (as text) the way OpenAC reads its
    /// environment: a distance must be positive and finite, a yaw finite,
    /// a pitch finite and within -89..=89; anything else is ignored.
    #[must_use]
    pub fn parse(distance: Option<&str>, yaw: Option<&str>, pitch: Option<&str>) -> Self {
        let finite = |s: Option<&str>| {
            s.and_then(|s| s.trim().parse::<f32>().ok())
                .filter(|v| v.is_finite())
        };
        Self {
            distance_meters: finite(distance).filter(|d| *d > 0.0),
            yaw_degrees: finite(yaw),
            pitch_degrees: finite(pitch).filter(|p| (-89.0..=89.0).contains(p)),
        }
    }

    /// The default orbit camera with these overrides applied.
    #[must_use]
    pub fn orbit(&self) -> OrbitCamera {
        let mut orbit = OrbitCamera::default();
        if let Some(d) = self.distance_meters {
            orbit.distance = d;
        }
        if let Some(y) = self.yaw_degrees {
            orbit.yaw = y.to_radians();
        }
        if let Some(p) = self.pitch_degrees {
            orbit.pitch = p.to_radians();
        }
        orbit
    }
}

#[cfg(test)]
#[path = "chase_camera_tests.rs"]
mod chase_tests;

#[cfg(test)]
mod tests {
    use super::*;

    fn close(a: f32, b: f32, eps: f32) -> bool {
        (a - b).abs() <= eps
    }

    // Golden values: OpenAC tests/AcDream.App.Tests/Rendering/RetailFieldOfViewTests.cs.

    #[test]
    fn default_fov_matches_the_reference_formula() {
        for (aspect, expected) in [
            (4.0 / 3.0, 1.273_62),
            (16.0 / 9.0, 0.936_24),
            (21.0 / 9.0, 0.703_27),
        ] {
            let fov = applied_vertical_fov(DEFAULT_GAME_FOV_RADIANS, aspect).unwrap();
            // xUnit precision 4: equal after rounding to four decimals.
            let round4 = |x: f32| (x * 1e4).round();
            assert_eq!(round4(fov), round4(expected), "{aspect}: {fov}");
        }
    }

    #[test]
    fn the_formula_holds_the_horizontal_view_roughly_constant() {
        for aspect in [4.0_f32 / 3.0, 16.0 / 9.0, 21.0 / 9.0] {
            let fov = applied_vertical_fov(DEFAULT_GAME_FOV_RADIANS, aspect).unwrap();
            let horizontal = 2.0 * ((fov / 2.0).tan() * aspect).atan();
            assert!((80f32.to_radians()..=90f32.to_radians()).contains(&horizontal));
        }
    }

    #[test]
    fn rejects_results_outside_the_accepted_interval() {
        for aspect in [0.05, 0.1, 0.55] {
            assert_eq!(applied_vertical_fov(DEFAULT_GAME_FOV_RADIANS, aspect), None);
        }
    }

    #[test]
    fn default_lens_is_the_formula_at_the_default_pair() {
        assert_eq!(
            Lens::default().fov_y(),
            applied_vertical_fov(DEFAULT_GAME_FOV_RADIANS, 16.0 / 9.0).unwrap()
        );
    }

    #[test]
    fn set_aspect_recomputes_the_vertical_angle() {
        let mut lens = Lens::default();
        lens.set_aspect(4.0 / 3.0);
        assert_eq!(lens.aspect(), 4.0 / 3.0);
        assert!(close(
            lens.fov_y(),
            applied_vertical_fov(lens.game_fov(), 4.0 / 3.0).unwrap(),
            1e-5
        ));
    }

    #[test]
    fn a_rejected_aspect_keeps_the_previous_angle_but_takes_the_aspect() {
        let mut lens = Lens::default();
        let before = lens.fov_y();
        lens.set_aspect(0.5);
        assert_eq!(lens.aspect(), 0.5);
        assert_eq!(lens.fov_y(), before);
    }

    #[test]
    fn set_game_fov_recomputes_at_the_current_aspect() {
        let mut lens = Lens::default();
        let narrow = 45f32.to_radians();
        lens.set_game_fov(narrow);
        assert_eq!(lens.game_fov(), narrow);
        assert!(close(
            lens.fov_y(),
            applied_vertical_fov(narrow, 16.0 / 9.0).unwrap(),
            1e-5
        ));
    }

    #[test]
    fn projection_puts_the_near_plane_at_a_tenth_of_a_meter() {
        let p = Lens::default().projection();
        let at = |z: f32| {
            let c = p * glam::Vec4::new(0.0, 0.0, -z, 1.0);
            c.z / c.w
        };
        assert!(close(at(NEAR_PLANE), 0.0, 1e-5));
        assert!(close(at(FAR_PLANE), 1.0, 1e-4));
    }

    #[test]
    fn yaw_of_reads_the_local_forward_axis() {
        assert!(close(
            yaw_of(Quat::IDENTITY),
            std::f32::consts::FRAC_PI_2,
            1e-6
        ));
        let east = Quat::from_rotation_z(-std::f32::consts::FRAC_PI_2);
        assert!(close(yaw_of(east), 0.0, 1e-6));
    }

    // Golden values: OpenAC tests/AcDream.App.Tests/Rendering/RetailChaseCameraTests.cs.

    #[test]
    fn desired_pose_with_no_orbit_puts_the_boom_behind_the_heading() {
        let pivot = Vec3::new(10.0, 20.0, 1.5);
        let (eye, forward) = desired_pose(pivot, Vec3::X, 5.0, 0.0, 0.0);
        assert_eq!(eye, Vec3::new(5.0, 20.0, 1.5));
        assert_eq!(forward, Vec3::X);
    }

    #[test]
    fn desired_pose_keeps_the_orbit_and_looks_at_the_pivot() {
        let pivot = Vec3::new(10.0, 20.0, 1.5);
        let (eye, forward) = desired_pose(pivot, Vec3::X, 5.0, 0.0, std::f32::consts::FRAC_PI_2);
        assert!(close(eye.x, 10.0, 1e-5));
        assert!(close(eye.y, 15.0, 1e-5));
        assert!(close(eye.z, 1.5, 1e-5));
        assert_eq!(forward, (pivot - eye).normalize());
    }

    #[test]
    fn basis_of_a_horizontal_heading_is_orthonormal_with_up_up() {
        let (f, r, u) = basis(Vec3::X);
        for v in [f, r, u] {
            assert!(close(v.length(), 1.0, 1e-5));
        }
        assert!(close(f.dot(r), 0.0, 1e-5));
        assert!(close(f.dot(u), 0.0, 1e-5));
        assert!(close(r.dot(u), 0.0, 1e-5));
        assert!(close(u.x, 0.0, 1e-5) && close(u.y, 0.0, 1e-5) && u.z > 0.0);
    }

    #[test]
    fn basis_of_a_vertical_heading_uses_x_for_right() {
        let (_, r, u) = basis(Vec3::Z);
        assert!(close(r.length(), 1.0, 1e-5));
        assert!(close(u.length(), 1.0, 1e-5));
        assert!(close(r.dot(u), 0.0, 1e-5));
    }

    #[test]
    fn damping_alpha_follows_the_default_stiffness() {
        assert!(close(damping_alpha(0.45, 1.0 / 60.0), 0.075, 1e-4));
        assert_eq!(damping_alpha(0.45, 1.0), 1.0);
        assert_eq!(damping_alpha(0.45, 0.0), 0.0);
        assert_eq!(damping_alpha(0.0, 1.0), 0.0);
    }

    #[test]
    fn first_update_snaps_to_the_target() {
        let mut cam = ChaseCamera {
            distance: 5.0,
            pitch: 0.0,
            ..ChaseCamera::default()
        };
        cam.update(
            &ChaseSubject::standing(Vec3::new(10.0, 20.0, 30.0), 0.0),
            1.0 / 60.0,
            None,
        );
        assert!(close(cam.eye().x, 5.0, 1e-4));
        assert!(close(cam.eye().y, 20.0, 1e-4));
        assert!(close(cam.eye().z, 31.5, 1e-4));
    }

    #[test]
    fn second_update_eases_toward_the_target() {
        let mut cam = ChaseCamera {
            distance: 5.0,
            pitch: 0.0,
            ..ChaseCamera::default()
        };
        cam.update(&ChaseSubject::standing(Vec3::ZERO, 0.0), 1.0 / 60.0, None);
        cam.update(
            &ChaseSubject::standing(Vec3::new(10.0, 0.0, 0.0), 0.0),
            1.0 / 60.0,
            None,
        );
        assert!(close(cam.eye().x, -4.25, 1e-3));
        assert!(close(cam.eye().y, 0.0, 1e-4));
        assert!(close(cam.eye().z, 1.5, 1e-4));
    }

    /// A probe that returns the wanted eye in a fixed cell.
    struct Passthrough(u32);

    impl CameraCollisionProbe for Passthrough {
        fn sweep_eye(
            &self,
            _pivot: Vec3,
            desired_eye: Vec3,
            _cell_id: u32,
            _self_id: u32,
            _player_position: Vec3,
        ) -> crate::camera_collision::CameraSweep {
            crate::camera_collision::CameraSweep {
                eye: desired_eye,
                viewer_cell: self.0,
            }
        }
    }

    /// A probe that stops the eye at a wall `x = wall`, or fails (no cell).
    struct Wall(Option<f32>);

    impl CameraCollisionProbe for Wall {
        fn sweep_eye(
            &self,
            _pivot: Vec3,
            desired_eye: Vec3,
            _cell_id: u32,
            _self_id: u32,
            player_position: Vec3,
        ) -> crate::camera_collision::CameraSweep {
            match self.0 {
                Some(wall) => crate::camera_collision::CameraSweep {
                    eye: Vec3::new(desired_eye.x.max(wall), desired_eye.y, desired_eye.z),
                    viewer_cell: 0xA9B4_0100,
                },
                None => crate::camera_collision::CameraSweep {
                    eye: player_position,
                    viewer_cell: 0,
                },
            }
        }
    }

    // OpenAC `CameraParkStabilityTests.ParkedCamera_StaticInputs_ReachesBitStableFixedPoint`
    // (its flat case: a body standing on flat ground).
    #[test]
    fn a_parked_collided_camera_is_a_bit_exact_fixed_point() {
        let mut cam = ChaseCamera::default();
        let probe = Passthrough(0x8A02_0142);
        let collision = ChaseCollision {
            probe: &probe,
            cell_id: 0x8A02_0142,
            self_id: 5,
        };
        let player = Vec3::new(49.5, -39.9, -5.9);
        let dt = 1.0 / 1500.0;
        for _ in 0..20_000 {
            cam.update(&ChaseSubject::standing(player, 1.83), dt, Some(collision));
        }
        let (eye, view) = (cam.eye(), cam.view());
        for _ in 0..2_000 {
            cam.update(&ChaseSubject::standing(player, 1.83), dt, Some(collision));
            assert_eq!(cam.eye(), eye, "the parked eye wandered");
            assert_eq!(cam.view(), view, "the parked view changed");
        }
        assert_eq!(cam.viewer_cell(), Some(0x8A02_0142));
    }

    #[test]
    fn the_swept_eye_is_published_and_eased_from() {
        let mut cam = ChaseCamera {
            distance: 5.0,
            pitch: 0.0,
            ..ChaseCamera::default()
        };
        let probe = Wall(Some(-2.0));
        let collision = ChaseCollision {
            probe: &probe,
            cell_id: 0xA9B4_0101,
            self_id: 1,
        };
        // Wanted 5 m behind (x = -5); the wall stops it at x = -2.
        cam.update(
            &ChaseSubject::standing(Vec3::ZERO, 0.0),
            1.0 / 60.0,
            Some(collision),
        );
        assert!(close(cam.eye().x, -2.0, 1e-5));
        assert_eq!(cam.viewer_cell(), Some(0xA9B4_0100));
        // Without the wall the next frame eases from the stopped eye.
        cam.update(&ChaseSubject::standing(Vec3::ZERO, 0.0), 1.0 / 60.0, None);
        assert!(close(cam.eye().x, -2.0 + (-5.0 + 2.0) * 0.075, 1e-4));
        assert_eq!(cam.viewer_cell(), None);
    }

    #[test]
    fn a_failed_sweep_puts_the_eye_on_the_player_in_the_players_cell() {
        let mut cam = ChaseCamera::default();
        let probe = Wall(None);
        let player = Vec3::new(3.0, 4.0, 5.0);
        cam.update(
            &ChaseSubject::standing(player, 0.0),
            1.0 / 60.0,
            Some(ChaseCollision {
                probe: &probe,
                cell_id: 0xA9B4_0101,
                self_id: 1,
            }),
        );
        assert_eq!(cam.eye(), player);
        assert_eq!(cam.viewer_cell(), Some(0xA9B4_0101));
    }

    #[test]
    fn the_mouse_filter_averages_within_its_window() {
        // OpenAC RetailChaseCameraTests.MouseFilter_* golden values.
        let (mut last, mut time) = (5.0, 0.0);
        let out = filter_mouse_axis(10.0, 0.5, 1.0, &mut last, &mut time, 0.25);
        assert!(close(out, 10.0, 1e-5));
        let (mut last, mut time) = (10.0, 0.0);
        let out = filter_mouse_axis(20.0, 0.5, 0.1, &mut last, &mut time, 0.25);
        assert!(close(out, 17.5, 1e-5));
        let (mut last, mut time) = (10.0, 0.0);
        let out = filter_mouse_axis(20.0, 0.0, 0.1, &mut last, &mut time, 0.25);
        assert!(close(out, 20.0, 1e-5));
    }

    #[test]
    fn the_camera_settings_reach_the_chase_camera() {
        // OpenAC RuntimeSettingsTargets.ApplyCameraTurning: stiffness is the
        // translation and rotation stiffness; CameraFrameController: a held
        // key moves adjustment speed x dt units; CameraPointerInputController:
        // a right-button drag turns by sensitivity x 0.0666667 per pixel,
        // the vertical sense flipped when inverted.
        let settings = ac_host::settings::CameraTurningSettings {
            stiffness: 0.9,
            adjustment_speed: 60.0,
            mouse_look_sensitivity: 0.5,
            invert_mouse_look_y_axis: true,
            ..ac_host::settings::CameraTurningSettings::default()
        };
        let mut cam = ChaseCamera {
            tuning: ChaseTuning::from(&settings),
            distance: 5.0,
            pitch: 0.0,
            ..ChaseCamera::default()
        };
        assert_eq!(ChaseTuning::default().stiffness, CHASE_STIFFNESS);

        // Stiffness: the second frame eases 0.9 * dt * 10 of the way.
        cam.update(&ChaseSubject::standing(Vec3::ZERO, 0.0), 1.0 / 60.0, None);
        let start = cam.eye();
        cam.update(
            &ChaseSubject::standing(Vec3::new(1.0, 0.0, 0.0), 0.0),
            1.0 / 60.0,
            None,
        );
        let moved = cam.eye().x - start.x;
        assert!(close(moved, damping_alpha(0.9, 1.0 / 60.0), 1e-5));
        assert!(close(damping_alpha(0.9, 1.0 / 60.0), 0.15, 1e-6));

        // Adjustment speed: a key held 1/60 s swings 60/60 = 1 unit (8°).
        cam.adjust_held(
            ChaseAdjustment {
                rotate_right: true,
                ..ChaseAdjustment::default()
            },
            1.0 / 60.0,
        );
        assert!(close(cam.yaw_offset, OFFSET_ANGLE_RADIANS, 1e-5));
        cam.adjust_held(
            ChaseAdjustment {
                zoom_out: true,
                ..ChaseAdjustment::default()
            },
            1.0 / 60.0,
        );
        assert!(close(cam.distance, 6.0, 1e-4));

        // Sensitivity and inversion: 30 px right turns -(-1)*30*0.5*0.0666667
        // = +1 unit; 30 px down tilts -1 unit (inverted).
        let yaw = cam.yaw_offset;
        cam.orbit_drag(30.0, 0.0, 10.0);
        assert!(close(cam.yaw_offset - yaw, OFFSET_ANGLE_RADIANS, 1e-4));
        cam.pitch = 0.3;
        let pitch_before = cam.pitch;
        cam.orbit_drag(0.0, 30.0, 20.0);
        assert!(close(cam.pitch - pitch_before, -OFFSET_ANGLE_RADIANS, 1e-4));

        // Align to slope follows the setting (OpenAC
        // `RuntimeSettingsTargets.ApplyCameraTurning` sets
        // `CameraDiagnostics.AlignToSlope`).
        assert!(ChaseTuning::from(&settings).align_to_slope);
        let flat = ac_host::settings::CameraTurningSettings {
            align_to_slope: false,
            ..settings
        };
        assert!(!ChaseTuning::from(&flat).align_to_slope);
    }

    #[test]
    fn zoom_scales_the_boom_by_a_fifth_per_unit() {
        let mut cam = ChaseCamera {
            distance: 5.0,
            ..ChaseCamera::default()
        };
        cam.adjust_distance(1.0);
        assert!(close(cam.distance, 6.0, 1e-5));
        cam.adjust_distance(-1.0);
        assert!(close(cam.distance, 4.8, 1e-5));
    }

    #[test]
    fn zoom_is_refused_whole_at_the_limits() {
        let mut far = ChaseCamera {
            distance: 9.9,
            pitch: 0.0,
            ..ChaseCamera::default()
        };
        far.adjust_distance(1.0);
        assert!(close(far.distance, 9.9, 1e-5));
        let mut near = ChaseCamera {
            distance: 0.55,
            pitch: 0.0,
            ..ChaseCamera::default()
        };
        near.adjust_distance(-1.0);
        assert!(close(near.distance, 0.55, 1e-5));
    }

    #[test]
    fn a_diagonal_orbit_checks_x_and_y_separately() {
        let mut cam = ChaseCamera {
            distance: 12.0,
            pitch: 0.0,
            yaw_offset: std::f32::consts::FRAC_PI_4,
            ..ChaseCamera::default()
        };
        cam.adjust_distance(0.5);
        assert!(close(cam.distance, 13.2, 1e-5));
    }

    #[test]
    fn pitch_turns_eight_degrees_and_keeps_the_length() {
        let mut cam = ChaseCamera {
            distance: 5.0,
            pitch: 0.0,
            ..ChaseCamera::default()
        };
        cam.adjust_pitch(1.0);
        assert!(close(cam.pitch, OFFSET_ANGLE_RADIANS, 1e-6));
        assert!(close(cam.distance, 5.0, 1e-6));
        let mut low = ChaseCamera {
            distance: 3.0,
            pitch: -0.55,
            ..ChaseCamera::default()
        };
        low.adjust_pitch(-1.0);
        assert_eq!((low.pitch, low.distance), (-0.55, 3.0));
    }

    #[test]
    fn yaw_turns_eight_degrees_per_unit() {
        let mut cam = ChaseCamera::default();
        cam.adjust_yaw(1.0);
        assert!(close(cam.yaw_offset, OFFSET_ANGLE_RADIANS, 1e-6));
    }

    #[test]
    fn convergence_snap_holds_tiny_steps_and_passes_real_ones() {
        let eye = Vec3::new(5.0, 6.0, 7.0);
        let fwd = Vec3::X;
        let tiny = eye + Vec3::new(0.0001, 0.0, 0.0);
        assert_eq!(convergence_snap(eye, fwd, tiny, fwd), (eye, fwd, true));
        let step = eye + Vec3::new(0.01, 0.0, 0.0);
        assert_eq!(convergence_snap(eye, fwd, step, fwd), (step, fwd, false));
        let turn = Vec3::new(1.0, 0.05, 0.0).normalize();
        assert!(!convergence_snap(eye, fwd, tiny, turn).2);
    }

    #[test]
    fn default_view_is_two_and_three_quarters_back_and_0_825_up() {
        // The game's default view: 2.5 m back and 0.75 m up, scaled by 1.1
        // in the game world.
        let mut cam = ChaseCamera {
            distance: 7.0,
            pitch: 0.5,
            ..ChaseCamera::default()
        };
        cam.set_default_view();
        assert!(close(cam.distance * cam.pitch.cos(), 2.75, 1e-5));
        assert!(close(cam.distance * cam.pitch.sin(), 0.825, 1e-5));
    }

    #[test]
    fn the_chase_camera_starts_in_the_scaled_default_view() {
        let cam = ChaseCamera::default();
        assert!(close(cam.distance * cam.pitch.cos(), 2.75, 1e-5));
        assert!(close(cam.distance * cam.pitch.sin(), 0.825, 1e-5));
        assert!(close(cam.distance, 2.871_15, 1e-4));
        // The scale lengthens the boom but keeps its angle (16.7 degrees).
        assert!(close(cam.pitch, 0.75f32.atan2(2.5), 1e-6));
    }

    #[test]
    fn orbit_looks_at_its_target_from_the_default_angle() {
        let orbit = OrbitCamera::default();
        let eye = orbit.eye();
        assert!(close(eye.distance(orbit.target), 300.0, 1e-3));
        assert!(eye.z > orbit.target.z);
    }

    #[test]
    fn fly_moves_flat_whatever_the_pitch() {
        let mut fly = FlyCamera {
            pitch: -1.2,
            ..FlyCamera::default()
        };
        let start = fly.position;
        fly.update(
            1.0,
            FlyInput {
                forward: true,
                ..FlyInput::default()
            },
        );
        // Facing +y (yaw pi/2) at 12 m/s.
        assert!(close(fly.position.y - start.y, 12.0, 1e-4));
        assert!(close(fly.position.z, start.z, 1e-6));
        fly.update(
            0.5,
            FlyInput {
                right: true,
                up: true,
                boost: true,
                ..FlyInput::default()
            },
        );
        assert!(close(fly.position.x - start.x, 25.0, 1e-3));
        assert!(close(fly.position.z - start.z, 25.0, 1e-3));
    }

    #[test]
    fn fly_look_turns_right_and_down_and_stops_short_of_straight() {
        let mut fly = FlyCamera::default();
        let yaw = fly.yaw;
        fly.look(100.0, 0.0);
        assert!(close(fly.yaw, yaw - 0.3, 1e-6));
        fly.look(0.0, 10_000.0);
        assert_eq!(fly.pitch, -FLY_PITCH_LIMIT);
        fly.look(0.0, -20_000.0);
        assert_eq!(fly.pitch, FLY_PITCH_LIMIT);
    }

    const READY: PlayerModeFacts = PlayerModeFacts {
        live_in_world: true,
        player_present: true,
        controller_ready: true,
        world_ready: true,
        world_entry: 1,
    };
    const GONE: PlayerModeFacts = PlayerModeFacts {
        live_in_world: false,
        player_present: false,
        controller_ready: false,
        world_ready: false,
        world_entry: 1,
    };

    // ── the player-mode trigger: OpenAC `AutoEnterPlayerModeTests` ──────

    #[test]
    fn auto_entry_armed_with_the_world_not_ready_waits() {
        let mut guard = PlayerModeAutoEntry::default();
        guard.arm();
        let mut facts = READY;
        facts.world_ready = false;
        assert!(!guard.try_enter(&facts, false));
        assert!(guard.is_armed());
        facts.world_ready = true;
        assert!(guard.try_enter(&facts, false));
    }

    #[test]
    fn auto_entry_not_armed_never_fires() {
        let mut guard = PlayerModeAutoEntry::default();
        assert!(!guard.try_enter(&READY, false));
        assert!(!guard.is_armed());
    }

    #[test]
    fn auto_entry_waits_for_each_condition() {
        // `TryEnter_Armed_LiveNotInWorld_DoesNotFire`,
        // `..._PlayerEntityNotPresent_...`, `..._PlayerControllerNotReady_...`.
        for unmet in 0..3 {
            let mut facts = READY;
            match unmet {
                0 => facts.live_in_world = false,
                1 => facts.player_present = false,
                _ => facts.controller_ready = false,
            }
            let mut guard = PlayerModeAutoEntry::default();
            guard.arm();
            assert!(!guard.try_enter(&facts, false), "condition {unmet}");
            assert!(guard.is_armed(), "condition {unmet}");
        }
    }

    #[test]
    fn auto_entry_fires_exactly_once() {
        let mut guard = PlayerModeAutoEntry::default();
        guard.arm();
        assert!(guard.try_enter(&READY, false));
        assert!(!guard.is_armed());
        assert!(!guard.try_enter(&READY, false), "one shot");
    }

    #[test]
    fn auto_entry_fires_on_the_tick_its_last_condition_comes_true() {
        let mut guard = PlayerModeAutoEntry::default();
        guard.arm();
        let mut facts = PlayerModeFacts {
            world_ready: true,
            ..GONE
        };
        facts.live_in_world = true;
        assert!(!guard.try_enter(&facts, false));
        facts.player_present = true;
        assert!(!guard.try_enter(&facts, false));
        facts.controller_ready = true;
        assert!(guard.try_enter(&facts, false));
    }

    #[test]
    fn auto_entry_cancelled_before_firing_stays_quiet() {
        let mut guard = PlayerModeAutoEntry::default();
        guard.arm();
        guard.cancel();
        assert!(!guard.is_armed());
        assert!(!guard.try_enter(&READY, false));
    }

    #[test]
    fn auto_entry_armed_twice_fires_once() {
        let mut guard = PlayerModeAutoEntry::default();
        guard.arm();
        guard.arm();
        assert!(guard.try_enter(&READY, false));
        assert!(!guard.try_enter(&READY, false));
    }

    #[test]
    fn auto_entry_retires_when_player_mode_is_already_on() {
        // `PortalEntryThatAlreadyActivatedPlayerMode_RetiresArmedGuard`.
        let mut guard = PlayerModeAutoEntry::default();
        guard.arm();
        assert!(!guard.try_enter(&READY, true));
        assert!(!guard.is_armed());
    }

    /// OpenAC `PlayerModePortalEntryTests.
    /// UnreadyExistingController_PreservesAutoEntryUntilPortalModeAcquired`
    /// (login and teleport): portal space with a body the session's
    /// physics does not move yet keeps the trigger armed; once it does,
    /// portal space is taken and the trigger is retired.
    #[test]
    fn portal_space_keeps_the_trigger_until_the_body_is_moved_by_the_session() {
        for login in [false, true] {
            let mut c = CameraController::new(OrbitCamera::default());
            c.follow_player(PlayerModeFacts {
                controller_ready: false,
                world_ready: false,
                ..READY
            });
            assert!(c.auto_entry().is_armed());
            assert!(!c.enter_portal_space(login));
            assert!(c.auto_entry().is_armed());
            c.follow_player(PlayerModeFacts {
                world_ready: false,
                ..READY
            });
            assert!(c.enter_portal_space(login));
            assert!(!c.auto_entry().is_armed());
            assert_eq!(c.mode() == CameraMode::Chase, login, "login {login}");
        }
    }

    /// OpenAC `PlayerModeController` with its auto-entry: entering the
    /// world arms the trigger, which waits for the world to be shown;
    /// toggling player mode or fly by hand cancels it; leaving the world
    /// falls back to orbiting and the next entry arms it again.
    #[test]
    fn the_camera_enters_player_mode_once_the_world_is_ready() {
        let mut c = CameraController::new(OrbitCamera::default());
        c.follow_player(PlayerModeFacts {
            world_ready: false,
            ..READY
        });
        assert_eq!(c.mode(), CameraMode::Orbit, "the reveal is not over");
        c.follow_player(READY);
        assert_eq!(c.mode(), CameraMode::Chase);
        c.toggle_player_mode();
        c.follow_player(READY);
        assert_eq!(c.mode(), CameraMode::Orbit, "the trigger fired once");
        c.follow_player(GONE);
        c.toggle_player_mode();
        assert_eq!(c.mode(), CameraMode::Orbit, "no toggle out of the world");
        // A hand toggle before the world is ready cancels the trigger.
        c.follow_player(PlayerModeFacts {
            world_ready: false,
            ..READY
        });
        c.toggle_fly();
        c.toggle_fly();
        c.follow_player(READY);
        assert_eq!(c.mode(), CameraMode::Chase, "the fly toggle returned");
        c.toggle_player_mode();
        c.follow_player(READY);
        assert_eq!(c.mode(), CameraMode::Orbit, "cancelled, not re-fired");
    }

    /// Whether the chase camera sits in its default view: 2.75 m back and
    /// 0.825 m up (the game's 2.5 m and 0.75 m scaled by 1.1), no swing,
    /// no look-down, map or first-person view.
    fn in_default_view(c: &ChaseCamera) -> bool {
        close(c.distance * c.pitch.cos(), 2.75, 1e-5)
            && close(c.distance * c.pitch.sin(), 0.825, 1e-5)
            && c.yaw_offset == 0.0
            && !c.is_looking_down()
            && !c.is_map_mode()
            && !c.is_in_head()
    }

    /// The game puts the camera back in its default view whenever it makes
    /// the player (each entry into the world): a camera zoomed out and
    /// turned around the character, or in the map view, before a log out
    /// starts the next log in, the same character or another, in the
    /// default view. The player's camera settings stay.
    #[test]
    fn every_entry_into_the_world_starts_the_chase_camera_in_its_default_view() {
        for map in [false, true] {
            let mut c = CameraController::new(OrbitCamera::default());
            c.follow_player(READY);
            assert!(in_default_view(&c.chase));
            c.chase.tuning.stiffness = 0.25;
            c.zoom(6.0);
            c.chase.yaw_offset = 2.0;
            if map {
                c.chase.toggle_map_mode_view();
            }
            c.follow_player(READY);
            assert!(!in_default_view(&c.chase), "kept while in the world");
            c.follow_player(GONE);
            assert!(!in_default_view(&c.chase), "kept at character selection");
            c.follow_player(PlayerModeFacts {
                world_entry: 2,
                ..READY
            });
            assert!(in_default_view(&c.chase), "map {map}: reset by the log in");
            assert_eq!(c.chase.tuning.stiffness, 0.25, "the settings stay");
        }
    }

    /// A log out and log in the window saw no frame of (it was minimised,
    /// say) is still a new entry: the entry count moved on.
    #[test]
    fn an_entry_seen_only_by_its_count_resets_the_view_too() {
        let mut c = CameraController::new(OrbitCamera::default());
        c.follow_player(READY);
        c.zoom(6.0);
        c.follow_player(READY);
        assert!(!in_default_view(&c.chase), "the same entry keeps the view");
        c.follow_player(PlayerModeFacts {
            world_entry: 2,
            ..READY
        });
        assert!(in_default_view(&c.chase));
    }

    #[test]
    fn the_camera_follows_the_player_and_toggles_fly() {
        let mut c = CameraController::new(OrbitCamera::default());
        assert_eq!(c.mode(), CameraMode::Orbit);
        c.follow_player(READY);
        assert_eq!(c.mode(), CameraMode::Chase);
        c.toggle_fly();
        assert_eq!(c.mode(), CameraMode::Fly);
        // Losing the player while flying keeps flying.
        c.follow_player(GONE);
        assert_eq!(c.mode(), CameraMode::Fly);
        c.toggle_fly();
        assert_eq!(c.mode(), CameraMode::Orbit);
        c.follow_player(READY);
        assert_eq!(c.mode(), CameraMode::Chase);
        c.toggle_fly();
        c.toggle_fly();
        assert_eq!(
            c.mode(),
            CameraMode::Chase,
            "fly toggles back to the player"
        );
        c.toggle_player_mode();
        assert_eq!(c.mode(), CameraMode::Orbit);
        c.toggle_player_mode();
        assert_eq!(c.mode(), CameraMode::Chase);
    }

    /// OpenAC `WorldRenderFrameBuilderTests` (the overhead view): only the
    /// chase camera's map view is the overhead view.
    #[test]
    fn only_the_chase_cameras_map_view_is_the_overhead_view() {
        let mut c = CameraController::new(OrbitCamera::default());
        // In the world, before the chase camera takes over.
        c.follow_player(PlayerModeFacts {
            world_ready: false,
            ..READY
        });
        c.chase.toggle_map_mode_view();
        assert!(c.chase.is_map_mode());
        assert!(!c.is_overhead_view(), "orbiting");
        c.follow_player(READY);
        assert_eq!(c.mode(), CameraMode::Chase);
        assert!(c.is_overhead_view());
        c.toggle_fly();
        assert!(!c.is_overhead_view(), "flying");
        c.toggle_fly();
        c.chase.toggle_map_mode_view();
        assert!(!c.chase.is_map_mode());
        assert!(!c.is_overhead_view());
    }

    #[test]
    fn translating_moves_every_camera() {
        let mut c = CameraController::new(OrbitCamera::default());
        let before = (c.orbit.target, c.fly.position);
        c.translate(Vec3::new(192.0, -192.0, 0.0));
        assert_eq!(c.orbit.target, before.0 + Vec3::new(192.0, -192.0, 0.0));
        assert_eq!(c.fly.position, before.1 + Vec3::new(192.0, -192.0, 0.0));
    }

    /// From OpenAC `tests/AcDream.App.Tests/RuntimeOptionsTests.cs`:
    /// `OrbitDistanceOverride_AcceptsOnlyPositiveFiniteMeters`,
    /// `OrbitAngleOverrides_AcceptFiniteYawAndBoundedPitchDegrees`.
    #[test]
    fn orbit_overrides_accept_only_sane_values() {
        assert_eq!(
            OrbitOverrides::parse(None, None, None),
            OrbitOverrides::default()
        );
        assert_eq!(
            OrbitOverrides::parse(Some("120"), None, None).distance_meters,
            Some(120.0)
        );
        for rejected in ["0", "-1", "NaN", "Infinity", "near"] {
            assert_eq!(
                OrbitOverrides::parse(Some(rejected), None, None).distance_meters,
                None
            );
        }
        let parsed = OrbitOverrides::parse(None, Some("-135.5"), Some("7.25"));
        assert_eq!(parsed.yaw_degrees, Some(-135.5));
        assert_eq!(parsed.pitch_degrees, Some(7.25));
        for rejected in ["NaN", "Infinity", "angle"] {
            assert_eq!(
                OrbitOverrides::parse(None, Some(rejected), None).yaw_degrees,
                None
            );
        }
        for rejected in ["-90", "90", "NaN", "pitch"] {
            assert_eq!(
                OrbitOverrides::parse(None, None, Some(rejected)).pitch_degrees,
                None
            );
        }
        let orbit = OrbitOverrides::parse(Some("50"), Some("90"), Some("45")).orbit();
        assert_eq!(orbit.distance, 50.0);
        assert!(close(orbit.yaw, std::f32::consts::FRAC_PI_2, 1e-6));
        assert!(close(orbit.pitch, std::f32::consts::FRAC_PI_4, 1e-6));
    }
}
