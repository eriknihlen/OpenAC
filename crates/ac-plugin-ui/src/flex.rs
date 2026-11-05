//! Flex layout for plugin markup (OpenAC `UI/Layout/Flex`): a reduced CSS
//! flexbox — direction, gap, padding, justify, align, wrap, grow, shrink,
//! basis, min/max and align-self, plus unbounded scrolling axes. Pure: it
//! reads and writes the nodes of a [`FlexTree`] and knows nothing of the UI.
//!
//! [`FlexTree::measure`] and [`FlexTree::arrange`] each measure the whole
//! tree bottom-up; arrange then places every node top-down. A leaf carries
//! its content's measurement ([`FlexNode::measure`]); markup controls do not
//! wrap, so it is asked without a width.
//!
//! Sizing is height-for-width at any depth. A node whose extent along one
//! axis depends on the size it gets along the other (a wrapping container,
//! or any container holding one, that does not scroll) is size-dependent:
//! its parent finds that extent by laying the node's subtree out at the size
//! it gives it, rather than trusting a measurement made before any size was
//! known. Greedy line breaking is not monotonic, so a root wrapping
//! container's minimum cross size is a close estimate ([`FlexTree::fit_minimum`]
//! grows it until the content fits).

/// The main axis of a container.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum FlexDirection {
    /// Left to right.
    #[default]
    Row,
    /// Top to bottom.
    Column,
}

/// Where a line's items sit along the main axis when space is left.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum FlexJustify {
    /// At the start.
    #[default]
    Start,
    /// Centred.
    Center,
    /// At the end.
    End,
    /// Spread with the space between.
    SpaceBetween,
}

/// Where an item sits across its line.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum FlexAlign {
    /// At the start.
    Start,
    /// Centred.
    Center,
    /// At the end.
    End,
    /// Across the whole line.
    #[default]
    Stretch,
}

/// A size in points.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct FlexSize {
    /// Width.
    pub width: f32,
    /// Height.
    pub height: f32,
}

impl FlexSize {
    /// A size.
    #[must_use]
    pub const fn new(width: f32, height: f32) -> Self {
        Self { width, height }
    }
}

/// A rectangle in points, relative to the parent node's top-left corner.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct FlexRect {
    /// Left.
    pub x: f32,
    /// Top.
    pub y: f32,
    /// Width.
    pub width: f32,
    /// Height.
    pub height: f32,
}

impl FlexRect {
    /// A rectangle.
    #[must_use]
    pub const fn new(x: f32, y: f32, width: f32, height: f32) -> Self {
        Self {
            x,
            y,
            width,
            height,
        }
    }
}

/// Space between a container's edge and its items, in points.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct FlexEdges {
    /// Top.
    pub top: f32,
    /// Right.
    pub right: f32,
    /// Bottom.
    pub bottom: f32,
    /// Left.
    pub left: f32,
}

impl FlexEdges {
    /// The same on every side.
    #[must_use]
    pub const fn all(v: f32) -> Self {
        Self {
            top: v,
            right: v,
            bottom: v,
            left: v,
        }
    }

    /// Left plus right.
    #[must_use]
    pub fn horizontal(self) -> f32 {
        self.left + self.right
    }

    /// Top plus bottom.
    #[must_use]
    pub fn vertical(self) -> f32 {
        self.top + self.bottom
    }
}

/// What a node needs: the size it would like and the smallest it can be
/// given without its content breaking (both with a container's padding).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct FlexMeasurement {
    /// The size it would like.
    pub preferred: FlexSize,
    /// The smallest it can be.
    pub minimum: FlexSize,
}

impl FlexMeasurement {
    /// A measurement.
    #[must_use]
    pub const fn new(preferred: FlexSize, minimum: FlexSize) -> Self {
        Self { preferred, minimum }
    }
}

