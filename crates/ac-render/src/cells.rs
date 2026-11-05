//! Interior cells and buildings, read from the dats on the loader thread:
//! the meshes of building interiors and dungeon rooms (from each cell's
//! shape in its Environment and the cell's own surface list), the objects
//! standing in them, the portal data the frame walk floods through, and
//! each building's doorways and level-of-detail ladder.

use std::collections::HashMap;

use ac_dat::objects::{
    BspNode as DatBspNode, BspTree as DatBspTree, CellStruct, EnvCell, EnvCellFlags, Environment,
    GfxObj, GfxObjDegradeInfo, GfxObjFlags, LandBlockInfo, Polygon as DatPolygon, Setup,
};
use glam::{Mat4, Vec3};

use crate::assets::{GfxObjData, MeshGpuVertex, StaticInstance, SubMeshRange};
use crate::content::Content;
use crate::degrade::DegradeLevel;
use crate::meshing::{Cull, flatten_setup, quat, vec3};
use crate::walk::{self, BspNode, BspTree, BuildingPortal, CellPortal, WalkBuilding, WalkCell};
use crate::world_frame::LandblockCoord;

/// Surfaces of interior cells are stored relative to this id.
const SURFACE_BASE: u32 = 0x0800_0000;

/// One interior cell, landblock-local.
#[derive(Debug, Clone)]
pub struct CellData {
    /// Full cell id.
    pub id: u32,
    /// The room's mesh (`None` for a cell with nothing to draw, such as a
    /// doorway connector: it still floods and holds objects).
    pub mesh: Option<GfxObjData>,
    /// Cell to landblock.
    pub transform: Mat4,
    /// The walk's view of it (its matrices landblock-local).
    pub walk: WalkCell,
    /// Its collision, for finding which cell a point is in (placement
    /// landblock-local); `None` without a containment tree.
    pub physics: Option<ac_physics::CellPhysics>,
    /// Objects standing in it, landblock-local.
    pub statics: Vec<StaticInstance>,
    /// The statics it draws: every static whose drawn parts reach into it,
    /// its own and its neighbours' ([`crate::membership`]).
    pub members: Vec<crate::membership::CellStatic>,
    /// The cell can see outdoors.
    pub seen_outside: bool,
}

/// One building of a landblock, landblock-local.
#[derive(Debug, Clone)]
pub struct BuildingData {
    /// The shell's model (GfxObj or Setup).
    pub model_id: u32,
    /// Building to landblock.
    pub transform: Mat4,
    /// The land cell it stands in (`x * 8 + y`).
    pub land_cell: usize,
    /// Its doorways, with part 0's matrices landblock-local.
    pub walk: WalkBuilding,
    /// Part 0's model and its transform within the building.
    pub part0: (u32, Mat4),
    /// Part 0's z scale and sort centre (part-local).
    pub part0_scale_z: f32,
    /// Part 0's sort centre, part-local.
    pub sort_center: Vec3,
    /// Part 0's ladder (empty without one).
    pub levels: Vec<DegradeLevel>,
    /// Part 0's doorway tree, and the tree of each level's model.
    pub trees: HashMap<u32, BspTree>,
    /// Its collision shell for finding cells.
    pub physics: ac_physics::BuildingPhysics,
}

/// A GfxObj's ladder; empty when it has none.
#[must_use]
pub fn degrade_levels(content: &Content, gfx: &GfxObj) -> Vec<DegradeLevel> {
    if !gfx.flags.contains(GfxObjFlags::HAS_DID_DEGRADE) || gfx.did_degrade == 0 {
        return Vec::new();
    }
    content
        .dats
        .get::<GfxObjDegradeInfo>(gfx.did_degrade)
        .ok()
        .flatten()
        .map(|info| {
            info.degrades
                .iter()
                .map(|d| DegradeLevel {
                    gfx_obj_id: d.id,
                    mode: d.degrade_mode,
                    min_dist: d.min_dist,
                    ideal_dist: d.ideal_dist,
                    max_dist: d.max_dist,
                })
                .collect()
        })
        .unwrap_or_default()
}

