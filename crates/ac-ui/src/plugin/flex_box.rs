//! The flex side of a markup window: one layout tree per flex root (a
//! `<group layout>` whose parent is not a flex container, or a window's
//! content area with `layout`). Flex containers nested inside a root are
//! inner nodes of its tree (height-for-width only works across inner
//! nodes). A root lays its tree out when it is drawn, and only when
//! something changed: its size, a child's visibility, or a leaf's measured
//! size. Each leaf keeps its last measurement and measures again only when
//! what it was measured from (caption, font, theme) changed, so a bound
//! caption that changes every frame costs one measure, and no layout
//! unless its size changed. Nothing calls into it to invalidate; it
//! compares at draw.

use std::sync::Arc;

use ac_plugin_ui::MarkupDocument;
use ac_plugin_ui::flex::{FlexMeasurement, FlexNode, FlexRect, FlexSize, FlexTree, resolve_leaf};
use ac_plugin_ui::markup::{EXPANDER_HEADER_HEIGHT, Element as Markup};

use super::content_size::{self, MeasureInputs};
use crate::root::UiRoot;
use crate::tree::ElementId;

/// One child of a flex container.
#[derive(Debug, Clone)]
struct Item {
    element: ElementId,
    /// Its markup element.
    markup: usize,
    /// The nested container it is in (`None`: the root).
    parent: Option<usize>,
    /// Its node in the tree.
    node: usize,
    /// A nested container's own items; empty for a leaf.
    children: Vec<usize>,
    /// A nested flex container (its node has children of its own).
    inner: bool,
    /// An absolute group's authored size: it is never measured.
    authored: Option<FlexMeasurement>,
    /// The last measurement and what it was made from.
    measured: Option<(MeasureInputs, FlexMeasurement)>,
    /// An expander's authored `h`, `maxh` and `minh`, applied only while it
    /// is expanded.
    expander: Option<ExpanderLimits>,
}

#[derive(Debug, Clone, Copy)]
struct ExpanderLimits {
    height: f32,
    max_height: Option<f32>,
    min_height: Option<f32>,
}

/// A flex root's layout: its tree and one item per child at every depth.
#[derive(Debug, Clone)]
pub struct FlexBox {
    document: Arc<MarkupDocument>,
    tree: FlexTree,
    root: usize,
    items: Vec<Item>,
    /// The root's own items.
    top: Vec<usize>,
    laid: Option<(f32, f32)>,
    /// A window's content area: the smallest size its content fits in is
    /// recomputed whenever the content changes.
    pub tracks_minimum: bool,
    minimum: FlexSize,
    minimum_changed: Option<FlexSize>,
    /// Leaf measurements made, for the cost tests.
    pub measure_count: usize,
    /// Item slots of removed items, reused by the next add.
    free_items: Vec<usize>,
    /// Tree node slots of removed items, reused by the next add.
    free_nodes: Vec<usize>,
}

impl FlexBox {
    /// A root whose container node is `container`.
    #[must_use]
    pub fn new(document: Arc<MarkupDocument>, container: FlexNode) -> Self {
        let mut tree = FlexTree::default();
        let root = tree.add(container);
        Self {
            document,
            tree,
            root,
            items: Vec::new(),
            top: Vec::new(),
            laid: None,
            tracks_minimum: false,
            minimum: FlexSize::default(),
            minimum_changed: None,
            measure_count: 0,
            free_items: Vec::new(),
            free_nodes: Vec::new(),
        }
    }

    /// The root container's node.
    #[must_use]
    pub fn root_node(&self) -> &FlexNode {
        &self.tree.nodes[self.root]
    }

    /// The root container's node, to set its scrolling.
    pub fn root_node_mut(&mut self) -> &mut FlexNode {
        &mut self.tree.nodes[self.root]
    }

