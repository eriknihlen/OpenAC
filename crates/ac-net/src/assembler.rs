//! Reassembly of game messages from fragments.

use std::collections::HashMap;
use std::time::Duration;

use crate::NetTime;
use crate::packet::Fragment;

/// A partial message is dropped this long after its last new fragment.
/// A floor, not a tuning knob: a slow but alive message must survive it.
pub const PARTIAL_TTL: Duration = Duration::from_secs(60);

/// How many completed multi-fragment message sequences are remembered so a
/// late duplicate fragment is dropped instead of starting a partial that
/// can never complete.
pub const COMPLETED_RING_SIZE: usize = 64;

/// A whole game message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InboundMessage {
    /// The queue from the fragment header (see [`crate::GameMessageGroup`]).
    pub queue: u16,
    /// The message body (starts with the 4-byte opcode).
    pub body: Vec<u8>,
}

impl InboundMessage {
    /// The queue as a known group, if it is one.
    #[must_use]
    pub fn group(&self) -> Option<crate::GameMessageGroup> {
        crate::GameMessageGroup::from_u16(self.queue)
    }

    /// The leading 32-bit opcode, if the body has one.
    #[must_use]
    pub fn opcode(&self) -> Option<u32> {
        let b = self.body.get(..4)?;
        Some(u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }
}

#[derive(Debug)]
struct PartialMessage {
    fragments: Vec<Option<Vec<u8>>>,
    queue: u16,
    received: usize,
    last_fragment_at: NetTime,
}

/// Collects fragments by message sequence and releases each message once
/// every index has arrived, in index order regardless of arrival order.
#[derive(Debug)]
pub struct FragmentAssembler {
    in_flight: HashMap<u32, PartialMessage>,
    completed: [u32; COMPLETED_RING_SIZE],
    completed_next: usize,
    completed_count: usize,
}

impl Default for FragmentAssembler {
    fn default() -> Self {
        Self::new()
    }
}

impl FragmentAssembler {
    /// An empty assembler.
    #[must_use]
    pub fn new() -> Self {
        Self {
            in_flight: HashMap::new(),
            completed: [0; COMPLETED_RING_SIZE],
            completed_next: 0,
            completed_count: 0,
        }
    }

    /// Messages waiting for more fragments.
    #[must_use]
    pub fn partial_count(&self) -> usize {
        self.in_flight.len()
    }

    /// Add one fragment; returns the message when it is complete.
    ///
    /// Repeating an index already held is harmless and does not refresh the
    /// partial's age. A fragment whose count or queue disagrees with the
    /// partial it would join is ignored.
    pub fn ingest(&mut self, now: NetTime, fragment: &Fragment<'_>) -> Option<InboundMessage> {
        let h = fragment.header;
        if h.count == 1 && h.index == 0 {
            return Some(InboundMessage {
                queue: h.queue,
                body: fragment.payload.to_vec(),
            });
        }

        let partial = match self.in_flight.get_mut(&h.sequence) {
            Some(partial) => {
                if partial.fragments.len() != usize::from(h.count) || partial.queue != h.queue {
                    return None;
                }
                partial
            }
            None => {
                if self.was_recently_completed(h.sequence) {
                    return None;
                }
                self.in_flight.entry(h.sequence).or_insert(PartialMessage {
                    fragments: vec![None; usize::from(h.count)],
                    queue: h.queue,
                    received: 0,
                    last_fragment_at: now,
                })
            }
        };

        let slot = partial.fragments.get_mut(usize::from(h.index))?;
        if slot.is_none() {
            *slot = Some(fragment.payload.to_vec());
            partial.received += 1;
            partial.last_fragment_at = now;
        }
        if partial.received < partial.fragments.len() {
            return None;
        }

        let partial = self.in_flight.remove(&h.sequence)?;
        self.remember_completed(h.sequence);
        let body = partial.fragments.into_iter().flatten().flatten().collect();
        Some(InboundMessage {
            queue: partial.queue,
            body,
        })
    }

