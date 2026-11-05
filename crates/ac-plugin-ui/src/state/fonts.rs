//! The fonts plugins hold, as session state (see [`crate::fonts`]): each
//! plugin's table, the bundled bakes they share, and the display density their
//! sharper bakes follow.

use std::collections::HashSet;

use super::PluginUi;
use crate::fonts::{AcquiredFont, BundledKey, FontInfo, FontTable, FontWeight};
use crate::model::UiChange;

impl PluginUi {
    fn font_table(&mut self, owner: &str) -> &mut FontTable {
        let last = self.last_font_handles.get(owner).copied().unwrap_or(0);
        self.fonts
            .entry(owner.to_owned())
            .or_insert_with(|| FontTable::after(last))
    }

    /// The bundled bakes plugins other than `owner` hold: those cost `owner`
    /// nothing.
    fn bundled_held_elsewhere(&self, owner: &str) -> HashSet<BundledKey> {
        self.fonts
            .iter()
            .filter(|(o, _)| o.as_str() != owner)
            .flat_map(|(_, table)| table.bundled_keys())
            .collect()
    }

    fn font_answer(&mut self, owner: &str, result: Result<AcquiredFont, String>) -> FontInfo {
        match result {
            Ok(acquired) => {
                if let Some(notice) = acquired.notice {
                    self.reports.push((owner.to_owned(), notice));
                }
                if let Some(view) = acquired.added {
                    self.changes.push(UiChange::FontAdded(Box::new(view)));
                    self.prepare_fonts(owner);
                }
                acquired.info
            }
            Err(report) => {
                if !report.is_empty() {
                    self.reports.push((owner.to_owned(), report));
                }
                FontInfo::default()
            }
        }
    }

    /// The bundled font (Noto Sans) in a weight at a size, for plugin `owner`;
    /// all zero when refused, and reported once.
    pub fn font_bundled(&mut self, owner: &str, pixel_size: f32, weight: FontWeight) -> FontInfo {
        let others = self.bundled_held_elsewhere(owner);
        let mut bundled = std::mem::take(&mut self.bundled_fonts);
        let result = self.font_table(owner).acquire_bundled(
            owner,
            pixel_size,
            weight,
            &mut bundled,
            &others,
        );
        self.bundled_fonts = bundled;
        self.font_answer(owner, result)
    }

    /// A TrueType or OpenType font plugin `owner` ships, by the plugin's own
    /// name for the file, at a size, with the characters `ranges` names (none:
    /// the default set); all zero when refused, and reported once. A second
    /// request for the same name, size and characters is the same font held
    /// once more, and its bytes are not read.
    pub fn font_load(
        &mut self,
        owner: &str,
        name: &str,
        bytes: &[u8],
        pixel_size: f32,
        ranges: &[(u32, u32)],
    ) -> FontInfo {
        let others = self.bundled_held_elsewhere(owner);
        let result = self
            .font_table(owner)
            .acquire_own(owner, name, bytes, pixel_size, ranges, &others);
        self.font_answer(owner, result)
    }

    /// Lets go of one hold; false for a handle the plugin does not hold.
    pub fn font_release(&mut self, owner: &str, handle: u32) -> bool {
        let Some(table) = self.fonts.get_mut(owner) else {
            return false;
        };
        match table.release(handle) {
            None => false,
            Some(last) => {
                if last {
                    self.changes.push(UiChange::FontRemoved {
                        owner: owner.to_owned(),
                        handle,
                    });
                }
                true
            }
        }
    }

    /// How many distinct fonts the plugin holds.
    #[must_use]
    pub fn font_count(&self, owner: &str) -> usize {
        self.fonts.get(owner).map_or(0, FontTable::count)
    }

    #[cfg(test)]
    pub(crate) fn font_owned_bytes(&self, owner: &str) -> u64 {
        self.fonts.get(owner).map_or(0, FontTable::owned_bytes)
    }

    /// The width of `text` in one of the plugin's fonts and the font's line
    /// height; nothing for a font it does not hold, and for empty text.
    #[must_use]
    pub fn font_measure(&self, owner: &str, handle: u32, text: &str) -> (f32, f32) {
        self.fonts
            .get(owner)
            .map_or((0.0, 0.0), |table| table.measure(handle, text))
    }

    /// The window's display has `density` framebuffer pixels a unit: every
    /// plugin's fonts get sharper bakes for it. Fonts are baked here, not on
    /// the window's thread, so a frame never waits for one.
    pub(super) fn set_font_density(&mut self, density: f32) {
        if self.font_density == density {
            return;
        }
        self.font_density = density;
        let owners: Vec<String> = self.fonts.keys().cloned().collect();
        for owner in owners {
            self.prepare_fonts(&owner);
        }
    }

    fn prepare_fonts(&mut self, owner: &str) {
        let density = self.font_density;
        let others = self.bundled_held_elsewhere(owner);
        let mut bundled = std::mem::take(&mut self.bundled_fonts);
        let (changed, reports) =
            self.font_table(owner)
                .prepare_density(owner, density, &mut bundled, &others);
        self.bundled_fonts = bundled;
        for view in changed {
            self.changes.push(UiChange::FontAdded(Box::new(view)));
        }
        for report in reports {
            self.reports.push((owner.to_owned(), report));
        }
    }

    /// Every font plugin `owner` holds goes with it. Its handles are never
    /// given out again, even after a reload.
    pub(super) fn release_fonts(&mut self, owner: &str) {
        if let Some(table) = self.fonts.remove(owner) {
            self.last_font_handles
                .insert(owner.to_owned(), table.last_handle());
            for handle in table.handles() {
                self.changes.push(UiChange::FontRemoved {
                    owner: owner.to_owned(),
                    handle,
                });
            }
        }
    }

    /// The fonts held, for a window that attaches.
    pub(super) fn font_snapshot(&self) -> Vec<UiChange> {
        self.fonts
            .iter()
            .flat_map(|(owner, table)| table.views(owner))
            .map(|view| UiChange::FontAdded(Box::new(view)))
            .collect()
    }
}
