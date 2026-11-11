//! The textures a frame's triangles name: where their pixels come from
//! ([`UiTextureSource`]), the pixels as they are uploaded ([`UiPixels`]),
//! and which the far side (the GPU, or a web page) is owed or may drop
//! ([`TextureLedger`]).
//!
//! The rules are the client's: a texture is read the first time a frame
//! draws it and kept until its owner lets go of it; a surface the dats do
//! not hold draws as the 1 x 1 magenta placeholder (named once); a runtime
//! texture nobody assigned draws nothing (and is asked for again next
//! frame).

use std::collections::HashSet;

use ac_dat::texture::DecodedTexture;
use ac_ui::draw::TextureRef;

use super::batch::{UiBatch, UiRun, UiTexture};

/// Where the UI's texture pixels come from.
pub trait UiTextureSource {
    /// The pixels of `texture`, `None` when there are none.
    fn decode(&mut self, texture: TextureRef) -> Option<UiPixels>;

    /// Textures their owner let go of since the last call, to drop from
    /// the GPU.
    fn released(&mut self) -> Vec<TextureRef> {
        Vec::new()
    }
}

/// A UI texture's pixels as they are uploaded.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UiPixels {
    /// 8-bit RGBA.
    Rgba(DecodedTexture),
    /// One byte a texel, drawn as white with that alpha (a font's glyphs):
    /// exactly what the four-channel image (255, 255, 255, value) draws, at
    /// a quarter of the memory.
    Coverage {
        /// Width in texels.
        width: u32,
        /// Height in texels.
        height: u32,
        /// The alpha of each texel, rows top to bottom.
        alpha: Vec<u8>,
    },
    /// A plugin's own 8-bit RGBA image, with its smaller levels (mipmaps)
    /// when it has them, the image first: sampled linearly between
    /// levels too (trilinear), so a large image drawn small shrinks
    /// smoothly, and clamped at its edges, so a part cut from the edge of a
    /// sheet does not pick up the opposite edge.
    Plugin(Vec<DecodedTexture>),
}

/// One level of a texture: its width, height and bytes.
pub type Level<'a> = (u32, u32, &'a [u8]);

impl UiPixels {
    /// Width and height in texels.
    #[must_use]
    pub fn size(&self) -> (u32, u32) {
        match self {
            Self::Rgba(t) => (t.width, t.height),
            Self::Coverage { width, height, .. } => (*width, *height),
            Self::Plugin(levels) => levels.first().map_or((0, 0), |t| (t.width, t.height)),
        }
    }

    /// Each level's size and bytes, the image first, and the bytes a texel
    /// (1 for coverage, else 4).
    #[must_use]
    pub fn levels(&self) -> (Vec<Level<'_>>, u32) {
        match self {
            Self::Rgba(t) => (vec![(t.width, t.height, t.rgba.as_slice())], 4),
            Self::Coverage {
                width,
                height,
                alpha,
            } => (vec![(*width, *height, alpha.as_slice())], 1),
            Self::Plugin(levels) => (
                levels
                    .iter()
                    .map(|t| (t.width, t.height, t.rgba.as_slice()))
                    .collect(),
                4,
            ),
        }
    }

    /// Every level's bytes fill its size, and each level is half the one
    /// above (rounded down, at least 1), the way the GPU sizes them.
    #[must_use]
    pub fn is_whole(&self) -> bool {
        let (levels, per_texel) = self.levels();
        let Some(&(w, h, _)) = levels.first() else {
            return false;
        };
        let max_levels = 32 - w.max(h).leading_zeros();
        w > 0
            && h > 0
            && levels.len() as u32 <= max_levels
            && levels.iter().enumerate().all(|(i, (lw, lh, bytes))| {
                *lw == (w >> i).max(1)
                    && *lh == (h >> i).max(1)
                    && bytes.len() as u64 == u64::from(per_texel) * u64::from(*lw) * u64::from(*lh)
            })
    }

    /// The GPU memory it takes once uploaded, bytes: every level counted.
    #[must_use]
    pub fn gpu_bytes(&self) -> u64 {
        let (levels, per_texel) = self.levels();
        levels
            .iter()
            .map(|(w, h, _)| u64::from(per_texel) * u64::from(*w) * u64::from(*h))
            .sum()
    }

