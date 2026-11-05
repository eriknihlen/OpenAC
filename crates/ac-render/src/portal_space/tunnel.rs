//! The portal tunnel: the swirl model the game shows in place of the world
//! while the player is in portal space, its looping animation, and the
//! camera that sits inside it and rolls about its own forward axis by eased
//! random turns.

use std::sync::Arc;

use ac_core::sky::DotNetRandom;
use ac_dat::DatCollection;
use ac_dat::objects::{AnimData, AnimationHook, EnumIdMap, Setup};
use ac_motion::{AnimationLoader, Sequence};
use glam::{Mat4, Quat, Vec3};

use super::sequencer::{TUNNEL_FRAMES_PER_SECOND, retail_animation_level};
use super::view_plane::{far_plane, near_plane, perspective, view_plane_distance};
use crate::animation::PartPose;
use crate::animation::pose::default_scale;
use crate::meshing::setup_part_transforms;

/// The client enum value of the tunnel's Setup.
pub const SETUP_CLIENT_ENUM: u32 = 0x1000_0001;
/// The client enum value of the tunnel's animation.
pub const ANIMATION_CLIENT_ENUM: u32 = 0x1000_0002;
/// The client enum category both are listed under.
pub const CLIENT_ENUM_CATEGORY: u32 = 7;

/// Portal space is cleared to opaque black.
pub const PORTAL_SPACE_CLEAR: wgpu::Color = wgpu::Color {
    r: 0.0,
    g: 0.0,
    b: 0.0,
    a: 1.0,
};

/// The shortest camera turn, seconds.
const ROTATION_DURATION_MIN: f64 = 0.6;
/// The longest camera turn, seconds.
const ROTATION_DURATION_MAX: f64 = 1.8;

/// The line shown each time the camera starts a new turn.
pub const WAIT_TEXT: &str = "In Portal Space - Please Wait...";

/// The dat id a client enum value of `category` maps to, through the portal
/// dat's master enum map; 0 when the chain breaks.
#[must_use]
pub fn resolve_client_enum(dats: &DatCollection, enum_value: u32, category: u32) -> u32 {
    let lookup = |map_id: u32, key: u32| -> Option<u32> {
        let map = dats.get::<EnumIdMap>(map_id).ok()??;
        map.client_enum_to_id
            .iter()
            .find(|(k, _)| *k == key)
            .map(|(_, id)| *id)
    };
    let master = dats.portal.header().master_map_id as u32;
    if master == 0 {
        return 0;
    }
    lookup(master, category)
        .and_then(|sub| lookup(sub, enum_value))
        .unwrap_or(0)
}

/// The camera inside the tunnel: fixed at the game's eye point, looking
/// along +y, rolled about that axis by `direction_degrees`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PortalTunnelCamera {
    /// The roll, degrees.
    pub direction_degrees: f32,
    /// Vertical field of view, radians.
    pub fov_radians: f32,
    /// Near plane, metres.
    pub near: f32,
    /// Far plane, metres.
    pub far: f32,
    /// Width over height.
    pub aspect: f32,
}

impl Default for PortalTunnelCamera {
    fn default() -> Self {
        Self {
            direction_degrees: 0.0,
            fov_radians: std::f32::consts::FRAC_PI_4,
            near: 0.1,
            far: 4000.0,
            aspect: 1.0,
        }
    }
}

impl PortalTunnelCamera {
    /// Where the camera sits, model space.
    pub const RETAIL_EYE: Vec3 = Vec3::new(0.24, -2.7, 0.88);

    /// Take the field of view and the near and far planes of the world's
    /// projection (the tunnel inherits the active view's).
    pub fn use_smart_box_fov(&mut self, projection: &Mat4) {
        let vertical_scale = view_plane_distance(projection);
        if !vertical_scale.is_finite() || vertical_scale <= 0.0 {
            return;
        }
        let fov = 2.0 * (1.0 / vertical_scale).atan();
        if fov.is_finite() && fov > 0.0 && fov < std::f32::consts::PI {
            self.fov_radians = fov;
        }
        let near = near_plane(projection);
        let far = far_plane(projection);
        if far.is_finite() && far > 0.0 {
            self.far = far;
        }
        if near.is_finite() && near > 0.0 && near < self.far {
            self.near = near;
        }
    }

    /// The view matrix.
    #[must_use]
    pub fn view(&self) -> Mat4 {
        let rotation = Quat::from_axis_angle(Vec3::Y, self.direction_degrees.to_radians());
        let up = rotation * Vec3::Z;
        glam::camera::rh::view::look_at_mat4(Self::RETAIL_EYE, Self::RETAIL_EYE + Vec3::Y, up)
    }

