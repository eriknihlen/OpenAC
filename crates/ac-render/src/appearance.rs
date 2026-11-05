//! An object's look from its appearance description: part swaps (a
//! different model on a body part: clothing, armour, a head), texture swaps
//! (a different picture on some faces of a part) and palette swaps (colour
//! ranges replaced, which is how skin, hair and cloth get their colours).
//!
//! The description is applied in the game's order: part swaps, then texture
//! swaps, then the palette. The pure rules live here; reading the models,
//! surfaces and palettes they name happens on the loader thread
//! (`assets::appearance_parts`).

use std::sync::Arc;

use ac_dat::objects::Palette;
use ac_dat::types::ColorArgb;
use ac_proto::ModelData;

use crate::meshing::MeshRef;

/// Colours in a modified palette (the game builds every modified palette
/// at this size).
pub const MODIFIED_PALETTE_COLORS: u32 = 0x800;

/// One palette range replaced by the same range of another palette, in
/// colours (the wire counts in units of eight).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct SubPaletteRange {
    /// Palette the colours come from.
    pub palette_id: u32,
    /// First colour replaced.
    pub offset: u32,
    /// Number of colours replaced.
    pub count: u32,
}

impl SubPaletteRange {
    /// A range as the wire states it: offset and length in units of eight
    /// colours, a length of 0 meaning 256 units (every colour).
    #[must_use]
    pub fn from_wire(palette_id: u32, offset: u8, length: u8) -> Self {
        let units = if length == 0 {
            0x100
        } else {
            u32::from(length)
        };
        Self {
            palette_id,
            offset: u32::from(offset) << 3,
            count: units << 3,
        }
    }
}

/// The palette swap of an appearance: the base palette and the ranges laid
/// over it, in order.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct PaletteSpec {
    /// The palette the swap starts from; only parts painted with it are
    /// repainted.
    pub base_palette_id: u32,
    /// Ranges, applied in order.
    pub ranges: Vec<SubPaletteRange>,
}

/// Everything that decides how a server object looks: its setup and its
/// appearance description. Equal keys look the same, so the window prepares
/// each key once.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Default)]
pub struct AppearanceKey {
    /// The model (`0x02` setup, or a `0x01` single model).
    pub setup_id: u32,
    /// The palette swap, when the description has palette ranges.
    pub palette: Option<PaletteSpec>,
    /// Texture swaps: (part index, old surface texture, new surface texture),
    /// in wire order.
    pub texture_changes: Vec<(u8, u32, u32)>,
    /// Part swaps: (part index, new model), in wire order.
    pub part_changes: Vec<(u8, u32)>,
    /// A server object in the world: a humanoid draws each part at its
    /// closest level (a figure in a viewport keeps the parts as stored).
    pub close_degrades: bool,
}

impl AppearanceKey {
    /// The key of a setup drawn with `model`.
    #[must_use]
    pub fn new(setup_id: u32, model: &ModelData) -> Self {
        let palette = (!model.sub_palettes.is_empty()).then(|| PaletteSpec {
            base_palette_id: model.base_palette_id.unwrap_or(0),
            ranges: model
                .sub_palettes
                .iter()
                .map(|s| SubPaletteRange::from_wire(s.sub_palette_id, s.offset, s.length))
                .collect(),
        });
        Self {
            setup_id,
            palette,
            texture_changes: model
                .texture_changes
                .iter()
                .map(|t| (t.part_index, t.old_texture, t.new_texture))
                .collect(),
            part_changes: model
                .anim_part_changes
                .iter()
                .map(|p| (p.part_index, p.new_model_id))
                .collect(),
            close_degrades: false,
        }
    }

    /// The key of a server object in the world drawn with `model`.
    #[must_use]
    pub fn in_world(setup_id: u32, model: &ModelData) -> Self {
        Self {
            close_degrades: true,
            ..Self::new(setup_id, model)
        }
    }

    /// True when the description changes nothing: the setup is drawn as it
    /// is stored.
    #[must_use]
    pub fn is_plain(&self) -> bool {
        self.palette.is_none() && self.texture_changes.is_empty() && self.part_changes.is_empty()
    }
}

/// Swap part models. A change for a part the setup does not have is
/// ignored; a later change for the same part wins. The part keeps its pose.
pub fn apply_part_changes(parts: &mut [MeshRef], changes: &[(u8, u32)]) {
    for &(index, model) in changes {
        if let Some(part) = parts.get_mut(usize::from(index)) {
            part.gfx_obj_id = model;
        }
    }
}

/// The surface texture a surface of part `part_index` shows instead of its
/// own `original_texture`, if a change names it. Changes apply in order and
/// each matches the surface's original texture, so the last match wins; a
/// change to texture 0 is refused.
#[must_use]
pub fn texture_change_for(
    changes: &[(u8, u32, u32)],
    part_index: usize,
    original_texture: u32,
) -> Option<u32> {
    if original_texture == 0 {
        return None;
    }
    changes
        .iter()
        .filter(|&&(part, old, new)| {
            usize::from(part) == part_index && old == original_texture && new != 0
        })
        .map(|&(_, _, new)| new)
        .next_back()
}

