//! `ToString(format, CultureInfo.InvariantCulture)` for numbers, as .NET 10
//! prints them.
//!
//! Every format .NET 10 has for these types: the standard formats `G`/`R`,
//! `E`, `F`, `N`, `P`, `C` (with or without precision) for floating point and
//! integers, plus `D`, `X`, `B` for integers; and every custom format: digit
//! placeholders `0` and `#`, `.` and `,`, `%` and `‰`, exponents (`E+0`,
//! `e-00`), quoted (`'..'`, `".."`) and escaped (`\x`) literal text, and up
//! to three `;` sections (positive; negative; zero). A standard letter .NET
//! does not know, or one that does not apply to the type (`D` on a double),
//! is [`FormatError::Invalid`], where .NET throws `FormatException`.

use super::digits::{Digits, Rounding};

/// A format string this module cannot print exactly as .NET does.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum FormatError {
    /// .NET accepts it, but this kit does not reproduce it.
    #[error("format string {0:?} is not supported by the compatibility kit")]
    Unsupported(String),
    /// .NET throws `FormatException` for it.
    #[error("format string {0:?} is invalid for this type")]
    Invalid(String),
}

/// Why a format string is not a format.
enum BadSpecifier {
    /// More than nine precision digits (`G1000000000`).
    Precision,
    /// A standard letter .NET does not know (`Q`, `Z5`).
    Letter,
}

/// The decimal separator, group separator and group size of the invariant culture.
const DECIMAL_SEPARATOR: char = '.';
const GROUP_SEPARATOR: char = ',';
const GROUP_SIZE: usize = 3;
/// The invariant culture's percent, per-mille and currency symbols.
const PERCENT_SYMBOL: char = '%';
const PER_MILLE_SYMBOL: char = '\u{2030}';
const CURRENCY_SYMBOL: char = '\u{a4}';
/// `NumberFormatInfo.InvariantInfo.NumberDecimalDigits`, `PercentDecimalDigits`
/// and `CurrencyDecimalDigits`.
const DEFAULT_DECIMALS: u32 = 2;
/// Digits after the point of `E` without a precision (`DefaultPrecisionExponentialFormat`).
const DEFAULT_EXPONENT_DECIMALS: u32 = 6;
/// The fewest exponent digits of `E` (`1.0E+003`) and of `G` (`1E+15`).
const SCIENTIFIC_EXPONENT_DIGITS: usize = 3;
const GENERAL_EXPONENT_DIGITS: usize = 2;
/// A custom exponent (`0E+000000000000`) prints at most ten digits.
const MAX_CUSTOM_EXPONENT_DIGITS: usize = 10;
/// A standard format's precision stops being read at this value: a tenth
/// digit after it is a `FormatException`.
const MAX_PRECISION_BEFORE_LAST_DIGIT: u32 = 100_000_000;

/// Significant digits a custom format starts from (`DoublePrecisionCustomFormat`,
/// `SinglePrecisionCustomFormat`).
const DOUBLE_CUSTOM_DIGITS: u32 = 15;
const SINGLE_CUSTOM_DIGITS: u32 = 7;
/// The shortest round-trip form switches to scientific notation past this many
/// integer digits: the most digits a round trip needs (17 for `double`, 9 for `float`).
const DOUBLE_GENERAL_DIGITS: usize = 17;
const SINGLE_GENERAL_DIGITS: usize = 9;

/// What a number buffer holds, which decides two .NET rules: floating point
/// keeps a negative sign on a value that rounds to zero; integers and
/// `decimal` drop it. Floating-point digits are exact and round half to even
/// in standard formats; `decimal` digits round half up.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Kind {
    Floating,
    Integer,
    Decimal,
}

