//! Orderings the planner's results depend on, kept exactly as OpenAC's
//! runtime orders them so routes come out the same: the priority queue the
//! search expands nodes from, the sort that ranks candidates, the order a
//! set of nodes is walked in, and how a number is written into a reason.
//!
//! Ties are where these matter. A* expands the first of several nodes of
//! equal priority, and a ranked list keeps equal candidates in whatever
//! order its sort leaves them, so a different heap or sort plans a
//! different (equally good) route.

use std::cmp::Ordering;
use std::collections::HashSet;

/// Compares two floats as a total order: a NaN equals a NaN and sorts below
/// every number.
#[must_use]
pub fn compare_f32(a: f32, b: f32) -> Ordering {
    if a < b {
        Ordering::Less
    } else if a > b {
        Ordering::Greater
    } else if a == b {
        Ordering::Equal
    } else if a.is_nan() {
        if b.is_nan() {
            Ordering::Equal
        } else {
            Ordering::Less
        }
    } else {
        Ordering::Greater
    }
}

/// A min-priority queue of elements, a heap with four children per node.
/// Of elements of equal priority the one it hands out first is fixed by
/// the order they went in, as the queue OpenAC searches with.
#[derive(Debug, Clone)]
pub struct QuaternaryHeap<E: Copy> {
    nodes: Vec<(E, f32)>,
}

impl<E: Copy> Default for QuaternaryHeap<E> {
    fn default() -> Self {
        Self { nodes: Vec::new() }
    }
}

impl<E: Copy> QuaternaryHeap<E> {
    const ARITY: usize = 4;

    /// How many elements are queued.
    #[must_use]
    pub fn len(&self) -> usize {
        self.nodes.len()
    }

    /// Whether nothing is queued.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.nodes.is_empty()
    }

    /// Queues an element at a priority.
    pub fn enqueue(&mut self, element: E, priority: f32) {
        self.nodes.push((element, priority));
        let last = self.nodes.len() - 1;
        self.move_up((element, priority), last);
    }

    /// The element of least priority, removed, with its priority.
    pub fn dequeue(&mut self) -> Option<(E, f32)> {
        let root = *self.nodes.first()?;
        let last = self.nodes.len() - 1;
        if last > 0 {
            let last_node = self.nodes[last];
            self.nodes.truncate(last);
            self.move_down(last_node, 0);
        } else {
            self.nodes.truncate(last);
        }
        Some(root)
    }

    fn move_up(&mut self, node: (E, f32), mut index: usize) {
        while index > 0 {
            let parent_index = (index - 1) >> 2;
            let parent = self.nodes[parent_index];
            if compare_f32(node.1, parent.1) == Ordering::Less {
                self.nodes[index] = parent;
                index = parent_index;
            } else {
                break;
            }
        }
        self.nodes[index] = node;
    }

    fn move_down(&mut self, node: (E, f32), mut index: usize) {
        let size = self.nodes.len();
        loop {
            let mut i = (index << 2) + 1;
            if i >= size {
                break;
            }
            let mut min_child = self.nodes[i];
            let mut min_child_index = i;
            let upper = (i + Self::ARITY).min(size);
            i += 1;
            while i < upper {
                let next = self.nodes[i];
                if compare_f32(next.1, min_child.1) == Ordering::Less {
                    min_child = next;
                    min_child_index = i;
                }
                i += 1;
            }
            if compare_f32(node.1, min_child.1) != Ordering::Greater {
                break;
            }
            self.nodes[index] = min_child;
            index = min_child_index;
        }
        self.nodes[index] = node;
    }
}

/// Sorts a slice in place as OpenAC's runtime sorts a list with a
/// comparison: an introspective sort (quicksort on a median-of-three pivot,
/// insertion sort for 16 or fewer, heapsort past the depth limit). It is not
/// stable; this is the order it leaves ties in.
pub fn intro_sort<T: Copy>(keys: &mut [T], mut compare: impl FnMut(&T, &T) -> Ordering) {
    if keys.len() > 1 {
        let depth = 2 * (usize::BITS - 1 - keys.len().leading_zeros()) as i32 + 2;
        intro_sort_range(keys, depth, &mut compare);
    }
}

