//! A soft edge one pixel wide, fading to clear, straddles every edge, so what
//! the triangles cover weighted by alpha is the shape's own area; curves are
//! cut finely enough to stay within a tenth of a pixel of the true curve; bad
//! polygons are told apart from empty ones.

use std::collections::HashSet;
use std::f64::consts::PI as PI64;

use super::*;

const WHITE: Color = Color::WHITE;
const RED: Color = Color::rgba(1.0, 0.0, 0.0, 1.0);
const BLUE: Color = Color::rgba(0.0, 0.0, 1.0, 1.0);

fn assert_covers(expected: f64, triangles: &[ColorVertex], tolerance: f64) {
    let covered = covered_area(triangles);
    assert!(
        (covered - expected).abs() <= expected * tolerance,
        "covered {covered:.3}, expected {expected:.3} within {tolerance}"
    );
}

fn white(points: &[(f32, f32)]) -> Vec<ColorVertex> {
    painted(points, WHITE)
}

fn polygon(points: &[(f32, f32)]) -> Vec<ColorVertex> {
    let mut out = Vec::new();
    assert_eq!(
        fill_convex_polygon(&white(points), 1.0, &mut out),
        ShapeOutcome::Drawn
    );
    out
}

fn max_alpha(triangles: &[ColorVertex]) -> f32 {
    triangles.iter().map(|v| v.color.a).fold(f32::MIN, f32::max)
}

fn alphas_in_range(triangles: &[ColorVertex]) -> bool {
    triangles.iter().all(|v| (0.0..=1.0).contains(&v.color.a))
}

fn shoelace(points: &[(f32, f32)]) -> f64 {
    let n = points.len();
    let sum: f64 = (0..n)
        .map(|i| {
            let (a, b) = (points[i], points[(i + 1) % n]);
            f64::from(a.0) * f64::from(b.1) - f64::from(b.0) * f64::from(a.1)
        })
        .sum();
    sum.abs() / 2.0
}

fn bits(v: ColorVertex) -> (u32, u32) {
    (v.x.to_bits(), v.y.to_bits())
}

#[test]
fn a_circle_covers_its_area_with_a_solid_inside_and_a_clear_rim() {
    let mut t = Vec::new();
    fill_ellipse((30.0, 10.0, 40.0, 40.0), WHITE, 1.0, &mut t);
    assert_eq!(t.len(), 282);
    assert_covers(PI64 * 20.0 * 20.0, &t, 0.01);
    for v in &t {
        let distance = length((v.x - 50.0, v.y - 30.0));
        if v.color.a == 1.0 {
            assert!(distance <= 19.5 + 1e-3);
        } else {
            assert_eq!(v.color.a, 0.0);
            assert!((distance - 20.5).abs() <= 1e-3);
        }
    }
}

#[test]
fn a_square_cornered_rectangle_has_its_soft_edge_half_inside_and_half_outside() {
    let mut t = Vec::new();
    fill_rounded_rect(
        (10.0, 10.0, 20.0, 20.0),
        CornerRadii::default(),
        WHITE,
        1.0,
        &mut t,
    );
    assert_eq!(t.len(), 30);
    assert_covers(400.0, &t, 0.005);
    let at_alpha = |a: f32| -> HashSet<(u32, u32)> {
        t.iter()
            .filter(|v| v.color.a == a)
            .map(|v| bits(*v))
            .collect()
    };
    let points = |c: [(f32, f32); 4]| -> HashSet<(u32, u32)> {
        c.iter().map(|p| (p.0.to_bits(), p.1.to_bits())).collect()
    };
    assert_eq!(
        at_alpha(0.0),
        points([(9.5, 9.5), (30.5, 9.5), (30.5, 30.5), (9.5, 30.5)])
    );
    assert_eq!(
        at_alpha(1.0),
        points([(10.5, 10.5), (29.5, 10.5), (29.5, 29.5), (10.5, 29.5)])
    );
}

#[test]
fn rounded_corners_take_their_corners_out() {
    let mut t = Vec::new();
    fill_rounded_rect(
        (10.0, 10.0, 60.0, 30.0),
        CornerRadii::all(8.0),
        WHITE,
        1.0,
        &mut t,
    );
    assert_covers(60.0 * 30.0 - (4.0 - PI64) * 8.0 * 8.0, &t, 0.01);
}

