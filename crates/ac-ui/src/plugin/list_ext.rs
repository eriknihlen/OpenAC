//! A column list's header, sort, resizable widths, select-all boxes,
//! multi-select and tree (OpenAC `UiMarkupList.Header`, `.Sort`,
//! `.Widths`, `.Selection`, `.Tree`). The list's rows stay in the plugin's
//! order; this decides the order they show in ([`ViewMap`]) and turns the
//! player's header, arrow and row presses into the plugin's handlers.

use std::collections::BTreeSet;

use ac_plugin_ui::ActionValue;
use ac_plugin_ui::list_view::{SortKeys, TreeShape, ViewMap, selection};
use ac_plugin_ui::markup::{Column, ColumnHeader, ColumnKind, SortKind};
use ac_plugin_ui::value::parse_float;

use super::controls::Emit;
use super::fold::Remembered;
use super::widgets::Cell;

/// The header row's height.
pub const HEADER_HEIGHT: f32 = 18.0;
/// Indent per tree level, and the width of a row's arrow slot.
pub const TREE_INDENT: f32 = 12.0;
/// The narrowest a player can drag a column.
pub const MIN_COLUMN_WIDTH: f32 = 16.0;
/// How far either side of a header boundary a press grabs it.
pub const RESIZE_GRIP: f32 = 3.0;

/// A tree list's sources and the client's own expansion.
#[derive(Debug, Clone)]
pub struct TreeState {
    /// The column the tree draws in.
    pub column: usize,
    /// Each plugin row's depth.
    pub levels: Vec<i32>,
    /// Each plugin row's key.
    pub keys: Vec<String>,
    /// The bound expanded keys, when the plugin owns the expansion.
    pub bound: Option<BTreeSet<String>>,
    /// Rows start folded.
    pub start_collapsed: bool,
    /// The `onexpand` handler.
    pub onexpand: Option<String>,
    toggled: Option<BTreeSet<String>>,
    remembered: Remembered,
}

impl TreeState {
    /// A tree in `column` remembering through `remembered`.
    #[must_use]
    pub fn new(
        column: usize,
        start_collapsed: bool,
        onexpand: Option<String>,
        remembered: Remembered,
    ) -> Self {
        Self {
            column,
            levels: Vec::new(),
            keys: Vec::new(),
            bound: None,
            start_collapsed,
            onexpand,
            toggled: None,
            remembered,
        }
    }

    fn key(&self, row: usize) -> String {
        self.keys
            .get(row)
            .cloned()
            .unwrap_or_else(|| format!("#{row}"))
    }

    fn toggled(&mut self) -> &mut BTreeSet<String> {
        if let Some(text) = self.remembered.refresh() {
            self.toggled = Some(decode_keys(text.as_deref()));
        }
        self.toggled.get_or_insert_with(BTreeSet::new)
    }
}

/// A JSON array of strings, as the expansion is remembered.
#[must_use]
pub fn encode_keys(keys: &BTreeSet<String>) -> String {
    let mut out = String::from("[");
    for (i, key) in keys.iter().enumerate() {
        if i > 0 {
            out.push(',');
        }
        out.push('"');
        for c in key.chars() {
            match c {
                '"' => out.push_str("\\\""),
                '\\' => out.push_str("\\\\"),
                c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
                c => out.push(c),
            }
        }
        out.push('"');
    }
    out.push(']');
    out
}

