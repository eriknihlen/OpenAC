//! The graphics profile the window runs with: the quality preset's
//! streaming window, anti-aliasing, texture filtering and streaming
//! completions, narrowed by the landscape draw distance, overridden by
//! startup options and shrunk by "interface only"; and Potato Mode, which
//! runs every quality choice at its cheapest while the stored choices stay
//! the player's.

use ac_host::settings::{DisplaySettings, ParticleRange, QualityPreset};

/// What a quality preset sets.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct QualitySettings {
    /// Landblocks loaded in full around the player's (the near tier).
    pub near_radius: u32,
    /// Landblocks whose terrain is loaded around the player's.
    pub far_radius: u32,
    /// Multisample anti-aliasing samples: 0 (off), 2, 4 or 8.
    pub msaa_samples: u32,
    /// Anisotropic filtering of the terrain: 1 (off), 4, 8 or 16.
    pub anisotropic_level: u16,
    /// Alpha-to-coverage for alpha-tested objects (only with MSAA).
    pub alpha_to_coverage: bool,
    /// The streaming budget's "completions per frame" (4 is the authored
    /// profile; the budget scales by this over 4).
    pub max_completions_per_frame: u32,
}

impl QualitySettings {
    /// The settings of a preset.
    #[must_use]
    pub fn from_preset(preset: QualityPreset) -> Self {
        let (near, far, msaa, aniso, a2c, completions) = match preset {
            QualityPreset::Potato => (1, 3, 0, 1, false, 2),
            QualityPreset::Low => (2, 5, 0, 4, false, 2),
            QualityPreset::Medium => (3, 8, 2, 8, false, 3),
            QualityPreset::High => (4, 12, 4, 16, true, 4),
            QualityPreset::Ultra => (5, 15, 4, 16, true, 6),
        };
        Self {
            near_radius: near,
            far_radius: far,
            msaa_samples: msaa,
            anisotropic_level: aniso,
            alpha_to_coverage: a2c,
            max_completions_per_frame: completions,
        }
    }

    /// The settings with each startup override that is set in place of
    /// the preset's value.
    #[must_use]
    pub fn with_overrides(self, overrides: &QualityOverrides) -> Self {
        Self {
            near_radius: overrides.near_radius.unwrap_or(self.near_radius),
            far_radius: overrides.far_radius.unwrap_or(self.far_radius),
            msaa_samples: overrides.msaa_samples.unwrap_or(self.msaa_samples),
            anisotropic_level: overrides
                .anisotropic_level
                .unwrap_or(self.anisotropic_level),
            alpha_to_coverage: overrides
                .alpha_to_coverage
                .unwrap_or(self.alpha_to_coverage),
            max_completions_per_frame: overrides
                .max_completions_per_frame
                .unwrap_or(self.max_completions_per_frame),
        }
    }

    /// The landscape draw distance (3 to 25 landblocks) is the far radius,
    /// and the near radius is no larger; any other stored value keeps the
    /// preset's window.
    #[must_use]
    pub fn with_landscape_draw_distance(self, distance: i32) -> Self {
        match u32::try_from(distance) {
            Ok(d @ 3..=25) => Self {
                near_radius: self.near_radius.min(d),
                far_radius: d,
                ..self
            },
            _ => self,
        }
    }

    /// With the world not drawn the streaming window shrinks to the 3 x 3
    /// landblocks around the player: enough for collision and the objects
    /// the server talks about.
    #[must_use]
    pub fn with_ui_only(self, ui_only: bool) -> Self {
        if ui_only {
            Self {
                near_radius: self.near_radius.min(UI_ONLY_STREAMING_RADIUS),
                far_radius: UI_ONLY_STREAMING_RADIUS,
                ..self
            }
        } else {
            self
        }
    }

    /// The near radius, and the far radius never below it.
    #[must_use]
    pub fn radii(&self) -> (u32, u32) {
        (self.near_radius, self.far_radius.max(self.near_radius))
    }
}

/// The streaming window with the world not drawn.
pub const UI_ONLY_STREAMING_RADIUS: u32 = 1;

/// Startup overrides of single quality values (each `None` keeps the
/// preset's).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct QualityOverrides {
    /// `--landblock-radius` / `OPENAC_LANDBLOCK_RADIUS`.
    pub near_radius: Option<u32>,
    /// `OPENAC_FAR_RADIUS`.
    pub far_radius: Option<u32>,
    /// `OPENAC_MSAA_SAMPLES`.
    pub msaa_samples: Option<u32>,
    /// `OPENAC_ANISOTROPIC`.
    pub anisotropic_level: Option<u16>,
    /// `OPENAC_A2C`.
    pub alpha_to_coverage: Option<bool>,
    /// `OPENAC_MAX_COMPLETIONS_PER_FRAME`.
    pub max_completions_per_frame: Option<u32>,
}

