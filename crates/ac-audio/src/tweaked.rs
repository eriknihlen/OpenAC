//! The "tweaked" sound hook: a wave with its own dice roll and volume.

use ac_dat::objects::HookData;

use crate::cookbook;
use crate::random::SoundRandom;

/// The fields of a tweaked sound hook, under the names the dat reader gives
/// them. The reader's `priority` and `probability` are the wrong way round
/// for what they mean: the first authored float (`priority` here) is the
/// play probability, the second (`probability` here) is the priority. Read
/// them through [`Self::play_probability`] and [`Self::authored_priority`].
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TweakedSoundHook {
    /// Wave id (`0x0A......`).
    pub sound_id: u32,
    /// First authored float.
    pub priority: f32,
    /// Second authored float.
    pub probability: f32,
    /// Volume, used as written (zero is silent).
    pub volume: f32,
}

impl TweakedSoundHook {
    /// The tweaked hook in `hook`, if it is one.
    #[must_use]
    pub fn from_hook(hook: &HookData) -> Option<Self> {
        match *hook {
            HookData::SoundTweaked {
                sound_id,
                priority,
                probability,
                volume,
            } => Some(Self {
                sound_id,
                priority,
                probability,
                volume,
            }),
            _ => None,
        }
    }

    /// The chance in `[0, 1]` that the hook makes a sound at all: the first
    /// authored float.
    #[must_use]
    pub fn play_probability(&self) -> f32 {
        self.priority
    }

    /// How important the sound is when the mixer must choose whose voice a
    /// new sound takes: the second authored float.
    #[must_use]
    pub fn authored_priority(&self) -> f32 {
        self.probability
    }

    /// Roll the hook's probability; when it wins, the wave and volume to
    /// play. A hook that loses is silent and is not retried.
    pub fn try_roll(&self, rng: &mut dyn SoundRandom) -> Option<(u32, f32)> {
        cookbook::play_probability(self.play_probability(), rng)
            .then_some((self.sound_id, self.volume))
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Audio/TweakedSoundHooksTests.cs`.

    use super::*;
    use crate::random::scripted::Scripted;

    fn hook(wave: u32, first: f32, second: f32, volume: f32) -> TweakedSoundHook {
        TweakedSoundHook::from_hook(&HookData::SoundTweaked {
            sound_id: wave,
            priority: first,
            probability: second,
            volume,
        })
        .unwrap()
    }

    fn roll(p: f32) -> Scripted {
        Scripted::new(&[], &[p])
    }

    #[test]
    fn a_hook_that_loses_its_roll_makes_no_sound() {
        assert_eq!(
            hook(0x0A00_038B, 0.1, 0.9, 1.0).try_roll(&mut roll(0.5)),
            None
        );
    }

    #[test]
    fn a_hook_that_wins_its_roll_plays_its_own_sound() {
        assert_eq!(
            hook(0x0A00_038B, 0.9, 0.1, 0.75).try_roll(&mut roll(0.5)),
            Some((0x0A00_038B, 0.75))
        );
    }

    #[test]
    fn the_play_probability_is_the_first_authored_float() {
        let likely = hook(0x0A00_0001, 0.9, 0.1, 1.0);
        let unlikely = hook(0x0A00_0001, 0.1, 0.9, 1.0);
        assert_eq!(likely.play_probability(), 0.9);
        assert_eq!(unlikely.play_probability(), 0.1);
        assert!(likely.try_roll(&mut roll(0.5)).is_some());
        assert!(unlikely.try_roll(&mut roll(0.5)).is_none());
    }

    #[test]
    fn the_authored_priority_is_the_second_authored_float() {
        let h = hook(0x0A00_0001, 0.1, 0.9, 1.0);
        assert_eq!(h.authored_priority(), 0.9);
        assert_eq!(h.play_probability(), 0.1);
    }

    #[test]
    fn always_and_never_are_the_ends_of_the_range() {
        assert!(
            hook(0x0A00_0001, 1.0, 0.0, 1.0)
                .try_roll(&mut roll(0.999))
                .is_some()
        );
        assert!(
            hook(0x0A00_0001, 0.0, 1.0, 1.0)
                .try_roll(&mut roll(0.0))
                .is_none()
        );
    }

    #[test]
    fn a_zero_volume_hook_plays_at_zero_not_at_full() {
        assert_eq!(
            hook(0x0A00_0001, 1.0, 0.0, 0.0).try_roll(&mut roll(0.5)),
            Some((0x0A00_0001, 0.0))
        );
    }

    #[test]
    fn the_authored_volume_is_passed_through_untouched() {
        assert_eq!(
            hook(0x0A00_0001, 1.0, 0.0, 4.5).try_roll(&mut roll(0.5)),
            Some((0x0A00_0001, 4.5))
        );
    }

    #[test]
    fn other_hooks_are_not_tweaked_hooks() {
        assert_eq!(
            TweakedSoundHook::from_hook(&HookData::Sound { id: 0x0A00_0001 }),
            None
        );
    }
}
