//! `<repeat>` in a plugin window (OpenAC `UiMarkupRepeat`, `MarkupRepeat`
//! and `MarkupBindingSource`): the window's elements come in sets, one for
//! the window's own markup and one per row of each repeat, each reading its
//! bindings from its own source (the panel's values, or the row's item).
//!
//! A repeat stands in its flex container as a zero-size, click-through
//! marker; its rows' top elements are the marker's siblings, placed in list
//! order straight after it, and items of the same flex layout. Whenever the
//! session's copy changes, each repeat reads its list again: with `key`, a
//! row whose key is still listed is kept (with its focus, half-typed text,
//! open menu, expander state and scroll) and moved to its new place, new
//! keys get rows and gone keys lose theirs; without `key`, or with
//! duplicate or missing keys, rows match by position. A row's handlers
//! reach the plugin named with their rows ([`ac_plugin_ui::row`]). A named
//! control in a keyed row remembers its state as `name#key` (outer keys
//! first), and a row whose key leaves the list forgets what its template's
//! names remembered.

use std::cell::RefCell;
use std::collections::{BTreeSet, HashMap, HashSet};
use std::rc::Rc;
use std::sync::Arc;

use ac_plugin_ui::control_state::{ControlState, keyed_name, keyed_path};
use ac_plugin_ui::flex::FlexNode;
use ac_plugin_ui::markup::{
    EXPANDER_HEADER_HEIGHT, Element as Markup, ListBody, MarkupDocument, Values,
};
use ac_plugin_ui::{ItemRecord, UiValue};

use super::{
    Look, MarkupButton, MarkupField, MarkupIcon, MarkupIcons, MarkupLabel, MarkupList, MarkupLog,
    MarkupToggle, PluginPanelController, add_flex_item, anchors, color, content_size, controls,
    flex_box, fold, keyboard_activated, layout, list_ext, transparent_panel,
};
use crate::font::UiFont;
use crate::geometry::AnchorEdges;
use crate::root::UiRoot;
use crate::tree::{Element, ElementId};
use crate::widgets::{Field, Menu, Meter, Scrollbar};

/// The set number of the window's own elements.
pub(super) const WINDOW_SET: u32 = 0;

/// A flex layout and the item a container is in it (`None`: its root).
pub(super) type FlexOf = (layout::SharedFlex, Option<usize>);

/// What every element of one set is built with: the window's font, where
/// its handlers go (with the row's prefix), the remembered values and the
/// row's key path they are kept under.
pub(super) struct Builder<'a> {
    pub doc: &'a Arc<MarkupDocument>,
    pub font: Option<Arc<UiFont>>,
    pub emit: controls::Emit,
    pub control_state: Option<fold::SharedControlState>,
    pub window_name: &'a str,
    /// The row's key path (`k1/k2`, see [`keyed_path`]); none outside a
    /// keyed row.
    pub state_path: Option<&'a str>,
    pub shifts: fold::Shifts,
}

impl Builder<'_> {
    /// The value `kind` a named control remembers: under `name#path` in a
    /// keyed row (OpenAC `KeyedControlState`).
    fn remembered(&self, control: Option<&String>, kind: &'static str) -> fold::Remembered {
        let name = control.map(|c| match self.state_path {
            Some(path) => keyed_name(c, path),
            None => c.clone(),
        });
        fold::Remembered::new(
            self.control_state.clone(),
            self.window_name,
            name.as_deref(),
            kind,
        )
    }
}

/// The elements one copy of markup built: the window's own, or one row's.
#[derive(Debug, Default)]
pub(super) struct ElementSet {
    /// The element of each markup element in the set (a repeat's marker
    /// for a `<repeat>`).
    pub nodes: HashMap<usize, ElementId>,
    /// The set's markup elements in document order.
    pub order: Vec<usize>,
    /// Each element's context menu element.
    pub menus: HashMap<usize, ElementId>,
    /// Each menu's entries as last shown.
    pub menu_items: HashMap<usize, Vec<String>>,
    /// The flex item of each element in a flex container.
    pub flex_items: HashMap<usize, usize>,
    /// Menus a flex container sizes: their popup follows their width.
    pub flex_menus: Vec<ElementId>,
    /// The repeats directly in the set.
    pub repeats: Vec<Repeat>,
    /// What the set's handlers are named after (a row's
    /// `List[index:key]/` steps; empty for the window), shared with its
    /// controls.
    pub prefix: Rc<RefCell<String>>,
    /// A keyed row's key path.
    pub state_path: Option<String>,
}

