//! Level of detail: which version of a model part the game draws at a
//! given distance.
//!
//! A GfxObj may name a degrade table: a ladder of stand-in models from the
//! close, detailed one (level 0) to cheap distant ones, some of them flat
//! sprites that turn to face the camera, and possibly an empty level (id 0)
//! where the part is not drawn at all.
//!
//! Only building shells walk the ladder: each frame the shell's distance
//! from the viewer picks a level and the shell draws that level's model.
//! Every other object (statics, scenery, server objects) draws its own
//! model at any distance, so trees and fences stay whole out to the edge of
//! the near tier; a humanoid server object draws each part's closest level
//! instead ([`close_gfx_obj`]); an editor-only marker is never drawn.

use glam::{Mat3, Mat4, Vec3};

/// One rung of a degrade ladder.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DegradeLevel {
    /// The model drawn at this level; 0 draws nothing.
    pub gfx_obj_id: u32,
    /// How the model is drawn (2..=5: turned to face the camera).
    pub mode: u32,
    /// Distances, metres.
    pub min_dist: f32,
    /// The distance the level is made for.
    pub ideal_dist: f32,
    /// Beyond this the next level is used (at the neutral multiplier... see
    /// [`select_level`]).
    pub max_dist: f32,
}

/// The settings that move the ladder: the distance before any degrading
/// starts (metres) and the bias (negative keeps detail longer, positive
/// degrades sooner).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DegradeSettings {
    /// Metres subtracted from every distance first.
    pub degrade_distance: f32,
    /// The bias between the levels' bands.
    pub multiplier: f32,
    /// A building whose chosen level is empty or a camera-facing sprite
    /// keeps the nearest solid level instead (an OpenAC display option).
    pub keep_distant_buildings: bool,
    /// Building shells skip the ladder and draw level 0 at any distance:
    /// the overhead map view, whose eye is hundreds of metres away, keeps
    /// every building at full detail.
    pub degrades_disabled: bool,
}

impl Default for DegradeSettings {
    /// OpenAC's display defaults: 50 m, no bias, distant buildings kept.
    fn default() -> Self {
        Self {
            degrade_distance: 50.0,
            multiplier: 0.0,
            keep_distant_buildings: true,
            degrades_disabled: false,
        }
    }
}

/// The level a part at `distance` (metres, already divided by the part's
/// z scale) uses: the first whose band reaches past the distance, else the
/// last.
#[must_use]
pub fn select_level(levels: &[DegradeLevel], distance: f32, settings: &DegradeSettings) -> usize {
    if levels.is_empty() {
        return 0;
    }
    // Negative becomes 0; a distance that is not a number stays one and
    // falls through to the last level, as the game's comparisons do.
    let beyond = distance.abs() - settings.degrade_distance;
    let effective = if beyond < 0.0 { 0.0 } else { beyond };
    let m = f64::from(settings.multiplier);
    for (i, level) in levels.iter().enumerate() {
        let ideal = f64::from(level.ideal_dist);
        let threshold = if m >= 0.0 {
            ideal - (ideal - f64::from(level.max_dist)) * m
        } else {
            ideal + (ideal - f64::from(level.min_dist)) * m
        };
        if f64::from(effective) < threshold {
            return i;
        }
    }
    levels.len() - 1
}

/// The model a part draws at `distance` from the viewer; 0 when the chosen
/// level draws nothing.
#[must_use]
pub fn degraded_gfx_obj(levels: &[DegradeLevel], distance: f32, settings: &DegradeSettings) -> u32 {
    levels
        .get(select_level(levels, distance, settings))
        .map_or(0, |l| l.gfx_obj_id)
}

/// Whether a level's model is a sprite turned to face the camera.
fn is_camera_facing(mode: u32) -> bool {
    (2..=5).contains(&mode)
}

