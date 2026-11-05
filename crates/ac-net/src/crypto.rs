//! The packet checksum hash and the ISAAC keystream that seals it.
//!
//! Every packet carries a 32-bit checksum built from [`hash32`] sums. When a
//! packet is "encrypted" the payload part of that checksum is XORed with the
//! next word of an ISAAC keystream both peers seeded during the handshake,
//! so the receiver can prove the packet came from its peer (nothing in the
//! body is actually encrypted).

/// The checksum hash: the length shifted into the top half, plus the data
/// summed as little-endian 32-bit words, plus the 0–3 tail bytes folded in
/// from the top byte down.
#[must_use]
pub fn hash32(data: &[u8]) -> u32 {
    // Lengths beyond 32 bits never occur on the wire; truncation matches the
    // 32-bit arithmetic of the format.
    let mut checksum = (data.len() as u32) << 16;
    let mut words = data.chunks_exact(4);
    for word in &mut words {
        checksum = checksum.wrapping_add(u32::from_le_bytes([word[0], word[1], word[2], word[3]]));
    }
    let mut shift = 24;
    for &byte in words.remainder() {
        checksum = checksum.wrapping_add(u32::from(byte) << shift);
        shift -= 8;
    }
    checksum
}

const STATE_SIZE: usize = 256;
const GOLDEN_RATIO: u32 = 0x9E37_79B9;

/// Bob Jenkins' ISAAC generator in the game's variant: seeded from one
/// 32-bit value, outputs consumed from the end of each 256-word batch.
#[derive(Clone)]
pub struct Isaac {
    mm: [u32; STATE_SIZE],
    rsl: [u32; STATE_SIZE],
    a: u32,
    b: u32,
    c: u32,
    offset: usize,
}

impl core::fmt::Debug for Isaac {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        // The state is 2 KiB of noise; the position is what matters.
        f.debug_struct("Isaac")
            .field("offset", &self.offset)
            .finish_non_exhaustive()
    }
}

fn mix(s: &mut [u32; 8]) {
    s[0] ^= s[1] << 11;
    s[3] = s[3].wrapping_add(s[0]);
    s[1] = s[1].wrapping_add(s[2]);
    s[1] ^= s[2] >> 2;
    s[4] = s[4].wrapping_add(s[1]);
    s[2] = s[2].wrapping_add(s[3]);
    s[2] ^= s[3] << 8;
    s[5] = s[5].wrapping_add(s[2]);
    s[3] = s[3].wrapping_add(s[4]);
    s[3] ^= s[4] >> 16;
    s[6] = s[6].wrapping_add(s[3]);
    s[4] = s[4].wrapping_add(s[5]);
    s[4] ^= s[5] << 10;
    s[7] = s[7].wrapping_add(s[4]);
    s[5] = s[5].wrapping_add(s[6]);
    s[5] ^= s[6] >> 4;
    s[0] = s[0].wrapping_add(s[5]);
    s[6] = s[6].wrapping_add(s[7]);
    s[6] ^= s[7] << 8;
    s[1] = s[1].wrapping_add(s[6]);
    s[7] = s[7].wrapping_add(s[0]);
    s[7] ^= s[0] >> 9;
    s[2] = s[2].wrapping_add(s[7]);
    s[0] = s[0].wrapping_add(s[1]);
}

impl Isaac {
    /// A keystream seeded with `seed` (the handshake carries it as four
    /// little-endian bytes).
    #[must_use]
    pub fn new(seed: u32) -> Self {
        let mut isaac = Self {
            mm: [0; STATE_SIZE],
            rsl: [0; STATE_SIZE],
            a: 0,
            b: 0,
            c: 0,
            offset: 0,
        };
        isaac.initialize();
        isaac.a = seed;
        isaac.b = seed;
        isaac.c = seed;
        isaac.scramble();
        isaac.offset = STATE_SIZE - 1;
        isaac
    }

    /// The next keystream word.
    pub fn next_word(&mut self) -> u32 {
        let value = self.rsl[self.offset];
        if self.offset > 0 {
            self.offset -= 1;
        } else {
            self.scramble();
            self.offset = STATE_SIZE - 1;
        }
        value
    }

