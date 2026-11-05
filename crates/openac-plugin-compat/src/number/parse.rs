//! `TryParse(text, NumberStyles, CultureInfo.InvariantCulture)` for numbers,
//! as .NET 10 accepts and rounds them.

/// .NET's `NumberStyles` flags.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct NumberStyles(u32);

impl NumberStyles {
    /// No flags: digits only.
    pub const NONE: Self = Self(0);
    /// Whitespace before the number.
    pub const ALLOW_LEADING_WHITE: Self = Self(0x1);
    /// Whitespace after the number.
    pub const ALLOW_TRAILING_WHITE: Self = Self(0x2);
    /// A sign before the number.
    pub const ALLOW_LEADING_SIGN: Self = Self(0x4);
    /// A sign after the number.
    pub const ALLOW_TRAILING_SIGN: Self = Self(0x8);
    /// Parentheses for a negative number.
    pub const ALLOW_PARENTHESES: Self = Self(0x10);
    /// A decimal point.
    pub const ALLOW_DECIMAL_POINT: Self = Self(0x20);
    /// Group separators (`,`) in the integer part.
    pub const ALLOW_THOUSANDS: Self = Self(0x40);
    /// An exponent (`e5`, `E-3`).
    pub const ALLOW_EXPONENT: Self = Self(0x80);
    /// The currency symbol (`¤` in the invariant culture).
    pub const ALLOW_CURRENCY_SYMBOL: Self = Self(0x100);
    /// Hexadecimal digits (integers only).
    pub const ALLOW_HEX_SPECIFIER: Self = Self(0x200);
    /// `NumberStyles.Integer`.
    pub const INTEGER: Self = Self(0x7);
    /// `NumberStyles.HexNumber`.
    pub const HEX_NUMBER: Self = Self(0x203);
    /// `NumberStyles.Number`.
    pub const NUMBER: Self = Self(0x6F);
    /// `NumberStyles.Float`.
    pub const FLOAT: Self = Self(0xA7);
    /// `NumberStyles.Currency`.
    pub const CURRENCY: Self = Self(0x17F);
    /// `NumberStyles.Any`.
    pub const ANY: Self = Self(0x1FF);

    /// Both sets of flags.
    #[must_use]
    pub const fn union(self, other: Self) -> Self {
        Self(self.0 | other.0)
    }

    const fn has(self, flag: Self) -> bool {
        self.0 & flag.0 != 0
    }
}

impl std::ops::BitOr for NumberStyles {
    type Output = Self;
    fn bitor(self, rhs: Self) -> Self {
        self.union(rhs)
    }
}

const POSITIVE_SIGN: char = '+';
const NEGATIVE_SIGN: char = '-';
const DECIMAL_SEPARATOR: char = '.';
const GROUP_SEPARATOR: char = ',';
const CURRENCY_SYMBOL: char = '\u{a4}';

/// .NET's `IsWhite`: space and U+0009..U+000D only.
fn is_white(c: char) -> bool {
    c == ' ' || ('\u{9}'..='\u{d}').contains(&c)
}

/// The parsed digits: `0.digits × 10^scale`, trailing zeros dropped.
#[derive(Debug)]
struct NumberBuffer {
    digits: Vec<u8>,
    scale: i64,
    negative: bool,
    /// The text had a decimal point.
    decimal: bool,
}

