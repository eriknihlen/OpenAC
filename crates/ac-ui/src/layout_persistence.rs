//! Window layout persistence: where every registered window sits, its
//! size, whether it is shown, collapsed or maximized, saved per character
//! and screen resolution, restored on login, reflowed when the screen
//! changes, and the named profiles of `/saveui` and `/loadui`.
//!
//! The persistence follows the windows each frame ([`LayoutPersistence::follow`]):
//! a window the player moved, resized, showed, hid, collapsed or maximized
//! since the last frame is saved; what the persistence itself did (a
//! restore, a clamp, a reflow) is not, as it updates its own record of the
//! windows when it acts.

use std::collections::{HashMap, HashSet};

use ac_host::settings::{SettingsStore, UiWindowLayout, UiWindowPlacement};

use crate::geometry::clamp;
use crate::host::UiHost;
use crate::widgets::NineSliceFrame;

/// What a window's own state adds to its place and size: collapsed (the
/// toolbar's short stop), maximized (the chat window), and the place and
/// height it returns to.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct WindowState {
    /// Collapsed.
    pub collapsed: bool,
    /// Maximized.
    pub maximized: bool,
    /// The top to save instead of the current one (a maximized window's
    /// normal top).
    pub persisted_top: Option<f32>,
    /// The height to save instead of the current one.
    pub persisted_height: Option<f32>,
    /// The left edge to save instead of the current one (a peeking dock's
    /// closed place).
    pub persisted_left: Option<f32>,
    /// The width to save instead of the current one.
    pub persisted_width: Option<f32>,
    /// The visibility to save instead of the current one.
    pub requested_visible: Option<bool>,
}

/// Where layouts are kept.
pub trait LayoutStore {
    /// The latest placement of `window` for `character` at `resolution`.
    fn load_window_placement(
        &self,
        character: &str,
        resolution: &str,
        window: &str,
        fallback: &UiWindowLayout,
    ) -> Option<UiWindowPlacement>;
    /// Save the placement of `window` for `character`.
    fn save_window_placement(
        &self,
        character: &str,
        window: &str,
        placement: &UiWindowPlacement,
    ) -> std::io::Result<()>;
    /// The latest placements of several windows (each with its fallback)
    /// for `character` at `resolution`, by name: only the windows with
    /// something saved. A store that keeps a file reads it once.
    fn load_window_placements(
        &self,
        character: &str,
        resolution: &str,
        windows: &[(&str, UiWindowLayout)],
    ) -> HashMap<String, UiWindowPlacement> {
        windows
            .iter()
            .filter_map(|(window, fallback)| {
                self.load_window_placement(character, resolution, window, fallback)
                    .map(|p| ((*window).to_owned(), p))
            })
            .collect()
    }
    /// Save the placements of several windows for `character`. A store
    /// that keeps a file writes it once.
    fn save_window_placements(
        &self,
        character: &str,
        placements: &[(&str, UiWindowPlacement)],
    ) -> std::io::Result<()> {
        for (window, placement) in placements {
            self.save_window_placement(character, window, placement)?;
        }
        Ok(())
    }
    /// Window `window` of the named profile.
    fn load_named_window_layout(
        &self,
        profile: &str,
        window: &str,
        fallback: &UiWindowLayout,
    ) -> Option<UiWindowLayout>;
    /// Save window `window` of the named profile.
    fn save_named_window_layout(
        &self,
        profile: &str,
        window: &str,
        layout: &UiWindowLayout,
    ) -> std::io::Result<()>;
}

