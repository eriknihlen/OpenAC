//! How the viewport figures stand.
//!
//! The inventory figure and the creation preview's resting figure hold one
//! frame of an authored pose: the last frame of an animation chosen per
//! body (the humanoid pose has 34 parts, the two Olthoi bodies 25 and 31,
//! so each body needs its own). The pose's animation is named by an enum
//! value the dats map to an animation id through their enum maps: the
//! master map's slot 7, then the pose value in that map.
//!
//! The creation preview otherwise plays an idle loop at 30 frames per
//! second; zooming in swaps it for the resting pose and zooming out starts
//! the loop again from its first frame.

use std::sync::Arc;

use ac_core::chargen::ChargenHeritageGroup as H;
use ac_dat::objects::{Animation, AnimationFrame, EnumIdMap, Setup};
use glam::{Mat4, Quat, Vec3};

use crate::animation::pose::default_scale;
use crate::meshing::{MeshRef, quat, vec3};

/// The pose every body without one of its own holds.
pub const DEFAULT_POSE_ENUM: u32 = 0x1000_0005;
/// The Olthoi body's pose.
pub const OLTHOI_POSE_ENUM: u32 = 0x1000_0011;
/// The acid Olthoi body's pose.
pub const OLTHOI_ACID_POSE_ENUM: u32 = 0x1000_0013;
/// The creation preview's idle loop for the bodies without one of their
/// own.
pub const DEFAULT_IDLE_ENUM: u32 = 0x1000_0006;
/// The master enum map's slot holding the pose map.
pub const POSE_MAP_SLOT: u32 = 7;
/// The idle loop's frames per second.
pub const IDLE_FRAMERATE: f32 = 30.0;

/// The pose a heritage's body holds (the inventory figure, and the
/// creation preview at rest).
#[must_use]
pub fn pose_enum(heritage: u32) -> u32 {
    match H(heritage) {
        H::OLTHOI => OLTHOI_POSE_ENUM,
        H::OLTHOI_ACID => OLTHOI_ACID_POSE_ENUM,
        _ => DEFAULT_POSE_ENUM,
    }
}

/// The creation preview's idle loop for a heritage (the Olthoi bodies
/// idle in their pose).
#[must_use]
pub fn idle_enum(heritage: u32) -> u32 {
    match H(heritage) {
        H::OLTHOI => OLTHOI_POSE_ENUM,
        H::OLTHOI_ACID => OLTHOI_ACID_POSE_ENUM,
        _ => DEFAULT_IDLE_ENUM,
    }
}

/// The animation id of `pose_enum`: the master map (`master_map_id`)'s
/// slot 7, then the value in that map; 0 when a link is missing.
pub fn resolve_pose_did(
    master_map_id: u32,
    map: impl Fn(u32) -> Option<Arc<EnumIdMap>>,
    pose_enum: u32,
) -> u32 {
    if master_map_id == 0 {
        return 0;
    }
    let Some(slot) = map(master_map_id).and_then(|m| m.client_id(POSE_MAP_SLOT)) else {
        return 0;
    };
    map(slot).and_then(|m| m.client_id(pose_enum)).unwrap_or(0)
}

/// Whether `did` names an animation (`0x03......`).
#[must_use]
pub fn is_animation(did: u32) -> bool {
    did >> 24 == 0x03
}

/// A part placed by a pose: its default scale, then the frame's turn and
/// offset, then the body's own size (left out at size 1).
#[must_use]
pub fn part_transform(
    default_scale: Vec3,
    origin: Vec3,
    orientation: Quat,
    object_scale: f32,
) -> Mat4 {
    let transform = Mat4::from_translation(origin)
        * Mat4::from_quat(orientation)
        * Mat4::from_scale(default_scale);
    if object_scale == 1.0 {
        transform
    } else {
        Mat4::from_scale(Vec3::splat(object_scale)) * transform
    }
}

/// `parts` (one per Setup part) placed by `frame`: a part the frame does
/// not list stands at the origin, unturned.
#[must_use]
pub fn hold(
    setup: &Setup,
    frame: &AnimationFrame,
    parts: &[MeshRef],
    object_scale: f32,
) -> Vec<MeshRef> {
    parts
        .iter()
        .enumerate()
        .map(|(i, part)| {
            let (origin, orientation) = frame
                .frames
                .get(i)
                .map_or((Vec3::ZERO, Quat::IDENTITY), |f| {
                    (vec3(f.origin), quat(f.orientation))
                });
            MeshRef {
                transform: part_transform(
                    default_scale(setup, i),
                    origin,
                    orientation,
                    object_scale,
                ),
                ..*part
            }
        })
        .collect()
}

