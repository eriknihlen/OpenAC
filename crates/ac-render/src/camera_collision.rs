//! The chase camera's collision: the eye is swept as a small sphere from the
//! pivot over the player's head back along the boom, through the same
//! collision the player walks in, so it stops at walls, ceilings, terrain and
//! objects, and the sweep reports the cell the eye really reached. The frame
//! is walked from that cell.
//!
//! The collision is a physics engine of the window's own: the landblock the
//! player is in and its eight neighbours (as a session holds them), built on
//! the loader thread from the dats and swapped in when it arrives. It has the
//! terrain, the interior cells, the buildings and the static objects, and
//! the server objects (doors, other players, creatures) are mirrored into it
//! from the session's object views each frame, as the session registers
//! them in its own. The eye passes through creatures and
//! players: a viewer is exempt from them.

use std::collections::{HashMap, HashSet};

use ac_core::items::ItemType;
use ac_core::world::LiveWorldOrigin;
use ac_physics::content::{
    ContentError, DatSource, ServerObjectShape, landblock_origin, publish_landblock,
    register_server_object, server_object_flags,
};
use ac_physics::{
    CollisionWorld, ObjectInfoState, PhysicsEngine, RegistrationSite, ResolveRequest,
};
use ac_proto::Position;
use ac_runtime::world::ObjectView;
use glam::{Quat, Vec3};

use crate::world_frame::{LandblockCoord, landblock_offset, landblocks_around};

/// The eye is a sphere of this radius, meters.
pub const VIEWER_SPHERE_RADIUS: f32 = 0.3;

/// The neighbourhood is the player's landblock and those within this many.
pub const NEIGHBOURHOOD_RADIUS: u32 = 1;

/// Where the sweep left the eye and the cell holding it (0: the sweep
/// failed and the eye was put back on the player).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CameraSweep {
    /// The eye, drawing frame.
    pub eye: Vec3,
    /// The cell holding the eye, 0 for none.
    pub viewer_cell: u32,
}

/// Something that can sweep the camera's eye from the pivot toward where
/// the boom wants it.
pub trait CameraCollisionProbe {
    /// Sweep from `pivot` toward `desired_eye`, starting in the player's
    /// cell `cell_id`, ignoring the object `self_id` (the player);
    /// `player_position` is where the eye goes when nothing works.
    fn sweep_eye(
        &self,
        pivot: Vec3,
        desired_eye: Vec3,
        cell_id: u32,
        self_id: u32,
        player_position: Vec3,
    ) -> CameraSweep;
}

/// The sweep through a physics engine whose frame is `offset` meters from
/// the drawing frame (a drawing-frame point is `engine point + offset`).
#[derive(Debug, Clone, Copy)]
pub struct PhysicsCameraProbe<'a> {
    engine: &'a PhysicsEngine,
    offset: Vec3,
}

impl<'a> PhysicsCameraProbe<'a> {
    /// A probe over `engine`, whose origin lies at `offset` in the drawing
    /// frame.
    #[must_use]
    pub fn new(engine: &'a PhysicsEngine, offset: Vec3) -> Self {
        Self { engine, offset }
    }

    /// The eye never goes below the water: outdoors the terrain height of a
    /// water cell is its surface, so an eye under it is lifted to the
    /// surface plus its own radius. Interior cells have no water; an eye
    /// over terrain that is not resident is left alone. Engine frame.
    fn keep_above_water(&self, eye: Vec3, eye_cell_id: u32) -> Vec3 {
        if eye_cell_id & 0xFFFF >= 0x0100 {
            return eye;
        }
        if self.engine.sample_water_depth(eye.x, eye.y) <= 0.0 {
            return eye;
        }
        match self.engine.sample_terrain_z(eye.x, eye.y) {
            Some(z) => clamp_above_water(eye, z, VIEWER_SPHERE_RADIUS),
            None => eye,
        }
    }
}

