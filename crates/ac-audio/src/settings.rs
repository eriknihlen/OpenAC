//! The player's sound settings and the startup switches.

/// The sound settings the options panel edits. Retail has three volume
/// knobs (effects, ambient, interface) and three disable switches; OpenAC
/// adds a master volume on top.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AudioSettings {
    /// Master volume, 0..1, multiplied into every sound.
    pub master: f32,
    /// Effects volume (world sounds and interface sounds).
    pub sfx: f32,
    /// Ambient volume.
    pub ambient: f32,
    /// The game's "sound features" preference (stored, not acted on).
    pub sound_features: i32,
    /// Effects on.
    pub sfx_enabled: bool,
    /// Ambient sounds on.
    pub ambient_enabled: bool,
    /// Interface sounds on.
    pub interface_enabled: bool,
    /// Interface volume (stored; interface sounds follow the effects volume,
    /// as the game's do).
    pub interface_volume: f32,
    /// Play sound only while the window has focus.
    pub play_sound_only_when_active: bool,
}

impl Default for AudioSettings {
    fn default() -> Self {
        Self {
            master: 1.0,
            sfx: 1.0,
            ambient: 1.0,
            sound_features: 0,
            sfx_enabled: true,
            ambient_enabled: true,
            interface_enabled: true,
            interface_volume: 1.0,
            play_sound_only_when_active: true,
        }
    }
}

impl AudioSettings {
    /// The effects and ambient volumes in force: a disabled category is 0.
    #[must_use]
    pub fn effective_category_volumes(&self) -> (f32, f32) {
        (
            if self.sfx_enabled { self.sfx } else { 0.0 },
            if self.ambient_enabled {
                self.ambient
            } else {
                0.0
            },
        )
    }

    /// Whether sound should be refused for a window with this focus.
    #[must_use]
    pub fn focus_muted(&self, window_focused: bool) -> bool {
        !window_focused && self.play_sound_only_when_active
    }
}

/// Startup switches for audio, read from the environment (see
/// `docs/launch-options.md`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct AudioLaunchOptions {
    /// `OPENAC_NO_AUDIO=1`: create no audio engine at all.
    pub no_audio: bool,
    /// `OPENAC_PROBE_AUDIO_VOICES=1`: one line on standard error when a
    /// sound is dropped or takes a playing voice, listing every voice.
    pub probe_voices: bool,
    /// `OPENAC_PROBE_SOUND_WIRE=1`: one line on standard error per server
    /// sound event, saying what it resolved to.
    pub probe_wire_sounds: bool,
    /// `OPENAC_PROBE_AUDIO_PLAYS=1`: one line on standard error per sound
    /// that starts, with its voice, gain and pan and the mixer's state.
    pub probe_plays: bool,
}

impl AudioLaunchOptions {
    /// Read the switches through `var` (the process environment, or a test
    /// map). Each is on only when exactly `1`.
    pub fn from_vars(var: impl Fn(&str) -> Option<String>) -> Self {
        let one = |name: &str| var(name).as_deref() == Some("1");
        Self {
            no_audio: one("OPENAC_NO_AUDIO"),
            probe_voices: one("OPENAC_PROBE_AUDIO_VOICES"),
            probe_wire_sounds: one("OPENAC_PROBE_SOUND_WIRE"),
            probe_plays: one("OPENAC_PROBE_AUDIO_PLAYS"),
        }
    }

    /// Read the switches from the process environment.
    #[must_use]
    pub fn from_env() -> Self {
        Self::from_vars(|name| std::env::var(name).ok())
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.UI.Abstractions.Tests/Panels/Settings/AudioSettingsTests.cs`,
    //! plus `RuntimeSettingsTargets.ComputeEffectiveCategoryVolumes` and
    //! `RuntimeOptions` (`ACDREAM_NO_AUDIO`, renamed `OPENAC_NO_AUDIO`).

    use super::*;

    #[test]
    fn default_values_match_engine_constructor_defaults() {
        let d = AudioSettings::default();
        assert_eq!((d.master, d.sfx, d.ambient), (1.0, 1.0, 1.0));
    }

    #[test]
    fn equality_is_value_based() {
        let a = AudioSettings::default();
        let b = AudioSettings {
            master: 0.5,
            ..AudioSettings::default()
        };
        let c = AudioSettings {
            master: 0.5,
            ..AudioSettings::default()
        };
        assert_ne!(a, b);
        assert_eq!(b, c);
    }

    #[test]
    fn a_disabled_category_plays_at_zero() {
        let s = AudioSettings {
            sfx: 0.7,
            ambient: 0.4,
            ambient_enabled: false,
            ..AudioSettings::default()
        };
        assert_eq!(s.effective_category_volumes(), (0.7, 0.0));
        let s = AudioSettings {
            sfx_enabled: false,
            ..s
        };
        assert_eq!(s.effective_category_volumes(), (0.0, 0.0));
    }

    #[test]
    fn focus_mute_follows_the_preference() {
        let s = AudioSettings::default();
        assert!(s.focus_muted(false));
        assert!(!s.focus_muted(true));
        let s = AudioSettings {
            play_sound_only_when_active: false,
            ..s
        };
        assert!(!s.focus_muted(false));
    }

    #[test]
    fn switches_are_on_only_when_exactly_one() {
        let o = AudioLaunchOptions::from_vars(|n| match n {
            "OPENAC_NO_AUDIO" => Some("1".into()),
            "OPENAC_PROBE_AUDIO_VOICES" => Some("true".into()),
            _ => None,
        });
        assert!(o.no_audio);
        assert!(!o.probe_voices);
        assert!(!o.probe_wire_sounds);
        assert!(!o.probe_plays);
        let plays = AudioLaunchOptions::from_vars(|n| {
            (n == "OPENAC_PROBE_AUDIO_PLAYS").then(|| "1".to_owned())
        });
        assert!(plays.probe_plays);
        assert_eq!(
            AudioLaunchOptions::from_vars(|_| None),
            AudioLaunchOptions::default()
        );
    }
}
