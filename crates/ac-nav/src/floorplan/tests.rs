//! Ported from OpenAC `tests/AcDream.Runtime.Tests/Maps/DungeonFloorplanBuilderTests.cs`
//! over its fixtures `TwoRoomLandblock.cs`, `StackedRoomLandblock.cs` and
//! `ChainedShaftLandblock.cs` (the dat lock half has no counterpart: the
//! reader is shared without one).

use std::any::Any;
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering as AtomicOrdering};

use ac_dat::objects::{
    BspTree, BspTreeKind, CellPortal, CellStruct, EnvCell, EnvCellFlags, Environment,
    LandBlockInfo, Polygon, SidesType, Vertex, VertexArray,
};
use ac_dat::types::{Frame, Quaternion, Vector3};
use ac_dat::{DatError, DatObject};

use super::*;

/// Dat objects by file id; counts every read.
#[derive(Default)]
pub(crate) struct FakeDats {
    objects: HashMap<u32, Arc<dyn Any + Send + Sync>>,
    reads: AtomicUsize,
}

impl FakeDats {
    fn add<T: Any + Send + Sync>(&mut self, id: u32, value: T) {
        self.objects.insert(id, Arc::new(value));
    }

    pub(crate) fn reads(&self) -> usize {
        self.reads.load(AtomicOrdering::Relaxed)
    }
}

impl DatSource for FakeDats {
    fn object<T: DatObject + Send + Sync + 'static>(
        &self,
        id: u32,
    ) -> Result<Option<Arc<T>>, DatError> {
        self.reads.fetch_add(1, AtomicOrdering::Relaxed);
        Ok(self
            .objects
            .get(&id)
            .and_then(|o| Arc::clone(o).downcast::<T>().ok()))
    }
}

const FLOOR: u16 = 0;
const CEILING: u16 = 1;
const SOUTH_WALL: u16 = 2;
const NORTH_WALL: u16 = 3;
const WEST_WALL: u16 = 4;
const EAST_WALL: u16 = 5;
const NORTH_WALL_EAST: u16 = 6;

fn v(x: f32, y: f32, z: f32) -> Vector3 {
    Vector3 { x, y, z }
}

fn landblock_info(id: u32, num_cells: u32) -> LandBlockInfo {
    LandBlockInfo {
        id,
        num_cells,
        objects: Vec::new(),
        buildings: Vec::new(),
        restriction_table: Vec::new(),
    }
}

fn polygon(ids: &[u16]) -> Polygon {
    Polygon {
        sides_type: SidesType(SidesType::CLOCKWISE),
        vertex_ids: ids.to_vec(),
        ..Polygon::default()
    }
}

fn empty_tree(kind: BspTreeKind) -> BspTree {
    BspTree {
        kind,
        nodes: Vec::new(),
    }
}

/// A 10 by 8 metre box from `bottom` to `top`, vertex normals up at the
/// bottom and down at the top; `extra` corners follow the eight.
fn box_struct(
    bottom: f32,
    top: f32,
    extra: &[Vector3],
    polygons: Vec<(u16, Polygon)>,
) -> CellStruct {
    let mut corners = vec![
        v(0.0, 0.0, bottom),
        v(10.0, 0.0, bottom),
        v(10.0, 8.0, bottom),
        v(0.0, 8.0, bottom),
        v(0.0, 0.0, top),
        v(10.0, 0.0, top),
        v(10.0, 8.0, top),
        v(0.0, 8.0, top),
    ];
    corners.extend_from_slice(extra);
    let vertices = corners
        .iter()
        .enumerate()
        .map(|(index, c)| {
            (
                u16::try_from(index).unwrap(),
                Vertex {
                    origin: *c,
                    normal: if c.z == bottom {
                        v(0.0, 0.0, 1.0)
                    } else {
                        v(0.0, 0.0, -1.0)
                    },
                    uvs: Vec::new(),
                },
            )
        })
        .collect();
    CellStruct {
        vertex_array: VertexArray {
            vertex_type: VertexArray::SW_VERTEX,
            vertices,
        },
        polygons,
        portals: Vec::new(),
        cell_bsp: empty_tree(BspTreeKind::Cell),
        physics_polygons: Vec::new(),
        physics_bsp: empty_tree(BspTreeKind::Physics),
        drawing_bsp: None,
    }
}

