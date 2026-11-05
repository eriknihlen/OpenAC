//! Turning a sequencer's part frames into the two matrices each part is
//! drawn and hung from:
//!
//! - the **visual** pose places the part's mesh: its Setup default scale,
//!   then its keyframe rotation and offset, then the object's own size;
//! - the **rigid** pose is the part's frame alone (rotation and an offset
//!   scaled by the object's size, no mesh scale): what held items, effects
//!   and lights hang from.
//!
//! Both are object-local (relative to the object's root, which carries its
//! position and heading but no scale). A keyframe that lists fewer parts
//! than the Setup leaves the trailing parts where they were last shown (at
//! first, their resting placement).

use ac_dat::objects::Setup;
use ac_motion::PartTransform;
use glam::{Mat4, Vec3};

use crate::meshing::{placement_frame, setup_part_transforms, vec3};

/// One part's pose. See the module documentation.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PartPose {
    /// Where the part's mesh is drawn, object-local (scales included).
    pub visual: Mat4,
    /// The part's frame, object-local (no mesh scale).
    pub rigid: Mat4,
}

/// The resting poses of a Setup's parts at `object_scale`, each part at its
/// default scale. A server object rests in `placement` (see
/// [`placement_frame`]); when the Setup has neither that placement nor the
/// default one, every part sits at the object's origin, unrotated. With no
/// placement (a static object) the resting placement is used, else the
/// default one, else the first.
#[must_use]
pub fn rest_poses(setup: &Setup, placement: Option<u32>, object_scale: f32) -> Vec<PartPose> {
    let rigid = match placement {
        None => setup_part_transforms(setup, None, object_scale),
        Some(id) => match placement_frame(setup, id) {
            Some(frame) => setup_part_transforms(setup, Some(frame), object_scale),
            None => vec![Mat4::IDENTITY; setup.parts.len()],
        },
    };
    (0..setup.parts.len())
        .map(|i| {
            let rigid = rigid.get(i).copied().unwrap_or(Mat4::IDENTITY);
            let scale = default_scale(setup, i) * object_scale;
            PartPose {
                visual: rigid * Mat4::from_scale(scale),
                rigid,
            }
        })
        .collect()
}

/// A part's default scale (one when the Setup lists none).
#[must_use]
pub fn default_scale(setup: &Setup, part: usize) -> Vec3 {
    setup
        .default_scale
        .get(part)
        .map_or(Vec3::ONE, |s| vec3(*s))
}

