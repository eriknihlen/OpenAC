use super::*;

const RANGE: f32 = DRAW_RANGE_METRES;

fn line(start: Vec3, end: Vec3, follow_terrain: bool) -> PlacedLine {
    PlacedLine {
        start,
        end,
        color_rgb: 0xFF40FF,
        follow_terrain,
        width_metres: DEFAULT_WIDTH_METRES,
    }
}

fn no_ground(_: f32, _: f32) -> Option<f32> {
    None
}

fn draw(lines: &[PlacedLine], camera: Vec3) -> Vec<WorldLineVertex> {
    let mut out = Vec::new();
    build(lines, camera, no_ground, &mut out);
    out
}

fn positions(vertices: &[WorldLineVertex]) -> Vec<Vec3> {
    vertices.iter().map(|v| Vec3::from(v.position)).collect()
}

// OpenAC `DebugLineRenderer.VertexBudget`: 4 MiB of 24-byte vertices.
#[test]
fn the_budget_is_four_mebibytes_of_vertices() {
    assert_eq!(size_of::<WorldLineVertex>(), 24);
    assert_eq!(VERTEX_BUDGET, 174_762);
}

// OpenAC `PluginWorldLineRendererTests.ALongGroundLineFromTheCameraIsOnlyCutIntoPiecesAsFarAsTheDrawRange`.
#[test]
fn a_long_ground_line_from_the_camera_is_only_cut_into_pieces_as_far_as_the_draw_range() {
    let out = draw(
        &[line(Vec3::ZERO, Vec3::new(5000.0, 0.0, 0.0), true)],
        Vec3::ZERO,
    );
    assert!(!out.is_empty());
    // Drawn out to the edge of the draw range and no further: the pieces of
    // its visible 250 m, not of its whole 5 km.
    let max_x = positions(&out).iter().map(|p| p.x).fold(f32::MIN, f32::max);
    assert!(
        (RANGE - 1.0..=RANGE + PIECE_METRES + 0.5).contains(&max_x),
        "{max_x}"
    );
    assert!(out.len() <= ((RANGE / PIECE_METRES).ceil() as usize + 2) * 36);
}

// OpenAC `PluginWorldLineRendererTests.AGroundLinePassingTheCameraWithBothEndsOutOfRangeIsDrawnWhereItIsInRange`.
#[test]
fn a_ground_line_passing_the_camera_with_both_ends_out_of_range_is_drawn_where_it_is_in_range() {
    let out = draw(
        &[line(
            Vec3::new(-1000.0, 10.0, 0.0),
            Vec3::new(1000.0, 10.0, 0.0),
            true,
        )],
        Vec3::ZERO,
    );
    assert!(!out.is_empty());
    for p in positions(&out) {
        assert!((-RANGE - 1.0..=RANGE + 1.0).contains(&p.x), "{p}");
    }
}

// OpenAC `PluginWorldLineRendererTests.HoweverManyLinesAPluginSendsTheUploadStaysWithinTheBudget`.
#[test]
fn however_many_lines_a_plugin_sends_the_upload_stays_within_the_budget() {
    let lines: Vec<PlacedLine> = (0..2000)
        .map(|i| {
            let y = (i % 200) as f32 - 100.0;
            let x = (i / 200) as f32 * 10.0 - 50.0;
            line(Vec3::new(x, y, 0.0), Vec3::new(x + 10.0, y, 0.0), true)
        })
        .collect();
    let out = draw(&lines, Vec3::ZERO);
    assert!((1..=VERTEX_BUDGET).contains(&out.len()), "{}", out.len());
}

