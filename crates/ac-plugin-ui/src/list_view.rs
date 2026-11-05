//! A markup list's row order, selection and tree (OpenAC
//! `UiMarkupListViewMap`, `UiMarkupListSelection`,
//! `UiMarkupListTreeShape`): pure functions over plugin rows (the plugin's
//! own indices) and view rows (what the player sees, top to bottom).

use std::cmp::Ordering;

/// A tree list's shape for one frame (OpenAC `UiMarkupListTreeShape`). The
/// plugin's rows are the tree flattened in pre-order, each with a depth; a
/// row's descendants are the rows straight after it that are deeper.
/// Levels are clamped as they load: a negative level, or a row past the end
/// of the levels, is level 0, and a row more than one deeper than the row
/// before it is one deeper.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TreeShape {
    levels: Vec<i32>,
    parents: Vec<i32>,
    collapsed: Vec<bool>,
    built: Option<(Vec<i32>, Vec<bool>)>,
}

impl TreeShape {
    /// The rows loaded.
    #[must_use]
    pub fn count(&self) -> usize {
        self.levels.len()
    }

    /// Loads `count` rows' levels; every row starts expanded.
    pub fn load(&mut self, levels: &[i32], count: usize) {
        self.levels.clear();
        self.parents.clear();
        self.collapsed.clear();
        self.collapsed.resize(count, false);
        let mut path: Vec<i32> = vec![0; count + 1];
        let mut previous = -1;
        for row in 0..count {
            let mut level = levels.get(row).copied().unwrap_or(0).max(0);
            level = level.min(previous + 1);
            self.levels.push(level);
            self.parents.push(if level == 0 {
                -1
            } else {
                path[(level - 1) as usize]
            });
            path[level as usize] = row as i32;
            previous = level;
        }
    }

    /// A row's depth (0 for a row outside).
    #[must_use]
    pub fn level(&self, row: i32) -> i32 {
        self.get(&self.levels, row).unwrap_or(0)
    }

    /// A row's parent; −1 for a top-level row.
    #[must_use]
    pub fn parent(&self, row: i32) -> i32 {
        self.get(&self.parents, row).unwrap_or(-1)
    }

    fn get<T: Copy>(&self, list: &[T], row: i32) -> Option<T> {
        usize::try_from(row).ok().and_then(|r| list.get(r).copied())
    }

    /// Whether the row has rows under it.
    #[must_use]
    pub fn has_children(&self, row: i32) -> bool {
        let Ok(r) = usize::try_from(row) else {
            return false;
        };
        r + 1 < self.levels.len() && self.levels[r + 1] > self.levels[r]
    }

    /// The first row after `row`'s descendants.
    #[must_use]
    pub fn subtree_end(&self, row: i32) -> i32 {
        let level = self.level(row);
        let mut end = row + 1;
        while (end as usize) < self.levels.len() && self.levels[end as usize] > level {
            end += 1;
        }
        end
    }

    /// Whether the row is folded.
    #[must_use]
    pub fn is_collapsed(&self, row: i32) -> bool {
        self.get(&self.collapsed, row).unwrap_or(false)
    }

    /// Folds or unfolds a row.
    pub fn set_collapsed(&mut self, row: i32, collapsed: bool) {
        if let Some(flag) = usize::try_from(row)
            .ok()
            .and_then(|r| self.collapsed.get_mut(r))
        {
            *flag = collapsed;
        }
    }

    /// Whether the levels or the folded rows differ from when
    /// [`Self::mark_built`] was last called.
    #[must_use]
    pub fn changed(&self) -> bool {
        match &self.built {
            Some((levels, collapsed)) => *levels != self.levels || *collapsed != self.collapsed,
            None => true,
        }
    }

    /// Notes the shape a view order has just been built from.
    pub fn mark_built(&mut self) {
        self.built = Some((self.levels.clone(), self.collapsed.clone()));
    }
}

