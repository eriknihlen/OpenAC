//! A surface's texture as the GPU receives it: decoded RGBA, or the dat's
//! block-compressed bytes kept as they are, each with its mip chain.
//!
//! Object, building, cell and creature textures stored as DXT1/3/5 stay
//! compressed on the GPU (a quarter to an eighth of the memory and upload of
//! RGBA) when the GPU can sample block-compressed images and the surface does
//! not need its pixels changed: a clip map turns black pixels transparent and
//! a translucent surface scales every alpha, so those are decoded. Paletted
//! textures (and the palette swaps creatures and items wear) are never block
//! compressed. The sky, particles, the terrain and the interface keep their
//! RGBA path.
//!
//! These world textures carry a full mip chain (each level half the one
//! before, down to 1 x 1), built on the loader thread: a compressed chain is
//! decoded, halved and encoded again level by level
//! ([`crate::block_compression`]); an RGBA chain is halved directly.

use std::sync::Arc;

use ac_dat::objects::PixelFormat;
use ac_dat::texture::{self, DecodedTexture};

use crate::block_compression::{self as bc, BcFormat, CodecError};
use crate::texture_detail::{WorldTextureDetail, downsample_box};

/// A texture ready to upload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SurfaceImage {
    /// 8-bit RGBA.
    Rgba {
        /// Level 0 (shared with the decoded-texture cache).
        level0: Arc<DecodedTexture>,
        /// Levels 1.., each half the one before.
        mips: Vec<Vec<u8>>,
    },
    /// Block compressed: whole 4 x 4 blocks per level.
    Compressed {
        /// The block format.
        format: BcFormat,
        /// Width of level 0 in texels (a multiple of 4).
        width: u32,
        /// Height of level 0 in texels (a multiple of 4).
        height: u32,
        /// Level 0 and the levels below it.
        levels: Vec<Vec<u8>>,
    },
}

impl SurfaceImage {
    /// A decoded image without mips.
    #[must_use]
    pub fn rgba(level0: Arc<DecodedTexture>) -> Self {
        Self::Rgba {
            level0,
            mips: Vec::new(),
        }
    }

    /// Width of level 0.
    #[must_use]
    pub fn width(&self) -> u32 {
        match self {
            Self::Rgba { level0, .. } => level0.width,
            Self::Compressed { width, .. } => *width,
        }
    }

    /// Height of level 0.
    #[must_use]
    pub fn height(&self) -> u32 {
        match self {
            Self::Rgba { level0, .. } => level0.height,
            Self::Compressed { height, .. } => *height,
        }
    }

    /// Levels in the image (1: no mips).
    #[must_use]
    pub fn level_count(&self) -> u32 {
        match self {
            Self::Rgba { mips, .. } => 1 + mips.len() as u32,
            Self::Compressed { levels, .. } => levels.len() as u32,
        }
    }

    /// The block format, when compressed.
    #[must_use]
    pub fn block_format(&self) -> Option<BcFormat> {
        match self {
            Self::Rgba { .. } => None,
            Self::Compressed { format, .. } => Some(*format),
        }
    }

    /// Each level's size and bytes, level 0 first.
    #[must_use]
    pub fn levels(&self) -> Vec<(u32, u32, &[u8])> {
        let (w, h) = (self.width(), self.height());
        let bytes: Vec<&[u8]> = match self {
            Self::Rgba { level0, mips } => std::iter::once(level0.rgba.as_slice())
                .chain(mips.iter().map(Vec::as_slice))
                .collect(),
            Self::Compressed { levels, .. } => levels.iter().map(Vec::as_slice).collect(),
        };
        bytes
            .into_iter()
            .enumerate()
            .map(|(level, data)| {
                let (lw, lh) = bc::level_extent(w, h, level as u32);
                (lw, lh, data)
            })
            .collect()
    }

    /// Bytes the GPU holds for it (every level).
    #[must_use]
    pub fn gpu_bytes(&self) -> u64 {
        self.levels()
            .iter()
            .map(|(_, _, data)| data.len() as u64)
            .sum()
    }
}

