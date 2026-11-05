//! Choosing which wave a sound type plays.

use ac_dat::objects::{SoundEntry, SoundTable};

use crate::random::SoundRandom;

/// Pick one entry: `index = (int)(roll * (count - 1))`. The probability is
/// not consulted, and the last entry of a list longer than one can never be
/// picked; both are the game's own behaviour and are kept.
pub fn pick_variant<'a>(
    entries: &'a [SoundEntry],
    rng: &mut dyn SoundRandom,
) -> Option<&'a SoundEntry> {
    if entries.is_empty() {
        return None;
    }
    let index = (rng.next_variant_roll() * (entries.len() - 1) as f32) as usize;
    entries.get(index)
}

/// The play gate: a sound plays when the probability roll is strictly below
/// its probability. Probability 1 still skips once in 32768 rolls.
pub fn play_probability(probability: f32, rng: &mut dyn SoundRandom) -> bool {
    rng.next_probability_roll() < probability
}

/// Pick an entry, then roll its play gate. `None` when the list is empty or
/// the gate says silence.
pub fn select<'a>(entries: &'a [SoundEntry], rng: &mut dyn SoundRandom) -> Option<&'a SoundEntry> {
    let picked = pick_variant(entries, rng)?;
    play_probability(picked.probability, rng).then_some(picked)
}

