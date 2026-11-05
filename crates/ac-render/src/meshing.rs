//! Models to triangles: a GfxObj's polygons become one indexed triangle
//! list per surface, and a Setup becomes its parts with their resting
//! transforms. Pure data in, pure data out; the renderer uploads the result.
//!
//! Matrices here are glam's column-vector convention: `a * b` applies `b`
//! first. "Part then scale" is `scale * part`.

use std::collections::HashMap;

use ac_dat::objects::{AnimationFrame, GfxObj, Polygon, Setup, SidesType, Stippling, SurfaceType};
use ac_dat::types::{Frame, Quaternion, Vector3};
use glam::{Mat4, Quat, Vec2, Vec3};

/// Placement id of the resting pose.
pub const PLACEMENT_RESTING: u32 = 101;
/// Placement id of the default pose.
pub const PLACEMENT_DEFAULT: u32 = 0;

/// One drawable vertex of a model.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MeshVertex {
    /// Model-local position, meters.
    pub position: Vec3,
    /// Unit normal (flipped for a polygon's back side).
    pub normal: Vec3,
    /// Texture coordinate.
    pub uv: Vec2,
}

/// The triangles of one GfxObj that use one surface on one side.
#[derive(Debug, Clone, PartialEq)]
pub struct SubMesh {
    /// Index into the GfxObj's surface list.
    pub surface_index: usize,
    /// The surface's dat id.
    pub surface_id: u32,
    /// True for the back sides of double-sided polygons.
    pub is_negative_side: bool,
    /// The polygons' sides type, which decides the faces the draw culls;
    /// polygons of different sides types never share a sub-mesh.
    pub sides_type: i32,
    /// Vertices, deduplicated per (position, uv, side).
    pub vertices: Vec<MeshVertex>,
    /// Triangle list.
    pub indices: Vec<u32>,
}

impl SubMesh {
    /// True when a texture coordinate leaves 0..=1, so the texture must
    /// repeat rather than clamp.
    #[must_use]
    pub fn needs_uv_repeat(&self) -> bool {
        self.vertices
            .iter()
            .any(|v| v.uv.x < 0.0 || v.uv.x > 1.0 || v.uv.y < 0.0 || v.uv.y > 1.0)
    }
}

/// Builds the sub-meshes of a GfxObj's drawing polygons, in the order each
/// (surface, side) pair first appears. Polygons with fewer than three
/// vertices, an out-of-range surface or a missing vertex are skipped.
/// A polygon draws its back side too when its stippling says so, or when it
/// is clockwise-sided and not marked "no negative".
#[must_use]
pub fn build_gfx_obj_mesh(gfx: &GfxObj) -> Vec<SubMesh> {
    let positions: HashMap<u16, &ac_dat::objects::Vertex> = gfx
        .vertices
        .vertices
        .iter()
        .map(|(id, v)| (*id, v))
        .collect();

    struct Bucket {
        mesh: SubMesh,
        dedupe: HashMap<(u16, usize), u32>,
    }
    let mut buckets: Vec<Bucket> = Vec::new();

    let mut emit = |poly: &Polygon, surface_index: i16, is_neg: bool| {
        let Ok(surface_index) = usize::try_from(surface_index) else {
            return;
        };
        if surface_index >= gfx.surfaces.len() {
            return;
        }
        let slot = match buckets.iter().position(|b| {
            b.mesh.surface_index == surface_index
                && b.mesh.is_negative_side == is_neg
                && b.mesh.sides_type == poly.sides_type.0
        }) {
            Some(slot) => slot,
            None => {
                buckets.push(Bucket {
                    mesh: SubMesh {
                        surface_index,
                        surface_id: gfx.surfaces[surface_index],
                        is_negative_side: is_neg,
                        sides_type: poly.sides_type.0,
                        vertices: Vec::new(),
                        indices: Vec::new(),
                    },
                    dedupe: HashMap::new(),
                });
                buckets.len() - 1
            }
        };
        let bucket = &mut buckets[slot];

        let mut poly_out = Vec::with_capacity(poly.vertex_ids.len());
        for (i, &pos_id) in poly.vertex_ids.iter().enumerate() {
            // The back side uses its own uv indices when it has them, else
            // borrows the front's; no index at all means slot 0.
            let uv_index =
                if is_neg && !poly.neg_uv_indices.is_empty() && i < poly.neg_uv_indices.len() {
                    usize::from(poly.neg_uv_indices[i])
                } else if i < poly.pos_uv_indices.len() {
                    usize::from(poly.pos_uv_indices[i])
                } else {
                    0
                };
            let Some(sw) = positions.get(&pos_id) else {
                return;
            };
            let uv = sw
                .uvs
                .get(uv_index)
                .map_or(Vec2::ZERO, |uv| Vec2::new(uv.u, uv.v));
            let n = vec3(sw.normal);
            let normal = if is_neg { -n } else { n }.normalize();
            let index = *bucket.dedupe.entry((pos_id, uv_index)).or_insert_with(|| {
                bucket.mesh.vertices.push(MeshVertex {
                    position: vec3(sw.origin),
                    normal,
                    uv,
                });
                (bucket.mesh.vertices.len() - 1) as u32
            });
            poly_out.push(index);
        }
        if poly_out.len() < 3 {
            return;
        }
        // Fan triangulation; the back side winds the other way.
        for i in 1..poly_out.len() - 1 {
            if is_neg {
                bucket
                    .mesh
                    .indices
                    .extend([poly_out[i + 1], poly_out[i], poly_out[0]]);
            } else {
                bucket
                    .mesh
                    .indices
                    .extend([poly_out[0], poly_out[i], poly_out[i + 1]]);
            }
        }
    };

    for (_, poly) in &gfx.polygons {
        if poly.vertex_ids.len() < 3 {
            continue;
        }
        emit(poly, poly.pos_surface, false);
        let s = poly.stippling;
        let has_neg = s.contains(Stippling::NEGATIVE)
            || s.contains(Stippling::BOTH)
            || (!s.contains(Stippling::NO_NEG) && poly.sides_type.0 == SidesType::CLOCKWISE);
        if has_neg {
            emit(poly, poly.neg_surface, true);
        }
    }

    buckets.into_iter().map(|b| b.mesh).collect()
}

