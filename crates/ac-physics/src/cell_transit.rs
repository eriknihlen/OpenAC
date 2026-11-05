//! Which cells a moving or placed object reaches into, and which one holds
//! it.
//!
//! Outdoors a body reaches the 24 m cell it is in and, when it comes within
//! its radius of a cell edge, the neighbours across that edge. Indoors it
//! reaches the cell it is in and, through each portal it comes near, the
//! cell behind. A building's portals lead from the outdoor cell the
//! building stands in to its rooms.

use ac_core::physics::{adjust_to_outside, gid_to_lcoord, lcoord_to_gid};
use glam::Vec3;

use crate::bsp_query::{PlaneSide, classify_box};
use crate::cell_array::CellArray;
use crate::geometry::{Plane, Sphere};
use crate::portal::PortalInfo;
use crate::shadow::ShadowPartBox;
use crate::world::{BuildingPhysics, CellPhysics, CollisionWorld};

/// How far beyond its radius a sphere reaches into a portal, metres.
const EPSILON: f32 = 0.02;

/// The general collision tolerance.
const F_EPSILON: f32 = 0.0002;

/// Side of an outdoor cell, metres.
const CELL_LENGTH: f32 = 24.0;

/// Where found cells go: the cell array, and the footprint of every cell
/// the search looked at, when one is being recorded.
#[derive(Debug)]
pub struct CellCollector<'a> {
    /// The cells found, in order.
    pub cells: &'a mut CellArray,
    /// Every cell the search touched (found or only probed).
    pub footprint: Option<&'a mut CellArray>,
}

impl<'a> CellCollector<'a> {
    /// A collector into `cells` with no footprint.
    pub fn new(cells: &'a mut CellArray) -> Self {
        Self {
            cells,
            footprint: None,
        }
    }

    /// A collector into `cells` that also records into `footprint`.
    pub fn with_footprint(cells: &'a mut CellArray, footprint: Option<&'a mut CellArray>) -> Self {
        Self { cells, footprint }
    }

    /// Adds a found cell (and records it).
    pub fn add(&mut self, id: u32) {
        if let Some(f) = self.footprint.as_deref_mut() {
            f.add(id);
        }
        self.cells.add(id);
    }

    /// Records a cell the search looked at without finding it.
    pub fn probe(&mut self, id: u32) {
        if let Some(f) = self.footprint.as_deref_mut() {
            f.add(id);
        }
    }

    fn len(&self) -> usize {
        self.cells.len()
    }

    fn get(&self, index: usize) -> u32 {
        self.cells.ordered_ids()[index]
    }
}

fn effective_sphere_count(spheres: &[Sphere], num_spheres: usize) -> usize {
    if num_spheres == 0 || spheres.is_empty() {
        return 0;
    }
    num_spheres.min(spheres.len())
}

fn portal_plane(cell: &CellPhysics, portal: &PortalInfo) -> Option<Plane> {
    cell.portal_polygons
        .get(&portal.polygon_id)
        .map(|p| p.plane)
}

fn has_containment(cell: Option<&CellPhysics>) -> Option<&CellPhysics> {
    cell.filter(|c| c.has_containment())
}

/// The cells behind the portals of an interior cell that any of the
/// spheres comes near. `exit_outside` is set when a sphere straddles a
/// portal to the outdoors.
pub fn find_transit_cells_sphere(
    world: &CollisionWorld,
    current_cell: &CellPhysics,
    current_cell_id: u32,
    world_spheres: &[Sphere],
    num_spheres: usize,
    candidates: &mut CellCollector<'_>,
) -> bool {
    let mut exit_outside = false;
    let lb_prefix = current_cell_id & 0xFFFF_0000;
    let sphere_count = effective_sphere_count(world_spheres, num_spheres);
    if sphere_count == 0 {
        return exit_outside;
    }

    for portal in &current_cell.portals {
        let Some(portal_plane) = portal_plane(current_cell, portal) else {
            continue;
        };

        if portal.other_cell_id == 0xFFFF {
            if !exit_outside {
                for sphere in &world_spheres[..sphere_count] {
                    let pad = sphere.radius + F_EPSILON;
                    let local_center = current_cell.placement.to_local(sphere.center);
                    let dist = local_center.dot(portal_plane.normal) + portal_plane.d;
                    if dist > -pad && dist < pad {
                        exit_outside = true;
                        break;
                    }
                }
            }
            continue;
        }

        let other_id = lb_prefix | u32::from(portal.other_cell_id);
        candidates.probe(other_id);
        if let Some(other) = has_containment(world.cell(other_id)) {
            for sphere in &world_spheres[..sphere_count] {
                let other_local = other.placement.to_local(sphere.center);
                if other
                    .containment_bsp
                    .sphere_intersects(other_local, sphere.radius)
                {
                    candidates.add(other_id);
                    break;
                }
            }
            continue;
        }

        for sphere in &world_spheres[..sphere_count] {
            let rad = sphere.radius + EPSILON;
            let local_center = current_cell.placement.to_local(sphere.center);
            let dist = local_center.dot(portal_plane.normal) + portal_plane.d;
            let hit = if portal.portal_side() {
                dist > -rad
            } else {
                dist < rad
            };
            if hit {
                candidates.add(other_id);
                break;
            }
        }
    }
    exit_outside
}