/// One `<repeat>`: its marker, where its rows go, and its rows in list
/// order.
#[derive(Debug)]
pub(super) struct Repeat {
    /// Its markup element.
    pub node: usize,
    marker: ElementId,
    container: ElementId,
    flex: Option<FlexOf>,
    rows: Vec<Row>,
    /// The list as last read.
    last: Option<Arc<Vec<ItemRecord>>>,
    logged_duplicates: bool,
    /// Its template's remembered names, read on the first forget.
    template_names: Option<BTreeSet<String>>,
}

impl Repeat {
    fn new(node: usize, marker: ElementId, container: ElementId, flex: Option<FlexOf>) -> Self {
        Self {
            node,
            marker,
            container,
            flex,
            rows: Vec::new(),
            last: None,
            logged_duplicates: false,
            template_names: None,
        }
    }

    /// Its rows' top elements, row by row (tests).
    pub(super) fn row_elements(&self) -> Vec<Vec<ElementId>> {
        self.rows
            .iter()
            .map(|r| r.top.iter().map(|(_, e)| *e).collect())
            .collect()
    }
}

/// One row: its set, the key it was built for, its key path, and the
/// elements it put in the container (markup element, element).
#[derive(Debug)]
struct Row {
    set: u32,
    key: Option<RowKey>,
    path: Option<String>,
    top: Vec<(usize, ElementId)>,
}

/// A key as rows are matched by it (OpenAC: `Equals` on the key).
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
enum RowKey {
    Bool(bool),
    Int(i32),
    Uint(u32),
    /// The bits, with -0 as 0 and every NaN as one (`float.Equals`).
    Float(u32),
    Text(String),
}

impl RowKey {
    /// The key of a value; `None` for a missing or null one.
    fn of(value: Option<&UiValue>) -> Option<Self> {
        Some(match value? {
            UiValue::Bool(v) | UiValue::OptionalBool(Some(v)) => Self::Bool(*v),
            UiValue::Int(v) => Self::Int(*v),
            UiValue::Uint(v) => Self::Uint(*v),
            UiValue::Float(v) => Self::Float(if v.is_nan() {
                f32::NAN.to_bits()
            } else if *v == 0.0 {
                0
            } else {
                v.to_bits()
            }),
            UiValue::Text(v) => Self::Text(v.clone()),
            _ => return None,
        })
    }
}

/// A repeat's place in its container: zero-size and click-through, with no
/// flex item.
fn add_marker(ui: &mut UiRoot, parent: ElementId) -> ElementId {
    let mut e = Element::at(0.0, 0.0, 0.0, 0.0).with_anchors(AnchorEdges::NONE);
    e.click_through = true;
    ui.add(parent, e, transparent_panel())
}