/// A parsed .NET numeric format string.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum NumberFormat {
    /// `G`/`g` (and `R`/`r`, the same on .NET Core 3.0 and later). A precision
    /// of 0 is the shortest round-trip form.
    General {
        /// Significant digits; 0 for the default.
        precision: u32,
        /// `E` (upper) or `e` in the exponent.
        upper: bool,
    },
    /// `E`/`e`: one digit, the point, `precision` digits, and an exponent of
    /// at least three digits (`1.234560E+003`).
    Scientific {
        /// Digits after the point.
        precision: u32,
        /// `E` (upper) or `e`.
        upper: bool,
    },
    /// `F`: fixed point.
    Fixed {
        /// Digits after the point.
        decimals: u32,
    },
    /// `N`: fixed point with group separators.
    Number {
        /// Digits after the point.
        decimals: u32,
    },
    /// `P`: the value times 100, `n %`.
    Percent {
        /// Digits after the point.
        decimals: u32,
    },
    /// `C`: currency, invariant patterns `¤n` and `(¤n)`, grouped.
    Currency {
        /// Digits after the point.
        decimals: u32,
    },
    /// `D`: integer digits, zero-padded (integers only).
    Decimal {
        /// Minimum digit count.
        digits: u32,
    },
    /// `X`/`x`: hexadecimal of the two's complement (integers only).
    Hex {
        /// Minimum digit count.
        digits: u32,
        /// Upper-case digits.
        upper: bool,
    },
    /// `B`/`b`: binary of the two's complement (integers only).
    Binary {
        /// Minimum digit count.
        digits: u32,
    },
    /// A custom format.
    Custom(CustomFormat),
}

impl NumberFormat {
    /// Reads a format string as .NET does (`Number.ParseFormatSpecifier`): a
    /// letter followed by at most nine digits is a standard format, anything
    /// else a custom one. Reading stops at a NUL character.
    pub fn parse(format: &str) -> Result<Self, FormatError> {
        Self::read(format).map_err(|_| FormatError::Invalid(format.to_owned()))
    }

    /// [`NumberFormat::parse`], telling apart the two ways it fails: .NET
    /// throws for an unreadable precision at once, but for an unknown letter
    /// only after it has printed a non-finite value's symbol.
    fn read(format: &str) -> Result<Self, BadSpecifier> {
        let format = format.split('\0').next().unwrap_or_default();
        let bytes = format.as_bytes();
        if format.is_empty() {
            return Ok(Self::General {
                precision: 0,
                upper: true,
            });
        }
        let standard = bytes[0].is_ascii_alphabetic() && bytes[1..].iter().all(u8::is_ascii_digit);
        if !standard {
            return Ok(Self::Custom(CustomFormat::new(format)));
        }
        let precision = if bytes.len() > 1 {
            let mut value = 0u32;
            for &digit in &bytes[1..] {
                if value >= MAX_PRECISION_BEFORE_LAST_DIGIT {
                    return Err(BadSpecifier::Precision);
                }
                value = value * 10 + u32::from(digit - b'0');
            }
            Some(value)
        } else {
            None
        };
        let letter = bytes[0];
        Ok(match letter.to_ascii_uppercase() {
            b'G' => Self::General {
                precision: precision.unwrap_or(0),
                upper: letter == b'G',
            },
            // "R" ignores any precision and is the shortest round trip.
            b'R' => Self::General {
                precision: 0,
                upper: letter == b'R',
            },
            b'E' => Self::Scientific {
                precision: precision.unwrap_or(DEFAULT_EXPONENT_DECIMALS),
                upper: letter == b'E',
            },
            b'F' => Self::Fixed {
                decimals: precision.unwrap_or(DEFAULT_DECIMALS),
            },
            b'N' => Self::Number {
                decimals: precision.unwrap_or(DEFAULT_DECIMALS),
            },
            b'P' => Self::Percent {
                decimals: precision.unwrap_or(DEFAULT_DECIMALS),
            },
            b'C' => Self::Currency {
                decimals: precision.unwrap_or(DEFAULT_DECIMALS),
            },
            b'D' => Self::Decimal {
                digits: precision.unwrap_or(0),
            },
            b'X' => Self::Hex {
                digits: precision.unwrap_or(0),
                upper: letter == b'X',
            },
            b'B' => Self::Binary {
                digits: precision.unwrap_or(0),
            },
            _ => return Err(BadSpecifier::Letter),
        })
    }

    /// `double.ToString(format, InvariantCulture)`.
    pub fn format_f64(&self, value: f64) -> Result<String, FormatError> {
        format_floating(value, false, self)
    }

    /// `float.ToString(format, InvariantCulture)`.
    pub fn format_f32(&self, value: f32) -> Result<String, FormatError> {
        format_floating(f64::from(value), true, self)
    }
}