/// The remembered expansion read back; anything unreadable is none.
#[must_use]
pub fn decode_keys(text: Option<&str>) -> BTreeSet<String> {
    let mut keys = BTreeSet::new();
    let Some(text) = text.map(str::trim) else {
        return keys;
    };
    let Some(inner) = text.strip_prefix('[').and_then(|t| t.strip_suffix(']')) else {
        return keys;
    };
    let mut chars = inner.chars().peekable();
    let mut found = Vec::new();
    loop {
        while chars.peek().is_some_and(|c| c.is_whitespace() || *c == ',') {
            chars.next();
        }
        match chars.next() {
            None => break,
            Some('"') => {
                let mut s = String::new();
                loop {
                    match chars.next() {
                        None => return BTreeSet::new(),
                        Some('"') => break,
                        Some('\\') => match chars.next() {
                            Some('n') => s.push('\n'),
                            Some('t') => s.push('\t'),
                            Some('u') => {
                                let hex: String = chars.by_ref().take(4).collect();
                                match u32::from_str_radix(&hex, 16).ok().and_then(char::from_u32) {
                                    Some(c) => s.push(c),
                                    None => return BTreeSet::new(),
                                }
                            }
                            Some(c) => s.push(c),
                            None => return BTreeSet::new(),
                        },
                        Some(c) => s.push(c),
                    }
                }
                found.push(s);
            }
            Some('n') => {
                // `null` entries are skipped.
                let rest: String = chars.by_ref().take(3).collect();
                if rest != "ull" {
                    return BTreeSet::new();
                }
            }
            Some(_) => return BTreeSet::new(),
        }
    }
    keys.extend(found);
    keys
}

#[derive(Debug, Clone)]
struct Resize {
    column: usize,
    press_x: f32,
    moved: bool,
    start_width: f32,
    start: Vec<f32>,
    asked: Vec<f32>,
    right_reserve: f32,
}

/// A column list's header, order, widths, selection and tree. See the
/// module documentation.
#[derive(Debug, Clone)]
pub struct ListExt {
    /// The header row is shown.
    pub header: bool,
    /// The bound sort, when the plugin owns it.
    pub bound_sort: Option<(i32, bool)>,
    /// The `onsort` handler.
    pub onsort: Option<String>,
    /// Header boundaries can be dragged.
    pub resizable: bool,
    /// The bound widths, when the plugin owns them.
    pub bound_widths: Option<Vec<f32>>,
    /// The `onresize` handler.
    pub onresize: Option<String>,
    /// Each column's header caption (none: select-all or nothing).
    pub captions: Vec<Option<String>>,
    /// Each column's numeric sort keys (`sortkeys` floats).
    pub number_keys: Vec<Option<Vec<f32>>>,
    /// Each column's text sort keys (`sortkeys` strings).
    pub text_keys: Vec<Option<Vec<String>>>,
    /// `selectmode="multi"`.
    pub multi: bool,
    /// The selected plugin rows in multi mode.
    pub selected_rows: Vec<i32>,
    /// The multi-select `onchange`.
    pub onchange: Option<String>,
    /// The tree, when it is one.
    pub tree: Option<TreeState>,
    /// The order the rows show in.
    pub view: ViewMap,
    host_sort: Option<(i32, bool)>,
    remembered_sort: Remembered,
    markup_widths: Vec<f32>,
    host_widths: Option<Vec<f32>>,
    remembered_widths: Remembered,
    resize: Option<Resize>,
    header_press: i32,
    anchor: i32,
    lead: i32,
    shape: TreeShape,
    emit: Emit,
}

impl ListExt {
    /// The extension of a list with `columns`.
    #[must_use]
    pub fn new(
        columns: &[Column],
        remembered_sort: Remembered,
        remembered_widths: Remembered,
        emit: Emit,
    ) -> Self {
        let last = columns.len().saturating_sub(1);
        Self {
            header: false,
            bound_sort: None,
            onsort: None,
            resizable: false,
            bound_widths: None,
            onresize: None,
            captions: vec![None; columns.len()],
            number_keys: vec![None; columns.len()],
            text_keys: vec![None; columns.len()],
            multi: false,
            selected_rows: Vec::new(),
            onchange: None,
            tree: None,
            view: ViewMap::default(),
            host_sort: None,
            remembered_sort,
            markup_widths: columns
                .iter()
                .enumerate()
                .map(|(i, c)| if c.auto || i == last { 0.0 } else { c.width })
                .collect(),
            host_widths: None,
            remembered_widths,
            resize: None,
            header_press: -1,
            anchor: -1,
            lead: -1,
            shape: TreeShape::default(),
            emit,
        }
    }