impl LayoutStore for SettingsStore {
    fn load_window_placement(
        &self,
        character: &str,
        resolution: &str,
        window: &str,
        fallback: &UiWindowLayout,
    ) -> Option<UiWindowPlacement> {
        SettingsStore::load_window_placement(self, character, resolution, window, fallback)
    }
    fn save_window_placement(
        &self,
        character: &str,
        window: &str,
        placement: &UiWindowPlacement,
    ) -> std::io::Result<()> {
        SettingsStore::save_window_placement(self, character, window, placement)
    }
    fn load_window_placements(
        &self,
        character: &str,
        resolution: &str,
        windows: &[(&str, UiWindowLayout)],
    ) -> HashMap<String, UiWindowPlacement> {
        SettingsStore::load_window_placements(self, character, resolution, windows)
    }
    fn save_window_placements(
        &self,
        character: &str,
        placements: &[(&str, UiWindowPlacement)],
    ) -> std::io::Result<()> {
        SettingsStore::save_window_placements(self, character, placements)
    }
    fn load_named_window_layout(
        &self,
        profile: &str,
        window: &str,
        fallback: &UiWindowLayout,
    ) -> Option<UiWindowLayout> {
        SettingsStore::load_named_window_layout(self, profile, window, fallback)
    }
    fn save_named_window_layout(
        &self,
        profile: &str,
        window: &str,
        layout: &UiWindowLayout,
    ) -> std::io::Result<()> {
        SettingsStore::save_named_window_layout(self, profile, window, layout)
    }
}

/// The window names the default placement knows.
pub mod window_names {
    /// The main chat window.
    pub const CHAT: &str = "chat";
    /// The radar.
    pub const RADAR: &str = "radar";
    /// The toolbar.
    pub const TOOLBAR: &str = "toolbar";
    /// The combat window.
    pub const COMBAT: &str = "combat";
    /// The indicator bar.
    pub const INDICATORS: &str = "indicators";
    /// The vitals.
    pub const VITALS: &str = "vitals";
    /// The side-by-side vitals.
    pub const SIDE_VITALS: &str = "side-vitals";
    /// The plugin dock.
    pub const PLUGIN_SHELF: &str = "plugin-shelf";
}

/// A saved placement projected onto a screen of `width` x `height` for a
/// window of `window_width` x `window_height`: on the same screen the place
/// is clamped; on another the place keeps its share of the free travel.
#[must_use]
pub fn project(
    placement: &UiWindowPlacement,
    width: i32,
    height: i32,
    window_width: f32,
    window_height: f32,
) -> UiWindowLayout {
    fn axis(position: f32, source: i32, source_extent: f32, target: i32, extent: f32) -> f32 {
        let source_travel = (source as f32 - source_extent).max(0.0);
        let target_travel = (target as f32 - extent).max(0.0);
        if !position.is_finite() {
            return 0.0;
        }
        if source == target {
            return clamp(position, 0.0, target_travel);
        }
        if source_travel > 0.0 {
            clamp(position / source_travel, 0.0, 1.0) * target_travel
        } else {
            0.0
        }
    }
    let l = placement.layout;
    UiWindowLayout {
        x: axis(l.x, placement.screen_width, l.width, width, window_width),
        y: axis(
            l.y,
            placement.screen_height,
            l.height,
            height,
            window_height,
        ),
        ..l
    }
}

/// The default place of window `name` (its authored layout) on a screen of
/// `width` x `height`: the chat window bottom-left, the radar top-right,
/// the toolbar bottom-right with the combat window above it, the
/// indicators top-left with the vitals beside them, everything else
/// centred.
#[must_use]
pub fn default_layout(
    name: &str,
    authored: &UiWindowLayout,
    width: i32,
    height: i32,
    windows: &HashMap<String, UiWindowLayout>,
) -> UiWindowLayout {
    use window_names as n;
    const MARGIN: f32 = 10.0;
    let (w, h) = (width as f32, height as f32);
    let right = (w - authored.width - MARGIN).max(0.0);
    let bottom = (h - authored.height - MARGIN).max(0.0);
    let center_x = ((w - authored.width) * 0.5).max(0.0);
    let center_y = ((h - authored.height) * 0.5).max(0.0);
    let toolbar_height = windows.get(n::TOOLBAR).map_or(0.0, |t| t.height);
    let indicator_width = windows.get(n::INDICATORS).map_or(0.0, |i| i.width);
    let (x, y) = match name {
        n::CHAT => (MARGIN, bottom),
        n::RADAR => (right, MARGIN),
        n::TOOLBAR => (right, bottom),
        n::COMBAT => (right, (bottom - toolbar_height - MARGIN).max(0.0)),
        n::INDICATORS => (MARGIN, MARGIN),
        n::VITALS | n::SIDE_VITALS => (indicator_width + MARGIN * 2.0, MARGIN),
        n::PLUGIN_SHELF => (MARGIN, center_y),
        _ => (center_x, center_y),
    };
    UiWindowLayout {
        x: clamp(x, 0.0, (w - authored.width).max(0.0)),
        y: clamp(y, 0.0, (h - authored.height).max(0.0)),
        ..*authored
    }
}

