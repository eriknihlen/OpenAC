//! The client's registered windows as the session knows them: the screen
//! the interface lays out in, and each window's place, size, state and
//! limits, as the attached window last reported them. Plugins read them and
//! ask for a window to be placed; the window does the placing, by the same
//! rules as the layouts it saves and restores, and reports the result.

/// Whose a registered window is.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub enum WindowOwner {
    /// One of the client's own windows.
    Client,
    /// The plugin dock.
    Dock,
    /// A plugin's window: the plugin's id.
    Plugin(String),
}

/// A window's place, size and state, in interface pixels from the screen's
/// top-left corner.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct WindowLayout {
    /// The left edge.
    pub x: f32,
    /// The top edge.
    pub y: f32,
    /// Width.
    pub width: f32,
    /// Height.
    pub height: f32,
    /// Shown.
    pub visible: bool,
    /// Collapsed to its short stop (the toolbar).
    pub collapsed: bool,
    /// Maximized (the chat window).
    pub maximized: bool,
}

/// Which parts of a [`WindowLayout`] a placing applies; the window keeps
/// the others as they are.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct WindowParts {
    /// The left and top edges.
    pub place: bool,
    /// Width and height.
    pub size: bool,
    /// Shown or hidden (never for a window the game shows and hides).
    pub shown: bool,
    /// Collapsed and maximized.
    pub state: bool,
}

impl WindowParts {
    /// Every part.
    pub const ALL: Self = Self {
        place: true,
        size: true,
        shown: true,
        state: true,
    };
}

/// A registered window as the attached window reports it.
#[derive(Debug, Clone, PartialEq)]
pub struct WindowReport {
    /// Its registered name: what the saved layouts are kept under.
    pub name: String,
    /// Whose it is.
    pub owner: WindowOwner,
    /// What its title bar says, for a window that has one.
    pub title: Option<String>,
    /// Where it is now, as the layouts save it.
    pub layout: WindowLayout,
    /// The game shows and hides it by itself (a vendor, a trade, the
    /// combat bar): its shown state is never restored or saved.
    pub state_managed: bool,
    /// Its smallest width and height.
    pub min_size: (f32, f32),
    /// Its largest width and height.
    pub max_size: (f32, f32),
    /// Whether its width, and its height, can change: a window fixed on an
    /// axis keeps its size there whatever is asked.
    pub resizable: (bool, bool),
}

impl WindowReport {
    /// Where the window ends up when `parts` of `layout` are applied to it
    /// on a screen of `screen` pixels: its size within its limits and the
    /// screen (unchanged on an axis it cannot be resized on), its place on
    /// the screen, its shown state unchanged when the game manages it. The
    /// window's own state can still change its size, and with it its place
    /// (a collapsed toolbar, a maximized chat window, a dock closed to its
    /// tile); the window's next report says.
    #[must_use]
    pub fn fit(
        &self,
        layout: &WindowLayout,
        parts: WindowParts,
        screen: (i32, i32),
    ) -> WindowLayout {
        self.fit_from(&self.layout, layout, parts, screen)
    }

    /// [`Self::fit`] from `now` instead of the reported layout: where the
    /// window is once the placings already asked of it are applied.
    #[must_use]
    pub fn fit_from(
        &self,
        now: &WindowLayout,
        layout: &WindowLayout,
        parts: WindowParts,
        screen: (i32, i32),
    ) -> WindowLayout {
        let now = *now;
        let screen = (screen.0.max(1) as f32, screen.1.max(1) as f32);
        let pick = |asked: f32, current: f32, take: bool| {
            if take && asked.is_finite() {
                asked
            } else {
                current
            }
        };
        let dimension = |value: f32, minimum: f32, maximum: f32, screen: f32| {
            let upper = minimum.max(maximum.min(screen));
            clamp(value, minimum, upper)
        };
        let width = dimension(
            pick(layout.width, now.width, parts.size && self.resizable.0),
            self.min_size.0,
            self.max_size.0,
            screen.0,
        );
        let height = dimension(
            pick(layout.height, now.height, parts.size && self.resizable.1),
            self.min_size.1,
            self.max_size.1,
            screen.1,
        );
        let x = clamp(
            pick(layout.x, now.x, parts.place),
            0.0,
            (screen.0 - width).max(0.0),
        );
        let y = clamp(
            pick(layout.y, now.y, parts.place),
            0.0,
            (screen.1 - height).max(0.0),
        );
        let state = |asked: bool, current: bool| if parts.state { asked } else { current };
        WindowLayout {
            x,
            y,
            width,
            height,
            visible: if parts.shown && !self.state_managed {
                layout.visible
            } else {
                now.visible
            },
            collapsed: state(layout.collapsed, now.collapsed),
            maximized: state(layout.maximized, now.maximized),
        }
    }