/// Pose the parts `frames` covers into `poses` (one per Setup part); the
/// trailing parts keep what they had.
pub fn compose(setup: &Setup, frames: &[PartTransform], object_scale: f32, poses: &mut [PartPose]) {
    for (i, (frame, pose)) in frames.iter().zip(poses.iter_mut()).enumerate() {
        let rotation = Mat4::from_quat(frame.orientation);
        pose.visual = Mat4::from_scale(Vec3::splat(object_scale))
            * Mat4::from_translation(frame.origin)
            * rotation
            * Mat4::from_scale(default_scale(setup, i));
        pose.rigid = Mat4::from_translation(frame.origin * object_scale) * rotation;
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `LiveEntityAnimationPresenterTests`
    //! (`Present_ComposesDistinctVisualAndRigidChannels`,
    //! `ShortFirstFrame_RetainsDistinctVisualAndRigidRestPoses`,
    //! `LaterShortFrame_RetainsPreviouslyPresentedTrailingPart`). OpenAC's
    //! row-vector products are written here in glam's column order.
    use super::*;
    use ac_dat::types::Vector3;
    use glam::Quat;

    fn setup(parts: usize) -> Setup {
        let mut s = crate::animation::test_support::setup(parts);
        s.default_scale = vec![
            Vector3 {
                x: 1.0,
                y: 1.0,
                z: 1.0
            };
            parts
        ];
        s
    }

    fn close(a: Mat4, b: Mat4) -> bool {
        a.abs_diff_eq(b, 1e-5)
    }

    #[test]
    fn present_composes_distinct_visual_and_rigid_channels() {
        let mut s = setup(2);
        s.default_scale[0] = Vector3 {
            x: 3.0,
            y: 4.0,
            z: 5.0,
        };
        let rotation = Quat::from_rotation_z(std::f32::consts::FRAC_PI_2);
        let frames = [
            PartTransform {
                origin: Vec3::new(1.0, 2.0, 3.0),
                orientation: rotation,
            },
            PartTransform {
                origin: Vec3::new(4.0, 5.0, 6.0),
                orientation: Quat::IDENTITY,
            },
        ];
        let mut poses = rest_poses(&s, None, 2.0);
        compose(&s, &frames, 2.0, &mut poses);
        // CreateScale(3,4,5) * CreateFromQuaternion * CreateTranslation(1,2,3) * CreateScale(2).
        let visual = Mat4::from_scale(Vec3::splat(2.0))
            * Mat4::from_translation(Vec3::new(1.0, 2.0, 3.0))
            * Mat4::from_quat(rotation)
            * Mat4::from_scale(Vec3::new(3.0, 4.0, 5.0));
        // CreateFromQuaternion * CreateTranslation(2,4,6).
        let rigid = Mat4::from_translation(Vec3::new(2.0, 4.0, 6.0)) * Mat4::from_quat(rotation);
        assert!(close(poses[0].visual, visual));
        assert!(close(poses[0].rigid, rigid));
        assert_eq!(poses.len(), 2);
    }

    #[test]
    fn a_short_first_frame_keeps_the_resting_trailing_part() {
        let s = setup(2);
        let mut poses = rest_poses(&s, None, 2.0);
        let rest = poses[1];
        compose(
            &s,
            &[PartTransform {
                origin: Vec3::X,
                orientation: Quat::IDENTITY,
            }],
            2.0,
            &mut poses,
        );
        assert_eq!(poses[1], rest);
    }

    #[test]
    fn a_later_short_frame_keeps_the_previously_presented_trailing_part() {
        let s = setup(2);
        let mut poses = rest_poses(&s, None, 1.0);
        compose(
            &s,
            &[
                PartTransform {
                    origin: Vec3::X,
                    orientation: Quat::IDENTITY,
                },
                PartTransform {
                    origin: Vec3::new(4.0, 5.0, 6.0),
                    orientation: Quat::from_rotation_z(0.5),
                },
            ],
            1.0,
            &mut poses,
        );
        let presented = poses[1];
        compose(
            &s,
            &[PartTransform {
                origin: Vec3::Y,
                orientation: Quat::IDENTITY,
            }],
            1.0,
            &mut poses,
        );
        assert_eq!(poses[1], presented);
    }

    /// A setup with a resting frame (lifted 1 m) and a hook frame (turned a
    /// quarter turn about x), and no default frame.
    fn placed(parts: usize) -> Setup {
        use ac_dat::objects::AnimationFrame;
        use ac_dat::types::{Frame, Quaternion};
        let frame = |origin: Vector3, q: Quat| Frame {
            origin,
            orientation: Quaternion {
                w: q.w,
                x: q.x,
                y: q.y,
                z: q.z,
            },
        };
        let pose = |f: Frame| AnimationFrame {
            frames: vec![f; parts],
            hooks: Vec::new(),
        };
        let mut s = setup(parts);
        s.placement_frames = vec![
            (
                0x65,
                pose(frame(
                    Vector3 {
                        x: 0.0,
                        y: 0.0,
                        z: 1.0,
                    },
                    Quat::IDENTITY,
                )),
            ),
            (
                0x67,
                pose(frame(
                    Vector3::default(),
                    Quat::from_rotation_x(std::f32::consts::FRAC_PI_2),
                )),
            ),
        ];
        s
    }

    // OpenAC `SetupPartTransformsTests.ResolvePlacementFrame_*` and
    // `ResolveObjectPose_WithNoFrame_LeavesEveryPartAtTheOrigin`.

    #[test]
    fn a_server_object_rests_in_its_placement() {
        let poses = rest_poses(&placed(1), Some(0x67), 1.0);
        let expected = Mat4::from_quat(Quat::from_rotation_x(std::f32::consts::FRAC_PI_2));
        assert!(close(poses[0].rigid, expected));
    }

    #[test]
    fn without_its_placement_or_a_default_a_server_object_has_no_frame() {
        let poses = rest_poses(&placed(2), Some(0x01), 1.0);
        assert_eq!(poses.len(), 2);
        assert!(poses.iter().all(|p| close(p.rigid, Mat4::IDENTITY)));
    }
}
