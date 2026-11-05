//! A markup `<number>` field's arithmetic: values as exact decimals (OpenAC
//! carries them as .NET `decimal`), the text it shows and reads, rounding,
//! clamping and stepping (OpenAC `UiMarkupNumber`).
//!
//! [`Dec`] holds what a .NET `decimal` holds for this use: a signed whole
//! number of at most 96 bits and a scale (digits after the point) of 0 to
//! 28. Arithmetic that would need more digits than fit drops the lowest
//! digits after the point, as `decimal` does.

use std::cmp::Ordering;
use std::fmt;

/// The largest whole-number part a .NET `decimal` holds (2^96 - 1).
const MAX_MANTISSA: i128 = (1i128 << 96) - 1;
/// The most digits after the point.
const MAX_SCALE: u32 = 28;

/// An exact decimal number: `mantissa / 10^scale`.
#[derive(Debug, Clone, Copy)]
pub struct Dec {
    mantissa: i128,
    scale: u32,
}

impl PartialEq for Dec {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other) == Ordering::Equal
    }
}

impl Eq for Dec {}

impl PartialOrd for Dec {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for Dec {
    fn cmp(&self, other: &Self) -> Ordering {
        match align(*self, *other) {
            Some((a, b, _)) => a.cmp(&b),
            // Too many digits to line up exactly: the whole parts decide
            // (they differ when the fractions could not line up).
            None => {
                let ai = self.mantissa / pow10(self.scale);
                let bi = other.mantissa / pow10(other.scale);
                ai.cmp(&bi)
            }
        }
    }
}

const fn pow10(n: u32) -> i128 {
    let mut v: i128 = 1;
    let mut i = 0;
    while i < n {
        v *= 10;
        i += 1;
    }
    v
}

/// Both at the larger scale, if that fits.
fn align(a: Dec, b: Dec) -> Option<(i128, i128, u32)> {
    let scale = a.scale.max(b.scale);
    let am = a.mantissa.checked_mul(pow10(scale - a.scale));
    let bm = b.mantissa.checked_mul(pow10(scale - b.scale));
    match (am, bm) {
        (Some(am), Some(bm)) => Some((am, bm, scale)),
        _ => None,
    }
}

/// `value / 10^by`, rounded half away from zero.
fn divide_round(value: i128, by: u32) -> i128 {
    if by == 0 {
        return value;
    }
    let d = pow10(by);
    let q = value / d;
    let r = value % d;
    if r.abs() * 2 >= d {
        q + value.signum()
    } else {
        q
    }
}

/// `value / 10`, rounded half to even.
fn divide_round_even(value: i128) -> i128 {
    let q = value / 10;
    let r = (value % 10).abs();
    if r > 5 || (r == 5 && q % 2 != 0) {
        q + value.signum()
    } else {
        q
    }
}

/// Why a number does not fit a `decimal`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Overflow;

impl Dec {
    /// Zero.
    pub const ZERO: Self = Self {
        mantissa: 0,
        scale: 0,
    };
    /// One.
    pub const ONE: Self = Self {
        mantissa: 1,
        scale: 0,
    };
    /// .NET `decimal.MaxValue`.
    pub const MAX: Self = Self {
        mantissa: MAX_MANTISSA,
        scale: 0,
    };
    /// .NET `decimal.MinValue`.
    pub const MIN: Self = Self {
        mantissa: -MAX_MANTISSA,
        scale: 0,
    };

    fn new(mantissa: i128, scale: u32) -> Result<Self, Overflow> {
        let mut m = mantissa;
        let mut s = scale;
        // Too many digits after the point: drop the lowest ones, rounding
        // half to even as decimal arithmetic does.
        while s > MAX_SCALE || (m.abs() > MAX_MANTISSA && s > 0) {
            m = divide_round_even(m);
            s -= 1;
        }
        if m.abs() > MAX_MANTISSA {
            return Err(Overflow);
        }
        Ok(Self {
            mantissa: m,
            scale: s,
        })
    }

    /// A whole number.
    #[must_use]
    pub fn from_i64(v: i64) -> Self {
        Self {
            mantissa: i128::from(v),
            scale: 0,
        }
    }

    /// A `float` as .NET converts it to `decimal`: rounded to 7 significant
    /// digits (so `0.1f` reads as 0.1), NaN as 0, and beyond the range as
    /// its limit.
    #[must_use]
    pub fn from_f32(v: f32) -> Self {
        if v.is_nan() {
            return Self::ZERO;
        }
        if v.is_infinite() {
            return if v > 0.0 { Self::MAX } else { Self::MIN };
        }
        let text = format!("{v:.6e}");
        match parse_scientific(&text) {
            Some(d) => d,
            None => {
                if v > 0.0 {
                    Self::MAX
                } else {
                    Self::MIN
                }
            }
        }
    }

