//! The spellbook's components page (0x100002AD): the spell components the
//! player carries, grouped under their category headers, each with the
//! amount owned and the amount the player wants kept (the component
//! book's desired amounts, which the server keeps).
//!
//! The page shows what [`ComponentsView`] hands each update and asks for a
//! new desired amount with `GameplayCommand::SetDesiredComponent`.

use std::collections::{BTreeMap, HashMap};

use ac_core::items::{ClientObject, ClientObjectTable};
use ac_runtime::gameplay::GameplayCommand;
use ac_runtime::session::SessionCommand;

use crate::geometry::AnchorEdges;
use crate::icons::IconSource;
use crate::import::ImportedLayout;
use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, RowTemplates};
use crate::root::{ElementMessage, UiRoot};
use crate::states;
use crate::text::Line;
use crate::tree::{Element, ElementId};
use crate::widgets::catalog_slot::catalog_element;
use crate::widgets::{CatalogSlot, Field, ItemList, ScrollSource, Scrollbar, Text};

/// The component list (or the host one is made in).
pub const LIST_ID: u32 = 0x1000_0464;
/// Its scrollbar.
pub const SCROLLBAR_ID: u32 = 0x1000_0465;
/// The row templates' layout.
pub const TEMPLATE_LAYOUT_ID: u32 = 0x2100_0033;
/// A category header row.
pub const CATEGORY_TEMPLATE_ID: u32 = 0x1000_0466;
/// A component row.
pub const COMPONENT_TEMPLATE_ID: u32 = 0x1000_0467;
/// A component row's icon host.
pub const ICON_ID: u32 = 0x1000_0468;
/// Its name.
pub const NAME_ID: u32 = 0x1000_0469;
/// Its owned count.
pub const OWNED_COUNT_ID: u32 = 0x1000_046A;
/// Its desired count.
pub const DESIRED_COUNT_ID: u32 = 0x1000_046B;
/// A row's height, pixels.
pub const ROW_HEIGHT: f32 = 32.0;
/// The largest desired amount the page takes.
pub const MAX_DESIRED: u32 = 5000;

/// The seven categories' string keys and their English captions.
pub const CATEGORY_STRINGS: [(&str, &str); 7] = [
    ("ID_SpellComp_Category_Scarabs", "SCARABS"),
    ("ID_SpellComp_Category_Herbs", "HERBS"),
    ("ID_SpellComp_Category_Gems", "POWDERED GEMS"),
    ("ID_SpellComp_Category_Alchemical", "ALCHEMICAL SUBSTANCES"),
    ("ID_SpellComp_Category_Talismans", "TALISMANS"),
    ("ID_SpellComp_Category_Tapers", "TAPERS"),
    ("ID_SpellComp_Category_Peas", "PEAS"),
];

/// A component the dat describes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ComponentDescriptor {
    /// Its name.
    pub name: String,
    /// Its category (0..=6).
    pub category: u32,
    /// Its icon.
    pub icon: u32,
}

/// What the page reads, borrowed for one update.
#[derive(Debug, Clone, Copy)]
pub struct ComponentsView<'a> {
    /// The object table.
    pub objects: &'a ClientObjectTable,
    /// The player.
    pub player: u32,
    /// The component book's desired amounts.
    pub desired: &'a BTreeMap<u32, u32>,
    /// The dat's components, by id (weenie class).
    pub components: &'a HashMap<u32, ComponentDescriptor>,
    /// The selected object.
    pub selected: Option<u32>,
}

/// One owned component: its id, the first pack's object, its name and
/// icon, the quantity carried.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OwnedComponent {
    /// The component (weenie class).
    pub component: u32,
    /// The first pack of it.
    pub object: u32,
    /// The name shown.
    pub name: String,
    /// The icon.
    pub icon: u32,
    /// The category (8 when the dat does not know it).
    pub category: u32,
    /// How many are carried.
    pub quantity: i32,
}

