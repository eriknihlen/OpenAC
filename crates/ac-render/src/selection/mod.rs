//! World selection: what the pointer is over in the 3-D view, and the flash
//! of an object picked there.
//!
//! The world frame publishes the parts of server objects it drew (only
//! those whose drawing sphere is in the camera's frustum count); a pick
//! tests a ray through the pointer against the last published frame's
//! parts ([`picker`]). Parts drawn outside the world frame (a paper doll,
//! a 3-D panel) never enter it. The flash of a picked object
//! ([`pulse`]) advances with the world frame, so it runs out whether or
//! not anything else draws.

pub mod picker;
pub mod pulse;

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use ac_dat::objects::{BspNode, GfxObj};
use glam::{Mat4, Vec3, Vec4};

use crate::frustum::FrustumPlanes;
pub use picker::{SelectionHit, SelectionMesh, SelectionPart, SelectionPolygon};
pub use pulse::{SelectionLighting, SelectionLightingPulse};

/// The pick shape of a model: its drawing tree's root sphere and every
/// drawn polygon with at least three corners whose corners are all
/// listed, in file order (single-sided when the polygon's sides type is
/// 0). `None` without a drawing tree or with an empty root sphere.
#[must_use]
pub fn selection_mesh(gfx: &GfxObj) -> Option<SelectionMesh> {
    let root = gfx.drawing_bsp.as_ref()?.root()?;
    let sphere = match root {
        BspNode::Split(s) => s.sphere,
        BspNode::Portal(p) => p.sphere,
        BspNode::Leaf(l) => l.sphere,
    };
    if sphere.radius <= 0.0 {
        return None;
    }
    let vertices: HashMap<u16, Vec3> = gfx
        .vertices
        .vertices
        .iter()
        .map(|(id, v)| (*id, Vec3::new(v.origin.x, v.origin.y, v.origin.z)))
        .collect();
    let polygons = gfx
        .polygons
        .iter()
        .filter(|(_, p)| p.vertex_ids.len() >= 3)
        .filter_map(|(_, p)| {
            let corners: Option<Vec<Vec3>> = p
                .vertex_ids
                .iter()
                .map(|id| vertices.get(id).copied())
                .collect();
            Some(SelectionPolygon {
                vertices: corners?,
                single_sided: p.sides_type.0 == 0,
            })
        })
        .collect();
    Some(SelectionMesh {
        sphere_center: Vec3::new(sphere.origin.x, sphere.origin.y, sphere.origin.z),
        sphere_radius: sphere.radius,
        polygons,
    })
}

/// Where pick shapes come from: a model id to its shape (`None` when it has
/// none), remembered once resolved.
pub trait SelectionGeometry {
    /// The shape of model `gfx_obj_id`.
    fn resolve(&mut self, gfx_obj_id: u32) -> Option<Arc<SelectionMesh>>;
}

/// Pick shapes read from the dats on first use and kept.
#[derive(Debug, Default)]
pub struct DatSelectionGeometry {
    cache: HashMap<u32, Option<Arc<SelectionMesh>>>,
}

impl DatSelectionGeometry {
    /// The shape of `gfx_obj_id`, read through `read` the first time.
    pub fn resolve_with(
        &mut self,
        gfx_obj_id: u32,
        read: impl FnOnce(u32) -> Option<Arc<GfxObj>>,
    ) -> Option<Arc<SelectionMesh>> {
        self.cache
            .entry(gfx_obj_id)
            .or_insert_with(|| read(gfx_obj_id).and_then(|g| selection_mesh(&g).map(Arc::new)))
            .clone()
    }
}

/// Pick shapes read through the process's cached dats.
#[derive(Debug)]
pub struct DatsGeometry<'a> {
    /// The shapes read so far.
    pub cache: &'a mut DatSelectionGeometry,
    /// The dats.
    pub dats: &'a ac_dat::CachedDats,
}

impl SelectionGeometry for DatsGeometry<'_> {
    fn resolve(&mut self, gfx_obj_id: u32) -> Option<Arc<SelectionMesh>> {
        let dats = self.dats;
        self.cache
            .resolve_with(gfx_obj_id, |id| dats.get::<GfxObj>(id).ok().flatten())
    }
}

/// A part as the frame recorded it, its shape not yet resolved.
#[derive(Debug, Clone, Copy)]
struct DrawnPart {
    guid: u32,
    local_id: u32,
    part_index: u32,
    gfx_obj_id: u32,
    world: Mat4,
}