/// What a sort orders rows by.
#[derive(Debug, Clone, Copy)]
pub enum SortKeys<'a> {
    /// Text, compared ignoring case; `None` sorts last.
    Text(&'a [Option<String>]),
    /// Numbers; NaN sorts last.
    Number(&'a [f64]),
}

/// .NET `string.Compare(a, b, StringComparison.OrdinalIgnoreCase)`: the
/// characters upper-cased one by one, then compared by number.
#[must_use]
pub fn compare_ignore_case(a: &str, b: &str) -> Ordering {
    let upper = |c: char| {
        let mut u = c.to_uppercase();
        match (u.next(), u.next()) {
            (Some(single), None) => single,
            _ => c,
        }
    };
    a.chars().map(upper).cmp(b.chars().map(upper))
}

impl SortKeys<'_> {
    fn compare(&self, a: usize, b: usize, descending: bool) -> Ordering {
        let order = match self {
            Self::Number(keys) => {
                let (x, y) = (keys[a], keys[b]);
                match (x.is_nan(), y.is_nan()) {
                    (true, false) => return Ordering::Greater,
                    (false, true) => return Ordering::Less,
                    (true, true) => Ordering::Equal,
                    (false, false) => x.partial_cmp(&y).unwrap_or(Ordering::Equal),
                }
            }
            Self::Text(keys) => match (&keys[a], &keys[b]) {
                (None, Some(_)) => return Ordering::Greater,
                (Some(_), None) => return Ordering::Less,
                (None, None) => Ordering::Equal,
                (Some(x), Some(y)) => compare_ignore_case(x, y),
            },
        };
        let order = if descending { order.reverse() } else { order };
        order.then(a.cmp(&b))
    }
}

/// The order a list shows its rows in (OpenAC `UiMarkupListViewMap`): view
/// row to plugin row and back. Identity until sorted. A sort is stable and
/// puts rows without a key last, both ways. With a tree it shows the rows in
/// pre-order, sorts each set of siblings, and leaves out the descendants of
/// folded rows.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ViewMap {
    view_to_plugin: Vec<i32>,
    plugin_to_view: Vec<i32>,
    identity: bool,
    view_count: usize,
    plugin_count: usize,
}

impl ViewMap {
    /// The rows shown.
    #[must_use]
    pub fn view_count(&self) -> usize {
        self.view_count
    }

    /// The plugin's rows, shown or not.
    #[must_use]
    pub fn plugin_count(&self) -> usize {
        self.plugin_count
    }

    /// The plugin row shown at a view row; −1 outside the rows.
    #[must_use]
    pub fn plugin_row(&self, view: i32) -> i32 {
        match usize::try_from(view) {
            Ok(v) if v < self.view_count => {
                if self.identity {
                    view
                } else {
                    self.view_to_plugin[v]
                }
            }
            _ => -1,
        }
    }

    /// Where a plugin row is shown; −1 outside the rows or folded away.
    #[must_use]
    pub fn view_row(&self, plugin: i32) -> i32 {
        match usize::try_from(plugin) {
            Ok(p) if p < self.plugin_count => {
                if self.identity {
                    plugin
                } else {
                    self.plugin_to_view[p]
                }
            }
            _ => -1,
        }
    }

    /// The plugin's order (a tree's pre-order without its folded rows).
    pub fn set_identity(&mut self, count: usize, tree: Option<&TreeShape>) {
        if let Some(tree) = tree {
            self.arrange_tree(tree, None);
            return;
        }
        self.identity = true;
        self.view_count = count;
        self.plugin_count = count;
    }

    /// Sorted by `keys` (one per plugin row); a tree sorts each set of
    /// siblings.
    pub fn sort(
        &mut self,
        keys: SortKeys<'_>,
        count: usize,
        descending: bool,
        tree: Option<&TreeShape>,
    ) {
        if let Some(tree) = tree {
            self.arrange_tree(tree, Some((keys, descending)));
            return;
        }
        let mut order: Vec<usize> = (0..count).collect();
        order.sort_by(|a, b| keys.compare(*a, *b, descending));
        self.view_to_plugin = order.iter().map(|r| *r as i32).collect();
        self.plugin_to_view = vec![0; count];
        for (view, plugin) in order.iter().enumerate() {
            self.plugin_to_view[*plugin] = view as i32;
        }
        self.identity = false;
        self.view_count = count;
        self.plugin_count = count;
    }

