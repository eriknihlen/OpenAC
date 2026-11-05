//! Icon composition: an item's cell icon is its type underlay, its own
//! underlay, its base icon, its overlay and its effect tint, layered from
//! dat render surfaces; a spell's icon is its power-level backing, its art,
//! its tint and its self/fellowship overlay.
//!
//! Composition is pure pixel work on decoded surfaces. The composer hands
//! each finished image to an [`IconUploader`] (the renderer's texture
//! cache) once and remembers the texture it got back, so the panels only
//! ever see [`TextureRef`]s.

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::ItemType;
use ac_core::spells::determine_power_level_of_component;
use ac_dat::DatCollection;
use ac_dat::objects::{EnumIdMap, Palette, RenderSurface, SpellTable};
use ac_dat::texture::{DecodeOptions, decode};

use crate::draw::TextureRef;

/// The side of every composed spell and object icon, pixels.
pub const ICON_EXTENT: u32 = 32;
/// The master enum map slot naming the item-type underlay map.
pub const UNDERLAY_ENUM_CATEGORY: u32 = 0x1000_0004;
/// The master enum map slot naming the item-effect tile map.
pub const EFFECT_ENUM_CATEGORY: u32 = 0x1000_0005;
/// The master enum map slot naming the spell power-level backings.
pub const SPELL_POWER_ENUM_CATEGORY: u32 = 0x1000_0006;
/// The master enum map slot naming the spell tints and overlays.
pub const SPELL_TINT_ENUM_CATEGORY: u32 = 0x1000_0007;
/// The spell table.
pub const SPELL_TABLE_ID: u32 = 0x0E00_000E;

/// The spell's bit field: a harmful (reversed) spell.
const SPELL_REVERSED: u32 = 0x0000_0010;
/// The spell's bit field: cast on the caster.
const SPELL_SELF_TARGETED: u32 = 0x0000_0008;
/// The spell's bit field: cast on the fellowship.
const SPELL_FELLOWSHIP: u32 = 0x0000_2000;

/// A decoded image: rows from the top, four bytes per pixel (RGBA).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Rgba {
    /// Width, pixels.
    pub width: u32,
    /// Height, pixels.
    pub height: u32,
    /// The pixels.
    pub pixels: Vec<u8>,
}

impl Rgba {
    /// An image of `width` × `height` from its pixels.
    #[must_use]
    pub fn new(width: u32, height: u32, pixels: Vec<u8>) -> Self {
        Self {
            width,
            height,
            pixels,
        }
    }
}

/// Alpha-blend `layers` over the first, in order: each source pixel with
/// alpha `a` replaces `a` of the result. The result has the first layer's
/// size; later layers are clipped to it. No layers give an empty image.
#[must_use]
pub fn compose(layers: &[&Rgba]) -> Rgba {
    let Some(first) = layers.first() else {
        return Rgba::default();
    };
    let (w, h) = (first.width as usize, first.height as usize);
    let mut out = first.pixels.clone();
    for src in &layers[1..] {
        let (sw, sh) = (src.width as usize, src.height as usize);
        let (cw, ch) = (w.min(sw), h.min(sh));
        for y in 0..ch {
            for x in 0..cw {
                let di = (y * w + x) * 4;
                let si = (y * sw + x) * 4;
                if si + 3 >= src.pixels.len() || di + 3 >= out.len() {
                    continue;
                }
                let sa = f32::from(src.pixels[si + 3]) / 255.0;
                if sa <= 0.0 {
                    continue;
                }
                let da = 1.0 - sa;
                // Truncation toward zero, as the byte casts it ports do.
                for c in 0..3 {
                    out[di + c] =
                        (f32::from(src.pixels[si + c]) * sa + f32::from(out[di + c]) * da) as u8;
                }
                out[di + 3] =
                    (f32::from(src.pixels[si + 3]) + f32::from(out[di + 3]) * da).min(255.0) as u8;
            }
        }
    }
    Rgba::new(first.width, first.height, out)
}