    /// The node of `container`, an item that is a nested container, or the
    /// root for `None`.
    #[must_use]
    pub fn container_node(&self, container: Option<usize>) -> &FlexNode {
        &self.tree.nodes[container.map_or(self.root, |i| self.items[i].node)]
    }

    /// Adds `element` (markup element `markup`) under `parent` (an item that
    /// is a nested container, or the root for `None`): a leaf measured from
    /// its content, an absolute group of its `authored` size, or with
    /// `inner` a nested container whose node is `node`. Returns the item.
    pub fn add(
        &mut self,
        parent: Option<usize>,
        element: ElementId,
        markup: usize,
        node: FlexNode,
        inner: bool,
        authored: Option<FlexMeasurement>,
    ) -> usize {
        let parent_node = parent.map_or(self.root, |p| self.items[p].node);
        let node = match self.free_nodes.pop() {
            Some(slot) => {
                self.tree.nodes[slot] = node;
                self.tree.nodes[parent_node].children.push(slot);
                slot
            }
            None => self.tree.add_child(parent_node, node),
        };
        let item = Item {
            element,
            markup,
            parent,
            node,
            children: Vec::new(),
            inner,
            authored,
            measured: None,
            expander: None,
        };
        let index = match self.free_items.pop() {
            Some(slot) => {
                self.items[slot] = item;
                slot
            }
            None => {
                self.items.push(item);
                self.items.len() - 1
            }
        };
        match parent {
            Some(p) => self.items[p].children.push(index),
            None => self.top.push(index),
        }
        // An item added after the first layout is laid out on the next.
        self.laid = None;
        index
    }

    /// Takes item `item` and everything nested in it out of the layout
    /// (OpenAC `UiFlexBox.Remove`); the next draw lays the rest out again.
    /// Its slots are reused by later adds.
    pub fn remove(&mut self, item: usize) {
        let parent = self.items[item].parent;
        let node = self.items[item].node;
        let parent_node = parent.map_or(self.root, |p| self.items[p].node);
        match parent {
            Some(p) => self.items[p].children.retain(|&c| c != item),
            None => self.top.retain(|&c| c != item),
        }
        self.tree.nodes[parent_node].children.retain(|&c| c != node);
        self.free(item);
        self.laid = None;
    }

    fn free(&mut self, item: usize) {
        for child in std::mem::take(&mut self.items[item].children) {
            self.free(child);
        }
        let node = self.items[item].node;
        self.tree.nodes[node].children.clear();
        self.items[item].measured = None;
        self.free_nodes.push(node);
        self.free_items.push(item);
    }

    /// Puts the items of `parent` (an item that is a nested container, or
    /// the root for `None`) in the order `rank` gives their elements (equal
    /// ranks keep their order), as the container's children stand after a
    /// `<repeat>` moved its rows (OpenAC `UiFlexBox.Move`).
    pub fn order_by(&mut self, parent: Option<usize>, rank: impl Fn(ElementId) -> usize) {
        let mut list = match parent {
            Some(p) => std::mem::take(&mut self.items[p].children),
            None => std::mem::take(&mut self.top),
        };
        list.sort_by_key(|&i| rank(self.items[i].element));
        let parent_node = parent.map_or(self.root, |p| self.items[p].node);
        self.tree.nodes[parent_node].children = list.iter().map(|&i| self.items[i].node).collect();
        match parent {
            Some(p) => self.items[p].children = list,
            None => self.top = list,
        }
        self.laid = None;
    }

    /// The elements of `parent`'s items, in layout order.
    #[must_use]
    pub fn elements(&self, parent: Option<usize>) -> Vec<ElementId> {
        let list = match parent {
            Some(p) => &self.items[p].children,
            None => &self.top,
        };
        list.iter().map(|&i| self.items[i].element).collect()
    }