    /// The projection matrix.
    #[must_use]
    pub fn projection(&self) -> Mat4 {
        let aspect = if self.aspect <= 0.0 { 1.0 } else { self.aspect };
        perspective(self.fov_radians, aspect, self.near, self.far)
    }
}

/// The camera's roll: turns of 0.6 to 1.8 seconds to a random angle, eased
/// by the game's table; a new turn starts when the last one ends.
#[derive(Debug, Clone)]
pub struct TunnelRotation {
    elapsed: f64,
    duration: f64,
    start: f32,
    end: f32,
    current: f32,
    random: DotNetRandom,
}

impl TunnelRotation {
    /// At rest, rolling by `random`.
    #[must_use]
    pub fn new(random: DotNetRandom) -> Self {
        Self {
            elapsed: 0.0,
            duration: 0.0,
            start: 0.0,
            end: 0.0,
            current: 0.0,
            random,
        }
    }

    /// Back to no roll (the next tick starts a turn).
    pub fn reset(&mut self) {
        self.elapsed = 0.0;
        self.duration = 0.0;
        self.start = 0.0;
        self.end = 0.0;
        self.current = 0.0;
    }

    /// The roll now, degrees.
    #[must_use]
    pub fn angle(&self) -> f32 {
        self.current
    }

    /// Advance `dt` seconds; true when a new turn began (the wait line
    /// shows again).
    pub fn tick(&mut self, dt: f32) -> bool {
        self.elapsed += f64::from(dt);
        if self.elapsed >= self.duration {
            self.current = self.end;
            self.elapsed = 0.0;
            self.duration = self.next(ROTATION_DURATION_MIN, ROTATION_DURATION_MAX);
            self.start = self.current;
            self.end = self.next(0.0, 360.0) as f32;
            return true;
        }
        let t = if self.duration <= 0.0 {
            1.0
        } else {
            (self.elapsed / self.duration) as f32
        };
        let level = f32::from(retail_animation_level(t)) / 1024.0;
        self.current = self.start + (self.end - self.start) * level;
        false
    }

    fn next(&mut self, min: f64, max: f64) -> f64 {
        min + self.random.next_double() * (max - min)
    }
}

/// The tunnel scene: its Setup posed by its animation, and the camera.
pub struct PortalTunnel {
    /// The tunnel's Setup id.
    pub setup_id: u32,
    /// Its animation id.
    pub animation_id: u32,
    setup: Arc<Setup>,
    sequence: Sequence,
    hooks: Vec<AnimationHook>,
    poses: Vec<PartPose>,
    /// The camera.
    pub camera: PortalTunnelCamera,
    rotation: TunnelRotation,
    visible: bool,
}

impl std::fmt::Debug for PortalTunnel {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PortalTunnel")
            .field("setup_id", &self.setup_id)
            .field("animation_id", &self.animation_id)
            .field("visible", &self.visible)
            .finish_non_exhaustive()
    }
}

impl PortalTunnel {
    /// The tunnel of `setup` (`setup_id`) playing `animation_id`, its
    /// camera rolled by `random`.
    #[must_use]
    pub fn new(
        setup_id: u32,
        setup: Arc<Setup>,
        animation_id: u32,
        loader: Arc<dyn AnimationLoader + Send + Sync>,
        random: DotNetRandom,
    ) -> Self {
        let poses = pose(&setup, None);
        Self {
            setup_id,
            animation_id,
            setup,
            sequence: Sequence::new(loader),
            hooks: Vec::new(),
            poses,
            camera: PortalTunnelCamera::default(),
            rotation: TunnelRotation::new(random),
            visible: false,
        }
    }

    /// Shown.
    #[must_use]
    pub fn is_visible(&self) -> bool {
        self.visible
    }

    /// The animation's current frame.
    #[must_use]
    pub fn current_animation_frame(&self) -> i32 {
        self.sequence.curr_frame_number()
    }

    /// The Setup.
    #[must_use]
    pub fn setup(&self) -> &Setup {
        &self.setup
    }

    /// Each part's pose, model space.
    #[must_use]
    pub fn poses(&self) -> &[PartPose] {
        &self.poses
    }

    /// Show it: the animation starts from its second frame at 40 frames per
    /// second, the camera unrolled.
    pub fn enter(&mut self) {
        self.hooks.clear();
        self.sequence.clear_animations();
        self.sequence.append_animation(&AnimData {
            anim_id: self.animation_id,
            low_frame: 1,
            high_frame: -1,
            framerate: TUNNEL_FRAMES_PER_SECOND,
        });
        self.rotation.reset();
        self.camera.direction_degrees = 0.0;
        self.visible = true;
        self.rebuild_pose();
    }