/// Mesh an interior cell's shape with the cell's surfaces: each polygon
/// once per face its sides type draws (a double-sided polygon a second time
/// reversed, a two-surface polygon with its negative surface too), batched
/// per surface slot in slot order; untextured slots are not drawn.
/// `surface_untextured` says whether a surface id is untextured (`None`
/// when it does not exist).
#[must_use]
pub fn build_cell_mesh(
    cell_id: u32,
    cell_struct: &CellStruct,
    surfaces: &[u16],
    surface_untextured: impl Fn(u32) -> Option<bool>,
) -> GfxObjData {
    struct Batch {
        indices: Vec<u32>,
        repeat: bool,
    }
    let positions: HashMap<u16, &ac_dat::objects::Vertex> = cell_struct
        .vertex_array
        .vertices
        .iter()
        .map(|(id, v)| (*id, v))
        .collect();
    let mut vertices: Vec<MeshGpuVertex> = Vec::new();
    let mut lookup: HashMap<(u16, u16, bool), u32> = HashMap::new();
    let mut batches: std::collections::BTreeMap<usize, Batch> = std::collections::BTreeMap::new();
    let mut slot_state: HashMap<usize, Option<(u32, bool)>> = HashMap::new();

    for (_, poly) in &cell_struct.polygons {
        if poly.vertex_ids.len() < 3 {
            continue;
        }
        for candidate in side_candidates(poly.sides_type.0) {
            let raw = if candidate.positive {
                poly.pos_surface
            } else {
                poly.neg_surface
            };
            let Ok(slot) = usize::try_from(raw) else {
                continue;
            };
            let state = *slot_state.entry(slot).or_insert_with(|| {
                let id = SURFACE_BASE | u32::from(*surfaces.get(slot)?);
                surface_untextured(id).map(|untextured| (id, untextured))
            });
            let Some((_, untextured)) = state else {
                continue;
            };
            if untextured {
                continue;
            }
            let uv_absent = poly.stippling.0
                & if candidate.positive {
                    ac_dat::objects::Stippling::NO_POS
                } else {
                    ac_dat::objects::Stippling::NO_NEG
                }
                != 0;
            let batch = batches.entry(slot).or_insert_with(|| Batch {
                indices: Vec::new(),
                repeat: false,
            });
            emit_cell_polygon(
                poly,
                &positions,
                *candidate,
                uv_absent,
                &mut vertices,
                &mut lookup,
                &mut batch.indices,
                &mut batch.repeat,
            );
        }
    }

    let mut data = GfxObjData {
        id: cell_id,
        vertices: Vec::new(),
        indices: Vec::new(),
        sub_meshes: Vec::new(),
        degrades: Vec::new(),
        sort_center: Vec3::ZERO,
        drawing_sphere: None,
    };
    data.vertices = vertices;
    for (slot, batch) in batches {
        if batch.indices.is_empty() {
            continue;
        }
        let Some(Some((surface_id, _))) = slot_state.get(&slot) else {
            continue;
        };
        data.sub_meshes.push(SubMeshRange {
            surface_id: *surface_id,
            first_index: data.indices.len() as u32,
            index_count: batch.indices.len() as u32,
            base_vertex: 0,
            repeat: batch.repeat,
            cull: Cull::Back,
        });
        data.indices.extend_from_slice(&batch.indices);
    }
    data
}

/// How a polygon face is emitted.
#[derive(Debug, Clone, Copy)]
struct SideCandidate {
    /// Reads the positive surface and texture coordinates.
    positive: bool,
    /// Flips the normal.
    invert_normal: bool,
    /// Reverses the triangle winding.
    reverse: bool,
}