/// The cells behind the portals of an interior cell that any part box
/// crosses into. Returns true when a box crosses a portal to the outdoors.
pub fn find_transit_cells_box(
    world: &CollisionWorld,
    current_cell: &CellPhysics,
    current_cell_id: u32,
    world_parts: &[ShadowPartBox],
    world_part_spheres: &[Sphere],
    candidates: &mut CellCollector<'_>,
) -> bool {
    let mut exit_outside = false;
    let part_count = world_parts.len().min(world_part_spheres.len());
    if part_count == 0 {
        return exit_outside;
    }
    let lb_prefix = current_cell_id & 0xFFFF_0000;

    for portal in &current_cell.portals {
        let Some(portal_plane) = portal_plane(current_cell, portal) else {
            continue;
        };

        for i in 0..part_count {
            let sphere = world_part_spheres[i];
            let rad = sphere.radius + F_EPSILON;
            let local_center = current_cell.placement.to_local(sphere.center);
            let dist = local_center.dot(portal_plane.normal) + portal_plane.d;
            let passes_cheap_reject = if portal.portal_side() {
                dist > -rad
            } else {
                dist < rad
            };
            if !passes_cheap_reject {
                continue;
            }

            let part_box = world_parts[i];
            let (local_min, local_max) = part_box.refit_to_local(&current_cell.placement);
            let sidedness = classify_box(&portal_plane, local_min, local_max);
            let crossing_side = if portal.portal_side() {
                PlaneSide::Positive
            } else {
                PlaneSide::Negative
            };
            let crosses = sidedness == PlaneSide::Straddle || sidedness == crossing_side;
            if !crosses {
                continue;
            }

            if portal.other_cell_id == 0xFFFF {
                exit_outside = true;
                break;
            }

            let other_id = lb_prefix | u32::from(portal.other_cell_id);
            candidates.probe(other_id);
            let Some(other) = has_containment(world.cell(other_id)) else {
                candidates.add(other_id);
                break;
            };

            let (dest_min, dest_max) = part_box.refit_to_local(&other.placement);
            if other.containment_bsp.box_intersects(dest_min, dest_max) {
                candidates.add(other_id);
                break;
            }
        }
    }
    exit_outside
}

/// Adds the outdoor cell a sphere's centre is in and the neighbours whose
/// edge it comes within its radius of. `current_block_origin` is the world
/// position of `current_cell_id`'s landblock corner. False when the centre
/// is off the map.
pub fn add_all_outside_cells(
    world_sphere_center: Vec3,
    sphere_radius: f32,
    current_cell_id: u32,
    current_block_origin: Vec3,
    candidates: &mut CellCollector<'_>,
) -> bool {
    let center = world_sphere_center - current_block_origin;
    let mut cell_id = current_cell_id;
    let mut local = center.to_array();
    if !adjust_to_outside(&mut cell_id, &mut local) {
        return false;
    }
    let center = Vec3::from_array(local);
    let Some((lx, ly)) = gid_to_lcoord(cell_id) else {
        return false;
    };

    add_outside_cell(candidates, lx, ly);

    let point_x = center.x - (center.x / CELL_LENGTH).floor() * CELL_LENGTH;
    let point_y = center.y - (center.y / CELL_LENGTH).floor() * CELL_LENGTH;
    let min_rad = sphere_radius;
    let max_rad = CELL_LENGTH - sphere_radius;

    if point_x > max_rad {
        add_outside_cell(candidates, lx + 1, ly);
        if point_y > max_rad {
            add_outside_cell(candidates, lx + 1, ly + 1);
        }
        if point_y < min_rad {
            add_outside_cell(candidates, lx + 1, ly - 1);
        }
    }
    if point_x < min_rad {
        add_outside_cell(candidates, lx - 1, ly);
        if point_y > max_rad {
            add_outside_cell(candidates, lx - 1, ly + 1);
        }
        if point_y < min_rad {
            add_outside_cell(candidates, lx - 1, ly - 1);
        }
    }
    if point_y > max_rad {
        add_outside_cell(candidates, lx, ly + 1);
    }
    if point_y < min_rad {
        add_outside_cell(candidates, lx, ly - 1);
    }
    true
}

