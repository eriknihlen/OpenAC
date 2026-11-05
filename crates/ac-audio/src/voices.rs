//! Which voice a new sound gets, and the one pool of voices everything
//! shares: world sounds, ambient sounds and interface sounds alike.

use crate::options::AudioMixerOptions;

/// The priority every voice records when authored priority is off: one value
/// for the whole pool, as the game does. Since a playing voice is only taken
/// by a strictly higher priority, a started sound then always finishes and
/// the next one is dropped instead.
pub const FLAT_VOICE_PRIORITY: f32 = 0.0;

/// What one voice holds when a new sound asks for a place to play.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct VoiceSlotState {
    /// The voice was given a sound.
    pub occupied: bool,
    /// That sound is still sounding.
    pub still_playing: bool,
    /// The priority recorded with it.
    pub priority: f32,
    /// Which wave it was (0 = no id).
    pub wave_id: u32,
    /// When it started, milliseconds.
    pub started_at_ms: i64,
}

/// The game's two passes from `cursor` round the ring: the first voice that
/// is free or finished; else the first whose recorded priority is strictly
/// lower than `priority`. `None`: drop the sound.
#[must_use]
pub fn acquire_retail(slots: &[VoiceSlotState], cursor: usize, priority: f32) -> Option<usize> {
    let n = slots.len();
    if n == 0 {
        return None;
    }
    let ring = |offset: usize| (cursor + offset) % n;
    (0..n)
        .map(ring)
        .find(|&i| !slots[i].occupied || !slots[i].still_playing)
        .or_else(|| (0..n).map(ring).find(|&i| slots[i].priority < priority))
}

/// The ring position after the voice just claimed.
#[must_use]
pub fn advance_cursor(claimed: usize, slot_count: usize) -> usize {
    if slot_count == 0 {
        0
    } else {
        (claimed + 1) % slot_count
    }
}

/// The voice a new sound was given, and whether it was taken from a sound
/// still making noise.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VoiceClaim {
    /// The voice's index in the pool.
    pub slot: usize,
    /// It cut a playing sound short.
    pub took_playing_voice: bool,
}

/// Three questions in order: is this wave already holding as many voices as
/// it may (then it takes the oldest of its own)? Is a voice free or
/// finished? Is one carrying something strictly less important? With no cap
/// and one flat priority only the second can answer: the game's mixer.
#[must_use]
pub fn acquire(
    slots: &[VoiceSlotState],
    cursor: usize,
    priority: f32,
    wave_id: u32,
    max_voices_per_wave: i32,
) -> Option<VoiceClaim> {
    if slots.is_empty() {
        return None;
    }
    if let Some(slot) = oldest_of_one_wave(slots, cursor, wave_id, max_voices_per_wave) {
        return Some(VoiceClaim {
            slot,
            took_playing_voice: true,
        });
    }
    let slot = acquire_retail(slots, cursor, priority)?;
    let claimed = slots[slot];
    Some(VoiceClaim {
        slot,
        took_playing_voice: claimed.occupied && claimed.still_playing,
    })
}

/// When one wave holds its whole allowance, the copy playing longest.
fn oldest_of_one_wave(
    slots: &[VoiceSlotState],
    cursor: usize,
    wave_id: u32,
    max_voices_per_wave: i32,
) -> Option<usize> {
    if max_voices_per_wave <= 0 || wave_id == 0 {
        return None;
    }
    let n = slots.len();
    let mut holding = 0;
    let mut oldest: Option<usize> = None;
    for offset in 0..n {
        let i = (cursor + offset) % n;
        let s = slots[i];
        if !s.occupied || !s.still_playing || s.wave_id != wave_id {
            continue;
        }
        holding += 1;
        if oldest.is_none_or(|o| s.started_at_ms < slots[o].started_at_ms) {
            oldest = Some(i);
        }
    }
    if holding >= max_voices_per_wave {
        oldest
    } else {
        None
    }
}