fn owned_by(objects: &ClientObjectTable, item: &ClientObject, player: u32) -> bool {
    if player == 0 {
        return item.container_id != 0 || item.wielder_id != 0;
    }
    let mut current = item;
    for _ in 0..16 {
        if current.wielder_id == player || current.container_id == player {
            return true;
        }
        match objects.get(current.container_id) {
            Some(parent) if current.container_id != 0 => current = parent,
            _ => return false,
        }
    }
    false
}

/// The components the player carries, by category, then name (any case).
#[must_use]
pub fn owned_components(view: &ComponentsView<'_>) -> Vec<OwnedComponent> {
    let mut groups: BTreeMap<u32, OwnedComponent> = BTreeMap::new();
    let mut order = Vec::new();
    for item in view.objects.objects() {
        let wcid = item.weenie_class_id;
        if !(item.is_component_pack || view.components.contains_key(&wcid))
            || !owned_by(view.objects, item, view.player)
        {
            continue;
        }
        let quantity = item.stack_size.max(1);
        match groups.get_mut(&wcid) {
            Some(g) => g.quantity += quantity,
            None => {
                let d = view.components.get(&wcid);
                let name = if item.name.trim().is_empty() {
                    d.map_or_else(|| format!("Component {wcid}"), |d| d.name.clone())
                } else {
                    item.name.clone()
                };
                let icon = if item.icon_id != 0 {
                    item.icon_id
                } else {
                    d.map_or(0, |d| d.icon)
                };
                order.push(wcid);
                groups.insert(
                    wcid,
                    OwnedComponent {
                        component: wcid,
                        object: item.object_id(),
                        name,
                        icon,
                        category: d.map_or(8, |d| d.category),
                        quantity,
                    },
                );
            }
        }
    }
    let mut owned: Vec<OwnedComponent> = order
        .into_iter()
        .filter_map(|w| groups.remove(&w))
        .collect();
    owned.sort_by(|a, b| {
        let cat = |c: &OwnedComponent| {
            if view.components.contains_key(&c.component) {
                c.category
            } else {
                u32::MAX
            }
        };
        cat(a)
            .cmp(&cat(b))
            .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
    });
    owned
}

#[derive(Debug, Clone)]
struct Row {
    cell: ElementId,
    component: Option<u32>,
    object: u32,
    field: Option<ElementId>,
    committed: u32,
}

/// The components page's controller.
#[derive(Debug)]
pub struct ComponentsPageController {
    list: ElementId,
    category_names: Vec<String>,
    rows: Vec<Row>,
    shown: Option<(Vec<OwnedComponent>, BTreeMap<u32, u32>)>,
    selected: Option<u32>,
    seen_selection: Option<Option<u32>>,
    focused: Option<ElementId>,
}

