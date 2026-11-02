//! The plugin dock: a tile that opens and closes it, one slot per plugin window
//! that asked for one, a gear that opens and closes the Appearance window, and
//! while windows are unlocked a grip that moves it. A slot opens or closes its
//! window and shows the first of these it can use: the window's own PNG, its
//! plugin's `icon.png`, the window's render surface, a monogram. It floats
//! running either way, or snaps to a screen edge as a rail. It draws the same
//! shape in every theme, in the theme's colours or its own under Classic; where
//! things go is worked out by [`layout`] and how they look by [`style`].
//!
//! With more windows than fit, the slots scroll with the wheel or the arrows at
//! each end of the list, and a bar beside them says how far through it they are.
//! While windows are unlocked a handle at its far end limits how many slots it
//! shows, by a whole slot at a time, and the rest scroll the same way. Dragged out
//! until they all show, or as long as the screen lets it be, it has no limit.
//!
//! Closed, the dock is only the tile, with a count of the plugin windows open,
//! and those windows stay as they are. The state is saved per character with
//! the dock's place. With hovering turned on, the pointer on a closed dock also
//! peeks it open until the pointer leaves, which saves nothing: while it
//! peeks, the layout it reports is the closed one. Opening and closing take a
//! moment, in which the dock runs from where it was to where it ends up, showing
//! as much of itself as has room.
//!
//! The root sets a dragged window's place from the pointer on every move, so
//! the dock reads that place each frame, decides its mode, and puts itself
//! back on its edge. Pointer input arrives before the frame's tick, so a snap
//! shows in the frame of the move that caused it.

pub mod layout;
pub mod style;

use std::sync::Arc;

use ac_plugin_ui::markup::normalize_icon;
use ac_plugin_ui::{PanelView, PluginIcon, UiInput};

use self::layout::{ARROW_LENGTH, DockLayout, LENGTH_HANDLE, Metrics, ScrollBar};
pub use self::layout::{DockMode, DockOpens, DockSize};
use self::style::DockSide;
use super::style::{PointerState, with_alpha};
use super::widgets::ButtonLook;
use super::{Icon, Palette, PluginOutbox, PluginTheme};
use crate::bundled_font::ModernFonts;
use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext};
use crate::event::{UiEvent, kind};
use crate::font::UiFont;
use crate::geometry::{AnchorEdges, Rect, ResizeEdges};
use crate::host::PanelController;
use crate::layout_persistence::WindowState;
use crate::root::{ElementMessage, UiRoot};
use crate::tree::{Element, ElementId, Widget};

/// The dock's window name. It keeps the name it was first saved under, so
/// placements the player has already saved still apply.
pub const WINDOW: &str = "plugin-shelf";

/// Where the dock goes first: its top.
pub const DEFAULT_TOP: f32 = 116.0;
/// Where the dock goes first: its left edge.
pub const DEFAULT_LEFT: f32 = 10.0;
/// The gear's hover label.
pub const APPEARANCE_LABEL: &str = "Plugin appearance";
/// The tile's hover label.
pub const TILE_LABEL: &str = "Plugins";
/// How long the pointer must stay on a closed dock before it peeks open, seconds.
pub const PEEK_OPEN_SECONDS: f32 = 0.12;
/// How long the pointer must stay away before a peeking dock closes, seconds.
pub const PEEK_CLOSE_SECONDS: f32 = 0.5;
/// How long the dock takes to open or close, seconds.
pub const REVEAL_SECONDS: f32 = 0.14;

/// Where the dock's slot images come from: the window's textures.
pub trait DockIcons {
    /// A decoded PNG a plugin ships as a texture.
    fn file(&mut self, icon: &PluginIcon) -> Option<Icon>;
    /// A render surface (a full image id) as a texture.
    fn surface(&mut self, id: u32) -> Option<Icon>;
}

/// The picture on a window's slot, for a dock in a light theme or not: the first
/// of the window's own PNG, its plugin's `icon.png` and its render surface that
/// gives a texture. A file is drawn without asking for what comes after it.
fn slot_icon(panel: &PanelView, light: bool, icons: &mut dyn DockIcons) -> Option<Icon> {
    let own = panel.window_icon.as_ref().and_then(|w| w.for_theme(light));
    if let Some(icon) = own.and_then(|file| icons.file(file)) {
        return Some(icon);
    }
    if let Some(icon) = panel.file_icon.as_ref().and_then(|file| icons.file(file)) {
        return Some(icon);
    }
    let surface = normalize_icon(panel.descriptor.icon_surface_id);
    (surface != 0).then(|| icons.surface(surface)).flatten()
}

/// The letters a slot shows without an icon, two at most: the plugin's own,
/// else the title's first two letters (one word) or its first two words'
/// first letters, upper case.
#[must_use]
pub fn initials(requested: Option<&str>, title: &str) -> String {
    if let Some(r) = requested.map(str::trim).filter(|r| !r.is_empty()) {
        return r.chars().take(2).collect();
    }
    let words: Vec<&str> = title
        .split(' ')
        .map(str::trim)
        .filter(|w| !w.is_empty())
        .collect();
    match words.as_slice() {
        [] => "?".to_owned(),
        [one] => one.chars().take(2).collect::<String>().to_uppercase(),
        many => many
            .iter()
            .take(2)
            .filter_map(|w| w.chars().next())
            .flat_map(char::to_uppercase)
            .collect(),
    }
}

/// An arrow's hover label: how many slots are off the list that way, which the
/// player knows as plugins.
fn more_label(more: usize) -> String {
    if more == 1 {
        "1 more plugin".to_owned()
    } else {
        format!("{more} more plugins")
    }
}

/// A slot's hover label: the window's title, and its plugin's name under it
/// unless the two are the same.
fn label_text<'a>(title: &'a str, owner: &'a str) -> (&'a str, Option<&'a str>) {
    (title, (title != owner).then_some(owner))
}

#[derive(Debug, Clone)]
struct Entry {
    panel: u32,
    slot: ElementId,
    /// The window's name.
    window: String,
    /// How long its edge pill is now; it eases toward its state's length.
    pill: f32,
    owner: String,
    owner_name: String,
    title: String,
    open: bool,
}

/// What the window gives the dock to draw with: the tile's icon, the font of its
/// count, and the letters' fonts for the sizes that do not use the title font.
#[derive(Debug, Clone, Default)]
pub struct DockArt {
    /// The OpenAC icon.
    pub icon: Option<Icon>,
    /// The count on a closed tile.
    pub count_font: Option<Arc<UiFont>>,
    /// Monograms on the smaller dock.
    pub smaller_letters: Option<Arc<UiFont>>,
    /// Monograms on the bigger dock.
    pub bigger_letters: Option<Arc<UiFont>>,
}

/// The dock on its way open or closed.
#[derive(Debug, Clone, Copy)]
struct Reveal {
    from: Rect,
    to: Rect,
    elapsed: f32,
    closing: bool,
}

