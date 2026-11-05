// Objects: instanced GfxObj triangles, one texture per surface, lit per
// vertex (a port of OpenAC's mesh_modern.vert/.frag object path): ambient,
// plus the sun for objects outside, plus up to eight point lights chosen for
// the object for objects inside a cell; fogged by distance from the camera.

struct Globals {
    view_proj: mat4x4<f32>,
    camera_pos: vec4<f32>,
};

struct Light {
    // xyz position, w kind (0 directional, 1 point, 2 spot).
    pos_and_kind: vec4<f32>,
    // xyz forward, w reach.
    dir_and_range: vec4<f32>,
    // xyz colour, w intensity.
    color_and_intensity: vec4<f32>,
    // x cone angle, y 1 for a dynamic light.
    cone_angle_etc: vec4<f32>,
};

struct SceneLighting {
    lights: array<Light, 8>,
    // xyz ambient, w active light count.
    cell_ambient: vec4<f32>,
    // x fog start, y fog end, z lightning flash, w fog mode.
    fog_params: vec4<f32>,
    fog_color: vec4<f32>,
    camera_and_time: vec4<f32>,
};

struct PointLights {
    lights: array<Light, 48>,
};

struct Material {
    // Pixels with alpha below this are discarded.
    alpha_cutoff: f32,
    // Multiplies the texture's alpha.
    opacity: f32,
    // 1 when the surface is fogged (an additive surface is not), for the
    // detail-textured path.
    fog_enabled: f32,
    // 1 when the surface blends (its pipeline reads the alpha).
    blended: f32,
};

@group(0) @binding(0) var<uniform> globals: Globals;
@group(0) @binding(1) var<uniform> scene: SceneLighting;
@group(0) @binding(2) var<uniform> points: PointLights;
@group(0) @binding(3) var<storage, read> clips: array<ClipRegion>;
@group(1) @binding(0) var surface_tex: texture_2d<f32>;
@group(1) @binding(1) var surface_sampler: sampler;
@group(1) @binding(2) var<uniform> material: Material;

// The retail detail textures laid over building shells and interior cells.
struct Detail {
    // x building shells, y interior cells: repeats per surface texture
    // repeat; 0 turns that detail off.
    tiling: vec4<f32>,
};

@group(2) @binding(0) var building_detail: texture_2d<f32>;
@group(2) @binding(1) var environment_detail: texture_2d<f32>;
@group(2) @binding(2) var detail_sampler: sampler;
@group(2) @binding(3) var<uniform> detail: Detail;

struct VertexIn {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
    @location(3) m0: vec4<f32>,
    @location(4) m1: vec4<f32>,
    @location(5) m2: vec4<f32>,
    @location(6) m3: vec4<f32>,
    // Indices into the point lights; -1 unused.
    @location(7) lights0: vec4<i32>,
    @location(8) lights1: vec4<i32>,
    // Bit 0: an object inside a cell (torch-lit, no sun). Bit 1: procedural
    // scenery with leaves (the atmospheric wind; ignored here).
    @location(9) indoor: u32,
    // How opaque the instance is drawn (its object's and part's
    // translucency); multiplies the alpha.
    @location(11) opacity: f32,
    // Its detail texture: 0 none, 1 a building shell's, 2 an interior
    // cell's.
    @location(12) detail: u32,
    // The selection flash: luminosity, then the factor on the scene's light.
    @location(13) selection: vec2<f32>,
};

struct VertexOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) lit: vec3<f32>,
    @location(2) world: vec3<f32>,
    @location(4) @interpolate(flat) opacity: f32,
    @location(5) @interpolate(flat) detail: u32,
    @location(6) @interpolate(flat) selection: vec2<f32>,
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
    let world = model * vec4<f32>(in.position, 1.0);
    let n = normalize((model * vec4<f32>(in.normal, 0.0)).xyz);
    var lit = scene.cell_ambient.xyz;
    if ((in.indoor & 1u) == 0u) {
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
            lit = lit + l.color_and_intensity.xyz * l.color_and_intensity.w * ndl;
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
    lit = lit + min(point_acc, vec3<f32>(1.0));

    var out: VertexOut;
    out.clip = globals.view_proj * world;
    out.uv = in.uv;
    out.lit = lit;
    out.world = world.xyz;
    out.opacity = in.opacity;
    out.detail = in.detail;
    out.selection = in.selection;
    return out;
}

// The retail alpha-test references (100/255 paletted, 200/255 otherwise).
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
    // Both detail textures are sampled in uniform control flow; the
    // instance's category picks one (OpenAC's mesh_modern.frag).
    let tiling = select(select(0.0, detail.tiling.y, in.detail == 2u), detail.tiling.x, in.detail == 1u);
    let building = textureSample(building_detail, detail_sampler, in.uv * detail.tiling.x);
    let environment = textureSample(environment_detail, detail_sampler, in.uv * detail.tiling.y);
    let d = select(environment, building, in.detail == 1u);
    let detail_active = tiling != 0.0;
    // A draw reached through a portal view is drawn whole and depth sorts
    // it (OpenAC draws cells and objects without per-view clipping); only
    // the portal polygons are limited to their view.
    // The selection flash lights the object (OpenAC's mesh_modern.frag).
    var lit = vec3<f32>(in.selection.x) + in.selection.y * in.lit;
    // Lightning flash: an additive bump of the whole scene.
    lit = lit + scene.fog_params.z * vec3<f32>(0.6, 0.6, 0.75);
    lit = min(lit, vec3<f32>(1.0));
    if (detail_active) {
        // The detail texture blended over the lit surface by the surface's
        // opacity times the detail's alpha; the result's alpha is that
        // weight times the detail's alpha again (the base texture's alpha
        // is not used), tested against the retail reference only.
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
        // A solid surface draws through the solid pipeline, whose alpha
        // only feeds alpha-to-coverage: OpenAC draws a detailed solid
        // surface without coverage, so it stays whole here too.
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