/// The camera a pick looks through.
#[derive(Debug, Clone, Copy)]
pub struct PickCamera {
    /// Inverse of the drawing frame to clip space.
    pub inverse_view_projection: Mat4,
    /// The eye, drawing frame.
    pub eye: Vec3,
    /// Viewport, pixels.
    pub width: f32,
    /// Viewport, pixels.
    pub height: f32,
}

/// The pickable scene: the parts the last world frame drew, and the flash.
#[derive(Debug, Default)]
pub struct SelectionScene {
    building: Vec<DrawnPart>,
    building_keys: HashSet<(u32, u32, u32)>,
    building_frustum: Option<FrustumPlanes>,
    published: Vec<DrawnPart>,
    published_frustum: Option<FrustumPlanes>,
    /// The published parts with their shapes (resolved at the first pick).
    resolved: Option<Vec<SelectionPart>>,
    frame_open: bool,
    pulse: SelectionLightingPulse,
}

impl SelectionScene {
    /// An empty scene.
    #[must_use]
    pub fn new() -> Self {
        Self {
            pulse: SelectionLightingPulse::new(),
            ..Self::default()
        }
    }

    /// Flash an object picked in the world.
    pub fn begin_lighting_pulse(&mut self, guid: u32, local_id: u32, now: f64) {
        self.pulse.start(guid, local_id, now);
    }

    /// Flash some parts of an object (bit N lights part N).
    pub fn begin_part_lighting_pulse(&mut self, guid: u32, local_id: u32, mask: u32, now: f64) {
        self.pulse.start_parts(guid, local_id, mask, now);
    }

    /// Flash a whole figure on the doll's schedule.
    pub fn begin_whole_figure_lighting_pulse(&mut self, guid: u32, local_id: u32, now: f64) {
        self.pulse.start_whole_figure(guid, local_id, now);
    }

    /// How an object is lit by the flash.
    #[must_use]
    pub fn lighting(&self, guid: u32, local_id: u32) -> Option<SelectionLighting> {
        self.pulse.lighting(guid, local_id)
    }

    /// Whether some parts of an object are lit by a parts flash.
    #[must_use]
    pub fn has_part_lighting(&self, guid: u32, local_id: u32) -> bool {
        self.pulse.has_part_lighting(guid, local_id)
    }

    /// How one part is lit by a parts flash.
    #[must_use]
    pub fn part_lighting(&self, guid: u32, local_id: u32, part: u32) -> Option<SelectionLighting> {
        self.pulse.part_lighting(guid, local_id, part)
    }

    /// Forget every frame and the flash (the window left the world).
    pub fn reset(&mut self) {
        let pulse = SelectionLightingPulse::new();
        *self = Self {
            pulse,
            ..Self::default()
        };
    }

    /// Start a world frame at `now` seconds, culling against `frustum`; the
    /// flash advances here. A frame still open is dropped first.
    pub fn begin_frame(&mut self, frustum: Option<FrustumPlanes>, now: f64) {
        self.abort_frame();
        self.pulse.tick(now);
        self.frame_open = true;
        self.building.clear();
        self.building_keys.clear();
        self.building_frustum = frustum;
    }

    /// The world frame drew part `part_index` (model `gfx_obj_id`, placed at
    /// `world`) of object `guid` (incarnation `local_id`). Outside a frame,
    /// for no object, or a second time, it is ignored.
    pub fn add_visible_part(
        &mut self,
        guid: u32,
        local_id: u32,
        part_index: u32,
        gfx_obj_id: u32,
        world: Mat4,
    ) {
        if !self.frame_open || guid == 0 {
            return;
        }
        if !self
            .building_keys
            .insert((local_id, part_index, gfx_obj_id))
        {
            return;
        }
        self.building.push(DrawnPart {
            guid,
            local_id,
            part_index,
            gfx_obj_id,
            world,
        });
    }

    /// The frame is done: its parts become the pickable scene.
    pub fn complete_frame(&mut self) {
        if !self.frame_open {
            return;
        }
        self.frame_open = false;
        std::mem::swap(&mut self.published, &mut self.building);
        self.published_frustum = self.building_frustum.take();
        self.building.clear();
        self.building_keys.clear();
        self.resolved = None;
    }

    /// The frame is dropped; the last completed one stays pickable.
    pub fn abort_frame(&mut self) {
        if !self.frame_open {
            return;
        }
        self.frame_open = false;
        self.building.clear();
        self.building_keys.clear();
        self.building_frustum = None;
    }

