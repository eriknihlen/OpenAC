// The enhanced night sky (a port of OpenAC's sky.frag night branch): with
// the atmospheric rendering on, the star layer's model is drawn with a
// procedural sky instead of its texture: a faint noise glow and four tiers
// of stars hashed from the view direction, each a small gaussian dot sized
// in screen pixels, tinted from red to blue. It fades out as the sky's
// light (the layer's tint) rises toward day. Drawn additively.

struct SkyParams {
    model: mat4x4<f32>,
    view_proj: mat4x4<f32>,
    ambient_emissive: vec4<f32>,
    sun_color_diffuse: vec4<f32>,
    sun_dir_transparency: vec4<f32>,
    scroll_fog_opacity: vec4<f32>,
};

@group(0) @binding(0) var<uniform> sky: SkyParams;
@group(0) @binding(2) var<storage, read> clips: array<ClipRegion>;

// OpenAC's seed for the star field.
const NIGHT_SKY_SEED: u32 = 11u;

struct VertexIn {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
};

struct VertexOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) tint: vec3<f32>,
    // The model-space direction of the point (the star field's frame).
    @location(1) dir: vec3<f32>,
};

@vertex
fn vs_main(in: VertexIn) -> VertexOut {
    var out: VertexOut;
    let world = sky.model * vec4<f32>(in.position, 1.0);
    out.clip = sky.view_proj * world;
    out.dir = in.position;
    let n = normalize((sky.model * vec4<f32>(in.normal, 0.0)).xyz);
    let diff = max(dot(n, sky.sun_dir_transparency.xyz), 0.0);
    let lit = vec3<f32>(sky.ambient_emissive.w) + sky.ambient_emissive.xyz
        + sky.sun_color_diffuse.xyz * sky.sun_color_diffuse.w * diff;
    out.tint = clamp(lit, vec3<f32>(0.0), vec3<f32>(1.0));
    return out;
}

fn ns_pcg(v_in: u32) -> u32 {
    let v = v_in * 747796405u + 2891336453u;
    let w = ((v >> ((v >> 28u) + 4u)) ^ v) * 277803737u;
    return (w >> 22u) ^ w;
}

fn ns_rand(s: u32) -> f32 {
    return f32(s) * (1.0 / 4294967296.0);
}

fn ns_tint(r: f32) -> vec3<f32> {
    if (r < 0.04) { return vec3<f32>(1.00, 0.55, 0.35); }
    if (r < 0.12) { return vec3<f32>(1.00, 0.80, 0.60); }
    if (r < 0.62) { return vec3<f32>(1.00, 0.97, 0.93); }
    if (r < 0.88) { return vec3<f32>(0.90, 0.95, 1.00); }
    return vec3<f32>(0.70, 0.82, 1.00);
}

fn ns_cell_hash(c: vec3<i32>, seed: u32) -> u32 {
    let h = u32(c.x + 1024) * 0x8DA6B343u
        ^ u32(c.y + 1024) * 0xD8163841u
        ^ u32(c.z + 1024) * 0xCB1AB31Fu
        ^ seed;
    return ns_pcg(h);
}

fn ns_vnoise(p: vec3<f32>, seed: u32) -> f32 {
    let i = floor(p);
    var f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    let ii = vec3<i32>(i);
    var acc = 0.0;
    for (var c = 0; c < 8; c = c + 1) {
        let o = vec3<i32>(c & 1, (c >> 1u) & 1, (c >> 2u) & 1);
        let v = ns_rand(ns_cell_hash(ii + o, seed));
        let wf = mix(vec3<f32>(1.0) - f, f, vec3<f32>(o));
        acc = acc + v * wf.x * wf.y * wf.z;
    }
    return acc;
}

fn ns_star_tier(
    dir: vec3<f32>,
    ex: vec3<f32>,
    ey: vec3<f32>,
    seed: u32,
    cells: f32,
    density: f32,
    b_min: f32,
    b_max: f32,
    size_px: f32,
    halo_amp: f32,
) -> vec3<f32> {
    let g = dir * cells;
    let a = dot(ex, ex);
    let b = dot(ex, ey);
    let c = dot(ey, ey);
    let det = max(a * c - b * b, 1e-14);
    let base = vec3<i32>(floor(g - 0.5));
    var acc = vec3<f32>(0.0);
    for (var ci = 0; ci < 8; ci = ci + 1) {
        let cc = base + vec3<i32>(ci & 1, (ci >> 1u) & 1, (ci >> 2u) & 1);
        let h = ns_cell_hash(cc, seed ^ u32(cells));
        if (f32(h & 0xFFu) > density * 255.0) {
            continue;
        }
        let jitter = vec3<f32>(
            f32((h >> 8u) & 0xFFu),
            f32((h >> 16u) & 0xFFu),
            f32((h >> 24u) & 0xFFu),
        ) * (1.0 / 255.0);
        let sdir = normalize(vec3<f32>(cc) + jitter);
        let v = sdir - dir * dot(sdir, dir);
        let bx = dot(v, ex);
        let by = dot(v, ey);
        let s_px = vec2<f32>(bx * c - by * b, by * a - bx * b) / det;
        let d2 = dot(s_px, s_px);
        if (d2 > 400.0) {
            continue;
        }
        let h2 = ns_pcg(h);
        let t = f32(h2 & 0xFFFFu) * (1.0 / 65535.0);
        let br = mix(b_min, b_max, t * t * t);
        let tint = ns_tint(f32((h2 >> 16u) & 0xFFu) * (1.0 / 255.0));
        var star = exp(-d2 / (2.0 * size_px * size_px));
        star = star + halo_amp * exp(-d2 / (24.0 * size_px * size_px));
        acc = acc + br * tint * star;
    }
    return acc;
}

fn night_sky(dir: vec3<f32>, ex: vec3<f32>, ey: vec3<f32>, seed: u32) -> vec3<f32> {
    let n = 0.62 * ns_vnoise(dir * 3.0, seed ^ 0x9E3779B9u)
        + 0.38 * ns_vnoise(dir * 7.0, seed ^ 0x85EBCA6Bu);
    var rgb = (0.004 + 0.009 * n) * vec3<f32>(0.85, 0.92, 1.10);
    rgb = rgb + ns_star_tier(dir, ex, ey, seed ^ 0x1B873593u, 110.0, 0.85, 0.05, 0.35, 0.50, 0.0);
    rgb = rgb + ns_star_tier(dir, ex, ey, seed ^ 0xCC9E2D51u, 48.0, 0.45, 0.20, 0.70, 0.65, 0.0);
    rgb = rgb + ns_star_tier(dir, ex, ey, seed ^ 0x27D4EB2Fu, 18.0, 0.30, 0.50, 1.40, 0.90, 0.05);
    rgb = rgb + ns_star_tier(dir, ex, ey, seed ^ 0x165667B1u, 6.0, 0.08, 2.00, 4.00, 1.40, 0.10);
    return rgb;
}

@fragment
fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
    // Screen-space derivatives first, in uniform control flow.
    let dir = normalize(in.dir);
    let ex = dpdx(dir);
    let ey = dpdy(dir);
    // Drawn whole through a portal view, as OpenAC's sky.
    let day_l = dot(in.tint, vec3<f32>(0.299, 0.587, 0.114));
    let night = 1.0 - smoothstep(0.18, 0.45, day_l);
    if (night < 0.004) {
        return vec4<f32>(0.0, 0.0, 0.0, 1.0);
    }
    let rgb = night_sky(dir, ex, ey, NIGHT_SKY_SEED);
    return vec4<f32>(rgb * night, 1.0);
}
