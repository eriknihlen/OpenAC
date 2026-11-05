//! Mixer settings and the `/mixer` command that reads and changes them.

/// How many sounds the mixer can hold at once, and what it does when they
/// are all held. Client settings, never on the wire.
///
/// The game mixes through sixteen voices, records no priority against any of
/// them, and so drops every new sound while all sixteen are busy. OpenAC's
/// defaults: more voices, the authored priority the sound data carries, and
/// a cap on how many voices one sound may hold. [`Self::retail_mixer`]
/// restores the game's behaviour exactly.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AudioMixerOptions {
    /// Mix exactly as the game does: sixteen voices, no authored priority,
    /// no per-sound cap. Overrides the three settings below without
    /// rewriting them.
    pub retail_mixer: bool,
    /// How many sounds may be audible at once.
    pub voice_count: i32,
    /// Whether the authored priority is recorded against a voice, letting an
    /// important sound take a voice from a less important one still playing.
    pub use_authored_priority: bool,
    /// How many voices one sound may hold, from [`NO_PER_WAVE_CAP`] (no
    /// limit) to [`MAXIMUM_MAX_VOICES_PER_WAVE`].
    pub max_voices_per_wave: i32,
}

/// The fewest voices the mixer may be given.
pub const MINIMUM_VOICE_COUNT: i32 = 16;
/// The most voices the mixer may be given.
pub const MAXIMUM_VOICE_COUNT: i32 = 64;
/// The voice count the retail mixer pins the pool to.
pub const RETAIL_MIXER_VOICE_COUNT: i32 = 16;
/// The value of `max_voices_per_wave` that means "no cap".
pub const NO_PER_WAVE_CAP: i32 = 0;
/// The most voices one sound may hold.
pub const MAXIMUM_MAX_VOICES_PER_WAVE: i32 = 8;
/// Default voice count.
pub const DEFAULT_VOICE_COUNT: i32 = 32;
/// Default per-sound cap.
pub const DEFAULT_MAX_VOICES_PER_WAVE: i32 = 4;

impl Default for AudioMixerOptions {
    fn default() -> Self {
        Self {
            retail_mixer: false,
            voice_count: DEFAULT_VOICE_COUNT,
            use_authored_priority: true,
            max_voices_per_wave: DEFAULT_MAX_VOICES_PER_WAVE,
        }
    }
}

impl AudioMixerOptions {
    /// The voice count in force.
    #[must_use]
    pub fn effective_voice_count(&self) -> i32 {
        if self.retail_mixer {
            RETAIL_MIXER_VOICE_COUNT
        } else {
            clamp_voice_count(self.voice_count)
        }
    }

    /// Whether authored priority is in force.
    #[must_use]
    pub fn effective_use_authored_priority(&self) -> bool {
        !self.retail_mixer && self.use_authored_priority
    }

    /// The per-sound cap in force.
    #[must_use]
    pub fn effective_max_voices_per_wave(&self) -> i32 {
        if self.retail_mixer {
            NO_PER_WAVE_CAP
        } else {
            clamp_max_voices_per_wave(self.max_voices_per_wave)
        }
    }

    /// The same settings with every value inside its range.
    #[must_use]
    pub fn normalized(self) -> Self {
        Self {
            voice_count: clamp_voice_count(self.voice_count),
            max_voices_per_wave: clamp_max_voices_per_wave(self.max_voices_per_wave),
            ..self
        }
    }
}

/// A voice count inside its range.
#[must_use]
pub fn clamp_voice_count(voice_count: i32) -> i32 {
    voice_count.clamp(MINIMUM_VOICE_COUNT, MAXIMUM_VOICE_COUNT)
}

/// A per-sound cap inside its range.
#[must_use]
pub fn clamp_max_voices_per_wave(max_voices_per_wave: i32) -> i32 {
    max_voices_per_wave.clamp(NO_PER_WAVE_CAP, MAXIMUM_MAX_VOICES_PER_WAVE)
}

/// The command's verb.
pub const MIXER_VERB: &str = "mixer";

/// The usage line.
pub const MIXER_USAGE: &str = "/mixer | /mixer retail on|off | /mixer voices 16-64 | /mixer priority on|off | /mixer perwave 0-8";

/// Said when the settings could not be written down.
pub const MIXER_SAVE_FAILED: &str = "mixer: the settings could not be saved, so nothing changed.";

/// Said when the settings were written down but no mixer is running.
pub const MIXER_NOT_RUNNING: &str =
    "mixer: saved, but there is no mixer running to change - it will start this way next time.";

