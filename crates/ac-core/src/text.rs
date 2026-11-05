//! Text helpers shared by several features: the game's duration wording and
//! the number parsing and formatting its text files and chat markup use.
//!
//! Every number here is read and written the US way regardless of the
//! machine's locale: a period for the decimal point, no group separators,
//! an ASCII hyphen for the minus sign. The game is a US client and shows
//! every player the same text.

const SECONDS_PER_MONTH: i64 = 2_592_000; // a flat 30-day month
const SECONDS_PER_DAY: i64 = 86_400;
const SECONDS_PER_HOUR: i64 = 3_600;
const SECONDS_PER_MINUTE: i64 = 60;

/// A duration as the game words it everywhere: largest unit first, each unit
/// left out when zero, seconds always shown (`"1mo 2d 3h 4m 5s"`, `"45s"`).
///
/// Fractions of a second are cut off, not rounded (59.99 s reads `"59s"`),
/// and a negative or not-a-number duration reads `"0s"`.
#[must_use]
pub fn format_duration(seconds: f64) -> String {
    // `as` truncates toward zero, saturates on overflow and maps NaN to 0.
    let total = (seconds as i64).max(0);

    let months = total / SECONDS_PER_MONTH;
    let mut rest = total % SECONDS_PER_MONTH;
    let days = rest / SECONDS_PER_DAY;
    rest %= SECONDS_PER_DAY;
    let hours = rest / SECONDS_PER_HOUR;
    rest %= SECONDS_PER_HOUR;
    let minutes = rest / SECONDS_PER_MINUTE;
    let secs = rest % SECONDS_PER_MINUTE;

    let mut parts = Vec::with_capacity(5);
    if months != 0 {
        parts.push(format!("{months}mo"));
    }
    if days != 0 {
        parts.push(format!("{days}d"));
    }
    if hours != 0 {
        parts.push(format!("{hours}h"));
    }
    if minutes != 0 {
        parts.push(format!("{minutes}m"));
    }
    parts.push(format!("{secs}s"));
    parts.join(" ")
}

/// The white space a number may carry around it: tab, line feed, vertical
/// tab, form feed, carriage return and space.
fn is_number_white(c: char) -> bool {
    matches!(c, '\t' | '\n' | '\u{0B}' | '\u{0C}' | '\r' | ' ')
}

fn trim_number_white(text: &str) -> &str {
    text.trim_matches(is_number_white)
}

/// Splits an optional leading sign off; `true` when it was a minus.
fn split_sign(text: &str) -> (bool, &str) {
    if let Some(rest) = text.strip_prefix('-') {
        (true, rest)
    } else if let Some(rest) = text.strip_prefix('+') {
        (false, rest)
    } else {
        (false, text)
    }
}

/// Reads a whole number in the integer grammar: optional surrounding white
/// space, an optional sign, then one or more ASCII digits. `None` on any
/// other text or when the value does not fit.
pub fn parse_integer_i32(text: &str) -> Option<i32> {
    let (negative, digits) = split_sign(trim_number_white(text));
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let significant = digits.trim_start_matches('0');
    if significant.len() > 10 {
        return None;
    }
    let magnitude: i64 = if significant.is_empty() {
        0
    } else {
        significant.parse().ok()?
    };
    let value = if negative { -magnitude } else { magnitude };
    i32::try_from(value).ok()
}

/// Reads an unsigned whole number in the integer grammar. A minus sign is
/// accepted only in front of zero.
pub fn parse_integer_u32(text: &str) -> Option<u32> {
    let (negative, digits) = split_sign(trim_number_white(text));
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let significant = digits.trim_start_matches('0');
    if significant.is_empty() {
        return Some(0);
    }
    if negative {
        return None;
    }
    significant.parse::<u32>().ok()
}

/// True when `text` (already stripped of sign and white space) is a decimal
/// number: digits with an optional fraction (at least one digit overall),
/// then an optional exponent.
fn is_decimal_grammar(text: &str) -> bool {
    let bytes = text.as_bytes();
    let mut i = 0;
    let mut digits = 0;
    while i < bytes.len() && bytes[i].is_ascii_digit() {
        i += 1;
        digits += 1;
    }
    if i < bytes.len() && bytes[i] == b'.' {
        i += 1;
        while i < bytes.len() && bytes[i].is_ascii_digit() {
            i += 1;
            digits += 1;
        }
    }
    if digits == 0 {
        return false;
    }
    if i < bytes.len() && (bytes[i] == b'e' || bytes[i] == b'E') {
        i += 1;
        if i < bytes.len() && (bytes[i] == b'+' || bytes[i] == b'-') {
            i += 1;
        }
        let start = i;
        while i < bytes.len() && bytes[i].is_ascii_digit() {
            i += 1;
        }
        if i == start {
            return false;
        }
    }
    i == bytes.len()
}

