//! The atmospheric passes on a real GPU the test makes itself (ignored by
//! default, as the other GPU tests; skipped when the machine has none):
//! every pipeline builds without a validation error, the composite
//! reproduces the plain picture with neutral settings (OpenAC
//! `AtmosphericColorPipelineTests.NeutralPresetReproducesThePackOffPixelWithinHalfAnEightBitStep`,
//! here through the shaders), the default settings give the CPU colour
//! path's value, and the shadow receiver lights and shades by the map
//! (OpenAC `DirectionalShadowGpuTests`' receiver cases).

use std::path::PathBuf;
use std::sync::Arc;

use glam::{Mat4, Vec3};

use super::*;
use crate::atmosphere::color;
use crate::camera::FlyInput;
use crate::content::ContentSlot;
use crate::renderer::{WorldRenderer, WorldSettings};
use ac_host::settings::{DisplaySettings, RenderPackSelection};

fn gpu() -> Option<(wgpu::Device, wgpu::Queue)> {
    crate::test_gpu::device()
}

const SIZE: (u32, u32) = (64, 32);
const FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;

fn renderer(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    selection: &RenderPackSelection,
) -> WorldRenderer {
    let settings = WorldSettings {
        // No dats: the world draws nothing, the pass only clears.
        content: Arc::new(ContentSlot::new(PathBuf::from("no-dats-for-this-test"))),
        quality_overrides: crate::quality::QualityOverrides {
            near_radius: Some(0),
            far_radius: Some(0),
            msaa_samples: Some(0),
            ..Default::default()
        },
        residency: Default::default(),
        orbit: Default::default(),
        reveal_radius: None,
        sky: Default::default(),
        audio: Default::default(),
    };
    let mut display = DisplaySettings::default();
    display.set_render_pack_selection(selection);
    WorldRenderer::new(
        device,
        queue,
        FORMAT,
        SIZE.0,
        SIZE.1,
        &settings,
        &display,
        &[4],
    )
}

/// Draw one frame cleared to `clear` and read it back (RGBA rows).
fn draw(
    world: &mut WorldRenderer,
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    clear: wgpu::Color,
) -> Vec<u8> {
    world.prepare(device, queue, None, FlyInput::default());
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("atmosphere test target"),
        size: wgpu::Extent3d {
            width: SIZE.0,
            height: SIZE.1,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: FORMAT,
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
        view_formats: &[],
    });
    let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
    let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
    world.draw(&mut encoder, &view, clear, None);
    read_back(device, queue, encoder, &texture, SIZE)
}

fn read_back(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    mut encoder: wgpu::CommandEncoder,
    texture: &wgpu::Texture,
    (width, height): (u32, u32),
) -> Vec<u8> {
    let row = width * 4;
    let padded =
        row.div_ceil(wgpu::COPY_BYTES_PER_ROW_ALIGNMENT) * wgpu::COPY_BYTES_PER_ROW_ALIGNMENT;
    let buffer = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("atmosphere test readback"),
        size: u64::from(padded) * u64::from(height),
        usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        mapped_at_creation: false,
    });
    encoder.copy_texture_to_buffer(
        texture.as_image_copy(),
        wgpu::TexelCopyBufferInfo {
            buffer: &buffer,
            layout: wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(padded),
                rows_per_image: Some(height),
            },
        },
        texture.size(),
    );
    queue.submit([encoder.finish()]);
    buffer.map_async(wgpu::MapMode::Read, .., |_| {});
    device
        .poll(wgpu::PollType::wait_indefinitely())
        .expect("the GPU finishes");
    let mapped = buffer.slice(..).get_mapped_range().expect("mapped");
    let mut out = Vec::with_capacity((row * height) as usize);
    for y in 0..height {
        let at = (y * padded) as usize;
        out.extend_from_slice(&mapped[at..at + row as usize]);
    }
    out
}

fn neutral() -> RenderPackSelection {
    let mut s = RenderPackSelection::atmospheric("medium");
    for (id, value) in [
        ("bloom-strength", "0"),
        ("filmic-strength", "0"),
        ("exposure", "1"),
        ("grade-saturation", "1"),
        ("grade-contrast", "1"),
        ("vignette-strength", "0"),
        ("sun-ray-strength", "0"),
        ("volumetric-strength", "0"),
    ] {
        s.set_user_value(id, value);
    }
    s
}

