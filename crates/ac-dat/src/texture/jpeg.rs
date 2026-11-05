//! JPEG decoding (baseline and progressive, 8-bit, 1, 3 or 4 components)
//! to RGBA.
//!
//! The arithmetic is fixed-point and must stay bit-exact with the decoder
//! the golden values come from: the same integer inverse DCT, the same
//! chroma upsampling (a 3:1 weighting of the nearer and farther source
//! pixel) and the same colour conversion. Any change to rounding changes
//! pixels.
//!
//! Inside a scan the decoder is tolerant: fill bytes before a marker are
//! skipped, and once a marker ends the entropy data, missing bits read as
//! zero. A file that ends before its end-of-image marker, or that has an
//! unreadable segment after the image data, is an error rather than a
//! partial image.

use std::num::Wrapping;

/// Errors from decoding a JPEG file.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum JpegError {
    /// The data does not start with a start-of-image marker.
    #[error("not a JPEG file")]
    NotJpeg,
    /// The file is malformed; the text names what was wrong.
    #[error("corrupt JPEG: {0}")]
    Corrupt(&'static str),
    /// The file ended before its end-of-image marker.
    #[error("JPEG data ends early")]
    Truncated,
    /// The image is too large to decode.
    #[error("JPEG image is too large")]
    TooLarge,
}

type Result<T> = core::result::Result<T, JpegError>;

/// A decoded image: `width * height` pixels of red, green, blue, alpha.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Image {
    /// Width in pixels.
    pub width: u32,
    /// Height in pixels.
    pub height: u32,
    /// Pixels, row by row, four bytes each.
    pub rgba: Vec<u8>,
}

/// No marker pending.
const MARKER_NONE: u8 = 0xFF;

/// Natural-order index of each zig-zag position, padded so a run that
/// overshoots position 63 stays in bounds.
const DEZIGZAG: [u8; 79] = [
    0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20,
    13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59,
    52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    63, 63, 63, 63, 63,
];

const BMASK: [u32; 17] = [
    0, 1, 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535,
];

/// Value of a negative coefficient with `n` magnitude bits, less the bits.
const JBIAS: [i32; 16] = [
    0, -1, -3, -7, -15, -31, -63, -127, -255, -511, -1023, -2047, -4095, -8191, -16383, -32767,
];

/// Lookahead width of the fast Huffman tables.
const FAST_BITS: u32 = 9;

/// A Huffman table with a 9-bit fast lookup.
#[derive(Clone)]
struct Huffman {
    fast: [u8; 512],
    code: [u16; 256],
    values: [u8; 256],
    size: [u8; 257],
    maxcode: [u32; 18],
    delta: [i32; 17],
}

impl Huffman {
    fn new() -> Self {
        Self {
            fast: [0; 512],
            code: [0; 256],
            values: [0; 256],
            size: [0; 257],
            maxcode: [0; 18],
            delta: [0; 17],
        }
    }

    /// Build the code tables from the number of codes of each length.
    fn build(&mut self, count: &[usize; 16]) -> Result<()> {
        let mut k = 0usize;
        for (i, &n) in count.iter().enumerate() {
            for _ in 0..n {
                let slot = self
                    .size
                    .get_mut(k)
                    .ok_or(JpegError::Corrupt("bad size list"))?;
                *slot = (i + 1) as u8;
                k += 1;
                if k >= 257 {
                    return Err(JpegError::Corrupt("bad size list"));
                }
            }
        }
        self.size[k] = 0;
        let mut code = 0u32;
        k = 0;
        for j in 1..=16usize {
            self.delta[j] = (k as i64 - i64::from(code)) as i32;
            if usize::from(self.size[k]) == j {
                while usize::from(self.size[k]) == j {
                    self.code[k] = code as u16;
                    code += 1;
                    k += 1;
                }
                if code > 1 << j {
                    return Err(JpegError::Corrupt("bad code lengths"));
                }
            }
            self.maxcode[j] = code << (16 - j);
            code <<= 1;
        }
        self.maxcode[17] = u32::MAX;

        self.fast = [255; 512];
        for i in 0..k {
            let s = u32::from(self.size[i]);
            if s <= FAST_BITS {
                let c = usize::from(self.code[i]) << (FAST_BITS - s);
                let m = 1usize << (FAST_BITS - s);
                for j in 0..m {
                    if let Some(slot) = self.fast.get_mut(c + j) {
                        *slot = i as u8;
                    }
                }
            }
        }
        Ok(())
    }

    /// Precombined run, size and value for short AC codes whose value fits
    /// in the 9-bit lookahead; 0 where the slow path is needed.
    fn fast_ac(&self) -> [i16; 512] {
        let mut out = [0i16; 512];
        for (i, slot) in out.iter_mut().enumerate() {
            let fast = self.fast[i];
            if fast == 255 {
                continue;
            }
            let rs = i32::from(self.values[usize::from(fast)]);
            let run = (rs >> 4) & 15;
            let magbits = rs & 15;
            let len = i32::from(self.size[usize::from(fast)]);
            if magbits != 0 && len + magbits <= FAST_BITS as i32 {
                let mut k = ((i as i32) << len & 511) >> (FAST_BITS as i32 - magbits);
                let m = 1 << (magbits - 1);
                if k < m {
                    k += (-1 << magbits) + 1;
                }
                if (-128..=127).contains(&k) {
                    *slot = (k * 256 + run * 16 + len + magbits) as i16;
                }
            }
        }
        out
    }
}

/// The byte stream and the entropy decoder's state.
struct Stream<'a> {
    src: &'a [u8],
    pos: usize,
    /// Set when a read went past the end of the data.
    overrun: bool,
    code_buffer: u32,
    code_bits: i32,
    marker: u8,
    nomore: bool,
    todo: i32,
    eob_run: i32,
    spec_start: i32,
    spec_end: i32,
    succ_high: i32,
    succ_low: i32,
}