    fn arrange_tree(&mut self, tree: &TreeShape, order: Option<(SortKeys<'_>, bool)>) {
        let count = tree.count();
        // Each parent's children, in plugin order (group = parent + 1).
        let mut groups: Vec<Vec<usize>> = vec![Vec::new(); count + 1];
        for row in 0..count {
            groups[(tree.parent(row as i32) + 1) as usize].push(row);
        }
        if let Some((keys, descending)) = order {
            for group in &mut groups {
                if group.len() > 1 {
                    group.sort_by(|a, b| keys.compare(*a, *b, descending));
                }
            }
        }
        self.view_to_plugin = Vec::with_capacity(count);
        self.plugin_to_view = vec![-1; count];
        // Depth first with an explicit stack, so a deep tree cannot overflow.
        let mut stack: Vec<(usize, usize)> = vec![(0, 0)];
        while let Some((group, next)) = stack.last_mut() {
            let Some(&row) = groups[*group].get(*next) else {
                stack.pop();
                continue;
            };
            *next += 1;
            self.plugin_to_view[row] = self.view_to_plugin.len() as i32;
            self.view_to_plugin.push(row as i32);
            if !tree.is_collapsed(row as i32) && !groups[row + 1].is_empty() {
                stack.push((row + 1, 0));
            }
        }
        self.identity = false;
        self.view_count = self.view_to_plugin.len();
        self.plugin_count = count;
    }
}

/// The multi-select rules (OpenAC `UiMarkupListSelection`). Ranges run in
/// view order; every result is plugin rows ascending, without duplicates or
/// rows outside the list.
pub mod selection {
    use super::ViewMap;

    fn is_row(row: i32, count: usize) -> bool {
        usize::try_from(row).is_ok_and(|r| r < count)
    }

    fn normalize(rows: impl IntoIterator<Item = i32>, count: usize) -> Vec<i32> {
        let mut out: Vec<i32> = rows.into_iter().filter(|r| is_row(*r, count)).collect();
        out.sort_unstable();
        out.dedup();
        out
    }

    /// The selection after a click on `row`: that row alone; toggled in or
    /// out with `add` (Ctrl); the view range from `anchor` with `range`
    /// (Shift), added to the selection when `add` is held too. A range with
    /// no valid anchor acts as the same click without Shift.
    #[must_use]
    pub fn click(
        current: &[i32],
        row: i32,
        add: bool,
        range: bool,
        anchor: i32,
        view: &ViewMap,
    ) -> Vec<i32> {
        let count = view.plugin_count();
        if range && is_row(anchor, count) {
            let span = self::range(anchor, row, view);
            return if add {
                normalize(current.iter().copied().chain(span), count)
            } else {
                span
            };
        }
        if add {
            return if current.contains(&row) {
                normalize(current.iter().copied().filter(|r| *r != row), count)
            } else {
                normalize(current.iter().copied().chain([row]), count)
            };
        }
        normalize([row], count)
    }

    /// Whether a click moves the anchor to the clicked row: every click but
    /// a range that used it.
    #[must_use]
    pub fn moves_anchor(range: bool, anchor: i32, count: usize) -> bool {
        !(range && is_row(anchor, count))
    }

    /// The plugin rows shown from `anchor`'s view row to `lead`'s.
    #[must_use]
    pub fn range(anchor: i32, lead: i32, view: &ViewMap) -> Vec<i32> {
        let from = view.view_row(anchor);
        let to = view.view_row(lead);
        if to < 0 {
            return Vec::new();
        }
        if from < 0 {
            return vec![lead];
        }
        let (low, high) = if from <= to { (from, to) } else { (to, from) };
        let mut rows: Vec<i32> = (low..=high).map(|v| view.plugin_row(v)).collect();
        rows.sort_unstable();
        rows
    }

    /// Every row shown.
    #[must_use]
    pub fn all(view: &ViewMap) -> Vec<i32> {
        let mut rows: Vec<i32> = (0..view.view_count() as i32)
            .map(|v| view.plugin_row(v))
            .collect();
        rows.sort_unstable();
        rows
    }

    /// The plugin row one view row from `lead`, clamped to the rows; the
    /// first view row when the lead is not a row; −1 with no rows.
    #[must_use]
    pub fn step(lead: i32, delta: i32, view: &ViewMap) -> i32 {
        if view.view_count() == 0 {
            return -1;
        }
        let at = view.view_row(lead);
        let last = view.view_count() as i32 - 1;
        view.plugin_row(if at < 0 {
            0
        } else {
            (at + delta).clamp(0, last)
        })
    }
}

#[cfg(test)]
mod tests;
