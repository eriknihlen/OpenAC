//! A landblock's indoor cells flattened onto the ground plane, for a map of
//! a dungeon: per height band, the floor area and the wall lines, in the
//! landblock's own frame and in metres.
//!
//! Each cell's structure is placed by the cell's own position and turn, and
//! every polygon is flattened: a level polygon that faces up is floor, a
//! standing polygon is a wall seen edge-on as a line, and the doorways
//! between cells (the structure's portal polygons and the ones the cell's
//! own portal list names) are left open. Cells are drawn in six-metre bands
//! of height; a tall room's upper cell, which has walls but no floor, is
//! drawn with the cell it opens onto below.

use std::cmp::Ordering;
use std::collections::hash_map::Entry;
use std::collections::{BTreeMap, HashMap, HashSet};
use std::sync::Arc;

use ac_dat::DatError;
use ac_dat::objects::{CellStruct, EnvCell, Environment, LandBlockInfo};
use ac_physics::content::DatSource;
use glam::{Mat4, Quat, Vec2, Vec3};

/// How far a polygon's normal must lean upward, as the cosine of its angle
/// from straight up, to count as level: 0.7 is about 45 degrees, so a stair
/// ramp is floor and a steep chute is not.
const LEVEL_MINIMUM_UP: f32 = 0.7;
/// The most a polygon's normal may lean up or down, as a cosine, to count
/// as standing: 0.3 is about 17 degrees off vertical.
const STANDING_MAXIMUM_UP: f32 = 0.3;
/// Cells are bucketed into bands this tall, metres.
const LAYER_HEIGHT: f32 = 6.0;
/// Bands are shifted down by this much so a cell whose origin sits a little
/// below a whole band still lands in that band.
const LAYER_OFFSET: f32 = 3.0;
/// Two wall lines within this angle, degrees, may be one line.
const MERGE_ANGLE_DEGREES: f32 = 1.0;
/// Two wall lines this far apart sideways, metres, may be one line.
const MERGE_OFFSET_METRES: f32 = 0.05;
/// Two collinear spans with a gap this small, metres, are joined.
const MERGE_GAP_METRES: f32 = 0.05;
/// A wall shorter than this, metres, is a sliver and dropped.
const MINIMUM_WALL_METRES: f32 = 0.01;

/// One landblock's indoor cells flattened onto the ground plane. Built once
/// and never changed afterwards.
#[derive(Debug, Clone, PartialEq)]
pub struct DungeonFloorplan {
    /// The landblock, with a zero low half.
    pub landblock_id: u32,
    /// The height bands, lowest first.
    pub layers: Vec<DungeonFloorplanLayer>,
    /// Every cell with geometry, in cell id order.
    pub cells: Vec<DungeonFloorplanCell>,
    /// The smallest box around every cell's geometry.
    pub bounds: FloorplanBounds,
    /// How the polygons were sorted.
    pub counts: FloorplanCounts,
}

/// What became of every polygon in a plan.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct FloorplanCounts {
    /// Polygons looked at.
    pub polygons: u32,
    /// Floor polygons.
    pub floors: u32,
    /// Level polygons facing down.
    pub ceilings: u32,
    /// Standing polygons that became wall lines.
    pub walls: u32,
    /// Doorways left open.
    pub portals: u32,
    /// Polygons too steep to be floor and too flat to be wall.
    pub slopes: u32,
    /// Wall lines left once collinear runs were joined.
    pub walls_after_merge: u32,
}

impl std::fmt::Display for FloorplanCounts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "polygons={} floors={} ceilings={} walls={} merged={} doorways={} slopes={}",
            self.polygons,
            self.floors,
            self.ceilings,
            self.walls,
            self.walls_after_merge,
            self.portals,
            self.slopes
        )
    }
}

/// One six-metre band of height: every floor polygon at that height and the
/// walls of every cell drawn there.
#[derive(Debug, Clone, PartialEq)]
pub struct DungeonFloorplanLayer {
    /// The band's key: the lowest height it covers, metres.
    pub z: f32,
    /// Floor area as closed polygons (landblock metres); they overlap where
    /// cells meet and are meant to be filled, not stitched.
    pub floors: Vec<Vec<Vec2>>,
    /// Wall lines, collinear runs merged.
    pub walls: Vec<FloorplanWall>,
}