/// [`add_all_outside_cells`] for each sphere, stopping at the first one off
/// the map.
pub fn add_all_outside_cells_for_spheres(
    world_spheres: &[Sphere],
    num_spheres: usize,
    current_cell_id: u32,
    current_block_origin: Vec3,
    candidates: &mut CellCollector<'_>,
) {
    let count = effective_sphere_count(world_spheres, num_spheres);
    for sphere in &world_spheres[..count] {
        if !add_all_outside_cells(
            sphere.center,
            sphere.radius,
            current_cell_id,
            current_block_origin,
            candidates,
        ) {
            break;
        }
    }
}

/// Adds every outdoor cell of the rectangle the part boxes cover (one
/// rectangle around all parts, measured from the first part's cell, in
/// global cell coordinates so it may cross landblocks). False when the
/// first part is off the map.
pub fn add_all_outside_cells_from_parts(
    world_parts: &[ShadowPartBox],
    current_cell_id: u32,
    current_block_origin: Vec3,
    candidates: &mut CellCollector<'_>,
) -> bool {
    let Some(first) = world_parts.first() else {
        return false;
    };
    let seed_frame_pos = first.world_position - current_block_origin;
    let mut base_frame = seed_frame_pos.to_array();
    let mut base_cell_id = current_cell_id;
    if !adjust_to_outside(&mut base_cell_id, &mut base_frame) {
        return false;
    }
    let base_frame_pos = Vec3::from_array(base_frame);
    let Some((gx, gy)) = gid_to_lcoord(base_cell_id) else {
        return false;
    };

    let base_x = (((base_cell_id & 0xFFFF).wrapping_sub(1)) >> 3) as i32;
    let base_y = (base_cell_id.wrapping_sub(1) & 7) as i32;

    let frame_origin = current_block_origin - (base_frame_pos - seed_frame_pos);

    let (mut min_dx, mut min_dy, mut max_dx, mut max_dy) = (0i32, 0i32, 0i32, 0i32);
    for part in world_parts {
        let (box_min, box_max) = part.refit_to(frame_origin);
        let a = (box_min.x / CELL_LENGTH).floor() as i32;
        let b = (box_min.y / CELL_LENGTH).floor() as i32;
        let c = (box_max.x / CELL_LENGTH).floor() as i32;
        let d = (box_max.y / CELL_LENGTH).floor() as i32;
        if a - base_x < min_dx {
            min_dx = a - base_x;
        }
        if b - base_y < min_dy {
            min_dy = b - base_y;
        }
        if c - base_x > max_dx {
            max_dx = c - base_x;
        }
        if d - base_y > max_dy {
            max_dy = d - base_y;
        }
    }

    add_cell_block(
        gx + min_dx,
        gy + min_dy,
        gx + max_dx,
        gy + max_dy,
        candidates,
    );
    true
}

fn add_cell_block(x0: i32, y0: i32, x1: i32, y1: i32, candidates: &mut CellCollector<'_>) {
    for x in x0..=x1 {
        for y in y0..=y1 {
            add_outside_cell(candidates, x, y);
        }
    }
}

fn add_outside_cell(candidates: &mut CellCollector<'_>, lx: i32, ly: i32) {
    if let Some(gid) = lcoord_to_gid(lx, ly)
        && gid != 0
    {
        candidates.add(gid);
    }
}