/// The dock's controller: its entries and how it lays them out.
#[derive(Debug)]
pub struct PluginDock {
    /// The dock (its window's root).
    pub root: ElementId,
    grip: ElementId,
    tile: ElementId,
    gear: ElementId,
    back: ElementId,
    forward: ElementId,
    length_handle: ElementId,
    entries: Vec<Entry>,
    mode: DockMode,
    size: DockSize,
    /// The player closed it, which is saved. It can still be open while
    /// `peeking`.
    minimized: bool,
    opens: DockOpens,
    /// The pointer has it open for now.
    peeking: bool,
    /// How long the pointer has wanted the peek changed, seconds.
    peek_wait: f32,
    /// A press closed it with the pointer still on it: no peek until the pointer
    /// has left.
    peek_held_off: bool,
    /// The player hid it (Toggle Plugin Manager).
    hidden: bool,
    /// A screen that has no in-game windows covers it.
    covered: bool,
    font: Option<Arc<UiFont>>,
    modern: Option<ModernFonts>,
    art: DockArt,
    outbox: PluginOutbox,
    theme: PluginTheme,
    /// Whether the slots' icons were last picked for a light theme.
    icons_light: bool,
    first: usize,
    /// How many slots it shows at most; none for as many as the screen has room
    /// for.
    slot_limit: Option<usize>,
    layout: DockLayout,
    screen: (f32, f32),
    /// The dock is being dragged, with the closest the pointer has been to its
    /// rail's edge since it was grabbed or snapped there.
    dragging: Option<f32>,
    /// The pointer has moved since the drag began.
    moved_in_drag: bool,
    /// Where a closed dock was when it opened, if that made it move to fit:
    /// it goes back there when it closes.
    closed_at: Option<(f32, f32)>,
    /// Whether it was last laid out closed, and in which mode.
    laid_out_as: (bool, DockMode),
    /// How long opening and closing take, seconds.
    reveal_seconds: f32,
    /// It is on its way open or closed.
    reveal: Option<Reveal>,
    /// The mode it had when the drag began.
    mode_before_drag: DockMode,
    /// A drag ended in another mode since the window last asked.
    mode_dragged: bool,
    /// The length handle is being dragged, with the limit it had when it was
    /// taken hold of.
    stretching: Option<Option<usize>>,
    /// The pointer has moved since the length handle was taken hold of.
    moved_in_stretch: bool,
    /// A drag of the length handle ended on another limit since the window last
    /// asked.
    limit_stretched: bool,
    /// The player right-clicked the dock or pressed the gear since the window
    /// last asked.
    appearance_requested: bool,
}

impl PluginDock {
    /// Builds the dock (hidden until it has entries) at its first place; the
    /// caller registers its window.
    pub fn build(
        ui: &mut UiRoot,
        font: Option<Arc<UiFont>>,
        modern: Option<ModernFonts>,
        outbox: PluginOutbox,
    ) -> Self {
        let mode = DockMode::default();
        let grip = !ui.ui_locked();
        let layout = layout::compute(
            mode,
            DockSize::default(),
            false,
            grip,
            &[],
            f32::INFINITY,
            0,
        );
        let mut e = Element::at(DEFAULT_LEFT, DEFAULT_TOP, layout.width, layout.height)
            .with_anchors(AnchorEdges::NONE)
            .with_visible(false);
        e.name = Some("PluginDock".to_owned());
        // The dock's size is worked out here, so a restored layout's saved
        // size never applies.
        e.resize_x = false;
        e.resize_y = false;
        let root = ui.insert(e, DockFrame::default());
        let mut g = Element::at(0.0, 0.0, 0.0, 0.0).with_anchors(AnchorEdges::NONE);
        g.window_move_handle = true;
        g.name = Some("PluginDockGrip".to_owned());
        let grip = ui.add(root, g, DockGrip::default());
        let mut t = Element::at(0.0, 0.0, 0.0, 0.0).with_anchors(AnchorEdges::NONE);
        t.name = Some("PluginDockTile".to_owned());
        let tile = ui.add(root, t, DockTile::default());
        let mut c = Element::at(0.0, 0.0, 0.0, 0.0).with_anchors(AnchorEdges::NONE);
        c.name = Some("PluginDockGear".to_owned());
        let gear = ui.add(root, c, DockGear::default());
        let mut arrow = |name: &str, forward: bool| {
            let mut a = Element::at(0.0, 0.0, 0.0, 0.0).with_anchors(AnchorEdges::NONE);
            a.name = Some(name.to_owned());
            let widget = DockArrow {
                forward,
                ..DockArrow::default()
            };
            ui.add(root, a, widget)
        };
        let back = arrow("PluginDockBack", false);
        let forward = arrow("PluginDockForward", true);
        let mut h = Element::at(0.0, 0.0, 0.0, 0.0).with_anchors(AnchorEdges::NONE);
        h.name = Some("PluginDockLengthHandle".to_owned());
        let length_handle = ui.add(root, h, DockLengthHandle::default());
        let mut dock = Self {
            root,
            grip,
            tile,
            gear,
            back,
            forward,
            length_handle,
            entries: Vec::new(),
            mode,
            size: DockSize::default(),
            minimized: false,
            opens: DockOpens::default(),
            peeking: false,
            peek_wait: 0.0,
            peek_held_off: false,
            hidden: false,
            covered: false,
            font,
            modern,
            art: DockArt::default(),
            outbox,
            theme: PluginTheme::Classic,
            icons_light: false,
            first: 0,
            slot_limit: None,
            layout,
            screen: ui.size(),
            dragging: None,
            moved_in_drag: false,
            closed_at: None,
            laid_out_as: (false, mode),
            reveal_seconds: REVEAL_SECONDS,
            reveal: None,
            mode_before_drag: mode,
            mode_dragged: false,
            stretching: None,
            moved_in_stretch: false,
            limit_stretched: false,
            appearance_requested: false,
        };
        dock.reflow(ui);
        dock
    }

    /// What it draws with.
    pub fn set_art(&mut self, ui: &mut UiRoot, art: DockArt) {
        self.art = art;
        self.reflow(ui);
    }

    /// How big it is drawn.
    #[must_use]
    pub fn size(&self) -> DockSize {
        self.size
    }

    /// Draws it at `size`.
    pub fn set_size(&mut self, ui: &mut UiRoot, size: DockSize) {
        if self.size != size {
            self.size = size;
            self.reflow(ui);
        }
    }

    /// How many slots it shows at most, if it is limited.
    #[must_use]
    pub fn slot_limit(&self) -> Option<usize> {
        self.slot_limit
    }

    /// Shows at most `limit` slots, and scrolls the rest; none for as many as the
    /// screen has room for.
    pub fn set_slot_limit(&mut self, ui: &mut UiRoot, limit: Option<usize>) {
        if self.slot_limit != limit {
            self.slot_limit = limit;
            self.reflow(ui);
        }
    }

    /// Whether the dock shows: it has a window to offer and the player has
    /// not hidden it (OpenAC `PluginSidePanel.Visible`).
    #[must_use]
    pub fn is_shown(&self) -> bool {
        !self.entries.is_empty() && !self.hidden
    }

    /// The player hides (`true`) or shows the dock (OpenAC `Hide`, `Show`).
    /// Showing it again also expands it, so the key always brings the slots
    /// back.
    pub fn set_hidden(&mut self, ui: &mut UiRoot, hidden: bool) {
        self.hidden = hidden;
        if !hidden {
            self.set_minimized(ui, false);
        }
        self.reflow(ui);
    }

    /// While a screen with no in-game windows shows (`covered`), the dock
    /// leaves its window as that screen left it, and does not bring itself
    /// back or raise itself.
    pub fn set_covered(&mut self, covered: bool) {
        self.covered = covered;
    }

    /// How many windows have a slot.
    #[must_use]
    pub fn entry_count(&self) -> usize {
        self.entries.len()
    }

    /// The slot of panel `panel`.
    #[must_use]
    pub fn slot(&self, panel: u32) -> Option<ElementId> {
        self.entries
            .iter()
            .find(|e| e.panel == panel)
            .map(|e| e.slot)
    }

    /// Where its parts are now.
    #[must_use]
    pub fn layout(&self) -> &DockLayout {
        &self.layout
    }

    /// Whether the player has closed it, peeking or not.
    #[must_use]
    pub fn is_minimized(&self) -> bool {
        self.minimized
    }

    /// Whether it shows closed now.
    fn closed(&self) -> bool {
        self.minimized && !self.peeking
    }

    /// Whether it is laid out closed: on its way closed it keeps its slots until
    /// it gets there, so they go with it.
    fn laid_out_closed(&self) -> bool {
        self.closed() && !self.reveal.is_some_and(|r| r.closing)
    }

    /// How it opens.
    #[must_use]
    pub fn opens(&self) -> DockOpens {
        self.opens
    }

    /// Sets how it opens. Turned off, a peek ends at once.
    pub fn set_opens(&mut self, ui: &mut UiRoot, opens: DockOpens) {
        self.opens = opens;
        if opens == DockOpens::Click && self.peeking {
            self.change_openness(ui, |dock| dock.peeking = false);
        }
    }

    /// Where it is and which way it runs.
    #[must_use]
    pub fn mode(&self) -> DockMode {
        self.mode
    }