/// The level a building shell draws at `distance`: as [`select_level`]
/// (level 0 whatever the distance with
/// [`DegradeSettings::degrades_disabled`]), but with
/// [`DegradeSettings::keep_distant_buildings`] an empty or camera-facing
/// level falls back to the nearest solid one before it (or the nearest
/// non-empty one when every earlier level faces the camera).
#[must_use]
pub fn select_building_level(
    levels: &[DegradeLevel],
    distance: f32,
    settings: &DegradeSettings,
) -> usize {
    let index = if settings.degrades_disabled {
        0
    } else {
        select_level(levels, distance, settings)
    };
    if !settings.keep_distant_buildings || levels.is_empty() {
        return index;
    }
    let chosen = levels[index];
    if chosen.gfx_obj_id != 0 && !is_camera_facing(chosen.mode) {
        return index;
    }
    let mut nearest_nonblank = None;
    for i in (0..=index).rev() {
        if levels[i].gfx_obj_id == 0 {
            continue;
        }
        if nearest_nonblank.is_none() {
            nearest_nonblank = Some(i);
        }
        if !is_camera_facing(levels[i].mode) {
            return i;
        }
    }
    nearest_nonblank.unwrap_or(index)
}

/// The distance a part's level is chosen by: from the viewer to the part's
/// sort centre (scaled), divided by the part's z scale.
#[must_use]
pub fn part_distance(viewer: Vec3, sort_center_world: Vec3, scale_z: f32) -> f32 {
    let d = (sort_center_world - viewer).length();
    if scale_z > 0.0 { d / scale_z } else { d }
}

/// An editor-only placement marker: a model whose closest level is meant
/// for distance 0 only and whose ladder has an empty level, so the game
/// never draws it at any real distance.
#[must_use]
pub fn is_runtime_hidden_marker(levels: &[DegradeLevel]) -> bool {
    levels.first().is_some_and(|l| l.max_dist == 0.0) && levels.iter().any(|l| l.gfx_obj_id == 0)
}

/// The model a part made of `base` draws at close range: its ladder's
/// level 0 when that names a model that exists, else `base` itself.
/// `ladder_of` gives a GfxObj's ladder (its levels' model ids, empty
/// without one), `None` when the GfxObj is missing; a missing `base` gives
/// `None`.
pub fn close_gfx_obj(base: u32, ladder_of: impl Fn(u32) -> Option<Vec<u32>>) -> Option<u32> {
    let ladder = ladder_of(base)?;
    let Some(&close) = ladder.first() else {
        return Some(base);
    };
    if close == 0 || ladder_of(close).is_none() {
        return Some(base);
    }
    Some(close)
}

/// The id of the empty "null" part that humanoid setups fill their unused
/// part slots with.
const NULL_PART_GFX_OBJ: u32 = 0x0100_01EC;

/// Whether a setup's parts (their model ids, in setup order) have the
/// humanoid layout whose parts are drawn at their closest level: 34 parts,
/// at least 8 of those from index 17 on being the null part.
#[must_use]
pub fn has_humanoid_null_part_layout(part_ids: impl IntoIterator<Item = u32>) -> bool {
    let (mut count, mut null_slots) = (0usize, 0usize);
    for (i, id) in part_ids.into_iter().enumerate() {
        count += 1;
        if i >= 17 && id == NULL_PART_GFX_OBJ {
            null_slots += 1;
        }
    }
    count == 34 && null_slots >= 8
}

/// Where a building shell's part 0 is drawn at a level of `mode`:
/// `placement` itself unless the mode turns the model toward the viewer.
/// Mode 2 turns the whole model to head (its +y) along the line from
/// `viewer` to its sort centre; modes 3, 4 and 5 keep the model's x, y or
/// z axis and swing the axis two after it (z, x, y) toward that line.
/// `sort_center` is in the model's own frame; scale and position are kept.
#[must_use]
pub fn camera_facing_placement(
    placement: Mat4,
    sort_center: Vec3,
    viewer: Vec3,
    mode: u32,
) -> Mat4 {
    use ac_physics::globals::{EPSILON, EPSILON_SQ};
    if !is_camera_facing(mode) || !placement.is_finite() {
        return placement;
    }
    let (scale, orientation, translation) = placement.to_scale_rotation_translation();
    if !(scale.is_finite() && orientation.is_finite() && translation.is_finite()) {
        return placement;
    }

    let heading = placement.transform_point3(sort_center) - viewer;
    let length_squared = heading.length_squared();
    if !length_squared.is_finite() {
        return placement;
    }
    let heading = if length_squared < EPSILON_SQ {
        Vec3::Z
    } else {
        heading / length_squared.sqrt()
    };

    if mode == 2 {
        let orientation = ac_physics::frame_math::set_vector_heading(orientation, heading);
        return Mat4::from_scale_rotation_translation(scale, orientation, translation);
    }

    let rotation = Mat3::from_quat(orientation);
    let mut axes = [rotation.x_axis, rotation.y_axis, rotation.z_axis];
    let fixed_axis = (mode - 3) as usize;
    let facing_axis = (fixed_axis + 2) % 3;
    let remaining_axis = (facing_axis + 2) % 3;
    let axis = axes[fixed_axis];
    let mut facing = heading - axis * axis.dot(heading);
    let projected_length_squared = facing.length_squared();
    if projected_length_squared < EPSILON_SQ {
        facing = axes[facing_axis];
    } else {
        facing /= projected_length_squared.sqrt();
    }
    if axis.dot(facing).abs() > EPSILON * 5.0 {
        return placement;
    }
    axes[facing_axis] = facing;
    axes[remaining_axis] = facing.cross(axis);
    Mat4::from_translation(translation)
        * Mat4::from_mat3(Mat3::from_cols(axes[0], axes[1], axes[2]))
        * Mat4::from_scale(scale)
}