    /// Makes item `item` an expander placed by the layout: its `h` is the
    /// expanded height, given only while it is expanded; collapsed it
    /// measures as its header.
    pub fn set_expander(&mut self, item: usize, expanded_height: f32) {
        let node = &mut self.tree.nodes[self.items[item].node];
        let limits = ExpanderLimits {
            height: node.height.unwrap_or(expanded_height),
            max_height: node.max_height,
            min_height: node.min_height,
        };
        node.height = None;
        self.items[item].expander = Some(limits);
    }

    /// The node of item `item`.
    #[must_use]
    pub fn item_node(&self, item: usize) -> &FlexNode {
        &self.tree.nodes[self.items[item].node]
    }

    /// Measures the tree now, outside a draw: the window's first
    /// measurement at build time. The preferred size and the fitted minimum.
    pub fn measure_now(&mut self, ui: &UiRoot) -> FlexMeasurement {
        let top = self.top.clone();
        self.changed(ui, &top);
        self.sync_hidden(ui, &top);
        self.prepare(ui, &top);
        let minimum = self.tree.fit_minimum(self.root);
        self.minimum = minimum;
        let preferred = self.tree.measure(self.root).preferred;
        // A size-dependent root can prefer less than it needs on its
        // dependent axis.
        FlexMeasurement::new(
            FlexSize::new(
                preferred.width.max(minimum.width),
                preferred.height.max(minimum.height),
            ),
            minimum,
        )
    }

    /// The smallest size the content fits in, after the last layout that
    /// measured it.
    #[must_use]
    pub fn minimum(&self) -> FlexSize {
        self.minimum
    }

    /// The new minimum the last layout found, once (a window's content
    /// area reports it from its tick).
    pub fn take_minimum_changed(&mut self) -> Option<FlexSize> {
        self.minimum_changed.take()
    }

    /// Lays the tree out in `width` x `height` and places every element,
    /// when anything changed since the last layout. Whether it laid out.
    pub fn ensure_layout(&mut self, ui: &mut UiRoot, width: f32, height: f32) -> bool {
        let top = self.top.clone();
        // `changed` runs even before the first layout: it brings the leaves
        // measured at build up to date.
        let content_changed = self.changed(ui, &top) | self.laid.is_none();
        if !content_changed && self.laid == Some((width, height)) {
            return false;
        }
        self.sync_hidden(ui, &top);
        self.prepare(ui, &top);
        if content_changed && self.tracks_minimum {
            let minimum = self.tree.fit_minimum(self.root);
            if minimum != self.minimum {
                self.minimum = minimum;
                self.minimum_changed = Some(minimum);
            }
        }
        self.tree
            .arrange(self.root, FlexRect::new(0.0, 0.0, width, height));
        self.place(ui, &top);
        self.laid = Some((width, height));
        true
    }

    /// Whether any item's visibility or any shown leaf's size changed, at
    /// any depth. Every shown leaf is brought up to date on the way.
    fn changed(&mut self, ui: &UiRoot, items: &[usize]) -> bool {
        let mut changed = false;
        for &i in items {
            let hidden = !ui.element(self.items[i].element).is_some_and(|e| e.visible);
            if hidden != self.tree.nodes[self.items[i].node].hidden {
                changed = true;
            }
            if hidden {
                continue;
            }
            if self.items[i].inner {
                let children = self.items[i].children.clone();
                changed |= self.changed(ui, &children);
            } else {
                changed |= self.refresh(ui, i);
            }
        }
        changed
    }

    /// Measures leaf `i` again if what it was measured from changed.
    /// Whether that changes the size it lays out at; a fixed-size leaf
    /// never does.
    fn refresh(&mut self, ui: &UiRoot, i: usize) -> bool {
        let Some((inputs, measured)) = self.items[i].measured.clone() else {
            return false;
        };
        let markup = &self.document.nodes[self.items[i].markup].element;
        let now = content_size::inputs(ui, self.items[i].element, markup);
        if inputs.collapsed_differs(&now) {
            // An expander opening or closing changes its limits, not just
            // its content.
            self.measure_leaf(ui, i);
            return true;
        }
        if self.is_fixed(i) || inputs == now {
            return false;
        }
        let node = &self.tree.nodes[self.items[i].node];
        let before = resolve_leaf(node, measured);
        self.measure_leaf(ui, i);
        let after = self.items[i]
            .measured
            .as_ref()
            .map(|m| m.1)
            .unwrap_or_default();
        resolve_leaf(&self.tree.nodes[self.items[i].node], after) != before
    }

