//! The flat float layouts lights are handed to the GPU in. Pure data; the
//! renderer uploads it.

use glam::Vec3;

use super::manager::{LightManager, MAX_ACTIVE_LIGHTS};
use super::source::LightSource;
use crate::sky::AtmosphereSnapshot;

/// Floats per light in the point-light buffer (four vec4).
pub const FLOATS_PER_LIGHT: usize = 16;

/// Write `snapshot` into `buffer` as 16 floats per light: position and kind,
/// forward and reach, colour and intensity, cone angle and the dynamic flag
/// (1 for dynamic). The buffer is grown when short (with room for 16 more
/// lights) and the used part is zeroed first; it always holds at least one
/// light's worth so an empty frame still uploads something. Returns the
/// light count; upload `max(count, 1) × 16` floats.
pub fn pack_global_lights<'a, I>(snapshot: I, buffer: &mut Vec<f32>) -> usize
where
    I: IntoIterator<Item = &'a LightSource>,
    I::IntoIter: ExactSizeIterator,
{
    let lights = snapshot.into_iter();
    let n = lights.len();
    let floats_needed = n.max(1) * FLOATS_PER_LIGHT;
    if buffer.len() < floats_needed {
        buffer.resize(floats_needed + FLOATS_PER_LIGHT * 16, 0.0);
    }
    buffer[..floats_needed].fill(0.0);

    for (i, light) in lights.enumerate() {
        let o = i * FLOATS_PER_LIGHT;
        buffer[o] = light.world_position.x;
        buffer[o + 1] = light.world_position.y;
        buffer[o + 2] = light.world_position.z;
        buffer[o + 3] = light.kind as i32 as f32;
        buffer[o + 4] = light.world_forward.x;
        buffer[o + 5] = light.world_forward.y;
        buffer[o + 6] = light.world_forward.z;
        buffer[o + 7] = light.range;
        buffer[o + 8] = light.color_linear.x;
        buffer[o + 9] = light.color_linear.y;
        buffer[o + 10] = light.color_linear.z;
        buffer[o + 11] = light.intensity;
        buffer[o + 12] = light.cone_angle;
        buffer[o + 13] = if light.is_dynamic { 1.0 } else { 0.0 };
    }
    n
}

/// One light of the scene-lighting block (std140: four vec4, 64 bytes).
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct UboLight {
    /// xyz position, w kind.
    pub pos_and_kind: [f32; 4],
    /// xyz forward, w reach.
    pub dir_and_range: [f32; 4],
    /// xyz colour, w intensity.
    pub color_and_intensity: [f32; 4],
    /// x cone angle; yzw zero.
    pub cone_angle_etc: [f32; 4],
}

impl UboLight {
    /// The block entry for a light.
    #[must_use]
    pub fn from_source(light: &LightSource) -> Self {
        let v4 = |v: Vec3, w: f32| [v.x, v.y, v.z, w];
        Self {
            pos_and_kind: v4(light.world_position, light.kind as i32 as f32),
            dir_and_range: v4(light.world_forward, light.range),
            color_and_intensity: v4(light.color_linear, light.intensity),
            cone_angle_etc: [light.cone_angle, 0.0, 0.0, 0.0],
        }
    }
}

/// The per-frame scene-lighting block every shader reads: the active set,
/// ambient, fog, camera and time (std140, 576 bytes).
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct SceneLightingUbo {
    /// The active set; unused slots are zero.
    pub lights: [UboLight; MAX_ACTIVE_LIGHTS],
    /// xyz ambient colour, w active light count.
    pub cell_ambient: [f32; 4],
    /// x fog start, y fog end, z lightning flash, w fog mode.
    pub fog_params: [f32; 4],
    /// xyz fog colour.
    pub fog_color: [f32; 4],
    /// xyz camera position, w day fraction.
    pub camera_and_time: [f32; 4],
}