/// Replace every pure opaque white pixel of `dst` with the pixel at the
/// same place in `src` (where `src` has one): the effect tint keyed into an
/// icon's white areas.
pub fn replace_white_from_surface(dst: &mut Rgba, src: &Rgba) {
    let (dw, dh) = (dst.width as usize, dst.height as usize);
    let (sw, sh) = (src.width as usize, src.height as usize);
    for y in 0..dh {
        for x in 0..dw {
            let di = (y * dw + x) * 4;
            if di + 3 >= dst.pixels.len() {
                return;
            }
            if dst.pixels[di..di + 4] == [255, 255, 255, 255] && x < sw && y < sh {
                let si = (y * sw + x) * 4;
                if si + 3 < src.pixels.len() {
                    dst.pixels[di..di + 4].copy_from_slice(&src.pixels[si..si + 4]);
                }
            }
        }
    }
}

/// Where composed icons go: the renderer uploads the image and names the
/// texture it made.
pub trait IconUploader {
    /// Upload `image` (nearest-filtered UI art) and return its texture.
    fn upload(&mut self, image: &Rgba) -> TextureRef;
}

/// What the item panels ask for icons. The [`IconComposer`] answers from
/// the dats; tests answer with fixed textures.
pub trait IconSource {
    /// An item's cell icon (type underlay, underlay, icon, overlay, effect).
    fn item_icon(
        &mut self,
        item_type: ItemType,
        icon: u32,
        underlay: u32,
        overlay: u32,
        effects: u32,
    ) -> Option<TextureRef>;

    /// The icon a drag shows: the item's icon, overlay and effect without
    /// the underlays.
    fn drag_icon(
        &mut self,
        item_type: ItemType,
        icon: u32,
        underlay: u32,
        overlay: u32,
        effects: u32,
    ) -> Option<TextureRef>;

    /// A spell's icon.
    fn spell_icon(&mut self, spell_id: u32) -> Option<TextureRef>;

    /// A spell component's icon (its pure white turned black).
    fn component_icon(&mut self, icon: u32) -> Option<TextureRef>;
}

/// An icon source that has none (panels show empty cells).
#[derive(Debug, Clone, Copy, Default)]
pub struct NoIcons;

impl IconSource for NoIcons {
    fn item_icon(&mut self, _: ItemType, _: u32, _: u32, _: u32, _: u32) -> Option<TextureRef> {
        None
    }
    fn drag_icon(&mut self, _: ItemType, _: u32, _: u32, _: u32, _: u32) -> Option<TextureRef> {
        None
    }
    fn spell_icon(&mut self, _: u32) -> Option<TextureRef> {
        None
    }
    fn component_icon(&mut self, _: u32) -> Option<TextureRef> {
        None
    }
}

/// The index into the underlay map for an item type: one past its lowest
/// set bit, 0x21 for no bits.
#[must_use]
pub fn underlay_index(item_type: ItemType) -> u32 {
    let raw = item_type.bits();
    if raw == 0 {
        0x21
    } else {
        raw.trailing_zeros() + 1
    }
}

/// The index into the effect map for an effects bit field: one past its
/// lowest set bit, 0 for no bits.
#[must_use]
pub fn effect_index(effects: u32) -> u32 {
    if effects == 0 {
        0
    } else {
        effects.trailing_zeros() + 1
    }
}

/// A composed drag image and its uploaded texture.
type DragImage = (Arc<Rgba>, TextureRef);

/// Composes item and spell icons from the dats. See the module
/// documentation.
pub struct IconComposer {
    dats: Arc<DatCollection>,
    uploader: Box<dyn IconUploader>,
    sub_maps: HashMap<u32, Option<EnumIdMap>>,
    surfaces: HashMap<u32, Option<Arc<Rgba>>>,
    composed: HashMap<(u32, u32, u32, u32, u32), Option<TextureRef>>,
    drag: HashMap<(u32, u32, u32), Option<DragImage>>,
    spells: HashMap<u32, Option<TextureRef>>,
    components: HashMap<u32, Option<TextureRef>>,
    spell_table: Option<Option<SpellTable>>,
}

