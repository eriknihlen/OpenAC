//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/Flex/FlexLayoutArrangeTests.cs`,
//! `FlexLayoutMeasureTests.cs`, `FlexLayoutWrapScrollTests.cs`,
//! `FlexFitTests.cs` and `FlexLayoutNestingTests.cs` (golden values worked
//! by hand from the CSS flexbox algorithm there). `FlexTestNodes` becomes
//! the builders below.

use super::*;

struct T {
    tree: FlexTree,
}

impl T {
    fn new() -> Self {
        Self {
            tree: FlexTree::default(),
        }
    }

    fn leaf(&mut self, w: f32, h: f32) -> usize {
        self.leaf_min(w, h, None, None)
    }

    fn leaf_min(&mut self, w: f32, h: f32, min_w: Option<f32>, min_h: Option<f32>) -> usize {
        self.tree.add(FlexNode::leaf(FlexMeasurement::new(
            FlexSize::new(w, h),
            FlexSize::new(min_w.unwrap_or(w), min_h.unwrap_or(h)),
        )))
    }

    fn container(&mut self, direction: FlexDirection, children: &[usize]) -> usize {
        let mut node = FlexNode::container(direction);
        node.children = children.to_vec();
        self.tree.add(node)
    }

    fn row(&mut self, children: &[usize]) -> usize {
        self.container(FlexDirection::Row, children)
    }

    fn column(&mut self, children: &[usize]) -> usize {
        self.container(FlexDirection::Column, children)
    }

    fn n(&mut self, i: usize) -> &mut FlexNode {
        &mut self.tree.nodes[i]
    }

    fn rect(&self, i: usize) -> FlexRect {
        self.tree.nodes[i].rect
    }

    fn arrange(&mut self, root: usize, w: f32, h: f32) {
        self.tree.arrange(root, FlexRect::new(0.0, 0.0, w, h));
    }
}

fn r(x: f32, y: f32, w: f32, h: f32) -> FlexRect {
    FlexRect::new(x, y, w, h)
}

#[test]
fn the_root_keeps_its_bounds() {
    // Root_keeps_the_bounds_it_is_given
    let mut t = T::new();
    let a = t.leaf(10.0, 10.0);
    let row = t.row(&[a]);
    t.tree.arrange(row, r(5.5, 7.0, 100.0, 30.0));
    assert_eq!(t.rect(row), r(5.5, 7.0, 100.0, 30.0));
}

#[test]
fn grow_shares_free_space_by_factor() {
    // Grow_shares_free_space_by_factor
    let mut t = T::new();
    let (a, b) = (t.leaf(50.0, 10.0), t.leaf(50.0, 10.0));
    t.n(a).grow = 1.0;
    t.n(b).grow = 2.0;
    let row = t.row(&[a, b]);
    t.arrange(row, 300.0, 10.0);
    assert_eq!(t.rect(a), r(0.0, 0.0, 117.0, 10.0));
    assert_eq!(t.rect(b), r(117.0, 0.0, 183.0, 10.0));
}

#[test]
fn factors_below_one_take_their_share_of_the_initial_free_space() {
    // Factors_summing_below_one_take_that_share_of_the_initial_free_space
    let mut t = T::new();
    let a = t.leaf_min(50.0, 10.0, Some(0.0), None);
    let b = t.leaf_min(50.0, 10.0, Some(0.0), None);
    t.n(a).grow = 0.5;
    t.n(a).max_width = Some(100.0);
    t.n(b).grow = 0.25;
    let row = t.row(&[a, b]);
    t.arrange(row, 300.0, 10.0);
    assert_eq!(t.rect(a), r(0.0, 0.0, 100.0, 10.0));
    assert_eq!(t.rect(b), r(100.0, 0.0, 100.0, 10.0));
}