/// The held frame of a pose animation: its last; `None` for none.
#[must_use]
pub fn held_frame(animation: &Animation) -> Option<&AnimationFrame> {
    animation.part_frames.last()
}

/// The creation preview's figure: its parts at rest and in the idle loop.
#[derive(Debug, Clone)]
pub struct ChargenFigure {
    /// The parts in the resting pose.
    rest: Vec<MeshRef>,
    /// Each part's default scale.
    scales: Vec<Vec3>,
    /// The idle loop (`None`: the figure keeps its rest pose).
    idle: Option<Arc<Animation>>,
    high: i32,
    frame: f32,
    zoomed_in: bool,
    current: Vec<MeshRef>,
}

impl ChargenFigure {
    /// A figure of `parts` (one per Setup part) resting in `rest_frame`
    /// (`None`: the Setup's own placement the parts carry) and idling in
    /// `idle`. It starts idling at the loop's first frame.
    #[must_use]
    pub fn new(
        setup: &Setup,
        parts: &[MeshRef],
        rest_frame: Option<&AnimationFrame>,
        idle: Option<Arc<Animation>>,
    ) -> Self {
        let rest = match rest_frame {
            Some(frame) => hold(setup, frame, parts, 1.0),
            None => parts.to_vec(),
        };
        let idle = idle.filter(|a| !a.part_frames.is_empty());
        let high = idle.as_ref().map_or(-1, |a| {
            i32::try_from(a.part_frames.len()).unwrap_or(i32::MAX) - 1
        });
        let mut figure = Self {
            scales: (0..parts.len()).map(|i| default_scale(setup, i)).collect(),
            current: rest.clone(),
            rest,
            idle,
            high,
            frame: 0.0,
            zoomed_in: false,
        };
        figure.apply_idle_frame();
        figure
    }

    /// The parts as they stand now.
    #[must_use]
    pub fn parts(&self) -> &[MeshRef] {
        &self.current
    }

    /// The figure is frozen at rest (zoomed in).
    #[must_use]
    pub fn is_zoomed_in(&self) -> bool {
        self.zoomed_in
    }

    /// Freeze at rest (zoomed in) or start the idle loop again from its
    /// first frame (zoomed out).
    pub fn set_zoomed_in(&mut self, zoomed_in: bool) {
        if self.zoomed_in == zoomed_in {
            return;
        }
        self.zoomed_in = zoomed_in;
        if zoomed_in {
            self.current.clone_from(&self.rest);
        } else {
            self.frame = 0.0;
            self.apply_idle_frame();
        }
    }

    /// Advance the idle loop by `elapsed` seconds.
    pub fn tick(&mut self, elapsed: f32) {
        if self.zoomed_in || self.idle.is_none() || elapsed <= 0.0 {
            return;
        }
        self.frame = ac_motion::advance_cycle(self.frame, 0, self.high, IDLE_FRAMERATE, elapsed);
        self.apply_idle_frame();
    }

