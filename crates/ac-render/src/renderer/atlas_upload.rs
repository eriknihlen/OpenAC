//! A region's terrain atlas reaches the GPU a piece at a time: one terrain
//! layer with its mips, one alpha map, then the tiling table, the bind
//! group and the detail textures. Each piece is one piece of the frame's
//! streaming work, so the region's terrain art (about 48 MB) is spread over
//! a few frames instead of landing in one. The atlas in use is replaced
//! only once every piece of the new one is written, and nothing the loader
//! sends after the atlas is taken before then, so the order in which
//! things arrive is what it was when the atlas went up whole.

use super::WorldRenderer;
use super::pipelines::{write_layer, write_layer_texels};
use crate::assets::TerrainAtlasData;
use crate::work_budget::StreamingWorkCost;

/// One piece of an atlas upload.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum AtlasStep {
    /// Terrain layer `n`, every mip level.
    Layer(usize),
    /// Alpha map `n`.
    Alpha(usize),
    /// The tiling table, the bind group and the detail textures: the atlas
    /// goes into use.
    Finish,
}

/// The `index`th piece of `atlas`'s upload: its terrain layers in order,
/// then its alpha maps, then the finish; `None` past the finish.
#[must_use]
pub(crate) fn step_at(atlas: &TerrainAtlasData, index: usize) -> Option<AtlasStep> {
    let layers = atlas.layers.len();
    let alphas = atlas.alpha_layers.len();
    if index < layers {
        Some(AtlasStep::Layer(index))
    } else if index < layers + alphas {
        Some(AtlasStep::Alpha(index - layers))
    } else if index == layers + alphas {
        Some(AtlasStep::Finish)
    } else {
        None
    }
}

/// What writing `step` of `atlas` costs the frame: the bytes it uploads.
#[must_use]
pub(crate) fn step_cost(atlas: &TerrainAtlasData, step: AtlasStep) -> StreamingWorkCost {
    fn len(bytes: usize) -> i64 {
        i64::try_from(bytes).unwrap_or(i64::MAX)
    }
    let bytes = match step {
        AtlasStep::Layer(n) => atlas.layers[n].levels.iter().map(|l| len(l.len())).sum(),
        AtlasStep::Alpha(n) => len(atlas.alpha_layers[n].len()),
        AtlasStep::Finish => atlas
            .details
            .iter()
            .chain(std::iter::once(&atlas.landscape_detail))
            .flatten()
            .flat_map(|d| &d.image.levels)
            .map(|l| len(l.len()))
            .sum::<i64>()
            .saturating_add(len(size_of_val(&atlas.tiling))),
    };
    StreamingWorkCost {
        gpu_upload_bytes: bytes,
        ..StreamingWorkCost::default()
    }
}

/// An atlas whose textures exist and whose pieces are being written.
#[derive(Debug)]
pub(crate) struct AtlasUpload {
    atlas: Box<TerrainAtlasData>,
    terrain: wgpu::Texture,
    alpha: wgpu::Texture,
    next: usize,
}

impl AtlasUpload {
    /// The piece written next; `None` once the atlas is in use.
    #[must_use]
    pub(crate) fn next_step(&self) -> Option<AtlasStep> {
        step_at(&self.atlas, self.next)
    }

    /// What the next piece costs.
    #[must_use]
    pub(crate) fn next_cost(&self) -> Option<StreamingWorkCost> {
        self.next_step().map(|step| step_cost(&self.atlas, step))
    }
}