impl std::fmt::Debug for IconComposer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("IconComposer")
            .field("composed", &self.composed.len())
            .field("spells", &self.spells.len())
            .finish_non_exhaustive()
    }
}

impl IconComposer {
    /// A composer over `dats`, uploading through `uploader`.
    #[must_use]
    pub fn new(dats: Arc<DatCollection>, uploader: Box<dyn IconUploader>) -> Self {
        Self {
            dats,
            uploader,
            sub_maps: HashMap::new(),
            surfaces: HashMap::new(),
            composed: HashMap::new(),
            drag: HashMap::new(),
            spells: HashMap::new(),
            components: HashMap::new(),
            spell_table: None,
        }
    }

    /// The dat id stored at `index` of the enum map in master slot
    /// `category` (0 when there is none).
    pub fn resolve_enum(&mut self, category: u32, index: u32) -> u32 {
        if !self.sub_maps.contains_key(&category) {
            let map = self.load_sub_map(category);
            self.sub_maps.insert(category, map);
        }
        self.sub_maps
            .get(&category)
            .and_then(Option::as_ref)
            .and_then(|m| m.client_id(index))
            .unwrap_or(0)
    }

    fn load_sub_map(&self, category: u32) -> Option<EnumIdMap> {
        let master_id = self.dats.portal.header().master_map_id.cast_unsigned();
        if master_id == 0 {
            return None;
        }
        let master: EnumIdMap = self.dats.get(master_id).ok().flatten()?;
        let sub_id = master.client_id(category)?;
        self.dats.get(sub_id).ok().flatten()
    }

    /// The type underlay surface of an item type.
    pub fn resolve_underlay(&mut self, item_type: ItemType) -> u32 {
        self.resolve_enum(UNDERLAY_ENUM_CATEGORY, underlay_index(item_type))
    }

    /// The effect tile surface of an effects bit field (the "no effect"
    /// tile, index 0x21, when the bit has none).
    pub fn resolve_effect(&mut self, effects: u32) -> u32 {
        match self.resolve_enum(EFFECT_ENUM_CATEGORY, effect_index(effects)) {
            0 => self.resolve_enum(EFFECT_ENUM_CATEGORY, 0x21),
            did => did,
        }
    }

    /// Render surface `id` decoded (cached; `None` for 0 or a surface that
    /// does not decode).
    pub fn surface(&mut self, id: u32) -> Option<Arc<Rgba>> {
        if id == 0 {
            return None;
        }
        if let Some(s) = self.surfaces.get(&id) {
            return s.clone();
        }
        let decoded = self.decode_surface(id).map(Arc::new);
        self.surfaces.insert(id, decoded.clone());
        decoded
    }

    fn decode_surface(&self, id: u32) -> Option<Rgba> {
        let rs: RenderSurface = self.dats.get(id).ok().flatten()?;
        let palette: Option<Palette> = (rs.default_palette_id != 0)
            .then(|| self.dats.get(rs.default_palette_id).ok().flatten())
            .flatten();
        let t = decode(&rs, palette.as_ref(), DecodeOptions::default()).ok()?;
        Some(Rgba::new(t.width, t.height, t.rgba))
    }

    /// The effect tile of an effects bit field, decoded.
    pub fn effect_tile(&mut self, effects: u32) -> Option<Arc<Rgba>> {
        let did = self.resolve_effect(effects);
        self.surface(did)
    }

    fn drag_image(&mut self, icon: u32, overlay: u32, effects: u32) -> Option<DragImage> {
        let key = (icon, overlay, effects);
        if let Some(d) = self.drag.get(&key) {
            return d.clone();
        }
        let layers: Vec<Arc<Rgba>> = [icon, overlay]
            .into_iter()
            .filter_map(|id| self.surface(id))
            .collect();
        let made = if layers.is_empty() {
            None
        } else {
            let refs: Vec<&Rgba> = layers.iter().map(Arc::as_ref).collect();
            let mut composed = compose(&refs);
            if let Some(tile) = self.effect_tile(effects) {
                replace_white_from_surface(&mut composed, &tile);
            }
            let texture = self.uploader.upload(&composed);
            Some((Arc::new(composed), texture))
        };
        self.drag.insert(key, made.clone());
        made
    }

