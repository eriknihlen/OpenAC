//! The world pick: a ray from the eye through the pointer, tested against
//! the parts the last world frame drew. Each part is tested in its own
//! frame (the ray carried in by the inverse of the part's placement, so a
//! scaled part keeps world distances): first its drawing sphere, then its
//! polygons in the order the model file lists them, stopping at the first
//! polygon hit. A polygon hit anywhere beats every sphere hit; the nearest
//! sphere answers only when no polygon was hit.

use std::sync::Arc;

use glam::{Mat4, Vec3, Vec4};

/// Below this a ray's squared length, or a polygon's facing, is too small
/// to test.
const RAY_EPSILON: f64 = 0.0002;

/// What a part is picked against: its drawing sphere and its polygons, in
/// its own frame.
#[derive(Debug, Clone, PartialEq)]
pub struct SelectionMesh {
    /// The drawing sphere's centre.
    pub sphere_center: Vec3,
    /// Its radius (a part without one is never picked).
    pub sphere_radius: f32,
    /// The drawn polygons, in file order.
    pub polygons: Vec<SelectionPolygon>,
}

/// One drawn polygon: its corners in winding order, and whether only its
/// front face counts.
#[derive(Debug, Clone, PartialEq)]
pub struct SelectionPolygon {
    /// Corners, in the order the model file winds them.
    pub vertices: Vec<Vec3>,
    /// Only the front face is hit.
    pub single_sided: bool,
}

/// A part the world frame drew.
#[derive(Debug, Clone)]
pub struct SelectionPart {
    /// The object's server id.
    pub guid: u32,
    /// Which incarnation of the object (its local id).
    pub local_id: u32,
    /// Which part.
    pub part_index: u32,
    /// Part to drawing frame (scale included).
    pub local_to_world: Mat4,
    /// What it is picked against.
    pub mesh: Arc<SelectionMesh>,
}

/// What a pick found.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SelectionHit {
    /// The object's server id.
    pub guid: u32,
    /// Its incarnation.
    pub local_id: u32,
    /// The part hit.
    pub part_index: u32,
    /// Distance along the ray, metres.
    pub distance: f64,
    /// A polygon was hit (else only the drawing sphere).
    pub polygon_hit: bool,
}

/// The nearest part the ray from `origin` along `direction` hits, polygon
/// hits first; parts of no object (guid 0) and of `skip_guid` are left
/// out.
#[must_use]
pub fn pick<'a>(
    origin: Vec3,
    direction: Vec3,
    parts: impl IntoIterator<Item = &'a SelectionPart>,
    skip_guid: u32,
) -> Option<SelectionHit> {
    if direction.length_squared() < 1e-10 {
        return None;
    }
    let mut closest_sphere: Option<SelectionHit> = None;
    let mut closest_polygon: Option<SelectionHit> = None;
    for part in parts {
        if part.guid == 0 || part.guid == skip_guid {
            continue;
        }
        if part.mesh.sphere_radius <= 0.0 || part.local_to_world.determinant() == 0.0 {
            continue;
        }
        let world_to_local = part.local_to_world.inverse();
        let local_origin = world_to_local.transform_point3(origin);
        let local_direction = world_to_local.transform_vector3(direction);
        let Some(sphere_t) = intersect_sphere(
            local_origin,
            local_direction,
            part.mesh.sphere_center,
            part.mesh.sphere_radius,
        ) else {
            continue;
        };
        if closest_polygon.is_some_and(|winner| sphere_t > winner.distance) {
            continue;
        }
        let hit = |distance, polygon_hit| SelectionHit {
            guid: part.guid,
            local_id: part.local_id,
            part_index: part.part_index,
            distance,
            polygon_hit,
        };
        if closest_sphere.is_none_or(|s| sphere_t < s.distance) {
            closest_sphere = Some(hit(sphere_t, false));
        }
        for polygon in &part.mesh.polygons {
            let Some(polygon_t) = intersect_polygon(local_origin, local_direction, polygon) else {
                continue;
            };
            if closest_polygon.is_none_or(|p| polygon_t < p.distance) {
                closest_polygon = Some(hit(polygon_t, true));
            }
            break;
        }
    }
    closest_polygon.or(closest_sphere)
}

/// The distance along the ray to the sphere, or `None` when it misses or
/// the ray begins inside it (as the game's test).
#[must_use]
pub fn intersect_sphere(origin: Vec3, direction: Vec3, center: Vec3, radius: f32) -> Option<f64> {
    let offset = origin - center;
    let c = f64::from(offset.dot(offset)) - f64::from(radius) * f64::from(radius);
    if c <= 0.0 {
        return None;
    }
    let a = f64::from(direction.dot(direction));
    if a < RAY_EPSILON {
        return None;
    }
    let b = -f64::from(offset.dot(direction));
    let discriminant = b * b - c * a;
    if discriminant < 0.0 {
        return None;
    }
    let root = discriminant.sqrt();
    Some(if b > root {
        (b - root) / a
    } else {
        (b + root) / a
    })
}