/// The arguments of a typed `/mixer` (or `@mixer`) line, trimmed; `None`
/// for any other line. The verb ignores case, as a client command's does.
#[must_use]
pub fn mixer_command_arguments(text: &str) -> Option<&str> {
    let trimmed = text.trim();
    let rest = trimmed
        .strip_prefix('/')
        .or_else(|| trimmed.strip_prefix('@'))?;
    let (verb, arguments) = match rest.find([' ', '\t']) {
        Some(at) => (&rest[..at], rest[at + 1..].trim()),
        None => (rest, ""),
    };
    verb.eq_ignore_ascii_case(MIXER_VERB).then_some(arguments)
}

/// What `/mixer` asks for.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MixerCommandResult {
    /// The settings the command asks for.
    pub options: AudioMixerOptions,
    /// Whether they differ from the ones it was given.
    pub changed: bool,
    /// Lines to show the player.
    pub lines: Vec<String>,
}

/// The `/mixer` command: text in, settings and text out. Applying and saving
/// them is the caller's job (see [`run_mixer_command`]).
#[must_use]
pub fn execute_mixer_command(
    current: &AudioMixerOptions,
    arguments: Option<&str>,
) -> MixerCommandResult {
    let before = current.normalized();
    let words: Vec<&str> = arguments.unwrap_or("").split_whitespace().collect();
    let unchanged = |line: String| MixerCommandResult {
        options: before,
        changed: false,
        lines: vec![line],
    };
    let Some(first) = words.first() else {
        return unchanged(describe_mixer(&before));
    };
    let asked = match first.to_lowercase().as_str() {
        "retail" => read_switch(&words).map(|v| AudioMixerOptions {
            retail_mixer: v,
            ..before
        }),
        "voices" => read_count(&words).map(|v| AudioMixerOptions {
            voice_count: clamp_voice_count(v),
            ..before
        }),
        "priority" => read_switch(&words).map(|v| AudioMixerOptions {
            use_authored_priority: v,
            ..before
        }),
        "perwave" => read_count(&words).map(|v| AudioMixerOptions {
            max_voices_per_wave: clamp_max_voices_per_wave(v),
            ..before
        }),
        _ => None,
    };
    let Some(asked) = asked else {
        return unchanged(MIXER_USAGE.to_owned());
    };
    let after = asked.normalized();
    MixerCommandResult {
        options: after,
        changed: after != before,
        lines: vec![describe_mixer(&after)],
    }
}

/// One line saying what the mixer is doing.
#[must_use]
pub fn describe_mixer(options: &AudioMixerOptions) -> String {
    let on_off = |v: bool| if v { "on" } else { "off" };
    let cap = match options.effective_max_voices_per_wave() {
        n if n <= NO_PER_WAVE_CAP => "no cap per sound".to_owned(),
        n => format!("at most {n} voices per sound"),
    };
    format!(
        "mixer: retail mixer {}, {} voices, authored priority {}, {cap}",
        on_off(options.retail_mixer),
        options.effective_voice_count(),
        on_off(options.effective_use_authored_priority()),
    )
}

fn read_switch(words: &[&str]) -> Option<bool> {
    if words.len() != 2 {
        return None;
    }
    match words[1].to_lowercase().as_str() {
        "on" | "1" | "true" => Some(true),
        "off" | "0" | "false" => Some(false),
        _ => None,
    }
}

/// Digits only (no sign, no spaces), fitting an `i32`.
fn read_count(words: &[&str]) -> Option<i32> {
    if words.len() != 2 || words[1].is_empty() || !words[1].bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    words[1].parse().ok()
}

/// Whether a mixer change was written down and whether a mixer was running
/// to take it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MixerChange {
    /// False: nothing changed at all.
    pub saved: bool,
    /// False: saved, but it takes effect on the next start.
    pub mixer_running: bool,
}

/// The one way a mixer setting changes: written down first, then applied to
/// the running mixer. A failed save leaves both alone.
pub fn change_mixer(
    options: &AudioMixerOptions,
    persist: impl FnOnce(&AudioMixerOptions) -> bool,
    apply_to_running_mixer: impl FnOnce(&AudioMixerOptions) -> bool,
) -> MixerChange {
    if !persist(options) {
        return MixerChange {
            saved: false,
            mixer_running: false,
        };
    }
    MixerChange {
        saved: true,
        mixer_running: apply_to_running_mixer(options),
    }
}

