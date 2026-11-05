// Terrain with the atmospheric rendering on (a port of OpenAC's
// terrain_atmospheric.vert/.frag): terrain.wgsl with the sun's term kept
// apart so the shadow maps can take it away. Drawn into the HDR world
// target.

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
    // x fog start, y fog end, z lightning flash, w fog mode.
    fog_params: vec4<f32>,
    fog_color: vec4<f32>,
    camera_and_time: vec4<f32>,
};

@group(0) @binding(0) var<uniform> globals: Globals;
@group(0) @binding(1) var<uniform> scene: SceneLighting;
@group(0) @binding(3) var<storage, read> clips: array<ClipRegion>;

@group(1) @binding(0) var terrain_tex: texture_2d_array<f32>;
@group(1) @binding(1) var terrain_sampler: sampler;
@group(1) @binding(2) var alpha_tex: texture_2d_array<f32>;
@group(1) @binding(3) var alpha_sampler: sampler;
// Repeats per cell by texture layer, four per vec4 (std140 arrays pad each
// element to 16 bytes).
@group(1) @binding(4) var<uniform> tiling: array<vec4<f32>, 9>;
// The landscape detail texture: a fine pattern repeated many times per cell
// and blended over the lit terrain near the player.
@group(1) @binding(5) var landscape_detail: texture_2d<f32>;
@group(1) @binding(6) var detail_sampler: sampler;
// Detail repeats per cell: x building shells, y interior cells, z the
// landscape (the one read here); 0 turns that detail off.
@group(1) @binding(7) var<uniform> detail_tiling: vec4<f32>;

@group(2) @binding(0) var<uniform> shadow: ShadowBlock;
@group(2) @binding(1) var shadow_map: texture_depth_2d_array;
@group(2) @binding(2) var shadow_sampler: sampler;

struct VertexIn {
    @builtin(vertex_index) vertex_index: u32,
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) packed0: vec4<u32>,
    @location(3) packed1: vec4<u32>,
    @location(4) packed2: vec4<u32>,
    @location(5) packed3: vec4<u32>,
    // The landblock's corner in the drawing frame.
    @location(6) block_offset: vec3<f32>,
    // 1 when the landblock is drawn at full detail (near the player).
    @location(7) full_detail: u32,
};

struct VertexOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) base_uv: vec2<f32>,
    @location(1) lighting: vec3<f32>,
    @location(2) overlay0: vec4<f32>,
    @location(3) overlay1: vec4<f32>,
    @location(4) overlay2: vec4<f32>,
    @location(5) road0: vec4<f32>,
    @location(6) road1: vec4<f32>,
    @location(7) @interpolate(flat) base_layer: f32,
    @location(8) world: vec3<f32>,
    @location(9) @interpolate(flat) full_detail: u32,
    // The landscape detail's strength at this vertex, 0..1, from its
    // distance in front of the camera; interpolated across the triangle.
    @location(12) detail_fade: f32,
    // The sun, which shadows take away (`lighting` holds the ambient).
    @location(10) sun: vec3<f32>,
    @location(11) normal: vec3<f32>,
};

// (u, v, texture layer, alpha layer); a layer of -1 means absent.
fn unpack_overlay(tex_idx: u32, alpha_idx: u32, rot: u32, uv: vec2<f32>) -> vec4<f32> {
    var t = f32(tex_idx);
    var a = f32(alpha_idx);
    if (t >= 254.0) { t = -1.0; }
    if (a >= 254.0) { a = -1.0; }
    var r = uv;
    if (rot == 1u) { r = vec2<f32>(1.0 - uv.y, uv.x); }
    else if (rot == 2u) { r = vec2<f32>(1.0 - uv.x, 1.0 - uv.y); }
    else if (rot == 3u) { r = vec2<f32>(uv.y, 1.0 - uv.x); }
    return vec4<f32>(r.x, r.y, t, a);
}

// The landscape detail's strength at `depth` metres in front of the
// camera: full nearer than 10 m, fading linearly to nothing at 50 m in
// byte steps (truncated), nothing beyond.
fn landscape_detail_fade(depth: f32) -> f32 {
    if (depth < 10.0) {
        return 1.0;
    }
    if (depth <= 50.0) {
        return trunc((1.0 - (depth - 10.0) * (1.0 / 40.0)) * 255.0) / 255.0;
    }
    return 0.0;
}

