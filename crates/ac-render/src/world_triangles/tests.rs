use super::*;

const RED: [f32; 4] = [1.0, 0.0, 0.0, 0.5];

fn triangle(a: Vec3, b: Vec3, c: Vec3, color: [f32; 4]) -> PlacedTriangle {
    PlacedTriangle {
        corners: [a, b, c],
        colors: [color; 3],
    }
}

fn draw(
    triangles: &[PlacedTriangle],
    camera: Vec3,
    frustum: Option<&FrustumPlanes>,
) -> Vec<WorldTriangleVertex> {
    let mut out = Vec::new();
    build(triangles, camera, frustum, &mut out);
    out
}

/// A camera at the origin looking east, 60 degrees across.
fn looking_east() -> FrustumPlanes {
    let view = glam::camera::rh::view::look_at_mat4(Vec3::ZERO, Vec3::X, Vec3::Z);
    let projection =
        glam::camera::rh::proj::directx::perspective(60f32.to_radians(), 1.0, 0.1, 1000.0);
    FrustumPlanes::from_view_projection(projection * view)
}

#[test]
fn the_budget_is_one_mebibyte_of_vertices() {
    assert_eq!(size_of::<WorldTriangleVertex>(), 28);
    assert_eq!(VERTEX_BUDGET, 37_449);
}

// Each corner moves a little toward the camera, so a triangle on a surface
// is drawn over it.
#[test]
fn a_triangle_is_drawn_between_its_corners_in_its_colour() {
    let corners = [
        Vec3::new(10.0, 0.0, 0.0),
        Vec3::new(20.0, 0.0, 0.0),
        Vec3::new(10.0, 10.0, 0.0),
    ];
    let camera = Vec3::new(0.0, 0.0, 5.0);
    let out = draw(
        &[triangle(corners[0], corners[1], corners[2], RED)],
        camera,
        None,
    );
    assert_eq!(out.len(), 3);
    for (vertex, corner) in out.iter().zip(corners) {
        let expected = corner + (camera - corner) * DEPTH_NUDGE;
        assert!(Vec3::from(vertex.position).distance(expected) < 1e-4);
        assert_eq!(vertex.color, RED);
    }
}

#[test]
fn corner_colours_are_uploaded_per_vertex() {
    let colors = [
        [1.0, 0.0, 0.0, 1.0],
        [0.0, 1.0, 0.0, 0.5],
        [0.0, 0.0, 1.0, 0.25],
    ];
    let placed = PlacedTriangle {
        corners: [
            Vec3::new(5.0, 0.0, 0.0),
            Vec3::new(6.0, 0.0, 0.0),
            Vec3::new(5.0, 1.0, 0.0),
        ],
        colors,
    };
    let out = draw(&[placed], Vec3::ZERO, None);
    assert_eq!(out.iter().map(|v| v.color).collect::<Vec<_>>(), colors);
}

#[test]
fn a_triangle_with_a_corner_that_is_not_a_number_is_dropped() {
    let out = draw(
        &[
            triangle(
                Vec3::new(5.0, 0.0, 0.0),
                Vec3::new(f32::NAN, 0.0, 0.0),
                Vec3::new(5.0, 1.0, 0.0),
                RED,
            ),
            triangle(
                Vec3::new(5.0, 0.0, 0.0),
                Vec3::new(f32::INFINITY, 0.0, 0.0),
                Vec3::new(5.0, 1.0, 0.0),
                RED,
            ),
            triangle(
                Vec3::new(5.0, 0.0, 0.0),
                Vec3::new(6.0, 0.0, 0.0),
                Vec3::new(5.0, 1.0, 0.0),
                RED,
            ),
        ],
        Vec3::ZERO,
        None,
    );
    assert_eq!(out.len(), 3);
}

#[test]
fn a_triangle_out_of_range_is_not_drawn() {
    let out = draw(
        &[triangle(
            Vec3::new(300.0, 0.0, 0.0),
            Vec3::new(310.0, 0.0, 0.0),
            Vec3::new(300.0, 10.0, 0.0),
            RED,
        )],
        Vec3::ZERO,
        None,
    );
    assert!(out.is_empty());
}