fn finite_or(value: f32, fallback: f32) -> f32 {
    if value.is_finite() { value } else { fallback }
}

fn clamp_dimension(saved: f32, current: f32, minimum: f32, maximum: f32, screen: i32) -> f32 {
    let value = finite_or(saved, current);
    let upper = minimum.max(maximum.min(screen as f32));
    clamp(value, minimum, upper)
}

fn can_persist(key: &str) -> bool {
    !key.trim().is_empty() && !key.eq_ignore_ascii_case("default")
}

fn migrate_authored_geometry(saved: UiWindowLayout, authored: UiWindowLayout) -> UiWindowLayout {
    if saved.authored_geometry_revision == authored.authored_geometry_revision {
        return saved;
    }
    UiWindowLayout {
        width: authored.width,
        height: authored.height,
        authored_geometry_revision: authored.authored_geometry_revision,
        ..saved
    }
}

/// The capture of a window: its place, size, visibility and state.
#[must_use]
pub fn capture(host: &UiHost, name: &str) -> Option<UiWindowLayout> {
    let handle = host.root.window(name)?;
    let e = host.root.element(handle.frame)?;
    let state = host.capture_window_state(name);
    Some(UiWindowLayout {
        x: state.persisted_left.unwrap_or(e.left),
        y: state.persisted_top.unwrap_or(e.top),
        width: state.persisted_width.unwrap_or(e.width),
        height: state.persisted_height.unwrap_or(e.height),
        visible: state.requested_visible.unwrap_or(e.visible()),
        collapsed: state.collapsed,
        maximized: state.maximized,
        authored_geometry_revision: handle.authored_geometry_revision,
    })
}

fn authored_geometry(host: &UiHost, name: &str, current: UiWindowLayout) -> UiWindowLayout {
    let Some(h) = host.root.window(name) else {
        return current;
    };
    UiWindowLayout {
        width: h.authored_width,
        height: h.authored_height,
        authored_geometry_revision: h.authored_geometry_revision,
        ..current
    }
}

/// Size and place window `name` as `layout` says (within its limits and
/// the screen), restore its state, and show or hide it when asked. `spot`
/// says where the saved place puts a window of a given size, for a window
/// its own state resizes.
fn apply(
    host: &mut UiHost,
    name: &str,
    layout: &UiWindowLayout,
    screen: (i32, i32),
    restore_visibility: bool,
    spot: &dyn Fn(f32, f32) -> (f32, f32),
) {
    let Some(frame) = host.root.window(name).map(|w| w.frame) else {
        return;
    };
    let Some(e) = host.root.element(frame) else {
        return;
    };
    let width = clamp_dimension(layout.width, e.width, e.min_width, e.max_width, screen.0);
    let height = clamp_dimension(
        layout.height,
        e.height,
        e.min_height,
        e.max_height,
        screen.1,
    );
    let constrain = e.constrain_resize_to_parent;
    if let Some(e) = host.root.element_mut(frame) {
        e.constrain_resize_to_parent = false;
    }
    host.root.resize_window(name, width, height);
    if let Some(e) = host.root.element_mut(frame) {
        e.constrain_resize_to_parent = constrain;
    }
    let (cur_left, cur_top, w, h) = host
        .root
        .element(frame)
        .map_or((0.0, 0.0, 0.0, 0.0), |e| (e.left, e.top, e.width, e.height));
    let max_x = (screen.0 as f32 - w).max(0.0);
    let max_y = (screen.1 as f32 - h).max(0.0);
    let x = clamp(finite_or(layout.x, cur_left), 0.0, max_x);
    let y = clamp(finite_or(layout.y, cur_top), 0.0, max_y);
    host.root.move_window(name, x, y);
    host.restore_window_state(
        name,
        WindowState {
            collapsed: layout.collapsed,
            maximized: layout.maximized,
            persisted_top: Some(y),
            persisted_height: Some(h),
            persisted_left: Some(x),
            persisted_width: Some(w),
            requested_visible: Some(layout.visible),
        },
    );
    // A window whose state changes its size (a dock restored closed is only
    // its tile) was kept on the screen at the size it had before; it goes
    // where its saved place puts it at its new size. A maximized window
    // places itself.
    if !layout.maximized
        && let Some((now_w, now_h)) = host.root.element(frame).map(|e| (e.width, e.height))
        && (now_w, now_h) != (w, h)
    {
        let (sx, sy) = spot(now_w, now_h);
        let x = clamp(finite_or(sx, x), 0.0, (screen.0 as f32 - now_w).max(0.0));
        let y = clamp(finite_or(sy, y), 0.0, (screen.1 as f32 - now_h).max(0.0));
        host.root.move_window(name, x, y);
    }
    if restore_visibility {
        if layout.visible {
            host.root.show_window(name);
        } else {
            host.root.hide_window(name);
        }
    }
}