/// The building's rooms that any sphere reaches into through the
/// building's portals. Returns true when one does.
pub fn check_building_transit(
    world: &CollisionWorld,
    building: &BuildingPhysics,
    world_spheres: &[Sphere],
    num_spheres: usize,
    candidates: &mut CellCollector<'_>,
) -> bool {
    let mut hits_interior_cell = false;
    let sphere_count = effective_sphere_count(world_spheres, num_spheres);
    if sphere_count == 0 {
        return hits_interior_cell;
    }

    for portal in &building.portals {
        if portal.other_portal_id < 0 {
            continue;
        }
        candidates.probe(portal.other_cell_id);
        let Some(other) = has_containment(world.cell(portal.other_cell_id)) else {
            continue;
        };

        let mut inside = false;
        for sphere in &world_spheres[..sphere_count] {
            if inside {
                break;
            }
            let local_center = other.placement.to_local(sphere.center);
            inside = other
                .containment_bsp
                .sphere_intersects(local_center, sphere.radius);
        }

        if inside {
            hits_interior_cell = true;
            candidates.add(portal.other_cell_id);
        }
    }
    hits_interior_cell
}

/// The cells an object registered with spheres reaches (its shadow cell
/// set): the seed cell, outdoor neighbours, portal neighbours and building
/// rooms. A static object seeded indoors keeps only the seed cell and the
/// cells visible from it.
#[must_use]
pub fn build_shadow_cell_set(
    world: &CollisionWorld,
    seed_cell_id: u32,
    world_spheres: &[Sphere],
    num_spheres: usize,
    is_static: bool,
) -> Vec<u32> {
    let mut cells = CellArray::new();
    let sphere_count = effective_sphere_count(world_spheres, num_spheres);
    if seed_cell_id == 0 || sphere_count == 0 {
        return cells.ordered_ids().to_vec();
    }
    let mut candidates = CellCollector::new(&mut cells);

    let seed_low = seed_cell_id & 0xFFFF;
    let block_origin = world.terrain_origin(seed_cell_id).unwrap_or(Vec3::ZERO);

    let seed_loaded = if seed_low >= 0x0100 {
        candidates.add(seed_cell_id);
        world.cell(seed_cell_id).is_some()
    } else {
        add_all_outside_cells_for_spheres(
            world_spheres,
            sphere_count,
            seed_cell_id,
            block_origin,
            &mut candidates,
        );
        world.is_visible(seed_cell_id)
    };

    if seed_loaded {
        let mut outdoor_added = seed_low < 0x0100;
        let mut i = 0;
        while i < candidates.len() {
            let cell_id = candidates.get(i);
            i += 1;
            if cell_id & 0xFFFF >= 0x0100 {
                let Some(cell) = world.cell(cell_id) else {
                    continue;
                };
                let exit_straddle = find_transit_cells_sphere(
                    world,
                    cell,
                    cell_id,
                    world_spheres,
                    sphere_count,
                    &mut candidates,
                );
                if exit_straddle && !outdoor_added {
                    add_all_outside_cells_for_spheres(
                        world_spheres,
                        sphere_count,
                        seed_cell_id,
                        block_origin,
                        &mut candidates,
                    );
                    outdoor_added = true;
                }
            } else {
                if !world.is_visible(cell_id) {
                    continue;
                }
                if !outdoor_added {
                    add_all_outside_cells_for_spheres(
                        world_spheres,
                        sphere_count,
                        seed_cell_id,
                        block_origin,
                        &mut candidates,
                    );
                    outdoor_added = true;
                }
                if let Some(building) = world.building(cell_id) {
                    check_building_transit(
                        world,
                        building,
                        world_spheres,
                        sphere_count,
                        &mut candidates,
                    );
                }
            }
        }

        if is_static
            && seed_low >= 0x0100
            && let Some(seed_cell) = world.cell(seed_cell_id)
        {
            let keep: Vec<u32> = cells
                .iter()
                .filter(|id| *id == seed_cell_id || seed_cell.visible_cells.contains(*id))
                .collect();
            if keep.len() != cells.len() {
                cells.clear();
                for id in keep {
                    cells.add(id);
                }
            }
        }
    }

    cells.ordered_ids().to_vec()
}

