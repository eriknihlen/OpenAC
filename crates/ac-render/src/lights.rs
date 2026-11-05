//! The window's lights: the sun and ambient from the sky, the lights of
//! static objects (torches, lamps placed in the landblocks) and of server
//! objects, the player's fill light, and for each drawn object the up to
//! eight point lights that reach it.
//!
//! The game lights an object either by the sun or by torches, never both:
//! an object standing in a cell inside a building or dungeon gets its
//! nearest point lights and no sun; an object outside gets the sun and no
//! point lights.

use std::collections::HashMap;

use ac_core::lighting::{
    FLOATS_PER_LIGHT, LightId, LightInfoSource, LightManager, LightOwnerPlacement,
    MAX_DYNAMIC_POINT_LIGHTS, MAX_LIGHTS_PER_OBJECT, MAX_STATIC_POINT_LIGHTS, SceneLightingUbo,
    load_lights, pack_global_lights, select_for_object,
};
use ac_core::sky::{AtmosphereSnapshot, SkyKeyframe};
use glam::{Mat4, Vec3};

use crate::world_frame::LandblockCoord;

/// Point lights the GPU holds per frame: the 7 dynamic and 40 static of the
/// snapshot, rounded up.
pub const GPU_POINT_LIGHTS: usize = 48;

/// Whether an object in `cell_id` is lit by torches (and not the sun): it is
/// in an interior cell (`0x0100` and up), not the landblock marker
/// (`0xFFFF`) or an outdoor cell.
#[must_use]
pub fn indoor_object_receives_torches(cell_id: Option<u32>) -> bool {
    cell_id.is_some_and(|c| {
        let low = c & 0xFFFF;
        (0x0100..0xFFFF).contains(&low)
    })
}

/// Per-instance light indices: positions in the uploaded point lights, -1
/// unused.
pub type LightSet = [i32; MAX_LIGHTS_PER_OBJECT];

/// No point lights.
pub const NO_LIGHTS: LightSet = [-1; MAX_LIGHTS_PER_OBJECT];

/// A static object's light owner: where it stands in the drawing frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StaticLightOwner {
    /// Its placement (rotation and translation), drawing frame.
    pub transform: Mat4,
    /// The interior cell it stands in; 0 outdoors.
    pub cell_id: u32,
}

/// The window's light state. See the module documentation.
#[derive(Debug, Default)]
pub struct WindowLights {
    manager: LightManager,
    statics: HashMap<LandblockCoord, Vec<LightId>>,
    objects: HashMap<u32, Vec<LightId>>,
    /// Each server object's lights on or off (OpenAC `LightingHookSink`'s
    /// per-owner state): first from its lighting state bit, then from the
    /// bit's changes and the "set light" hooks.
    lit: HashMap<u32, bool>,
    /// The lighting state bit last seen per object.
    lighting_bit: HashMap<u32, bool>,
    packed: Vec<f32>,
}

/// The physics state bit that turns an object's lights on.
pub const LIGHTING_STATE: u32 = 0x0000_0800;
/// The physics state bit of an object that never moves (its lights are
/// static lights).
pub const STATIC_STATE: u32 = 0x0000_0001;

impl WindowLights {
    /// No lights.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The light manager (diagnostics, tests).
    #[must_use]
    pub fn manager(&self) -> &LightManager {
        &self.manager
    }

    /// Whether a landblock's static lights are registered.
    #[must_use]
    pub fn has_static_block(&self, block: LandblockCoord) -> bool {
        self.statics.contains_key(&block)
    }

    /// Register a landblock's static lights: each owner with its model's
    /// lights. Replaces what the landblock had.
    pub fn register_static_block(
        &mut self,
        block: LandblockCoord,
        owners: impl IntoIterator<Item = (StaticLightOwner, Vec<LightInfoSource>)>,
    ) {
        self.drop_static_block(block);
        let mut ids = Vec::new();
        for (owner, infos) in owners {
            if infos.is_empty() {
                continue;
            }
            let (_, rotation, position) = owner.transform.to_scale_rotation_translation();
            let placement = LightOwnerPlacement {
                owner_id: 0,
                position,
                rotation,
                is_dynamic: false,
                cell_id: owner.cell_id,
                tracks_owner_pose: false,
            };
            for light in load_lights(&infos, &placement) {
                if let Ok(id) = self.manager.register(light) {
                    ids.push(id);
                }
            }
        }
        self.statics.insert(block, ids);
    }

    /// Forget a landblock's static lights.
    pub fn drop_static_block(&mut self, block: LandblockCoord) {
        for id in self.statics.remove(&block).unwrap_or_default() {
            self.manager.unregister(id);
        }
    }

