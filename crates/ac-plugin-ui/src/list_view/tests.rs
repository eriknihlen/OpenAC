//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiMarkupListViewMapTests.cs`,
//! `UiMarkupListSelectionTests.cs` and `UiMarkupListTreeShapeTests.cs`.

use super::selection::{all, click, moves_anchor, range, step};
use super::*;

fn order(map: &ViewMap) -> Vec<i32> {
    (0..map.view_count() as i32)
        .map(|v| map.plugin_row(v))
        .collect()
}

fn texts(keys: &[Option<&str>]) -> Vec<Option<String>> {
    keys.iter().map(|k| k.map(str::to_owned)).collect()
}

fn identity(count: usize) -> ViewMap {
    let mut map = ViewMap::default();
    map.set_identity(count, None);
    map
}

/// Keys 30, 10, 20, 40 ascending: view order is plugin rows 1, 2, 0, 3.
fn sorted() -> ViewMap {
    let mut map = ViewMap::default();
    map.sort(SortKeys::Number(&[30.0, 10.0, 20.0, 40.0]), 4, false, None);
    map
}

#[test]
fn identity_maps_each_row_to_itself() {
    // IdentityMapsEachRowToItself
    let map = identity(3);
    assert_eq!(order(&map), [0, 1, 2]);
    assert_eq!(map.view_row(2), 2);
    assert_eq!(map.plugin_row(3), -1);
    assert_eq!(map.plugin_row(-1), -1);
    assert_eq!(map.view_row(3), -1);
}

#[test]
fn a_text_sort_ignores_case_and_puts_missing_keys_last() {
    // TextSortIgnoresCaseAndPutsMissingKeysLast
    let keys = texts(&[
        Some("mosswart"),
        Some("Drudge"),
        None,
        Some("rat"),
        Some("Banderling"),
    ]);
    let mut map = ViewMap::default();
    map.sort(SortKeys::Text(&keys), 5, false, None);
    assert_eq!(order(&map), [4, 1, 0, 3, 2]);
    map.sort(SortKeys::Text(&keys), 5, true, None);
    assert_eq!(order(&map), [3, 0, 1, 4, 2]);
}

#[test]
fn a_number_sort_puts_nan_last_both_ways() {
    // NumberSortPutsNaNLastBothWays
    let keys = [10.0, f64::NAN, 2.0, 33.0];
    let mut map = ViewMap::default();
    map.sort(SortKeys::Number(&keys), 4, false, None);
    assert_eq!(order(&map), [2, 0, 3, 1]);
    map.sort(SortKeys::Number(&keys), 4, true, None);
    assert_eq!(order(&map), [3, 0, 2, 1]);
}

#[test]
fn ties_keep_the_plugin_order_both_ways() {
    // TiesKeepThePluginOrderBothWays
    let keys = [1.0, 0.0, 1.0, 0.0];
    let mut map = ViewMap::default();
    map.sort(SortKeys::Number(&keys), 4, false, None);
    assert_eq!(order(&map), [1, 3, 0, 2]);
    map.sort(SortKeys::Number(&keys), 4, true, None);
    assert_eq!(order(&map), [0, 2, 1, 3]);
}

#[test]
fn view_row_inverts_plugin_row_and_counts_agree_without_a_tree() {
    // ViewRowInvertsPluginRow, Counts_AreEqualWithoutATree
    let mut map = ViewMap::default();
    map.sort(SortKeys::Number(&[5.0, 3.0, 9.0, 1.0]), 4, false, None);
    for view in 0..map.view_count() as i32 {
        assert_eq!(map.view_row(map.plugin_row(view)), view);
    }
    let map = identity(3);
    assert_eq!((map.view_count(), map.plugin_count()), (3, 3));
}

#[test]
fn a_click_selects_toggles_and_ranges() {
    // Click_Plain_SelectsOnlyThatRow, Click_Add_TogglesTheRow
    assert_eq!(click(&[0, 3], 2, false, false, -1, &identity(4)), [2]);
    assert_eq!(click(&[3, 0], 2, true, false, -1, &identity(4)), [0, 2, 3]);
    assert_eq!(click(&[3, 0], 3, true, false, -1, &identity(4)), [0]);
    // Click_Range_RunsInViewOrderBothWays
    assert_eq!(click(&[3], 0, false, true, 1, &sorted()), [0, 1, 2]);
    assert_eq!(click(&[], 0, false, true, 3, &sorted()), [0, 3]);
    // Click_AddRange_UnionsWithCurrent
    assert_eq!(click(&[3], 0, true, true, 2, &sorted()), [0, 2, 3]);
    // Click_RangeWithoutAnchor_ActsAsClickWithoutShift
    for anchor in [-1, 9] {
        assert_eq!(click(&[3], 1, false, true, anchor, &identity(4)), [1]);
        assert_eq!(click(&[3], 1, true, true, anchor, &identity(4)), [1, 3]);
    }
    // Click_DropsStaleAndDuplicateRows
    assert_eq!(
        click(&[2, 2, 7, -1], 1, true, false, -1, &identity(4)),
        [1, 2]
    );
}

#[test]
fn only_a_range_click_with_a_valid_anchor_keeps_it() {
    // MovesAnchor_OnlyARangeClickWithAValidAnchorKeepsIt
    assert!(!moves_anchor(true, 1, 4));
    assert!(moves_anchor(true, -1, 4));
    assert!(moves_anchor(true, 4, 4));
    assert!(moves_anchor(false, 1, 4));
}

