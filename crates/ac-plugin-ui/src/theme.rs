//! The plugin appearance the player picks (OpenAC `PluginUiTheme`,
//! `PluginUiThemeSettings`, `PluginUiPalette`): Classic (the client's own
//! look) or one of the themes that windows opting in with
//! `theme="plugin"` take. The window keeps the player's choice in its
//! settings and tells the session; plugins read it to paint their canvases
//! in the same colours.

use crate::model::Color8;

/// The sizes themed windows are drawn with, in interface pixels, so a canvas
/// drawn to match uses the same ones.
pub mod metrics {
    /// The corner radius of a themed window.
    pub const WINDOW_RADIUS: f32 = 10.0;
    /// The corner radius of a button, input or other control.
    pub const CONTROL_RADIUS: f32 = 6.0;
    /// The height of a themed window's title band.
    pub const HEADER_HEIGHT: f32 = 24.0;
    /// The width of a switch's track.
    pub const SWITCH_WIDTH: f32 = 26.0;
    /// The height of a switch's track.
    pub const SWITCH_HEIGHT: f32 = 14.0;
    /// The diameter of a switch's knob.
    pub const SWITCH_KNOB: f32 = 8.0;
}

/// A theme the Appearance picker offers.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Hash)]
pub enum ThemeKind {
    /// The client's own black and gold; themed windows draw unthemed.
    #[default]
    Classic,
    /// Charcoal with moss-green accents.
    Moss,
    /// Warm graphite with brass accents.
    Brass,
    /// Dark violet with lavender accents.
    LavenderDusk,
    /// Blue slate with orange accents.
    Ember,
    /// Dark warm grey with coral-red accents.
    Bloodmoon,
    /// Blue-black with icy cyan accents.
    Glacier,
    /// A light theme: paper with ink text and rust accents.
    Parchment,
}

/// The seven colours a theme draws themed windows in (OpenAC
/// `PluginUiPalette`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ThemePalette {
    /// The window's face.
    pub background: Color8,
    /// Inputs, lists and other sunken areas.
    pub field: Color8,
    /// Window and control edges.
    pub border: Color8,
    /// Body text.
    pub text: Color8,
    /// Captions, hints and disabled labels.
    pub muted: Color8,
    /// Highlights: a switch that is on, focus, links.
    pub accent: Color8,
    /// The fill behind a selected row or tab.
    pub selected: Color8,
}

const fn rgb(packed: u32) -> Color8 {
    Color8::new(
        ((packed >> 16) & 0xFF) as u8,
        ((packed >> 8) & 0xFF) as u8,
        (packed & 0xFF) as u8,
        0xFF,
    )
}

impl ThemePalette {
    /// Charcoal + moss.
    pub const MOSS: Self = Self {
        background: rgb(0x17_1D1B),
        field: rgb(0x10_1613),
        border: rgb(0x35_443B),
        text: rgb(0xE0_E8E2),
        muted: rgb(0x9A_A99E),
        accent: rgb(0x97_BE81),
        selected: rgb(0x34_4B37),
    };

    /// Warm graphite + brass.
    pub const BRASS: Self = Self {
        background: rgb(0x22_1F1B),
        field: rgb(0x18_1612),
        border: rgb(0x4C_4335),
        text: rgb(0xE9_E2D5),
        muted: rgb(0xB2_A58E),
        accent: rgb(0xC9_A665),
        selected: rgb(0x51_442D),
    };

    /// Dark violet + lavender.
    pub const LAVENDER_DUSK: Self = Self {
        background: rgb(0x17_141F),
        field: rgb(0x10_0E17),
        border: rgb(0x35_2C4A),
        text: rgb(0xEE_EAF6),
        muted: rgb(0xA6_9EBA),
        accent: rgb(0xBB_A0FF),
        selected: rgb(0x51_2F4A),
    };

    /// Blue slate + orange.
    pub const EMBER: Self = Self {
        background: rgb(0x15_1A21),
        field: rgb(0x0E_1218),
        border: rgb(0x2C_3644),
        text: rgb(0xE8_ECF1),
        muted: rgb(0x9A_A6B5),
        accent: rgb(0xFF_9447),
        selected: rgb(0x1E_3B44),
    };