    /// Keep only the landblocks `keep` accepts.
    pub fn retain_static_blocks(&mut self, mut keep: impl FnMut(LandblockCoord) -> bool) {
        let gone: Vec<LandblockCoord> =
            self.statics.keys().copied().filter(|b| !keep(*b)).collect();
        for block in gone {
            self.drop_static_block(block);
        }
    }

    /// Follow a server object's physics state: the first time its lights'
    /// on/off state starts from the lighting bit; a later change of the bit
    /// switches them (OpenAC `LiveEntityLightController.Register` and
    /// `OnStateChanged`).
    pub fn follow_state(&mut self, local_id: u32, physics_state: u32) {
        let bit = physics_state & LIGHTING_STATE != 0;
        match self.lighting_bit.insert(local_id, bit) {
            None => {
                self.lit.entry(local_id).or_insert(bit);
            }
            Some(previous) if previous != bit => {
                self.set_owner_lighting(local_id, bit);
            }
            Some(_) => {}
        }
    }

    /// Switch a server object's lights on or off (its lighting bit or a
    /// "set light" hook). Off removes them; on registers them again at the
    /// next placement. Returns whether the state changed.
    pub fn set_owner_lighting(&mut self, local_id: u32, on: bool) -> bool {
        if self.lit.get(&local_id) == Some(&on) {
            return false;
        }
        self.lit.insert(local_id, on);
        if !on {
            self.drop_object(local_id);
        }
        true
    }

    /// Whether a server object's lights are on (on unless switched off).
    #[must_use]
    pub fn owner_lit(&self, local_id: u32) -> bool {
        self.lit.get(&local_id).copied().unwrap_or(true)
    }

    /// Place a server object's lights (following the object; dynamic
    /// unless `is_dynamic` is false) at `transform` (rotation and
    /// translation, drawing frame) in `cell_id`; registered the first time,
    /// and not at all while the object's lights are off.
    pub fn place_object(
        &mut self,
        local_id: u32,
        transform: Mat4,
        cell_id: u32,
        infos: &[LightInfoSource],
        is_dynamic: bool,
    ) {
        if !self.owner_lit(local_id) {
            self.drop_object(local_id);
            return;
        }
        let (_, rotation, position) = transform.to_scale_rotation_translation();
        let placement = LightOwnerPlacement {
            owner_id: local_id,
            position,
            rotation,
            is_dynamic,
            cell_id,
            tracks_owner_pose: true,
        };
        let placed = load_lights(infos, &placement);
        match self.objects.get(&local_id) {
            Some(ids) if ids.len() == placed.len() => {
                for (id, light) in ids.iter().zip(placed) {
                    if let Some(current) = self.manager.get_mut(*id) {
                        current.world_position = light.world_position;
                        current.world_forward = light.world_forward;
                        current.ranking_origin = light.ranking_origin;
                        current.cell_id = cell_id;
                    }
                }
            }
            _ => {
                self.drop_object(local_id);
                let ids = placed
                    .into_iter()
                    .filter_map(|l| self.manager.register(l).ok())
                    .collect();
                self.objects.insert(local_id, ids);
            }
        }
    }

    /// Forget a server object's lights.
    pub fn drop_object(&mut self, local_id: u32) {
        for id in self.objects.remove(&local_id).unwrap_or_default() {
            self.manager.unregister(id);
        }
    }

    /// Forget the light state of server objects `keep` does not accept
    /// (they left the world).
    pub fn retain_object_states(&mut self, mut keep: impl FnMut(u32) -> bool) {
        self.lit.retain(|id, _| keep(*id));
        self.lighting_bit.retain(|id, _| keep(*id));
    }

    /// Forget every server object's lights but those `keep` accepts.
    pub fn retain_objects(&mut self, mut keep: impl FnMut(u32) -> bool) {
        let gone: Vec<u32> = self
            .objects
            .keys()
            .copied()
            .filter(|id| !keep(*id))
            .collect();
        for id in gone {
            self.drop_object(id);
        }
    }

    /// Forget every static and object light (the drawing frame moved).
    pub fn clear_placed(&mut self) {
        let blocks: Vec<LandblockCoord> = self.statics.keys().copied().collect();
        for block in blocks {
            self.drop_static_block(block);
        }
        let objects: Vec<u32> = self.objects.keys().copied().collect();
        for id in objects {
            self.drop_object(id);
        }
    }

