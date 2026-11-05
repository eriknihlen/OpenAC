//! The pre-game screens in the game window (connecting, character
//! selection, the credits, the creation wizard): mounted with the other
//! windows, fed every frame from the session's pre-game state, handed the
//! messages of their elements, and given the screen while they show (the
//! window's hotkeys and the chat entry stand aside).
//!
//! OpenAC `RetailUiRuntime` (`ConfigureCharacterManagement`,
//! `ConfigureCharacterCreation`, `OpenCredits`, the connection mount
//! coordinator).

use std::sync::Arc;
use std::time::Instant;

use ac_dat::DatCollection;
use ac_runtime::session::SessionCommand;
use ac_ui::plugin::dock::{self, PluginDock};
use ac_ui::pregame::chargen::catalog::DatChargenCatalog;
use ac_ui::pregame::chargen::swatch::DatSwatchTextures;
use ac_ui::pregame::{PreGameAssets, PreGameScreens};
use ac_ui::root::{ElementMessage, UiOutput};
use ac_ui::tree::ElementId;

use super::window::WindowUi;
use crate::scene::Scene;

/// The pre-game screens' name in the mounted list.
pub const PREGAME_SCREENS: &str = "pregame";

/// The client version line the selection screen shows.
#[must_use]
pub fn version_line() -> String {
    format!("openAC-rust {}", env!("CARGO_PKG_VERSION"))
}

impl WindowUi {
    /// Mount the pre-game screens from the dats (hidden until the session
    /// says they show).
    pub(super) fn mount_pregame(&mut self, dats: &Arc<DatCollection>) {
        let swatches = (
            Box::new(DatChargenCatalog::new(Arc::clone(dats))) as Box<_>,
            Box::new(DatSwatchTextures::new(
                Arc::clone(dats),
                self.images.uploader(),
            )) as Box<_>,
        );
        let (screens, notices) = PreGameScreens::mount(
            &mut self.host.root,
            &self.content,
            &version_line(),
            Some(swatches),
        );
        self.notices
            .extend(notices.into_iter().map(|n| format!("ui: {n}")));
        let any = screens.connection().is_some()
            || screens.characters().is_some()
            || screens.creation().is_some()
            || screens.credits().is_some();
        if any {
            self.mounted.push(PREGAME_SCREENS);
        }
        self.pregame = Some(screens);
    }

    /// Feed the screens the session's pre-game state; what they ask goes
    /// out with the window's next requests. While a screen shows, Enter
    /// and Tab do not open the chat entry.
    pub(super) fn feed_pregame(&mut self, scene: &mut Scene) {
        let Some(screens) = self.pregame.as_mut() else {
            return;
        };
        let assets = PreGameAssets::of(&self.content);
        let outputs = screens.update(
            &mut self.host.root,
            assets,
            scene.pregame_mut(),
            Instant::now(),
        );
        for output in outputs {
            self.host.root.emit(UiOutput::Panel(output));
        }
        let active = screens.is_active(&self.host.root);
        self.hide_in_game_windows(active);
        if active {
            if self.host.root.default_text_input.is_some() {
                self.parked_text_input = self.host.root.default_text_input.take();
            }
        } else if let Some(input) = self.parked_text_input.take()
            && self.host.root.exists(input)
        {
            self.host.root.default_text_input = Some(input);
        }
    }

