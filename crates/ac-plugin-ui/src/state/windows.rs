//! The screen and the registered windows as the attached window last
//! reported them, and the placings plugins ask of it. The window places a
//! window a frame later; the answer here is where the same rules put it
//! ([`WindowReport::fit`]), and the window's next report is where it went.
//! Placings wait for the character's layout: before it is restored the
//! window would put them back, and nothing would be saved.

use std::collections::BTreeMap;

use super::PluginUi;
use crate::model::UiChange;
use crate::windows::{WindowApplied, WindowLayout, WindowParts, WindowReport, merged};

/// What the session knows of the window's screen and registered windows.
#[derive(Debug, Default)]
pub(super) struct WindowMirror {
    /// The screen, once the attached window reported it.
    screen: Option<(i32, i32)>,
    /// The character's layout is restored: placings are taken.
    ready: bool,
    /// The registered windows, in registration order.
    windows: Vec<WindowReport>,
    /// Where the placings asked since the last report put each window, so
    /// the next placing of the same window starts from there.
    pending: BTreeMap<String, WindowLayout>,
    /// The screen changed since the session last took the change.
    screen_changed: Option<(i32, i32)>,
    /// A window was registered or unregistered since the session last
    /// took the change.
    windows_changed: bool,
}

impl WindowMirror {
    /// The window reported the screen, whether the character's layout is
    /// restored, and its windows. When the layout has just been restored
    /// both changes are told again, so a plugin places the windows over
    /// the character's own layout.
    pub(super) fn report(&mut self, screen: (i32, i32), ready: bool, windows: Vec<WindowReport>) {
        if self.screen != Some(screen) {
            self.screen = Some(screen);
            self.screen_changed = Some(screen);
        }
        let names_differ = self.windows.len() != windows.len()
            || self
                .windows
                .iter()
                .zip(&windows)
                .any(|(was, now)| was.name != now.name);
        if names_differ {
            self.windows_changed = true;
        }
        if ready && !self.ready {
            self.screen_changed = Some(screen);
            self.windows_changed = true;
        }
        self.ready = ready;
        self.windows = windows;
        self.pending.clear();
    }

    /// Some windows changed: each as it is now, and where a placing of it
    /// went, so the next placing of it starts from there. A window the
    /// session does not know (one a whole report has not named yet) is left
    /// for that report.
    pub(super) fn update(&mut self, windows: Vec<WindowReport>) {
        for window in windows {
            self.pending.remove(&window.name);
            if let Some(known) = self.windows.iter_mut().find(|w| w.name == window.name) {
                *known = window;
            }
        }
    }

    /// The window left: nothing is known until another reports.
    pub(super) fn forget(&mut self) {
        *self = Self::default();
    }
}

impl PluginUi {
    /// The screen the interface lays out in, pixels: none until an
    /// attached window reported it.
    #[must_use]
    pub fn screen_size(&self) -> Option<(i32, i32)> {
        self.window_mirror.screen
    }

    /// Whether the attached window has the character's layout restored, so
    /// a placing is taken and saved for the character.
    #[must_use]
    pub fn windows_ready(&self) -> bool {
        self.window_attached && self.window_mirror.ready
    }

    /// The registered windows as the attached window last reported them, in
    /// registration order (none without a window).
    #[must_use]
    pub fn registered_windows(&self) -> &[WindowReport] {
        &self.window_mirror.windows
    }

    /// Asks the window to place registered window `name`: the `parts` of
    /// `layout`, within the window's limits and the screen, a shown state
    /// only for a window the game does not show and hide by itself. The
    /// window applies it the way it restores a saved layout and saves the
    /// result for the character, as the player's own change. The answer is
    /// where those rules put it. Placings of one window before the window
    /// reports again build on each other, in order.
    ///
    /// # Errors
    /// No window is attached, the character's layout is not restored yet
    /// (before login, or while a pre-game screen covers the game), or none
    /// of the window's windows is called `name`.
    pub fn apply_window(
        &mut self,
        name: &str,
        layout: &WindowLayout,
        parts: WindowParts,
    ) -> Result<WindowApplied, String> {
        let Some(screen) = self.window_mirror.screen.filter(|_| self.window_attached) else {
            return Err("no window is attached".to_owned());
        };
        if !self.window_mirror.ready {
            return Err(
                "the character's window layout is not restored yet: place windows after the \
                 screen and windows events that follow it"
                    .to_owned(),
            );
        }
        let Some(report) = self.window_mirror.windows.iter().find(|w| w.name == name) else {
            return Err(format!("no registered window is called '{name}'"));
        };
        let now = self
            .window_mirror
            .pending
            .get(name)
            .copied()
            .unwrap_or(report.layout);
        let fit = report.fit_from(&now, layout, parts, screen);
        let applied = WindowApplied {
            layout: fit,
            shown_ignored: parts.shown && report.state_managed,
        };
        self.changes.push(UiChange::WindowApply {
            name: name.to_owned(),
            layout: merged(&now, layout, parts),
            parts,
        });
        self.window_mirror.pending.insert(name.to_owned(), fit);
        Ok(applied)
    }

