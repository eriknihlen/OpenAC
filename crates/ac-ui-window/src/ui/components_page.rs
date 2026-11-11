//! The spellbook's components page in the game window: bound when the
//! spellbook mounts (its category captions from the interface strings),
//! fed the carried components and the component book's desired amounts
//! when they change and while the spellbook shows.

use std::collections::HashMap;

use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::import::LayoutSource;
use ac_ui::panels::DatRowTemplates;
use ac_ui::panels::magic::SpellbookWindowController;
use ac_ui::panels::spell_components::{CATEGORY_STRINGS, ComponentDescriptor, ComponentsView};
use ac_ui::strings::compute_hash;

use super::facts::Dirty;
use super::window::{SPELLBOOK_WINDOW, UI_STRING_TABLE, WindowUi};

impl WindowUi {
    /// The components page's seven category captions (OpenAC
    /// `ComponentBookTemplateFactory.TryLoad`).
    pub(super) fn component_category_names(&self) -> Vec<String> {
        let strings = self.content.strings();
        CATEGORY_STRINGS
            .iter()
            .map(|(key, fallback)| {
                strings
                    .resolve(UI_STRING_TABLE, compute_hash(key), 0)
                    .unwrap_or_else(|| (*fallback).to_owned())
            })
            .collect()
    }

    /// Feed the components page.
    pub(super) fn update_components_page(&mut self, dirty: Dirty, stale: &[&str]) {
        let shown = self.host.root.is_window_visible(SPELLBOOK_WINDOW);
        if !(shown || dirty.items || dirty.player || stale.contains(&SPELLBOOK_WINDOW)) {
            return;
        }
        let selected = self.player_view.and_then(|v| v.selected);
        let Self {
            host,
            facts,
            icons,
            content,
            components,
            component_classes,
            ..
        } = self;
        let Some(book) = facts.numbers().and_then(|n| n.spellbook()) else {
            return;
        };
        let descriptors = components
            .as_ref()
            .map(|t| component_descriptors(t, component_classes))
            .unwrap_or_default();
        let view = ComponentsView {
            objects: &facts.objects,
            player: facts.state.player,
            desired: book.desired_components(),
            components: &descriptors,
            selected,
        };
        let mut rows = DatRowTemplates::new(&*content, content.font(DEFAULT_FONT_ID));
        host.with_controller::<SpellbookWindowController, _>(SPELLBOOK_WINDOW, |c, ui| {
            if let Some(p) = c.components_mut() {
                p.update(ui, &view, &mut rows, icons);
            }
        });
    }
}

/// Each spell component's descriptor keyed by its weenie class, as the
/// components page knows a carried component (OpenAC
/// `MagicCatalog.Load`: the component table through the component id map
/// 0x27000002). A component the map does not name is left out.
#[must_use]
pub fn component_descriptors(
    table: &ac_dat::objects::SpellComponentTable,
    classes: &HashMap<u32, u32>,
) -> HashMap<u32, ComponentDescriptor> {
    table
        .components
        .iter()
        .filter_map(|(id, b)| {
            Some((
                *classes.get(id)?,
                ComponentDescriptor {
                    name: b.name.clone(),
                    category: b.category,
                    icon: b.icon,
                },
            ))
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// OpenAC `MagicCatalog.Load` on the retail dats: the descriptors are
    /// keyed by each component's weenie class, not its component id.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_descriptors_are_keyed_by_weenie_class() {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let dats = ac_dat::DatCollection::open(dir).expect("open the retail dats");
        let table: ac_dat::objects::SpellComponentTable = dats
            .get(ac_dat::objects::SPELL_COMPONENT_TABLE_ID)
            .unwrap()
            .unwrap();
        let map: ac_dat::objects::EnumIdMap = dats.get(0x2700_0002).unwrap().unwrap();
        let classes: HashMap<u32, u32> = table
            .components
            .iter()
            .filter_map(|(id, _)| Some((*id, map.client_id(*id)?)))
            .collect();
        assert!(!classes.is_empty());
        let d = component_descriptors(&table, &classes);
        assert_eq!(d.len(), classes.len());
        assert!(classes.iter().any(|(id, class)| id != class));
        for (id, base) in &table.components {
            if let Some(class) = classes.get(id) {
                assert_eq!(d[class].name, base.name);
            }
        }
    }
}
