//! Moving sun and moon shadows: which sky object casts them, whether the
//! frame shadows at all, the shadow maps' quality and how the camera's view
//! is cut into cascades.
//!
//! A shadow map is a picture of the scene's depth seen from the light: a
//! point is in shadow when something nearer the light covers it there.
//! One map cannot be sharp both at the player's feet and 200 m away, so the
//! view is cut into slices by distance ("cascades"), each with its own
//! map: the near slice covers a few metres per map, the far one a lot. Each
//! map's square is snapped to whole map pixels so the shadows do not
//! crawl as the camera moves.

use ac_core::sky::{AtmosphereSnapshot, DayGroupData, SkyKeyframe, WeatherKind};
use bytemuck::{Pod, Zeroable};
use glam::{Mat4, Vec2, Vec3, Vec4};

/// The shadow quality preset.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ShadowPreset {
    /// Two cascades, 768 pixels, 72 m.
    Low,
    /// Three cascades, 1536 pixels, 144 m.
    Medium,
    /// Four cascades, 2048 pixels, 240 m.
    High,
}

/// How the depth comparison is pushed off the surface, in map pixels,
/// within bounds in metres (so it scales with how much ground a map pixel
/// covers).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct BiasPolicy {
    /// Along the light, always.
    pub constant_texels: f32,
    /// Along the light, more on surfaces the light grazes.
    pub slope_texels: f32,
    /// Along the surface's normal.
    pub normal_texels: f32,
    /// Lowest bias, metres.
    pub minimum_meters: f32,
    /// Highest bias, metres.
    pub maximum_meters: f32,
}

/// A cascade's bias in metres.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct WorldBias {
    /// Along the light.
    pub constant_depth_meters: f32,
    /// Along the light on grazed surfaces.
    pub slope_depth_meters: f32,
    /// Along the normal.
    pub normal_offset_meters: f32,
}

impl BiasPolicy {
    /// The bias in metres for map pixels `texel_world_size` metres wide.
    #[must_use]
    pub fn resolve(&self, texel_world_size: f32) -> WorldBias {
        let clamp = |texels: f32| {
            (texels * texel_world_size).clamp(self.minimum_meters, self.maximum_meters)
        };
        WorldBias {
            constant_depth_meters: clamp(self.constant_texels),
            slope_depth_meters: clamp(self.slope_texels),
            normal_offset_meters: clamp(self.normal_texels),
        }
    }
}

/// The shadow maps' quality.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShadowQuality {
    /// The preset it started from.
    pub preset: ShadowPreset,
    /// Cascades (1 to 4).
    pub cascade_count: usize,
    /// Side of each map, pixels.
    pub map_resolution: u32,
    /// Farthest shadowed distance from the camera, metres.
    pub maximum_reach_meters: f32,
    /// Filter radius: 0 one sample, 1 four, 2 nine (OpenAC's 1, 9, 25 taps).
    pub pcf_radius_texels: u32,
    /// The maps' bytes.
    pub approximate_depth_map_bytes: u64,
    /// The bias.
    pub bias: BiasPolicy,
}

impl ShadowQuality {
    /// A preset's quality (OpenAC `DirectionalShadowQuality.For`).
    #[must_use]
    pub fn for_preset(preset: ShadowPreset) -> Self {
        let (cascades, resolution, reach, pcf, bias) = match preset {
            ShadowPreset::Low => (2, 768, 72.0, 0, (0.45, 1.25, 1.0, 0.35)),
            ShadowPreset::Medium => (3, 1536, 144.0, 1, (0.40, 1.15, 0.9, 0.30)),
            ShadowPreset::High => (4, 2048, 240.0, 2, (0.35, 1.0, 0.8, 0.25)),
        };
        Self {
            preset,
            cascade_count: cascades,
            map_resolution: resolution,
            maximum_reach_meters: reach,
            pcf_radius_texels: pcf,
            approximate_depth_map_bytes: cascades as u64 * u64::from(resolution).pow(2) * 4,
            bias: BiasPolicy {
                constant_texels: bias.0,
                slope_texels: bias.1,
                normal_texels: bias.2,
                minimum_meters: 0.001,
                maximum_meters: bias.3,
            },
        }
    }
}