impl Stream<'_> {
    fn get8(&mut self) -> u8 {
        match self.src.get(self.pos) {
            Some(&b) => {
                self.pos += 1;
                b
            }
            None => {
                self.overrun = true;
                0
            }
        }
    }

    fn get16be(&mut self) -> i32 {
        let hi = i32::from(self.get8());
        (hi << 8) + i32::from(self.get8())
    }

    fn skip(&mut self, n: i32) {
        let n = usize::try_from(n).unwrap_or(0);
        match self.pos.checked_add(n) {
            Some(end) if end <= self.src.len() => self.pos = end,
            _ => {
                self.overrun = true;
                self.pos = self.src.len();
            }
        }
    }

    fn at_eof(&self) -> bool {
        self.pos >= self.src.len()
    }

    /// The next marker: a pending one, else the byte after a run of 0xFF
    /// fill bytes; [`MARKER_NONE`] when the next byte is not 0xFF.
    fn get_marker(&mut self) -> u8 {
        if self.marker != MARKER_NONE {
            let m = self.marker;
            self.marker = MARKER_NONE;
            return m;
        }
        let mut x = self.get8();
        if x != 0xFF {
            return MARKER_NONE;
        }
        while x == 0xFF {
            x = self.get8();
        }
        x
    }

    /// Fill the bit buffer to more than 24 bits. A marker stops the fill
    /// (and the entropy data); after it, zero bits are supplied.
    fn grow_buffer(&mut self) {
        loop {
            let b = if self.nomore {
                0
            } else {
                u32::from(self.get8())
            };
            if b == 0xFF {
                let mut c = self.get8();
                while c == 0xFF {
                    c = self.get8();
                }
                if c != 0 {
                    self.marker = c;
                    self.nomore = true;
                    return;
                }
            }
            self.code_buffer |= b.wrapping_shl((24 - self.code_bits) as u32);
            self.code_bits += 8;
            if self.code_bits > 24 {
                return;
            }
        }
    }

    /// Decode one Huffman symbol; -1 for an invalid code.
    fn huff_decode(&mut self, h: &Huffman) -> i32 {
        if self.code_bits < 16 {
            self.grow_buffer();
        }
        let c = ((self.code_buffer >> (32 - FAST_BITS)) & 511) as usize;
        let k = h.fast[c];
        if k < 255 {
            let s = i32::from(h.size[usize::from(k)]);
            if s > self.code_bits {
                return -1;
            }
            self.code_buffer <<= s;
            self.code_bits -= s;
            return i32::from(h.values[usize::from(k)]);
        }
        let temp = self.code_buffer >> 16;
        let mut k = FAST_BITS as usize + 1;
        while k < 17 && temp >= h.maxcode[k] {
            k += 1;
        }
        if k == 17 {
            self.code_bits -= 16;
            return -1;
        }
        if k as i32 > self.code_bits {
            return -1;
        }
        let c = i64::from((self.code_buffer >> (32 - k)) & BMASK[k]) + i64::from(h.delta[k]);
        if !(0..256).contains(&c) {
            return -1;
        }
        self.code_bits -= k as i32;
        self.code_buffer <<= k;
        i32::from(h.values[c as usize])
    }

    /// Read `n` bits as a signed coefficient (JPEG's "extend").
    fn extend_receive(&mut self, n: i32) -> i32 {
        if self.code_bits < n {
            self.grow_buffer();
        }
        if self.code_bits < n {
            return 0;
        }
        let n = n as usize;
        let sgn = (self.code_buffer >> 31) as i32;
        let k = self.code_buffer.rotate_left(n as u32);
        self.code_buffer = k & !BMASK[n];
        let k = k & BMASK[n];
        self.code_bits -= n as i32;
        (i64::from(k) + i64::from(JBIAS[n] & (sgn - 1))) as i32
    }

    fn get_bits(&mut self, n: i32) -> i32 {
        if self.code_bits < n {
            self.grow_buffer();
        }
        if self.code_bits < n {
            return 0;
        }
        let n = n as usize;
        let k = self.code_buffer.rotate_left(n as u32);
        self.code_buffer = k & !BMASK[n];
        self.code_bits -= n as i32;
        (k & BMASK[n]) as i32
    }

    fn get_bit(&mut self) -> bool {
        if self.code_bits < 1 {
            self.grow_buffer();
        }
        if self.code_bits < 1 {
            return false;
        }
        let k = self.code_buffer;
        self.code_buffer <<= 1;
        self.code_bits -= 1;
        k & 0x8000_0000 != 0
    }
}

fn addints_valid(a: i32, b: i32) -> bool {
    if (a >= 0) != (b >= 0) {
        return true;
    }
    if a < 0 && b < 0 {
        return a >= i32::MIN - b;
    }
    a <= i32::MAX - b
}

fn mul2shorts_valid(a: i32, b: i32) -> bool {
    if b == 0 || b == -1 {
        return true;
    }
    if (a >= 0) == (b >= 0) {
        return a <= 32767 / b;
    }
    if b < 0 {
        return a <= -32768 / b;
    }
    a >= -32768 / b
}

/// One sequential (baseline) block, dequantised.
fn decode_block(
    s: &mut Stream<'_>,
    data: &mut [i16; 64],
    hdc: &Huffman,
    hac: &Huffman,
    fac: &[i16; 512],
    dc_pred: &mut i32,
    dequant: &[u16; 64],
) -> Result<()> {
    if s.code_bits < 16 {
        s.grow_buffer();
    }
    let t = s.huff_decode(hdc);
    if !(0..=15).contains(&t) {
        return Err(JpegError::Corrupt("bad huffman code"));
    }
    *data = [0; 64];
    let diff = if t != 0 { s.extend_receive(t) } else { 0 };
    if !addints_valid(*dc_pred, diff) {
        return Err(JpegError::Corrupt("bad delta"));
    }
    let dc = *dc_pred + diff;
    *dc_pred = dc;
    if !mul2shorts_valid(dc, i32::from(dequant[0])) {
        return Err(JpegError::Corrupt("can't merge dc and ac"));
    }
    data[0] = dc.wrapping_mul(i32::from(dequant[0])) as i16;

    let mut k = 1usize;
    loop {
        if s.code_bits < 16 {
            s.grow_buffer();
        }
        let c = ((s.code_buffer >> (32 - FAST_BITS)) & 511) as usize;
        let r = i32::from(fac[c]);
        if r != 0 {
            k += ((r >> 4) & 15) as usize;
            let len = r & 15;
            if len > s.code_bits {
                return Err(JpegError::Corrupt("bad huffman code"));
            }
            s.code_buffer <<= len;
            s.code_bits -= len;
            let zig = usize::from(DEZIGZAG[k]);
            k += 1;
            data[zig] = (r >> 8).wrapping_mul(i32::from(dequant[zig])) as i16;
        } else {
            let rs = s.huff_decode(hac);
            if rs < 0 {
                return Err(JpegError::Corrupt("bad huffman code"));
            }
            let len = rs & 15;
            let run = rs >> 4;
            if len == 0 {
                if rs != 0xF0 {
                    break;
                }
                k += 16;
            } else {
                k += run as usize;
                let zig = usize::from(DEZIGZAG[k]);
                k += 1;
                data[zig] = s.extend_receive(len).wrapping_mul(i32::from(dequant[zig])) as i16;
            }
        }
        if k >= 64 {
            break;
        }
    }
    Ok(())
}

/// The DC coefficient of a progressive block (first scan or refinement).
fn decode_block_prog_dc(
    s: &mut Stream<'_>,
    data: &mut [i16],
    hdc: &Huffman,
    dc_pred: &mut i32,
) -> Result<()> {
    if s.spec_end != 0 {
        return Err(JpegError::Corrupt("can't merge dc and ac"));
    }
    if s.code_bits < 16 {
        s.grow_buffer();
    }
    if s.succ_high == 0 {
        data.fill(0);
        let t = s.huff_decode(hdc);
        if !(0..=15).contains(&t) {
            return Err(JpegError::Corrupt("can't merge dc and ac"));
        }
        let diff = if t != 0 { s.extend_receive(t) } else { 0 };
        if !addints_valid(*dc_pred, diff) {
            return Err(JpegError::Corrupt("bad delta"));
        }
        let dc = *dc_pred + diff;
        *dc_pred = dc;
        if !mul2shorts_valid(dc, 1 << s.succ_low) {
            return Err(JpegError::Corrupt("can't merge dc and ac"));
        }
        data[0] = dc.wrapping_mul(1 << s.succ_low) as i16;
    } else if s.get_bit() {
        data[0] = data[0].wrapping_add((1i32 << s.succ_low) as i16);
    }
    Ok(())
}