    /// `layout` with the parts `parts` leaves out taken from where the
    /// window is now: what the window is asked to apply as a whole.
    #[must_use]
    pub fn merged(&self, layout: &WindowLayout, parts: WindowParts) -> WindowLayout {
        merged(&self.layout, layout, parts)
    }
}

/// `layout` with the parts `parts` leaves out taken from `now`.
#[must_use]
pub fn merged(now: &WindowLayout, layout: &WindowLayout, parts: WindowParts) -> WindowLayout {
    let pick = |take: bool, asked: f32, current: f32| if take { asked } else { current };
    let flag = |take: bool, asked: bool, current: bool| if take { asked } else { current };
    WindowLayout {
        x: pick(parts.place, layout.x, now.x),
        y: pick(parts.place, layout.y, now.y),
        width: pick(parts.size, layout.width, now.width),
        height: pick(parts.size, layout.height, now.height),
        visible: flag(parts.shown, layout.visible, now.visible),
        collapsed: flag(parts.state, layout.collapsed, now.collapsed),
        maximized: flag(parts.state, layout.maximized, now.maximized),
    }
}

/// What a placing answers: where the window ends up, and whether a shown
/// state asked of a window the game shows and hides was left alone.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WindowApplied {
    /// Where the window ends up ([`WindowReport::fit`]).
    pub layout: WindowLayout,
    /// The shown state was asked for but not applied: the game shows and
    /// hides this window.
    pub shown_ignored: bool,
}

/// `value` within `min..=max`, `min` winning when they cross (the same
/// rule the window's layouts use).
fn clamp(value: f32, min: f32, max: f32) -> f32 {
    if value < min {
        min
    } else if value > max {
        max.max(min)
    } else {
        value
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn report(state_managed: bool) -> WindowReport {
        WindowReport {
            name: "radar".into(),
            owner: WindowOwner::Client,
            title: None,
            layout: WindowLayout {
                x: 10.0,
                y: 20.0,
                width: 200.0,
                height: 150.0,
                visible: true,
                collapsed: false,
                maximized: false,
            },
            state_managed,
            min_size: (100.0, 80.0),
            max_size: (400.0, 300.0),
            resizable: (true, true),
        }
    }

    #[test]
    fn a_fit_keeps_the_size_in_its_limits_and_the_window_on_the_screen() {
        let asked = WindowLayout {
            x: 1900.0,
            y: -5.0,
            width: 900.0,
            height: 10.0,
            visible: false,
            collapsed: false,
            maximized: false,
        };
        let fit = report(false).fit(&asked, WindowParts::ALL, (1920, 1080));
        assert_eq!((fit.width, fit.height), (400.0, 80.0));
        assert_eq!((fit.x, fit.y), (1520.0, 0.0));
        assert!(!fit.visible);
    }

    #[test]
    fn a_fit_takes_only_the_parts_asked_for() {
        let asked = WindowLayout {
            x: 500.0,
            y: 400.0,
            width: 300.0,
            height: 250.0,
            visible: false,
            collapsed: true,
            maximized: false,
        };
        let place_only = WindowParts {
            place: true,
            ..WindowParts::default()
        };
        let fit = report(false).fit(&asked, place_only, (1920, 1080));
        assert_eq!(
            fit,
            WindowLayout {
                x: 500.0,
                y: 400.0,
                ..report(false).layout
            }
        );
        let merged = report(false).merged(&asked, place_only);
        assert_eq!(merged, fit);
    }

    #[test]
    fn a_window_the_game_manages_keeps_its_shown_state() {
        let asked = WindowLayout {
            visible: false,
            ..report(true).layout
        };
        let fit = report(true).fit(&asked, WindowParts::ALL, (1920, 1080));
        assert!(fit.visible);
    }

    #[test]
    fn a_screen_smaller_than_the_minimum_keeps_the_minimum() {
        let fit = report(false).fit(&report(false).layout, WindowParts::ALL, (90, 60));
        assert_eq!(
            (fit.width, fit.height, fit.x, fit.y),
            (100.0, 80.0, 0.0, 0.0)
        );
    }

    #[test]
    fn a_window_fixed_on_an_axis_keeps_its_size_there() {
        let fixed_height = WindowReport {
            resizable: (true, false),
            ..report(false)
        };
        let asked = WindowLayout {
            x: 0.0,
            y: 1000.0,
            width: 300.0,
            height: 280.0,
            ..report(false).layout
        };
        let fit = fixed_height.fit(&asked, WindowParts::ALL, (800, 600));
        assert_eq!((fit.width, fit.height), (300.0, 150.0));
        // The place follows the size the window keeps.
        assert_eq!(fit.y, 450.0);
    }
}