    /// Where the rows start.
    #[must_use]
    pub fn rows_top(&self) -> f32 {
        if self.header { HEADER_HEIGHT } else { 0.0 }
    }

    fn sortable(columns: &[Column], column: i32) -> bool {
        usize::try_from(column)
            .ok()
            .and_then(|c| columns.get(c))
            .is_some_and(|c| c.sort != SortKind::None)
    }

    /// The sort in force (bound, remembered or none); an unsortable
    /// column is none.
    pub fn current_sort(&mut self, columns: &[Column]) -> (i32, bool) {
        let (column, descending) = match self.bound_sort {
            Some(s) => s,
            None => {
                if let Some(text) = self.remembered_sort.refresh() {
                    self.host_sort = Some(decode_sort(text.as_deref()).unwrap_or((-1, false)));
                }
                *self.host_sort.get_or_insert((-1, false))
            }
        };
        if Self::sortable(columns, column) {
            (column, descending)
        } else {
            (-1, false)
        }
    }

    /// A click on a sortable header: ascending, descending, then none.
    pub fn cycle_sort(&mut self, columns: &[Column], column: usize) {
        let column = column as i32;
        if !Self::sortable(columns, column) {
            return;
        }
        let (current, descending) = self.current_sort(columns);
        let next = if current != column {
            (column, false)
        } else if !descending {
            (column, true)
        } else {
            (-1, false)
        };
        if self.bound_sort.is_none() {
            self.host_sort = Some(next);
            let encoded = (next.0 >= 0).then(|| encode_sort(next.0, next.1));
            self.remembered_sort.remember(encoded.as_deref());
        }
        self.emit
            .act(self.onsort.as_deref(), ActionValue::IntBool(next.0, next.1));
    }

    /// Every column's width as asked for: bound, remembered or the
    /// markup's (0 = automatic).
    pub fn widths(&mut self) -> Vec<f32> {
        if let Some(bound) = &self.bound_widths {
            return if bound.len() == self.markup_widths.len() {
                bound.clone()
            } else {
                self.markup_widths.clone()
            };
        }
        if let Some(text) = self.remembered_widths.refresh() {
            self.host_widths = decode_widths(text.as_deref(), self.markup_widths.len());
        }
        self.host_widths
            .clone()
            .unwrap_or_else(|| self.markup_widths.clone())
    }

    /// The columns with the widths in force.
    pub fn columns_now(&mut self, columns: &[Column]) -> Vec<Column> {
        let widths = self.widths();
        let last = columns.len().saturating_sub(1);
        columns
            .iter()
            .enumerate()
            .map(|(i, c)| {
                let mut c = c.clone();
                let w = widths.get(i).copied().unwrap_or(0.0);
                c.auto = i == last || w <= 0.0;
                c.width = if w > 0.0 { w } else { c.width };
                c
            })
            .collect()
    }