/// A wall seen from above: one straight line, landblock metres.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FloorplanWall {
    /// One end.
    pub start: Vec2,
    /// The other end.
    pub end: Vec2,
}

impl FloorplanWall {
    /// The wall's length, metres.
    #[must_use]
    pub fn length(&self) -> f32 {
        self.start.distance(self.end)
    }
}

/// Where one cell sits in the plan.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DungeonFloorplanCell {
    /// The full cell id.
    pub cell_id: u32,
    /// The middle of the cell's geometry, landblock metres.
    pub center: Vec3,
    /// The key of the band the cell is drawn in.
    pub layer_z: f32,
    /// The box around every polygon of the cell, landblock metres.
    pub bounds: FloorplanBounds,
    /// The height of the cell's lowest floor (a level polygon facing up,
    /// at its corners' mean height), landblock metres; `None` for a cell
    /// with no floor of its own, such as a shaft or the upper half of a
    /// tall room.
    pub floor_z: Option<f32>,
}

/// The box around a plan, landblock metres.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FloorplanBounds {
    /// Lowest corner.
    pub min: Vec3,
    /// Highest corner.
    pub max: Vec3,
}

impl FloorplanBounds {
    /// The bounds of nothing, from which any point extends.
    pub const EMPTY: Self = Self {
        min: Vec3::splat(f32::MAX),
        max: Vec3::splat(f32::MIN),
    };

    /// True when nothing has been added.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.min.x > self.max.x
    }

    /// These bounds grown to include `point`.
    #[must_use]
    pub fn including(self, point: Vec3) -> Self {
        Self {
            min: self.min.min(point),
            max: self.max.max(point),
        }
    }
}

/// The band a cell at height `z` is drawn in.
#[must_use]
pub fn layer_key(z: f32) -> f32 {
    let key = ((z + LAYER_OFFSET) / LAYER_HEIGHT).floor() * LAYER_HEIGHT;
    // Minus zero is the same band as zero.
    if key == 0.0 { 0.0 } else { key }
}

/// Builds plans and keeps each one, and each flattened cell structure
/// (hundreds of cells share a few dozen), since the data never changes.
#[derive(Debug, Default)]
pub struct DungeonFloorplanBuilder {
    geometry_by_structure: HashMap<u32, Option<Arc<CellGeometry>>>,
    plans_by_landblock: HashMap<u32, Option<Arc<DungeonFloorplan>>>,
}

impl DungeonFloorplanBuilder {
    /// Nothing kept yet.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// How many cell structures have been flattened and kept.
    #[must_use]
    pub fn cached_geometry_count(&self) -> usize {
        self.geometry_by_structure.len()
    }

    /// The plan for the landblock of `landblock_id` (any cell id in it will
    /// do), or `None` when the landblock is not in the data or has no
    /// indoor cells. The same plan comes back on every later call.
    ///
    /// # Errors
    /// A dat read or decode failure; nothing is kept then.
    pub fn try_build<D: DatSource>(
        &mut self,
        dats: &D,
        landblock_id: u32,
    ) -> Result<Option<Arc<DungeonFloorplan>>, DatError> {
        let landblock = landblock_id & 0xFFFF_0000;
        if let Some(kept) = self.plans_by_landblock.get(&landblock) {
            return Ok(kept.clone());
        }
        let placed = self.read_cells(dats, landblock)?;
        let plan = build(landblock, &placed).map(Arc::new);
        self.plans_by_landblock.insert(landblock, plan.clone());
        Ok(plan)
    }

