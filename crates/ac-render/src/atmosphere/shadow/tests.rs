//! Ported from OpenAC `tests/AcDream.App.Tests/Rendering/`:
//! `DirectionalShadowCascadeFitterTests`, `DirectionalShadowQualityTests`,
//! `DirectionalShadowReceiverTests`, `DirectionalShadowEnvironmentGateTests`
//! and `Packs/AuthoredCelestialShadowSourceResolverTests`.

use super::*;
use ac_core::sky::{
    DatSkyKeyframeData, EnvironOverride, FogMode, SkyObjectData, SkyObjectReplaceData,
};

fn perspective() -> Mat4 {
    glam::camera::rh::proj::directx::perspective(70f32.to_radians(), 16.0 / 9.0, 0.1, 5000.0)
}

fn camera_input(position: Vec3, quality: ShadowQuality) -> CascadeFitInput {
    let target = position + Vec3::new(1.0, 2.0, -0.2).normalize();
    let view = glam::camera::rh::view::look_at_mat4(position, target, Vec3::Z);
    CascadeFitInput::new(
        view,
        perspective(),
        Vec3::new(0.4, 0.7, 0.55).normalize(),
        quality,
    )
}

#[test]
fn fit_uses_practical_increasing_splits_and_the_exact_preset_reach() {
    for (preset, count, reach) in [
        (ShadowPreset::Low, 2, 72.0),
        (ShadowPreset::Medium, 3, 144.0),
        (ShadowPreset::High, 4, 240.0),
    ] {
        let input = camera_input(Vec3::ZERO, ShadowQuality::for_preset(preset));
        let cascades = fit(&input).unwrap();
        assert_eq!(cascades.len(), count);
        let mut previous = input.camera_near_meters;
        for c in &cascades {
            assert_eq!(c.split_near_meters, previous);
            assert!(c.split_far_meters > previous);
            assert!(c.texel_world_size > 0.0);
            assert!(c.world_to_shadow_clip.is_finite());
            previous = c.split_far_meters;
        }
        assert!((cascades[count - 1].split_far_meters - reach).abs() < 1e-3);
    }
}

#[test]
fn a_sub_texel_camera_move_keeps_the_snapped_center() {
    let quality = ShadowQuality::for_preset(ShadowPreset::Medium);
    let start = Vec3::new(100.0, 200.0, 30.0);
    let first = fit(&camera_input(start, quality)).unwrap();
    let light = Vec3::new(0.4, 0.7, 0.55).normalize();
    let light_x = stable_light_up(light).cross(light).normalize();
    let moved = start + light_x * (first[0].texel_world_size * 0.2);
    let second = fit(&camera_input(moved, quality)).unwrap();
    assert_eq!(
        first[0].stabilized_light_space_center,
        second[0].stabilized_light_space_center
    );
    assert_eq!(first[0].half_extent_meters, second[0].half_extent_meters);
}

#[test]
fn the_stable_up_does_not_turn_at_the_former_high_light_threshold() {
    let below = Vec3::new(0.3125, 0.02, 0.9498).normalize();
    let above = Vec3::new(0.3110, 0.02, 0.9503).normalize();
    let (bu, au) = (stable_light_up(below), stable_light_up(above));
    assert!(below.dot(bu).abs() <= 1e-5);
    assert!(above.dot(au).abs() <= 1e-5);
    assert!(bu.dot(au) > 0.999);
}

#[test]
fn the_stable_up_is_finite_and_continuous_through_the_zenith() {
    let zenith = stable_light_up(Vec3::Z);
    assert!(zenith.is_finite());
    assert!((zenith.length() - 1.0).abs() < 1e-5);
    assert!(Vec3::Z.dot(zenith).abs() <= 1e-5);
    let before = Vec3::new(0.001, 0.002, 1.0).normalize();
    let after = Vec3::new(-0.001, -0.002, 1.0).normalize();
    assert!(stable_light_up(before).dot(zenith) > 0.99999);
    assert!(zenith.dot(stable_light_up(after)) > 0.99999);
    assert!(before.dot(stable_light_up(before)).abs() <= 1e-5);
    assert!(after.dot(stable_light_up(after)).abs() <= 1e-5);
}