    /// Builds this frame's order from the rows, sort and tree.
    pub fn refresh(&mut self, columns: &[Column], rows: &[Vec<Cell>]) {
        let count = rows.len();
        let tree = match &mut self.tree {
            Some(t) => {
                self.shape.load(&t.levels, count);
                let bound = t.bound.clone();
                let start_collapsed = t.start_collapsed;
                let keys: Vec<String> = (0..count).map(|r| t.key(r)).collect();
                let toggled = if bound.is_none() {
                    t.toggled().clone()
                } else {
                    BTreeSet::new()
                };
                for (row, key) in keys.iter().enumerate() {
                    if !self.shape.has_children(row as i32) {
                        continue;
                    }
                    let expanded = match &bound {
                        Some(b) => b.contains(key),
                        None => toggled.contains(key) == start_collapsed,
                    };
                    self.shape.set_collapsed(row as i32, !expanded);
                }
                Some(&self.shape)
            }
            None => None,
        };
        let (sorted, descending) = {
            let (column, descending) = match self.bound_sort {
                Some(s) => s,
                None => self.host_sort.unwrap_or((-1, false)),
            };
            if Self::sortable(columns, column) {
                (column, descending)
            } else {
                (-1, false)
            }
        };
        let Ok(c) = usize::try_from(sorted) else {
            self.view.set_identity(count, tree);
            return;
        };
        let column = &columns[c];
        let cell = |row: &Vec<Cell>| row.get(c).cloned();
        if let Some(keys) = &self.number_keys[c] {
            let numbers: Vec<f64> = (0..count)
                .map(|r| keys.get(r).map_or(f64::NAN, |v| f64::from(*v)))
                .collect();
            self.view
                .sort(SortKeys::Number(&numbers), count, descending, tree);
        } else if column.sort == SortKind::Bool {
            let numbers: Vec<f64> = rows
                .iter()
                .map(|row| match cell(row) {
                    Some(Cell::Check(v)) => f64::from(u8::from(v)),
                    _ => f64::NAN,
                })
                .collect();
            self.view
                .sort(SortKeys::Number(&numbers), count, descending, tree);
        } else {
            let texts: Vec<Option<String>> = match &self.text_keys[c] {
                Some(keys) => (0..count).map(|r| keys.get(r).cloned()).collect(),
                None => rows
                    .iter()
                    .map(|row| match cell(row) {
                        Some(Cell::Text(s, _) | Cell::Button(s)) => Some(s),
                        _ => None,
                    })
                    .collect(),
            };
            if column.sort == SortKind::Number {
                let numbers: Vec<f64> = texts
                    .iter()
                    .map(|t| {
                        t.as_deref()
                            .and_then(|t| {
                                t.trim()
                                    .parse::<f64>()
                                    .ok()
                                    .or_else(|| parse_float(t).map(f64::from))
                            })
                            .unwrap_or(f64::NAN)
                    })
                    .collect();
                self.view
                    .sort(SortKeys::Number(&numbers), count, descending, tree);
            } else {
                self.view
                    .sort(SortKeys::Text(&texts), count, descending, tree);
            }
        }
    }

    /// The tree's level and arrow for plugin row `row`: (indent level,
    /// has children, expanded).
    #[must_use]
    pub fn tree_row(&self, row: usize) -> Option<(i32, bool, bool)> {
        self.tree.as_ref()?;
        let r = row as i32;
        Some((
            self.shape.level(r),
            self.shape.has_children(r),
            !self.shape.is_collapsed(r),
        ))
    }

    /// Whether a plugin row draws the selection band in multi mode.
    #[must_use]
    pub fn banded(&self, row: usize) -> bool {
        self.multi && self.selected_rows.contains(&(row as i32))
    }

    /// A pointer pick of a plugin row in multi mode (`add`: Ctrl, `range`:
    /// Shift).
    pub fn pick(&mut self, row: i32, add: bool, range: bool) {
        let next = selection::click(
            &self.selected_rows,
            row,
            add,
            range,
            self.anchor,
            &self.view,
        );
        if selection::moves_anchor(range, self.anchor, self.view.plugin_count()) {
            self.anchor = row;
        }
        self.lead = row;
        self.emit
            .act(self.onchange.as_deref(), ActionValue::IntList(next));
    }

    /// Ctrl+A: every row shown.
    pub fn select_all_rows(&mut self) {
        let all = selection::all(&self.view);
        self.emit
            .act(self.onchange.as_deref(), ActionValue::IntList(all));
    }