#[test]
fn radii_too_large_for_the_rectangle_shrink_together() {
    assert_eq!(
        clamp_radii(CornerRadii::all(40.0), 60.0, 30.0),
        CornerRadii::all(15.0)
    );
    let one = CornerRadii::new(10.0, 0.0, 0.0, 0.0);
    assert_eq!(clamp_radii(one, 60.0, 30.0), one);
    let scaled = clamp_radii(CornerRadii::new(30.0, 40.0, 0.0, 0.0), 60.0, 100.0);
    assert!((25.714..=25.715).contains(&scaled.top_left));
    assert!((34.285..=34.286).contains(&scaled.top_right));

    let mut t = Vec::new();
    fill_rounded_rect(
        (10.0, 10.0, 60.0, 30.0),
        CornerRadii::all(40.0),
        WHITE,
        1.0,
        &mut t,
    );
    assert_covers(60.0 * 30.0 - (4.0 - PI64) * 15.0 * 15.0, &t, 0.01);
}

#[test]
fn a_stroke_is_centred_on_the_outline() {
    let mut rect = Vec::new();
    stroke_rounded_rect(
        (10.0, 10.0, 40.0, 20.0),
        CornerRadii::default(),
        WHITE,
        2.0,
        1.0,
        &mut rect,
    );
    assert_eq!(rect.len(), 72);
    assert_covers(42.0 * 22.0 - 38.0 * 18.0, &rect, 0.005);

    let mut circle = Vec::new();
    stroke_ellipse((10.0, 10.0, 40.0, 40.0), WHITE, 2.0, 1.0, &mut circle);
    assert_eq!(circle.len(), 648);
    assert_covers(PI64 * (21.0 * 21.0 - 19.0 * 19.0), &circle, 0.01);
}

#[test]
fn a_stroke_thinner_than_a_pixel_is_one_pixel_wide_and_proportionally_fainter() {
    let mut circle = Vec::new();
    stroke_ellipse((10.0, 10.0, 40.0, 40.0), WHITE, 0.5, 1.0, &mut circle);
    // Two soft edges and no solid core between them.
    assert_eq!(circle.len(), 432);
    assert_eq!(max_alpha(&circle), 0.5);
    assert_covers(PI64 * 40.0 * 0.5, &circle, 0.01);

    let mut rect = Vec::new();
    stroke_rounded_rect(
        (10.0, 10.0, 40.0, 20.0),
        CornerRadii::all(5.0),
        WHITE,
        0.25,
        1.0,
        &mut rect,
    );
    assert_eq!(max_alpha(&rect), 0.25);
    assert_covers((2.0 * (30.0 + 10.0) + 2.0 * PI64 * 5.0) * 0.25, &rect, 0.01);
}

#[test]
fn a_stroke_thicker_than_the_shape_fills_it() {
    let mut t = Vec::new();
    stroke_rounded_rect(
        (10.0, 10.0, 10.0, 10.0),
        CornerRadii::default(),
        WHITE,
        30.0,
        1.0,
        &mut t,
    );
    assert_covers(40.0 * 40.0, &t, 0.005);
}

#[test]
fn a_stroke_that_leaves_no_hole_covers_the_grown_rectangle() {
    for (width, height, thickness) in [(100.0, 2.0, 2.0), (100.0, 1.0, 1.0), (100.0, 4.0, 4.0)] {
        let mut t = Vec::new();
        stroke_rounded_rect(
            (10.0, 10.0, width, height),
            CornerRadii::default(),
            WHITE,
            thickness,
            1.0,
            &mut t,
        );
        let expected = f64::from(width + thickness) * f64::from(height + thickness);
        assert_covers(expected, &t, 0.01);
    }
}

#[test]
fn a_polygon_covers_its_area_whichever_way_it_winds() {
    let clockwise = [(10.0, 10.0), (50.0, 10.0), (30.0, 40.0)];
    let anticlockwise = [(30.0, 40.0), (50.0, 10.0), (10.0, 10.0)];
    for points in [clockwise, anticlockwise] {
        let t = polygon(&points);
        assert_eq!(t.len(), 21);
        assert_covers(600.0, &t, 0.005);
    }
}