/// What the cascade fit needs.
#[derive(Debug, Clone, Copy)]
pub struct CascadeFitInput {
    /// The camera's view.
    pub camera_view: Mat4,
    /// The camera's projection (depth 0..1).
    pub camera_projection: Mat4,
    /// From the ground toward the light.
    pub surface_to_light: Vec3,
    /// The quality.
    pub quality: ShadowQuality,
    /// The camera's near distance, metres.
    pub camera_near_meters: f32,
    /// Between even (0) and logarithmic (1) slices.
    pub practical_split_lambda: f32,
    /// Room toward the light for casters outside the view, metres.
    pub caster_depth_padding_meters: f32,
    /// The farthest loaded distance, metres.
    pub resident_maximum_reach_meters: f32,
}

impl CascadeFitInput {
    /// The fit's defaults (OpenAC `DirectionalShadowCascadeFitInput`).
    #[must_use]
    pub fn new(
        camera_view: Mat4,
        camera_projection: Mat4,
        surface_to_light: Vec3,
        quality: ShadowQuality,
    ) -> Self {
        Self {
            camera_view,
            camera_projection,
            surface_to_light,
            quality,
            camera_near_meters: 0.1,
            practical_split_lambda: 0.65,
            caster_depth_padding_meters: 48.0,
            resident_maximum_reach_meters: f32::INFINITY,
        }
    }
}

/// One cascade: its slice of the view and the light's view of it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Cascade {
    /// Which.
    pub index: usize,
    /// Slice start, metres from the camera.
    pub split_near_meters: f32,
    /// Slice end.
    pub split_far_meters: f32,
    /// The light's view.
    pub light_view: Mat4,
    /// The light's box.
    pub light_projection: Mat4,
    /// World to the map's clip space.
    pub world_to_shadow_clip: Mat4,
    /// The box's centre in light space, snapped to whole map pixels.
    pub stabilized_light_space_center: Vec2,
    /// Half the box's side, metres.
    pub half_extent_meters: f32,
    /// Ground one map pixel covers, metres.
    pub texel_world_size: f32,
    /// Room toward the light, metres.
    pub caster_depth_padding_meters: f32,
    /// The bias.
    pub bias: WorldBias,
}

/// Why a fit was refused (OpenAC throws these).
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum FitError {
    /// Cascades not 1 to 4.
    #[error("cascade count must be in [1,4]")]
    CascadeCount,
    /// A dimension is not usable.
    #[error("shadow quality dimensions are invalid")]
    Quality,
    /// The near distance is not positive.
    #[error("camera near distance must be positive")]
    Near,
    /// The resident reach is negative or not a number.
    #[error("resident shadow reach must be nonnegative or positive infinity")]
    ResidentReach,
    /// The split blend is outside 0..1.
    #[error("split lambda must be in [0,1]")]
    Lambda,
    /// The padding is not positive.
    #[error("caster depth padding must be positive")]
    Padding,
    /// The light direction is zero or not a number.
    #[error("light direction must be finite and nonzero")]
    Light,
    /// A camera matrix has no inverse.
    #[error("camera matrix is not invertible")]
    Matrix,
    /// The projection put a corner at infinity.
    #[error("camera projection produced a corner at infinity")]
    Corner,
}

const RADIUS_QUANTIZATION_METERS: f32 = 1.0 / 16.0;

/// The slice end `split_index` of `split_count` between `near` and `far`:
/// a blend of even and logarithmic spacing.
#[must_use]
pub fn practical_split(
    near: f32,
    far: f32,
    split_index: usize,
    split_count: usize,
    lambda: f32,
) -> f32 {
    let fraction = split_index as f32 / split_count as f32;
    let logarithmic = near * (far / near).powf(fraction);
    let uniform = near + (far - near) * fraction;
    lambda * logarithmic + (1.0 - lambda) * uniform
}

