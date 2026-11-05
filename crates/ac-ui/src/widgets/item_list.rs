//! The item list: a grid of item cells (toolbar shortcuts, pack contents,
//! equipment slots, spell favourites). It lays its cells out in rows or
//! columns, scrolls them a pixel at a time, can keep a tail of empty cells
//! filling a single row, and passes drops of non-item payloads to its
//! owner.

use crate::draw::RenderContext;
use crate::event::{UiEvent, kind};
use crate::geometry::AnchorEdges;
use crate::root::{ElementMessage, UiRoot};
use crate::text::Scrollable;
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::item_slot::{ItemCell, slot_element};

pub use crate::widgets::item_slot::DEFAULT_EMPTY_SPRITE;

/// How cells fill the grid.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum ItemListFlow {
    /// Across, then down.
    #[default]
    RowMajor,
    /// Down, then across.
    ColumnMajor,
}

/// Makes a fresh empty cell (detached) for the empty tail.
pub type CellFactory = Box<dyn FnMut(&mut UiRoot) -> ElementId>;

/// An item list.
pub struct ItemList {
    cells: Vec<ElementId>,
    layout_deferral_depth: u32,
    cell_empty_sprite: u32,
    cooldown_sprites: Vec<u32>,
    /// The scroll model (pixels; along x for a horizontally scrolling row).
    pub scroll: Scrollable,
    /// Columns of the grid.
    pub columns: usize,
    /// Fill order.
    pub flow: ItemListFlow,
    /// Cell width, pixels (0: one cell filling the list).
    pub cell_width: f32,
    /// Cell height, pixels.
    pub cell_height: f32,
    /// All cells in one row.
    pub single_row: bool,
    /// A single row scrolls sideways.
    pub horizontal_scroll: bool,
    /// Keep empty cells filling the visible part of a single row.
    pub fill_visible_empty_slots: bool,
    /// When set, the list holds exactly this many cells, whatever its
    /// width, in place of [`ItemList::fill_visible_empty_slots`]: it pads
    /// with empty cells, and drops the last cells past the count even when
    /// they hold an item, so a container never shows more items than it
    /// can hold.
    pub fixed_slot_count: Option<usize>,
    /// Makes those empty cells (default: [`ItemCell`]).
    pub empty_slot_factory: Option<CellFactory>,
    /// Drops of non-item payloads are reported as
    /// [`ElementMessage::CatalogDropped`].
    pub accepts_catalog_drops: bool,
}

impl std::fmt::Debug for ItemList {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ItemList")
            .field("cells", &self.cells.len())
            .field("columns", &self.columns)
            .finish_non_exhaustive()
    }
}

impl Default for ItemList {
    fn default() -> Self {
        Self {
            cells: Vec::new(),
            layout_deferral_depth: 0,
            cell_empty_sprite: 0,
            cooldown_sprites: Vec::new(),
            scroll: Scrollable::default(),
            columns: 1,
            flow: ItemListFlow::RowMajor,
            cell_width: 0.0,
            cell_height: 0.0,
            single_row: false,
            horizontal_scroll: false,
            fill_visible_empty_slots: false,
            fixed_slot_count: None,
            empty_slot_factory: None,
            accepts_catalog_drops: false,
        }
    }
}

/// Rows needed for `cell_count` cells in `columns` columns.
#[must_use]
pub fn row_count(cell_count: usize, columns: usize) -> usize {
    cell_count.div_ceil(columns.max(1))
}

/// The top-left of cell `index` in a row-major grid.
#[must_use]
pub fn cell_offset(index: usize, columns: usize, cell_w: f32, cell_h: f32) -> (f32, f32) {
    let columns = columns.max(1);
    let (col, row) = (index % columns, index / columns);
    (col as f32 * cell_w, row as f32 * cell_h)
}

/// The top-left of cell `index` in either flow.
#[must_use]
pub fn cell_offset_flow(
    index: usize,
    columns: usize,
    cell_count: usize,
    flow: ItemListFlow,
    cell_w: f32,
    cell_h: f32,
) -> (f32, f32) {
    let cols = columns.max(1);
    match flow {
        ItemListFlow::ColumnMajor => {
            let rows = row_count(cell_count, cols).max(1);
            let (col, row) = (index / rows, index % rows);
            (col as f32 * cell_w, row as f32 * cell_h)
        }
        ItemListFlow::RowMajor => cell_offset(index, cols, cell_w, cell_h),
    }
}

impl ItemList {
    /// A list in `ui` under `parent`, starting with one empty cell (a
    /// single-cell slot until it is given more).
    pub fn create(ui: &mut UiRoot, parent: ElementId, element: Element) -> ElementId {
        let id = ui.add(parent, element, ItemList::default());
        ui.with::<ItemList, _>(id, |l, ui| l.ensure_first_cell(ui, id));
        id
    }

