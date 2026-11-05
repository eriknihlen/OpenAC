//! Where everything in the plugin dock goes, worked out from its mode,
//! whether it is minimized, which plugin owns each slot and how long it may
//! be. It is pure: the dock applies the result to its children.
//!
//! It works along the dock's main axis, the way its slots run, and across
//! it, so a horizontal dock is the vertical one turned on its side. Along the
//! main axis: the grip while the dock can be moved, the tile, a divider, the
//! slots (a divider between two plugins, a gap between one plugin's windows), a
//! divider, the gear slot and padding. When the slots do not fit, only those
//! that do from the first one are laid out, with an arrow at each end of the
//! list and a bar beside it saying how far through it they are; the grip, the
//! tile and the gear stay, and do not move as it scrolls. While the dock can be
//! moved and has more than one slot it ends in the handle that sets how many of
//! them show, not in padding. Closed, the dock is only the grip and the tile, and
//! the tile's art is in the same place either way.

use crate::geometry::Rect;

/// The padding round a floating dock's slots at the default size.
pub const PADDING: f32 = 6.0;
/// A floating dock's slot, each way, at the default size.
pub const SLOT: f32 = 36.0;
/// Between a floating dock's slots of one plugin at the default size.
pub const GAP: f32 = 4.0;
/// A slot's art, each way, at the default size.
pub const ART: f32 = 24.0;
/// A floating dock's thickness across its main axis at the default size.
pub const FLOATING_THICKNESS: f32 = PADDING + SLOT + PADDING;
/// How long the grip is along the main axis, at every size.
pub const GRIP_LENGTH: f32 = 14.0;
/// How long the handle at the dock's far end is along the main axis, at every
/// size.
pub const LENGTH_HANDLE: f32 = 12.0;
/// The space each side of a divider.
pub const DIVIDER_SPACE: f32 = 4.0;
/// How long the arrow at each end of an overflowing list is along the main axis,
/// at every size.
pub const ARROW_LENGTH: f32 = 16.0;
/// The shortest the scroll bar's thumb gets, so a long list's can still be seen.
pub const MIN_THUMB: f32 = 12.0;
/// A rail's thickness across its main axis at the default size.
pub const RAIL_THICKNESS: f32 = 46.0;
/// A rail's slot along its main axis at the default size.
pub const RAIL_SLOT: f32 = 38.0;
/// Between a rail's slots of one plugin.
pub const RAIL_GAP: f32 = 2.0;
/// How far a floating dock's longest stays from each end of the screen, so
/// that one which fills the screen is not touching an edge wherever it is
/// dragged.
pub const FLOATING_MARGIN: f32 = 24.0;

/// A dragged dock's edge snaps to a screen edge this close.
pub const SNAP_IN: f32 = 8.0;
/// A rail floats again once it is pulled this far from its screen edge.
pub const SNAP_OUT: f32 = 32.0;

const DIVIDER_BLOCK: f32 = DIVIDER_SPACE * 2.0 + 1.0;

/// How a closed dock opens. Pressing the tile opens and closes it either way.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DockOpens {
    /// Only by pressing the tile.
    #[default]
    Click,
    /// Also by hovering over it, until the pointer leaves the dock.
    Hover,
}

impl DockOpens {
    /// Every way, in the order the Appearance window lists them.
    pub const ALL: [Self; 2] = [Self::Click, Self::Hover];

    /// The name it is saved under.
    #[must_use]
    pub const fn saved_name(self) -> &'static str {
        match self {
            Self::Click => "click",
            Self::Hover => "hover",
        }
    }

    /// The way saved as `name`, in any case; an unknown name is a click.
    #[must_use]
    pub fn from_saved(name: &str) -> Self {
        Self::ALL
            .into_iter()
            .find(|o| o.saved_name().eq_ignore_ascii_case(name))
            .unwrap_or_default()
    }

    /// Its name in the Appearance window.
    #[must_use]
    pub const fn display_name(self) -> &'static str {
        match self {
            Self::Click => "When the icon is clicked",
            Self::Hover => "When hovering over the icon",
        }
    }

    /// The way with this name in the Appearance window.
    #[must_use]
    pub fn from_display_name(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|o| o.display_name() == name)
    }
}

/// How big the dock is drawn.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DockSize {
    /// Smaller than the default.
    Smaller,
    /// The default.
    #[default]
    Default,
    /// Bigger than the default. A dat icon's 32 pixels are its art's own size
    /// on a standard screen.
    Bigger,
}

impl DockSize {
    /// Every size, in the order the Appearance window lists them.
    pub const ALL: [Self; 3] = [Self::Smaller, Self::Default, Self::Bigger];

    /// The name it is saved under.
    #[must_use]
    pub const fn saved_name(self) -> &'static str {
        match self {
            Self::Smaller => "smaller",
            Self::Default => "default",
            Self::Bigger => "bigger",
        }
    }

    /// The size saved as `name`, in any case; an unknown name is the default.
    #[must_use]
    pub fn from_saved(name: &str) -> Self {
        Self::ALL
            .into_iter()
            .find(|s| s.saved_name().eq_ignore_ascii_case(name))
            .unwrap_or_default()
    }

    /// Its name in the Appearance window.
    #[must_use]
    pub const fn display_name(self) -> &'static str {
        match self {
            Self::Smaller => "Smaller",
            Self::Default => "Default",
            Self::Bigger => "Bigger",
        }
    }

    /// The size with this name in the Appearance window.
    #[must_use]
    pub fn from_display_name(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|s| s.display_name() == name)
    }

    /// Its measurements.
    #[must_use]
    pub const fn metrics(self) -> Metrics {
        match self {
            Self::Smaller => Metrics {
                padding: 5.0,
                slot: 28.0,
                gap: 3.0,
                art: 18.0,
                rail_thickness: 36.0,
                rail_slot: 30.0,
                radius: 12.0,
                rail_radius: 10.0,
                well_radius: 7.0,
            },
            Self::Default => Metrics {
                padding: PADDING,
                slot: SLOT,
                gap: GAP,
                art: ART,
                rail_thickness: RAIL_THICKNESS,
                rail_slot: RAIL_SLOT,
                radius: 14.0,
                rail_radius: 12.0,
                well_radius: 9.0,
            },
            Self::Bigger => Metrics {
                padding: 7.0,
                slot: 44.0,
                gap: 5.0,
                art: 32.0,
                rail_thickness: 56.0,
                rail_slot: 46.0,
                radius: 16.0,
                rail_radius: 14.0,
                well_radius: 11.0,
            },
        }
    }
}

