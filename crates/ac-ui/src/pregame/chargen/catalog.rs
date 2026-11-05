//! The appearance choices' dat side: palette sets, palettes and clothing
//! tables, read on first use and kept.

use std::cell::RefCell;
use std::collections::HashMap;
use std::sync::Arc;

use ac_core::chargen::{
    ChargenAnimPartChange, ChargenClothingBaseEffect, ChargenClothingPaletteTemplate,
    ChargenClothingSubPaletteChoice, ChargenClothingSubPaletteRange, ChargenClothingTable,
    ChargenClothingTableSource, ChargenPalSet, ChargenPalSetSource, ChargenPaletteColorSource,
    ChargenSwatchRgb, ChargenTextureChange,
};
use ac_dat::DatCollection;
use ac_dat::objects::{ClothingTable, Palette, PaletteSet};

/// Palette sets, palettes and clothing tables from the dats.
pub struct DatChargenCatalog {
    dats: Arc<DatCollection>,
    pal_sets: RefCell<HashMap<u32, Option<Arc<ChargenPalSet>>>>,
    tables: RefCell<HashMap<u32, Option<Arc<ChargenClothingTable>>>>,
    palettes: RefCell<HashMap<u32, Option<Arc<Palette>>>>,
}

impl std::fmt::Debug for DatChargenCatalog {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DatChargenCatalog").finish_non_exhaustive()
    }
}

impl DatChargenCatalog {
    /// A catalog over `dats`.
    #[must_use]
    pub fn new(dats: Arc<DatCollection>) -> Self {
        Self {
            dats,
            pal_sets: RefCell::default(),
            tables: RefCell::default(),
            palettes: RefCell::default(),
        }
    }
}

impl ChargenPalSetSource for DatChargenCatalog {
    fn pal_set(&self, pal_set_id: u32) -> Option<Arc<ChargenPalSet>> {
        self.pal_sets
            .borrow_mut()
            .entry(pal_set_id)
            .or_insert_with(|| {
                let set: PaletteSet = self.dats.get(pal_set_id).ok().flatten()?;
                Some(Arc::new(ChargenPalSet {
                    palette_ids: set.palettes,
                }))
            })
            .clone()
    }
}

impl ChargenClothingTableSource for DatChargenCatalog {
    fn clothing_table(&self, clothing_table_id: u32) -> Option<Arc<ChargenClothingTable>> {
        self.tables
            .borrow_mut()
            .entry(clothing_table_id)
            .or_insert_with(|| {
                let table: ClothingTable = self.dats.get(clothing_table_id).ok().flatten()?;
                Some(Arc::new(project_clothing(&table)))
            })
            .clone()
    }
}

impl ChargenPaletteColorSource for DatChargenCatalog {
    fn color(&self, palette_id: u32, index: i32) -> Option<ChargenSwatchRgb> {
        let palette = self
            .palettes
            .borrow_mut()
            .entry(palette_id)
            .or_insert_with(|| {
                let p: Palette = self.dats.get(palette_id).ok().flatten()?;
                Some(Arc::new(p))
            })
            .clone()?;
        let c = palette.colors.get(usize::try_from(index).ok()?)?;
        Some(ChargenSwatchRgb {
            r: c.red,
            g: c.green,
            b: c.blue,
        })
    }
}

/// A clothing table as the appearance rules read it: per body setup the
/// part and texture swaps, per colour template its dyes.
#[must_use]
pub fn project_clothing(table: &ClothingTable) -> ChargenClothingTable {
    let base_effects_by_setup_id = table
        .base_effects
        .iter()
        .map(|(setup, effect)| {
            let mut part_changes = Vec::new();
            let mut texture_changes = Vec::new();
            for object in &effect.object_effects {
                let part_index = u8::try_from(object.index).unwrap_or(u8::MAX);
                part_changes.push(ChargenAnimPartChange {
                    part_index,
                    part_id: object.model_id,
                });
                for t in &object.texture_effects {
                    texture_changes.push(ChargenTextureChange {
                        part_index,
                        old_texture_id: t.old_texture,
                        new_texture_id: t.new_texture,
                    });
                }
            }
            (
                *setup,
                ChargenClothingBaseEffect {
                    part_changes,
                    texture_changes,
                },
            )
        })
        .collect();
    let palette_templates_by_id = table
        .sub_pal_effects
        .iter()
        .map(|(id, effect)| {
            (
                *id,
                ChargenClothingPaletteTemplate {
                    choices: effect
                        .sub_palettes
                        .iter()
                        .map(|s| ChargenClothingSubPaletteChoice {
                            pal_set_id: s.palette_set,
                            ranges: s
                                .ranges
                                .iter()
                                .map(|r| ChargenClothingSubPaletteRange {
                                    offset: r.offset,
                                    num_colors: r.num_colors,
                                })
                                .collect(),
                        })
                        .collect(),
                },
            )
        })
        .collect();
    ChargenClothingTable {
        base_effects_by_setup_id,
        palette_templates_by_id,
    }
}