    /// Puts the dock in `mode`, as saved or picked: a rail goes to its edge, and a floating dock
    /// that was a rail goes 10 points in from the edge it was on. It keeps its
    /// place along the edge.
    pub fn set_mode(&mut self, ui: &mut UiRoot, mode: DockMode) {
        let from = self.mode;
        if from == mode {
            return;
        }
        self.mode = mode;
        self.reflow(ui);
        let Some((left, top)) = ui.element(self.root).map(|e| (e.left, e.top)) else {
            return;
        };
        let (sw, sh) = self.screen;
        let (w, h) = (self.layout.width, self.layout.height);
        let (left, top) = match from {
            DockMode::Left => (DEFAULT_LEFT, top),
            DockMode::Right => (sw - w - DEFAULT_LEFT, top),
            DockMode::Top => (left, DEFAULT_LEFT),
            DockMode::Bottom => (left, sh - h - DEFAULT_LEFT),
            DockMode::Floating | DockMode::FloatingHorizontal => (left, top),
        };
        if !mode.is_rail() {
            ui.move_window(WINDOW, left, top);
        }
    }

    /// The limit a drag of the length handle left the dock with, once, when the
    /// drag has ended (the window saves it). A drag in progress does not change
    /// what is saved.
    pub fn take_stretched_limit(&mut self) -> Option<Option<usize>> {
        std::mem::take(&mut self.limit_stretched).then_some(self.slot_limit)
    }

    /// The mode a drag left the dock in, once, when the drag has ended (the
    /// window saves it). A drag in progress does not change what is saved.
    pub fn take_dragged_mode(&mut self) -> Option<DockMode> {
        std::mem::take(&mut self.mode_dragged).then_some(self.mode)
    }

    /// Whether the player right-clicked the dock or pressed the gear since
    /// the last call (the window opens or closes the Appearance window).
    pub fn take_appearance_request(&mut self) -> bool {
        std::mem::take(&mut self.appearance_requested)
    }

    /// The plugin theme: the dock takes its colours and fonts.
    pub fn set_theme(&mut self, ui: &mut UiRoot, theme: PluginTheme) {
        if self.theme == theme {
            return;
        }
        self.theme = theme;
        self.reflow(ui);
    }

    /// The colours it draws in: the theme's, or its own under Classic.
    fn palette(&self) -> Palette {
        self.theme.palette().unwrap_or(Palette::CLASSIC_DOCK)
    }

    /// The font for monograms and label titles: the bundled title weight
    /// under a theme, the interface font in Classic.
    fn title_font(&self) -> Option<Arc<UiFont>> {
        match (&self.modern, self.theme) {
            (Some(m), theme) if theme != PluginTheme::Classic => Some(Arc::clone(&m.semibold)),
            _ => self.font.clone(),
        }
    }

    /// The font for a monogram's letters, which fits the art at this size.
    fn letters_font(&self) -> Option<Arc<UiFont>> {
        let sized = match self.size {
            DockSize::Smaller => self.art.smaller_letters.clone(),
            DockSize::Default => None,
            DockSize::Bigger => self.art.bigger_letters.clone(),
        };
        sized
            .filter(|_| self.theme != PluginTheme::Classic)
            .or_else(|| self.title_font())
    }

    /// The font for the line under a label's title.
    fn body_font(&self) -> Option<Arc<UiFont>> {
        match (&self.modern, self.theme) {
            (Some(m), theme) if theme != PluginTheme::Classic => Some(Arc::clone(&m.regular)),
            _ => self.font.clone(),
        }
    }

    /// The panels with a slot: the windows that asked for one, in
    /// registration order, each open or not, with its icon ([`slot_icon`]). A
    /// change between a light theme and a dark one draws each window's PNG in
    /// its version for the theme.
    pub fn sync(&mut self, ui: &mut UiRoot, panels: &[PanelView], icons: &mut dyn DockIcons) {
        let wanted: Vec<&PanelView> = panels
            .iter()
            .filter(|p| p.descriptor.show_in_side_panel)
            .collect();
        let same = wanted.len() == self.entries.len()
            && wanted
                .iter()
                .zip(&self.entries)
                .all(|(p, e)| p.id == e.panel);
        if !same {
            for entry in self.entries.drain(..) {
                ui.destroy(entry.slot);
            }
            for panel in &wanted {
                let mut e = Element::at(0.0, 0.0, 0.0, 0.0).with_anchors(AnchorEdges::NONE);
                e.name = Some(format!("PluginDockSlot:{}", panel.window_name()));
                let letters = initials(
                    panel.descriptor.icon_text.as_deref(),
                    &panel.descriptor.title,
                );
                let slot = DockSlot {
                    letters,
                    hue: style::monogram_hue(&panel.owner.id),
                    ..DockSlot::default()
                };
                let id = ui.add(self.root, e, slot);
                self.entries.push(Entry {
                    panel: panel.id,
                    slot: id,
                    window: panel.window_name(),
                    pill: 0.0,
                    owner: panel.owner.id.clone(),
                    owner_name: panel.owner.display_name.clone(),
                    title: panel.descriptor.title.clone(),
                    open: false,
                });
            }
        }
        let light = self.palette().is_light();
        let resolve = !same || self.icons_light != light;
        self.icons_light = light;
        for (entry, panel) in self.entries.iter_mut().zip(&wanted) {
            entry.open = panel.shown();
            entry.title.clone_from(&panel.descriptor.title);
            if resolve {
                let icon = slot_icon(panel, light, icons);
                if let Some(slot) = ui.widget_mut::<DockSlot>(entry.slot) {
                    slot.icon = icon;
                }
            }
        }
        self.reflow(ui);
    }

    /// Lays the dock out for its mode and the screen, and tells its parts how
    /// to look.
    fn reflow(&mut self, ui: &mut UiRoot) {
        // The slide was for the old screen or mode: the dock goes straight to
        // where it was heading, and the layout below fits that to the new
        // one. Stopped partway, that place would stay, and be saved.
        if self.screen != ui.size() || self.laid_out_as.1 != self.mode {
            self.finish_reveal(ui);
        }
        self.screen = ui.size();
        let owners: Vec<&str> = self.entries.iter().map(|e| e.owner.as_str()).collect();
        let closed = self.laid_out_closed();
        let grip = !ui.ui_locked();
        let longest = layout::max_length(self.mode, self.screen.0, self.screen.1).min(
            layout::length_for_slots(self.mode, self.size, grip, &owners, self.slot_limit),
        );
        let l = layout::compute(
            self.mode, self.size, closed, grip, &owners, longest, self.first,
        );
        self.first = l.first;
        let palette = self.palette();
        let metrics = self.size.metrics();
        let letters = self.letters_font();
        for (entry, slot) in self.entries.iter().zip(&l.slots) {
            if let Some(r) = slot {
                place(ui, entry.slot, *r);
            }
            ui.set_visible(entry.slot, slot.is_some());
            if let Some(w) = ui.widget_mut::<DockSlot>(entry.slot) {
                w.palette = palette;
                w.mode = self.mode;
                w.open = entry.open;
                w.metrics = metrics;
                w.font.clone_from(&letters);
                // A slot put away is not left looking hovered for when it
                // comes back.
                if slot.is_none() {
                    w.pointer = PointerState::default();
                }
            }
        }
        if let Some(r) = l.grip {
            place(ui, self.grip, r);
        }
        ui.set_visible(self.grip, l.grip.is_some());
        place(ui, self.tile, l.tile);
        place(ui, self.gear, l.gear);
        ui.set_visible(self.gear, !closed);
        let after = self.entries.len() - (l.first + l.visible).min(self.entries.len());
        for (id, rect, more) in [
            (self.back, l.back, l.first),
            (self.forward, l.forward, after),
        ] {
            if let Some(r) = rect {
                place(ui, id, r);
            }
            ui.set_visible(id, rect.is_some());
            if let Some(w) = ui.widget_mut::<DockArrow>(id) {
                w.palette = palette;
                w.mode = self.mode;
                w.metrics = metrics;
                w.more = more;
                if rect.is_none() {
                    w.pointer = PointerState::default();
                }
            }
        }
        // A dock on the screen's right or bottom edge keeps that edge when its
        // size changes without opening, closing or changing mode (the grip
        // coming or going, a new size), as one on the left or top edge keeps
        // that one. Opening and closing place it themselves, and the length
        // handle is the end that moves.
        let same = self.laid_out_as == (closed, self.mode) && !self.length_handle_held(ui);
        self.laid_out_as = (closed, self.mode);
        let (sw, sh) = self.screen;
        if let Some(e) = ui.element_mut(self.root) {
            let on_right = e.left > 0.0 && e.left + e.width >= sw - 0.5;
            let on_bottom = e.top > 0.0 && e.top + e.height >= sh - 0.5;
            e.width = l.width;
            e.height = l.height;
            if same && on_right {
                e.left = sw - l.width;
            }
            if same && on_bottom {
                e.top = sh - l.height;
            }
        }
        let open_count = self.entries.iter().filter(|e| e.open).count();
        if let Some(r) = l.length_handle {
            place(ui, self.length_handle, r);
        }
        ui.set_visible(self.length_handle, l.length_handle.is_some());
        if let Some(w) = ui.widget_mut::<DockLengthHandle>(self.length_handle) {
            w.palette = palette;
            w.mode = self.mode;
            w.metrics = metrics;
            if l.length_handle.is_none() {
                w.pointer = PointerState::default();
            }
        }
        if let Some(w) = ui.widget_mut::<DockGrip>(self.grip) {
            w.palette = palette;
            w.mode = self.mode;
            w.metrics = metrics;
        }
        let (app_icon, count_font) = (self.art.icon, self.art.count_font.clone());
        if let Some(w) = ui.widget_mut::<DockTile>(self.tile) {
            w.palette = palette;
            w.mode = self.mode;
            w.metrics = metrics;
            w.icon = app_icon;
            w.count_font = count_font;
            w.count = if closed { open_count } else { 0 };
            w.closed = closed;
        }
        if let Some(w) = ui.widget_mut::<DockGear>(self.gear) {
            w.palette = palette;
            w.mode = self.mode;
            w.metrics = metrics;
        }
        if let Some(frame) = ui.widget_mut::<DockFrame>(self.root) {
            frame.palette = palette;
            frame.mode = self.mode;
            frame.metrics = metrics;
            frame.minimized = closed;
            frame.dividers.clone_from(&l.dividers);
            frame.scroll = l.scroll;
        }
        self.layout = l;
        self.mark_slots(ui);
        self.keep_in_place(ui);
        self.place_reveal(ui);
        self.follow_pointer(ui);
        self.apply_visibility(ui);
    }