const INTROSORT_SIZE_THRESHOLD: usize = 16;

fn intro_sort_range<T: Copy>(
    keys: &mut [T],
    mut depth_limit: i32,
    compare: &mut impl FnMut(&T, &T) -> Ordering,
) {
    let mut partition_size = keys.len();
    while partition_size > 1 {
        if partition_size <= INTROSORT_SIZE_THRESHOLD {
            if partition_size == 2 {
                swap_if_greater(keys, compare, 0, 1);
                return;
            }
            if partition_size == 3 {
                swap_if_greater(keys, compare, 0, 1);
                swap_if_greater(keys, compare, 0, 2);
                swap_if_greater(keys, compare, 1, 2);
                return;
            }
            insertion_sort(&mut keys[..partition_size], compare);
            return;
        }
        if depth_limit == 0 {
            heap_sort(&mut keys[..partition_size], compare);
            return;
        }
        depth_limit -= 1;
        let pivot = pick_pivot_and_partition(&mut keys[..partition_size], compare);
        intro_sort_range(&mut keys[pivot + 1..partition_size], depth_limit, compare);
        partition_size = pivot;
    }
}

fn swap_if_greater<T: Copy>(
    keys: &mut [T],
    compare: &mut impl FnMut(&T, &T) -> Ordering,
    i: usize,
    j: usize,
) {
    if compare(&keys[i], &keys[j]) == Ordering::Greater {
        keys.swap(i, j);
    }
}

fn pick_pivot_and_partition<T: Copy>(
    keys: &mut [T],
    compare: &mut impl FnMut(&T, &T) -> Ordering,
) -> usize {
    let hi = keys.len() - 1;
    let middle = hi >> 1;
    swap_if_greater(keys, compare, 0, middle);
    swap_if_greater(keys, compare, 0, hi);
    swap_if_greater(keys, compare, middle, hi);
    let pivot = keys[middle];
    keys.swap(middle, hi - 1);
    let mut left = 0;
    let mut right = hi - 1;
    while left < right {
        loop {
            left += 1;
            if compare(&keys[left], &pivot) != Ordering::Less {
                break;
            }
        }
        loop {
            right -= 1;
            if compare(&pivot, &keys[right]) != Ordering::Less {
                break;
            }
        }
        if left >= right {
            break;
        }
        keys.swap(left, right);
    }
    if left != hi - 1 {
        keys.swap(left, hi - 1);
    }
    left
}

fn insertion_sort<T: Copy>(keys: &mut [T], compare: &mut impl FnMut(&T, &T) -> Ordering) {
    for i in 0..keys.len() - 1 {
        let t = keys[i + 1];
        let mut j = i as isize;
        while j >= 0 && compare(&t, &keys[j as usize]) == Ordering::Less {
            keys[(j + 1) as usize] = keys[j as usize];
            j -= 1;
        }
        keys[(j + 1) as usize] = t;
    }
}

fn heap_sort<T: Copy>(keys: &mut [T], compare: &mut impl FnMut(&T, &T) -> Ordering) {
    let n = keys.len();
    let mut i = n >> 1;
    while i >= 1 {
        down_heap(keys, i, n, compare);
        i -= 1;
    }
    let mut i = n;
    while i > 1 {
        keys.swap(0, i - 1);
        down_heap(keys, 1, i - 1, compare);
        i -= 1;
    }
}

fn down_heap<T: Copy>(
    keys: &mut [T],
    mut i: usize,
    n: usize,
    compare: &mut impl FnMut(&T, &T) -> Ordering,
) {
    let d = keys[i - 1];
    while i <= n >> 1 {
        let mut child = 2 * i;
        if child < n && compare(&keys[child - 1], &keys[child]) == Ordering::Less {
            child += 1;
        }
        if compare(&d, &keys[child - 1]) != Ordering::Less {
            break;
        }
        keys[i - 1] = keys[child - 1];
        i = child;
    }
    keys[i - 1] = d;
}