#[test]
#[ignore = "needs a GPU"]
fn neutral_settings_reproduce_the_plain_picture_through_the_shaders() {
    let Some((device, queue)) = gpu() else {
        eprintln!("no GPU adapter on this machine: the atmospheric passes are not checked");
        return;
    };
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    let mut world = renderer(&device, &queue, &neutral());
    for step in [0u8, 37, 64, 128, 200, 255] {
        let c = f64::from(step) / 255.0;
        let clear = wgpu::Color {
            r: c,
            g: c * 0.5,
            b: 1.0 - c,
            a: 1.0,
        };
        let pixels = draw(&mut world, &device, &queue, clear);
        assert!(world.atmosphere.is_some(), "the atmospheric rendering runs");
        let want = [
            (clear.r * 255.0).round() as i32,
            (clear.g * 255.0).round() as i32,
            (clear.b * 255.0).round() as i32,
        ];
        for p in pixels.chunks_exact(4) {
            for ch in 0..3 {
                assert!(
                    (i32::from(p[ch]) - want[ch]).abs() <= 1,
                    "step {step} channel {ch}: {} against {}",
                    p[ch],
                    want[ch]
                );
            }
        }
    }
    let error = crate::surface::block_on(scope.pop());
    assert!(error.is_none(), "{error:?}");
}

/// The bloom the downsample extracts from a uniform light value (the
/// shader's soft knee), times its strength.
fn bloom_of(scene: Vec3, strength: f32) -> Vec3 {
    let brightness = scene.dot(Vec3::new(0.2126, 0.7152, 0.0722));
    let threshold = color::BLOOM_THRESHOLD_LINEAR;
    let knee = color::BLOOM_KNEE_LINEAR.max(0.0001);
    let soft = ((brightness - threshold + knee) / (2.0 * knee)).clamp(0.0, 1.0);
    let soft = soft * soft;
    let contribution = ((brightness - threshold).max(0.0) + soft * knee) / brightness.max(0.0001);
    scene * contribution * strength
}

#[test]
#[ignore = "needs a GPU"]
fn the_default_composite_matches_the_cpu_colour_path() {
    let Some((device, queue)) = gpu() else {
        eprintln!("no GPU adapter on this machine: the atmospheric passes are not checked");
        return;
    };
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    let mut world = renderer(&device, &queue, &RenderPackSelection::atmospheric("high"));
    let display = Vec3::new(0.8, 0.55, 0.3);
    let pixels = draw(
        &mut world,
        &device,
        &queue,
        wgpu::Color {
            r: f64::from(display.x),
            g: f64::from(display.y),
            b: f64::from(display.z),
            a: 1.0,
        },
    );
    let post = world.atmosphere.as_ref().expect("running").config.post;
    // A uniform world blooms uniformly (the blur keeps a flat field);
    // without a sun on the screen there are no rays; the centre has no
    // vignette.
    let scene = color::decode(display);
    let hdr = scene + bloom_of(scene, post.bloom_strength);
    let want = color::encode(color::filmic(
        hdr,
        post.exposure,
        post.filmic_strength,
        post.saturation,
        post.contrast,
        1.0,
    ));
    let center = ((SIZE.1 / 2) * SIZE.0 + SIZE.0 / 2) as usize * 4;
    let got = &pixels[center..center + 3];
    for (ch, w) in [want.x, want.y, want.z].into_iter().enumerate() {
        let w = (w * 255.0).round() as i32;
        assert!(
            (i32::from(got[ch]) - w).abs() <= 2,
            "channel {ch}: {} against {w}",
            got[ch]
        );
    }
    // The corners are darker (the vignette).
    assert!(pixels[0] < got[0]);
    let error = crate::surface::block_on(scope.pop());
    assert!(error.is_none(), "{error:?}");
}

const RECEIVER_PROBE: &str = r"
@group(0) @binding(0) var<uniform> shadow: ShadowBlock;
@group(0) @binding(1) var shadow_map: texture_depth_2d_array;
@group(0) @binding(2) var shadow_sampler: sampler;

@vertex
fn vs(@builtin(vertex_index) id: u32) -> @builtin(position) vec4<f32> {
    let corner = vec2<f32>(f32((id << 1u) & 2u), f32(id & 2u));
    return vec4<f32>(corner * 2.0 - 1.0, 0.0, 1.0);
}