/// One box: a container laying out its children, or a leaf with a content
/// measurement, and its own settings as an item of its parent.
#[derive(Debug, Clone)]
pub struct FlexNode {
    // ── Container settings.
    /// The main axis.
    pub direction: FlexDirection,
    /// Points between items on a line and between wrapped lines.
    pub gap: f32,
    /// Space inside the edges.
    pub padding: FlexEdges,
    /// Along the main axis.
    pub justify: FlexJustify,
    /// Across each line.
    pub align: FlexAlign,
    /// Items wrap onto new lines.
    pub wrap: bool,
    /// Scrolls horizontally: items lay out with no width limit.
    pub scroll_x: bool,
    /// Scrolls vertically: items lay out with no height limit.
    pub scroll_y: bool,
    /// The bars' thickness, taken from the viewport while shown (0: none).
    pub scrollbar_size: f32,
    /// The children, by index in the tree.
    pub children: Vec<usize>,
    // ── Item settings.
    /// Share of free space taken.
    pub grow: f32,
    /// Share of overflow given up.
    pub shrink: f32,
    /// The main-axis starting size (none: auto).
    pub basis: Option<f32>,
    /// Preferred width, over the measured one.
    pub width: Option<f32>,
    /// Preferred height, over the measured one.
    pub height: Option<f32>,
    /// None: the content minimum.
    pub min_width: Option<f32>,
    /// None: no limit.
    pub max_width: Option<f32>,
    /// None: the content minimum.
    pub min_height: Option<f32>,
    /// None: no limit.
    pub max_height: Option<f32>,
    /// None: the parent's align.
    pub align_self: Option<FlexAlign>,
    /// Takes no space and is not laid out.
    pub hidden: bool,
    /// A leaf's content measurement (none: a container).
    pub measure: Option<FlexMeasurement>,
    // ── Results.
    /// Where the last arrange put it, relative to its parent.
    pub rect: FlexRect,
    /// The last measurement, preferred clamped to the limits.
    pub measured: FlexMeasurement,
    /// After an arrange: how far the items reach, plus trailing padding.
    pub content_size: FlexSize,
    /// After an arrange of a scrolling container: it shows a horizontal bar.
    pub scrollbar_shown_x: bool,
    /// As [`Self::scrollbar_shown_x`], vertical.
    pub scrollbar_shown_y: bool,
    /// After an arrange: the part the items lay out in (less the bars).
    pub viewport: FlexSize,
    preferred_unclamped: FlexSize,
    minimum_size: FlexSize,
    maximum_size: FlexSize,
    size_dependent: bool,
    height_for_width: bool,
    scrolls_dependent_content: bool,
    line_ends: Vec<usize>,
    scratch: Vec<f32>,
}

impl Default for FlexNode {
    fn default() -> Self {
        Self {
            direction: FlexDirection::Row,
            gap: 0.0,
            padding: FlexEdges::default(),
            justify: FlexJustify::Start,
            align: FlexAlign::Stretch,
            wrap: false,
            scroll_x: false,
            scroll_y: false,
            scrollbar_size: 0.0,
            children: Vec::new(),
            grow: 0.0,
            shrink: 1.0,
            basis: None,
            width: None,
            height: None,
            min_width: None,
            max_width: None,
            min_height: None,
            max_height: None,
            align_self: None,
            hidden: false,
            measure: None,
            rect: FlexRect::default(),
            measured: FlexMeasurement::default(),
            content_size: FlexSize::default(),
            scrollbar_shown_x: false,
            scrollbar_shown_y: false,
            viewport: FlexSize::default(),
            preferred_unclamped: FlexSize::default(),
            minimum_size: FlexSize::default(),
            maximum_size: FlexSize::default(),
            size_dependent: false,
            height_for_width: false,
            scrolls_dependent_content: false,
            line_ends: Vec::new(),
            scratch: Vec::new(),
        }
    }
}

impl FlexNode {
    /// A leaf of content measurement `m`.
    #[must_use]
    pub fn leaf(m: FlexMeasurement) -> Self {
        Self {
            measure: Some(m),
            ..Self::default()
        }
    }

    /// A container laying out along `direction`.
    #[must_use]
    pub fn container(direction: FlexDirection) -> Self {
        Self {
            direction,
            ..Self::default()
        }
    }
}

/// Thickness of a scrollbar, reserved in a scrolling container's minimum.
pub const SCROLLBAR_THICKNESS: f32 = 16.0;
/// The smallest viewport a scrolling container shrinks to along an axis it
/// scrolls.
pub const MINIMUM_SCROLL_VIEWPORT: f32 = 40.0;

const TARGET: usize = 0;
const FROZEN: usize = 1;
const BASE: usize = 2;
const MIN: usize = 3;
const CROSS: usize = 4;
const CROSS_FLOOR: usize = 5;

/// Non-finite or negative computed values are defects; they read as 0.
fn positive(v: f32) -> f32 {
    if v.is_finite() && v > 0.0 { v } else { 0.0 }
}

/// .NET `Math.Clamp` for `min <= max`.
fn clamp(v: f32, min: f32, max: f32) -> f32 {
    if v < min {
        min
    } else if v > max {
        max
    } else {
        v
    }
}

fn main_of(s: FlexSize, row: bool) -> f32 {
    if row { s.width } else { s.height }
}

fn cross_of(s: FlexSize, row: bool) -> f32 {
    if row { s.height } else { s.width }
}

fn size(main: f32, cross: f32, row: bool) -> FlexSize {
    if row {
        FlexSize::new(main, cross)
    } else {
        FlexSize::new(cross, main)
    }
}