/// The palette a part was authored with: that of its first surface that
/// names one (`surface_palettes` in the model's surface order, 0 for a
/// surface without a palette). A palette swap repaints only parts whose
/// palette is the swap's base palette.
#[must_use]
pub fn part_original_palette(surface_palettes: impl IntoIterator<Item = u32>) -> Option<u32> {
    surface_palettes.into_iter().find(|&id| id != 0)
}

/// Build the modified palette: the base palette's first 2048 colours with
/// each range copied over them, in order, from the same positions of its
/// palette.
///
/// `None` when the swap cannot be built, which leaves every part with its
/// own colours: a range reaching past colour 2048, or a sub palette that
/// cannot be read. A single range covering all 2048 colours from offset 0
/// is that palette itself. Colours the base or a sub palette lacks are
/// left as they are (the game reads past the end of the stored colours
/// there).
pub fn compose_modified_palette(
    base: &Palette,
    ranges: &[SubPaletteRange],
    mut resolve: impl FnMut(u32) -> Option<Arc<Palette>>,
) -> Option<Palette> {
    if let [only] = ranges
        && only.offset == 0
        && only.count == MODIFIED_PALETTE_COLORS
    {
        return resolve(only.palette_id).map(|p| (*p).clone());
    }
    let mut colors: Vec<ColorArgb> = (0..MODIFIED_PALETTE_COLORS as usize)
        .map(|i| base.colors.get(i).copied().unwrap_or_default())
        .collect();
    for range in ranges {
        let end = range.offset.checked_add(range.count)?;
        if end > MODIFIED_PALETTE_COLORS {
            return None;
        }
        let sub = resolve(range.palette_id)?;
        let (start, end) = (range.offset as usize, end as usize);
        let available = sub.colors.len().clamp(start, end);
        colors[start..available].copy_from_slice(&sub.colors[start..available]);
    }
    Some(Palette {
        id: base.id,
        colors,
    })
}

#[cfg(test)]
mod tests {
    //! Palette composition after OpenAC `ComposeModifiedPaletteTests` where
    //! it agrees with the game's palette composition; the game's own rules
    //! (a 2048-colour result, a
    //! failed range failing the whole palette) are tested on their own.
    //! Part swaps after OpenAC `AnimPartChangeTests`.
    use super::*;
    use glam::Mat4;

    fn ramp(channel: u8, len: usize) -> Arc<Palette> {
        Arc::new(Palette {
            id: 0x0400_0000 | u32::from(channel),
            colors: (0..len)
                .map(|i| ColorArgb {
                    alpha: 0xFF,
                    red: channel,
                    green: 0,
                    blue: i as u8,
                })
                .collect(),
        })
    }

    fn entry(p: &Palette, i: usize) -> (u8, u8) {
        (p.colors[i].red, p.colors[i].blue)
    }

    #[test]
    fn overlapping_ranges_apply_in_order_at_eight_color_granularity() {
        let base = ramp(0x10, 2048);
        let (first, second) = (ramp(0x20, 2048), ramp(0x30, 2048));
        let ranges = [
            SubPaletteRange::from_wire(1, 1, 1),
            SubPaletteRange::from_wire(2, 1, 2),
        ];
        let out = compose_modified_palette(&base, &ranges, |id| match id {
            1 => Some(Arc::clone(&first)),
            2 => Some(Arc::clone(&second)),
            _ => None,
        })
        .unwrap();
        assert_eq!(out.colors.len(), 2048);
        for i in 0..8 {
            assert_eq!(entry(&out, i), (0x10, i as u8));
        }
        for i in 8..24 {
            assert_eq!(entry(&out, i), (0x30, i as u8));
        }
        for i in 24..64 {
            assert_eq!(entry(&out, i), (0x10, i as u8));
        }
        assert_eq!(entry(&base, 10), (0x10, 10), "the base is not changed");
    }

    #[test]
    fn a_zero_length_byte_means_two_thousand_forty_eight_colors() {
        assert_eq!(SubPaletteRange::from_wire(1, 0, 0).count, 2048);
        assert_eq!(SubPaletteRange::from_wire(1, 3, 2).offset, 24);
        let whole = ramp(0x20, 2048);
        let out = compose_modified_palette(
            &ramp(0x10, 2048),
            &[SubPaletteRange::from_wire(1, 0, 0)],
            |_| Some(Arc::clone(&whole)),
        )
        .unwrap();
        assert!((0..2048).all(|i| out.colors[i].red == 0x20));
    }

    #[test]
    fn an_empty_range_list_is_a_copy_of_the_base() {
        let base = ramp(0x10, 2048);
        let out = compose_modified_palette(&base, &[], |_| None).unwrap();
        assert_eq!(out, *base);
    }

