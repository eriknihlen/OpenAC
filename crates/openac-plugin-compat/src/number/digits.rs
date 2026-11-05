//! Decimal digits of a binary floating-point value, and the two rounding
//! rules .NET applies to them.
//!
//! .NET formats a number from a digit buffer: the decimal digits `d1 d2 ...`
//! and a scale such that the value is `0.d1d2... × 10^scale`, with no
//! trailing zeros. Standard formats (`F2`, `N0`, `G15`, ...) take the exact
//! digits of the binary value rounded half to even at the requested digit;
//! custom formats (`0.##`) first take 15 significant digits (7 for `float`)
//! and then round half up. Both are reproduced here on exact digits.

/// A value as .NET's number buffer holds it: `0.digits × 10^scale`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Digits {
    /// The sign bit; kept for zero (`-0` prints as `-0` for floating point).
    pub negative: bool,
    /// Decimal digits, values 0..=9, no leading or trailing zeros. Empty for zero.
    pub digits: Vec<u8>,
    /// Decimal exponent: the value is `0.d1d2... × 10^scale`. 0 for zero.
    pub scale: i32,
}

/// How a cut-off digit sequence is rounded.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Rounding {
    /// Exact ties go to the even digit (standard formats on exact digits).
    HalfEven,
    /// A first dropped digit of 5 or more rounds up (`RoundNumber` on a
    /// digit buffer that is not already correctly rounded).
    HalfUp,
}

impl Digits {
    pub fn zero(negative: bool) -> Self {
        Self {
            negative,
            digits: Vec::new(),
            scale: 0,
        }
    }

    pub fn is_zero(&self) -> bool {
        self.digits.is_empty()
    }

    /// The exact decimal expansion of a finite `f64`.
    pub fn exact_f64(value: f64) -> Self {
        let bits = value.to_bits();
        let negative = bits >> 63 != 0;
        let exponent_bits = ((bits >> 52) & 0x7FF) as i32;
        let fraction = bits & ((1u64 << 52) - 1);
        let (mut mantissa, mut exponent) = if exponent_bits == 0 {
            (fraction, -1074)
        } else {
            (fraction | (1u64 << 52), exponent_bits - 1075)
        };
        if mantissa == 0 {
            return Self::zero(negative);
        }
        while mantissa & 1 == 0 {
            mantissa >>= 1;
            exponent += 1;
        }
        let mut big = BigUint::from_u64(mantissa);
        let shift_back = if exponent >= 0 {
            big.shl(exponent as u32);
            0
        } else {
            // m × 2^-k = m × 5^k / 10^k.
            let k = (-exponent) as u32;
            big.mul_pow5(k);
            k as i32
        };
        let digits = big.into_decimal_digits();
        let scale = digits.len() as i32 - shift_back;
        let mut out = Self {
            negative,
            digits,
            scale,
        };
        out.trim();
        out
    }

    /// The shortest digits that read back as the same `f64` (closest to the
    /// exact value among the shortest), as .NET's `R` and plain `ToString()`.
    pub fn shortest_f64(value: f64) -> Self {
        let shortest = Self::from_scientific(&format!("{value:e}"), value.is_sign_negative());
        let exact = Self::exact_f64(value);
        shortest.tie_to_even(&exact, |text| text.parse::<f64>().ok() == Some(value))
    }

    /// The shortest digits that read back as the same `f32`.
    pub fn shortest_f32(value: f32) -> Self {
        let shortest = Self::from_scientific(&format!("{value:e}"), value.is_sign_negative());
        let exact = Self::exact_f64(f64::from(value));
        shortest.tie_to_even(&exact, |text| text.parse::<f32>().ok() == Some(value))
    }