fn clip_xy_density(m: Mat4) -> f32 {
    // Row 0 and row 1 of the column-major matrix, first three columns.
    let x = Vec3::new(m.x_axis.x, m.y_axis.x, m.z_axis.x).length();
    let y = Vec3::new(m.x_axis.y, m.y_axis.y, m.z_axis.y).length();
    0.5 * (x + y)
}

#[test]
fn the_clip_density_ratio_matches_the_cascades_texel_footprint_ratio() {
    let input = camera_input(
        Vec3::new(40.0, -15.0, 8.0),
        ShadowQuality::for_preset(ShadowPreset::High),
    );
    let c = fit(&input).unwrap();
    let n = c.len();
    let shader_scale =
        clip_xy_density(c[n - 1].world_to_shadow_clip) / clip_xy_density(c[0].world_to_shadow_clip);
    let expected = c[0].texel_world_size / c[n - 1].texel_world_size;
    assert!((shader_scale - expected).abs() < 1e-4);
    assert!((0.0..=0.999).contains(&shader_scale));
}

#[test]
fn the_resident_window_clamps_only_the_final_reach() {
    let quality = ShadowQuality::for_preset(ShadowPreset::High);
    let mut input = camera_input(Vec3::ZERO, quality);
    input.resident_maximum_reach_meters = 96.0;
    let c = fit(&input).unwrap();
    assert_eq!(c.len(), quality.cascade_count);
    assert!((c[c.len() - 1].split_far_meters - 96.0).abs() < 1e-3);
    assert!(c.iter().all(|c| (0.0..=96.0).contains(&c.split_far_meters)));
    input.resident_maximum_reach_meters = 0.0;
    assert!(fit(&input).unwrap().is_empty());
}

#[test]
fn the_caster_padding_covers_the_resident_receiver_reach() {
    assert_eq!(
        caster_depth_padding_meters(48.0, 144.0, f32::INFINITY),
        144.0
    );
    assert_eq!(caster_depth_padding_meters(48.0, 144.0, 96.0), 96.0);
    assert_eq!(caster_depth_padding_meters(160.0, 144.0, 96.0), 160.0);
}

fn inside_clip(point: Vec3, transform: Mat4) -> bool {
    let clip = transform * point.extend(1.0);
    if !clip.w.is_finite() || clip.w.abs() <= 1e-6 {
        return false;
    }
    let ndc = clip.truncate() / clip.w;
    ndc.x.abs() <= 1.0 && ndc.y.abs() <= 1.0 && (0.0..=1.0).contains(&ndc.z)
}

#[test]
fn reach_sized_padding_keeps_a_low_sun_tree_shadow_inside_while_turning() {
    let quality = ShadowQuality::for_preset(ShadowPreset::Medium);
    let light = Vec3::new(0.8, 0.4, 0.15).normalize();
    let receiver = Vec3::new(30.0, 0.0, 0.0);
    let caster = receiver + light * 80.0;
    let padding = caster_depth_padding_meters(48.0, quality.maximum_reach_meters, f32::INFINITY);
    assert_eq!(padding, quality.maximum_reach_meters);
    let mut visible = 0;
    let mut legacy_clipped = false;
    for yaw_degrees in (-50..=50).step_by(5) {
        let yaw = (yaw_degrees as f32).to_radians();
        let forward = Vec3::new(yaw.cos(), yaw.sin(), 0.0);
        let view = glam::camera::rh::view::look_at_mat4(Vec3::ZERO, forward, Vec3::Z);
        let projection = perspective();
        if !inside_clip(receiver, projection * view) {
            continue;
        }
        visible += 1;
        let mut input = CascadeFitInput::new(view, projection, light, quality);
        input.caster_depth_padding_meters = padding;
        let cascades = fit(&input).unwrap();
        input.caster_depth_padding_meters = 48.0;
        let legacy = fit(&input).unwrap();
        assert_eq!(cascades.len(), legacy.len());
        for (a, b) in cascades.iter().zip(&legacy) {
            assert_eq!(a.half_extent_meters, b.half_extent_meters);
            assert_eq!(a.texel_world_size, b.texel_world_size);
        }
        let splits = [
            cascades[0].split_far_meters,
            cascades[1].split_far_meters,
            cascades[2].split_far_meters,
            0.0,
        ];
        let selected = select_cascade(receiver.length(), splits, cascades.len(), 2.0);
        legacy_clipped |= !inside_clip(caster, legacy[selected.primary].world_to_shadow_clip);
        assert!(
            inside_clip(receiver, cascades[selected.primary].world_to_shadow_clip),
            "receiver left cascade {} at yaw {yaw_degrees}",
            selected.primary
        );
        assert!(
            inside_clip(caster, cascades[selected.primary].world_to_shadow_clip),
            "caster left cascade {} at yaw {yaw_degrees}",
            selected.primary
        );
    }
    assert!(visible > 1);
    assert!(legacy_clipped);
}