fn env_cell(
    id: u32,
    environment_id: u16,
    structure: u16,
    origin: Vector3,
    orientation: Quaternion,
    portals: &[(u16, u16)],
) -> EnvCell {
    EnvCell {
        id,
        flags: EnvCellFlags::default(),
        cell_id: id,
        surfaces: Vec::new(),
        environment_id,
        cell_structure: structure,
        position: Frame {
            origin,
            orientation,
        },
        cell_portals: portals
            .iter()
            .map(|&(polygon_id, other_cell_id)| CellPortal {
                flags: 0,
                polygon_id,
                other_cell_id,
                other_portal_id: 0,
            })
            .collect(),
        visible_cells: Vec::new(),
        static_objects: Vec::new(),
        restriction_obj: 0,
    }
}

const IDENTITY: Quaternion = Quaternion {
    w: 1.0,
    x: 0.0,
    y: 0.0,
    z: 0.0,
};

// -- TwoRoomLandblock ----------------------------------------------------------

pub(crate) const TWO_ROOMS: u32 = 0x0123_0000;
pub(crate) const TURNED_ROOM: u32 = TWO_ROOMS | 0x0100;
pub(crate) const UPSTAIRS_ROOM: u32 = TWO_ROOMS | 0x0101;
pub(crate) const CELL_SEEING_OUTSIDE: u32 = TWO_ROOMS | 0x0102;
pub(crate) const EMPTY_LANDBLOCK: u32 = 0x89AB_0000;

/// One landblock of two box rooms: one turned a quarter turn with a doorway
/// in its east wall onto the other, which it sees; the other a storey
/// higher with its north wall in two pieces and a doorway outdoors through a
/// polygon its structure lacks; a third cell has no geometry; a second
/// landblock has no cells.
pub(crate) fn two_rooms() -> FakeDats {
    const ENV: u16 = 0x0123;
    let walls = |split: bool| {
        let mut p = vec![
            (FLOOR, polygon(&[0, 1, 2, 3])),
            (CEILING, polygon(&[7, 6, 5, 4])),
            (SOUTH_WALL, polygon(&[0, 4, 5, 1])),
            (WEST_WALL, polygon(&[0, 3, 7, 4])),
            (EAST_WALL, polygon(&[1, 5, 6, 2])),
        ];
        if split {
            p.push((NORTH_WALL, polygon(&[3, 8, 9, 7])));
            p.push((NORTH_WALL_EAST, polygon(&[8, 2, 6, 9])));
        } else {
            p.push((NORTH_WALL, polygon(&[3, 2, 6, 7])));
        }
        p
    };
    let extra = [v(5.0, 8.0, 0.0), v(5.0, 8.0, 3.0)];
    let mut with_door = box_struct(0.0, 3.0, &extra, walls(false));
    with_door.portals.push(EAST_WALL);
    let split = box_struct(0.0, 3.0, &extra, walls(true));
    let mut dats = FakeDats::default();
    dats.add(TWO_ROOMS | 0xFFFE, landblock_info(TWO_ROOMS | 0xFFFE, 3));
    dats.add(
        EMPTY_LANDBLOCK | 0xFFFE,
        landblock_info(EMPTY_LANDBLOCK | 0xFFFE, 0),
    );
    dats.add(
        0x0D00_0000 | u32::from(ENV),
        Environment {
            id: 0x0D00_0000 | u32::from(ENV),
            cells: vec![(1, with_door), (2, split)],
        },
    );
    let quarter = std::f32::consts::FRAC_PI_4;
    let mut turned = env_cell(
        TURNED_ROOM,
        ENV,
        1,
        v(20.0, 30.0, 0.5),
        Quaternion {
            w: quarter.cos(),
            x: 0.0,
            y: 0.0,
            z: quarter.sin(),
        },
        &[(EAST_WALL, 0x0101)],
    );
    turned.visible_cells = vec![0x0101];
    dats.add(TURNED_ROOM, turned);
    let mut upstairs = env_cell(UPSTAIRS_ROOM, ENV, 2, v(40.0, 30.0, 7.2), IDENTITY, &[]);
    upstairs.cell_portals.push(CellPortal {
        flags: CellPortal::NO_OTHER_CELL | CellPortal::EXACT_MATCH | CellPortal::PORTAL_SIDE,
        polygon_id: 99,
        other_cell_id: 0xFFFF,
        other_portal_id: 0,
    });
    // Another way out, named only by the cell it does not open onto.
    upstairs.cell_portals.push(CellPortal {
        flags: 0,
        polygon_id: 98,
        other_cell_id: 0xFFFF,
        other_portal_id: 0,
    });
    upstairs.visible_cells = vec![0x0100, 0x0102];
    dats.add(UPSTAIRS_ROOM, upstairs);
    let mut outside = env_cell(CELL_SEEING_OUTSIDE, 0, 0, v(0.0, 0.0, 0.0), IDENTITY, &[]);
    outside.flags = EnvCellFlags(EnvCellFlags::SEEN_OUTSIDE);
    dats.add(CELL_SEEING_OUTSIDE, outside);
    dats
}