    /// When the exact value lies exactly halfway between two shortest
    /// candidates, .NET takes the one with the even last digit (`1281711.25f`
    /// prints `1281711.2`, `1281711.75f` prints `1281711.8`); Rust's
    /// formatter rounds such ties up.
    fn tie_to_even(self, exact: &Self, reads_back: impl Fn(&str) -> bool) -> Self {
        let n = self.digits.len();
        if n == 0 || exact.digits.len() != n + 1 || exact.digits[n] != 5 {
            return self;
        }
        let even = exact.round_at(n as i64, Rounding::HalfEven);
        if even == self || !reads_back(&even.to_scientific_text()) {
            return self;
        }
        even
    }

    /// `0.ddd e scale`, text Rust's parser reads.
    fn to_scientific_text(&self) -> String {
        let digits: String = self.digits.iter().map(|&d| char::from(b'0' + d)).collect();
        format!(
            "{}0.{}e{}",
            if self.negative { "-" } else { "" },
            if digits.is_empty() { "0" } else { &digits },
            self.scale
        )
    }

    /// Reads Rust's `{:e}` output (`-1.2345e-3`, `1e15`, `0e0`).
    fn from_scientific(text: &str, negative: bool) -> Self {
        let text = text.trim_start_matches('-');
        let (mantissa, exponent) = text.split_once('e').unwrap_or((text, "0"));
        let exponent: i32 = exponent.parse().unwrap_or(0);
        let mut digits: Vec<u8> = mantissa
            .bytes()
            .filter(u8::is_ascii_digit)
            .map(|b| b - b'0')
            .collect();
        // Leading zeros only occur for zero itself ("0e0").
        while digits.first() == Some(&0) {
            digits.remove(0);
        }
        if digits.is_empty() {
            return Self::zero(negative);
        }
        let mut out = Self {
            negative,
            digits,
            scale: exponent + 1,
        };
        out.trim();
        out
    }

    /// Digits of an unsigned integer.
    pub fn from_u128(negative: bool, value: u128) -> Self {
        if value == 0 {
            return Self::zero(negative);
        }
        let digits: Vec<u8> = value.to_string().bytes().map(|b| b - b'0').collect();
        let scale = digits.len() as i32;
        let mut out = Self {
            negative,
            digits,
            scale,
        };
        out.trim();
        out
    }

    /// Drops trailing zero digits; an all-zero buffer becomes zero with scale 0.
    fn trim(&mut self) {
        while self.digits.last() == Some(&0) {
            self.digits.pop();
        }
        if self.digits.is_empty() {
            self.scale = 0;
        }
    }

    /// Keeps the first `keep` digits (counted from the first significant
    /// digit; zero or negative keeps none) and rounds the rest away.
    /// The sign is kept when the result is zero (floating point).
    pub fn round_at(&self, keep: i64, rounding: Rounding) -> Self {
        let len = self.digits.len() as i64;
        if keep >= len {
            return self.clone();
        }
        let round_up = if keep < 0 {
            false
        } else {
            let first_dropped = self.digits[keep as usize];
            match rounding {
                Rounding::HalfUp => first_dropped >= 5,
                Rounding::HalfEven => {
                    let rest_nonzero = self.digits[keep as usize + 1..].iter().any(|&d| d != 0);
                    let previous_odd = keep > 0 && self.digits[keep as usize - 1] % 2 == 1;
                    first_dropped > 5 || (first_dropped == 5 && (rest_nonzero || previous_odd))
                }
            }
        };
        let mut digits: Vec<u8> = self.digits[..keep.max(0) as usize].to_vec();
        let mut scale = self.scale;
        if round_up {
            let mut i = digits.len();
            loop {
                if i == 0 {
                    // Every kept digit was 9 (or none was kept): one more digit.
                    digits.insert(0, 1);
                    scale += 1;
                    break;
                }
                i -= 1;
                if digits[i] == 9 {
                    digits[i] = 0;
                } else {
                    digits[i] += 1;
                    break;
                }
            }
        }
        let mut out = Self {
            negative: self.negative,
            digits,
            scale,
        };
        out.trim();
        out
    }