#[test]
fn the_presets_keep_their_planning_envelope_and_bias_floor() {
    for (preset, cascades, resolution, reach, bytes) in [
        (ShadowPreset::Low, 2, 768, 72.0, 4_718_592),
        (ShadowPreset::Medium, 3, 1536, 144.0, 28_311_552),
        (ShadowPreset::High, 4, 2048, 240.0, 67_108_864),
    ] {
        let q = ShadowQuality::for_preset(preset);
        assert_eq!(q.cascade_count, cascades);
        assert_eq!(q.map_resolution, resolution);
        assert_eq!(q.maximum_reach_meters, reach);
        assert_eq!(q.approximate_depth_map_bytes, bytes);
        assert_eq!(q.bias.minimum_meters, 0.001);
    }
}

#[test]
fn the_bias_is_in_metres_and_scales_with_the_texel_footprint() {
    let policy = ShadowQuality::for_preset(ShadowPreset::Medium).bias;
    let near = policy.resolve(0.02);
    let far = policy.resolve(0.20);
    for v in [
        near.constant_depth_meters,
        near.slope_depth_meters,
        near.normal_offset_meters,
    ] {
        assert!((policy.minimum_meters..=policy.maximum_meters).contains(&v));
    }
    assert!(far.constant_depth_meters > near.constant_depth_meters);
    assert!(far.slope_depth_meters > near.slope_depth_meters);
    assert!(far.normal_offset_meters > near.normal_offset_meters);
}

#[test]
fn the_cascade_transition_is_continuous_across_the_split() {
    let splits = [10.0, 30.0, 60.0, 100.0];
    let at = select_cascade(10.0, splits, 4, 2.0);
    let after = select_cascade(10.0001, splits, 4, 2.0);
    assert_eq!((at.primary, at.secondary), (0, 1));
    assert!((at.secondary_weight - 1.0).abs() < 1e-5);
    assert_eq!((after.primary, after.secondary), (1, 2));
    assert!(after.secondary_weight <= 0.00001);
    assert!(at.within_shadow_reach && after.within_shadow_reach);
    let outside = select_cascade(100.01, splits, 4, 2.0);
    assert!(!outside.within_shadow_reach);
    assert_eq!(outside.primary, 3);
}

#[test]
fn the_receiver_bias_is_metres_and_grows_on_grazed_surfaces() {
    let bias = WorldBias {
        constant_depth_meters: 0.01,
        slope_depth_meters: 0.04,
        normal_offset_meters: 0.02,
    };
    assert!((receiver_bias_meters(&bias, 1.0) - 0.01).abs() < 1e-6);
    assert!((receiver_bias_meters(&bias, 0.0) - 0.05).abs() < 1e-6);
}

fn celestial(kind: CelestialSourceKind, elevation_degrees: f32, energy: f32) -> CelestialSource {
    let elevation = elevation_degrees.to_radians();
    let heading = 120f32.to_radians();
    let horizontal = elevation.cos();
    let direction = Vec3::new(
        horizontal * heading.cos(),
        horizontal * heading.sin(),
        elevation.sin(),
    );
    let gfx_obj_id = match kind {
        CelestialSourceKind::Sun => SUN_GFX_OBJ_ID,
        CelestialSourceKind::DominantMoon => DOMINANT_MOON_GFX_OBJ_ID,
        CelestialSourceKind::SecondaryMoon => SECONDARY_MOON_GFX_OBJ_ID,
        CelestialSourceKind::None => 0,
    };
    CelestialSource {
        kind,
        object_index: 4,
        gfx_obj_id,
        surface_to_light: direction,
        elevation_sin: direction.z,
        authored_energy: energy,
    }
}

