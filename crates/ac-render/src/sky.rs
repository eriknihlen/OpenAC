//! The sky: which sky objects (the dome, clouds, sun, moons, stars and
//! weather layers) are up at a time of day, how each is turned and faded,
//! and the projection they are drawn with. Pure rules; the renderer draws
//! what [`sky_draws`] lists.
//!
//! The sky is drawn around the camera (the view without its translation),
//! with depth testing off, before the world; weather layers marked "post
//! scene" are drawn after it.

use ac_core::sky::{DayGroupData, SkyObjectReplaceData};
use glam::{Mat4, Vec2};

/// Near plane of the sky projection, meters.
pub const SKY_NEAR: f32 = 0.1;
/// Far plane of the sky projection, meters (the sky meshes are large).
pub const SKY_FAR: f32 = 1_000_000.0;

/// Sky object property: hidden while the server's fog override is on.
const PROPERTY_HIDDEN_UNDER_FOG_OVERRIDE: u32 = 0x02;
/// Sky object property: a weather layer that stays centred on the camera
/// instead of sitting 120 m below it.
const PROPERTY_WEATHER_UNLOWERED: u32 = 0x08;
/// How far below the camera a weather layer sits, meters.
const WEATHER_DROP: f32 = 120.0;

/// One sky object to draw this frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SkyObjectDraw {
    /// Its model (`0x01` or `0x02`).
    pub model_id: u32,
    /// Object-to-sky transform.
    pub transform: Mat4,
    /// Texture scroll, wrapped to one repeat.
    pub uv_scroll: Vec2,
    /// Fade, 0 (visible) to 1 (gone).
    pub transparency: f32,
    /// Self-lighting replacing each surface's own, when the keyframe sets it.
    pub luminosity: Option<f32>,
    /// Diffuse factor replacing each surface's own, when the keyframe sets it.
    pub diffuse: Option<f32>,
    /// The texture scrolls, so it must repeat.
    pub scrolls: bool,
}

/// The keyframe overrides in force at `day_fraction`: those of the last
/// keyframe that began at or before it (the last keyframe before the
/// first).
#[must_use]
pub fn active_replaces(group: &DayGroupData, day_fraction: f32) -> &[SkyObjectReplaceData] {
    let Some(mut k1) = group.sky_times.last() else {
        return &[];
    };
    for time in &group.sky_times {
        if time.keyframe.begin <= day_fraction {
            k1 = time;
        } else {
            break;
        }
    }
    &k1.replaces
}

/// Whether the pass after the world (the weather) is drawn this frame: the
/// sky is known and the player stands in an outdoor land cell. Indoors the
/// weather is not drawn, not even through doorways and windows.
#[must_use]
pub fn weather_pass_open(sky_known: bool, player_cell: Option<u32>) -> bool {
    sky_known && player_cell.is_some_and(|cell| cell & 0xFFFF < 0x0100)
}

