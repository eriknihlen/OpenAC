//! The two dice the sound code rolls.

use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};

/// The largest variant roll (`1 - 2^-23`), so a roll scaled by a count
/// never reaches the count.
pub const MAX_VARIANT_ROLL: f32 = 1.0 - f32::EPSILON;

/// The C library's `RAND_MAX`, the grid the probability roll lands on.
pub const RAND_MAX: u32 = 32767;

/// Where the sound code's randomness comes from. Two rolls with different
/// grids: the variant roll is uniform in `[0, 1)`; the probability roll is
/// `rand() / RAND_MAX`, which can reach exactly 1.
pub trait SoundRandom {
    /// A uniform roll in `[0, MAX_VARIANT_ROLL]`.
    fn next_variant_roll(&mut self) -> f32;
    /// `n / 32767` for a uniform `n` in `0..=32767`.
    fn next_probability_roll(&mut self) -> f32;
}

/// The default [`SoundRandom`]: a small seeded generator (SplitMix64).
#[derive(Debug, Clone)]
pub struct SeededSoundRandom {
    state: u64,
}

impl SeededSoundRandom {
    /// A generator with a fixed seed (tests, replays).
    #[must_use]
    pub fn new(seed: u64) -> Self {
        Self { state: seed }
    }

    /// A generator seeded differently on every run.
    #[must_use]
    pub fn from_entropy() -> Self {
        let mut hasher = RandomState::new().build_hasher();
        hasher.write_u64(0x5EED);
        Self::new(hasher.finish())
    }

    fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }
}

impl SoundRandom for SeededSoundRandom {
    fn next_variant_roll(&mut self) -> f32 {
        // 53 random bits as a double in [0, 1), then narrowed; narrowing can
        // round up to 1.0, which the clamp keeps out.
        let unit = (self.next_u64() >> 11) as f64 * (1.0 / (1u64 << 53) as f64);
        (unit as f32).min(MAX_VARIANT_ROLL)
    }

    fn next_probability_roll(&mut self) -> f32 {
        let n = (self.next_u64() >> 32) as u32 % (RAND_MAX + 1);
        n as f32 * (1.0 / RAND_MAX as f32)
    }
}

#[cfg(test)]
pub(crate) mod scripted {
    //! A [`SoundRandom`] that replays fixed rolls, as OpenAC's tests do.

    use std::collections::VecDeque;

    use super::SoundRandom;

    /// Replays the given rolls; panics when a roll is asked for that was not
    /// scripted (unless built with [`Scripted::lenient`]).
    #[derive(Debug, Default)]
    pub(crate) struct Scripted {
        variant: VecDeque<f32>,
        probability: VecDeque<f32>,
        /// One queue for both rolls, returning 0 once empty.
        shared: Option<VecDeque<f32>>,
    }

    impl Scripted {
        pub(crate) fn new(variant: &[f32], probability: &[f32]) -> Self {
            Self {
                variant: variant.iter().copied().collect(),
                probability: probability.iter().copied().collect(),
                shared: None,
            }
        }

        /// Both rolls come from one queue, and 0 once it runs out.
        pub(crate) fn lenient(rolls: &[f32]) -> Self {
            Self {
                shared: Some(rolls.iter().copied().collect()),
                ..Self::default()
            }
        }
    }

    impl SoundRandom for Scripted {
        fn next_variant_roll(&mut self) -> f32 {
            match &mut self.shared {
                Some(q) => q.pop_front().unwrap_or(0.0),
                None => self.variant.pop_front().expect("unscripted variant roll"),
            }
        }

        fn next_probability_roll(&mut self) -> f32 {
            match &mut self.shared {
                Some(q) => q.pop_front().unwrap_or(0.0),
                None => self
                    .probability
                    .pop_front()
                    .expect("unscripted probability roll"),
            }
        }
    }
}
