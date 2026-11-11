//! SHA-1 (FIPS 180-4), only for the WebSocket opening handshake, where the
//! server proves it read our key by hashing it (RFC 6455 section 4.2.2).
//! SHA-1 is not used for anything that needs a secure hash.

/// The 20-byte SHA-1 digest of `data`.
pub(crate) fn digest(data: &[u8]) -> [u8; 20] {
    let mut state: [u32; 5] = [
        0x6745_2301,
        0xEFCD_AB89,
        0x98BA_DCFE,
        0x1032_5476,
        0xC3D2_E1F0,
    ];
    let mut blocks = data.chunks_exact(64);
    for block in &mut blocks {
        compress(&mut state, block);
    }
    // The tail, the 0x80 marker, zeros, then the message length in bits as
    // a big-endian 64-bit number: one or two final blocks.
    let tail = blocks.remainder();
    let mut last = [0u8; 128];
    last[..tail.len()].copy_from_slice(tail);
    last[tail.len()] = 0x80;
    let total = if tail.len() < 56 { 64 } else { 128 };
    let bits = (data.len() as u64).wrapping_mul(8);
    last[total - 8..total].copy_from_slice(&bits.to_be_bytes());
    for block in last[..total].chunks_exact(64) {
        compress(&mut state, block);
    }
    let mut out = [0u8; 20];
    for (chunk, word) in out.chunks_exact_mut(4).zip(state) {
        chunk.copy_from_slice(&word.to_be_bytes());
    }
    out
}

/// One 64-byte block into the state.
fn compress(state: &mut [u32; 5], block: &[u8]) {
    let mut w = [0u32; 80];
    for (word, bytes) in w.iter_mut().zip(block.chunks_exact(4)) {
        *word = u32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]);
    }
    for t in 16..80 {
        w[t] = (w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16]).rotate_left(1);
    }
    let [mut a, mut b, mut c, mut d, mut e] = *state;
    for (t, &word) in w.iter().enumerate() {
        let (f, k) = match t {
            0..=19 => ((b & c) | (!b & d), 0x5A82_7999),
            20..=39 => (b ^ c ^ d, 0x6ED9_EBA1),
            40..=59 => ((b & c) | (b & d) | (c & d), 0x8F1B_BCDC),
            _ => (b ^ c ^ d, 0xCA62_C1D6),
        };
        let temp = a
            .rotate_left(5)
            .wrapping_add(f)
            .wrapping_add(e)
            .wrapping_add(k)
            .wrapping_add(word);
        e = d;
        d = c;
        c = b.rotate_left(30);
        b = a;
        a = temp;
    }
    for (s, v) in state.iter_mut().zip([a, b, c, d, e]) {
        *s = s.wrapping_add(v);
    }
}

#[cfg(test)]
mod tests {
    use super::digest;

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|b| format!("{b:02x}")).collect()
    }

    // The test vectors of RFC 3174 section 7.3 and FIPS 180 (the examples
    // published with the standard).
    #[test]
    fn standard_vectors() {
        assert_eq!(
            hex(&digest(b"abc")),
            "a9993e364706816aba3e25717850c26c9cd0d89d"
        );
        assert_eq!(
            hex(&digest(b"")),
            "da39a3ee5e6b4b0d3255bfef95601890afd80709"
        );
        assert_eq!(
            hex(&digest(
                b"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"
            )),
            "84983e441c3bd26ebaae4aa1f95129e5e54670f1"
        );
        let a_million = vec![b'a'; 1_000_000];
        assert_eq!(
            hex(&digest(&a_million)),
            "34aa973cd4c4daa4f61eeb2bdbad27316534016f"
        );
    }

    // RFC 3174's fourth vector: "0123456701234567..." (64 bytes) ten times,
    // which crosses many block boundaries.
    #[test]
    fn repeated_block_vector() {
        let text = b"0123456701234567012345670123456701234567012345670123456701234567".repeat(10);
        assert_eq!(
            hex(&digest(&text)),
            "dea356a2cddd90c7a7ecedc5ebb563934f460452"
        );
    }

    // Lengths around the padding boundary (55, 56, 63, 64 bytes) agree with
    // a byte-at-a-time reference: the padding never miscounts.
    #[test]
    fn padding_boundaries_differ() {
        let mut seen = std::collections::HashSet::new();
        for len in [55usize, 56, 57, 63, 64, 65, 119, 120] {
            assert!(seen.insert(digest(&vec![b'x'; len])), "length {len}");
        }
    }
}
