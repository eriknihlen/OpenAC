//! `DateTime` and `TimeSpan` text as .NET 10 writes it in the invariant (and
//! en-US) culture: the round-trip `"o"` form the plugins stamp frames with,
//! and the custom patterns they print (`yyyy-MM-dd`, `MM/dd/yyyy HH:mm:ss`,
//! `M/d/yyyy h:mm:ss tt`, `dd\.hh\:mm\:ss`).
//!
//! Time comes from the host as Unix milliseconds plus the local offset; this
//! module only turns it into text.

/// Ticks (100 ns) per unit.
const TICKS_PER_MILLISECOND: i64 = 10_000;
const TICKS_PER_SECOND: i64 = 10_000_000;
const TICKS_PER_MINUTE: i64 = 60 * TICKS_PER_SECOND;
const TICKS_PER_HOUR: i64 = 60 * TICKS_PER_MINUTE;
const TICKS_PER_DAY: i64 = 24 * TICKS_PER_HOUR;
/// Days from 0001-01-01 to 1970-01-01.
const UNIX_EPOCH_DAYS: i64 = 719_162;

const MONTH_NAMES: [&str; 12] = [
    "January",
    "February",
    "March",
    "April",
    "May",
    "June",
    "July",
    "August",
    "September",
    "October",
    "November",
    "December",
];
const DAY_NAMES: [&str; 7] = [
    "Sunday",
    "Monday",
    "Tuesday",
    "Wednesday",
    "Thursday",
    "Friday",
    "Saturday",
];

/// A format string this module does not reproduce, or one .NET refuses.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum DateFormatError {
    /// .NET accepts it, but this kit does not reproduce it.
    #[error("date format {0:?} is not supported by the compatibility kit")]
    Unsupported(String),
    /// .NET throws `FormatException` for it.
    #[error("date format {0:?} is invalid")]
    Invalid(String),
}

/// .NET's `DateTimeKind`, with the offset a local time was taken at.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DateTimeKind {
    /// `DateTimeKind.Utc`: `"o"` ends in `Z`.
    Utc,
    /// `DateTimeKind.Local` at this offset from UTC: `"o"` ends in `+hh:mm`.
    Local {
        /// Minutes ahead of UTC.
        offset_minutes: i32,
    },
    /// `DateTimeKind.Unspecified`: `"o"` has no zone suffix.
    Unspecified,
}

/// A .NET `DateTime`: ticks since 0001-01-01 00:00 and a kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DateTime {
    ticks: i64,
    kind: DateTimeKind,
}

impl DateTime {
    /// `DateTime.UtcNow` for a host clock reading in Unix milliseconds.
    #[must_use]
    pub fn utc_from_unix_millis(unix_ms: i64) -> Self {
        Self {
            ticks: (UNIX_EPOCH_DAYS * TICKS_PER_DAY) + unix_ms * TICKS_PER_MILLISECOND,
            kind: DateTimeKind::Utc,
        }
    }

    /// `DateTime.Now` for a host clock reading and the local offset.
    #[must_use]
    pub fn local_from_unix_millis(unix_ms: i64, offset_minutes: i32) -> Self {
        Self {
            ticks: (UNIX_EPOCH_DAYS * TICKS_PER_DAY)
                + unix_ms * TICKS_PER_MILLISECOND
                + i64::from(offset_minutes) * TICKS_PER_MINUTE,
            kind: DateTimeKind::Local { offset_minutes },
        }
    }

    /// A date and time from its parts (`new DateTime(y, M, d, h, m, s, ms, kind)`);
    /// `None` for a date that does not exist.
    #[must_use]
    #[allow(clippy::too_many_arguments)]
    pub fn from_parts(
        year: i32,
        month: u32,
        day: u32,
        hour: u32,
        minute: u32,
        second: u32,
        millisecond: u32,
        kind: DateTimeKind,
    ) -> Option<Self> {
        if !(1..=9999).contains(&year)
            || !(1..=12).contains(&month)
            || day == 0
            || day > days_in_month(year, month)
            || hour > 23
            || minute > 59
            || second > 59
            || millisecond > 999
        {
            return None;
        }
        let days = days_from_civil(year, month, day);
        let ticks = days * TICKS_PER_DAY
            + i64::from(hour) * TICKS_PER_HOUR
            + i64::from(minute) * TICKS_PER_MINUTE
            + i64::from(second) * TICKS_PER_SECOND
            + i64::from(millisecond) * TICKS_PER_MILLISECOND;
        Some(Self { ticks, kind })
    }