    /// Drop partials whose last new fragment is more than [`PARTIAL_TTL`]
    /// old; returns how many were dropped.
    pub fn sweep_expired(&mut self, now: NetTime) -> usize {
        let before = self.in_flight.len();
        self.in_flight
            .retain(|_, partial| now.since(partial.last_fragment_at) <= PARTIAL_TTL);
        before - self.in_flight.len()
    }

    /// Forget every partial and the completed ring.
    pub fn drop_all(&mut self) {
        self.in_flight.clear();
        self.completed_next = 0;
        self.completed_count = 0;
    }

    fn was_recently_completed(&self, sequence: u32) -> bool {
        self.completed[..self.completed_count].contains(&sequence)
    }

    fn remember_completed(&mut self, sequence: u32) {
        self.completed[self.completed_next] = sequence;
        self.completed_next = (self.completed_next + 1) % COMPLETED_RING_SIZE;
        if self.completed_count < COMPLETED_RING_SIZE {
            self.completed_count += 1;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::packet::FragmentHeader;

    fn frag(sequence: u32, count: u16, index: u16, payload: &[u8], queue: u16) -> Fragment<'_> {
        Fragment {
            header: FragmentHeader {
                sequence,
                id: FragmentHeader::OUTBOUND_ID,
                count,
                total_size: (FragmentHeader::SIZE + payload.len()) as u16,
                index,
                queue,
            },
            payload,
        }
    }

    fn at(seconds: f64) -> NetTime {
        NetTime::from_duration(Duration::from_secs_f64(seconds))
    }

    // Source for this module's tests: OpenAC tests `Packets/FragmentAssemblerTests.cs`.

    #[test]
    fn single_fragment_releases_immediately() {
        let mut a = FragmentAssembler::new();
        let m = a
            .ingest(NetTime::ZERO, &frag(1, 1, 0, &[1, 2, 3], 42))
            .unwrap();
        assert_eq!(m.body, vec![1, 2, 3]);
        assert_eq!(m.queue, 42);
        assert_eq!(a.partial_count(), 0);
    }

    #[test]
    fn three_fragments_release_on_the_last_in_index_order() {
        let mut a = FragmentAssembler::new();
        let t = NetTime::ZERO;
        assert!(a.ingest(t, &frag(7, 3, 0, &[0xAA, 0xBB], 9)).is_none());
        assert_eq!(a.partial_count(), 1);
        assert!(a.ingest(t, &frag(7, 3, 1, &[0xCC, 0xDD], 9)).is_none());
        let m = a.ingest(t, &frag(7, 3, 2, &[0xEE], 9)).unwrap();
        assert_eq!(m.body, vec![0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        assert_eq!(m.queue, 9);
        assert_eq!(a.partial_count(), 0);

        // Out of order arrival still assembles by index.
        assert!(a.ingest(t, &frag(3, 3, 2, &[0xCC], 7)).is_none());
        assert!(a.ingest(t, &frag(3, 3, 0, &[0xAA], 7)).is_none());
        let m = a.ingest(t, &frag(3, 3, 1, &[0xBB], 7)).unwrap();
        assert_eq!(m.body, vec![0xAA, 0xBB, 0xCC]);
    }

    #[test]
    fn duplicates_missing_and_parallel_messages() {
        let mut a = FragmentAssembler::new();
        let t = NetTime::ZERO;
        assert!(a.ingest(t, &frag(5, 2, 0, &[0x11], 7)).is_none());
        assert!(a.ingest(t, &frag(5, 2, 0, &[0x11], 7)).is_none());
        assert_eq!(a.partial_count(), 1);
        assert_eq!(
            a.ingest(t, &frag(5, 2, 1, &[0x22], 7)).unwrap().body,
            vec![0x11, 0x22]
        );

        assert!(a.ingest(t, &frag(9, 3, 0, &[1], 7)).is_none());
        assert!(a.ingest(t, &frag(9, 3, 2, &[3], 7)).is_none());
        assert_eq!(a.partial_count(), 1);

        assert!(a.ingest(t, &frag(100, 2, 0, &[0xA1], 7)).is_none());
        assert!(a.ingest(t, &frag(200, 2, 0, &[0xB1], 7)).is_none());
        assert_eq!(a.partial_count(), 3);
        assert_eq!(
            a.ingest(t, &frag(100, 2, 1, &[0xA2], 7)).unwrap().body,
            vec![0xA1, 0xA2]
        );
        assert_eq!(
            a.ingest(t, &frag(200, 2, 1, &[0xB2], 7)).unwrap().body,
            vec![0xB1, 0xB2]
        );

        a.drop_all();
        assert_eq!(a.partial_count(), 0);
    }

    #[test]
    fn conflicting_identity_preserves_the_original_partial() {
        let mut a = FragmentAssembler::new();
        let t = NetTime::ZERO;
        assert!(a.ingest(t, &frag(30, 2, 0, &[1], 13)).is_none());
        assert!(a.ingest(t, &frag(30, 3, 1, &[9], 14)).is_none());
        assert_eq!(a.partial_count(), 1);
        let m = a.ingest(t, &frag(30, 2, 1, &[2], 13)).unwrap();
        assert_eq!((m.queue, m.body), (13, vec![1, 2]));
    }

    #[test]
    fn sweep_evicts_aged_partials_and_keeps_fresh_ones() {
        let mut a = FragmentAssembler::new();
        assert!(a.ingest(at(0.0), &frag(1, 2, 0, &[0xA1], 7)).is_none());
        assert!(a.ingest(at(30.0), &frag(2, 3, 0, &[0xB1], 7)).is_none());
        assert_eq!(a.sweep_expired(at(60.0)), 0);
        assert_eq!(a.partial_count(), 2);
        // Past the floor: the aged partial goes, the fresh one stays.
        assert_eq!(a.sweep_expired(at(61.0)), 1);
        assert_eq!(a.partial_count(), 1);
        assert!(a.ingest(at(61.0), &frag(2, 3, 1, &[0xB2], 7)).is_none());
        let m = a.ingest(at(61.0), &frag(2, 3, 2, &[0xB3], 7)).unwrap();
        assert_eq!(m.body, vec![0xB1, 0xB2, 0xB3]);
    }

    #[test]
    fn new_fragments_refresh_the_age_duplicates_do_not() {
        let mut a = FragmentAssembler::new();
        assert!(a.ingest(at(0.0), &frag(5, 3, 0, &[1], 7)).is_none());
        assert!(a.ingest(at(50.0), &frag(5, 3, 1, &[2], 7)).is_none());
        assert_eq!(a.sweep_expired(at(61.0)), 0);
        assert!(a.ingest(at(100.0), &frag(5, 3, 1, &[2], 7)).is_none());
        assert_eq!(a.sweep_expired(at(111.5)), 1);
        assert_eq!(a.partial_count(), 0);
    }

    #[test]
    fn late_duplicate_of_a_completed_message_is_dropped() {
        let mut a = FragmentAssembler::new();
        let t = NetTime::ZERO;
        assert!(a.ingest(t, &frag(9, 2, 0, &[1], 7)).is_none());
        assert!(a.ingest(t, &frag(9, 2, 1, &[2], 7)).is_some());
        assert!(a.ingest(t, &frag(9, 2, 0, &[1], 7)).is_none());
        assert_eq!(a.partial_count(), 0);
    }

    #[test]
    fn completed_ring_is_bounded() {
        let mut a = FragmentAssembler::new();
        let t = NetTime::ZERO;
        for seq in 0..=64 {
            assert!(a.ingest(t, &frag(seq, 2, 0, &[1], 7)).is_none());
            assert!(a.ingest(t, &frag(seq, 2, 1, &[2], 7)).is_some());
        }
        // Sequence 0 fell out of the 64-entry ring: its duplicate re-partials.
        assert!(a.ingest(t, &frag(0, 2, 0, &[1], 7)).is_none());
        assert_eq!(a.partial_count(), 1);
        assert!(a.ingest(t, &frag(64, 2, 0, &[1], 7)).is_none());
        assert_eq!(a.partial_count(), 1);
    }
}