    fn ensure_first_cell(&mut self, ui: &mut UiRoot, me: ElementId) {
        if self.cells.is_empty() {
            let cell = ui.insert(slot_element(), ItemCell::default());
            self.add_item(ui, me, cell);
        }
    }

    /// The cooldown sweep images every cell shows (existing and future
    /// cells).
    pub fn set_cooldown_sprites(&mut self, ui: &mut UiRoot, sprites: &[u32]) {
        self.cooldown_sprites = sprites.to_vec();
        for c in &self.cells {
            if let Some(cell) = ui.widget_mut::<ItemCell>(*c) {
                cell.cooldown_sprites.clone_from(&self.cooldown_sprites);
            }
        }
    }

    /// The cooldown sweep images given to cells.
    #[must_use]
    pub fn cooldown_sprites(&self) -> &[u32] {
        &self.cooldown_sprites
    }

    /// The image cells show while empty (0: each cell's own).
    #[must_use]
    pub fn cell_empty_sprite(&self) -> u32 {
        self.cell_empty_sprite
    }

    /// Every cell, in order.
    #[must_use]
    pub fn cells(&self) -> &[ElementId] {
        &self.cells
    }

    /// The image every cell shows while empty (0 leaves each cell's own).
    pub fn set_cell_empty_sprite(&mut self, ui: &mut UiRoot, sprite: u32) {
        self.cell_empty_sprite = sprite;
        if sprite != 0 {
            for c in &self.cells {
                if let Some(cell) = ui.widget_mut::<ItemCell>(*c) {
                    cell.empty_sprite = sprite;
                }
            }
        }
    }

    /// Number of cells.
    #[must_use]
    pub fn num_items(&self) -> usize {
        self.cells.len()
    }

    /// Cell `index`.
    #[must_use]
    pub fn item(&self, index: usize) -> Option<ElementId> {
        self.cells.get(index).copied()
    }

    /// The position of `cell`.
    #[must_use]
    pub fn index_of(&self, cell: ElementId) -> Option<usize> {
        self.cells.iter().position(|c| *c == cell)
    }

    /// Append a (detached) cell.
    pub fn add_item(&mut self, ui: &mut UiRoot, me: ElementId, cell: ElementId) {
        self.adopt(ui, me, cell);
        if self.layout_deferral_depth == 0 {
            self.layout_cells(ui, me);
        }
    }

    fn adopt(&mut self, ui: &mut UiRoot, me: ElementId, cell: ElementId) {
        if let Some(c) = ui.widget_mut::<ItemCell>(cell) {
            if self.cell_empty_sprite != 0 {
                c.empty_sprite = self.cell_empty_sprite;
            }
            if !self.cooldown_sprites.is_empty() {
                c.cooldown_sprites.clone_from(&self.cooldown_sprites);
            }
        }
        ui.add_child(me, cell);
        ui.set_anchors(cell, AnchorEdges::NONE);
        self.cells.push(cell);
    }

    /// Hold layout until the matching [`ItemList::end_defer_layout`] (a
    /// rebuild lays out once, keeping the scroll offset).
    pub fn defer_layout(&mut self) {
        self.layout_deferral_depth += 1;
    }

    /// End one [`ItemList::defer_layout`]; the last lays out.
    pub fn end_defer_layout(&mut self, ui: &mut UiRoot, me: ElementId) {
        if self.layout_deferral_depth == 0 {
            return;
        }
        self.layout_deferral_depth -= 1;
        if self.layout_deferral_depth == 0 {
            self.layout_cells(ui, me);
        }
    }

    /// Remove and free every cell.
    pub fn flush(&mut self, ui: &mut UiRoot) {
        for c in self.cells.drain(..) {
            ui.destroy(c);
        }
    }

    fn update_empty_slots(&mut self, ui: &mut UiRoot, me: ElementId, width: f32) {
        if self.fixed_slot_count.is_none()
            && (!self.fill_visible_empty_slots || !self.single_row || self.cell_width <= 0.0)
        {
            return;
        }
        let visible = self
            .fixed_slot_count
            .unwrap_or_else(|| (width / self.cell_width).floor().max(0.0) as usize);
        while self.cells.len() < visible {
            let cell = match self.empty_slot_factory.as_mut() {
                Some(f) => f(ui),
                None => ui.insert(slot_element(), ItemCell::default()),
            };
            self.adopt(ui, me, cell);
        }
        while self.cells.len() > visible {
            let last = *self.cells.last().expect("non-empty");
            if self.fixed_slot_count.is_none()
                && !ui.widget_dyn(last).is_some_and(|w| w.is_empty_slot())
            {
                break;
            }
            self.cells.pop();
            ui.destroy(last);
        }
    }