fn snap(v: f32) -> f32 {
    if v.is_finite() {
        (v + 0.5).floor()
    } else {
        0.0
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum LineSizes {
    Minimum,
    Hypothetical,
}

/// A layout's nodes, children by index.
#[derive(Debug, Clone, Default)]
pub struct FlexTree {
    /// Every node.
    pub nodes: Vec<FlexNode>,
}

/// The limits a node's own sizes put on a content measurement.
fn limits(node: &FlexNode, content: FlexMeasurement) -> (f32, f32, f32, f32, f32, f32) {
    let pref_w = positive(node.width.unwrap_or(content.preferred.width));
    let pref_h = positive(node.height.unwrap_or(content.preferred.height));
    let auto_min_w = match node.width {
        Some(w) => positive(w).min(positive(content.minimum.width)),
        None => positive(content.minimum.width),
    };
    let auto_min_h = match node.height {
        Some(h) => positive(h).min(positive(content.minimum.height)),
        None => positive(content.minimum.height),
    };
    let max_w = node.max_width.map_or(f32::MAX, positive);
    let max_h = node.max_height.map_or(f32::MAX, positive);
    let min_w = node.min_width.map_or(auto_min_w, positive).min(max_w);
    let min_h = node.min_height.map_or(auto_min_h, positive).min(max_h);
    (pref_w, pref_h, min_w, min_h, max_w, max_h)
}

/// What a leaf measured at `content` resolves to once its own sizes and
/// limits apply: two contents that resolve alike lay out alike.
#[must_use]
pub fn resolve_leaf(node: &FlexNode, content: FlexMeasurement) -> FlexMeasurement {
    let (pw, ph, nw, nh, xw, xh) = limits(node, content);
    FlexMeasurement::new(
        FlexSize::new(clamp(pw, nw, xw), clamp(ph, nh, xh)),
        FlexSize::new(nw, nh),
    )
}

impl FlexTree {
    /// Adds a node; its index.
    pub fn add(&mut self, node: FlexNode) -> usize {
        self.nodes.push(node);
        self.nodes.len() - 1
    }

    /// Adds `child` under `parent`; the child's index.
    pub fn add_child(&mut self, parent: usize, child: FlexNode) -> usize {
        let index = self.add(child);
        self.nodes[parent].children.push(index);
        index
    }

    /// Measures `root` and everything under it.
    pub fn measure(&mut self, root: usize) -> FlexMeasurement {
        self.measure_node(root);
        self.nodes[root].measured
    }

    /// Measures the tree, then lays it out with `root` at `bounds`.
    pub fn arrange(&mut self, root: usize, bounds: FlexRect) {
        self.measure_node(root);
        self.nodes[root].rect = bounds;
        self.arrange_children(
            root,
            Some(positive(bounds.width)),
            Some(positive(bounds.height)),
            true,
        );
    }

    /// The smallest whole-point size, from the measured minimum, at which
    /// `root`'s content fits; the tree is left arranged at it (OpenAC
    /// `FlexFit.Minimum`).
    pub fn fit_minimum(&mut self, root: usize) -> FlexSize {
        let measured = self.measure(root).minimum;
        let mut width = measured.width.ceil();
        let mut height = measured.height.ceil();
        for _ in 0..4 {
            self.arrange(root, FlexRect::new(0.0, 0.0, width, height));
            let n = &self.nodes[root];
            let bar = if n.scrollbar_size > 0.0 && n.scrollbar_size.is_finite() {
                n.scrollbar_size
            } else {
                0.0
            };
            let bar_w = if n.scrollbar_shown_y { bar } else { 0.0 };
            let bar_h = if n.scrollbar_shown_x { bar } else { 0.0 };
            let need_w = if n.scroll_x {
                width
            } else {
                (n.content_size.width + bar_w).ceil()
            };
            let need_h = if n.scroll_y {
                height
            } else {
                (n.content_size.height + bar_h).ceil()
            };
            if need_w <= width && need_h <= height {
                break;
            }
            width = width.max(need_w);
            height = height.max(need_h);
        }
        self.arrange(root, FlexRect::new(0.0, 0.0, width, height));
        FlexSize::new(width, height)
    }

    fn measure_node(&mut self, i: usize) {
        let content = match self.nodes[i].measure {
            Some(m) => m,
            None => self.measure_children(i),
        };
        let (pw, ph, nw, nh, xw, xh) = limits(&self.nodes[i], content);
        self.mark_size_dependent(i);
        let n = &mut self.nodes[i];
        let (mut floor_w, mut floor_h) = (nw, nh);
        if n.measure.is_none() && (n.wrap || n.size_dependent) {
            let height_depends = if n.size_dependent {
                n.height_for_width
            } else {
                n.direction == FlexDirection::Row
            };
            if height_depends {
                floor_h = n.min_height.map_or(0.0, positive).min(xh);
            } else {
                floor_w = n.min_width.map_or(0.0, positive).min(xw);
            }
        }
        n.preferred_unclamped = FlexSize::new(pw, ph);
        n.minimum_size = FlexSize::new(nw, nh);
        n.maximum_size = FlexSize::new(xw, xh);
        n.measured = FlexMeasurement::new(
            FlexSize::new(clamp(pw, floor_w, xw), clamp(ph, floor_h, xh)),
            FlexSize::new(nw, nh),
        );
    }

    fn mark_size_dependent(&mut self, i: usize) {
        let (dependent, height_for_width) = {
            let n = &self.nodes[i];
            if n.measure.is_some() {
                (false, false)
            } else if n.wrap {
                (true, n.direction == FlexDirection::Row)
            } else {
                let mut dependent = false;
                let mut hfw = false;
                for &c in &n.children {
                    let child = &self.nodes[c];
                    if child.hidden || !child.size_dependent {
                        continue;
                    }
                    dependent = true;
                    hfw |= child.height_for_width;
                }
                (dependent, hfw)
            }
        };
        let n = &mut self.nodes[i];
        n.size_dependent = false;
        n.height_for_width = false;
        n.scrolls_dependent_content = false;
        if n.measure.is_some() {
            return;
        }
        n.height_for_width = height_for_width;
        if n.scroll_x || n.scroll_y {
            n.scrolls_dependent_content = dependent;
        } else {
            n.size_dependent = dependent;
        }
    }

    fn measured_base(child: &FlexNode, row: bool) -> f32 {
        child
            .basis
            .map_or_else(|| main_of(child.preferred_unclamped, row), positive)
    }

    fn measure_children(&mut self, i: usize) -> FlexMeasurement {
        let row = self.nodes[i].direction == FlexDirection::Row;
        let gap = positive(self.nodes[i].gap);
        let children = self.nodes[i].children.clone();
        let (mut pref_main, mut pref_cross, mut min_main_sum, mut min_main_max, mut min_cross) =
            (0.0f32, 0.0f32, 0.0f32, 0.0f32, 0.0f32);
        let mut visible = 0;
        for &c in &children {
            if self.nodes[c].hidden {
                continue;
            }
            self.measure_node(c);
            let child = &self.nodes[c];
            let child_min_main = main_of(child.minimum_size, row);
            pref_main += clamp(
                Self::measured_base(child, row),
                child_min_main,
                child_min_main.max(main_of(child.maximum_size, row)),
            );
            pref_cross = pref_cross.max(cross_of(child.measured.preferred, row));
            min_main_sum += child_min_main;
            min_main_max = min_main_max.max(child_min_main);
            min_cross = min_cross.max(cross_of(child.minimum_size, row));
            visible += 1;
        }
        let gaps = if visible > 1 {
            gap * (visible - 1) as f32
        } else {
            0.0
        };
        pref_main += gaps;
        let min_main = if self.nodes[i].wrap && visible > 0 {
            min_cross = self.break_lines(i, row, min_main_max, gap, LineSizes::Minimum, false);
            min_main_max
        } else {
            min_main_sum + gaps
        };
        let n = &self.nodes[i];
        let p = n.padding;
        let pad_main = if row { p.horizontal() } else { p.vertical() };
        let pad_cross = if row { p.vertical() } else { p.horizontal() };
        let preferred = size(pref_main + pad_main, pref_cross + pad_cross, row);
        let minimum = size(min_main + pad_main, min_cross + pad_cross, row);
        let (mut min_w, mut min_h) = (minimum.width, minimum.height);
        if n.scroll_x {
            min_w = MINIMUM_SCROLL_VIEWPORT;
        }
        if n.scroll_y {
            min_h = MINIMUM_SCROLL_VIEWPORT;
        }
        if n.scroll_y {
            min_w += SCROLLBAR_THICKNESS;
        }
        if n.scroll_x {
            min_h += SCROLLBAR_THICKNESS;
        }
        FlexMeasurement::new(preferred, FlexSize::new(min_w, min_h))
    }

    fn arrange_children(
        &mut self,
        i: usize,
        width: Option<f32>,
        height: Option<f32>,
        descend: bool,
    ) {
        let n = &mut self.nodes[i];
        n.scrollbar_shown_x = false;
        n.scrollbar_shown_y = false;
        if n.measure.is_none()
            && (n.scroll_x || n.scroll_y)
            && positive(n.scrollbar_size) > 0.0
            && let (Some(w), Some(h)) = (width, height)
        {
            self.settle_scrollbars(i, w, h, descend);
            return;
        }
        n.viewport = FlexSize::new(width.unwrap_or(0.0), height.unwrap_or(0.0));
        self.arrange_content(i, width, height, descend);
    }

    fn settle_scrollbars(&mut self, i: usize, width: f32, height: f32, descend: bool) {
        let bar = positive(self.nodes[i].scrollbar_size);
        let (mut bar_x, mut bar_y) = (false, false);
        let (mut view_w, mut view_h) = (positive(width), positive(height));
        for _ in 0..3 {
            self.arrange_content(i, Some(view_w), Some(view_h), false);
            let n = &self.nodes[i];
            let need_x = bar_x || (n.scroll_x && n.content_size.width > view_w);
            let need_y = bar_y || (n.scroll_y && n.content_size.height > view_h);
            if need_x == bar_x && need_y == bar_y {
                break;
            }
            bar_x = need_x;
            bar_y = need_y;
            view_w = positive(width - if bar_y { bar } else { 0.0 });
            view_h = positive(height - if bar_x { bar } else { 0.0 });
        }
        self.arrange_content(i, Some(view_w), Some(view_h), descend);
        let n = &mut self.nodes[i];
        n.scrollbar_shown_x = bar_x;
        n.scrollbar_shown_y = bar_y;
        n.viewport = FlexSize::new(view_w, view_h);
    }

    fn arrange_content(
        &mut self,
        i: usize,
        width: Option<f32>,
        height: Option<f32>,
        descend: bool,
    ) {
        if self.nodes[i].measure.is_some() {
            self.nodes[i].content_size = FlexSize::new(width.unwrap_or(0.0), height.unwrap_or(0.0));
            return;
        }
        let (row, p, gap, wrap) = {
            let n = &self.nodes[i];
            (
                n.direction == FlexDirection::Row,
                n.padding,
                positive(n.gap),
                n.wrap,
            )
        };
        let (main_scrolls, cross_scrolls) = {
            let n = &self.nodes[i];
            if row {
                (n.scroll_x, n.scroll_y)
            } else {
                (n.scroll_y, n.scroll_x)
            }
        };
        let (main_size, cross_size) = if row {
            (width, height)
        } else {
            (height, width)
        };
        let available_main = match main_size {
            Some(ms) if !main_scrolls => Some(positive(
                ms - if row { p.horizontal() } else { p.vertical() },
            )),
            _ => None,
        };
        let available_cross = match cross_size {
            Some(cs) if !cross_scrolls => Some(positive(
                cs - if row { p.vertical() } else { p.horizontal() },
            )),
            _ => None,
        };
        let origin_main = if row { p.left } else { p.top };
        let origin_cross = if row { p.top } else { p.left };
        let single_line = !wrap || available_main.is_none();
        let count = self.nodes[i].children.len();
        if self.nodes[i].scratch.len() < count * 6 {
            self.nodes[i].scratch = vec![0.0; count * 6];
        }
        self.resolve_bases(i, row, count, available_cross, single_line);
        self.nodes[i].line_ends.clear();
        match available_main {
            Some(available) if wrap => {
                self.break_lines(i, row, available, gap, LineSizes::Hypothetical, true);
            }
            _ => self.nodes[i].line_ends.push(count),
        }
        let ends = self.nodes[i].line_ends.clone();
        let line_count = ends.len();
        let mut lines_cross = 0.0;
        let mut start = 0;
        for &end in &ends {
            self.resolve_flexible_lengths(i, row, count, start, end, available_main, gap);
            self.resolve_along_cross(i, row, count, start, end);
            lines_cross += self.line_cross(i, start, end);
            start = end;
        }
        let line_gaps = if line_count > 1 {
            gap * (line_count - 1) as f32
        } else {
            0.0
        };
        let mut extra_per_line = 0.0;
        if let Some(cross_space) = available_cross
            && line_count > 0
        {
            extra_per_line = if single_line {
                cross_space - lines_cross
            } else {
                ((cross_space - lines_cross - line_gaps) / line_count as f32).max(0.0)
            };
        }
        let (mut max_main_edge, mut max_cross_edge) = (origin_main, origin_cross);
        let mut cross_cursor = origin_cross;
        start = 0;
        for &end in &ends {
            let line_cross = (self.line_cross(i, start, end) + extra_per_line).max(0.0);
            self.place_line(
                i,
                row,
                count,
                start,
                end,
                available_main,
                gap,
                origin_main,
                cross_cursor,
                line_cross,
            );
            for k in start..end {
                let c = self.nodes[i].children[k];
                let child = &self.nodes[c];
                if child.hidden {
                    continue;
                }
                let r = child.rect;
                max_main_edge = max_main_edge.max(if row { r.x + r.width } else { r.y + r.height });
                max_cross_edge =
                    max_cross_edge.max(if row { r.y + r.height } else { r.x + r.width });
            }
            cross_cursor += line_cross + gap;
            start = end;
        }
        if descend {
            for k in 0..count {
                let c = self.nodes[i].children[k];
                if self.nodes[c].hidden {
                    self.nodes[c].rect = FlexRect::default();
                    continue;
                }
                let r = self.nodes[c].rect;
                self.arrange_children(c, Some(r.width), Some(r.height), true);
            }
        }
        let trail_main = if row { p.right } else { p.bottom };
        let trail_cross = if row { p.bottom } else { p.right };
        self.nodes[i].content_size = size(
            max_main_edge + trail_main,
            max_cross_edge + trail_cross,
            row,
        );
    }

    fn is_sized_across(&self, parent: usize, child: usize) -> bool {
        let c = &self.nodes[child];
        c.size_dependent
            && c.height_for_width == (self.nodes[parent].direction == FlexDirection::Column)
    }

    fn is_scrolled_across(&self, parent: usize, child: usize) -> bool {
        let c = &self.nodes[child];
        if !c.scrolls_dependent_content {
            return false;
        }
        let column = self.nodes[parent].direction == FlexDirection::Column;
        if c.height_for_width != column {
            return false;
        }
        if column {
            c.scroll_y && !c.scroll_x
        } else {
            c.scroll_x && !c.scroll_y
        }
    }

    fn is_laid_across(&self, parent: usize, child: usize) -> bool {
        self.is_sized_across(parent, child) || self.is_scrolled_across(parent, child)
    }

    fn is_sized_along(&self, parent: usize, child: usize) -> bool {
        let c = &self.nodes[child];
        c.size_dependent
            && c.height_for_width == (self.nodes[parent].direction == FlexDirection::Row)
    }

    fn resolve_bases(
        &mut self,
        i: usize,
        row: bool,
        count: usize,
        available_cross: Option<f32>,
        single_line: bool,
    ) {
        for k in 0..count {
            let c = self.nodes[i].children[k];
            if self.nodes[c].hidden {
                continue;
            }
            if !self.is_laid_across(i, c) {
                let child = &self.nodes[c];
                let (b, m, cr, cf) = (
                    Self::measured_base(child, row),
                    main_of(child.minimum_size, row),
                    cross_of(child.measured.preferred, row),
                    cross_of(child.minimum_size, row),
                );
                let s = &mut self.nodes[i].scratch;
                s[BASE * count + k] = b;
                s[MIN * count + k] = m;
                s[CROSS * count + k] = cr;
                s[CROSS_FLOOR * count + k] = cf;
                continue;
            }
            let (explicit_cross, min_cross, max_cross, align, pref_cross) = {
                let child = &self.nodes[c];
                (
                    if row { child.height } else { child.width },
                    cross_of(child.minimum_size, row),
                    cross_of(child.maximum_size, row),
                    child.align_self.unwrap_or(self.nodes[i].align),
                    cross_of(child.measured.preferred, row),
                )
            };
            let stretched = single_line && align == FlexAlign::Stretch && explicit_cross.is_none();
            let mut cross = pref_cross;
            if let Some(space) = available_cross {
                cross = if stretched { space } else { cross.min(space) };
            }
            let cross = clamp(cross, min_cross, min_cross.max(max_cross));
            {
                let s = &mut self.nodes[i].scratch;
                s[CROSS * count + k] = cross;
                s[CROSS_FLOOR * count + k] = cross;
            }
            let extent = self.extent(c, !row, cross);
            let child = &self.nodes[c];
            let explicit_main = if row { child.width } else { child.height };
            let explicit_min = if row {
                child.min_width
            } else {
                child.min_height
            };
            let max_main = main_of(child.maximum_size, row);
            let content_min = if child.size_dependent {
                extent
            } else {
                main_of(child.minimum_size, row)
            };
            let auto_min = explicit_main.map_or(content_min, |e| positive(e).min(content_min));
            let base = child
                .basis
                .map(positive)
                .or_else(|| explicit_main.map(positive))
                .unwrap_or(extent);
            let min = explicit_min.map_or(auto_min, positive).min(max_main);
            let s = &mut self.nodes[i].scratch;
            s[BASE * count + k] = base;
            s[MIN * count + k] = min;
        }
    }

    fn extent(&mut self, node: usize, width_given: bool, given: f32) -> f32 {
        let low = positive(given).floor();
        let high = positive(given).ceil();
        let extent = self.laid_out_extent(node, width_given, low);
        if (high - low).abs() < f32::EPSILON {
            extent
        } else {
            extent.max(self.laid_out_extent(node, width_given, high))
        }
    }

    fn laid_out_extent(&mut self, node: usize, width_given: bool, size: f32) -> f32 {
        if width_given {
            self.arrange_children(node, Some(size), None, false);
            self.nodes[node].content_size.height
        } else {
            self.arrange_children(node, None, Some(size), false);
            self.nodes[node].content_size.width
        }
    }

    fn hypothetical(&self, i: usize, c: usize, row: bool, count: usize, k: usize) -> f32 {
        let s = &self.nodes[i].scratch;
        let min = s[MIN * count + k];
        clamp(
            s[BASE * count + k],
            min,
            min.max(main_of(self.nodes[c].maximum_size, row)),
        )
    }

    /// The one greedy line breaker both passes share; returns the lines'
    /// cross sizes plus the gaps between them.
    fn break_lines(
        &mut self,
        i: usize,
        row: bool,
        available: f32,
        gap: f32,
        sizes: LineSizes,
        record: bool,
    ) -> f32 {
        let count = self.nodes[i].children.len();
        let minimum = sizes == LineSizes::Minimum;
        let (mut total, mut line_main, mut line_cross) = (0.0f32, 0.0f32, 0.0f32);
        let (mut lines, mut in_line) = (0usize, 0usize);
        let mut ends = Vec::new();
        for k in 0..count {
            let c = self.nodes[i].children[k];
            if self.nodes[c].hidden {
                continue;
            }
            let main = if minimum {
                main_of(self.nodes[c].minimum_size, row)
            } else {
                self.hypothetical(i, c, row, count, k)
            };
            let cross = if minimum {
                cross_of(self.nodes[c].minimum_size, row)
            } else {
                self.nodes[i].scratch[CROSS * count + k]
            };
            if in_line > 0 && line_main + gap + main > available {
                ends.push(k);
                total += line_cross;
                lines += 1;
                line_main = 0.0;
                line_cross = 0.0;
                in_line = 0;
            }
            line_main += (if in_line > 0 { gap } else { 0.0 }) + main;
            line_cross = line_cross.max(cross);
            in_line += 1;
        }
        if in_line > 0 {
            total += line_cross;
            lines += 1;
        }
        ends.push(count);
        if record {
            self.nodes[i].line_ends = ends;
        }
        total
            + if lines > 1 {
                gap * (lines - 1) as f32
            } else {
                0.0
            }
    }

    #[allow(clippy::too_many_arguments)]
    fn resolve_flexible_lengths(
        &mut self,
        i: usize,
        row: bool,
        count: usize,
        start: usize,
        end: usize,
        available_main: Option<f32>,
        gap: f32,
    ) {
        let children = self.nodes[i].children.clone();
        let mut visible = 0;
        let mut hypothetical_sum = 0.0;
        for (k, &c) in children.iter().enumerate().take(end).skip(start) {
            if self.nodes[c].hidden {
                continue;
            }
            let h = self.hypothetical(i, c, row, count, k);
            self.nodes[i].scratch[TARGET * count + k] = h;
            hypothetical_sum += h;
            visible += 1;
        }
        let Some(available) = available_main else {
            return;
        };
        if visible == 0 {
            return;
        }
        let gaps = if visible > 1 {
            gap * (visible - 1) as f32
        } else {
            0.0
        };
        let growing = available - gaps - hypothetical_sum > 0.0;
        for k in start..end {
            let c = children[k];
            if self.nodes[c].hidden {
                continue;
            }
            let child = &self.nodes[c];
            let factor = if growing {
                positive(child.grow)
            } else {
                positive(child.shrink)
            };
            let s = &mut self.nodes[i].scratch;
            let base = s[BASE * count + k];
            let target = s[TARGET * count + k];
            let frozen = factor == 0.0 || (growing && base > target) || (!growing && base < target);
            s[FROZEN * count + k] = if frozen { 1.0 } else { 0.0 };
        }
        let mut initial_free = 0.0;
        for pass in 0..=visible {
            let (mut used, mut factor_sum, mut scaled_shrink_sum) = (gaps, 0.0f32, 0.0f32);
            for k in start..end {
                let c = children[k];
                let child = &self.nodes[c];
                if child.hidden {
                    continue;
                }
                let s = &self.nodes[i].scratch;
                if s[FROZEN * count + k] != 0.0 {
                    used += s[TARGET * count + k];
                    continue;
                }
                used += s[BASE * count + k];
                factor_sum += if growing {
                    positive(child.grow)
                } else {
                    positive(child.shrink)
                };
                scaled_shrink_sum += positive(child.shrink) * s[BASE * count + k];
            }
            if factor_sum == 0.0 {
                break;
            }
            let mut free = available - used;
            if pass == 0 {
                initial_free = free;
            }
            if factor_sum < 1.0 && (initial_free * factor_sum).abs() < free.abs() {
                free = initial_free * factor_sum;
            }
            let mut violation = 0.0;
            for k in start..end {
                let c = children[k];
                let (hidden, grow, shrink, max_main) = {
                    let child = &self.nodes[c];
                    (
                        child.hidden,
                        positive(child.grow),
                        positive(child.shrink),
                        main_of(child.maximum_size, row),
                    )
                };
                let s = &mut self.nodes[i].scratch;
                if hidden || s[FROZEN * count + k] != 0.0 {
                    continue;
                }
                let base = s[BASE * count + k];
                let flexed = if growing {
                    base + free * grow / factor_sum
                } else if scaled_shrink_sum > 0.0 {
                    base + free * shrink * base / scaled_shrink_sum
                } else {
                    base
                };
                let min = s[MIN * count + k];
                let clamped = clamp(flexed, min, min.max(max_main));
                violation += clamped - flexed;
                s[TARGET * count + k] = clamped;
            }
            if violation == 0.0 {
                break;
            }
            for k in start..end {
                let c = children[k];
                let (hidden, max_main) = {
                    let child = &self.nodes[c];
                    (child.hidden, main_of(child.maximum_size, row))
                };
                let s = &mut self.nodes[i].scratch;
                if hidden || s[FROZEN * count + k] != 0.0 {
                    continue;
                }
                let at_min = s[TARGET * count + k] <= s[MIN * count + k];
                let at_max = s[TARGET * count + k] >= max_main;
                if (violation > 0.0 && at_min) || (violation < 0.0 && at_max) {
                    s[FROZEN * count + k] = 1.0;
                }
            }
        }
    }

    fn resolve_along_cross(&mut self, i: usize, row: bool, count: usize, start: usize, end: usize) {
        for k in start..end {
            let c = self.nodes[i].children[k];
            if self.nodes[c].hidden || !self.is_sized_along(i, c) {
                continue;
            }
            let target = self.nodes[i].scratch[TARGET * count + k];
            let extent = self.extent(c, row, target);
            let child = &self.nodes[c];
            let explicit_cross = if row { child.height } else { child.width };
            let explicit_min = if row {
                child.min_height
            } else {
                child.min_width
            };
            let max_cross = cross_of(child.maximum_size, row);
            let auto_min = explicit_cross.map_or(extent, |e| positive(e).min(extent));
            let min_cross = explicit_min.map_or(auto_min, positive).min(max_cross);
            let s = &mut self.nodes[i].scratch;
            s[CROSS_FLOOR * count + k] = min_cross;
            s[CROSS * count + k] = clamp(
                explicit_cross.map_or(extent, positive),
                min_cross,
                min_cross.max(max_cross),
            );
        }
    }

    fn line_cross(&self, i: usize, start: usize, end: usize) -> f32 {
        let n = &self.nodes[i];
        let count = n.children.len();
        let mut cross: f32 = 0.0;
        for k in start..end {
            if self.nodes[n.children[k]].hidden {
                continue;
            }
            cross = cross.max(n.scratch[CROSS * count + k]);
        }
        cross
    }

    #[allow(clippy::too_many_arguments)]
    fn place_line(
        &mut self,
        i: usize,
        row: bool,
        count: usize,
        start: usize,
        end: usize,
        available_main: Option<f32>,
        gap: f32,
        origin_main: f32,
        line_cross_start: f32,
        line_cross: f32,
    ) {
        let children = self.nodes[i].children.clone();
        let mut visible = 0;
        let mut used = 0.0;
        for (k, &c) in children.iter().enumerate().take(end).skip(start) {
            if self.nodes[c].hidden {
                continue;
            }
            used += self.nodes[i].scratch[TARGET * count + k];
            visible += 1;
        }
        if visible == 0 {
            return;
        }
        used += gap * (visible - 1) as f32;
        let free = available_main.map_or(0.0, |a| (a - used).max(0.0));
        let (mut offset, mut between) = (0.0, gap);
        match self.nodes[i].justify {
            FlexJustify::Center => offset = free / 2.0,
            FlexJustify::End => offset = free,
            FlexJustify::SpaceBetween if visible > 1 => between = gap + free / (visible - 1) as f32,
            _ => {}
        }
        let mut cursor = origin_main + offset;
        let parent_align = self.nodes[i].align;
        for k in start..end {
            let c = children[k];
            if self.nodes[c].hidden {
                continue;
            }
            let laid_across = self.is_laid_across(i, c);
            let s = &self.nodes[i].scratch;
            let main_start = cursor;
            let main_end = cursor + s[TARGET * count + k];
            cursor = main_end + between;
            let child = &self.nodes[c];
            let align = child.align_self.unwrap_or(parent_align);
            let explicit_cross = if row { child.height } else { child.width };
            let min_cross = s[CROSS_FLOOR * count + k];
            let max_cross = cross_of(child.maximum_size, row);
            let cross = if !laid_across && align == FlexAlign::Stretch && explicit_cross.is_none() {
                clamp(line_cross, min_cross, min_cross.max(max_cross))
            } else {
                s[CROSS * count + k]
            };
            let cross_offset = match align {
                FlexAlign::Center => (line_cross - cross) / 2.0,
                FlexAlign::End => line_cross - cross,
                _ => 0.0,
            };
            let cross_start = line_cross_start + cross_offset;
            let cross_end = cross_start + cross;
            let (m0, m1, c0, c1) = (
                snap(main_start),
                snap(main_end),
                snap(cross_start),
                snap(cross_end),
            );
            self.nodes[c].rect = if row {
                FlexRect::new(m0, c0, positive(m1 - m0), positive(c1 - c0))
            } else {
                FlexRect::new(c0, m0, positive(c1 - c0), positive(m1 - m0))
            };
        }
    }
}

#[cfg(test)]
mod tests;