@vertex
fn vs_main(in: VertexIn) -> VertexOut {
    let rot_ovl0 = (in.packed3.x >> 2u) & 3u;
    let rot_ovl1 = (in.packed3.x >> 4u) & 3u;
    let rot_ovl2 = (in.packed3.x >> 6u) & 3u;
    let rot_rd0 = in.packed3.y & 3u;
    let rot_rd1 = (in.packed3.y >> 2u) & 3u;
    let split = (in.packed3.y >> 4u) & 1u;

    // Which cell corner this vertex is, from its place in the cell's six.
    let v = in.vertex_index % 6u;
    var corner = 0u;
    if (split == 0u) {
        let corners = array<u32, 6>(0u, 1u, 2u, 0u, 2u, 3u);
        corner = corners[v];
    } else {
        let corners = array<u32, 6>(0u, 1u, 3u, 1u, 2u, 3u);
        corner = corners[v];
    }
    var uv = vec2<f32>(0.0, 0.0);
    if (corner == 0u) { uv = vec2<f32>(0.0, 1.0); }
    else if (corner == 1u) { uv = vec2<f32>(1.0, 1.0); }
    else if (corner == 2u) { uv = vec2<f32>(1.0, 0.0); }

    var out: VertexOut;
    out.base_uv = uv;
    out.full_detail = in.full_detail;
    let n = normalize(in.normal);
    let sun = scene.lights[0];
    let l = max(dot(n, -sun.dir_and_range.xyz), 0.0);
    out.lighting = scene.cell_ambient.xyz;
    out.sun = sun.color_and_intensity.xyz * sun.color_and_intensity.w * l;
    out.normal = n;

    var base = f32(in.packed0.x);
    if (base >= 254.0) { base = -1.0; }
    out.base_layer = base;
    out.overlay0 = unpack_overlay(in.packed0.z, in.packed0.w, rot_ovl0, uv);
    out.overlay1 = unpack_overlay(in.packed1.x, in.packed1.y, rot_ovl1, uv);
    out.overlay2 = unpack_overlay(in.packed1.z, in.packed1.w, rot_ovl2, uv);
    out.road0 = unpack_overlay(in.packed2.x, in.packed2.y, rot_rd0, uv);
    out.road1 = unpack_overlay(in.packed2.z, in.packed2.w, rot_rd1, uv);

    let world = in.position + in.block_offset;
    out.world = world;
    out.clip = globals.view_proj * vec4<f32>(world.x, world.y, world.z - 0.01, 1.0);
    // The clip w of a perspective projection is the view-space depth.
    out.detail_fade = landscape_detail_fade(out.clip.w);
    return out;
}

fn layer_tiling(layer: f32) -> f32 {
    let i = u32(layer);
    return tiling[i / 4u][i % 4u];
}

fn sample_terrain(uv: vec2<f32>, layer: f32) -> vec4<f32> {
    return textureSample(terrain_tex, terrain_sampler, uv * layer_tiling(layer), u32(layer));
}

fn sample_alpha(uv: vec2<f32>, layer: f32) -> vec4<f32> {
    // One byte a texel (the blend weight), given in every channel.
    return vec4<f32>(textureSample(alpha_tex, alpha_sampler, uv, u32(layer)).r);
}

fn mask_blend3(t0: vec4<f32>, t1: vec4<f32>, t2: vec4<f32>, h0: f32, h1: f32, h2: f32) -> vec4<f32> {
    var a0 = select(t0.a, 1.0, h0 == 0.0);
    var a1 = select(t1.a, 1.0, h1 == 0.0);
    var a2 = select(t2.a, 1.0, h2 == 0.0);
    let ar = 1.0 - (a0 * a1 * a2);
    let ar_safe = max(ar, 1e-6);
    a0 = 1.0 - a0;
    a1 = 1.0 - a1;
    a2 = 1.0 - a2;
    let r0 = a0 * t0.rgb + (1.0 - a0) * a1 * t1.rgb + (1.0 - a1) * a2 * t2.rgb;
    return vec4<f32>(r0 / ar_safe, ar);
}

