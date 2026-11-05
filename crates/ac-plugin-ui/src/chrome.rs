//! The host-drawn chrome around a plugin window's content area: the
//! frame's border and, when the window has one, its title bar. A window
//! with a content area places its authored children inside these insets,
//! and its authored `w`/`h`/`minw`/`minh` describe that area. Classic and
//! the themes share the same insets.

/// The Classic border's width; the left, right and bottom inset.
pub const BORDER: f32 = 5.0;

/// The plugin title bar's height, the same in Classic and in a theme.
pub const TITLE_BAR_HEIGHT: f32 = 24.0;

/// The close button's side: the bar below the frame's top border.
pub const CLOSE_BUTTON_SIZE: f32 = TITLE_BAR_HEIGHT - BORDER;

/// The chrome left and right of a content area, together.
pub const HORIZONTAL_INSETS: f32 = 2.0 * BORDER;

/// The chrome above and below a content area with a title bar.
pub const VERTICAL_INSETS: f32 = TITLE_BAR_HEIGHT + BORDER;

/// Bumped whenever the insets change, so windows whose chrome grew or
/// shrank drop their saved size once.
pub const VERSION: i32 = 1;

/// The Classic close button's art, 24x25 (the inventory window's close
/// button).
pub const CLASSIC_CLOSE_SPRITE: u32 = 0x0600_4D0C;

/// The same button's pressed art, 24x25. The art has no hover state.
pub const CLASSIC_CLOSE_PRESSED_SPRITE: u32 = 0x0600_4D0D;

/// The narrowest frame a window with a title bar may be: the close button
/// and its inset, the title's inset and gap, and room for about eight
/// characters of title, so the button never covers the title.
pub const MINIMUM_BAR_FRAME_WIDTH: f32 = 96.0;

/// The close button's tooltip.
pub const CLOSE_TOOLTIP: &str = "Close";

/// The close button's tooltip on a window on the plugin dock.
pub const DOCKED_CLOSE_TOOLTIP: &str = "Close (reopen from the plugin dock)";

/// The geometry attributes whose raw values make a window's revision.
pub const GEOMETRY_ATTRIBUTES: [&str; 8] = [
    "w",
    "h",
    "minw",
    "minh",
    "resizable",
    "resize",
    "layout",
    "titlebar",
];

/// The top inset of a content area: the title bar, or the border alone.
#[must_use]
pub fn top_inset(title_bar: bool) -> f32 {
    if title_bar { TITLE_BAR_HEIGHT } else { BORDER }
}

/// The chrome above and below a content area, with or without a title bar.
#[must_use]
pub fn vertical_insets_for(title_bar: bool) -> f32 {
    if title_bar {
        VERTICAL_INSETS
    } else {
        2.0 * BORDER
    }
}

/// A frame's minimum width from its content's: the content plus the side
/// insets, floored for a title bar.
#[must_use]
pub fn frame_minimum_width(content_minimum: f32, title_bar: bool) -> f32 {
    (content_minimum + HORIZONTAL_INSETS).max(if title_bar {
        MINIMUM_BAR_FRAME_WIDTH
    } else {
        0.0
    })
}

/// What the markup states about a window's geometry, as written: each
/// geometry attribute's raw value (`None` when absent), then the chrome
/// [`VERSION`], then `scroll=<value>` when the root scrolls. Nothing
/// measured, so a bound caption or a theme never resets a player's saved
/// size. `attribute` reads the root's raw attribute.
#[must_use]
pub fn authored_inputs<'a>(attribute: impl Fn(&str) -> Option<&'a str>) -> Vec<Option<String>> {
    let mut inputs: Vec<Option<String>> = GEOMETRY_ATTRIBUTES
        .iter()
        .map(|name| attribute(name).map(str::to_owned))
        .collect();
    inputs.push(Some(VERSION.to_string()));
    // `scroll` changes the window's minimum; it enters only when present,
    // so the revision of every window without it is what it was.
    if let Some(scroll) = attribute("scroll") {
        inputs.push(Some(format!("scroll={scroll}")));
    }
    inputs
}

/// The revision of a window whose geometry its markup states rather than
/// measures: one entry per authored input, `None` where it is absent.
/// FNV-1a over each input's UTF-16 units, so the value is the same in every
/// process; it is saved with the window's layout. Never negative.
#[must_use]
pub fn authored_geometry_revision(inputs: &[Option<String>]) -> i32 {
    const PRIME: u32 = 16_777_619;
    let mut hash: u32 = 2_166_136_261;
    for input in inputs {
        let text = input.as_deref().unwrap_or("\0absent");
        for unit in text.encode_utf16() {
            hash = (hash ^ u32::from(unit)).wrapping_mul(PRIME);
        }
        // Between inputs.
        hash = (hash ^ 0xFFFF).wrapping_mul(PRIME);
    }
    (hash & 0x7FFF_FFFF) as i32
}