/// Builds the markup elements of `scope` (the window's own for `None`, a
/// repeat's template for `Some(repeat)`) in document order, each bound
/// through `values` and placed into its parent's element (`containers`,
/// seeded with the scope's container) and flex layout (`flex_of`). A
/// repeat inside gets its marker; its rows come later.
#[allow(clippy::too_many_lines)]
pub(super) fn build_set(
    ui: &mut UiRoot,
    b: &Builder<'_>,
    values: &dyn Values,
    scope: Option<usize>,
    mut containers: HashMap<Option<usize>, ElementId>,
    mut flex_of: HashMap<Option<usize>, FlexOf>,
) -> ElementSet {
    let doc = b.doc;
    let mut set = ElementSet::default();
    for (index, node) in doc
        .nodes
        .iter()
        .enumerate()
        .filter(|(_, n)| n.repeat == scope)
    {
        let parent = containers[&node.parent];
        if let Markup::Repeat { .. } = node.element {
            let marker = add_marker(ui, parent);
            let flex = flex_of.get(&node.parent).cloned();
            if let Some(f) = &flex {
                flex_of.insert(Some(index), f.clone());
            }
            containers.insert(Some(index), parent);
            set.nodes.insert(index, marker);
            set.order.push(index);
            set.repeats.push(Repeat::new(index, marker, parent, flex));
            continue;
        }
        let mut container = None;
        let mut e = Element::at(node.rect.x, node.rect.y, node.rect.w, node.rect.h)
            .with_anchors(anchors(node.common.anchors));
        e.name.clone_from(&node.common.name);
        let font = b.font.clone();
        let menu_font = font.clone();
        let id = match &node.element {
            Markup::Group {
                layout: group_layout,
                scroll_x,
                scroll_y,
                ..
            } => {
                let scrolls = *scroll_x || *scroll_y;
                // A scrolling group is hit-testable so the wheel reaches it
                // over empty space.
                e.click_through = !scrolls;
                let id = ui.add(parent, e, layout::MarkupGroup::new(transparent_panel()));
                let mut flex = None;
                if let Some(container) = group_layout {
                    let mut flex_node = FlexNode::default();
                    container.apply(&mut flex_node);
                    if scrolls {
                        flex_node.scroll_x = *scroll_x;
                        flex_node.scroll_y = *scroll_y;
                        flex_node.scrollbar_size = layout::BAR_SIZE;
                    }
                    let (shared, item) = match (&node.flex, flex_of.get(&node.parent)) {
                        // Nested in a flex container: an inner node of its
                        // root's tree.
                        (Some(attrs), Some((shared, parent_item))) => {
                            attrs.apply(&mut flex_node);
                            let item = shared.borrow_mut().add(
                                *parent_item,
                                id,
                                index,
                                flex_node,
                                true,
                                None,
                            );
                            set.flex_items.insert(index, item);
                            (Rc::clone(shared), Some(item))
                        }
                        _ => (
                            Rc::new(RefCell::new(flex_box::FlexBox::new(
                                Arc::clone(b.doc),
                                flex_node,
                            ))),
                            None,
                        ),
                    };
                    flex_of.insert(Some(index), (Rc::clone(&shared), item));
                    flex = Some(layout::FlexPart {
                        layout: shared,
                        item,
                    });
                }
                let scroll = scrolls.then(|| layout::ScrollArea::new(ui, id, *scroll_x, *scroll_y));
                if let Some(g) = ui.widget_mut::<layout::MarkupGroup>(id) {
                    g.flex = flex;
                    g.scroll = scroll;
                }
                id
            }
            Markup::Meter { sprites, .. } => {
                Meter::configure(&mut e);
                e.click_through = true;
                let mut meter = Meter::default();
                meter.font = font;
                [
                    meter.back_left,
                    meter.back_tile,
                    meter.back_right,
                    meter.front_left,
                    meter.front_tile,
                    meter.front_right,
                ] = *sprites;
                meter.fill = None;
                ui.add(parent, e, meter)
            }
            Markup::Label { .. } => {
                e.click_through = node.common.tooltip.is_none();
                ui.add(parent, e, MarkupLabel::new(font))
            }
            Markup::Button { .. } => ui.add(parent, keyboard_activated(e), MarkupButton::new(font)),
            Markup::Tab { .. } => ui.add(parent, keyboard_activated(e), MarkupButton::tab(font)),
            Markup::Toggle { .. } => ui.add(parent, keyboard_activated(e), MarkupToggle::new(font)),
            Markup::Icon { .. } => {
                e.click_through = node.common.tooltip.is_none();
                ui.add(parent, e, MarkupIcon::default())
            }
            Markup::Slider { retail_art, .. } => {
                Scrollbar::configure(&mut e);
                let mut bar = Scrollbar::default();
                bar.scalar = true;
                bar.horizontal = true;
                bar.retail_art = *retail_art;
                if *retail_art {
                    crate::panels::external_container::apply_horizontal_bar_art(&mut bar);
                }
                ui.add(parent, e, bar)
            }
            Markup::Field {
                one_line,
                editable,
                max_length,
                clear_on_submit,
                text,
                onup,
                ondown,
                onsubmit,
                onblur,
                ..
            } => {
                Field::configure(&mut e);
                e.tab_stop = true;
                let mut field = Field::default();
                field.font = font;
                field.one_line = *one_line;
                field.selectable = true;
                field.max_characters = usize::try_from(*max_length).unwrap_or(1).max(1);
                field.clear_on_submit = *clear_on_submit;
                field.record_history = false;
                field.reports_focus_loss = onblur.is_some();
                field.has_submit_handler = onsubmit.is_some();
                field.set_editable_detached(&mut e, *editable);
                field.set_text(&text.read(values).unwrap_or_default());
                ui.add(
                    parent,
                    e,
                    MarkupField {
                        inner: field,
                        emit: b.emit.clone(),
                        onup: onup.clone(),
                        ondown: ondown.clone(),
                    },
                )
            }
            Markup::Menu {
                rows,
                row_height,
                open_upward,
                retail_art,
                searchable,
                ..
            } => {
                Menu::configure(&mut e);
                let mut menu = Menu::default();
                menu.searchable = *searchable;
                menu.font = font.clone();
                menu.rows_per_column = usize::try_from(*rows).unwrap_or(1).max(1);
                menu.row_height = *row_height;
                menu.column_width = node.rect.w.max(20.0);
                menu.open_upward = *open_upward;
                menu.scroll_track_sprite = super::widgets::vertical_bar::TRACK;
                menu.scroll_thumb_sprite = super::widgets::vertical_bar::THUMB_MID;
                menu.scroll_thumb_top_sprite = super::widgets::vertical_bar::THUMB_TOP;
                menu.scroll_thumb_bottom_sprite = super::widgets::vertical_bar::THUMB_BOT;
                menu.scroll_up_sprite = super::widgets::vertical_bar::UP;
                menu.scroll_down_sprite = super::widgets::vertical_bar::DOWN;
                menu.text_indent = 6.0;
                menu.button_text_indent = 6.0;
                menu.normal_sprite = 0x0600_4D65;
                menu.pressed_sprite = 0x0600_4D66;
                menu.popup_bg_sprite = 0x0600_124C;
                menu.item_normal_sprite = 0x0600_124E;
                menu.item_highlight_sprite = 0x0600_124D;
                menu.retail_button_art = *retail_art;
                menu.scrollable = true;
                menu.popup_scrollbar_hide_when_disabled = true;
                menu.fits_long_labels = true;
                ui.add(parent, e, menu)
            }
            Markup::Log { .. } => ui.add(parent, e, MarkupLog::new(font)),
            Markup::List {
                row_height,
                selection_band,
                body,
                header,
                tree,
                multi,
                onchange,
                ..
            } => {
                let mut list = MarkupList::new(font, *row_height);
                list.selection_band = *selection_band;
                if let ListBody::Columns(columns) = body {
                    list.columns.clone_from(columns);
                    list.markup_columns.clone_from(columns);
                    let mut ext = list_ext::ListExt::new(
                        columns,
                        b.remembered(node.common.name.as_ref(), "sort"),
                        b.remembered(node.common.name.as_ref(), "widths"),
                        b.emit.clone(),
                    );
                    ext.header = header.show;
                    ext.onsort.clone_from(&header.onsort);
                    ext.resizable = header.resizable;
                    ext.onresize.clone_from(&header.onresize);
                    ext.multi = *multi;
                    if *multi {
                        ext.onchange.clone_from(onchange);
                    }
                    ext.tree = tree.as_ref().map(|t| {
                        list_ext::TreeState::new(
                            t.column,
                            t.start_collapsed,
                            t.onexpand.clone(),
                            b.remembered(node.common.name.as_ref(), "expanded"),
                        )
                    });
                    if *multi || tree.is_some() {
                        e = keyboard_activated(e);
                    }
                    list.ext = Some(Box::new(ext));
                }
                ui.add(parent, e, list)
            }
            Markup::Checkbox { onchange, .. } => ui.add(
                parent,
                keyboard_activated(e),
                controls::MarkupCheckbox::new(font, onchange.clone(), b.emit.clone()),
            ),
            Markup::Separator { .. } => {
                e.click_through = true;
                ui.add(parent, e, controls::MarkupSeparator::default())
            }
            Markup::Radio { onchange, .. } => ui.add(
                parent,
                keyboard_activated(e),
                controls::MarkupRadio::new(font, onchange.clone(), b.emit.clone()),
            ),
            Markup::Number {
                onchange,
                kind,
                spec: number_spec,
                ..
            } => {
                Field::configure(&mut e);
                e.tab_stop = true;
                let mut field = Field::default();
                field.font = font;
                field.one_line = true;
                field.selectable = true;
                field.select_all_on_focus = true;
                field.record_history = false;
                field.max_characters = 41;
                ui.add(
                    parent,
                    e,
                    controls::MarkupNumber::new(
                        field,
                        *number_spec,
                        *kind == ac_plugin_ui::markup::NumberKind::Int,
                        onchange.clone(),
                        b.emit.clone(),
                    ),
                )
            }
            Markup::Hotkey { onchange, .. } => {
                e.accepts_focus = true;
                e.focus_on_mouse_click = true;
                e.tab_stop = true;
                ui.add(
                    parent,
                    e,
                    controls::MarkupHotkey::new(font, onchange.clone(), b.emit.clone()),
                )
            }
            Markup::Color { .. } => {
                e.accepts_focus = true;
                e.focus_on_mouse_click = false;
                e.tab_stop = true;
                ui.add(
                    parent,
                    e,
                    color::MarkupColor::new(font, &node.element, b.emit.clone()),
                )
            }
            Markup::Expander {
                open,
                onchange,
                framed,
                ..
            } => {
                e.click_through = true;
                let h = node.rect.h;
                // In flex without a w: start at the content width, so the
                // content's anchors are captured against a real box.
                let w = if node.flex.is_some() && node.rect.w <= 0.0 {
                    content_size::LIST_WIDTH
                } else {
                    node.rect.w
                };
                e.width = w;
                let mut header_element =
                    keyboard_activated(Element::at(0.0, 0.0, w, EXPANDER_HEADER_HEIGHT))
                        .with_anchors(AnchorEdges::LEFT | AnchorEdges::RIGHT | AnchorEdges::TOP);
                header_element.runtime_tooltip_text =
                    node.common.tooltip.as_ref().and_then(|t| t.read(values));
                let header = ui.insert(header_element, fold::ExpanderHeader::new(font));
                let mut content_element = Element::at(
                    0.0,
                    EXPANDER_HEADER_HEIGHT,
                    w,
                    (h - EXPANDER_HEADER_HEIGHT).max(0.0),
                )
                .with_anchors(
                    AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::RIGHT | AnchorEdges::BOTTOM,
                );
                content_element.click_through = true;
                let content = ui.insert(content_element, transparent_panel());
                let mut expander = fold::MarkupExpander::new(
                    header,
                    content,
                    *open,
                    b.remembered(node.common.name.as_ref(), "expanded"),
                    Rc::clone(&b.shifts),
                    onchange.clone(),
                    b.emit.clone(),
                );
                expander.framed = *framed;
                let id = ui.add(parent, e, expander);
                ui.add_child(id, header);
                ui.add_child(id, content);
                container = Some(content);
                id
            }
            Markup::Repeat { .. } => unreachable!("a repeat gets its marker above"),
            Markup::Split {
                horizontal,
                min,
                max,
                default_position,
                at_given,
                onchange,
                ..
            } => {
                e.click_through = true;
                let mut bar_element = keyboard_activated(Element::at(0.0, 0.0, 0.0, 0.0))
                    .with_anchors(AnchorEdges::NONE)
                    .with_z_order(1);
                bar_element.captures_pointer_drag = true;
                bar_element.runtime_tooltip_text =
                    node.common.tooltip.as_ref().and_then(|t| t.read(values));
                let bar = ui.insert(bar_element, fold::SplitBar::new(*horizontal));
                let mut split = fold::MarkupSplit::new(
                    *horizontal,
                    bar,
                    b.remembered(node.common.name.as_ref(), "position"),
                    onchange.clone(),
                    b.emit.clone(),
                );
                split.min = *min;
                split.max = *max;
                split.default_position = *default_position;
                if node.flex.is_some() && (e.width <= 0.0 || e.height <= 0.0) {
                    // In flex without a w or h: start at the content size,
                    // so the panes' children capture their anchors against
                    // real panes.
                    if e.width <= 0.0 {
                        e.width = content_size::LIST_WIDTH;
                    }
                    if e.height <= 0.0 {
                        e.height = content_size::LIST_HEIGHT;
                    }
                    if !*at_given {
                        let room = if *horizontal { e.width } else { e.height }
                            - ac_plugin_ui::markup::SPLIT_BAR_SIZE;
                        split.default_position = (room.max(0.0) / 2.0).floor();
                    }
                }
                let id = ui.add(parent, e, split);
                ui.add_child(id, bar);
                id
            }
        };
        if let Some(attrs) = &node.flex {
            if let Some(item) = add_flex_item(ui, &flex_of, node, index, id, attrs) {
                set.flex_items.insert(index, item);
            }
            if matches!(node.element, Markup::Menu { .. }) {
                set.flex_menus.push(id);
            }
        }
        if let Some(t) = &node.common.tooltip
            && let Some(el) = ui.element_mut(id)
        {
            el.runtime_tooltip_text = t.read(values);
        }
        if let Some(menu) = &node.common.context_menu {
            let mut menu_element = Element::at(0.0, 0.0, 0.0, 0.0).with_anchors(AnchorEdges::NONE);
            menu_element.accepts_focus = true;
            menu_element.focus_on_mouse_click = true;
            menu_element.captures_pointer_drag = true;
            let menu = ui.add(
                id,
                menu_element,
                fold::MarkupContextMenu::new(
                    menu_font,
                    menu.onselect.clone(),
                    menu.onopen.clone(),
                    b.emit.clone(),
                ),
            );
            if let Some(el) = ui.element_mut(id) {
                el.context_menu = Some(menu);
            }
            set.menus.insert(index, menu);
        }
        set.nodes.insert(index, id);
        set.order.push(index);
        containers.insert(Some(index), container.unwrap_or(id));
    }
    // A split sizes its two panes: lay it out at its authored start now,
    // so the panes' children record their anchors against those sizes.
    for &index in &set.order {
        if let Markup::Split { .. } = doc.nodes[index].element {
            let id = set.nodes[&index];
            let panes: Vec<ElementId> = doc.nodes[index]
                .children
                .iter()
                .map(|c| set.nodes[c])
                .collect();
            for pane in &panes {
                ui.set_anchors(*pane, AnchorEdges::NONE);
            }
            ui.with::<fold::MarkupSplit, _>(id, |split, ui| {
                split.first = panes.first().copied();
                split.second = panes.get(1).copied();
                split.layout_at_default(ui, id);
            });
        }
    }
    set
}