/// The measurements of a dock of one size, in points.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Metrics {
    /// The padding round a floating dock's slots.
    pub padding: f32,
    /// A floating dock's slot, each way.
    pub slot: f32,
    /// Between a floating dock's slots of one plugin.
    pub gap: f32,
    /// A slot's art, each way.
    pub art: f32,
    /// A rail's thickness across its main axis.
    pub rail_thickness: f32,
    /// A rail's slot along its main axis.
    pub rail_slot: f32,
    /// A floating dock's corner radius.
    pub radius: f32,
    /// A rail's inner corner radius.
    pub rail_radius: f32,
    /// A slot's hover well's corner radius.
    pub well_radius: f32,
}

impl Metrics {
    /// A floating dock's thickness across its main axis.
    #[must_use]
    pub const fn floating_thickness(&self) -> f32 {
        self.padding + self.slot + self.padding
    }
}

/// Where the dock is and which way it runs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DockMode {
    /// Free on the screen, running top to bottom.
    #[default]
    Floating,
    /// Free on the screen, running left to right.
    FloatingHorizontal,
    /// A rail on the left edge.
    Left,
    /// A rail on the right edge.
    Right,
    /// A rail on the top edge.
    Top,
    /// A rail on the bottom edge.
    Bottom,
}

impl DockMode {
    /// Every mode, in the order the Appearance window lists them.
    pub const ALL: [Self; 6] = [
        Self::Floating,
        Self::FloatingHorizontal,
        Self::Left,
        Self::Right,
        Self::Top,
        Self::Bottom,
    ];

    /// The name it is saved under.
    #[must_use]
    pub const fn saved_name(self) -> &'static str {
        match self {
            Self::Floating => "floating",
            Self::FloatingHorizontal => "floating-horizontal",
            Self::Left => "left",
            Self::Right => "right",
            Self::Top => "top",
            Self::Bottom => "bottom",
        }
    }

    /// The mode saved as `name`, in any case; an unknown name is floating.
    #[must_use]
    pub fn from_saved(name: &str) -> Self {
        Self::ALL
            .into_iter()
            .find(|m| m.saved_name().eq_ignore_ascii_case(name))
            .unwrap_or_default()
    }

    /// Its name in the Appearance window.
    #[must_use]
    pub const fn display_name(self) -> &'static str {
        match self {
            Self::Floating => "Floating",
            Self::FloatingHorizontal => "Floating, horizontal",
            Self::Left => "Left edge",
            Self::Right => "Right edge",
            Self::Top => "Top edge",
            Self::Bottom => "Bottom edge",
        }
    }

    /// The mode with this name in the Appearance window.
    #[must_use]
    pub fn from_display_name(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|m| m.display_name() == name)
    }

    /// Whether its slots run left to right.
    #[must_use]
    pub const fn is_horizontal(self) -> bool {
        matches!(self, Self::FloatingHorizontal | Self::Top | Self::Bottom)
    }

    /// Whether it is a rail on a screen edge.
    #[must_use]
    pub const fn is_rail(self) -> bool {
        !matches!(self, Self::Floating | Self::FloatingHorizontal)
    }
}

/// The longest a dock in `mode` may be on a screen of `width` by `height`. It
/// comes from the screen alone, not from where the dock is: a dock limited by
/// the room left below it would always end level with the bottom of the
/// screen, and so count as touching that edge wherever it was dragged.
#[must_use]
pub fn max_length(mode: DockMode, width: f32, height: f32) -> f32 {
    let screen = if mode.is_horizontal() { width } else { height };
    if mode.is_rail() {
        screen
    } else {
        screen - 2.0 * FLOATING_MARGIN
    }
}

/// The pointer's distance from a rail's screen edge; none for a floating dock.
#[must_use]
pub fn pointer_from_edge(
    mode: DockMode,
    (screen_w, screen_h): (f32, f32),
    (x, y): (f32, f32),
) -> Option<f32> {
    match mode {
        DockMode::Left => Some(x),
        DockMode::Right => Some(screen_w - x),
        DockMode::Top => Some(y),
        DockMode::Bottom => Some(screen_h - y),
        DockMode::Floating | DockMode::FloatingHorizontal => None,
    }
}

/// The mode a dock dragged to `at` takes, with the pointer at `pointer`. A
/// floating dock becomes a rail when one of its edges is within [`SNAP_IN`] of
/// a screen edge and that is the screen edge nearest the pointer, so that a
/// long dock whose far end touches an edge does not snap there while it is
/// being carried past.
///
/// A rail stays until the pointer is [`SNAP_OUT`] further from its edge than
/// `closest`, the closest it has been in this drag, and then floats running the
/// way it ran, so moving to another edge means floating first. It goes by the
/// pointer because a dock that snaps by its far end may turn into a rail well
/// away from the pointer.
#[must_use]
pub fn dragged_mode(
    mode: DockMode,
    at: Rect,
    screen: (f32, f32),
    pointer: (f32, f32),
    closest: f32,
) -> DockMode {
    let (screen_w, screen_h) = screen;
    let (x, y) = pointer;
    if let Some(away) = pointer_from_edge(mode, screen, pointer) {
        return match mode {
            _ if away <= closest + SNAP_OUT => mode,
            DockMode::Left | DockMode::Right => DockMode::Floating,
            _ => DockMode::FloatingHorizontal,
        };
    }
    let edges = [
        (DockMode::Left, x, at.x),
        (DockMode::Right, screen_w - x, screen_w - (at.x + at.w)),
        (DockMode::Top, y, at.y),
        (DockMode::Bottom, screen_h - y, screen_h - (at.y + at.h)),
    ];
    let mut nearest = edges[0];
    for edge in &edges[1..] {
        if edge.1 < nearest.1 {
            nearest = *edge;
        }
    }
    if nearest.2 <= SNAP_IN {
        nearest.0
    } else {
        mode
    }
}

/// The bar beside an overflowing list, along the main axis in the dock's space:
/// the whole list is its track and what shows now is its thumb.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ScrollBar {
    /// Where the track starts.
    pub start: f32,
    /// Where the track ends.
    pub end: f32,
    /// Where the thumb starts.
    pub thumb_start: f32,
    /// Where the thumb ends.
    pub thumb_end: f32,
}

/// Where the dock's parts are, in its own space.
#[derive(Debug, Clone, PartialEq)]
pub struct DockLayout {
    /// The dock's width.
    pub width: f32,
    /// The dock's height.
    pub height: f32,
    /// Each slot's place; none for a slot that is not laid out.
    pub slots: Vec<Option<Rect>>,
    /// Where each one-point divider starts along the main axis.
    pub dividers: Vec<f32>,
    /// The gear's place; empty on a closed dock.
    pub gear: Rect,
    /// The tile, which opens and closes the dock.
    pub tile: Rect,
    /// The grip, which moves the dock; none while it cannot be moved.
    pub grip: Option<Rect>,
    /// The handle, which sets how many slots show; none while the dock cannot
    /// be moved, while it is closed and with one slot or none.
    pub length_handle: Option<Rect>,
    /// The arrow before the list; none while every slot fits.
    pub back: Option<Rect>,
    /// The arrow after the list; none while every slot fits.
    pub forward: Option<Rect>,
    /// The bar beside the list; none while every slot fits.
    pub scroll: Option<ScrollBar>,
    /// The first slot laid out; kept while the dock is closed.
    pub first: usize,
    /// How many slots are laid out.
    pub visible: usize,
    /// The furthest the slots can scroll.
    pub max_first: usize,
    /// Where the list starts along the main axis.
    pub list_start: f32,
}

