//! The number text of .NET Framework, which the original MosswartMassacre
//! (a DECAL plugin) wrote and its backend parses byte for byte.
//!
//! .NET Framework's `"R"` printed fifteen significant digits when they read
//! back as the same double, else seventeen (seven and nine for `float`);
//! modern .NET prints the shortest text that reads back. Its fixed formats
//! (`F2`, `N0`, ...) took the value to fifteen significant digits and rounded
//! that half away from zero, never printing a negative zero; modern .NET
//! rounds the exact binary value half to even and keeps `-0.00`.

use super::digits::{Digits, Rounding};
use super::format::{
    FormatError, Kind, NumberFormat, custom_format, format_f64, general, to_string_f64,
};

/// The largest `decimal`, `79228162514264337593543950335` (2^96 - 1).
const DECIMAL_MAX_DIGITS: &[u8] = b"79228162514264337593543950335";
/// Digits after the point a `decimal` holds.
const DECIMAL_MAX_SCALE: u32 = 28;

/// .NET Framework's `double.ToString("R")`: `G15` if it reads back as the
/// same value, else `G17`; a negative zero prints as `0`.
pub fn round_trip_f64(value: f64) -> String {
    let value = if value == 0.0 { 0.0 } else { value };
    let short = format_f64(value, "G15").unwrap_or_else(|_| to_string_f64(value));
    if short.parse::<f64>().ok() == Some(value) {
        return short;
    }
    format_f64(value, "G17").unwrap_or_else(|_| to_string_f64(value))
}

/// .NET Framework's `float.ToString("R")`: `G7` if it reads back, else `G9`.
pub fn round_trip_f32(value: f32) -> String {
    let value = if value == 0.0 { 0.0 } else { value };
    if !value.is_finite() {
        return to_string_f64(f64::from(value));
    }
    let digits_of = |precision: u32| {
        let digits =
            Digits::exact_f64(f64::from(value)).round_significant(precision, Rounding::HalfEven);
        general(&digits, precision as usize, 'E')
    };
    let short = digits_of(7);
    if short.parse::<f32>().ok() == Some(value) {
        return short;
    }
    digits_of(9)
}

/// .NET Framework's fixed and number formats for a double (`F7`, `F2`, `F0`,
/// `N0`, custom), as MosswartMassacre's `FrameworkFixed` computes them: the
/// `G15` digits read as a `decimal` and formatted as one. Values outside the
/// `decimal` range and non-finite values print as modern .NET prints them.
pub fn fixed(value: f64, format: &str) -> Result<String, FormatError> {
    let parsed = NumberFormat::parse(format)?;
    if !value.is_finite() {
        return parsed.format_f64(value);
    }
    let g15 = Digits::exact_f64(value).round_significant(15, Rounding::HalfEven);
    if !fits_decimal(&g15) {
        return parsed.format_f64(value);
    }
    // The decimal keeps at most 28 digits after the point, rounded half to even.
    let decimal = g15.round_decimals(DECIMAL_MAX_SCALE, Rounding::HalfEven);
    Ok(match parsed {
        NumberFormat::Fixed { decimals } => decimal_fixed(&decimal, decimals, false),
        NumberFormat::Number { decimals } => decimal_fixed(&decimal, decimals, true),
        NumberFormat::Custom(custom) => custom_format(decimal, Kind::Decimal, &custom),
        _ => return Err(FormatError::Unsupported(format.to_owned())),
    })
}

/// Whether `decimal.TryParse` accepts the digits (below 2^96).
fn fits_decimal(digits: &Digits) -> bool {
    let limit = DECIMAL_MAX_DIGITS.len() as i32;
    if digits.is_zero() || digits.scale < limit {
        return true;
    }
    if digits.scale > limit {
        return false;
    }
    for (i, &max) in DECIMAL_MAX_DIGITS.iter().enumerate() {
        let d = digits.digit(i as i64);
        if d != max - b'0' {
            return d < max - b'0';
        }
    }
    true
}

/// `decimal.ToString("F"/"N")`: half up, and a value that rounds to zero
/// loses its sign.
fn decimal_fixed(digits: &Digits, decimals: u32, grouped: bool) -> String {
    let mut rounded = digits.round_decimals(decimals, Rounding::HalfUp);
    if rounded.is_zero() {
        rounded.negative = false;
    }
    super::format::fixed(&rounded, decimals, grouped)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mosswart_massacre_wire_numbers() {
        // MosswartMassacre tests/Backend/WireNumberTests.cs (the original's bytes).
        assert_eq!(round_trip_f64(0.1), "0.1");
        assert_eq!(round_trip_f64(75.0), "75");
        assert_eq!(round_trip_f64(1e-05), "1E-05");
        assert_eq!(
            round_trip_f64(-0.066_666_670_143_604_28),
            "-0.066666670143604279"
        );
        assert_eq!(round_trip_f64(f64::from(33.65f32)), "33.650001525878906");
        assert_eq!(round_trip_f64(0.30000000000000004), "0.30000000000000004");
        assert_eq!(round_trip_f64(-0.0), "0");
        assert_eq!(round_trip_f32(2.711_539_7), "2.71153975");
        assert_eq!(round_trip_f32(0.1), "0.1");
        assert_eq!(round_trip_f32(3.0), "3");
        assert_eq!(round_trip_f32(1.000_000_1), "1.00000012");
        assert_eq!(round_trip_f32(-0.0), "0");
    }

    #[test]
    fn mosswart_massacre_fixed_formats() {
        // WireNumberTests.Fixed_formats_round_as_the_framework_did.
        assert_eq!(fixed(0.125, "F2").unwrap(), "0.13");
        assert_eq!(fixed(2.675, "F2").unwrap(), "2.68");
        assert_eq!(fixed(-0.001, "F2").unwrap(), "0.00");
        assert_eq!(fixed(-0.0, "F7").unwrap(), "0.0000000");
        assert_eq!(fixed(12.5, "F0").unwrap(), "13");
        assert_eq!(fixed(33.650001525878906, "F7").unwrap(), "33.6500015");
        assert_eq!(fixed(1234567.5, "N0").unwrap(), "1,234,568");
    }
}