// OpenAC `PluginWorldLineRendererTests.WhenTheBudgetRunsOutTheLinesNearestTheCameraAreTheOnesDrawn`.
#[test]
fn when_the_budget_runs_out_the_lines_nearest_the_camera_are_the_ones_drawn() {
    let mut lines: Vec<PlacedLine> = (0..1000)
        .map(|i| {
            // Far lines, on a ring 200 m out, sent first.
            let angle = i as f32 * std::f32::consts::TAU / 1000.0;
            let at = Vec3::new(angle.cos() * 200.0, angle.sin() * 200.0, 0.0);
            line(at, at + Vec3::new(10.0, 0.0, 0.0), true)
        })
        .collect();
    // The one line right beside the camera is sent last.
    lines.push(line(
        Vec3::new(5.0, -1.0, 0.0),
        Vec3::new(5.0, 1.0, 0.0),
        false,
    ));
    let out = draw(&lines, Vec3::ZERO);
    assert!((1..=VERTEX_BUDGET).contains(&out.len()));
    assert!(
        positions(&out)
            .iter()
            .any(|p| (p.x - 5.0).abs() < 0.5 && p.y.abs() <= 1.5)
    );
}

// The bar of a straight line (OpenAC `PluginWorldLineRenderer.DrawBar`): a
// box around the segment, its width across and up, in its colour.
#[test]
fn a_straight_line_is_one_box_as_wide_and_tall_as_its_width() {
    let mut l = line(Vec3::ZERO, Vec3::new(10.0, 0.0, 0.0), false);
    l.width_metres = 0.5;
    l.color_rgb = 0x336699;
    let out = draw(&[l], Vec3::ZERO);
    assert_eq!(out.len(), VERTICES_PER_BAR);
    for v in &out {
        let p = Vec3::from(v.position);
        assert!(p.x == 0.0 || p.x == 10.0, "{p}");
        assert!((p.y.abs() - 0.25).abs() < 1e-6, "{p}");
        assert!((p.z.abs() - 0.25).abs() < 1e-6, "{p}");
        assert_eq!(
            v.color,
            [
                0x33 as f32 / 255.0,
                0x66 as f32 / 255.0,
                0x99 as f32 / 255.0
            ]
        );
    }
    // The first face is the start's end cap: -side-up, +side-up, +side+up.
    // Side is the direction crossed with up (+Z): -Y for a line due east.
    assert_eq!(out[0].position, [0.0, 0.25, -0.25]);
    assert_eq!(out[1].position, [0.0, -0.25, -0.25]);
    assert_eq!(out[2].position, [0.0, -0.25, 0.25]);
}

// A vertical line has no side from up; OpenAC's falls back to +X.
#[test]
fn a_vertical_line_takes_its_side_along_x() {
    let mut l = line(Vec3::ZERO, Vec3::new(0.0, 0.0, 3.0), false);
    l.width_metres = 1.0;
    let out = draw(&[l], Vec3::ZERO);
    assert_eq!(out.len(), VERTICES_PER_BAR);
    for p in positions(&out) {
        assert!((p.x.abs() - 0.5).abs() < 1e-6, "{p}");
        assert!((p.y.abs() - 0.5).abs() < 1e-6, "{p}");
    }
}

// OpenAC `PluginWorldLineRenderer.Render`: widths clamp to 0.01..2 m and a
// width that is not a number draws at 0.25 m.
#[test]
fn widths_are_clamped_and_a_bad_width_draws_at_the_default() {
    assert_eq!(thickness(0.25), 0.25);
    assert_eq!(thickness(0.0), 0.01);
    assert_eq!(thickness(-3.0), 0.01);
    assert_eq!(thickness(5.0), 2.0);
    assert_eq!(thickness(f32::NAN), 0.25);
    assert_eq!(thickness(f32::INFINITY), 0.25);
}

