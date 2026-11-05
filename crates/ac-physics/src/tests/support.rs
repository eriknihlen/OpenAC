#![allow(dead_code)] // Shared by test modules; not every helper is used by each.
//! Helpers shared by the ported tests: polygon and tree builders, engines
//! over synthetic geometry, and direct BSP queries through a transition.

use std::collections::HashMap;

use glam::{Quat, Vec3};

use crate::bsp::{PhysicsBsp, PhysicsBspNode};
use crate::bsp_collisions::{BspFrame, LocalSpheres};
use crate::cell_array::CellArray;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::PhysicsEngine;
use crate::geometry::{Plane, Sphere};
use crate::polygon::ResolvedPolygon;
use crate::shadow::ShadowCollisionType;
use crate::shadow_registry::{RegistrationSite, SingleShape};
use crate::state::TransitionState;
use crate::terrain::TerrainSurface;
use crate::transition::Transition;
use crate::transition::bsp_bridge::BspContext;
use crate::world::{CellPhysics, CollisionWorld, GfxObjPhysics, Placement};

/// A `Cell` the engine's hooks can capture: they must be `Send`.
#[derive(Debug, Default)]
pub struct SyncCell<T>(std::sync::Mutex<T>);

impl<T: Copy> SyncCell<T> {
    pub fn new(value: T) -> Self {
        Self(std::sync::Mutex::new(value))
    }

    pub fn get(&self) -> T {
        *self.0.lock().expect("test cell")
    }

    pub fn set(&self, value: T) {
        *self.0.lock().expect("test cell") = value;
    }
}

/// A `RefCell` the engine's hooks can capture: they must be `Send`.
#[derive(Debug, Default)]
pub struct SyncRefCell<T>(std::sync::Mutex<T>);

impl<T> SyncRefCell<T> {
    pub fn new(value: T) -> Self {
        Self(std::sync::Mutex::new(value))
    }

    pub fn borrow(&self) -> std::sync::MutexGuard<'_, T> {
        self.0.lock().expect("test cell")
    }

    pub fn borrow_mut(&self) -> std::sync::MutexGuard<'_, T> {
        self.0.lock().expect("test cell")
    }
}

/// `a` and `b` within `eps`.
#[track_caller]
pub fn assert_near(expected: f32, actual: f32, eps: f32) {
    assert!(
        (expected - actual).abs() <= eps,
        "expected {expected} ± {eps}, got {actual}"
    );
}

/// Vectors within `eps` per axis.
#[track_caller]
pub fn assert_vec_near(expected: Vec3, actual: Vec3, eps: f32) {
    assert!(
        (expected - actual).abs().max_element() <= eps,
        "expected {expected} ± {eps}, got {actual}"
    );
}

/// A polygon whose plane has `normal` and passes through the vertices'
/// mean (OpenAC's fixture builders).
pub fn quad(id: u16, vertices: &[Vec3], normal: Vec3) -> ResolvedPolygon {
    let dot_sum: f32 = vertices.iter().map(|v| normal.dot(*v)).sum();
    let d = -(dot_sum / vertices.len() as f32);
    ResolvedPolygon::new(id, vertices.to_vec(), Plane::new(normal, d))
}

/// A horizontal floor (normal +z).
pub fn floor(id: u16, vertices: &[Vec3]) -> ResolvedPolygon {
    quad(id, vertices, Vec3::Z)
}

/// A tree of one leaf holding every polygon.
pub fn single_leaf(center: Vec3, radius: f32, polygons: Vec<ResolvedPolygon>) -> PhysicsBsp {
    let ids: Vec<u16> = polygons.iter().map(|p| p.id).collect();
    let map: HashMap<u16, ResolvedPolygon> = polygons.into_iter().map(|p| (p.id, p)).collect();
    PhysicsBsp::new(
        vec![PhysicsBspNode::Leaf {
            sphere: Sphere::new(center, radius),
            solid: false,
            polygons: ids,
        }],
        map,
    )
    .expect("a single leaf is a valid tree")
}

/// Flat terrain: every corner at `z`.
pub fn flat_terrain(z: f32) -> TerrainSurface {
    TerrainSurface::new(&[0u8; 81], &[z; 256], 0, 0, None)
}

/// Terrain from height-table indices with a linear table (index = metres).
pub fn terrain_from_heights(heights: &[u8; 81]) -> TerrainSurface {
    let table: [f32; 256] = std::array::from_fn(|i| i as f32);
    TerrainSurface::new(heights, &table, 0, 0, None)
}

/// A model collision with no visual bounds.
pub fn gfx_obj(id: u32, bsp: PhysicsBsp, bounding_radius: f32) -> GfxObjPhysics {
    GfxObjPhysics {
        id,
        bsp,
        bounding_sphere: Sphere::new(Vec3::ZERO, bounding_radius),
        visual_bounds: None,
    }
}

