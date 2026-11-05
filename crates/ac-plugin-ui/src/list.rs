//! A markup list's geometry: where its columns sit, which rows show, and
//! what a press at a point means. Pure, so the window's list widget and
//! its tests share it.

use crate::markup::{Column, ColumnKind};

/// Room for the scrollbar when the rows overflow, pixels.
pub const SCROLLBAR_WIDTH: f32 = 16.0;

/// Each column's left edge and width across `total_width`: fixed columns
/// take their width (clamped to what is left), the auto columns (and
/// always the last) share the rest equally, rounded down, the last taking
/// the rounding slack (OpenAC `UiMarkupList.ComputeColumnLayout`).
#[must_use]
pub fn column_layout(columns: &[Column], total_width: f32) -> Vec<(f32, f32)> {
    let n = columns.len();
    let mut auto = vec![false; n];
    let mut fixed = vec![0.0f32; n];
    let mut x = 0.0f32;
    let mut sum_fixed = 0.0f32;
    let mut auto_count = 0usize;
    for (i, column) in columns.iter().enumerate() {
        let is_auto = i == n - 1 || column.auto;
        auto[i] = is_auto;
        if is_auto {
            auto_count += 1;
            continue;
        }
        let available = (total_width - x).max(0.0);
        let w = column.width.max(0.0).min(available);
        fixed[i] = w;
        x += w;
        sum_fixed += w;
    }
    let remaining = (total_width - sum_fixed).max(0.0);
    let share = if auto_count > 0 {
        (remaining / auto_count as f32).floor()
    } else {
        0.0
    };
    let mut cursor = 0.0f32;
    let mut layout = Vec::with_capacity(n);
    for i in 0..n {
        let w = if auto[i] {
            if i == n - 1 {
                (remaining - share * (auto_count as f32 - 1.0)).max(0.0)
            } else {
                share
            }
        } else {
            fixed[i]
        };
        layout.push((cursor, w));
        cursor += w;
    }
    layout
}

/// Rows that fit: at least one.
#[must_use]
pub fn visible_rows(height: f32, row_height: f32) -> usize {
    ((height / row_height.max(1.0)).floor() as usize).max(1)
}

/// The top row kept in range.
#[must_use]
pub fn clamp_top(top: usize, count: usize, visible: usize) -> usize {
    top.min(count.saturating_sub(visible))
}

/// The top row after the selection moved to `selected`: scrolled just far
/// enough to show it.
#[must_use]
pub fn reveal(top: usize, selected: i32, count: usize, visible: usize) -> usize {
    let Ok(selected) = usize::try_from(selected) else {
        return top;
    };
    if selected >= count {
        return top;
    }
    if selected < top {
        selected
    } else if selected >= top + visible {
        selected + 1 - visible
    } else {
        top
    }
}

/// Where a button column's button sits in its cell, as (x, y, w, h): the
/// column's button size, centred, never past the cell's one-pixel inset; a
/// size of 0 fills the inset cell (OpenAC `UiMarkupList.ButtonRect`).
#[must_use]
pub fn button_rect(
    button_width: f32,
    button_height: f32,
    cell_x: f32,
    cell_w: f32,
    row_y: f32,
    row_height: f32,
) -> (f32, f32, f32, f32) {
    let max_w = (cell_w - 2.0).max(0.0);
    let max_h = (row_height - 2.0).max(0.0);
    let w = if button_width > 0.0 {
        button_width.min(max_w)
    } else {
        max_w
    };
    let h = if button_height > 0.0 {
        button_height.min(max_h)
    } else {
        max_h
    };
    (
        (cell_x + (cell_w - w) * 0.5).floor(),
        (row_y + (row_height - h) * 0.5).floor(),
        w,
        h,
    )
}

/// What a press in a column list does.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ColumnPress {
    /// Select the row (a text column without its own handler).
    Select(usize),
    /// A text column's own handler, with the row.
    TextClicked(usize, usize),
    /// A check column's handler, with the row.
    CheckChanged(usize, usize),
    /// An icon column's handler, with the row.
    IconClicked(usize, usize),
    /// Nothing (past the rows, or past a short column's own rows).
    Nothing,
}