#[test]
fn shrink_is_weighted_by_basis_and_limits_freeze() {
    // Shrink_is_weighted_by_basis,
    // An_item_shrunk_to_its_minimum_freezes_and_the_rest_take_the_remainder,
    // An_item_grown_to_its_maximum_freezes_and_the_rest_take_the_remainder
    let mut t = T::new();
    let a = t.leaf_min(100.0, 10.0, Some(0.0), None);
    let b = t.leaf_min(50.0, 10.0, Some(0.0), None);
    let row = t.row(&[a, b]);
    t.arrange(row, 100.0, 10.0);
    assert_eq!(t.rect(a), r(0.0, 0.0, 67.0, 10.0));
    assert_eq!(t.rect(b), r(67.0, 0.0, 33.0, 10.0));

    let mut t = T::new();
    let a = t.leaf_min(100.0, 10.0, Some(80.0), None);
    let b = t.leaf_min(100.0, 10.0, Some(0.0), None);
    let row = t.row(&[a, b]);
    t.arrange(row, 100.0, 10.0);
    assert_eq!(t.rect(a).width, 80.0);
    assert_eq!(t.rect(b), r(80.0, 0.0, 20.0, 10.0));

    let mut t = T::new();
    let (a, b) = (t.leaf(50.0, 10.0), t.leaf(50.0, 10.0));
    t.n(a).grow = 1.0;
    t.n(a).max_width = Some(100.0);
    t.n(b).grow = 1.0;
    let row = t.row(&[a, b]);
    t.arrange(row, 300.0, 10.0);
    assert_eq!(t.rect(a).width, 100.0);
    assert_eq!(t.rect(b), r(100.0, 0.0, 200.0, 10.0));
}

#[test]
fn a_grow_below_one_and_a_basis() {
    // Grow_factors_summing_below_one_take_only_that_share,
    // Basis_replaces_the_measured_size_as_the_starting_point
    let mut t = T::new();
    let a = t.leaf(100.0, 10.0);
    t.n(a).grow = 0.5;
    let row = t.row(&[a]);
    t.arrange(row, 200.0, 10.0);
    assert_eq!(t.rect(a).width, 150.0);

    let mut t = T::new();
    let a = t.leaf_min(10.0, 10.0, Some(0.0), None);
    let b = t.leaf_min(10.0, 10.0, Some(0.0), None);
    for i in [a, b] {
        t.n(i).basis = Some(0.0);
        t.n(i).grow = 1.0;
    }
    let row = t.row(&[a, b]);
    t.arrange(row, 100.0, 10.0);
    assert_eq!((t.rect(a).width, t.rect(b).width), (50.0, 50.0));
}

#[test]
fn justify_places_items_along_the_main_axis() {
    // Justify_places_items_along_the_main_axis, Justify_does_not_move_items_that_overflow
    for (justify, xs) in [
        (FlexJustify::Start, [0.0, 50.0, 100.0]),
        (FlexJustify::Center, [75.0, 125.0, 175.0]),
        (FlexJustify::End, [150.0, 200.0, 250.0]),
        (FlexJustify::SpaceBetween, [0.0, 125.0, 250.0]),
    ] {
        let mut t = T::new();
        let items = [t.leaf(50.0, 10.0), t.leaf(50.0, 10.0), t.leaf(50.0, 10.0)];
        let row = t.row(&items);
        t.n(row).justify = justify;
        t.arrange(row, 300.0, 10.0);
        let got: Vec<f32> = items.iter().map(|i| t.rect(*i).x).collect();
        assert_eq!(got, xs, "{justify:?}");
    }
    let mut t = T::new();
    let (a, b) = (t.leaf(40.0, 10.0), t.leaf(40.0, 10.0));
    let row = t.row(&[a, b]);
    t.n(row).justify = FlexJustify::Center;
    t.arrange(row, 50.0, 10.0);
    assert_eq!(t.rect(b), r(40.0, 0.0, 40.0, 10.0));
    assert_eq!(t.tree.nodes[row].content_size, FlexSize::new(80.0, 10.0));
}