impl CameraCollisionProbe for PhysicsCameraProbe<'_> {
    fn sweep_eye(
        &self,
        pivot: Vec3,
        desired_eye: Vec3,
        cell_id: u32,
        self_id: u32,
        player_position: Vec3,
    ) -> CameraSweep {
        if cell_id == 0 {
            return CameraSweep {
                eye: player_position,
                viewer_cell: 0,
            };
        }
        let pivot_e = pivot - self.offset;
        let desired_e = desired_eye - self.offset;

        // Indoors the pivot (1.5 m over the feet) can be in another cell
        // than the feet: the sweep starts in the pivot's.
        let mut start_cell = cell_id;
        if cell_id & 0xFFFF >= 0x0100 {
            let (pivot_cell, found) = self.engine.adjust_position(cell_id, pivot_e);
            if found {
                start_cell = pivot_cell;
            }
        }

        let request = ResolveRequest {
            current_pos: to_sphere_path(pivot_e, VIEWER_SPHERE_RADIUS),
            target_pos: to_sphere_path(desired_e, VIEWER_SPHERE_RADIUS),
            cell_id: start_cell,
            sphere_radius: VIEWER_SPHERE_RADIUS,
            // One sphere (no head sphere), no stepping, no ground snap.
            sphere_height: 0.0,
            step_up_height: 0.0,
            step_down_height: 0.0,
            is_on_ground: false,
            mover_flags: ObjectInfoState(
                ObjectInfoState::IS_VIEWER
                    | ObjectInfoState::PATH_CLIPPED
                    | ObjectInfoState::FREE_ROTATE
                    | ObjectInfoState::PERFECT_CLIP,
            ),
            moving_entity_id: self_id,
            ..ResolveRequest::default()
        };
        let result = self.engine.resolve_with_transition(&request, None);
        let eye = from_sphere_path(result.position, VIEWER_SPHERE_RADIUS);
        if result.ok {
            return CameraSweep {
                eye: self.keep_above_water(eye, result.cell_id) + self.offset,
                viewer_cell: result.cell_id,
            };
        }

        // The sweep failed: keep the wanted eye when a cell holds it.
        let (eye_cell, found) = self.engine.adjust_position(cell_id, desired_e);
        if found {
            return CameraSweep {
                eye: self.keep_above_water(desired_e, eye_cell) + self.offset,
                viewer_cell: eye_cell,
            };
        }

        // Nothing holds it: the eye goes to the player, with no cell.
        CameraSweep {
            eye: player_position,
            viewer_cell: 0,
        }
    }
}

/// The sphere's centre as the movement path carries it (the path point is
/// the sphere's bottom: one radius below the centre).
#[must_use]
pub fn to_sphere_path(sphere_point: Vec3, radius: f32) -> Vec3 {
    sphere_point - Vec3::new(0.0, 0.0, radius)
}

/// Back from a path point to the sphere's centre.
#[must_use]
pub fn from_sphere_path(path_point: Vec3, radius: f32) -> Vec3 {
    path_point + Vec3::new(0.0, 0.0, radius)
}

/// An eye below `water_surface_z + margin` is lifted to it.
#[must_use]
pub fn clamp_above_water(eye: Vec3, water_surface_z: f32, margin: f32) -> Vec3 {
    let floor = water_surface_z + margin;
    if eye.z < floor {
        Vec3::new(eye.x, eye.y, floor)
    } else {
        eye
    }
}

/// The camera's collision around `center`: its landblock and the
/// neighbours within [`NEIGHBOURHOOD_RADIUS`], each published as a session
/// publishes it, with `center`'s south-west corner at the engine's origin.
/// A landblock the dats lack is skipped.
///
/// # Errors
/// A landblock could not be read or its collision built.
pub fn build_neighbourhood<D: DatSource>(
    dats: &D,
    height_table: &[f32; 256],
    center: LandblockCoord,
) -> Result<PhysicsEngine, ContentError> {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    let center_id = center.terrain_id();
    for block in landblocks_around(center, NEIGHBOURHOOD_RADIUS) {
        let id = block.terrain_id();
        publish_landblock(
            &mut engine,
            dats,
            height_table,
            id,
            landblock_origin(center_id, id),
        )?;
    }
    Ok(engine)
}

/// The window's camera collision: the neighbourhood resident now (kept
/// until a newer one arrives), the one asked of the loader, and the server
/// objects of the session's view mirrored into the resident one.
#[derive(Debug, Default)]
pub struct CameraCollision {
    resident: Option<(LandblockCoord, Box<PhysicsEngine>)>,
    requested: Option<LandblockCoord>,
    objects: HashMap<u32, Mirrored>,
}

/// A server object as last mirrored (by its client id): what it was built
/// from, its physics state and where it stood, and whether it has rows
/// (one that could not be built is not tried again until it changes).
#[derive(Debug, Clone, PartialEq)]
struct Mirrored {
    shape: ServerObjectShape,
    state: u32,
    at: Position,
    registered: bool,
}

/// Where an object at `at` registers in a neighbourhood centred on
/// landblock `center_id` (whose south-west corner is the engine's origin).
fn registration_site(center_id: u32, at: &Position) -> RegistrationSite {
    let origin = landblock_origin(center_id, at.cell_id);
    let o = at.orientation;
    RegistrationSite {
        world_pos: origin + Vec3::new(at.origin.x, at.origin.y, at.origin.z),
        rotation: Quat::from_xyzw(o.x, o.y, o.z, o.w),
        world_offset_x: origin.x,
        world_offset_y: origin.y,
        landblock_id: at.cell_id,
        seed_cell_id: at.cell_id,
    }
}

/// What an object view collides as: its setup, scale, part swaps, motion
/// table and the flags its public description gives (`None`: it has no
/// setup).
fn view_shape(view: &ObjectView) -> Option<ServerObjectShape> {
    Some(ServerObjectShape {
        setup_id: view.setup_id?,
        scale: view.scale.unwrap_or(1.0),
        part_changes: view
            .model
            .anim_part_changes
            .iter()
            .map(|c| (usize::from(c.part_index), c.new_model_id))
            .collect(),
        motion_table_id: view.motion_table_id,
        placement: view.rest_placement,
        flags: server_object_flags(
            Some(view.radar.public_weenie_bitfield),
            view.radar.item_type == ItemType::CREATURE.bits(),
        ),
    })
}