/// [`select`] among the entries a table stores for `sound`.
pub fn select_in_table<'a>(
    table: &'a SoundTable,
    sound: u32,
    rng: &mut dyn SoundRandom,
) -> Option<&'a SoundEntry> {
    let (_, data) = table.sounds.iter().find(|(key, _)| *key == sound)?;
    select(&data.entries, rng)
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Audio/SoundCookbookTests.cs`.

    use ac_dat::objects::SoundData;

    use super::*;
    use crate::random::scripted::Scripted;
    use crate::random::{MAX_VARIANT_ROLL, SeededSoundRandom};
    use crate::sound_id::SoundId;

    fn entry(id: u32, probability: f32, volume: f32) -> SoundEntry {
        SoundEntry {
            id,
            priority: 1.0,
            probability,
            volume,
        }
    }

    fn e(id: u32) -> SoundEntry {
        entry(id, 1.0, 1.0)
    }

    #[test]
    fn pick_variant_empty_list_returns_none() {
        assert!(pick_variant(&[], &mut Scripted::new(&[0.0], &[])).is_none());
    }

    #[test]
    fn pick_variant_single_entry_always_index_zero() {
        let entries = [e(0x0A00_0001)];
        for roll in [0.0, 0.5, MAX_VARIANT_ROLL] {
            let picked = pick_variant(&entries, &mut Scripted::new(&[roll], &[])).unwrap();
            assert_eq!(picked.id, 0x0A00_0001);
        }
    }

    #[test]
    fn pick_variant_two_entries_last_is_unreachable() {
        let entries = [e(0x0A00_0001), e(0x0A00_051E)];
        for roll in [0.0, 0.25, 0.5, 0.75, 0.999, MAX_VARIANT_ROLL] {
            let picked = pick_variant(&entries, &mut Scripted::new(&[roll], &[])).unwrap();
            assert_eq!(picked.id, 0x0A00_0001);
        }
    }

    #[test]
    fn pick_variant_three_entries_truncates_toward_zero() {
        let entries = [e(0x0A00_0001), e(0x0A00_0002), e(0x0A00_0003)];
        for (roll, expected) in [(0.0, 0), (0.49, 0), (0.5, 1), (MAX_VARIANT_ROLL, 1)] {
            let picked = pick_variant(&entries, &mut Scripted::new(&[roll], &[])).unwrap();
            assert_eq!(picked.id, entries[expected].id, "roll {roll}");
        }
    }

    #[test]
    fn pick_variant_ignores_probability_entirely() {
        let entries = [entry(0x0A00_0001, 0.0, 1.0), entry(0x0A00_0002, 1.0, 1.0)];
        let picked = pick_variant(&entries, &mut Scripted::new(&[0.9], &[])).unwrap();
        assert_eq!(picked.id, 0x0A00_0001);
    }

    #[test]
    fn play_probability_is_strict_less_than() {
        for (p, roll, expected) in [
            (0.5, 0.49, true),
            (0.5, 0.5, false),
            (0.5, 0.51, false),
            (0.0, 0.0, false),
            (1.0, 0.99997, true),
            (1.0, 1.0, false),
        ] {
            assert_eq!(
                play_probability(p, &mut Scripted::new(&[], &[roll])),
                expected,
                "p {p} roll {roll}"
            );
        }
    }

    #[test]
    fn play_probability_zero_never_plays_across_the_whole_grid() {
        let mut rng = SeededSoundRandom::new(1);
        for _ in 0..20_000 {
            assert!(!play_probability(0.0, &mut rng));
        }
    }

    #[test]
    fn play_probability_five_percent_matches_retail_rate() {
        let mut rng = SeededSoundRandom::new(20_260_808);
        let played = (0..100_000)
            .filter(|_| play_probability(0.05, &mut rng))
            .count();
        assert!((4_600..=5_400).contains(&played), "{played}");
    }

    #[test]
    fn probability_roll_reaches_exactly_one_and_never_exceeds_it() {
        let mut rng = SeededSoundRandom::new(7);
        let mut saw_one = false;
        for _ in 0..500_000 {
            let roll = rng.next_probability_roll();
            assert!((0.0..=1.0).contains(&roll));
            saw_one |= roll == 1.0;
        }
        assert!(saw_one, "rand()/32767 must be able to return exactly 1.0");
    }

    #[test]
    fn variant_roll_never_reaches_one() {
        let mut rng = SeededSoundRandom::new(11);
        for _ in 0..500_000 {
            let roll = rng.next_variant_roll();
            assert!((0.0..=MAX_VARIANT_ROLL).contains(&roll));
        }
    }

    #[test]
    fn select_gate_failure_returns_none() {
        let entries = [entry(0x0A00_0001, 0.05, 1.0)];
        assert!(select(&entries, &mut Scripted::new(&[0.0], &[0.9])).is_none());
    }

    #[test]
    fn select_gate_pass_returns_picked_entry() {
        let entries = [entry(0x0A00_0001, 0.05, 1.0)];
        let picked = select(&entries, &mut Scripted::new(&[0.0], &[0.01])).unwrap();
        assert_eq!(picked.id, 0x0A00_0001);
    }

    #[test]
    fn select_single_entry_below_one_is_gated_not_short_circuited() {
        let entries = [entry(0x0A00_0001, 0.05, 1.0)];
        let mut rng = SeededSoundRandom::new(99);
        let played = (0..20_000)
            .filter(|_| select(&entries, &mut rng).is_some())
            .count();
        assert!((800..=1_200).contains(&played), "{played}");
    }

    fn table(sounds: Vec<(u32, SoundData)>) -> SoundTable {
        SoundTable {
            id: 0x2000_0001,
            hash_key: 0,
            hashes: Vec::new(),
            sounds,
        }
    }

    #[test]
    fn select_with_sound_table_looks_up_by_sound() {
        let t = table(vec![(
            SoundId::Footstep1.value(),
            SoundData {
                entries: vec![entry(0x0A00_0123, 1.0, 0.7)],
                unknown: 0,
            },
        )]);
        let picked = select_in_table(
            &t,
            SoundId::Footstep1.value(),
            &mut Scripted::new(&[0.0], &[0.0]),
        )
        .unwrap();
        assert_eq!(picked.id, 0x0A00_0123);
        assert_eq!(picked.volume, 0.7);
    }

    #[test]
    fn select_with_sound_table_missing_sound_returns_none() {
        let t = table(Vec::new());
        assert!(
            select_in_table(
                &t,
                SoundId::Attack1.value(),
                &mut Scripted::new(&[0.0], &[0.0])
            )
            .is_none()
        );
    }

    #[test]
    fn select_empty_entry_list_does_not_consume_a_probability_roll() {
        let t = table(vec![(
            SoundId::Attack1.value(),
            SoundData {
                entries: Vec::new(),
                unknown: 0,
            },
        )]);
        assert!(
            select_in_table(
                &t,
                SoundId::Attack1.value(),
                &mut Scripted::new(&[0.0], &[])
            )
            .is_none()
        );
    }
}