impl SceneLightingUbo {
    /// Size in bytes.
    pub const SIZE_IN_BYTES: usize = MAX_ACTIVE_LIGHTS * 64 + 4 * 16;
    /// Shader binding point.
    pub const BINDING_POINT: u32 = 1;
    /// Size in floats.
    pub const FLOATS: usize = Self::SIZE_IN_BYTES / 4;

    /// Build the block from the active set (call [`LightManager::tick`]
    /// first), the atmosphere, the camera and the day fraction.
    #[must_use]
    pub fn build(
        lights: &LightManager,
        atmosphere: &AtmosphereSnapshot,
        camera_world_pos: Vec3,
        day_fraction: f32,
    ) -> Self {
        let mut ubo = Self::default();
        let mut count = 0usize;
        for (slot, light) in ubo.lights.iter_mut().zip(lights.active()) {
            *slot = UboLight::from_source(light);
            count += 1;
        }
        let ambient = lights.current_ambient().ambient_color;
        ubo.cell_ambient = [ambient.x, ambient.y, ambient.z, count as f32];
        ubo.fog_params = [
            atmosphere.fog_start,
            atmosphere.fog_end,
            atmosphere.lightning_flash,
            atmosphere.fog_mode as i32 as f32,
        ];
        let fog = atmosphere.fog_color;
        ubo.fog_color = [fog.x, fog.y, fog.z, 0.0];
        ubo.camera_and_time = [
            camera_world_pos.x,
            camera_world_pos.y,
            camera_world_pos.z,
            day_fraction,
        ];
        ubo
    }