#[test]
fn each_corner_keeps_its_colour_and_its_soft_edge_fades_from_it() {
    let strip = [
        ColorVertex::new(0.0, 0.0, RED),
        ColorVertex::new(100.0, 0.0, BLUE),
        ColorVertex::new(100.0, 10.0, BLUE),
        ColorVertex::new(0.0, 10.0, RED),
    ];
    let mut t = Vec::new();
    fill_convex_polygon(&strip, 1.0, &mut t);
    for v in &t {
        let expected = if v.x < 50.0 { RED } else { BLUE };
        assert_eq!(
            v.color,
            Color {
                a: v.color.a,
                ..expected
            }
        );
        assert!(v.color.a == 0.0 || v.color.a == 1.0);
    }
    assert!(t.contains(&ColorVertex::new(0.5, 0.5, RED)));
    assert!(t.contains(&ColorVertex::new(100.5, -0.5, transparent(BLUE))));
}

#[test]
fn a_polygon_that_is_not_convex_is_told_apart_from_one_with_no_area() {
    use std::f32::consts::PI;
    let mut t = Vec::new();
    let mut outcome = |points: &[(f32, f32)]| fill_convex_polygon(&white(points), 1.0, &mut t);
    let star: Vec<(f32, f32)> = (0..5)
        .map(|i| {
            let angle = i as f32 * 4.0 * PI / 5.0;
            (50.0 + 20.0 * angle.cos(), 50.0 + 20.0 * angle.sin())
        })
        .collect();
    let too_many: Vec<(f32, f32)> = (0..65)
        .map(|i| {
            let angle = i as f32 * 2.0 * PI / 65.0;
            (50.0 * angle.cos(), 50.0 * angle.sin())
        })
        .collect();
    assert_eq!(
        outcome(&[
            (0.0, 0.0),
            (20.0, 0.0),
            (10.0, 5.0),
            (20.0, 20.0),
            (0.0, 20.0)
        ]),
        ShapeOutcome::NotConvex
    );
    assert_eq!(outcome(&star), ShapeOutcome::NotConvex);
    assert_eq!(
        outcome(&[(0.0, 0.0), (20.0, 0.0), (10.0, 0.0), (10.0, 10.0)]),
        ShapeOutcome::NotConvex
    );
    assert_eq!(
        outcome(&[
            (0.0, 0.0),
            (5.0, 0.0),
            (5.0, 3.0),
            (5.0, 0.0),
            (10.0, 0.0),
            (10.0, 10.0),
            (0.0, 10.0),
        ]),
        ShapeOutcome::NotConvex
    );
    assert_eq!(
        outcome(&[(0.0, 0.0), (10.0, 0.0), (20.0, 0.0)]),
        ShapeOutcome::Nothing
    );
    assert_eq!(
        outcome(&[(0.0, 0.0), (10.0, 10.0)]),
        ShapeOutcome::TooFewPoints
    );
    assert_eq!(outcome(&too_many), ShapeOutcome::TooManyPoints);
    assert!(t.is_empty());
}

#[test]
fn repeated_points_are_merged() {
    let t = polygon(&[
        (0.0, 0.0),
        (0.0, 0.0),
        (20.0, 0.0),
        (20.0, 20.0),
        (0.0, 0.0),
    ]);
    assert_eq!(t.len(), 21);
}

#[test]
fn curves_are_cut_finer_the_larger_they_are_on_screen() {
    assert_eq!(arc_segments(0.0, TAU, 1.0), 0);
    assert_eq!(arc_segments(0.05, TAU, 1.0), 1);
    assert_eq!(arc_segments(20.5, TAU, 1.0), 32);
    assert_eq!(arc_segments(40.5, TAU, 1.0), 45);
    // Two device pixels to a unit cut as finely as twice the radius.
    assert_eq!(arc_segments(41.0, TAU, 1.0), arc_segments(20.5, TAU, 0.5));
}

#[test]
fn huge_shapes_are_cut_into_a_bounded_number_of_pieces() {
    let mut t = Vec::new();
    fill_rounded_rect(
        (0.0, 0.0, 20000.0, 20000.0),
        CornerRadii::all(10000.0),
        WHITE,
        1.0,
        &mut t,
    );
    // Four corners of 32 chords: 132 points a ring.
    assert_eq!(t.len(), 1182);

    t.clear();
    fill_ellipse((0.0, 0.0, 100_000.0, 100_000.0), WHITE, 1.0, &mut t);
    // 256 points a ring.
    assert_eq!(t.len(), 2298);
}