    /// Dark warm grey + coral red.
    pub const BLOODMOON: Self = Self {
        background: rgb(0x1C_1617),
        field: rgb(0x14_0F10),
        border: rgb(0x43_302F),
        text: rgb(0xF2_E9E7),
        muted: rgb(0xB8_A3A0),
        accent: rgb(0xFF_6B6B),
        selected: rgb(0x4D_2327),
    };

    /// Blue-black + icy cyan.
    pub const GLACIER: Self = Self {
        background: rgb(0x11_161C),
        field: rgb(0x0A_0E13),
        border: rgb(0x26_323F),
        text: rgb(0xE4_EEF4),
        muted: rgb(0x93_A4B3),
        accent: rgb(0x5E_D6E8),
        selected: rgb(0x1F_3554),
    };

    /// The one light theme: paper face, ink text, rust accent.
    pub const PARCHMENT: Self = Self {
        background: rgb(0xED_E7DC),
        field: rgb(0xFA_F7F1),
        border: rgb(0xC9_BFAE),
        text: rgb(0x2A_2520),
        muted: rgb(0x65_5C50),
        accent: rgb(0xB4_471F),
        selected: rgb(0xDD_CFB6),
    };

    /// Whether the face is pale: a light palette, meant for dark text and
    /// art.
    #[must_use]
    pub fn is_light(&self) -> bool {
        let b = self.background;
        0.2126 * f64::from(b.r) + 0.7152 * f64::from(b.g) + 0.0722 * f64::from(b.b) > 127.5
    }
}

impl ThemeKind {
    /// Every theme in picker order.
    pub const ALL: [Self; 8] = [
        Self::Classic,
        Self::Moss,
        Self::Brass,
        Self::LavenderDusk,
        Self::Ember,
        Self::Bloodmoon,
        Self::Glacier,
        Self::Parchment,
    ];

