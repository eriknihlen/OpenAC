//! Wrap-safe comparison of 32-bit sequence numbers.

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
}