/// .NET's `TryParseNumber` for the invariant culture, followed by the
/// trailing-NUL check of `TryStringToNumber`. `integer` is the integer
/// buffer kind (a zero is never negative).
fn parse_number(text: &str, styles: NumberStyles, integer: bool) -> Option<NumberBuffer> {
    const STATE_SIGN: u32 = 0x1;
    const STATE_PARENS: u32 = 0x2;
    const STATE_DIGITS: u32 = 0x4;
    const STATE_NON_ZERO: u32 = 0x8;
    const STATE_DECIMAL: u32 = 0x10;
    const STATE_CURRENCY: u32 = 0x20;

    let chars: Vec<char> = text.chars().collect();
    let at = |p: usize| chars.get(p).copied().unwrap_or('\0');
    let mut number = NumberBuffer {
        digits: Vec::new(),
        scale: 0,
        negative: false,
        decimal: false,
    };
    let mut currency = styles.has(NumberStyles::ALLOW_CURRENCY_SYMBOL);
    let mut state = 0u32;
    let mut p = 0usize;
    let mut ch = at(p);

    loop {
        // Whitespace is eaten only before a sign ("- 1" is refused).
        if !is_white(ch)
            || !styles.has(NumberStyles::ALLOW_LEADING_WHITE)
            || (state & STATE_SIGN != 0 && state & STATE_CURRENCY == 0)
        {
            if styles.has(NumberStyles::ALLOW_LEADING_SIGN)
                && state & STATE_SIGN == 0
                && (ch == POSITIVE_SIGN || ch == NEGATIVE_SIGN)
            {
                if ch == NEGATIVE_SIGN {
                    number.negative = true;
                }
                state |= STATE_SIGN;
            } else if ch == '('
                && styles.has(NumberStyles::ALLOW_PARENTHESES)
                && state & STATE_SIGN == 0
            {
                state |= STATE_SIGN | STATE_PARENS;
                number.negative = true;
            } else if currency && ch == CURRENCY_SYMBOL {
                state |= STATE_CURRENCY;
                currency = false;
            } else {
                break;
            }
        }
        p += 1;
        ch = at(p);
    }

    loop {
        if ch.is_ascii_digit() {
            state |= STATE_DIGITS;
            if ch != '0' || state & STATE_NON_ZERO != 0 {
                number.digits.push(ch as u8 - b'0');
                if state & STATE_DECIMAL == 0 {
                    number.scale += 1;
                }
                state |= STATE_NON_ZERO;
            } else if state & STATE_DECIMAL != 0 {
                number.scale -= 1;
            }
        } else if styles.has(NumberStyles::ALLOW_DECIMAL_POINT)
            && state & STATE_DECIMAL == 0
            && ch == DECIMAL_SEPARATOR
        {
            state |= STATE_DECIMAL;
            number.decimal = true;
        } else if styles.has(NumberStyles::ALLOW_THOUSANDS)
            && state & STATE_DIGITS != 0
            && state & STATE_DECIMAL == 0
            && ch == GROUP_SEPARATOR
        {
        } else {
            break;
        }
        p += 1;
        ch = at(p);
    }

    if state & STATE_DIGITS == 0 {
        return None;
    }
    if (ch == 'E' || ch == 'e') && styles.has(NumberStyles::ALLOW_EXPONENT) {
        let temp = p;
        p += 1;
        ch = at(p);
        let mut negative_exponent = false;
        if ch == POSITIVE_SIGN {
            p += 1;
            ch = at(p);
        } else if ch == NEGATIVE_SIGN {
            p += 1;
            ch = at(p);
            negative_exponent = true;
        }
        if ch.is_ascii_digit() {
            let mut exponent: i64 = 0;
            loop {
                if exponent >= 100_000_000 {
                    // Too large to matter: the value is infinite or zero.
                    exponent = i64::from(i32::MAX);
                    number.scale = 0;
                    while ch.is_ascii_digit() {
                        p += 1;
                        ch = at(p);
                    }
                    break;
                }
                exponent = exponent * 10 + i64::from(ch as u8 - b'0');
                p += 1;
                ch = at(p);
                if !ch.is_ascii_digit() {
                    break;
                }
            }
            if negative_exponent {
                exponent = -exponent;
            }
            number.scale += exponent;
        } else {
            p = temp;
            ch = at(p);
        }
    }

    loop {
        if !is_white(ch) || !styles.has(NumberStyles::ALLOW_TRAILING_WHITE) {
            if styles.has(NumberStyles::ALLOW_TRAILING_SIGN)
                && state & STATE_SIGN == 0
                && (ch == POSITIVE_SIGN || ch == NEGATIVE_SIGN)
            {
                if ch == NEGATIVE_SIGN {
                    number.negative = true;
                }
                state |= STATE_SIGN;
            } else if ch == ')' && state & STATE_PARENS != 0 {
                state &= !STATE_PARENS;
            } else if currency && ch == CURRENCY_SYMBOL {
                currency = false;
            } else {
                break;
            }
        }
        p += 1;
        ch = at(p);
    }
    if state & STATE_PARENS != 0 {
        return None;
    }
    if state & STATE_NON_ZERO == 0 {
        number.scale = 0;
        if integer && state & STATE_DECIMAL == 0 {
            number.negative = false;
        }
    }
    // Only NULs may follow the number.
    if chars[p.min(chars.len())..].iter().any(|&c| c != '\0') {
        return None;
    }
    while number.digits.last() == Some(&0) {
        number.digits.pop();
    }
    Some(number)
}