/// Reads a decimal number in the floating grammar: optional surrounding
/// white space, an optional sign, digits with an optional fraction, an
/// optional exponent. A value too large for the type reads as infinity.
pub fn parse_float_f64(text: &str) -> Option<f64> {
    let trimmed = trim_number_white(text);
    let (_, unsigned) = split_sign(trimmed);
    if !is_decimal_grammar(unsigned) {
        return None;
    }
    trimmed.parse::<f64>().ok()
}

/// [`parse_float_f64`] for single precision.
pub fn parse_float_f32(text: &str) -> Option<f32> {
    let trimmed = trim_number_white(text);
    let (_, unsigned) = split_sign(trimmed);
    if !is_decimal_grammar(unsigned) {
        return None;
    }
    trimmed.parse::<f32>().ok()
}

/// Writes a number with at most six decimals and no trailing zeros
/// (`1.5`, `-8.25`, `90`, `0`), the way the journal file stores numbers.
///
/// The value is first taken to 15 significant digits, then rounded to six
/// decimals with halves going away from zero. A negative value that rounds
/// to zero keeps its sign (`-0`).
pub fn format_up_to_six_decimals(value: f64) -> String {
    if value.is_nan() {
        return "NaN".to_owned();
    }
    if value.is_infinite() {
        return if value > 0.0 { "Infinity" } else { "-Infinity" }.to_owned();
    }
    let negative = value.is_sign_negative();

    // 15 significant digits: "d.dddddddddddddde<exp>".
    let scientific = format!("{:.14e}", value.abs());
    let (mantissa, exponent) = scientific.split_once('e').unwrap_or((&scientific, "0"));
    let mut exponent: i64 = exponent.parse().unwrap_or(0);
    let mut digits: Vec<u8> = mantissa
        .bytes()
        .filter(u8::is_ascii_digit)
        .map(|b| b - b'0')
        .collect();
    if digits.iter().all(|&d| d == 0) {
        exponent = 0;
    }

    // Keep the digits up to the sixth decimal; round half away from zero.
    let keep = exponent + 1 + 6;
    if keep <= 0 {
        let round_up = keep == 0 && digits.first().copied().unwrap_or(0) >= 5;
        digits = if round_up { vec![1] } else { vec![0] };
        exponent = if round_up { -6 } else { 0 };
    } else if let Ok(keep) = usize::try_from(keep)
        && keep < digits.len()
    {
        let round_up = digits[keep] >= 5;
        digits.truncate(keep);
        if round_up {
            let mut carry = true;
            for digit in digits.iter_mut().rev() {
                if *digit == 9 {
                    *digit = 0;
                } else {
                    *digit += 1;
                    carry = false;
                    break;
                }
            }
            if carry {
                digits.insert(0, 1);
                exponent += 1;
            }
        }
    }

    // Lay the digits out around the decimal point.
    let mut integer = String::new();
    let mut fraction = String::new();
    if exponent < 0 {
        integer.push('0');
        for _ in 0..(-exponent - 1) {
            fraction.push('0');
        }
        fraction.extend(digits.iter().map(|&d| char::from(b'0' + d)));
    } else {
        let int_len = usize::try_from(exponent + 1).unwrap_or(usize::MAX);
        for i in 0..int_len {
            integer.push(char::from(b'0' + digits.get(i).copied().unwrap_or(0)));
        }
        if digits.len() > int_len {
            fraction.extend(digits[int_len..].iter().map(|&d| char::from(b'0' + d)));
        }
    }
    fraction.truncate(6);
    let fraction = fraction.trim_end_matches('0');

    let mut out = String::with_capacity(integer.len() + fraction.len() + 2);
    if negative {
        out.push('-');
    }
    out.push_str(&integer);
    if !fraction.is_empty() {
        out.push('.');
        out.push_str(fraction);
    }
    out
}

/// A position as the game's `/loc` prints it: the cell in hex, the origin
/// in brackets, then the orientation `w x y z`, each number with six
/// decimals (`0xA9B40001 [1.250000 -2.500000 3.000000] 0.900000 0.100000
/// 0.200000 0.300000`).
#[must_use]
pub fn format_retail_position(cell: u32, origin: [f32; 3], orientation_wxyz: [f32; 4]) -> String {
    let [x, y, z] = origin;
    let [qw, qx, qy, qz] = orientation_wxyz;
    format!("0x{cell:08X} [{x:.6} {y:.6} {z:.6}] {qw:.6} {qx:.6} {qy:.6} {qz:.6}")
}