fn apply_placement(
    host: &mut UiHost,
    name: &str,
    placement: &UiWindowPlacement,
    screen: (i32, i32),
    restore_visibility: bool,
) {
    let Some(frame) = host.root.window(name).map(|w| w.frame) else {
        return;
    };
    let Some(e) = host.root.element(frame) else {
        return;
    };
    let l = placement.layout;
    let width = if e.resize_x {
        clamp_dimension(l.width, e.width, e.min_width, e.max_width, screen.0)
    } else {
        e.width
    };
    let height = if e.resize_y {
        clamp_dimension(l.height, e.height, e.min_height, e.max_height, screen.1)
    } else {
        e.height
    };
    let layout = project(placement, screen.0, screen.1, width, height);
    let spot = |w: f32, h: f32| {
        let at = project(placement, screen.0, screen.1, w, h);
        (at.x, at.y)
    };
    apply(host, name, &layout, screen, restore_visibility, &spot);
}

fn with_current_state(
    host: &UiHost,
    name: &str,
    placement: UiWindowPlacement,
) -> UiWindowPlacement {
    let Some(current) = capture(host, name) else {
        return placement;
    };
    UiWindowPlacement {
        layout: UiWindowLayout {
            visible: current.visible,
            collapsed: current.collapsed,
            maximized: current.maximized,
            ..placement.layout
        },
        ..placement
    }
}

/// The layouts of the registered windows. See the module documentation.
pub struct LayoutPersistence {
    store: Option<Box<dyn LayoutStore>>,
    character: String,
    screen: (i32, i32),
    state_managed: HashSet<String>,
    attached: Vec<String>,
    defaults: HashMap<String, UiWindowLayout>,
    placements: HashMap<String, UiWindowPlacement>,
    seen: HashMap<String, UiWindowLayout>,
    restored_character: Option<String>,
    gameplay_active: bool,
    failures: Vec<String>,
}

impl std::fmt::Debug for LayoutPersistence {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LayoutPersistence")
            .field("character", &self.character)
            .field("screen", &self.screen)
            .field("attached", &self.attached)
            .finish_non_exhaustive()
    }
}

impl LayoutPersistence {
    /// Persistence into `store` (none: nothing is saved or loaded) for the
    /// character `character` ("default" before one is chosen: never saved)
    /// on a screen of `screen` pixels. The windows in `state_managed` show
    /// and hide as the game says: their visibility is never restored and a
    /// change of it is not saved. Every window already registered is
    /// attached.
    pub fn new(
        host: &mut UiHost,
        store: Option<Box<dyn LayoutStore>>,
        character: &str,
        screen: (i32, i32),
        state_managed: &[&str],
    ) -> Self {
        let mut p = Self {
            store,
            character: character.to_owned(),
            screen,
            state_managed: state_managed.iter().map(|s| (*s).to_owned()).collect(),
            attached: Vec::new(),
            defaults: HashMap::new(),
            placements: HashMap::new(),
            seen: HashMap::new(),
            restored_character: None,
            gameplay_active: true,
            failures: Vec::new(),
        };
        p.attach_new(host);
        p
    }

    /// The character whose layouts are kept (changes made until now are
    /// the previous character's).
    pub fn set_character(&mut self, host: &mut UiHost, character: &str) {
        self.follow(host);
        character.clone_into(&mut self.character);
    }

    /// The screen size (call [`Self::follow`] first so changes made on the
    /// old screen are recorded there).
    pub fn set_screen_size(&mut self, width: i32, height: i32) {
        self.screen = (width, height);
    }