/// An up direction at right angles to the light that turns smoothly as the
/// light moves, also straight overhead.
#[must_use]
pub fn stable_light_up(surface_to_light: Vec3) -> Vec3 {
    let l = surface_to_light.normalize();
    let sign = 1.0f32.copysign(l.z);
    let a = -1.0 / (sign + l.z);
    let b = l.x * l.y * a;
    Vec3::new(b, sign + l.y * l.y * a, -l.y).normalize()
}

fn snap_to_texel(value: f32, texel: f32) -> f32 {
    // .NET MidpointRounding.AwayFromZero: Rust's f32::round.
    (value / texel).round() * texel
}

/// Fit the cascades (OpenAC `DirectionalShadowCascadeFitter.Fit`): none
/// when the loaded reach does not pass the camera's near distance.
///
/// # Errors
///
/// An unusable input, as OpenAC refuses it.
pub fn fit(input: &CascadeFitInput) -> Result<Vec<Cascade>, FitError> {
    validate(input)?;
    let inverse_view = invertible(input.camera_view)?;
    let inverse_projection = invertible(input.camera_projection)?;
    let light = input.surface_to_light.normalize();
    let maximum_reach = input
        .quality
        .maximum_reach_meters
        .min(input.resident_maximum_reach_meters);
    if maximum_reach <= input.camera_near_meters {
        return Ok(Vec::new());
    }
    let mut out = Vec::with_capacity(input.quality.cascade_count);
    let mut split_near = input.camera_near_meters;
    for index in 0..input.quality.cascade_count {
        let split_far = practical_split(
            input.camera_near_meters,
            maximum_reach,
            index + 1,
            input.quality.cascade_count,
            input.practical_split_lambda,
        );
        let corners = slice_corners(inverse_view, inverse_projection, split_near, split_far)?;
        out.push(fit_cascade(
            index,
            split_near,
            split_far,
            &corners,
            light,
            input.quality.map_resolution,
            input.caster_depth_padding_meters,
            &input.quality.bias,
        ));
        split_near = split_far;
    }
    Ok(out)
}

fn invertible(m: Mat4) -> Result<Mat4, FitError> {
    let d = m.determinant();
    if d == 0.0 || !d.is_finite() {
        return Err(FitError::Matrix);
    }
    Ok(m.inverse())
}

fn validate(input: &CascadeFitInput) -> Result<(), FitError> {
    let q = &input.quality;
    if q.cascade_count == 0 || q.cascade_count > 4 {
        return Err(FitError::CascadeCount);
    }
    if q.map_resolution == 0
        || !q.maximum_reach_meters.is_finite()
        || q.maximum_reach_meters <= input.camera_near_meters
    {
        return Err(FitError::Quality);
    }
    if !input.camera_near_meters.is_finite() || input.camera_near_meters <= 0.0 {
        return Err(FitError::Near);
    }
    if input.resident_maximum_reach_meters.is_nan() || input.resident_maximum_reach_meters < 0.0 {
        return Err(FitError::ResidentReach);
    }
    if !input.practical_split_lambda.is_finite()
        || !(0.0..=1.0).contains(&input.practical_split_lambda)
    {
        return Err(FitError::Lambda);
    }
    if !input.caster_depth_padding_meters.is_finite() || input.caster_depth_padding_meters <= 0.0 {
        return Err(FitError::Padding);
    }
    let length = input.surface_to_light.length();
    if !length.is_finite() || length <= 1e-6 {
        return Err(FitError::Light);
    }
    Ok(())
}

fn slice_corners(
    inverse_view: Mat4,
    inverse_projection: Mat4,
    near: f32,
    far: f32,
) -> Result<[Vec3; 8], FitError> {
    let mut corners = [Vec3::ZERO; 8];
    let mut cursor = 0;
    for distance in [near, far] {
        for y in [-1.0f32, 1.0] {
            for x in [-1.0f32, 1.0] {
                let v = inverse_projection * Vec4::new(x, y, 1.0, 1.0);
                if v.w.abs() <= 1e-6 {
                    return Err(FitError::Corner);
                }
                let mut view = v.truncate() / v.w;
                let depth = view.z.abs();
                if depth <= 1e-6 {
                    return Err(FitError::Corner);
                }
                view *= distance / depth;
                corners[cursor] = inverse_view.transform_point3(view);
                cursor += 1;
            }
        }
    }
    Ok(corners)
}