// Pixel i probes one receiver: 0 in front of the map's depth (lit), 1
// behind it (shadowed), 2 outside the map (lit), 3 beyond the reach (lit).
@fragment
fn fs(@builtin(position) p: vec4<f32>) -> @location(0) vec4<f32> {
    let i = u32(p.x);
    var world = vec3<f32>(0.0, 0.0, 0.3);
    var camera = vec3<f32>(0.0);
    if (i == 1u) { world = vec3<f32>(0.0, 0.0, 0.7); }
    if (i == 2u) { world = vec3<f32>(2.0, 0.0, 0.7); }
    if (i == 3u) { world = vec3<f32>(0.0, 0.0, 0.7); camera = vec3<f32>(0.0, 0.0, 5000.0); }
    let toward = vec3<f32>(0.0, 0.0, -1.0);
    let v = acdream_directional_shadow_visibility(world, toward, camera, toward);
    return vec4<f32>(v, v, v, 1.0);
}
";

#[test]
#[ignore = "needs a GPU"]
fn the_receiver_lights_in_front_of_the_map_and_shades_behind_it() {
    let Some((device, queue)) = gpu() else {
        eprintln!("no GPU adapter on this machine: the receiver is not checked");
        return;
    };
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    let layouts = AtmosphereLayouts::new(&device);
    let maps = ShadowMaps::new(&device, 16, 2);
    // Clear cascade 0 to depth 0.5 (the light's nearest surface there).
    let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
    for (layer, depth) in [(0, 0.5f32), (1, 0.5)] {
        let _pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("shadow clear"),
            color_attachments: &[],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: &maps.layers[layer],
                depth_ops: Some(wgpu::Operations {
                    load: wgpu::LoadOp::Clear(depth),
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: None,
            }),
            ..Default::default()
        });
    }
    // World x, y map straight to the map's clip space, z to its depth.
    let block = ShadowUniforms {
        world_to_clip: [Mat4::IDENTITY.to_cols_array(); 4],
        split_far_meters: [1000.0, 2000.0, 2000.0, 2000.0],
        control: [0.75, 1.0, 1000.0, 20.0],
        bias_meters: [0.0, 0.0, 0.0, 48.0],
        texture_and_flags: [0, 2, 16, 1],
        light_direction_and_source: [0.0, 0.0, -1.0, 1.0],
    };
    let buffer = |bytes: &[u8]| {
        use wgpu::util::DeviceExt;
        device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: None,
            contents: bytes,
            usage: wgpu::BufferUsages::UNIFORM,
        })
    };
    let shadow_uniform = buffer(bytemuck::bytes_of(&block));
    let frame_uniform = buffer(bytemuck::bytes_of(&AtmosphericFrameGpu::zeroed()));
    let nearest = device.create_sampler(&wgpu::SamplerDescriptor::default());
    let group = atmosphere_group(
        &device,
        &layouts,
        &shadow_uniform,
        &maps,
        &nearest,
        &frame_uniform,
    );
    let source = format!(
        "{}\n{}\n{}",
        include_str!("../../shaders/atmosphere_common.wgsl"),
        include_str!("../../shaders/shadow_receiver.wgsl"),
        RECEIVER_PROBE
    );
    let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("receiver probe"),
        source: wgpu::ShaderSource::Wgsl(source.into()),
    });
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: None,
        bind_group_layouts: &[Some(&layouts.atmosphere)],
        immediate_size: 0,
    });
    let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("receiver probe"),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &module,
            entry_point: Some("vs"),
            compilation_options: Default::default(),
            buffers: &[],
        },
        primitive: wgpu::PrimitiveState::default(),
        depth_stencil: None,
        multisample: wgpu::MultisampleState::default(),
        fragment: Some(wgpu::FragmentState {
            module: &module,
            entry_point: Some("fs"),
            compilation_options: Default::default(),
            targets: &[Some(FORMAT.into())],
        }),
        multiview_mask: None,
        cache: None,
    });
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: None,
        size: wgpu::Extent3d {
            width: 4,
            height: 1,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: FORMAT,
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
        view_formats: &[],
    });
    let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
    {
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("receiver probe"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: &view,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Clear(wgpu::Color::BLACK),
                    store: wgpu::StoreOp::Store,
                },
            })],
            ..Default::default()
        });
        pass.set_pipeline(&pipeline);
        pass.set_bind_group(0, &group, &[]);
        pass.draw(0..3, 0..1);
    }
    let pixels = read_back(&device, &queue, encoder, &texture, (4, 1));
    let lit = 255;
    // Shadowed: the strength (0.75) takes three quarters of the light.
    let shaded = (0.25f32 * 255.0).round() as i32;
    for (i, want) in [lit, shaded, lit, lit].into_iter().enumerate() {
        let got = i32::from(pixels[i * 4]);
        assert!((got - want).abs() <= 1, "probe {i}: {got} against {want}");
    }
    let error = crate::surface::block_on(scope.pop());
    assert!(error.is_none(), "{error:?}");
}