/// A custom numeric format: digit placeholders (`0` mandatory, `#`
/// optional), one decimal point, group separators (`,` between digits;
/// trailing commas divide by 1000 each), `%` and `‰` (times 100 and 1000),
/// exponents, literal text, and `;` sections.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CustomFormat {
    pattern: Vec<char>,
}

impl CustomFormat {
    fn new(format: &str) -> Self {
        Self {
            pattern: format.chars().collect(),
        }
    }
}

/// `double.ToString(format, CultureInfo.InvariantCulture)`.
pub fn format_f64(value: f64, format: &str) -> Result<String, FormatError> {
    floating_text(value, false, format)
}

/// `float.ToString(format, CultureInfo.InvariantCulture)`.
pub fn format_f32(value: f32, format: &str) -> Result<String, FormatError> {
    floating_text(f64::from(value), true, format)
}

fn floating_text(value: f64, single: bool, format: &str) -> Result<String, FormatError> {
    match NumberFormat::read(format) {
        Ok(parsed) => format_floating(value, single, &parsed),
        // NaN and the infinities print as their symbol whatever the letter.
        Err(BadSpecifier::Letter) if !value.is_finite() => {
            Ok(non_finite(value).unwrap_or_default())
        }
        Err(_) => Err(FormatError::Invalid(format.to_owned())),
    }
}

/// `double.ToString(CultureInfo.InvariantCulture)`: the shortest text that
/// reads back as the same value (`0.1`, `1E+15`, `-0`, `NaN`).
pub fn to_string_f64(value: f64) -> String {
    general_floating(value, false, 0, true)
}

/// `float.ToString(CultureInfo.InvariantCulture)`.
pub fn to_string_f32(value: f32) -> String {
    general_floating(f64::from(value), true, 0, true)
}

/// `int.ToString(format, CultureInfo.InvariantCulture)`.
pub fn format_i32(value: i32, format: &str) -> Result<String, FormatError> {
    format_integer(
        value < 0,
        u128::from(value.unsigned_abs()),
        u64::from(value as u32),
        format,
    )
}

/// `uint.ToString(format, CultureInfo.InvariantCulture)`.
pub fn format_u32(value: u32, format: &str) -> Result<String, FormatError> {
    format_integer(false, u128::from(value), u64::from(value), format)
}

/// `long.ToString(format, CultureInfo.InvariantCulture)`.
pub fn format_i64(value: i64, format: &str) -> Result<String, FormatError> {
    format_integer(
        value < 0,
        u128::from(value.unsigned_abs()),
        value as u64,
        format,
    )
}

/// `ulong.ToString(format, CultureInfo.InvariantCulture)`.
pub fn format_u64(value: u64, format: &str) -> Result<String, FormatError> {
    format_integer(false, u128::from(value), value, format)
}

fn non_finite(value: f64) -> Option<String> {
    if value.is_nan() {
        Some("NaN".to_owned())
    } else if value == f64::INFINITY {
        Some("Infinity".to_owned())
    } else if value == f64::NEG_INFINITY {
        Some("-Infinity".to_owned())
    } else {
        None
    }
}

/// A floating-point value in a parsed format. NaN and the infinities print
/// as their symbol in every format, even the integer-only ones (.NET checks
/// the value before the letter).
fn format_floating(value: f64, single: bool, format: &NumberFormat) -> Result<String, FormatError> {
    if let Some(text) = non_finite(value) {
        return Ok(text);
    }
    Ok(match *format {
        NumberFormat::General { precision, upper } => {
            general_floating(value, single, precision, upper)
        }
        NumberFormat::Scientific { precision, upper } => {
            let count = precision.saturating_add(1);
            let digits = Digits::exact_f64(value).round_significant(count, Rounding::HalfEven);
            signed_scientific(&digits, count, upper)
        }
        NumberFormat::Fixed { decimals } => {
            let digits = Digits::exact_f64(value).round_decimals(decimals, Rounding::HalfEven);
            fixed(&digits, decimals, false)
        }
        NumberFormat::Number { decimals } => {
            let digits = Digits::exact_f64(value).round_decimals(decimals, Rounding::HalfEven);
            fixed(&digits, decimals, true)
        }
        NumberFormat::Percent { decimals } => {
            let digits =
                times_100(Digits::exact_f64(value)).round_decimals(decimals, Rounding::HalfEven);
            percent(&digits, decimals)
        }
        NumberFormat::Currency { decimals } => {
            let digits = Digits::exact_f64(value).round_decimals(decimals, Rounding::HalfEven);
            currency(&digits, decimals)
        }
        NumberFormat::Decimal { .. } | NumberFormat::Hex { .. } | NumberFormat::Binary { .. } => {
            return Err(FormatError::Invalid(format!("{format:?}")));
        }
        NumberFormat::Custom(ref custom) => {
            let significant = if single {
                SINGLE_CUSTOM_DIGITS
            } else {
                DOUBLE_CUSTOM_DIGITS
            };
            let digits =
                Digits::exact_f64(value).round_significant(significant, Rounding::HalfEven);
            custom_format(digits, Kind::Floating, custom)
        }
    })
}