#[allow(clippy::too_many_arguments)]
fn fit_cascade(
    index: usize,
    split_near: f32,
    split_far: f32,
    corners: &[Vec3; 8],
    surface_to_light: Vec3,
    map_resolution: u32,
    depth_padding: f32,
    bias: &BiasPolicy,
) -> Cascade {
    let center = corners.iter().copied().sum::<Vec3>() / 8.0;
    let mut radius = corners
        .iter()
        .map(|c| center.distance(*c))
        .fold(0.0f32, f32::max);
    radius = (radius / RADIUS_QUANTIZATION_METERS).ceil() * RADIUS_QUANTIZATION_METERS;
    radius = radius.max(RADIUS_QUANTIZATION_METERS);

    let up = stable_light_up(surface_to_light);
    let light_rotation = glam::camera::rh::view::look_at_mat4(Vec3::ZERO, -surface_to_light, up);
    let light_center = light_rotation.transform_point3(center);
    let texel = (2.0 * radius) / map_resolution as f32;
    let snapped_x = snap_to_texel(light_center.x, texel);
    let snapped_y = snap_to_texel(light_center.y, texel);

    let (mut min_z, mut max_z) = (f32::INFINITY, f32::NEG_INFINITY);
    for c in corners {
        let z = light_rotation.transform_point3(*c).z;
        min_z = min_z.min(z);
        max_z = max_z.max(z);
    }
    let eye = surface_to_light * (max_z + depth_padding);
    let light_view = glam::camera::rh::view::look_at_mat4(eye, eye - surface_to_light, up);
    let near_plane = 0.1f32;
    let far_plane = (near_plane + 0.1).max((max_z - min_z) + 2.0 * depth_padding);
    let light_projection = glam::camera::rh::proj::directx::orthographic(
        snapped_x - radius,
        snapped_x + radius,
        snapped_y - radius,
        snapped_y + radius,
        near_plane,
        far_plane,
    );
    Cascade {
        index,
        split_near_meters: split_near,
        split_far_meters: split_far,
        light_view,
        light_projection,
        world_to_shadow_clip: light_projection * light_view,
        stabilized_light_space_center: Vec2::new(snapped_x, snapped_y),
        half_extent_meters: radius,
        texel_world_size: texel,
        caster_depth_padding_meters: depth_padding,
        bias: bias.resolve(texel),
    }
}

/// The caster padding: at least the configured room, and at least the
/// shadowed reach, so a long low-sun shadow's caster stays in the map
/// (OpenAC `DirectionalSunShadowRenderer.ResolveCasterDepthPaddingMeters`).
#[must_use]
pub fn caster_depth_padding_meters(
    configured_padding_meters: f32,
    quality_reach_meters: f32,
    resident_maximum_reach_meters: f32,
) -> f32 {
    configured_padding_meters.max(quality_reach_meters.min(resident_maximum_reach_meters))
}

/// Which sky object casts the shadows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[repr(u32)]
pub enum CelestialSourceKind {
    /// None is up.
    #[default]
    None = 0,
    /// The sun.
    Sun = 1,
    /// The larger moon.
    DominantMoon = 2,
    /// The smaller moon.
    SecondaryMoon = 3,
}

/// The light the shadows come from: a sky object's direction.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CelestialSource {
    /// Which object.
    pub kind: CelestialSourceKind,
    /// Its index in the day group (-1: none).
    pub object_index: i32,
    /// Its model id.
    pub gfx_obj_id: u32,
    /// From the ground toward it.
    pub surface_to_light: Vec3,
    /// The sine of its height above the horizon.
    pub elevation_sin: f32,
    /// The keyframe's sun brightness, 0..1.
    pub authored_energy: f32,
}