    /// Tells the frame what to draw beside each slot.
    fn mark_slots(&self, ui: &mut UiRoot) {
        let marks: Vec<SlotMark> = self
            .entries
            .iter()
            .zip(&self.layout.slots)
            .filter_map(|(entry, slot)| {
                slot.map(|slot| SlotMark {
                    slot,
                    open: entry.open,
                    pill: entry.pill,
                })
            })
            .collect();
        if let Some(frame) = ui.widget_mut::<DockFrame>(self.root) {
            frame.marks = marks;
        }
    }

    /// Whether the window shows matches whether the dock should.
    fn apply_visibility(&mut self, ui: &mut UiRoot) {
        if self.covered {
            return;
        }
        let shown = self.is_shown();
        if ui.window(WINDOW).is_some() {
            if shown != ui.is_window_visible(WINDOW) {
                if shown {
                    ui.show_window(WINDOW);
                } else {
                    ui.hide_window(WINDOW);
                }
            }
        } else {
            ui.set_visible(self.root, shown);
        }
    }

    /// A rail stays on its edge, and a dock that grew, or a screen that
    /// shrank, leaves it where it all fits.
    fn keep_in_place(&self, ui: &mut UiRoot) {
        if self.reveal.is_some() {
            return;
        }
        let (sw, sh) = self.screen;
        let (w, h) = (self.layout.width, self.layout.height);
        let (far_x, far_y) = ((sw - w).max(0.0), (sh - h).max(0.0));
        if let Some(e) = ui.element_mut(self.root) {
            e.left = e.left.clamp(0.0, far_x);
            e.top = e.top.clamp(0.0, far_y);
            match self.mode {
                DockMode::Left => e.left = 0.0,
                DockMode::Right => e.left = far_x,
                DockMode::Top => e.top = 0.0,
                DockMode::Bottom => e.top = far_y,
                DockMode::Floating | DockMode::FloatingHorizontal => {}
            }
        }
    }

    /// Whether the pointer is on `r`, in the dock's space.
    fn pointer_on(&self, ui: &UiRoot, r: Rect) -> bool {
        let (mx, my) = ui.mouse();
        let (mx, my) = (mx as f32, my as f32);
        ui.element(self.root).is_some_and(|e| {
            let (x, y) = (mx - e.left - r.x, my - e.top - r.y);
            e.visible() && x >= 0.0 && x < r.w && y >= 0.0 && y < r.h
        })
    }

    /// Opens or closes the dock for the player. A press that closes it leaves the
    /// pointer on the tile, which must not peek it straight open again.
    fn set_minimized(&mut self, ui: &mut UiRoot, minimized: bool) {
        self.peek_held_off = minimized;
        self.change_openness(ui, |dock| {
            dock.minimized = minimized;
            dock.peeking = false;
        });
    }

    /// Makes `change`, and lays the dock out for it. A dock that has to move to
    /// fit when it opens goes back to where it was when it closes.
    fn change_openness(&mut self, ui: &mut UiRoot, change: impl FnOnce(&mut Self)) {
        let seen = self.placement(ui);
        self.end_reveal(ui);
        let was_closed = self.closed();
        let before = ui.element(self.root).map(|e| (e.left, e.top));
        change(self);
        self.reflow(ui);
        let closed = self.closed();
        if closed && !was_closed {
            if let (Some((left, top)), Some(e)) = (self.closed_at.take(), ui.element_mut(self.root))
            {
                e.left = left;
                e.top = top;
            }
            self.keep_in_place(ui);
        } else if !closed && was_closed {
            let after = ui.element(self.root).map(|e| (e.left, e.top));
            self.closed_at = before.filter(|_| before != after);
        }
        if closed != was_closed {
            self.begin_reveal(ui, seen, closed);
        }
    }

    /// Where the dock is on the screen and how big it is now.
    fn placement(&self, ui: &UiRoot) -> Option<Rect> {
        ui.element(self.root)
            .map(|e| Rect::new(e.left, e.top, e.width, e.height))
    }

    /// Runs the dock to where it is now, from `from`.
    fn begin_reveal(&mut self, ui: &mut UiRoot, from: Option<Rect>, closing: bool) {
        let (Some(from), Some(to)) = (from, self.placement(ui)) else {
            return;
        };
        if self.reveal_seconds <= 0.0 || !self.is_shown() {
            return;
        }
        self.reveal = Some(Reveal {
            from,
            to,
            elapsed: 0.0,
            closing,
        });
        self.reflow(ui);
    }

    /// Moves the dock on, and finishes when it is there. The player taking hold
    /// of it finishes it too.
    fn ease_reveal(&mut self, ui: &mut UiRoot, dt: f32) {
        let Some(reveal) = &mut self.reveal else {
            return;
        };
        reveal.elapsed += dt.max(0.0);
        let done = reveal.elapsed >= self.reveal_seconds || !self.is_shown();
        if done || ui.is_window_move_active() || self.length_handle_held(ui) {
            self.end_reveal(ui);
        } else {
            self.place_reveal(ui);
        }
    }

    /// Puts the dock where it has got to: fast at first, settling at the end.
    fn place_reveal(&self, ui: &mut UiRoot) {
        let Some(r) = self.reveal else {
            return;
        };
        let t = (r.elapsed / self.reveal_seconds).clamp(0.0, 1.0);
        let eased = 1.0 - (1.0 - t).powi(3);
        let at = |from: f32, to: f32| from + (to - from) * eased;
        if let Some(e) = ui.element_mut(self.root) {
            e.left = at(r.from.x, r.to.x);
            e.top = at(r.from.y, r.to.y);
            e.width = at(r.from.w, r.to.w);
            e.height = at(r.from.h, r.to.h);
        }
    }

    /// Puts the dock where it was going, and lays it out for that.
    fn end_reveal(&mut self, ui: &mut UiRoot) {
        if self.finish_reveal(ui) {
            self.reflow(ui);
        }
    }