#[test]
fn padding_gap_and_align() {
    // Padding_and_gap_offset_the_items, Align_places_items_across_the_line,
    // AlignSelf_overrides_the_container,
    // Stretch_keeps_an_explicit_cross_size_and_respects_the_maximum
    let mut t = T::new();
    let (a, b) = (t.leaf(20.0, 10.0), t.leaf(20.0, 10.0));
    let row = t.row(&[a, b]);
    t.n(row).padding = FlexEdges::all(10.0);
    t.n(row).gap = 5.0;
    t.arrange(row, 200.0, 50.0);
    assert_eq!(t.rect(a), r(10.0, 10.0, 20.0, 30.0));
    assert_eq!(t.rect(b), r(35.0, 10.0, 20.0, 30.0));

    for (align, y, h) in [
        (FlexAlign::Start, 0.0, 10.0),
        (FlexAlign::Center, 20.0, 10.0),
        (FlexAlign::End, 40.0, 10.0),
        (FlexAlign::Stretch, 0.0, 50.0),
    ] {
        let mut t = T::new();
        let a = t.leaf(20.0, 10.0);
        let row = t.row(&[a]);
        t.n(row).align = align;
        t.arrange(row, 100.0, 50.0);
        assert_eq!(t.rect(a), r(0.0, y, 20.0, h), "{align:?}");
    }

    let mut t = T::new();
    let (a, b) = (t.leaf(20.0, 10.0), t.leaf(20.0, 10.0));
    t.n(b).align_self = Some(FlexAlign::End);
    let row = t.row(&[a, b]);
    t.arrange(row, 100.0, 50.0);
    assert_eq!(t.rect(a).height, 50.0);
    assert_eq!(t.rect(b), r(20.0, 40.0, 20.0, 10.0));

    let mut t = T::new();
    let (fixed, capped) = (t.leaf(20.0, 10.0), t.leaf(20.0, 10.0));
    t.n(fixed).height = Some(12.0);
    t.n(capped).max_height = Some(30.0);
    let row = t.row(&[fixed, capped]);
    t.arrange(row, 100.0, 50.0);
    assert_eq!((t.rect(fixed).height, t.rect(capped).height), (12.0, 30.0));
}

#[test]
fn hidden_items_and_columns() {
    // Hidden_items_take_no_space, Column_lays_out_vertically
    let mut t = T::new();
    let (a, hidden, b) = (t.leaf(20.0, 10.0), t.leaf(500.0, 10.0), t.leaf(20.0, 10.0));
    t.n(hidden).hidden = true;
    let row = t.row(&[a, hidden, b]);
    t.n(row).gap = 4.0;
    t.arrange(row, 100.0, 10.0);
    assert_eq!(t.rect(b).x, 24.0);
    assert_eq!(t.rect(hidden), FlexRect::default());

    let mut t = T::new();
    let (a, b) = (t.leaf(20.0, 50.0), t.leaf(30.0, 50.0));
    t.n(b).grow = 1.0;
    let column = t.column(&[a, b]);
    t.n(column).align = FlexAlign::Start;
    t.arrange(column, 100.0, 300.0);
    assert_eq!(t.rect(a), r(0.0, 0.0, 20.0, 50.0));
    assert_eq!(t.rect(b), r(0.0, 50.0, 30.0, 250.0));
}

