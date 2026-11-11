//! The key of an object's look: its setup and its appearance description
//! (part swaps, texture swaps, palette swaps). Equal keys look the same; the
//! renderer composes the look a key names.

use ac_proto::ModelData;

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