impl QualityOverrides {
    /// Read the quality variables through `env` (the radii are read by the
    /// host's option parser): an unset, empty or unreadable value keeps
    /// the preset's. `OPENAC_A2C` is off for `0` or `false` (any case) and
    /// on for anything else.
    #[must_use]
    pub fn from_vars(env: impl Fn(&str) -> Option<String>) -> Self {
        let int = |name: &str| {
            env(name)
                .map(|v| v.trim().to_owned())
                .filter(|v| !v.is_empty())
                .and_then(|v| v.parse::<i64>().ok())
        };
        let a2c = env("OPENAC_A2C")
            .filter(|v| !v.is_empty())
            .map(|v| !matches!(v.as_str(), "0") && !v.eq_ignore_ascii_case("false"));
        Self {
            near_radius: None,
            far_radius: None,
            msaa_samples: int("OPENAC_MSAA_SAMPLES").and_then(|v| u32::try_from(v).ok()),
            anisotropic_level: int("OPENAC_ANISOTROPIC").and_then(|v| u16::try_from(v).ok()),
            alpha_to_coverage: a2c,
            max_completions_per_frame: int("OPENAC_MAX_COMPLETIONS_PER_FRAME")
                .and_then(|v| u32::try_from(v).ok())
                .filter(|v| *v > 0),
        }
    }
}

/// The display settings the window runs with: Potato Mode forces every
/// quality choice to its cheapest (the stored choices stay as they are, so
/// turning it off brings them back); otherwise the settings as stored.
#[must_use]
pub fn effective_display(display: &DisplaySettings) -> DisplaySettings {
    if !display.potato_mode {
        return display.clone();
    }
    DisplaySettings {
        quality: QualityPreset::Potato,
        particle_range: ParticleRange::Retail,
        automatic_degrades: false,
        landscape_texture_detail: 4,
        environment_texture_detail: 4,
        texture_filtering: 0,
        landscape_draw_distance: 3,
        building_detail_textures: false,
        landscape_detail_textures: false,
        multi_pass_alpha: false,
        render_pack: None,
        ..display.clone()
    }
}

/// How many times its authored degrade distance an emitter is drawn out to
/// under the particle range setting: once for "Retail", twice for
/// "Extended".
#[must_use]
pub fn particle_range_multiplier(range: ParticleRange) -> f32 {
    match range {
        ParticleRange::Retail => 1.0,
        ParticleRange::Extended => ac_vfx::EXTENDED_RANGE_MULTIPLIER,
    }
}

/// [`effective_display`] for a window with or without the keyboard focus:
/// with "UI Only in Background" on, a window in the background runs
/// interface-only (the stored switch is untouched).
#[must_use]
pub fn effective_display_for_focus(display: &DisplaySettings, focused: bool) -> DisplaySettings {
    let mut effective = effective_display(display);
    effective.ui_only = ui_only_for_focus(display, focused);
    effective
}

/// Whether the world pass is off: the stored "UI Only", or "UI Only in
/// Background" while the window has no focus.
#[must_use]
pub fn ui_only_for_focus(display: &DisplaySettings, focused: bool) -> bool {
    display.ui_only || (!focused && display.ui_only_when_unfocused)
}