    fn initialize(&mut self) {
        let mut s = [GOLDEN_RATIO; 8];
        for _ in 0..4 {
            mix(&mut s);
        }
        // First pass folds the (zero) result buffer into the state, the
        // second folds the state back into itself.
        for j in (0..STATE_SIZE).step_by(8) {
            for (word, &fold) in s.iter_mut().zip(&self.rsl[j..j + 8]) {
                *word = word.wrapping_add(fold);
            }
            mix(&mut s);
            self.mm[j..j + 8].copy_from_slice(&s);
        }
        for j in (0..STATE_SIZE).step_by(8) {
            for (word, &fold) in s.iter_mut().zip(&self.mm[j..j + 8]) {
                *word = word.wrapping_add(fold);
            }
            mix(&mut s);
            self.mm[j..j + 8].copy_from_slice(&s);
        }
    }

    fn scramble(&mut self) {
        self.c = self.c.wrapping_add(1);
        self.b = self.b.wrapping_add(self.c);
        for i in 0..STATE_SIZE {
            let x = self.mm[i];
            match i & 3 {
                0 => self.a ^= self.a << 13,
                1 => self.a ^= self.a >> 6,
                2 => self.a ^= self.a << 2,
                _ => self.a ^= self.a >> 16,
            }
            self.a = self.a.wrapping_add(self.mm[(i + 128) & 0xFF]);
            let y = self.mm[((x >> 2) & 0xFF) as usize]
                .wrapping_add(self.a)
                .wrapping_add(self.b);
            self.mm[i] = y;
            let next_b = self.mm[((y >> 10) & 0xFF) as usize].wrapping_add(x);
            self.rsl[i] = next_b;
            self.b = next_b;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Source: OpenAC tests `Cryptography/Hash32Tests.cs` (hand-computed values).
    #[test]
    fn hash32_golden_values() {
        assert_eq!(hash32(&[]), 0);
        assert_eq!(hash32(&[0x01, 0x02, 0x03, 0x04]), 0x0407_0201);
        assert_eq!(hash32(&[0xAA, 0xBB, 0xCC, 0xDD, 0xEE]), 0xCBD1_BBAA);
        assert_eq!(hash32(&[0x42]), 0x4201_0000);
        assert_eq!(hash32(&[0x42, 0x7F]), 0x4281_0000);
        assert_eq!(hash32(&[0x01, 0x02, 0x03]), 0x0105_0300);
    }

    // Source: OpenAC tests `Cryptography/IsaacRandomTests.cs`, seed 0x12345678
    // golden vectors (originally the reference server's own ISAAC vectors).
    #[test]
    fn isaac_golden_keystream_seed_12345678() {
        const GOLDEN: [u32; 16] = [
            0xFDD4_AFEB,
            0x3983_11D2,
            0xC718_29A4,
            0xB19D_F96A,
            0x7ED4_A1E7,
            0xB718_A446,
            0x5FA7_7DF1,
            0x5CB7_93D2,
            0x52C9_5BAC,
            0xA8F8_FD7E,
            0x1858_2C04,
            0xD257_F506,
            0x4D11_A3F0,
            0x919D_E7A7,
            0x1D80_9C6B,
            0xE97F_65F8,
        ];
        let mut isaac = Isaac::new(u32::from_le_bytes([0x78, 0x56, 0x34, 0x12]));
        for expected in GOLDEN {
            assert_eq!(isaac.next_word(), expected);
        }
    }

    // Source: OpenAC tests `IsaacRandomTests` (same-seed / different-seed / two batches).
    #[test]
    fn isaac_is_deterministic_and_spans_batches() {
        let mut a = Isaac::new(0xDDCC_BBAA);
        let mut b = Isaac::new(0xDDCC_BBAA);
        for _ in 0..1000 {
            assert_eq!(a.next_word(), b.next_word());
        }
        assert_ne!(Isaac::new(1).next_word(), Isaac::new(2).next_word());

        let mut isaac = Isaac::new(0x0403_0201);
        let mut outputs: Vec<u32> = (0..512).map(|_| isaac.next_word()).collect();
        outputs.sort_unstable();
        outputs.dedup();
        assert!(outputs.len() > 400, "{} distinct of 512", outputs.len());
    }
}