    /// The tick count (`DateTime.Ticks`).
    #[must_use]
    pub fn ticks(&self) -> i64 {
        self.ticks
    }

    /// `DateTime.AddTicks`: the same kind, moved by `ticks` (100 ns each).
    #[must_use]
    pub fn add_ticks(self, ticks: i64) -> Self {
        Self {
            ticks: self.ticks + ticks,
            kind: self.kind,
        }
    }

    /// `DateTime.ToString(format, InvariantCulture)`: the standard `"o"`/`"O"`
    /// round-trip form, or a custom pattern.
    pub fn format(&self, format: &str) -> Result<String, DateFormatError> {
        match format {
            "o" | "O" => Ok(self.round_trip()),
            "" => Err(DateFormatError::Unsupported(format.to_owned())),
            f if f.chars().count() == 1 => Err(DateFormatError::Unsupported(format.to_owned())),
            f => self.custom(f),
        }
    }

    fn parts(&self) -> Parts {
        let days = self.ticks.div_euclid(TICKS_PER_DAY);
        let rest = self.ticks.rem_euclid(TICKS_PER_DAY);
        let (year, month, day) = civil_from_days(days);
        Parts {
            year,
            month,
            day,
            day_of_week: ((days + 1).rem_euclid(7)) as usize,
            hour: (rest / TICKS_PER_HOUR) as u32,
            minute: (rest / TICKS_PER_MINUTE % 60) as u32,
            second: (rest / TICKS_PER_SECOND % 60) as u32,
            fraction: (rest % TICKS_PER_SECOND) as u32,
        }
    }

    /// `"o"`: `yyyy-MM-ddTHH:mm:ss.fffffff` and the kind's zone suffix.
    fn round_trip(&self) -> String {
        let p = self.parts();
        let mut out = format!(
            "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:07}",
            p.year, p.month, p.day, p.hour, p.minute, p.second, p.fraction
        );
        match self.kind {
            DateTimeKind::Utc => out.push('Z'),
            DateTimeKind::Local { offset_minutes } => out.push_str(&offset_text(offset_minutes)),
            DateTimeKind::Unspecified => {}
        }
        out
    }