impl WorldRenderer {
    /// A region's atlas arrived: its two texture arrays are made now and
    /// written piece by piece over the next frames ([`Self::advance_atlas`]).
    pub(super) fn begin_atlas(&mut self, device: &wgpu::Device, atlas: Box<TerrainAtlasData>) {
        let Some(first) = atlas.layers.first() else {
            self.failure = Some("the region lists no terrain textures".into());
            return;
        };
        let terrain = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("terrain atlas"),
            size: wgpu::Extent3d {
                width: first.width,
                height: first.height,
                depth_or_array_layers: atlas.layers.len() as u32,
            },
            mip_level_count: first.levels.len() as u32,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            // The layers are BGRA, as the dats store 32-bit colour.
            format: wgpu::TextureFormat::Bgra8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        let alpha = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("terrain alpha atlas"),
            size: wgpu::Extent3d {
                width: atlas.alpha_width,
                height: atlas.alpha_height,
                depth_or_array_layers: atlas.alpha_layers.len() as u32,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            // One byte a texel, the blend weight; the terrain shader reads
            // it from the red channel.
            format: wgpu::TextureFormat::R8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        self.atlas_upload = Some(AtlasUpload {
            atlas,
            terrain,
            alpha,
            next: 0,
        });
    }

    /// Write the pending atlas's next piece; the finish puts it in use.
    pub(super) fn advance_atlas(&mut self, device: &wgpu::Device, queue: &wgpu::Queue) {
        let Some(upload) = self.atlas_upload.as_mut() else {
            return;
        };
        let Some(step) = upload.next_step() else {
            self.atlas_upload = None;
            return;
        };
        upload.next += 1;
        match step {
            AtlasStep::Layer(n) => {
                let chain = &upload.atlas.layers[n];
                let (mut w, mut h) = (chain.width, chain.height);
                for (level, pixels) in chain.levels.iter().enumerate() {
                    write_layer(queue, &upload.terrain, level as u32, n as u32, w, h, pixels);
                    (w, h) = ((w / 2).max(1), (h / 2).max(1));
                }
            }
            AtlasStep::Alpha(n) => {
                let atlas = &upload.atlas;
                write_layer_texels(
                    queue,
                    &upload.alpha,
                    0,
                    n as u32,
                    (atlas.alpha_width, atlas.alpha_height, 1),
                    &atlas.alpha_layers[n],
                );
            }
            AtlasStep::Finish => {
                let Some(AtlasUpload {
                    atlas,
                    terrain,
                    alpha,
                    ..
                }) = self.atlas_upload.take()
                else {
                    return;
                };
                self.finish_atlas(device, queue, &atlas, &terrain, &alpha);
            }
        }
    }

    /// Every layer is written: the tiling table, the views and the bind
    /// group put the atlas in use, with the region's detail textures.
    fn finish_atlas(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        atlas: &TerrainAtlasData,
        terrain: &wgpu::Texture,
        alpha: &wgpu::Texture,
    ) {
        let tiling = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("terrain tiling"),
            size: size_of_val(&atlas.tiling) as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.write_buffer(&tiling, 0, bytemuck::cast_slice(&atlas.tiling));
        let array_view = |t: &wgpu::Texture| {
            t.create_view(&wgpu::TextureViewDescriptor {
                dimension: Some(wgpu::TextureViewDimension::D2Array),
                ..Default::default()
            })
        };
        self.terrain_atlas = Some((array_view(terrain), array_view(alpha), tiling));
        // The terrain's group binds the landscape detail image: upload the
        // detail images first.
        self.detail.set_textures(
            device,
            queue,
            &self.detail_layout,
            &atlas.details,
            atlas.landscape_detail.as_ref(),
        );
        self.rebuild_terrain_group(device);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::assets::{DetailTexture, MipChain};

    fn chain(width: u32, levels: usize) -> MipChain {
        MipChain {
            width,
            height: width,
            levels: (0..levels)
                .map(|l| vec![0u8; ((width >> l).max(1) * (width >> l).max(1) * 4) as usize])
                .collect(),
        }
    }

    fn atlas(layers: usize, alphas: usize, detail: bool) -> TerrainAtlasData {
        TerrainAtlasData {
            layers: (0..layers).map(|_| chain(8, 4)).collect(),
            alpha_width: 4,
            alpha_height: 4,
            alpha_layers: vec![vec![0u8; 16]; alphas],
            tiling: [1.0; ac_core::terrain::TERRAIN_LAYER_CAPACITY],
            blending: ac_core::terrain::TerrainBlendingContext::empty(),
            details: [
                detail.then(|| DetailTexture {
                    image: chain(4, 3),
                    tiling: 2.0,
                }),
                None,
            ],
            landscape_detail: detail.then(|| DetailTexture {
                image: chain(4, 3),
                tiling: 4.0,
            }),
        }
    }

    // Every terrain layer and every alpha map is its own piece, in order,
    // and the finish comes once, last.
    #[test]
    fn an_atlas_goes_up_a_layer_and_an_alpha_map_at_a_time() {
        let a = atlas(3, 2, false);
        let steps: Vec<AtlasStep> = (0..).map_while(|i| step_at(&a, i)).collect();
        assert_eq!(
            steps,
            [
                AtlasStep::Layer(0),
                AtlasStep::Layer(1),
                AtlasStep::Layer(2),
                AtlasStep::Alpha(0),
                AtlasStep::Alpha(1),
                AtlasStep::Finish,
            ]
        );
    }

    // The pieces together upload what the whole atlas did, and none of
    // them is more than one layer's worth (or the finish's detail art).
    #[test]
    fn the_pieces_upload_the_whole_atlas_between_them() {
        let a = atlas(3, 2, true);
        let layer: i64 = (0..4).map(|l| ((8i64 >> l).max(1)).pow(2) * 4).sum();
        let detail: i64 = (0..3).map(|l| ((4i64 >> l).max(1)).pow(2) * 4).sum();
        let tiling = i64::try_from(size_of_val(&a.tiling)).unwrap();
        let costs: Vec<i64> = (0..)
            .map_while(|i| step_at(&a, i))
            .map(|s| step_cost(&a, s).gpu_upload_bytes)
            .collect();
        // The finish uploads the building and the landscape detail images.
        assert_eq!(costs, [layer, layer, layer, 16, 16, 2 * detail + tiling]);
    }
}