/// Which faces a draw throws away. The front face is the one whose
/// triangles wind counter-clockwise on screen (the winding
/// [`build_gfx_obj_mesh`] gives a polygon's own side).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Cull {
    /// Both faces draw.
    None,
    /// Front faces are culled.
    Front,
    /// Back faces are culled.
    Back,
}

/// The culling of a polygon's sides type: none for "no sides", front for
/// clockwise, back for counter-clockwise and landblock polygons.
#[must_use]
pub fn cull_of(sides_type: i32) -> Cull {
    match sides_type {
        SidesType::NONE => Cull::None,
        SidesType::CLOCKWISE => Cull::Front,
        _ => Cull::Back,
    }
}

/// How a surface blends.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Translucency {
    /// Drawn solid.
    Opaque,
    /// Solid with holes (alpha tested).
    ClipMap,
    /// Blended by its alpha.
    AlphaBlend,
    /// Added onto what is behind.
    Additive,
    /// Blended by one minus its alpha.
    InvAlpha,
}

/// The blend of a surface type. A translucent surface that would otherwise
/// be opaque or a clip map is alpha blended; otherwise additive, inverse
/// alpha, alpha/translucent and clip map win in that order.
#[must_use]
pub fn translucency_of(t: SurfaceType) -> Translucency {
    let has = |bits: u32| t.0 & bits != 0;
    let translucent = has(SurfaceType::TRANSLUCENT);
    let clip = has(SurfaceType::BASE1_CLIP_MAP);
    let would_be_opaque = !has(SurfaceType::ADDITIVE | SurfaceType::ALPHA | SurfaceType::INV_ALPHA);
    if translucent && (clip || would_be_opaque) {
        return Translucency::AlphaBlend;
    }
    if has(SurfaceType::ADDITIVE) {
        Translucency::Additive
    } else if has(SurfaceType::INV_ALPHA) {
        Translucency::InvAlpha
    } else if has(SurfaceType::ALPHA | SurfaceType::TRANSLUCENT) {
        Translucency::AlphaBlend
    } else if clip {
        Translucency::ClipMap
    } else {
        Translucency::Opaque
    }
}