    /// Puts the dock where it was going and forgets the slide, without
    /// laying it out (the caller does). False when it was not on its way.
    fn finish_reveal(&mut self, ui: &mut UiRoot) -> bool {
        let Some(r) = self.reveal.take() else {
            return false;
        };
        place(ui, self.root, r.to);
        true
    }

    /// With hovering on, the pointer on a closed dock peeks it open after a
    /// moment, and away from it closes it again after a longer one. The grip of a
    /// closed dock only moves it, so it does not count. Nothing peeks open while
    /// something else has the pointer's attention, and a peek stays open while it
    /// is being dragged.
    fn follow_peek(&mut self, ui: &mut UiRoot, dt: f32) {
        if self.opens != DockOpens::Hover || !self.minimized {
            self.peek_wait = 0.0;
            return;
        }
        let whole = Rect::new(0.0, 0.0, self.layout.width, self.layout.height);
        let on_grip = self.closed() && self.layout.grip.is_some_and(|g| self.pointer_on(ui, g));
        let over = self.pointer_on(ui, whole) && !on_grip;
        if !over {
            self.peek_held_off = false;
        }
        let held = self.length_handle_held(ui)
            || ui
                .widget::<DockGrip>(self.grip)
                .is_some_and(|g| g.held.is_some());
        let busy = held || ui.is_window_move_active() || ui.modal.is_some() || self.covered;
        let want = if busy {
            self.peeking
        } else {
            over && !self.peek_held_off
        };
        if want == self.peeking {
            self.peek_wait = 0.0;
            return;
        }
        self.peek_wait += dt.max(0.0);
        let delay = if want {
            PEEK_OPEN_SECONDS
        } else {
            PEEK_CLOSE_SECONDS
        };
        if self.peek_wait >= delay {
            self.peek_wait = 0.0;
            self.change_openness(ui, |dock| dock.peeking = want);
        }
    }

    /// While the grip is held the dock takes the mode its place and the pointer
    /// say. The drag's last move is followed in the frame after it ends.
    fn follow_drag(&mut self, ui: &mut UiRoot) {
        let press = ui.widget::<DockGrip>(self.grip).and_then(|g| g.held);
        let moving = ui.is_window_move_active() && press.is_some();
        // A whole drag can fall between two frames when a frame is slow; a
        // press and release in place cannot have moved the dock.
        let done_between_frames =
            !moving && self.dragging.is_none() && press.is_some_and(|start| start != ui.mouse());
        if !moving && self.dragging.is_none() && !done_between_frames {
            self.release_grip(ui);
            return;
        }
        let Some(at) = ui
            .element(self.root)
            .map(|e| Rect::new(e.left, e.top, e.width, e.height))
        else {
            return;
        };
        if self.dragging.is_none() {
            self.mode_before_drag = self.mode;
            self.moved_in_drag = false;
        }
        let (mx, my) = ui.mouse();
        let pointer = (mx as f32, my as f32);
        let from_edge = |mode, at| layout::pointer_from_edge(mode, self.screen, at);
        let start = press.map_or(pointer, |(x, y)| (x as f32, y as f32));
        let closest = self
            .dragging
            .or_else(|| from_edge(self.mode, start))
            .unwrap_or(f32::INFINITY);
        let closest = from_edge(self.mode, pointer).map_or(closest, |d| closest.min(d));
        // Pressing the grip is not yet moving the dock.
        self.moved_in_drag = self.moved_in_drag || pointer != start;
        let next = if self.moved_in_drag {
            self.closed_at = None;
            layout::dragged_mode(self.mode, at, self.screen, pointer, closest)
        } else {
            self.mode
        };
        let closest = if next == self.mode {
            closest
        } else {
            from_edge(next, pointer).unwrap_or(f32::INFINITY)
        };
        if next != self.mode {
            self.mode = next;
            self.reflow(ui);
        }
        self.dragging = moving.then_some(closest);
        if !moving {
            self.mode_dragged = self.mode != self.mode_before_drag;
            self.release_grip(ui);
        }
    }

    /// Whether the length handle has the pointer.
    fn length_handle_held(&self, ui: &UiRoot) -> bool {
        ui.widget::<DockLengthHandle>(self.length_handle)
            .is_some_and(|h| h.grabbed.is_some())
    }

    /// While the length handle is held the dock takes the limit that brings its
    /// far end nearest the pointer, along its main axis and never off the screen.
    /// A press in place changes nothing. The drag's last move is followed in the
    /// frame after it ends, like the grip's.
    fn follow_length_handle(&mut self, ui: &mut UiRoot) {
        let Some((grabbed, press, released)) = ui
            .widget::<DockLengthHandle>(self.length_handle)
            .and_then(|h| h.grabbed.map(|g| (g.along, g.press, h.released)))
        else {
            return;
        };
        let before = *self.stretching.get_or_insert(self.slot_limit);
        let gone = self.layout.length_handle.is_none() || !self.is_shown() || self.covered;
        self.moved_in_stretch = self.moved_in_stretch || ui.mouse() != press;
        if self.moved_in_stretch
            && !gone
            && let Some((left, top)) = ui.element(self.root).map(|e| (e.left, e.top))
        {
            let (mx, my) = ui.mouse();
            let (sw, sh) = self.screen;
            let (start, pointer, screen) = if self.mode.is_horizontal() {
                (left, mx as f32, sw)
            } else {
                (top, my as f32, sh)
            };
            let owners: Vec<&str> = self.entries.iter().map(|e| e.owner.as_str()).collect();
            let limit = layout::slots_for_length(
                self.mode,
                self.size,
                self.layout.grip.is_some(),
                &owners,
                layout::max_length(self.mode, sw, sh),
                screen - start,
                pointer - start + LENGTH_HANDLE - grabbed,
            );
            self.set_slot_limit(ui, limit);
        }
        if released || gone {
            if let Some(h) = ui.widget_mut::<DockLengthHandle>(self.length_handle) {
                h.grabbed = None;
                h.released = false;
            }
            self.stretching = None;
            self.moved_in_stretch = false;
            self.limit_stretched = self.slot_limit != before;
        }
    }

    fn release_grip(&mut self, ui: &mut UiRoot) {
        if let Some(g) = ui.widget_mut::<DockGrip>(self.grip) {
            g.held = None;
        }
    }

    /// The plugin window drawn over the dock's other open windows.
    fn front_window(&self, ui: &UiRoot) -> Option<usize> {
        let mut front: Option<(usize, i32)> = None;
        for (i, entry) in self.entries.iter().enumerate() {
            if !entry.open {
                continue;
            }
            let Some(z) = ui
                .window(&entry.window)
                .and_then(|w| ui.element(w.frame))
                .map(Element::z_order)
            else {
                continue;
            };
            if front.is_none_or(|(_, top)| z >= top) {
                front = Some((i, z));
            }
        }
        front.map(|(i, _)| i)
    }

    /// Each edge pill eases toward its state's length: tallest beside the
    /// front window, then an open window, then under the pointer.
    fn ease_pills(&mut self, ui: &mut UiRoot, dt: f32) {
        if !self.mode.is_rail() {
            return;
        }
        let front = self.front_window(ui);
        let step = style::PILL_FRONT / style::PILL_SECONDS * dt.max(0.0);
        for i in 0..self.entries.len() {
            let entry = &self.entries[i];
            let hovered = ui
                .widget::<DockSlot>(entry.slot)
                .is_some_and(|s| s.pointer.look(true) != ButtonLook::Normal);
            let target = if front == Some(i) {
                style::PILL_FRONT
            } else if entry.open {
                style::PILL_OPEN
            } else if hovered {
                style::PILL_HOVER
            } else {
                0.0
            };
            let entry = &mut self.entries[i];
            entry.pill += (target - entry.pill).clamp(-step, step);
        }
        self.mark_slots(ui);
    }

