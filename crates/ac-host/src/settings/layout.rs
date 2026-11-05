//! Saved window places: per character, per screen resolution, the latest
//! placement, named layout profiles, and the version-1 position records
//! they migrate from.

use std::collections::HashMap;

use crate::json::{Member, Node, Value};

use super::{SettingsStore, float_text, member, object_member, set_member};

/// A window's saved position (the version-1 record).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct UiWindowPosition {
    /// Left, pixels.
    pub x: f32,
    /// Top, pixels.
    pub y: f32,
}

/// A window's saved place, size and state.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct UiWindowLayout {
    /// Left, pixels.
    pub x: f32,
    /// Top, pixels.
    pub y: f32,
    /// Width, pixels.
    pub width: f32,
    /// Height, pixels.
    pub height: f32,
    /// Shown.
    pub visible: bool,
    /// Collapsed.
    pub collapsed: bool,
    /// Maximized.
    pub maximized: bool,
    /// The revision of the authored geometry it was saved against.
    pub authored_geometry_revision: i32,
}

/// A layout and the screen it was saved on.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct UiWindowPlacement {
    /// The layout.
    pub layout: UiWindowLayout,
    /// Screen width, pixels.
    pub screen_width: i32,
    /// Screen height, pixels.
    pub screen_height: i32,
}

/// `"<width>x<height>"` with both positive.
fn parse_resolution(key: &str) -> Option<(i32, i32)> {
    let separator = key.find(['x', 'X']).filter(|i| *i > 0)?;
    let width = key[..separator].parse::<i32>().ok()?;
    let height = key[separator + 1..].parse::<i32>().ok()?;
    (width > 0 && height > 0).then_some((width, height))
}

fn object(node: Option<&Node>) -> Option<&[Member]> {
    match node.map(|n| &n.value) {
        Some(Value::Object(m)) => Some(m),
        _ => None,
    }
}

fn path<'a>(root: &'a [Member], names: &[&str]) -> Option<&'a [Member]> {
    let mut at = root;
    for name in names {
        at = object(member(at, name))?;
    }
    Some(at)
}

/// A number member as a float; anything else falls back.
fn node_float(m: &[Member], name: &str, fallback: f32) -> f32 {
    match member(m, name).map(|n| &n.value) {
        Some(Value::Number(raw)) => raw.parse::<f32>().unwrap_or(fallback),
        _ => fallback,
    }
}

/// A boolean member; anything else falls back.
fn node_bool(m: &[Member], name: &str, fallback: bool) -> bool {
    match member(m, name).map(|n| &n.value) {
        Some(Value::Bool(b)) => *b,
        _ => fallback,
    }
}

/// A whole-number member that fits 32 bits; anything else falls back.
fn node_int(m: &[Member], name: &str, fallback: i32) -> i32 {
    match member(m, name).map(|n| &n.value) {
        Some(Value::Number(raw)) => raw.parse::<i32>().unwrap_or(fallback),
        _ => fallback,
    }
}

fn read_layout(m: &[Member], fallback: &UiWindowLayout) -> UiWindowLayout {
    UiWindowLayout {
        x: node_float(m, "x", fallback.x),
        y: node_float(m, "y", fallback.y),
        width: node_float(m, "width", fallback.width),
        height: node_float(m, "height", fallback.height),
        visible: node_bool(m, "visible", fallback.visible),
        collapsed: node_bool(m, "collapsed", fallback.collapsed),
        maximized: node_bool(m, "maximized", fallback.maximized),
        // A missing revision is 0, whatever the fallback says.
        authored_geometry_revision: node_int(m, "authoredGeometryRevision", 0),
    }
}

fn number(f: f32) -> Value {
    Value::Number(float_text(f))
}

fn layout_value(l: &UiWindowLayout) -> Value {
    Value::Object(vec![
        Member::made("x", number(l.x)),
        Member::made("y", number(l.y)),
        Member::made("width", number(l.width)),
        Member::made("height", number(l.height)),
        Member::made("visible", Value::Bool(l.visible)),
        Member::made("collapsed", Value::Bool(l.collapsed)),
        Member::made("maximized", Value::Bool(l.maximized)),
        Member::made(
            "authoredGeometryRevision",
            Value::Number(l.authored_geometry_revision.to_string()),
        ),
    ])
}

/// The entry for `window` among a character's resolutions nearest to
/// `width` x `height` (squared distance; the first of equals wins), with
/// that resolution.
fn nearest<'a>(
    resolutions: &'a [Member],
    window: &str,
    width: i32,
    height: i32,
) -> Option<(&'a [Member], i32, i32)> {
    let mut best: Option<(&[Member], i32, i32)> = None;
    let mut best_distance = i64::MAX;
    for entry in resolutions {
        let Some(candidate) = object(Some(&entry.value)).and_then(|w| object(member(w, window)))
        else {
            continue;
        };
        let Some((w, h)) = parse_resolution(&entry.name) else {
            continue;
        };
        let dx = i64::from(w) - i64::from(width);
        let dy = i64::from(h) - i64::from(height);
        let distance = dx * dx + dy * dy;
        if distance >= best_distance {
            continue;
        }
        best_distance = distance;
        best = Some((candidate, w, h));
    }
    best
}