    /// Whether the game (not a pre-game screen) is shown: nothing is
    /// saved or restored otherwise.
    pub fn set_gameplay_active(&mut self, host: &mut UiHost, active: bool) {
        self.follow(host);
        self.gameplay_active = active;
    }

    /// Whether the game is shown (see [`Self::set_gameplay_active`]).
    #[must_use]
    pub fn gameplay_active(&self) -> bool {
        self.gameplay_active
    }

    /// Where layouts are kept from now on.
    pub fn set_store(&mut self, store: Option<Box<dyn LayoutStore>>) {
        self.store = store;
    }

    /// Saves that failed since the last call, worded.
    pub fn take_failures(&mut self) -> Vec<String> {
        std::mem::take(&mut self.failures)
    }

    fn valid_screen(&self) -> (i32, i32) {
        (self.screen.0.max(1), self.screen.1.max(1))
    }

    fn resolution_key(screen: (i32, i32)) -> String {
        format!("{}x{}", screen.0, screen.1)
    }

    /// Remember what every attached window looks like now, so what the
    /// persistence just did is not taken for the player's change.
    fn resync(&mut self, host: &UiHost) {
        for name in &self.attached {
            if let Some(c) = capture(host, name) {
                self.seen.insert(name.clone(), c);
            }
        }
    }

    fn attach_new(&mut self, host: &mut UiHost) {
        let registered: Vec<String> = host.root.windows().iter().map(|w| w.name.clone()).collect();
        self.attached.retain(|name| {
            let keep = registered.contains(name);
            if !keep {
                self.defaults.remove(name);
                self.placements.remove(name);
                self.seen.remove(name);
            }
            keep
        });
        for name in registered {
            if self.attached.contains(&name) {
                continue;
            }
            let Some(c) = capture(host, &name) else {
                continue;
            };
            let screen = self.valid_screen();
            self.attached.push(name.clone());
            self.defaults.insert(name.clone(), c);
            self.placements.insert(
                name.clone(),
                UiWindowPlacement {
                    layout: c,
                    screen_width: screen.0,
                    screen_height: screen.1,
                },
            );
            self.seen.insert(name.clone(), c);
            if self.gameplay_active
                && self.restored_character.as_deref() == Some(self.character.as_str())
            {
                let character = self.character.clone();
                self.restore_window(host, &name, &character, screen, true, true);
                if let Some(c) = capture(host, &name) {
                    self.seen.insert(name, c);
                }
            }
        }
    }

    /// One frame: attach windows registered since the last frame, forget
    /// unregistered ones, and save each window the player changed.
    pub fn follow(&mut self, host: &mut UiHost) {
        self.attach_new(host);
        let screen = self.valid_screen();
        for name in self.attached.clone() {
            let Some(now) = capture(host, &name) else {
                continue;
            };
            let Some(before) = self.seen.insert(name.clone(), now) else {
                continue;
            };
            let geometry = (now.x, now.y, now.width, now.height)
                != (before.x, before.y, before.width, before.height);
            let state = now.collapsed != before.collapsed || now.maximized != before.maximized;
            let shown = now.visible != before.visible && !self.state_managed.contains(&name);
            if !self.gameplay_active || !(geometry || state || shown) {
                continue;
            }
            if geometry {
                self.placements.insert(
                    name.clone(),
                    UiWindowPlacement {
                        layout: now,
                        screen_width: screen.0,
                        screen_height: screen.1,
                    },
                );
            }
            if can_persist(&self.character) {
                let character = self.character.clone();
                self.save_placement(host, &character, &name);
            }
        }
    }

    fn save_placement(&mut self, host: &UiHost, character: &str, name: &str) {
        let Some(placement) = self.placements.get(name).copied() else {
            return;
        };
        let placement = with_current_state(host, name, placement);
        self.placements.insert(name.to_owned(), placement);
        if let Some(store) = &self.store
            && let Err(e) = store.save_window_placement(character, name, &placement)
        {
            self.failures
                .push(format!("settings: window layout save failed [{name}]: {e}"));
        }
    }