/// One voice of the pool.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Voice {
    /// The mixer slot this voice speaks through (0 = none).
    pub source_id: u32,
    /// The entity whose sound this is, or 0 (an ambient, an interface cue).
    pub owner_id: u32,
    /// Given a sound and not vacated since.
    pub in_use: bool,
    /// Carrying an interface sound, which a world change leaves playing.
    pub is_interface: bool,
    /// The recorded priority.
    pub priority: f32,
    /// The wave it was given.
    pub wave_id: u32,
    /// When it was given that wave, milliseconds.
    pub spoke_at_ms: i64,
}

impl Voice {
    /// Give the voice back to the pool. The caller silences its source.
    pub fn vacate(&mut self) {
        *self = Self {
            source_id: self.source_id,
            ..Self::default()
        };
    }
}

/// A claim request.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ClaimRequest {
    /// The entity the sound belongs to, or 0.
    pub owner_id: u32,
    /// An interface sound.
    pub is_interface: bool,
    /// The authored priority (ignored when authored priority is off).
    pub authored_priority: f32,
    /// The wave, for the per-sound cap.
    pub wave_id: u32,
    /// Now, milliseconds, so the cap can find the oldest copy.
    pub now_ms: i64,
}

/// The pool of voices, sized and governed by [`AudioMixerOptions`].
#[derive(Debug, Clone)]
pub struct VoicePool {
    voices: Vec<Voice>,
    options: AudioMixerOptions,
    cursor: usize,
}

impl VoicePool {
    /// A pool with the settings' voice count; sources are unassigned (0).
    #[must_use]
    pub fn new(options: AudioMixerOptions) -> Self {
        let options = options.normalized();
        let count = options.effective_voice_count() as usize;
        Self {
            voices: vec![Voice::default(); count],
            options,
            cursor: 0,
        }
    }

    /// The settings in force.
    #[must_use]
    pub fn options(&self) -> &AudioMixerOptions {
        &self.options
    }

    /// Number of voices.
    #[must_use]
    pub fn len(&self) -> usize {
        self.voices.len()
    }