/// AC coefficients of a progressive block (first scan or refinement).
fn decode_block_prog_ac(
    s: &mut Stream<'_>,
    data: &mut [i16],
    hac: &Huffman,
    fac: &[i16; 512],
) -> Result<()> {
    if s.spec_start == 0 {
        return Err(JpegError::Corrupt("can't merge dc and ac"));
    }
    if s.succ_high == 0 {
        let shift = s.succ_low;
        if s.eob_run != 0 {
            s.eob_run -= 1;
            return Ok(());
        }
        let mut k = s.spec_start as usize;
        loop {
            if s.code_bits < 16 {
                s.grow_buffer();
            }
            let c = ((s.code_buffer >> (32 - FAST_BITS)) & 511) as usize;
            let r = i32::from(fac[c]);
            if r != 0 {
                k += ((r >> 4) & 15) as usize;
                let len = r & 15;
                if len > s.code_bits {
                    return Err(JpegError::Corrupt("bad huffman code"));
                }
                s.code_buffer <<= len;
                s.code_bits -= len;
                let zig = usize::from(DEZIGZAG[k]);
                k += 1;
                data[zig] = (r >> 8).wrapping_mul(1 << shift) as i16;
            } else {
                let rs = s.huff_decode(hac);
                if rs < 0 {
                    return Err(JpegError::Corrupt("bad huffman code"));
                }
                let len = rs & 15;
                let run = rs >> 4;
                if len == 0 {
                    if run < 15 {
                        s.eob_run = 1 << run;
                        if run != 0 {
                            s.eob_run += s.get_bits(run);
                        }
                        s.eob_run -= 1;
                        break;
                    }
                    k += 16;
                } else {
                    k += run as usize;
                    let zig = usize::from(DEZIGZAG[k]);
                    k += 1;
                    data[zig] = s.extend_receive(len).wrapping_mul(1 << shift) as i16;
                }
            }
            if k > s.spec_end as usize {
                break;
            }
        }
    } else {
        // Refinement: one more bit of precision for coefficients.
        let bit = (1i32 << s.succ_low) as i16;
        let spec_end = s.spec_end as usize;
        if s.eob_run != 0 {
            s.eob_run -= 1;
            for k in s.spec_start as usize..=spec_end {
                let p = &mut data[usize::from(DEZIGZAG[k])];
                if *p != 0 && s.get_bit() && (*p & bit) == 0 {
                    if *p > 0 {
                        *p = p.wrapping_add(bit);
                    } else {
                        *p = p.wrapping_sub(bit);
                    }
                }
            }
        } else {
            let mut k = s.spec_start as usize;
            loop {
                let rs = s.huff_decode(hac);
                if rs < 0 {
                    return Err(JpegError::Corrupt("bad huffman code"));
                }
                let mut value = rs & 15;
                let mut run = rs >> 4;
                match value {
                    0 => {
                        if run < 15 {
                            s.eob_run = (1 << run) - 1;
                            if run != 0 {
                                s.eob_run += s.get_bits(run);
                            }
                            run = 64;
                        }
                    }
                    1 => {
                        value = if s.get_bit() {
                            i32::from(bit)
                        } else {
                            -i32::from(bit)
                        }
                    }
                    _ => return Err(JpegError::Corrupt("bad huffman code")),
                }
                while k <= spec_end {
                    let p = &mut data[usize::from(DEZIGZAG[k])];
                    k += 1;
                    if *p != 0 {
                        if s.get_bit() && (*p & bit) == 0 {
                            if *p > 0 {
                                *p = p.wrapping_add(bit);
                            } else {
                                *p = p.wrapping_sub(bit);
                            }
                        }
                    } else {
                        if run == 0 {
                            *p = value as i16;
                            break;
                        }
                        run -= 1;
                    }
                }
                if k > spec_end {
                    break;
                }
            }
        }
    }
    Ok(())
}

/// One pass of the integer inverse DCT over eight values; returns the even
/// part `x0..x3` and the odd part `t0..t3`. Constants are the cosine
/// factors scaled by 4096 and truncated toward zero.
#[allow(clippy::many_single_char_names)]
fn idct_1d(s: [i32; 8]) -> ([Wrapping<i32>; 4], [Wrapping<i32>; 4]) {
    let w = |v: i32| Wrapping(v);
    let s = s.map(Wrapping);
    let (p2, p3) = (s[2], s[6]);
    let p1 = (p2 + p3) * w(2217);
    let t2 = p1 + p3 * w(-7567);
    let t3 = p1 + p2 * w(3135);
    let (p2, p3) = (s[0], s[4]);
    let t0 = (p2 + p3) * w(4096);
    let t1 = (p2 - p3) * w(4096);
    let x0 = t0 + t3;
    let x3 = t0 - t3;
    let x1 = t1 + t2;
    let x2 = t1 - t2;
    let (mut t0, mut t1, mut t2, mut t3) = (s[7], s[5], s[3], s[1]);
    let mut p3 = t0 + t2;
    let mut p4 = t1 + t3;
    let mut p1 = t0 + t3;
    let mut p2 = t1 + t2;
    let p5 = (p3 + p4) * w(4816);
    t0 *= w(1223);
    t1 *= w(8410);
    t2 *= w(12586);
    t3 *= w(6149);
    p1 = p5 + p1 * w(-3685);
    p2 = p5 + p2 * w(-10497);
    p3 *= w(-8034);
    p4 *= w(-1597);
    t3 += p1 + p4;
    t2 += p2 + p3;
    t1 += p2 + p4;
    t0 += p1 + p3;
    ([x0, x1, x2, x3], [t0, t1, t2, t3])
}

fn clamp(x: i32) -> u8 {
    x.clamp(0, 255) as u8
}

/// Inverse DCT of one block into 8 rows of 8 pixels at `out[offset..]`
/// with `stride` bytes per row.
fn idct_block(out: &mut [u8], offset: usize, stride: usize, data: &[i16; 64]) {
    let mut val = [0i32; 64];
    for i in 0..8 {
        let d = |r: usize| i32::from(data[r * 8 + i]);
        if (1..8).all(|r| d(r) == 0) {
            let dc = d(0) * 4;
            for r in 0..8 {
                val[r * 8 + i] = dc;
            }
        } else {
            let ([x0, x1, x2, x3], [t0, t1, t2, t3]) =
                idct_1d([d(0), d(1), d(2), d(3), d(4), d(5), d(6), d(7)]);
            let r = Wrapping(512);
            let (x0, x1, x2, x3) = (x0 + r, x1 + r, x2 + r, x3 + r);
            val[i] = ((x0 + t3) >> 10).0;
            val[56 + i] = ((x0 - t3) >> 10).0;
            val[8 + i] = ((x1 + t2) >> 10).0;
            val[48 + i] = ((x1 - t2) >> 10).0;
            val[16 + i] = ((x2 + t1) >> 10).0;
            val[40 + i] = ((x2 - t1) >> 10).0;
            val[24 + i] = ((x3 + t0) >> 10).0;
            val[32 + i] = ((x3 - t0) >> 10).0;
        }
    }
    for (row, v) in val.chunks_exact(8).enumerate() {
        let ([x0, x1, x2, x3], [t0, t1, t2, t3]) =
            idct_1d([v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]]);
        // Rounding (65536) and the +128 level shift, in 17-bit fixed point.
        let r = Wrapping(65536 + (128 << 17));
        let (x0, x1, x2, x3) = (x0 + r, x1 + r, x2 + r, x3 + r);
        let start = offset + row * stride;
        let Some(o) = out.get_mut(start..start + 8) else {
            return;
        };
        o[0] = clamp(((x0 + t3) >> 17).0);
        o[7] = clamp(((x0 - t3) >> 17).0);
        o[1] = clamp(((x1 + t2) >> 17).0);
        o[6] = clamp(((x1 - t2) >> 17).0);
        o[2] = clamp(((x2 + t1) >> 17).0);
        o[5] = clamp(((x2 - t1) >> 17).0);
        o[3] = clamp(((x3 + t0) >> 17).0);
        o[4] = clamp(((x3 - t0) >> 17).0);
    }
}