#[test]
fn snapping_spreads_rounding_across_the_line() {
    // Snapping_spreads_rounding_across_the_line, Snapping_a_fractional_gap_keeps_item_sizes,
    // Space_between_with_a_remainder_ends_exactly_at_the_edge
    let mut t = T::new();
    let items = [t.leaf(0.0, 10.0), t.leaf(0.0, 10.0), t.leaf(0.0, 10.0)];
    for i in items {
        t.n(i).grow = 1.0;
    }
    let row = t.row(&items);
    t.arrange(row, 100.0, 10.0);
    let widths: Vec<f32> = items.iter().map(|i| t.rect(*i).width).collect();
    assert_eq!(widths, [33.0, 34.0, 33.0]);

    let mut t = T::new();
    let items = [t.leaf(10.0, 10.0), t.leaf(10.0, 10.0), t.leaf(10.0, 10.0)];
    let row = t.row(&items);
    t.n(row).gap = 2.5;
    t.arrange(row, 100.0, 10.0);
    let xs: Vec<f32> = items.iter().map(|i| t.rect(*i).x).collect();
    assert_eq!(xs, [0.0, 13.0, 25.0]);

    let mut t = T::new();
    let items = [
        t.leaf(10.0, 10.0),
        t.leaf(10.0, 10.0),
        t.leaf(10.0, 10.0),
        t.leaf(10.0, 10.0),
    ];
    let row = t.row(&items);
    t.n(row).justify = FlexJustify::SpaceBetween;
    t.arrange(row, 101.0, 10.0);
    let xs: Vec<f32> = items.iter().map(|i| t.rect(*i).x).collect();
    assert_eq!(xs, [0.0, 30.0, 61.0, 91.0]);
}

#[test]
fn measuring_leaves_rows_columns_and_limits() {
    // FlexLayoutMeasureTests: Leaf_reports_its_content_measurement,
    // Explicit_size_overrides_preferred_and_caps_the_automatic_minimum,
    // Explicit_minimum_lowers_the_floor_and_maximum_clamps_both,
    // Row_adds_items_gaps_and_padding, Column_swaps_the_axes, Hidden_items_are_not_counted
    let mut t = T::new();
    let leaf = t.leaf_min(120.0, 20.0, Some(40.0), None);
    let m = t.tree.measure(leaf);
    assert_eq!(
        (m.preferred, m.minimum),
        (FlexSize::new(120.0, 20.0), FlexSize::new(40.0, 20.0))
    );
    let leaf = t.leaf_min(120.0, 20.0, Some(80.0), None);
    t.n(leaf).width = Some(50.0);
    let m = t.tree.measure(leaf);
    assert_eq!((m.preferred.width, m.minimum.width), (50.0, 50.0));
    let lowered = t.leaf_min(120.0, 20.0, Some(80.0), None);
    t.n(lowered).min_width = Some(10.0);
    assert_eq!(t.tree.measure(lowered).minimum.width, 10.0);
    let capped = t.leaf_min(120.0, 20.0, Some(80.0), None);
    t.n(capped).max_width = Some(60.0);
    let m = t.tree.measure(capped);
    assert_eq!((m.preferred.width, m.minimum.width), (60.0, 60.0));

    let mut t = T::new();
    let items = [
        t.leaf_min(50.0, 10.0, Some(20.0), None),
        t.leaf(30.0, 24.0),
        t.leaf_min(40.0, 12.0, Some(10.0), None),
    ];
    let row = t.row(&items);
    t.n(row).gap = 4.0;
    t.n(row).padding = FlexEdges {
        top: 1.0,
        right: 2.0,
        bottom: 3.0,
        left: 4.0,
    };
    let m = t.tree.measure(row);
    assert_eq!(m.preferred, FlexSize::new(134.0, 28.0));
    assert_eq!(m.minimum, FlexSize::new(74.0, 28.0));

    let mut t = T::new();
    let items = [
        t.leaf(50.0, 10.0),
        t.leaf_min(30.0, 24.0, Some(5.0), Some(6.0)),
    ];
    let column = t.column(&items);
    t.n(column).gap = 2.0;
    let m = t.tree.measure(column);
    assert_eq!(
        (m.preferred, m.minimum),
        (FlexSize::new(50.0, 36.0), FlexSize::new(50.0, 18.0))
    );
}

#[test]
fn a_wrapping_minimum_is_the_widest_item_and_its_lines() {
    // Wrapping_minimum_is_the_widest_item_and_the_lines_it_makes
    let mut t = T::new();
    let items = [
        t.leaf(40.0, 10.0),
        t.leaf(20.0, 12.0),
        t.leaf(10.0, 6.0),
        t.leaf(30.0, 8.0),
    ];
    let row = t.row(&items);
    t.n(row).wrap = true;
    t.n(row).gap = 4.0;
    let m = t.tree.measure(row);
    assert_eq!(m.preferred, FlexSize::new(112.0, 12.0));
    assert_eq!(m.minimum, FlexSize::new(40.0, 38.0));
}