/// How a dock's slots and tile sit along its main axis and across it.
#[derive(Debug, Clone, Copy)]
struct SlotRun {
    tile_start: f32,
    cross_at: f32,
    across: f32,
    along: f32,
    gap: f32,
}

impl SlotRun {
    fn new(mode: DockMode, m: &Metrics, lead: f32) -> Self {
        // A rail's tile is centred in the square a closed rail is, so its art
        // stays where it was.
        if mode.is_rail() {
            Self {
                tile_start: lead + (m.rail_thickness - m.rail_slot) / 2.0,
                cross_at: 0.0,
                across: m.rail_thickness,
                along: m.rail_slot,
                gap: RAIL_GAP,
            }
        } else {
            Self {
                tile_start: lead + m.padding,
                cross_at: m.padding,
                across: m.slot,
                along: m.slot,
                gap: m.gap,
            }
        }
    }

    /// Between slot `i` and the one before it: a gap within one plugin, a
    /// divider between two.
    fn spacing(&self, owners: &[&str], i: usize) -> f32 {
        if owners[i] == owners[i - 1] {
            self.gap
        } else {
            DIVIDER_BLOCK
        }
    }

    /// Everything that is not the slot list: the grip, tile and divider before
    /// it, and the divider, gear and `end` after it.
    fn around_list(&self, count: usize, end: f32) -> f32 {
        let divider = if count > 0 { DIVIDER_BLOCK } else { 0.0 };
        self.tile_start + self.along + DIVIDER_BLOCK + divider + self.along + end
    }
}

/// What comes after the gear: the handle where there is one, else padding.
fn after_gear(m: &Metrics, grip: bool, count: usize) -> f32 {
    if grip && count > 1 {
        LENGTH_HANDLE
    } else {
        m.padding
    }
}

/// The longest a dock may be to show `limit` slots wherever it has scrolled to:
/// room for the longest run of that many in the list, and its arrows. It is
/// counted in slots, so the same limit shows the same windows at every size,
/// locked or not, on any screen. With no limit, or one the list fits in, it can
/// be as long as it likes.
#[must_use]
pub fn length_for_slots(
    mode: DockMode,
    size: DockSize,
    grip: bool,
    owners: &[&str],
    limit: Option<usize>,
) -> f32 {
    let count = owners.len();
    let Some(limit) = limit.map(|l| l.max(1)).filter(|l| *l < count) else {
        return f32::INFINITY;
    };
    let m = size.metrics();
    let run = SlotRun::new(mode, &m, if grip { GRIP_LENGTH } else { 0.0 });
    let longest = ((limit - 1)..count)
        .map(|last| {
            ((last + 1 - limit + 1)..=last)
                .map(|i| run.spacing(owners, i))
                .sum::<f32>()
        })
        .fold(0.0, f32::max);
    run.around_list(count, after_gear(&m, grip, count))
        + 2.0 * ARROW_LENGTH
        + limit as f32 * run.along
        + longest
}

/// The limit that brings a dock nearest to `length` along its main axis, with
/// `longest` the most it may be and `room` what the screen has left from where
/// it starts: `None` when that is no limit at all.
#[must_use]
pub fn slots_for_length(
    mode: DockMode,
    size: DockSize,
    grip: bool,
    owners: &[&str],
    longest: f32,
    room: f32,
    length: f32,
) -> Option<usize> {
    let count = owners.len();
    let along = |limit: Option<usize>| {
        let most = longest.min(length_for_slots(mode, size, grip, owners, limit));
        let l = compute(mode, size, false, grip, owners, most, 0);
        if mode.is_horizontal() {
            l.width
        } else {
            l.height
        }
    };
    let mut best = None;
    let mut nearest = f32::INFINITY;
    for limit in 1..=count {
        let limit = (limit < count).then_some(limit);
        let reached = along(limit);
        if reached > room && best.is_some() {
            break;
        }
        // The later of two the same keeps more slots, and so one as long as the
        // list can be is no limit.
        if (reached - length).abs() <= nearest {
            nearest = (reached - length).abs();
            best = limit.or(Some(count));
        }
    }
    best.filter(|limit| *limit < count)
}

/// Places things by main and cross axis in a dock running either way.
#[derive(Debug, Clone, Copy)]
struct Axes {
    horizontal: bool,
}

impl Axes {
    fn rect(self, main: f32, cross: f32, main_length: f32, cross_length: f32) -> Rect {
        if self.horizontal {
            Rect::new(main, cross, main_length, cross_length)
        } else {
            Rect::new(cross, main, cross_length, main_length)
        }
    }

    fn size(self, main_length: f32, cross_length: f32) -> (f32, f32) {
        if self.horizontal {
            (main_length, cross_length)
        } else {
            (cross_length, main_length)
        }
    }
}