    /// Rounded to `count` significant digits.
    pub fn round_significant(&self, count: u32, rounding: Rounding) -> Self {
        self.round_at(i64::from(count), rounding)
    }

    /// Rounded to `decimals` digits after the decimal point.
    pub fn round_decimals(&self, decimals: u32, rounding: Rounding) -> Self {
        self.round_at(i64::from(self.scale) + i64::from(decimals), rounding)
    }

    /// The digit at position `index` from the first significant digit, or 0.
    pub fn digit(&self, index: i64) -> u8 {
        if index < 0 {
            return 0;
        }
        self.digits.get(index as usize).copied().unwrap_or(0)
    }
}

/// `(double)BigInteger` for a decimal integer: the magnitude truncated to a
/// double (the largest double not above it), infinity from 2^1024 up. Not
/// correctly rounded: `99999999999999999999` gives `99999999999999983616`.
pub(crate) fn truncating_integer_to_f64(text: &str) -> f64 {
    let negative = text.starts_with('-');
    let magnitude = text.trim_start_matches(['-', '+']).trim_start_matches('0');
    let integer = Digits::from_decimal_integer(magnitude);
    let two_pow_1024 = {
        let mut big = BigUint::from_u64(1);
        big.shl(1024);
        let digits = big.into_decimal_digits();
        let scale = digits.len() as i32;
        Digits {
            negative: false,
            digits,
            scale,
        }
    };
    let result = if compare_magnitude(&integer, &two_pow_1024).is_ge() {
        f64::INFINITY
    } else {
        let rounded = magnitude.parse::<f64>().unwrap_or(f64::MAX).min(f64::MAX);
        if compare_magnitude(&Digits::exact_f64(rounded), &integer).is_gt() {
            rounded.next_down()
        } else {
            rounded
        }
    };
    if negative { -result } else { result }
}

/// Compares two non-negative digit buffers by value.
fn compare_magnitude(a: &Digits, b: &Digits) -> std::cmp::Ordering {
    match (a.is_zero(), b.is_zero()) {
        (true, true) => return std::cmp::Ordering::Equal,
        (true, false) => return std::cmp::Ordering::Less,
        (false, true) => return std::cmp::Ordering::Greater,
        _ => {}
    }
    a.scale.cmp(&b.scale).then_with(|| {
        let len = a.digits.len().max(b.digits.len()) as i64;
        (0..len)
            .map(|i| a.digit(i).cmp(&b.digit(i)))
            .find(|o| o.is_ne())
            .unwrap_or(std::cmp::Ordering::Equal)
    })
}

impl Digits {
    /// Digits of a decimal integer written without sign or leading zeros.
    fn from_decimal_integer(text: &str) -> Self {
        let digits: Vec<u8> = text.bytes().map(|b| b - b'0').collect();
        let scale = digits.len() as i32;
        let mut out = Self {
            negative: false,
            digits,
            scale,
        };
        out.trim();
        out
    }
}

/// A minimal unsigned big integer (little-endian 32-bit limbs), enough to
/// expand a double exactly.
struct BigUint {
    limbs: Vec<u32>,
}

impl BigUint {
    fn from_u64(value: u64) -> Self {
        let mut limbs = vec![value as u32, (value >> 32) as u32];
        while limbs.last() == Some(&0) {
            limbs.pop();
        }
        Self { limbs }
    }

    fn shl(&mut self, bits: u32) {
        let words = (bits / 32) as usize;
        let rest = bits % 32;
        if rest != 0 {
            let mut carry = 0u32;
            for limb in &mut self.limbs {
                let next = *limb >> (32 - rest);
                *limb = (*limb << rest) | carry;
                carry = next;
            }
            if carry != 0 {
                self.limbs.push(carry);
            }
        }
        if words > 0 {
            self.limbs.splice(0..0, std::iter::repeat_n(0, words));
        }
    }

