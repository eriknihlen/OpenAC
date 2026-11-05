//! The shared item cooldown sweep: every item list of the interface shows,
//! over the cell of an item whose cooldown group is running, one of the ten
//! authored overlays by how much of the item's cooldown is left. One clock
//! for every list; only lists shown on screen are walked, and an item's
//! step is decided once per frame wherever it shows.

use std::collections::HashMap;

use ac_core::items::{ClientObjectTable, cooldown_overlay_step};
use ac_core::spells::Spellbook;

use crate::root::UiRoot;
use crate::tree::ElementId;
use crate::widgets::{ItemList, ItemSlot};

/// See the module documentation.
#[derive(Debug, Default)]
pub struct ItemCooldownController {
    lists: Vec<ElementId>,
    steps: HashMap<u32, i32>,
}

impl ItemCooldownController {
    /// Every item list under `root` takes the overlays (their present and
    /// future cells).
    pub fn bind(ui: &mut UiRoot, root: ElementId, sprites: &[u32]) -> Self {
        let mut lists = Vec::new();
        let mut stack = vec![root];
        while let Some(e) = stack.pop() {
            if ui.widget::<ItemList>(e).is_some() {
                ui.with::<ItemList, _>(e, |l, ui| l.set_cooldown_sprites(ui, sprites));
                lists.push(e);
            }
            let mut children = ui.children(e);
            children.reverse();
            stack.extend(children);
        }
        Self {
            lists,
            steps: HashMap::new(),
        }
    }

    /// The lists bound.
    #[must_use]
    pub fn lists(&self) -> &[ElementId] {
        &self.lists
    }

    /// The step `item` shows (0: none).
    #[must_use]
    pub fn step(&self, item: u32) -> i32 {
        self.steps.get(&item).copied().unwrap_or(0)
    }

    /// One frame: with a cooldown running, each item of a shown list gets
    /// its step from the spellbook at `now` (session seconds, read only
    /// then); every cell then shows its item's step.
    pub fn tick(
        &mut self,
        ui: &mut UiRoot,
        spellbook: Option<&mut Spellbook>,
        objects: &ClientObjectTable,
        now: impl FnOnce() -> f64,
    ) {
        self.steps.clear();
        if let Some(book) = spellbook.filter(|b| b.has_cooldown_enchantments()) {
            let now = now();
            for &list in &self.lists {
                if !effectively_visible(ui, list) {
                    continue;
                }
                let cells = ui
                    .widget::<ItemList>(list)
                    .map(|l| l.cells().to_vec())
                    .unwrap_or_default();
                for cell in cells {
                    let Some(item) = ui.widget::<ItemSlot>(cell).map(|s| s.object_id) else {
                        continue;
                    };
                    if item == 0 || self.steps.contains_key(&item) {
                        continue;
                    }
                    let step = objects
                        .get(item)
                        .and_then(|o| Some((o.cooldown_id?, o.cooldown_duration?)))
                        .map_or(0, |(id, duration)| {
                            let check = book.on_cooldown(id, now);
                            if check.active {
                                cooldown_overlay_step(duration, check.remaining)
                            } else {
                                0
                            }
                        });
                    self.steps.insert(item, step);
                }
            }
        }
        for &list in &self.lists {
            let cells = ui
                .widget::<ItemList>(list)
                .map(|l| l.cells().to_vec())
                .unwrap_or_default();
            for cell in cells {
                if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                    s.cooldown_step = self.steps.get(&s.object_id).copied().unwrap_or(0);
                }
            }
        }
    }
}

/// `element` and every parent are visible.
#[must_use]
pub fn effectively_visible(ui: &UiRoot, element: ElementId) -> bool {
    let mut at = Some(element);
    while let Some(e) = at {
        if !ui.element(e).is_some_and(|e| e.visible) {
            return false;
        }
        at = ui.parent(e);
    }
    true
}
