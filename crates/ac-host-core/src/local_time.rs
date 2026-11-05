//! The player's local time zone, for the times the client and the launcher
//! show (chat timestamps, house dates, the date of a saved list): how far
//! local time is from UTC at a given moment (daylight saving included).
//!
//! The standard library has no time zones. On Windows the operating system
//! converts the moment (`SystemTimeToTzSpecificLocalTime` through
//! `windows-sys`); on Unix the zone file is read once (`$TZ`, else
//! `/etc/localtime`) and evaluated here: its transition table, then the
//! rule string at its end for moments past the last transition. No C code
//! is compiled. Anything unreadable falls back to UTC.

/// The local zone the session formats times in.
#[derive(Debug, Clone, PartialEq)]
pub struct LocalZone {
    kind: Kind,
}

#[derive(Debug, Clone, PartialEq)]
enum Kind {
    Fixed(i32),
    #[cfg(windows)]
    System,
    #[cfg_attr(not(unix), allow(dead_code))]
    Rules(tz::Zone),
}

impl LocalZone {
    /// The operating system's current zone.
    #[must_use]
    pub fn system() -> Self {
        #[cfg(windows)]
        {
            Self { kind: Kind::System }
        }
        #[cfg(unix)]
        {
            Self {
                kind: unix::load().map_or(Kind::Fixed(0), Kind::Rules),
            }
        }
        #[cfg(not(any(windows, unix)))]
        {
            Self::fixed(0)
        }
    }

    /// A zone a fixed number of seconds east of UTC.
    #[must_use]
    pub fn fixed(utc_offset_seconds: i32) -> Self {
        Self {
            kind: Kind::Fixed(utc_offset_seconds),
        }
    }

    /// How many seconds local time is ahead of UTC at `unix_ms`
    /// (milliseconds since the Unix epoch, UTC).
    #[must_use]
    pub fn utc_offset_seconds(&self, unix_ms: i64) -> i32 {
        match &self.kind {
            Kind::Fixed(offset) => *offset,
            #[cfg(windows)]
            Kind::System => windows::utc_offset_seconds(unix_ms).unwrap_or(0),
            Kind::Rules(zone) => zone.utc_offset(unix_ms.div_euclid(1000)),
        }
    }
}

#[cfg(windows)]
mod windows {
    //! The OS converts a UTC moment to local time with the current zone's
    //! rules; the offset is the difference.

    use windows_sys::Win32::Foundation::{FILETIME, SYSTEMTIME};
    use windows_sys::Win32::System::Time::{
        FileTimeToSystemTime, SystemTimeToFileTime, SystemTimeToTzSpecificLocalTime,
    };

    /// 100-nanosecond intervals between 1601-01-01 and 1970-01-01.
    const EPOCH_DIFFERENCE: i64 = 116_444_736_000_000_000;
    const TICKS_PER_MS: i64 = 10_000;
    const TICKS_PER_SECOND: i64 = 10_000_000;

    fn to_filetime(ticks: i64) -> FILETIME {
        let bits = ticks as u64;
        FILETIME {
            dwLowDateTime: bits as u32,
            dwHighDateTime: (bits >> 32) as u32,
        }
    }

    fn from_filetime(time: FILETIME) -> i64 {
        ((u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime)) as i64
    }

    #[allow(unsafe_code)]
    pub(super) fn utc_offset_seconds(unix_ms: i64) -> Option<i32> {
        let ticks = unix_ms
            .checked_mul(TICKS_PER_MS)?
            .checked_add(EPOCH_DIFFERENCE)?;
        if ticks < 0 {
            return None;
        }
        let utc_file = to_filetime(ticks);
        let mut utc = SYSTEMTIME::default();
        let mut local = SYSTEMTIME::default();
        let mut local_file = FILETIME::default();
        // SAFETY: every pointer is to a live, properly aligned local of the
        // type the function expects; a null zone pointer asks for the
        // current zone, as documented. Each call writes only its out
        // parameter and its result is checked before the value is read.
        let converted = unsafe {
            FileTimeToSystemTime(&utc_file, &mut utc) != 0
                && SystemTimeToTzSpecificLocalTime(std::ptr::null(), &utc, &mut local) != 0
                && SystemTimeToFileTime(&local, &mut local_file) != 0
        };
        if !converted {
            return None;
        }
        let difference = from_filetime(local_file) - ticks;
        i32::try_from(difference / TICKS_PER_SECOND).ok()
    }
}