impl CelestialSource {
    /// No source.
    #[must_use]
    pub fn none(authored_energy: f32) -> Self {
        Self {
            kind: CelestialSourceKind::None,
            object_index: -1,
            gfx_obj_id: 0,
            surface_to_light: Vec3::Z,
            elevation_sin: 0.0,
            authored_energy: authored_energy.clamp(0.0, 1.0),
        }
    }

    /// Some object casts.
    #[must_use]
    pub fn is_available(&self) -> bool {
        self.kind != CelestialSourceKind::None
    }
}

/// Dereth's sun model.
pub const SUN_GFX_OBJ_ID: u32 = 0x0100_1348;
/// The larger moon's model.
pub const DOMINANT_MOON_GFX_OBJ_ID: u32 = 0x0100_1F6A;
/// The smaller moon's model.
pub const SECONDARY_MOON_GFX_OBJ_ID: u32 = 0x0100_1F67;

/// The shadow source at `day_fraction`: the sun when it is up and not
/// faded out, else the larger moon, else the smaller one (OpenAC
/// `AuthoredCelestialShadowSourceResolver.Resolve`). The direction is the
/// object's model sort centre turned as the sky draws it.
#[must_use]
pub fn resolve_celestial_source(
    day_group: Option<&DayGroupData>,
    day_fraction: f32,
    sky: &SkyKeyframe,
) -> CelestialSource {
    let color = sky.sun_color();
    let energy = color.x.max(color.y.max(color.z)).clamp(0.0, 1.0);
    let Some(group) = day_group.filter(|_| day_fraction.is_finite()) else {
        return CelestialSource::none(energy);
    };
    [
        (SUN_GFX_OBJ_ID, CelestialSourceKind::Sun),
        (DOMINANT_MOON_GFX_OBJ_ID, CelestialSourceKind::DominantMoon),
        (
            SECONDARY_MOON_GFX_OBJ_ID,
            CelestialSourceKind::SecondaryMoon,
        ),
    ]
    .into_iter()
    .find_map(|(id, kind)| try_resolve(group, day_fraction, id, kind, energy))
    .unwrap_or_else(|| CelestialSource::none(energy))
}

fn finite_direction(v: Vec3) -> bool {
    v.is_finite() && v.length_squared() > 1e-10
}

fn try_resolve(
    group: &DayGroupData,
    day_fraction: f32,
    role_gfx_obj_id: u32,
    kind: CelestialSourceKind,
    energy: f32,
) -> Option<CelestialSource> {
    for (index, object) in group.sky_objects.iter().enumerate() {
        if object.gfx_obj_id != role_gfx_obj_id || !object.is_visible(day_fraction) {
            continue;
        }
        let replace = active_replace(group, day_fraction, index as u32);
        if replace.is_some_and(|r| r.transparent >= 1.0 - 1e-5) {
            continue;
        }
        let replaced = replace.filter(|r| r.gfx_obj_id != 0);
        let effective = replaced.map_or(object.gfx_obj_id, |r| r.gfx_obj_id);
        let anchor = replaced.map_or(object.authored_sort_center, |r| r.authored_sort_center);
        if !finite_direction(anchor) {
            continue;
        }
        let heading = replace.map_or(0.0, |r| r.rotate).to_radians();
        let rotation = object.current_angle(day_fraction).to_radians();
        // The sky's transform: turned by -heading about z, then by
        // -rotation about y.
        let model = Mat4::from_rotation_y(-rotation) * Mat4::from_rotation_z(-heading);
        let transformed = model.transform_vector3(anchor);
        let length = transformed.length();
        if !length.is_finite() || length <= 1e-5 {
            continue;
        }
        let direction = transformed / length;
        if !finite_direction(direction) || direction.z <= 0.0 {
            continue;
        }
        return Some(CelestialSource {
            kind,
            object_index: index as i32,
            gfx_obj_id: effective,
            surface_to_light: direction,
            elevation_sin: direction.z,
            authored_energy: energy,
        });
    }
    None
}