/// `G`/`R` for a finite or non-finite floating-point value.
fn general_floating(value: f64, single: bool, precision: u32, upper: bool) -> String {
    if let Some(text) = non_finite(value) {
        return text;
    }
    let (digits, max_digits) = if precision == 0 {
        let digits = if single {
            Digits::shortest_f32(value as f32)
        } else {
            Digits::shortest_f64(value)
        };
        let floor = if single {
            SINGLE_GENERAL_DIGITS
        } else {
            DOUBLE_GENERAL_DIGITS
        };
        let max = digits.digits.len().max(floor);
        (digits, max)
    } else {
        let digits = Digits::exact_f64(value).round_significant(precision, Rounding::HalfEven);
        (digits, precision as usize)
    };
    general(&digits, max_digits, if upper { 'E' } else { 'e' })
}

/// The value times 100 (`P`); zero stays zero.
fn times_100(mut digits: Digits) -> Digits {
    if !digits.is_zero() {
        digits.scale += 2;
    }
    digits
}

/// .NET's `FormatGeneral`: fixed notation unless the exponent is beyond the
/// digit count or below -4.
pub(crate) fn general(digits: &Digits, max_digits: usize, exponent_char: char) -> String {
    let mut out = String::new();
    if digits.negative {
        out.push('-');
    }
    let scale = i64::from(digits.scale);
    let scientific = scale > max_digits as i64 || scale < -3;
    let mut dig_pos = if scientific { 1 } else { scale };
    let mut index = 0usize;
    if dig_pos > 0 {
        while dig_pos > 0 {
            out.push(char::from(b'0' + digits.digit(index as i64)));
            if index < digits.digits.len() {
                index += 1;
            }
            dig_pos -= 1;
        }
    } else {
        out.push('0');
    }
    if index < digits.digits.len() || dig_pos < 0 {
        out.push(DECIMAL_SEPARATOR);
        while dig_pos < 0 {
            out.push('0');
            dig_pos += 1;
        }
        for &d in &digits.digits[index..] {
            out.push(char::from(b'0' + d));
        }
    }
    if scientific {
        push_exponent(
            &mut out,
            scale - 1,
            exponent_char,
            GENERAL_EXPONENT_DIGITS,
            true,
        );
    }
    out
}

/// .NET's `FormatExponent`: the letter, a sign (`+` only when asked), and at
/// least `min_digits` digits.
fn push_exponent(
    out: &mut String,
    value: i64,
    exponent_char: char,
    min_digits: usize,
    positive_sign: bool,
) {
    out.push(exponent_char);
    if value < 0 {
        out.push('-');
    } else if positive_sign {
        out.push('+');
    }
    out.push_str(&format!("{:0min_digits$}", value.unsigned_abs()));
}

/// `E` (`FormatScientific`) with the sign: `count` significant digits.
fn signed_scientific(digits: &Digits, count: u32, upper: bool) -> String {
    let mut out = String::new();
    if digits.negative {
        out.push('-');
    }
    out.push(char::from(b'0' + digits.digit(0)));
    if count != 1 {
        out.push(DECIMAL_SEPARATOR);
    }
    for i in 1..i64::from(count) {
        out.push(char::from(b'0' + digits.digit(i)));
    }
    let exponent = if digits.is_zero() {
        0
    } else {
        i64::from(digits.scale) - 1
    };
    push_exponent(
        &mut out,
        exponent,
        if upper { 'E' } else { 'e' },
        SCIENTIFIC_EXPONENT_DIGITS,
        true,
    );
    out
}

