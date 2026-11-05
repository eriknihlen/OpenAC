//! A minimal PNG writer: 8-bit RGBA, no filtering, zlib "stored" (raw)
//! deflate blocks. Files are larger than a compressing writer's but any
//! viewer reads them.

/// Encode `width * height` RGBA pixels (row by row, top first) as a PNG.
#[must_use]
pub fn encode_rgba(width: u32, height: u32, rgba: &[u8]) -> Vec<u8> {
    let row = width as usize * 4;
    // Every scanline starts with its filter type, 0 (none).
    let mut raw = Vec::with_capacity((row + 1) * height as usize);
    for line in rgba.chunks_exact(row.max(1)).take(height as usize) {
        raw.push(0);
        raw.extend_from_slice(line);
    }

    let mut png = b"\x89PNG\r\n\x1a\n".to_vec();
    let mut ihdr = Vec::with_capacity(13);
    ihdr.extend_from_slice(&width.to_be_bytes());
    ihdr.extend_from_slice(&height.to_be_bytes());
    // Bit depth 8, colour type 6 (RGBA), deflate, adaptive filtering, no
    // interlace.
    ihdr.extend_from_slice(&[8, 6, 0, 0, 0]);
    chunk(&mut png, b"IHDR", &ihdr);
    chunk(&mut png, b"IDAT", &zlib_stored(&raw));
    chunk(&mut png, b"IEND", &[]);
    png
}

/// Append a chunk: length, type, data, CRC of type and data.
fn chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    let len = u32::try_from(data.len()).unwrap_or(u32::MAX);
    out.extend_from_slice(&len.to_be_bytes());
    out.extend_from_slice(kind);
    out.extend_from_slice(data);
    let mut crc = Crc32::new();
    crc.update(kind);
    crc.update(data);
    out.extend_from_slice(&crc.finish().to_be_bytes());
}

/// A zlib stream of stored deflate blocks (at most 65 535 bytes each).
fn zlib_stored(data: &[u8]) -> Vec<u8> {
    // CMF 0x78 (deflate, 32 KiB window), FLG 0x01 (check bits, no
    // dictionary, fastest level): 0x7801 is a multiple of 31.
    let mut out = vec![0x78, 0x01];
    let mut blocks = data.chunks(0xFFFF).peekable();
    if blocks.peek().is_none() {
        // One empty final block.
        out.extend_from_slice(&[1, 0, 0, 0xFF, 0xFF]);
    }
    while let Some(block) = blocks.next() {
        let last = u8::from(blocks.peek().is_none());
        let len = block.len() as u16;
        out.push(last);
        out.extend_from_slice(&len.to_le_bytes());
        out.extend_from_slice(&(!len).to_le_bytes());
        out.extend_from_slice(block);
    }
    out.extend_from_slice(&adler32(data).to_be_bytes());
    out
}

/// Adler-32 checksum (zlib's trailer).
fn adler32(data: &[u8]) -> u32 {
    const MOD: u32 = 65_521;
    let (mut a, mut b) = (1u32, 0u32);
    // 5552 bytes is the longest run before the sums can overflow a u32.
    for run in data.chunks(5552) {
        for &x in run {
            a += u32::from(x);
            b += a;
        }
        a %= MOD;
        b %= MOD;
    }
    (b << 16) | a
}

/// CRC-32 (ISO-HDLC, the one PNG uses).
struct Crc32 {
    table: [u32; 256],
    value: u32,
}

impl Crc32 {
    fn new() -> Self {
        let mut table = [0u32; 256];
        for (n, slot) in table.iter_mut().enumerate() {
            let mut c = n as u32;
            for _ in 0..8 {
                c = if c & 1 != 0 {
                    0xEDB8_8320 ^ (c >> 1)
                } else {
                    c >> 1
                };
            }
            *slot = c;
        }
        Self {
            table,
            value: 0xFFFF_FFFF,
        }
    }

    fn update(&mut self, data: &[u8]) {
        for &b in data {
            self.value =
                self.table[((self.value ^ u32::from(b)) & 0xFF) as usize] ^ (self.value >> 8);
        }
    }

    fn finish(&self) -> u32 {
        self.value ^ 0xFFFF_FFFF
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crc_of_known_inputs() {
        let mut c = Crc32::new();
        c.update(b"IEND");
        assert_eq!(c.finish(), 0xAE42_6082);
        let mut c = Crc32::new();
        c.update(b"123456789");
        assert_eq!(c.finish(), 0xCBF4_3926);
    }

    #[test]
    fn adler_of_known_inputs() {
        assert_eq!(adler32(b"Wikipedia"), 0x11E6_0398);
        assert_eq!(adler32(b""), 1);
        // Long input exercises the modulo runs.
        let big = vec![0xFFu8; 100_000];
        let (mut a, mut b) = (1u64, 0u64);
        for &x in &big {
            a = (a + u64::from(x)) % 65_521;
            b = (b + a) % 65_521;
        }
        assert_eq!(adler32(&big), ((b << 16) | a) as u32);
    }

    #[test]
    fn stored_blocks_split_at_65535() {
        let data = vec![7u8; 70_000];
        let z = zlib_stored(&data);
        assert_eq!(&z[..2], &[0x78, 0x01]);
        // First block: not final, 65535 bytes.
        assert_eq!(&z[2..7], &[0, 0xFF, 0xFF, 0, 0]);
        // Second block header after it: final, 4465 bytes.
        let second = 7 + 65_535;
        assert_eq!(z[second], 1);
        assert_eq!(u16::from_le_bytes([z[second + 1], z[second + 2]]), 4465);
        assert_eq!(z.len(), 2 + 5 + 65_535 + 5 + 4465 + 4);
    }

    #[test]
    fn png_layout() {
        let png = encode_rgba(2, 1, &[1, 2, 3, 4, 5, 6, 7, 8]);
        assert_eq!(&png[..8], b"\x89PNG\r\n\x1a\n");
        assert_eq!(&png[8..16], &[0, 0, 0, 13, b'I', b'H', b'D', b'R']);
        assert_eq!(&png[16..24], &[0, 0, 0, 2, 0, 0, 0, 1]);
        assert_eq!(&png[24..29], &[8, 6, 0, 0, 0]);
        assert_eq!(
            &png[png.len() - 12..],
            &[0, 0, 0, 0, b'I', b'E', b'N', b'D', 0xAE, 0x42, 0x60, 0x82]
        );
        // IDAT holds the filter byte then the pixels, uncompressed.
        let idat = 8 + 25;
        let len = u32::from_be_bytes(png[idat..idat + 4].try_into().unwrap()) as usize;
        let z = &png[idat + 8..idat + 8 + len];
        assert_eq!(&z[2..7], &[1, 9, 0, !9u8, 0xFF]);
        assert_eq!(&z[7..16], &[0, 1, 2, 3, 4, 5, 6, 7, 8]);
    }
}