    /// Retail has no in-game windows before the world: while a pre-game
    /// screen shows (`active`), every in-game window that shows is hidden
    /// (also one shown meanwhile), and the screens' going shows them again
    /// where they were. The windows stay mounted. The hiding and the
    /// showing again are not the player's: the character's saved layout
    /// never learns of them (the layouts stop counting changes as the
    /// player's before a window is hidden, and count them again only once
    /// the world shows, see `feed_status`).
    fn hide_in_game_windows(&mut self, active: bool) {
        if active
            && let Some(layouts) = self.status.layouts.as_mut()
            && layouts.gameplay_active()
        {
            layouts.set_gameplay_active(&mut self.host, false);
        }
        // The dock shows itself when it has windows to offer, so it is told.
        self.host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, _| c.set_covered(active));
        let root = &mut self.host.root;
        if active {
            let shown: Vec<(String, ElementId)> = root
                .windows()
                .iter()
                .filter(|w| root.element(w.frame).is_some_and(|e| e.visible))
                .map(|w| (w.name.clone(), w.frame))
                .collect();
            for (name, frame) in shown {
                root.set_visible(frame, false);
                self.pregame_hidden.push(name);
            }
        } else {
            for name in std::mem::take(&mut self.pregame_hidden) {
                if let Some(frame) = root.window(&name).map(|w| w.frame) {
                    root.set_visible(frame, true);
                }
            }
        }
    }

    /// The in-game windows hidden under the pre-game screens.
    #[must_use]
    pub fn hidden_under_pregame(&self) -> &[String] {
        &self.pregame_hidden
    }

    /// Whether a pre-game screen (or its dialog) shows.
    #[must_use]
    pub fn pregame_active(&self) -> bool {
        self.pregame
            .as_ref()
            .is_some_and(|s| s.is_active(&self.host.root))
    }

    /// The pre-game screens.
    #[must_use]
    pub fn pregame(&self) -> Option<&PreGameScreens> {
        self.pregame.as_ref()
    }

    /// A message from an element inside a pre-game screen: its requests
    /// become session commands. False when the element is not theirs.
    pub(super) fn pregame_message(
        &mut self,
        element: ElementId,
        message: &ElementMessage,
        commands: &mut Vec<SessionCommand>,
    ) -> bool {
        let Some(screens) = self.pregame.as_mut() else {
            return false;
        };
        let assets = PreGameAssets::of(&self.content);
        let Some(outputs) = screens.on_message(
            &mut self.host.root,
            assets,
            element,
            message,
            Instant::now(),
        ) else {
            return false;
        };
        for output in outputs {
            if let Some(command) = super::window::session_command(&output) {
                commands.push(command);
            }
        }
        true
    }

    /// Where the shown pre-game screens' buttons, rows and entries are, as
    /// `ui-probe:` lines (canvas positions turned into window pixels).
    pub(super) fn probe_pregame(&self, lines: &mut Vec<String>) {
        use ac_ui::widgets::Button;
        let Some(screens) = self.pregame.as_ref() else {
            return;
        };
        let root = &self.host.root;
        let (sx, sy) = root.canvas_scale();
        let rect = |id| {
            let (x, y) = root.screen_position(id);
            let (w, h) = root.element(id).map_or((0.0, 0.0), |e| (e.width, e.height));
            format!(
                "{} {} {} {}",
                (x * sx) as i32,
                (y * sy) as i32,
                (w * sx) as i32,
                (h * sy) as i32
            )
        };
        let mut screens_shown = Vec::new();
        if let Some(c) = screens.connection().filter(|c| c.is_visible(root)) {
            screens_shown.push(("connecting", c.root()));
        }
        if let Some(c) = screens.characters().filter(|c| c.is_active()) {
            screens_shown.push(("characters", c.root()));
            for (i, (row, id)) in c.rows().iter().enumerate() {
                let name = root
                    .widget::<Button>(*row)
                    .and_then(|b| b.label.clone())
                    .unwrap_or_default();
                lines.push(format!(
                    "ui-probe: character {i} {id:#010x} {} \"{name}\"",
                    rect(*row)
                ));
            }
        }
        if let Some(c) = screens.creation().filter(|c| c.is_open()) {
            screens_shown.push(("creation", c.root()));
        }
        if let Some(root_id) = screens.open_dialog().map(|d| d.root()) {
            screens_shown.push(("dialog", root_id));
        }
        for (name, start) in screens_shown {
            self.probe_screen(lines, name, start);
        }
    }

    /// One screen or dialog as `ui-probe:` lines: its rectangle, then its
    /// shown buttons (with their captions) and entries, in window pixels.
    pub(super) fn probe_screen(&self, lines: &mut Vec<String>, name: &str, start: ElementId) {
        use ac_ui::widgets::{Button, Field};
        let root = &self.host.root;
        let (sx, sy) = root.canvas_scale();
        let rect = |id| {
            let (x, y) = root.screen_position(id);
            let (w, h) = root.element(id).map_or((0.0, 0.0), |e| (e.width, e.height));
            format!(
                "{} {} {} {}",
                (x * sx) as i32,
                (y * sy) as i32,
                (w * sx) as i32,
                (h * sy) as i32
            )
        };
        let shown = |id| {
            let mut at = Some(id);
            while let Some(e) = at {
                if !root.element(e).is_some_and(|e| e.visible) {
                    return false;
                }
                at = root.parent(e);
            }
            true
        };
        lines.push(format!("ui-probe: screen {name} {}", rect(start)));
        let mut stack = vec![start];
        while let Some(id) = stack.pop() {
            let mut children = root.children(id);
            children.reverse();
            stack.extend(children);
            if !shown(id) {
                continue;
            }
            let dat = root.element(id).map_or(0, |e| e.dat_element_id);
            if let Some(b) = root.widget::<Button>(id) {
                if dat != 0 {
                    let label = b
                        .label
                        .as_deref()
                        .map_or_else(String::new, |l| format!(" \"{l}\""));
                    lines.push(format!(
                        "ui-probe: button {name} {dat:#010x} {}{label}",
                        rect(id)
                    ));
                }
            } else if root.widget::<Field>(id).is_some() {
                lines.push(format!("ui-probe: entry {name} {dat:#010x} {}", rect(id)));
            }
        }
    }
}