/// The integer part of a fixed-point number, optionally grouped.
fn integer_part(digits: &Digits, grouped: bool) -> String {
    let scale = digits.scale.max(0) as usize;
    let mut plain = String::with_capacity(scale.max(1));
    if scale == 0 {
        plain.push('0');
    } else {
        for i in 0..scale {
            plain.push(char::from(b'0' + digits.digit(i as i64)));
        }
    }
    if !grouped || plain.len() <= GROUP_SIZE {
        return plain;
    }
    let mut out = String::with_capacity(plain.len() + plain.len() / GROUP_SIZE);
    let first = plain.len() % GROUP_SIZE;
    for (i, c) in plain.chars().enumerate() {
        if i > 0 && (i + GROUP_SIZE - first).is_multiple_of(GROUP_SIZE) {
            out.push(GROUP_SEPARATOR);
        }
        out.push(c);
    }
    out
}

/// The fraction digits of a fixed-point number.
fn fraction_part(digits: &Digits, decimals: u32) -> String {
    let mut out = String::with_capacity(decimals as usize);
    let start = i64::from(digits.scale);
    for i in 0..i64::from(decimals) {
        out.push(char::from(b'0' + digits.digit(start + i)));
    }
    out
}

/// The unsigned fixed-point text: integer part, then the point and decimals.
fn magnitude(digits: &Digits, decimals: u32, grouped: bool) -> String {
    let mut out = integer_part(digits, grouped);
    if decimals > 0 {
        out.push(DECIMAL_SEPARATOR);
        out.push_str(&fraction_part(digits, decimals));
    }
    out
}

/// `F` and `N` (`FormatFixed`), negative pattern `-n`.
pub(crate) fn fixed(digits: &Digits, decimals: u32, grouped: bool) -> String {
    let mut out = String::new();
    if digits.negative {
        out.push('-');
    }
    out.push_str(&magnitude(digits, decimals, grouped));
    out
}

/// `P` with the invariant patterns `n %` and `-n %`.
fn percent(digits: &Digits, decimals: u32) -> String {
    let mut out = fixed(digits, decimals, true);
    out.push(' ');
    out.push(PERCENT_SYMBOL);
    out
}

/// `C` with the invariant patterns `¤n` and `(¤n)`.
fn currency(digits: &Digits, decimals: u32) -> String {
    let body = magnitude(digits, decimals, true);
    if digits.negative {
        format!("({CURRENCY_SYMBOL}{body})")
    } else {
        format!("{CURRENCY_SYMBOL}{body}")
    }
}

/// What the first pass over one section of a custom format learns.
struct Section {
    /// Digit placeholders (`0` and `#`).
    digit_count: i64,
    /// Placeholders before the decimal point.
    decimal_pos: i64,
    /// Placeholders before the first `0`, `i64::MAX` when there is none.
    first_digit: i64,
    /// Placeholders up to and including the last `0`.
    last_digit: i64,
    /// An exponent (`E0`, `e+0`, `E-0`) is present.
    scientific: bool,
    /// Group separators are printed.
    thousand_seps: bool,
    /// Decimal digits the value moves by: `%` +2, `‰` +3, each trailing `,` -3.
    scale_adjust: i64,
}

/// .NET's `FindSection`: where section `wanted` (0 positive, 1 negative,
/// 2 zero) starts; an absent or empty section is the first one.
fn find_section(pattern: &[char], wanted: u32) -> usize {
    if wanted == 0 {
        return 0;
    }
    let mut remaining = wanted;
    let mut src = 0;
    while src < pattern.len() {
        let ch = pattern[src];
        src += 1;
        match ch {
            '\'' | '"' => {
                while src < pattern.len() {
                    let c = pattern[src];
                    src += 1;
                    if c == ch {
                        break;
                    }
                }
            }
            '\\' if src < pattern.len() => src += 1,
            ';' => {
                remaining -= 1;
                if remaining != 0 {
                    continue;
                }
                return if src < pattern.len() && pattern[src] != ';' {
                    src
                } else {
                    0
                };
            }
            _ => {}
        }
    }
    0
}

