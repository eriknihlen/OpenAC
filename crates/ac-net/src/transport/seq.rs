//! Wrap-safe comparison of sequence numbers and 16-bit stamps.

use std::cmp::Ordering;

/// True when `a` is strictly newer than `b`, treating the 32-bit space as a
/// circle (anything up to half the space ahead counts as newer).
#[must_use]
pub fn is_newer(a: u32, b: u32) -> bool {
    // Reinterpreting the difference as signed is the wrap-safe comparison.
    (a.wrapping_sub(b) as i32) > 0
}

/// The newer of two sequence numbers (either when equal).
#[must_use]
pub fn newest(a: u32, b: u32) -> u32 {
    if is_newer(a, b) { a } else { b }
}

/// How 16-bit stamp `a` compares with `b` on the 16-bit circle: the plain
/// order when they are at most 0x7FFF apart, the reverse when further.
/// Exactly half the space apart, the larger counts as the older (unlike a
/// signed difference, which is not symmetric there).
#[must_use]
pub fn compare_stamps(a: u16, b: u16) -> Ordering {
    if a == b {
        return Ordering::Equal;
    }
    let (distance, order) = if a < b {
        (b - a, Ordering::Less)
    } else {
        (a - b, Ordering::Greater)
    };
    if distance <= 0x7FFF {
        order
    } else {
        order.reverse()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Source: OpenAC tests `OutboundReliableTransportTests.SequenceMath_IsNewer_IsWrapSafe`.
    #[test]
    fn is_newer_is_wrap_safe() {
        assert!(is_newer(2, 1));
        assert!(!is_newer(1, 2));
        assert!(!is_newer(7, 7));
        assert!(is_newer(1, u32::MAX));
        assert!(!is_newer(u32::MAX, 1));
        assert_eq!(newest(5, 3), 5);
        assert_eq!(newest(3, 5), 5);
        assert_eq!(newest(0xFFFF_FFF6, 5), 5);
    }

    // The game client's 16-bit stamp comparison (connection iterations).
    #[test]
    fn compare_stamps_wraps_and_is_symmetric_at_half_the_space() {
        assert_eq!(compare_stamps(7, 7), Ordering::Equal);
        assert_eq!(compare_stamps(8, 7), Ordering::Greater);
        assert_eq!(compare_stamps(6, 7), Ordering::Less);
        assert_eq!(compare_stamps(0, 0xFFFF), Ordering::Greater);
        assert_eq!(compare_stamps(0xFFFF, 0), Ordering::Less);
        assert_eq!(compare_stamps(0x8006, 7), Ordering::Greater);
        // Exactly 0x8000 apart: the larger is older, the smaller newer.
        assert_eq!(compare_stamps(0x8007, 7), Ordering::Less);
        assert_eq!(compare_stamps(7, 0x8007), Ordering::Greater);
    }
}