/// `text` if it fits in `max_width`, else its longest prefix that fits
/// with "..." after it (dat fonts have no ellipsis glyph), else "..."
/// alone, else nothing.
#[must_use]
pub fn ellipsize(text: &str, measure: impl Fn(&str) -> f32, max_width: f32) -> String {
    if measure(text) <= max_width {
        return text.to_owned();
    }
    const DOTS: &str = "...";
    // Every shorter prefix that ends on a character, longest first.
    let bounds: Vec<usize> = text.char_indices().map(|(i, _)| i).skip(1).collect();
    for &end in bounds.iter().rev() {
        let candidate = format!("{}{DOTS}", text[..end].trim_end());
        if measure(&candidate) <= max_width {
            return candidate;
        }
    }
    if measure(DOTS) <= max_width {
        DOTS.to_owned()
    } else {
        String::new()
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/PluginWindowChromeTests.cs`.

    use super::*;

    #[test]
    fn the_chrome_insets_are_the_classic_border_and_the_header_band() {
        // TheChromeInsetsAreTheClassicBorderAndTheHeaderBand
        assert_eq!(BORDER, 5.0);
        assert_eq!(TITLE_BAR_HEIGHT, 24.0);
        assert_eq!(CLOSE_BUTTON_SIZE, 19.0);
        assert_eq!(HORIZONTAL_INSETS, 10.0);
        assert_eq!(VERTICAL_INSETS, 29.0);
    }

    fn inputs(values: &[Option<&str>]) -> Vec<Option<String>> {
        values.iter().map(|v| v.map(str::to_owned)).collect()
    }

    #[test]
    fn the_revision_is_fnv1a_over_the_authored_inputs() {
        // TheRevisionIsFnv1aOverTheAuthoredInputs (pinned in the C# test)
        let v = inputs(&[
            Some("300"),
            Some("200"),
            None,
            None,
            None,
            None,
            None,
            Some("true"),
            Some("1"),
        ]);
        assert_eq!(authored_geometry_revision(&v), 1_338_326_359);
    }

    #[test]
    fn an_absent_input_is_not_an_empty_one() {
        // AnAbsentInputIsNotAnEmptyOne
        assert_ne!(
            authored_geometry_revision(&inputs(&[None, Some("1")])),
            authored_geometry_revision(&inputs(&[Some(""), Some("1")]))
        );
    }

    #[test]
    fn changing_any_input_changes_the_revision() {
        // ChangingAnyInputChangesTheRevision
        let base = [
            Some("300"),
            Some("200"),
            Some("250"),
            Some("150"),
            Some("true"),
            Some("x"),
            None,
            Some("true"),
            Some("1"),
        ];
        for index in 0..base.len() {
            let before = authored_geometry_revision(&inputs(&base));
            let mut changed = inputs(&base);
            changed[index] = Some(match &changed[index] {
                None => "row".to_owned(),
                Some(v) => format!("{v}0"),
            });
            let after = authored_geometry_revision(&changed);
            assert_ne!(before, after, "input {index}");
            assert!(after >= 0);
        }
    }

    #[test]
    fn the_authored_inputs_are_the_geometry_attributes_then_the_chrome_version() {
        // TheAuthoredInputsAreTheGeometryAttributesThenTheChromeVersion
        let attrs = [
            ("x", "9"),
            ("title", "T"),
            ("theme", "plugin"),
            ("w", "300"),
            ("h", "200"),
            ("minw", "250"),
            ("resizable", "true"),
            ("resize", "x"),
            ("titlebar", "true"),
        ];
        let read = |name: &str| attrs.iter().find(|(n, _)| *n == name).map(|(_, v)| *v);
        assert_eq!(
            authored_inputs(read),
            inputs(&[
                Some("300"),
                Some("200"),
                Some("250"),
                None,
                Some("true"),
                Some("x"),
                None,
                Some("true"),
                Some("1"),
            ])
        );
    }

    #[test]
    fn long_titles_are_shortened_with_dots() {
        // LongTitlesAreShortenedWithDots
        let measure = |s: &str| s.chars().count() as f32 * 10.0;
        for (text, width, expected) in [
            ("Buffs", 50.0, "Buffs"),
            ("Buff Bot", 70.0, "Buff..."),
            ("Buff Bot", 60.0, "Buf..."),
            ("Buff Bot", 30.0, "..."),
            ("Buff Bot", 29.0, ""),
        ] {
            assert_eq!(ellipsize(text, measure, width), expected, "{text} {width}");
        }
    }
}
