//! The colour-spot art of the appearance page: the authored spot with its
//! pure black centre painted in a swatch's colour, the blank spot, the
//! gradient disc and the eyes' plug, each read from the dats through the
//! interface enumeration and uploaded once.

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::chargen::ChargenSwatchRgb;
use ac_dat::DatCollection;
use ac_dat::objects::{Palette, RenderSurface};
use ac_dat::texture::DecodeOptions;

use super::appearance::SwatchTextures;
use crate::draw::TextureRef;
use crate::icons::{IconUploader, Rgba};
use crate::panels::options::chat_dat::resolve_data_id;

/// The spot's entry in enumeration category 7.
pub const SPOT_ENUM: u32 = 0x1000_000D;
/// The blank spot's.
pub const BLANK_ENUM: u32 = 0x1000_000F;
/// The gradient disc's.
pub const GRAD_DISK_ENUM: u32 = 0x1000_000E;
/// The eyes' plug's.
pub const GRAD_PLUG_ENUM: u32 = 0x1000_0010;
/// The enumeration category of the colour-spot art.
pub const ENUM_CATEGORY: u32 = 7;

/// `rgba` with every opaque, exactly black pixel painted `rgb`.
#[must_use]
pub fn replace_exact_black(rgba: &[u8], rgb: ChargenSwatchRgb) -> Vec<u8> {
    let mut baked = rgba.to_vec();
    for px in baked.chunks_exact_mut(4) {
        if px == [0, 0, 0, 255] {
            px.copy_from_slice(&[rgb.r, rgb.g, rgb.b, 255]);
        }
    }
    baked
}

/// The colour-spot art from the dats.
pub struct DatSwatchTextures {
    dats: Arc<DatCollection>,
    uploader: Box<dyn IconUploader>,
    spot: Option<Option<Rgba>>,
    baked: HashMap<(u8, u8, u8), TextureRef>,
    blank: Option<Option<TextureRef>>,
    disk: Option<Option<TextureRef>>,
    plug: Option<Option<TextureRef>>,
}

impl std::fmt::Debug for DatSwatchTextures {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DatSwatchTextures")
            .field("baked", &self.baked.len())
            .finish_non_exhaustive()
    }
}

impl DatSwatchTextures {
    /// Art from `dats`, uploaded through `uploader`.
    #[must_use]
    pub fn new(dats: Arc<DatCollection>, uploader: Box<dyn IconUploader>) -> Self {
        Self {
            dats,
            uploader,
            spot: None,
            baked: HashMap::new(),
            blank: None,
            disk: None,
            plug: None,
        }
    }

    fn decode(&self, value: u32) -> Option<Rgba> {
        let id = resolve_data_id(&self.dats, value, ENUM_CATEGORY);
        if id == 0 {
            return None;
        }
        let surface: RenderSurface = self.dats.get(id).ok().flatten()?;
        let palette: Option<Palette> = (surface.default_palette_id != 0)
            .then(|| self.dats.get(surface.default_palette_id).ok().flatten())
            .flatten();
        let decoded =
            ac_dat::texture::decode(&surface, palette.as_ref(), DecodeOptions::default()).ok()?;
        Some(Rgba::new(decoded.width, decoded.height, decoded.rgba))
    }

    fn uploaded(
        &mut self,
        value: u32,
        slot: fn(&mut Self) -> &mut Option<Option<TextureRef>>,
    ) -> Option<TextureRef> {
        if let Some(done) = *slot(self) {
            return done;
        }
        let texture = self.decode(value).map(|image| self.uploader.upload(&image));
        *slot(self) = Some(texture);
        texture
    }
}

impl SwatchTextures for DatSwatchTextures {
    fn blank_spot(&mut self) -> Option<TextureRef> {
        self.uploaded(BLANK_ENUM, |s| &mut s.blank)
    }

    fn grad_disk(&mut self) -> Option<TextureRef> {
        self.uploaded(GRAD_DISK_ENUM, |s| &mut s.disk)
    }

    fn grad_plug(&mut self) -> Option<TextureRef> {
        self.uploaded(GRAD_PLUG_ENUM, |s| &mut s.plug)
    }

    fn active_spot(&mut self, rgb: ChargenSwatchRgb) -> Option<TextureRef> {
        if self.spot.is_none() {
            self.spot = Some(self.decode(SPOT_ENUM));
        }
        let template = self.spot.as_ref()?.as_ref()?;
        let key = (rgb.r, rgb.g, rgb.b);
        if let Some(t) = self.baked.get(&key) {
            return Some(*t);
        }
        let baked = Rgba::new(
            template.width,
            template.height,
            replace_exact_black(&template.pixels, rgb),
        );
        let texture = self.uploader.upload(&baked);
        self.baked.insert(key, texture);
        Some(texture)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_opaque_exact_black_takes_the_colour() {
        // OpenAC `ChargenColorSpotComposerTests.
        // ReplaceExactBlackWithColor_RecolorsOnlyOpaqueExactBlackPixels_LeavesEverythingElseUntouched`
        // and `..._SemiTransparentBlackLeftAlone`.
        let source = [
            0, 0, 0, 255, // opaque black: painted
            0, 0, 0, 128, // translucent black: kept
            1, 0, 0, 255, // near black: kept
            200, 100, 50, 255, // colour: kept
        ];
        let rgb = ChargenSwatchRgb {
            r: 10,
            g: 20,
            b: 30,
        };
        let baked = replace_exact_black(&source, rgb);
        assert_eq!(
            baked,
            [
                10, 20, 30, 255, 0, 0, 0, 128, 1, 0, 0, 255, 200, 100, 50, 255
            ]
        );
        assert_eq!(source[0..4], [0, 0, 0, 255], "the source is not changed");
    }
}
