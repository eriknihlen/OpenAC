//! Screen brightness: the game hands the display a gamma ramp built from
//! the brightness setting, a straight line through zero whose slope grows
//! with the setting. The display applies it to the whole picture (world
//! and interface), and only in exclusive fullscreen: in a window the ramp
//! has no effect. Screenshots read the frame before the ramp.
//!
//! The ramp is a straight line, so this window draws it as a scale of the
//! finished frame's stored colour values, blended over it after the
//! interface.

/// The darkest setting the ramp accepts.
pub const BRIGHTNESS_MIN: f32 = -0.2;
/// The brightest setting the ramp accepts.
pub const BRIGHTNESS_MAX: f32 = 1.0;

/// The setting as the ramp takes it: above 1 is 1, below -0.2 is -0.2.
#[must_use]
pub fn clamp_brightness(brightness: f32) -> f32 {
    // Written as the game compares: "at most 1" first, then "below -0.2";
    // a NaN passes both tests unchanged.
    if brightness <= BRIGHTNESS_MAX || brightness.is_nan() {
        if brightness < BRIGHTNESS_MIN {
            BRIGHTNESS_MIN
        } else {
            brightness
        }
    } else {
        BRIGHTNESS_MAX
    }
}

/// Float to integer as the game converts: toward zero; a value with no
/// 32-bit integer (NaN, out of range) is the integer "indefinite".
fn to_int_toward_zero(value: f64) -> i32 {
    if value.is_finite() && value >= f64::from(i32::MIN) && value < f64::from(i32::MAX) + 1.0 {
        value.trunc() as i32
    } else {
        i32::MIN
    }
}

/// The 256-entry ramp (16-bit output per 8-bit input, the same for red,
/// green and blue) for a brightness setting: entry `i` is
/// `255 i - trunc(-2 b 255 i)`, kept within 0..=65535.
#[must_use]
pub fn gamma_ramp(brightness: f32) -> [u16; 256] {
    let b = f64::from(clamp_brightness(brightness));
    let mut ramp = [0u16; 256];
    for (i, entry) in ramp.iter_mut().enumerate() {
        let base = 255 * i as i32;
        let shift = to_int_toward_zero(f64::from(base) * b * -2.0);
        let value = base.wrapping_sub(shift).clamp(0, 0xFFFF);
        *entry = value as u16;
    }
    ramp
}

/// The ramp as one scale of a stored colour value (0..1 in, 0..1 out,
/// clamped at 1 by the target): its slope, `65025 / 65535 × (1 + 2 b)`.
#[must_use]
pub fn ramp_scale(brightness: f32) -> f32 {
    let b = clamp_brightness(brightness);
    if b.is_nan() {
        return 0.0;
    }
    (65_025.0 / 65_535.0) * (1.0 + 2.0 * b)
}

/// The blended passes that scale the frame by `scale`: each pass multiplies
/// the stored colour by `colour + constant` (both 0..=1), so at most 2 a
/// pass; a larger scale takes two equal passes (the target clamps at 1
/// between them as it would at the end).
#[must_use]
pub fn scale_passes(scale: f32) -> Vec<(f32, f32)> {
    let scale = scale.max(0.0);
    if scale <= 1.0 {
        vec![(0.0, scale)]
    } else if scale <= 2.0 {
        vec![(scale - 1.0, 1.0)]
    } else {
        let half = scale.sqrt();
        vec![(half - 1.0, 1.0), (half - 1.0, 1.0)]
    }
}

const SHADER: &str = r"
@group(0) @binding(0) var<uniform> colour: vec4<f32>;

@vertex
fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    let x = f32((i << 1u) & 2u) * 2.0 - 1.0;
    let y = f32(i & 2u) * 2.0 - 1.0;
    return vec4<f32>(x, y, 0.0, 1.0);
}

@fragment
fn fs() -> @location(0) vec4<f32> {
    return colour;
}
";

/// The GPU side: one full-screen triangle per pass.
#[derive(Debug)]
pub struct BrightnessPass {
    pipeline: wgpu::RenderPipeline,
    layout: wgpu::BindGroupLayout,
    buffers: Vec<(wgpu::Buffer, wgpu::BindGroup)>,
}

impl BrightnessPass {
    /// The pipeline for frames of `format`.
    #[must_use]
    pub fn new(device: &wgpu::Device, format: wgpu::TextureFormat) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("brightness"),
            source: wgpu::ShaderSource::Wgsl(SHADER.into()),
        });
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("brightness"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: None,
                },
                count: None,
            }],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("brightness"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("brightness"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs"),
                compilation_options: wgpu::PipelineCompilationOptions::default(),
                buffers: &[],
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs"),
                compilation_options: wgpu::PipelineCompilationOptions::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    // stored × colour + stored × constant.
                    blend: Some(wgpu::BlendState {
                        color: wgpu::BlendComponent {
                            src_factor: wgpu::BlendFactor::Dst,
                            dst_factor: wgpu::BlendFactor::Constant,
                            operation: wgpu::BlendOperation::Add,
                        },
                        alpha: wgpu::BlendComponent::REPLACE,
                    }),
                    write_mask: wgpu::ColorWrites::COLOR,
                })],
            }),
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            multiview_mask: None,
            cache: None,
        });
        Self {
            pipeline,
            layout,
            buffers: Vec::new(),
        }
    }

    /// Scale `target`'s stored colours by the ramp of `brightness`.
    pub fn draw(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        target: &wgpu::TextureView,
        brightness: f32,
    ) {
        let passes = scale_passes(ramp_scale(brightness));
        while self.buffers.len() < passes.len() {
            let buffer = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("brightness"),
                size: 16,
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            let group = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("brightness"),
                layout: &self.layout,
                entries: &[wgpu::BindGroupEntry {
                    binding: 0,
                    resource: buffer.as_entire_binding(),
                }],
            });
            self.buffers.push((buffer, group));
        }
        for (i, (colour, constant)) in passes.into_iter().enumerate() {
            let (buffer, group) = &self.buffers[i];
            queue.write_buffer(
                buffer,
                0,
                bytemuck::cast_slice(&[colour, colour, colour, 0.0f32]),
            );
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("brightness"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: target,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Load,
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
            pass.set_pipeline(&self.pipeline);
            pass.set_bind_group(0, group, &[]);
            pass.set_blend_constant(wgpu::Color {
                r: f64::from(constant),
                g: f64::from(constant),
                b: f64::from(constant),
                a: 1.0,
            });
            pass.draw(0..3, 0..1);
        }
    }
}