#[test]
fn a_scrolling_axis_needs_only_a_small_viewport() {
    // Scrolling_axis_needs_only_a_small_viewport_and_its_bar_takes_the_other_axis
    for (sx, sy, min, pref) in [
        (false, true, (316.0, 40.0), (316.0, 400.0)),
        (true, false, (40.0, 416.0), (300.0, 416.0)),
        (true, true, (56.0, 56.0), (300.0, 400.0)),
    ] {
        let mut t = T::new();
        let items = [t.leaf(300.0, 200.0), t.leaf(300.0, 200.0)];
        let column = t.column(&items);
        t.n(column).scroll_x = sx;
        t.n(column).scroll_y = sy;
        let m = t.tree.measure(column);
        assert_eq!(m.minimum, FlexSize::new(min.0, min.1), "{sx} {sy}");
        assert_eq!(m.preferred, FlexSize::new(pref.0, pref.1), "{sx} {sy}");
    }
}

#[test]
fn wrapping_breaks_lines_and_shares_the_cross_space() {
    // FlexLayoutWrapScrollTests: Wrap_breaks_items_into_lines_that_fit,
    // Wrapped_lines_stretch_their_items_over_the_shared_space,
    // A_wrapping_row_that_scrolls_vertically_keeps_its_width_and_grows_down,
    // Wrap_does_not_break_along_an_axis_that_scrolls
    let mut t = T::new();
    let items: Vec<usize> = (0..5).map(|_| t.leaf(30.0, 10.0)).collect();
    let row = t.row(&items);
    t.n(row).wrap = true;
    t.n(row).gap = 5.0;
    t.n(row).align = FlexAlign::Start;
    t.arrange(row, 100.0, 100.0);
    assert_eq!(t.rect(items[2]), r(70.0, 0.0, 30.0, 10.0));
    assert_eq!(t.rect(items[3]), r(0.0, 53.0, 30.0, 10.0));
    assert_eq!(t.rect(items[4]), r(35.0, 53.0, 30.0, 10.0));

    let mut t = T::new();
    let items: Vec<usize> = (0..5).map(|_| t.leaf(30.0, 10.0)).collect();
    let row = t.row(&items);
    t.n(row).wrap = true;
    t.n(row).gap = 5.0;
    t.arrange(row, 100.0, 100.0);
    assert_eq!(t.rect(items[0]).height, 48.0);
    assert_eq!(t.rect(items[3]), r(0.0, 53.0, 30.0, 47.0));

    let mut t = T::new();
    let items: Vec<usize> = (0..7).map(|_| t.leaf(30.0, 30.0)).collect();
    let row = t.row(&items);
    t.n(row).wrap = true;
    t.n(row).gap = 5.0;
    t.n(row).scroll_y = true;
    t.arrange(row, 100.0, 50.0);
    assert_eq!(t.rect(items[6]), r(0.0, 70.0, 30.0, 30.0));
    assert_eq!(t.tree.nodes[row].content_size, FlexSize::new(100.0, 100.0));

    let mut t = T::new();
    let items: Vec<usize> = (0..5).map(|_| t.leaf(30.0, 10.0)).collect();
    let row = t.row(&items);
    t.n(row).wrap = true;
    t.n(row).scroll_x = true;
    t.arrange(row, 100.0, 10.0);
    assert_eq!(t.rect(items[4]), r(120.0, 0.0, 30.0, 10.0));
    assert_eq!(t.tree.nodes[row].content_size, FlexSize::new(150.0, 10.0));
}