/// The distance along the ray to the polygon, or `None` when it misses, is
/// edge-on, lies behind, or (single-sided) is seen from the back. The
/// inside test walks the edges, so a concave notch is outside.
#[must_use]
pub fn intersect_polygon(origin: Vec3, direction: Vec3, polygon: &SelectionPolygon) -> Option<f64> {
    if polygon.vertices.len() < 3 {
        return None;
    }
    let (normal, plane_d) = plane_of(&polygon.vertices)?;
    let denominator = f64::from(direction.dot(normal));
    if polygon.single_sided && denominator > 0.0 {
        return None;
    }
    if denominator.abs() < RAY_EPSILON {
        return None;
    }
    let distance = -(f64::from(origin.dot(normal)) + f64::from(plane_d)) / denominator;
    if distance < 0.0 {
        return None;
    }
    let point = origin + direction * distance as f32;
    point_in_polygon(point, &polygon.vertices, normal).then_some(distance)
}

/// The polygon's plane: the normal summed over its fan, and the distance
/// averaged over every corner.
fn plane_of(vertices: &[Vec3]) -> Option<(Vec3, f32)> {
    let first = vertices[0];
    let mut sum = Vec3::ZERO;
    for pair in vertices[1..].windows(2) {
        sum += (pair[0] - first).cross(pair[1] - first);
    }
    if sum.length_squared() <= 1e-12 {
        return None;
    }
    let normal = sum.normalize();
    let average: f64 = vertices
        .iter()
        .map(|v| f64::from(normal.dot(*v)))
        .sum::<f64>()
        / vertices.len() as f64;
    Some((normal, -average as f32))
}

fn point_in_polygon(point: Vec3, vertices: &[Vec3], normal: Vec3) -> bool {
    let mut previous = vertices[vertices.len() - 1];
    for &current in vertices {
        let inward = normal.cross(current - previous);
        if (point - previous).dot(inward) < 0.0 {
            return false;
        }
        previous = current;
    }
    true
}

/// The ray from the camera's viewpoint through the pixel (`mouse_x`,
/// `mouse_y`) of a `width` × `height` viewport, as origin and unit
/// direction (zero for a degenerate camera). The view and projection are
/// the renderer's (right-handed, depth 0..1).
#[must_use]
pub fn build_ray(
    mouse_x: f32,
    mouse_y: f32,
    width: f32,
    height: f32,
    view: Mat4,
    projection: Mat4,
) -> (Vec3, Vec3) {
    let view_projection = projection * view;
    if view_projection.determinant() == 0.0 || view.determinant() == 0.0 {
        return (Vec3::ZERO, Vec3::ZERO);
    }
    let eye = view.inverse().transform_point3(Vec3::ZERO);
    ray_through(
        mouse_x,
        mouse_y,
        width,
        height,
        view_projection.inverse(),
        eye,
    )
}