/// One image component (a colour channel) and its decoded samples.
#[derive(Default)]
struct Component {
    id: u8,
    h: usize,
    v: usize,
    tq: usize,
    hd: usize,
    ha: usize,
    dc_pred: i32,
    /// Size in samples.
    x: usize,
    y: usize,
    /// Size of the sample buffer (whole MCUs).
    w2: usize,
    h2: usize,
    data: Vec<u8>,
    /// Progressive coefficients, 64 per block.
    coeff: Vec<i16>,
    coeff_w: usize,
    linebuf: Vec<u8>,
}

struct Decoder<'a> {
    s: Stream<'a>,
    huff_dc: Vec<Huffman>,
    huff_ac: Vec<Huffman>,
    fast_ac: Vec<[i16; 512]>,
    dequant: [[u16; 64]; 4],
    comps: Vec<Component>,
    img_x: usize,
    img_y: usize,
    img_n: usize,
    img_h_max: usize,
    img_v_max: usize,
    img_mcu_x: usize,
    img_mcu_y: usize,
    progressive: bool,
    restart_interval: i32,
    scan_n: usize,
    order: [usize; 4],
    jfif: bool,
    app14_color_transform: i32,
    rgb: i32,
}

/// Allocate a zeroed buffer, failing instead of aborting when memory is
/// short.
fn zeroed<T: Clone + Default>(len: usize) -> Result<Vec<T>> {
    let mut v = Vec::new();
    v.try_reserve_exact(len).map_err(|_| JpegError::TooLarge)?;
    v.resize(len, T::default());
    Ok(v)
}

/// True when `a * b * c + add` fits in an `i32`.
fn mad3_valid(a: usize, b: usize, c: usize, add: usize) -> bool {
    a.checked_mul(b)
        .and_then(|x| x.checked_mul(c))
        .and_then(|x| x.checked_add(add))
        .is_some_and(|x| x <= i32::MAX as usize)
}

impl<'a> Decoder<'a> {
    fn new(src: &'a [u8]) -> Self {
        Self {
            s: Stream {
                src,
                pos: 0,
                overrun: false,
                code_buffer: 0,
                code_bits: 0,
                marker: MARKER_NONE,
                nomore: false,
                todo: 0,
                eob_run: 0,
                spec_start: 0,
                spec_end: 0,
                succ_high: 0,
                succ_low: 0,
            },
            huff_dc: vec![Huffman::new(); 4],
            huff_ac: vec![Huffman::new(); 4],
            fast_ac: vec![[0; 512]; 4],
            dequant: [[0; 64]; 4],
            comps: (0..4).map(|_| Component::default()).collect(),
            img_x: 0,
            img_y: 0,
            img_n: 0,
            img_h_max: 1,
            img_v_max: 1,
            img_mcu_x: 0,
            img_mcu_y: 0,
            progressive: false,
            restart_interval: 0,
            scan_n: 0,
            order: [0; 4],
            jfif: false,
            app14_color_transform: -1,
            rgb: 0,
        }
    }

    fn reset(&mut self) {
        let s = &mut self.s;
        s.code_bits = 0;
        s.code_buffer = 0;
        s.nomore = false;
        for c in &mut self.comps {
            c.dc_pred = 0;
        }
        s.marker = MARKER_NONE;
        s.todo = if self.restart_interval != 0 {
            self.restart_interval
        } else {
            i32::MAX
        };
        s.eob_run = 0;
    }

    /// Count down the restart interval after an MCU. Returns false when the
    /// scan ends (a marker other than a restart marker was reached).
    fn after_mcu(&mut self) -> bool {
        self.s.todo -= 1;
        if self.s.todo <= 0 {
            if self.s.code_bits < 24 {
                self.s.grow_buffer();
            }
            if !(0xD0..=0xD7).contains(&self.s.marker) {
                return false;
            }
            self.reset();
        }
        true
    }

    fn parse_entropy_coded_data(&mut self) -> Result<()> {
        self.reset();
        let mut block = [0i16; 64];
        if !self.progressive {
            if self.scan_n == 1 {
                let n = self.order[0];
                let w = (self.comps[n].x + 7) >> 3;
                let h = (self.comps[n].y + 7) >> 3;
                for j in 0..h {
                    for i in 0..w {
                        self.baseline_block(n, &mut block)?;
                        let c = &mut self.comps[n];
                        idct_block(&mut c.data, c.w2 * j * 8 + i * 8, c.w2, &block);
                        if !self.after_mcu() {
                            return Ok(());
                        }
                    }
                }
                return Ok(());
            }
            for j in 0..self.img_mcu_y {
                for i in 0..self.img_mcu_x {
                    for k in 0..self.scan_n {
                        let n = self.order[k];
                        for y in 0..self.comps[n].v {
                            for x in 0..self.comps[n].h {
                                let x2 = (i * self.comps[n].h + x) * 8;
                                let y2 = (j * self.comps[n].v + y) * 8;
                                self.baseline_block(n, &mut block)?;
                                let c = &mut self.comps[n];
                                idct_block(&mut c.data, c.w2 * y2 + x2, c.w2, &block);
                            }
                        }
                    }
                    if !self.after_mcu() {
                        return Ok(());
                    }
                }
            }
            return Ok(());
        }
        if self.scan_n == 1 {
            let n = self.order[0];
            let w = (self.comps[n].x + 7) >> 3;
            let h = (self.comps[n].y + 7) >> 3;
            for j in 0..h {
                for i in 0..w {
                    let at = 64 * (i + j * self.comps[n].coeff_w);
                    let Decoder {
                        s,
                        huff_dc,
                        huff_ac,
                        fast_ac,
                        comps,
                        ..
                    } = self;
                    let c = &mut comps[n];
                    let data = &mut c.coeff[at..at + 64];
                    if s.spec_start == 0 {
                        decode_block_prog_dc(s, data, &huff_dc[c.hd], &mut c.dc_pred)?;
                    } else {
                        decode_block_prog_ac(s, data, &huff_ac[c.ha], &fast_ac[c.ha])?;
                    }
                    if !self.after_mcu() {
                        return Ok(());
                    }
                }
            }
            return Ok(());
        }
        for j in 0..self.img_mcu_y {
            for i in 0..self.img_mcu_x {
                for k in 0..self.scan_n {
                    let n = self.order[k];
                    for y in 0..self.comps[n].v {
                        for x in 0..self.comps[n].h {
                            let x2 = i * self.comps[n].h + x;
                            let y2 = j * self.comps[n].v + y;
                            let at = 64 * (x2 + y2 * self.comps[n].coeff_w);
                            let Decoder {
                                s, huff_dc, comps, ..
                            } = self;
                            let c = &mut comps[n];
                            let data = &mut c.coeff[at..at + 64];
                            decode_block_prog_dc(s, data, &huff_dc[c.hd], &mut c.dc_pred)?;
                        }
                    }
                }
                if !self.after_mcu() {
                    return Ok(());
                }
            }
        }
        Ok(())
    }