#[test]
fn a_scrolling_column_does_not_shrink_its_items() {
    // A_scrolling_column_does_not_shrink_its_items
    let mut t = T::new();
    let a = t.leaf_min(50.0, 80.0, None, Some(10.0));
    let b = t.leaf_min(50.0, 80.0, None, Some(10.0));
    let column = t.column(&[a, b]);
    t.n(column).scroll_y = true;
    t.n(column).gap = 4.0;
    t.n(column).padding = FlexEdges::all(2.0);
    t.arrange(column, 100.0, 100.0);
    assert_eq!(t.rect(b), r(2.0, 86.0, 96.0, 80.0));
    assert_eq!(
        t.tree.nodes[column].content_size,
        FlexSize::new(100.0, 168.0)
    );
}

#[test]
fn fitting_grows_past_an_estimate_but_never_a_scrolling_axis() {
    // FlexFitTests: A_plain_column_fits_its_measured_minimum,
    // Wrapped_lines_that_break_on_a_basis_grow_the_minimum,
    // A_scrolling_axis_is_never_grown, Fractional_minimums_round_up_to_whole_points
    let mut t = T::new();
    let items = [t.leaf_min(80.0, 20.0, Some(30.0), None), t.leaf(50.0, 30.0)];
    let root = t.column(&items);
    t.n(root).gap = 4.0;
    t.n(root).padding = FlexEdges::all(5.0);
    assert_eq!(t.tree.fit_minimum(root), FlexSize::new(60.0, 64.0));

    let mut t = T::new();
    let (a, b, c) = (t.leaf(10.0, 10.0), t.leaf(10.0, 10.0), t.leaf(60.0, 10.0));
    t.n(a).basis = Some(50.0);
    t.n(b).basis = Some(50.0);
    let root = t.row(&[a, b, c]);
    t.n(root).wrap = true;
    assert_eq!(t.tree.measure(root).minimum, FlexSize::new(60.0, 20.0));
    assert_eq!(t.tree.fit_minimum(root), FlexSize::new(60.0, 30.0));

    let mut t = T::new();
    let items = [t.leaf(50.0, 100.0), t.leaf(50.0, 100.0)];
    let root = t.column(&items);
    t.n(root).scroll_y = true;
    let fitted = t.tree.fit_minimum(root);
    assert_eq!(fitted.height, MINIMUM_SCROLL_VIEWPORT);
    assert_eq!(fitted.width, 50.0 + SCROLLBAR_THICKNESS);

    let mut t = T::new();
    let items = [t.leaf(10.25, 7.5), t.leaf(10.25, 7.5)];
    let root = t.row(&items);
    assert_eq!(t.tree.fit_minimum(root), FlexSize::new(21.0, 8.0));
}

#[test]
fn a_wrapping_row_in_a_column_is_as_tall_as_its_lines() {
    // FlexLayoutNestingTests: a size-dependent child is laid out at the
    // width it gets to find its height.
    let mut t = T::new();
    let items: Vec<usize> = (0..4).map(|_| t.leaf(40.0, 10.0)).collect();
    let wrap = t.row(&items);
    t.n(wrap).wrap = true;
    let after = t.leaf(20.0, 10.0);
    let column = t.column(&[wrap, after]);
    t.n(column).align = FlexAlign::Start;
    t.arrange(column, 100.0, 200.0);
    // At 100 wide: two per line, two lines of 10.
    assert_eq!(t.rect(wrap).height, 20.0);
    assert_eq!(t.rect(after).y, 20.0);
}

#[test]
fn scrollbars_settle_and_take_room_from_the_viewport() {
    // FlexLayoutScrollbarTests: a vertical bar narrows the viewport the
    // items lay out in.
    let mut t = T::new();
    let items = [t.leaf(50.0, 80.0), t.leaf(50.0, 80.0)];
    let column = t.column(&items);
    t.n(column).scroll_y = true;
    t.n(column).scrollbar_size = 16.0;
    t.arrange(column, 100.0, 100.0);
    let n = &t.tree.nodes[column];
    assert!(n.scrollbar_shown_y);
    assert!(!n.scrollbar_shown_x);
    assert_eq!(n.viewport, FlexSize::new(84.0, 100.0));
    assert_eq!(t.rect(items[0]).width, 84.0);
}