/// A set of node indices that walks its members in the order they were
/// first added, as OpenAC's set of nodes does when nothing is removed.
#[derive(Debug, Clone, Default)]
pub struct InsertionSet {
    order: Vec<i32>,
    members: HashSet<i32>,
}

impl InsertionSet {
    /// An empty set.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds a member; false when it was already there.
    pub fn insert(&mut self, value: i32) -> bool {
        if self.members.insert(value) {
            self.order.push(value);
            true
        } else {
            false
        }
    }

    /// Whether a value is a member.
    #[must_use]
    pub fn contains(&self, value: i32) -> bool {
        self.members.contains(&value)
    }

    /// How many members there are.
    #[must_use]
    pub fn len(&self) -> usize {
        self.order.len()
    }

    /// Whether there are none.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.order.is_empty()
    }

    /// The members, in the order they were added.
    pub fn iter(&self) -> impl Iterator<Item = i32> + '_ {
        self.order.iter().copied()
    }
}

/// A number written with exactly `decimals` decimals, rounding halves away
/// from zero (the `0.0` pattern of OpenAC's reasons).
#[must_use]
pub fn fixed(value: f32, decimals: u32) -> String {
    let value = f64::from(value);
    if !value.is_finite() {
        return non_finite(value);
    }
    let scale = 10f64.powi(decimals as i32);
    let rounded = (value * scale).round() / scale;
    let text = format!("{rounded:.prec$}", prec = decimals as usize);
    negative_zero(text)
}

/// A number written with at most one decimal, dropping a trailing zero (the
/// `0.#` pattern).
#[must_use]
pub fn at_most_one_decimal(value: f32) -> String {
    let text = fixed(value, 1);
    match text.strip_suffix(".0") {
        Some(whole) => negative_zero(whole.to_owned()),
        None => text,
    }
}

/// A number with a sign always written and two decimals (the
/// `+0.00;-0.00` pattern).
#[must_use]
pub fn signed_two_decimals(value: f32) -> String {
    let text = fixed(value.abs(), 2);
    if value < 0.0 {
        format!("-{text}")
    } else {
        format!("+{text}")
    }
}

fn non_finite(value: f64) -> String {
    if value.is_nan() {
        "NaN".to_owned()
    } else if value > 0.0 {
        "∞".to_owned()
    } else {
        "-∞".to_owned()
    }
}

fn negative_zero(text: String) -> String {
    if text.starts_with('-') && text[1..].chars().all(|c| c == '0' || c == '.') {
        text[1..].to_owned()
    } else {
        text
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_heap_hands_out_the_least_priority_first() {
        let mut heap = QuaternaryHeap::default();
        for (element, priority) in [(1, 5.0), (2, 1.0), (3, 3.0), (4, 1.0), (5, 0.5), (6, 9.0)] {
            heap.enqueue(element, priority);
        }
        let mut order = Vec::new();
        while let Some((element, _)) = heap.dequeue() {
            order.push(element);
        }
        assert_eq!(order[0], 5);
        assert_eq!(order[3], 3);
        assert_eq!(order[5], 6);
        assert!(heap.is_empty());
    }

    #[test]
    fn the_sort_orders_long_and_short_lists() {
        let mut long: Vec<f32> = (0..200).map(|i| ((i * 7919) % 211) as f32).collect();
        intro_sort(&mut long, |a, b| compare_f32(*a, *b));
        assert!(long.windows(2).all(|w| w[0] <= w[1]));
        let mut short = [3.0f32, 1.0, 2.0];
        intro_sort(&mut short, |a, b| compare_f32(*a, *b));
        assert_eq!(short, [1.0, 2.0, 3.0]);
    }

    #[test]
    fn numbers_are_written_as_the_reasons_write_them() {
        assert_eq!(fixed(2.25, 1), "2.3");
        assert_eq!(fixed(0.5, 0), "1");
        assert_eq!(fixed(-0.04, 1), "0.0");
        assert_eq!(at_most_one_decimal(2.5), "2.5");
        assert_eq!(at_most_one_decimal(3.0), "3");
        assert_eq!(signed_two_decimals(0.123), "+0.12");
        assert_eq!(signed_two_decimals(-0.5), "-0.50");
    }
}
