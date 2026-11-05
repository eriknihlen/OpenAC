use std::cmp::Reverse;
use std::collections::{BTreeMap, BinaryHeap};

use super::TransportStats;
use super::seq::is_newer;
use crate::crypto::Isaac;
use crate::packet::SequenceList;

/// The watermark starts at 1: the server's first sealed packet is sequence
/// 2 (its cleartext connect request took 0 and it never sends 1), so a zero
/// start would NAK a sequence that never existed.
pub const INITIAL_WATERMARK: u32 = 1;

/// Packets more than this far ahead of the watermark are dropped as bogus.
pub const SANITY_WINDOW: u32 = 0x7FFF;

/// A keystream word set aside for a sequence, with the order it was drawn.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct ParkedWord {
    word: u32,
    draw_order: u64,
}

/// What to do with an arriving sequenced packet.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Admission {
    /// Drop it unseen (duplicate or implausible sequence).
    Drop,
    /// Check its checksum: against `key` when sealed, in the clear when
    /// `key` is `None`. `draw_order` lets a failed check re-park the key.
    Process {
        /// The keystream word to verify with.
        key: Option<u32>,
        /// When the word was drawn (for re-parking).
        draw_order: u64,
    },
}

/// Inbound sequencing and the inbound keystream.
///
/// Packets are processed as they arrive (there is no reorder buffer), but
/// the keystream is consumed in *sequence* order: when a packet arrives
/// ahead of a gap, one word is drawn for each missing sequence first and
/// parked beside it (that set doubles as the NAK list), then the arriving
/// packet draws its own. A late arrival uses its parked word. Consuming in
/// arrival order instead would shift the stream forever after one loss.
#[derive(Debug)]
pub struct InboundSequenceTracker {
    keystream: Isaac,
    highest_id_received: u32,
    nak_set: BTreeMap<u32, ParkedWord>,
    /// Words returned by a cleartext reject, consumed lowest draw order first.
    reclaimed: BinaryHeap<Reverse<(u64, u32)>>,
    draw_ordinal: u64,
}

impl InboundSequenceTracker {
    /// A tracker whose watermark starts at `initial_watermark`.
    pub fn new(keystream: Isaac, initial_watermark: u32) -> Self {
        Self {
            keystream,
            highest_id_received: initial_watermark,
            nak_set: BTreeMap::new(),
            reclaimed: BinaryHeap::new(),
            draw_ordinal: 0,
        }
    }

    /// The highest sequence seen (the value our cumulative ack carries).
    pub fn highest_id_received(&self) -> u32 {
        self.highest_id_received
    }

    /// Missing sequences awaiting a resend.
    pub fn nak_count(&self) -> usize {
        self.nak_set.len()
    }

    /// Reclaimed words waiting to be used.
    pub fn reclaimed_word_count(&self) -> usize {
        self.reclaimed.len()
    }

    /// Decide what to do with a sequenced (non-zero) packet, parking words
    /// for any gap it reveals.
    pub fn admit(
        &mut self,
        sequence: u32,
        encrypted: bool,
        stats: &mut TransportStats,
    ) -> Admission {
        if is_newer(
            sequence,
            self.highest_id_received.wrapping_add(SANITY_WINDOW),
        ) {
            stats.inbound_sanity_drops += 1;
            return Admission::Drop;
        }

        let newer = is_newer(sequence, self.highest_id_received);
        let mut parked = None;
        if encrypted && !newer {
            match self.nak_set.remove(&sequence) {
                Some(word) => parked = Some(word),
                None => {
                    // A duplicate of something already decoded: no word spent.
                    stats.inbound_dups_dropped += 1;
                    return Admission::Drop;
                }
            }
        }

        if newer {
            // A cleartext packet borrows an already-used sequence, so the
            // walk includes its own id: the real packet is still owed.
            let end = if encrypted {
                sequence
            } else {
                sequence.wrapping_add(1)
            };
            let mut id = self.highest_id_received.wrapping_add(1);
            while id != end {
                if id != 0 {
                    self.add_nakked(id, stats);
                }
                id = id.wrapping_add(1);
            }
            self.highest_id_received = sequence;
        }

        if !encrypted {
            return Admission::Process {
                key: None,
                draw_order: u64::MAX,
            };
        }
        let own = match parked {
            Some(word) => word,
            None => self.next_word(),
        };
        Admission::Process {
            key: Some(own.word),
            draw_order: own.draw_order,
        }
    }