    /// An icon keyed by nothing but its own art and the "no effect" tile
    /// (plugin and markup icons).
    pub fn keyed_icon(&mut self, icon: u32) -> Option<TextureRef> {
        if icon == 0 {
            return None;
        }
        self.drag_image(icon, 0, 0).map(|(_, t)| t)
    }
}

impl IconSource for IconComposer {
    fn item_icon(
        &mut self,
        item_type: ItemType,
        icon: u32,
        underlay: u32,
        overlay: u32,
        effects: u32,
    ) -> Option<TextureRef> {
        if icon == 0 {
            return None;
        }
        let type_underlay = self.resolve_underlay(item_type);
        let key = (type_underlay, icon, underlay, overlay, effects);
        if let Some(t) = self.composed.get(&key) {
            return *t;
        }
        let drag = self.drag_image(icon, overlay, effects);
        let mut layers: Vec<Arc<Rgba>> = [type_underlay, underlay]
            .into_iter()
            .filter_map(|id| self.surface(id))
            .collect();
        if let Some((image, _)) = drag {
            layers.push(image);
        }
        let texture = if layers.is_empty() {
            None
        } else {
            let refs: Vec<&Rgba> = layers.iter().map(Arc::as_ref).collect();
            Some(self.uploader.upload(&compose(&refs)))
        };
        self.composed.insert(key, texture);
        texture
    }

    fn drag_icon(
        &mut self,
        _item_type: ItemType,
        icon: u32,
        _underlay: u32,
        overlay: u32,
        effects: u32,
    ) -> Option<TextureRef> {
        if icon == 0 {
            return None;
        }
        self.drag_image(icon, overlay, effects).map(|(_, t)| t)
    }

    fn spell_icon(&mut self, spell_id: u32) -> Option<TextureRef> {
        if let Some(t) = self.spells.get(&spell_id) {
            return *t;
        }
        if self.spell_table.is_none() {
            self.spell_table = Some(self.dats.get(SPELL_TABLE_ID).ok().flatten());
        }
        let spell = self
            .spell_table
            .as_ref()
            .and_then(Option::as_ref)
            .and_then(|t| t.spells.iter().find(|(id, _)| *id == spell_id))
            .map(|(_, s)| (s.components(), s.icon, s.bitfield))?;
        let (components, art, bitfield) = spell;
        let power = components
            .first()
            .map_or(0, |&c| determine_power_level_of_component(c));
        let backing = self.resolve_enum(SPELL_POWER_ENUM_CATEGORY, power);
        let layers: Vec<Arc<Rgba>> = [backing, art]
            .into_iter()
            .filter_map(|id| self.surface(id))
            .collect();
        if layers.is_empty() {
            self.spells.insert(spell_id, None);
            return None;
        }
        let refs: Vec<&Rgba> = layers.iter().map(Arc::as_ref).collect();
        let mut composed = compose(&refs);
        let tint_index = if bitfield & SPELL_REVERSED != 0 { 1 } else { 2 };
        let tint = self.resolve_enum(SPELL_TINT_ENUM_CATEGORY, tint_index);
        if let Some(tint) = self.surface(tint) {
            replace_white_from_surface(&mut composed, &tint);
        }
        let overlay_index = if bitfield & SPELL_FELLOWSHIP != 0 {
            4
        } else if bitfield & SPELL_SELF_TARGETED != 0 {
            3
        } else {
            0
        };
        if overlay_index != 0 {
            let did = self.resolve_enum(SPELL_TINT_ENUM_CATEGORY, overlay_index);
            if let Some(overlay) = self.surface(did) {
                composed = compose(&[&composed, &overlay]);
            }
        }
        let texture = self.uploader.upload(&composed);
        self.spells.insert(spell_id, Some(texture));
        Some(texture)
    }