    /// Whether leaf `i`'s size never depends on its content: an absolute
    /// group of its authored size, or a control whose markup states its
    /// width, height and both minimums.
    fn is_fixed(&self, i: usize) -> bool {
        let node = &self.tree.nodes[self.items[i].node];
        self.items[i].authored.is_some()
            || (node.width.is_some()
                && node.height.is_some()
                && node.min_width.is_some()
                && node.min_height.is_some())
    }

    fn measure_leaf(&mut self, ui: &UiRoot, i: usize) {
        self.measure_count += 1;
        let item = &self.items[i];
        let markup = &self.document.nodes[item.markup].element;
        let inputs = content_size::inputs(ui, item.element, markup);
        let expanded_height = item.expander.map_or(0.0, |e| e.height);
        let measured =
            content_size::measure(ui, item.element, markup, expanded_height).unwrap_or_default();
        self.items[i].measured = Some((inputs, measured));
    }

    /// Gives every shown leaf its measurement (a leaf is measured the first
    /// time it is laid out; after that `changed` keeps it current).
    fn prepare(&mut self, ui: &UiRoot, items: &[usize]) {
        for &i in items {
            if self.tree.nodes[self.items[i].node].hidden {
                continue;
            }
            if self.items[i].inner {
                let children = self.items[i].children.clone();
                self.prepare(ui, &children);
                continue;
            }
            let measurement = match self.items[i].authored {
                Some(authored) => authored,
                None => {
                    if self.items[i].measured.is_none() {
                        self.measure_leaf(ui, i);
                    }
                    self.items[i]
                        .measured
                        .as_ref()
                        .map(|m| m.1)
                        .unwrap_or_default()
                }
            };
            self.tree.nodes[self.items[i].node].measure = Some(measurement);
        }
    }

    fn sync_hidden(&mut self, ui: &UiRoot, items: &[usize]) {
        for &i in items {
            let item = &self.items[i];
            let visible = ui.element(item.element).is_some_and(|e| e.visible);
            let markup = &self.document.nodes[item.markup].element;
            let collapsed = content_size::collapsed(ui, item.element, markup);
            let node = &mut self.tree.nodes[item.node];
            node.hidden = !visible;
            if let Some(limits) = item.expander {
                // A collapsed expander is capped at its header, so neither
                // grow along a column nor stretch across a row opens it up.
                node.max_height = if collapsed {
                    Some(EXPANDER_HEADER_HEIGHT)
                } else {
                    limits.max_height
                };
                node.min_height = if collapsed { None } else { limits.min_height };
            }
            if item.inner {
                let children = item.children.clone();
                self.sync_hidden(ui, &children);
            }
        }
    }

    fn place(&self, ui: &mut UiRoot, items: &[usize]) {
        for &i in items {
            let item = &self.items[i];
            let node = &self.tree.nodes[item.node];
            if node.hidden {
                continue;
            }
            let r = node.rect;
            if let Some(e) = ui.element_mut(item.element) {
                e.left = r.x;
                e.top = r.y;
                e.width = r.width;
                e.height = r.height;
            }
            if item.inner {
                self.place(ui, &item.children);
            }
        }
    }

    /// The markup element of item `i` (tests).
    #[must_use]
    pub fn markup_of(&self, i: usize) -> &Markup {
        &self.document.nodes[self.items[i].markup].element
    }
}

impl MeasureInputs {
    fn collapsed_differs(&self, other: &Self) -> bool {
        self.collapsed() != other.collapsed()
    }
}