#[cfg(unix)]
mod unix {
    //! Find and read the zone file.

    use super::tz;

    pub(super) fn load() -> Option<tz::Zone> {
        match std::env::var("TZ") {
            Ok(value) if value.is_empty() => Some(tz::Zone::utc()),
            Ok(value) => {
                let name = value.strip_prefix(':').unwrap_or(&value);
                let path = if name.starts_with('/') {
                    std::path::PathBuf::from(name)
                } else {
                    std::path::Path::new("/usr/share/zoneinfo").join(name)
                };
                match std::fs::read(path) {
                    Ok(bytes) => tz::Zone::parse_tzif(&bytes),
                    // Not a zone file name: a rule string itself.
                    Err(_) => tz::Rule::parse(name).map(tz::Zone::from_rule),
                }
            }
            Err(_) => tz::Zone::parse_tzif(&std::fs::read("/etc/localtime").ok()?),
        }
    }
}

#[cfg_attr(not(unix), allow(dead_code))]
pub(crate) mod tz {
    //! Zone files (the compiled `TZif` format: a table of transitions and
    //! offsets, then a rule string for later moments) and the rule strings
    //! (`CET-1CEST,M3.5.0,M10.5.0/3`: names, offsets west of UTC, and the
    //! month/week/weekday dates daylight time starts and ends).

    /// A parsed zone.
    #[derive(Debug, Clone, PartialEq)]
    pub struct Zone {
        /// Transition moments (Unix seconds) with the offset in force from
        /// each, ascending.
        transitions: Vec<(i64, i32)>,
        /// The offset before the first transition.
        initial: i32,
        /// The rule for moments past the last transition.
        rule: Option<Rule>,
    }

    impl Zone {
        /// UTC.
        #[must_use]
        pub fn utc() -> Self {
            Self {
                transitions: Vec::new(),
                initial: 0,
                rule: None,
            }
        }

        /// A zone that is only a rule.
        #[must_use]
        pub fn from_rule(rule: Rule) -> Self {
            Self {
                transitions: Vec::new(),
                initial: rule.std_offset,
                rule: Some(rule),
            }
        }

        /// Seconds east of UTC at `unix_seconds`.
        #[must_use]
        pub fn utc_offset(&self, unix_seconds: i64) -> i32 {
            match self.transitions.last() {
                Some(&(last, offset)) if unix_seconds >= last => self
                    .rule
                    .as_ref()
                    .map_or(offset, |rule| rule.utc_offset(unix_seconds)),
                Some(_) => {
                    let index = self
                        .transitions
                        .partition_point(|&(at, _)| at <= unix_seconds);
                    match index {
                        0 => self.initial,
                        i => self.transitions[i - 1].1,
                    }
                }
                None => self
                    .rule
                    .as_ref()
                    .map_or(self.initial, |rule| rule.utc_offset(unix_seconds)),
            }
        }

        /// Read a zone file. Version 2 and later files are read from their
        /// 64-bit block; `None` for anything malformed.
        #[must_use]
        pub fn parse_tzif(bytes: &[u8]) -> Option<Self> {
            let header = Header::read(bytes)?;
            if header.version >= b'2' {
                let second = bytes.get(header.v1_len()?..)?;
                let header2 = Header::read(second)?;
                let zone = header2.read_block(second, 8)?;
                let footer = second.get(header2.v2_len()?..)?;
                Some(Self {
                    rule: parse_footer(footer),
                    ..zone
                })
            } else {
                header.read_block(bytes, 4)
            }
        }
    }

    struct Header {
        version: u8,
        isutcnt: usize,
        isstdcnt: usize,
        leapcnt: usize,
        timecnt: usize,
        typecnt: usize,
        charcnt: usize,
    }

    const HEADER_LEN: usize = 44;