    /// The published parts with their shapes: a part whose model has no
    /// shape, or whose drawing sphere is outside the frame's frustum (or
    /// whose frame had none), is left out.
    fn parts(&mut self, geometry: &mut dyn SelectionGeometry) -> &[SelectionPart] {
        if self.resolved.is_none() {
            let frustum = self.published_frustum;
            let parts = self
                .published
                .iter()
                .filter_map(|p| {
                    let mesh = geometry.resolve(p.gfx_obj_id)?;
                    let frustum = frustum?;
                    drawing_sphere_in_frustum(&mesh, p.world, &frustum).then_some(SelectionPart {
                        guid: p.guid,
                        local_id: p.local_id,
                        part_index: p.part_index,
                        local_to_world: p.world,
                        mesh,
                    })
                })
                .collect();
            self.resolved = Some(parts);
        }
        self.resolved.as_deref().unwrap_or_default()
    }

    /// The object at pixel (`x`, `y`) through `camera`, leaving out
    /// `skip_guid`.
    pub fn pick(
        &mut self,
        x: f32,
        y: f32,
        camera: &PickCamera,
        skip_guid: u32,
        geometry: &mut dyn SelectionGeometry,
    ) -> Option<SelectionHit> {
        if camera.width <= 0.0 || camera.height <= 0.0 {
            return None;
        }
        let (origin, direction) = picker::ray_through(
            x,
            y,
            camera.width,
            camera.height,
            camera.inverse_view_projection,
            camera.eye,
        );
        picker::pick(origin, direction, self.parts(geometry), skip_guid)
    }
}