#[cfg(test)]
mod tests {
    //! Golden values worked from the game's screen brightness ramp builder.
    use super::*;

    #[test]
    fn the_setting_is_held_between_minus_a_fifth_and_one() {
        assert_eq!(clamp_brightness(5.0), 1.0);
        assert_eq!(clamp_brightness(1.0), 1.0);
        assert_eq!(clamp_brightness(-1.0), -0.2);
        assert_eq!(clamp_brightness(0.25), 0.25);
        assert!(clamp_brightness(f32::NAN).is_nan());
    }

    #[test]
    fn the_ramp_is_a_line_through_zero() {
        let neutral = gamma_ramp(0.0);
        assert_eq!(neutral[0], 0);
        assert_eq!(neutral[1], 255);
        assert_eq!(neutral[255], 65_025);

        let bright = gamma_ramp(1.0);
        assert_eq!(bright[1], 765);
        assert_eq!(bright[85], 65_025);
        assert_eq!(bright[86], 65_535);
        assert_eq!(bright[255], 65_535);

        // -0.2 is stored as -0.200000003, so -2 b 255 i lands just above
        // the whole number and is cut back to it.
        let dark = gamma_ramp(-1.0);
        assert_eq!(dark[1], 153);
        assert_eq!(dark[255], 39_015);

        // A NaN setting converts to the integer "indefinite": all black.
        assert!(gamma_ramp(f32::NAN).iter().all(|v| *v == 0));
    }

    #[test]
    fn the_scale_matches_the_ramp() {
        for b in [-0.2f32, 0.0, 0.3, 1.0] {
            let ramp = gamma_ramp(b);
            let k = ramp_scale(b);
            for i in [1usize, 64, 128, 200] {
                let expected = f32::from(ramp[i]) / 65_535.0;
                let scaled = (i as f32 / 255.0 * k).min(1.0);
                assert!((expected - scaled).abs() < 1.0 / 65_535.0, "{b} {i}");
            }
        }
    }

    /// The passes on a real GPU give the ramp's output to within one 8-bit
    /// step for every stored value.
    #[test]
    #[ignore = "needs a GPU"]
    fn the_gpu_passes_match_the_ramp() {
        let (device, queue) = crate::test_gpu::device().expect("a GPU device");
        let format = wgpu::TextureFormat::Rgba8Unorm;
        let mut pass = BrightnessPass::new(&device, format);
        for brightness in [-1.0f32, -0.2, 0.0, 0.3, 0.6, 1.0] {
            let texture = device.create_texture(&wgpu::TextureDescriptor {
                label: None,
                size: wgpu::Extent3d {
                    width: 64,
                    height: 4,
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format,
                usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                    | wgpu::TextureUsages::COPY_SRC
                    | wgpu::TextureUsages::COPY_DST,
                view_formats: &[],
            });
            // Every value 0..=255 once, in red, green and blue.
            let pixels: Vec<u8> = (0..256u32)
                .flat_map(|v| [v as u8, v as u8, v as u8, 255])
                .collect();
            queue.write_texture(
                texture.as_image_copy(),
                &pixels,
                wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(256),
                    rows_per_image: Some(4),
                },
                texture.size(),
            );
            let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
            let readback = device.create_buffer(&wgpu::BufferDescriptor {
                label: None,
                size: 1024,
                usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
                mapped_at_creation: false,
            });
            let mut encoder =
                device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
            pass.draw(&device, &queue, &mut encoder, &view, brightness);
            encoder.copy_texture_to_buffer(
                texture.as_image_copy(),
                wgpu::TexelCopyBufferInfo {
                    buffer: &readback,
                    layout: wgpu::TexelCopyBufferLayout {
                        offset: 0,
                        bytes_per_row: Some(256),
                        rows_per_image: Some(4),
                    },
                },
                texture.size(),
            );
            queue.submit([encoder.finish()]);
            readback.map_async(wgpu::MapMode::Read, .., |_| {});
            device
                .poll(wgpu::PollType::wait_indefinitely())
                .expect("the GPU finishes");
            let ramp = gamma_ramp(brightness);
            let mapped = readback.slice(..).get_mapped_range().expect("mapped");
            for v in 0..256usize {
                let got = i32::from(mapped[v * 4]);
                let want = (f64::from(ramp[v]) / 65_535.0 * 255.0).round() as i32;
                assert!(
                    (got - want).abs() <= 1,
                    "brightness {brightness}, value {v}: {got} against {want}"
                );
                assert_eq!(mapped[v * 4 + 3], 255, "alpha is left alone");
            }
        }
    }

    #[test]
    fn a_large_scale_takes_two_passes() {
        assert_eq!(scale_passes(0.6), vec![(0.0, 0.6)]);
        assert_eq!(scale_passes(1.5), vec![(0.5, 1.0)]);
        let two = scale_passes(2.25);
        assert_eq!(two, vec![(0.5, 1.0), (0.5, 1.0)]);
    }
}