    /// The landblock's cells placed, each with its structure's geometry and
    /// its own doorways opened. A structure seen before is not read again.
    fn read_cells<D: DatSource>(
        &mut self,
        dats: &D,
        landblock: u32,
    ) -> Result<Vec<PlacedCell>, DatError> {
        let mut placed = Vec::new();
        let Some(info) = dats.object::<LandBlockInfo>(landblock | 0xFFFE)? else {
            return Ok(placed);
        };
        if info.num_cells == 0 {
            return Ok(placed);
        }
        let first_cell = landblock | 0x0100;
        let mut pending: Vec<(u32, Arc<EnvCell>, u32)> = Vec::new();
        for offset in 0..info.num_cells {
            let cell_id = first_cell + offset;
            let Some(env_cell) = dats.object::<EnvCell>(cell_id)? else {
                continue;
            };
            if env_cell.environment_id == 0 {
                continue;
            }
            let key = structure_key(env_cell.environment_id, env_cell.cell_structure);
            if let Entry::Vacant(slot) = self.geometry_by_structure.entry(key) {
                let environment =
                    dats.object::<Environment>(0x0D00_0000 | u32::from(env_cell.environment_id))?;
                let geometry = environment
                    .as_ref()
                    .and_then(|e| e.cell(u32::from(env_cell.cell_structure)))
                    .map(|structure| Arc::new(CellGeometry::from_structure(structure)));
                slot.insert(geometry);
            }
            pending.push((cell_id, env_cell, key));
        }
        for (cell_id, env_cell, key) in pending {
            let Some(Some(geometry)) = self.geometry_by_structure.get(&key) else {
                continue;
            };
            // A doorway is a polygon the structure lists as a portal, or one
            // this cell's own portal list points at; both are openings.
            let mut openings = geometry.portal_polygon_ids.clone();
            for portal in &env_cell.cell_portals {
                openings.insert(portal.polygon_id);
            }
            let geometry = if openings == geometry.portal_polygon_ids {
                Arc::clone(geometry)
            } else {
                Arc::new(geometry.with_openings(openings))
            };
            let neighbours = env_cell
                .cell_portals
                .iter()
                .map(|p| u32::from(p.other_cell_id))
                .collect();
            let frame = env_cell.position;
            let o = frame.orientation;
            placed.push(PlacedCell {
                cell_id,
                origin: Vec3::new(frame.origin.x, frame.origin.y, frame.origin.z),
                orientation: Quat::from_xyzw(o.x, o.y, o.z, o.w),
                geometry,
                neighbours,
            });
        }
        Ok(placed)
    }
}

fn structure_key(environment_id: u16, cell_structure: u16) -> u32 {
    (u32::from(environment_id) << 16) | u32::from(cell_structure)
}

/// A band key with a total order, for the sorted band map.
#[derive(Debug, Clone, Copy, PartialEq)]
struct BandKey(f32);

impl Eq for BandKey {}