/// The first pass of .NET's `NumberToStringFormat` over the section at `start`.
fn scan_section(pattern: &[char], start: usize) -> Section {
    let mut s = Section {
        digit_count: 0,
        decimal_pos: -1,
        first_digit: i64::MAX,
        last_digit: 0,
        scientific: false,
        thousand_seps: false,
        scale_adjust: 0,
    };
    let mut thousand_pos: i64 = -1;
    let mut thousand_count: i64 = 0;
    let mut src = start;
    while src < pattern.len() {
        let ch = pattern[src];
        src += 1;
        match ch {
            ';' => break,
            '#' => s.digit_count += 1,
            '0' => {
                if s.first_digit == i64::MAX {
                    s.first_digit = s.digit_count;
                }
                s.digit_count += 1;
                s.last_digit = s.digit_count;
            }
            '.' if s.decimal_pos < 0 => s.decimal_pos = s.digit_count,
            ',' if s.digit_count > 0 && s.decimal_pos < 0 => {
                if thousand_pos >= 0 {
                    if thousand_pos == s.digit_count {
                        thousand_count += 1;
                        continue;
                    }
                    s.thousand_seps = true;
                }
                thousand_pos = s.digit_count;
                thousand_count = 1;
            }
            PERCENT_SYMBOL => s.scale_adjust += 2,
            PER_MILLE_SYMBOL => s.scale_adjust += 3,
            '\'' | '"' => {
                while src < pattern.len() {
                    let c = pattern[src];
                    src += 1;
                    if c == ch {
                        break;
                    }
                }
            }
            '\\' if src < pattern.len() => src += 1,
            'E' | 'e' => {
                let next = pattern.get(src).copied();
                let after = pattern.get(src + 1).copied();
                if next == Some('0') || (matches!(next, Some('+' | '-')) && after == Some('0')) {
                    src += 1;
                    while src < pattern.len() && pattern[src] == '0' {
                        src += 1;
                    }
                    s.scientific = true;
                }
            }
            _ => {}
        }
    }
    if s.decimal_pos < 0 {
        s.decimal_pos = s.digit_count;
    }
    if thousand_pos >= 0 {
        if thousand_pos == s.decimal_pos {
            s.scale_adjust -= thousand_count * 3;
        } else {
            s.thousand_seps = true;
        }
    }
    s
}