fn atmosphere(kind: WeatherKind) -> AtmosphereSnapshot {
    AtmosphereSnapshot {
        kind,
        intensity: 1.0,
        fog_color: Vec3::splat(0.4),
        fog_start: 80.0,
        fog_end: 350.0,
        fog_mode: FogMode::Linear,
        lightning_flash: 0.0,
        environ_override: EnvironOverride::None,
    }
}

fn gate_input(
    enabled: bool,
    cover: bool,
    indoor: bool,
    source: CelestialSource,
) -> ShadowEnvironmentInput {
    ShadowEnvironmentInput {
        enabled,
        portal_or_login_cover: cover,
        player_inside_cell: indoor,
        source,
        atmosphere: atmosphere(WeatherKind::Clear),
        active_day_group_multiplier: 1.0,
    }
}

#[test]
fn the_non_world_gates_disable_without_celestial_work() {
    let source = celestial(CelestialSourceKind::SecondaryMoon, 35.0, 1.0);
    for (enabled, cover, indoor, expected) in [
        (false, false, false, ShadowGateReason::Disabled),
        (true, true, false, ShadowGateReason::PortalOrLoginCover),
        (true, false, true, ShadowGateReason::Indoor),
    ] {
        let r = evaluate_shadow_gate(
            &gate_input(enabled, cover, indoor, source),
            &ShadowAtmospherePolicy::built_in(),
        );
        assert!(!r.should_render());
        assert_eq!(r.reason, expected);
        assert_eq!(r.strength, 0.0);
    }
}

#[test]
fn a_source_below_the_horizon_disables_and_keeps_its_identity() {
    let source = celestial(CelestialSourceKind::SecondaryMoon, -2.0, 1.0);
    let r = evaluate_shadow_gate(
        &gate_input(true, false, false, source),
        &ShadowAtmospherePolicy::built_in(),
    );
    assert_eq!(r.reason, ShadowGateReason::SelectedLightBelowHorizon);
    assert_eq!(r.surface_to_light, source.surface_to_light);
    assert_eq!(r.source_kind, source.kind);
    assert_eq!(r.source_object_index, source.object_index);
    assert_eq!(r.source_gfx_obj_id, source.gfx_obj_id);
}

#[test]
fn no_celestial_and_no_energy_disable() {
    let none = evaluate_shadow_gate(
        &gate_input(true, false, false, CelestialSource::none(1.0)),
        &ShadowAtmospherePolicy::built_in(),
    );
    assert_eq!(none.reason, ShadowGateReason::NoVisibleCelestial);
    assert_eq!(none.source_kind, CelestialSourceKind::None);
    let dark = celestial(CelestialSourceKind::DominantMoon, 35.0, 0.0);
    let r = evaluate_shadow_gate(
        &gate_input(true, false, false, dark),
        &ShadowAtmospherePolicy::built_in(),
    );
    assert_eq!(r.reason, ShadowGateReason::SelectedLightHasNoEnergy);
    assert_eq!(r.source_gfx_obj_id, dark.gfx_obj_id);
}

#[test]
fn weather_and_day_group_soften_and_reduce_but_keep_the_source() {
    let source = celestial(CelestialSourceKind::DominantMoon, 35.0, 1.0);
    let clear_input = gate_input(true, false, false, source);
    let rain_input = ShadowEnvironmentInput {
        atmosphere: atmosphere(WeatherKind::Rain),
        active_day_group_multiplier: 0.8,
        ..clear_input
    };
    let policy = ShadowAtmospherePolicy::built_in();
    let clear = evaluate_shadow_gate(&clear_input, &policy);
    let rain = evaluate_shadow_gate(&rain_input, &policy);
    assert!(clear.should_render() && rain.should_render());
    assert_eq!(clear.surface_to_light, rain.surface_to_light);
    assert_eq!(clear.source_kind, CelestialSourceKind::DominantMoon);
    assert_eq!(rain.source_kind, clear.source_kind);
    assert!(rain.strength < clear.strength);
    assert!(rain.softness_multiplier > clear.softness_multiplier);
    let zero = ShadowEnvironmentInput {
        active_day_group_multiplier: 0.0,
        ..gate_input(
            true,
            false,
            false,
            celestial(CelestialSourceKind::Sun, 35.0, 1.0),
        )
    };
    assert_eq!(
        evaluate_shadow_gate(&zero, &policy).reason,
        ShadowGateReason::AtmosphereSuppressed
    );
}