// Samples are taken unconditionally (uniform control flow for implicit
// derivatives); absent layers are masked out afterwards.
fn overlay_sample(uv: vec2<f32>, o: vec4<f32>) -> vec4<f32> {
    var t = sample_terrain(uv, max(o.z, 0.0));
    let a = sample_alpha(o.xy, max(o.w, 0.0));
    if (o.w >= 0.0) { t.a = a.a; }
    return t;
}

fn combine_overlays(uv: vec2<f32>, o0: vec4<f32>, o1: vec4<f32>, o2: vec4<f32>) -> vec4<f32> {
    let h0 = select(1.0, 0.0, o0.z < 0.0);
    let h1 = select(1.0, 0.0, o1.z < 0.0);
    let h2 = select(1.0, 0.0, o2.z < 0.0);
    let t0 = select(vec4<f32>(0.0), overlay_sample(uv, o0), h0 > 0.0);
    let t1 = select(vec4<f32>(0.0), overlay_sample(uv, o1), h1 > 0.0);
    let t2 = select(vec4<f32>(0.0), overlay_sample(uv, o2), h2 > 0.0);
    return mask_blend3(t0, t1, t2, h0, h1, h2);
}

fn combine_road(uv: vec2<f32>, r0: vec4<f32>, r1: vec4<f32>) -> vec4<f32> {
    var result = sample_terrain(uv, max(r0.z, 0.0));
    let a0 = sample_alpha(r0.xy, max(r0.w, 0.0));
    let a1 = sample_alpha(r1.xy, max(r1.w, 0.0));
    if (r0.z < 0.0) {
        return vec4<f32>(0.0);
    }
    if (r0.w >= 0.0) {
        result.a = 1.0 - a0.a;
        if (r1.z >= 0.0 && r1.w >= 0.0) {
            result.a = 1.0 - (a0.a * a1.a);
        }
    }
    return result;
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

// The lit terrain with the landscape detail blended over it, as
// terrain.wgsl does.
fn landscape_detailed(lit: vec3<f32>, cell_uv: vec2<f32>, full_detail: u32, fade: f32) -> vec3<f32> {
    if (detail_tiling.z == 0.0) {
        return lit;
    }
    let d = textureSample(landscape_detail, detail_sampler, cell_uv * detail_tiling.z);
    let weight = d.a * fade;
    let detailed = d.rgb * weight + lit * (1.0 - weight);
    return select(lit, detailed, full_detail != 0u);
}

@fragment
fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
    let base_sample = sample_terrain(in.base_uv, max(in.base_layer, 0.0));
    let base_color = select(vec4<f32>(0.0), base_sample, in.base_layer >= 0.0);

    let overlays_all = combine_overlays(in.base_uv, in.overlay0, in.overlay1, in.overlay2);
    let overlays = select(vec4<f32>(0.0), overlays_all, in.overlay0.z >= 0.0);

    let roads_all = combine_road(in.base_uv, in.road0, in.road1);
    let roads = select(vec4<f32>(0.0), roads_all, in.road0.z >= 0.0);

    let base_masked = base_color.rgb * ((1.0 - overlays.a) * (1.0 - roads.a));
    let ovl_masked = overlays.rgb * (overlays.a * (1.0 - roads.a));
    let road_masked = roads.rgb * roads.a;
    let rgb = clamp(base_masked + ovl_masked + road_masked, vec3<f32>(0.0), vec3<f32>(1.0));
    let surface_to_light = normalize(shadow.light_direction_and_source.xyz);
    let visibility = acdream_directional_shadow_visibility(
        in.world,
        normalize(in.normal),
        scene.camera_and_time.xyz,
        surface_to_light,
    );
    let lighting = in.lighting + in.sun * visibility;
    var lit = rgb * min(lighting, vec3<f32>(1.0));
    lit = lit + scene.fog_params.z * vec3<f32>(0.6, 0.6, 0.75);
    lit = landscape_detailed(lit, in.base_uv, in.full_detail, in.detail_fade);
    lit = apply_fog(lit, in.world);
    // Drawn whole through a portal view, as OpenAC's terrain; depth sorts it.
    return vec4<f32>(lit, 1.0);
}