#[cfg(test)]
mod tests {
    use super::*;
    use glam::Quat;

    // OpenAC `WalkBuildingDrawTransform.Resolve` (no test of its own; the
    // populator pins mode 4 at a translation, `WalkStaticStreamPopulatorTests`
    // ClassifyEntityForWalk_..., so these work the rule through by hand).
    fn near(a: Vec3, b: Vec3) -> bool {
        (a - b).length() < 1e-4
    }

    #[test]
    fn only_modes_two_to_five_turn_a_level() {
        let placement = Mat4::from_translation(Vec3::new(8.0, 9.0, 10.0));
        for mode in [0, 1, 6, 7] {
            assert_eq!(
                camera_facing_placement(placement, Vec3::ZERO, Vec3::ZERO, mode),
                placement
            );
        }
    }

    #[test]
    fn mode_two_heads_the_model_along_the_line_from_the_viewer() {
        // Viewer at the origin, model 10 m along +x scaled 2: its +y now
        // points along +x, its +z stays up.
        let placement = Mat4::from_scale_rotation_translation(
            Vec3::splat(2.0),
            Quat::IDENTITY,
            Vec3::new(10.0, 0.0, 0.0),
        );
        let turned = camera_facing_placement(placement, Vec3::ZERO, Vec3::ZERO, 2);
        assert!(near(
            turned.transform_point3(Vec3::Y),
            Vec3::new(12.0, 0.0, 0.0)
        ));
        assert!(near(
            turned.transform_point3(Vec3::Z),
            Vec3::new(10.0, 0.0, 2.0)
        ));
        assert!(near(
            turned.transform_point3(Vec3::X),
            Vec3::new(10.0, -2.0, 0.0)
        ));
    }

    #[test]
    fn mode_four_keeps_y_and_swings_x_toward_the_line() {
        // The populator's case: a model at (8, 9, 10), viewer and sort
        // centre at the origin. y stays; x swings to (8, 0, 10) / |..|.
        let placement = Mat4::from_translation(Vec3::new(8.0, 9.0, 10.0));
        let turned = camera_facing_placement(placement, Vec3::ZERO, Vec3::ZERO, 4);
        let len = 164.0_f32.sqrt();
        let x = Vec3::new(8.0 / len, 0.0, 10.0 / len);
        let o = Vec3::new(8.0, 9.0, 10.0);
        assert!(near(turned.transform_point3(Vec3::X), o + x));
        assert!(near(turned.transform_point3(Vec3::Y), o + Vec3::Y));
        assert!(near(
            turned.transform_point3(Vec3::Z),
            o + Vec3::new(-x.z, 0.0, x.x)
        ));
    }

    #[test]
    fn mode_five_keeps_z_and_swings_y_toward_the_line() {
        // An upright sprite at (0, 10, 0) seen from (10, 10, 0): its y turns
        // to -x (away from the viewer), x follows as y × z.
        let placement = Mat4::from_translation(Vec3::new(0.0, 10.0, 0.0));
        let viewer = Vec3::new(10.0, 10.0, 0.0);
        let turned = camera_facing_placement(placement, Vec3::ZERO, viewer, 5);
        let o = Vec3::new(0.0, 10.0, 0.0);
        assert!(near(turned.transform_point3(Vec3::Y), o - Vec3::X));
        assert!(near(turned.transform_point3(Vec3::X), o + Vec3::Y));
        assert!(near(turned.transform_point3(Vec3::Z), o + Vec3::Z));
    }