#[test]
fn shapes_with_no_area_or_no_thickness_write_nothing() {
    let mut t = Vec::new();
    let square = CornerRadii::default();
    fill_ellipse((0.0, 0.0, 0.0, 10.0), WHITE, 1.0, &mut t);
    fill_rounded_rect((0.0, 0.0, 10.0, 0.0), square, WHITE, 1.0, &mut t);
    stroke_ellipse((0.0, 0.0, 10.0, 10.0), WHITE, 0.0, 1.0, &mut t);
    stroke_rounded_rect((0.0, 0.0, 10.0, 10.0), square, WHITE, 0.0, 1.0, &mut t);
    fill_ellipse((0.0, 0.0, f32::NAN, 10.0), WHITE, 1.0, &mut t);
    shadow_bands((0.0, 0.0, 10.0, 10.0), square, (0.0, 6), WHITE, 1.0, &mut t);
    shadow_core((0.0, 0.0, 10.0, 10.0), square, (4.0, 0), WHITE, 1.0, &mut t);
    assert!(t.is_empty());
}

#[test]
fn shapes_narrower_than_their_soft_edge_still_cover_only_their_own_area() {
    let strip = polygon(&[(0.0, 0.0), (100.0, 0.0), (100.0, 0.4), (0.0, 0.4)]);
    assert_covers(40.0, &strip, 0.02);
    assert!(alphas_in_range(&strip));

    // A diagonal quad 0.4 across: corners offset along a 45 degree line.
    let d = 0.4 / 2.0_f32.sqrt();
    let diagonal = polygon(&[(0.0, 0.0), (100.0, 100.0), (100.0 + d, 100.0 - d), (d, -d)]);
    assert_covers(0.4 * 100.0 * 2.0_f64.sqrt(), &diagonal, 0.02);
    assert!(alphas_in_range(&diagonal));

    let mut rect = Vec::new();
    fill_rounded_rect(
        (0.0, 0.0, 100.0, 0.4),
        CornerRadii::default(),
        WHITE,
        1.0,
        &mut rect,
    );
    assert_covers(40.0, &rect, 0.02);
    assert!(alphas_in_range(&rect));

    let mut dot = Vec::new();
    fill_ellipse((0.0, 0.0, 0.4, 0.4), WHITE, 1.0, &mut dot);
    assert_covers(PI64 * 0.2 * 0.2, &dot, 0.02);
    assert!(alphas_in_range(&dot));
}

#[test]
fn an_ellipse_that_is_not_a_circle_covers_its_area() {
    let mut t = Vec::new();
    fill_ellipse((0.0, 0.0, 80.0, 20.0), WHITE, 1.0, &mut t);
    assert_covers(PI64 * 40.0 * 10.0, &t, 0.01);
}

/// An ellipse's perimeter, to well within these tests' tolerances
/// (Ramanujan's second formula).
fn perimeter(a: f64, b: f64) -> f64 {
    let h = ((a - b) / (a + b)).powi(2);
    PI64 * (a + b) * (1.0 + 3.0 * h / (10.0 + (4.0 - 3.0 * h).sqrt()))
}

/// How far `point` is from the outline of an ellipse with radii (`a`, `b`)
/// about the origin, by walking the outline.
fn from_outline(point: (f64, f64), a: f64, b: f64) -> f64 {
    (0..1440)
        .map(|i| {
            let t = f64::from(i) * std::f64::consts::TAU / 1440.0;
            let (dx, dy) = (point.0 - a * t.cos(), point.1 - b * t.sin());
            (dx * dx + dy * dy).sqrt()
        })
        .fold(f64::MAX, f64::min)
}

#[test]
fn a_stroke_on_a_stretched_ellipse_is_as_thick_all_the_way_round() {
    // A band of even width has the area of its width times the perimeter.
    let mut t = Vec::new();
    stroke_ellipse((0.0, 0.0, 80.0, 20.0), WHITE, 2.0, 1.0, &mut t);
    assert_covers(2.0 * perimeter(40.0, 10.0), &t, 0.005);

    // Each solid corner stays within half the width of the outline.
    let mut t = Vec::new();
    stroke_ellipse((-40.0, -10.0, 80.0, 20.0), WHITE, 6.0, 1.0, &mut t);
    for v in t.iter().filter(|v| v.color.a == 1.0) {
        let far = from_outline((f64::from(v.x), f64::from(v.y)), 40.0, 10.0);
        assert!(far <= 2.5 + 0.02, "{v:?} is {far} from the outline");
    }
}

