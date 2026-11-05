//! The server's environment-change cues that are sounds (thunder and the
//! other interface stingers).

use crate::sound_id::SoundId;

/// The environment-change codes that play a sound, and the sound. An
/// explicit table: `0x73` and `0x74` have no sound, so the codes are not a
/// constant offset from the sounds.
pub const ENVIRON_SOUND_CUES: [(u32, SoundId); 21] = [
    (0x65, SoundId::UiRoar),
    (0x66, SoundId::UiBell),
    (0x67, SoundId::UiChant1),
    (0x68, SoundId::UiChant2),
    (0x69, SoundId::UiDarkWhispers1),
    (0x6A, SoundId::UiDarkWhispers2),
    (0x6B, SoundId::UiDarkLaugh),
    (0x6C, SoundId::UiDarkWind),
    (0x6D, SoundId::UiDarkSpeech),
    (0x6E, SoundId::UiDrums),
    (0x6F, SoundId::UiGhostSpeak),
    (0x70, SoundId::UiBreathing),
    (0x71, SoundId::UiHowl),
    (0x72, SoundId::UiLostSouls),
    (0x75, SoundId::UiSqueal),
    (0x76, SoundId::UiThunder1),
    (0x77, SoundId::UiThunder2),
    (0x78, SoundId::UiThunder3),
    (0x79, SoundId::UiThunder4),
    (0x7A, SoundId::UiThunder5),
    (0x7B, SoundId::UiThunder6),
];

/// The sound an environment-change code plays, if it is a sound cue.
#[must_use]
pub fn environ_sound(change_type: u32) -> Option<SoundId> {
    ENVIRON_SOUND_CUES
        .iter()
        .find(|(code, _)| *code == change_type)
        .map(|(_, sound)| *sound)
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Audio/EnvironSoundCueMapTests.cs`.

    use super::*;

    #[test]
    fn every_retail_case_maps_to_its_sound() {
        let names = [
            (0x65, "UI_Roar"),
            (0x66, "UI_Bell"),
            (0x67, "UI_Chant1"),
            (0x68, "UI_Chant2"),
            (0x69, "UI_DarkWhispers1"),
            (0x6A, "UI_DarkWhispers2"),
            (0x6B, "UI_DarkLaugh"),
            (0x6C, "UI_DarkWind"),
            (0x6D, "UI_DarkSpeech"),
            (0x6E, "UI_Drums"),
            (0x6F, "UI_GhostSpeak"),
            (0x70, "UI_Breathing"),
            (0x71, "UI_Howl"),
            (0x72, "UI_LostSouls"),
            (0x75, "UI_Squeal"),
            (0x76, "UI_Thunder1"),
            (0x77, "UI_Thunder2"),
            (0x78, "UI_Thunder3"),
            (0x79, "UI_Thunder4"),
            (0x7A, "UI_Thunder5"),
            (0x7B, "UI_Thunder6"),
        ];
        for (code, name) in names {
            assert_eq!(
                environ_sound(code).map(SoundId::name),
                Some(name),
                "{code:#x}"
            );
        }
    }

    #[test]
    fn gap_codes_have_no_case() {
        assert_eq!(environ_sound(0x73), None);
        assert_eq!(environ_sound(0x74), None);
    }

    #[test]
    fn codes_outside_the_run_have_no_case() {
        for code in [0, 1, 6, 0x64, 0x7C, 0xFF] {
            assert_eq!(environ_sound(code), None);
        }
    }

    #[test]
    fn the_table_is_not_a_constant_offset() {
        assert_eq!(environ_sound(0x72).unwrap().value() - 0x72, 0x11);
        assert_eq!(environ_sound(0x75).unwrap().value() - 0x75, 0x0F);
    }

    #[test]
    fn covers_exactly_the_twenty_one_interface_stingers() {
        let mut sounds: Vec<u32> = ENVIRON_SOUND_CUES.iter().map(|(_, s)| s.value()).collect();
        sounds.sort_unstable();
        assert_eq!(sounds, (0x76..0x76 + 21).collect::<Vec<u32>>());
    }
}