/// Adds the building's rooms that a part box crosses into through the
/// reciprocal portal, and their portal neighbours.
#[allow(clippy::too_many_arguments)]
pub(crate) fn check_building_transit_from_parts(
    world: &CollisionWorld,
    building: &BuildingPhysics,
    world_parts: &[ShadowPartBox],
    world_part_spheres: &[Sphere],
    candidates: &mut CellCollector<'_>,
    seed_cell_id: u32,
    block_origin: Vec3,
    outdoor_added: &mut bool,
) -> Result<(), CellTransitError> {
    let part_count = world_parts.len().min(world_part_spheres.len());
    if part_count == 0 {
        return Ok(());
    }

    for building_portal in &building.portals {
        let reciprocal_index = building_portal.other_portal_id;
        if reciprocal_index < 0 {
            continue;
        }
        candidates.probe(building_portal.other_cell_id);
        let Some(destination) = has_containment(world.cell(building_portal.other_cell_id)) else {
            continue;
        };

        let reciprocal_index = reciprocal_index as usize;
        let Some(reciprocal) = destination.portals.get(reciprocal_index) else {
            return Err(CellTransitError::MissingReciprocalPortal {
                cell: building_portal.other_cell_id,
                portal: reciprocal_index,
                portals: destination.portals.len(),
            });
        };
        let Some(plane) = portal_plane(destination, reciprocal) else {
            continue;
        };

        for i in 0..part_count {
            let sphere = world_part_spheres[i];
            let padded_radius = sphere.radius + F_EPSILON;
            let center = destination.placement.to_local(sphere.center);
            let distance = center.dot(plane.normal) + plane.d;
            // The building gate is inclusive and faces the other way from
            // the indoor exit gate.
            let passes_cheap_reject = if reciprocal.portal_side() {
                distance <= padded_radius
            } else {
                distance >= -padded_radius
            };
            if !passes_cheap_reject {
                continue;
            }

            let (min, max) = world_parts[i].refit_to_local(&destination.placement);
            let side = classify_box(&plane, min, max);
            let same_side = if reciprocal.portal_side() {
                PlaneSide::Negative
            } else {
                PlaneSide::Positive
            };
            if side != PlaneSide::Straddle && side != same_side {
                continue;
            }
            if !destination.containment_bsp.box_intersects(min, max) {
                continue;
            }

            candidates.add(building_portal.other_cell_id);
            let exit_outside = find_transit_cells_box(
                world,
                destination,
                building_portal.other_cell_id,
                world_parts,
                world_part_spheres,
                candidates,
            );
            if exit_outside && !*outdoor_added {
                *outdoor_added = add_all_outside_cells_from_parts(
                    world_parts,
                    seed_cell_id,
                    block_origin,
                    candidates,
                );
            }
            break;
        }
    }
    Ok(())
}

/// Why a cell-set search gave up.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CellTransitError {
    /// A building portal names a reciprocal portal its room does not have.
    MissingReciprocalPortal {
        /// The room.
        cell: u32,
        /// The portal index named.
        portal: usize,
        /// How many portals the room has.
        portals: usize,
    },
}

impl std::fmt::Display for CellTransitError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::MissingReciprocalPortal {
                cell,
                portal,
                portals,
            } => write!(
                f,
                "building portal to cell {cell:#010X} references reciprocal portal {portal}, \
                 but the destination has {portals} portals"
            ),
        }
    }
}

impl std::error::Error for CellTransitError {}