#[test]
fn a_thick_stroke_on_a_stretched_ellipse_does_not_fold_inside() {
    // Half the width is more than the outline's tightest radius (2.5), so a
    // plain inner offset would cross itself near the ends of the long axis.
    let half = 4.0;
    let mut t = Vec::new();
    stroke_ellipse((-40.0, -10.0, 80.0, 20.0), WHITE, 2.0 * half, 1.0, &mut t);
    // What should be covered, cell by cell over one quarter of it.
    let step = 0.5;
    let mut expected = 0.0;
    for ix in 0..100 {
        for iy in 0..40 {
            let x = (f64::from(ix) + 0.5) * step;
            let y = (f64::from(iy) + 0.5) * step;
            if from_outline((x, y), 40.0, 10.0) <= f64::from(half) {
                expected += 4.0 * step * step;
            }
        }
    }
    assert_covers(expected, &t, 0.02);
    assert!(alphas_in_range(&t));
}

#[test]
fn a_stroke_thicker_than_a_stretched_ellipse_fills_it() {
    let half = 15.0_f64;
    let mut t = Vec::new();
    stroke_ellipse((0.0, 0.0, 80.0, 20.0), WHITE, 30.0, 1.0, &mut t);
    // The outer parallel curve encloses the ellipse, a band and a full circle.
    let outer = PI64 * 40.0 * 10.0 + perimeter(40.0, 10.0) * half + PI64 * half * half;
    assert_covers(outer, &t, 0.01);
}

#[test]
fn a_wide_stroke_stays_smooth_round_the_tight_ends_of_an_ellipse() {
    // At the ends of a thin ellipse the outline turns fast, and a wide
    // stroke's outer edge carries that turn a long way out.
    let (a, b, half) = (40.0, 2.0, 20.0_f64);
    let curve = EllipseCurve::new((0.0, 0.0), (40.0, 2.0), 20.5, 1.0);
    let ring = curve.ring(20.0);
    for i in 0..ring.len() {
        let (p, q) = (ring[i], ring[(i + 1) % ring.len()]);
        let middle = (f64::from(p.0 + q.0) * 0.5, f64::from(p.1 + q.1) * 0.5);
        let far = from_outline(middle, a, b);
        assert!(far >= half - 0.25, "a chord strays to {far}");
    }
}

#[test]
fn a_thin_ellipse_covers_its_area_with_its_ends_fading() {
    // The soft edge is deeper than the tightest radius at its ends.
    let mut t = Vec::new();
    fill_ellipse((0.0, 0.0, 200.0, 4.0), WHITE, 1.0, &mut t);
    assert_covers(PI64 * 100.0 * 2.0, &t, 0.02);
    assert!(alphas_in_range(&t));
    assert_eq!(max_alpha(&t), 1.0);
}

#[test]
fn an_ellipse_keeps_its_symmetry_and_a_circle_its_even_cuts() {
    let curve = EllipseCurve::new((0.0, 0.0), (40.0, 10.0), 3.5, 1.0);
    let ring = curve.ring(3.0);
    assert_eq!(ring.len() % 4, 0);
    for &(x, y) in &ring {
        let mirrored = |mx: f32, my: f32| {
            ring.iter()
                .any(|p| (p.0 - mx).abs() < 1e-3 && (p.1 - my).abs() < 1e-3)
        };
        assert!(mirrored(-x, y) && mirrored(x, -y), "{x},{y}");
    }
    let circle = EllipseCurve::new((0.0, 0.0), (20.0, 20.0), 0.5, 1.0);
    assert_eq!(circle.ring(0.5).len(), 32);
}

#[test]
fn a_sliver_triangle_does_not_fold() {
    let t = polygon(&[(0.0, 0.0), (100.0, 0.0), (50.0, 0.3)]);
    assert_covers(15.0, &t, 0.02);
    assert!(alphas_in_range(&t));
}