/// Whether a part's drawing sphere, placed at `world` (its radius scaled by
/// the largest axis scale), touches the frustum.
#[must_use]
pub fn drawing_sphere_in_frustum(
    mesh: &SelectionMesh,
    world: Mat4,
    frustum: &FrustumPlanes,
) -> bool {
    let center = world.transform_point3(mesh.sphere_center);
    let scale = world
        .x_axis
        .truncate()
        .length()
        .max(world.y_axis.truncate().length())
        .max(world.z_axis.truncate().length());
    let radius = mesh.sphere_radius * scale;
    let test = |p: Vec4| p.x * center.x + p.y * center.y + p.z * center.z + p.w >= -radius;
    test(frustum.left)
        && test(frustum.right)
        && test(frustum.bottom)
        && test(frustum.top)
        && test(frustum.near)
        && test(frustum.far)
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Rendering/RetailSelectionSceneTests.cs`
    //! and `RetailSelectionAssetTests.DrawingSphereViewconeRejectsPartBehindCamera`.

    use super::*;
    use crate::selection::pulse::FLIP_INTERVAL_SECONDS;

    const MODEL: u32 = 0x0100_0001;

    struct Square;

    impl SelectionGeometry for Square {
        fn resolve(&mut self, gfx_obj_id: u32) -> Option<Arc<SelectionMesh>> {
            (gfx_obj_id == MODEL).then(|| {
                Arc::new(SelectionMesh {
                    sphere_center: Vec3::ZERO,
                    sphere_radius: 2.0,
                    polygons: vec![SelectionPolygon {
                        vertices: vec![
                            Vec3::new(-1.0, -1.0, 0.0),
                            Vec3::new(1.0, -1.0, 0.0),
                            Vec3::new(1.0, 1.0, 0.0),
                            Vec3::new(-1.0, 1.0, 0.0),
                        ],
                        single_sided: false,
                    }],
                })
            })
        }
    }

    fn camera() -> (Mat4, Mat4) {
        let view = glam::camera::rh::view::look_at_mat4(Vec3::ZERO, Vec3::NEG_Z, Vec3::Y);
        let projection = glam::camera::rh::proj::directx::perspective(
            std::f32::consts::PI / 3.0,
            4.0 / 3.0,
            0.1,
            100.0,
        );
        (view, projection)
    }

    fn frustum() -> FrustumPlanes {
        let (view, projection) = camera();
        FrustumPlanes::from_view_projection(projection * view)
    }

    fn publish(scene: &mut SelectionScene, guid: u32, local_id: u32, now: f64) {
        scene.begin_frame(Some(frustum()), now);
        scene.add_visible_part(
            guid,
            local_id,
            0,
            MODEL,
            Mat4::from_translation(Vec3::new(0.0, 0.0, -5.0)),
        );
        scene.complete_frame();
    }

    fn pick_center(scene: &mut SelectionScene) -> Option<SelectionHit> {
        let (view, projection) = camera();
        let pick_camera = PickCamera {
            inverse_view_projection: (projection * view).inverse(),
            eye: Vec3::ZERO,
            width: 800.0,
            height: 600.0,
        };
        scene.pick(400.0, 300.0, &pick_camera, 0, &mut Square)
    }

    fn run_empty_world_frame(scene: &mut SelectionScene, now: f64) {
        scene.begin_frame(None, now);
        scene.complete_frame();
    }

    #[test]
    fn a_published_hit_carries_the_objects_identity() {
        let mut scene = SelectionScene::new();
        publish(&mut scene, 0x5000_0044, 44, 0.0);
        let hit = pick_center(&mut scene).unwrap();
        assert_eq!(hit.guid, 0x5000_0044);
        assert_eq!(hit.local_id, 44);
    }

    #[test]
    fn reset_clears_the_published_frame_and_the_flash() {
        let mut scene = SelectionScene::new();
        publish(&mut scene, 0x5000_0045, 45, 1.0);
        scene.begin_lighting_pulse(0x5000_0045, 45, 1.0);
        scene.reset();
        assert!(pick_center(&mut scene).is_none());
        assert!(scene.lighting(0x5000_0045, 45).is_none());
    }

    #[test]
    fn an_aborted_frame_keeps_the_published_one() {
        let mut scene = SelectionScene::new();
        publish(&mut scene, 0x5000_0046, 46, 0.0);
        scene.begin_frame(Some(frustum()), 0.0);
        scene.add_visible_part(
            0x5000_0047,
            47,
            0,
            MODEL,
            Mat4::from_translation(Vec3::new(0.0, 0.0, -5.0)),
        );
        scene.abort_frame();
        assert_eq!(pick_center(&mut scene).unwrap().local_id, 46);
    }

    #[test]
    fn parts_outside_a_world_frame_do_not_enter_the_pick() {
        let mut scene = SelectionScene::new();
        publish(&mut scene, 0x5000_0049, 49, 0.0);
        // A private viewport's part, drawn with no world frame open.
        scene.add_visible_part(
            0x5000_0050,
            50,
            0,
            MODEL,
            Mat4::from_translation(Vec3::new(0.0, 0.0, -5.0)),
        );
        assert_eq!(pick_center(&mut scene).unwrap().local_id, 49);
    }

    #[test]
    fn the_world_frame_alone_runs_the_flash_through_and_ends_it() {
        let mut scene = SelectionScene::new();
        let mut now = 10.0;
        scene.begin_lighting_pulse(0x5000_0060, 60, now);
        assert_eq!(
            scene.lighting(0x5000_0060, 60),
            Some(SelectionLighting::HIGH)
        );
        for step in [
            SelectionLighting::LOW,
            SelectionLighting::HIGH,
            SelectionLighting::LOW,
        ] {
            now += FLIP_INTERVAL_SECONDS;
            run_empty_world_frame(&mut scene, now);
            assert_eq!(scene.lighting(0x5000_0060, 60), Some(step));
        }
        now += FLIP_INTERVAL_SECONDS;
        run_empty_world_frame(&mut scene, now);
        assert!(scene.lighting(0x5000_0060, 60).is_none());
    }

    #[test]
    fn the_world_frame_alone_ends_the_figure_flash_on_the_dolls_schedule() {
        let mut scene = SelectionScene::new();
        let mut now = 3.0;
        scene.begin_part_lighting_pulse(0x5000_0061, 61, 0b101, now);
        assert!(scene.has_part_lighting(0x5000_0061, 61));
        now += FLIP_INTERVAL_SECONDS;
        run_empty_world_frame(&mut scene, now);
        assert_eq!(
            scene.part_lighting(0x5000_0061, 61, 2),
            Some(SelectionLighting::LOW)
        );
        now += FLIP_INTERVAL_SECONDS;
        run_empty_world_frame(&mut scene, now);
        assert!(!scene.has_part_lighting(0x5000_0061, 61));
    }

    #[test]
    fn frames_inside_one_interval_do_not_rush_the_flash() {
        let mut scene = SelectionScene::new();
        let mut now = 5.0;
        scene.begin_lighting_pulse(0x5000_0062, 62, now);
        for _ in 0..30 {
            now += FLIP_INTERVAL_SECONDS / 40.0;
            run_empty_world_frame(&mut scene, now);
        }
        assert_eq!(
            scene.lighting(0x5000_0062, 62),
            Some(SelectionLighting::HIGH)
        );
    }

    #[test]
    fn the_drawing_sphere_test_rejects_a_part_behind_the_camera() {
        let mesh = SelectionMesh {
            sphere_center: Vec3::ZERO,
            sphere_radius: 1.0,
            polygons: vec![],
        };
        let f = frustum();
        assert!(drawing_sphere_in_frustum(
            &mesh,
            Mat4::from_translation(Vec3::new(0.0, 0.0, -5.0)),
            &f
        ));
        assert!(!drawing_sphere_in_frustum(
            &mesh,
            Mat4::from_translation(Vec3::new(0.0, 0.0, 5.0)),
            &f
        ));
    }
}
