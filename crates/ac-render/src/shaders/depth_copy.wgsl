// Copies the world pass's depth (DEPTH_TYPE: texture_depth_2d, or
// texture_depth_multisampled_2d whose first sample is taken) into a
// single-sample float picture the post passes read.

@group(0) @binding(0) var scene_depth: DEPTH_TYPE;

@vertex
fn vs_fullscreen(@builtin(vertex_index) id: u32) -> @builtin(position) vec4<f32> {
    let corner = vec2<f32>(f32((id << 1u) & 2u), f32(id & 2u));
    return vec4<f32>(corner * 2.0 - 1.0, 0.0, 1.0);
}

@fragment
fn fs_copy(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let depth = textureLoad(scene_depth, vec2<i32>(position.xy), 0);
    return vec4<f32>(depth, 0.0, 0.0, 1.0);
}