    /// The screen edge the dock faces: a rail's own edge, else the nearer of
    /// the two its slots' sides face.
    fn side(&self, ui: &UiRoot) -> DockSide {
        let (left, top) = ui
            .element(self.root)
            .map_or((0.0, 0.0), |e| (e.left, e.top));
        let (sw, sh) = self.screen;
        let (w, h) = (self.layout.width, self.layout.height);
        match self.mode {
            DockMode::Left => DockSide::Left,
            DockMode::Right => DockSide::Right,
            DockMode::Top => DockSide::Top,
            DockMode::Bottom => DockSide::Bottom,
            DockMode::Floating if left + w / 2.0 > sw / 2.0 => DockSide::Right,
            DockMode::Floating => DockSide::Left,
            DockMode::FloatingHorizontal if top + h / 2.0 > sh / 2.0 => DockSide::Bottom,
            DockMode::FloatingHorizontal => DockSide::Top,
        }
    }

    /// What follows the pointer each frame: the grip's look and the hover
    /// label.
    fn follow_pointer(&mut self, ui: &mut UiRoot) {
        let over = self.layout.grip.is_some_and(|g| self.pointer_on(ui, g));
        if let Some(g) = ui.widget_mut::<DockGrip>(self.grip) {
            g.hovered = over;
        }
        let side = self.side(ui);
        let label = self.hover_label(ui, side);
        if let Some(frame) = ui.widget_mut::<DockFrame>(self.root) {
            frame.side = side;
            frame.label = label;
        }
    }

    /// The label of the tile, slot, arrow or gear under the pointer.
    fn hover_label(&self, ui: &UiRoot, side: DockSide) -> Option<HoverLabel> {
        let hovered = |look: Option<ButtonLook>| {
            matches!(look, Some(ButtonLook::Hovered | ButtonLook::Pressed))
        };
        for (id, rect) in [
            (self.back, self.layout.back),
            (self.forward, self.layout.forward),
        ] {
            let Some((arrow, rect)) = ui.widget::<DockArrow>(id).zip(rect) else {
                continue;
            };
            if hovered(Some(arrow.pointer.look(arrow.more > 0))) {
                let title = more_label(arrow.more);
                return Some(self.label_at(ui, side, rect, &title, None));
            }
        }
        let (slot, title, subtitle) = if hovered(
            ui.widget::<DockTile>(self.tile)
                .map(|t| t.pointer.look(true)),
        ) {
            (self.layout.tile, TILE_LABEL, None)
        } else if self.closed() {
            return None;
        } else if hovered(
            ui.widget::<DockGear>(self.gear)
                .map(|g| g.pointer.look(true)),
        ) {
            (self.layout.gear, APPEARANCE_LABEL, None)
        } else {
            let (entry, slot) = self.entries.iter().zip(&self.layout.slots).find(|(e, s)| {
                s.is_some() && hovered(ui.widget::<DockSlot>(e.slot).map(|w| w.pointer.look(true)))
            })?;
            let (title, subtitle) = label_text(&entry.title, &entry.owner_name);
            ((*slot)?, title, subtitle)
        };
        Some(self.label_at(ui, side, slot, title, subtitle))
    }

    /// A label saying `title` and `subtitle` beside `slot`.
    fn label_at(
        &self,
        ui: &UiRoot,
        side: DockSide,
        slot: Rect,
        title: &str,
        subtitle: Option<&str>,
    ) -> HoverLabel {
        HoverLabel {
            rect: self.label_rect(ui, side, slot, title, subtitle),
            title: title.to_owned(),
            subtitle: subtitle.map(str::to_owned),
            title_font: self.title_font(),
            body_font: self.body_font(),
        }
    }

    /// Where a slot's label goes in the dock's space: beside the slot, on the
    /// side away from the screen edge, and kept on the screen both ways.
    fn label_rect(
        &self,
        ui: &UiRoot,
        side: DockSide,
        slot: Rect,
        title: &str,
        subtitle: Option<&str>,
    ) -> Rect {
        let title_font = self.title_font();
        let body_font = self.body_font();
        let measure = |font: &Option<Arc<UiFont>>, text: &str| {
            font.as_ref().map_or(0.0, |f| f.measure_width(text))
        };
        let line = |font: &Option<Arc<UiFont>>| font.as_ref().map_or(12.0, |f| f.line_height);
        let text =
            measure(&title_font, title).max(subtitle.map_or(0.0, |s| measure(&body_font, s)));
        let lines = line(&title_font) + subtitle.map_or(0.0, |_| line(&body_font) + 1.0);
        let w = (text + 2.0 * style::LABEL_PAD_X).ceil();
        let h = (lines + 2.0 * style::LABEL_PAD_Y).ceil();
        let (dw, dh) = (self.layout.width, self.layout.height);
        let along_x = (slot.x + (slot.w - w) / 2.0).round();
        let along_y = (slot.y + (slot.h - h) / 2.0).round();
        let (x, y) = match side {
            DockSide::Left => (dw + style::LABEL_GAP, along_y),
            DockSide::Right => (-style::LABEL_GAP - w, along_y),
            DockSide::Top => (along_x, dh + style::LABEL_GAP),
            DockSide::Bottom => (along_x, -style::LABEL_GAP - h),
        };
        let (left, top) = ui
            .element(self.root)
            .map_or((0.0, 0.0), |e| (e.left, e.top));
        let (sw, sh) = self.screen;
        Rect::new(
            (left + x).clamp(0.0, (sw - w).max(0.0)) - left,
            (top + y).clamp(0.0, (sh - h).max(0.0)) - top,
            w,
            h,
        )
    }

    /// Scrolls the list to start at slot `first`, as far as it goes.
    fn scroll_to(&mut self, ui: &mut UiRoot, first: usize) {
        self.first = first.min(self.layout.max_first);
        self.reflow(ui);
    }

    /// The dock sits over the other windows, whichever was raised last: when
    /// one comes up over it, it goes back on top. What is not such a window
    /// stays over it, in the order it was in: a plugin's dialog box or modal
    /// window (anything blocking a window), a tooltip, a game dialog. A modal
    /// window keeps the top.
    fn keep_on_top(&self, ui: &mut UiRoot) {
        if ui.modal.is_some() || !self.is_shown() || self.covered {
            return;
        }
        let Some(z) = ui.element(self.root).map(Element::z_order) else {
            return;
        };
        let blockers = ui.window_blockers();
        let windows: Vec<ElementId> = ui
            .windows()
            .iter()
            .map(|w| w.frame)
            .filter(|f| *f != self.root && !blockers.contains(f))
            .collect();
        let top = ui.root();
        let under = windows.iter().any(|f| {
            ui.parent(*f) == Some(top)
                && ui
                    .element(*f)
                    .is_some_and(|e| e.visible() && e.z_order() >= z)
        });
        if !under {
            return;
        }
        let mut over: Vec<(i32, ElementId)> = ui
            .children(top)
            .into_iter()
            .filter(|c| *c != self.root && !windows.contains(c))
            .filter_map(|c| {
                let cz = ui.element(c)?.z_order();
                (cz > z).then_some((cz, c))
            })
            .collect();
        over.sort_by_key(|(cz, _)| *cz);
        ui.bring_to_front(self.root);
        for (_, c) in over {
            ui.bring_to_front(c);
        }
    }
}

/// Puts `id` at `r` in its parent.
fn place(ui: &mut UiRoot, id: ElementId, r: Rect) {
    if let Some(e) = ui.element_mut(id) {
        e.left = r.x;
        e.top = r.y;
        e.width = r.w;
        e.height = r.h;
    }
}

impl PanelController for PluginDock {
    /// Minimized or not is saved per character, with its place. A peek is
    /// not the player's change, so while it is open the place and size saved
    /// are the closed dock's: where it goes back to, at the tile's size. On its
    /// way open or closed, what is saved is where it is going.
    fn capture_window_state(&self, ui: &UiRoot) -> Option<WindowState> {
        let going = self.reveal.map(|r| r.to);
        let closed = self
            .peeking
            .then(|| {
                let at = going
                    .map(|r| (r.x, r.y))
                    .or_else(|| ui.element(self.root).map(|e| (e.left, e.top)))?;
                let (left, top) = self.closed_at.unwrap_or(at);
                let grip = self.layout.grip.is_some();
                let tile = layout::compute(self.mode, self.size, true, grip, &[], 0.0, 0);
                Some(Rect::new(left, top, tile.width, tile.height))
            })
            .flatten()
            .or(going);
        Some(WindowState {
            collapsed: self.minimized,
            persisted_left: closed.map(|c| c.x),
            persisted_top: closed.map(|c| c.y),
            persisted_width: closed.map(|c| c.w),
            persisted_height: closed.map(|c| c.h),
            ..WindowState::default()
        })
    }