    fn baseline_block(&mut self, n: usize, block: &mut [i16; 64]) -> Result<()> {
        let Decoder {
            s,
            huff_dc,
            huff_ac,
            fast_ac,
            dequant,
            comps,
            ..
        } = self;
        let c = &mut comps[n];
        decode_block(
            s,
            block,
            &huff_dc[c.hd],
            &huff_ac[c.ha],
            &fast_ac[c.ha],
            &mut c.dc_pred,
            &dequant[c.tq],
        )
    }

    /// Dequantise and transform every progressive block.
    fn finish(&mut self) {
        if !self.progressive {
            return;
        }
        for n in 0..self.img_n {
            let dq = self.dequant[self.comps[n].tq];
            let c = &mut self.comps[n];
            let w = (c.x + 7) >> 3;
            let h = (c.y + 7) >> 3;
            for j in 0..h {
                for i in 0..w {
                    let at = 64 * (i + j * c.coeff_w);
                    let mut block = [0i16; 64];
                    for (k, b) in block.iter_mut().enumerate() {
                        *b = c.coeff[at + k].wrapping_mul(dq[k] as i16);
                    }
                    idct_block(&mut c.data, c.w2 * j * 8 + i * 8, c.w2, &block);
                }
            }
        }
    }

    fn process_marker(&mut self, m: u8) -> Result<()> {
        match m {
            MARKER_NONE => Err(JpegError::Corrupt("expected marker")),
            0xDD => {
                if self.s.get16be() != 4 {
                    return Err(JpegError::Corrupt("bad DRI len"));
                }
                self.restart_interval = self.s.get16be();
                Ok(())
            }
            0xDB => {
                let mut l = self.s.get16be() - 2;
                while l > 0 {
                    let q = self.s.get8();
                    let p = q >> 4;
                    let sixteen = p != 0;
                    let t = usize::from(q & 15);
                    if p != 0 && p != 1 {
                        return Err(JpegError::Corrupt("bad DQT type"));
                    }
                    if t > 3 {
                        return Err(JpegError::Corrupt("bad DQT table"));
                    }
                    for &z in DEZIGZAG.iter().take(64) {
                        self.dequant[t][usize::from(z)] = if sixteen {
                            self.s.get16be() as u16
                        } else {
                            u16::from(self.s.get8())
                        };
                    }
                    l -= if sixteen { 129 } else { 65 };
                }
                if l == 0 {
                    Ok(())
                } else {
                    Err(JpegError::Corrupt("bad DQT len"))
                }
            }
            0xC4 => {
                let mut l = self.s.get16be() - 2;
                while l > 0 {
                    let q = self.s.get8();
                    let tc = q >> 4;
                    let th = usize::from(q & 15);
                    if tc > 1 || th > 3 {
                        return Err(JpegError::Corrupt("bad DHT header"));
                    }
                    let mut sizes = [0usize; 16];
                    let mut n = 0usize;
                    for size in &mut sizes {
                        *size = usize::from(self.s.get8());
                        n += *size;
                    }
                    if n > 256 {
                        return Err(JpegError::Corrupt("bad DHT header"));
                    }
                    l -= 17;
                    let table = if tc == 0 {
                        &mut self.huff_dc[th]
                    } else {
                        &mut self.huff_ac[th]
                    };
                    table.build(&sizes)?;
                    for i in 0..n {
                        table.values[i] = self.s.get8();
                    }
                    if tc != 0 {
                        self.fast_ac[th] = self.huff_ac[th].fast_ac();
                    }
                    l -= n as i32;
                }
                if l == 0 {
                    Ok(())
                } else {
                    Err(JpegError::Corrupt("bad DHT len"))
                }
            }
            0xE0..=0xEF | 0xFE => {
                let mut l = self.s.get16be();
                if l < 2 {
                    return Err(JpegError::Corrupt(if m == 0xFE {
                        "bad COM len"
                    } else {
                        "bad APP len"
                    }));
                }
                l -= 2;
                if m == 0xE0 && l >= 5 {
                    let mut ok = true;
                    for &t in b"JFIF\0" {
                        if self.s.get8() != t {
                            ok = false;
                        }
                    }
                    l -= 5;
                    if ok {
                        self.jfif = true;
                    }
                } else if m == 0xEE && l >= 12 {
                    let mut ok = true;
                    for &t in b"Adobe\0" {
                        if self.s.get8() != t {
                            ok = false;
                        }
                    }
                    l -= 6;
                    if ok {
                        self.s.get8(); // version
                        self.s.get16be(); // flags 0
                        self.s.get16be(); // flags 1
                        self.app14_color_transform = i32::from(self.s.get8());
                        l -= 6;
                    }
                }
                self.s.skip(l);
                Ok(())
            }
            _ => Err(JpegError::Corrupt("unknown marker")),
        }
    }

    fn process_scan_header(&mut self) -> Result<()> {
        let ls = self.s.get16be();
        self.scan_n = usize::from(self.s.get8());
        if self.scan_n < 1 || self.scan_n > 4 || self.scan_n > self.img_n {
            return Err(JpegError::Corrupt("bad SOS component count"));
        }
        if ls != 6 + 2 * self.scan_n as i32 {
            return Err(JpegError::Corrupt("bad SOS len"));
        }
        for i in 0..self.scan_n {
            let id = self.s.get8();
            let q = self.s.get8();
            let Some(which) = self.comps[..self.img_n].iter().position(|c| c.id == id) else {
                return Err(JpegError::Corrupt("bad SOS component id"));
            };
            let c = &mut self.comps[which];
            c.hd = usize::from(q >> 4);
            if c.hd > 3 {
                return Err(JpegError::Corrupt("bad DC huff"));
            }
            c.ha = usize::from(q & 15);
            if c.ha > 3 {
                return Err(JpegError::Corrupt("bad AC huff"));
            }
            self.order[i] = which;
        }
        let s = &mut self.s;
        s.spec_start = i32::from(s.get8());
        s.spec_end = i32::from(s.get8());
        let aa = s.get8();
        s.succ_high = i32::from(aa >> 4);
        s.succ_low = i32::from(aa & 15);
        if self.progressive {
            if s.spec_start > 63
                || s.spec_end > 63
                || s.spec_start > s.spec_end
                || s.succ_high > 13
                || s.succ_low > 13
            {
                return Err(JpegError::Corrupt("bad SOS"));
            }
        } else {
            if s.spec_start != 0 || s.succ_high != 0 || s.succ_low != 0 {
                return Err(JpegError::Corrupt("bad SOS"));
            }
            s.spec_end = 63;
        }
        Ok(())
    }