    /// Sampled clamped and between levels (a plugin's own image).
    #[must_use]
    pub fn clamped(&self) -> bool {
        matches!(self, Self::Plugin(_))
    }

    /// The magenta placeholder of a surface that cannot be read.
    #[must_use]
    pub fn magenta() -> Self {
        Self::Rgba(DecodedTexture {
            width: 1,
            height: 1,
            rgba: vec![0xFF, 0x00, 0xFF, 0xFF],
        })
    }
}

/// What one frame owes the far side: textures to drop, textures to upload
/// (in the order the frame first draws them), and the runs that draw.
#[derive(Debug, Default)]
pub struct TexturePlan {
    /// Textures their owner let go of that the far side holds: drop them
    /// first.
    pub releases: Vec<TextureRef>,
    /// Textures the frame draws that the far side does not hold yet, with
    /// their pixels (whole: a broken decode is the magenta placeholder).
    pub uploads: Vec<(TextureRef, UiPixels)>,
    /// The batch's runs that draw this frame: a run of a runtime texture
    /// nobody assigned yet is left out.
    pub runs: Vec<UiRun>,
}

/// What the far side holds of the UI's textures, and so what each frame
/// owes it. The GPU renderer keeps one, and so can a page that does its own
/// uploading.
#[derive(Debug, Default)]
pub struct TextureLedger {
    held: HashSet<TextureRef>,
    missing: HashSet<u32>,
    new_missing: Vec<u32>,
}

impl TextureLedger {
    /// A ledger of a far side that holds nothing.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// What drawing `batch` on a target of `size` pixels owes the far side;
    /// textures first drawn are read from `source`, and the far side holds
    /// them from here on. Released textures are dropped even when nothing
    /// draws; nothing is read for an empty batch or an empty target.
    pub fn plan(
        &mut self,
        batch: &UiBatch,
        size: (u32, u32),
        source: &mut dyn UiTextureSource,
    ) -> TexturePlan {
        let mut plan = TexturePlan::default();
        for texture in source.released() {
            if self.release(texture) {
                plan.releases.push(texture);
            }
        }
        if batch.is_empty() || size.0 == 0 || size.1 == 0 {
            return plan;
        }
        for run in &batch.runs {
            let draws = match run.key.texture {
                UiTexture::White => true,
                UiTexture::Named(texture) => self.ensure(texture, source, &mut plan.uploads),
            };
            if draws {
                plan.runs.push(*run);
            }
        }
        plan
    }

    /// Make sure the far side holds `texture`, owing it the pixels when it
    /// does not; false when it draws nothing.
    fn ensure(
        &mut self,
        texture: TextureRef,
        source: &mut dyn UiTextureSource,
        uploads: &mut Vec<(TextureRef, UiPixels)>,
    ) -> bool {
        if self.held.contains(&texture) {
            return true;
        }
        let pixels = match (source.decode(texture), texture) {
            (Some(pixels), _) if pixels.is_whole() => pixels,
            // Pixels that do not fill their size (a broken decode).
            (Some(_), _) => UiPixels::magenta(),
            (None, TextureRef::Surface(id)) => {
                if self.missing.insert(id) {
                    self.new_missing.push(id);
                }
                UiPixels::magenta()
            }
            // A runtime texture nobody has assigned yet: nothing to draw.
            (None, TextureRef::Runtime(_)) => return false,
        };
        self.held.insert(texture);
        uploads.push((texture, pixels));
        true
    }

    /// The far side holds `texture`, got some other way (a texture the
    /// renderer draws into): it is never read or uploaded while held.
    pub fn hold(&mut self, texture: TextureRef) {
        self.held.insert(texture);
    }

    /// The far side dropped `texture`; false when it did not hold it.
    pub fn release(&mut self, texture: TextureRef) -> bool {
        self.held.remove(&texture)
    }

    /// Whether the far side holds `texture`.
    #[must_use]
    pub fn holds(&self, texture: TextureRef) -> bool {
        self.held.contains(&texture)
    }

    /// How many textures the far side holds.
    #[must_use]
    pub fn held_count(&self) -> usize {
        self.held.len()
    }

