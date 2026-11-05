//! ISO 8601 times in the launcher's files: written as UTC with seven
//! fractional digits and `+00:00` (the round-trip form OpenAC's files carry),
//! read in the extended profile with or without an offset.

use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// A time read from text: the instant, and the offset the text named.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ParsedTime {
    /// The instant.
    pub instant: SystemTime,
    /// The offset from UTC in minutes the text named, `None` when it named
    /// none (OpenAC then assumes local time; callers here refuse it or treat
    /// it as not UTC).
    pub offset_minutes: Option<i32>,
}

/// `instant` as `yyyy-MM-ddTHH:mm:ss.fffffff+00:00`.
pub fn format_utc(instant: SystemTime) -> String {
    ac_host_core::status::Timestamp(instant).to_string()
}

/// Reads `yyyy-MM-dd`, optionally followed by `THH:mm`, `:ss`, a fraction of
/// one to seven digits, and `Z` or `+HH:mm` / `-HH:mm`.
pub fn parse_iso8601(text: &str) -> Option<ParsedTime> {
    let bytes = text.as_bytes();
    let mut position = 0;
    let year = digits(bytes, &mut position, 4)?;
    expect(bytes, &mut position, b'-')?;
    let month = digits(bytes, &mut position, 2)?;
    expect(bytes, &mut position, b'-')?;
    let day = digits(bytes, &mut position, 2)?;
    if !(1..=12).contains(&month) || day < 1 || day > days_in_month(year, month) {
        return None;
    }
    let (mut hour, mut minute, mut second, mut nanos) = (0, 0, 0, 0u32);
    let mut offset_minutes = None;
    if position < bytes.len() {
        expect(bytes, &mut position, b'T')?;
        hour = digits(bytes, &mut position, 2)?;
        expect(bytes, &mut position, b':')?;
        minute = digits(bytes, &mut position, 2)?;
        if bytes.get(position) == Some(&b':') {
            position += 1;
            second = digits(bytes, &mut position, 2)?;
            if bytes.get(position) == Some(&b'.') {
                position += 1;
                let start = position;
                let mut fraction = 0u32;
                while position < bytes.len() && bytes[position].is_ascii_digit() {
                    if position - start >= 7 {
                        return None;
                    }
                    fraction = fraction * 10 + u32::from(bytes[position] - b'0');
                    position += 1;
                }
                let count = position - start;
                if count == 0 {
                    return None;
                }
                nanos = fraction * 10u32.pow(9 - u32::try_from(count).ok()?);
            }
        }
        if hour > 23 || minute > 59 || second > 59 {
            return None;
        }
        match bytes.get(position) {
            None => {}
            Some(b'Z') => {
                position += 1;
                offset_minutes = Some(0);
            }
            Some(&sign @ (b'+' | b'-')) => {
                position += 1;
                let offset_hours = digits(bytes, &mut position, 2)?;
                expect(bytes, &mut position, b':')?;
                let offset_minute = digits(bytes, &mut position, 2)?;
                if offset_hours > 14 || offset_minute > 59 {
                    return None;
                }
                let total = i32::try_from(offset_hours * 60 + offset_minute).ok()?;
                offset_minutes = Some(if sign == b'-' { -total } else { total });
            }
            Some(_) => return None,
        }
        if position != bytes.len() {
            return None;
        }
    }
    let days = days_from_civil(year, month, day);
    let local_seconds = days * 86_400 + hour * 3600 + minute * 60 + second;
    let utc_seconds = local_seconds - i64::from(offset_minutes.unwrap_or(0)) * 60;
    let instant = if utc_seconds >= 0 {
        UNIX_EPOCH + Duration::new(u64::try_from(utc_seconds).ok()?, nanos)
    } else {
        let before = Duration::new(u64::try_from(-utc_seconds).ok()?, 0);
        (UNIX_EPOCH - before) + Duration::new(0, nanos)
    };
    Some(ParsedTime {
        instant,
        offset_minutes,
    })
}

fn digits(bytes: &[u8], position: &mut usize, count: usize) -> Option<i64> {
    let slice = bytes.get(*position..*position + count)?;
    if !slice.iter().all(u8::is_ascii_digit) {
        return None;
    }
    *position += count;
    Some(
        slice
            .iter()
            .fold(0i64, |value, digit| value * 10 + i64::from(digit - b'0')),
    )
}

fn expect(bytes: &[u8], position: &mut usize, wanted: u8) -> Option<()> {
    (bytes.get(*position) == Some(&wanted)).then(|| *position += 1)
}

fn is_leap(year: i64) -> bool {
    (year % 4 == 0 && year % 100 != 0) || year % 400 == 0
}

fn days_in_month(year: i64, month: i64) -> i64 {
    match month {
        2 if is_leap(year) => 29,
        2 => 28,
        4 | 6 | 9 | 11 => 30,
        _ => 31,
    }
}

/// Proleptic Gregorian date to days since 1970-01-01.
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let year = if month <= 2 { year - 1 } else { year };
    let era = year.div_euclid(400);
    let year_of_era = year.rem_euclid(400);
    let month_index = if month > 2 { month - 3 } else { month + 9 };
    let day_of_year = (153 * month_index + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_the_written_form() {
        let instant = UNIX_EPOCH + Duration::new(1_790_000_000, 123_456_700);
        let text = format_utc(instant);
        assert_eq!(text, "2026-09-21T14:13:20.1234567+00:00");
        let parsed = parse_iso8601(&text).unwrap();
        assert_eq!(parsed.instant, instant);
        assert_eq!(parsed.offset_minutes, Some(0));
    }

    #[test]
    fn offsets_and_short_forms() {
        let z = parse_iso8601("2026-01-02T03:04:05Z").unwrap();
        let plus = parse_iso8601("2026-01-02T05:04:05+02:00").unwrap();
        assert_eq!(z.instant, plus.instant);
        assert_eq!(plus.offset_minutes, Some(120));
        let local = parse_iso8601("2026-01-02T03:04").unwrap();
        assert_eq!(local.offset_minutes, None);
        assert!(parse_iso8601("2026-01-02").is_some());
        assert!(parse_iso8601("2026-02-30T00:00:00Z").is_none());
        assert!(parse_iso8601("2026-01-02T03:04:05.12345678Z").is_none());
        assert!(parse_iso8601("not a time").is_none());
        assert!(parse_iso8601("2026-01-02T03:04:05Zjunk").is_none());
    }
}