impl PluginPanelController {
    /// Builds each `<repeat>`'s rows for `values` (the window's first rows,
    /// before its first measurement).
    pub(super) fn update_repeats(&mut self, ui: &mut UiRoot, values: &dyn Values) {
        self.sync_repeats(
            ui,
            WINDOW_SET,
            values,
            &mut super::NoMarkupIcons,
            &Look::default(),
        );
    }

    /// Brings each repeat directly in set `set` in step with its list as
    /// `values` has it, then shows each row's item and does the same for
    /// the repeats inside it (OpenAC `UiMarkupRepeat.OnTick` for each
    /// marker, and the copies' per-frame bindings).
    pub(super) fn sync_repeats(
        &mut self,
        ui: &mut UiRoot,
        set: u32,
        values: &dyn Values,
        icons: &mut dyn MarkupIcons,
        look: &Look,
    ) {
        let Some(s) = self.sets.get_mut(&set) else {
            return;
        };
        if s.repeats.is_empty() {
            return;
        }
        let mut repeats = std::mem::take(&mut s.repeats);
        let prefix = s.prefix.borrow().clone();
        let path = s.state_path.clone();
        let doc = Arc::clone(&self.document);
        for repeat in &mut repeats {
            let Markup::Repeat { items, key } = &doc.nodes[repeat.node].element else {
                continue;
            };
            let list = match values.value(items) {
                Some(UiValue::Items(list)) => Arc::clone(list),
                _ => Arc::default(),
            };
            self.reconcile(ui, repeat, &list, items, key.as_deref(), path.as_deref());
            for (i, record) in list.iter().enumerate() {
                let Some(row_set) = repeat.rows.get(i).map(|r| r.set) else {
                    break;
                };
                let key_text = key
                    .as_ref()
                    .and_then(|k| record.values.get(k))
                    .map(UiValue::display);
                let named = format!(
                    "{prefix}{}",
                    ac_plugin_ui::row::step_text(items, i, key_text.as_deref())
                );
                if let Some(s) = self.sets.get(&row_set)
                    && *s.prefix.borrow() != named
                {
                    *s.prefix.borrow_mut() = named;
                }
                self.sync_set(ui, row_set, &record.values, None, icons, look);
                self.sync_repeats(ui, row_set, &record.values, icons, look);
            }
        }
        if let Some(s) = self.sets.get_mut(&set) {
            s.repeats = repeats;
        }
    }