impl PartialOrd for BandKey {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for BandKey {
    fn cmp(&self, other: &Self) -> Ordering {
        self.0.total_cmp(&other.0)
    }
}

#[derive(Debug, Default)]
struct LayerBuilder {
    floors: Vec<Vec<Vec2>>,
    walls: Vec<WallSpan>,
}

/// A wall line before merging: its direction, its sideways offset from the
/// origin, and its extent along the direction.
#[derive(Debug, Clone, Copy)]
struct WallSpan {
    along: Vec2,
    offset: f32,
    minimum: f32,
    maximum: f32,
}

#[derive(Debug)]
struct PlacedCell {
    cell_id: u32,
    origin: Vec3,
    orientation: Quat,
    geometry: Arc<CellGeometry>,
    /// The low halves of the cells this cell's doorways open onto.
    neighbours: Vec<u32>,
}

/// A cell after flattening, before its band is settled.
#[derive(Debug)]
struct FlattenedCell {
    cell_id: u32,
    neighbours: Vec<u32>,
    center: Vec3,
    /// The band of the cell's floor, weighted by vertex; `None` without a
    /// floor.
    floor_band: Option<f32>,
    /// The band the cell's origin is in.
    origin_band: f32,
    bounds: FloorplanBounds,
    floor_z: Option<f32>,
    walls: Vec<WallSpan>,
}

/// One polygon of a structure, in the structure's own frame.
#[derive(Debug, Clone)]
struct PolygonGeometry {
    polygon_id: u16,
    vertices: Vec<Vec3>,
    winding_normal: Vec3,
    mean_vertex_normal: Vec3,
    is_portal: bool,
}

/// A cell structure's polygons, resolved to points and normals once.
#[derive(Debug)]
struct CellGeometry {
    polygons: Vec<PolygonGeometry>,
    portal_polygon_ids: HashSet<u16>,
}

impl CellGeometry {
    fn from_structure(structure: &CellStruct) -> Self {
        let portals: HashSet<u16> = structure.portals.iter().copied().collect();
        let vertices: HashMap<u16, &ac_dat::objects::Vertex> = structure
            .vertex_array
            .vertices
            .iter()
            .map(|(id, v)| (*id, v))
            .collect();
        let mut polygons = Vec::with_capacity(structure.polygons.len());
        for (polygon_id, polygon) in &structure.polygons {
            if polygon.vertex_ids.len() < 3 {
                continue;
            }
            let mut points = Vec::with_capacity(polygon.vertex_ids.len());
            let mut shading = Vec3::ZERO;
            let mut complete = true;
            for id in &polygon.vertex_ids {
                let Some(vertex) = vertices.get(id) else {
                    complete = false;
                    break;
                };
                points.push(Vec3::new(vertex.origin.x, vertex.origin.y, vertex.origin.z));
                shading += Vec3::new(vertex.normal.x, vertex.normal.y, vertex.normal.z);
            }
            if !complete {
                continue;
            }
            let winding = winding_normal(&points);
            if winding == Vec3::ZERO {
                continue;
            }
            let shading_length = shading.length();
            polygons.push(PolygonGeometry {
                polygon_id: *polygon_id,
                vertices: points,
                winding_normal: winding,
                mean_vertex_normal: if shading_length > 1e-6 {
                    shading / shading_length
                } else {
                    Vec3::ZERO
                },
                is_portal: portals.contains(polygon_id),
            });
        }
        Self {
            polygons,
            portal_polygon_ids: portals,
        }
    }