    /// Whether the pool has no voices.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.voices.is_empty()
    }

    /// The voices.
    #[must_use]
    pub fn voices(&self) -> &[Voice] {
        &self.voices
    }

    /// The voices, mutably.
    pub fn voices_mut(&mut self) -> &mut [Voice] {
        &mut self.voices
    }

    /// Claim a voice for a new sound; `None` drops the sound.
    /// `is_still_playing` asks the mixer whether a source still sounds: a
    /// voice whose sound finished is free again though never vacated.
    pub fn claim(
        &mut self,
        is_still_playing: impl Fn(u32) -> bool,
        request: ClaimRequest,
    ) -> Option<VoiceClaim> {
        if self.voices.is_empty() {
            return None;
        }
        let recorded = if self.options.effective_use_authored_priority() {
            request.authored_priority
        } else {
            FLAT_VOICE_PRIORITY
        };
        let slots: Vec<VoiceSlotState> = self
            .voices
            .iter()
            .map(|v| VoiceSlotState {
                occupied: v.in_use,
                still_playing: v.in_use && is_still_playing(v.source_id),
                priority: v.priority,
                wave_id: v.wave_id,
                started_at_ms: v.spoke_at_ms,
            })
            .collect();
        let claim = acquire(
            &slots,
            self.cursor,
            recorded,
            request.wave_id,
            self.options.effective_max_voices_per_wave(),
        )?;
        let v = &mut self.voices[claim.slot];
        v.in_use = true;
        v.owner_id = request.owner_id;
        v.is_interface = request.is_interface;
        v.priority = recorded;
        v.wave_id = request.wave_id;
        v.spoke_at_ms = request.now_ms;
        self.cursor = advance_cursor(claim.slot, self.voices.len());
        Some(claim)
    }

    /// Put new settings in force. Voices beyond the new count go to
    /// `retire` (last first); new voices ask `create_source` for a source.
    pub fn apply_options(
        &mut self,
        options: AudioMixerOptions,
        mut retire: impl FnMut(Voice),
        mut create_source: impl FnMut() -> u32,
    ) {
        self.options = options.normalized();
        let target = self.options.effective_voice_count() as usize;
        while self.voices.len() > target {
            if let Some(leaving) = self.voices.pop() {
                retire(leaving);
            }
        }
        while self.voices.len() < target {
            self.voices.push(Voice {
                source_id: create_source(),
                ..Voice::default()
            });
        }
        if self.cursor >= self.voices.len() {
            self.cursor = 0;
        }
    }

    /// Indices of the voices a world change silences: every one not carrying
    /// an interface sound.
    pub fn silenced_by_world_change(&self) -> impl Iterator<Item = usize> + '_ {
        self.voices
            .iter()
            .enumerate()
            .filter(|(_, v)| !v.is_interface)
            .map(|(i, _)| i)
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Audio/RetailVoicePoolTests.cs`,
    //! `VoiceAllocationTests.cs` and `tests/AcDream.App.Tests/Audio/WorldVoicePoolTests.cs`.

    use super::*;
    use crate::options::{NO_PER_WAVE_CAP, RETAIL_MIXER_VOICE_COUNT};

    fn free() -> VoiceSlotState {
        VoiceSlotState::default()
    }
    fn finished(priority: f32) -> VoiceSlotState {
        VoiceSlotState {
            occupied: true,
            priority,
            ..VoiceSlotState::default()
        }
    }
    fn busy(priority: f32) -> VoiceSlotState {
        VoiceSlotState {
            occupied: true,
            still_playing: true,
            priority,
            ..VoiceSlotState::default()
        }
    }
    fn all_busy(priority: f32, n: usize) -> Vec<VoiceSlotState> {
        vec![busy(priority); n]
    }

    // RetailVoicePoolTests

    #[test]
    fn empty_pool_drops_the_sound() {
        assert_eq!(acquire_retail(&[], 0, 1.0), None);
    }

    #[test]
    fn first_pass_prefers_a_free_slot_scanning_from_the_cursor() {
        let mut s = all_busy(1.0, 16);
        s[9] = free();
        assert_eq!(acquire_retail(&s, 0, 0.0), Some(9));
    }

    #[test]
    fn first_pass_reclaims_a_finished_voice_even_at_higher_priority() {
        let mut s = all_busy(1.0, 16);
        s[4] = finished(1.0);
        assert_eq!(acquire_retail(&s, 0, 0.1), Some(4));
    }

    #[test]
    fn first_pass_wraps_around_the_ring() {
        let mut s = all_busy(1.0, 16);
        s[2] = free();
        assert_eq!(acquire_retail(&s, 5, 0.0), Some(2));
    }

    #[test]
    fn first_pass_takes_the_nearest_free_slot_in_ring_order() {
        let mut s = all_busy(1.0, 16);
        s[1] = free();
        s[12] = free();
        assert_eq!(acquire_retail(&s, 10, 0.0), Some(12));
    }

    #[test]
    fn second_pass_evicts_strictly_lower_priority() {
        let mut s = all_busy(0.5, 16);
        s[7] = busy(0.2);
        assert_eq!(acquire_retail(&s, 0, 0.3), Some(7));
    }

    #[test]
    fn second_pass_equal_priority_never_evicts() {
        assert_eq!(acquire_retail(&all_busy(0.5, 16), 0, 0.5), None);
    }

    #[test]
    fn second_pass_higher_priority_pool_drops_the_new_sound() {
        assert_eq!(acquire_retail(&all_busy(0.9, 16), 0, 0.4), None);
    }

    #[test]
    fn second_pass_takes_the_first_lower_slot_in_ring_order_not_the_lowest() {
        let mut s = all_busy(0.9, 16);
        s[3] = busy(0.1);
        s[6] = busy(0.5);
        assert_eq!(acquire_retail(&s, 6, 0.6), Some(6));
    }

    #[test]
    fn cursor_advances_past_the_claimed_slot_and_wraps() {
        assert_eq!(advance_cursor(0, 16), 1);
        assert_eq!(advance_cursor(15, 16), 0);
        assert_eq!(advance_cursor(9, 16), 10);
    }

    #[test]
    fn ring_order_is_stable_across_repeated_claims() {
        let s = vec![free(); 4];
        let mut cursor = 0;
        let mut claimed = Vec::new();
        for _ in 0..4 {
            let c = acquire_retail(&s, cursor, 1.0).unwrap();
            claimed.push(c);
            cursor = advance_cursor(c, 4);
        }
        assert_eq!(claimed, [0, 1, 2, 3]);
    }

    // VoiceAllocationTests

    const FOOTSTEP: u32 = 0x0A00_0101;
    const SPELL: u32 = 0x0A00_0202;
    const FOOTSTEP_PRIORITY: f32 = 0.10;
    const SPELL_PRIORITY: f32 = 0.90;

    fn playing(priority: f32, wave_id: u32, started_at_ms: i64) -> VoiceSlotState {
        VoiceSlotState {
            occupied: true,
            still_playing: true,
            priority,
            wave_id,
            started_at_ms,
        }
    }

    fn all_playing(priority: f32, n: usize, wave_id: u32) -> Vec<VoiceSlotState> {
        (0..n)
            .map(|i| playing(priority, wave_id, (i as i64 + 1) * 100))
            .collect()
    }

    #[test]
    fn with_every_voice_holding_a_footstep_the_spell_takes_one() {
        let s = all_playing(FOOTSTEP_PRIORITY, 16, FOOTSTEP);
        let r = acquire(&s, 0, SPELL_PRIORITY, SPELL, NO_PER_WAVE_CAP).unwrap();
        assert_eq!(r.slot, 0);
        assert!(r.took_playing_voice);
    }

    #[test]
    fn with_every_voice_holding_a_footstep_another_footstep_is_dropped() {
        let s = all_playing(FOOTSTEP_PRIORITY, 16, FOOTSTEP);
        assert_eq!(
            acquire(&s, 0, FOOTSTEP_PRIORITY, FOOTSTEP, NO_PER_WAVE_CAP),
            None
        );
    }

    #[test]
    fn without_authored_priority_even_the_spell_is_dropped() {
        let s = all_playing(FLAT_VOICE_PRIORITY, 16, FOOTSTEP);
        assert_eq!(
            acquire(&s, 0, FLAT_VOICE_PRIORITY, SPELL, NO_PER_WAVE_CAP),
            None
        );
    }

    #[test]
    fn at_the_cap_the_next_copy_replaces_the_oldest_copy_of_itself() {
        let mut s = vec![free(); 16];
        s[0] = playing(FOOTSTEP_PRIORITY, FOOTSTEP, 400);
        s[1] = playing(SPELL_PRIORITY, SPELL, 50);
        s[2] = playing(FOOTSTEP_PRIORITY, FOOTSTEP, 100);
        s[3] = playing(FOOTSTEP_PRIORITY, FOOTSTEP, 300);
        s[4] = playing(FOOTSTEP_PRIORITY, FOOTSTEP, 200);
        for slot in s.iter_mut().skip(5) {
            *slot = playing(FOOTSTEP_PRIORITY, 0x0A00_0999, 10);
        }
        let r = acquire(&s, 0, FOOTSTEP_PRIORITY, FOOTSTEP, 4).unwrap();
        assert_eq!(r.slot, 2);
        assert!(r.took_playing_voice);
    }

    #[test]
    fn at_the_cap_for_one_sound_a_different_sound_still_takes_a_free_voice() {
        let mut s = vec![free(); 16];
        for (i, slot) in s.iter_mut().enumerate().take(4) {
            *slot = playing(FOOTSTEP_PRIORITY, FOOTSTEP, (i as i64 + 1) * 100);
        }
        let r = acquire(&s, 0, SPELL_PRIORITY, SPELL, 4).unwrap();
        assert_eq!(r.slot, 4);
        assert!(!r.took_playing_voice);
    }

    #[test]
    fn below_the_cap_the_copy_takes_a_free_voice() {
        let mut s = vec![free(); 16];
        for (i, slot) in s.iter_mut().enumerate().take(3) {
            *slot = playing(FOOTSTEP_PRIORITY, FOOTSTEP, (i as i64 + 1) * 100);
        }
        let r = acquire(&s, 0, FOOTSTEP_PRIORITY, FOOTSTEP, 4).unwrap();
        assert_eq!(r.slot, 3);
        assert!(!r.took_playing_voice);
    }

    #[test]
    fn a_finished_copy_does_not_count_towards_the_cap() {
        let mut s = vec![playing(SPELL_PRIORITY, SPELL, 10); 16];
        s[0] = playing(FOOTSTEP_PRIORITY, FOOTSTEP, 100);
        s[1] = playing(FOOTSTEP_PRIORITY, FOOTSTEP, 200);
        s[2] = playing(FOOTSTEP_PRIORITY, FOOTSTEP, 300);
        s[3] = VoiceSlotState {
            wave_id: FOOTSTEP,
            ..finished(FOOTSTEP_PRIORITY)
        };
        let r = acquire(&s, 0, FOOTSTEP_PRIORITY, FOOTSTEP, 4).unwrap();
        assert_eq!(r.slot, 3);
        assert!(!r.took_playing_voice);
    }

    #[test]
    fn a_sound_with_no_id_is_not_capped() {
        let s = all_playing(FLAT_VOICE_PRIORITY, 16, 0);
        assert_eq!(acquire(&s, 0, FLAT_VOICE_PRIORITY, 0, 1), None);
    }

    #[test]
    fn a_free_voice_is_preferred_to_taking_one() {
        let s = [
            playing(SPELL_PRIORITY, SPELL, 100),
            free(),
            playing(FOOTSTEP_PRIORITY, FOOTSTEP, 200),
            finished(FOOTSTEP_PRIORITY),
        ];
        let r = acquire(&s, 0, SPELL_PRIORITY, SPELL, NO_PER_WAVE_CAP).unwrap();
        assert_eq!(r.slot, 1);
        assert!(!r.took_playing_voice);
    }

    #[test]
    fn an_empty_pool_has_nothing_to_give() {
        assert_eq!(acquire(&[], 0, SPELL_PRIORITY, SPELL, 4), None);
    }

    #[test]
    fn with_thirty_two_voices_the_thirty_two_slots_are_all_usable() {
        let mut s = all_playing(FOOTSTEP_PRIORITY, 32, FOOTSTEP);
        s[31] = free();
        let r = acquire(&s, 0, FOOTSTEP_PRIORITY, FOOTSTEP, NO_PER_WAVE_CAP).unwrap();
        assert_eq!(r.slot, 31);
    }

    // WorldVoicePoolTests

    fn shipped() -> AudioMixerOptions {
        AudioMixerOptions {
            retail_mixer: true,
            ..AudioMixerOptions::default()
        }
    }

    fn sixteen_with_priority() -> AudioMixerOptions {
        AudioMixerOptions {
            voice_count: 16,
            use_authored_priority: true,
            max_voices_per_wave: NO_PER_WAVE_CAP,
            ..AudioMixerOptions::default()
        }
    }

    fn yes(_: u32) -> bool {
        true
    }
    fn no(_: u32) -> bool {
        false
    }

    fn request(owner_id: u32, is_interface: bool) -> ClaimRequest {
        ClaimRequest {
            owner_id,
            is_interface,
            authored_priority: 0.0,
            wave_id: 0,
            now_ms: 0,
        }
    }

    fn wave_request(owner_id: u32, priority: f32, wave_id: u32, now_ms: i64) -> ClaimRequest {
        ClaimRequest {
            owner_id,
            is_interface: false,
            authored_priority: priority,
            wave_id,
            now_ms,
        }
    }

    fn with_sources(options: AudioMixerOptions) -> VoicePool {
        let mut pool = VoicePool::new(options);
        for (i, v) in pool.voices_mut().iter_mut().enumerate() {
            v.source_id = i as u32 + 1;
        }
        pool
    }

    fn filled(options: AudioMixerOptions) -> VoicePool {
        let mut pool = with_sources(options);
        for i in 0..pool.len() {
            let c = pool.claim(yes, request(i as u32 + 1, i % 4 == 0)).unwrap();
            assert_eq!(c.slot, i);
        }
        pool
    }

    fn filled_with_footsteps(options: AudioMixerOptions) -> VoicePool {
        let mut pool = with_sources(options);
        for i in 0..pool.len() {
            assert!(
                pool.claim(
                    yes,
                    wave_request(
                        i as u32 + 1,
                        FOOTSTEP_PRIORITY,
                        FOOTSTEP,
                        (i as i64 + 1) * 100
                    )
                )
                .is_some()
            );
        }
        pool
    }

    #[test]
    fn the_shipped_mixer_has_sixteen_voices_and_the_default_thirty_two() {
        assert_eq!(VoicePool::new(shipped()).len(), 16);
        assert_eq!(VoicePool::new(AudioMixerOptions::default()).len(), 32);
    }

    #[test]
    fn sixteen_voices_claim_in_ring_order() {
        let pool = filled(shipped());
        for (i, v) in pool.voices().iter().enumerate() {
            assert!(v.in_use);
            assert_eq!(v.owner_id, i as u32 + 1);
        }
    }

    #[test]
    fn a_seventeenth_sound_is_dropped_without_taking_a_voice_from_a_playing_one() {
        let mut pool = filled(shipped());
        assert_eq!(pool.claim(yes, request(0xDEAD, false)), None);
        for (i, v) in pool.voices().iter().enumerate() {
            assert_eq!(v.owner_id, i as u32 + 1);
        }
    }

    #[test]
    fn with_thirty_two_voices_thirty_two_sounds_play_before_one_is_dropped() {
        let mut pool = with_sources(AudioMixerOptions {
            voice_count: 32,
            ..AudioMixerOptions::default()
        });
        let played = (0..64)
            .take_while(|&i| {
                pool.claim(
                    yes,
                    wave_request(
                        i + 1,
                        FOOTSTEP_PRIORITY,
                        0x0A00_1000 + i,
                        (i64::from(i) + 1) * 100,
                    ),
                )
                .is_some()
            })
            .count();
        assert_eq!(played, 32);
    }

    #[test]
    fn an_important_sound_takes_a_voice_from_a_less_important_one() {
        let mut pool = filled_with_footsteps(sixteen_with_priority());
        let c = pool
            .claim(yes, wave_request(0x5000_0001, SPELL_PRIORITY, SPELL, 9_000))
            .unwrap();
        assert!(c.took_playing_voice);
        assert_eq!(c.slot, 0);
        assert_eq!(pool.voices()[0].priority, SPELL_PRIORITY);
        assert_eq!(pool.voices()[0].wave_id, SPELL);
        assert_eq!(
            pool.claim(
                yes,
                wave_request(0x5000_0002, FOOTSTEP_PRIORITY, FOOTSTEP, 9_100)
            ),
            None
        );
    }

    #[test]
    fn with_the_shipped_mixer_even_the_spell_is_dropped() {
        let mut pool = filled_with_footsteps(shipped());
        assert_eq!(
            pool.claim(yes, wave_request(0x5000_0001, SPELL_PRIORITY, SPELL, 9_000)),
            None
        );
        assert!(
            pool.voices()
                .iter()
                .all(|v| v.priority == FLAT_VOICE_PRIORITY)
        );
    }

    #[test]
    fn at_its_cap_a_sound_replaces_its_own_oldest_copy() {
        let mut pool = with_sources(AudioMixerOptions {
            voice_count: 16,
            max_voices_per_wave: 4,
            ..AudioMixerOptions::default()
        });
        for i in 0..4 {
            assert!(
                pool.claim(
                    yes,
                    wave_request(i + 1, FOOTSTEP_PRIORITY, FOOTSTEP, (i64::from(i) + 1) * 100)
                )
                .is_some()
            );
        }
        let fifth = pool
            .claim(yes, wave_request(0x777, FOOTSTEP_PRIORITY, FOOTSTEP, 500))
            .unwrap();
        assert!(fifth.took_playing_voice);
        assert_eq!(fifth.slot, 0);
        assert_eq!(pool.voices()[0].owner_id, 0x777);
        assert_eq!(pool.voices()[0].spoke_at_ms, 500);
        let other = pool
            .claim(yes, wave_request(0x888, SPELL_PRIORITY, SPELL, 600))
            .unwrap();
        assert!(!other.took_playing_voice);
        assert_eq!(other.slot, 4);
    }

    #[test]
    fn an_interface_sound_is_refused_when_all_are_busy_and_takes_a_free_one() {
        let mut pool = filled(shipped());
        assert_eq!(pool.claim(yes, request(0, true)), None);
        pool.voices_mut()[3].vacate();
        let c = pool.claim(yes, request(0, true)).unwrap();
        assert_eq!(c.slot, 3);
        assert!(pool.voices()[3].is_interface);
    }

    #[test]
    fn a_world_change_silences_the_world_voices_only() {
        let mut pool = with_sources(shipped());
        let world = pool.claim(yes, request(0x5000_0001, false)).unwrap().slot;
        let cue = pool.claim(yes, request(0, true)).unwrap().slot;
        let silenced: Vec<usize> = pool.silenced_by_world_change().collect();
        assert_eq!(silenced.len(), pool.len() - 1);
        assert!(silenced.contains(&world));
        assert!(!silenced.contains(&cue));
    }

    #[test]
    fn without_authored_priority_every_voice_records_the_one_priority() {
        let pool = filled(shipped());
        assert!(
            pool.voices()
                .iter()
                .all(|v| v.priority == FLAT_VOICE_PRIORITY)
        );
    }

    #[test]
    fn a_voice_whose_sound_has_finished_is_claimed_again() {
        let mut pool = filled(shipped());
        let c = pool.claim(no, request(0xBEEF, false)).unwrap();
        let v = pool.voices()[c.slot];
        assert_eq!(v.owner_id, 0xBEEF);
        assert!(v.in_use);
        assert!(!v.is_interface);
    }

    #[test]
    fn a_vacated_voice_is_free_again() {
        let mut pool = filled(shipped());
        pool.voices_mut()[5].vacate();
        let v = pool.voices()[5];
        assert!(!v.in_use && !v.is_interface);
        assert_eq!((v.owner_id, v.wave_id), (0, 0));
        assert_eq!(v.source_id, 6);
        assert_eq!(pool.claim(yes, request(0x1234, false)).unwrap().slot, 5);
    }

    #[test]
    fn shrinking_the_pool_hands_back_every_voice_beyond_the_new_count() {
        let mut pool = filled(AudioMixerOptions {
            voice_count: 32,
            ..AudioMixerOptions::default()
        });
        let first = pool.voices()[0];
        let mut retired = Vec::new();
        pool.apply_options(
            shipped(),
            |v| retired.push(v.source_id),
            || panic!("nothing to create"),
        );
        assert_eq!(pool.len(), RETAIL_MIXER_VOICE_COUNT as usize);
        assert_eq!(retired, (17..=32).rev().collect::<Vec<u32>>());
        assert_eq!(pool.voices()[0], first);
    }

    #[test]
    fn growing_the_pool_asks_for_one_source_per_voice_it_gains() {
        let mut pool = VoicePool::new(shipped());
        let mut next = 100u32;
        pool.apply_options(
            AudioMixerOptions {
                voice_count: 24,
                ..AudioMixerOptions::default()
            },
            |_| panic!("nothing to retire"),
            || {
                next += 1;
                next - 1
            },
        );
        assert_eq!(pool.len(), 24);
        assert_eq!(next, 108);
        for i in 16..24 {
            assert_eq!(pool.voices()[i].source_id, 100 + i as u32 - 16);
        }
    }

    #[test]
    fn new_settings_take_effect_on_the_next_claim() {
        let mut pool = filled_with_footsteps(sixteen_with_priority());
        pool.apply_options(shipped(), |_| {}, || 0);
        assert_eq!(
            pool.claim(yes, wave_request(0x5000_0001, SPELL_PRIORITY, SPELL, 9_000)),
            None
        );
    }
}
