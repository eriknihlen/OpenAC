//! The examine portrait and the inventory figure: a copy of the creature
//! (or the player) at the origin, turned toward the viewer, and the box
//! the examine camera frames.
//!
//! The copy follows its creature every frame: the same parts in the same
//! pose and look, and the objects it holds placed on it where they sit on
//! the creature (measured in the creature's own frame and carried over to
//! the copy's).

use glam::{Mat4, Quat, Vec3};

/// The heading the copies are turned to, degrees: a little past facing
/// the viewer, so the figure stands slightly turned.
pub const FIGURE_HEADING_DEGREES: f32 = 191.367_9;

/// At most this many held objects are drawn with an examined creature.
pub const MAX_HELD: usize = 4;

/// The copies' turn: about +Z by minus the figure heading.
#[must_use]
pub fn figure_turn() -> Quat {
    Quat::from_axis_angle(Vec3::Z, -FIGURE_HEADING_DEGREES.to_radians())
}

/// The box around a creature for framing, in its own frame at its size:
/// its own box (`bounds`, model-local at size 1) grown by its `scale`
/// when known, else one built around where its parts stand in its own
/// frame (`part_offsets`, its size already in them; half a metre each
/// way, and at least a 1 x 1 x 2 m body standing on the origin).
#[must_use]
pub fn creature_box(
    bounds: Option<(Vec3, Vec3)>,
    scale: f32,
    part_offsets: impl IntoIterator<Item = Vec3>,
) -> (Vec3, Vec3) {
    if let Some((min, max)) = bounds {
        return (min * scale, max * scale);
    }
    let (mut min, mut max) = super::camera::EXAMINE_DEFAULT_BOX;
    for p in part_offsets {
        min = min.min(p - Vec3::splat(0.5));
        max = max.max(p + Vec3::splat(0.5));
    }
    (min, max)
}

/// The box `(min, max)` turned by `turn`: the box around its eight turned
/// corners.
#[must_use]
pub fn turned_box((min, max): (Vec3, Vec3), turn: Quat) -> (Vec3, Vec3) {
    let mut out: Option<(Vec3, Vec3)> = None;
    for corner in 0..8 {
        let p = Vec3::new(
            if corner & 1 == 0 { min.x } else { max.x },
            if corner & 2 == 0 { min.y } else { max.y },
            if corner & 4 == 0 { min.z } else { max.z },
        );
        let p = turn * p;
        out = Some(match out {
            None => (p, p),
            Some((lo, hi)) => (lo.min(p), hi.max(p)),
        });
    }
    out.unwrap_or((min, max))
}