impl CameraCollision {
    /// Nothing resident, nothing asked for.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The neighbourhood to ask the loader for when the player stands in
    /// `player_block`: `None` when the resident or requested one is already
    /// centred there. Marks it requested.
    pub fn wanted(&mut self, player_block: LandblockCoord) -> Option<LandblockCoord> {
        if self.resident.as_ref().map(|(c, _)| *c) == Some(player_block) {
            self.requested = None;
            return None;
        }
        if self.requested == Some(player_block) {
            return None;
        }
        self.requested = Some(player_block);
        Some(player_block)
    }

    /// A neighbourhood arrived. `Err`: it could not be built; the camera
    /// keeps the previous one and it is not asked for again until the
    /// player enters another landblock.
    ///
    /// # Errors
    /// Hands back the build failure for the window to report.
    pub fn arrived(
        &mut self,
        center: LandblockCoord,
        engine: Result<Box<PhysicsEngine>, String>,
    ) -> Result<(), String> {
        let engine = engine?;
        if self.requested == Some(center) {
            self.requested = None;
        }
        self.resident = Some((center, engine));
        // The new engine has no server objects: the next sync puts them in.
        self.objects.clear();
        Ok(())
    }

    /// Drop everything (the window left the world).
    pub fn clear(&mut self) {
        self.resident = None;
        self.requested = None;
        self.objects.clear();
    }