    #[test]
    fn mode_three_keeps_x_and_a_viewer_on_the_kept_axis_changes_nothing() {
        // Mode 3 keeps x; the line runs along x itself, so the facing axis
        // (z) keeps its own direction and the model is not turned.
        let placement = Mat4::from_translation(Vec3::new(5.0, 0.0, 0.0));
        let turned = camera_facing_placement(placement, Vec3::ZERO, Vec3::ZERO, 3);
        assert!(near(
            turned.transform_point3(Vec3::Z),
            Vec3::new(5.0, 0.0, 1.0)
        ));
        assert!(near(
            turned.transform_point3(Vec3::Y),
            Vec3::new(5.0, 1.0, 0.0)
        ));
    }

    // Ported from OpenAC tests/AcDream.Core.Tests/Meshing/GfxObjDegradeResolverTests.cs
    // (`TryResolveCloseGfxObj`); ladders as the dats store them.
    fn ladders<'a>(entries: &'a [(u32, &'a [u32])]) -> impl Fn(u32) -> Option<Vec<u32>> + 'a {
        move |id| {
            entries
                .iter()
                .find(|(g, _)| *g == id)
                .map(|(_, l)| l.to_vec())
        }
    }

    #[test]
    fn a_model_without_a_ladder_is_its_own_close_model() {
        let dats = ladders(&[(0x0100_1212, &[])]);
        assert_eq!(close_gfx_obj(0x0100_1212, dats), Some(0x0100_1212));
    }

    #[test]
    fn a_ladder_gives_its_level_zero() {
        // The low-detail Aluvian male upper arm and its close model.
        let dats = ladders(&[(0x0100_0055, &[0x0100_1795]), (0x0100_1795, &[])]);
        assert_eq!(close_gfx_obj(0x0100_0055, dats), Some(0x0100_1795));
    }

    #[test]
    fn a_missing_or_empty_level_zero_keeps_the_base() {
        let missing = ladders(&[(0x0100_0055, &[0xDEAD_BEEF])]);
        assert_eq!(close_gfx_obj(0x0100_0055, missing), Some(0x0100_0055));
        let empty_ladder = ladders(&[(0x0100_0055, &[])]);
        assert_eq!(close_gfx_obj(0x0100_0055, empty_ladder), Some(0x0100_0055));
        let blank = ladders(&[(0x0100_0055, &[0, 0x0100_1795]), (0x0100_1795, &[])]);
        assert_eq!(close_gfx_obj(0x0100_0055, blank), Some(0x0100_0055));
    }

    #[test]
    fn a_missing_base_has_no_close_model() {
        assert_eq!(close_gfx_obj(0xDEAD_BEEF, ladders(&[])), None);
    }

    #[test]
    fn the_humanoid_layout_has_34_parts_and_eight_null_slots_from_17() {
        let mut parts = [0x0100_004E; 34];
        for p in &mut parts[17..25] {
            *p = NULL_PART_GFX_OBJ;
        }
        assert!(has_humanoid_null_part_layout(parts.iter().copied()));
        parts[24] = 0x0100_0001;
        assert!(
            !has_humanoid_null_part_layout(parts.iter().copied()),
            "seven null slots"
        );
        // Null parts before index 17 do not count.
        parts[0] = NULL_PART_GFX_OBJ;
        assert!(!has_humanoid_null_part_layout(parts.iter().copied()));
        let mut short = vec![NULL_PART_GFX_OBJ; 33];
        assert!(!has_humanoid_null_part_layout(short.iter().copied()));
        short.extend([NULL_PART_GFX_OBJ; 2]);
        assert!(
            !has_humanoid_null_part_layout(short.iter().copied()),
            "35 parts"
        );
    }

    fn level(id: u32, mode: u32, min: f32, ideal: f32, max: f32) -> DegradeLevel {
        DegradeLevel {
            gfx_obj_id: id,
            mode,
            min_dist: min,
            ideal_dist: ideal,
            max_dist: max,
        }
    }

    fn ladder() -> Vec<DegradeLevel> {
        vec![
            level(0x0100_0001, 1, 0.0, 20.0, 40.0),
            level(0x0100_0002, 1, 20.0, 60.0, 100.0),
            level(0x0100_0003, 3, 60.0, 150.0, 300.0),
        ]
    }

    #[test]
    fn levels_follow_the_ideal_distances_past_the_degrade_distance() {
        let s = DegradeSettings {
            keep_distant_buildings: false,
            ..DegradeSettings::default()
        };
        // Within the 50 m degrade distance everything is level 0.
        assert_eq!(select_level(&ladder(), 49.0, &s), 0);
        assert_eq!(select_level(&ladder(), 69.9, &s), 0);
        assert_eq!(select_level(&ladder(), 70.0, &s), 1);
        assert_eq!(select_level(&ladder(), 199.0, &s), 2);
        assert_eq!(select_level(&ladder(), 10_000.0, &s), 2);
    }

    #[test]
    fn the_multiplier_moves_the_threshold_towards_max_or_min() {
        let mut s = DegradeSettings {
            degrade_distance: 0.0,
            multiplier: 1.0,
            keep_distant_buildings: false,
            degrades_disabled: false,
        };
        // Multiplier 1: the threshold is max_dist.
        assert_eq!(select_level(&ladder(), 39.0, &s), 0);
        assert_eq!(select_level(&ladder(), 41.0, &s), 1);
        // Multiplier -1: the threshold is min_dist.
        s.multiplier = -1.0;
        assert_eq!(select_level(&ladder(), 0.0, &s), 1);
    }

    #[test]
    fn distant_buildings_keep_the_nearest_solid_level() {
        let s = DegradeSettings::default();
        // Level 2 faces the camera: kept at level 1.
        assert_eq!(select_building_level(&ladder(), 10_000.0, &s), 1);
        let blank = vec![
            level(0x0100_0001, 1, 0.0, 20.0, 40.0),
            level(0, 1, 0.0, 1e9, 1e9),
        ];
        assert_eq!(select_building_level(&blank, 500.0, &s), 0);
        let off = DegradeSettings {
            keep_distant_buildings: false,
            ..s
        };
        assert_eq!(select_building_level(&blank, 500.0, &off), 1);
    }

    #[test]
    fn a_marker_is_hidden_at_every_real_distance() {
        // As in OpenAC: slot 0 for distance 0 only, slot 1 empty.
        let marker = vec![
            level(0x0100_28CA, 1, 0.0, 0.0, 0.0),
            level(0, 1, 0.0, f32::MAX, f32::MAX),
        ];
        assert!(is_runtime_hidden_marker(&marker));
        assert_eq!(
            degraded_gfx_obj(&marker, 60.0, &DegradeSettings::default()),
            0
        );
        assert!(!is_runtime_hidden_marker(&ladder()));
    }

    // Ported from OpenAC tests/AcDream.App.Tests/Rendering/Walk/RetailFrameWalkTests.cs
    // (the WalkBuilding.Select cases).

    fn settings(distance: f32, multiplier: f32, keep: bool) -> DegradeSettings {
        DegradeSettings {
            degrade_distance: distance,
            multiplier,
            keep_distant_buildings: keep,
            degrades_disabled: false,
        }
    }

    // OpenAC RetailFrameWalkTests:
    // KeepDistantBuildingsReachesSelectionThroughTheWalkContext and
    // OverheadBuildingSelectionSurvivesTheFinalEmptyLevelAndRestoresDistanceSelection.
    #[test]
    fn the_overhead_view_draws_every_building_at_level_zero() {
        let overhead = |keep| DegradeSettings {
            degrades_disabled: true,
            ..settings(50.0, 0.0, keep)
        };
        let ladder = [
            level(0x0100_0051, 3, 1.0, 2.0, 3.0),
            level(0, 7, 5.0, 6.0, 7.0),
        ];
        assert_eq!(
            select_building_level(&ladder, 450.0, &settings(50.0, 0.0, false)),
            1
        );
        assert_eq!(select_building_level(&ladder, 450.0, &overhead(false)), 0);
        assert_eq!(select_building_level(&ladder, 450.0, &overhead(true)), 0);
        let blank_end = [
            level(0x0100_0001, 1, 24.0, 48.0, 96.0),
            level(0x0100_0002, 1, 192.0, 392.0, 392.0),
            level(0, 1, f32::MAX, f32::MAX, f32::MAX),
        ];
        assert_eq!(
            select_building_level(&blank_end, 450.0, &settings(50.0, 0.0, false)),
            2
        );
        assert_eq!(
            select_building_level(&blank_end, 450.0, &overhead(false)),
            0
        );
        assert_eq!(
            select_building_level(&blank_end, f32::INFINITY, &overhead(false)),
            0
        );
    }

    #[test]
    fn selection_uses_a_strict_threshold() {
        let levels = [
            level(0x0100_0001, 1, 10.0, 20.0, 30.0),
            level(0, 1, 30.0, 40.0, 50.0),
        ];
        let s = settings(0.0, 1.0, false);
        assert_eq!(degraded_gfx_obj(&levels, 29.999, &s), 0x0100_0001);
        assert_eq!(select_level(&levels, 30.0, &s), 1);
        assert_eq!(degraded_gfx_obj(&levels, 30.0, &s), 0);
    }

    #[test]
    fn the_stored_float_distance_is_compared_with_the_wide_threshold() {
        let effective = f32::from_bits(0x41C0_6267);
        let levels = [
            level(0x0100_0001, 1, 0.0, 24.0, 48.0),
            level(0x0100_0002, 1, 48.0, 64.0, 80.0),
        ];
        let s = settings(0.0, 0.002_002_002, false);
        assert_eq!(select_level(&levels, effective, &s), 0);
    }

    #[test]
    fn ladder_selection_follows_the_bias_and_odd_distances_take_the_last_level() {
        let levels = [
            level(0x0100_0011, 3, 10.0, 20.0, 30.0),
            level(0x0100_0012, 5, 30.0, 40.0, 50.0),
            level(0, 7, 50.0, 60.0, 70.0),
        ];
        assert_eq!(select_level(&levels, 29.0, &settings(5.0, 0.5, false)), 0);
        assert_eq!(select_level(&levels, -19.0, &settings(5.0, -0.5, false)), 0);
        assert_eq!(select_level(&levels, 30.0, &settings(5.0, 0.5, false)), 1);
        for d in [f32::INFINITY, f32::NEG_INFINITY, f32::NAN] {
            assert_eq!(select_level(&levels, d, &settings(5.0, 0.0, false)), 2);
        }
    }

    #[test]
    fn keep_distant_buildings_walks_down_to_a_mesh_never_up() {
        let blank_end = [
            level(0x0100_0031, 3, 1.0, 2.0, 3.0),
            level(0x0100_0032, 5, 3.0, 4.0, 5.0),
            level(0, 7, 5.0, 6.0, 7.0),
            level(0, 9, 7.0, 8.0, 9.0),
        ];
        assert_eq!(
            select_building_level(&blank_end, f32::INFINITY, &settings(0.0, 0.0, true)),
            1
        );
        assert_eq!(
            select_building_level(&blank_end, f32::INFINITY, &settings(0.0, 0.0, false)),
            3
        );
        let blank_middle = [
            level(0x0100_0061, 3, 1.0, 2.0, 3.0),
            level(0, 5, 3.0, 4.0, 5.0),
            level(0x0100_0063, 7, 5.0, 6.0, 7.0),
        ];
        assert_eq!(
            select_building_level(&blank_middle, 3.0, &settings(0.0, 0.0, true)),
            0
        );
        assert_eq!(
            select_building_level(&blank_middle, 3.0, &settings(0.0, 0.0, false)),
            1
        );
        let no_blank = [
            level(0x0100_0041, 3, 1.0, 2.0, 3.0),
            level(0x0100_0042, 5, 3.0, 4.0, 5.0),
        ];
        for keep in [false, true] {
            assert_eq!(
                select_building_level(&no_blank, 0.0, &settings(0.0, 0.0, keep)),
                0
            );
            assert_eq!(
                select_building_level(&no_blank, f32::INFINITY, &settings(0.0, 0.0, keep)),
                1
            );
        }
    }
}