/// The layout of a dock in `mode` at `size`, minimized or not, with a grip or
/// not, with one slot per entry of `owners` (each slot's plugin), at most
/// `max_length` long, scrolled to start at slot `first`.
#[must_use]
pub fn compute(
    mode: DockMode,
    size: DockSize,
    minimized: bool,
    grip: bool,
    owners: &[&str],
    max_length: f32,
    first: usize,
) -> DockLayout {
    let axes = Axes {
        horizontal: mode.is_horizontal(),
    };
    let count = owners.len();
    let mut slots = vec![None; count];

    let m = size.metrics();
    let thickness = if mode.is_rail() {
        m.rail_thickness
    } else {
        m.floating_thickness()
    };
    let lead = if grip { GRIP_LENGTH } else { 0.0 };
    let end = after_gear(&m, grip, count);
    let handled = grip && count > 1;
    let grip = grip.then(|| axes.rect(0.0, 0.0, GRIP_LENGTH, thickness));
    if minimized {
        let (width, height) = axes.size(lead + thickness, thickness);
        return DockLayout {
            width,
            height,
            slots,
            dividers: Vec::new(),
            gear: Rect::new(0.0, 0.0, 0.0, 0.0),
            tile: axes.rect(lead, 0.0, thickness, thickness),
            grip,
            length_handle: None,
            back: None,
            forward: None,
            scroll: None,
            first,
            visible: 0,
            max_first: 0,
            list_start: 0.0,
        };
    }

    let run = SlotRun::new(mode, &m, lead);
    let (slot_cross_at, slot_across, slot_along) = (run.cross_at, run.across, run.along);
    let tile = axes.rect(run.tile_start, slot_cross_at, slot_along, slot_across);
    let tile_end = run.tile_start + slot_along;
    let spacing = |i: usize| run.spacing(owners, i);
    let room = max_length - run.around_list(count, end);

    // How many slots fit back from the last one in `room`: at least one, however
    // little room there is.
    let last_page = |room: f32| {
        let mut fit = 0;
        let mut used = 0.0;
        for i in (0..count).rev() {
            let next = used + if fit > 0 { spacing(i + 1) } else { 0.0 } + slot_along;
            if next > room && fit > 0 {
                break;
            }
            used = next;
            fit += 1;
        }
        fit
    };
    let overflows = last_page(room) < count;
    let arrow = if overflows { ARROW_LENGTH } else { 0.0 };
    let room = room - 2.0 * arrow;
    let max_first = count - last_page(room);
    let first = first.min(max_first);
    // How many slots fit from `first`, and how long they are.
    let page = |first: usize| {
        let mut fit = 0;
        let mut used = 0.0;
        for i in first..count {
            let next = used + if fit > 0 { spacing(i) } else { 0.0 } + slot_along;
            if next > room && fit > 0 {
                break;
            }
            used = next;
            fit += 1;
        }
        (fit, used)
    };
    let visible = page(first).0;
    // The list is as long as its longest page, so what comes after it stays put
    // as it scrolls.
    let list_length = (0..=max_first).map(|f| page(f).1).fold(0.0, f32::max);

    let mut dividers = vec![tile_end + DIVIDER_SPACE + 0.5];
    let list_start = tile_end + DIVIDER_BLOCK + arrow;
    let mut at = list_start;
    for (i, slot) in slots.iter_mut().enumerate().skip(first).take(visible) {
        if i > first {
            let space = spacing(i);
            if owners[i] != owners[i - 1] {
                dividers.push(at + DIVIDER_SPACE + 0.5);
            }
            at += space;
        }
        *slot = Some(axes.rect(at, slot_cross_at, slot_along, slot_across));
        at += slot_along;
    }
    let arrow_at = |main| overflows.then(|| axes.rect(main, slot_cross_at, arrow, slot_across));
    let back = arrow_at(tile_end + DIVIDER_BLOCK);
    let forward = arrow_at(list_start + list_length);
    let scroll = overflows.then(|| {
        let share = list_length * visible as f32 / count as f32;
        let thumb = share.max(MIN_THUMB).min(list_length);
        let thumb_start = list_start + (list_length - thumb) * first as f32 / max_first as f32;
        ScrollBar {
            start: list_start,
            end: list_start + list_length,
            thumb_start,
            thumb_end: thumb_start + thumb,
        }
    });
    if overflows {
        at = list_start + list_length + arrow;
    }
    if count > 0 {
        dividers.push(at + DIVIDER_SPACE + 0.5);
        at += DIVIDER_BLOCK;
    }
    let gear = axes.rect(at, slot_cross_at, slot_along, slot_across);
    let length_handle = handled.then(|| axes.rect(at + slot_along, 0.0, LENGTH_HANDLE, thickness));
    let (width, height) = axes.size(at + slot_along + end, thickness);
    DockLayout {
        width,
        height,
        slots,
        dividers,
        gear,
        tile,
        grip,
        length_handle,
        back,
        forward,
        scroll,
        first,
        visible,
        max_first,
        list_start,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// At the default size.
    fn compute(
        mode: DockMode,
        minimized: bool,
        owners: &[&str],
        max: f32,
        first: usize,
    ) -> DockLayout {
        super::compute(
            mode,
            DockSize::Default,
            minimized,
            false,
            owners,
            max,
            first,
        )
    }

    fn floating(owners: &[&str], max: f32, first: usize) -> DockLayout {
        compute(DockMode::Floating, false, owners, max, first)
    }

    fn twelve() -> Vec<String> {
        (0..12).map(|i| format!("p{i}")).collect()
    }

    fn refs(owners: &[String]) -> Vec<&str> {
        owners.iter().map(String::as_str).collect()
    }

    #[test]
    fn one_slot_is_the_tile_a_divider_a_slot_a_divider_the_gear_and_padding() {
        let l = floating(&["a"], f32::INFINITY, 0);
        assert_eq!(l.width, 48.0);
        // 6 + 36 (tile) + 9 (divider) + 36 (slot) + 9 (divider) + 36 (gear) + 6
        assert_eq!(l.height, 138.0);
        assert_eq!(l.tile, Rect::new(6.0, 6.0, 36.0, 36.0));
        assert_eq!(l.slots[0], Some(Rect::new(6.0, 51.0, 36.0, 36.0)));
        assert_eq!(l.dividers, [46.5, 91.5]);
        assert_eq!(l.gear, Rect::new(6.0, 96.0, 36.0, 36.0));
    }

    #[test]
    fn slots_of_one_plugin_have_a_gap_and_another_plugin_a_divider() {
        let l = floating(&["moss", "moss", "goarrow"], f32::INFINITY, 0);
        let main = |i: usize| l.slots[i].unwrap().y;
        assert_eq!(main(0), 51.0);
        assert_eq!(main(1), 91.0); // 51 + 36 + 4
        assert_eq!(main(2), 136.0); // 91 + 36 + 9
        assert_eq!(l.dividers, [46.5, 131.5, 176.5]);
        assert_eq!(l.gear.y, 181.0);
        assert_eq!(l.height, 223.0);
    }

    #[test]
    fn overflow_shows_what_fits_and_scrolling_stops_at_the_last_full_page() {
        let owners = twelve();
        let owners = refs(&owners);
        // Room for 51 + 16 + 3 slots (36 + 9 + 36 + 9 + 36) + 16 + 9 + 36 + 6 = 260.
        let top = floating(&owners, 262.0, 0);
        assert_eq!((top.visible, top.first, top.max_first), (3, 0, 9));
        assert!(top.height <= 262.0);
        assert!(top.slots[2].is_some());
        assert!(top.slots[3].is_none());

        let end = floating(&owners, 262.0, 50);
        assert_eq!(end.first, 9);
        assert!(end.slots[8].is_none());
        assert_eq!(end.slots[9].unwrap().y, 67.0);
        assert!(end.slots[11].is_some());
    }

    #[test]
    fn too_little_room_still_shows_one_slot() {
        let l = floating(&["a", "b"], 10.0, 0);
        assert_eq!(l.visible, 1);
        assert!(l.slots[0].is_some());
    }

    #[test]
    fn a_list_that_fits_has_no_arrows_or_bar() {
        let l = floating(&["a", "b"], f32::INFINITY, 0);
        assert_eq!((l.back, l.forward, l.scroll), (None, None, None));
        // One slot that does not fit has nowhere to scroll to.
        let l = floating(&["a"], 10.0, 0);
        assert_eq!((l.back, l.forward, l.scroll), (None, None, None));
    }

    #[test]
    fn the_arrows_are_at_each_end_of_the_list_inside_its_dividers() {
        let owners = twelve();
        let owners = refs(&owners);
        let l = floating(&owners, 262.0, 0);
        assert_eq!(l.back, Some(Rect::new(6.0, 51.0, 36.0, ARROW_LENGTH)));
        assert_eq!(l.list_start, 67.0);
        assert_eq!(l.slots[0].unwrap().y, 67.0);
        assert_eq!(l.forward, Some(Rect::new(6.0, 193.0, 36.0, ARROW_LENGTH)));
        assert_eq!(l.dividers.first(), Some(&46.5));
        assert_eq!(l.dividers.last(), Some(&213.5));
        assert_eq!(l.gear.y, 218.0);
    }

    #[test]
    fn what_comes_after_the_list_stays_put_as_it_scrolls() {
        // Pages of different lengths: one plugin's windows sit closer together
        // than two plugins'.
        let owners = ["a", "b", "c", "d", "e", "e", "e", "e", "e", "f", "g", "h"];
        let start = floating(&owners, 300.0, 0);
        assert!(start.max_first > 0);
        for first in 0..=start.max_first {
            let l = floating(&owners, 300.0, first);
            assert_eq!(l.forward, start.forward, "{first}");
            assert_eq!(l.gear, start.gear, "{first}");
            assert_eq!(l.height, start.height, "{first}");
            let last = l.slots.iter().flatten().last().unwrap();
            assert!(last.y + last.h <= l.forward.unwrap().y, "{first}");
        }
    }

    #[test]
    fn the_bar_runs_along_the_list_and_its_thumb_says_how_far_through_it_is() {
        let owners = twelve();
        let owners = refs(&owners);
        let start = floating(&owners, 262.0, 0);
        let end = floating(&owners, 262.0, start.max_first);
        let (a, b) = (start.scroll.unwrap(), end.scroll.unwrap());
        assert_eq!(a.start, start.list_start);
        assert_eq!(a.end, start.forward.unwrap().y);
        assert_eq!(a.thumb_start, a.start);
        assert_eq!(b.thumb_end, b.end);
        // 3 of 12 show, so the thumb is a quarter of the track.
        let track = a.end - a.start;
        assert_eq!(a.thumb_end - a.thumb_start, track / 4.0);
        let mut last = a.thumb_start;
        for first in 1..=start.max_first {
            let bar = floating(&owners, 262.0, first).scroll.unwrap();
            assert!(bar.thumb_start > last, "{first}");
            assert!(bar.thumb_end <= bar.end, "{first}");
            last = bar.thumb_start;
        }
    }

    #[test]
    fn a_thumb_on_a_very_long_list_can_still_be_seen() {
        let owners: Vec<String> = (0..200).map(|i| format!("p{i}")).collect();
        let owners = refs(&owners);
        let start = floating(&owners, 262.0, 0);
        let end = floating(&owners, 262.0, start.max_first);
        let (a, b) = (start.scroll.unwrap(), end.scroll.unwrap());
        assert_eq!(a.thumb_end - a.thumb_start, MIN_THUMB);
        assert_eq!(b.thumb_end, b.end);
    }

    #[test]
    fn closed_keeps_its_place_in_the_list() {
        let owners = twelve();
        let owners = refs(&owners);
        let l = compute(DockMode::Floating, true, &owners, 262.0, 4);
        assert_eq!(l.first, 4);
        assert_eq!((l.back, l.forward, l.scroll), (None, None, None));
    }

    #[test]
    fn a_rail_has_full_width_slots_at_the_same_pitch_as_floating() {
        for mode in [DockMode::Left, DockMode::Right] {
            let rail = compute(mode, false, &["a", "a"], f32::INFINITY, 0);
            assert_eq!(rail.width, 46.0);
            assert_eq!(rail.tile, Rect::new(0.0, 4.0, 46.0, 38.0));
            assert_eq!(rail.slots[0], Some(Rect::new(0.0, 51.0, 46.0, 38.0)));
            assert_eq!(rail.slots[1].unwrap().y, 91.0); // 51 + 38 + 2
        }
    }

    #[test]
    fn closed_is_only_the_tile_a_square_as_thick_as_the_dock() {
        for (mode, side) in [
            (DockMode::Floating, 48.0),
            (DockMode::FloatingHorizontal, 48.0),
            (DockMode::Left, 46.0),
            (DockMode::Right, 46.0),
            (DockMode::Top, 46.0),
            (DockMode::Bottom, 46.0),
        ] {
            let l = compute(mode, true, &["a"], 500.0, 0);
            assert_eq!((l.width, l.height), (side, side));
            assert_eq!(l.tile, Rect::new(0.0, 0.0, side, side));
            assert!(l.slots.iter().all(Option::is_none));
            assert!(l.dividers.is_empty());
        }
    }

    #[test]
    fn the_tiles_art_does_not_move_between_closed_and_open() {
        for mode in DockMode::ALL {
            let centre = |r: Rect| (r.x + r.w / 2.0, r.y + r.h / 2.0);
            let closed = compute(mode, true, &["a"], 500.0, 0);
            let open = compute(mode, false, &["a"], 500.0, 0);
            assert_eq!(centre(closed.tile), centre(open.tile), "{mode:?}");
        }
    }

    #[test]
    fn the_three_sizes_measure_as_agreed_and_the_default_is_unchanged() {
        let owners = ["a", "b", "c", "d"];
        for (size, slot, art, thick, rail, height) in [
            (DockSize::Smaller, 28.0, 18.0, 38.0, 36.0, 223.0),
            (DockSize::Default, 36.0, 24.0, 48.0, 46.0, 273.0),
            (DockSize::Bigger, 44.0, 32.0, 58.0, 56.0, 323.0),
        ] {
            let m = size.metrics();
            assert_eq!((m.slot, m.art), (slot, art), "{size:?}");
            let free = super::compute(DockMode::Floating, size, false, false, &owners, 2000.0, 0);
            assert_eq!(free.width, thick, "{size:?}");
            assert_eq!(free.height, height, "{size:?}");
            assert_eq!(free.tile.w, slot, "{size:?}");
            let edge = super::compute(DockMode::Left, size, false, false, &owners, 2000.0, 0);
            assert_eq!(edge.width, rail, "{size:?}");
            let closed = super::compute(DockMode::Floating, size, true, false, &owners, 2000.0, 0);
            assert_eq!((closed.width, closed.height), (thick, thick), "{size:?}");
            for mode in DockMode::ALL {
                let centre = |r: Rect| (r.x + r.w / 2.0, r.y + r.h / 2.0);
                let shut = super::compute(mode, size, true, false, &owners, 2000.0, 0);
                let open = super::compute(mode, size, false, false, &owners, 2000.0, 0);
                assert_eq!(centre(shut.tile), centre(open.tile), "{size:?} {mode:?}");
            }
        }
    }

    /// At the default size, with the grip.
    fn with_grip(mode: DockMode, minimized: bool, owners: &[&str], max: f32) -> DockLayout {
        super::compute(mode, DockSize::Default, minimized, true, owners, max, 0)
    }

    #[test]
    fn the_grip_comes_first_and_everything_else_moves_along_by_its_length() {
        for mode in DockMode::ALL {
            for minimized in [false, true] {
                let plain = compute(mode, minimized, &["a"], 2000.0, 0);
                let l = with_grip(mode, minimized, &["a"], 2000.0);
                assert_eq!(plain.grip, None);
                let along = |r: Rect| {
                    if mode.is_horizontal() {
                        Rect::new(r.x + GRIP_LENGTH, r.y, r.w, r.h)
                    } else {
                        Rect::new(r.x, r.y + GRIP_LENGTH, r.w, r.h)
                    }
                };
                let thick = plain.width.min(plain.height);
                let grip = l.grip.unwrap();
                if mode.is_horizontal() {
                    assert_eq!(grip, Rect::new(0.0, 0.0, GRIP_LENGTH, thick));
                    assert_eq!(l.width, plain.width + GRIP_LENGTH);
                    assert_eq!(l.height, plain.height);
                } else {
                    assert_eq!(grip, Rect::new(0.0, 0.0, thick, GRIP_LENGTH));
                    assert_eq!(l.height, plain.height + GRIP_LENGTH);
                    assert_eq!(l.width, plain.width);
                }
                assert_eq!(l.tile, along(plain.tile), "{mode:?}");
                if !minimized {
                    assert_eq!(l.gear, along(plain.gear), "{mode:?}");
                    assert_eq!(l.slots[0], plain.slots[0].map(along), "{mode:?}");
                }
            }
        }
    }

    #[test]
    fn the_grip_takes_its_room_out_of_the_slots_and_the_dock_still_fits() {
        let owners = twelve();
        let owners = refs(&owners);
        let plain = compute(DockMode::Floating, false, &owners, 272.0, 0);
        assert_eq!(plain.visible, 3);
        let l = with_grip(DockMode::Floating, false, &owners, 272.0);
        assert_eq!(l.visible, 2);
        assert!(l.height <= 272.0);
    }

    #[test]
    fn with_the_grip_the_tiles_art_still_does_not_move_between_closed_and_open() {
        for mode in DockMode::ALL {
            let centre = |r: Rect| (r.x + r.w / 2.0, r.y + r.h / 2.0);
            let closed = with_grip(mode, true, &["a"], 500.0);
            let open = with_grip(mode, false, &["a"], 500.0);
            assert_eq!(centre(closed.tile), centre(open.tile), "{mode:?}");
        }
    }

    #[test]
    fn the_handle_ends_a_dock_that_can_be_moved_and_has_slots_to_choose_from() {
        for mode in DockMode::ALL {
            let l = with_grip(mode, false, &["a", "b"], 2000.0);
            let handle = l.length_handle.unwrap();
            let gear = l.gear;
            if mode.is_horizontal() {
                assert_eq!(handle.x, gear.x + gear.w, "{mode:?}");
                assert_eq!(l.width, handle.x + LENGTH_HANDLE, "{mode:?}");
                assert_eq!((handle.y, handle.h), (0.0, l.height), "{mode:?}");
            } else {
                assert_eq!(handle.y, gear.y + gear.h, "{mode:?}");
                assert_eq!(l.height, handle.y + LENGTH_HANDLE, "{mode:?}");
                assert_eq!((handle.x, handle.w), (0.0, l.width), "{mode:?}");
            }
            // Locked, closed, or with nothing to choose between, it is not there
            // and the dock ends as it always has.
            assert_eq!(
                compute(mode, false, &["a", "b"], 2000.0, 0).length_handle,
                None
            );
            assert_eq!(
                with_grip(mode, true, &["a", "b"], 2000.0).length_handle,
                None
            );
            assert_eq!(with_grip(mode, false, &["a"], 2000.0).length_handle, None);
        }
    }

    #[test]
    fn size_names_round_trip_and_an_unknown_one_is_the_default() {
        for size in DockSize::ALL {
            assert_eq!(DockSize::from_saved(size.saved_name()), size);
            assert_eq!(DockSize::from_display_name(size.display_name()), Some(size));
        }
        assert_eq!(DockSize::from_saved("BIGGER"), DockSize::Bigger);
        assert_eq!(DockSize::from_saved("huge"), DockSize::Default);
        assert_eq!(DockSize::from_saved(""), DockSize::Default);
    }

    #[test]
    fn opening_names_round_trip_and_an_unknown_one_is_a_click() {
        for opens in DockOpens::ALL {
            assert_eq!(DockOpens::from_saved(opens.saved_name()), opens);
            assert_eq!(
                DockOpens::from_display_name(opens.display_name()),
                Some(opens)
            );
        }
        assert_eq!(DockOpens::from_saved("HOVER"), DockOpens::Hover);
        assert_eq!(DockOpens::from_saved("psychic"), DockOpens::Click);
    }

    #[test]
    fn no_entries_have_no_divider_before_the_gear() {
        let l = floating(&[], f32::INFINITY, 0);
        assert_eq!(l.dividers, [46.5]);
        assert_eq!(l.gear.y, 51.0);
    }

    // ── Running the other way ───────────────────────────────────────────

    /// A rectangle turned on its side.
    fn turned(r: Rect) -> Rect {
        Rect::new(r.y, r.x, r.h, r.w)
    }

    #[test]
    fn a_horizontal_dock_is_the_vertical_one_turned_on_its_side() {
        let owners = ["moss", "moss", "goarrow"];
        for (vertical, horizontal) in [
            (DockMode::Floating, DockMode::FloatingHorizontal),
            (DockMode::Left, DockMode::Top),
            (DockMode::Right, DockMode::Bottom),
        ] {
            for minimized in [false, true] {
                let v = compute(vertical, minimized, &owners, 260.0, 0);
                let h = compute(horizontal, minimized, &owners, 260.0, 0);
                assert_eq!((h.width, h.height), (v.height, v.width));
                assert_eq!(h.tile, turned(v.tile));
                assert_eq!(h.gear, turned(v.gear));
                assert_eq!(h.dividers, v.dividers);
                assert_eq!(
                    h.slots,
                    v.slots.iter().map(|s| s.map(turned)).collect::<Vec<_>>()
                );
                assert_eq!(
                    (h.first, h.visible, h.max_first),
                    (v.first, v.visible, v.max_first)
                );
            }
        }
    }

    #[test]
    fn an_overflowing_horizontal_dock_is_the_vertical_one_turned_on_its_side() {
        let owners = twelve();
        let owners = refs(&owners);
        let v = compute(DockMode::Left, false, &owners, 400.0, 2);
        let h = compute(DockMode::Top, false, &owners, 400.0, 2);
        assert!(v.back.is_some());
        assert_eq!(h.back, v.back.map(turned));
        assert_eq!(h.forward, v.forward.map(turned));
        assert_eq!(h.scroll, v.scroll);
    }

    #[test]
    fn a_horizontal_dock_lays_out_along_x() {
        let l = compute(
            DockMode::FloatingHorizontal,
            false,
            &["a"],
            f32::INFINITY,
            0,
        );
        assert_eq!((l.width, l.height), (138.0, 48.0));
        assert_eq!(l.tile, Rect::new(6.0, 6.0, 36.0, 36.0));
        assert_eq!(l.slots[0], Some(Rect::new(51.0, 6.0, 36.0, 36.0)));
        assert_eq!(l.gear, Rect::new(96.0, 6.0, 36.0, 36.0));
    }

    #[test]
    fn a_horizontal_dock_overflows_along_its_length() {
        let owners = twelve();
        let owners = refs(&owners);
        let l = compute(DockMode::Top, false, &owners, 400.0, 2);
        assert_eq!(l.height, RAIL_THICKNESS);
        assert!(l.width <= 400.0);
        assert!(l.back.is_some() && l.forward.is_some());
        assert!(l.slots[1].is_none());
        assert_eq!(l.slots[2].unwrap().x, l.list_start);
    }

    #[test]
    fn saved_names_round_trip_and_an_unknown_one_is_floating() {
        for mode in DockMode::ALL {
            assert_eq!(DockMode::from_saved(mode.saved_name()), mode);
            assert_eq!(DockMode::from_display_name(mode.display_name()), Some(mode));
        }
        assert_eq!(DockMode::from_saved("RIGHT"), DockMode::Right);
        assert_eq!(DockMode::from_saved(""), DockMode::Floating);
        assert_eq!(DockMode::from_saved("sideways"), DockMode::Floating);
        assert_eq!(DockMode::from_display_name("Sideways"), None);
    }

    const SCREEN: (f32, f32) = (800.0, 600.0);

    fn floated(at: Rect, pointer: (f32, f32)) -> DockMode {
        dragged_mode(DockMode::Floating, at, SCREEN, pointer, 0.0)
    }

    #[test]
    fn a_floating_dock_snaps_to_the_edge_nearest_the_pointer() {
        let tall = |x, y| Rect::new(x, y, 48.0, 100.0);
        assert_eq!(floated(tall(6.0, 200.0), (16.0, 210.0)), DockMode::Left);
        assert_eq!(floated(tall(746.0, 200.0), (756.0, 210.0)), DockMode::Right);
        assert_eq!(floated(tall(300.0, 5.0), (310.0, 10.0)), DockMode::Top);
        assert_eq!(
            floated(tall(300.0, 496.0), (310.0, 590.0)),
            DockMode::Bottom
        );
        assert_eq!(
            floated(tall(300.0, 200.0), (310.0, 210.0)),
            DockMode::Floating
        );
    }

    #[test]
    fn a_dock_touching_an_edge_far_from_the_pointer_does_not_snap_there() {
        // A tall dock carried across the screen: its bottom is on the edge,
        // the pointer on its handle near the top.
        let at = Rect::new(300.0, 100.0, 48.0, 496.0);
        assert_eq!(floated(at, (310.0, 110.0)), DockMode::Floating);
        // Carried on down, past where it can go, the pointer comes near.
        assert_eq!(floated(at, (310.0, 590.0)), DockMode::Bottom);
    }

    #[test]
    fn a_dock_as_long_as_the_screen_goes_to_the_end_the_pointer_is_near() {
        let at = Rect::new(300.0, 0.0, 48.0, 600.0);
        assert_eq!(floated(at, (310.0, 5.0)), DockMode::Top);
        assert_eq!(floated(at, (310.0, 595.0)), DockMode::Bottom);
        // Nearer a side it does not touch, it carries on floating.
        assert_eq!(floated(at, (100.0, 300.0)), DockMode::Floating);
    }

    #[test]
    fn in_a_corner_the_pointers_edge_wins() {
        let at = Rect::new(2.0, 2.0, 48.0, 100.0);
        assert_eq!(floated(at, (5.0, 9.0)), DockMode::Left);
        assert_eq!(floated(at, (9.0, 5.0)), DockMode::Top);
    }

    #[test]
    fn a_horizontal_floating_dock_snaps_like_a_vertical_one() {
        let at = Rect::new(200.0, 496.0, 200.0, 100.0);
        assert_eq!(
            dragged_mode(
                DockMode::FloatingHorizontal,
                at,
                SCREEN,
                (205.0, 590.0),
                0.0
            ),
            DockMode::Bottom
        );
    }

    #[test]
    fn a_rail_stays_until_the_pointer_is_pulled_away_and_floats_running_as_it_ran() {
        let at = Rect::new(0.0, 0.0, 46.0, 46.0);
        let rail = |mode, pointer| dragged_mode(mode, at, SCREEN, pointer, 10.0);
        assert_eq!(rail(DockMode::Left, (42.0, 300.0)), DockMode::Left);
        assert_eq!(rail(DockMode::Left, (43.0, 300.0)), DockMode::Floating);
        assert_eq!(rail(DockMode::Right, (758.0, 300.0)), DockMode::Right);
        assert_eq!(rail(DockMode::Right, (757.0, 300.0)), DockMode::Floating);
        assert_eq!(rail(DockMode::Top, (400.0, 42.0)), DockMode::Top);
        assert_eq!(
            rail(DockMode::Top, (400.0, 43.0)),
            DockMode::FloatingHorizontal
        );
        assert_eq!(rail(DockMode::Bottom, (400.0, 558.0)), DockMode::Bottom);
        assert_eq!(
            rail(DockMode::Bottom, (400.0, 557.0)),
            DockMode::FloatingHorizontal
        );
    }

    #[test]
    fn a_rail_goes_by_the_pointer_so_one_snapped_by_its_far_end_stays() {
        // Snapped to the bottom with the pointer 138 above the edge: it holds
        // until the pointer is 32 further away still.
        let at = Rect::new(300.0, 554.0, 200.0, 46.0);
        let rail = |y| dragged_mode(DockMode::Bottom, at, SCREEN, (310.0, y), 138.0);
        assert_eq!(rail(450.0), DockMode::Bottom);
        assert_eq!(rail(429.0), DockMode::FloatingHorizontal);
    }

    fn along(mode: DockMode, l: &DockLayout) -> f32 {
        if mode.is_horizontal() {
            l.width
        } else {
            l.height
        }
    }

    /// The layout of a dock limited to `limit` slots, on a long screen.
    fn limited(
        mode: DockMode,
        size: DockSize,
        grip: bool,
        owners: &[&str],
        limit: usize,
        first: usize,
    ) -> DockLayout {
        let most = length_for_slots(mode, size, grip, owners, Some(limit));
        super::compute(mode, size, false, grip, owners, most, first)
    }

    /// Plugins with one window, two or several, so its pages differ in length.
    const MIXED: [&str; 12] = ["a", "b", "c", "c", "d", "e", "e", "e", "f", "g", "h", "h"];

    #[test]
    fn a_limit_shows_that_many_slots_wherever_the_list_has_scrolled_to() {
        for mode in DockMode::ALL {
            for size in DockSize::ALL {
                for grip in [false, true] {
                    for limit in 1..MIXED.len() {
                        let start = limited(mode, size, grip, &MIXED, limit, 0);
                        for first in 0..=start.max_first {
                            let l = limited(mode, size, grip, &MIXED, limit, first);
                            assert!(l.visible >= limit, "{mode:?} {size:?} {limit} {first}");
                            assert_eq!(along(mode, &l), along(mode, &start));
                        }
                    }
                }
            }
        }
    }

    #[test]
    fn no_limit_or_one_the_list_fits_in_changes_nothing() {
        for mode in DockMode::ALL {
            for limit in [None, Some(12), Some(50)] {
                assert_eq!(
                    length_for_slots(mode, DockSize::Default, true, &MIXED, limit),
                    f32::INFINITY
                );
            }
        }
        assert_eq!(
            length_for_slots(DockMode::Left, DockSize::Default, false, &[], Some(3)),
            f32::INFINITY
        );
    }

    #[test]
    fn the_smallest_limit_is_one_slot_with_its_arrows() {
        let owners = twelve();
        let owners = refs(&owners);
        let l = limited(DockMode::Floating, DockSize::Default, false, &owners, 1, 0);
        // 6 + 36 + 9 + 16 + 36 + 16 + 9 + 36 + 6
        assert_eq!(l.height, 170.0);
        assert_eq!(l.visible, 1);
        let zero = length_for_slots(
            DockMode::Floating,
            DockSize::Default,
            false,
            &owners,
            Some(0),
        );
        assert_eq!(zero, 170.0);
    }

    #[test]
    fn a_limit_is_turned_on_its_side_with_the_dock() {
        let v = limited(DockMode::Left, DockSize::Default, false, &MIXED, 4, 2);
        let h = limited(DockMode::Top, DockSize::Default, false, &MIXED, 4, 2);
        assert_eq!((h.width, h.height), (v.height, v.width));
        assert_eq!(
            h.slots,
            v.slots.iter().map(|s| s.map(turned)).collect::<Vec<_>>()
        );
    }

    #[test]
    fn a_length_gives_back_the_limit_that_reaches_it() {
        for mode in DockMode::ALL {
            for size in DockSize::ALL {
                for grip in [false, true] {
                    let most = |limit: Option<usize>| {
                        let l = length_for_slots(mode, size, grip, &MIXED, limit);
                        let l = super::compute(mode, size, false, grip, &MIXED, l, 0);
                        along(mode, &l)
                    };
                    let back = |length| {
                        slots_for_length(
                            mode,
                            size,
                            grip,
                            &MIXED,
                            f32::INFINITY,
                            f32::INFINITY,
                            length,
                        )
                    };
                    for limit in 1..MIXED.len() {
                        let reached = most(Some(limit));
                        let found = back(reached);
                        // A limit with the same length as the one above it shows
                        // the same slots: a tight run leaves no room for its arrows.
                        assert_eq!(
                            found.map_or(most(None), |f| most(Some(f))),
                            reached,
                            "{mode:?} {size:?} {limit}"
                        );
                    }
                    assert_eq!(back(most(None)), None, "{mode:?} {size:?}");
                    assert_eq!(back(5000.0), None, "{mode:?} {size:?}");
                    assert_eq!(back(0.0), Some(1), "{mode:?} {size:?}");
                }
            }
        }
    }

    #[test]
    fn a_length_goes_to_the_nearer_limit() {
        let owners = twelve();
        let owners = refs(&owners);
        let back = |length| {
            slots_for_length(
                DockMode::Floating,
                DockSize::Default,
                false,
                &owners,
                f32::INFINITY,
                f32::INFINITY,
                length,
            )
        };
        // Each slot and its divider is 45, from 170 for one.
        assert_eq!(back(170.0), Some(1));
        assert_eq!(back(192.0), Some(1));
        assert_eq!(back(193.0), Some(2));
        assert_eq!(back(215.0), Some(2));
    }

    #[test]
    fn a_limit_never_reaches_past_the_screen() {
        let owners = twelve();
        let owners = refs(&owners);
        let back = |length, room| {
            slots_for_length(
                DockMode::Floating,
                DockSize::Default,
                false,
                &owners,
                f32::INFINITY,
                room,
                length,
            )
        };
        // 300 is 260 for three slots and 305 for four.
        assert_eq!(back(304.0, 300.0), Some(3));
        // With no room even for one, one is what it shows.
        assert_eq!(back(90.0, 80.0), Some(1));
    }

    #[test]
    fn a_dock_dragged_as_long_as_the_screen_lets_it_has_no_limit() {
        let owners = twelve();
        let owners = refs(&owners);
        assert_eq!(
            slots_for_length(
                DockMode::Floating,
                DockSize::Default,
                false,
                &owners,
                400.0,
                400.0,
                400.0,
            ),
            None
        );
        // But not when it is where it is that stops it: on its own it would grow
        // on up the screen. 395 is six slots.
        assert_eq!(
            slots_for_length(
                DockMode::Floating,
                DockSize::Default,
                false,
                &owners,
                700.0,
                400.0,
                400.0,
            ),
            Some(6)
        );
    }

    #[test]
    fn room_comes_from_the_screen_and_a_floating_dock_keeps_clear_of_its_ends() {
        let (w, h) = (1280.0, 720.0);
        assert_eq!(max_length(DockMode::Left, w, h), 720.0);
        assert_eq!(max_length(DockMode::Bottom, w, h), 1280.0);
        assert_eq!(max_length(DockMode::Floating, w, h), 672.0);
        assert_eq!(max_length(DockMode::FloatingHorizontal, w, h), 1232.0);
    }
}