// OpenAC `PluginWorldLineRenderer.Ground`: each piece end is raised to
// 0.05 m over the ground under it, never lowered.
#[test]
fn a_terrain_following_line_is_raised_just_above_the_ground_and_never_lowered() {
    let ground = |x: f32, _: f32| Some(if x < 5.0 { 10.0 } else { -4.0 });
    let mut l = line(Vec3::ZERO, Vec3::new(10.0, 0.0, 0.0), true);
    l.width_metres = 0.1;
    let mut out = Vec::new();
    build(&[l], Vec3::ZERO, ground, &mut out);
    // 10 m in pieces of 0.75 m: 14 pieces.
    assert_eq!(out.len(), 14 * VERTICES_PER_BAR);
    for p in positions(&out) {
        let centre = if p.x < 5.0 - 0.1 {
            10.05
        } else if p.x > 5.0 + 0.8 {
            0.0
        } else {
            continue;
        };
        assert!((p.z - centre).abs() <= 0.05 + 1e-4, "{p}");
    }
    // A straight line ignores the ground.
    let mut out = Vec::new();
    build(
        &[line(Vec3::ZERO, Vec3::new(10.0, 0.0, 0.0), false)],
        Vec3::ZERO,
        ground,
        &mut out,
    );
    assert_eq!(out.len(), VERTICES_PER_BAR);
    assert!(positions(&out).iter().all(|p| p.z.abs() <= 0.125 + 1e-6));
}

#[test]
fn a_line_out_of_range_or_not_a_number_is_not_drawn() {
    assert_eq!(
        plan_pieces(
            Vec3::new(300.0, 0.0, 0.0),
            Vec3::new(400.0, 0.0, 0.0),
            true,
            Vec3::ZERO,
            RANGE
        ),
        None
    );
    assert!(
        draw(
            &[line(Vec3::new(f32::NAN, 0.0, 0.0), Vec3::ONE, false)],
            Vec3::ZERO
        )
        .is_empty()
    );
    // A point line in range is one piece.
    assert_eq!(
        plan_pieces(Vec3::ONE, Vec3::ONE, true, Vec3::ZERO, RANGE),
        Some(LinePieces {
            count: 1,
            first: 0,
            last: 1,
            distance_squared: 3.0
        })
    );
}

// The pieces of a terrain-following line are cut along its whole ground
// length, so they stay put as the camera moves.
#[test]
fn ground_pieces_are_cut_along_the_whole_line() {
    let pieces = plan_pieces(
        Vec3::new(-1000.0, 0.0, 0.0),
        Vec3::new(1000.0, 0.0, 0.0),
        true,
        Vec3::ZERO,
        RANGE,
    )
    .unwrap();
    // 2000 m in pieces of 0.75 m.
    assert_eq!(pieces.count, 2667);
    // In range from 750 m to 1250 m along.
    assert_eq!(pieces.first, (0.375f64 * 2667.0).floor() as u32);
    assert_eq!(pieces.last, (0.625f64 * 2667.0).ceil() as u32);
    assert_eq!(pieces.distance_squared, 0.0);
}

/// Ground level along x, flat at 0 but for a smooth bowl (negative
/// `height`) or mound (positive) from x = 20 to x = 80.
fn bump(height: f32) -> impl Fn(f32, f32) -> Option<f32> {
    move |x: f32, _: f32| {
        Some(if (20.0..=80.0).contains(&x) {
            height * (std::f32::consts::PI * (x - 20.0) / 60.0).sin()
        } else {
            0.0
        })
    }
}

fn draw_over(l: PlacedLine, ground: &impl Fn(f32, f32) -> Option<f32>) -> Vec<Vec3> {
    let mut out = Vec::new();
    build(&[l], Vec3::ZERO, ground, &mut out);
    assert!(!out.is_empty());
    positions(&out)
}

/// Every corner of a 0.1 m bar within its half width (and a little for the
/// slope) of a centre 0.05 m over the ground.
fn assert_hugs(points: &[Vec3], ground: &impl Fn(f32, f32) -> Option<f32>) {
    for p in points {
        let centre = ground(p.x, p.y).unwrap() + GROUND_LIFT_METRES;
        assert!((p.z - centre).abs() <= 0.05 + 0.03, "{p} vs {centre}");
    }
}