    fn restore_window_state(&mut self, ui: &mut UiRoot, state: &WindowState) -> bool {
        self.closed_at = None;
        self.peeking = false;
        self.minimized = state.collapsed;
        self.reflow(ui);
        true
    }

    fn tick(&mut self, ui: &mut UiRoot, dt: f64) {
        self.ease_reveal(ui, dt as f32);
        self.follow_drag(ui);
        self.follow_length_handle(ui);
        let size = ui.element(self.root).map(|e| (e.width, e.height));
        if self.screen != ui.size()
            || (self.reveal.is_none() && size != Some((self.layout.width, self.layout.height)))
            || self.layout.grip.is_some() == ui.ui_locked()
        {
            self.reflow(ui);
        }
        self.follow_peek(ui, dt as f32);
        self.keep_in_place(ui);
        self.ease_pills(ui, dt as f32);
        self.follow_pointer(ui);
        self.apply_visibility(ui);
        self.keep_on_top(ui);
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        match message {
            ElementMessage::RightClicked if element == self.root => {
                self.appearance_requested = true;
                return true;
            }
            ElementMessage::Wheel(dy) if element == self.root => {
                if !self.closed() && self.layout.max_first > 0 {
                    let first = if *dy > 0 {
                        self.first.saturating_sub(1)
                    } else {
                        self.first + 1
                    };
                    self.scroll_to(ui, first);
                }
                return true;
            }
            ElementMessage::Clicked { .. } => {}
            _ => return false,
        }
        if element == self.tile {
            let minimized = !self.minimized;
            self.set_minimized(ui, minimized);
            return true;
        }
        if element == self.gear {
            self.appearance_requested = true;
            return true;
        }
        // An arrow moves a page, with the slot at the far end left showing to
        // keep the player's place.
        let page = self.layout.visible.saturating_sub(1).max(1);
        if element == self.back {
            self.scroll_to(ui, self.first.saturating_sub(page));
            return true;
        }
        if element == self.forward {
            self.scroll_to(ui, self.first + page);
            return true;
        }
        let Some(entry) = self.entries.iter().find(|e| e.slot == element) else {
            return false;
        };
        self.outbox.borrow_mut().push(UiInput::PanelShown {
            panel: entry.panel,
            shown: !entry.open,
        });
        true
    }
}

/// Where a slot is on the dock, whether its window is open and how long its
/// edge pill is.
#[derive(Debug, Clone, Copy)]
struct SlotMark {
    slot: Rect,
    open: bool,
    pill: f32,
}

/// What a hover label says and where it goes.
#[derive(Debug, Clone)]
struct HoverLabel {
    rect: Rect,
    title: String,
    subtitle: Option<String>,
    title_font: Option<Arc<UiFont>>,
    body_font: Option<Arc<UiFont>>,
}

/// The dock's own frame: its body, what is drawn beside each slot, the scroll
/// bar and the hover label; and the right click (on it or any slot) and the wheel,
/// which it tells its controller.
#[derive(Debug, Clone)]
struct DockFrame {
    palette: Palette,
    mode: DockMode,
    metrics: Metrics,
    side: DockSide,
    minimized: bool,
    dividers: Vec<f32>,
    marks: Vec<SlotMark>,
    scroll: Option<ScrollBar>,
    label: Option<HoverLabel>,
}

impl Default for DockFrame {
    fn default() -> Self {
        Self {
            palette: Palette::CLASSIC_DOCK,
            mode: DockMode::default(),
            metrics: DockSize::default().metrics(),
            side: DockSide::default(),
            minimized: false,
            dividers: Vec::new(),
            marks: Vec::new(),
            scroll: None,
            label: None,
        }
    }
}

fn element_size(ui: &UiRoot, me: ElementId) -> (f32, f32) {
    ui.element(me).map_or((0.0, 0.0), |e| (e.width, e.height))
}

impl Widget for DockFrame {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        match e.kind {
            kind::RIGHT_CLICK => {
                ui.send_message(me, ElementMessage::RightClicked);
                true
            }
            kind::SCROLL => {
                ui.send_message(me, ElementMessage::Wheel(e.data0));
                true
            }
            _ => false,
        }
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let size = element_size(ui, me);
        style::body(rc, &self.palette, self.mode, &self.metrics, size.0, size.1);
        for at in &self.dividers {
            style::divider(rc, &self.palette, self.mode, size, *at);
        }
        if self.minimized {
            return;
        }
        let p = &self.palette;
        for mark in &self.marks {
            if self.mode.is_rail() {
                let color = if mark.open { p.accent } else { p.muted };
                style::edge_pill(rc, color, self.side, size, mark.slot, mark.pill);
            } else if mark.open {
                style::open_dot(rc, p, self.side, size, mark.slot);
            }
        }
        if let Some(bar) = &self.scroll {
            style::scroll_bar(rc, p, self.side, size, bar);
        }
    }

    fn draw_overlay(&mut self, _ui: &mut UiRoot, _me: ElementId, rc: &mut RenderContext<'_>) {
        let Some(label) = &self.label else {
            return;
        };
        let p = &self.palette;
        style::hover_label(rc, p, label.rect);
        let x = label.rect.x + style::LABEL_PAD_X;
        let mut y = label.rect.y + style::LABEL_PAD_Y;
        if let Some(font) = &label.title_font {
            rc.string_dat(
                font,
                &label.title,
                x,
                y,
                p.text,
                false,
                DEFAULT_OUTLINE_COLOR,
            );
            y += font.line_height + 1.0;
        }
        if let (Some(font), Some(subtitle)) = (&label.body_font, &label.subtitle) {
            rc.string_dat(font, subtitle, x, y, p.muted, false, DEFAULT_OUTLINE_COLOR);
        }
    }

    /// The hover label is drawn outside the dock.
    fn expands_clip_for_popup(&self) -> bool {
        true
    }
}

/// A press and a release on the element send it [`ElementMessage::Clicked`].
fn click_event(pointer: &mut PointerState, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
    if e.target != Some(me) {
        return false;
    }
    pointer.observe(e);
    if e.kind != kind::CLICK {
        return false;
    }
    ui.send_message(
        me,
        ElementMessage::Clicked {
            x: e.data1,
            y: e.data2,
        },
    );
    true
}

/// The grip: dots that move the dock, there only while windows are unlocked.
#[derive(Debug, Clone)]
struct DockGrip {
    palette: Palette,
    mode: DockMode,
    metrics: Metrics,
    hovered: bool,
    /// Where the button went down on it, until the dock sees the drag end.
    held: Option<(i32, i32)>,
}

impl Default for DockGrip {
    fn default() -> Self {
        Self {
            palette: Palette::CLASSIC_DOCK,
            mode: DockMode::default(),
            metrics: DockSize::default().metrics(),
            hovered: false,
            held: None,
        }
    }
}

impl Widget for DockGrip {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.kind == kind::MOUSE_DOWN && e.target == Some(me) {
            self.held = Some(ui.mouse());
        }
        false
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = element_size(ui, me);
        let look = match (self.held.is_some(), self.hovered) {
            (true, _) => ButtonLook::Pressed,
            (false, true) => ButtonLook::Hovered,
            (false, false) => ButtonLook::Normal,
        };
        let horizontal = self.mode.is_horizontal();
        let radius = self.metrics.well_radius.min(4.0);
        style::grip(
            rc,
            &self.palette,
            Rect::new(0.0, 0.0, w, h),
            horizontal,
            radius,
            look,
        );
    }
}

/// Where the length handle was taken hold of.
#[derive(Debug, Clone, Copy)]
struct LengthGrab {
    /// How far along the handle.
    along: f32,
    /// Where the pointer was.
    press: (i32, i32),
}