    fn custom(&self, format: &str) -> Result<String, DateFormatError> {
        let p = self.parts();
        let chars: Vec<char> = format.chars().collect();
        let mut out = String::new();
        let mut i = 0;
        while i < chars.len() {
            let ch = chars[i];
            let count = chars[i..].iter().take_while(|&&c| c == ch).count();
            match ch {
                'd' => {
                    match count {
                        1 => out.push_str(&p.day.to_string()),
                        2 => out.push_str(&format!("{:02}", p.day)),
                        3 => out.push_str(&DAY_NAMES[p.day_of_week][..3]),
                        _ => out.push_str(DAY_NAMES[p.day_of_week]),
                    }
                    i += count;
                }
                'M' => {
                    match count {
                        1 => out.push_str(&p.month.to_string()),
                        2 => out.push_str(&format!("{:02}", p.month)),
                        3 => out.push_str(&MONTH_NAMES[p.month as usize - 1][..3]),
                        _ => out.push_str(MONTH_NAMES[p.month as usize - 1]),
                    }
                    i += count;
                }
                'y' => {
                    match count {
                        1 => out.push_str(&(p.year % 100).to_string()),
                        2 => out.push_str(&format!("{:02}", p.year % 100)),
                        n => out.push_str(&format!("{:0n$}", p.year)),
                    }
                    i += count;
                }
                'H' | 'h' | 'm' | 's' => {
                    let value = match ch {
                        'H' => p.hour,
                        'h' => match p.hour % 12 {
                            0 => 12,
                            h => h,
                        },
                        'm' => p.minute,
                        _ => p.second,
                    };
                    if count == 1 {
                        out.push_str(&value.to_string());
                    } else {
                        out.push_str(&format!("{value:02}"));
                    }
                    i += count;
                }
                'f' | 'F' => {
                    if count > 7 {
                        return Err(DateFormatError::Invalid(format.to_owned()));
                    }
                    let digits = format!("{:07}", p.fraction);
                    let mut shown = digits[..count].to_owned();
                    if ch == 'F' {
                        shown = shown.trim_end_matches('0').to_owned();
                        if shown.is_empty() && out.ends_with('.') {
                            // "F" drops a separator left with nothing after it.
                            out.pop();
                        }
                    }
                    out.push_str(&shown);
                    i += count;
                }
                't' => {
                    let designator = if p.hour < 12 { "AM" } else { "PM" };
                    out.push_str(if count == 1 {
                        &designator[..1]
                    } else {
                        designator
                    });
                    i += count;
                }
                ':' | '/' => {
                    // The invariant (and en-US) separators.
                    out.push(ch);
                    i += 1;
                }
                '\'' | '"' => {
                    let end = chars[i + 1..]
                        .iter()
                        .position(|&c| c == ch)
                        .ok_or_else(|| DateFormatError::Invalid(format.to_owned()))?;
                    out.extend(&chars[i + 1..i + 1 + end]);
                    i += end + 2;
                }
                '\\' => {
                    let next = chars
                        .get(i + 1)
                        .ok_or_else(|| DateFormatError::Invalid(format.to_owned()))?;
                    out.push(*next);
                    i += 2;
                }
                'z' | 'K' | 'g' | '%' => {
                    return Err(DateFormatError::Unsupported(format.to_owned()));
                }
                other => {
                    out.push(other);
                    i += 1;
                }
            }
        }
        Ok(out)
    }
}

/// A .NET `TimeSpan`, in ticks.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TimeSpan {
    ticks: i64,
}

impl TimeSpan {
    /// A span of this many ticks (100 ns).
    #[must_use]
    pub fn from_ticks(ticks: i64) -> Self {
        Self { ticks }
    }

    /// A span of this many milliseconds.
    #[must_use]
    pub fn from_millis(ms: i64) -> Self {
        Self {
            ticks: ms * TICKS_PER_MILLISECOND,
        }
    }

    /// The span between two times (`a - b`).
    #[must_use]
    pub fn between(later: DateTime, earlier: DateTime) -> Self {
        Self {
            ticks: later.ticks - earlier.ticks,
        }
    }

    /// `TimeSpan.ToString(format, InvariantCulture)` for a custom format
    /// (`dd\.hh\:mm\:ss`): `d`, `h`, `m`, `s`, `f`, `F`, quoted and
    /// back-slashed literals. A negative span prints its magnitude, as .NET's
    /// custom formats do.
    pub fn format(&self, format: &str) -> Result<String, DateFormatError> {
        let invalid = || DateFormatError::Invalid(format.to_owned());
        let ticks = self.ticks.unsigned_abs();
        let days = ticks / TICKS_PER_DAY as u64;
        let hours = ticks / TICKS_PER_HOUR as u64 % 24;
        let minutes = ticks / TICKS_PER_MINUTE as u64 % 60;
        let seconds = ticks / TICKS_PER_SECOND as u64 % 60;
        let fraction = ticks % TICKS_PER_SECOND as u64;
        let chars: Vec<char> = format.chars().collect();
        let mut out = String::new();
        let mut i = 0;
        while i < chars.len() {
            let ch = chars[i];
            let count = chars[i..].iter().take_while(|&&c| c == ch).count();
            match ch {
                'd' => {
                    if count > 8 {
                        return Err(invalid());
                    }
                    out.push_str(&format!("{days:0count$}"));
                    i += count;
                }
                'h' | 'm' | 's' => {
                    if count > 2 {
                        return Err(invalid());
                    }
                    let value = match ch {
                        'h' => hours,
                        'm' => minutes,
                        _ => seconds,
                    };
                    out.push_str(&format!("{value:0count$}"));
                    i += count;
                }
                'f' | 'F' => {
                    if count > 7 {
                        return Err(invalid());
                    }
                    let digits = format!("{fraction:07}");
                    let shown = &digits[..count];
                    out.push_str(if ch == 'F' {
                        shown.trim_end_matches('0')
                    } else {
                        shown
                    });
                    i += count;
                }
                '\'' | '"' => {
                    let end = chars[i + 1..]
                        .iter()
                        .position(|&c| c == ch)
                        .ok_or_else(invalid)?;
                    out.extend(&chars[i + 1..i + 1 + end]);
                    i += end + 2;
                }
                '\\' => {
                    out.push(*chars.get(i + 1).ok_or_else(invalid)?);
                    i += 2;
                }
                // Any other character must be quoted or escaped in a TimeSpan format.
                _ => return Err(invalid()),
            }
        }
        Ok(out)
    }
}