    /// Place every cell for the current size and scroll offset.
    pub fn layout_cells(&mut self, ui: &mut UiRoot, me: ElementId) {
        let Some((width, height)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        // A fixed cell count holds whatever the cell size; the width only
        // decides how many cells fill the strip without one.
        self.update_empty_slots(ui, me, width);
        if self.cell_width <= 0.0 {
            if let Some(&c) = self.cells.first() {
                if let Some(e) = ui.element_mut(c) {
                    (e.left, e.top, e.width, e.height) = (0.0, 0.0, width, height);
                }
                ui.set_visible(c, true);
            }
            return;
        }
        let cols = if self.single_row {
            self.cells.len().max(1)
        } else {
            self.columns.max(1)
        };
        let cell_h = self.cell_height.round_ties_even() as i32;

        if self.single_row && self.horizontal_scroll {
            let cell_w = (self.cell_width.round_ties_even() as i32).max(1);
            self.scroll.line_height = cell_w;
            self.scroll.content_height = self.cells.len() as i32 * cell_w;
            self.scroll.view_height = width.floor() as i32;
            let y = self.scroll.scroll_y();
            self.scroll.set_scroll_y(y);
            let scroll_x = self.scroll.scroll_y() as f32;
            for (i, &c) in self.cells.iter().enumerate() {
                let left = i as f32 * self.cell_width - scroll_x;
                if let Some(e) = ui.element_mut(c) {
                    (e.left, e.top, e.width, e.height) =
                        (left, 0.0, self.cell_width, self.cell_height);
                }
                ui.set_visible(c, left < width && left + self.cell_width > 0.0);
            }
            return;
        }

        self.scroll.line_height = if cell_h > 0 { cell_h } else { 1 };
        self.scroll.content_height = row_count(self.cells.len(), cols) as i32 * cell_h;
        self.scroll.view_height = height.floor() as i32;
        let y = self.scroll.scroll_y();
        self.scroll.set_scroll_y(y);
        let scroll_y = self.scroll.scroll_y() as f32;
        let count = self.cells.len();
        for (i, &c) in self.cells.iter().enumerate() {
            let (x, base_y) =
                cell_offset_flow(i, cols, count, self.flow, self.cell_width, self.cell_height);
            let top = base_y - scroll_y;
            if let Some(e) = ui.element_mut(c) {
                (e.left, e.top, e.width, e.height) = (x, top, self.cell_width, self.cell_height);
            }
            ui.set_visible(c, top < height && top + self.cell_height > 0.0);
        }
    }

    /// Scroll just enough to show cell `index`.
    pub fn scroll_item_into_view(&mut self, ui: &UiRoot, me: ElementId, index: usize) {
        if self.cell_width <= 0.0 || index >= self.cells.len() {
            return;
        }
        let Some((width, height)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        let (start, end, view) = if self.single_row && self.horizontal_scroll {
            let left = index as f32 * self.cell_width;
            (left, left + self.cell_width, width)
        } else {
            let columns = self.columns.max(1);
            let row = match self.flow {
                ItemListFlow::ColumnMajor => index % row_count(self.cells.len(), columns).max(1),
                ItemListFlow::RowMajor => index / columns,
            };
            let top = row as f32 * self.cell_height;
            (top, top + self.cell_height, height)
        };
        let scroll = self.scroll.scroll_y() as f32;
        if start < scroll {
            self.scroll.set_scroll_y(start.floor() as i32);
        } else if end > scroll + view {
            self.scroll.set_scroll_y((end - view).ceil() as i32);
        }
    }
}

impl Widget for ItemList {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if self.accepts_catalog_drops
            && let Some(payload) = &e.payload
        {
            if e.kind == kind::DRAG_ENTER || e.kind == kind::DRAG_OVER {
                return true;
            }
            if e.kind == kind::DROP_RELEASED {
                ui.send_message(
                    me,
                    ElementMessage::CatalogDropped {
                        payload: payload.clone(),
                        x: e.data1,
                        y: e.data2,
                    },
                );
                return true;
            }
        }
        if e.kind == kind::SCROLL && self.cell_width > 0.0 {
            // Wheel up shows earlier rows.
            self.scroll.scroll_by_lines(-e.data0);
            return true;
        }
        ui.dispatch_to_pointer_region(me, e)
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, _rc: &mut RenderContext<'_>) {
        self.layout_cells(ui, me);
    }

    fn consumes_dat_children(&self) -> bool {
        true
    }

    fn on_children_attached(&mut self, ui: &mut UiRoot, me: ElementId) {
        self.ensure_first_cell(ui, me);
    }

    fn scroll_model(&mut self) -> Option<&mut Scrollable> {
        Some(&mut self.scroll)
    }
}
