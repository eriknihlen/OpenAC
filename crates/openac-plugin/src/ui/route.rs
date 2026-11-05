//! Route-editor and destination-search helpers for navigation plugins
//! (OpenAC `PluginRouteEditor`): named destinations, a
//! route's stable-keyed rows, and a small, deterministic search a text
//! field can bind its suggestions to.

use super::MapPoint;

/// A named destination a navigation plugin offers.
#[derive(Debug, Clone, PartialEq)]
pub struct Destination {
    /// Its name.
    pub name: String,
    /// Where it is.
    pub position: MapPoint,
}

/// One row of a route editor.
#[derive(Debug, Clone, PartialEq)]
pub struct RouteLegRow {
    /// Stable across edits.
    pub key: String,
    /// Where the leg goes.
    pub destination: String,
    /// How (walk, portal, ...).
    pub leg_type: String,
    /// Something to do there.
    pub action: Option<String>,
    /// How far.
    pub distance: Option<f64>,
}

impl RouteLegRow {
    /// A row without action or distance.
    #[must_use]
    pub fn new(key: &str, destination: &str, leg_type: &str) -> Self {
        Self {
            key: key.to_owned(),
            destination: destination.to_owned(),
            leg_type: leg_type.to_owned(),
            action: None,
            distance: None,
        }
    }
}

/// An editable route with stable row keys (OpenAC `PluginRouteEditorState`).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct RouteEditorState {
    legs: Vec<RouteLegRow>,
}

impl RouteEditorState {
    /// The rows in display order.
    #[must_use]
    pub fn legs(&self) -> &[RouteLegRow] {
        &self.legs
    }

    /// Replaces the route.
    pub fn set_legs(&mut self, legs: impl IntoIterator<Item = RouteLegRow>) {
        self.legs = legs.into_iter().collect();
    }

    /// Removes a row by key; false when there is none.
    pub fn remove(&mut self, key: &str) -> bool {
        let Some(index) = self.legs.iter().position(|row| row.key == key) else {
            return false;
        };
        self.legs.remove(index);
        true
    }

    /// Moves a row to `new_index`; false for an unknown key or an index out
    /// of range.
    pub fn move_to(&mut self, key: &str, new_index: usize) -> bool {
        let Some(old) = self.legs.iter().position(|row| row.key == key) else {
            return false;
        };
        if new_index >= self.legs.len() {
            return false;
        }
        let row = self.legs.remove(old);
        self.legs.insert(new_index, row);
        true
    }
}

/// Destinations whose names contain `query` (any case, trimmed), the ones
/// that start with it first, then by name (ordinal, ignoring case, as .NET upper-cases), at most `limit`
/// (OpenAC `PluginDestinationSearch.Suggest`). An empty query matches
/// everything.
#[must_use]
pub fn suggest(destinations: &[Destination], query: Option<&str>, limit: usize) -> Vec<String> {
    if limit == 0 {
        return Vec::new();
    }
    let needle = query.map(str::trim).unwrap_or("").to_lowercase();
    let mut matches: Vec<&Destination> = destinations
        .iter()
        .filter(|d| needle.is_empty() || d.name.to_lowercase().contains(&needle))
        .collect();
    // A stable sort: equal names keep their order, as LINQ's OrderBy does.
    matches.sort_by(|a, b| {
        let not_prefix =
            |d: &Destination| !needle.is_empty() && !d.name.to_lowercase().starts_with(&needle);
        not_prefix(a)
            .cmp(&not_prefix(b))
            .then_with(|| a.name.to_uppercase().cmp(&b.name.to_uppercase()))
    });
    matches
        .into_iter()
        .take(limit)
        .map(|d| d.name.clone())
        .collect()
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Plugin.Tests/PluginDestinationSearchTests.cs`.

    use super::*;

    fn place(name: &str) -> Destination {
        Destination {
            name: name.to_owned(),
            position: MapPoint::default(),
        }
    }

    #[test]
    fn prefixes_sort_before_contains_matches_and_are_capped() {
        let mut places: Vec<Destination> =
            (0..5000).map(|i| place(&format!("Place {i:04}"))).collect();
        places.push(place("Old Place"));
        assert_eq!(
            suggest(&places, Some("place"), 3),
            ["Place 0000", "Place 0001", "Place 0002"]
        );
        // The contains match comes after every prefix match.
        assert_eq!(
            suggest(&places, Some("place"), 5001).last().unwrap(),
            "Old Place"
        );
    }

    #[test]
    fn the_route_editor_removes_and_reorders_stable_rows() {
        let mut editor = RouteEditorState::default();
        editor.set_legs([
            RouteLegRow::new("a", "A", "walk"),
            RouteLegRow::new("b", "B", "portal"),
            RouteLegRow::new("c", "C", "walk"),
        ]);
        assert!(editor.move_to("c", 0));
        assert!(editor.remove("b"));
        assert_eq!(
            editor
                .legs()
                .iter()
                .map(|r| r.key.as_str())
                .collect::<Vec<_>>(),
            ["c", "a"]
        );
        assert!(!editor.move_to("a", 5));
        assert!(!editor.remove("zz"));
    }

    #[test]
    fn empty_or_invalid_limits_are_safe() {
        let holtburg = [place("Holtburg")];
        assert_eq!(suggest(&holtburg, None, 20).len(), 1);
        assert!(suggest(&holtburg, Some("holt"), 0).is_empty());
    }
}