/// The handle at the dock's far end, which limits how many slots it shows, there
/// only while windows are unlocked. The dock does the work: it makes its own
/// length by whole slots, and a saved layout's size never applies to it.
#[derive(Debug, Clone)]
struct DockLengthHandle {
    palette: Palette,
    pointer: PointerState,
    mode: DockMode,
    metrics: Metrics,
    /// Until the dock sees the drag end.
    grabbed: Option<LengthGrab>,
    /// The button came up, or the pointer was taken away.
    released: bool,
}

impl Default for DockLengthHandle {
    fn default() -> Self {
        Self {
            palette: Palette::CLASSIC_DOCK,
            pointer: PointerState::default(),
            mode: DockMode::default(),
            metrics: DockSize::default().metrics(),
            grabbed: None,
            released: false,
        }
    }
}

impl Widget for DockLengthHandle {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if e.target != Some(me) {
            return false;
        }
        self.pointer.observe(e);
        match e.kind {
            kind::MOUSE_DOWN => {
                let along = if self.mode.is_horizontal() {
                    e.data1
                } else {
                    e.data2
                };
                self.grabbed = Some(LengthGrab {
                    along: along as f32,
                    press: ui.mouse(),
                });
                self.released = false;
            }
            kind::MOUSE_UP | kind::CAPTURE_CHANGED if self.grabbed.is_some() => {
                self.released = true;
            }
            _ => {}
        }
        false
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn hover_outlasts_tooltip(&self) -> bool {
        true
    }

    fn resize_cursor_edges(&self) -> Option<ResizeEdges> {
        Some(if self.mode.is_horizontal() {
            ResizeEdges::RIGHT
        } else {
            ResizeEdges::BOTTOM
        })
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = element_size(ui, me);
        style::length_handle(
            rc,
            &self.palette,
            Rect::new(0.0, 0.0, w, h),
            self.mode.is_horizontal(),
            self.metrics.well_radius.min(4.0),
            self.pointer.look(true),
        );
    }
}

/// The tile: the OpenAC icon, which opens and closes the dock, with a count of
/// open plugin windows while closed.
#[derive(Debug, Clone)]
struct DockTile {
    palette: Palette,
    pointer: PointerState,
    mode: DockMode,
    metrics: Metrics,
    closed: bool,
    icon: Option<Icon>,
    count: usize,
    count_font: Option<Arc<UiFont>>,
}

impl Default for DockTile {
    fn default() -> Self {
        Self {
            palette: Palette::CLASSIC_DOCK,
            pointer: PointerState::default(),
            mode: DockMode::default(),
            metrics: DockSize::default().metrics(),
            closed: false,
            icon: None,
            count: 0,
            count_font: None,
        }
    }
}

impl Widget for DockTile {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        click_event(&mut self.pointer, ui, me, e)
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    /// Its hover label is the dock's own, not a timed tooltip.
    fn hover_outlasts_tooltip(&self) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = element_size(ui, me);
        let look = self.pointer.look(true);
        let well = if self.closed {
            Rect::new(3.0, 3.0, w - 6.0, h - 6.0)
        } else {
            style::slot_well_rect(self.mode, w, h)
        };
        style::slot_well(rc, &self.palette, well, self.metrics.well_radius, look);
        if let Some((texture, iw, ih)) = self.icon
            && iw > 0.0
            && ih > 0.0
        {
            let art = self.metrics.art;
            let (x, y) = ((w - art) / 2.0, (h - art) / 2.0);
            rc.sprite(texture, x, y, art, art, (0.0, 0.0, 1.0, 1.0), Color::WHITE);
        }
        if self.count > 0 {
            style::count_badge(rc, &self.palette, self.count_font.as_ref(), w, self.count);
        }
    }
}

/// The gear slot, which opens and closes the Appearance window. Its hover
/// label says what it is.
#[derive(Debug, Clone)]
struct DockGear {
    palette: Palette,
    pointer: PointerState,
    mode: DockMode,
    metrics: Metrics,
}

impl Default for DockGear {
    fn default() -> Self {
        Self {
            palette: Palette::CLASSIC_DOCK,
            pointer: PointerState::default(),
            mode: DockMode::default(),
            metrics: DockSize::default().metrics(),
        }
    }
}

impl Widget for DockGear {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        click_event(&mut self.pointer, ui, me, e)
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    /// Its hover label is the dock's own, not a timed tooltip.
    fn hover_outlasts_tooltip(&self) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = element_size(ui, me);
        let p = &self.palette;
        let look = self.pointer.look(true);
        let radius = self.metrics.well_radius;
        style::slot_well(rc, p, style::slot_well_rect(self.mode, w, h), radius, look);
        let color = if look == ButtonLook::Normal {
            p.muted
        } else {
            p.text
        };
        let art = self.metrics.art;
        style::gear(rc, (w - art) / 2.0, (h - art) / 2.0, art, color);
    }
}

/// An arrow at one end of an overflowing list, which scrolls it a page that way
/// and dims when there is no more that way. Its hover label says how many more.
#[derive(Debug, Clone)]
struct DockArrow {
    palette: Palette,
    pointer: PointerState,
    mode: DockMode,
    metrics: Metrics,
    forward: bool,
    /// How many slots are off the list this way.
    more: usize,
}

impl Default for DockArrow {
    fn default() -> Self {
        Self {
            palette: Palette::CLASSIC_DOCK,
            pointer: PointerState::default(),
            mode: DockMode::default(),
            metrics: DockSize::default().metrics(),
            forward: false,
            more: 0,
        }
    }
}

impl Widget for DockArrow {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        click_event(&mut self.pointer, ui, me, e)
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    /// Its hover label is the dock's own, not a timed tooltip.
    fn hover_outlasts_tooltip(&self) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = element_size(ui, me);
        let p = &self.palette;
        let look = self.pointer.look(self.more > 0);
        let radius = self.metrics.well_radius.min(ARROW_LENGTH / 2.0);
        style::slot_well(rc, p, style::slot_well_rect(self.mode, w, h), radius, look);
        let color = match look {
            ButtonLook::Normal => p.muted,
            ButtonLook::Disabled => with_alpha(p.muted, style::SPENT_ARROW_ALPHA),
            ButtonLook::Hovered | ButtonLook::Pressed => p.text,
        };
        style::arrow(rc, color, self.mode.is_horizontal(), self.forward, (w, h));
    }
}

/// One plugin window's slot: its art, which opens or closes the window. Its
/// hover label names the window.
#[derive(Debug, Clone)]
struct DockSlot {
    palette: Palette,
    pointer: PointerState,
    mode: DockMode,
    metrics: Metrics,
    open: bool,
    icon: Option<Icon>,
    letters: String,
    hue: Color,
    font: Option<Arc<UiFont>>,
}

impl Default for DockSlot {
    fn default() -> Self {
        Self {
            palette: Palette::CLASSIC_DOCK,
            pointer: PointerState::default(),
            mode: DockMode::default(),
            metrics: DockSize::default().metrics(),
            open: false,
            icon: None,
            letters: String::new(),
            hue: Color::BLACK,
            font: None,
        }
    }
}

impl Widget for DockSlot {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        click_event(&mut self.pointer, ui, me, e)
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    /// Its hover label is the dock's own, not a timed tooltip.
    fn hover_outlasts_tooltip(&self) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let (w, h) = element_size(ui, me);
        let look = self.pointer.look(true);
        style::slot_well(
            rc,
            &self.palette,
            style::slot_well_rect(self.mode, w, h),
            self.metrics.well_radius,
            look,
        );
        let alpha = if self.open || look != ButtonLook::Normal {
            1.0
        } else {
            style::CLOSED_ART_ALPHA
        };
        let art = self.metrics.art;
        let (x, y) = ((w - art) / 2.0, (h - art) / 2.0);
        match self.icon {
            Some((texture, iw, ih)) if iw > 0.0 && ih > 0.0 => {
                let tint = Color::rgba(1.0, 1.0, 1.0, alpha);
                rc.sprite(texture, x, y, art, art, (0.0, 0.0, 1.0, 1.0), tint);
            }
            _ => style::monogram(
                rc,
                self.font.as_ref(),
                &self.letters,
                self.hue,
                x,
                y,
                art,
                alpha,
            ),
        }
    }
}

#[cfg(test)]
mod tests;
