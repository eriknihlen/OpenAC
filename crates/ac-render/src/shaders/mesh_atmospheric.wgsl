// Objects with the atmospheric rendering on (a port of OpenAC's
// mesh_atmospheric.vert/.frag): mesh.wgsl's lighting with the sun's term
// kept apart so the shadow maps can take it away, and procedural scenery
// swaying in the wind. Drawn into the HDR world target.
//
// WIND_KIND is the pipeline's wind role for scenery: 2 leaves (clip-mapped
// sub-meshes), 4 trunks (solid ones), 0 none (blended).

override WIND_KIND: u32 = 0u;

struct Globals {
    view_proj: mat4x4<f32>,
    camera_pos: vec4<f32>,
};

struct Light {
    pos_and_kind: vec4<f32>,
    dir_and_range: vec4<f32>,
    color_and_intensity: vec4<f32>,
    cone_angle_etc: vec4<f32>,
};

struct SceneLighting {
    lights: array<Light, 8>,
    cell_ambient: vec4<f32>,
    fog_params: vec4<f32>,
    fog_color: vec4<f32>,
    camera_and_time: vec4<f32>,
};

struct PointLights {
    lights: array<Light, 48>,
};

struct Material {
    alpha_cutoff: f32,
    opacity: f32,
    fog_enabled: f32,
    blended: f32,
};

@group(0) @binding(0) var<uniform> globals: Globals;
@group(0) @binding(1) var<uniform> scene: SceneLighting;
@group(0) @binding(2) var<uniform> points: PointLights;
@group(0) @binding(3) var<storage, read> clips: array<ClipRegion>;
@group(1) @binding(0) var surface_tex: texture_2d<f32>;
@group(1) @binding(1) var surface_sampler: sampler;
@group(1) @binding(2) var<uniform> material: Material;

struct Detail {
    tiling: vec4<f32>,
};

@group(2) @binding(0) var building_detail: texture_2d<f32>;
@group(2) @binding(1) var environment_detail: texture_2d<f32>;
@group(2) @binding(2) var detail_sampler: sampler;
@group(2) @binding(3) var<uniform> detail: Detail;

@group(3) @binding(0) var<uniform> shadow: ShadowBlock;
@group(3) @binding(1) var shadow_map: texture_depth_2d_array;
@group(3) @binding(2) var shadow_sampler: sampler;
@group(3) @binding(3) var<uniform> frame: AtmosphericFrame;

struct VertexIn {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
    @location(3) m0: vec4<f32>,
    @location(4) m1: vec4<f32>,
    @location(5) m2: vec4<f32>,
    @location(6) m3: vec4<f32>,
    @location(7) lights0: vec4<i32>,
    @location(8) lights1: vec4<i32>,
    // Bit 0: inside a cell. Bit 1: procedural scenery with leaves.
    @location(9) indoor: u32,
    @location(11) opacity: f32,
    @location(12) detail: u32,
    @location(13) selection: vec2<f32>,
};

struct VertexOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
    // Ambient and point lights.
    @location(1) lit: vec3<f32>,
    @location(2) world: vec3<f32>,
    @location(4) @interpolate(flat) opacity: f32,
    @location(5) @interpolate(flat) detail: u32,
    @location(6) @interpolate(flat) selection: vec2<f32>,
    // The outdoor sun, which shadows take away.
    @location(7) sun: vec3<f32>,
    @location(8) normal: vec3<f32>,
    @location(9) @interpolate(flat) receives: u32,
};

fn point_contribution(n: vec3<f32>, world_pos: vec3<f32>, l: Light) -> vec3<f32> {
    let kind = i32(l.pos_and_kind.w);
    let to_l = l.pos_and_kind.xyz - world_pos;
    let dist_sq = dot(to_l, to_l);
    let d = sqrt(dist_sq);
    let range = l.dir_and_range.w;
    if (d >= range || range <= 1e-4) {
        return vec3<f32>(0.0);
    }
    let intensity = l.color_and_intensity.w;
    let base_col = l.color_and_intensity.xyz;
    if (l.cone_angle_etc.y > 0.5) {
        let ldir = to_l / max(d, 1e-4);
        let ndl = max(0.0, dot(n, ldir));
        if (ndl <= 0.0) {
            return vec3<f32>(0.0);
        }
        if (kind == 2 && dot(-ldir, l.dir_and_range.xyz) <= cos(l.cone_angle_etc.x * 0.5)) {
            return vec3<f32>(0.0);
        }
        return (intensity * ndl / max(d, 1e-3)) * base_col;
    }
    let angular = max(0.0, dot(n, to_l));
    if (angular <= 0.0) {
        return vec3<f32>(0.0);
    }
    var norm = d;
    if (dist_sq > 1.0) {
        norm = dist_sq * d;
    }
    var scale = (1.0 - d / range) * intensity * (angular / norm);
    if (kind == 2) {
        let ldir = to_l / max(d, 1e-4);
        if (dot(-ldir, l.dir_and_range.xyz) <= cos(l.cone_angle_etc.x * 0.5)) {
            scale = 0.0;
        }
    }
    return min(scale * base_col, base_col);
}