/// The replacement the keyframe active at `day_fraction` makes for object
/// `object_index` (the last one listed).
fn active_replace(
    group: &DayGroupData,
    day_fraction: f32,
    object_index: u32,
) -> Option<ac_core::sky::SkyObjectReplaceData> {
    let times = &group.sky_times;
    let mut active = times.last()?;
    for t in times {
        if t.keyframe.begin <= day_fraction {
            active = t;
        } else {
            break;
        }
    }
    active
        .replaces
        .iter()
        .rev()
        .find(|r| r.object_index == object_index)
        .copied()
}

/// Why the frame has no shadows.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ShadowGateReason {
    /// It has.
    Enabled,
    /// The option is off.
    Disabled,
    /// Portal space or the login cover hides the world.
    PortalOrLoginCover,
    /// The player or camera is inside a closed cell.
    Indoor,
    /// No sun or moon is up.
    NoVisibleCelestial,
    /// The source is at or under the horizon.
    SelectedLightBelowHorizon,
    /// The keyframe's sun has no brightness.
    SelectedLightHasNoEnergy,
    /// The weather, day group or elevation curve take all of it.
    AtmosphereSuppressed,
    /// Nothing is loaded far enough.
    ResidentWindowUnavailable,
}

/// The weather's effect on the shadows.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShadowAtmospherePolicy {
    /// No shadows at or below this elevation sine.
    pub minimum_light_elevation_sin: f32,
    /// Full shadows from this elevation sine.
    pub full_strength_light_elevation_sin: f32,
    /// Strength by weather: clear, overcast, rain, snow, storm.
    pub strength: [f32; 5],
    /// Filter widening by weather.
    pub softness: [f32; 5],
}

impl ShadowAtmospherePolicy {
    /// OpenAC's built-in policy.
    #[must_use]
    pub fn built_in() -> Self {
        Self {
            minimum_light_elevation_sin: 1.0f32.to_radians().sin(),
            full_strength_light_elevation_sin: 12.0f32.to_radians().sin(),
            strength: [1.0, 0.65, 0.45, 0.60, 0.25],
            softness: [1.0, 1.5, 1.8, 1.6, 2.0],
        }
    }

    /// The policy the atmospheric rendering runs the gate with: elevation
    /// is left to the shadow curve (OpenAC
    /// `RenderPackAtmospherePolicyEvaluation.NeutralDirectionalShadowElevation`).
    #[must_use]
    pub fn neutral_elevation() -> Self {
        Self {
            minimum_light_elevation_sin: -1.001,
            full_strength_light_elevation_sin: -1.0,
            ..Self::built_in()
        }
    }

    fn of(values: &[f32; 5], weather: WeatherKind) -> f32 {
        values[weather as usize]
    }
}

/// What the gate needs.
#[derive(Debug, Clone, Copy)]
pub struct ShadowEnvironmentInput {
    /// The option is on.
    pub enabled: bool,
    /// Portal space or the login cover is up.
    pub portal_or_login_cover: bool,
    /// The player or camera is inside a closed cell.
    pub player_inside_cell: bool,
    /// The shadow source.
    pub source: CelestialSource,
    /// The weather.
    pub atmosphere: AtmosphereSnapshot,
    /// The day group's (and curve's and setting's) strength, 0..1.
    pub active_day_group_multiplier: f32,
}

/// The gate's verdict.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShadowEnvironmentState {
    /// Why (or that it shadows).
    pub reason: ShadowGateReason,
    /// From the ground toward the light.
    pub surface_to_light: Vec3,
    /// The light's elevation sine.
    pub light_elevation_sin: f32,
    /// Shadow strength, 0..1.
    pub strength: f32,
    /// Filter widening.
    pub softness_multiplier: f32,
    /// Which object.
    pub source_kind: CelestialSourceKind,
    /// Its index.
    pub source_object_index: i32,
    /// Its model.
    pub source_gfx_obj_id: u32,
}

impl ShadowEnvironmentState {
    /// The frame shadows.
    #[must_use]
    pub fn should_render(&self) -> bool {
        self.reason == ShadowGateReason::Enabled
    }

    fn disabled(reason: ShadowGateReason) -> Self {
        Self {
            reason,
            surface_to_light: Vec3::Z,
            light_elevation_sin: 0.0,
            strength: 0.0,
            softness_multiplier: 1.0,
            source_kind: CelestialSourceKind::None,
            source_object_index: -1,
            source_gfx_obj_id: 0,
        }
    }
}