#[test]
fn a_tiny_edge_on_a_big_polygon_keeps_it_solid() {
    let chamfer = polygon(&[
        (0.0, 0.0),
        (100.0, 0.0),
        (100.0, 99.8),
        (99.8, 100.0),
        (0.0, 100.0),
    ]);
    assert_covers(10000.0 - 0.02, &chamfer, 0.01);
    assert_eq!(max_alpha(&chamfer), 1.0);

    let near = polygon(&[
        (0.0, 0.0),
        (100.0, 0.0),
        (100.0, 100.0),
        (99.95, 100.0),
        (0.0, 100.0),
    ]);
    assert_covers(10000.0, &near, 0.01);
    assert_eq!(max_alpha(&near), 1.0);
}

#[test]
fn a_one_pixel_strip_polygon_covers_its_area() {
    let t = polygon(&[(0.0, 0.0), (100.0, 0.0), (100.0, 1.0), (0.0, 1.0)]);
    assert_covers(100.0, &t, 0.01);
}

#[test]
fn a_strip_polygon_thinner_than_a_pixel_is_a_line_not_a_spindle() {
    let t = polygon(&[(0.0, 0.0), (100.0, 0.0), (100.0, 0.4), (0.0, 0.4)]);
    let seen: Vec<f32> = t.iter().map(|v| v.color.a).filter(|a| *a > 0.0).collect();
    assert!(!seen.is_empty());
    let low = seen.iter().copied().fold(f32::MAX, f32::min);
    let high = seen.iter().copied().fold(f32::MIN, f32::max);
    assert!(high - low <= 1e-4);
}

#[test]
fn a_shape_exactly_one_pixel_across_covers_its_area() {
    let mut dot = Vec::new();
    fill_ellipse((0.0, 0.0, 1.0, 1.0), WHITE, 1.0, &mut dot);
    assert_covers(PI64 / 4.0, &dot, 0.02);

    let mut square = Vec::new();
    fill_rounded_rect(
        (0.0, 0.0, 1.0, 1.0),
        CornerRadii::default(),
        WHITE,
        1.0,
        &mut square,
    );
    assert_covers(1.0, &square, 0.02);
}

#[test]
fn sharp_corners_and_thin_shapes_cover_their_area() {
    let triangle = polygon(&[(0.0, 0.0), (100.0, 0.0), (50.0, 5.0)]);
    assert_covers(250.0, &triangle, 0.01);

    let needle = polygon(&[(0.0, 0.0), (32.0, -1.5), (64.0, 0.0), (32.0, 1.5)]);
    assert_covers(96.0, &needle, 0.01);

    let nick = polygon(&[
        (0.0, 0.0),
        (100.0, 0.0),
        (100.0, 0.4),
        (99.95, 0.4),
        (0.0, 0.4),
    ]);
    assert_covers(40.0, &nick, 0.02);
    assert!(max_alpha(&nick) < 1.0);

    let slanted = [
        (25.055_227, 0.763_589_86),
        (96.050_29, 0.803_835_03),
        (93.911_33, -0.019_011_175),
        (21.913_465, -0.213_656_74),
    ];
    assert_covers(shoelace(&slanted), &polygon(&slanted), 0.02);

    let far = polygon(&[
        (3000.0, 2000.0),
        (3000.3, 2000.0),
        (3000.3, 2000.3),
        (3000.0, 2000.3),
    ]);
    assert_covers(0.09, &far, 0.02);
}

#[test]
fn nearly_straight_corners_still_pull_the_soft_edge_in() {
    let polygons: [&[(f32, f32)]; 3] = [
        &[
            (13.501_612, 1.472_670_9),
            (-14.647_315, -0.419_214_16),
            (-3.411_929_1, -3.565_782_3),
            (-3.402_439_8, -3.565_115_5),
            (10.416_538, -2.593_997),
        ],
        &[
            (4.069_453_2, 22.838_041),
            (3.562_912_2, 48.943_974),
            (2.215_275_8, 78.548_645),
            (-2.944_639_4, 65.922_005),
            (-2.944_656_1, 65.895_14),
            (-3.025_542_5, -64.121_704),
        ],
        &[
            (0.808_685_54, 0.015_361_176),
            (-0.118_674_494, 0.030_672_016),
            (-0.920_632_24, 0.004_768_012_6),
            (-0.438_404_35, -0.027_287_133),
            (-0.437_772_33, -0.027_265_519),
        ],
    ];
    for points in polygons {
        assert_covers(shoelace(points), &polygon(points), 0.02);
    }
}