    /// Matches `repeat`'s rows to `list` when the list changed (OpenAC
    /// `UiMarkupRepeat.OnTick`): by key when every key is set and unique,
    /// else by position, with one notice until the keys are good again.
    /// Rows that moved, came or went are put in order after the marker,
    /// and the remembered state of keys that left the list is forgotten.
    fn reconcile(
        &mut self,
        ui: &mut UiRoot,
        repeat: &mut Repeat,
        list: &Arc<Vec<ItemRecord>>,
        items: &str,
        key: Option<&str>,
        parent_path: Option<&str>,
    ) {
        let unchanged = repeat
            .last
            .as_ref()
            .is_some_and(|last| Arc::ptr_eq(last, list) || (last.is_empty() && list.is_empty()));
        if unchanged {
            return;
        }
        repeat.last = Some(Arc::clone(list));
        let keys: Option<Vec<Option<RowKey>>> =
            key.map(|k| list.iter().map(|r| RowKey::of(r.values.get(k))).collect());
        let unique = keys.as_ref().is_some_and(|keys| {
            let mut seen = HashSet::with_capacity(keys.len());
            keys.iter()
                .all(|k| k.as_ref().is_some_and(|k| seen.insert(k)))
        });
        let mut forget = BTreeSet::new();
        let moved = match keys.as_deref() {
            Some(keys) if unique => {
                repeat.logged_duplicates = false;
                self.match_by_key(ui, repeat, list, keys, key, parent_path, &mut forget)
            }
            keys => {
                if keys.is_some() && !repeat.logged_duplicates {
                    self.notices.push(format!(
                        "markup <repeat items=\"{{{items}}}\">: duplicate or missing keys; rows are matched by position"
                    ));
                    repeat.logged_duplicates = true;
                }
                self.match_by_index(ui, repeat, list, keys, key, parent_path, &mut forget)
            }
        };
        if moved {
            self.place_rows(ui, repeat);
        }
        if !forget.is_empty() {
            self.forget(repeat, &forget);
        }
    }