    /// The screen's new size, once, when it changed since the last call
    /// (the first report of an attached window is a change, and so is the
    /// character's layout being restored).
    pub fn take_screen_change(&mut self) -> Option<(i32, i32)> {
        self.window_mirror.screen_changed.take()
    }

    /// Whether a window was registered or unregistered since the last
    /// call (the first report of an attached window with any is such a
    /// change, and so is the character's layout being restored).
    pub fn take_windows_change(&mut self) -> bool {
        std::mem::take(&mut self.window_mirror.windows_changed)
    }
}

#[cfg(test)]
mod tests {
    use crate::PluginUi;
    use crate::model::{UiChange, UiInput};
    use crate::windows::{WindowLayout, WindowOwner, WindowParts, WindowReport};

    fn report(name: &str, state_managed: bool) -> WindowReport {
        WindowReport {
            name: name.into(),
            owner: WindowOwner::Client,
            title: None,
            layout: WindowLayout {
                x: 10.0,
                y: 10.0,
                width: 200.0,
                height: 100.0,
                visible: false,
                collapsed: false,
                maximized: false,
            },
            state_managed,
            min_size: (50.0, 50.0),
            max_size: (500.0, 500.0),
            resizable: (true, true),
        }
    }

    fn input(screen: (i32, i32), ready: bool, names: &[&str]) -> UiInput {
        UiInput::Windows {
            screen,
            ready,
            windows: names.iter().map(|n| report(n, *n == "vendor")).collect(),
        }
    }

    fn take(ui: &mut PluginUi) -> (Option<(i32, i32)>, bool) {
        (ui.take_screen_change(), ui.take_windows_change())
    }

    #[test]
    fn the_screen_and_the_windows_are_what_the_window_last_reported() {
        let mut ui = PluginUi::new();
        // Without a window nothing is known.
        ui.input(input((800, 600), true, &["radar"]));
        assert_eq!(ui.screen_size(), None);
        assert!(ui.registered_windows().is_empty());
        ui.set_window_attached(true);
        ui.input(input((800, 600), true, &["radar"]));
        assert_eq!(ui.screen_size(), Some((800, 600)));
        assert_eq!(take(&mut ui), (Some((800, 600)), true));
        // A move is no change of either.
        let mut moved = report("radar", false);
        moved.layout.x = 300.0;
        ui.input(UiInput::Windows {
            screen: (800, 600),
            ready: true,
            windows: vec![moved],
        });
        assert_eq!(take(&mut ui), (None, false));
        assert_eq!(ui.registered_windows()[0].layout.x, 300.0);
        // A new screen, a window registered.
        ui.input(input((1280, 720), true, &["radar", "vendor"]));
        assert_eq!(take(&mut ui), (Some((1280, 720)), true));
        // The window leaves: nothing is known, and no change is told.
        ui.set_window_attached(false);
        assert_eq!(ui.screen_size(), None);
        assert!(ui.registered_windows().is_empty());
        assert_eq!(take(&mut ui), (None, false));
    }

    // Before the character's layout is restored a placing is refused (the
    // restore would undo it, and nothing would be saved); once it is, both
    // changes are told again so a plugin places the windows then.
    #[test]
    fn placings_wait_for_the_characters_layout_and_hear_it_restored() {
        let mut ui = PluginUi::new();
        ui.set_window_attached(true);
        ui.input(input((800, 600), false, &["radar"]));
        assert_eq!(take(&mut ui), (Some((800, 600)), true));
        assert!(!ui.windows_ready());
        let layout = report("radar", false).layout;
        let refused = ui
            .apply_window("radar", &layout, WindowParts::ALL)
            .unwrap_err();
        assert!(refused.contains("not restored"), "{refused}");
        ui.input(input((800, 600), true, &["radar"]));
        assert!(ui.windows_ready());
        assert_eq!(take(&mut ui), (Some((800, 600)), true));
        assert!(ui.apply_window("radar", &layout, WindowParts::ALL).is_ok());
        // A pre-game screen covers the game, then goes: told again.
        ui.input(input((800, 600), false, &["radar"]));
        assert_eq!(take(&mut ui), (None, false));
        ui.input(input((800, 600), true, &["radar"]));
        assert_eq!(take(&mut ui), (Some((800, 600)), true));
    }