/// The sky objects of one pass (`post_scene` false: the sky before the
/// world; true: weather after it) at `day_fraction`, `seconds` after the
/// sky started (texture scroll), in the day group's order.
/// `environ_override` is the server's fog override being on;
/// `disable_weather` is the character option "disable most weather
/// effects", which drops the whole pass after the world and every weather
/// object.
#[must_use]
pub fn sky_draws(
    group: &DayGroupData,
    day_fraction: f32,
    seconds: f32,
    post_scene: bool,
    environ_override: bool,
    disable_weather: bool,
) -> Vec<SkyObjectDraw> {
    if post_scene && disable_weather {
        return Vec::new();
    }
    let replaces = active_replaces(group, day_fraction);
    let mut out = Vec::new();
    for (index, object) in group.sky_objects.iter().enumerate() {
        if object.is_post_scene() != post_scene || !object.is_visible(day_fraction) {
            continue;
        }
        if disable_weather && object.is_weather() {
            continue;
        }
        if environ_override && object.properties & PROPERTY_HIDDEN_UNDER_FOG_OVERRIDE != 0 {
            continue;
        }
        let mut model_id = object.gfx_obj_id;
        let mut heading_degrees = 0.0;
        let mut transparency = 0.0;
        let mut luminosity = None;
        let mut diffuse = None;
        if let Some(replace) = replaces
            .iter()
            .rev()
            .find(|r| r.object_index as usize == index)
        {
            if replace.gfx_obj_id != 0 {
                model_id = replace.gfx_obj_id;
            }
            if replace.rotate != 0.0 {
                heading_degrees = replace.rotate;
            }
            transparency = replace.transparent.clamp(0.0, 1.0);
            if replace.luminosity > 0.0 {
                luminosity = Some(replace.luminosity);
            }
            if replace.max_bright > 0.0 {
                diffuse = Some(replace.max_bright);
            }
        }
        if model_id == 0 {
            continue;
        }
        let rotation = object.current_angle(day_fraction).to_radians();
        let mut transform =
            Mat4::from_rotation_y(-rotation) * Mat4::from_rotation_z(-heading_degrees.to_radians());
        if post_scene && object.is_weather() && object.properties & PROPERTY_WEATHER_UNLOWERED == 0
        {
            transform =
                Mat4::from_translation(glam::Vec3::new(0.0, 0.0, -WEATHER_DROP)) * transform;
        }
        out.push(SkyObjectDraw {
            model_id,
            transform,
            uv_scroll: Vec2::new(
                (object.tex_velocity_x * seconds) % 1.0,
                (object.tex_velocity_y * seconds) % 1.0,
            ),
            transparency,
            luminosity,
            diffuse,
            scrolls: object.tex_velocity_x != 0.0 || object.tex_velocity_y != 0.0,
        });
    }
    out
}

/// The world projection with the sky's depth range: the same field of
/// view and shape, depth mapped from `near` to `far` (right-handed, depth
/// 0..1).
///
/// # Panics
/// When `0 < near < far` does not hold or `projection` is not perspective
/// (a programming error: the renderer passes its own constants).
#[must_use]
pub fn sky_projection(projection: Mat4, near: f32, far: f32) -> Mat4 {
    assert!(
        near.is_finite() && far.is_finite() && near > 0.0 && far > near,
        "sky depth range must be finite with 0 < near < far"
    );
    let mut result = projection;
    // Column-vector form: the w row reads -z for a right-handed projection.
    let w_from_z = projection.z_axis.w;
    if w_from_z < 0.0 {
        result.z_axis.z = far / (near - far);
        result.w_axis.z = near * far / (near - far);
    } else if w_from_z > 0.0 {
        result.z_axis.z = far / (far - near);
        result.w_axis.z = -near * far / (far - near);
    } else {
        panic!("the sky projection must be perspective");
    }
    result
}

/// The sky's view: the camera's rotation without its position, so the sky
/// never comes closer.
#[must_use]
pub fn sky_view(view: Mat4) -> Mat4 {
    let mut v = view;
    v.w_axis.x = 0.0;
    v.w_axis.y = 0.0;
    v.w_axis.z = 0.0;
    v
}

#[cfg(test)]
mod tests {
    //! `sky_projection` after OpenAC `SkyProjectionTests`; the pass rules
    //! after OpenAC `SkyRenderer.RenderPass` and `PickReplaces`.
    use super::*;
    use ac_core::sky::{DatSkyKeyframeData, SkyKeyframe, SkyObjectData};

    fn perspective(degrees: f32) -> Mat4 {
        glam::camera::rh::proj::directx::perspective(degrees.to_radians(), 16.0 / 9.0, 0.1, 5_000.0)
    }

