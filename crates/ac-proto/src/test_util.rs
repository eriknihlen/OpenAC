//! Helpers shared by the unit tests.

use ac_bits::Writer;

/// Little-endian `u32` words as a body.
pub fn words(words: &[u32]) -> Vec<u8> {
    let mut w = Writer::new();
    for &word in words {
        w.u32(word);
    }
    w.into_vec()
}

/// Assert `parse` returns an error, not a panic, for every strict prefix of
/// `valid`, and succeeds on `valid` itself.
pub fn assert_every_prefix_fails<T, E: core::fmt::Debug>(
    valid: &[u8],
    parse: impl Fn(&[u8]) -> Result<T, E>,
) {
    for len in 0..valid.len() {
        assert!(
            parse(&valid[..len]).is_err(),
            "prefix of length {len} parsed"
        );
    }
    assert!(parse(valid).is_ok(), "full body failed to parse");
}

/// A builder that takes only the sequence number.
pub type BareBuilder = fn(u32) -> Vec<u8>;