fn invalid(what: &str) -> std::io::Error {
    std::io::Error::new(std::io::ErrorKind::InvalidInput, what.to_owned())
}

/// The placement of `window` for `toon` in `root` (see
/// [`SettingsStore::load_window_placement`]).
fn read_placement(
    root: &[Member],
    toon: &str,
    width: i32,
    height: i32,
    window: &str,
    fallback: &UiWindowLayout,
) -> Option<UiWindowPlacement> {
    if let Some(latest) = path(root, &["windowPlacements", toon, window]) {
        let screen_width = node_int(latest, "screenWidth", 0);
        let screen_height = node_int(latest, "screenHeight", 0);
        if screen_width > 0
            && screen_height > 0
            && let Some(layout) = object(member(latest, "layout"))
        {
            return Some(UiWindowPlacement {
                layout: read_layout(layout, fallback),
                screen_width,
                screen_height,
            });
        }
    }
    if let Some(resolutions) = path(root, &["windowLayouts", toon])
        && let Some((node, w, h)) = nearest(resolutions, window, width, height)
    {
        return Some(UiWindowPlacement {
            layout: read_layout(node, fallback),
            screen_width: w,
            screen_height: h,
        });
    }
    let position = path(root, &["windowPositions", toon, window])?;
    Some(UiWindowPlacement {
        layout: UiWindowLayout {
            x: node_float(position, "x", fallback.x),
            y: node_float(position, "y", fallback.y),
            ..*fallback
        },
        screen_width: width,
        screen_height: height,
    })
}

/// Put where `window` is for `toon` into `root`: the latest placement, and
/// the same layout under its resolution.
fn put_placement(root: &mut Vec<Member>, toon: &str, window: &str, placement: &UiWindowPlacement) {
    let placements = object_member(root, "windowPlacements");
    let toon_map = object_member(placements, toon);
    set_member(
        toon_map,
        window,
        Value::Object(vec![
            Member::made(
                "screenWidth",
                Value::Number(placement.screen_width.to_string()),
            ),
            Member::made(
                "screenHeight",
                Value::Number(placement.screen_height.to_string()),
            ),
            Member::made("layout", layout_value(&placement.layout)),
        ]),
    );
    let key = format!("{}x{}", placement.screen_width, placement.screen_height);
    let layouts = object_member(root, "windowLayouts");
    let resolutions = object_member(layouts, toon);
    let windows = object_member(resolutions, &key);
    set_member(windows, window, layout_value(&placement.layout));
}

impl SettingsStore {
    /// The version-1 position of `window` for character `toon`.
    #[must_use]
    pub fn load_window_position(&self, toon: &str, window: &str) -> Option<UiWindowPosition> {
        let root = self.read_root().ok()??;
        let node = path(&root, &["windowPositions", toon, window])?;
        let x = match member(node, "x").map(|n| &n.value) {
            Some(Value::Number(raw)) => raw.parse::<f32>().ok()?,
            _ => return None,
        };
        let y = match member(node, "y").map(|n| &n.value) {
            Some(Value::Number(raw)) => raw.parse::<f32>().ok()?,
            _ => return None,
        };
        Some(UiWindowPosition { x, y })
    }

    /// Save a version-1 position, keeping every other section.
    pub fn save_window_position(
        &self,
        toon: &str,
        window: &str,
        position: UiWindowPosition,
    ) -> std::io::Result<()> {
        if toon.trim().is_empty() || window.trim().is_empty() {
            return Err(invalid("a window position needs a character and a window"));
        }
        let mut root = self.load_mutable_root()?;
        let positions = object_member(&mut root, "windowPositions");
        let toon_map = object_member(positions, toon);
        set_member(
            toon_map,
            window,
            Value::Object(vec![
                Member::made("x", number(position.x)),
                Member::made("y", number(position.y)),
            ]),
        );
        self.write_mutable_root(root)
    }

    /// The layout of `window` for `toon` at `resolution` (`WxH`): the exact
    /// resolution, else the nearest saved one, else the version-1 position
    /// over `fallback`; `None` when there is nothing.
    #[must_use]
    pub fn load_window_layout(
        &self,
        toon: &str,
        resolution: &str,
        window: &str,
        fallback: &UiWindowLayout,
    ) -> Option<UiWindowLayout> {
        let root = self.read_root().ok()??;
        let mut node = path(&root, &["windowLayouts", toon, resolution, window]);
        if node.is_none()
            && let Some(resolutions) = path(&root, &["windowLayouts", toon])
            && let Some((w, h)) = parse_resolution(resolution)
        {
            node = nearest(resolutions, window, w, h).map(|(n, _, _)| n);
        }
        if let Some(node) = node {
            return Some(read_layout(node, fallback));
        }
        let legacy = path(&root, &["windowPositions", toon, window])?;
        Some(UiWindowLayout {
            x: node_float(legacy, "x", fallback.x),
            y: node_float(legacy, "y", fallback.y),
            ..*fallback
        })
    }

