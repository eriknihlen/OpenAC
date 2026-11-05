//! A seeded packet-loss filter for proving loss recovery on a link that
//! never loses anything (loopback). Diagnostic only; off by default.

use crate::packet::{PacketFlags, PacketHeader};

/// Which direction loses packets.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum LossDirection {
    /// Only datagrams we send.
    Out,
    /// Only datagrams we receive.
    In,
    /// Both.
    #[default]
    Both,
}

/// Loss settings. A drop percentage of 0 means no filter at all.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LossOptions {
    /// Percent of datagrams dropped, 0–100.
    pub drop_percent: u8,
    /// Seed of the drop pattern (the same seed gives the same pattern).
    pub seed: u64,
    /// Which direction to drop.
    pub direction: LossDirection,
}

impl Default for LossOptions {
    fn default() -> Self {
        Self {
            drop_percent: 0,
            seed: 1,
            direction: LossDirection::Both,
        }
    }
}

/// The options were out of range.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
#[error("drop percentage {0} is not in 0..=100")]
pub struct InvalidDropPercent(pub u8);

/// What the filter has done so far.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct LossCounters {
    /// Outbound datagrams dropped.
    pub outbound_dropped: u64,
    /// Inbound datagrams dropped.
    pub inbound_dropped: u64,
    /// Outbound datagrams passed.
    pub outbound_forwarded: u64,
    /// Inbound datagrams passed.
    pub inbound_forwarded: u64,
}

/// SplitMix64: a small, well-mixed deterministic generator.
#[derive(Debug, Clone)]
struct SplitMix64(u64);

impl SplitMix64 {
    fn next_u64(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    /// Uniform in `0..bound`.
    fn below(&mut self, bound: u64) -> u64 {
        ((u128::from(self.next_u64()) * u128::from(bound)) >> 64) as u64
    }
}

/// Drops a seeded fraction of datagrams.
///
/// Nothing is dropped until the first keystream-sealed outbound datagram
/// has passed: the cleartext handshake always survives, so the filter
/// exercises the reliable transport rather than the login timeout.
#[derive(Debug, Clone)]
pub struct LossyFilter {
    drop_percent: u64,
    drop_outbound: bool,
    drop_inbound: bool,
    outbound_rng: SplitMix64,
    inbound_rng: SplitMix64,
    armed: bool,
    counters: LossCounters,
}

impl LossyFilter {
    /// A filter for `options`, or `None` at 0 % (no filter at all).
    pub fn new(options: LossOptions) -> Result<Option<Self>, InvalidDropPercent> {
        if options.drop_percent > 100 {
            return Err(InvalidDropPercent(options.drop_percent));
        }
        if options.drop_percent == 0 {
            return Ok(None);
        }
        Ok(Some(Self {
            drop_percent: u64::from(options.drop_percent),
            drop_outbound: matches!(options.direction, LossDirection::Out | LossDirection::Both),
            drop_inbound: matches!(options.direction, LossDirection::In | LossDirection::Both),
            outbound_rng: SplitMix64(options.seed),
            inbound_rng: SplitMix64(!options.seed),
            armed: false,
            counters: LossCounters::default(),
        }))
    }

    /// True once the first sealed outbound datagram has passed.
    #[must_use]
    pub fn is_armed(&self) -> bool {
        self.armed
    }

    /// What has been dropped and passed.
    #[must_use]
    pub fn counters(&self) -> LossCounters {
        self.counters
    }

    /// Whether to send `datagram` (`false` = drop it).
    pub fn pass_outbound(&mut self, datagram: &[u8]) -> bool {
        if self.armed && self.drop_outbound && self.outbound_rng.below(100) < self.drop_percent {
            self.counters.outbound_dropped += 1;
            return false;
        }
        self.counters.outbound_forwarded += 1;
        if !self.armed
            && datagram.len() > PacketHeader::SIZE
            && PacketHeader::from_bytes(datagram)
                .is_some_and(|h| h.flags.has(PacketFlags::ENCRYPTED_CHECKSUM))
        {
            self.armed = true;
        }
        true
    }