#[test]
fn ranges_all_and_steps_follow_the_view() {
    // Range_InvalidEnds, All_IsEveryShownRowAscending, Step_MovesInViewOrderAndClamps
    let map = sorted();
    assert_eq!(range(-1, 2, &map), [2]);
    assert!(range(1, 9, &map).is_empty());
    assert_eq!(all(&identity(3)), [0, 1, 2]);
    assert!(all(&identity(0)).is_empty());
    assert_eq!(all(&sorted()), [0, 1, 2, 3]);
    assert_eq!(step(2, 1, &map), 0);
    assert_eq!(step(1, -1, &map), 1);
    assert_eq!(step(3, 1, &map), 3);
    assert_eq!(step(-1, 1, &map), 1);
    assert_eq!(step(8, -1, &map), 1);
    assert_eq!(step(0, 1, &identity(0)), -1);
}

/// A, A1, A2, A2a, B, B1.
const LEVELS: [i32; 6] = [0, 1, 1, 2, 0, 1];

fn shape(levels: &[i32], count: usize) -> TreeShape {
    let mut shape = TreeShape::default();
    shape.load(levels, count);
    shape
}

#[test]
fn loading_clamps_levels_and_derives_the_tree() {
    // Load_ClampsLevels, Load_DerivesParentsChildrenAndSubtrees, Load_ClearsCollapsedFlags
    let s = shape(&[2, 3, 1, -4, 9], 6);
    assert_eq!(
        (0..6).map(|r| s.level(r)).collect::<Vec<_>>(),
        [0, 1, 1, 0, 1, 0]
    );
    let mut s = shape(&LEVELS, 6);
    assert_eq!(
        (0..6).map(|r| s.parent(r)).collect::<Vec<_>>(),
        [-1, 0, 0, 2, -1, 4]
    );
    assert_eq!(
        (0..6).map(|r| s.has_children(r)).collect::<Vec<_>>(),
        [true, false, true, false, true, false]
    );
    assert_eq!(s.subtree_end(0), 4);
    assert_eq!(s.subtree_end(2), 4);
    assert_eq!(s.subtree_end(4), 6);
    assert_eq!(s.subtree_end(1), 2);
    s.set_collapsed(0, true);
    s.load(&LEVELS, 6);
    assert!(!s.is_collapsed(0));
}

#[test]
fn identity_leaves_out_folded_descendants() {
    // Identity_LeavesOutCollapsedDescendants
    let mut s = shape(&LEVELS, 6);
    let mut map = ViewMap::default();
    map.set_identity(6, Some(&s));
    assert_eq!(order(&map), [0, 1, 2, 3, 4, 5]);
    s.set_collapsed(0, true);
    map.set_identity(6, Some(&s));
    assert_eq!(order(&map), [0, 4, 5]);
    assert_eq!((map.view_count(), map.plugin_count()), (3, 6));
    assert_eq!(map.view_row(1), -1);
    assert_eq!(map.view_row(3), -1);
    assert_eq!(map.view_row(4), 1);
    s.set_collapsed(0, false);
    s.set_collapsed(2, true);
    map.set_identity(6, Some(&s));
    assert_eq!(order(&map), [0, 1, 2, 4, 5]);
}

#[test]
fn a_tree_sort_sorts_siblings_under_their_parent() {
    // Sort_SortsSiblingsAndKeepsChildrenUnderTheirParent, Sort_RespectsCollapsedRows
    let names = texts(&[
        Some("A"),
        Some("A1"),
        Some("A2"),
        Some("A2a"),
        Some("B"),
        Some("B1"),
    ]);
    let mut map = ViewMap::default();
    map.sort(SortKeys::Text(&names), 6, true, Some(&shape(&LEVELS, 6)));
    assert_eq!(order(&map), [4, 5, 0, 2, 3, 1]);
    map.sort(SortKeys::Text(&names), 6, false, Some(&shape(&LEVELS, 6)));
    assert_eq!(order(&map), [0, 1, 2, 3, 4, 5]);
    let mut folded = shape(&LEVELS, 6);
    folded.set_collapsed(4, true);
    map.sort(SortKeys::Text(&names), 6, true, Some(&folded));
    assert_eq!(order(&map), [4, 0, 2, 3, 1]);
    assert_eq!(map.view_row(5), -1);
}

#[test]
fn a_tree_sort_is_stable_with_missing_keys_last() {
    // Sort_IsStableAndPutsMissingKeysLastWithinSiblings
    let mut map = ViewMap::default();
    map.sort(
        SortKeys::Number(&[0.0, f64::NAN, 5.0, 5.0]),
        4,
        true,
        Some(&shape(&[0, 1, 1, 1], 4)),
    );
    assert_eq!(order(&map), [0, 2, 3, 1]);
}

#[test]
fn a_deep_chain_arranges_without_recursion() {
    // DeepChain_ArrangesWithoutRecursion
    let levels: Vec<i32> = (0..2000).collect();
    let mut map = ViewMap::default();
    map.set_identity(2000, Some(&shape(&levels, 2000)));
    assert_eq!(order(&map), (0..2000).collect::<Vec<_>>());
}