/// The quality the window runs with for `display` (already effective):
/// the preset, narrowed by the landscape draw distance, the startup
/// overrides, then interface-only.
#[must_use]
pub fn resolve(display: &DisplaySettings, overrides: &QualityOverrides) -> QualitySettings {
    QualitySettings::from_preset(display.quality)
        .with_landscape_draw_distance(display.landscape_draw_distance)
        .with_overrides(overrides)
        .with_ui_only(display.ui_only)
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.UI.Abstractions.Tests/Panels/Settings/QualityPresetTests.cs`
    //! and the quality cases of `tests/AcDream.App.Tests/Settings/RuntimeSettingsControllerTests.cs`.

    use super::*;

    #[test]
    fn each_preset_sets_its_radii_msaa_filtering_and_completions() {
        use QualityPreset::*;
        for (preset, near, far, msaa, aniso, a2c, completions) in [
            (Potato, 1, 3, 0, 1, false, 2),
            (Low, 2, 5, 0, 4, false, 2),
            (Medium, 3, 8, 2, 8, false, 3),
            (High, 4, 12, 4, 16, true, 4),
            (Ultra, 5, 15, 4, 16, true, 6),
        ] {
            let s = QualitySettings::from_preset(preset);
            assert_eq!(
                (
                    s.near_radius,
                    s.far_radius,
                    s.msaa_samples,
                    s.anisotropic_level,
                    s.alpha_to_coverage,
                    s.max_completions_per_frame
                ),
                (near, far, msaa, aniso, a2c, completions),
                "{preset:?}"
            );
        }
    }

    fn vars(pairs: &'static [(&'static str, &'static str)]) -> impl Fn(&str) -> Option<String> {
        move |name| {
            pairs
                .iter()
                .find(|(k, _)| *k == name)
                .map(|(_, v)| (*v).to_owned())
        }
    }

    #[test]
    fn a_startup_value_overrides_only_its_own_field() {
        let high = QualitySettings::from_preset(QualityPreset::High);
        let near = high.with_overrides(&QualityOverrides {
            near_radius: Some(2),
            ..QualityOverrides::default()
        });
        assert_eq!((near.near_radius, near.far_radius), (2, 12));
        let far = high.with_overrides(&QualityOverrides {
            far_radius: Some(20),
            ..QualityOverrides::default()
        });
        assert_eq!((far.near_radius, far.far_radius), (4, 20));
        let o = QualityOverrides::from_vars(vars(&[
            ("OPENAC_MSAA_SAMPLES", "8"),
            ("OPENAC_ANISOTROPIC", "4"),
            ("OPENAC_MAX_COMPLETIONS_PER_FRAME", "8"),
        ]));
        let resolved = high.with_overrides(&o);
        assert_eq!(resolved.msaa_samples, 8);
        assert_eq!(resolved.anisotropic_level, 4);
        assert_eq!(resolved.max_completions_per_frame, 8);
        assert_eq!(
            high.with_overrides(&QualityOverrides::from_vars(vars(&[]))),
            high,
            "nothing set keeps the preset"
        );
    }

    #[test]
    fn alpha_to_coverage_reads_zero_and_false_as_off() {
        let high = QualitySettings::from_preset(QualityPreset::High);
        let low = QualitySettings::from_preset(QualityPreset::Low);
        for off in ["0", "false"] {
            let o =
                QualityOverrides::from_vars(move |n| (n == "OPENAC_A2C").then(|| off.to_owned()));
            assert!(!high.with_overrides(&o).alpha_to_coverage, "{off}");
        }
        let on = QualityOverrides::from_vars(vars(&[("OPENAC_A2C", "1")]));
        assert!(low.with_overrides(&on).alpha_to_coverage);
    }

    #[test]
    fn the_landscape_draw_distance_is_the_far_radius() {
        let high = QualitySettings::from_preset(QualityPreset::High);
        for (value, near, far) in [(3, 3, 3), (5, 4, 5), (8, 4, 8), (12, 4, 12), (25, 4, 25)] {
            let r = high.with_landscape_draw_distance(value);
            assert_eq!((r.near_radius, r.far_radius), (near, far), "{value}");
        }
        for invalid in [0, 2, 26] {
            assert_eq!(high.with_landscape_draw_distance(invalid), high);
        }
    }

    #[test]
    fn interface_only_shrinks_the_window_to_the_neighbours() {
        let ultra = QualitySettings::from_preset(QualityPreset::Ultra);
        let ui = ultra.with_ui_only(true);
        assert_eq!((ui.near_radius, ui.far_radius), (1, 1));
        assert_eq!(ui.msaa_samples, ultra.msaa_samples);
        assert_eq!(ultra.with_ui_only(false), ultra);
        let potato = QualitySettings::from_preset(QualityPreset::Potato).with_ui_only(true);
        assert_eq!((potato.near_radius, potato.far_radius), (1, 1));
    }

    #[test]
    fn background_ui_only_follows_the_focus_only_while_the_option_is_on() {
        // OpenAC BackgroundUiOnly_FollowsWindowFocus_OnlyWhileTheOptionIsOn.
        let off = DisplaySettings::default();
        assert!(!effective_display_for_focus(&off, false).ui_only);
        let on = DisplaySettings {
            ui_only_when_unfocused: true,
            ..DisplaySettings::default()
        };
        assert!(!effective_display_for_focus(&on, true).ui_only);
        let background = effective_display_for_focus(&on, false);
        assert!(background.ui_only);
        assert!(!on.ui_only, "the stored switch is untouched");
        let q = resolve(&background, &QualityOverrides::default());
        assert_eq!(q.far_radius, 1);
        // A stored UI Only stays on regardless of the focus.
        let stored = DisplaySettings {
            ui_only: true,
            ..on
        };
        assert!(effective_display_for_focus(&stored, true).ui_only);
    }

    #[test]
    fn potato_mode_runs_the_cheapest_while_the_stored_choices_stay() {
        let stored = DisplaySettings {
            potato_mode: true,
            quality: QualityPreset::Ultra,
            landscape_draw_distance: 25,
            particle_range: ParticleRange::Extended,
            ..DisplaySettings::default()
        };
        let effective = effective_display(&stored);
        let q = resolve(&effective, &QualityOverrides::default());
        assert_eq!((q.near_radius, q.far_radius, q.msaa_samples), (1, 3, 0));
        assert_eq!(effective.quality, QualityPreset::Potato);
        assert_eq!(effective.particle_range, ParticleRange::Retail);
        assert!(!effective.building_detail_textures);
        assert!(!effective.landscape_detail_textures);
        assert_eq!(stored.quality, QualityPreset::Ultra, "stored untouched");
        let off = DisplaySettings {
            potato_mode: false,
            ..stored
        };
        assert_eq!(effective_display(&off), off);
    }

    #[test]
    fn the_default_settings_run_high_at_the_default_draw_distance() {
        // OpenAC: the default landscape draw distance (8) is the far radius.
        let q = resolve(
            &effective_display(&DisplaySettings::default()),
            &QualityOverrides::default(),
        );
        assert_eq!(q.radii(), (4, 8));
        assert_eq!(q.msaa_samples, 4);
    }
}
