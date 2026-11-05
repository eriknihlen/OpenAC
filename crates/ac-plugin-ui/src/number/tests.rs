//! Ported from OpenAC `tests/AcDream.App.Tests/UI/MarkupNumberTests.cs`:
//! the reading, rounding, clamping and stepping a `<number>` does (the
//! field it draws in is the window's, `ac-ui`). The C# binding's `int`
//! `Count` and `float` `Scale` are the Rust kinds; `long`, `double` and
//! `decimal` have no Rust binding kind.

use super::*;

fn d(text: &str) -> Dec {
    Dec::parse_float_style(text).unwrap()
}

/// `value="{Count}" min="0" max="100" step="5"` on an `int`.
fn count() -> NumberSpec {
    NumberSpec {
        min: d("0"),
        max: d("100"),
        step: d("5"),
        decimals: 0,
    }
}

/// A `float` value's defaults: the decimal range, step 1, two decimals.
fn float(decimals: u32) -> NumberSpec {
    NumberSpec {
        min: Dec::MIN,
        max: Dec::MAX,
        step: Dec::ONE,
        decimals,
    }
}

#[test]
fn floats_read_as_decimals_of_seven_significant_digits() {
    // NumberReader_ReadsEveryNumericTypeAsDecimal (the float cases)
    assert_eq!(Dec::from_f32(1.5), d("1.5"));
    assert_eq!(Dec::from_f32(0.1), d("0.1"));
    assert_eq!(Dec::from_i64(10), d("10"));
    // NumberReader_ClampsUnrepresentableDoubles, for a float
    assert_eq!(Dec::from_f32(f32::NAN), Dec::ZERO);
    assert_eq!(Dec::from_f32(f32::INFINITY), Dec::MAX);
    assert_eq!(Dec::from_f32(f32::NEG_INFINITY), Dec::MIN);
    assert_eq!(Dec::from_f32(1e30), Dec::MAX);
}

#[test]
fn shows_the_bound_value_formatted() {
    // Number_ShowsTheBoundValueFormatted
    assert_eq!(count().format(Dec::from_i64(10)), "10");
    assert_eq!(float(2).format(Dec::from_f32(0.5)), "0.50");
    assert_eq!(float(2).format(d("-0.004")), "0.00");
    assert_eq!(float(1).format(d("0.25")), "0.3");
}

#[test]
fn commit_clamps_and_rounds() {
    // Number_CommitClampsAndRounds
    assert_eq!(count().commit("250", Dec::from_i64(10)), Some(d("100")));
    let ratio = NumberSpec {
        min: d("-1"),
        max: d("1"),
        step: Dec::ONE,
        decimals: 1,
    };
    let v = ratio.commit("0.25", d("0.5")).unwrap();
    assert_eq!(v, d("0.3"));
    assert_eq!(ratio.format(v), "0.3");
}

#[test]
fn unparseable_text_restores() {
    // Number_UnparseableTextRestores
    for text in ["-", "", "1-2"] {
        assert_eq!(count().commit(text, Dec::from_i64(10)), None, "{text:?}");
    }
}

#[test]
fn the_same_value_is_not_reported() {
    // Number_SameValueIsNotReported, Number_FocusAndBlurWithoutEditingDoesNotReport
    assert_eq!(count().commit("10", Dec::from_i64(10)), None);
    let scale = Dec::from_f32(1.1);
    assert_eq!(float(2).commit(&float(2).format(scale), scale), None);
}

#[test]
fn up_down_step_and_shift_steps_ten() {
    // Number_UpDownStepAndShiftStepsTen
    let c = count();
    assert_eq!(c.step_by(1, false, None, Dec::from_i64(10)), Some(d("15")));
    assert_eq!(c.step_by(-1, false, None, Dec::from_i64(15)), Some(d("10")));
    assert_eq!(c.step_by(1, true, None, Dec::from_i64(10)), Some(d("60")));
}

#[test]
fn a_spin_at_the_limit_reports_nothing() {
    // Number_SpinClampsAtTheLimitWithoutReporting
    assert_eq!(count().step_by(1, false, None, Dec::from_i64(100)), None);
}

#[test]
fn stepping_past_the_decimal_limit_clamps() {
    // Number_SteppingPastDecimalLimitsClamps
    let money = Dec::MAX.checked_add(d("-0.5")).unwrap();
    assert_eq!(float(1).step_by(1, true, None, money), Some(Dec::MAX));
}

#[test]
fn a_float_step_at_its_max_reports_nothing() {
    // Number_FloatStepAtItsMaxReportsNothing
    let spec = NumberSpec {
        max: d("1.1"),
        ..float(2)
    };
    assert_eq!(spec.step_by(1, false, None, Dec::from_f32(1.1)), None);
}

#[test]
fn a_float_retyped_unchanged_reports_nothing() {
    // Number_FloatRetypedUnchangedReportsNothing
    let scale = Dec::from_f32(0.1);
    assert_eq!(float(2).format(scale), "0.10");
    assert_eq!(float(2).commit("0.1", scale), None);
}

#[test]
fn a_key_step_starts_from_the_typed_text() {
    // Number_KeyStepCommitsTypedTextFirst, Number_WheelStepCommitsTypedTextFirst
    assert_eq!(
        count().step_by(1, false, Some("42"), Dec::from_i64(10)),
        Some(d("47"))
    );
    // Text that is the bound value as shown steps from the bound value.
    assert_eq!(
        count().step_by(1, false, Some("10"), Dec::from_i64(10)),
        Some(d("15"))
    );
}

#[test]
fn float_steps_keep_their_type() {
    // Number_LongAndFloatStepAsTheirTypes (the float case)
    let spec = NumberSpec {
        step: d("0.25"),
        ..float(2)
    };
    let v = spec.step_by(1, false, None, Dec::from_f32(1.5)).unwrap();
    assert_eq!(v.to_f32(), 1.75);
    assert_eq!(Dec::from_i64(42).to_i32(), 42);
}

#[test]
fn the_character_filter_follows_range_and_decimals() {
    // Number_CharacterFilterFollowsRangeAndDecimals
    let c = count();
    assert!(c.accepts_character('5'));
    assert!(!c.accepts_character('a'));
    assert!(!c.accepts_character('-'));
    assert!(!c.accepts_character('.'));
    let ratio = NumberSpec {
        min: d("-1"),
        max: d("1"),
        step: Dec::ONE,
        decimals: 1,
    };
    assert!(ratio.accepts_character('-'));
    assert!(ratio.accepts_character('.'));
}

#[test]
fn invalid_text_is_out_of_range_or_not_a_number() {
    // Number_InvalidTextDrawsInTheErrorColour
    assert!(!count().is_valid_text("250"));
    assert!(count().is_valid_text("50"));
    assert!(!count().is_valid_text("5 0"));
}

#[test]
fn plain_parsing_takes_a_sign_and_a_point_only() {
    assert_eq!(Dec::parse_plain("-1.50"), Some(d("-1.5")));
    assert_eq!(Dec::parse_plain(".5"), Some(d("0.5")));
    assert_eq!(Dec::parse_plain("5."), Some(d("5")));
    assert_eq!(Dec::parse_plain(" 5"), None);
    assert_eq!(Dec::parse_plain("1e3"), None);
    assert_eq!(Dec::parse_float_style(" 1e3 "), Some(d("1000")));
    assert_eq!(d("1.50").to_string(), "1.50");
}