    fn restore_window(
        &mut self,
        host: &mut UiHost,
        name: &str,
        character: &str,
        screen: (i32, i32),
        save_back: bool,
        restore_visibility: bool,
    ) {
        let Some(fallback) = self.fallback(name, screen) else {
            return;
        };
        let saved = self.store.as_ref().and_then(|s| {
            s.load_window_placement(character, &Self::resolution_key(screen), name, &fallback)
        });
        let placement =
            self.apply_restored(host, name, fallback, saved, screen, restore_visibility);
        if save_back {
            self.save_placements(character, &[(name, placement)]);
        }
    }

    /// The default layout of `name` on `screen` (`None` for a window not
    /// attached).
    fn fallback(&self, name: &str, screen: (i32, i32)) -> Option<UiWindowLayout> {
        let authored = self.defaults.get(name)?;
        Some(default_layout(
            name,
            authored,
            screen.0,
            screen.1,
            &self.defaults,
        ))
    }

    /// Put `name` where `saved` says (its default `fallback` when nothing
    /// is saved) and return the placement to save back.
    fn apply_restored(
        &mut self,
        host: &mut UiHost,
        name: &str,
        fallback: UiWindowLayout,
        saved: Option<UiWindowPlacement>,
        screen: (i32, i32),
        restore_visibility: bool,
    ) -> UiWindowPlacement {
        let mut placement = saved.unwrap_or(UiWindowPlacement {
            layout: fallback,
            screen_width: screen.0,
            screen_height: screen.1,
        });
        placement.layout =
            migrate_authored_geometry(placement.layout, authored_geometry(host, name, fallback));
        self.placements.insert(name.to_owned(), placement);
        apply_placement(
            host,
            name,
            &placement,
            screen,
            restore_visibility && !self.state_managed.contains(name),
        );
        let placement = with_current_state(host, name, placement);
        self.placements.insert(name.to_owned(), placement);
        placement
    }

    /// Save `placements` for `character` with one write of the store.
    fn save_placements(&mut self, character: &str, placements: &[(&str, UiWindowPlacement)]) {
        if placements.is_empty() {
            return;
        }
        if let Some(store) = &self.store
            && let Err(e) = store.save_window_placements(character, placements)
        {
            for (name, _) in placements {
                self.failures
                    .push(format!("settings: window layout save failed [{name}]: {e}"));
            }
        }
    }

    fn restore_all_core(&mut self, host: &mut UiHost, save_back: bool, restore_visibility: bool) {
        self.follow(host);
        if !self.gameplay_active || !can_persist(&self.character) {
            return;
        }
        let character = self.character.clone();
        let screen = self.valid_screen();
        self.restored_character = Some(character.clone());
        // The settings file holds every section, so it is read once for all
        // the windows and written once after them, not once per window.
        let fallbacks: Vec<(String, UiWindowLayout)> = self
            .attached
            .iter()
            .filter_map(|name| Some((name.clone(), self.fallback(name, screen)?)))
            .collect();
        let windows: Vec<(&str, UiWindowLayout)> =
            fallbacks.iter().map(|(n, f)| (n.as_str(), *f)).collect();
        let saved = self
            .store
            .as_ref()
            .map(|s| s.load_window_placements(&character, &Self::resolution_key(screen), &windows))
            .unwrap_or_default();
        let mut restored = Vec::with_capacity(fallbacks.len());
        for (name, fallback) in &fallbacks {
            let placement = self.apply_restored(
                host,
                name,
                *fallback,
                saved.get(name).copied(),
                screen,
                restore_visibility,
            );
            restored.push((name.as_str(), placement));
        }
        if save_back {
            self.save_placements(&character, &restored);
        }
        self.resync(host);
    }

    /// Restore every window from the character's saved layout (on login,
    /// `/loadautoui`), saving the result back.
    pub fn restore_all(&mut self, host: &mut UiHost) {
        self.restore_all_core(host, true, true);
    }

    /// Restore every window's place and size after the display changed,
    /// keeping what is shown; nothing is saved.
    pub fn restore_after_display_change(&mut self, host: &mut UiHost) {
        self.restore_all_core(host, false, false);
    }

    /// Every window to its default place on the current screen.
    pub fn reset_to_defaults(&mut self, host: &mut UiHost) {
        self.follow(host);
        let screen = self.valid_screen();
        for name in self.attached.clone() {
            let Some(authored) = self.defaults.get(&name).copied() else {
                continue;
            };
            let layout = default_layout(&name, &authored, screen.0, screen.1, &self.defaults);
            let placement = UiWindowPlacement {
                layout,
                screen_width: screen.0,
                screen_height: screen.1,
            };
            self.placements.insert(name.clone(), placement);
            apply_placement(host, &name, &placement, screen, false);
        }
        self.resync(host);
    }