fn near(actual: Vec2, expected: Vec2) -> bool {
    actual.distance(expected) < 0.01
}

fn matches(wall: &FloorplanWall, a: Vec2, b: Vec2) -> bool {
    (near(wall.start, a) && near(wall.end, b)) || (near(wall.start, b) && near(wall.end, a))
}

fn lengths(walls: &[FloorplanWall]) -> Vec<f32> {
    let mut l: Vec<f32> = walls.iter().map(FloorplanWall::length).collect();
    l.sort_by(f32::total_cmp);
    l
}

fn assert_close(expected: &[f32], actual: &[f32]) {
    assert_eq!(expected.len(), actual.len(), "{actual:?}");
    for (e, a) in expected.iter().zip(actual) {
        assert!(
            (e - a).abs() < 0.01,
            "expected {expected:?}, got {actual:?}"
        );
    }
}

fn assert_vec3(expected: Vec3, actual: Vec3) {
    assert!(
        expected.distance(actual) < 0.01,
        "expected {expected}, got {actual}"
    );
}

#[test]
fn two_rooms_become_two_layers_of_floors_and_walls() {
    let dats = two_rooms();
    let mut builder = DungeonFloorplanBuilder::new();
    let plan = builder.try_build(&dats, TURNED_ROOM).unwrap().unwrap();
    assert_eq!(plan.landblock_id, TWO_ROOMS);
    assert_eq!(
        plan.layers.iter().map(|l| l.z).collect::<Vec<_>>(),
        [0.0, 6.0]
    );

    let ground = &plan.layers[0];
    assert_eq!(ground.floors.len(), 1);
    assert_eq!(ground.floors[0].len(), 4);
    // Six faces: the ceiling faces down and the east wall is the doorway.
    assert_eq!(ground.walls.len(), 3);
    assert_close(&[8.0, 10.0, 10.0], &lengths(&ground.walls));
    // The south wall, local y = 0 from x 0 to 10, turned a quarter turn
    // about (20, 30): local x runs along world +y.
    assert!(
        ground
            .walls
            .iter()
            .any(|w| matches(w, Vec2::new(20.0, 30.0), Vec2::new(20.0, 40.0)))
    );

    let upstairs = &plan.layers[1];
    // Five wall polygons, four wall lines: the split north wall merged.
    assert_eq!(upstairs.walls.len(), 4);
    assert_close(&[8.0, 8.0, 10.0, 10.0], &lengths(&upstairs.walls));
    assert!(upstairs.walls.iter().any(|w| matches(
        w,
        Vec2::new(40.0, 38.0),
        Vec2::new(50.0, 38.0)
    )));

    assert_eq!(
        plan.cells.iter().map(|c| c.cell_id).collect::<Vec<_>>(),
        [TURNED_ROOM, UPSTAIRS_ROOM]
    );
    assert_eq!(plan.cells[0].layer_z, 0.0);
    assert_eq!(plan.cells[1].layer_z, 6.0);
    assert_vec3(Vec3::new(16.0, 35.0, 2.0), plan.cells[0].center);
    assert_vec3(Vec3::new(12.0, 30.0, 0.5), plan.bounds.min);
    assert_vec3(Vec3::new(50.0, 40.0, 10.2), plan.bounds.max);

    assert_eq!(plan.counts.floors, 2);
    assert_eq!(plan.counts.ceilings, 2);
    assert_eq!(plan.counts.portals, 1);
    assert_eq!(plan.counts.walls, 8);
    assert_eq!(plan.counts.walls_after_merge, 7);
}