/// The opacity a surface's translucency value gives: only a surface with
/// the translucent bit is see-through (`1 - translucency`, clamped).
#[must_use]
pub fn opacity_of(t: SurfaceType, translucency: f32) -> f32 {
    if t.0 & SurfaceType::TRANSLUCENT == 0 {
        1.0
    } else {
        (1.0 - translucency).clamp(0.0, 1.0)
    }
}

/// A surface with neither image bit is untextured (a flat colour).
#[must_use]
pub fn is_untextured(t: SurfaceType) -> bool {
    t.0 & (SurfaceType::BASE1_IMAGE | SurfaceType::BASE1_CLIP_MAP) == 0
}

/// Whether an untextured subset draws: everywhere except on a building's
/// outside shell.
#[must_use]
pub fn untextured_subset_draws(is_building_shell: bool, is_untextured: bool) -> bool {
    !is_untextured || !is_building_shell
}

/// One part of a Setup: its model and where it sits in the object.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MeshRef {
    /// The part's GfxObj id.
    pub gfx_obj_id: u32,
    /// Object-local transform of the part (scale, then rotation, then
    /// translation).
    pub transform: Mat4,
    /// The part's surface substitutions from its object's appearance
    /// (texture and palette swaps); 0 for none.
    pub variant: u32,
}

/// The pose a Setup rests in: an explicit frame, else the resting
/// placement, else the default placement, else the first one listed.
#[must_use]
pub fn resting_frame<'a>(
    setup: &'a Setup,
    override_frame: Option<&'a AnimationFrame>,
) -> Option<&'a AnimationFrame> {
    override_frame
        .or_else(|| placement(setup, PLACEMENT_RESTING))
        .or_else(|| placement(setup, PLACEMENT_DEFAULT))
        .or_else(|| setup.placement_frames.first().map(|(_, f)| f))
}

/// The frame a Setup is posed in for placement `id`: that placement's
/// frame, else the default one, else none.
#[must_use]
pub fn placement_frame(setup: &Setup, id: u32) -> Option<&AnimationFrame> {
    placement(setup, id).or_else(|| placement(setup, PLACEMENT_DEFAULT))
}

fn placement(setup: &Setup, id: u32) -> Option<&AnimationFrame> {
    setup
        .placement_frames
        .iter()
        .find(|(k, _)| *k == id)
        .map(|(_, f)| f)
}

/// The parts of a Setup in its resting pose, each with its default scale.
/// A part with no frame sits at the object's origin unrotated.
#[must_use]
pub fn flatten_setup(setup: &Setup, override_frame: Option<&AnimationFrame>) -> Vec<MeshRef> {
    let pose = resting_frame(setup, override_frame);
    setup
        .parts
        .iter()
        .enumerate()
        .map(|(i, &gfx_obj_id)| {
            let frame = pose.and_then(|p| p.frames.get(i));
            let scale = setup.default_scale.get(i).map_or(Vec3::ONE, |s| vec3(*s));
            let (rotation, translation) = frame.map_or((Quat::IDENTITY, Vec3::ZERO), split);
            MeshRef {
                gfx_obj_id,
                transform: Mat4::from_translation(translation)
                    * Mat4::from_quat(rotation)
                    * Mat4::from_scale(scale),
                variant: 0,
            }
        })
        .collect()
}

/// The rigid (unscaled) transform of each part in the resting pose, with
/// the part's offset multiplied by `object_scale`. Empty when the Setup
/// lists no placement at all.
#[must_use]
pub fn setup_part_transforms(
    setup: &Setup,
    override_frame: Option<&AnimationFrame>,
    object_scale: f32,
) -> Vec<Mat4> {
    let Some(pose) = resting_frame(setup, override_frame) else {
        return Vec::new();
    };
    (0..setup.parts.len())
        .map(|i| {
            let (rotation, translation) = pose
                .frames
                .get(i)
                .map_or((Quat::IDENTITY, Vec3::ZERO), split);
            Mat4::from_translation(translation * object_scale) * Mat4::from_quat(rotation)
        })
        .collect()
}

/// A dat vector as glam's.
#[must_use]
pub fn vec3(v: Vector3) -> Vec3 {
    Vec3::new(v.x, v.y, v.z)
}

/// A dat quaternion (w, x, y, z) as glam's.
#[must_use]
pub fn quat(q: Quaternion) -> Quat {
    Quat::from_xyzw(q.x, q.y, q.z, q.w)
}