    #[test]
    fn a_placing_is_answered_at_once_and_asked_of_the_window() {
        let mut ui = PluginUi::new();
        let asked = WindowLayout {
            x: 700.0,
            y: 20.0,
            width: 300.0,
            height: 100.0,
            visible: true,
            collapsed: false,
            maximized: false,
        };
        assert!(ui.apply_window("radar", &asked, WindowParts::ALL).is_err());
        ui.set_window_attached(true);
        ui.input(input((800, 600), true, &["radar", "vendor"]));
        ui.take_changes();
        let error = ui
            .apply_window("nowhere", &asked, WindowParts::ALL)
            .unwrap_err();
        assert!(error.contains("nowhere"), "{error}");
        let radar = ui.apply_window("radar", &asked, WindowParts::ALL).unwrap();
        assert_eq!((radar.layout.x, radar.layout.width), (500.0, 300.0));
        assert!(radar.layout.visible);
        assert!(!radar.shown_ignored);
        let vendor = ui.apply_window("vendor", &asked, WindowParts::ALL).unwrap();
        assert!(!vendor.layout.visible);
        assert!(vendor.shown_ignored);
        let changes = ui.take_changes();
        assert_eq!(changes.len(), 2);
        // What the session knows stays the window's report until the next.
        assert_eq!(ui.registered_windows()[0].layout.x, 10.0);
    }

    // Two placings of one window before it reports again build on each
    // other: a size alone after a place keeps the new place.
    #[test]
    fn placings_of_one_window_in_a_row_build_on_each_other() {
        let mut ui = PluginUi::new();
        ui.set_window_attached(true);
        ui.input(input((800, 600), true, &["radar"]));
        ui.take_changes();
        let place = WindowParts {
            place: true,
            ..WindowParts::default()
        };
        let size = WindowParts {
            size: true,
            ..WindowParts::default()
        };
        let at = WindowLayout {
            x: 400.0,
            y: 300.0,
            ..report("radar", false).layout
        };
        ui.apply_window("radar", &at, place).unwrap();
        let bigger = WindowLayout {
            width: 450.0,
            height: 200.0,
            ..report("radar", false).layout
        };
        let second = ui.apply_window("radar", &bigger, size).unwrap();
        // Kept on the screen at its new size: 800 - 450.
        assert_eq!(
            (second.layout.x, second.layout.y, second.layout.width),
            (350.0, 300.0, 450.0)
        );
        let changes = ui.take_changes();
        let UiChange::WindowApply { layout, .. } = &changes[1] else {
            panic!("{changes:?}");
        };
        assert_eq!((layout.x, layout.y, layout.width), (400.0, 300.0, 450.0));
        // The window's next report is where it is: the next placing starts
        // from there.
        ui.input(input((800, 600), true, &["radar"]));
        let again = ui.apply_window("radar", &bigger, size).unwrap();
        assert_eq!((again.layout.x, again.layout.y), (10.0, 10.0));
    }

    // A report of the changed windows only: those are updated, the rest
    // kept, no change is told, and a placing of a reported window starts
    // from where it went.
    #[test]
    fn a_report_of_the_changed_windows_updates_only_those() {
        let mut ui = PluginUi::new();
        ui.set_window_attached(true);
        ui.input(input((800, 600), true, &["radar", "chat"]));
        take(&mut ui);
        let place = WindowParts {
            place: true,
            ..WindowParts::default()
        };
        let at = WindowLayout {
            x: 400.0,
            y: 300.0,
            ..report("radar", false).layout
        };
        ui.apply_window("radar", &at, place).unwrap();
        ui.apply_window("chat", &at, place).unwrap();
        let mut radar = report("radar", false);
        radar.layout.x = 390.0;
        ui.input(UiInput::WindowsChanged(vec![
            radar,
            report("nowhere", false),
        ]));
        assert_eq!(take(&mut ui), (None, false));
        let names: Vec<&str> = ui
            .registered_windows()
            .iter()
            .map(|w| w.name.as_str())
            .collect();
        assert_eq!(names, ["radar", "chat"]);
        assert_eq!(ui.registered_windows()[0].layout.x, 390.0);
        let size = WindowParts {
            size: true,
            ..WindowParts::default()
        };
        let radar = ui.apply_window("radar", &at, size).unwrap();
        assert_eq!(radar.layout.x, 390.0, "from where it went");
        let chat = ui.apply_window("chat", &at, size).unwrap();
        assert_eq!(chat.layout.x, 400.0, "still from its own placing");
    }
}