    #[test]
    fn the_sky_projection_keeps_the_field_of_view() {
        for degrees in [60.0, 120.0, 179.885] {
            let active = perspective(degrees);
            let sky = sky_projection(active, 0.1, 1_000_000.0);
            assert_eq!(active.x_axis.x, sky.x_axis.x);
            assert_eq!(active.y_axis.y, sky.y_axis.y);
            assert_eq!(active.z_axis.w, sky.z_axis.w);
            assert_eq!(active.w_axis.w, sky.w_axis.w);
            assert!((sky.z_axis.z - 1_000_000.0 / (0.1 - 1_000_000.0)).abs() < 1e-6);
            assert!((sky.w_axis.z - 0.1 * 1_000_000.0 / (0.1 - 1_000_000.0)).abs() < 1e-6);
        }
    }

    #[test]
    fn the_sky_projection_keeps_off_centre_terms() {
        let mut active = perspective(60.0);
        active.z_axis.x = 0.2;
        active.z_axis.y = -0.1;
        let sky = sky_projection(active, 1.0, 10_000.0);
        assert_eq!((sky.z_axis.x, sky.z_axis.y), (0.2, -0.1));
    }

    #[test]
    #[should_panic(expected = "perspective")]
    fn an_orthographic_projection_is_refused() {
        let ortho = glam::camera::rh::proj::directx::orthographic(-5.0, 5.0, -5.0, 5.0, 0.1, 100.0);
        let _ = sky_projection(ortho, 0.1, 1_000.0);
    }

    #[test]
    fn the_sky_view_drops_the_camera_position() {
        let view =
            Mat4::from_rotation_z(0.3) * Mat4::from_translation(glam::Vec3::new(5.0, 6.0, 7.0));
        let sky = sky_view(view);
        assert_eq!(sky.w_axis.truncate(), glam::Vec3::ZERO);
        assert_eq!(sky.x_axis, view.x_axis);
    }

    fn object(gfx: u32, begin: f32, end: f32, properties: u32) -> SkyObjectData {
        SkyObjectData {
            begin_time: begin,
            end_time: end,
            begin_angle: 0.0,
            end_angle: 180.0,
            gfx_obj_id: gfx,
            properties,
            ..SkyObjectData::default()
        }
    }

    fn keyframe(begin: f32, replaces: Vec<SkyObjectReplaceData>) -> DatSkyKeyframeData {
        DatSkyKeyframeData {
            keyframe: SkyKeyframe {
                begin,
                ..SkyStateProviderDefault::keyframe()
            },
            replaces,
        }
    }

    struct SkyStateProviderDefault;
    impl SkyStateProviderDefault {
        fn keyframe() -> SkyKeyframe {
            ac_core::sky::SkyStateProvider::default_sky().interpolate(0.5)
        }
    }

    fn group() -> DayGroupData {
        DayGroupData {
            name: "Clear".into(),
            sky_objects: vec![
                object(0x0100_0001, 0.0, 0.0, 0),
                object(0x0100_0002, 0.25, 0.75, 0),
                object(0x0100_0003, 0.0, 0.0, 0x02),
                object(0x0100_0004, 0.0, 0.0, 0x01 | 0x04),
            ],
            sky_times: vec![
                keyframe(0.0, vec![]),
                keyframe(
                    0.5,
                    vec![SkyObjectReplaceData {
                        object_index: 0,
                        gfx_obj_id: 0x0100_0099,
                        rotate: 90.0,
                        transparent: 1.5,
                        luminosity: 0.4,
                        max_bright: 0.0,
                        ..SkyObjectReplaceData::default()
                    }],
                ),
            ],
            ..DayGroupData::default()
        }
    }

    #[test]
    fn objects_show_inside_their_window_and_by_pass() {
        let g = group();
        let ids = |t, post, fog| -> Vec<u32> {
            sky_draws(&g, t, 0.0, post, fog, false)
                .iter()
                .map(|d| d.model_id)
                .collect()
        };
        assert_eq!(ids(0.1, false, false), [0x0100_0001, 0x0100_0003]);
        assert_eq!(
            ids(0.3, false, false),
            [0x0100_0001, 0x0100_0002, 0x0100_0003]
        );
        assert_eq!(
            ids(0.3, false, true),
            [0x0100_0001, 0x0100_0002],
            "hidden under fog"
        );
        assert_eq!(ids(0.3, true, false), [0x0100_0004]);
    }