    /// This frame's lighting: the sun and ambient from `keyframe` (black
    /// sun and flat ambient when the player is in a sealed cell), the fill
    /// light above the player, the active set around the camera and the
    /// point snapshot around the player. Returns the scene block and fills
    /// the point lights to upload ([`Self::point_floats`]).
    pub fn frame(
        &mut self,
        keyframe: &SkyKeyframe,
        atmosphere: &AtmosphereSnapshot,
        player_inside_cell: bool,
        player: Option<Vec3>,
        camera: Vec3,
        day_fraction: f32,
    ) -> SceneLightingUbo {
        self.manager
            .update_sun_from_sky(keyframe, player_inside_cell);
        if let Some(player) = player {
            self.manager.update_viewer_light(player);
        }
        self.manager.tick(camera);
        self.manager
            .build_point_light_snapshot(player.unwrap_or(camera));
        let count = pack_global_lights(self.manager.point_snapshot_lights(), &mut self.packed);
        debug_assert!(count <= MAX_DYNAMIC_POINT_LIGHTS + MAX_STATIC_POINT_LIGHTS);
        SceneLightingUbo::build(&self.manager, atmosphere, camera, day_fraction)
    }

    /// The point snapshot as the GPU reads it: [`GPU_POINT_LIGHTS`] lights
    /// of 16 floats, unused ones zero.
    #[must_use]
    pub fn point_floats(&self) -> Vec<f32> {
        let mut out = vec![0.0; GPU_POINT_LIGHTS * FLOATS_PER_LIGHT];
        let used = self.manager.point_snapshot().len().min(GPU_POINT_LIGHTS) * FLOATS_PER_LIGHT;
        out[..used].copy_from_slice(&self.packed[..used]);
        out
    }

    /// The point lights reaching an object in `cell_id` whose bounds are
    /// the sphere at `center` with `radius`: none outside.
    #[must_use]
    pub fn object_lights(&self, cell_id: Option<u32>, center: Vec3, radius: f32) -> LightSet {
        if !indoor_object_receives_torches(cell_id) {
            return NO_LIGHTS;
        }
        let mut indices = [0usize; MAX_LIGHTS_PER_OBJECT];
        let n = select_for_object(
            self.manager.point_snapshot_lights(),
            center,
            radius,
            &mut indices,
        );
        let mut set = NO_LIGHTS;
        for (slot, index) in set.iter_mut().zip(&indices[..n]) {
            if *index < GPU_POINT_LIGHTS {
                *slot = *index as i32;
            }
        }
        set
    }
}

#[cfg(test)]
mod tests {
    //! The torch gate after OpenAC `WbDrawDispatcherIndoorFlagTests`; the
    //! rest checks the wiring of `ac_core::lighting`, whose rules are tested
    //! there.
    use super::*;

    #[test]
    fn only_objects_in_interior_cells_receive_torches() {
        assert!(indoor_object_receives_torches(Some(0xA9B4_0100)));
        assert!(indoor_object_receives_torches(Some(0x0007_0120)));
        assert!(!indoor_object_receives_torches(Some(0xA9B4_0040)));
        assert!(!indoor_object_receives_torches(Some(0xA9B4_00FF)));
        assert!(!indoor_object_receives_torches(Some(0xA9B4_FFFF)));
        assert!(!indoor_object_receives_torches(None));
    }

    fn torch() -> LightInfoSource {
        LightInfoSource {
            origin: Vec3::new(0.0, 0.0, 1.0),
            orientation: glam::Quat::IDENTITY,
            color: [255, 128, 64],
            intensity: 2.0,
            falloff: 5.0,
            cone_angle: 0.0,
        }
    }

    fn sky() -> (SkyKeyframe, AtmosphereSnapshot) {
        let kf = ac_core::sky::SkyStateProvider::default_sky().interpolate(0.5);
        let atmosphere = ac_core::sky::WeatherSystem::new().snapshot(&kf, false);
        (kf, atmosphere)
    }