#[test]
fn triangle_like_polygons_with_a_tiny_extra_edge_stay_solid() {
    let polygons: [&[(f32, f32)]; 3] = [
        &[(0.0, 0.0), (20.0, 0.0), (10.0, 20.0), (9.99, 20.0)],
        &[(0.0, 0.0), (10.0, 5.0), (10.0, 5.001), (0.0, 10.0)],
        &[(0.0, 0.0), (10.0, 0.0), (5.15, 20.0), (4.85, 20.0)],
    ];
    for points in polygons {
        let t = polygon(points);
        assert_covers(shoelace(points), &t, 0.01);
        assert_eq!(max_alpha(&t), 1.0);
    }
}

#[test]
fn a_shadow_falls_off_without_a_step_from_its_rectangle_to_nothing() {
    let layer = Color::rgba(0.0, 0.0, 0.0, 0.1);
    let rect = (20.0, 20.0, 40.0, 20.0);
    let mut bands = Vec::new();
    shadow_bands(
        rect,
        CornerRadii::default(),
        (12.0, 3),
        layer,
        1.0,
        &mut bands,
    );
    // A ring's darkness by how far it has grown: as dark as the fills stacked
    // there, each band blending from one ring to the next.
    let stacked_at = |n: i32| 1.0 - 0.9_f32.powi(n);
    let right_side = bands
        .iter()
        .filter(|v| v.x >= 60.0 && v.y >= 20.0 && v.y <= 40.0);
    for v in right_side {
        let expected = match (v.x - 60.0).round() as i32 {
            0 => stacked_at(3),
            4 => stacked_at(2),
            8 => stacked_at(1),
            12 => 0.0,
            other => panic!("a ring at {other}"),
        };
        assert!((v.color.a - expected).abs() < 1e-6, "{v:?}");
    }
    // Every band runs from one darkness to another: none is flat.
    for quad in bands.chunks_exact(6) {
        let lowest = quad.iter().map(|v| v.color.a).fold(f32::MAX, f32::min);
        assert!(max_alpha(quad) > lowest, "{quad:?}");
    }
}

#[test]
fn the_shadow_of_a_square_corner_curves_as_it_spreads() {
    let layer = Color::rgba(0.0, 0.0, 0.0, 0.1);
    let mut bands = Vec::new();
    shadow_bands(
        (20.0, 20.0, 40.0, 20.0),
        CornerRadii::default(),
        (12.0, 3),
        layer,
        1.0,
        &mut bands,
    );
    // Its rim stays a spread from the corner, not out at the square's corner.
    for v in bands.iter().filter(|v| v.x >= 60.0 && v.y <= 20.0) {
        let from_corner = length((v.x - 60.0, v.y - 20.0));
        assert!(from_corner <= 12.0 + 0.1, "{v:?}");
    }
    assert!(bands.iter().any(|v| v.x > 60.0 && v.x < 72.0 && v.y < 20.0));
}

#[test]
fn a_shadow_reaches_as_far_as_its_spread_and_meets_its_core() {
    let layer = Color::rgba(0.0, 0.0, 0.0, 0.1);
    let rect = (20.0, 20.0, 40.0, 20.0);
    let radii = CornerRadii::all(4.0);
    let mut bands = Vec::new();
    shadow_bands(rect, radii, (12.0, 3), layer, 1.0, &mut bands);
    let mut core = Vec::new();
    shadow_core(rect, radii, (12.0, 3), layer, 1.0, &mut core);

    let dark = 1.0 - 0.9_f32.powi(3);
    assert!((max_alpha(&bands) - dark).abs() < 1e-6);
    assert!(core.iter().all(|v| (v.color.a - dark).abs() < 1e-6));
    assert!(bands.iter().any(|v| v.color.a == 0.0));
    for v in &bands {
        assert!(v.x >= 20.0 - 12.0 - 1e-3 && v.x <= 60.0 + 12.0 + 1e-3);
        assert!(v.y >= 20.0 - 12.0 - 1e-3 && v.y <= 40.0 + 12.0 + 1e-3);
    }
    // The core's edge is the first band's inner edge, exactly.
    let points =
        |t: &[ColorVertex]| -> HashSet<(u32, u32)> { t.iter().map(|v| bits(*v)).collect() };
    assert!(points(&core).is_subset(&points(&bands)));
}