    fn process_frame_header(&mut self) -> Result<()> {
        let lf = self.s.get16be();
        if lf < 11 {
            return Err(JpegError::Corrupt("bad SOF len"));
        }
        if self.s.get8() != 8 {
            return Err(JpegError::Corrupt("only 8-bit"));
        }
        self.img_y = self.s.get16be() as usize;
        if self.img_y == 0 {
            return Err(JpegError::Corrupt("no header height"));
        }
        self.img_x = self.s.get16be() as usize;
        if self.img_x == 0 {
            return Err(JpegError::Corrupt("0 width"));
        }
        let c = usize::from(self.s.get8());
        if c != 3 && c != 1 && c != 4 {
            return Err(JpegError::Corrupt("bad component count"));
        }
        self.img_n = c;
        if lf != 8 + 3 * c as i32 {
            return Err(JpegError::Corrupt("bad SOF len"));
        }
        self.rgb = 0;
        for i in 0..c {
            let id = self.s.get8();
            if c == 3 && id == b"RGB"[i] {
                self.rgb += 1;
            }
            let q = self.s.get8();
            let comp = &mut self.comps[i];
            comp.id = id;
            comp.h = usize::from(q >> 4);
            if comp.h == 0 || comp.h > 4 {
                return Err(JpegError::Corrupt("bad H"));
            }
            comp.v = usize::from(q & 15);
            if comp.v == 0 || comp.v > 4 {
                return Err(JpegError::Corrupt("bad V"));
            }
            comp.tq = usize::from(self.s.get8());
            if comp.tq > 3 {
                return Err(JpegError::Corrupt("bad TQ"));
            }
        }
        if !mad3_valid(self.img_x, self.img_y, c, 0) {
            return Err(JpegError::TooLarge);
        }
        let h_max = self.comps[..c].iter().map(|x| x.h).max().unwrap_or(1);
        let v_max = self.comps[..c].iter().map(|x| x.v).max().unwrap_or(1);
        for comp in &self.comps[..c] {
            if h_max % comp.h != 0 {
                return Err(JpegError::Corrupt("bad H"));
            }
            if v_max % comp.v != 0 {
                return Err(JpegError::Corrupt("bad V"));
            }
        }
        self.img_h_max = h_max;
        self.img_v_max = v_max;
        let mcu_w = h_max * 8;
        let mcu_h = v_max * 8;
        self.img_mcu_x = self.img_x.div_ceil(mcu_w);
        self.img_mcu_y = self.img_y.div_ceil(mcu_h);
        for comp in &mut self.comps[..c] {
            comp.x = (self.img_x * comp.h).div_ceil(h_max);
            comp.y = (self.img_y * comp.v).div_ceil(v_max);
            comp.w2 = self.img_mcu_x * comp.h * 8;
            comp.h2 = self.img_mcu_y * comp.v * 8;
            if !mad3_valid(comp.w2, comp.h2, 1, 15) {
                return Err(JpegError::TooLarge);
            }
            comp.data = zeroed(comp.w2 * comp.h2)?;
            if self.progressive {
                if !mad3_valid(comp.w2, comp.h2, 2, 15) {
                    return Err(JpegError::TooLarge);
                }
                comp.coeff_w = comp.w2 / 8;
                comp.coeff = zeroed(comp.w2 * comp.h2)?;
            }
        }
        Ok(())
    }

    fn decode_header(&mut self) -> Result<()> {
        self.jfif = false;
        self.app14_color_transform = -1;
        self.s.marker = MARKER_NONE;
        if self.s.get_marker() != 0xD8 {
            return Err(JpegError::NotJpeg);
        }
        let mut m = self.s.get_marker();
        while !matches!(m, 0xC0..=0xC2) {
            self.process_marker(m)?;
            m = self.s.get_marker();
            while m == MARKER_NONE {
                if self.s.at_eof() {
                    return Err(JpegError::Corrupt("no SOF"));
                }
                m = self.s.get_marker();
            }
        }
        self.progressive = m == 0xC2;
        self.process_frame_header()
    }

    /// Skip junk after a scan up to the next marker.
    fn skip_junk_at_end(&mut self) -> u8 {
        while !self.s.at_eof() {
            let mut x = self.s.get8();
            while x == 0xFF {
                if self.s.at_eof() {
                    return MARKER_NONE;
                }
                x = self.s.get8();
                if x != 0x00 && x != 0xFF {
                    return x;
                }
            }
        }
        MARKER_NONE
    }

    fn decode_image(&mut self) -> Result<()> {
        self.restart_interval = 0;
        self.decode_header()?;
        let mut m = self.s.get_marker();
        loop {
            match m {
                0xD9 => {
                    self.finish();
                    return Ok(());
                }
                0xDA => {
                    self.process_scan_header()?;
                    self.parse_entropy_coded_data()?;
                    if self.s.marker == MARKER_NONE {
                        self.s.marker = self.skip_junk_at_end();
                    }
                    m = self.s.get_marker();
                    if (0xD0..=0xD7).contains(&m) {
                        m = self.s.get_marker();
                    }
                }
                0xDC => {
                    let ld = self.s.get16be();
                    let nl = self.s.get16be();
                    if ld != 4 {
                        return Err(JpegError::Corrupt("bad DNL len"));
                    }
                    if nl as usize != self.img_y {
                        return Err(JpegError::Corrupt("bad DNL height"));
                    }
                    m = self.s.get_marker();
                }
                _ => {
                    if self.s.overrun {
                        return Err(JpegError::Truncated);
                    }
                    self.process_marker(m)?;
                    m = self.s.get_marker();
                }
            }
        }
    }