    /// Mirror the session's server objects (its object views) into the
    /// resident neighbourhood, as the session registers them in its own
    /// engine: every object with a setup and a position in a landblock the
    /// neighbourhood holds, not held by another, with the collision its
    /// setup, scale, part swaps and default pose give and its physics
    /// state; an object that moved has its rows follow it, one whose state
    /// changed takes the new state, one that changed otherwise is built
    /// again, and one that left leaves the lists. Returns the client ids of
    /// the objects whose collision could not be built or moved, with why.
    ///
    /// Building an object new is the costly part (its setup is read and its
    /// collision made): the first is always built, and each further one
    /// only while `may_build` says the frame has time for it; the rest are
    /// built by later calls. A neighbourhood that arrives with a town's
    /// worth of objects fills over a few frames instead of in one.
    pub fn sync_objects<'v, D: DatSource>(
        &mut self,
        dats: &D,
        views: impl IntoIterator<Item = &'v ObjectView>,
        mut may_build: impl FnMut() -> bool,
    ) -> Vec<(u32, ContentError)> {
        let mut failed = Vec::new();
        let Some((center, engine)) = self.resident.as_mut() else {
            self.objects.clear();
            return failed;
        };
        let center_id = center.terrain_id();
        let mut seen = HashSet::new();
        let mut built = 0usize;
        for view in views {
            let id = view.key.local_id;
            let wanted = match (view.parent, view.position, view_shape(view)) {
                (None, Some(at), Some(shape))
                    if engine.is_landblock_terrain_resident(at.cell_id) =>
                {
                    Some((at, shape))
                }
                _ => None,
            };
            let Some((at, shape)) = wanted else {
                if self.objects.remove(&id).is_some_and(|m| m.registered) {
                    engine.shadows.deregister(id);
                }
                continue;
            };
            seen.insert(id);
            let site = registration_site(center_id, &at);
            match self.objects.get_mut(&id) {
                Some(m) if m.shape == shape && (m.registered || m.state == view.state) => {
                    if !m.registered {
                        continue;
                    }
                    if m.state != view.state {
                        engine.shadows.update_physics_state(id, view.state);
                        m.state = view.state;
                    }
                    if m.at != at {
                        m.at = at;
                        let (shadows, world) = engine.shadows_and_world();
                        if let Err(error) = shadows.update_position(world, id, site) {
                            failed.push((id, ContentError::Flood { owner: id, error }));
                        }
                    }
                }
                existing => {
                    // A new object waits for a later frame once this one
                    // has built its share.
                    if existing.is_none() && built > 0 && !may_build() {
                        continue;
                    }
                    built += 1;
                    if self.objects.remove(&id).is_some_and(|m| m.registered) {
                        engine.shadows.deregister(id);
                    }
                    let registered =
                        match register_server_object(engine, dats, id, &shape, view.state, site) {
                            Ok(registered) => registered,
                            Err(error) => {
                                failed.push((id, error));
                                false
                            }
                        };
                    self.objects.insert(
                        id,
                        Mirrored {
                            shape,
                            state: view.state,
                            at,
                            registered,
                        },
                    );
                }
            }
        }
        let gone: Vec<u32> = self
            .objects
            .keys()
            .copied()
            .filter(|id| !seen.contains(id))
            .collect();
        for id in gone {
            if self.objects.remove(&id).is_some_and(|m| m.registered) {
                engine.shadows.deregister(id);
            }
        }
        failed
    }

    /// Whether the server object with client id `local_id` has rows in the
    /// resident neighbourhood.
    #[must_use]
    pub fn has_object(&self, local_id: u32) -> bool {
        self.objects.get(&local_id).is_some_and(|m| m.registered)
    }

    /// The cells the server object with client id `local_id` is drawn in:
    /// every cell its drawn parts reach, as its registration flooded them
    /// (`None` when it has no rows in the resident neighbourhood).
    #[must_use]
    pub fn render_cells(&self, local_id: u32) -> Option<&[u32]> {
        if !self.has_object(local_id) {
            return None;
        }
        let (_, engine) = self.resident.as_ref()?;
        engine
            .shadows
            .retail_cell_array(local_id)
            .filter(|cells| !cells.is_empty())
    }

    /// Every collision object of the resident neighbourhood and the offset
    /// from its frame to the drawing frame (a diagnostic).
    #[must_use]
    pub fn shadows(
        &self,
        origin: &LiveWorldOrigin,
    ) -> Option<(Vec<ac_physics::ShadowEntry>, Vec3)> {
        let (center, engine) = self.resident.as_ref()?;
        Some((
            engine.shadows.capture_entries(),
            landblock_offset(origin, *center),
        ))
    }

    /// A probe for a player in `player_cell`, when the resident
    /// neighbourhood holds that cell's landblock.
    #[must_use]
    pub fn probe(
        &self,
        origin: &LiveWorldOrigin,
        player_cell: u32,
    ) -> Option<PhysicsCameraProbe<'_>> {
        let (center, engine) = self.resident.as_ref()?;
        if !engine.is_landblock_terrain_resident(player_cell) {
            return None;
        }
        Some(PhysicsCameraProbe::new(
            engine,
            landblock_offset(origin, *center),
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // OpenAC `PhysicsCameraCollisionProbeTests.SpherePathOffset_RoundTrips`.
    #[test]
    fn the_sphere_path_offset_round_trips() {
        let p = Vec3::new(10.0, 20.0, 30.0);
        let path = to_sphere_path(p, 0.3);
        assert!((path.z - (p.z - 0.3)).abs() < 1e-5);
        assert_eq!((path.x, path.y), (p.x, p.y));
        let back = from_sphere_path(path, 0.3);
        assert!((back - p).length() < 1e-5);
    }

    // OpenAC `PhysicsCameraCollisionProbeTests.ClampAboveWater_LiftsAnEyeThatIsUnderTheSurface`.
    #[test]
    fn an_eye_under_the_water_surface_is_lifted_to_it() {
        for (eye_z, expected) in [(-3.0, 0.3), (0.1, 0.3), (2.0, 2.0)] {
            let clamped = clamp_above_water(Vec3::new(5.0, 7.0, eye_z), 0.0, 0.3);
            assert_eq!((clamped.x, clamped.y), (5.0, 7.0));
            assert!((clamped.z - expected).abs() < 1e-5, "{eye_z}: {clamped}");
        }
    }

    // OpenAC `PhysicsCameraCollisionProbeTests.SweepEye_NoStartingCell_SnapsToPlayer`
    // and `CameraCollisionUpdateViewerTests.SweepEye_NoStartCell_SnapsToPlayer`.
    #[test]
    fn with_no_starting_cell_the_eye_snaps_to_the_player() {
        let engine = PhysicsEngine::new();
        let probe = PhysicsCameraProbe::new(&engine, Vec3::ZERO);
        for (pivot, eye, player) in [
            (
                Vec3::new(0.0, 0.0, 1.5),
                Vec3::new(-2.0, 0.0, 2.2),
                Vec3::ZERO,
            ),
            (
                Vec3::new(7.0, 8.0, 10.5),
                Vec3::new(7.0, 13.0, 11.0),
                Vec3::new(7.0, 8.0, 9.0),
            ),
        ] {
            let result = probe.sweep_eye(pivot, eye, 0, 0, player);
            assert_eq!(result.eye, player);
            assert_eq!(result.viewer_cell, 0);
        }
    }

    /// An interior cell at the world origin with no collision polygons whose
    /// containment is the space in front of `plane`.
    fn open_cell(id: u32, plane: ac_physics::Plane, visible: &[u32]) -> ac_physics::CellPhysics {
        use ac_physics::{CellBsp, CellBspNode, PhysicsBsp, PhysicsBspNode, PortalInfo, Sphere};
        let mut cell = ac_physics::CellPhysics {
            id,
            physics_bsp: PhysicsBsp::new(
                vec![PhysicsBspNode::Leaf {
                    sphere: Sphere::new(Vec3::ZERO, 0.0),
                    solid: false,
                    polygons: Vec::new(),
                }],
                std::collections::HashMap::new(),
            )
            .unwrap(),
            containment_bsp: CellBsp::new(vec![
                CellBspNode::Split {
                    plane,
                    positive: Some(1),
                    negative: None,
                },
                CellBspNode::Leaf,
            ])
            .unwrap(),
            portals: vec![PortalInfo::new(0xFFFF, 0, 0)],
            ..ac_physics::CellPhysics::default()
        };
        for v in visible {
            cell.visible_cells.add(*v);
        }
        cell
    }

    // OpenAC `CameraCollisionUpdateViewerTests.SweepEye_IndoorPivotInCellAboveFeet_SeatsStartAtPivotCell`.
    #[test]
    fn an_indoor_sweep_starts_in_the_pivots_cell_above_the_feet() {
        const FEET: u32 = 0xA9B4_0174;
        const ROOM: u32 = 0xA9B4_0171;
        let mut engine = PhysicsEngine::new();
        let mut world = CollisionWorld::new();
        // The feet cell is z <= 94, the room above it z >= 94.
        world.insert_cell(open_cell(
            FEET,
            ac_physics::Plane::new(Vec3::NEG_Z, 94.0),
            &[ROOM],
        ));
        world.insert_cell(open_cell(ROOM, ac_physics::Plane::new(Vec3::Z, -94.0), &[]));
        engine.attach_world(world);
        engine.add_landblock(
            0xA9B4_FFFF,
            ac_physics::TerrainSurface::new(&[0u8; 81], &[-1000.0; 256], 0, 0, None),
            Vec::new(),
            Vec::new(),
            0.0,
            0.0,
        );
        let probe = PhysicsCameraProbe::new(&engine, Vec3::ZERO);
        let result = probe.sweep_eye(
            Vec3::new(0.0, 0.0, 94.5),
            Vec3::new(0.0, 3.0, 95.5),
            FEET,
            0,
            Vec3::new(0.0, 0.0, 93.0),
        );
        assert_eq!(result.viewer_cell, ROOM, "{result:?}");
    }

    /// OpenAC `CameraCollisionIndoorTests`' fixture: a building in landcell
    /// (0, 0) whose shell (model `model_id`, 0 for none) is one wall across
    /// y = 4 m, terrain far below.
    fn engine_with_building(model_id: u32) -> PhysicsEngine {
        use ac_physics::{
            BuildingPhysics, GfxObjPhysics, PhysicsBsp, PhysicsBspNode, Placement, Plane,
            ResolvedPolygon, Sphere,
        };
        const SHELL: u32 = 0x01AA_BB01;
        let wall = ResolvedPolygon::new(
            1,
            vec![
                Vec3::new(-3.0, 0.0, -3.0),
                Vec3::new(3.0, 0.0, -3.0),
                Vec3::new(3.0, 0.0, 3.0),
                Vec3::new(-3.0, 0.0, 3.0),
            ],
            Plane::new(Vec3::NEG_Y, 0.0),
        );
        let mut world = CollisionWorld::new();
        world.insert_gfx_obj(GfxObjPhysics {
            id: SHELL,
            bsp: PhysicsBsp::new(
                vec![PhysicsBspNode::Leaf {
                    sphere: Sphere::new(Vec3::ZERO, 10.0),
                    solid: false,
                    polygons: vec![1],
                }],
                std::iter::once((1, wall)).collect(),
            )
            .unwrap(),
            bounding_sphere: Sphere::new(Vec3::ZERO, 10.0),
            visual_bounds: None,
        });
        world.insert_building(
            0xA9B4_0001,
            BuildingPhysics {
                placement: Placement::new(glam::Quat::IDENTITY, Vec3::new(0.0, 4.0, 96.0)),
                portals: Vec::new(),
                model_id: if model_id == 0 { 0 } else { SHELL },
            },
        );
        let mut engine = PhysicsEngine::new();
        engine.attach_world(world);
        engine.add_landblock(
            0xA9B4_FFFF,
            ac_physics::TerrainSurface::new(&[0u8; 81], &[-1000.0; 256], 0, 0, None),
            Vec::new(),
            Vec::new(),
            0.0,
            0.0,
        );
        engine
    }

    // OpenAC `CameraCollisionIndoorTests.SweepEye_OutdoorCell_StoppedByBuildingChannelShell`
    // and `SweepEye_BuildingWithoutModelId_ChannelInert`.
    #[test]
    fn an_outdoor_sweep_stops_at_a_building_shell_with_a_model() {
        let pivot = Vec3::new(0.0, 1.0, 95.5);
        let desired = Vec3::new(0.0, 5.0, 96.25);
        let player = pivot - Vec3::new(0.0, 0.0, 1.5);
        for (model, stopped) in [(1, true), (0, false)] {
            let engine = engine_with_building(model);
            let probe = PhysicsCameraProbe::new(&engine, Vec3::ZERO);
            let eye = probe.sweep_eye(pivot, desired, 0xA9B4_0001, 0, player).eye;
            let pulled_in = (desired.y - eye.y).abs();
            if stopped {
                assert!(pulled_in >= 0.5, "pulled in {pulled_in} m, eye {eye}");
            } else {
                assert!(pulled_in < 0.05, "pulled in {pulled_in} m, eye {eye}");
            }
        }
    }

    /// Dat objects by file id, for the server objects' setups.
    #[derive(Default)]
    struct FakeDats(HashMap<u32, std::sync::Arc<dyn std::any::Any + Send + Sync>>);

    impl DatSource for FakeDats {
        fn object<T: ac_dat::DatObject + Send + Sync + 'static>(
            &self,
            id: u32,
        ) -> Result<Option<std::sync::Arc<T>>, ac_dat::DatError> {
            Ok(self
                .0
                .get(&id)
                .and_then(|o| std::sync::Arc::clone(o).downcast::<T>().ok()))
        }
    }

    const POST_SETUP: u32 = 0x0200_0099;

    /// A setup with no parts that collides as one upright cylinder, 1 m
    /// across its radius and 4 m tall.
    fn post_dats() -> FakeDats {
        let mut setup = crate::animation::test_support::setup(0);
        setup.id = POST_SETUP;
        setup.cyl_spheres = vec![ac_dat::types::CylSphere {
            origin: ac_dat::types::Vector3 {
                x: 0.0,
                y: 0.0,
                z: 0.0,
            },
            radius: 1.0,
            height: 4.0,
        }];
        let mut dats = FakeDats::default();
        dats.0.insert(POST_SETUP, std::sync::Arc::new(setup));
        dats
    }

    /// A server object (client id 7) with the post's setup standing in
    /// landcell 0xA9B4_0001 at (x, y, z).
    fn post_view(x: f32, y: f32, z: f32) -> ObjectView {
        use ac_runtime::world::{EffectFacts, ObjectKey, RadarFacts};
        ObjectView {
            key: ObjectKey {
                local_id: 7,
                incarnation: 1,
            },
            guid: 0x7000_0007,
            name: None,
            setup_id: Some(POST_SETUP),
            position: Some(Position {
                cell_id: 0xA9B4_0001,
                origin: ac_proto::Vector3 { x, y, z },
                orientation: ac_proto::Quaternion::IDENTITY,
            }),
            parent: None,
            placement: None,
            rest_placement: 0,
            scale: None,
            translucency: None,
            state: 0,
            velocity: None,
            model: ac_proto::ModelData::default(),
            motion_table_id: None,
            sound_table_id: None,
            radar: RadarFacts::default(),
            motion: None,
            effects: EffectFacts::default(),
            step_blend: None,
        }
    }

    /// How far the eye is pulled in from where the boom wanted it, with the
    /// views `views` mirrored into the neighbourhood first.
    fn pulled_in(c: &mut CameraCollision, dats: &FakeDats, views: &[ObjectView]) -> f32 {
        assert!(c.sync_objects(dats, views, || true).is_empty());
        let origin = {
            let mut o = LiveWorldOrigin::new();
            o.try_initialize(0xA9, 0xB4);
            o
        };
        let probe = c.probe(&origin, 0xA9B4_0001).expect("resident");
        let pivot = Vec3::new(0.0, 1.0, 95.5);
        let desired = Vec3::new(0.0, 5.0, 96.25);
        let eye = probe
            .sweep_eye(pivot, desired, 0xA9B4_0001, 1, pivot - Vec3::Z * 1.5)
            .eye;
        (desired.y - eye.y).abs()
    }

    // OpenAC `PhysicsCameraCollisionProbe.SweepEye` sweeps the session's
    // engine, server objects included; `CollisionExemption.ShouldSkip`
    // exempts a viewer from creatures and from an ethereal object that
    // ignores collisions. The window mirrors its views into its own.
    /// A setup (the post's id) of one 1 m drawn box part, centred on its
    /// origin, without collision.
    fn crate_dats() -> FakeDats {
        use ac_dat::objects::{GfxObj, GfxObjFlags, Vertex, VertexArray};
        use ac_dat::types::Vector3;
        const PART: u32 = 0x0100_0098;
        let mut setup = crate::animation::test_support::setup(1);
        setup.id = POST_SETUP;
        setup.parts = vec![PART];
        let corner = |i: u16| {
            let s = |bit: u16| if i & bit == 0 { -0.5 } else { 0.5 };
            (
                i,
                Vertex {
                    origin: Vector3 {
                        x: s(1),
                        y: s(2),
                        z: s(4),
                    },
                    normal: Vector3::default(),
                    uvs: Vec::new(),
                },
            )
        };
        let gfx = GfxObj {
            id: PART,
            flags: GfxObjFlags(GfxObjFlags::HAS_DRAWING),
            surfaces: Vec::new(),
            vertices: VertexArray {
                vertex_type: VertexArray::SW_VERTEX,
                vertices: (0..8).map(corner).collect(),
            },
            physics_polygons: Vec::new(),
            physics_bsp: None,
            sort_center: Vector3::default(),
            polygons: Vec::new(),
            drawing_bsp: None,
            did_degrade: 0,
        };
        let mut dats = FakeDats::default();
        dats.0.insert(POST_SETUP, std::sync::Arc::new(setup));
        dats.0.insert(PART, std::sync::Arc::new(gfx));
        dats
    }

    // OpenAC `LiveEntityCollisionBuilder` registers a live object with its
    // drawn parts and the walk draws it in every cell their flood reaches.
    #[test]
    fn a_server_object_is_drawn_in_every_cell_its_drawn_parts_reach() {
        let dats = crate_dats();
        let mut c = CameraCollision::new();
        let center = LandblockCoord { x: 0xA9, y: 0xB4 };
        c.arrived(center, Ok(Box::new(engine_with_building(0))))
            .unwrap();
        // Unknown: drawn where it stands.
        assert_eq!(c.render_cells(7), None);
        // Inside land cell 1 (x 0 to 24 m): that cell alone.
        let inside = post_view(12.0, 12.0, 94.0);
        assert!(c.sync_objects(&dats, [&inside], || true).is_empty());
        assert_eq!(c.render_cells(7), Some(&[0xA9B4_0001][..]));
        // Moved onto the line at x = 24 m: its box reaches cell 9 too.
        let straddling = post_view(23.8, 12.0, 94.0);
        assert!(c.sync_objects(&dats, [&straddling], || true).is_empty());
        let cells = c.render_cells(7).expect("registered");
        assert!(cells.contains(&0xA9B4_0001), "{cells:X?}");
        assert!(cells.contains(&0xA9B4_0009), "{cells:X?}");
        // Gone: drawn nowhere by the mirror.
        assert!(c.sync_objects(&dats, [], || true).is_empty());
        assert_eq!(c.render_cells(7), None);
    }

    #[test]
    fn the_eye_stops_at_a_server_object_the_session_shows() {
        let dats = post_dats();
        let mut c = CameraCollision::new();
        let center = LandblockCoord { x: 0xA9, y: 0xB4 };
        c.arrived(center, Ok(Box::new(engine_with_building(0))))
            .unwrap();
        // Nothing in the way: the eye goes where the boom wants it.
        assert!(pulled_in(&mut c, &dats, &[]) < 0.05);

        // A post on the boom stops it.
        let post = post_view(0.0, 3.0, 94.0);
        assert!(pulled_in(&mut c, &dats, std::slice::from_ref(&post)) >= 0.5);
        assert!(c.has_object(7));

        // The post turns ethereal and ignores collisions: the eye passes.
        let ghost = ObjectView {
            state: 0x4 | 0x10,
            ..post.clone()
        };
        assert!(pulled_in(&mut c, &dats, std::slice::from_ref(&ghost)) < 0.05);
        // Solid again, then moved off the boom: the eye passes.
        assert!(pulled_in(&mut c, &dats, std::slice::from_ref(&post)) >= 0.5);
        let moved = post_view(20.0, 3.0, 94.0);
        assert!(pulled_in(&mut c, &dats, std::slice::from_ref(&moved)) < 0.05);

        // A creature in the way does not stop a viewer.
        let creature = ObjectView {
            radar: ac_runtime::world::RadarFacts {
                item_type: ItemType::CREATURE.bits(),
                ..ac_runtime::world::RadarFacts::default()
            },
            ..post.clone()
        };
        assert!(pulled_in(&mut c, &dats, std::slice::from_ref(&creature)) < 0.05);

        // Back as a post, then gone from the session: the eye passes.
        assert!(pulled_in(&mut c, &dats, std::slice::from_ref(&post)) >= 0.5);
        assert!(pulled_in(&mut c, &dats, &[]) < 0.05);
        assert!(!c.has_object(7));

        // A new neighbourhood starts empty and the next sync fills it.
        c.arrived(center, Ok(Box::new(engine_with_building(0))))
            .unwrap();
        assert!(!c.has_object(7));
        assert!(pulled_in(&mut c, &dats, std::slice::from_ref(&post)) >= 0.5);
    }

    // A neighbourhood arriving with many objects builds the first at once
    // and the rest while the frame has time; what did not fit is built by
    // the next call, and nothing already built waits.
    #[test]
    fn new_objects_are_built_while_the_frame_has_time() {
        let dats = post_dats();
        let mut c = CameraCollision::new();
        let center = LandblockCoord { x: 0xA9, y: 0xB4 };
        c.arrived(center, Ok(Box::new(engine_with_building(0))))
            .unwrap();
        let posts: Vec<ObjectView> = (0..3)
            .map(|i| {
                let mut v = post_view(10.0 * i as f32, 3.0, 94.0);
                v.key.local_id = 7 + i;
                v
            })
            .collect();
        let built = |c: &CameraCollision| (7..10).filter(|&id| c.has_object(id)).count();

        // Time for one more after the first: two are built.
        let mut budget = 1;
        let mut one_more = || {
            budget -= 1;
            budget >= 0
        };
        assert!(c.sync_objects(&dats, &posts, &mut one_more).is_empty());
        assert_eq!(built(&c), 2);
        // No time at all: the next call still builds one, and that finishes
        // them; the two built before stay.
        assert!(c.sync_objects(&dats, &posts, || false).is_empty());
        assert_eq!(built(&c), 3);
        // Moving a built one is not building: it follows at once.
        let mut moved = posts.clone();
        moved[0] = post_view(1.0, 3.0, 94.0);
        assert!(c.sync_objects(&dats, &moved, || false).is_empty());
        assert_eq!(built(&c), 3);
    }

    #[test]
    fn a_neighbourhood_is_asked_for_once_per_landblock() {
        let mut c = CameraCollision::new();
        let a = LandblockCoord { x: 0xA9, y: 0xB4 };
        let b = LandblockCoord { x: 0xA9, y: 0xB5 };
        assert_eq!(c.wanted(a), Some(a));
        assert_eq!(c.wanted(a), None);
        c.arrived(a, Ok(Box::new(PhysicsEngine::new()))).unwrap();
        assert_eq!(c.wanted(a), None);
        assert_eq!(c.wanted(b), Some(b));
        assert_eq!(c.wanted(b), None);
        // A failed build keeps the previous neighbourhood, reports, and is
        // not asked for again while the player stays in that landblock.
        assert!(c.arrived(b, Err("bad".into())).is_err());
        assert_eq!(c.resident.as_ref().map(|(center, _)| *center), Some(a));
        assert_eq!(c.wanted(b), None);
        assert_eq!(c.wanted(a), None);
        assert_eq!(c.wanted(b), Some(b));
    }

    // OpenAC `LifestoneCameraSettleTests`: a player with their back to a
    // lifestone (one collision sphere, 1.504 m, centred 1.504 m up). The boom
    // points into it, so the eye is pulled in to where its sphere touches the
    // lifestone and stays there frame after frame, instead of re-extending
    // and snapping back.
    #[test]
    fn with_its_back_to_a_lifestone_the_eye_settles_against_it() {
        use crate::camera::{ChaseCamera, ChaseCollision, ChaseSubject};
        use ac_physics::{EntityCollisionFlags, ShadowCollisionType, SingleShape, TerrainSurface};
        const LIFESTONE_RADIUS: f32 = 1.504;
        const CELL: u32 = 0xA9B4_0001;
        let lifestone = Vec3::new(12.0, 12.0, 0.0);
        let sphere_center = lifestone + Vec3::new(0.0, 0.0, LIFESTONE_RADIUS);

        let mut engine = PhysicsEngine::new();
        engine.attach_world(CollisionWorld::new());
        engine.add_landblock(
            0xA9B4_FFFF,
            TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None),
            Vec::new(),
            Vec::new(),
            0.0,
            0.0,
        );
        let (shadows, world) = engine.shadows_and_world();
        shadows
            .register(
                world,
                0x7AA0_C34F,
                &SingleShape {
                    gfx_obj_id: 0,
                    radius: LIFESTONE_RADIUS,
                    collision_type: ShadowCollisionType::Sphere,
                    cyl_height: 0.0,
                    scale: 1.0,
                },
                RegistrationSite {
                    world_pos: sphere_center,
                    rotation: Quat::IDENTITY,
                    world_offset_x: 0.0,
                    world_offset_y: 0.0,
                    landblock_id: 0xA9B4_0000,
                    seed_cell_id: 0,
                },
                0x410,
                EntityCollisionFlags(EntityCollisionFlags::HAS_WEENIE),
                false,
                None,
            )
            .expect("register");
        let probe = PhysicsCameraProbe::new(&engine, Vec3::ZERO);

        for distance in [2.3_f32, 2.8, 3.2, 3.7] {
            // Facing +y, away from the lifestone south of the player.
            let feet = lifestone + Vec3::new(0.0, distance, 0.0);
            let mut cam = ChaseCamera::default();
            cam.tuning.stiffness = 0.45;
            cam.tuning.align_to_slope = true;
            let pivot = feet + Vec3::new(0.0, 0.0, cam.pivot_height);
            let subject = ChaseSubject::standing(feet, std::f32::consts::FRAC_PI_2);
            let (mut min, mut max, mut gap) = (f32::MAX, f32::MIN, 0.0);
            for frame in 0..600 {
                cam.update(
                    &subject,
                    1.0 / 60.0,
                    Some(ChaseCollision {
                        probe: &probe,
                        cell_id: CELL,
                        self_id: 0x000F_4243,
                    }),
                );
                if frame < 300 {
                    continue;
                }
                let d = cam.eye().distance(pivot);
                min = min.min(d);
                max = max.max(d);
                gap = cam.eye().distance(sphere_center) - (LIFESTONE_RADIUS + VIEWER_SPHERE_RADIUS);
            }
            assert!(min < cam.distance - 0.05, "{distance} m: not pulled in");
            assert!(
                max - min < 0.001,
                "{distance} m: the eye moved between {min} and {max} m"
            );
            assert!(
                (-0.001..0.005).contains(&gap),
                "{distance} m: the eye rests {gap} m from the lifestone"
            );
        }
    }
}
