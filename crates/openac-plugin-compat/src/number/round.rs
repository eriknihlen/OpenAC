//! `Math.Round` and `Convert.ToInt32(double)` as .NET 10 computes them.

/// .NET's `MidpointRounding`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MidpointRounding {
    /// Halves go to the even neighbour (banker's rounding, the default).
    ToEven,
    /// Halves go away from zero.
    AwayFromZero,
    /// Toward zero (truncation).
    ToZero,
    /// Toward negative infinity (floor).
    ToNegativeInfinity,
    /// Toward positive infinity (ceiling).
    ToPositiveInfinity,
}

/// Powers of ten `Math.Round` scales by (`RoundPower10Double`).
const POWER10: [f64; 16] = [
    1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
];

/// Beyond this magnitude every double is already whole to 15 digits
/// (`doubleRoundLimit`).
const ROUND_LIMIT: f64 = 1e16;

/// `Math.Round(double)`: to the nearest whole number, halves to even.
pub fn round(value: f64) -> f64 {
    value.round_ties_even()
}

/// `Math.Round(double, MidpointRounding)`.
pub fn round_mode(value: f64, mode: MidpointRounding) -> f64 {
    round_digits(value, 0, mode)
}

/// `Math.Round(double, int digits, MidpointRounding)`: scales by `10^digits`,
/// rounds, scales back (so the result carries the double arithmetic of the
/// scaling, as in .NET).
///
/// # Panics
///
/// When `digits` is above 15 (.NET throws `ArgumentOutOfRangeException`).
pub fn round_digits(value: f64, digits: u32, mode: MidpointRounding) -> f64 {
    assert!(digits <= 15, "Math.Round: digits {digits} is above 15");
    if value.abs() >= ROUND_LIMIT || value.is_nan() {
        return value;
    }
    let power10 = POWER10[digits as usize];
    let scaled = value * power10;
    let rounded = match mode {
        MidpointRounding::ToEven => scaled.round_ties_even(),
        // Adding the largest double below one half, then truncating, rounds
        // halves away from zero without rounding 0.49999999999999994 up.
        MidpointRounding::AwayFromZero => {
            let almost_half = f64::from_bits(0.5f64.to_bits() - 1);
            (scaled + almost_half.copysign(scaled)).trunc()
        }
        MidpointRounding::ToZero => scaled.trunc(),
        MidpointRounding::ToNegativeInfinity => scaled.floor(),
        MidpointRounding::ToPositiveInfinity => scaled.ceil(),
    };
    rounded / power10
}

/// `Convert.ToInt32(double)`: halves to even; `None` where .NET throws
/// `OverflowException` (out of range or NaN).
pub fn convert_to_int32(value: f64) -> Option<i32> {
    if value >= 0.0 {
        if value < 2_147_483_647.5 {
            let mut result = value as i32;
            let dif = value - f64::from(result);
            if dif > 0.5 || (dif == 0.5 && result & 1 != 0) {
                result += 1;
            }
            return Some(result);
        }
    } else if value >= -2_147_483_648.5 {
        let mut result = value as i32;
        let dif = value - f64::from(result);
        if dif < -0.5 || (dif == -0.5 && result & 1 != 0) {
            result -= 1;
        }
        return Some(result);
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plugin_cases() {
        // .NET 10: Math.Round(2.5) = 2, Math.Round(2.5, AwayFromZero) = 3,
        // Math.Round(-0.001, 2) = -0 (MosswartMassacre's negative-zero test).
        assert_eq!(round(2.5), 2.0);
        assert_eq!(round_mode(2.5, MidpointRounding::AwayFromZero), 3.0);
        assert_eq!(
            round_mode(0.49999999999999994, MidpointRounding::AwayFromZero),
            0.0
        );
        let r = round_digits(-0.001, 2, MidpointRounding::ToEven);
        assert_eq!(r, 0.0);
        assert!(r.is_sign_negative());
        assert_eq!(convert_to_int32(2.5), Some(2));
        assert_eq!(convert_to_int32(-3.5), Some(-4));
        assert_eq!(convert_to_int32(f64::NAN), None);
    }
}