fn light_index(in: VertexIn, k: u32) -> i32 {
    if (k < 4u) {
        return in.lights0[k];
    }
    return in.lights1[k - 4u];
}

@vertex
fn vs_main(in: VertexIn) -> VertexOut {
    let model = mat4x4<f32>(in.m0, in.m1, in.m2, in.m3);
    var world = model * vec4<f32>(in.position, 1.0);
    let flags = select(0u, WIND_KIND, (in.indoor & 2u) != 0u);
    world = vec4<f32>(
        acdream_foliage_displace(world.xyz, in.m3.xyz, flags, frame.clock_wind, frame.wind_amplitude),
        world.w,
    );
    let n = normalize((model * vec4<f32>(in.normal, 0.0)).xyz);
    let indoor = (in.indoor & 1u) != 0u;
    var sun = vec3<f32>(0.0);
    if (!indoor) {
        let active_count = i32(scene.cell_ambient.w);
        for (var i = 0; i < 8; i = i + 1) {
            if (i >= active_count) {
                break;
            }
            let l = scene.lights[i];
            if (i32(l.pos_and_kind.w) != 0) {
                continue;
            }
            let ndl = max(0.0, dot(n, -l.dir_and_range.xyz));
            sun = sun + l.color_and_intensity.xyz * l.color_and_intensity.w * ndl;
        }
    }
    var point_acc = vec3<f32>(0.0);
    for (var k = 0u; k < 8u; k = k + 1u) {
        let gi = light_index(in, k);
        if (gi < 0) {
            continue;
        }
        point_acc = point_acc + point_contribution(n, world.xyz, points.lights[gi]);
    }

    var out: VertexOut;
    out.clip = globals.view_proj * world;
    out.uv = in.uv;
    out.lit = scene.cell_ambient.xyz + min(point_acc, vec3<f32>(1.0));
    out.sun = sun;
    out.normal = n;
    out.receives = select(1u, 0u, indoor);
    out.world = world.xyz;
    out.opacity = in.opacity;
    out.detail = in.detail;
    out.selection = in.selection;
    return out;
}

fn is_clip_reference(value: f32) -> bool {
    return abs(value - 100.0 / 255.0) < 0.000001 || abs(value - 200.0 / 255.0) < 0.000001;
}

fn apply_fog(lit: vec3<f32>, world_pos: vec3<f32>) -> vec3<f32> {
    let mode = i32(scene.fog_params.w);
    if (mode == 0) {
        return lit;
    }
    let d = length(world_pos - scene.camera_and_time.xyz);
    let span = max(1e-3, scene.fog_params.y - scene.fog_params.x);
    let fog = clamp((d - scene.fog_params.x) / span, 0.0, 1.0);
    return mix(lit, scene.fog_color.xyz, fog);
}

@fragment
fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
    let color = textureSample(surface_tex, surface_sampler, in.uv);
    let tiling = select(select(0.0, detail.tiling.y, in.detail == 2u), detail.tiling.x, in.detail == 1u);
    let building = textureSample(building_detail, detail_sampler, in.uv * detail.tiling.x);
    let environment = textureSample(environment_detail, detail_sampler, in.uv * detail.tiling.y);
    let d = select(environment, building, in.detail == 1u);
    let detail_active = tiling != 0.0;
    // Drawn whole through a portal view (OpenAC draws cells and objects
    // without per-view clipping); depth sorts it.
    let surface_to_light = normalize(shadow.light_direction_and_source.xyz);
    var visibility = 1.0;
    if (in.receives != 0u) {
        visibility = acdream_directional_shadow_visibility(
            in.world,
            normalize(in.normal),
            scene.camera_and_time.xyz,
            surface_to_light,
        );
    }
    let scene_lit = in.lit + in.sun * visibility;
    var lit = vec3<f32>(in.selection.x) + in.selection.y * scene_lit;
    lit = lit + scene.fog_params.z * vec3<f32>(0.6, 0.6, 0.75);
    lit = min(lit, vec3<f32>(1.0));
    if (detail_active) {
        let material_alpha = material.opacity * in.opacity;
        let weight = material_alpha * d.a;
        var rgb = d.rgb * weight + (color.rgb * lit) * (1.0 - weight);
        let alpha = material_alpha * d.a * d.a;
        if (is_clip_reference(material.alpha_cutoff) && alpha < material.alpha_cutoff) {
            discard;
        }
        if (material.fog_enabled != 0.0) {
            rgb = apply_fog(rgb, in.world);
        }
        let solid = material.blended == 0.0
            && !is_clip_reference(material.alpha_cutoff)
            && in.opacity >= 1.0;
        return vec4<f32>(rgb, select(alpha, 1.0, solid));
    }
    if (color.a < material.alpha_cutoff) {
        discard;
    }
    let rgb = apply_fog(color.rgb * lit, in.world);
    return vec4<f32>(rgb, color.a * material.opacity * in.opacity);
}