/// The block format a dat texture stays in on the GPU, or `None` when it is
/// decoded: it must be DXT1/3/5, not a clip map, not translucent, and whole
/// 4 x 4 blocks on both sides (the GPU's rule for level 0).
#[must_use]
pub fn keeps_compressed(
    format: PixelFormat,
    clip_map: bool,
    translucency: f32,
    width: u32,
    height: u32,
) -> Option<BcFormat> {
    let block = BcFormat::from_pixel_format(format)?;
    let whole_blocks = width > 0
        && height > 0
        && width.is_multiple_of(bc::BLOCK_EXTENT)
        && height.is_multiple_of(bc::BLOCK_EXTENT);
    (!clip_map && translucency <= 0.0 && whole_blocks).then_some(block)
}

/// A compressed image of the dat's stored level 0; `None` when the data is
/// shorter than the size needs (the caller decodes, which reports it).
#[must_use]
pub fn compressed_from_dat(
    format: BcFormat,
    width: u32,
    height: u32,
    data: &[u8],
) -> Option<SurfaceImage> {
    let size = bc::level_size_bytes(format, width, height);
    let level0 = data.get(..size)?.to_vec();
    Some(SurfaceImage::Compressed {
        format,
        width,
        height,
        levels: vec![level0],
    })
}

/// Make an image ready for a world surface: shrunk to the environment
/// texture detail, then given its full mip chain. `full` (sky and particle
/// textures) keeps it as it is, one level.
pub fn prepare(
    image: SurfaceImage,
    detail: &WorldTextureDetail,
    full: bool,
) -> Result<SurfaceImage, CodecError> {
    if full {
        return Ok(image);
    }
    let image = reduce_environment(image, detail)?;
    with_mips(image)
}

/// The image at the environment scale; unchanged when nothing shrinks. A
/// compressed image is decoded, averaged and encoded again in its own
/// format, so it keeps its memory advantage; one whose reduced sides are
/// not whole blocks stays decoded.
pub fn reduce_environment(
    image: SurfaceImage,
    detail: &WorldTextureDetail,
) -> Result<SurfaceImage, CodecError> {
    match image {
        SurfaceImage::Rgba { level0, mips } => Ok(match detail.reduce_environment(&level0) {
            Some(reduced) => SurfaceImage::rgba(Arc::new(reduced)),
            None => SurfaceImage::Rgba { level0, mips },
        }),
        SurfaceImage::Compressed {
            format,
            width,
            height,
            levels,
        } => {
            let (w, h) = (
                detail.environment_size(width),
                detail.environment_size(height),
            );
            if (w, h) == (width, height) {
                return Ok(SurfaceImage::Compressed {
                    format,
                    width,
                    height,
                    levels,
                });
            }
            let pixel_format = match format {
                BcFormat::Bc1 => PixelFormat::DXT1,
                BcFormat::Bc2 => PixelFormat::DXT3,
                BcFormat::Bc3 => PixelFormat::DXT5,
            };
            let rgba =
                texture::decode_block_compressed(&levels[0], width, height, pixel_format, false)?;
            let reduced = downsample_box(&rgba, width, height, 4, w, h);
            if w.is_multiple_of(bc::BLOCK_EXTENT) && h.is_multiple_of(bc::BLOCK_EXTENT) {
                Ok(SurfaceImage::Compressed {
                    format,
                    width: w,
                    height: h,
                    levels: vec![bc::encode_level(format, &reduced, w, h)?],
                })
            } else {
                Ok(SurfaceImage::rgba(Arc::new(DecodedTexture {
                    width: w,
                    height: h,
                    rgba: reduced,
                })))
            }
        }
    }
}

/// The image with its full mip chain (levels it already has below 0 are
/// replaced).
pub fn with_mips(image: SurfaceImage) -> Result<SurfaceImage, CodecError> {
    let count = bc::full_mip_level_count(image.width(), image.height());
    match image {
        SurfaceImage::Rgba { level0, .. } => {
            let mips = bc::build_from_rgba(None, &level0.rgba, level0.width, level0.height, count)?
                .into_iter()
                .map(|level| level.data)
                .collect();
            Ok(SurfaceImage::Rgba { level0, mips })
        }
        SurfaceImage::Compressed {
            format,
            width,
            height,
            mut levels,
        } => {
            levels.truncate(1);
            let below = bc::build_compressed(format, &levels[0], width, height, count)?;
            levels.extend(below.into_iter().map(|level| level.data));
            Ok(SurfaceImage::Compressed {
                format,
                width,
                height,
                levels,
            })
        }
    }
}