impl ComponentsPageController {
    /// Bind to the components page of `layout` (the list, made in its
    /// host when the host is not one); `category_names` are the seven
    /// captions.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        category_names: Vec<String>,
    ) -> Option<Self> {
        let host = layout.find(LIST_ID)?;
        let list = if ui.widget::<ItemList>(host).is_some() {
            host
        } else {
            let (w, h) = ui.element(host).map_or((0.0, 0.0), |e| (e.width, e.height));
            let e = Element::at(0.0, 0.0, w, h).with_anchors(AnchorEdges::ALL);
            let list = ItemList::create(ui, host, e);
            // Margins now: the first layout pass skips a hidden spellbook,
            // which may have grown before it is first shown.
            ui.capture_current_anchor_baseline(list);
            list
        };
        let width = ui.element(list).map_or(96.0, |e| e.width.max(1.0));
        ui.with::<ItemList, _>(list, |l, ui| {
            l.flush(ui);
            l.columns = 1;
            l.cell_width = width;
            l.cell_height = ROW_HEIGHT;
        });
        if let Some(bar) = layout.find(SCROLLBAR_ID)
            && let Some(s) = ui.widget_mut::<Scrollbar>(bar)
        {
            s.model = ScrollSource::Element(list);
        }
        Some(Self {
            list,
            category_names,
            rows: Vec::new(),
            shown: None,
            selected: None,
            seen_selection: None,
            focused: None,
        })
    }

    /// The list.
    #[must_use]
    pub fn list(&self) -> ElementId {
        self.list
    }

    /// The rows: each cell with its component (`None` for a header).
    #[must_use]
    pub fn rows(&self) -> Vec<(ElementId, Option<u32>)> {
        self.rows.iter().map(|r| (r.cell, r.component)).collect()
    }

    /// The selected component.
    #[must_use]
    pub fn selected(&self) -> Option<u32> {
        self.selected
    }

    fn category_name(&self, category: u32) -> String {
        usize::try_from(category)
            .ok()
            .and_then(|i| self.category_names.get(i))
            .cloned()
            .unwrap_or_else(|| "OTHER COMPONENTS".to_owned())
    }

    /// Show `view`: rebuilt when the carried components or the desired
    /// amounts changed (not while a desired amount is being typed); a
    /// field the player left commits; the selection follows the world's.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &ComponentsView<'_>,
        templates: &mut dyn RowTemplates,
        icons: &mut dyn IconSource,
    ) {
        let focus = ui.keyboard_focus();
        if let Some(was) = self.focused
            && focus != Some(was)
            && let Some(i) = self.rows.iter().position(|r| r.field == Some(was))
        {
            self.commit(ui, i);
        }
        self.focused = focus;
        let editing = focus.is_some_and(|f| self.rows.iter().any(|r| r.field == Some(f)));
        let owned = owned_components(view);
        let key = (owned, view.desired.clone());
        if self.shown.as_ref() != Some(&key) && !editing {
            self.rebuild(ui, &key.0, view.desired, templates, icons);
            self.shown = Some(key);
        }
        // The world's selection, when it changed, picks the row.
        if self.seen_selection != Some(view.selected) {
            self.seen_selection = Some(view.selected);
            self.selected = view
                .selected
                .and_then(|s| view.objects.get(s))
                .filter(|o| o.is_component_pack || view.components.contains_key(&o.weenie_class_id))
                .map(|o| o.weenie_class_id);
        }
        self.sync_selection(ui);
    }

    fn rebuild(
        &mut self,
        ui: &mut UiRoot,
        owned: &[OwnedComponent],
        desired: &BTreeMap<u32, u32>,
        templates: &mut dyn RowTemplates,
        icons: &mut dyn IconSource,
    ) {
        let list = self.list;
        let width = ui.element(list).map_or(96.0, |e| e.width.max(96.0));
        ui.with::<ItemList, _>(list, |l, ui| {
            l.cell_width = width;
            l.defer_layout();
            l.flush(ui);
        });
        self.rows.clear();
        let mut category = None;
        for c in owned {
            if category != Some(c.category) {
                category = Some(c.category);
                if let Some(row) = templates.build_row(ui, TEMPLATE_LAYOUT_ID, CATEGORY_TEMPLATE_ID)
                {
                    let name = self.category_name(c.category);
                    if let Some(t) = ui.widget_mut::<Text>(row) {
                        let color = t.default_color;
                        t.set_lines(vec![Line::new(name, color)]);
                    }
                    bind_click(ui, row);
                    ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, row));
                    self.rows.push(Row {
                        cell: row,
                        component: None,
                        object: 0,
                        field: None,
                        committed: 0,
                    });
                }
            }
            let Some(row) = templates.build_row(ui, TEMPLATE_LAYOUT_ID, COMPONENT_TEMPLATE_ID)
            else {
                continue;
            };
            let find = |ui: &UiRoot, id| ui.find_descendant(row, id);
            if let Some(host) = find(ui, ICON_ID) {
                let (w, h) = ui.element(host).map_or((0.0, 0.0), |e| (e.width, e.height));
                let slot = CatalogSlot {
                    entry_id: c.component,
                    icon: icons.component_icon(c.icon),
                    show_label: false,
                    icon_width: w,
                    icon_height: h,
                    ..CatalogSlot::default()
                };
                let mut e = catalog_element(w, h);
                e.anchors = AnchorEdges::ALL;
                e.click_through = true;
                ui.add(host, e, slot);
            }
            for (id, text) in [
                (NAME_ID, c.name.clone()),
                (OWNED_COUNT_ID, c.quantity.to_string()),
            ] {
                if let Some(t) = find(ui, id).and_then(|e| ui.widget_mut::<Text>(e)) {
                    let color = t.default_color;
                    t.set_lines(vec![Line::new(text, color)]);
                }
            }
            let amount = desired.get(&c.component).copied().unwrap_or(0);
            let field = find(ui, DESIRED_COUNT_ID).filter(|f| ui.widget::<Field>(*f).is_some());
            if let Some(f) = field.and_then(|f| ui.widget_mut::<Field>(f)) {
                f.clear_on_submit = false;
                f.record_history = false;
                f.select_all_on_focus = true;
                f.character_filter = Some(Box::new(|c: char| c.is_ascii_digit()));
                f.set_text(&amount.to_string());
            }
            bind_click(ui, row);
            ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, row));
            self.rows.push(Row {
                cell: row,
                component: Some(c.component),
                object: c.object,
                field,
                committed: amount,
            });
        }
        ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
    }

    fn sync_selection(&self, ui: &mut UiRoot) {
        for r in &self.rows {
            if r.component.is_some() {
                let state = if r.component == self.selected {
                    states::HIGHLIGHT
                } else {
                    states::NORMAL
                };
                ui.set_retail_state(r.cell, state);
            }
        }
    }

    /// Take the typed desired amount of row `i`: a number of at most 5000
    /// that differs is asked for; anything else puts the field back.
    fn commit(&mut self, ui: &mut UiRoot, i: usize) {
        let Some(row) = self.rows.get(i).cloned() else {
            return;
        };
        let (Some(field), Some(component)) = (row.field, row.component) else {
            return;
        };
        let text = ui
            .widget::<Field>(field)
            .map(Field::text)
            .unwrap_or_default();
        let parsed = (!text.is_empty() && text.bytes().all(|b| b.is_ascii_digit()))
            .then(|| text.parse::<u32>().ok())
            .flatten()
            .filter(|v| *v <= MAX_DESIRED);
        let Some(amount) = parsed else {
            if let Some(f) = ui.widget_mut::<Field>(field) {
                f.set_text(&row.committed.to_string());
            }
            return;
        };
        if amount == row.committed {
            return;
        }
        self.rows[i].committed = amount;
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Gameplay(
                GameplayCommand::SetDesiredComponent { component, amount },
            )),
        );
    }

    /// A message for the page; true when it was the page's.
    pub fn on_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if let ElementMessage::Submitted(_) = message
            && let Some(i) = self.rows.iter().position(|r| r.field == Some(element))
        {
            self.commit(ui, i);
            return true;
        }
        let Some(row) = self.rows.iter().find(|r| r.cell == element).cloned() else {
            return false;
        };
        if matches!(message, ElementMessage::Clicked { .. }) {
            match row.component {
                Some(c) => {
                    self.selected = Some(c);
                    self.sync_selection(ui);
                    emit(ui, PanelOutput::SelectObject(row.object));
                }
                None => {
                    self.selected = None;
                    self.sync_selection(ui);
                    emit(
                        ui,
                        PanelOutput::Session(SessionCommand::Gameplay(
                            GameplayCommand::ClearSelection,
                        )),
                    );
                }
            }
        }
        true
    }
}