    /// Save the layout of `window` for `toon` at `resolution`.
    pub fn save_window_layout(
        &self,
        toon: &str,
        resolution: &str,
        window: &str,
        layout: &UiWindowLayout,
    ) -> std::io::Result<()> {
        if toon.trim().is_empty() || resolution.trim().is_empty() || window.trim().is_empty() {
            return Err(invalid(
                "a window layout needs a character, a resolution and a window",
            ));
        }
        let mut root = self.load_mutable_root()?;
        let layouts = object_member(&mut root, "windowLayouts");
        let resolutions = object_member(layouts, toon);
        let windows = object_member(resolutions, resolution);
        set_member(windows, window, layout_value(layout));
        self.write_mutable_root(root)
    }

    /// Where `window` was last placed for `toon`, with the screen it was
    /// placed on: the latest placement, else the nearest per-resolution
    /// layout, else the version-1 position at the requested resolution.
    /// `None` for an unreadable `resolution` or nothing saved.
    #[must_use]
    pub fn load_window_placement(
        &self,
        toon: &str,
        resolution: &str,
        window: &str,
        fallback: &UiWindowLayout,
    ) -> Option<UiWindowPlacement> {
        let (width, height) = parse_resolution(resolution)?;
        let root = self.read_root().ok()??;
        read_placement(&root, toon, width, height, window, fallback)
    }

    /// [`Self::load_window_placement`] for several windows, each with its
    /// fallback, from one read of the file: the windows with something
    /// saved, by name.
    #[must_use]
    pub fn load_window_placements(
        &self,
        toon: &str,
        resolution: &str,
        windows: &[(&str, UiWindowLayout)],
    ) -> HashMap<String, UiWindowPlacement> {
        let mut found = HashMap::new();
        let Some((width, height)) = parse_resolution(resolution) else {
            return found;
        };
        if windows.is_empty() {
            return found;
        }
        let Ok(Some(root)) = self.read_root() else {
            return found;
        };
        for (window, fallback) in windows {
            if let Some(placement) = read_placement(&root, toon, width, height, window, fallback) {
                found.insert((*window).to_owned(), placement);
            }
        }
        found
    }

    /// Save where `window` is now for `toon`: the latest placement, and the
    /// same layout under its resolution. A screen size of zero or less is
    /// refused before anything is written.
    pub fn save_window_placement(
        &self,
        toon: &str,
        window: &str,
        placement: &UiWindowPlacement,
    ) -> std::io::Result<()> {
        self.save_window_placements(toon, &[(window, *placement)])
    }

    /// [`Self::save_window_placement`] for several windows with one read
    /// and one write of the file. Every entry is checked before anything is
    /// written; an empty list writes nothing.
    pub fn save_window_placements(
        &self,
        toon: &str,
        placements: &[(&str, UiWindowPlacement)],
    ) -> std::io::Result<()> {
        if toon.trim().is_empty() || placements.iter().any(|(w, _)| w.trim().is_empty()) {
            return Err(invalid("a window placement needs a character and a window"));
        }
        if placements
            .iter()
            .any(|(_, p)| p.screen_width <= 0 || p.screen_height <= 0)
        {
            return Err(invalid("a window placement needs a positive screen size"));
        }
        if placements.is_empty() {
            return Ok(());
        }
        let mut root = self.load_mutable_root()?;
        for (window, placement) in placements {
            put_placement(&mut root, toon, window, placement);
        }
        self.write_mutable_root(root)
    }

    /// Window `window` of the named layout profile `profile`.
    #[must_use]
    pub fn load_named_window_layout(
        &self,
        profile: &str,
        window: &str,
        fallback: &UiWindowLayout,
    ) -> Option<UiWindowLayout> {
        let root = self.read_root().ok()??;
        let node = path(&root, &["namedWindowLayouts", profile, window])?;
        Some(read_layout(node, fallback))
    }

    /// Save window `window` of the named layout profile `profile`.
    pub fn save_named_window_layout(
        &self,
        profile: &str,
        window: &str,
        layout: &UiWindowLayout,
    ) -> std::io::Result<()> {
        if window.trim().is_empty() {
            return Err(invalid("a named window layout needs a window"));
        }
        let mut root = self.load_mutable_root()?;
        let profiles = object_member(&mut root, "namedWindowLayouts");
        let windows = object_member(profiles, profile);
        set_member(windows, window, layout_value(layout));
        self.write_mutable_root(root)
    }
}