/// The cells an object registered with part boxes reaches (the
/// bounding-box shadow cell set): the outdoor rectangle around the parts,
/// interior cells through portals the boxes cross, and building rooms.
pub fn build_shadow_cell_set_from_parts(
    world: &CollisionWorld,
    seed_cell_id: u32,
    world_parts: &[ShadowPartBox],
    world_part_spheres: &[Sphere],
    is_static: bool,
) -> Result<Vec<u32>, CellTransitError> {
    let _ = is_static;
    let mut cells = CellArray::new();
    if seed_cell_id == 0 || world_parts.is_empty() {
        return Ok(cells.ordered_ids().to_vec());
    }
    let mut candidates = CellCollector::new(&mut cells);

    let sphere_count = effective_sphere_count(world_part_spheres, world_part_spheres.len());
    let seed_low = seed_cell_id & 0xFFFF;
    let block_origin = world.terrain_origin(seed_cell_id).unwrap_or(Vec3::ZERO);

    let mut outdoor_added = false;
    let seed_loaded = if seed_low >= 0x0100 {
        candidates.add(seed_cell_id);
        world.cell(seed_cell_id).is_some()
    } else {
        candidates.add(seed_cell_id);
        outdoor_added = add_all_outside_cells_from_parts(
            world_parts,
            seed_cell_id,
            block_origin,
            &mut candidates,
        );
        world.is_visible(seed_cell_id)
    };

    if !seed_loaded {
        return Ok(cells.ordered_ids().to_vec());
    }

    let mut i = 0;
    while i < candidates.len() {
        let cell_id = candidates.get(i);
        i += 1;
        if cell_id & 0xFFFF >= 0x0100 {
            let Some(cell) = world.cell(cell_id) else {
                continue;
            };
            if sphere_count == 0 || world_parts.is_empty() {
                continue;
            }
            let exit_straddle = find_transit_cells_box(
                world,
                cell,
                cell_id,
                world_parts,
                world_part_spheres,
                &mut candidates,
            );
            if exit_straddle && !outdoor_added {
                outdoor_added = add_all_outside_cells_from_parts(
                    world_parts,
                    seed_cell_id,
                    block_origin,
                    &mut candidates,
                );
            }
        } else {
            if !world.is_visible(cell_id) {
                continue;
            }
            if !outdoor_added {
                outdoor_added = add_all_outside_cells_from_parts(
                    world_parts,
                    seed_cell_id,
                    block_origin,
                    &mut candidates,
                );
            }
            if let Some(building) = world.building(cell_id)
                && sphere_count > 0
            {
                check_building_transit_from_parts(
                    world,
                    building,
                    world_parts,
                    world_part_spheres,
                    &mut candidates,
                    seed_cell_id,
                    block_origin,
                    &mut outdoor_added,
                )?;
            }
        }
    }

    Ok(cells.ordered_ids().to_vec())
}

/// The interior cell holding `world_point`: `start_cell_id` itself, else
/// one of the cells visible from it (`use_stab_list`) or one behind its
/// portals. 0 when none does. Every cell looked at is recorded in
/// `probed_cells`.
pub fn find_visible_child_cell(
    world: &CollisionWorld,
    start_cell_id: u32,
    world_point: Vec3,
    use_stab_list: bool,
    mut probed_cells: Option<&mut CellArray>,
) -> u32 {
    if let Some(p) = probed_cells.as_deref_mut() {
        p.add(start_cell_id);
    }
    let Some(start) = world.cell(start_cell_id) else {
        return 0;
    };

    if point_in_cell(Some(start), world_point) {
        return start_cell_id;
    }

    if use_stab_list {
        for id in &start.visible_cells {
            if let Some(p) = probed_cells.as_deref_mut() {
                p.add(id);
            }
            if point_in_cell(world.cell(id), world_point) {
                return id;
            }
        }
    } else {
        // The portals' other-cell ids are looked up as they are stored (low
        // word only), as OpenAC does.
        for portal in &start.portals {
            let other = u32::from(portal.other_cell_id);
            if let Some(p) = probed_cells.as_deref_mut() {
                p.add(other);
            }
            if point_in_cell(world.cell(other), world_point) {
                return other;
            }
        }
    }

    0
}

fn point_in_cell(cell: Option<&CellPhysics>, world_point: Vec3) -> bool {
    let Some(cell) = cell else {
        return false;
    };
    if cell.portals.is_empty() || !cell.has_containment() {
        return false;
    }
    let local = cell.placement.to_local(world_point);
    cell.containment_bsp.point_inside(local)
}

/// The cell holding a sphere, and every cell it reaches (into
/// `candidates`). `carried_block_origin` overrides the landblock corner the
/// search measures outdoor cells from.
pub fn find_cell_set(
    world: &CollisionWorld,
    world_sphere_center: Vec3,
    sphere_radius: f32,
    current_cell_id: u32,
    candidates: &mut CellArray,
    carried_block_origin: Option<Vec3>,
) -> u32 {
    let spheres = [Sphere::new(world_sphere_center, sphere_radius)];
    let mut collector = CellCollector::new(candidates);
    build_cell_set_and_pick_containing(
        world,
        &spheres,
        1,
        current_cell_id,
        carried_block_origin,
        &mut collector,
    )
    .0
}