/// .NET's `NumberToStringFormat`: a custom format applied to a number buffer.
pub(crate) fn custom_format(mut number: Digits, kind: Kind, format: &CustomFormat) -> String {
    let pattern = &format.pattern[..];
    let wanted = if number.is_zero() {
        2
    } else if number.negative {
        1
    } else {
        0
    };
    let mut section = find_section(pattern, wanted);
    let scan = loop {
        let scan = scan_section(pattern, section);
        if number.is_zero() {
            if kind != Kind::Floating {
                number.negative = false;
            }
            number.scale = 0;
        } else {
            number.scale += scan.scale_adjust as i32;
            let pos = if scan.scientific {
                scan.digit_count
            } else {
                i64::from(number.scale) + scan.digit_count - scan.decimal_pos
            };
            number = number.round_at(pos, Rounding::HalfUp);
            if number.is_zero() {
                if kind != Kind::Floating {
                    number.negative = false;
                }
                // A value that rounds to zero takes the zero section, if any.
                let zero_section = find_section(pattern, 2);
                if zero_section != section {
                    section = zero_section;
                    continue;
                }
            }
        }
        break scan;
    };

    let decimal_pos = scan.decimal_pos;
    let digit_count = scan.digit_count;
    let thousand_seps = scan.thousand_seps;
    let first_digit = if scan.first_digit < decimal_pos {
        decimal_pos - scan.first_digit
    } else {
        0
    };
    let last_digit = if scan.last_digit > decimal_pos {
        decimal_pos - scan.last_digit
    } else {
        0
    };
    let scale = i64::from(number.scale);
    // `dig_pos` counts down over the placeholders before the point; `adjust`
    // is how many more integer digits the number has than the format (or,
    // negative, how many placeholders have no digit).
    let (mut dig_pos, mut adjust) = if scan.scientific {
        (decimal_pos, 0)
    } else {
        (scale.max(decimal_pos), scale - decimal_pos)
    };

    // Where group separators go, counted in integer digits from the right.
    let mut separator_positions: Vec<i64> = Vec::new();
    if thousand_seps {
        let total_digits = dig_pos + if adjust < 0 { adjust } else { 0 };
        let num_digits = first_digit.max(total_digits);
        let mut group_total = GROUP_SIZE as i64;
        while num_digits > group_total {
            separator_positions.push(group_total);
            group_total += GROUP_SIZE as i64;
        }
    }
    let mut separator_index = separator_positions.len() as i64 - 1;
    let mut push_separator = |out: &mut String, dig_pos: i64| {
        if thousand_seps
            && dig_pos > 1
            && separator_index >= 0
            && dig_pos == separator_positions[separator_index as usize] + 1
        {
            out.push(GROUP_SEPARATOR);
            separator_index -= 1;
        }
    };

    let mut out = String::new();
    if number.negative && section == 0 && number.scale != 0 {
        out.push('-');
    }
    let mut scientific = scan.scientific;
    let mut cursor = 0usize;
    let mut decimal_written = false;
    let mut src = section;
    while src < pattern.len() {
        let ch = pattern[src];
        src += 1;
        if ch == ';' {
            break;
        }
        if adjust > 0 && matches!(ch, '#' | '0' | '.') {
            while adjust > 0 {
                let d = number.digits.get(cursor).copied().unwrap_or(0);
                if cursor < number.digits.len() {
                    cursor += 1;
                }
                out.push(char::from(b'0' + d));
                push_separator(&mut out, dig_pos);
                dig_pos -= 1;
                adjust -= 1;
            }
        }
        match ch {
            '#' | '0' => {
                let emitted = if adjust < 0 {
                    adjust += 1;
                    (dig_pos <= first_digit).then_some(b'0')
                } else if let Some(&d) = number.digits.get(cursor) {
                    cursor += 1;
                    Some(b'0' + d)
                } else {
                    (dig_pos > last_digit).then_some(b'0')
                };
                if let Some(c) = emitted {
                    out.push(char::from(c));
                    push_separator(&mut out, dig_pos);
                }
                dig_pos -= 1;
            }
            '.' => {
                // Only the first point that falls after the integer digits prints.
                if dig_pos != 0 || decimal_written {
                    continue;
                }
                if last_digit < 0 || (decimal_pos < digit_count && cursor < number.digits.len()) {
                    out.push(DECIMAL_SEPARATOR);
                    decimal_written = true;
                }
            }
            PER_MILLE_SYMBOL => out.push(PER_MILLE_SYMBOL),
            PERCENT_SYMBOL => out.push(PERCENT_SYMBOL),
            ',' => {}
            '\'' | '"' => {
                while src < pattern.len() && pattern[src] != ch {
                    out.push(pattern[src]);
                    src += 1;
                }
                if src < pattern.len() {
                    src += 1;
                }
            }
            '\\' => {
                if src < pattern.len() {
                    out.push(pattern[src]);
                    src += 1;
                }
            }
            'E' | 'e' => {
                let next = pattern.get(src).copied();
                let after = pattern.get(src + 1).copied();
                if scientific {
                    let mut zeros = 0usize;
                    let mut positive_sign = false;
                    match (next, after) {
                        (Some('0'), _) => zeros += 1,
                        (Some('+'), Some('0')) => positive_sign = true,
                        (Some('-'), Some('0')) => {}
                        _ => {
                            out.push(ch);
                            continue;
                        }
                    }
                    src += 1;
                    while src < pattern.len() && pattern[src] == '0' {
                        zeros += 1;
                        src += 1;
                    }
                    let exponent = if number.is_zero() {
                        0
                    } else {
                        scale - decimal_pos
                    };
                    push_exponent(
                        &mut out,
                        exponent,
                        ch,
                        zeros.min(MAX_CUSTOM_EXPONENT_DIGITS),
                        positive_sign,
                    );
                    scientific = false;
                } else {
                    // Only the first exponent is one; later ones are text.
                    out.push(ch);
                    if let Some(sign @ ('+' | '-')) = next {
                        out.push(sign);
                        src += 1;
                    }
                    while src < pattern.len() && pattern[src] == '0' {
                        out.push('0');
                        src += 1;
                    }
                }
            }
            other => out.push(other),
        }
    }
    if number.negative && section == 0 && number.scale == 0 && !out.is_empty() {
        out.insert(0, '-');
    }
    out
}