    impl Header {
        fn read(bytes: &[u8]) -> Option<Self> {
            if bytes.get(..4)? != b"TZif" {
                return None;
            }
            let count = |i: usize| -> Option<usize> {
                let at = 20 + 4 * i;
                let word = bytes.get(at..at + 4)?;
                usize::try_from(u32::from_be_bytes(word.try_into().ok()?)).ok()
            };
            Some(Self {
                version: *bytes.get(4)?,
                isutcnt: count(0)?,
                isstdcnt: count(1)?,
                leapcnt: count(2)?,
                timecnt: count(3)?,
                typecnt: count(4)?,
                charcnt: count(5)?,
            })
        }

        fn data_len(&self, time_size: usize) -> Option<usize> {
            let len = self
                .timecnt
                .checked_mul(time_size + 1)?
                .checked_add(self.typecnt.checked_mul(6)?)?
                .checked_add(self.charcnt)?
                .checked_add(self.leapcnt.checked_mul(time_size + 4)?)?
                .checked_add(self.isstdcnt)?
                .checked_add(self.isutcnt)?;
            len.checked_add(HEADER_LEN)
        }

        fn v1_len(&self) -> Option<usize> {
            self.data_len(4)
        }

        fn v2_len(&self) -> Option<usize> {
            self.data_len(8)
        }

        fn read_block(&self, bytes: &[u8], time_size: usize) -> Option<Zone> {
            if self.typecnt == 0 {
                return None;
            }
            let times_at = HEADER_LEN;
            let indices_at = times_at + self.timecnt * time_size;
            let types_at = indices_at + self.timecnt;
            let offset_of = |index: usize| -> Option<i32> {
                let at = types_at + 6 * index;
                Some(i32::from_be_bytes(bytes.get(at..at + 4)?.try_into().ok()?))
            };
            let mut transitions = Vec::with_capacity(self.timecnt);
            for i in 0..self.timecnt {
                let at = times_at + i * time_size;
                let raw = bytes.get(at..at + time_size)?;
                let moment = if time_size == 8 {
                    i64::from_be_bytes(raw.try_into().ok()?)
                } else {
                    i64::from(i32::from_be_bytes(raw.try_into().ok()?))
                };
                let index = usize::from(*bytes.get(indices_at + i)?);
                if index >= self.typecnt {
                    return None;
                }
                transitions.push((moment, offset_of(index)?));
            }
            Some(Zone {
                transitions,
                initial: offset_of(0)?,
                rule: None,
            })
        }
    }

    fn parse_footer(footer: &[u8]) -> Option<Rule> {
        let text = std::str::from_utf8(footer).ok()?;
        let text = text.strip_prefix('\n')?;
        let end = text.find('\n')?;
        Rule::parse(&text[..end])
    }

    /// A date rule: month, week (5 = the last) and weekday (0 = Sunday);
    /// a day of the year 1..=365 that skips 29 February; or a day of the
    /// year 0..=365 that counts it.
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    enum Date {
        MonthWeekDay(u32, u32, u32),
        JulianNoLeap(u32),
        Julian(u32),
    }

    /// When daylight time starts or ends: a date, and seconds after local
    /// midnight.
    type Switch = (Date, i32);

    /// A rule string.
    #[derive(Debug, Clone, PartialEq)]
    pub struct Rule {
        /// Standard time's seconds east of UTC.
        std_offset: i32,
        /// Daylight time: its offset, and when it starts and ends.
        dst: Option<(i32, Switch, Switch)>,
    }