fn side_candidates(sides_type: i32) -> &'static [SideCandidate] {
    const SINGLE: [SideCandidate; 1] = [SideCandidate {
        positive: true,
        invert_normal: false,
        reverse: false,
    }];
    const DOUBLE: [SideCandidate; 2] = [
        SideCandidate {
            positive: true,
            invert_normal: false,
            reverse: false,
        },
        SideCandidate {
            positive: true,
            invert_normal: true,
            reverse: true,
        },
    ];
    const BOTH: [SideCandidate; 2] = [
        SideCandidate {
            positive: true,
            invert_normal: false,
            reverse: false,
        },
        SideCandidate {
            positive: false,
            invert_normal: true,
            reverse: false,
        },
    ];
    match sides_type {
        1 => &DOUBLE,
        2 => &BOTH,
        _ => &SINGLE,
    }
}

#[allow(clippy::too_many_arguments)]
fn emit_cell_polygon(
    poly: &DatPolygon,
    positions: &HashMap<u16, &ac_dat::objects::Vertex>,
    candidate: SideCandidate,
    uv_absent: bool,
    vertices: &mut Vec<MeshGpuVertex>,
    lookup: &mut HashMap<(u16, u16, bool), u32>,
    indices: &mut Vec<u32>,
    repeat: &mut bool,
) {
    let mut fan = Vec::with_capacity(poly.vertex_ids.len());
    for (i, &vid) in poly.vertex_ids.iter().enumerate() {
        let uv_index: i32 = if uv_absent {
            0
        } else if candidate.positive {
            poly.pos_uv_indices
                .get(i)
                .map_or(0, |&u| i32::from(u as i8))
        } else {
            poly.neg_uv_indices
                .get(i)
                .map_or(0, |&u| i32::from(u as i8))
        };
        let Some(vertex) = positions.get(&vid) else {
            continue;
        };
        let uv = usize::try_from(uv_index)
            .ok()
            .and_then(|i| vertex.uvs.get(i))
            .map_or([0.0, 0.0], |uv| [uv.u, uv.v]);
        if uv[0] < 0.0 || uv[0] > 1.0 || uv[1] < 0.0 || uv[1] > 1.0 {
            *repeat = true;
        }
        let key_uv = u16::try_from(uv_index).unwrap_or(u16::MAX);
        let key = (vid, key_uv, candidate.invert_normal);
        let index = *lookup.entry(key).or_insert_with(|| {
            let mut n = vec3(vertex.normal).normalize_or_zero();
            if candidate.invert_normal {
                n = -n;
            }
            vertices.push(MeshGpuVertex {
                position: vec3(vertex.origin).to_array(),
                normal: n.to_array(),
                uv,
            });
            (vertices.len() - 1) as u32
        });
        fan.push(index);
    }
    for t in 0..fan.len().saturating_sub(2) {
        let (a, b, c) = if candidate.reverse {
            (t + 2, t + 1, 0)
        } else {
            (0, t + 1, t + 2)
        };
        indices.extend_from_slice(&[fan[a], fan[b], fan[c]]);
    }
}

/// The walk's portal polygon of a cell shape's polygon `id`.
fn cell_polygon(cell_struct: &CellStruct, id: u16) -> walk::Polygon {
    let positions: HashMap<u16, Vec3> = cell_struct
        .vertex_array
        .vertices
        .iter()
        .map(|(i, v)| (*i, vec3(v.origin)))
        .collect();
    cell_struct
        .polygons
        .iter()
        .find(|(pid, _)| *pid == id)
        .and_then(|(_, p)| {
            let vs: Option<Vec<Vec3>> = p
                .vertex_ids
                .iter()
                .map(|v| positions.get(v).copied())
                .collect();
            walk::Polygon::new(vs?)
        })
        .unwrap_or_default()
}

/// The walk's portal side from a portal's flags: 0 when the side bit is
/// set (the cell lies in front of the polygon), else 1.
fn portal_side(flags: u16) -> u8 {
    if flags & 0x2 != 0 { 0 } else { 1 }
}