    /// Put back a word whose packet failed its checksum, so the resend of
    /// that sequence verifies with the same word.
    pub fn repark(&mut self, sequence: u32, key: u32, draw_order: u64, stats: &mut TransportStats) {
        if self.nak_set.contains_key(&sequence) {
            return;
        }
        self.nak_set.insert(
            sequence,
            ParkedWord {
                word: key,
                draw_order,
            },
        );
        stats.keys_parked += 1;
    }

    /// The server will never resend these: stop asking.
    pub fn on_reject_retransmit(&mut self, ids: SequenceList<'_>) {
        for id in ids.iter() {
            self.nak_set.remove(&id);
        }
    }

    /// Up to `max` missing sequences, numerically ascending.
    pub fn nak_ids_ascending(&self, max: usize) -> Vec<u32> {
        self.nak_set.keys().take(max).copied().collect()
    }

    /// A validated cleartext reject packet arrived at a fresh sequence. The
    /// server spent no keystream word on it, yet the gap walk parked one for
    /// it: remove that mis-park, shift every later-drawn parked word down one
    /// position, and pool the excess word for the next fresh draw.
    pub fn on_cleartext_reject_sequence(&mut self, sequence: u32, stats: &mut TransportStats) {
        let Some(reclaimed) = self.nak_set.remove(&sequence) else {
            return;
        };
        // Ascending draw order is ascending sequence inside the set, and
        // ordering by draw order is wrap-proof.
        let mut later: Vec<(u64, u32)> = self
            .nak_set
            .iter()
            .filter(|(_, parked)| parked.draw_order > reclaimed.draw_order)
            .map(|(&id, parked)| (parked.draw_order, id))
            .collect();
        later.sort_unstable();

        let mut carry = reclaimed;
        for (_, id) in later {
            if let Some(slot) = self.nak_set.get_mut(&id) {
                core::mem::swap(slot, &mut carry);
            }
        }
        self.reclaimed.push(Reverse((carry.draw_order, carry.word)));
        stats.reject_words_reclaimed += 1;
    }

    fn next_word(&mut self) -> ParkedWord {
        if let Some(Reverse((draw_order, word))) = self.reclaimed.pop() {
            return ParkedWord { word, draw_order };
        }
        self.draw_ordinal += 1;
        ParkedWord {
            word: self.keystream.next_word(),
            draw_order: self.draw_ordinal,
        }
    }