struct Parts {
    year: i32,
    month: u32,
    day: u32,
    day_of_week: usize,
    hour: u32,
    minute: u32,
    second: u32,
    /// Ticks within the second.
    fraction: u32,
}

fn offset_text(offset_minutes: i32) -> String {
    let sign = if offset_minutes < 0 { '-' } else { '+' };
    let magnitude = offset_minutes.unsigned_abs();
    format!("{sign}{:02}:{:02}", magnitude / 60, magnitude % 60)
}

fn is_leap(year: i32) -> bool {
    (year % 4 == 0 && year % 100 != 0) || year % 400 == 0
}

fn days_in_month(year: i32, month: u32) -> u32 {
    match month {
        2 if is_leap(year) => 29,
        2 => 28,
        4 | 6 | 9 | 11 => 30,
        _ => 31,
    }
}

/// Days since 0001-01-01 for a proleptic Gregorian date.
fn days_from_civil(year: i32, month: u32, day: u32) -> i64 {
    let y = i64::from(year) - 1;
    let mut days = y * 365 + y / 4 - y / 100 + y / 400;
    for m in 1..month {
        days += i64::from(days_in_month(year, m));
    }
    days + i64::from(day) - 1
}

/// The proleptic Gregorian date of a day count since 0001-01-01.
fn civil_from_days(days: i64) -> (i32, u32, u32) {
    // 400-year cycles of 146,097 days, then centuries, 4-year cycles, years.
    let n400 = days.div_euclid(146_097);
    let mut rest = days.rem_euclid(146_097);
    let n100 = (rest / 36_524).min(3);
    rest -= n100 * 36_524;
    let n4 = rest / 1_461;
    rest -= n4 * 1_461;
    let n1 = (rest / 365).min(3);
    rest -= n1 * 365;
    let year = (n400 * 400 + n100 * 100 + n4 * 4 + n1 + 1) as i32;
    let mut month = 1;
    let mut day_of_year = rest as u32;
    while day_of_year >= days_in_month(year, month) {
        day_of_year -= days_in_month(year, month);
        month += 1;
    }
    (year, month, day_of_year + 1)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mosswart_massacre_frame_stamps() {
        // MM tests: harness clock UTC 2026-09-25 08:30:15.123 → "o".
        let t = DateTime::from_parts(2026, 9, 25, 8, 30, 15, 123, DateTimeKind::Utc).unwrap();
        assert_eq!(t.format("o").unwrap(), "2026-09-25T08:30:15.1230000Z");
        let unix = DateTime::utc_from_unix_millis(1_790_325_015_123);
        assert_eq!(unix, t);
        // 90 minutes of session: "00.01:30:00".
        let span = TimeSpan::from_millis(90 * 60 * 1000);
        assert_eq!(span.format(r"dd\.hh\:mm\:ss").unwrap(), "00.01:30:00");
    }

    #[test]
    fn civil_dates_round_trip() {
        for days in [
            0, 59, 60, 365, 366, 146_096, 146_097, 719_162, 739_883, 3_652_058,
        ] {
            let (y, m, d) = civil_from_days(days);
            assert_eq!(days_from_civil(y, m, d), days, "{y}-{m}-{d}");
        }
    }
}