    /// Upsample and colour-convert the decoded components to RGBA.
    fn output_rgba(&mut self) -> Result<Vec<u8>> {
        let n_out = 4usize;
        let is_rgb =
            self.img_n == 3 && (self.rgb == 3 || (self.app14_color_transform == 0 && !self.jfif));
        let decode_n = self.img_n;
        let mut res = Vec::new();
        for k in 0..decode_n {
            let c = &mut self.comps[k];
            c.linebuf = zeroed(self.img_x + 3)?;
            let hs = self.img_h_max / c.h;
            let vs = self.img_v_max / c.v;
            res.push(Resample {
                hs,
                vs,
                ystep: vs >> 1,
                w_lores: self.img_x.div_ceil(hs),
                ypos: 0,
                line0: 0,
                line1: 0,
            });
        }
        if !mad3_valid(n_out, self.img_x, self.img_y, 1) {
            return Err(JpegError::TooLarge);
        }
        let mut out: Vec<u8> = zeroed(n_out * self.img_x * self.img_y)?;
        let mut rows = [Row::Line; 4];
        for out_row in out.chunks_exact_mut(n_out * self.img_x) {
            for k in 0..decode_n {
                let r = &mut res[k];
                let Component {
                    data,
                    linebuf,
                    y,
                    w2,
                    ..
                } = &mut self.comps[k];
                let y_bot = r.ystep >= (r.vs >> 1);
                let (near, far) = if y_bot {
                    (r.line1, r.line0)
                } else {
                    (r.line0, r.line1)
                };
                let near = &data[near..];
                let far = &data[far..];
                rows[k] = match (r.hs, r.vs) {
                    (1, 1) => Row::Data(if y_bot { r.line1 } else { r.line0 }),
                    (1, 2) => {
                        resample_row_v2(linebuf, near, far, r.w_lores);
                        Row::Line
                    }
                    (2, 1) => {
                        resample_row_h2(linebuf, near, r.w_lores);
                        Row::Line
                    }
                    (2, 2) => {
                        resample_row_hv2(linebuf, near, far, r.w_lores);
                        Row::Line
                    }
                    _ => {
                        resample_row_generic(linebuf, near, r.w_lores, r.hs);
                        Row::Line
                    }
                };
                r.ystep += 1;
                if r.ystep >= r.vs {
                    r.ystep = 0;
                    r.line0 = r.line1;
                    r.ypos += 1;
                    if r.ypos < *y {
                        r.line1 += *w2;
                    }
                }
            }
            let row = |k: usize| -> &[u8] {
                let c = &self.comps[k];
                match rows[k] {
                    Row::Data(at) => &c.data[at..],
                    Row::Line => &c.linebuf,
                }
            };
            let x = self.img_x;
            match self.img_n {
                3 if is_rgb => {
                    let (r, g, b) = (row(0), row(1), row(2));
                    for (i, px) in out_row.chunks_exact_mut(4).enumerate() {
                        px.copy_from_slice(&[r[i], g[i], b[i], 255]);
                    }
                }
                3 => ycbcr_to_rgba(out_row, row(0), row(1), row(2), x),
                4 => {
                    let k = row(3);
                    match self.app14_color_transform {
                        0 => {
                            let (c, m, y) = (row(0), row(1), row(2));
                            for (i, px) in out_row.chunks_exact_mut(4).enumerate() {
                                px.copy_from_slice(&[
                                    blinn_8x8(c[i], k[i]),
                                    blinn_8x8(m[i], k[i]),
                                    blinn_8x8(y[i], k[i]),
                                    255,
                                ]);
                            }
                        }
                        2 => {
                            ycbcr_to_rgba(out_row, row(0), row(1), row(2), x);
                            for (i, px) in out_row.chunks_exact_mut(4).enumerate() {
                                for ch in &mut px[..3] {
                                    *ch = blinn_8x8(255 - *ch, k[i]);
                                }
                            }
                        }
                        _ => ycbcr_to_rgba(out_row, row(0), row(1), row(2), x),
                    }
                }
                _ => {
                    let y = row(0);
                    for (i, px) in out_row.chunks_exact_mut(4).enumerate() {
                        px.copy_from_slice(&[y[i], y[i], y[i], 255]);
                    }
                }
            }
        }
        Ok(out)
    }
}

/// Where a component's current output row lives.
#[derive(Clone, Copy)]
enum Row {
    /// In the component's samples, at this offset (no resampling needed).
    Data(usize),
    /// In the component's line buffer.
    Line,
}

/// Vertical and horizontal upsampling state of one component.
struct Resample {
    hs: usize,
    vs: usize,
    ystep: usize,
    w_lores: usize,
    ypos: usize,
    line0: usize,
    line1: usize,
}

/// Double vertically: 3/4 of the nearer row plus 1/4 of the farther.
fn resample_row_v2(out: &mut [u8], near: &[u8], far: &[u8], w: usize) {
    for i in 0..w {
        out[i] = ((3 * u32::from(near[i]) + u32::from(far[i]) + 2) >> 2) as u8;
    }
}

/// Double horizontally: 3/4 of the nearer sample plus 1/4 of its neighbour.
fn resample_row_h2(out: &mut [u8], input: &[u8], w: usize) {
    let x = |i: usize| u32::from(input[i]);
    if w == 1 {
        out[0] = input[0];
        out[1] = input[0];
        return;
    }
    out[0] = input[0];
    out[1] = ((x(0) * 3 + x(1) + 2) >> 2) as u8;
    let mut i = 1;
    while i < w - 1 {
        let n = 3 * x(i) + 2;
        out[i * 2] = ((n + x(i - 1)) >> 2) as u8;
        out[i * 2 + 1] = ((n + x(i + 1)) >> 2) as u8;
        i += 1;
    }
    out[i * 2] = ((x(w - 2) * 3 + x(w - 1) + 2) >> 2) as u8;
    out[i * 2 + 1] = input[w - 1];
}

/// Double both ways: the vertical 3:1 blend, then the horizontal one.
fn resample_row_hv2(out: &mut [u8], near: &[u8], far: &[u8], w: usize) {
    let col = |i: usize| 3 * u32::from(near[i]) + u32::from(far[i]);
    if w == 1 {
        let v = ((col(0) + 2) >> 2) as u8;
        out[0] = v;
        out[1] = v;
        return;
    }
    let mut t1 = col(0);
    out[0] = ((t1 + 2) >> 2) as u8;
    for i in 1..w {
        let t0 = t1;
        t1 = col(i);
        out[i * 2 - 1] = ((3 * t0 + t1 + 8) >> 4) as u8;
        out[i * 2] = ((3 * t1 + t0 + 8) >> 4) as u8;
    }
    out[w * 2 - 1] = ((t1 + 2) >> 2) as u8;
}

/// Repeat each sample `hs` times.
fn resample_row_generic(out: &mut [u8], near: &[u8], w: usize, hs: usize) {
    for i in 0..w {
        for j in 0..hs {
            out[i * hs + j] = near[i];
        }
    }
}

/// YCbCr to RGB in 20-bit fixed point (factors 1.402, 0.71414, 0.34414,
/// 1.772 scaled by 2^20 via 4096 * 256).
fn ycbcr_to_rgba(out: &mut [u8], y: &[u8], cb: &[u8], cr: &[u8], count: usize) {
    for (i, px) in out.chunks_exact_mut(4).take(count).enumerate() {
        let y_fixed = (i32::from(y[i]) << 20) + (1 << 19);
        let cr = i32::from(cr[i]) - 128;
        let cb = i32::from(cb[i]) - 128;
        let r = y_fixed.wrapping_add(cr * 1_470_208);
        let g = y_fixed
            .wrapping_add(cr * -748_800)
            .wrapping_add(((cb * -360_960) as u32 & 0xFFFF_0000) as i32);
        let b = y_fixed.wrapping_add(cb * 1_858_048);
        px[0] = clamp(r >> 20);
        px[1] = clamp(g >> 20);
        px[2] = clamp(b >> 20);
        px[3] = 255;
    }
}

/// `x * y / 255`, rounded.
fn blinn_8x8(x: u8, y: u8) -> u8 {
    let t = u32::from(x) * u32::from(y) + 128;
    ((t + (t >> 8)) >> 8) as u8
}

