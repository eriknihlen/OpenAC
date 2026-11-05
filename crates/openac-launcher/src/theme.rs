//! OpenAC's launcher colours (MainWindow.axaml) as egui colours, and the
//! visuals that apply them.

use egui::{Color32, CornerRadius, Stroke, Visuals};

/// The window behind everything (`#10171C`).
pub const BACKGROUND: Color32 = Color32::from_rgb(0x10, 0x17, 0x1C);
/// The header band (`#152029`).
pub const HEADER: Color32 = Color32::from_rgb(0x15, 0x20, 0x29);
/// Cards and dialogs (`#1D2931`).
pub const CARD: Color32 = Color32::from_rgb(0x1D, 0x29, 0x31);
/// An account group's body (`#141E24`).
pub const GROUP: Color32 = Color32::from_rgb(0x14, 0x1E, 0x24);
/// The bottom bar (`#172128`).
pub const BOTTOM: Color32 = Color32::from_rgb(0x17, 0x21, 0x28);
/// The installation and update banners (`#292820`).
pub const BANNER: Color32 = Color32::from_rgb(0x29, 0x28, 0x20);
/// A progress box (`#24344B`).
pub const PROGRESS_BOX: Color32 = Color32::from_rgb(0x24, 0x34, 0x4B);
/// Borders (`#35434B`).
pub const BORDER: Color32 = Color32::from_rgb(0x35, 0x43, 0x4B);
/// A row's top line (`#2B3941`).
pub const ROW_LINE: Color32 = Color32::from_rgb(0x2B, 0x39, 0x41);
/// Buttons (`#202C33`, edge `#405059`).
pub const BUTTON: Color32 = Color32::from_rgb(0x20, 0x2C, 0x33);
/// A button's edge.
pub const BUTTON_EDGE: Color32 = Color32::from_rgb(0x40, 0x50, 0x59);
/// The gold of the title and a ready primary button (`#DBB573`).
pub const GOLD: Color32 = Color32::from_rgb(0xDB, 0xB5, 0x73);
/// Text on gold (`#181A1B`).
pub const ON_GOLD: Color32 = Color32::from_rgb(0x18, 0x1A, 0x1B);
/// The subtitle (`#F0E4CD`).
pub const PARCHMENT: Color32 = Color32::from_rgb(0xF0, 0xE4, 0xCD);
/// Body text (`#C3D0D6`).
pub const TEXT: Color32 = Color32::from_rgb(0xC3, 0xD0, 0xD6);
/// Quiet text (`#7C8F99`).
pub const MUTED: Color32 = Color32::from_rgb(0x7C, 0x8F, 0x99);
/// Secondary text (`#A9BBC5`).
pub const SECONDARY: Color32 = Color32::from_rgb(0xA9, 0xBB, 0xC5);
/// Errors (`#FFB0A0`).
pub const ERROR: Color32 = Color32::from_rgb(0xFF, 0xB0, 0xA0);
/// An error box (`#5B2630`).
pub const ERROR_BOX: Color32 = Color32::from_rgb(0x5B, 0x26, 0x30);
/// Danger buttons (`#8A3A3A`).
pub const DANGER: Color32 = Color32::from_rgb(0x8A, 0x3A, 0x3A);

/// A profile tag's box, its edge and its text: a muted teal, apart from the
/// gold of buttons and the selected filter.
pub const TAG: Color32 = Color32::from_rgb(0x17, 0x2E, 0x33);
pub const TAG_EDGE: Color32 = Color32::from_rgb(0x2E, 0x55, 0x5C);
pub const TAG_TEXT: Color32 = Color32::from_rgb(0x8C, 0xC4, 0xC4);

/// `#RRGGBB` (the row model's colours) to a colour.
pub fn hex(text: &str) -> Color32 {
    let digits = text.trim_start_matches('#');
    let channel = |at: usize| {
        digits
            .get(at..at + 2)
            .and_then(|pair| u8::from_str_radix(pair, 16).ok())
            .unwrap_or(0)
    };
    Color32::from_rgb(channel(0), channel(2), channel(4))
}

/// `0xRRGGBB` (console runs) to a colour.
pub fn rgb(value: u32) -> Color32 {
    Color32::from_rgb((value >> 16) as u8, (value >> 8) as u8, value as u8)
}

/// Applies the launcher's dark look.
pub fn apply(ctx: &egui::Context) {
    let mut visuals = Visuals::dark();
    visuals.panel_fill = BACKGROUND;
    visuals.window_fill = CARD;
    visuals.extreme_bg_color = Color32::from_rgb(0x0C, 0x12, 0x16);
    visuals.override_text_color = Some(TEXT);
    visuals.window_stroke = Stroke::new(1.0, BORDER);
    visuals.window_corner_radius = CornerRadius::same(4);
    for widgets in [
        &mut visuals.widgets.inactive,
        &mut visuals.widgets.hovered,
        &mut visuals.widgets.active,
    ] {
        widgets.corner_radius = CornerRadius::same(3);
    }
    visuals.widgets.inactive.weak_bg_fill = BUTTON;
    visuals.widgets.inactive.bg_fill = BUTTON;
    visuals.widgets.inactive.bg_stroke = Stroke::new(1.0, BUTTON_EDGE);
    visuals.widgets.hovered.weak_bg_fill = Color32::from_rgb(0x2A, 0x38, 0x40);
    visuals.widgets.hovered.bg_stroke = Stroke::new(1.0, SECONDARY);
    visuals.selection.bg_fill = Color32::from_rgb(0x3A, 0x4E, 0x5A);
    // The launcher is dark whatever the system's theme, as OpenAC's window is.
    ctx.set_theme(egui::ThemePreference::Dark);
    ctx.set_visuals_of(egui::Theme::Dark, visuals);
    ctx.style_mut_of(egui::Theme::Dark, |style| {
        style.spacing.button_padding = egui::vec2(10.0, 5.0);
        style.spacing.item_spacing = egui::vec2(8.0, 6.0);
    });
}