    /// Keeps each row whose key is still listed, moved to the key's place;
    /// builds rows for new keys and releases the rest. Whether any row
    /// came, went or moved.
    #[allow(clippy::too_many_arguments)]
    fn match_by_key(
        &mut self,
        ui: &mut UiRoot,
        repeat: &mut Repeat,
        list: &Arc<Vec<ItemRecord>>,
        keys: &[Option<RowKey>],
        key: Option<&str>,
        parent_path: Option<&str>,
        forget: &mut BTreeSet<String>,
    ) -> bool {
        let mut by_key: HashMap<RowKey, (usize, Row)> = HashMap::with_capacity(repeat.rows.len());
        let mut released = Vec::new();
        for (i, row) in std::mem::take(&mut repeat.rows).into_iter().enumerate() {
            match &row.key {
                Some(k) if !by_key.contains_key(k) => {
                    by_key.insert(k.clone(), (i, row));
                }
                _ => released.push(row),
            }
        }
        let mut moved = false;
        let slots: Vec<Option<Row>> = keys
            .iter()
            .enumerate()
            .map(|(i, k)| match k.as_ref().and_then(|k| by_key.remove(k)) {
                Some((was, row)) => {
                    moved |= was != i;
                    Some(row)
                }
                None => {
                    moved = true;
                    None
                }
            })
            .collect();
        released.extend(by_key.into_values().map(|(_, row)| row));
        moved |= !released.is_empty();
        for row in released {
            self.release(ui, row, repeat.flex.as_ref(), Some(keys), forget);
        }
        for (i, slot) in slots.into_iter().enumerate() {
            let row = match slot {
                Some(row) => row,
                None => self.build_row(ui, repeat, list, i, key, parent_path),
            };
            repeat.rows.push(row);
        }
        moved
    }