    /// The far side lost everything (a page reloaded its canvas): every
    /// texture a frame draws is owed again. Surfaces already named missing
    /// are not named again.
    pub fn lost_everything(&mut self) {
        self.held.clear();
    }

    /// Render surfaces the dats could not give since the last call (each
    /// named once).
    pub fn take_missing(&mut self) -> Vec<u32> {
        std::mem::take(&mut self.new_missing)
    }
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;

    use ac_ui::draw::Filter;

    use super::super::batch::UiTextureKey;
    use super::*;

    /// A source with fixed pixels, counting what it is asked for.
    #[derive(Default)]
    struct Source {
        pixels: HashMap<TextureRef, UiPixels>,
        released: Vec<TextureRef>,
        decoded: Vec<TextureRef>,
    }

    impl UiTextureSource for Source {
        fn decode(&mut self, texture: TextureRef) -> Option<UiPixels> {
            self.decoded.push(texture);
            self.pixels.get(&texture).cloned()
        }

        fn released(&mut self) -> Vec<TextureRef> {
            std::mem::take(&mut self.released)
        }
    }

    fn rgba(w: u32, h: u32) -> UiPixels {
        UiPixels::Rgba(DecodedTexture {
            width: w,
            height: h,
            rgba: vec![7; (w * h * 4) as usize],
        })
    }

    fn run(texture: UiTexture, first: u32) -> UiRun {
        UiRun {
            key: UiTextureKey {
                texture,
                filter: Filter::Linear,
            },
            first,
            count: 3,
        }
    }

    fn batch(runs: Vec<UiRun>) -> UiBatch {
        UiBatch {
            vertices: vec![bytemuck::Zeroable::zeroed(); 3 * runs.len()],
            runs,
            ..UiBatch::default()
        }
    }

    const SIZE: (u32, u32) = (800, 600);
    const A: TextureRef = TextureRef::Surface(0x0600_0001);
    const B: TextureRef = TextureRef::Surface(0x0600_0002);
    const GONE: TextureRef = TextureRef::Surface(0x0600_0bad);
    const LATE: TextureRef = TextureRef::Runtime(9);

    fn source() -> Source {
        Source {
            pixels: HashMap::from([(A, rgba(2, 2)), (B, rgba(1, 1))]),
            ..Source::default()
        }
    }

    #[test]
    fn a_texture_is_read_and_uploaded_the_first_frame_it_draws_only() {
        let mut ledger = TextureLedger::new();
        let mut source = source();
        let frame = batch(vec![
            run(UiTexture::Named(A), 0),
            run(UiTexture::White, 3),
            run(UiTexture::Named(A), 6),
            run(UiTexture::Named(B), 9),
        ]);
        let plan = ledger.plan(&frame, SIZE, &mut source);
        assert_eq!(plan.uploads, [(A, rgba(2, 2)), (B, rgba(1, 1))]);
        assert_eq!(plan.runs, frame.runs, "every run draws");
        assert!(plan.releases.is_empty());
        assert_eq!(source.decoded, [A, B], "each read once");

        let again = ledger.plan(&frame, SIZE, &mut source);
        assert!(again.uploads.is_empty(), "the far side holds them");
        assert_eq!(again.runs, frame.runs);
        assert_eq!(source.decoded, [A, B]);
        assert!(ledger.holds(A) && ledger.holds(B));
        assert_eq!(ledger.held_count(), 2);
    }

    #[test]
    fn a_missing_surface_is_magenta_and_named_once() {
        let mut ledger = TextureLedger::new();
        let mut source = source();
        let frame = batch(vec![run(UiTexture::Named(GONE), 0)]);
        let plan = ledger.plan(&frame, SIZE, &mut source);
        assert_eq!(plan.uploads, [(GONE, UiPixels::magenta())]);
        assert_eq!(plan.runs.len(), 1, "the placeholder draws");
        assert_eq!(ledger.take_missing(), [0x0600_0bad]);
        assert!(ledger.take_missing().is_empty());
        ledger.lost_everything();
        let plan = ledger.plan(&frame, SIZE, &mut source);
        assert_eq!(plan.uploads, [(GONE, UiPixels::magenta())], "owed again");
        assert!(ledger.take_missing().is_empty(), "not named again");
    }