fn sky(begin: f32, dir_color: Vec3, dir_bright: f32) -> SkyKeyframe {
    SkyKeyframe {
        begin,
        sun_heading_deg: 90.0,
        sun_pitch_deg: 45.0,
        dir_color,
        dir_bright,
        amb_color: Vec3::splat(0.2),
        amb_bright: 0.4,
        fog_color: Vec3::splat(0.4),
        fog_density: 0.0,
        fog_start: SkyKeyframe::DEFAULT_FOG_START,
        fog_end: SkyKeyframe::DEFAULT_FOG_END,
        fog_mode: FogMode::Linear,
    }
}

fn object(
    gfx_obj_id: u32,
    sort_center: Vec3,
    angles: (f32, f32),
    times: (f32, f32),
) -> SkyObjectData {
    SkyObjectData {
        gfx_obj_id,
        authored_sort_center: sort_center,
        begin_time: times.0,
        end_time: times.1,
        begin_angle: angles.0,
        end_angle: angles.1,
        ..SkyObjectData::default()
    }
}

fn upright(gfx_obj_id: u32, sort_center: Vec3) -> SkyObjectData {
    object(gfx_obj_id, sort_center, (90.0, 90.0), (0.0, 0.0))
}

fn group(objects: Vec<SkyObjectData>, replaces: Vec<SkyObjectReplaceData>) -> DayGroupData {
    DayGroupData {
        chance_of_occur: 1.0,
        name: "Synthetic".into(),
        sky_objects: objects,
        sky_times: if replaces.is_empty() {
            Vec::new()
        } else {
            vec![DatSkyKeyframeData {
                keyframe: sky(0.0, Vec3::ONE, 1.0),
                replaces,
            }]
        },
    }
}

fn replace(
    object_index: u32,
    gfx: u32,
    rotate: f32,
    transparent: f32,
    center: Vec3,
) -> SkyObjectReplaceData {
    SkyObjectReplaceData {
        object_index,
        gfx_obj_id: gfx,
        rotate,
        transparent,
        authored_sort_center: center,
        ..SkyObjectReplaceData::default()
    }
}

fn resolve(group: &DayGroupData, t: f32) -> CelestialSource {
    resolve_celestial_source(Some(group), t, &sky(0.0, Vec3::ONE, 1.0))
}

fn close(a: Vec3, b: Vec3) -> bool {
    (a - b).abs().max_element() <= 1e-5
}

#[test]
fn the_sun_wins_whatever_the_order_then_the_larger_moon() {
    let g = group(
        vec![
            upright(SECONDARY_MOON_GFX_OBJ_ID, Vec3::X),
            upright(DOMINANT_MOON_GFX_OBJ_ID, Vec3::X),
            upright(SUN_GFX_OBJ_ID, Vec3::X),
        ],
        vec![],
    );
    let r = resolve(&g, 0.5);
    assert_eq!(
        (r.kind, r.object_index, r.gfx_obj_id),
        (CelestialSourceKind::Sun, 2, SUN_GFX_OBJ_ID)
    );
    let g = group(
        vec![
            upright(SECONDARY_MOON_GFX_OBJ_ID, Vec3::X),
            upright(DOMINANT_MOON_GFX_OBJ_ID, Vec3::X),
        ],
        vec![],
    );
    let r = resolve(&g, 0.5);
    assert_eq!(
        (r.kind, r.object_index),
        (CelestialSourceKind::DominantMoon, 1)
    );
}