    #[test]
    fn an_indoor_object_near_a_torch_gets_it_and_an_outdoor_one_does_not() {
        let mut lights = WindowLights::new();
        let block = LandblockCoord { x: 0xA9, y: 0xB4 };
        lights.register_static_block(
            block,
            [(
                StaticLightOwner {
                    transform: Mat4::from_translation(Vec3::new(10.0, 10.0, 0.0)),
                    cell_id: 0,
                },
                vec![torch()],
            )],
        );
        let (kf, atmosphere) = sky();
        let ubo = lights.frame(&kf, &atmosphere, false, Some(Vec3::ZERO), Vec3::ZERO, 0.5);
        assert!(ubo.cell_ambient[3] >= 1.0, "the sun is active");
        let floats = lights.point_floats();
        assert_eq!(floats.len(), GPU_POINT_LIGHTS * FLOATS_PER_LIGHT);
        // The fill light first (dynamic), then the torch.
        assert_eq!(lights.manager().point_snapshot().len(), 2);
        let near = Vec3::new(10.0, 11.0, 1.0);
        let set = lights.object_lights(Some(0xA9B4_0105), near, 1.0);
        assert!(set.contains(&1), "{set:?}");
        assert_eq!(
            lights.object_lights(Some(0xA9B4_0031), near, 1.0),
            NO_LIGHTS
        );
        lights.drop_static_block(block);
        lights.frame(&kf, &atmosphere, false, Some(Vec3::ZERO), Vec3::ZERO, 0.5);
        assert_eq!(lights.manager().point_snapshot().len(), 1);
    }

    #[test]
    fn an_object_light_follows_its_owner() {
        let mut lights = WindowLights::new();
        lights.place_object(
            7,
            Mat4::from_translation(Vec3::X),
            0xA9B4_0105,
            &[torch()],
            true,
        );
        lights.place_object(
            7,
            Mat4::from_translation(Vec3::Y * 3.0),
            0xA9B4_0105,
            &[torch()],
            true,
        );
        let (kf, atmosphere) = sky();
        lights.frame(&kf, &atmosphere, true, None, Vec3::ZERO, 0.5);
        let positions: Vec<Vec3> = lights
            .manager()
            .point_snapshot_lights()
            .map(|l| l.world_position)
            .collect();
        assert_eq!(positions, [Vec3::new(0.0, 3.0, 1.0)]);
        lights.retain_objects(|_| false);
        lights.frame(&kf, &atmosphere, true, None, Vec3::ZERO, 0.5);
        assert!(lights.manager().point_snapshot().is_empty());
    }

    fn lit_count(lights: &mut WindowLights) -> usize {
        let (kf, atmosphere) = sky();
        lights.frame(&kf, &atmosphere, true, None, Vec3::ZERO, 0.5);
        lights.manager().point_snapshot().len()
    }

    fn place(lights: &mut WindowLights, state: u32) {
        lights.follow_state(7, state);
        lights.place_object(
            7,
            Mat4::IDENTITY,
            0xA9B4_0105,
            &[torch()],
            state & STATIC_STATE == 0,
        );
    }

    #[test]
    fn an_object_has_lights_only_while_its_lighting_state_is_on() {
        // OpenAC LiveEntityLightController.Register (the lighting bit seeds
        // the owner's state) and OnStateChanged (a change of the bit
        // switches it).
        let mut lights = WindowLights::new();
        place(&mut lights, 0);
        assert_eq!(lit_count(&mut lights), 0);
        place(&mut lights, LIGHTING_STATE);
        assert_eq!(lit_count(&mut lights), 1);
        place(&mut lights, LIGHTING_STATE);
        assert_eq!(lit_count(&mut lights), 1);
        place(&mut lights, 0);
        assert_eq!(lit_count(&mut lights), 0);
    }

    #[test]
    fn a_set_light_hook_switches_an_objects_lights_until_the_state_changes() {
        // OpenAC LightingHookSink.OnHook / SetOwnerLighting and the
        // controller's OwnerLightingChanged: off unregisters, on registers
        // again; the state is kept while the object stays.
        let mut lights = WindowLights::new();
        place(&mut lights, LIGHTING_STATE);
        assert_eq!(lit_count(&mut lights), 1);
        assert!(lights.set_owner_lighting(7, false));
        assert!(!lights.set_owner_lighting(7, false));
        place(&mut lights, LIGHTING_STATE);
        assert_eq!(lit_count(&mut lights), 0, "the hook's off holds");
        assert!(lights.set_owner_lighting(7, true));
        place(&mut lights, LIGHTING_STATE);
        assert_eq!(lit_count(&mut lights), 1);
        // The object leaves: its state is forgotten, and it comes back from
        // its lighting bit.
        lights.set_owner_lighting(7, false);
        lights.retain_objects(|_| false);
        lights.retain_object_states(|_| false);
        place(&mut lights, LIGHTING_STATE);
        assert_eq!(lit_count(&mut lights), 1);
    }

    #[test]
    fn a_static_objects_lights_are_static() {
        let mut lights = WindowLights::new();
        place(&mut lights, LIGHTING_STATE | STATIC_STATE);
        lit_count(&mut lights);
        assert!(
            lights
                .manager()
                .point_snapshot_lights()
                .all(|l| !l.is_dynamic)
        );
    }
}