/// Create the GPU texture of `image` and write every level.
#[must_use]
pub fn create_texture(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    image: &SurfaceImage,
    label: &str,
) -> wgpu::Texture {
    let format = image
        .block_format()
        .map_or(wgpu::TextureFormat::Rgba8Unorm, BcFormat::wgpu_format);
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d {
            width: image.width(),
            height: image.height(),
            depth_or_array_layers: 1,
        },
        mip_level_count: image.level_count(),
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    for (level, (w, h, data)) in image.levels().into_iter().enumerate() {
        // A compressed level is written as whole blocks: its stored size
        // rounded up to 4 texels, one row of blocks at a time.
        let (bytes_per_row, rows, extent) = match image.block_format() {
            Some(block) => {
                let (bx, by) = (bc::block_count(w), bc::block_count(h));
                (
                    bx * block.block_size() as u32,
                    by,
                    (bx * bc::BLOCK_EXTENT, by * bc::BLOCK_EXTENT),
                )
            }
            None => (4 * w, h, (w, h)),
        };
        queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &texture,
                mip_level: level as u32,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            data,
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(bytes_per_row),
                rows_per_image: Some(rows),
            },
            wgpu::Extent3d {
                width: extent.0,
                height: extent.1,
                depth_or_array_layers: 1,
            },
        );
    }
    texture
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::texture_detail::ImageScale;

    fn solid_bc1(width: u32, height: u32, colour: u16) -> Vec<u8> {
        let blocks = (width / 4 * (height / 4)) as usize;
        let mut data = Vec::with_capacity(blocks * 8);
        for _ in 0..blocks {
            data.extend_from_slice(&colour.to_le_bytes());
            data.extend_from_slice(&colour.to_le_bytes());
            data.extend_from_slice(&[0; 4]);
        }
        data
    }

    #[test]
    fn only_plain_whole_block_dxt_textures_stay_compressed() {
        // OpenAC MeshExtractor.CanPreserveCompressedTexture: compressed, not
        // a clip map, not translucent.
        assert_eq!(
            keeps_compressed(PixelFormat::DXT1, false, 0.0, 64, 64),
            Some(BcFormat::Bc1)
        );
        assert_eq!(
            keeps_compressed(PixelFormat::DXT5, false, 0.0, 256, 128),
            Some(BcFormat::Bc3)
        );
        assert_eq!(keeps_compressed(PixelFormat::DXT1, true, 0.0, 64, 64), None);
        assert_eq!(
            keeps_compressed(PixelFormat::DXT3, false, 0.5, 64, 64),
            None
        );
        assert_eq!(keeps_compressed(PixelFormat::P8, false, 0.0, 64, 64), None);
        assert_eq!(
            keeps_compressed(PixelFormat::A8R8G8B8, false, 0.0, 64, 64),
            None
        );
        // The GPU needs whole blocks at level 0.
        assert_eq!(keeps_compressed(PixelFormat::DXT1, false, 0.0, 6, 8), None);
    }

    #[test]
    fn the_stored_level_is_sliced_to_its_size_and_short_data_is_refused() {
        let mut data = solid_bc1(8, 8, 0xF800);
        data.extend_from_slice(&[9; 8]);
        let image = compressed_from_dat(BcFormat::Bc1, 8, 8, &data).unwrap();
        assert_eq!(image.levels()[0].2.len(), 32);
        assert_eq!(image.gpu_bytes(), 32);
        assert!(compressed_from_dat(BcFormat::Bc1, 8, 8, &data[..31]).is_none());
    }

    #[test]
    fn a_compressed_chain_runs_to_one_by_one_in_whole_blocks() {
        let image = compressed_from_dat(BcFormat::Bc1, 16, 8, &solid_bc1(16, 8, 0x001F)).unwrap();
        let image = prepare(image, &WorldTextureDetail::FULL, false).unwrap();
        let sizes: Vec<(u32, u32, usize)> = image
            .levels()
            .iter()
            .map(|(w, h, d)| (*w, *h, d.len()))
            .collect();
        assert_eq!(
            sizes,
            [(16, 8, 64), (8, 4, 16), (4, 2, 8), (2, 1, 8), (1, 1, 8)]
        );
        // Sky and particle textures keep one level.
        let one = compressed_from_dat(BcFormat::Bc1, 16, 8, &solid_bc1(16, 8, 0x001F)).unwrap();
        assert_eq!(
            prepare(one, &WorldTextureDetail::FULL, true)
                .unwrap()
                .level_count(),
            1
        );
    }

    #[test]
    fn an_rgba_chain_halves_level_zero() {
        let level0 = Arc::new(DecodedTexture {
            width: 4,
            height: 2,
            rgba: [10, 20, 30, 40].repeat(8),
        });
        let image = prepare(SurfaceImage::rgba(level0), &WorldTextureDetail::FULL, false).unwrap();
        assert_eq!(image.level_count(), 3);
        let levels = image.levels();
        assert_eq!((levels[1].0, levels[1].1), (2, 1));
        assert_eq!(levels[2].2, [10, 20, 30, 40]);
        assert_eq!(image.gpu_bytes(), (8 + 2 + 1) * 4);
    }

    /// Ported from OpenAC `WorldTextureDetailTests.ReduceEnvironment_ReencodesACompressedLayerInItsOwnFormat`.
    #[test]
    fn a_compressed_layer_is_reduced_and_encoded_again_in_its_own_format() {
        // 16x16 BC1 of pure red (5:6:5 0xF800), all indices 0.
        let image = compressed_from_dat(BcFormat::Bc1, 16, 16, &solid_bc1(16, 16, 0xF800)).unwrap();
        let detail = WorldTextureDetail {
            landscape: ImageScale::Full,
            environment: ImageScale::Half,
        };
        let reduced = reduce_environment(image, &detail).unwrap();
        let SurfaceImage::Compressed {
            format,
            width,
            height,
            levels,
        } = &reduced
        else {
            panic!("stays compressed: {reduced:?}");
        };
        assert_eq!((*format, *width, *height), (BcFormat::Bc1, 8, 8));
        assert_eq!(levels[0].len(), (8 / 4) * (8 / 4) * 8);
        let pixels =
            texture::decode_block_compressed(&levels[0], 8, 8, PixelFormat::DXT1, false).unwrap();
        for p in pixels.chunks_exact(4) {
            assert!(p[0] >= 240, "red {}", p[0]);
            assert!(p[1] <= 8 && p[2] <= 8, "green {} blue {}", p[1], p[2]);
        }
    }

    #[test]
    fn a_reduction_to_partial_blocks_stays_decoded() {
        // 20 x 20 halves to 10 x 10, which is not whole blocks.
        let image = compressed_from_dat(BcFormat::Bc1, 20, 20, &solid_bc1(20, 20, 0x07E0)).unwrap();
        let detail = WorldTextureDetail {
            landscape: ImageScale::Full,
            environment: ImageScale::Half,
        };
        let reduced = reduce_environment(image, &detail).unwrap();
        assert!(reduced.block_format().is_none());
        assert_eq!((reduced.width(), reduced.height()), (10, 10));
        let SurfaceImage::Rgba { level0, .. } = reduced else {
            unreachable!()
        };
        assert!(level0.rgba.chunks_exact(4).all(|p| p == [0, 255, 0, 255]));
    }

    #[test]
    fn full_detail_leaves_the_bytes_alone() {
        let data = solid_bc1(8, 8, 0x1234);
        let image = compressed_from_dat(BcFormat::Bc1, 8, 8, &data).unwrap();
        let same = reduce_environment(image.clone(), &WorldTextureDetail::FULL).unwrap();
        assert_eq!(same, image);
    }

    /// A device that samples block-compressed textures, or `None` (no
    /// adapter, or one without the feature): the GPU test is then skipped.
    fn bc_device() -> Option<(wgpu::Device, wgpu::Queue)> {
        crate::test_gpu::device().filter(|(device, _)| {
            device
                .features()
                .contains(wgpu::Features::TEXTURE_COMPRESSION_BC)
        })
    }

    /// Every texel of `level` of `texture`, read on the GPU with
    /// `textureLoad` (no filtering), as RGBA bytes.
    fn gpu_texels(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        texture: &wgpu::Texture,
        level: u32,
        width: u32,
        height: u32,
    ) -> Vec<u8> {
        const SHADER: &str = "
            @group(0) @binding(0) var t: texture_2d<f32>;
            @group(0) @binding(1) var<storage, read_write> texels: array<u32>;
            @group(0) @binding(2) var<uniform> size: vec4<u32>;
            @compute @workgroup_size(8, 8)
            fn main(@builtin(global_invocation_id) id: vec3<u32>) {
                if (id.x >= size.x || id.y >= size.y) { return; }
                let c = textureLoad(t, vec2<i32>(id.xy), i32(size.z));
                texels[id.y * size.x + id.x] = pack4x8unorm(c);
            }";
        use wgpu::util::DeviceExt;
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: None,
            source: wgpu::ShaderSource::Wgsl(SHADER.into()),
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: None,
            layout: None,
            module: &module,
            entry_point: Some("main"),
            compilation_options: Default::default(),
            cache: None,
        });
        let bytes = u64::from(width * height * 4);
        let storage = device.create_buffer(&wgpu::BufferDescriptor {
            label: None,
            size: bytes,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let readback = device.create_buffer(&wgpu::BufferDescriptor {
            label: None,
            size: bytes,
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        let size = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: None,
            contents: bytemuck::cast_slice(&[width, height, level, 0]),
            usage: wgpu::BufferUsages::UNIFORM,
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        let group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: None,
            layout: &pipeline.get_bind_group_layout(0),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(&view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: storage.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: size.as_entire_binding(),
                },
            ],
        });
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor::default());
            pass.set_pipeline(&pipeline);
            pass.set_bind_group(0, &group, &[]);
            pass.dispatch_workgroups(width.div_ceil(8), height.div_ceil(8), 1);
        }
        encoder.copy_buffer_to_buffer(&storage, 0, &readback, 0, bytes);
        queue.submit([encoder.finish()]);
        readback.map_async(wgpu::MapMode::Read, .., |_| {});
        device
            .poll(wgpu::PollType::wait_indefinitely())
            .expect("the GPU finishes");
        let mapped = readback.slice(..).get_mapped_range().expect("mapped");
        mapped.to_vec()
    }

    /// A BC texture with its mip chain, uploaded as the renderer uploads
    /// world surfaces, reads back on the GPU like its CPU decode, level by
    /// level, within a step of rounding (hardware decoders round the
    /// in-between colours their own way). Skipped without a GPU that
    /// samples block-compressed textures.
    #[test]
    fn a_block_compressed_texture_samples_like_its_cpu_decode() {
        let Some((device, queue)) = bc_device() else {
            eprintln!("no GPU with block-compressed textures: the GPU decode is not checked");
            return;
        };
        let (w, h) = (32u32, 16u32);
        // A colour and alpha gradient with a cut-out corner: every block
        // has distinct endpoints, and BC1's three-colour mode is used.
        let mut rgba = Vec::with_capacity((w * h * 4) as usize);
        for y in 0..h {
            for x in 0..w {
                let cut = x < 4 && y < 4 && (x + y) % 2 == 0;
                rgba.extend_from_slice(&[
                    (x * 8) as u8,
                    (y * 16) as u8,
                    ((x * 5 + y * 3) % 256) as u8,
                    if cut { 0 } else { (128 + x * 4) as u8 },
                ]);
            }
        }
        for format in [BcFormat::Bc1, BcFormat::Bc2, BcFormat::Bc3] {
            let level0 = bc::encode_level(format, &rgba, w, h).unwrap();
            let image = compressed_from_dat(format, w, h, &level0).unwrap();
            let image = prepare(image, &WorldTextureDetail::FULL, false).unwrap();
            assert_eq!(image.level_count(), 6);
            let texture = create_texture(&device, &queue, &image, "bc test");
            for (level, (lw, lh, blocks)) in image.levels().into_iter().enumerate() {
                let cpu = bc::decode_level(format, blocks, lw, lh).unwrap();
                let gpu = gpu_texels(&device, &queue, &texture, level as u32, lw, lh);
                for (i, (c, g)) in cpu.chunks_exact(4).zip(gpu.chunks_exact(4)).enumerate() {
                    let worst = c.iter().zip(g).map(|(a, b)| a.abs_diff(*b)).max();
                    assert!(
                        worst.unwrap_or(0) <= 3,
                        "{format:?} level {level} texel {i}: CPU {c:?}, GPU {g:?}"
                    );
                }
            }
        }
    }
}
