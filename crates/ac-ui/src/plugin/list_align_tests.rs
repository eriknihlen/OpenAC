//! A list column's text alignment (`align="left|right"`, API 1.10): where
//! a text cell's glyphs are drawn. The markup cases are in `ac-plugin-ui`'s
//! `markup/tests.rs`, the arithmetic in its `list.rs`.

use std::sync::Arc;

use ac_plugin_ui::markup::{Column, ColumnAlign, ColumnHeader, ColumnKind, SortKind};

use super::widgets::{Cell, MarkupList};
use crate::draw::{Color, DrawCmd, NoSprites, TextureRef};
use crate::font::UiFont;
use crate::root::UiRoot;
use crate::tree::Element;

/// The list's cell padding, pixels.
const PADDING: f32 = 3.0;

fn text_column(width: f32, align: ColumnAlign) -> Column {
    Column {
        width,
        auto: false,
        kind: ColumnKind::Text {
            items: "Values".into(),
            colors: None,
            onclick: None,
        },
        header: ColumnHeader::None,
        sort: SortKind::None,
        sort_keys: None,
        align,
    }
}

fn font() -> Arc<UiFont> {
    Arc::new(
        crate::bundled_font::bake(16.0, crate::bundled_font::Weight::Regular)
            .unwrap()
            .create_font(TextureRef::Runtime(1)),
    )
}

/// The left edge of the leftmost glyph a 40-wide first column (then a
/// 60-wide one) draws for `text`, aligned `align`.
fn first_glyph_x(font: &Arc<UiFont>, text: &str, align: ColumnAlign) -> f32 {
    let mut ui = UiRoot::new();
    ui.set_size(800.0, 600.0);
    let mut list = MarkupList::new(Some(Arc::clone(font)), 20.0);
    list.columns = vec![
        text_column(40.0, align),
        text_column(60.0, ColumnAlign::Left),
    ];
    list.rows = vec![vec![Cell::Text(text.to_owned(), Color::WHITE), Cell::Empty]];
    list.background = Color::TRANSPARENT;
    list.border = Color::TRANSPARENT;
    let root = ui.root();
    ui.add(root, Element::at(0.0, 0.0, 100.0, 60.0), list);
    let drawn = ui.draw((800.0, 600.0), &NoSprites);
    drawn
        .main
        .iter()
        .filter_map(|c| match c {
            DrawCmd::Glyphs {
                texture: TextureRef::Runtime(1),
                quads,
                ..
            } => Some(quads.iter().map(|q| q.x).collect::<Vec<_>>()),
            _ => None,
        })
        .flatten()
        .fold(f32::INFINITY, f32::min)
}

#[test]
fn a_right_aligned_column_ends_its_text_a_padding_short_of_its_right_edge() {
    let font = font();
    let width = font.measure_width("12");
    assert!(width > 0.0 && width < 40.0 - 2.0 * PADDING, "{width}");
    let left = first_glyph_x(&font, "12", ColumnAlign::Left);
    let right = first_glyph_x(&font, "12", ColumnAlign::Right);
    assert!(left.is_finite() && right.is_finite());
    // The same glyphs, moved from x = PADDING to x = 40 - width - PADDING.
    let moved = (40.0 - width - PADDING) - PADDING;
    assert!(
        (right - left - moved).abs() < 0.01,
        "{left} {right} {moved}"
    );
}

#[test]
fn a_right_aligned_text_too_wide_for_its_column_starts_at_the_left_padding() {
    let font = font();
    let text = "1234567890";
    assert!(font.measure_width(text) > 40.0);
    assert_eq!(
        first_glyph_x(&font, text, ColumnAlign::Right),
        first_glyph_x(&font, text, ColumnAlign::Left)
    );
}