    /// A press on a parent row's arrow: the client applies (and
    /// remembers) it unbound; bound, the plugin is asked. A collapse drops
    /// the row's descendants from a multi selection.
    pub fn toggle_expanded(&mut self, row: usize) {
        let r = row as i32;
        if !self.shape.has_children(r) {
            return;
        }
        let expand = self.shape.is_collapsed(r);
        let Some(tree) = &mut self.tree else {
            return;
        };
        let key = tree.key(row);
        if tree.bound.is_none() {
            let toggled = tree.toggled();
            if !toggled.remove(&key) {
                toggled.insert(key.clone());
            }
            let encoded = (!toggled.is_empty()).then(|| encode_keys(toggled));
            tree.remembered.remember(encoded.as_deref());
            self.shape.set_collapsed(r, !expand);
        }
        let onexpand = tree.onexpand.clone();
        self.emit
            .act(onexpand.as_deref(), ActionValue::TextBool(key, expand));
        if !expand && self.multi {
            let end = self.shape.subtree_end(r);
            if self.anchor > r && self.anchor < end {
                self.anchor = r;
            }
            if self.lead > r && self.lead < end {
                self.lead = r;
            }
            let kept: Vec<i32> = self
                .selected_rows
                .iter()
                .copied()
                .filter(|s| *s <= r || *s >= end)
                .collect();
            if kept.len() != self.selected_rows.len() {
                self.emit
                    .act(self.onchange.as_deref(), ActionValue::IntList(kept));
            }
        }
    }

    /// Whether a press at list-local `x` lands in plugin row `row`'s arrow
    /// slot (its cell starting at `cell_x`).
    #[must_use]
    pub fn on_arrow(&self, row: usize, cell_x: f32, x: f32) -> bool {
        if self.tree.is_none() || !self.shape.has_children(row as i32) {
            return false;
        }
        let slot = cell_x + 3.0 + self.shape.level(row as i32) as f32 * TREE_INDENT;
        x >= slot && x < slot + TREE_INDENT
    }

    /// The header boundary right of a column within grabbing distance.
    #[must_use]
    pub fn boundary_at(&self, layout: &[(f32, f32)], x: f32, y: f32) -> Option<usize> {
        if !self.resizable
            || !self.header
            || !(0.0..HEADER_HEIGHT).contains(&y)
            || (self.bound_widths.is_some() && self.onresize.is_none())
        {
            return None;
        }
        (0..layout.len().saturating_sub(1)).find(|c| {
            let (cx, cw) = layout[*c];
            (x - (cx + cw)).abs() <= RESIZE_GRIP
        })
    }

    /// The header cell at `x`.
    #[must_use]
    pub fn header_column_at(layout: &[(f32, f32)], x: f32) -> Option<usize> {
        layout.iter().position(|(cx, cw)| x >= *cx && x < cx + cw)
    }

    /// A press on the header: a boundary begins a resize; a cell is held
    /// until its release.
    pub fn header_down(&mut self, layout: &[(f32, f32)], x: f32, y: f32, enabled: bool) {
        if !enabled {
            self.header_press = -1;
            return;
        }
        if let Some(column) = self.boundary_at(layout, x, y) {
            let start = self.widths();
            let mut right_reserve = 0.0;
            for c in column + 1..start.len() {
                let auto = c == start.len() - 1 || start[c] <= 0.0;
                right_reserve += if auto { MIN_COLUMN_WIDTH } else { start[c] };
            }
            self.resize = Some(Resize {
                column,
                press_x: x,
                moved: false,
                start_width: layout[column].1,
                asked: start.clone(),
                start,
                right_reserve,
            });
            return;
        }
        self.header_press = Self::header_column_at(layout, x).map_or(-1, |c| c as i32);
    }

    /// Whether a column resize is being dragged.
    #[must_use]
    pub fn resizing(&self) -> bool {
        self.resize.is_some()
    }