fn split(frame: &Frame) -> (Quat, Vec3) {
    (quat(frame.orientation), vec3(frame.origin))
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_dat::objects::{GfxObjFlags, SetupFlags, Uv, Vertex, VertexArray};
    use ac_dat::types::Sphere;

    fn v(x: f32, y: f32, z: f32) -> Vector3 {
        Vector3 { x, y, z }
    }

    fn vertex(origin: Vector3, normal: Vector3, uvs: &[(f32, f32)]) -> Vertex {
        Vertex {
            origin,
            normal,
            uvs: uvs.iter().map(|&(u, v)| Uv { u, v }).collect(),
        }
    }

    fn poly(surface: i16, ids: &[u16], uvs: &[u8]) -> Polygon {
        Polygon {
            pos_surface: surface,
            neg_surface: -1,
            vertex_ids: ids.to_vec(),
            pos_uv_indices: uvs.to_vec(),
            ..Polygon::default()
        }
    }

    fn gfx(surfaces: &[u32], vertices: Vec<Vertex>, polygons: Vec<Polygon>) -> GfxObj {
        GfxObj {
            id: 0x0100_0000,
            flags: GfxObjFlags(GfxObjFlags::HAS_DRAWING),
            surfaces: surfaces.to_vec(),
            vertices: VertexArray {
                vertex_type: VertexArray::SW_VERTEX,
                vertices: vertices
                    .into_iter()
                    .enumerate()
                    .map(|(i, v)| (i as u16, v))
                    .collect(),
            },
            physics_polygons: Vec::new(),
            physics_bsp: None,
            sort_center: Vector3::default(),
            polygons: polygons
                .into_iter()
                .enumerate()
                .map(|(i, p)| (i as u16, p))
                .collect(),
            drawing_bsp: None,
            did_degrade: 0,
        }
    }

    fn up() -> Vector3 {
        v(0.0, 0.0, 1.0)
    }

    fn single_triangle() -> GfxObj {
        gfx(
            &[0x0800_0000],
            vec![
                vertex(v(0.0, 0.0, 0.0), up(), &[(0.0, 0.0)]),
                vertex(v(1.0, 0.0, 0.0), up(), &[(1.0, 0.0)]),
                vertex(v(0.0, 1.0, 0.0), up(), &[(0.0, 1.0)]),
            ],
            vec![poly(0, &[0, 1, 2], &[0, 0, 0])],
        )
    }

    // Golden cases: OpenAC tests/AcDream.Core.Tests/Meshing/GfxObjMeshTests.cs.

    #[test]
    fn single_triangle_gives_one_sub_mesh_of_one_triangle() {
        let subs = build_gfx_obj_mesh(&single_triangle());
        assert_eq!(subs.len(), 1);
        assert_eq!(subs[0].surface_id, 0x0800_0000);
        assert_eq!(subs[0].vertices.len(), 3);
        assert_eq!(subs[0].indices.len(), 3);
    }

    #[test]
    fn single_triangle_copies_positions_normals_and_uvs() {
        let sub = &build_gfx_obj_mesh(&single_triangle())[0];
        let at = |k: usize| sub.vertices[sub.indices[k] as usize];
        assert_eq!(at(0).position, Vec3::ZERO);
        assert_eq!(at(1).position, Vec3::X);
        assert_eq!(at(2).position, Vec3::Y);
        assert_eq!(at(0).normal, Vec3::Z);
        assert_eq!(at(0).uv, Vec2::new(0.0, 0.0));
        assert_eq!(at(1).uv, Vec2::new(1.0, 0.0));
        assert_eq!(at(2).uv, Vec2::new(0.0, 1.0));
    }

    fn quad_vertices() -> Vec<Vertex> {
        [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)]
            .into_iter()
            .map(|(x, y)| vertex(v(x, y, 0.0), up(), &[(0.0, 0.0)]))
            .collect()
    }

    #[test]
    fn a_quad_is_triangulated_as_a_fan() {
        let g = gfx(
            &[0x0800_0000],
            quad_vertices(),
            vec![poly(0, &[0, 1, 2, 3], &[0, 0, 0, 0])],
        );
        let sub = &build_gfx_obj_mesh(&g)[0];
        assert_eq!(sub.vertices.len(), 4);
        assert_eq!(sub.indices, [0, 1, 2, 0, 2, 3]);
    }

    #[test]
    fn same_position_with_different_uvs_duplicates_the_vertex() {
        let g = gfx(
            &[0x0800_0000],
            vec![
                vertex(v(0.0, 0.0, 0.0), up(), &[(0.0, 0.0), (1.0, 1.0)]),
                vertex(v(1.0, 0.0, 0.0), up(), &[(0.0, 0.0)]),
                vertex(v(0.0, 1.0, 0.0), up(), &[(0.0, 0.0)]),
            ],
            vec![
                poly(0, &[0, 1, 2], &[0, 0, 0]),
                poly(0, &[0, 1, 2], &[1, 0, 0]),
            ],
        );
        let sub = &build_gfx_obj_mesh(&g)[0];
        assert_eq!(sub.vertices.len(), 4);
        assert_eq!(sub.indices.len(), 6);
    }

    #[test]
    fn several_surfaces_give_several_sub_meshes() {
        let g = gfx(
            &[0x0800_0001, 0x0800_0002],
            quad_vertices(),
            vec![
                poly(0, &[0, 1, 2], &[0, 0, 0]),
                poly(1, &[1, 3, 2], &[0, 0, 0]),
            ],
        );
        let ids: Vec<u32> = build_gfx_obj_mesh(&g)
            .iter()
            .map(|s| s.surface_id)
            .collect();
        assert_eq!(ids, [0x0800_0001, 0x0800_0002]);
    }

    #[test]
    fn a_two_vertex_polygon_is_skipped() {
        let g = gfx(
            &[0x0800_0000],
            quad_vertices()[..2].to_vec(),
            vec![poly(0, &[0, 1], &[0, 0])],
        );
        assert!(build_gfx_obj_mesh(&g).is_empty());
    }

    #[test]
    fn a_no_pos_quad_still_emits_its_front_side() {
        let mut p = poly(0, &[0, 1, 2, 3], &[]);
        p.stippling = Stippling(Stippling::NO_POS);
        let verts = [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)]
            .into_iter()
            .map(|(x, y)| vertex(v(x, y, 0.0), up(), &[]))
            .collect();
        let sub = &build_gfx_obj_mesh(&gfx(&[0x0800_0000], verts, vec![p]))[0];
        assert_eq!(sub.vertices.len(), 4);
        assert_eq!(sub.indices.len(), 6);
        assert!(sub.vertices.iter().all(|v| v.uv == Vec2::ZERO));
    }

    #[test]
    fn a_clockwise_polygon_draws_its_back_side_wound_the_other_way() {
        let mut p = poly(0, &[0, 1, 2], &[0, 0, 0]);
        p.sides_type = SidesType(SidesType::CLOCKWISE);
        p.neg_surface = 0;
        let mut g = single_triangle();
        g.polygons = vec![(0, p)];
        let subs = build_gfx_obj_mesh(&g);
        assert_eq!(subs.len(), 2);
        assert!(subs[1].is_negative_side);
        assert_eq!(subs[1].indices, [2, 1, 0]);
        assert_eq!(subs[1].vertices[0].normal, -Vec3::Z);
    }

    #[test]
    fn translucency_rules() {
        let t = |bits| translucency_of(SurfaceType(bits));
        assert_eq!(t(SurfaceType::BASE1_IMAGE), Translucency::Opaque);
        assert_eq!(t(SurfaceType::BASE1_CLIP_MAP), Translucency::ClipMap);
        assert_eq!(
            t(SurfaceType::BASE1_CLIP_MAP | SurfaceType::TRANSLUCENT),
            Translucency::AlphaBlend
        );
        assert_eq!(
            t(SurfaceType::ADDITIVE | SurfaceType::TRANSLUCENT),
            Translucency::Additive
        );
        assert_eq!(t(SurfaceType::INV_ALPHA), Translucency::InvAlpha);
        assert_eq!(t(SurfaceType::ALPHA), Translucency::AlphaBlend);
        assert_eq!(opacity_of(SurfaceType(0), 0.7), 1.0);
        assert!((opacity_of(SurfaceType(SurfaceType::TRANSLUCENT), 0.25) - 0.75).abs() < 1e-6);
    }

    #[test]
    fn untextured_policy() {
        assert!(is_untextured(SurfaceType(SurfaceType::BASE1_SOLID)));
        assert!(!is_untextured(SurfaceType(SurfaceType::BASE1_IMAGE)));
        assert!(!untextured_subset_draws(true, true));
        assert!(untextured_subset_draws(false, true));
        assert!(untextured_subset_draws(true, false));
    }

    fn frame(origin: Vector3, orientation: Quat) -> Frame {
        Frame {
            origin,
            orientation: Quaternion {
                w: orientation.w,
                x: orientation.x,
                y: orientation.y,
                z: orientation.z,
            },
        }
    }

    fn identity_at(origin: Vector3) -> Frame {
        frame(origin, Quat::IDENTITY)
    }

    fn pose(frames: Vec<Frame>) -> AnimationFrame {
        AnimationFrame {
            frames,
            hooks: Vec::new(),
        }
    }

    fn setup(parts: &[u32], scales: Vec<Vector3>, placements: Vec<(u32, AnimationFrame)>) -> Setup {
        Setup {
            id: 0x0200_0000,
            flags: SetupFlags(0),
            parts: parts.to_vec(),
            parent_index: Vec::new(),
            default_scale: scales,
            holding_locations: Vec::new(),
            connection_points: Vec::new(),
            placement_frames: placements,
            cyl_spheres: Vec::new(),
            spheres: Vec::new(),
            height: 0.0,
            radius: 0.0,
            step_up_height: 0.0,
            step_down_height: 0.0,
            sorting_sphere: Sphere::default(),
            selection_sphere: Sphere::default(),
            lights: Vec::new(),
            default_animation: 0,
            default_script: 0,
            default_motion_table: 0,
            default_sound_table: 0,
            default_script_table: 0,
        }
    }

    fn one() -> Vector3 {
        v(1.0, 1.0, 1.0)
    }

    // Golden cases: OpenAC tests/AcDream.Core.Tests/Meshing/SetupMeshTests.cs.

    #[test]
    fn flatten_single_part_is_identity() {
        let s = setup(
            &[0x0100_0100],
            vec![one()],
            vec![(PLACEMENT_DEFAULT, pose(vec![identity_at(v(0.0, 0.0, 0.0))]))],
        );
        let refs = flatten_setup(&s, None);
        assert_eq!(refs.len(), 1);
        assert_eq!(refs[0].gfx_obj_id, 0x0100_0100);
        assert_eq!(refs[0].transform, Mat4::IDENTITY);
    }

    #[test]
    fn flatten_two_parts_keeps_order_and_offsets() {
        let s = setup(
            &[0x0100_0100, 0x0100_0200],
            vec![one(), one()],
            vec![(
                PLACEMENT_DEFAULT,
                pose(vec![
                    identity_at(v(0.0, 0.0, 0.0)),
                    identity_at(v(10.0, 0.0, 0.0)),
                ]),
            )],
        );
        let refs = flatten_setup(&s, None);
        assert_eq!(refs[1].gfx_obj_id, 0x0100_0200);
        assert_eq!(refs[1].transform.w_axis.x, 10.0);
    }

    #[test]
    fn flatten_applies_the_part_scale() {
        let s = setup(
            &[0x0100_0100],
            vec![v(2.0, 3.0, 4.0)],
            vec![(
                PLACEMENT_DEFAULT,
                pose(vec![identity_at(Vector3::default())]),
            )],
        );
        let m = flatten_setup(&s, None)[0].transform;
        assert_eq!((m.x_axis.x, m.y_axis.y, m.z_axis.z), (2.0, 3.0, 4.0));
    }

    #[test]
    fn flatten_without_placements_uses_identity() {
        let s = setup(&[0x0100_0100], vec![one()], Vec::new());
        let refs = flatten_setup(&s, None);
        assert_eq!(refs.len(), 1);
        assert_eq!(refs[0].transform, Mat4::IDENTITY);
    }

    // Golden cases: OpenAC tests/AcDream.Core.Tests/Meshing/SetupPartTransformsTests.cs.

    #[test]
    fn part_transforms_prefer_resting_over_default() {
        let s = setup(
            &[0x0100_0100, 0x0100_0101],
            vec![one(), one()],
            vec![
                (
                    PLACEMENT_RESTING,
                    pose(vec![
                        identity_at(Vector3::default()),
                        identity_at(v(0.0, 0.0, 1.0)),
                    ]),
                ),
                (
                    PLACEMENT_DEFAULT,
                    pose(vec![
                        identity_at(Vector3::default()),
                        identity_at(Vector3::default()),
                    ]),
                ),
            ],
        );
        let t = setup_part_transforms(&s, None, 1.0);
        assert_eq!(t.len(), 2);
        assert_eq!(t[1].transform_point3(Vec3::ZERO), Vec3::new(0.0, 0.0, 1.0));
    }

    #[test]
    fn part_transforms_fall_back_to_default() {
        let s = setup(
            &[0x0100_0100],
            vec![one()],
            vec![(PLACEMENT_DEFAULT, pose(vec![identity_at(v(2.0, 0.0, 0.0))]))],
        );
        let t = setup_part_transforms(&s, None, 1.0);
        assert_eq!(t[0].transform_point3(Vec3::ZERO), Vec3::new(2.0, 0.0, 0.0));
    }

    #[test]
    fn part_transforms_are_empty_without_placements() {
        let s = setup(&[0x0100_0100, 0x0100_0101], Vec::new(), Vec::new());
        assert!(setup_part_transforms(&s, None, 1.0).is_empty());
    }

    #[test]
    fn part_transforms_exclude_the_default_scale() {
        let s = setup(
            &[0x0100_0100],
            vec![v(2.0, 2.0, 2.0)],
            vec![(
                PLACEMENT_RESTING,
                pose(vec![identity_at(Vector3::default())]),
            )],
        );
        let t = setup_part_transforms(&s, None, 1.0);
        assert_eq!(t[0].transform_point3(Vec3::ONE), Vec3::ONE);
    }

    #[test]
    fn object_scale_moves_the_origin_but_not_the_axes() {
        let s = setup(
            &[0x0100_0100],
            vec![v(7.0, 8.0, 9.0)],
            vec![(
                PLACEMENT_RESTING,
                pose(vec![frame(
                    v(2.0, 0.0, 0.0),
                    Quat::from_rotation_z(std::f32::consts::FRAC_PI_2),
                )]),
            )],
        );
        let t = setup_part_transforms(&s, None, 3.0)[0];
        assert_eq!(t.w_axis.truncate(), Vec3::new(6.0, 0.0, 0.0));
        let axis = t.transform_vector3(Vec3::X);
        assert!(axis.x.abs() < 1e-4 && (axis.y - 1.0).abs() < 1e-4);
    }

    // Golden cases: OpenAC tests/AcDream.Core.Tests/Meshing/SetupPartTransformsTests.cs
    // (`ResolvePlacementFrame_*`).

    /// Placement id of a house hook.
    const PLACEMENT_HOOK: u32 = 0x67;
    /// Placement id of a right hand in combat, a placement the item lacks.
    const PLACEMENT_RIGHT_HAND_COMBAT: u32 = 0x01;

    fn hooked_item_setup(with_default: bool) -> Setup {
        let mut placements = vec![
            (PLACEMENT_RESTING, pose(vec![identity_at(v(0.0, 0.0, 1.0))])),
            (
                PLACEMENT_HOOK,
                pose(vec![frame(
                    v(0.0, 0.0, 0.0),
                    Quat::from_rotation_x(std::f32::consts::FRAC_PI_2),
                )]),
            ),
        ];
        if with_default {
            placements.push((PLACEMENT_DEFAULT, pose(vec![identity_at(v(0.0, 0.0, 0.0))])));
        }
        setup(&[0x0100_0100], vec![one()], placements)
    }

    #[test]
    fn placement_frame_uses_the_server_placement_when_the_setup_has_it() {
        let s = hooked_item_setup(true);
        let frame = placement_frame(&s, PLACEMENT_HOOK).expect("hook frame");
        assert_eq!(frame, &s.placement_frames[1].1);
    }

    #[test]
    fn placement_frame_falls_back_to_default_when_the_placement_is_missing() {
        let s = hooked_item_setup(true);
        let frame = placement_frame(&s, PLACEMENT_RIGHT_HAND_COMBAT).expect("default frame");
        assert_eq!(frame, &s.placement_frames[2].1);
    }

    #[test]
    fn placement_frame_is_none_when_the_setup_has_neither() {
        let s = hooked_item_setup(false);
        assert!(placement_frame(&s, PLACEMENT_RIGHT_HAND_COMBAT).is_none());
    }
}