    fn component_icon(&mut self, icon: u32) -> Option<TextureRef> {
        if icon == 0 {
            return None;
        }
        if let Some(t) = self.components.get(&icon) {
            return *t;
        }
        let texture = self.surface(icon).map(|s| {
            let mut image = (*s).clone();
            for px in image.pixels.chunks_exact_mut(4) {
                if px == [255, 255, 255, 255] {
                    px[0] = 0;
                    px[1] = 0;
                    px[2] = 0;
                }
            }
            self.uploader.upload(&image)
        });
        self.components.insert(icon, texture);
        texture
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/IconComposerTests.cs`
    //! (the pixel cases; the dat cases are in `tests/item_panels.rs`).

    use super::*;

    fn solid(w: u32, h: u32, rgba: [u8; 4]) -> Rgba {
        Rgba::new(w, h, rgba.repeat((w * h) as usize))
    }

    #[test]
    fn compose_alpha_over_top_opaque_layer_wins() {
        let bottom = solid(2, 2, [255, 0, 0, 255]);
        let top = solid(2, 2, [0, 0, 255, 255]);
        let r = compose(&[&bottom, &top]);
        assert_eq!((r.width, r.height), (2, 2));
        assert_eq!(&r.pixels[0..4], &[0, 0, 255, 255]);
    }

    #[test]
    fn compose_alpha_over_transparent_top_keeps_bottom() {
        let bottom = solid(1, 1, [255, 0, 0, 255]);
        let top = solid(1, 1, [0, 0, 255, 0]);
        let r = compose(&[&bottom, &top]);
        assert_eq!(r.pixels[0], 255);
        assert_eq!(r.pixels[2], 0);
    }

    #[test]
    fn compose_opaque_underlay_first_result_is_fully_opaque() {
        let underlay = solid(2, 2, [128, 64, 32, 255]);
        let base = solid(2, 2, [0, 0, 0, 128]);
        let r = compose(&[&underlay, &base]);
        assert!(r.pixels.chunks(4).all(|p| p[3] == 255));
    }

    #[test]
    fn replace_white_copies_the_source_pixel_for_pure_white_opaque() {
        let mut dst = Rgba::new(
            2,
            2,
            vec![
                255, 255, 255, 255, 255, 0, 0, 255, 255, 255, 255, 0, 255, 255, 255, 255,
            ],
        );
        let src = Rgba::new(
            2,
            2,
            vec![
                10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255, 100, 110, 120, 255,
            ],
        );
        replace_white_from_surface(&mut dst, &src);
        assert_eq!(&dst.pixels[0..4], &[10, 20, 30, 255]);
        assert_eq!(&dst.pixels[4..8], &[255, 0, 0, 255]);
        assert_eq!(&dst.pixels[8..12], &[255, 255, 255, 0]);
        assert_eq!(&dst.pixels[12..16], &[100, 110, 120, 255]);
    }

    #[test]
    fn two_stage_with_effect_copies_the_tile_pixel_before_the_underlay() {
        let base = Rgba::new(1, 1, vec![255, 255, 255, 255]);
        let mut drag = compose(&[&base]);
        replace_white_from_surface(&mut drag, &Rgba::new(1, 1, vec![0, 0, 255, 255]));
        let underlay = Rgba::new(1, 1, vec![105, 70, 50, 255]);
        let r = compose(&[&underlay, &drag]);
        assert_eq!(r.pixels, vec![0, 0, 255, 255]);
    }

    #[test]
    fn underlay_and_effect_indices_are_one_past_the_lowest_bit() {
        assert_eq!(underlay_index(ItemType::NONE), 0x21);
        assert_eq!(underlay_index(ItemType::MELEE_WEAPON), 1);
        assert_eq!(underlay_index(ItemType::JEWELRY), 4);
        assert_eq!(effect_index(0), 0);
        assert_eq!(effect_index(0x1000), 13);
    }
}