// A route between two points on the ground goes down into a dip on the way,
// not across it in the air.
#[test]
fn a_line_between_two_ground_points_hugs_a_dip() {
    let dip = bump(-5.0);
    let mut l = line(Vec3::ZERO, Vec3::new(100.0, 0.0, 0.0), true);
    l.width_metres = 0.1;
    let points = draw_over(l, &dip);
    assert_hugs(&points, &dip);
    let lowest = points.iter().map(|p| p.z).fold(f32::MAX, f32::min);
    assert!(lowest < -4.9, "{lowest}");
    // Ends a little off the ground still count as on it.
    let mut l = line(
        Vec3::new(0.0, 0.0, ON_GROUND_METRES),
        Vec3::new(100.0, 0.0, -2.0),
        true,
    );
    l.width_metres = 0.1;
    assert_hugs(&draw_over(l, &dip), &dip);
}

// A route with an end on a bridge deck over the dip keeps its height across
// it: the ground only raises it.
#[test]
fn a_line_with_an_end_on_a_bridge_is_never_lowered() {
    let dip = bump(-5.0);
    // From the bank at x = 10 to the middle of a deck at z = 0 over the
    // dip's floor at x = 50.
    let mut l = line(Vec3::new(10.0, 0.0, 0.0), Vec3::new(50.0, 0.0, 0.0), true);
    l.width_metres = 0.1;
    for p in draw_over(l, &dip) {
        // Kept at the deck's 0, or lifted 0.05 m on the bank.
        assert!(
            (-0.05 - 1e-4..=GROUND_LIFT_METRES + 0.05 + 1e-4).contains(&p.z),
            "{p}"
        );
    }
    // An end just over the on-the-ground height is above the ground, too.
    let mut l = line(
        Vec3::new(10.0, 0.0, ON_GROUND_METRES + 0.1),
        Vec3::new(50.0, 0.0, ON_GROUND_METRES + 0.1),
        true,
    );
    l.width_metres = 0.1;
    let lowest = draw_over(l, &dip)
        .iter()
        .map(|p| p.z)
        .fold(f32::MAX, f32::min);
    assert!(lowest >= ON_GROUND_METRES + 0.1 - 0.05 - 1e-4, "{lowest}");
    // Where the ground under an end is not known, it is not on the ground.
    let unknown_far_end = |x: f32, y: f32| if x < 45.0 { dip(x, y) } else { None };
    let mut l = line(Vec3::new(10.0, 0.0, 0.0), Vec3::new(50.0, 0.0, 0.0), true);
    l.width_metres = 0.1;
    for p in draw_over(l, &unknown_far_end) {
        // Kept at the deck's 0, or lifted 0.05 m on the bank.
        assert!(
            (-0.05 - 1e-4..=GROUND_LIFT_METRES + 0.05 + 1e-4).contains(&p.z),
            "{p}"
        );
    }
}

// A route over a hill rises over it, from the ground and from a bridge.
#[test]
fn a_line_over_a_hill_rises_over_it() {
    let hill = bump(5.0);
    let mut l = line(Vec3::ZERO, Vec3::new(100.0, 0.0, 0.0), true);
    l.width_metres = 0.1;
    let points = draw_over(l, &hill);
    assert_hugs(&points, &hill);
    let highest = points.iter().map(|p| p.z).fold(f32::MIN, f32::max);
    assert!(highest > 5.0, "{highest}");
    // With an end raised above the ground, the hill still lifts it.
    let mut l = line(Vec3::ZERO, Vec3::new(100.0, 0.0, 2.0), true);
    l.width_metres = 0.1;
    let points = draw_over(l, &hill);
    for p in &points {
        let ground = hill(p.x, p.y).unwrap() + GROUND_LIFT_METRES;
        assert!(p.z >= ground - 0.05 - 0.03, "{p}");
    }
    let highest = points.iter().map(|p| p.z).fold(f32::MIN, f32::max);
    assert!(highest > 5.0, "{highest}");
}