    /// Whether to deliver a received datagram (`false` = drop it).
    pub fn pass_inbound(&mut self) -> bool {
        if self.armed && self.drop_inbound && self.inbound_rng.below(100) < self.drop_percent {
            self.counters.inbound_dropped += 1;
            return false;
        }
        self.counters.inbound_forwarded += 1;
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::crypto::Isaac;
    use crate::packet::encode;

    fn sealed(sequence: u32) -> Vec<u8> {
        let header = PacketHeader {
            sequence,
            flags: PacketFlags::ACK_SEQUENCE | PacketFlags::ENCRYPTED_CHECKSUM,
            ..PacketHeader::default()
        };
        encode(header, &[0; 4], Some(&mut Isaac::new(1))).unwrap()
    }

    fn cleartext() -> Vec<u8> {
        let header = PacketHeader {
            flags: PacketFlags::ACK_SEQUENCE,
            ..PacketHeader::default()
        };
        encode(header, &[0; 4], None).unwrap()
    }

    fn filter(percent: u8, seed: u64, direction: LossDirection) -> LossyFilter {
        let mut f = LossyFilter::new(LossOptions {
            drop_percent: percent,
            seed,
            direction,
        })
        .unwrap()
        .unwrap();
        assert!(f.pass_outbound(&sealed(2)));
        assert!(f.is_armed());
        f
    }

    // Source for this module's tests: OpenAC tests `Transport/LossyTransportDecoratorTests.cs`.

    #[test]
    fn same_seed_same_pattern_different_seed_differs() {
        let pattern = |seed| {
            let mut f = filter(25, seed, LossDirection::Both);
            (0..400u32)
                .map(|i| f.pass_outbound(&sealed(i + 3)))
                .collect::<Vec<_>>()
        };
        let first = pattern(42);
        assert_eq!(first, pattern(42));
        assert_ne!(first, pattern(43));
        assert!(first.contains(&true) && first.contains(&false));
        let dropped = first.iter().filter(|&&p| !p).count();
        assert!((60..140).contains(&dropped), "{dropped} of 400 at 25%");
    }

    #[test]
    fn direction_masks() {
        let mut out = filter(100, 1, LossDirection::Out);
        assert!((0..20).all(|i| !out.pass_outbound(&sealed(i + 3))));
        assert!((0..20).all(|_| out.pass_inbound()));
        assert_eq!(out.counters().outbound_dropped, 20);
        assert_eq!(out.counters().inbound_dropped, 0);

        let mut inbound = filter(100, 1, LossDirection::In);
        assert!((0..20).all(|i| inbound.pass_outbound(&sealed(i + 3))));
        assert!((0..20).all(|_| !inbound.pass_inbound()));
        assert_eq!(inbound.counters().inbound_dropped, 20);
    }

    #[test]
    fn zero_percent_is_no_filter_and_over_100_is_an_error() {
        assert!(LossyFilter::new(LossOptions::default()).unwrap().is_none());
        assert_eq!(
            LossyFilter::new(LossOptions {
                drop_percent: 101,
                ..LossOptions::default()
            })
            .unwrap_err(),
            InvalidDropPercent(101)
        );
    }

    #[test]
    fn nothing_drops_until_the_first_sealed_outbound_passed() {
        let mut f = LossyFilter::new(LossOptions {
            drop_percent: 100,
            seed: 1,
            direction: LossDirection::Both,
        })
        .unwrap()
        .unwrap();
        for _ in 0..5 {
            assert!(f.pass_outbound(&cleartext()));
        }
        // A header-only datagram never arms, whatever its flags.
        let mut header_only = sealed(2);
        header_only.truncate(PacketHeader::SIZE);
        assert!(f.pass_outbound(&header_only));
        assert!(!f.is_armed());
        assert!(f.pass_inbound());
        assert!(f.pass_outbound(&sealed(2)));
        assert!(f.is_armed());
        assert!(!f.pass_outbound(&sealed(3)));
        assert!(!f.pass_outbound(&cleartext()));
        assert!(!f.pass_inbound());
        assert_eq!(f.counters().outbound_dropped, 2);
        assert_eq!(f.counters().inbound_dropped, 1);
    }
}