    #[test]
    fn a_short_sub_palette_replaces_only_the_colors_it_has() {
        let short = ramp(0x20, 12);
        let out = compose_modified_palette(
            &ramp(0x10, 2048),
            &[SubPaletteRange::from_wire(1, 1, 2)],
            |_| Some(Arc::clone(&short)),
        )
        .unwrap();
        for i in 8..12 {
            assert_eq!(entry(&out, i), (0x20, i as u8));
        }
        for i in 12..24 {
            assert_eq!(entry(&out, i), (0x10, i as u8));
        }
    }

    #[test]
    fn the_result_always_has_2048_colors() {
        let out = compose_modified_palette(&ramp(0x10, 32), &[], |_| None).unwrap();
        assert_eq!(out.colors.len(), 2048);
        assert_eq!(out.colors[40], ColorArgb::default());
    }

    #[test]
    fn a_missing_sub_palette_fails_the_whole_palette() {
        // OpenAC skips only the missing range; in the game the range fails
        // and the composition then returns no palette.
        let present = ramp(0x20, 2048);
        let ranges = [
            SubPaletteRange::from_wire(1, 0, 1),
            SubPaletteRange::from_wire(2, 1, 1),
        ];
        let out = compose_modified_palette(&ramp(0x10, 2048), &ranges, |id| {
            (id == 2).then(|| Arc::clone(&present))
        });
        assert_eq!(out, None);
    }

    #[test]
    fn a_range_past_the_end_fails_the_whole_palette() {
        let sub = ramp(0x20, 2048);
        let range = SubPaletteRange {
            palette_id: 1,
            offset: 2040,
            count: 16,
        };
        assert_eq!(
            compose_modified_palette(&ramp(0x10, 2048), &[range], |_| Some(Arc::clone(&sub))),
            None
        );
    }

    fn part(id: u32) -> MeshRef {
        MeshRef {
            gfx_obj_id: id,
            transform: Mat4::from_translation(glam::Vec3::splat(id as f32)),
            variant: 0,
        }
    }

    #[test]
    fn a_part_change_swaps_the_model_and_keeps_the_pose() {
        let mut parts = vec![part(0x0100_0001), part(0x0100_0002)];
        let pose = parts[1].transform;
        apply_part_changes(&mut parts, &[(1, 0x0199_9999), (7, 0x0188_8888)]);
        assert_eq!(
            parts[0].gfx_obj_id, 0x0100_0001,
            "no change: the setup's own"
        );
        assert_eq!(parts[1].gfx_obj_id, 0x0199_9999);
        assert_eq!(parts[1].transform, pose);
    }

    #[test]
    fn a_later_part_change_for_the_same_part_wins() {
        let mut parts = vec![part(0x0100_0001)];
        apply_part_changes(&mut parts, &[(0, 0x0100_0001), (0, 0x0199_9999)]);
        assert_eq!(parts[0].gfx_obj_id, 0x0199_9999);
    }

    #[test]
    fn texture_changes_match_the_original_texture_and_the_last_wins() {
        let changes = [
            (2, 0x0500_0001, 0x0500_00AA),
            (2, 0x0500_0001, 0x0500_00BB),
            (3, 0x0500_0001, 0x0500_00CC),
            (2, 0x0500_0002, 0),
        ];
        assert_eq!(
            texture_change_for(&changes, 2, 0x0500_0001),
            Some(0x0500_00BB)
        );
        assert_eq!(
            texture_change_for(&changes, 3, 0x0500_0001),
            Some(0x0500_00CC)
        );
        assert_eq!(texture_change_for(&changes, 1, 0x0500_0001), None);
        assert_eq!(texture_change_for(&changes, 2, 0x0500_0002), None);
        assert_eq!(texture_change_for(&changes, 2, 0), None);
    }

    #[test]
    fn a_part_is_painted_with_its_first_named_palette() {
        assert_eq!(
            part_original_palette([0, 0x0400_007E, 0x0400_0001]),
            Some(0x0400_007E)
        );
        assert_eq!(part_original_palette([0, 0]), None);
    }

    #[test]
    fn the_key_records_the_description_and_an_empty_one_is_plain() {
        assert!(AppearanceKey::new(0x0200_0001, &ModelData::default()).is_plain());
        let model = ModelData {
            base_palette_id: Some(0x0400_007E),
            sub_palettes: vec![ac_proto::SubPalette {
                sub_palette_id: 0x0400_1000,
                offset: 3,
                length: 0,
            }],
            ..ModelData::default()
        };
        let key = AppearanceKey::new(0x0200_0001, &model);
        assert!(!key.is_plain());
        let palette = key.palette.unwrap();
        assert_eq!(palette.base_palette_id, 0x0400_007E);
        assert_eq!(
            palette.ranges,
            [SubPaletteRange {
                palette_id: 0x0400_1000,
                offset: 24,
                count: 2048
            }]
        );
    }
}