    /// The block as floats in memory order, ready to upload.
    #[must_use]
    pub fn to_floats(&self) -> [f32; Self::FLOATS] {
        let mut out = [0.0f32; Self::FLOATS];
        let mut o = 0;
        let mut put = |v: &[f32; 4]| {
            out[o..o + 4].copy_from_slice(v);
            o += 4;
        };
        for light in &self.lights {
            put(&light.pos_and_kind);
            put(&light.dir_and_range);
            put(&light.color_and_intensity);
            put(&light.cone_angle_etc);
        }
        put(&self.cell_ambient);
        put(&self.fog_params);
        put(&self.fog_color);
        put(&self.camera_and_time);
        out
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `GlobalLightPackerTests` and `SceneLightingUboTests`.
    use super::*;
    use crate::lighting::LightKind;
    use crate::sky::{EnvironOverride, FogMode, WeatherKind};

    #[test]
    fn pack_writes_sixteen_floats_per_light_in_the_expected_layout() {
        let light = LightSource {
            kind: LightKind::Point,
            world_position: Vec3::new(10.0, 20.0, 30.0),
            world_forward: Vec3::Z,
            color_linear: Vec3::new(1.0, 0.588, 0.314),
            intensity: 100.0,
            range: 5.2,
            cone_angle: 0.0,
            ..LightSource::default()
        };
        let mut buffer = Vec::new();
        assert_eq!(pack_global_lights([&light], &mut buffer), 1);
        assert!(buffer.len() >= 16);
        assert_eq!(
            buffer[..13],
            [
                10.0, 20.0, 30.0, 1.0, 0.0, 0.0, 1.0, 5.2, 1.0, 0.588, 0.314, 100.0, 0.0
            ]
        );
        assert_eq!(buffer[13], 0.0, "static");
    }

    #[test]
    fn pack_empty_returns_zero_and_keeps_one_slot() {
        let mut buffer = Vec::new();
        assert_eq!(pack_global_lights(std::iter::empty(), &mut buffer), 0);
        assert!(buffer.len() >= FLOATS_PER_LIGHT);
    }

    #[test]
    fn block_sizes_match_the_shader_layout() {
        assert_eq!(std::mem::size_of::<UboLight>(), 64);
        assert_eq!(
            std::mem::size_of::<SceneLightingUbo>(),
            SceneLightingUbo::SIZE_IN_BYTES
        );
        assert_eq!(SceneLightingUbo::SIZE_IN_BYTES, 576);
    }

    fn atmosphere(fog_color: Vec3, start: f32, end: f32, mode: FogMode) -> AtmosphereSnapshot {
        AtmosphereSnapshot {
            kind: WeatherKind::Clear,
            intensity: 1.0,
            fog_color,
            fog_start: start,
            fog_end: end,
            fog_mode: mode,
            lightning_flash: 0.0,
            environ_override: EnvironOverride::None,
        }
    }

    #[test]
    fn build_packs_active_lights_in_order() {
        let mut lights = LightManager::new();
        lights
            .register(
                LightSource {
                    kind: LightKind::Point,
                    world_position: Vec3::new(1.0, 2.0, 3.0),
                    color_linear: Vec3::new(1.0, 0.5, 0.25),
                    intensity: 0.8,
                    range: 6.0,
                    ..LightSource::default()
                }
                .with_ranking_origin(Vec3::new(1.0, 2.0, 3.0)),
            )
            .unwrap();
        lights.tick(Vec3::ZERO);
        let atmo = atmosphere(Vec3::new(0.7, 0.8, 0.9), 100.0, 400.0, FogMode::Linear);

        let ubo = SceneLightingUbo::build(&lights, &atmo, Vec3::new(10.0, 20.0, 30.0), 0.5);

        assert_eq!(ubo.lights[0].pos_and_kind, [1.0, 2.0, 3.0, 1.0]);
        assert_eq!(ubo.lights[0].dir_and_range[3], 6.0);
        assert_eq!(ubo.lights[0].color_and_intensity[3], 0.8);
        assert_eq!(ubo.lights[1].dir_and_range[3], 0.0);
        assert_eq!(ubo.cell_ambient[3], 1.0);
        assert_eq!(ubo.fog_params, [100.0, 400.0, 0.0, 1.0]);
        assert_eq!(ubo.camera_and_time[0], 10.0);
        assert_eq!(ubo.camera_and_time[3], 0.5);
        let floats = ubo.to_floats();
        assert_eq!(floats[..4], [1.0, 2.0, 3.0, 1.0]);
        assert_eq!(floats[128..132], ubo.cell_ambient);
        assert_eq!(floats[132..136], [100.0, 400.0, 0.0, 1.0]);
    }

    #[test]
    fn build_clamps_at_eight_lights() {
        let mut lights = LightManager::new();
        for i in 0..20 {
            let p = Vec3::new(i as f32, 0.0, 0.0);
            lights
                .register(
                    LightSource {
                        world_position: p,
                        range: 200.0,
                        ..LightSource::default()
                    }
                    .with_ranking_origin(p),
                )
                .unwrap();
        }
        lights.tick(Vec3::ZERO);
        let ubo = SceneLightingUbo::build(
            &lights,
            &atmosphere(Vec3::ZERO, 0.0, 0.0, FogMode::Off),
            Vec3::ZERO,
            0.0,
        );
        assert_eq!(ubo.cell_ambient[3], 8.0);
        assert_ne!(ubo.lights[7].dir_and_range[3], 0.0);
    }

    #[test]
    fn build_with_a_sun_puts_it_in_slot_zero() {
        let mut lights = LightManager::new();
        lights.set_sun(Some(LightSource {
            kind: LightKind::Directional,
            world_forward: -Vec3::Z,
            color_linear: Vec3::new(1.0, 0.9, 0.8),
            intensity: 1.2,
            ..LightSource::default()
        }));
        lights.tick(Vec3::ZERO);
        let ubo = SceneLightingUbo::build(
            &lights,
            &atmosphere(Vec3::ZERO, 0.0, 0.0, FogMode::Off),
            Vec3::ZERO,
            0.0,
        );
        assert_eq!(ubo.lights[0].pos_and_kind[3], 0.0);
        assert_eq!(ubo.lights[0].color_and_intensity[3], 1.2);
    }
}