    #[test]
    fn an_unassigned_runtime_texture_draws_nothing_and_is_asked_for_again() {
        let mut ledger = TextureLedger::new();
        let mut source = source();
        let frame = batch(vec![
            run(UiTexture::Named(LATE), 0),
            run(UiTexture::White, 3),
        ]);
        let plan = ledger.plan(&frame, SIZE, &mut source);
        assert!(plan.uploads.is_empty());
        assert_eq!(plan.runs, [frame.runs[1]]);
        assert!(!ledger.holds(LATE));
        source.pixels.insert(LATE, rgba(1, 1));
        let plan = ledger.plan(&frame, SIZE, &mut source);
        assert_eq!(plan.uploads, [(LATE, rgba(1, 1))]);
        assert_eq!(plan.runs, frame.runs);
        assert_eq!(source.decoded, [LATE, LATE]);
    }

    #[test]
    fn released_textures_are_dropped_first_even_when_nothing_draws() {
        let mut ledger = TextureLedger::new();
        let mut source = source();
        let frame = batch(vec![run(UiTexture::Named(A), 0)]);
        ledger.plan(&frame, SIZE, &mut source);
        source.released = vec![A, B];
        let plan = ledger.plan(&UiBatch::default(), SIZE, &mut source);
        assert_eq!(plan.releases, [A], "only what the far side holds");
        assert!(plan.uploads.is_empty() && plan.runs.is_empty());
        assert!(!ledger.holds(A));
        // Released and drawn again in one frame: dropped, then read again.
        ledger.plan(&frame, SIZE, &mut source);
        source.released = vec![A];
        let plan = ledger.plan(&frame, SIZE, &mut source);
        assert_eq!(plan.releases, [A]);
        assert_eq!(plan.uploads, [(A, rgba(2, 2))]);
        assert_eq!(source.decoded, [A, A, A]);
    }

    #[test]
    fn nothing_is_read_for_an_empty_target() {
        let mut ledger = TextureLedger::new();
        let mut source = source();
        let frame = batch(vec![run(UiTexture::Named(A), 0)]);
        for size in [(0, 600), (800, 0)] {
            let plan = ledger.plan(&frame, size, &mut source);
            assert!(plan.uploads.is_empty() && plan.runs.is_empty());
        }
        assert!(source.decoded.is_empty());
    }

    #[test]
    fn a_texture_held_another_way_is_never_read() {
        let mut ledger = TextureLedger::new();
        let mut source = source();
        let view = TextureRef::Runtime(0xFFFF_FF00);
        ledger.hold(view);
        let frame = batch(vec![run(UiTexture::Named(view), 0)]);
        let plan = ledger.plan(&frame, SIZE, &mut source);
        assert!(plan.uploads.is_empty());
        assert_eq!(plan.runs, frame.runs);
        assert!(source.decoded.is_empty());
        assert!(ledger.release(view));
        assert!(!ledger.release(view));
    }

    #[test]
    fn pixels_that_do_not_fill_their_size_go_up_as_magenta() {
        let mut ledger = TextureLedger::new();
        let broken = UiPixels::Rgba(DecodedTexture {
            width: 4,
            height: 4,
            rgba: vec![0; 3],
        });
        let mut source = Source {
            pixels: HashMap::from([(A, broken)]),
            ..Source::default()
        };
        let plan = ledger.plan(&batch(vec![run(UiTexture::Named(A), 0)]), SIZE, &mut source);
        assert_eq!(plan.uploads, [(A, UiPixels::magenta())]);
        assert!(ledger.take_missing().is_empty(), "it was read, not missing");
    }

    #[test]
    fn levels_name_the_bytes_a_texel() {
        let coverage = UiPixels::Coverage {
            width: 2,
            height: 1,
            alpha: vec![1, 2],
        };
        assert_eq!(coverage.levels().1, 1);
        assert!(coverage.is_whole());
        assert_eq!(coverage.gpu_bytes(), 2);
        assert_eq!(rgba(2, 2).levels().1, 4);
        assert!(!coverage.clamped() && UiPixels::Plugin(vec![]).clamped());
    }
}