#[cfg(test)]
mod tests {
    use super::*;

    // OpenAC tests/AcDream.Core.Tests/Ui/RetailPositionFormatterTests.cs
    // Format_MatchesPositionToStringFieldOrderAndPrecision.
    #[test]
    fn a_position_reads_cell_origin_then_w_x_y_z() {
        assert_eq!(
            format_retail_position(0xA9B4_0001, [1.25, -2.5, 3.0], [0.9, 0.1, 0.2, 0.3]),
            "0xA9B40001 [1.250000 -2.500000 3.000000] 0.900000 0.100000 0.200000 0.300000"
        );
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Quests/ContractProgressTextTests.cs
    // (RetailDurationText cases).
    #[test]
    fn duration_formats_largest_unit_first_and_always_shows_seconds() {
        let cases: [(f64, &str); 8] = [
            (0.0, "0s"),
            (45.0, "45s"),
            (60.0, "1m 0s"),
            (3600.0, "1h 0s"),
            (3661.0, "1h 1m 1s"),
            (86400.0, "1d 0s"),
            (2_592_000.0, "1mo 0s"),
            (2_592_000.0 + 86400.0 + 3600.0 + 61.0, "1mo 1d 1h 1m 1s"),
        ];
        for (seconds, expected) in cases {
            assert_eq!(format_duration(seconds), expected, "{seconds}");
        }
    }

    // OpenAC ContractProgressTextTests.DeltaTimeHasNoTrailingSpace.
    #[test]
    fn duration_has_no_trailing_space() {
        assert_eq!(format_duration(30.0), "30s");
    }

    // OpenAC ContractProgressTextTests.DeltaTimeTruncatesTowardZeroLikeRetailsFtol.
    #[test]
    fn duration_truncates_toward_zero() {
        assert_eq!(format_duration(59.99), "59s");
        assert_eq!(format_duration(-5.0), "0s");
        assert_eq!(format_duration(f64::NAN), "0s");
    }

    #[test]
    fn integers_follow_the_integer_grammar() {
        assert_eq!(parse_integer_i32(" 42 "), Some(42));
        assert_eq!(parse_integer_i32("+7"), Some(7));
        assert_eq!(parse_integer_i32("-7"), Some(-7));
        assert_eq!(parse_integer_i32("2147483648"), None);
        assert_eq!(parse_integer_i32("-2147483648"), Some(i32::MIN));
        assert_eq!(parse_integer_i32("1,000"), None);
        assert_eq!(parse_integer_i32(""), None);
        assert_eq!(parse_integer_u32("-0"), Some(0));
        assert_eq!(parse_integer_u32("-1"), None);
        assert_eq!(parse_integer_u32("1342177290"), Some(1_342_177_290));
    }

    #[test]
    fn floats_follow_the_float_grammar_whatever_the_locale() {
        assert_eq!(parse_float_f64("1.5"), Some(1.5));
        assert_eq!(parse_float_f64(" -8.25 "), Some(-8.25));
        assert_eq!(parse_float_f64(".5"), Some(0.5));
        assert_eq!(parse_float_f64("5."), Some(5.0));
        assert_eq!(parse_float_f64("1e3"), Some(1000.0));
        assert_eq!(parse_float_f64("1,5"), None);
        assert_eq!(parse_float_f64("inf"), None);
        assert_eq!(parse_float_f64("."), None);
        assert_eq!(parse_float_f32("2.25"), Some(2.25));
    }

    #[test]
    fn six_decimal_form_drops_trailing_zeros() {
        assert_eq!(format_up_to_six_decimals(1.5), "1.5");
        assert_eq!(format_up_to_six_decimals(-8.25), "-8.25");
        assert_eq!(format_up_to_six_decimals(90.0), "90");
        assert_eq!(format_up_to_six_decimals(0.0), "0");
        assert_eq!(format_up_to_six_decimals(f64::from(0.1f32)), "0.1");
        assert_eq!(format_up_to_six_decimals(1.000_000_4), "1");
        assert_eq!(format_up_to_six_decimals(1.000_000_6), "1.000001");
        assert_eq!(format_up_to_six_decimals(0.999_999_9), "1");
        assert_eq!(format_up_to_six_decimals(0.000_000_6), "0.000001");
        assert_eq!(format_up_to_six_decimals(0.000_000_4), "0");
        assert_eq!(format_up_to_six_decimals(123_456.5), "123456.5");
        assert_eq!(format_up_to_six_decimals(1e20), "100000000000000000000");
    }
}