/// The value of an integer buffer, as `TryNumberToInt64`/`UInt64` check it:
/// no fraction digits, at most `max_digits` integer digits, no overflow.
fn to_magnitude(number: &NumberBuffer, max_digits: i64) -> Option<u128> {
    let scale = number.scale;
    if scale > max_digits || scale < number.digits.len() as i64 {
        return None;
    }
    let mut value: u128 = 0;
    for i in 0..scale {
        value = value * 10 + u128::from(number.digits.get(i as usize).copied().unwrap_or(0));
    }
    Some(value)
}

fn parse_hex(text: &str, styles: NumberStyles, bits: u32) -> Option<u64> {
    let mut rest = text;
    if styles.has(NumberStyles::ALLOW_LEADING_WHITE) {
        rest = rest.trim_start_matches(is_white);
    }
    let digits_end = rest
        .find(|c: char| !c.is_ascii_hexdigit())
        .unwrap_or(rest.len());
    let (digits, mut tail) = rest.split_at(digits_end);
    if digits.is_empty() {
        return None;
    }
    if styles.has(NumberStyles::ALLOW_TRAILING_WHITE) {
        tail = tail.trim_start_matches(is_white);
    }
    if tail.chars().any(|c| c != '\0') {
        return None;
    }
    let significant = digits.trim_start_matches('0');
    if significant.len() > (bits / 4) as usize {
        return None;
    }
    if significant.is_empty() {
        return Some(0);
    }
    u64::from_str_radix(significant, 16).ok()
}

/// Styles .NET accepts for an integer parse: the hex specifier only with
/// whitespace flags.
fn integer_styles_valid(styles: NumberStyles) -> bool {
    !styles.has(NumberStyles::ALLOW_HEX_SPECIFIER) || styles.0 & !NumberStyles::HEX_NUMBER.0 == 0
}

macro_rules! signed_parser {
    ($name:ident, $ty:ty, $bits:expr, $max_digits:expr, $doc:expr) => {
        #[doc = $doc]
        pub fn $name(text: &str, styles: NumberStyles) -> Option<$ty> {
            if !integer_styles_valid(styles) {
                return None;
            }
            if styles.has(NumberStyles::ALLOW_HEX_SPECIFIER) {
                // The digits are the two's complement bits.
                return parse_hex(text, styles, $bits).map(|v| v as $ty);
            }
            let number = parse_number(text, styles, true)?;
            let magnitude = to_magnitude(&number, $max_digits)?;
            if number.negative {
                let limit = u128::from(<$ty>::MIN.unsigned_abs());
                (magnitude <= limit).then(|| (magnitude as i128).wrapping_neg() as $ty)
            } else {
                <$ty>::try_from(magnitude).ok()
            }
        }
    };
}

macro_rules! unsigned_parser {
    ($name:ident, $ty:ty, $bits:expr, $max_digits:expr, $doc:expr) => {
        #[doc = $doc]
        pub fn $name(text: &str, styles: NumberStyles) -> Option<$ty> {
            if !integer_styles_valid(styles) {
                return None;
            }
            if styles.has(NumberStyles::ALLOW_HEX_SPECIFIER) {
                return parse_hex(text, styles, $bits).map(|v| v as $ty);
            }
            let number = parse_number(text, styles, true)?;
            // A zero integer is not negative ("-0"), but "-0.0" is refused.
            if number.negative {
                return None;
            }
            let magnitude = to_magnitude(&number, $max_digits)?;
            <$ty>::try_from(magnitude).ok()
        }
    };
}

signed_parser!(
    parse_i32,
    i32,
    32,
    10,
    "`int.TryParse(text, styles, InvariantCulture)`; `None` where it returns false."
);
signed_parser!(
    parse_i64,
    i64,
    64,
    19,
    "`long.TryParse(text, styles, InvariantCulture)`; `None` where it returns false."
);
unsigned_parser!(
    parse_u32,
    u32,
    32,
    10,
    "`uint.TryParse(text, styles, InvariantCulture)`; `None` where it returns false."
);
unsigned_parser!(
    parse_u64,
    u64,
    64,
    20,
    "`ulong.TryParse(text, styles, InvariantCulture)`; `None` where it returns false."
);