const NOT_NUMBERS: [f32; 3] = [f32::NAN, f32::INFINITY, f32::NEG_INFINITY];

#[test]
fn a_position_or_size_that_is_not_a_number_writes_nothing() {
    let square = CornerRadii::default();
    for bad in NOT_NUMBERS {
        for rect in [
            (bad, 0.0, 10.0, 10.0),
            (0.0, bad, 10.0, 10.0),
            (0.0, 0.0, bad, 10.0),
            (0.0, 0.0, 10.0, bad),
        ] {
            let mut t = Vec::new();
            fill_rounded_rect(rect, square, WHITE, 1.0, &mut t);
            stroke_rounded_rect(rect, square, WHITE, 2.0, 1.0, &mut t);
            fill_ellipse(rect, WHITE, 1.0, &mut t);
            stroke_ellipse(rect, WHITE, 2.0, 1.0, &mut t);
            shadow_bands(rect, square, (4.0, 6), WHITE, 1.0, &mut t);
            shadow_core(rect, square, (4.0, 6), WHITE, 1.0, &mut t);
            assert!(t.is_empty(), "{rect:?}");
        }
        let rect = (0.0, 0.0, 10.0, 10.0);
        let mut t = Vec::new();
        stroke_rounded_rect(rect, square, WHITE, bad, 1.0, &mut t);
        stroke_ellipse(rect, WHITE, bad, 1.0, &mut t);
        shadow_bands(rect, square, (bad, 6), WHITE, 1.0, &mut t);
        shadow_core(rect, square, (bad, 6), WHITE, 1.0, &mut t);
        assert!(t.is_empty(), "thickness or spread {bad}");
    }
}

#[test]
fn a_radius_that_is_not_a_number_or_is_negative_is_a_square_corner() {
    assert_eq!(
        clamp_radii(
            CornerRadii::new(f32::NAN, -5.0, f32::INFINITY, 10.0),
            60.0,
            30.0
        ),
        CornerRadii::new(0.0, 0.0, 0.0, 10.0)
    );
    for bad in NOT_NUMBERS {
        // Thin enough for the alpha to be worked out from the corners.
        let mut t = Vec::new();
        fill_rounded_rect(
            (0.0, 0.0, 100.0, 0.5),
            CornerRadii::all(bad),
            WHITE,
            1.0,
            &mut t,
        );
        assert_covers(50.0, &t, 0.02);
    }
}

#[test]
fn a_polygon_with_a_point_that_is_not_a_number_is_told_apart() {
    for bad in NOT_NUMBERS {
        let good = [(0.0, 0.0), (20.0, 0.0), (20.0, 20.0), (0.0, 20.0)];
        for i in 0..good.len() {
            for point in [(bad, good[i].1), (good[i].0, bad)] {
                let mut points = good;
                points[i] = point;
                let mut t = Vec::new();
                assert_eq!(
                    fill_convex_polygon(&white(&points), 1.0, &mut t),
                    ShapeOutcome::NotFinite
                );
                assert!(t.is_empty());
            }
        }
        let mut corners = white(&good);
        corners[1].color.a = bad;
        let mut t = Vec::new();
        assert_eq!(
            fill_convex_polygon(&corners, 1.0, &mut t),
            ShapeOutcome::NotFinite
        );
    }
}

#[test]
fn a_polygon_too_large_to_measure_is_not_drawn() {
    let huge = 1e30;
    let mut t = Vec::new();
    // Every turn overflows, so none of them turns either way.
    assert_eq!(
        fill_convex_polygon(
            &white(&[(-huge, -huge), (huge, -huge), (huge, huge), (-huge, huge)]),
            1.0,
            &mut t,
        ),
        ShapeOutcome::Nothing
    );
    // One small corner does turn, and the rest cannot be added up.
    assert_eq!(
        fill_convex_polygon(
            &white(&[
                (-1.0, 0.0),
                (0.0, -1.0),
                (1.0, 0.0),
                (huge, huge),
                (0.0, 2.0 * huge),
                (-huge, huge),
            ]),
            1.0,
            &mut t,
        ),
        ShapeOutcome::NotConvex
    );
    assert!(t.is_empty());
}
