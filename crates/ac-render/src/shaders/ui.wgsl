// The retail UI: screen-pixel triangles, one texture per run, straight
// alpha. Texel times tint; an almost transparent result is dropped (as
// OpenAC's ui_text shader does).

struct Screen {
    size: vec2<f32>,
    pad: vec2<f32>,
};

@group(0) @binding(0) var<uniform> screen: Screen;
@group(1) @binding(0) var ui_texture: texture_2d<f32>;
@group(1) @binding(1) var ui_sampler: sampler;

struct VertexIn {
    @location(0) position: vec2<f32>,
    @location(1) uv: vec2<f32>,
    @location(2) color: vec4<f32>,
};

struct VertexOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@vertex
fn vs_main(v: VertexIn) -> VertexOut {
    var out: VertexOut;
    let ndc = vec2<f32>(
        v.position.x / screen.size.x * 2.0 - 1.0,
        1.0 - v.position.y / screen.size.y * 2.0,
    );
    out.clip = vec4<f32>(ndc, 0.0, 1.0);
    out.uv = v.uv;
    out.color = v.color;
    return out;
}

@fragment
fn fs_main(v: VertexOut) -> @location(0) vec4<f32> {
    let color = textureSample(ui_texture, ui_sampler, v.uv) * v.color;
    if (color.a < 0.005) {
        discard;
    }
    return color;
}

// A one-byte coverage texture (a font's glyphs): white with that alpha,
// times tint, as the four-channel image (255, 255, 255, value) draws.
@fragment
fn fs_coverage(v: VertexOut) -> @location(0) vec4<f32> {
    let coverage = textureSample(ui_texture, ui_sampler, v.uv).r;
    let color = vec4<f32>(1.0, 1.0, 1.0, coverage) * v.color;
    if (color.a < 0.005) {
        discard;
    }
    return color;
}