    /// The first rows follow the first items whatever their keys; rows past
    /// the list's end are released and missing ones built at the end.
    #[allow(clippy::too_many_arguments)]
    fn match_by_index(
        &mut self,
        ui: &mut UiRoot,
        repeat: &mut Repeat,
        list: &Arc<Vec<ItemRecord>>,
        keys: Option<&[Option<RowKey>]>,
        key: Option<&str>,
        parent_path: Option<&str>,
        forget: &mut BTreeSet<String>,
    ) -> bool {
        let count = list.len();
        let mut moved = false;
        if repeat.rows.len() > count {
            moved = true;
            for row in repeat.rows.split_off(count) {
                self.release(ui, row, repeat.flex.as_ref(), keys, forget);
            }
        }
        for i in repeat.rows.len()..count {
            moved = true;
            let row = self.build_row(ui, repeat, list, i, key, parent_path);
            repeat.rows.push(row);
        }
        moved
    }

    /// Builds the row of `list[index]` into the repeat's container, after
    /// everything there (`place_rows` puts it in order).
    fn build_row(
        &mut self,
        ui: &mut UiRoot,
        repeat: &Repeat,
        list: &Arc<Vec<ItemRecord>>,
        index: usize,
        key: Option<&str>,
        parent_path: Option<&str>,
    ) -> Row {
        let doc = Arc::clone(&self.document);
        let record = &list[index];
        let key_value = key.and_then(|k| record.values.get(k));
        // OpenAC `Convert.ToString(key, InvariantCulture) ?? ""`.
        let path = key.map(|_| {
            keyed_path(
                parent_path,
                &key_value.map(UiValue::display).unwrap_or_default(),
            )
        });
        let prefix = Rc::new(RefCell::new(String::new()));
        let builder = Builder {
            doc: &doc,
            font: self.classic_font.clone(),
            emit: controls::Emit::row(self.panel, Rc::clone(&self.outbox), Rc::clone(&prefix)),
            control_state: self.control_state.clone(),
            window_name: &self.window_name,
            state_path: path.as_deref(),
            shifts: Rc::clone(&self.shifts),
        };
        let mut containers = HashMap::new();
        containers.insert(Some(repeat.node), repeat.container);
        let mut flex_of = HashMap::new();
        if let Some(flex) = &repeat.flex {
            flex_of.insert(Some(repeat.node), flex.clone());
        }
        let mut set = build_set(
            ui,
            &builder,
            &record.values,
            Some(repeat.node),
            containers,
            flex_of,
        );
        set.prefix = prefix;
        set.state_path.clone_from(&path);
        let top: Vec<(usize, ElementId)> = doc.nodes[repeat.node]
            .children
            .iter()
            .filter_map(|c| set.nodes.get(c).map(|e| (*c, *e)))
            .collect();
        for &(_, element) in &top {
            ui.capture_authored_anchor_baselines(element);
        }
        let id = self.next_set;
        self.next_set += 1;
        for (&node, &element) in &set.nodes {
            self.by_element.insert(element, (id, node));
        }
        self.sets.insert(id, set);
        Row {
            set: id,
            key: RowKey::of(key_value),
            path,
            top,
        }
    }