// Every corner is past the range; its long edge passes 50 m away.
#[test]
fn a_large_triangle_reaching_into_range_is_drawn() {
    let out = draw(
        &[triangle(
            Vec3::new(-400.0, 50.0, 0.0),
            Vec3::new(400.0, 50.0, 0.0),
            Vec3::new(0.0, 600.0, 0.0),
            RED,
        )],
        Vec3::ZERO,
        None,
    );
    assert_eq!(out.len(), 3);
}

#[test]
fn a_triangle_out_of_view_is_not_drawn() {
    let frustum = looking_east();
    let out = draw(
        &[
            triangle(
                Vec3::new(50.0, 0.0, 0.0),
                Vec3::new(60.0, 0.0, 0.0),
                Vec3::new(50.0, 10.0, 0.0),
                RED,
            ),
            triangle(
                Vec3::new(-50.0, 0.0, 0.0),
                Vec3::new(-60.0, 0.0, 0.0),
                Vec3::new(-50.0, 10.0, 0.0),
                RED,
            ),
        ],
        Vec3::ZERO,
        Some(&frustum),
    );
    assert_eq!(out.len(), 3);
    assert!(out.iter().all(|v| v.position[0] > 0.0));
}

#[test]
fn when_the_budget_runs_out_the_triangles_nearest_the_camera_are_the_ones_drawn() {
    let capacity = VERTEX_BUDGET / 3;
    let count = capacity + 500;
    // Far triangles first, more than the budget holds...
    let mut triangles: Vec<PlacedTriangle> = (0..count)
        .map(|i| {
            let angle = i as f32 * std::f32::consts::TAU / count as f32;
            let at = Vec3::new(angle.cos() * 200.0, angle.sin() * 200.0, 0.0);
            triangle(at, at + Vec3::X, at + Vec3::Y, RED)
        })
        .collect();
    // ...then one right beside the camera.
    triangles.push(triangle(
        Vec3::new(5.0, 0.0, 0.0),
        Vec3::new(6.0, 0.0, 0.0),
        Vec3::new(5.0, 1.0, 0.0),
        RED,
    ));
    let out = draw(&triangles, Vec3::ZERO, None);
    assert_eq!(out.len(), capacity * 3);
    assert!(
        out.iter()
            .any(|v| (v.position[0] - 5.0).abs() < 0.1 && v.position[1].abs() < 0.1)
    );
}

#[test]
fn the_nearest_point_of_a_triangle_is_on_its_face_an_edge_or_a_corner() {
    let (a, b, c) = (
        Vec3::ZERO,
        Vec3::new(10.0, 0.0, 0.0),
        Vec3::new(0.0, 10.0, 0.0),
    );
    for (p, expected) in [
        // Over a corner.
        (Vec3::new(0.0, 0.0, 5.0), Vec3::ZERO),
        // Beside an edge.
        (Vec3::new(5.0, -3.0, 0.0), Vec3::new(5.0, 0.0, 0.0)),
        // Over the face.
        (Vec3::new(2.0, 2.0, 7.0), Vec3::new(2.0, 2.0, 0.0)),
        // Past the long edge.
        (Vec3::new(20.0, 20.0, 0.0), Vec3::new(5.0, 5.0, 0.0)),
    ] {
        let nearest = closest_point(p, a, b, c);
        assert!(nearest.distance(expected) < 0.001, "{p} -> {nearest}");
    }
}

// A triangle with no area (all corners on one line) is as near as its
// nearest corner.
#[test]
fn a_triangle_with_no_area_is_as_near_as_its_nearest_corner() {
    let nearest = closest_point(
        Vec3::new(0.0, 5.0, 0.0),
        Vec3::ZERO,
        Vec3::new(1.0, 0.0, 0.0),
        Vec3::new(2.0, 0.0, 0.0),
    );
    assert!(nearest.distance(Vec3::ZERO) < 0.001);
}