    fn with_openings(&self, openings: HashSet<u16>) -> Self {
        let polygons = self
            .polygons
            .iter()
            .map(|p| PolygonGeometry {
                is_portal: openings.contains(&p.polygon_id),
                ..p.clone()
            })
            .collect();
        Self {
            polygons,
            portal_polygon_ids: openings,
        }
    }
}

/// The polygon's normal from its vertex order, summed over every edge so a
/// bent or many-sided polygon still gives its overall facing.
fn winding_normal(points: &[Vec3]) -> Vec3 {
    let mut normal = Vec3::ZERO;
    for (index, current) in points.iter().enumerate() {
        let next = points[(index + 1) % points.len()];
        normal.x += (current.y - next.y) * (current.z + next.z);
        normal.y += (current.z - next.z) * (current.x + next.x);
        normal.z += (current.x - next.x) * (current.y + next.y);
    }
    let length = normal.length();
    if length < 1e-9 {
        Vec3::ZERO
    } else {
        normal / length
    }
}

fn build(landblock: u32, placed: &[PlacedCell]) -> Option<DungeonFloorplan> {
    if placed.is_empty() {
        return None;
    }
    let mut counts = FloorplanCounts::default();
    let mut bounds = FloorplanBounds::EMPTY;
    let mut layers: BTreeMap<BandKey, LayerBuilder> = BTreeMap::new();

    // Every cell is flattened first: its floor goes straight into the band
    // of its own height, since a floor is at the height it is at whatever
    // the cell's origin says; its walls wait until the cell's band is known.
    let mut flattened = Vec::with_capacity(placed.len());
    for cell in placed {
        let transform = Mat4::from_rotation_translation(cell.orientation, cell.origin);
        let mut cell_bounds = FloorplanBounds::EMPTY;
        let mut walls = Vec::new();
        let mut floor_height = 0.0_f32;
        let mut floor_vertices = 0_usize;
        let mut lowest_floor: Option<f32> = None;
        for polygon in &cell.geometry.polygons {
            counts.polygons += 1;
            let world: Vec<Vec3> = polygon
                .vertices
                .iter()
                .map(|v| transform.transform_point3(*v))
                .collect();
            for point in &world {
                cell_bounds = cell_bounds.including(*point);
            }
            if polygon.is_portal {
                counts.portals += 1;
                continue;
            }
            let facing = cell.orientation * polygon.winding_normal;
            let shading = cell.orientation * polygon.mean_vertex_normal;
            let up = facing.z.abs();
            if up >= LEVEL_MINIMUM_UP {
                // The authored vertex normals say which way a level face is
                // lit, which is which way the room is; the winding is the
                // fallback when they say nothing.
                let faces_up = if shading.z.abs() > 0.001 {
                    shading.z
                } else {
                    facing.z
                };
                if faces_up > 0.0 {
                    counts.floors += 1;
                    let height = world.iter().map(|v| v.z).sum::<f32>() / world.len() as f32;
                    layers
                        .entry(BandKey(layer_key(height)))
                        .or_default()
                        .floors
                        .push(world.iter().map(|v| Vec2::new(v.x, v.y)).collect());
                    floor_height += height * world.len() as f32;
                    floor_vertices += world.len();
                    lowest_floor = Some(lowest_floor.map_or(height, |low| low.min(height)));
                } else {
                    counts.ceilings += 1;
                }
            } else if up <= STANDING_MAXIMUM_UP {
                if let Some(span) = project_wall(&world, facing) {
                    counts.walls += 1;
                    walls.push(span);
                }
            } else {
                counts.slopes += 1;
            }
        }
        if cell_bounds.is_empty() {
            continue;
        }
        bounds = bounds.including(cell_bounds.min).including(cell_bounds.max);
        flattened.push(FlattenedCell {
            cell_id: cell.cell_id,
            neighbours: cell.neighbours.clone(),
            center: (cell_bounds.min + cell_bounds.max) * 0.5,
            floor_band: (floor_vertices != 0)
                .then(|| layer_key(floor_height / floor_vertices as f32)),
            origin_band: layer_key(cell.origin.z),
            bounds: cell_bounds,
            floor_z: lowest_floor,
            walls,
        });
    }

    // A tall room is authored as a cell on top of a cell: the upper one has
    // walls and a ceiling but no floor, and its doorway list names the cell
    // it opens onto below. Its walls belong in that cell's band. A cell with
    // a floor is drawn in its floor's band; a cell with no floor and no way
    // down stays in the band its origin is in.
    let mut bands: HashMap<u32, f32> = HashMap::with_capacity(flattened.len());
    for cell in &flattened {
        if let Some(band) = cell.floor_band {
            bands.insert(cell.cell_id, band);
        }
    }
    let mut unresolved: Vec<&FlattenedCell> = flattened
        .iter()
        .filter(|c| c.floor_band.is_none())
        .collect();
    // A cell may open onto another floorless cell, so folding goes round
    // until nothing more folds.
    let mut folded = true;
    while folded && !unresolved.is_empty() {
        folded = false;
        for index in (0..unresolved.len()).rev() {
            let cell = unresolved[index];
            let mut below: Option<f32> = None;
            for neighbour in &cell.neighbours {
                if let Some(&band) = bands.get(&(landblock | neighbour))
                    && band < cell.origin_band
                    && below.is_none_or(|b| band > b)
                {
                    below = Some(band);
                }
            }
            if let Some(found) = below {
                bands.insert(cell.cell_id, found);
                unresolved.remove(index);
                folded = true;
            }
        }
    }
    for cell in &unresolved {
        bands.insert(cell.cell_id, cell.origin_band);
    }

    let mut cells = Vec::with_capacity(flattened.len());
    for cell in &flattened {
        let band = bands[&cell.cell_id];
        layers
            .entry(BandKey(band))
            .or_default()
            .walls
            .extend(cell.walls.iter().copied());
        cells.push(DungeonFloorplanCell {
            cell_id: cell.cell_id,
            center: cell.center,
            layer_z: band,
            bounds: cell.bounds,
            floor_z: cell.floor_z,
        });
    }

    let mut built = Vec::with_capacity(layers.len());
    for (BandKey(z), layer) in layers {
        let walls = merge_walls(&layer.walls);
        counts.walls_after_merge += u32::try_from(walls.len()).unwrap_or(u32::MAX);
        built.push(DungeonFloorplanLayer {
            z,
            floors: layer.floors,
            walls,
        });
    }

    Some(DungeonFloorplan {
        landblock_id: landblock,
        layers: built,
        cells,
        bounds,
        counts,
    })
}

/// A standing polygon seen from above is a line: the polygon's plane meets
/// the ground along the direction across its normal, and the vertices reach
/// some way along it either side of their middle.
fn project_wall(world: &[Vec3], facing: Vec3) -> Option<WallSpan> {
    let across = Vec2::new(facing.x, facing.y);
    let across_length = across.length();
    if across_length < 1e-6 {
        return None;
    }
    let across = across / across_length;
    let along = canonical(Vec2::new(-across.y, across.x));
    // With the direction made canonical the sideways normal is fixed too, so
    // two walls facing opposite ways along one line share a key.
    let sideways = Vec2::new(-along.y, along.x);
    let mut offset = 0.0_f32;
    let mut minimum = f32::MAX;
    let mut maximum = f32::MIN;
    for vertex in world {
        let flat = Vec2::new(vertex.x, vertex.y);
        offset += flat.dot(sideways);
        let t = flat.dot(along);
        minimum = minimum.min(t);
        maximum = maximum.max(t);
    }
    offset /= world.len() as f32;
    if maximum - minimum < MINIMUM_WALL_METRES {
        return None;
    }
    Some(WallSpan {
        along,
        offset,
        minimum,
        maximum,
    })
}

/// A direction and its opposite are the same line; pick the one pointing
/// right, or up when it points straight up or down.
fn canonical(direction: Vec2) -> Vec2 {
    if direction.x > 1e-6 || (direction.x.abs() <= 1e-6 && direction.y > 0.0) {
        direction
    } else {
        -direction
    }
}

/// Joins spans that lie on the same line and touch or overlap: every
/// polygon a wall is authored in becomes one line per straight run.
fn merge_walls(spans: &[WallSpan]) -> Vec<FloorplanWall> {
    if spans.is_empty() {
        return Vec::new();
    }
    let angle_buckets = (180.0 / MERGE_ANGLE_DEGREES) as i32;
    let mut index_of: HashMap<(i32, i32), usize> = HashMap::new();
    let mut groups: Vec<Vec<WallSpan>> = Vec::new();
    for span in spans {
        let mut degrees = span.along.y.atan2(span.along.x) * (180.0 / std::f32::consts::PI);
        if degrees < 0.0 {
            degrees += 180.0;
        }
        let angle_key = ((degrees / MERGE_ANGLE_DEGREES).round_ties_even() as i32) % angle_buckets;
        let offset_key = (span.offset / MERGE_OFFSET_METRES).round_ties_even() as i32;
        let index = *index_of.entry((angle_key, offset_key)).or_insert_with(|| {
            groups.push(Vec::new());
            groups.len() - 1
        });
        groups[index].push(*span);
    }
    let mut walls = Vec::new();
    for mut group in groups {
        group.sort_by(|a, b| a.minimum.total_cmp(&b.minimum));
        let along = group[0].along;
        let offset = group.iter().map(|s| s.offset).sum::<f32>() / group.len() as f32;
        let sideways = Vec2::new(-along.y, along.x);
        let origin = sideways * offset;
        let mut run_start = group[0].minimum;
        let mut run_end = group[0].maximum;
        for span in &group[1..] {
            if span.minimum <= run_end + MERGE_GAP_METRES {
                run_end = run_end.max(span.maximum);
                continue;
            }
            walls.push(FloorplanWall {
                start: origin + along * run_start,
                end: origin + along * run_end,
            });
            run_start = span.minimum;
            run_end = span.maximum;
        }
        walls.push(FloorplanWall {
            start: origin + along * run_start,
            end: origin + along * run_end,
        });
    }
    walls
}

#[cfg(test)]
pub(crate) mod tests;