/// The press at list-local `point` in a column list: which column and
/// row it hits and what that does (OpenAC `UiMarkupList.OnEventColumns`).
/// `rows[c]` is how many rows column `c` has; the list's row count is the
/// largest.
#[must_use]
pub fn press_columns(
    columns: &[Column],
    layout: &[(f32, f32)],
    rows: &[usize],
    top: usize,
    visible: usize,
    row_height: f32,
    point: (f32, f32),
) -> ColumnPress {
    let (x, y) = point;
    let row_count = rows.iter().copied().max().unwrap_or(0);
    let row = (y / row_height.max(1.0)).floor();
    if row < 0.0 || row as usize >= visible {
        return ColumnPress::Nothing;
    }
    let index = top + row as usize;
    if index >= row_count {
        return ColumnPress::Nothing;
    }
    for (c, column) in columns.iter().enumerate() {
        let (cell_x, cell_w) = layout[c];
        if x < cell_x || x >= cell_x + cell_w {
            continue;
        }
        let own = rows.get(c).copied().unwrap_or(0);
        return match &column.kind {
            ColumnKind::Text {
                onclick: Some(_), ..
            } if index < own => ColumnPress::TextClicked(c, index),
            ColumnKind::Text {
                onclick: Some(_), ..
            } => ColumnPress::Nothing,
            ColumnKind::Text { onclick: None, .. } => ColumnPress::Select(index),
            ColumnKind::Check { .. } if index < own => ColumnPress::CheckChanged(c, index),
            ColumnKind::Icon { .. } if index < own => ColumnPress::IconClicked(c, index),
            _ => ColumnPress::Nothing,
        };
    }
    ColumnPress::Nothing
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/MarkupListColumnsTests.cs`
    //! (the layout and click-routing cases).

    use super::*;
    use crate::markup::{ColumnHeader, IconKind, SortKind};

    fn text(width: f32, auto: bool, onclick: bool) -> Column {
        Column {
            width,
            auto,
            kind: ColumnKind::Text {
                items: "T".into(),
                colors: None,
                onclick: onclick.then(|| "C".into()),
            },
            header: ColumnHeader::None,
            sort: SortKind::None,
            sort_keys: None,
        }
    }

    fn icon(width: f32, auto: bool) -> Column {
        Column {
            width,
            auto,
            kind: ColumnKind::Icon {
                values: "I".into(),
                kind: IconKind::Did,
                onclick: "C".into(),
            },
            header: ColumnHeader::None,
            sort: SortKind::None,
            sort_keys: None,
        }
    }

    fn check(width: f32) -> Column {
        Column {
            width,
            auto: false,
            kind: ColumnKind::Check {
                values: "B".into(),
                onchange: "C".into(),
                boxed: false,
            },
            header: ColumnHeader::None,
            sort: SortKind::None,
            sort_keys: None,
        }
    }

    #[test]
    fn auto_columns_share_the_rest_and_the_last_takes_the_slack() {
        // AutoWidthColumns_ShareRemainingWidthEqually_LastAbsorbsRoundingSlack:
        // 101 px, a 10 px column, two auto columns: 45 and 46.
        let layout = column_layout(
            &[text(10.0, false, false), icon(0.0, true), icon(0.0, true)],
            101.0,
        );
        assert_eq!(layout, vec![(0.0, 10.0), (10.0, 45.0), (55.0, 46.0)]);
    }

    #[test]
    fn declared_widths_past_the_list_clamp_and_later_columns_get_nothing() {
        // DeclaredWidthsExceedingListWidth_ClampTheOverflowingColumn_LaterColumnsGetZero
        let layout = column_layout(
            &[icon(80.0, false), icon(20.0, false), icon(20.0, false)],
            50.0,
        );
        assert_eq!(layout, vec![(0.0, 50.0), (50.0, 0.0), (50.0, 0.0)]);
    }

    #[test]
    fn the_last_column_absorbs_the_rest_whatever_its_width() {
        // LastColumn_AbsorbsRemainingWidth_RegardlessOfItsOwnDeclaredWidth
        let layout = column_layout(
            &[text(30.0, false, false), text(500.0, false, false)],
            120.0,
        );
        assert_eq!(layout, vec![(0.0, 30.0), (30.0, 90.0)]);
    }

    #[test]
    fn presses_route_by_column_kind() {
        // ClickInTextColumn_SelectsAndDoesNotFireCheckOrIcon,
        // ClickInCheckColumn_FiresCheckCallbackWithRowIndex_AndDoesNotSelect,
        // ClickInIconColumn_FiresIconCallbackWithRowIndex_AndDoesNotSelect,
        // ClickPastTheLastRow_DoesNothing_ButStillSwallowsThePress,
        // ClickInShortCheckColumn_PastItsOwnRowCount_FiresNothing,
        // ClickInTextColumn_WithOnclick_FiresItInsteadOfSelecting
        let columns = [text(60.0, false, false), check(20.0), icon(20.0, false)];
        let layout = column_layout(&columns, 100.0);
        let rows = [3, 2, 3];
        let press = |x, y| press_columns(&columns, &layout, &rows, 0, 5, 20.0, (x, y));
        assert_eq!(press(10.0, 25.0), ColumnPress::Select(1));
        assert_eq!(press(65.0, 25.0), ColumnPress::CheckChanged(1, 1));
        assert_eq!(press(85.0, 45.0), ColumnPress::IconClicked(2, 2));
        assert_eq!(press(65.0, 45.0), ColumnPress::Nothing);
        assert_eq!(press(10.0, 70.0), ColumnPress::Nothing);
        let clickable = [text(60.0, false, true)];
        let layout = column_layout(&clickable, 60.0);
        assert_eq!(
            press_columns(&clickable, &layout, &[3], 0, 10, 20.0, (10.0, 25.0)),
            ColumnPress::TextClicked(0, 1)
        );
        // Scrolled: the offset carries into the hit test
        // (Scroll_OffsetIsRespectedBySubsequentHitTests).
        assert_eq!(
            press_columns(&clickable, &layout, &[30], 7, 3, 20.0, (10.0, 25.0)),
            ColumnPress::TextClicked(0, 8)
        );
    }

    #[test]
    fn a_button_sits_centred_in_its_cell_and_never_past_it() {
        // OpenAC PR #223 ListButtonColumnTests.SizedButton_IsCentredInItsCell_AndOnlyTheButtonTakesClicks,
        // SizedButton_NeverGrowsPastItsCell: a cell x 50..100, y 0..20.
        assert_eq!(
            button_rect(30.0, 10.0, 50.0, 50.0, 0.0, 20.0),
            (60.0, 5.0, 30.0, 10.0)
        );
        assert_eq!(
            button_rect(500.0, 500.0, 50.0, 50.0, 0.0, 20.0),
            (51.0, 1.0, 48.0, 18.0)
        );
        assert_eq!(
            button_rect(0.0, 0.0, 50.0, 50.0, 40.0, 20.0),
            (51.0, 41.0, 48.0, 18.0)
        );
        // A press in a button column is the button's own, never a select.
        let columns = [
            text(50.0, false, false),
            Column {
                width: 50.0,
                auto: false,
                kind: ColumnKind::Button {
                    items: "B".into(),
                    onclick: "C".into(),
                    button_width: 0.0,
                    button_height: 0.0,
                },
                header: ColumnHeader::None,
                sort: SortKind::None,
                sort_keys: None,
            },
        ];
        let layout = column_layout(&columns, 100.0);
        assert_eq!(
            press_columns(&columns, &layout, &[3, 3], 0, 3, 20.0, (70.0, 45.0)),
            ColumnPress::Nothing
        );
    }

    #[test]
    fn rows_scroll_to_reveal_the_selection() {
        assert_eq!(visible_rows(60.0, 18.0), 3);
        assert_eq!(visible_rows(5.0, 18.0), 1);
        assert_eq!(reveal(0, 5, 10, 3), 3);
        assert_eq!(reveal(4, 2, 10, 3), 2);
        assert_eq!(reveal(4, 5, 10, 3), 4);
        assert_eq!(reveal(4, -1, 10, 3), 4);
        assert_eq!(clamp_top(9, 10, 3), 7);
        assert_eq!(clamp_top(9, 2, 3), 0);
    }
}