    /// Hide it and stop the animation.
    pub fn exit(&mut self) {
        self.visible = false;
        self.hooks.clear();
        self.sequence.clear_animations();
    }

    /// Advance `dt` seconds while shown: the animation (its hooks are
    /// returned, to sound) and the camera's roll. The flag is true when a
    /// new turn began (the wait line shows).
    pub fn tick(&mut self, dt: f32) -> (Vec<AnimationHook>, bool) {
        if !self.visible || dt < 0.0 {
            return (Vec::new(), false);
        }
        self.sequence
            .update(f64::from(dt), None, Some(&mut self.hooks));
        self.rebuild_pose();
        let new_turn = self.rotation.tick(dt);
        self.camera.direction_degrees = self.rotation.angle();
        (std::mem::take(&mut self.hooks), new_turn)
    }

    fn rebuild_pose(&mut self) {
        self.poses = pose(&self.setup, self.sequence.curr_animframe());
    }
}

/// The parts of `setup` in `frame` (else at rest), each at its default
/// scale.
fn pose(setup: &Setup, frame: Option<&ac_dat::objects::AnimationFrame>) -> Vec<PartPose> {
    setup_part_transforms(setup, frame, 1.0)
        .into_iter()
        .enumerate()
        .map(|(i, rigid)| PartPose {
            visual: rigid * Mat4::from_scale(default_scale(setup, i)),
            rigid,
        })
        .collect()
}

#[cfg(test)]
mod tests {
    //! OpenAC `PortalTunnelAssetTests` (the camera cases).
    use super::*;

    fn close(a: f32, b: f32) -> bool {
        (a - b).abs() < 1e-5
    }

    #[test]
    fn portal_tunnel_camera_rolls_around_forward_axis_without_leaving_passage() {
        let mut camera = PortalTunnelCamera {
            aspect: 1.0,
            ..PortalTunnelCamera::default()
        };
        let world = camera.view().inverse();
        let eye = world.w_axis.truncate();
        assert!(eye.abs_diff_eq(PortalTunnelCamera::RETAIL_EYE, 1e-5));
        camera.direction_degrees = 90.0;
        let world = camera.view().inverse();
        let forward = world.transform_vector3(Vec3::NEG_Z).normalize();
        let up = world.transform_vector3(Vec3::Y).normalize();
        assert!(close(forward.x, 0.0) && close(forward.y, 1.0) && close(forward.z, 0.0));
        assert!(close(up.x, 1.0) && close(up.y, 0.0) && close(up.z, 0.0));
    }

    #[test]
    fn portal_tunnel_camera_uses_active_smart_box_projection_clip_range() {
        for degrees in [45.0f32, 60.0, 90.0] {
            let expected = degrees.to_radians();
            let projection = perspective(expected, 16.0 / 9.0, 0.35, 5000.0);
            let mut camera = PortalTunnelCamera::default();
            camera.use_smart_box_fov(&projection);
            assert!(close(camera.fov_radians, expected), "{degrees}");
            assert!(close(camera.near, 0.35));
            // OpenAC allows 5 m; glam's single-precision depth terms
            // round the recovered far plane to 4993.6 m here.
            assert!((4990.0..=5010.0).contains(&camera.far), "{}", camera.far);
        }
    }

    #[test]
    fn portal_space_clears_to_opaque_black() {
        // OpenAC PortalSpace_RhiPassReclearsTheSameOpaqueBlackTheFrameEstablished.
        assert_eq!(PORTAL_SPACE_CLEAR, wgpu::Color::BLACK);
    }

    #[test]
    fn a_turn_eases_from_its_start_to_a_random_end_and_the_next_begins() {
        // The first tick starts a turn (and the wait line); within it the
        // roll moves by the eased table; at its end the next turn starts
        // from the angle reached.
        let mut rotation = TunnelRotation::new(DotNetRandom::new(7));
        assert!(rotation.tick(0.0));
        assert_eq!(rotation.angle(), 0.0);
        let end = rotation.end;
        let duration = rotation.duration as f32;
        assert!((0.6..=1.8).contains(&duration));
        assert!(!rotation.tick(duration * 0.5));
        let half = f32::from(retail_animation_level(0.5)) / 1024.0;
        assert!(close(rotation.angle(), end * half));
        assert!(rotation.tick(duration));
        assert_eq!(rotation.angle(), end);
    }
}