    /// Takes a row out; a keyed row whose key is not listed (`listed`) any
    /// more has its state forgotten after the matching.
    fn release(
        &mut self,
        ui: &mut UiRoot,
        row: Row,
        flex: Option<&FlexOf>,
        listed: Option<&[Option<RowKey>]>,
        forget: &mut BTreeSet<String>,
    ) {
        if let Some(path) = &row.path
            && !listed.is_some_and(|keys| keys.iter().any(|k| k.as_ref() == row.key.as_ref()))
        {
            forget.insert(path.clone());
        }
        self.release_row(ui, row, flex);
    }

    /// Takes a row's elements out of the container and the flex layout and
    /// frees them (the root drops focus, capture and popups inside them),
    /// with the rows of every repeat inside it.
    fn release_row(&mut self, ui: &mut UiRoot, row: Row, flex: Option<&FlexOf>) {
        let Some(mut set) = self.sets.remove(&row.set) else {
            return;
        };
        for repeat in std::mem::take(&mut set.repeats) {
            for inner in repeat.rows {
                self.release_row(ui, inner, repeat.flex.as_ref());
            }
        }
        if let Some((shared, _)) = flex {
            let mut layout = shared.borrow_mut();
            for (node, _) in &row.top {
                if let Some(&item) = set.flex_items.get(node) {
                    layout.remove(item);
                }
            }
        }
        for &(_, element) in &row.top {
            ui.destroy(element);
        }
        for element in set.nodes.values() {
            self.by_element.remove(element);
        }
    }

    /// Puts the repeat's rows (with the rows of repeats directly in them)
    /// straight after its marker in list order, in the container and in
    /// the flex layout.
    fn place_rows(&self, ui: &mut UiRoot, repeat: &Repeat) {
        let mut span = Vec::new();
        self.span(repeat, &mut span);
        ui.place_after(repeat.container, repeat.marker, &span);
        if let Some((shared, parent)) = &repeat.flex {
            let rank: HashMap<ElementId, usize> = ui
                .children(repeat.container)
                .into_iter()
                .enumerate()
                .map(|(i, e)| (e, i))
                .collect();
            shared
                .borrow_mut()
                .order_by(*parent, |e| rank.get(&e).copied().unwrap_or(usize::MAX));
        }
    }

    /// Every element a repeat's rows have in its container, in order: each
    /// row's own, and after a repeat directly in it, that repeat's
    /// (OpenAC `UiMarkupRepeat.Span`).
    fn span(&self, repeat: &Repeat, out: &mut Vec<ElementId>) {
        for row in &repeat.rows {
            for &(node, element) in &row.top {
                out.push(element);
                if matches!(self.document.nodes[node].element, Markup::Repeat { .. })
                    && let Some(inner) = self
                        .sets
                        .get(&row.set)
                        .and_then(|s| s.repeats.iter().find(|r| r.node == node))
                {
                    self.span(inner, out);
                }
            }
        }
    }

    /// Forgets what the template's names remembered under `paths` (OpenAC
    /// `IForgetsKeyedState.ForgetKeyed`), in one call.
    fn forget(&self, repeat: &mut Repeat, paths: &BTreeSet<String>) {
        let Some(store) = &self.control_state else {
            return;
        };
        let names = repeat
            .template_names
            .get_or_insert_with(|| self.document.template_names(repeat.node));
        store
            .borrow_mut()
            .window(&self.window_name)
            .forget_keyed(names, paths);
    }

    /// The rows of the first `<repeat>` built for markup element `node`
    /// (the window's own, else the first row's that has one): each row's
    /// top elements.
    #[must_use]
    pub fn repeat_rows(&self, node: usize) -> Vec<Vec<ElementId>> {
        let mut ids: Vec<&u32> = self.sets.keys().collect();
        ids.sort();
        ids.into_iter()
            .filter_map(|id| self.sets[id].repeats.iter().find(|r| r.node == node))
            .map(Repeat::row_elements)
            .next()
            .unwrap_or_default()
    }

    /// What the window noticed since the last call (a repeat's duplicate
    /// keys), one line each.
    pub fn take_notices(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notices)
    }
}