#[test]
fn the_plan_and_the_geometry_are_built_once() {
    let dats = two_rooms();
    let mut builder = DungeonFloorplanBuilder::new();
    let first = builder.try_build(&dats, TWO_ROOMS).unwrap().unwrap();
    let reads_for_one = dats.reads();
    let again = builder
        .try_build(&dats, TWO_ROOMS | 0xFFFF)
        .unwrap()
        .unwrap();
    assert!(Arc::ptr_eq(&first, &again));
    assert_eq!(dats.reads(), reads_for_one);
    assert_eq!(builder.cached_geometry_count(), 2);
}

#[test]
fn a_landblock_without_cells_has_no_plan() {
    let dats = two_rooms();
    let mut builder = DungeonFloorplanBuilder::new();
    assert!(builder.try_build(&dats, 0x4567_0000).unwrap().is_none());
    assert!(builder.try_build(&dats, EMPTY_LANDBLOCK).unwrap().is_none());
}

// -- StackedRoomLandblock and ChainedShaftLandblock --------------------------------

/// A box with only the pieces asked for; an opening is a floor polygon that
/// is a portal.
fn piece_box(bottom: f32, top: f32, floor: bool, ceiling: bool, opening: bool) -> CellStruct {
    let mut polygons = Vec::new();
    if floor || opening {
        polygons.push((FLOOR, polygon(&[0, 1, 2, 3])));
    }
    if ceiling {
        polygons.push((CEILING, polygon(&[7, 6, 5, 4])));
    }
    polygons.extend([
        (SOUTH_WALL, polygon(&[0, 4, 5, 1])),
        (WEST_WALL, polygon(&[0, 3, 7, 4])),
        (EAST_WALL, polygon(&[1, 5, 6, 2])),
        (NORTH_WALL, polygon(&[3, 2, 6, 7])),
    ]);
    let mut cell = box_struct(bottom, top, &[], polygons);
    if opening {
        cell.portals.push(FLOOR);
    }
    cell
}

const STACKED: u32 = 0x0234_0000;

/// A tall room as the real ones are authored: a lower cell with a floor and
/// walls whose origin sits a storey above its floor, an upper cell with
/// walls and a ceiling but no floor that opens onto the lower one, and a
/// lonely cell with walls and no floor and no doorway.
fn stacked_rooms() -> FakeDats {
    const ENV: u16 = 0x0234;
    let mut dats = FakeDats::default();
    dats.add(STACKED | 0xFFFE, landblock_info(STACKED | 0xFFFE, 3));
    dats.add(
        0x0D00_0000 | u32::from(ENV),
        Environment {
            id: 0x0D00_0000 | u32::from(ENV),
            cells: vec![
                (1, piece_box(-1.0, 5.0, true, false, false)),
                (2, piece_box(0.0, 6.0, false, true, true)),
                (3, piece_box(0.0, 3.0, false, true, false)),
            ],
        },
    );
    let lower = STACKED | 0x0100;
    let upper = STACKED | 0x0101;
    let lonely = STACKED | 0x0102;
    dats.add(
        lower,
        env_cell(
            lower,
            ENV,
            1,
            v(20.0, 30.0, 3.5),
            IDENTITY,
            &[(CEILING, 0x0101)],
        ),
    );
    dats.add(
        upper,
        env_cell(
            upper,
            ENV,
            2,
            v(20.0, 30.0, 8.5),
            IDENTITY,
            &[(FLOOR, 0x0100)],
        ),
    );
    dats.add(
        lonely,
        env_cell(lonely, ENV, 3, v(60.0, 30.0, 15.0), IDENTITY, &[]),
    );
    dats
}