    fn mul_small(&mut self, factor: u32) {
        let mut carry = 0u64;
        for limb in &mut self.limbs {
            let product = u64::from(*limb) * u64::from(factor) + carry;
            *limb = product as u32;
            carry = product >> 32;
        }
        if carry != 0 {
            self.limbs.push(carry as u32);
        }
    }

    fn mul_pow5(&mut self, mut power: u32) {
        // 5^13 is the largest power of five below 2^32.
        const POW5_13: u32 = 1_220_703_125;
        while power >= 13 {
            self.mul_small(POW5_13);
            power -= 13;
        }
        if power > 0 {
            self.mul_small(5u32.pow(power));
        }
    }

    /// Divides in place, returning the remainder.
    fn div_small(&mut self, divisor: u32) -> u32 {
        let mut remainder = 0u64;
        for limb in self.limbs.iter_mut().rev() {
            let value = (remainder << 32) | u64::from(*limb);
            *limb = (value / u64::from(divisor)) as u32;
            remainder = value % u64::from(divisor);
        }
        while self.limbs.last() == Some(&0) {
            self.limbs.pop();
        }
        remainder as u32
    }

    fn into_decimal_digits(mut self) -> Vec<u8> {
        const CHUNK: u32 = 1_000_000_000;
        let mut chunks = Vec::new();
        while !self.limbs.is_empty() {
            chunks.push(self.div_small(CHUNK));
        }
        let mut digits = Vec::with_capacity(chunks.len() * 9);
        for (i, chunk) in chunks.iter().rev().enumerate() {
            let text = if i == 0 {
                chunk.to_string()
            } else {
                format!("{chunk:09}")
            };
            digits.extend(text.bytes().map(|b| b - b'0'));
        }
        digits
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn text(d: &Digits) -> String {
        let digits: String = d.digits.iter().map(|x| char::from(b'0' + x)).collect();
        format!(
            "{}0.{}e{}",
            if d.negative { "-" } else { "" },
            digits,
            d.scale
        )
    }

    #[test]
    fn exact_expansion() {
        assert_eq!(text(&Digits::exact_f64(0.125)), "0.125e0");
        assert_eq!(text(&Digits::exact_f64(-2.5)), "-0.25e1");
        assert_eq!(
            text(&Digits::exact_f64(0.1)),
            "0.1000000000000000055511151231257827021181583404541015625e0"
        );
        assert_eq!(text(&Digits::exact_f64(1e21)), "0.1e22");
        assert_eq!(text(&Digits::exact_f64(-0.0)), "-0.e0");
        let tiny = Digits::exact_f64(f64::from_bits(1));
        assert_eq!(tiny.scale, -323);
        assert_eq!(tiny.digits.len(), 751);
    }

    #[test]
    fn shortest_digits() {
        assert_eq!(text(&Digits::shortest_f64(0.1)), "0.1e0");
        assert_eq!(text(&Digits::shortest_f64(1e15)), "0.1e16");
        assert_eq!(text(&Digits::shortest_f64(-1234.5)), "-0.12345e4");
        assert_eq!(text(&Digits::shortest_f32(0.1)), "0.1e0");
    }

    #[test]
    fn rounding_rules() {
        let d = Digits::exact_f64(0.125);
        assert_eq!(text(&d.round_decimals(2, Rounding::HalfEven)), "0.12e0");
        assert_eq!(text(&d.round_decimals(2, Rounding::HalfUp)), "0.13e0");
        let nines = Digits::exact_f64(9.995);
        assert_eq!(text(&nines.round_decimals(1, Rounding::HalfEven)), "0.1e2");
        assert_eq!(
            text(&Digits::exact_f64(0.5).round_at(0, Rounding::HalfUp)),
            "0.1e1"
        );
        assert_eq!(
            text(&Digits::exact_f64(0.5).round_at(0, Rounding::HalfEven)),
            "0.e0"
        );
        assert_eq!(
            text(&Digits::exact_f64(0.05).round_at(-1, Rounding::HalfUp)),
            "0.e0"
        );
    }
}