    /// What the Appearance picker calls it (OpenAC
    /// `PluginUiThemeSettings.ThemeNames`).
    #[must_use]
    pub fn display_name(self) -> &'static str {
        match self {
            Self::Classic => "Classic",
            Self::Moss => "Charcoal + moss",
            Self::Brass => "Warm graphite + brass",
            Self::LavenderDusk => "Lavender Dusk",
            Self::Ember => "Ember",
            Self::Bloodmoon => "Bloodmoon",
            Self::Glacier => "Glacier",
            Self::Parchment => "Parchment",
        }
    }

    /// The name the settings file keeps (the enum member's name).
    #[must_use]
    pub fn saved_name(self) -> &'static str {
        match self {
            Self::Classic => "Classic",
            Self::Moss => "Moss",
            Self::Brass => "Brass",
            Self::LavenderDusk => "LavenderDusk",
            Self::Ember => "Ember",
            Self::Bloodmoon => "Bloodmoon",
            Self::Glacier => "Glacier",
            Self::Parchment => "Parchment",
        }
    }

    /// The theme a saved name stands for (OpenAC `Enum.TryParse` with
    /// `Enum.IsDefined`: a member's name, exactly, or its number); anything
    /// else is Classic.
    #[must_use]
    pub fn from_saved(name: &str) -> Self {
        let by_number = name
            .trim()
            .parse::<usize>()
            .ok()
            .and_then(|n| Self::ALL.get(n).copied());
        by_number
            .or_else(|| {
                Self::ALL
                    .into_iter()
                    .find(|t| t.saved_name() == name.trim())
            })
            .unwrap_or_default()
    }

    /// The theme a picker name stands for.
    #[must_use]
    pub fn from_display_name(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|t| t.display_name() == name)
    }

    /// Its colours; none under Classic, where themed windows draw unthemed.
    #[must_use]
    pub fn palette(self) -> Option<ThemePalette> {
        match self {
            Self::Classic => None,
            Self::Moss => Some(ThemePalette::MOSS),
            Self::Brass => Some(ThemePalette::BRASS),
            Self::LavenderDusk => Some(ThemePalette::LAVENDER_DUSK),
            Self::Ember => Some(ThemePalette::EMBER),
            Self::Bloodmoon => Some(ThemePalette::BLOODMOON),
            Self::Glacier => Some(ThemePalette::GLACIER),
            Self::Parchment => Some(ThemePalette::PARCHMENT),
        }
    }

    /// Whether it is a light theme, meant for dark text and art; never
    /// under Classic, which has no palette.
    #[must_use]
    pub fn is_light(self) -> bool {
        self.palette().is_some_and(|p| p.is_light())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn saved_names_read_back_and_anything_else_is_classic() {
        for theme in ThemeKind::ALL {
            assert_eq!(ThemeKind::from_saved(theme.saved_name()), theme);
        }
        assert_eq!(ThemeKind::from_saved("2"), ThemeKind::Brass);
        assert_eq!(ThemeKind::from_saved("7"), ThemeKind::Parchment);
        assert_eq!(ThemeKind::from_saved("8"), ThemeKind::Classic);
        assert_eq!(ThemeKind::from_saved("-1"), ThemeKind::Classic);
        assert_eq!(
            ThemeKind::from_saved("LavenderDusk"),
            ThemeKind::LavenderDusk
        );
        assert_eq!(ThemeKind::from_saved("moss"), ThemeKind::Classic);
        assert_eq!(ThemeKind::from_saved(""), ThemeKind::Classic);
    }

    #[test]
    fn the_palettes_are_openacs_bytes() {
        // PluginUiPalette.Moss / .Brass, as `ToPluginPalette` rounds them.
        assert_eq!(
            ThemePalette::MOSS.background,
            Color8::new(0x17, 0x1D, 0x1B, 255)
        );
        assert_eq!(
            ThemePalette::MOSS.accent,
            Color8::new(0x97, 0xBE, 0x81, 255)
        );
        assert_eq!(
            ThemePalette::BRASS.selected,
            Color8::new(0x51, 0x44, 0x2D, 255)
        );
        assert_eq!(ThemeKind::Classic.palette(), None);
        assert_eq!(
            ThemeKind::from_display_name("Charcoal + moss"),
            Some(ThemeKind::Moss)
        );
    }

    #[test]
    fn the_new_palettes_keep_their_bytes() {
        // One channel set from each.
        assert_eq!(
            ThemePalette::LAVENDER_DUSK.accent,
            Color8::new(0xBB, 0xA0, 0xFF, 255)
        );
        assert_eq!(
            ThemePalette::EMBER.selected,
            Color8::new(0x1E, 0x3B, 0x44, 255)
        );
        assert_eq!(
            ThemePalette::BLOODMOON.border,
            Color8::new(0x43, 0x30, 0x2F, 255)
        );
        assert_eq!(
            ThemePalette::GLACIER.field,
            Color8::new(0x0A, 0x0E, 0x13, 255)
        );
        assert_eq!(
            ThemePalette::PARCHMENT.background,
            Color8::new(0xED, 0xE7, 0xDC, 255)
        );
    }

    #[test]
    fn every_name_is_its_own_and_every_theme_but_classic_has_colours() {
        let mut display: Vec<_> = ThemeKind::ALL.iter().map(|t| t.display_name()).collect();
        let mut saved: Vec<_> = ThemeKind::ALL.iter().map(|t| t.saved_name()).collect();
        display.sort_unstable();
        display.dedup();
        saved.sort_unstable();
        saved.dedup();
        assert_eq!((display.len(), saved.len()), (8, 8));
        for theme in ThemeKind::ALL {
            assert_eq!(
                ThemeKind::from_display_name(theme.display_name()),
                Some(theme)
            );
            assert_eq!(theme.palette().is_none(), theme == ThemeKind::Classic);
        }
    }

    #[test]
    fn only_parchment_is_light() {
        for theme in ThemeKind::ALL {
            assert_eq!(theme.is_light(), theme == ThemeKind::Parchment, "{theme:?}");
        }
    }
}