    /// The nearest `float`.
    #[must_use]
    pub fn to_f32(self) -> f32 {
        self.to_string().parse().unwrap_or(0.0)
    }

    /// The whole-number part when it fits an `i32` (a value already rounded
    /// to no decimals and clamped to an `int` range).
    #[must_use]
    pub fn to_i32(self) -> i32 {
        let whole = self.mantissa / pow10(self.scale);
        i32::try_from(whole).unwrap_or(if whole < 0 { i32::MIN } else { i32::MAX })
    }

    /// Digits after the point as written.
    #[must_use]
    pub fn scale(self) -> u32 {
        self.scale
    }

    /// Rounded to `decimals` digits after the point, half away from zero
    /// (.NET `Math.Round(d, decimals, MidpointRounding.AwayFromZero)`); a
    /// value with fewer digits is unchanged.
    #[must_use]
    pub fn round(self, decimals: u32) -> Self {
        if self.scale <= decimals {
            return self;
        }
        Self {
            mantissa: divide_round(self.mantissa, self.scale - decimals),
            scale: decimals,
        }
    }

    /// The sum.
    ///
    /// # Errors
    /// The whole part outgrows a `decimal`.
    pub fn checked_add(self, other: Self) -> Result<Self, Overflow> {
        let mut a = self;
        let mut b = other;
        loop {
            if let Some((am, bm, scale)) = align(a, b) {
                return match am.checked_add(bm) {
                    Some(sum) => Self::new(sum, scale),
                    None => Err(Overflow),
                };
            }
            // Lining up overflows: drop a digit from the finer one.
            if a.scale >= b.scale && a.scale > 0 {
                a = a.round(a.scale - 1);
            } else if b.scale > 0 {
                b = b.round(b.scale - 1);
            } else {
                return Err(Overflow);
            }
        }
    }

    /// The product with a small whole number.
    ///
    /// # Errors
    /// The whole part outgrows a `decimal`.
    pub fn checked_mul_int(self, by: i64) -> Result<Self, Overflow> {
        match self.mantissa.checked_mul(i128::from(by)) {
            Some(m) => Self::new(m, self.scale),
            None => Err(Overflow),
        }
    }

    /// Kept within `min..=max`.
    #[must_use]
    pub fn clamp_to(self, min: Self, max: Self) -> Self {
        if self < min {
            min
        } else if self > max {
            max
        } else {
            self
        }
    }

    /// Below zero.
    #[must_use]
    pub fn is_negative(self) -> bool {
        self.mantissa < 0
    }

    /// Written with exactly `decimals` digits after the point (.NET
    /// `ToString("F<decimals>")`, rounding half away from zero).
    #[must_use]
    pub fn format(self, decimals: u32) -> String {
        let mut m = self.round(decimals).mantissa;
        let mut scale = self.scale.min(decimals);
        while scale < decimals {
            m *= 10;
            scale += 1;
        }
        let negative = m < 0;
        let digits = m.unsigned_abs().to_string();
        let digits = if digits.len() <= decimals as usize {
            format!(
                "{}{digits}",
                "0".repeat(decimals as usize + 1 - digits.len())
            )
        } else {
            digits
        };
        let split = digits.len() - decimals as usize;
        let mut out = String::new();
        if negative && m != 0 {
            out.push('-');
        }
        out.push_str(&digits[..split]);
        if decimals > 0 {
            out.push('.');
            out.push_str(&digits[split..]);
        }
        out
    }

    /// .NET `decimal.TryParse(text, NumberStyles.AllowLeadingSign |
    /// NumberStyles.AllowDecimalPoint, InvariantCulture)`: an optional sign,
    /// digits with at most one point; no spaces, no exponent.
    #[must_use]
    pub fn parse_plain(text: &str) -> Option<Self> {
        let (negative, rest) = match text.as_bytes().first() {
            Some(b'-') => (true, &text[1..]),
            Some(b'+') => (false, &text[1..]),
            _ => (false, text),
        };
        parse_digits(rest, negative)
    }