    fn apply_idle_frame(&mut self) {
        let Some(animation) = self.idle.as_deref() else {
            return;
        };
        for (i, part) in self.current.iter_mut().enumerate() {
            let (origin, orientation) =
                ac_motion::try_interpolate_part(animation, self.frame, 0, self.high, i)
                    .unwrap_or((Vec3::ZERO, Quat::IDENTITY));
            part.transform = part_transform(self.scales[i], origin, orientation, 1.0);
        }
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `PaperdollHeritagePresentationTests.cs` (the pose
    //! rows), `PaperdollFramePresenterTests.cs` (the pose applicator rows),
    //! `ChargenPreviewEntityBuilderTests.cs` (rest pose) and
    //! `ChargenPreviewAnimatorTests.cs`.
    use super::*;
    use ac_dat::types::{Frame, Quaternion, Vector3};

    fn v(x: f32, y: f32, z: f32) -> Vector3 {
        Vector3 { x, y, z }
    }

    fn q(x: f32, y: f32, z: f32, w: f32) -> Quaternion {
        Quaternion { w, x, y, z }
    }

    fn setup(parts: usize) -> Setup {
        let mut s = crate::animation::test_support::setup(parts);
        s.default_scale = vec![v(1.0, 1.0, 1.0); parts];
        s
    }

    fn frame(origins: &[(f32, f32, f32)]) -> AnimationFrame {
        AnimationFrame {
            frames: origins
                .iter()
                .map(|&(x, y, z)| Frame {
                    origin: v(x, y, z),
                    orientation: q(0.0, 0.0, 0.0, 1.0),
                })
                .collect(),
            hooks: Vec::new(),
        }
    }

    fn animation(frames: Vec<AnimationFrame>) -> Arc<Animation> {
        Arc::new(Animation {
            id: 0x0300_0001,
            flags: ac_dat::objects::AnimationFlags(0),
            num_parts: 1,
            pos_frames: Vec::new(),
            part_frames: frames,
        })
    }

    fn part(gfx: u32, at: Vec3) -> MeshRef {
        MeshRef {
            gfx_obj_id: gfx,
            transform: Mat4::from_translation(at),
            variant: 7,
        }
    }

    #[test]
    fn each_body_holds_its_own_pose() {
        for h in [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11] {
            assert_eq!(pose_enum(h), DEFAULT_POSE_ENUM, "heritage {h}");
            assert_eq!(idle_enum(h), DEFAULT_IDLE_ENUM, "heritage {h}");
        }
        assert_eq!(pose_enum(12), 0x1000_0011);
        assert_eq!(pose_enum(13), 0x1000_0013);
        assert_eq!(idle_enum(12), 0x1000_0011);
        assert_eq!(idle_enum(13), 0x1000_0013);
    }

    #[test]
    fn the_pose_is_found_through_the_master_map_slot_seven() {
        let maps = |id: u32| -> Option<Arc<EnumIdMap>> {
            let table: &[(u32, u32)] = match id {
                0x2500_0000 => &[(6, 0x2500_0006), (7, 0x2500_0007)],
                0x2500_0007 => &[(0x1000_0005, 0x0300_0BEF), (0x1000_0011, 0x0400_0001)],
                _ => return None,
            };
            Some(Arc::new(EnumIdMap {
                id,
                client_enum_to_id: table.to_vec(),
                client_enum_to_name: Vec::new(),
                server_enum_to_id: Vec::new(),
                server_enum_to_name: Vec::new(),
            }))
        };
        assert_eq!(
            resolve_pose_did(0x2500_0000, maps, 0x1000_0005),
            0x0300_0BEF
        );
        assert!(is_animation(resolve_pose_did(
            0x2500_0000,
            maps,
            0x1000_0005
        )));
        // Not an animation, not listed, no master map, a missing map.
        assert!(!is_animation(resolve_pose_did(
            0x2500_0000,
            maps,
            0x1000_0011
        )));
        assert_eq!(resolve_pose_did(0x2500_0000, maps, 0x1000_0013), 0);
        assert_eq!(resolve_pose_did(0, maps, 0x1000_0005), 0);
        assert_eq!(resolve_pose_did(0x2500_0001, maps, 0x1000_0005), 0);
    }

    #[test]
    fn the_pose_replaces_every_part_transform_and_keeps_the_body_scale() {
        // RetailPaperdollPoseApplicator: the pose replaces each part's
        // transform outright, so the body's own scale is applied again.
        let mut s = setup(2);
        s.default_scale[1] = v(2.0, 2.0, 2.0);
        let parts = [
            part(0x0100_0001, Vec3::splat(9.0)),
            part(0x0100_0002, Vec3::splat(9.0)),
        ];
        let held = hold(&s, &frame(&[(1.0, 0.0, 0.0), (0.0, 0.0, 1.0)]), &parts, 1.5);
        assert_eq!(held[0].gfx_obj_id, 0x0100_0001);
        assert_eq!(held[1].variant, 7);
        assert!(
            held[0]
                .transform
                .w_axis
                .truncate()
                .abs_diff_eq(Vec3::new(1.5, 0.0, 0.0), 1e-5)
        );
        assert!(
            held[1]
                .transform
                .w_axis
                .truncate()
                .abs_diff_eq(Vec3::new(0.0, 0.0, 1.5), 1e-5)
        );
        // The part's size: default 2 times the body's 1.5.
        assert!((held[1].transform.x_axis.length() - 3.0).abs() < 1e-5);
        // At size 1 the composition is the unscaled one exactly.
        let unscaled = hold(&s, &frame(&[(1.0, 0.0, 0.0)]), &parts, 1.0);
        assert_eq!(
            unscaled[0].transform,
            part_transform(Vec3::ONE, Vec3::X, Quat::IDENTITY, 1.0)
        );
        // A part the frame does not list stands at the origin.
        assert_eq!(unscaled[1].transform, Mat4::from_scale(Vec3::splat(2.0)));
    }

    #[test]
    fn the_held_frame_is_the_last() {
        let a = animation(vec![frame(&[(1.0, 0.0, 0.0)]), frame(&[(2.0, 0.0, 0.0)])]);
        assert_eq!(held_frame(&a).unwrap().frames[0].origin.x, 2.0);
        assert!(held_frame(&animation(Vec::new())).is_none());
    }

    #[test]
    fn the_figure_starts_idling_at_the_first_frame() {
        // ChargenPreviewAnimatorTests: retail's default is idle playing,
        // frame 0.
        let s = setup(1);
        let idle = animation(vec![frame(&[(1.0, 0.0, 0.0)]), frame(&[(3.0, 0.0, 0.0)])]);
        let figure = ChargenFigure::new(
            &s,
            &[part(0x0100_0001, Vec3::splat(99.0))],
            Some(&frame(&[(99.0, 99.0, 99.0)])),
            Some(idle),
        );
        assert!(!figure.is_zoomed_in());
        assert_eq!(figure.parts()[0].transform.w_axis.truncate(), Vec3::X);
        assert_eq!(figure.parts()[0].variant, 7);
    }

    #[test]
    fn the_idle_loop_advances_at_thirty_frames_a_second_and_wraps() {
        let s = setup(1);
        let idle = animation(vec![frame(&[(0.0, 0.0, 0.0)]), frame(&[(3.0, 0.0, 0.0)])]);
        let mut figure = ChargenFigure::new(&s, &[part(1, Vec3::ZERO)], None, Some(idle));
        figure.tick(0.5 / 30.0);
        assert!((figure.parts()[0].transform.w_axis.x - 1.5).abs() < 1e-4);
        // Past the last frame it wraps to the first (span + 1 frames).
        figure.tick(1.0 / 30.0);
        assert!((figure.parts()[0].transform.w_axis.x - 1.5).abs() < 1e-4);
        // No time, no move.
        let before = figure.parts()[0].transform;
        figure.tick(0.0);
        assert_eq!(figure.parts()[0].transform, before);
    }

    #[test]
    fn zooming_in_freezes_at_rest_and_out_restarts_the_loop() {
        let s = setup(1);
        let idle = animation(vec![frame(&[(1.0, 0.0, 0.0)]), frame(&[(3.0, 0.0, 0.0)])]);
        let mut figure = ChargenFigure::new(
            &s,
            &[part(1, Vec3::ZERO)],
            Some(&frame(&[(99.0, 99.0, 99.0)])),
            Some(idle),
        );
        figure.tick(0.5 / 30.0);
        figure.set_zoomed_in(true);
        assert!(figure.is_zoomed_in());
        assert_eq!(
            figure.parts()[0].transform.w_axis.truncate(),
            Vec3::splat(99.0)
        );
        // Frozen while zoomed in.
        figure.tick(1.0);
        assert_eq!(
            figure.parts()[0].transform.w_axis.truncate(),
            Vec3::splat(99.0)
        );
        figure.set_zoomed_in(false);
        assert_eq!(figure.parts()[0].transform.w_axis.truncate(), Vec3::X);
    }

    #[test]
    fn without_an_idle_loop_the_figure_rests() {
        let s = setup(1);
        let mut figure = ChargenFigure::new(
            &s,
            &[part(1, Vec3::ZERO)],
            Some(&frame(&[(5.0, 0.0, 0.0)])),
            Some(animation(Vec::new())),
        );
        figure.tick(1.0);
        assert_eq!(figure.parts()[0].transform.w_axis.x, 5.0);
        // No rest frame either: the parts keep their own placement.
        let plain = ChargenFigure::new(&s, &[part(1, Vec3::splat(4.0))], None, None);
        assert_eq!(plain.parts()[0].transform.w_axis.x, 4.0);
    }
}