const MINIMUM_DIRECTIONAL_ENERGY: f32 = 1e-5;

/// Whether this frame shadows, from what and how strongly (OpenAC
/// `DirectionalShadowEnvironmentGate.Evaluate`).
#[must_use]
pub fn evaluate_shadow_gate(
    input: &ShadowEnvironmentInput,
    policy: &ShadowAtmospherePolicy,
) -> ShadowEnvironmentState {
    if !input.enabled {
        return ShadowEnvironmentState::disabled(ShadowGateReason::Disabled);
    }
    if input.portal_or_login_cover {
        return ShadowEnvironmentState::disabled(ShadowGateReason::PortalOrLoginCover);
    }
    if input.player_inside_cell {
        return ShadowEnvironmentState::disabled(ShadowGateReason::Indoor);
    }
    if !input.source.is_available() {
        return ShadowEnvironmentState::disabled(ShadowGateReason::NoVisibleCelestial);
    }
    let source = input.source;
    let with = |reason, strength, softness| ShadowEnvironmentState {
        reason,
        surface_to_light: source.surface_to_light,
        light_elevation_sin: source.elevation_sin,
        strength,
        softness_multiplier: softness,
        source_kind: source.kind,
        source_object_index: source.object_index,
        source_gfx_obj_id: source.gfx_obj_id,
    };
    let elevation = source.elevation_sin;
    if !elevation.is_finite() || elevation <= policy.minimum_light_elevation_sin {
        return with(ShadowGateReason::SelectedLightBelowHorizon, 0.0, 1.0);
    }
    let energy = source.authored_energy;
    if !energy.is_finite() || energy <= MINIMUM_DIRECTIONAL_ENERGY {
        return with(ShadowGateReason::SelectedLightHasNoEnergy, 0.0, 1.0);
    }
    let span =
        (policy.full_strength_light_elevation_sin - policy.minimum_light_elevation_sin).max(1e-5);
    let elevation_strength =
        ((elevation - policy.minimum_light_elevation_sin) / span).clamp(0.0, 1.0);
    let weather = input.atmosphere.kind;
    let strength = elevation_strength
        * energy.clamp(0.0, 1.0)
        * ShadowAtmospherePolicy::of(&policy.strength, weather)
        * input.atmosphere.intensity.clamp(0.0, 1.0)
        * input.active_day_group_multiplier.clamp(0.0, 1.0);
    let softness = ShadowAtmospherePolicy::of(&policy.softness, weather);
    if !strength.is_finite() || strength <= 0.0 {
        return with(ShadowGateReason::AtmosphereSuppressed, 0.0, softness);
    }
    with(
        ShadowGateReason::Enabled,
        strength.clamp(0.0, 1.0),
        softness.max(1.0),
    )
}

/// The cascade a receiver at a distance uses, and the next one it blends
/// into near the slice end.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CascadeBlend {
    /// The cascade.
    pub primary: usize,
    /// The one it blends toward.
    pub secondary: usize,
    /// How much of the secondary, 0..1.
    pub secondary_weight: f32,
    /// The distance is within the shadowed reach.
    pub within_shadow_reach: bool,
}

/// The receiver's cascade choice (OpenAC
/// `DirectionalShadowReceiverPolicy.SelectCascade`; the shader does the
/// same).
#[must_use]
pub fn select_cascade(
    camera_distance_meters: f32,
    split_far_meters: [f32; 4],
    cascade_count: usize,
    blend_width_meters: f32,
) -> CascadeBlend {
    let mut primary = 0;
    while primary < cascade_count && camera_distance_meters > split_far_meters[primary] {
        primary += 1;
    }
    if primary == cascade_count {
        return CascadeBlend {
            primary: cascade_count - 1,
            secondary: cascade_count - 1,
            secondary_weight: 0.0,
            within_shadow_reach: false,
        };
    }
    if primary == cascade_count - 1 || blend_width_meters <= 0.0 {
        return CascadeBlend {
            primary,
            secondary: primary,
            secondary_weight: 0.0,
            within_shadow_reach: true,
        };
    }
    let blend_start = (split_far_meters[primary] - blend_width_meters).max(0.0);
    let t = ((camera_distance_meters - blend_start) / blend_width_meters.max(1e-6)).clamp(0.0, 1.0);
    CascadeBlend {
        primary,
        secondary: primary + 1,
        secondary_weight: t * t * (3.0 - 2.0 * t),
        within_shadow_reach: true,
    }
}