#[test]
fn a_tall_rooms_upper_cell_folds_into_the_band_of_the_cell_below() {
    let dats = stacked_rooms();
    let plan = DungeonFloorplanBuilder::new()
        .try_build(&dats, STACKED)
        .unwrap()
        .unwrap();
    // The lower cell's origin is at z = 3.5, a storey up, but its floor is
    // at z = 2.5; the upper cell's origin is at 8.5 and it has no floor; the
    // lonely cell is at 15 and has no floor and no doorway.
    assert_eq!(
        plan.layers.iter().map(|l| l.z).collect::<Vec<_>>(),
        [0.0, 18.0]
    );
    let ground = &plan.layers[0];
    assert_eq!(ground.floors.len(), 1);
    // Eight wall polygons, four per cell, one on top of the other: four lines.
    assert_eq!(ground.walls.len(), 4);
    assert!(
        ground
            .walls
            .iter()
            .any(|w| matches(w, Vec2::new(20.0, 30.0), Vec2::new(30.0, 30.0)))
    );
    let lonely = &plan.layers[1];
    assert!(lonely.floors.is_empty());
    assert_eq!(lonely.walls.len(), 4);
    assert_eq!(
        plan.cells.iter().map(|c| c.layer_z).collect::<Vec<_>>(),
        [0.0, 0.0, 18.0]
    );
    assert_eq!(plan.counts.floors, 1);
    assert_eq!(plan.counts.ceilings, 2);
    assert_eq!(plan.counts.portals, 1);
    assert_eq!(plan.counts.walls, 12);
    assert_eq!(plan.counts.walls_after_merge, 8);
}

const SHAFT: u32 = 0x0345_0000;

/// One shaft three cells tall: a bottom cell with a floor, a middle cell
/// with no floor that opens onto the bottom, a top cell with no floor that
/// opens only onto the middle, numbered so one pass meets the top first.
fn chained_shaft() -> FakeDats {
    const ENV: u16 = 0x0345;
    let mut dats = FakeDats::default();
    dats.add(SHAFT | 0xFFFE, landblock_info(SHAFT | 0xFFFE, 3));
    dats.add(
        0x0D00_0000 | u32::from(ENV),
        Environment {
            id: 0x0D00_0000 | u32::from(ENV),
            cells: vec![
                (1, piece_box(-1.0, 5.0, true, false, false)),
                (2, piece_box(0.0, 6.0, false, false, true)),
                (3, piece_box(0.0, 6.0, false, true, true)),
            ],
        },
    );
    let (bottom, middle, top) = (SHAFT | 0x0100, SHAFT | 0x0101, SHAFT | 0x0102);
    dats.add(
        bottom,
        env_cell(
            bottom,
            ENV,
            1,
            v(20.0, 30.0, 3.5),
            IDENTITY,
            &[(CEILING, 0x0101)],
        ),
    );
    dats.add(
        middle,
        env_cell(
            middle,
            ENV,
            2,
            v(20.0, 30.0, 8.5),
            IDENTITY,
            &[(FLOOR, 0x0100), (CEILING, 0x0102)],
        ),
    );
    dats.add(
        top,
        env_cell(
            top,
            ENV,
            3,
            v(20.0, 30.0, 14.5),
            IDENTITY,
            &[(FLOOR, 0x0101)],
        ),
    );
    dats
}

#[test]
fn a_floorless_cell_over_another_floorless_cell_folds_all_the_way_down() {
    let dats = chained_shaft();
    let plan = DungeonFloorplanBuilder::new()
        .try_build(&dats, SHAFT)
        .unwrap()
        .unwrap();
    assert_eq!(plan.layers.len(), 1);
    let ground = &plan.layers[0];
    assert_eq!(ground.z, 0.0);
    assert_eq!(ground.floors.len(), 1);
    // Twelve wall polygons, four per cell, one on top of another: four lines.
    assert_eq!(ground.walls.len(), 4);
    assert_eq!(
        plan.cells.iter().map(|c| c.cell_id).collect::<Vec<_>>(),
        [SHAFT | 0x0100, SHAFT | 0x0101, SHAFT | 0x0102]
    );
    assert_eq!(
        plan.cells.iter().map(|c| c.layer_z).collect::<Vec<_>>(),
        [0.0, 0.0, 0.0]
    );
    assert_eq!(plan.counts.floors, 1);
    assert_eq!(plan.counts.ceilings, 1);
    assert_eq!(plan.counts.portals, 2);
    assert_eq!(plan.counts.walls, 12);
    assert_eq!(plan.counts.walls_after_merge, 4);
}

#[test]
fn a_band_key_puts_a_little_below_a_band_into_it() {
    // OpenAC `DungeonFloorplanBuilder.LayerKey`: floor((z + 3) / 6) * 6.
    assert_eq!(layer_key(2.5), 0.0);
    assert_eq!(layer_key(3.5), 6.0);
    assert_eq!(layer_key(-3.0), 0.0);
    assert_eq!(layer_key(-3.1), -6.0);
    assert_eq!(layer_key(15.0), 18.0);
}