/// An engine with landblock `0xA9B4FFFF` (flat terrain at `terrain_z`)
/// added before a world holding `gfx` is attached, and the model
/// registered as a shadow object at `object_position` (OpenAC
/// `BSPStepUpTests.MakeTestEngine`).
pub fn engine_with_object(
    gfx: GfxObjPhysics,
    object_position: Vec3,
    terrain_z: f32,
) -> PhysicsEngine {
    const LANDBLOCK_ID: u32 = 0xA9B4_FFFF;
    let id = gfx.id;
    let radius = gfx.bounding_sphere.radius;
    let mut engine = PhysicsEngine::new();
    engine.add_landblock(
        LANDBLOCK_ID,
        flat_terrain(terrain_z),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    let mut world = CollisionWorld::new();
    world.insert_gfx_obj(gfx);
    engine.attach_world(world);
    register_single(
        &mut engine,
        id,
        id,
        object_position,
        Quat::IDENTITY,
        radius,
        LANDBLOCK_ID,
        ShadowCollisionType::Bsp,
        0.0,
        1.0,
        0,
        EntityCollisionFlags::default(),
        0,
    );
    engine
}

/// Registers one single-shape shadow object.
#[allow(clippy::too_many_arguments)]
pub fn register_single(
    engine: &mut PhysicsEngine,
    entity_id: u32,
    gfx_obj_id: u32,
    position: Vec3,
    rotation: Quat,
    radius: f32,
    landblock_id: u32,
    collision_type: ShadowCollisionType,
    cyl_height: f32,
    scale: f32,
    state: u32,
    flags: EntityCollisionFlags,
    seed_cell_id: u32,
) {
    register_shadow(
        engine,
        entity_id,
        gfx_obj_id,
        position,
        rotation,
        radius,
        landblock_id,
        collision_type,
        cyl_height,
        scale,
        state,
        flags,
        seed_cell_id,
        true,
    );
}

/// Registers one single-shape shadow object, static or dynamic (OpenAC
/// `ShadowObjects.Register` with every argument given).
#[allow(clippy::too_many_arguments)]
pub fn register_shadow(
    engine: &mut PhysicsEngine,
    entity_id: u32,
    gfx_obj_id: u32,
    position: Vec3,
    rotation: Quat,
    radius: f32,
    landblock_id: u32,
    collision_type: ShadowCollisionType,
    cyl_height: f32,
    scale: f32,
    state: u32,
    flags: EntityCollisionFlags,
    seed_cell_id: u32,
    is_static: bool,
) {
    let (shadows, world) = engine.shadows_and_world();
    shadows
        .register(
            world,
            entity_id,
            &SingleShape {
                gfx_obj_id,
                radius,
                collision_type,
                cyl_height,
                scale,
            },
            RegistrationSite {
                world_pos: position,
                rotation,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id,
                seed_cell_id,
            },
            state,
            flags,
            is_static,
            None,
        )
        .expect("register");
}

/// A cell whose collision is `bsp`, placed at `placement`, with no
/// containment tree or portals.
pub fn cell_with_bsp(id: u32, bsp: PhysicsBsp, placement: Placement) -> CellPhysics {
    CellPhysics {
        id,
        physics_bsp: bsp,
        placement,
        visible_cells: CellArray::new(),
        ..CellPhysics::default()
    }
}

/// OpenAC `BSPQuery.FindCollisions(root, resolved, transition, localSphere,
/// localSphere1, localCurrCenter, localSpaceZ, scale, localToWorld,
/// engine)`: one tree test of the transition's spheres given in the tree's
/// frame.
#[allow(clippy::too_many_arguments)]
pub fn bsp_find(
    bsp: &PhysicsBsp,
    t: &mut Transition,
    sphere0: Sphere,
    sphere1: Option<Sphere>,
    curr_center: Vec3,
    space_z: Vec3,
    scale: f32,
    local_to_world: Quat,
    engine: Option<&PhysicsEngine>,
) -> TransitionState {
    let spheres = LocalSpheres {
        sphere0,
        sphere1,
        curr_center,
        space_z,
    };
    let frame = BspFrame {
        local_to_world,
        scale,
        world_origin: Vec3::ZERO,
    };
    let mut ctx = BspContext {
        transition: t,
        engine,
    };
    bsp.find_collisions(&mut ctx, &spheres, &frame)
}

/// xUnit `Assert.Equal(expected, actual, precision)`: both rounded to
/// `precision` decimals (ties to even) must be equal.
#[track_caller]
pub fn assert_eq_prec(expected: f32, actual: f32, precision: i32) {
    let scale = 10f64.powi(precision);
    let round = |x: f32| (f64::from(x) * scale).round_ties_even() / scale;
    assert_eq!(
        round(expected),
        round(actual),
        "expected {expected} to {precision} decimals, got {actual}"
    );
}

/// Registers a sphere-shaped shadow object (OpenAC's
/// `ShadowObjects.Register(..., collisionType: Sphere, ...)`).
#[allow(clippy::too_many_arguments)]
pub fn register_sphere(
    engine: &mut PhysicsEngine,
    entity_id: u32,
    center: Vec3,
    radius: f32,
    landblock_id: u32,
    state: u32,
    flags: EntityCollisionFlags,
    seed_cell_id: u32,
    is_static: bool,
) {
    let (shadows, world) = engine.shadows_and_world();
    shadows
        .register(
            world,
            entity_id,
            &SingleShape {
                gfx_obj_id: 0,
                radius,
                collision_type: ShadowCollisionType::Sphere,
                cyl_height: 0.0,
                scale: 1.0,
            },
            RegistrationSite {
                world_pos: center,
                rotation: Quat::IDENTITY,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id,
                seed_cell_id,
            },
            state,
            flags,
            is_static,
            None,
        )
        .expect("register");
}

/// The two-sphere human body (foot and head, 0.48 m, 1.835 m tall).
pub fn human_spheres() -> Vec<crate::transition::CollisionSphere> {
    use crate::transition::CollisionSphere;
    vec![
        CollisionSphere::new(Vec3::new(0.0, 0.0, 0.48), 0.48),
        CollisionSphere::new(Vec3::new(0.0, 0.0, 1.835 - 0.48), 0.48),
    ]
}

/// An engine with one landblock (`0xA9B4FFFF` at the origin) and no
/// collision world (OpenAC's bare `new PhysicsEngine()` + `AddLandblock`).
pub fn engine_with_terrain(terrain: TerrainSurface) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.add_landblock(0xA9B4_FFFF, terrain, Vec::new(), Vec::new(), 0.0, 0.0);
    engine
}