/// The named values `double.TryParse` accepts when the number does not parse
/// (`Infinity`, `-Infinity`, `NaN`, `+Infinity`, `+NaN`, `-NaN`, any case),
/// after trimming the whitespace the styles allow.
fn named_value(text: &str, styles: NumberStyles) -> Option<f64> {
    let mut trimmed = text;
    if styles.has(NumberStyles::ALLOW_LEADING_WHITE) {
        trimmed = trimmed.trim_start_matches(is_white);
    }
    if styles.has(NumberStyles::ALLOW_TRAILING_WHITE) {
        trimmed = trimmed.trim_end_matches(is_white);
    }
    let is = |a: &str, b: &str| a.eq_ignore_ascii_case(b);
    if is(trimmed, "Infinity") {
        Some(f64::INFINITY)
    } else if is(trimmed, "-Infinity") {
        Some(f64::NEG_INFINITY)
    } else if is(trimmed, "NaN") {
        Some(f64::NAN)
    } else if let Some(rest) = trimmed.strip_prefix('+') {
        if is(rest, "Infinity") {
            Some(f64::INFINITY)
        } else if is(rest, "NaN") {
            Some(f64::NAN)
        } else {
            None
        }
    } else if let Some(rest) = trimmed.strip_prefix('-') {
        is(rest, "NaN").then_some(f64::NAN)
    } else {
        None
    }
}

/// The digits as text Rust's correctly rounded parser reads.
fn scientific_text(number: &NumberBuffer) -> String {
    let digits: String = number
        .digits
        .iter()
        .map(|&d| char::from(b'0' + d))
        .collect();
    // Beyond these the value is infinite or zero whatever the digits.
    let scale = number.scale.clamp(-400_000, 400_000);
    format!(
        "{}0.{}e{}",
        if number.negative { "-" } else { "" },
        digits,
        scale
    )
}

/// `double.TryParse(text, styles, InvariantCulture)`: correctly rounded;
/// overflow gives infinity. `None` where .NET returns false.
pub fn parse_f64(text: &str, styles: NumberStyles) -> Option<f64> {
    if styles.has(NumberStyles::ALLOW_HEX_SPECIFIER) {
        return None;
    }
    match parse_number(text, styles, false) {
        Some(number) if number.digits.is_empty() => Some(if number.negative { -0.0 } else { 0.0 }),
        Some(number) => scientific_text(&number).parse().ok(),
        None => named_value(text, styles),
    }
}

/// `float.TryParse(text, styles, InvariantCulture)`: rounded once, straight
/// to `float`.
pub fn parse_f32(text: &str, styles: NumberStyles) -> Option<f32> {
    if styles.has(NumberStyles::ALLOW_HEX_SPECIFIER) {
        return None;
    }
    match parse_number(text, styles, false) {
        Some(number) if number.digits.is_empty() => Some(if number.negative { -0.0 } else { 0.0 }),
        Some(number) => scientific_text(&number).parse().ok(),
        None => named_value(text, styles).map(|v| v as f32),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plugin_shapes() {
        // .NET 10, invariant culture.
        assert_eq!(parse_f64(" 1.5e-3 ", NumberStyles::FLOAT), Some(0.0015));
        assert_eq!(parse_f64("1,000", NumberStyles::FLOAT), None);
        assert_eq!(
            parse_f64("1,000", NumberStyles::FLOAT | NumberStyles::ALLOW_THOUSANDS),
            Some(1000.0)
        );
        assert_eq!(
            parse_f64("-infinity", NumberStyles::FLOAT),
            Some(f64::NEG_INFINITY)
        );
        assert_eq!(parse_i32("1.0", NumberStyles::FLOAT), Some(1));
        assert_eq!(parse_i32("1.50", NumberStyles::FLOAT), None);
        assert_eq!(
            parse_i32("-2147483648", NumberStyles::INTEGER),
            Some(i32::MIN)
        );
        assert_eq!(
            parse_i32("80000000", NumberStyles::HEX_NUMBER),
            Some(i32::MIN)
        );
        assert_eq!(parse_u32("  -0", NumberStyles::INTEGER), Some(0));
        assert_eq!(parse_i32("- 1", NumberStyles::INTEGER), None);
    }
}