    /// The pointer moved during a resize.
    pub fn drag_resize(&mut self, layout: &[(f32, f32)], x: f32, content_width: f32) {
        let Some(r) = &mut self.resize else {
            return;
        };
        if (x - r.press_x).abs() > f32::EPSILON {
            r.moved = true;
        }
        let cell_x = layout.get(r.column).map_or(0.0, |l| l.0);
        let max = MIN_COLUMN_WIDTH.max(content_width - cell_x - r.right_reserve);
        let width = (r.start_width + x - r.press_x)
            .round()
            .clamp(MIN_COLUMN_WIDTH, max);
        let column = r.column;
        self.ask_width(column, width);
    }

    fn ask_width(&mut self, column: usize, width: f32) {
        if self.bound_widths.is_some() && self.onresize.is_none() {
            return;
        }
        let mut next = match &self.resize {
            Some(r) => r.asked.clone(),
            None => self.widths(),
        };
        if (next[column] - width).abs() < f32::EPSILON {
            return;
        }
        next[column] = width;
        if let Some(r) = &mut self.resize {
            r.asked.clone_from(&next);
        }
        if self.bound_widths.is_none() {
            self.host_widths = Some(next.clone());
        }
        self.emit
            .act(self.onresize.as_deref(), ActionValue::FloatList(next));
    }

    /// The press on the header came up at `x`: a resize ends (an unbound
    /// change is remembered), a held cell over the same cell is clicked
    /// (select-all, else a sort).
    pub fn header_up(
        &mut self,
        columns: &[Column],
        layout: &[(f32, f32)],
        x: f32,
        y: f32,
        rows: &[Vec<Cell>],
    ) {
        if let Some(r) = self.resize.take() {
            self.commit_widths(&r.start, &r.asked);
            return;
        }
        let pressed = std::mem::replace(&mut self.header_press, -1);
        if pressed < 0 || !(0.0..HEADER_HEIGHT).contains(&y) {
            return;
        }
        if Self::header_column_at(layout, x) != Some(pressed as usize) {
            return;
        }
        let column = pressed as usize;
        if let ColumnHeader::SelectAll(onall) = &columns[column].header {
            let state = select_all_state(rows, column);
            self.emit
                .act(onall.as_deref(), ActionValue::Bool(state != Some(true)));
            return;
        }
        self.cycle_sort(columns, column);
    }

    /// A double click on a boundary: the column left of it goes back to
    /// its markup width.
    pub fn reset_width(&mut self, layout: &[(f32, f32)], x: f32, y: f32) {
        let Some(column) = self.boundary_at(layout, x, y) else {
            return;
        };
        let before = self.widths();
        self.resize = Some(Resize {
            column,
            press_x: x,
            moved: false,
            start_width: 0.0,
            start: before.clone(),
            asked: before.clone(),
            right_reserve: 0.0,
        });
        let markup = self.markup_widths[column];
        self.ask_width(column, markup);
        if let Some(r) = self.resize.take() {
            self.commit_widths(&before, &r.asked);
        }
    }

    fn commit_widths(&mut self, before: &[f32], asked: &[f32]) {
        if self.bound_widths.is_some() || before == asked {
            return;
        }
        let encoded = encode_widths(asked);
        let markup = encode_widths(&self.markup_widths);
        self.remembered_widths
            .remember((encoded != markup).then_some(encoded.as_str()));
    }

    /// The select-all box's look for column `column`.
    #[must_use]
    pub fn select_all_look(rows: &[Vec<Cell>], column: usize) -> Option<bool> {
        select_all_state(rows, column)
    }
}

/// Ticked when every row is, clear when none is, else indeterminate.
fn select_all_state(rows: &[Vec<Cell>], column: usize) -> Option<bool> {
    let ticked = rows
        .iter()
        .filter(|r| matches!(r.get(column), Some(Cell::Check(true))))
        .count();
    if ticked == 0 {
        Some(false)
    } else if ticked == rows.len() {
        Some(true)
    } else {
        None
    }
}