    fn add_nakked(&mut self, sequence: u32, stats: &mut TransportStats) {
        if self.nak_set.contains_key(&sequence) {
            return;
        }
        let word = self.next_word();
        self.nak_set.insert(sequence, word);
        stats.keys_parked += 1;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SEED: u32 = 0x5EED_5EED;

    fn tracker(initial: u32) -> (InboundSequenceTracker, TransportStats) {
        (
            InboundSequenceTracker::new(Isaac::new(SEED), initial),
            TransportStats::default(),
        )
    }

    fn admitted(t: &mut InboundSequenceTracker, s: &mut TransportStats, sequence: u32) -> u32 {
        match t.admit(sequence, true, s) {
            Admission::Process { key: Some(key), .. } => key,
            other => panic!("sequence {sequence} not admitted: {other:?}"),
        }
    }

    fn words(n: usize) -> Vec<u32> {
        let mut shadow = Isaac::new(SEED);
        (0..n).map(|_| shadow.next_word()).collect()
    }

    fn ids(list: &[u32]) -> Vec<u8> {
        list.iter().flat_map(|id| id.to_le_bytes()).collect()
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.GapWalk_ParksTheMissingKey_LaterPacketsAndLateArrivalDecode`.
    #[test]
    fn gap_walk_parks_the_missing_key() {
        let (mut t, mut s) = tracker(9);
        let w = words(6); // w[0] = sequence 10, ...
        assert_eq!(admitted(&mut t, &mut s, 10), w[0]);
        assert_eq!(admitted(&mut t, &mut s, 11), w[1]);
        assert_eq!(admitted(&mut t, &mut s, 13), w[3]);
        assert_eq!(t.nak_count(), 1);
        assert_eq!(s.keys_parked, 1);
        assert_eq!(t.highest_id_received(), 13);
        assert_eq!(admitted(&mut t, &mut s, 14), w[4]);
        assert_eq!(admitted(&mut t, &mut s, 12), w[2]);
        assert_eq!(t.nak_count(), 0);
        assert_eq!(t.highest_id_received(), 14);
        assert_eq!(admitted(&mut t, &mut s, 15), w[5]);
        assert_eq!(s.inbound_dups_dropped, 0);
        assert_eq!(s.inbound_sanity_drops, 0);
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.Duplicate_NeverNakked_DropsAtZeroKeystreamCost`.
    #[test]
    fn duplicate_drops_at_zero_keystream_cost() {
        let (mut t, mut s) = tracker(9);
        let w = words(2);
        assert_eq!(admitted(&mut t, &mut s, 10), w[0]);
        assert_eq!(t.admit(10, true, &mut s), Admission::Drop);
        assert_eq!(s.inbound_dups_dropped, 1);
        assert_eq!(admitted(&mut t, &mut s, 11), w[1]);
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.ChecksumFailureRepark_TheRetransmissionDecodesWithTheSameKey`.
    #[test]
    fn checksum_failure_reparks_the_key() {
        let (mut t, mut s) = tracker(9);
        let w = words(2);
        let Admission::Process {
            key: Some(key),
            draw_order,
        } = t.admit(10, true, &mut s)
        else {
            panic!("not admitted");
        };
        assert_eq!(key, w[0]);
        t.repark(10, key, draw_order, &mut s);
        assert_eq!(t.nak_count(), 1);
        assert_eq!(s.keys_parked, 1);
        assert_eq!(admitted(&mut t, &mut s, 10), w[0]);
        assert_eq!(t.nak_count(), 0);
        assert_eq!(admitted(&mut t, &mut s, 11), w[1]);
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.Cleartext_AtHighestPlusOne_NaksItsOwnBorrowedId`.
    #[test]
    fn cleartext_one_ahead_naks_its_own_borrowed_id() {
        let (mut t, mut s) = tracker(9);
        let w = words(2);
        assert_eq!(
            t.admit(10, false, &mut s),
            Admission::Process {
                key: None,
                draw_order: u64::MAX
            }
        );
        assert_eq!(t.highest_id_received(), 10);
        assert_eq!(t.nak_count(), 1);
        assert_eq!(s.keys_parked, 1);
        assert_eq!(admitted(&mut t, &mut s, 10), w[0]);
        assert_eq!(t.nak_count(), 0);
        assert_eq!(admitted(&mut t, &mut s, 11), w[1]);
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.Cleartext_AtHighest_NoNak_NoKey_NoWatermarkChange`.
    #[test]
    fn cleartext_at_the_watermark_changes_nothing() {
        let (mut t, mut s) = tracker(9);
        let w = words(2);
        assert_eq!(admitted(&mut t, &mut s, 10), w[0]);
        assert!(matches!(
            t.admit(10, false, &mut s),
            Admission::Process { key: None, .. }
        ));
        assert_eq!(t.highest_id_received(), 10);
        assert_eq!(t.nak_count(), 0);
        assert_eq!(s.keys_parked, 0);
        assert_eq!(admitted(&mut t, &mut s, 11), w[1]);
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.SanityWindow_HighestPlus0x7FFF_Accepted_OnePastIt_Dropped`.
    #[test]
    fn sanity_window_boundary() {
        let (mut accepted, mut s) = tracker(100);
        assert!(matches!(
            accepted.admit(100 + 0x7FFF, true, &mut s),
            Admission::Process { .. }
        ));
        assert_eq!(accepted.highest_id_received(), 100 + 0x7FFF);

        let (mut dropped, mut s) = tracker(100);
        let w = words(1);
        assert_eq!(dropped.admit(100 + 0x8000, true, &mut s), Admission::Drop);
        assert_eq!(s.inbound_sanity_drops, 1);
        assert_eq!(dropped.highest_id_received(), 100);
        assert_eq!(dropped.nak_count(), 0);
        assert_eq!(admitted(&mut dropped, &mut s, 101), w[0]);
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.SanityWindow_IsWrapSafe`.
    #[test]
    fn sanity_window_is_wrap_safe() {
        let (mut t, mut s) = tracker(0xFFFF_FF00);
        assert_eq!(
            t.admit(0xFFFF_FF00u32.wrapping_add(0x8000), true, &mut s),
            Admission::Drop
        );
        assert_eq!(s.inbound_sanity_drops, 1);
        assert!(matches!(
            t.admit(3, true, &mut s),
            Admission::Process { .. }
        ));
        assert_eq!(t.highest_id_received(), 3);
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.GapWalk_SkipsSequenceZero_AcrossTheWrap`.
    #[test]
    fn gap_walk_skips_zero_across_the_wrap() {
        let (mut t, mut s) = tracker(0xFFFF_FFFE);
        let w = words(3); // parked for MAX, parked for 1, own for 2
        assert_eq!(admitted(&mut t, &mut s, 2), w[2]);
        assert_eq!(t.nak_count(), 2);
        assert_eq!(t.nak_ids_ascending(usize::MAX), vec![1, 0xFFFF_FFFF]);
        assert_eq!(admitted(&mut t, &mut s, 0xFFFF_FFFF), w[0]);
        assert_eq!(admitted(&mut t, &mut s, 1), w[1]);
        assert_eq!(t.nak_count(), 0);
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.RejectRetransmit_RemovesIds_AndTheStreamStaysAligned`.
    #[test]
    fn reject_retransmit_abandons_ids_and_stays_aligned() {
        let (mut t, mut s) = tracker(9);
        let w = words(5);
        assert_eq!(admitted(&mut t, &mut s, 10), w[0]);
        assert_eq!(admitted(&mut t, &mut s, 13), w[3]);
        assert_eq!(t.nak_count(), 2);
        let bytes = ids(&[11, 12]);
        t.on_reject_retransmit(SequenceList::from_raw(&bytes));
        assert_eq!(t.nak_count(), 0);
        assert_eq!(admitted(&mut t, &mut s, 14), w[4]);
        assert_eq!(t.admit(11, true, &mut s), Admission::Drop);
        assert_eq!(s.inbound_dups_dropped, 1);
    }

    // Source: OpenAC tests `NakEmissionTests.RejectInOrder_OwnMisparkedWord_FeedsTheNextFreshDraw`.
    #[test]
    fn reject_in_order_feeds_the_next_fresh_draw() {
        let (mut t, mut s) = tracker(9);
        let w = words(3);
        assert!(matches!(
            t.admit(10, false, &mut s),
            Admission::Process { .. }
        ));
        assert_eq!(t.nak_count(), 1);
        t.on_cleartext_reject_sequence(10, &mut s);
        assert_eq!(t.nak_count(), 0);
        assert_eq!(t.reclaimed_word_count(), 1);
        assert_eq!(admitted(&mut t, &mut s, 11), w[0]);
        assert_eq!(t.reclaimed_word_count(), 0);
        assert_eq!(admitted(&mut t, &mut s, 12), w[1]);
        assert_eq!(admitted(&mut t, &mut s, 13), w[2]);
    }

    // Source: OpenAC tests `NakEmissionTests.RejectAfterHigherArrival_BubbleRealignsTheParkedChain`.
    #[test]
    fn reject_after_a_higher_arrival_realigns_the_chain() {
        let (mut t, mut s) = tracker(9);
        let w = words(3);
        let Admission::Process {
            key: Some(key),
            draw_order,
        } = t.admit(11, true, &mut s)
        else {
            panic!("not admitted");
        };
        assert_eq!(key, w[1]);
        t.repark(11, key, draw_order, &mut s);
        assert_eq!(t.nak_count(), 2);
        assert!(matches!(
            t.admit(10, false, &mut s),
            Admission::Process { .. }
        ));
        t.on_cleartext_reject_sequence(10, &mut s);
        assert_eq!(t.nak_count(), 1);
        assert_eq!(t.reclaimed_word_count(), 1);
        assert_eq!(admitted(&mut t, &mut s, 11), w[0]);
        assert_eq!(admitted(&mut t, &mut s, 12), w[1]);
        assert_eq!(t.reclaimed_word_count(), 0);
        assert_eq!(admitted(&mut t, &mut s, 13), w[2]);
    }

    // Source: OpenAC tests `NakEmissionTests.RejectBodyIds_StayDiscarded_OnlyTheOwnSequenceReclaims`.
    #[test]
    fn reject_body_ids_are_discarded_only_the_own_sequence_reclaims() {
        let (mut t, mut s) = tracker(9);
        let w = words(5);
        assert_eq!(admitted(&mut t, &mut s, 12), w[2]);
        assert_eq!(t.nak_count(), 2);
        assert!(matches!(
            t.admit(13, false, &mut s),
            Admission::Process { .. }
        ));
        let bytes = ids(&[10, 11]);
        t.on_reject_retransmit(SequenceList::from_raw(&bytes));
        t.on_cleartext_reject_sequence(13, &mut s);
        assert_eq!(t.nak_count(), 0);
        assert_eq!(t.reclaimed_word_count(), 1);
        assert_eq!(admitted(&mut t, &mut s, 14), w[3]);
        assert_eq!(admitted(&mut t, &mut s, 15), w[4]);
        assert_eq!(t.reclaimed_word_count(), 0);
    }

    // Source: OpenAC tests `NakEmissionTests.TwoInterleavedRejects_InOrder_PoolPreservesAlignment`.
    #[test]
    fn two_rejects_in_order_preserve_alignment() {
        let (mut t, mut s) = tracker(9);
        let w = words(2);
        assert!(matches!(
            t.admit(10, false, &mut s),
            Admission::Process { .. }
        ));
        t.on_cleartext_reject_sequence(10, &mut s);
        assert!(matches!(
            t.admit(11, false, &mut s),
            Admission::Process { .. }
        ));
        t.on_cleartext_reject_sequence(11, &mut s);
        assert_eq!(t.nak_count(), 0);
        assert_eq!(t.reclaimed_word_count(), 1);
        assert_eq!(admitted(&mut t, &mut s, 12), w[0]);
        assert_eq!(admitted(&mut t, &mut s, 13), w[1]);
        assert_eq!(t.reclaimed_word_count(), 0);
    }

    // Source: OpenAC tests `NakEmissionTests.TwoInterleavedRejects_Crossed_PoolDrainsInDrawOrder`.
    #[test]
    fn two_crossed_rejects_drain_the_pool_in_draw_order() {
        let (mut t, mut s) = tracker(9);
        let w = words(5);
        for (sequence, expected) in [(12, w[2]), (13, w[3])] {
            let Admission::Process {
                key: Some(key),
                draw_order,
            } = t.admit(sequence, true, &mut s)
            else {
                panic!("not admitted");
            };
            assert_eq!(key, expected);
            t.repark(sequence, key, draw_order, &mut s);
        }
        assert_eq!(t.nak_count(), 4);
        assert!(matches!(
            t.admit(11, false, &mut s),
            Admission::Process { .. }
        ));
        t.on_cleartext_reject_sequence(11, &mut s);
        assert!(matches!(
            t.admit(10, false, &mut s),
            Admission::Process { .. }
        ));
        t.on_cleartext_reject_sequence(10, &mut s);
        assert_eq!(t.nak_count(), 2);
        assert_eq!(t.reclaimed_word_count(), 2);
        assert_eq!(admitted(&mut t, &mut s, 12), w[0]);
        assert_eq!(admitted(&mut t, &mut s, 13), w[1]);
        assert_eq!(t.nak_count(), 0);
        assert_eq!(admitted(&mut t, &mut s, 14), w[2]);
        assert_eq!(admitted(&mut t, &mut s, 15), w[3]);
        assert_eq!(t.reclaimed_word_count(), 0);
        assert_eq!(admitted(&mut t, &mut s, 16), w[4]);
    }
}