    /// .NET `decimal.TryParse(text, NumberStyles.Float, InvariantCulture)`:
    /// surrounding white space, a sign, a point and an exponent (a markup
    /// attribute).
    #[must_use]
    pub fn parse_float_style(text: &str) -> Option<Self> {
        let t = text.trim_matches(|c: char| c.is_ascii_whitespace());
        parse_scientific(t)
    }
}

fn parse_digits(rest: &str, negative: bool) -> Option<Dec> {
    if rest.is_empty() {
        return None;
    }
    let mut mantissa: i128 = 0;
    let mut scale = 0u32;
    let mut seen_point = false;
    let mut seen_digit = false;
    for c in rest.chars() {
        match c {
            '0'..='9' => {
                seen_digit = true;
                let d = i128::from(c as u8 - b'0');
                match mantissa.checked_mul(10).and_then(|m| m.checked_add(d)) {
                    Some(m) if m <= MAX_MANTISSA * 10 => {
                        mantissa = m;
                        if seen_point {
                            scale += 1;
                        }
                    }
                    // More digits than a decimal holds: past the point they
                    // are dropped, before it the number is too large.
                    _ if seen_point => {}
                    _ => return None,
                }
            }
            '.' if !seen_point => seen_point = true,
            _ => return None,
        }
    }
    if !seen_digit {
        return None;
    }
    let m = if negative { -mantissa } else { mantissa };
    Dec::new(m, scale).ok()
}

/// A number with an optional exponent (`1.5e3`).
fn parse_scientific(t: &str) -> Option<Dec> {
    let (negative, rest) = match t.as_bytes().first() {
        Some(b'-') => (true, &t[1..]),
        Some(b'+') => (false, &t[1..]),
        _ => (false, t),
    };
    let (body, exponent) = match rest.find(['e', 'E']) {
        Some(at) => {
            let e: i32 = rest[at + 1..].parse().ok()?;
            (&rest[..at], e)
        }
        None => (rest, 0),
    };
    let d = parse_digits(body, negative)?;
    if exponent >= 0 {
        let shift = u32::try_from(exponent).ok()?;
        if shift <= d.scale {
            return Some(Dec {
                mantissa: d.mantissa,
                scale: d.scale - shift,
            });
        }
        let extra = shift - d.scale;
        if extra > 38 {
            return None;
        }
        let m = d.mantissa.checked_mul(pow10(extra))?;
        Dec::new(m, 0).ok()
    } else {
        Dec::new(d.mantissa, d.scale + exponent.unsigned_abs()).ok()
    }
}

impl fmt::Display for Dec {
    /// As .NET writes a `decimal`: every digit of its scale.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.format(self.scale))
    }
}

/// The checked settings of one `<number>` (OpenAC `UiMarkupNumber`'s
/// `Min`, `Max`, `Step`, `Decimals`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct NumberSpec {
    /// The smallest value.
    pub min: Dec,
    /// The largest value.
    pub max: Dec,
    /// One step.
    pub step: Dec,
    /// Digits after the point.
    pub decimals: u32,
}

impl NumberSpec {
    /// How `value` shows: exactly [`Self::decimals`] digits after the point.
    #[must_use]
    pub fn format(&self, value: Dec) -> String {
        value.format(self.decimals)
    }

    /// What typing takes: digits always, a minus sign only when negatives
    /// are allowed, a point only with decimals.
    #[must_use]
    pub fn accepts_character(&self, c: char) -> bool {
        c.is_ascii_digit()
            || (c == '-' && self.min.is_negative())
            || (c == '.' && self.decimals > 0)
    }

    /// Typed text that is a number within range (otherwise it shows in
    /// red).
    #[must_use]
    pub fn is_valid_text(&self, text: &str) -> bool {
        Dec::parse_plain(text).is_some_and(|v| v >= self.min && v <= self.max)
    }

    /// What a requested value becomes: rounded to the decimals, then kept
    /// within range; `Some` only when that differs from the bound value (as
    /// rounded), which is when the plugin hears it.
    #[must_use]
    pub fn apply(&self, requested: Dec, current: Dec) -> Option<Dec> {
        let value = requested.round(self.decimals).clamp_to(self.min, self.max);
        (value != current.round(self.decimals)).then_some(value)
    }

    /// Enter or leaving the field with `text`: nothing when the text is the
    /// bound value as shown, or not a number (the bound value goes back);
    /// otherwise what [`Self::apply`] reports.
    #[must_use]
    pub fn commit(&self, text: &str, current: Dec) -> Option<Dec> {
        if text == self.format(current) {
            return None;
        }
        Dec::parse_plain(text).and_then(|typed| self.apply(typed, current))
    }

    /// One step up (`direction` +1) or down (-1), ten with Shift: from the
    /// number being typed when the field has focus and holds another valid
    /// number, else from the bound value.
    #[must_use]
    pub fn step_by(
        &self,
        direction: i64,
        shift: bool,
        typed: Option<&str>,
        current: Dec,
    ) -> Option<Dec> {
        let base = typed
            .filter(|t| *t != self.format(current))
            .and_then(Dec::parse_plain)
            .unwrap_or(current);
        let requested = self
            .step
            .checked_mul_int(direction * if shift { 10 } else { 1 })
            .and_then(|delta| base.checked_add(delta))
            .unwrap_or(if direction > 0 { self.max } else { self.min });
        self.apply(requested, current)
    }
}

#[cfg(test)]
mod tests;