/// Run `/mixer <arguments>` against the remembered settings: report, or
/// change (save, then apply), and say what happened through `say`.
pub fn run_mixer_command(
    current: &AudioMixerOptions,
    arguments: Option<&str>,
    persist: impl FnOnce(&AudioMixerOptions) -> bool,
    apply_to_running_mixer: impl FnOnce(&AudioMixerOptions) -> bool,
    mut say: impl FnMut(&str),
) {
    let result = execute_mixer_command(current, arguments);
    let mut running = true;
    if result.changed {
        let change = change_mixer(&result.options, persist, apply_to_running_mixer);
        if !change.saved {
            say(MIXER_SAVE_FAILED);
            return;
        }
        running = change.mixer_running;
    }
    for line in &result.lines {
        say(line);
    }
    if !running {
        say(MIXER_NOT_RUNNING);
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Audio/AudioMixerCommandTests.cs`
    //! and the device-free cases of
    //! `tests/AcDream.App.Tests/Audio/AudioMixerCommandBindingTests.cs`.

    use super::*;

    fn run(arguments: &str) -> MixerCommandResult {
        execute_mixer_command(&AudioMixerOptions::default(), Some(arguments))
    }

    #[test]
    fn the_defaults_are_the_enhanced_mixer() {
        let d = AudioMixerOptions::default();
        assert!(!d.retail_mixer);
        assert_eq!(d.effective_voice_count(), 32);
        assert!(d.effective_use_authored_priority());
        assert_eq!(d.effective_max_voices_per_wave(), 4);
    }

    #[test]
    fn with_no_arguments_it_reports_and_changes_nothing() {
        let r = execute_mixer_command(&AudioMixerOptions::default(), None);
        assert!(!r.changed);
        assert_eq!(
            r.lines,
            [
                "mixer: retail mixer off, 32 voices, authored priority on, at most 4 voices per sound"
            ]
        );
    }

    #[test]
    fn retail_on_pins_sixteen_voices_with_no_priority_and_no_cap() {
        let r = run("retail on");
        assert!(r.changed);
        assert!(r.options.retail_mixer);
        assert_eq!(r.options.effective_voice_count(), 16);
        assert!(!r.options.effective_use_authored_priority());
        assert_eq!(r.options.effective_max_voices_per_wave(), NO_PER_WAVE_CAP);
        assert_eq!(
            r.lines,
            ["mixer: retail mixer on, 16 voices, authored priority off, no cap per sound"]
        );
    }

    // OpenAC PluginCommandRegistry.TryHandle: '/' or '@', the verb up to the
    // first space or tab ignoring case, the rest trimmed.
    #[test]
    fn a_typed_line_is_the_mixer_command_only_by_its_verb() {
        assert_eq!(mixer_command_arguments("/mixer"), Some(""));
        assert_eq!(
            mixer_command_arguments("  @MIXER   retail on "),
            Some("retail on")
        );
        assert_eq!(
            mixer_command_arguments("/Mixer\tvoices 48"),
            Some("voices 48")
        );
        assert_eq!(mixer_command_arguments("/mixers"), None);
        assert_eq!(mixer_command_arguments("mixer retail on"), None);
        assert_eq!(mixer_command_arguments("/mute"), None);
        assert_eq!(mixer_command_arguments("/"), None);
    }

    #[test]
    fn retail_off_gives_back_the_settings_it_overrode() {
        let chosen = AudioMixerOptions {
            retail_mixer: true,
            voice_count: 48,
            use_authored_priority: true,
            max_voices_per_wave: 6,
        };
        let r = execute_mixer_command(&chosen, Some("retail off"));
        assert!(r.changed);
        assert_eq!(r.options.effective_voice_count(), 48);
        assert!(r.options.effective_use_authored_priority());
        assert_eq!(r.options.effective_max_voices_per_wave(), 6);
    }

    #[test]
    fn voices_sets_the_count_inside_its_range() {
        for (args, expected) in [
            ("voices 48", 48),
            ("voices 16", 16),
            ("voices 64", 64),
            ("voices 8", MINIMUM_VOICE_COUNT),
            ("voices 4096", MAXIMUM_VOICE_COUNT),
            ("VOICES 40", 40),
        ] {
            assert_eq!(
                run(args).options.effective_voice_count(),
                expected,
                "{args}"
            );
        }
    }

    #[test]
    fn priority_off_stops_an_important_sound_from_taking_a_voice() {
        let r = run("priority off");
        assert!(r.changed);
        assert!(!r.options.effective_use_authored_priority());
        assert_eq!(
            r.lines,
            [
                "mixer: retail mixer off, 32 voices, authored priority off, at most 4 voices per sound"
            ]
        );
    }

    #[test]
    fn per_wave_sets_the_cap_inside_its_range() {
        for (args, expected) in [
            ("perwave 0", NO_PER_WAVE_CAP),
            ("perwave 8", 8),
            ("perwave 20", MAXIMUM_MAX_VOICES_PER_WAVE),
            ("perwave 999", MAXIMUM_MAX_VOICES_PER_WAVE),
        ] {
            assert_eq!(
                run(args).options.effective_max_voices_per_wave(),
                expected,
                "{args}"
            );
        }
    }

    #[test]
    fn per_wave_zero_is_reported_as_no_cap() {
        assert_eq!(
            run("perwave 0").lines,
            ["mixer: retail mixer off, 32 voices, authored priority on, no cap per sound"]
        );
    }

    #[test]
    fn nonsense_changes_nothing_and_says_how_to_use_it() {
        for args in [
            "wibble",
            "voices",
            "voices lots",
            "voices 32 48",
            "retail",
            "retail maybe",
            "priority -1",
            "perwave four",
            "voices +40",
        ] {
            let r = run(args);
            assert!(!r.changed, "{args}");
            assert_eq!(r.options, AudioMixerOptions::default().normalized());
            assert_eq!(r.lines, [MIXER_USAGE], "{args}");
        }
    }

    #[test]
    fn setting_a_value_to_what_it_already_is_is_not_a_change() {
        assert!(!run("voices 32").changed);
    }

    #[test]
    fn on_and_off_also_answer_to_true_and_false() {
        for (args, expected) in [
            ("retail 1", true),
            ("retail true", true),
            ("retail 0", false),
            ("retail false", false),
        ] {
            assert_eq!(run(args).options.retail_mixer, expected, "{args}");
        }
    }

    #[test]
    fn hand_edited_values_are_brought_inside_their_ranges() {
        let n = AudioMixerOptions {
            voice_count: 4096,
            max_voices_per_wave: -3,
            ..AudioMixerOptions::default()
        }
        .normalized();
        assert_eq!(n.voice_count, MAXIMUM_VOICE_COUNT);
        assert_eq!(n.max_voices_per_wave, NO_PER_WAVE_CAP);
        let n = AudioMixerOptions {
            voice_count: 16,
            max_voices_per_wave: 40,
            ..AudioMixerOptions::default()
        }
        .normalized();
        assert_eq!(n.max_voices_per_wave, MAXIMUM_MAX_VOICES_PER_WAVE);
    }

    fn say_all(
        args: &str,
        saves: bool,
        running: bool,
    ) -> (Vec<String>, Vec<AudioMixerOptions>, Vec<AudioMixerOptions>) {
        let mut said = Vec::new();
        let mut saved = Vec::new();
        let mut applied = Vec::new();
        run_mixer_command(
            &AudioMixerOptions::default(),
            Some(args),
            |o| {
                if saves {
                    saved.push(*o);
                }
                saves
            },
            |o| {
                applied.push(*o);
                running
            },
            |line| said.push(line.to_owned()),
        );
        (said, saved, applied)
    }

    #[test]
    fn reading_the_settings_saves_and_applies_nothing() {
        let (said, saved, applied) = say_all("", true, true);
        assert_eq!(said.len(), 1);
        assert!(saved.is_empty() && applied.is_empty());
    }

    #[test]
    fn a_change_is_saved_then_applied() {
        let (said, saved, applied) = say_all("voices 40", true, true);
        assert_eq!(saved.len(), 1);
        assert_eq!(saved[0].voice_count, 40);
        assert_eq!(applied.len(), 1);
        assert_eq!(said.len(), 1);
    }

    #[test]
    fn a_save_that_fails_leaves_the_running_mixer_alone_and_says_so() {
        let (said, saved, applied) = say_all("voices 40", false, true);
        assert_eq!(said, [MIXER_SAVE_FAILED]);
        assert!(saved.is_empty() && applied.is_empty());
    }

    #[test]
    fn with_no_mixer_running_the_settings_are_saved_and_the_reply_says_so() {
        let (said, saved, _) = say_all("voices 40", true, false);
        assert_eq!(saved[0].voice_count, 40);
        assert_eq!(
            said,
            [
                "mixer: retail mixer off, 40 voices, authored priority on, at most 4 voices per sound",
                MIXER_NOT_RUNNING
            ]
        );
    }

    #[test]
    fn a_setting_that_does_not_change_is_neither_applied_nor_saved() {
        let (_, saved, applied) = say_all("voices 32", true, true);
        assert!(saved.is_empty() && applied.is_empty());
    }

    #[test]
    fn nonsense_is_answered_with_the_usage_line() {
        let (said, saved, _) = say_all("wibble", true, true);
        assert_eq!(said, [MIXER_USAGE]);
        assert!(saved.is_empty());
    }
}