/// Where something drawn at `world` (drawing frame) on a creature whose
/// root is `root` (position and turn, no scale) is drawn on the copy: the
/// same place relative to the creature, carried to the copy at the origin
/// turned by `turn`.
#[must_use]
pub fn on_copy(world: Mat4, root: Mat4, turn: Quat) -> Mat4 {
    Mat4::from_quat(turn) * root.inverse() * world
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `CreatureAppraisalPresentationTests.cs`
    //! (`HeldObjectsAreCopiedAtTheirPlaceOnTheCreature`) and the
    //! `CreatureAppraisalEntityBuilder.RotatedBounds` rules.
    use super::*;

    #[test]
    fn a_held_object_sits_on_the_copy_where_it_sits_on_the_creature() {
        let creature_turn = Quat::from_axis_angle(Vec3::Z, 1.1);
        let shield_turn = Quat::from_axis_angle(Vec3::X, 0.4);
        let offset = Vec3::new(0.3, 0.1, 1.2);
        let creature_at = Vec3::new(100.0, 50.0, 10.0);
        let root = Mat4::from_rotation_translation(creature_turn, creature_at);
        let shield = Mat4::from_rotation_translation(
            creature_turn * shield_turn,
            creature_at + creature_turn * offset,
        );
        let placed = on_copy(shield, root, figure_turn());
        let (_, rotation, translation) = placed.to_scale_rotation_translation();
        assert!(translation.abs_diff_eq(figure_turn() * offset, 1e-4));
        let expected = figure_turn() * shield_turn;
        assert!(rotation.dot(expected).abs() > 0.9999);
        // The creature itself lands at the origin, turned.
        let (_, r, t) = on_copy(root, root, figure_turn()).to_scale_rotation_translation();
        assert!(t.abs_diff_eq(Vec3::ZERO, 1e-3));
        assert!(r.dot(figure_turn()).abs() > 0.9999);
    }

    #[test]
    fn a_box_is_turned_by_its_corners() {
        let quarter = Quat::from_axis_angle(Vec3::Z, std::f32::consts::FRAC_PI_2);
        let (lo, hi) = turned_box(
            (Vec3::new(-1.0, -0.5, 0.0), Vec3::new(1.0, 0.5, 2.0)),
            quarter,
        );
        assert!(lo.abs_diff_eq(Vec3::new(-0.5, -1.0, 0.0), 1e-5));
        assert!(hi.abs_diff_eq(Vec3::new(0.5, 1.0, 2.0), 1e-5));
    }

    #[test]
    fn without_a_box_one_is_built_around_the_parts() {
        let part = |x| Vec3::new(x, 0.0, 3.0);
        let (lo, hi) = creature_box(None, 1.0, [part(2.0), part(-0.2)]);
        assert_eq!(lo, Vec3::new(-0.7, -0.5, 0.0));
        assert_eq!(hi, Vec3::new(2.5, 0.5, 3.5));
        // The parts already stand at the creature's size: not grown again.
        assert_eq!(creature_box(None, 2.0, [part(2.0), part(-0.2)]), (lo, hi));
        let own = (Vec3::splat(-3.0), Vec3::splat(3.0));
        assert_eq!(creature_box(Some(own), 1.0, [part(9.0)]), own);
        assert_eq!(
            creature_box(None, 1.0, []),
            crate::viewport::camera::EXAMINE_DEFAULT_BOX
        );
    }

    #[test]
    fn a_creatures_own_box_grows_with_its_size() {
        let own = (Vec3::new(-0.4, -0.3, 0.0), Vec3::new(0.4, 0.3, 1.8));
        let (lo, hi) = creature_box(Some(own), 1.15, []);
        assert!(lo.abs_diff_eq(own.0 * 1.15, 1e-6));
        assert!(hi.abs_diff_eq(own.1 * 1.15, 1e-6));
    }

    /// The portrait frames the creature as drawn: a creature larger (or
    /// smaller) than its model, as many are, still fits the viewport
    /// whatever the viewport's shape, filling it along one axis.
    #[test]
    fn a_scaled_creature_fits_the_portrait() {
        use crate::viewport::camera::{EXAMINE_FAR, ViewportCamera, examine_eye};
        let own = (Vec3::new(-0.4, -0.3, 0.0), Vec3::new(0.4, 0.3, 1.8));
        for scale in [0.6_f32, 1.0, 1.15, 2.5] {
            for aspect in [300.0 / 265.0, 240.0 / 165.0, 0.5, 2.0] {
                let framed = turned_box(creature_box(Some(own), scale, []), figure_turn());
                let mut camera =
                    ViewportCamera::new(examine_eye(framed.0, framed.1, aspect), EXAMINE_FAR);
                camera.aspect = aspect;
                let clip = camera.projection() * camera.view();
                // The corners of the copy as drawn: the model's box at the
                // creature's size, turned toward the viewer.
                let mut reach = 0.0_f32;
                for corner in 0..8 {
                    let p = Vec3::new(
                        if corner & 1 == 0 { own.0.x } else { own.1.x },
                        if corner & 2 == 0 { own.0.y } else { own.1.y },
                        if corner & 4 == 0 { own.0.z } else { own.1.z },
                    );
                    let ndc = clip.project_point3(figure_turn() * (p * scale));
                    reach = reach.max(ndc.x.abs()).max(ndc.y.abs());
                }
                assert!(
                    reach <= 1.0 + 1e-4,
                    "scale {scale}, aspect {aspect}: the figure reaches {reach} of the half view"
                );
                assert!(
                    reach > 0.8,
                    "scale {scale}, aspect {aspect}: the figure fills only {reach}"
                );
            }
        }
    }

    #[test]
    fn the_figure_turn_is_a_little_past_facing_the_viewer() {
        // The copy's forward (+Y) is turned 191.37 degrees clockwise seen
        // from above: it faces the eye (-Y), a little to the side.
        let forward = figure_turn() * Vec3::Y;
        assert!(forward.y < -0.97);
        assert!(forward.x.abs() > 0.1);
    }
}