/// The walk cell of an interior cell, landblock-local.
#[must_use]
pub fn walk_cell(id: u32, env: &EnvCell, cell_struct: &CellStruct, transform: Mat4) -> WalkCell {
    let prefix = id & 0xFFFF_0000;
    let mut portals = Vec::with_capacity(env.cell_portals.len());
    let mut polygons = Vec::with_capacity(env.cell_portals.len());
    for (i, p) in env.cell_portals.iter().enumerate() {
        portals.push(CellPortal {
            other_cell_id: if p.other_cell_id == 0xFFFF {
                walk::OUTSIDE
            } else {
                prefix | u32::from(p.other_cell_id)
            },
            polygon: i,
            portal_side: portal_side(p.flags),
            other_portal_id: i32::from(p.other_portal_id as i16),
            exact_match: p.flags & 0x1 != 0,
        });
        polygons.push(cell_polygon(cell_struct, p.polygon_id));
    }
    WalkCell {
        cell_id: id,
        portals,
        polygons,
        stab_list: env.visible_cell_ids().collect(),
        world: transform,
        inverse: transform.inverse(),
    }
}

/// Every interior cell of a landblock (`num_cells` from its info).
#[must_use]
pub fn build_cells(
    content: &Content,
    coord: LandblockCoord,
    info: &LandBlockInfo,
) -> Vec<CellData> {
    let prefix = coord.terrain_id() & 0xFFFF_0000;
    let mut out = Vec::new();
    for n in 0..info.num_cells {
        let id = prefix | (0x0100 + n);
        let Some(env) = content.dats.get::<EnvCell>(id).ok().flatten() else {
            continue;
        };
        let Some(environment) = content
            .dats
            .get::<Environment>(env.environment_file_id())
            .ok()
            .flatten()
        else {
            continue;
        };
        let Some((_, cell_struct)) = environment
            .cells
            .iter()
            .find(|(i, _)| *i == u32::from(env.cell_structure))
        else {
            continue;
        };
        let rotation = quat(env.position.orientation);
        let origin = vec3(env.position.origin);
        let transform = Mat4::from_rotation_translation(rotation, origin);
        let mesh = build_cell_mesh(id, cell_struct, &env.surfaces, |surface| {
            content
                .dats
                .get::<ac_dat::objects::Surface>(surface)
                .ok()
                .flatten()
                .map(|s| crate::meshing::is_untextured(s.surface_type))
        });
        let physics = ac_physics::CellPhysics::from_dat(
            id,
            &env,
            cell_struct,
            ac_physics::Placement::new(rotation, origin),
        )
        .and_then(Result::ok);
        let statics = env
            .static_objects
            .iter()
            .filter(|s| matches!(s.id >> 24, 0x01 | 0x02))
            .map(|s| StaticInstance {
                model_id: s.id,
                transform: crate::assets::frame_matrix(&s.frame),
                is_building_shell: false,
            })
            .collect();
        out.push(CellData {
            id,
            mesh: (!mesh.indices.is_empty()).then_some(mesh),
            transform,
            walk: walk_cell(id, &env, cell_struct, transform),
            physics,
            statics,
            members: Vec::new(),
            seen_outside: env.flags.contains(EnvCellFlags::SEEN_OUTSIDE),
        });
    }
    out
}

/// A GfxObj's drawing tree reduced to its doorway planes, with each
/// doorway's polygon.
#[must_use]
pub fn portal_tree(gfx: &GfxObj) -> Option<BspTree> {
    let tree: &DatBspTree = gfx.drawing_bsp.as_ref()?;
    let positions: HashMap<u16, Vec3> = gfx
        .vertices
        .vertices
        .iter()
        .map(|(i, v)| (*i, vec3(v.origin)))
        .collect();
    let polygon = |id: u16| -> Option<walk::Polygon> {
        let (_, p) = gfx.polygons.iter().find(|(pid, _)| *pid == id)?;
        let vs: Option<Vec<Vec3>> = p
            .vertex_ids
            .iter()
            .map(|v| positions.get(v).copied())
            .collect();
        walk::Polygon::new(vs?)
    };
    let plane = |p: &ac_dat::objects::Plane| walk::Plane {
        normal: vec3(p.normal),
        d: p.distance,
    };
    let nodes = tree
        .nodes
        .iter()
        .map(|n| match n {
            DatBspNode::Leaf(_) => BspNode::Leaf,
            DatBspNode::Split(s) => BspNode::Split {
                plane: plane(&s.plane),
                positive: s.positive,
                negative: s.negative,
            },
            DatBspNode::Portal(p) => BspNode::Portal {
                plane: plane(&p.plane),
                positive: Some(p.positive),
                negative: Some(p.negative),
                portals: p
                    .portals
                    .iter()
                    .filter_map(|r| Some((usize::from(r.portal_index), polygon(r.poly_id)?)))
                    .collect(),
            },
        })
        .collect();
    Some(nodes)
}