/// [`build_ray`] from the inverse of the view-projection and the eye.
#[must_use]
pub fn ray_through(
    mouse_x: f32,
    mouse_y: f32,
    width: f32,
    height: f32,
    inverse_view_projection: Mat4,
    eye: Vec3,
) -> (Vec3, Vec3) {
    let ndc_x = (2.0 * mouse_x) / width - 1.0;
    let ndc_y = 1.0 - (2.0 * mouse_y) / height;
    let near = inverse_view_projection * Vec4::new(ndc_x, ndc_y, 0.0, 1.0);
    let far = inverse_view_projection * Vec4::new(ndc_x, ndc_y, 1.0, 1.0);
    if near.w == 0.0 || far.w == 0.0 {
        return (Vec3::ZERO, Vec3::ZERO);
    }
    let direction = far.truncate() / far.w - near.truncate() / near.w;
    if direction.length_squared() < 1e-10 {
        return (Vec3::ZERO, Vec3::ZERO);
    }
    (eye, direction.normalize())
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Core.Tests/Selection/RetailWorldPickerTests.cs`.

    use super::*;

    fn square_at_local_z(z: f32, single_sided: bool) -> SelectionPolygon {
        SelectionPolygon {
            vertices: vec![
                Vec3::new(-1.0, -1.0, z),
                Vec3::new(1.0, -1.0, z),
                Vec3::new(1.0, 1.0, z),
                Vec3::new(-1.0, 1.0, z),
            ],
            single_sided,
        }
    }

    fn part(
        guid: u32,
        z: f32,
        polygons: Vec<SelectionPolygon>,
        sphere_center: Vec3,
        sphere_radius: f32,
    ) -> SelectionPart {
        SelectionPart {
            guid,
            local_id: guid + 1000,
            part_index: 0,
            local_to_world: Mat4::from_translation(Vec3::new(0.0, 0.0, z)),
            mesh: Arc::new(SelectionMesh {
                sphere_center,
                sphere_radius,
                polygons,
            }),
        }
    }

    fn simple(guid: u32, z: f32, polygons: Vec<SelectionPolygon>) -> SelectionPart {
        part(guid, z, polygons, Vec3::ZERO, 2.0)
    }

    #[test]
    fn the_ray_starts_at_the_camera_viewpoint_rather_than_the_near_plane() {
        let eye = Vec3::new(10.0, 20.0, 30.0);
        let view = glam::camera::rh::view::look_at_mat4(eye, eye - Vec3::Z, Vec3::Y);
        let projection = glam::camera::rh::proj::directx::perspective(
            std::f32::consts::PI / 3.0,
            4.0 / 3.0,
            0.1,
            1000.0,
        );
        let (origin, direction) = build_ray(400.0, 300.0, 800.0, 600.0, view, projection);
        assert!(eye.distance(origin) < 0.0001);
        assert!(Vec3::NEG_Z.distance(direction) < 0.0001);
    }

    #[test]
    fn a_polygon_hit_beats_a_nearer_sphere_fallback() {
        let sphere_only = simple(1, -3.0, vec![]);
        let polygon = simple(2, -8.0, vec![square_at_local_z(0.0, false)]);
        let hit = pick(Vec3::ZERO, Vec3::NEG_Z, [&sphere_only, &polygon], 0).unwrap();
        assert_eq!(hit.guid, 2);
        assert_eq!(hit.local_id, 1002);
        assert!(hit.polygon_hit);
    }

    #[test]
    fn the_sphere_answers_only_when_no_polygon_hits() {
        let p = simple(0xABCD, -5.0, vec![]);
        let hit = pick(Vec3::ZERO, Vec3::NEG_Z, [&p], 0).unwrap();
        assert_eq!(hit.guid, 0xABCD);
        assert!(!hit.polygon_hit);
        assert!((hit.distance - 3.0).abs() < 1e-4);
    }

    #[test]
    fn each_part_stops_at_its_first_hit_polygon_in_file_order() {
        let file_ordered = part(
            10,
            0.0,
            vec![
                square_at_local_z(-10.0, false),
                square_at_local_z(-5.0, false),
            ],
            Vec3::new(0.0, 0.0, -7.5),
            5.0,
        );
        let competitor = simple(20, -7.0, vec![square_at_local_z(0.0, false)]);
        let hit = pick(Vec3::ZERO, Vec3::NEG_Z, [&file_ordered, &competitor], 0).unwrap();
        assert_eq!(hit.guid, 20);
    }

    #[test]
    fn a_single_sided_polygon_rejects_its_back_face() {
        let front = SelectionPart {
            guid: 1,
            local_id: 1001,
            part_index: 0,
            local_to_world: Mat4::IDENTITY,
            mesh: Arc::new(SelectionMesh {
                sphere_center: Vec3::new(0.0, 0.0, -5.0),
                sphere_radius: 2.0,
                polygons: vec![square_at_local_z(-5.0, true)],
            }),
        };
        let back = pick(Vec3::new(0.0, 0.0, -10.0), Vec3::Z, [&front], 0).unwrap();
        assert!(!back.polygon_hit);
    }

    #[test]
    fn a_scaled_part_keeps_the_world_distance() {
        let transform =
            Mat4::from_translation(Vec3::new(0.0, 0.0, -10.0)) * Mat4::from_scale(Vec3::splat(2.0));
        let p = SelectionPart {
            guid: 3,
            local_id: 1003,
            part_index: 0,
            local_to_world: transform,
            mesh: Arc::new(SelectionMesh {
                sphere_center: Vec3::ZERO,
                sphere_radius: 1.0,
                polygons: vec![square_at_local_z(0.0, false)],
            }),
        };
        let hit = pick(Vec3::ZERO, Vec3::NEG_Z, [&p], 0).unwrap();
        assert!(hit.polygon_hit);
        assert!((hit.distance - 10.0).abs() < 1e-4);
    }

    #[test]
    fn the_skipped_object_and_parts_of_no_object_never_compete() {
        let a = simple(0, -2.0, vec![square_at_local_z(0.0, false)]);
        let b = simple(7, -4.0, vec![square_at_local_z(0.0, false)]);
        assert!(pick(Vec3::ZERO, Vec3::NEG_Z, [&a, &b], 7).is_none());
    }

    #[test]
    fn a_ray_that_begins_inside_the_drawing_sphere_misses_it() {
        assert!(intersect_sphere(Vec3::ZERO, Vec3::X, Vec3::ZERO, 2.0).is_none());
    }

    #[test]
    fn the_edge_test_rejects_a_concave_notch() {
        let concave = SelectionPolygon {
            vertices: vec![
                Vec3::new(-2.0, -2.0, -5.0),
                Vec3::new(2.0, -2.0, -5.0),
                Vec3::new(2.0, 2.0, -5.0),
                Vec3::new(0.0, 0.0, -5.0),
                Vec3::new(-2.0, 2.0, -5.0),
            ],
            single_sided: false,
        };
        assert!(intersect_polygon(Vec3::new(0.0, 1.0, 0.0), Vec3::NEG_Z, &concave).is_none());
    }
}