    #[test]
    fn the_keyframe_in_force_replaces_model_rotation_and_fade() {
        let g = group();
        let before = sky_draws(&g, 0.4, 0.0, false, false, false)[0];
        assert_eq!(before.model_id, 0x0100_0001);
        assert_eq!(before.transparency, 0.0);
        assert_eq!(before.luminosity, None);
        let after = sky_draws(&g, 0.6, 0.0, false, false, false)[0];
        assert_eq!(after.model_id, 0x0100_0099);
        assert_eq!(after.transparency, 1.0, "clamped");
        assert_eq!(after.luminosity, Some(0.4));
        assert_eq!(after.diffuse, None);
        // Heading 90 degrees about z (negated), then the object angle (0:
        // begin equals end) about y.
        let x = after.transform.transform_vector3(glam::Vec3::X);
        assert!((x - glam::Vec3::new(0.0, -1.0, 0.0)).length() < 1e-6);
    }

    #[test]
    fn a_weather_layer_sits_below_the_camera_unless_marked() {
        let g = group();
        let d = sky_draws(&g, 0.3, 0.0, true, false, false)[0];
        assert_eq!(d.transform.w_axis.z, -120.0);
        let mut g2 = group();
        g2.sky_objects[3].properties |= 0x08;
        let d = sky_draws(&g2, 0.3, 0.0, true, false, false)[0];
        assert_eq!(d.transform.w_axis.z, 0.0);
    }

    #[test]
    fn the_texture_scroll_wraps() {
        let mut g = group();
        g.sky_objects[0].tex_velocity_x = 0.3;
        let d = sky_draws(&g, 0.1, 5.0, false, false, false)[0];
        assert!((d.uv_scroll.x - 0.5).abs() < 1e-5);
        assert!(d.scrolls);
    }

    /// After OpenAC `RetailPViewPassExecutorTests.ShouldDrawWeatherOnce_MatchesRetailIsPlayerOutsideGate`.
    #[test]
    fn the_weather_pass_is_drawn_only_with_the_player_outdoors() {
        assert!(weather_pass_open(true, Some(0xF418_0003)));
        assert!(!weather_pass_open(true, Some(0xA9B4_0100)), "indoors");
        assert!(!weather_pass_open(false, Some(0xF418_0003)), "no sky");
        assert!(!weather_pass_open(true, None), "no player");
    }

    /// After OpenAC `WeatherEffectsRuleTests` and `SkyRenderer.RenderPass`:
    /// the option drops the whole pass after the world, and weather objects
    /// in the pass before it.
    #[test]
    fn disabling_most_weather_effects_drops_the_weather() {
        let mut g = group();
        // A post-scene object that is not weather, and a weather object
        // drawn before the world.
        g.sky_objects.push(object(0x0100_0005, 0.0, 0.0, 0x01));
        g.sky_objects.push(object(0x0100_0006, 0.0, 0.0, 0x04));
        let ids = |post, disable| -> Vec<u32> {
            sky_draws(&g, 0.3, 0.0, post, false, disable)
                .iter()
                .map(|d| d.model_id)
                .collect()
        };
        assert_eq!(ids(true, false), [0x0100_0004, 0x0100_0005]);
        assert!(ids(true, true).is_empty());
        assert_eq!(
            ids(false, false),
            [0x0100_0001, 0x0100_0002, 0x0100_0003, 0x0100_0006]
        );
        assert_eq!(ids(false, true), [0x0100_0001, 0x0100_0002, 0x0100_0003]);
    }

    #[test]
    fn the_last_keyframe_is_in_force_before_the_first_begins() {
        let mut g = group();
        g.sky_times[0].keyframe.begin = 0.2;
        assert_eq!(active_replaces(&g, 0.1).len(), 1);
        assert!(active_replaces(&g, 0.3).is_empty());
    }
}