    impl Rule {
        /// Parse a rule string; `None` for anything it cannot read.
        #[must_use]
        pub fn parse(text: &str) -> Option<Self> {
            let mut rest = text;
            skip_name(&mut rest)?;
            let std_offset = -parse_offset(&mut rest)?;
            if rest.is_empty() {
                return Some(Self {
                    std_offset,
                    dst: None,
                });
            }
            skip_name(&mut rest)?;
            let dst_offset = if rest.starts_with(',') || rest.is_empty() {
                std_offset + 3600
            } else {
                -parse_offset(&mut rest)?
            };
            // Without dates, the US rules of the format's definition apply.
            let (start, end) = if rest.is_empty() {
                (
                    (Date::MonthWeekDay(3, 2, 0), 7200),
                    (Date::MonthWeekDay(11, 1, 0), 7200),
                )
            } else {
                rest = rest.strip_prefix(',')?;
                let start = parse_transition(&mut rest)?;
                rest = rest.strip_prefix(',')?;
                let end = parse_transition(&mut rest)?;
                if !rest.is_empty() {
                    return None;
                }
                (start, end)
            };
            Some(Self {
                std_offset,
                dst: Some((dst_offset, start, end)),
            })
        }

        /// Seconds east of UTC at `unix_seconds`.
        #[must_use]
        pub fn utc_offset(&self, unix_seconds: i64) -> i32 {
            let Some((dst_offset, (start_date, start_time), (end_date, end_time))) = self.dst
            else {
                return self.std_offset;
            };
            let year = year_of(unix_seconds + i64::from(self.std_offset));
            // The start is given in standard time, the end in daylight time.
            let start =
                date_seconds(year, start_date) + i64::from(start_time) - i64::from(self.std_offset);
            let end = date_seconds(year, end_date) + i64::from(end_time) - i64::from(dst_offset);
            let in_dst = if start < end {
                start <= unix_seconds && unix_seconds < end
            } else {
                !(end <= unix_seconds && unix_seconds < start)
            };
            if in_dst { dst_offset } else { self.std_offset }
        }
    }

    fn skip_name(rest: &mut &str) -> Option<()> {
        if let Some(quoted) = rest.strip_prefix('<') {
            let end = quoted.find('>')?;
            *rest = &quoted[end + 1..];
            return Some(());
        }
        let len = rest
            .find(|c: char| !c.is_ascii_alphabetic())
            .unwrap_or(rest.len());
        if len < 3 {
            return None;
        }
        *rest = &rest[len..];
        Some(())
    }

    /// `[+-]hh[:mm[:ss]]`, as seconds.
    fn parse_offset(rest: &mut &str) -> Option<i32> {
        let (sign, body) = match rest.as_bytes().first()? {
            b'-' => (-1, &rest[1..]),
            b'+' => (1, &rest[1..]),
            _ => (1, *rest),
        };
        let len = body
            .find(|c: char| !(c.is_ascii_digit() || c == ':'))
            .unwrap_or(body.len());
        let mut seconds = 0i32;
        let mut scale = 3600;
        for part in body[..len].split(':') {
            if part.is_empty() || scale == 0 {
                return None;
            }
            seconds = seconds.checked_add(part.parse::<i32>().ok()?.checked_mul(scale)?)?;
            scale /= 60;
        }
        *rest = &body[len..];
        Some(sign * seconds)
    }

    fn parse_transition(rest: &mut &str) -> Option<(Date, i32)> {
        let date = if let Some(body) = rest.strip_prefix('M') {
            let len = body
                .find(|c: char| !(c.is_ascii_digit() || c == '.'))
                .unwrap_or(body.len());
            let mut parts = body[..len].split('.').map(str::parse::<u32>);
            let month = parts.next()?.ok()?;
            let week = parts.next()?.ok()?;
            let day = parts.next()?.ok()?;
            if parts.next().is_some()
                || !(1..=12).contains(&month)
                || !(1..=5).contains(&week)
                || day > 6
            {
                return None;
            }
            *rest = &body[len..];
            Date::MonthWeekDay(month, week, day)
        } else if let Some(body) = rest.strip_prefix('J') {
            let len = body
                .find(|c: char| !c.is_ascii_digit())
                .unwrap_or(body.len());
            let day = body[..len].parse::<u32>().ok()?;
            if !(1..=365).contains(&day) {
                return None;
            }
            *rest = &body[len..];
            Date::JulianNoLeap(day)
        } else {
            let len = rest
                .find(|c: char| !c.is_ascii_digit())
                .unwrap_or(rest.len());
            let day = rest[..len].parse::<u32>().ok()?;
            if day > 365 {
                return None;
            }
            *rest = &rest[len..];
            Date::Julian(day)
        };
        let time = match rest.strip_prefix('/') {
            Some(body) => {
                *rest = body;
                parse_offset(rest)?
            }
            None => 7200,
        };
        Some((date, time))
    }