/// The cell holding the first sphere, every cell the spheres reach (into
/// `candidates`, cleared first), and whether the returned cell really
/// contains the sphere. When no resident cell contains it, the movement
/// path keeps the seed cell; the flag is then false.
pub fn build_cell_set_and_pick_containing(
    world: &CollisionWorld,
    world_spheres: &[Sphere],
    num_spheres: usize,
    current_cell_id: u32,
    carried_block_origin: Option<Vec3>,
    candidates: &mut CellCollector<'_>,
) -> (u32, bool) {
    let mut containing_cell_found = true;
    candidates.cells.clear();
    let sphere_count = effective_sphere_count(world_spheres, num_spheres);
    if sphere_count == 0 {
        return (current_cell_id, containing_cell_found);
    }

    let world_sphere_center = world_spheres[0].center;
    let sphere_radius = world_spheres[0].radius;
    let current_low = current_cell_id & 0xFFFF;

    let block_origin = match carried_block_origin {
        Some(anchor) => anchor,
        None => match world.terrain_origin(current_cell_id) {
            Some(origin) => origin,
            None => {
                if current_low < 0x0100 {
                    return (current_cell_id, containing_cell_found);
                }
                Vec3::ZERO
            }
        },
    };

    let mut outdoor_pick_allowed = current_low < 0x0100;

    let mut outdoor_added;
    if current_low >= 0x0100 {
        if world.cell(current_cell_id).is_none() {
            return (current_cell_id, containing_cell_found);
        }
        candidates.add(current_cell_id);
        outdoor_added = false;
    } else {
        add_all_outside_cells_for_spheres(
            world_spheres,
            sphere_count,
            current_cell_id,
            block_origin,
            candidates,
        );
        outdoor_added = true;
    }

    let mut i = 0;
    while i < candidates.len() {
        let cell_id = candidates.get(i);
        i += 1;

        if cell_id & 0xFFFF < 0x0100 {
            if !world.is_visible(cell_id) {
                continue;
            }
            let Some(building) = world.building(cell_id) else {
                continue;
            };
            check_building_transit(world, building, world_spheres, sphere_count, candidates);
            continue;
        }

        let Some(cell) = world.cell(cell_id) else {
            continue;
        };
        let exit_outside_straddle = find_transit_cells_sphere(
            world,
            cell,
            cell_id,
            world_spheres,
            sphere_count,
            candidates,
        );
        outdoor_pick_allowed |= exit_outside_straddle;
        if exit_outside_straddle && !outdoor_added {
            add_all_outside_cells_for_spheres(
                world_spheres,
                sphere_count,
                current_cell_id,
                block_origin,
                candidates,
            );
            outdoor_added = true;
        }
    }

    let mut containing_outdoor_id = 0u32;
    {
        let pick_pos = world_sphere_center - block_origin;
        let mut pick_cell = current_cell_id;
        let mut pos = pick_pos.to_array();
        if adjust_to_outside(&mut pick_cell, &mut pos) {
            containing_outdoor_id = pick_cell;
        }
    }

    let mut outdoor_result = 0u32;
    for cand_id in candidates.cells.iter() {
        if cand_id & 0xFFFF >= 0x0100 {
            if point_in_cell(world.cell(cand_id), world_sphere_center) {
                return (cand_id, containing_cell_found);
            }
        } else if outdoor_result == 0
            && containing_outdoor_id != 0
            && outdoor_pick_allowed
            && world.is_visible(cand_id)
            && cand_id == containing_outdoor_id
        {
            outdoor_result = cand_id;
        }
    }

    if outdoor_result != 0 {
        return (outdoor_result, containing_cell_found);
    }

    if current_low >= 0x0100
        && let Some(cur) = has_containment(world.cell(current_cell_id))
    {
        let cur_local = cur.placement.to_local(world_sphere_center);
        if !cur
            .containment_bsp
            .sphere_intersects(cur_local, sphere_radius)
        {
            let recovered = find_visible_child_cell(
                world,
                current_cell_id,
                world_sphere_center,
                true,
                candidates.footprint.as_deref_mut(),
            );
            if recovered != 0 && recovered != current_cell_id {
                return (recovered, containing_cell_found);
            }
            // Every resident candidate rejected the point and the seed
            // cell does not contain it either.
            containing_cell_found = false;
        }
    }

    (current_cell_id, containing_cell_found)
}