/// Decode a JPEG file to RGBA.
pub fn decode(data: &[u8]) -> Result<Image> {
    let mut d = Decoder::new(data);
    d.decode_image()?;
    if d.s.overrun {
        return Err(JpegError::Truncated);
    }
    let rgba = d.output_rgba()?;
    Ok(Image {
        width: d.img_x as u32,
        height: d.img_y as u32,
        rgba,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A one-component image of `blocks` 8×8 blocks side by side with
    /// quantiser 1. The DC table has one 1-bit code (`0`) for category 3; the AC
    /// table one 1-bit code (`0`) for end of block. So `0 111 0` is a block
    /// whose DC difference is +7. With `restart`, a restart marker follows
    /// every block and the entropy data is one `0111_0111` byte per block.
    fn gray_jpeg(blocks: u16, restart: bool, entropy: &[u8]) -> Vec<u8> {
        let mut v = vec![0xFF, 0xD8];
        v.extend_from_slice(&[0xFF, 0xDB, 0x00, 0x43, 0x00]);
        v.extend_from_slice(&[1; 64]);
        let w = blocks * 8;
        v.extend_from_slice(&[0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x08]);
        v.extend_from_slice(&w.to_be_bytes());
        v.extend_from_slice(&[0x01, 0x01, 0x11, 0x00]);
        v.extend_from_slice(&[0xFF, 0xC4, 0x00, 0x14, 0x00, 1]);
        v.extend_from_slice(&[0; 15]);
        v.push(3);
        v.extend_from_slice(&[0xFF, 0xC4, 0x00, 0x14, 0x10, 1]);
        v.extend_from_slice(&[0; 15]);
        v.push(0);
        if restart {
            v.extend_from_slice(&[0xFF, 0xDD, 0x00, 0x04, 0x00, 0x01]);
        }
        v.extend_from_slice(&[0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
        if restart {
            for b in 0..blocks {
                v.push(0b0111_0111);
                if b + 1 < blocks {
                    v.extend_from_slice(&[0xFF, 0xD0 + (b % 8) as u8]);
                }
            }
        } else {
            v.extend_from_slice(entropy);
        }
        v.extend_from_slice(&[0xFF, 0xD9]);
        v
    }

    fn pixel(img: &Image, x: usize, y: usize) -> [u8; 4] {
        let at = (y * img.width as usize + x) * 4;
        img.rgba[at..at + 4].try_into().unwrap()
    }

    #[test]
    fn flat_block_decodes_to_its_dc_level() {
        let img = decode(&gray_jpeg(1, true, &[])).unwrap();
        assert_eq!((img.width, img.height), (8, 8));
        // DC 7 with quantiser 1: (7*4*4096 + 65536 + (128 << 17)) >> 17 = 129.
        assert!(img.rgba.chunks(4).all(|p| p == [129, 129, 129, 255]));
    }

    #[test]
    fn restart_markers_reset_the_dc_prediction() {
        let img = decode(&gray_jpeg(3, true, &[])).unwrap();
        assert_eq!((img.width, img.height), (24, 8));
        assert!(img.rgba.chunks(4).all(|p| p == [129, 129, 129, 255]));
    }

    #[test]
    fn prediction_carries_without_restarts() {
        // Two blocks of "0 111 0", padded with ones: 0111_0011 1011_1111.
        let img = decode(&gray_jpeg(2, false, &[0x73, 0xBF])).unwrap();
        assert_eq!(img.width, 16);
        // DC 7 then 14: (14*4*4096 + 65536 + (128 << 17)) >> 17 = 130.
        assert_eq!(pixel(&img, 7, 7), [129, 129, 129, 255]);
        assert_eq!(pixel(&img, 8, 0), [130, 130, 130, 255]);
    }

    #[test]
    fn truncated_file_is_an_error_at_every_length() {
        let bytes = gray_jpeg(3, true, &[]);
        for len in 0..bytes.len() {
            assert!(decode(&bytes[..len]).is_err(), "len {len}");
        }
    }

    #[test]
    fn corrupted_bytes_never_panic() {
        let bytes = gray_jpeg(3, true, &[]);
        for i in 0..bytes.len() {
            for x in [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF] {
                let mut b = bytes.clone();
                b[i] = x;
                let _ = decode(&b);
            }
        }
    }

    #[test]
    fn not_a_jpeg() {
        assert_eq!(decode(b"\x89PNG"), Err(JpegError::NotJpeg));
        assert_eq!(decode(&[]), Err(JpegError::NotJpeg));
    }

    #[test]
    fn idct_of_a_dc_block_is_flat() {
        let mut block = [0i16; 64];
        block[0] = -64;
        let mut out = [0u8; 64];
        idct_block(&mut out, 0, 8, &block);
        // (-64*4*4096 + 65536 + (128 << 17)) >> 17 = 120.
        assert!(out.iter().all(|&p| p == 120));
    }

    #[test]
    fn idct_with_ac_terms_matches_hand_computation() {
        // A single first-row AC coefficient: the column pass takes its fast
        // path (only row 0 non-zero) and the row pass spreads a cosine.
        let mut block = [0i16; 64];
        block[1] = 100;
        let mut out = [0u8; 64];
        idct_block(&mut out, 0, 8, &block);
        // Every row equal: the column pass gives 400 in column 1; the row
        // pass's odd part gives t3 = 2273200, t2 = 1926400, t1 = 1287600,
        // t0 = 452400, and o[k] = (16842752 ± t) >> 17.
        let row: [u8; 8] = out[..8].try_into().unwrap();
        assert!(out.chunks(8).all(|r| r == row));
        assert_eq!(row, [145, 143, 138, 131, 125, 118, 113, 111]);
    }

    #[test]
    fn ycbcr_conversion_matches_fixed_point() {
        let mut px = [0u8; 4];
        ycbcr_to_rgba(&mut px, &[128], &[128], &[128], 1);
        assert_eq!(px, [128, 128, 128, 255]);
        ycbcr_to_rgba(&mut px, &[76], &[85], &[255], 1);
        // Pure red, as an encoder would store it, comes back within a step.
        assert_eq!(px, [254, 0, 0, 255]);
        ycbcr_to_rgba(&mut px, &[0], &[255], &[255], 1);
        assert_eq!(px, [178, 0, 225, 255]);
    }

    #[test]
    fn hv2_upsampling_blends_three_to_one() {
        let near = [0u8, 100];
        let far = [40u8, 40];
        let mut out = [0u8; 4];
        resample_row_hv2(&mut out, &near, &far, 2);
        // Column blends: 3*0+40 = 40, 3*100+40 = 340.
        // out0 = (40+2)>>2 = 10; out1 = (3*40+340+8)>>4 = 29;
        // out2 = (3*340+40+8)>>4 = 66; out3 = (340+2)>>2 = 85.
        assert_eq!(out, [10, 29, 66, 85]);
    }

    #[test]
    fn h2_upsampling() {
        let input = [0u8, 100, 200];
        let mut out = [0u8; 6];
        resample_row_h2(&mut out, &input, 3);
        // The second-to-last output weights the two last inputs 3:1 toward
        // the earlier one; the golden decoder does the same.
        assert_eq!(out, [0, 25, 75, 125, 125, 200]);
    }

    #[test]
    fn huffman_table_decodes_canonical_codes() {
        // Two 2-bit codes (00, 01) and one 3-bit code (100).
        let mut h = Huffman::new();
        let mut count = [0usize; 16];
        count[1] = 2;
        count[2] = 1;
        h.build(&count).unwrap();
        h.values[..3].copy_from_slice(&[5, 6, 7]);
        let data = [0b0001_1000u8, 0xFF, 0xD9];
        let mut s = Stream {
            src: &data,
            pos: 0,
            overrun: false,
            code_buffer: 0,
            code_bits: 0,
            marker: MARKER_NONE,
            nomore: false,
            todo: 0,
            eob_run: 0,
            spec_start: 0,
            spec_end: 0,
            succ_high: 0,
            succ_low: 0,
        };
        assert_eq!(s.huff_decode(&h), 5);
        assert_eq!(s.huff_decode(&h), 6);
        assert_eq!(s.huff_decode(&h), 7);
        assert_eq!(s.marker, 0xD9);
    }

    #[test]
    fn over_full_code_lengths_are_rejected() {
        let mut h = Huffman::new();
        let mut count = [0usize; 16];
        count[0] = 3; // three 1-bit codes cannot exist
        assert!(h.build(&count).is_err());
    }
}