/// The receiver's depth bias in metres for a surface whose normal is at
/// `normal_dot_surface_to_light` to the light.
#[must_use]
pub fn receiver_bias_meters(bias: &WorldBias, normal_dot_surface_to_light: f32) -> f32 {
    bias.constant_depth_meters
        + bias.slope_depth_meters * (1.0 - normal_dot_surface_to_light.clamp(0.0, 1.0))
}

/// The shadow uniform block (OpenAC `DirectionalShadowUniforms`, std140,
/// 336 bytes): each cascade's world-to-map matrix, the slice ends, the
/// control values (strength, softness, reach, blend width), the bias,
/// the map's facts and the light.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Pod, Zeroable)]
pub struct ShadowUniforms {
    /// World to map clip space per cascade.
    pub world_to_clip: [[f32; 16]; 4],
    /// Slice ends, metres.
    pub split_far_meters: [f32; 4],
    /// Strength, softness, reach (metres), blend width (metres).
    pub control: [f32; 4],
    /// Constant, slope and normal bias, caster padding (metres).
    pub bias_meters: [f32; 4],
    /// Unused texture slot, cascade count, map size, flags (bit 0 on,
    /// bits 8..11 the filter radius).
    pub texture_and_flags: [u32; 4],
    /// From the ground toward the light, and the source kind.
    pub light_direction_and_source: [f32; 4],
}

impl ShadowUniforms {
    /// The block's size.
    pub const SIZE: usize = 336;

    /// The block for no shadows.
    #[must_use]
    pub fn disabled() -> Self {
        let identity = Mat4::IDENTITY.to_cols_array();
        Self {
            world_to_clip: [identity; 4],
            split_far_meters: [0.0; 4],
            control: [0.0; 4],
            bias_meters: [0.0; 4],
            texture_and_flags: [0; 4],
            light_direction_and_source: [0.0, 0.0, 1.0, 0.0],
        }
    }

    /// The block for `cascades` (OpenAC `DirectionalShadowUniforms.Create`).
    #[must_use]
    pub fn create(
        cascades: &[Cascade],
        environment: &ShadowEnvironmentState,
        quality: &ShadowQuality,
    ) -> Self {
        let matrix = |i: usize| {
            cascades
                .get(i)
                .map_or(Mat4::IDENTITY, |c| c.world_to_shadow_clip)
                .to_cols_array()
        };
        let split = |i: usize| {
            cascades
                .get(i)
                .map_or(quality.maximum_reach_meters, |c| c.split_far_meters)
        };
        let last = cascades.last();
        let bias = last.map_or_else(WorldBias::default, |c| c.bias);
        let reach = last.map_or(0.0, |c| c.split_far_meters);
        let l = environment.surface_to_light;
        Self {
            world_to_clip: [matrix(0), matrix(1), matrix(2), matrix(3)],
            split_far_meters: [split(0), split(1), split(2), split(3)],
            control: [
                environment.strength,
                environment.softness_multiplier,
                reach,
                (reach * 0.02).max(1.0),
            ],
            bias_meters: [
                bias.constant_depth_meters,
                bias.slope_depth_meters,
                bias.normal_offset_meters,
                cascades
                    .first()
                    .map_or(0.0, |c| c.caster_depth_padding_meters),
            ],
            texture_and_flags: [
                0,
                quality.cascade_count as u32,
                quality.map_resolution,
                1 | (quality.pcf_radius_texels << 8),
            ],
            light_direction_and_source: [l.x, l.y, l.z, environment.source_kind as u32 as f32],
        }
    }
}

#[cfg(test)]
mod tests;