/// Every building of a landblock with its doorways and ladder.
#[must_use]
pub fn build_buildings(
    content: &Content,
    coord: LandblockCoord,
    info: &LandBlockInfo,
) -> Vec<BuildingData> {
    let prefix = coord.terrain_id() & 0xFFFF_0000;
    let mut out = Vec::new();
    for b in &info.buildings {
        if !matches!(b.model_id >> 24, 0x01 | 0x02) {
            continue;
        }
        let rotation = quat(b.frame.orientation);
        let origin = vec3(b.frame.origin);
        let transform = Mat4::from_rotation_translation(rotation, origin);
        let (part0_id, part0_transform, scale_z) = if b.model_id >> 24 == 0x01 {
            (b.model_id, Mat4::IDENTITY, 1.0)
        } else {
            let Some(setup) = content.dats.get::<Setup>(b.model_id).ok().flatten() else {
                continue;
            };
            let parts = flatten_setup(&setup, None);
            let Some(first) = parts.first() else {
                continue;
            };
            let scale_z = setup.default_scale.first().map_or(1.0, |s| s.z);
            (first.gfx_obj_id, first.transform, scale_z)
        };
        let gfx = content.dats.get::<GfxObj>(part0_id).ok().flatten();
        let mut trees = HashMap::new();
        let mut levels = Vec::new();
        let mut sort_center = Vec3::ZERO;
        if let Some(gfx) = &gfx {
            sort_center = vec3(gfx.sort_center);
            if let Some(tree) = portal_tree(gfx) {
                trees.insert(part0_id, tree);
            }
            levels = degrade_levels(content, gfx);
            for level in &levels {
                if level.gfx_obj_id == 0 || trees.contains_key(&level.gfx_obj_id) {
                    continue;
                }
                if let Some(level_gfx) = content.dats.get::<GfxObj>(level.gfx_obj_id).ok().flatten()
                    && let Some(tree) = portal_tree(&level_gfx)
                {
                    trees.insert(level.gfx_obj_id, tree);
                }
            }
        }
        let part_world = transform * part0_transform;
        let portals = b
            .portals
            .iter()
            .map(|p| BuildingPortal {
                portal_side: portal_side(p.flags),
                other_cell_id: if p.other_cell_id == 0xFFFF {
                    walk::OUTSIDE
                } else {
                    prefix | u32::from(p.other_cell_id)
                },
                other_portal_id: i32::from(p.other_portal_id as i16),
                exact_match: p.flags & 0x1 != 0,
                stab_list: p.stab_list.iter().map(|s| prefix | u32::from(*s)).collect(),
            })
            .collect();
        let physics = ac_physics::BuildingPhysics {
            placement: ac_physics::Placement::new(rotation, origin),
            portals: b
                .portals
                .iter()
                .map(|p| ac_physics::BldPortalInfo {
                    other_cell_id: prefix | u32::from(p.other_cell_id),
                    other_portal_id: p.other_portal_id as i16,
                    flags: p.flags,
                })
                .collect(),
            model_id: part0_id,
        };
        let cell = |v: f32| ((v / 24.0).floor().max(0.0) as usize).min(7);
        out.push(BuildingData {
            model_id: b.model_id,
            transform,
            land_cell: cell(origin.x) * 8 + cell(origin.y),
            walk: WalkBuilding {
                portals,
                world: part_world,
                inverse: part_world.inverse(),
            },
            part0: (part0_id, part0_transform),
            part0_scale_z: scale_z,
            sort_center,
            levels,
            trees,
            physics,
        });
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_dat::objects::{SidesType, Stippling, Uv, Vertex, VertexArray};
    use ac_dat::types::Vector3;

    fn v(x: f32, y: f32) -> Vertex {
        Vertex {
            origin: Vector3 { x, y, z: 0.0 },
            normal: Vector3 {
                x: 0.0,
                y: 0.0,
                z: 1.0,
            },
            uvs: vec![Uv { u: 0.0, v: 0.0 }, Uv { u: 2.0, v: 1.0 }],
        }
    }

    fn quad(sides: i32, pos: i16, neg: i16) -> DatPolygon {
        DatPolygon {
            stippling: Stippling(0),
            sides_type: SidesType(sides),
            pos_surface: pos,
            neg_surface: neg,
            vertex_ids: vec![0, 1, 2, 3],
            pos_uv_indices: vec![0, 1, 1, 0],
            neg_uv_indices: vec![0, 0, 0, 0],
        }
    }

    fn shape(polys: Vec<DatPolygon>) -> CellStruct {
        CellStruct {
            vertex_array: VertexArray {
                vertex_type: 1,
                vertices: vec![
                    (0, v(0.0, 0.0)),
                    (1, v(1.0, 0.0)),
                    (2, v(1.0, 1.0)),
                    (3, v(0.0, 1.0)),
                ],
            },
            polygons: polys
                .into_iter()
                .enumerate()
                .map(|(i, p)| (i as u16, p))
                .collect(),
            portals: Vec::new(),
            cell_bsp: DatBspTree {
                kind: ac_dat::objects::BspTreeKind::Cell,
                nodes: Vec::new(),
            },
            physics_polygons: Vec::new(),
            physics_bsp: DatBspTree {
                kind: ac_dat::objects::BspTreeKind::Physics,
                nodes: Vec::new(),
            },
            drawing_bsp: None,
        }
    }

    #[test]
    fn single_double_and_two_surface_polygons_emit_their_faces() {
        // Ported shape of OpenAC CellStructSideCandidatesTests: single = one
        // fan, double = the fan again reversed, both = the negative surface
        // too.
        let surfaces = [0x10u16, 0x11];
        let textured = |_| Some(false);
        let single = build_cell_mesh(1, &shape(vec![quad(0, 0, -1)]), &surfaces, textured);
        assert_eq!(single.sub_meshes.len(), 1);
        assert_eq!(single.indices.len(), 6);
        assert_eq!(single.sub_meshes[0].surface_id, 0x0800_0010);
        assert!(single.sub_meshes[0].repeat);
        let double = build_cell_mesh(1, &shape(vec![quad(1, 0, -1)]), &surfaces, textured);
        assert_eq!(double.indices.len(), 12);
        // The second copy winds the other way: (t + 2, t + 1, 0).
        assert_eq!(
            &double.indices[6..9],
            &[double.indices[2] + 4, double.indices[1] + 4, 4]
        );
        let both = build_cell_mesh(1, &shape(vec![quad(2, 0, 1)]), &surfaces, textured);
        assert_eq!(both.sub_meshes.len(), 2);
        assert_eq!(both.sub_meshes[1].surface_id, 0x0800_0011);
    }

    #[test]
    fn untextured_and_missing_surfaces_are_not_drawn() {
        let surfaces = [0x10u16, 0x11];
        let mesh = build_cell_mesh(1, &shape(vec![quad(2, 0, 1)]), &surfaces, |id| {
            (id == 0x0800_0011).then_some(true)
        });
        assert!(mesh.sub_meshes.is_empty());
        assert!(mesh.indices.is_empty());
    }
}