#[test]
fn fully_transparent_sources_fall_back_to_the_smaller_moon() {
    let g = group(
        vec![
            upright(SUN_GFX_OBJ_ID, Vec3::X),
            upright(DOMINANT_MOON_GFX_OBJ_ID, Vec3::X),
            upright(SECONDARY_MOON_GFX_OBJ_ID, Vec3::X),
        ],
        vec![
            replace(0, 0, 0.0, 1.0, Vec3::ZERO),
            replace(1, 0, 0.0, 1.0, Vec3::ZERO),
        ],
    );
    let r = resolve(&g, 0.5);
    assert_eq!(
        (r.kind, r.object_index),
        (CelestialSourceKind::SecondaryMoon, 2)
    );
}

#[test]
fn a_replacement_gives_its_model_and_sort_centre() {
    let g = group(
        vec![upright(DOMINANT_MOON_GFX_OBJ_ID, -Vec3::X)],
        vec![replace(0, 0x0100_ABCD, 0.0, 0.35, Vec3::X)],
    );
    let r = resolve(&g, 0.5);
    assert_eq!(r.kind, CelestialSourceKind::DominantMoon);
    assert_eq!(r.gfx_obj_id, 0x0100_ABCD);
    assert!(close(r.surface_to_light, Vec3::Z));
}

#[test]
fn the_replacement_heading_turns_before_the_arc() {
    let g = group(
        vec![upright(SUN_GFX_OBJ_ID, Vec3::Y)],
        vec![replace(0, 0, 90.0, 0.0, Vec3::ZERO)],
    );
    let r = resolve(&g, 0.5);
    assert!(close(r.surface_to_light, Vec3::Z));
    assert!((r.elevation_sin - 1.0).abs() <= 1e-5);
}

#[test]
fn the_direction_matches_the_sky_transform() {
    let g = group(
        vec![object(
            SECONDARY_MOON_GFX_OBJ_ID,
            Vec3::new(2.0, 1.0, 3.0),
            (0.0, 80.0),
            (0.0, 1.0),
        )],
        vec![replace(0, 0, 30.0, 0.0, Vec3::ZERO)],
    );
    let r = resolve(&g, 0.5);
    let expected = Vec3::new(-0.058_399_98, -0.035_806_22, 0.997_650_9);
    assert_eq!(r.kind, CelestialSourceKind::SecondaryMoon);
    assert!(
        close(r.surface_to_light, expected),
        "{:?}",
        r.surface_to_light
    );
    assert!((r.elevation_sin - expected.z).abs() <= 1e-5);
}

#[test]
fn no_visible_or_risen_source_is_none() {
    let g = group(
        vec![
            object(SUN_GFX_OBJ_ID, Vec3::X, (90.0, 90.0), (0.1, 0.2)),
            object(
                DOMINANT_MOON_GFX_OBJ_ID,
                Vec3::X,
                (-10.0, -10.0),
                (0.0, 0.0),
            ),
            object(SECONDARY_MOON_GFX_OBJ_ID, Vec3::X, (0.0, 0.0), (0.0, 0.0)),
        ],
        vec![],
    );
    let r = resolve(&g, 0.5);
    assert!(!r.is_available());
    assert_eq!((r.object_index, r.gfx_obj_id), (-1, 0));
}

#[test]
fn a_window_across_midnight_is_up_on_both_sides() {
    let g = group(
        vec![object(
            SECONDARY_MOON_GFX_OBJ_ID,
            Vec3::X,
            (80.0, 100.0),
            (0.9, 0.1),
        )],
        vec![],
    );
    assert!(resolve(&g, 0.95).elevation_sin > 0.99);
    assert!(resolve(&g, 0.05).elevation_sin > 0.99);
    assert_eq!(resolve(&g, 0.5).kind, CelestialSourceKind::None);
}

#[test]
fn the_energy_is_the_sun_colour_times_brightness() {
    let g = group(vec![upright(SUN_GFX_OBJ_ID, Vec3::X)], vec![]);
    let k = sky(0.0, Vec3::new(0.4, 0.8, 0.2), 0.5);
    assert!((resolve_celestial_source(Some(&g), 0.5, &k).authored_energy - 0.4).abs() <= 1e-5);
    assert!((resolve_celestial_source(None, 0.5, &k).authored_energy - 0.4).abs() <= 1e-5);
}

#[test]
fn the_uniform_block_is_the_std140_size() {
    assert_eq!(size_of::<ShadowUniforms>(), ShadowUniforms::SIZE);
}