fn format_integer(
    negative: bool,
    magnitude: u128,
    bits: u64,
    format: &str,
) -> Result<String, FormatError> {
    let parsed = NumberFormat::parse(format)?;
    let digits = Digits::from_u128(negative, magnitude);
    let sign = if negative { "-" } else { "" };
    Ok(match parsed {
        NumberFormat::General { precision: 0, .. } => format!("{sign}{magnitude}"),
        NumberFormat::General { precision, upper } => {
            let rounded = digits.round_significant(precision, Rounding::HalfUp);
            general(&rounded, precision as usize, if upper { 'E' } else { 'e' })
        }
        NumberFormat::Scientific { precision, upper } => {
            let count = precision.saturating_add(1);
            signed_scientific(
                &digits.round_significant(count, Rounding::HalfUp),
                count,
                upper,
            )
        }
        NumberFormat::Decimal { digits: width } => {
            format!("{sign}{magnitude:0width$}", width = width as usize)
        }
        NumberFormat::Hex {
            digits: width,
            upper,
        } => {
            let width = width as usize;
            if upper {
                format!("{bits:0width$X}")
            } else {
                format!("{bits:0width$x}")
            }
        }
        NumberFormat::Binary { digits: width } => {
            format!("{bits:0width$b}", width = width as usize)
        }
        NumberFormat::Fixed { decimals } => fixed(&digits, decimals, false),
        NumberFormat::Number { decimals } => fixed(&digits, decimals, true),
        NumberFormat::Percent { decimals } => percent(&times_100(digits), decimals),
        NumberFormat::Currency { decimals } => currency(&digits, decimals),
        NumberFormat::Custom(custom) => custom_format(digits, Kind::Integer, &custom),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shapes_from_the_plugins() {
        // Values from MossTank and MosswartMassacre call sites; outputs printed by .NET 10.
        assert_eq!(format_f64(0.125, "F2").unwrap(), "0.12");
        assert_eq!(format_f64(0.125, "0.00").unwrap(), "0.13");
        assert_eq!(format_f64(-0.001, "F2").unwrap(), "-0.00");
        assert_eq!(format_f64(1234567.891, "N0").unwrap(), "1,234,568");
        assert_eq!(format_f64(0.125, "P").unwrap(), "12.50 %");
        assert_eq!(format_f64(1e15, "G15").unwrap(), "1E+15");
        assert_eq!(to_string_f64(1e15), "1000000000000000");
        assert_eq!(to_string_f64(1e17), "1E+17");
        assert_eq!(to_string_f64(1e16), "10000000000000000");
        assert_eq!(to_string_f64(-0.0), "-0");
        assert_eq!(to_string_f64(1e-5), "1E-05");
        assert_eq!(to_string_f32(1e8), "100000000");
        assert_eq!(to_string_f32(1e9), "1E+09");
        assert_eq!(format_i64(-1234, "N0").unwrap(), "-1,234");
        assert_eq!(format_u32(0xBEEF, "X8").unwrap(), "0000BEEF");
        assert_eq!(format_i32(-1, "X").unwrap(), "FFFFFFFF");
    }

    #[test]
    fn scientific_and_literal_formats() {
        // .NET 10 `double.ToString(format, InvariantCulture)` (data/format_more.tsv).
        assert_eq!(format_f64(1234.5678, "E2").unwrap(), "1.23E+003");
        assert_eq!(format_f64(-0.0, "e").unwrap(), "-0.000000e+000");
        assert_eq!(format_f64(3.5, "0.0 x").unwrap(), "3.5 x");
        assert_eq!(format_f64(-3.5, "~0.00").unwrap(), "-~3.50");
        assert_eq!(format_f64(0.0, "0.0;-0.0;'zero'").unwrap(), "zero");
        assert_eq!(format_f64(1234.5678, "0.00E+00").unwrap(), "1.23E+03");
        assert_eq!(format_f64(1.0, "C").unwrap(), "\u{a4}1.00");
    }

    #[test]
    fn invalid_formats_are_reported() {
        assert!(matches!(format_f64(1.0, "Q"), Err(FormatError::Invalid(_))));
        assert!(matches!(format_f64(1.0, "X"), Err(FormatError::Invalid(_))));
        assert!(matches!(
            format_f64(1.0, "G1000000000"),
            Err(FormatError::Invalid(_))
        ));
    }
}