    /// Every window's last placement projected onto the current screen
    /// (the display changed).
    pub fn reflow_to_screen(&mut self, host: &mut UiHost) {
        self.follow(host);
        if !self.gameplay_active {
            return;
        }
        let screen = self.valid_screen();
        for name in self.attached.clone() {
            let Some(placement) = self.placements.get(&name).copied() else {
                continue;
            };
            let placement = with_current_state(host, &name, placement);
            apply_placement(host, &name, &placement, screen, false);
        }
        self.resync(host);
    }

    /// Pull every window back onto the screen, the top-left corner first;
    /// nothing is saved.
    pub fn clamp_all_to_screen(&mut self, host: &mut UiHost) {
        self.follow(host);
        let screen = self.valid_screen();
        for name in self.attached.clone() {
            let Some(frame) = host.root.window(&name).map(|w| w.frame) else {
                continue;
            };
            let Some(e) = host.root.element(frame) else {
                continue;
            };
            let max_x = (screen.0 as f32 - e.width).max(0.0);
            let max_y = (screen.1 as f32 - e.height).max(0.0);
            let x = clamp(e.left, 0.0, max_x);
            let y = clamp(e.top, 0.0, max_y);
            if x != e.left || y != e.top {
                host.root.move_window(&name, x, y);
            }
        }
        self.resync(host);
    }

    /// Save every window for the character (`/saveautoui`).
    pub fn save_all(&mut self, host: &mut UiHost) {
        self.follow(host);
        if !self.gameplay_active || !can_persist(&self.character) {
            return;
        }
        let character = self.character.clone();
        let names = self.attached.clone();
        let mut placements = Vec::with_capacity(names.len());
        for name in &names {
            let Some(placement) = self.placements.get(name).copied() else {
                continue;
            };
            let placement = with_current_state(host, name, placement);
            self.placements.insert(name.clone(), placement);
            placements.push((name.as_str(), placement));
        }
        self.save_placements(&character, &placements);
    }

    /// Save every window under the named profile (`/saveui name`).
    pub fn save_named(&mut self, host: &mut UiHost, profile: &str) {
        self.follow(host);
        if !self.gameplay_active {
            return;
        }
        for name in self.attached.clone() {
            let Some(layout) = capture(host, &name) else {
                continue;
            };
            if let Some(store) = &self.store
                && let Err(e) = store.save_named_window_layout(profile, &name, &layout)
            {
                self.failures
                    .push(format!("settings: window layout save failed [{name}]: {e}"));
            }
        }
    }

    /// Restore every window saved under the named profile (`/loadui
    /// name`).
    pub fn restore_named(&mut self, host: &mut UiHost, profile: &str) {
        self.follow(host);
        if !self.gameplay_active {
            return;
        }
        let screen = self.valid_screen();
        for name in self.attached.clone() {
            let Some(current) = capture(host, &name) else {
                continue;
            };
            let Some(saved) = self
                .store
                .as_ref()
                .and_then(|s| s.load_named_window_layout(profile, &name, &current))
            else {
                continue;
            };
            let layout = migrate_authored_geometry(saved, authored_geometry(host, &name, current));
            apply(
                host,
                &name,
                &layout,
                screen,
                !self.state_managed.contains(&name),
                &|_, _| (layout.x, layout.y),
            );
            if let Some(c) = capture(host, &name) {
                self.placements.insert(
                    name.clone(),
                    UiWindowPlacement {
                        layout: c,
                        screen_width: screen.0,
                        screen_height: screen.1,
                    },
                );
            }
        }
        self.resync(host);
    }
}

/// A two-stop frame's state (the toolbar): collapsed when nearer its short
/// stop.
#[must_use]
pub fn collapsible_frame_state(frame: &NineSliceFrame, height: f32) -> Option<WindowState> {
    (frame.expanded_height > frame.collapsed_height).then(|| WindowState {
        collapsed: !frame.is_expanded(height),
        ..WindowState::default()
    })
}