/// `column:asc` or `column:desc`.
#[must_use]
pub fn encode_sort(column: i32, descending: bool) -> String {
    format!("{column}:{}", if descending { "desc" } else { "asc" })
}

/// The remembered sort read back.
#[must_use]
pub fn decode_sort(text: Option<&str>) -> Option<(i32, bool)> {
    let (column, direction) = text?.split_once(':')?;
    if column.is_empty() || !column.chars().all(|c| c.is_ascii_digit()) {
        return None;
    }
    let column: i32 = column.parse().ok()?;
    match direction {
        "asc" => Some((column, false)),
        "desc" => Some((column, true)),
        _ => None,
    }
}

/// Whole pixels joined by `,`.
#[must_use]
pub fn encode_widths(widths: &[f32]) -> String {
    widths
        .iter()
        .map(|w| (w.round() as i32).to_string())
        .collect::<Vec<_>>()
        .join(",")
}

/// The remembered widths read back: one per column, at least the minimum
/// (0 automatic); anything else is none.
#[must_use]
pub fn decode_widths(text: Option<&str>, count: usize) -> Option<Vec<f32>> {
    let parts: Vec<&str> = text?.split(',').collect();
    if parts.len() != count {
        return None;
    }
    parts
        .iter()
        .map(|p| {
            if p.is_empty() || !p.chars().all(|c| c.is_ascii_digit()) {
                return None;
            }
            let w: i32 = p.parse().ok()?;
            Some(if w > 0 {
                (w as f32).max(MIN_COLUMN_WIDTH)
            } else {
                0.0
            })
        })
        .collect()
}

/// Whether a column draws a select-all box in its header.
#[must_use]
pub fn is_select_all(column: &Column) -> bool {
    matches!(column.header, ColumnHeader::SelectAll(Some(_)))
        && matches!(column.kind, ColumnKind::Check { .. })
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/UI/MarkupListHeaderTests.cs` and
    //! `MarkupListTreeTests.cs`: the remembered forms of the sort, the
    //! widths and the expansion.
    use super::*;

    #[test]
    fn the_sort_remembers_as_column_and_direction() {
        // Sort_NameCyclesAscendingDescendingNone (the remembered text)
        assert_eq!(encode_sort(2, true), "2:desc");
        assert_eq!(decode_sort(Some("2:desc")), Some((2, true)));
        assert_eq!(decode_sort(Some("0:asc")), Some((0, false)));
        for bad in ["", "x:asc", ":asc", "-1:asc", "1:up", "1"] {
            assert_eq!(decode_sort(Some(bad)), None, "{bad}");
        }
    }

    #[test]
    fn widths_remember_as_whole_pixels_one_per_column() {
        // Resize_RemembersAndForgetsTheMarkupWidths
        assert_eq!(encode_widths(&[120.4, 80.0, 0.0]), "120,80,0");
        assert_eq!(
            decode_widths(Some("100,80,40,0"), 4),
            Some(vec![100.0, 80.0, 40.0, 0.0])
        );
        assert_eq!(decode_widths(Some("100,80"), 4), None);
        assert_eq!(
            decode_widths(Some("5,0"), 2),
            Some(vec![MIN_COLUMN_WIDTH, 0.0])
        );
        assert_eq!(decode_widths(Some("a,0"), 2), None);
    }

    #[test]
    fn the_expansion_remembers_as_a_json_array() {
        // Remembered_KeysFlipTheDefault, Remembered_MalformedValueIsIgnored
        let keys: BTreeSet<String> = ["b".to_owned(), "a\"q".to_owned()].into();
        let text = encode_keys(&keys);
        assert_eq!(text, r#"["a\"q","b"]"#);
        assert_eq!(decode_keys(Some(&text)), keys);
        assert_eq!(decode_keys(Some(r#"["x", null]"#)), ["x".to_owned()].into());
        assert!(decode_keys(Some("not json")).is_empty());
    }
}