    fn is_leap(year: i64) -> bool {
        year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)
    }

    fn days_in_month(year: i64, month: u32) -> u32 {
        match month {
            2 if is_leap(year) => 29,
            2 => 28,
            4 | 6 | 9 | 11 => 30,
            _ => 31,
        }
    }

    /// Days from 1970-01-01 to the civil date (proleptic Gregorian).
    fn days_from_civil(year: i64, month: u32, day: u32) -> i64 {
        let y = if month <= 2 { year - 1 } else { year };
        let era = y.div_euclid(400);
        let yoe = y - era * 400;
        let m = i64::from(month);
        let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + i64::from(day) - 1;
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        era * 146_097 + doe - 719_468
    }

    fn year_of(unix_seconds: i64) -> i64 {
        let days = unix_seconds.div_euclid(86_400) + 719_468;
        let era = days.div_euclid(146_097);
        let doe = days - era * 146_097;
        let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        let mp = (5 * doy + 2) / 153;
        let month = if mp < 10 { mp + 3 } else { mp - 9 };
        yoe + era * 400 + i64::from(month <= 2)
    }

    /// Unix seconds of local midnight on the rule date in `year`.
    fn date_seconds(year: i64, date: Date) -> i64 {
        let days = match date {
            Date::MonthWeekDay(month, week, weekday) => {
                let first = days_from_civil(year, month, 1);
                // 1970-01-01 was a Thursday (weekday 4).
                let first_weekday = (first + 4).rem_euclid(7) as u32;
                let mut day = 1 + (weekday + 7 - first_weekday) % 7 + (week - 1) * 7;
                while day > days_in_month(year, month) {
                    day -= 7;
                }
                days_from_civil(year, month, day)
            }
            Date::JulianNoLeap(n) => {
                let leap_skip = i64::from(is_leap(year) && n >= 60);
                days_from_civil(year, 1, 1) + i64::from(n) - 1 + leap_skip
            }
            Date::Julian(n) => days_from_civil(year, 1, 1) + i64::from(n),
        };
        days * 86_400
    }

    #[cfg(test)]
    mod tests {
        //! Golden offsets checked against the zone database's published
        //! rules: Central European time switches at 01:00 UTC on the last
        //! Sundays of March and October; US Eastern at 02:00 local on the
        //! second Sunday of March and the first of November.

        use super::*;

        /// 2026-03-29 01:00:00 UTC.
        const CET_SPRING: i64 = 1_774_746_000;
        /// 2026-10-25 01:00:00 UTC.
        const CET_AUTUMN: i64 = 1_792_890_000;

        #[test]
        fn central_european_rules_switch_on_the_last_sundays() {
            let rule = Rule::parse("CET-1CEST,M3.5.0,M10.5.0/3").unwrap();
            assert_eq!(rule.utc_offset(CET_SPRING - 1), 3600);
            assert_eq!(rule.utc_offset(CET_SPRING), 7200);
            assert_eq!(rule.utc_offset(CET_AUTUMN - 1), 7200);
            assert_eq!(rule.utc_offset(CET_AUTUMN), 3600);
        }

        #[test]
        fn us_eastern_rules_switch_on_the_second_and_first_sundays() {
            let rule = Rule::parse("EST5EDT,M3.2.0,M11.1.0").unwrap();
            // 2026-03-08 07:00 UTC is 02:00 EST.
            assert_eq!(rule.utc_offset(1_772_953_200 - 1), -5 * 3600);
            assert_eq!(rule.utc_offset(1_772_953_200), -4 * 3600);
            // 2026-11-01 06:00 UTC is 02:00 EDT.
            assert_eq!(rule.utc_offset(1_793_512_800 - 1), -4 * 3600);
            assert_eq!(rule.utc_offset(1_793_512_800), -5 * 3600);
        }

        #[test]
        fn southern_rules_wrap_the_year_and_quoted_names_read() {
            // New Zealand: daylight from the last Sunday of September to the
            // first Sunday of April.
            let rule = Rule::parse("NZST-12NZDT,M9.5.0,M4.1.0/3").unwrap();
            // 2026-01-15: summer.
            assert_eq!(rule.utc_offset(1_768_435_200), 13 * 3600);
            // 2026-07-15: winter.
            assert_eq!(rule.utc_offset(1_784_073_600), 12 * 3600);
            let fixed = Rule::parse("<+0530>-5:30").unwrap();
            assert_eq!(fixed.utc_offset(0), 5 * 3600 + 1800);
        }

        #[test]
        fn malformed_rules_are_refused() {
            for text in [
                "",
                "C",
                "CET",
                "CET-1CEST,M13.1.0,M10.5.0",
                "CET-1CEST,M3.5.0",
            ] {
                assert!(Rule::parse(text).is_none(), "{text}");
            }
        }

        fn tzif(version: u8, transitions: &[(i64, u8)], types: &[i32], footer: &str) -> Vec<u8> {
            let header = |out: &mut Vec<u8>| {
                out.extend_from_slice(b"TZif");
                out.push(version);
                out.extend_from_slice(&[0; 15]);
                for count in [0, 0, 0, transitions.len(), types.len(), 4] {
                    out.extend_from_slice(&(count as u32).to_be_bytes());
                }
            };
            let block = |out: &mut Vec<u8>, wide: bool| {
                for &(at, _) in transitions {
                    if wide {
                        out.extend_from_slice(&at.to_be_bytes());
                    } else {
                        out.extend_from_slice(&(at as i32).to_be_bytes());
                    }
                }
                out.extend(transitions.iter().map(|&(_, index)| index));
                for &offset in types {
                    out.extend_from_slice(&offset.to_be_bytes());
                    out.extend_from_slice(&[0, 0]);
                }
                out.extend_from_slice(b"ABC\0");
            };
            let mut out = Vec::new();
            header(&mut out);
            block(&mut out, false);
            if version >= b'2' {
                header(&mut out);
                block(&mut out, true);
                out.push(b'\n');
                out.extend_from_slice(footer.as_bytes());
                out.push(b'\n');
            }
            out
        }

        #[test]
        fn zone_files_read_their_transitions_then_their_rule() {
            let bytes = tzif(
                b'2',
                &[
                    (CET_SPRING - 86_400 * 365, 1),
                    (CET_SPRING - 86_400 * 180, 0),
                ],
                &[3600, 7200],
                "CET-1CEST,M3.5.0,M10.5.0/3",
            );
            let zone = Zone::parse_tzif(&bytes).unwrap();
            assert_eq!(zone.utc_offset(0), 3600);
            assert_eq!(zone.utc_offset(CET_SPRING - 86_400 * 300), 7200);
            // Past the table: the rule decides.
            assert_eq!(zone.utc_offset(CET_SPRING - 1), 3600);
            assert_eq!(zone.utc_offset(CET_SPRING), 7200);
            let v1 = tzif(b'\0', &[(1_000, 1)], &[0, -18_000], "");
            assert_eq!(Zone::parse_tzif(&v1).unwrap().utc_offset(2_000), -18_000);
        }

        #[test]
        fn malformed_zone_files_are_refused() {
            assert!(Zone::parse_tzif(b"").is_none());
            assert!(Zone::parse_tzif(b"TZif2").is_none());
            let mut bytes = tzif(b'2', &[(0, 5)], &[0], "UTC0");
            assert!(Zone::parse_tzif(&bytes).is_none());
            bytes.truncate(60);
            assert!(Zone::parse_tzif(&bytes).is_none());
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_fixed_zone_is_its_offset() {
        assert_eq!(LocalZone::fixed(-7200).utc_offset_seconds(0), -7200);
    }

    #[test]
    fn the_system_zone_answers_a_whole_number_of_minutes_within_a_day() {
        let offset = LocalZone::system().utc_offset_seconds(1_790_000_000_000);
        assert!(offset.abs() <= 14 * 3600);
        assert_eq!(offset % 60, 0);
    }
}
