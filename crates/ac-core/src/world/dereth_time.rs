//! Game time: the Dereth calendar and time of day.
//!
//! The server sends the game clock as a tick count (a double, in seconds).
//! Everything else follows from fixed lengths: a day is 7620 ticks (2 hours
//! 7 minutes of real time), a month 30 days, a year 12 months. A day has 16
//! named slots ("hours"), each half a Derethian hour long.
//!
//! The calendar is anchored by an origin offset that the world's region data
//! supplies: the tick count is shifted by it before any division, so tick 0
//! need not fall at midnight. Years count from [`ZERO_YEAR`].

use std::fmt;

/// Named slots in one day.
pub const HOURS_IN_A_DAY: i32 = 16;
/// Days in one month.
pub const DAYS_IN_A_MONTH: i32 = 30;
/// Months in one year.
pub const MONTHS_IN_A_YEAR: i32 = 12;

/// Ticks (seconds) in one day.
pub const DAY_TICKS: f64 = 7620.0;
/// Ticks in one hour slot (476.25).
pub const HOUR_TICKS: f64 = DAY_TICKS / HOURS_IN_A_DAY as f64;
/// Ticks in one month (228 600).
pub const MONTH_TICKS: f64 = DAY_TICKS * DAYS_IN_A_MONTH as f64;
/// Ticks in one year (2 743 200).
pub const YEAR_TICKS: f64 = MONTH_TICKS * MONTHS_IN_A_YEAR as f64;

/// The largest tick count the game client accepts on connect.
pub const MAX_TICKS: f64 = 1_073_741_828.0;

/// The calendar year at relative year 0 (dates read "… 10 P.Y.").
pub const ZERO_YEAR: i32 = 10;

/// The origin used when no region data has been loaded: 7/16 of a day
/// (3333.75 ticks), which puts tick 0 at Morntide-and-Half. The live region
/// data carries 3600.
pub const DEFAULT_ORIGIN_OFFSET_TICKS: f64 = (7.0 / 16.0) * DAY_TICKS;

/// The 16 named slots of a day, midnight first. Each is half a Derethian
/// hour; the `…AndHalf` slots are the second halves.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum HourName {
    Darktide = 0,
    DarktideAndHalf,
    Foredawn,
    ForedawnAndHalf,
    /// The first daytime slot.
    Dawnsong,
    DawnsongAndHalf,
    Morntide,
    MorntideAndHalf,
    Midsong,
    MidsongAndHalf,
    Warmtide,
    /// The last daytime slot.
    WarmtideAndHalf,
    Evensong,
    EvensongAndHalf,
    Gloaming,
    GloamingAndHalf,
}

impl HourName {
    /// All slots in order; index = slot number.
    pub const ALL: [HourName; 16] = [
        HourName::Darktide,
        HourName::DarktideAndHalf,
        HourName::Foredawn,
        HourName::ForedawnAndHalf,
        HourName::Dawnsong,
        HourName::DawnsongAndHalf,
        HourName::Morntide,
        HourName::MorntideAndHalf,
        HourName::Midsong,
        HourName::MidsongAndHalf,
        HourName::Warmtide,
        HourName::WarmtideAndHalf,
        HourName::Evensong,
        HourName::EvensongAndHalf,
        HourName::Gloaming,
        HourName::GloamingAndHalf,
    ];

    /// The slot with this number, if it is in 0..16.
    #[must_use]
    pub fn from_slot(slot: i32) -> Option<HourName> {
        usize::try_from(slot)
            .ok()
            .and_then(|index| Self::ALL.get(index))
            .copied()
    }
}

/// The 12 months, in calendar order.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum MonthName {
    Morningthaw = 0,
    Solclaim,
    Seedsow,
    Leafdawning,
    Verdantine,
    Thistledown,
    Harvestgain,
    Leafcull,
    Frostfell,
    Snowreap,
    Coldeve,
    Wintersebb,
}

impl MonthName {
    /// All months in order; index = month number.
    pub const ALL: [MonthName; 12] = [
        MonthName::Morningthaw,
        MonthName::Solclaim,
        MonthName::Seedsow,
        MonthName::Leafdawning,
        MonthName::Verdantine,
        MonthName::Thistledown,
        MonthName::Harvestgain,
        MonthName::Leafcull,
        MonthName::Frostfell,
        MonthName::Snowreap,
        MonthName::Coldeve,
        MonthName::Wintersebb,
    ];

    /// The month with this number, if it is in 0..12.
    #[must_use]
    pub fn from_index(index: i32) -> Option<MonthName> {
        usize::try_from(index)
            .ok()
            .and_then(|index| Self::ALL.get(index))
            .copied()
    }
}

/// A date and time of day.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CalendarDate {
    /// The calendar year, [`ZERO_YEAR`] included.
    pub year: i32,
    pub month: MonthName,
    /// Day of the month, 1-based (1..=30).
    pub day: i32,
    pub hour: HourName,
}

/// Why a calendar input was refused.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CalendarError {
    /// A tick count or origin was NaN or infinite.
    NonFinite,
    /// The origin offset was negative.
    NegativeOrigin,
}

impl fmt::Display for CalendarError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            CalendarError::NonFinite => f.write_str("tick count or origin is not finite"),
            CalendarError::NegativeOrigin => f.write_str("calendar origin offset is negative"),
        }
    }
}

impl std::error::Error for CalendarError {}

/// The calendar of one world: the tick arithmetic plus the world's origin
/// offset. Each world keeps its own, so two sessions with different region
/// data never share calendar state.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DerethCalendar {
    origin_offset_ticks: f64,
}

impl Default for DerethCalendar {
    /// A calendar with [`DEFAULT_ORIGIN_OFFSET_TICKS`].
    fn default() -> Self {
        Self {
            origin_offset_ticks: DEFAULT_ORIGIN_OFFSET_TICKS,
        }
    }
}

impl DerethCalendar {
    /// A calendar anchored at `origin_offset_ticks` (the region's origin).
    ///
    /// # Errors
    /// [`CalendarError::NonFinite`] or [`CalendarError::NegativeOrigin`].
    pub fn new(origin_offset_ticks: f64) -> Result<Self, CalendarError> {
        let mut calendar = Self::default();
        calendar.set_origin_offset(origin_offset_ticks)?;
        Ok(calendar)
    }

    /// The origin offset in ticks.
    #[must_use]
    pub fn origin_offset_ticks(&self) -> f64 {
        self.origin_offset_ticks
    }

    /// Installs the region's origin for this world. On error the old origin
    /// stays.
    ///
    /// # Errors
    /// [`CalendarError::NonFinite`] or [`CalendarError::NegativeOrigin`].
    pub fn set_origin_offset(&mut self, origin_offset_ticks: f64) -> Result<(), CalendarError> {
        if !origin_offset_ticks.is_finite() {
            return Err(CalendarError::NonFinite);
        }
        if origin_offset_ticks < 0.0 {
            return Err(CalendarError::NegativeOrigin);
        }
        self.origin_offset_ticks = origin_offset_ticks;
        Ok(())
    }

    /// How far through the day `ticks` is, in [0, 1): 0 is midnight
    /// (Darktide).
    ///
    /// # Errors
    /// [`CalendarError::NonFinite`] for a NaN or infinite tick count.
    pub fn day_fraction(&self, ticks: f64) -> Result<f64, CalendarError> {
        let shifted = self.shift(ticks)?;
        let rem = shifted - (shifted / DAY_TICKS).floor() * DAY_TICKS;
        Ok(rem / DAY_TICKS)
    }

    /// The named slot `ticks` falls in.
    ///
    /// # Errors
    /// [`CalendarError::NonFinite`] for a NaN or infinite tick count.
    pub fn current_hour(&self, ticks: f64) -> Result<HourName, CalendarError> {
        let slot = (self.day_fraction(ticks)? * f64::from(HOURS_IN_A_DAY)).floor() as i32;
        Ok(hour_from_clamped_slot(slot))
    }

    /// True from Dawnsong through Warmtide-and-Half (slots 4..=11).
    ///
    /// # Errors
    /// [`CalendarError::NonFinite`] for a NaN or infinite tick count.
    pub fn is_daytime(&self, ticks: f64) -> Result<bool, CalendarError> {
        let hour = self.current_hour(ticks)?;
        Ok(hour >= HourName::Dawnsong && hour <= HourName::WarmtideAndHalf)
    }

    /// The full date and time of day for `ticks`.
    ///
    /// # Errors
    /// [`CalendarError::NonFinite`] for a NaN or infinite tick count.
    pub fn to_calendar(&self, ticks: f64) -> Result<CalendarDate, CalendarError> {
        let shifted = self.shift(ticks)?;
        let relative_year = (shifted / YEAR_TICKS) as i32;
        let within_year = shifted - f64::from(relative_year) * YEAR_TICKS;
        let month = ((within_year / MONTH_TICKS) as i32).min(MONTHS_IN_A_YEAR - 1);
        let within_month = within_year - f64::from(month) * MONTH_TICKS;
        let day = ((within_month / DAY_TICKS) as i32 + 1).min(DAYS_IN_A_MONTH);
        Ok(CalendarDate {
            year: relative_year.saturating_add(ZERO_YEAR),
            month: month_from_clamped_index(month),
            day,
            hour: self.current_hour(ticks)?,
        })
    }

    /// Whole years since the calendar's start (relative year).
    ///
    /// # Errors
    /// [`CalendarError::NonFinite`] for a NaN or infinite tick count.
    pub fn year(&self, ticks: f64) -> Result<i32, CalendarError> {
        Ok((self.shift(ticks)? / YEAR_TICKS) as i32)
    }

    /// The calendar year, [`ZERO_YEAR`] included.
    ///
    /// # Errors
    /// [`CalendarError::NonFinite`] for a NaN or infinite tick count.
    pub fn absolute_year(&self, ticks: f64) -> Result<i32, CalendarError> {
        Ok(self.year(ticks)?.saturating_add(ZERO_YEAR))
    }

    /// Day of the year, 0-based (0..=359).
    ///
    /// # Errors
    /// [`CalendarError::NonFinite`] for a NaN or infinite tick count.
    pub fn day_of_year(&self, ticks: f64) -> Result<i32, CalendarError> {
        let shifted = self.shift(ticks)?;
        let year = (shifted / YEAR_TICKS) as i32;
        let within_year = shifted - f64::from(year) * YEAR_TICKS;
        Ok(((within_year / DAY_TICKS) as i32).clamp(0, DAYS_IN_A_MONTH * MONTHS_IN_A_YEAR - 1))
    }

    /// Negative tick counts count as 0; then the origin is added.
    fn shift(&self, ticks: f64) -> Result<f64, CalendarError> {
        if !ticks.is_finite() {
            return Err(CalendarError::NonFinite);
        }
        Ok(ticks.max(0.0) + self.origin_offset_ticks)
    }
}

fn hour_from_clamped_slot(slot: i32) -> HourName {
    let slot = slot.clamp(0, HOURS_IN_A_DAY - 1);
    HourName::from_slot(slot).unwrap_or(HourName::Darktide)
}

/// The shifted tick count is never negative, so the index is never below
/// 0; the lower clamp only keeps the lookup total.
fn month_from_clamped_index(index: i32) -> MonthName {
    let index = index.clamp(0, MONTHS_IN_A_YEAR - 1);
    MonthName::from_index(index).unwrap_or(MonthName::Morningthaw)
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/World/DerethDateTimeTests.cs`
    //! (its static helpers use the default origin, here
    //! `DerethCalendar::default()`), plus edge cases of `DerethCalendar`.

    use super::*;

    fn cal() -> DerethCalendar {
        DerethCalendar::default()
    }

    fn near(a: f64, b: f64) -> bool {
        (a - b).abs() < 0.5e-4
    }

    #[test]
    fn day_fraction_at_tick_0_is_morntide_and_half() {
        assert!(near(cal().day_fraction(0.0).unwrap(), 7.0 / 16.0));
    }

    #[test]
    fn day_fraction_at_half_day_from_tick_0_is_fifteen_sixteenths() {
        assert!(near(
            cal().day_fraction(DAY_TICKS / 2.0).unwrap(),
            15.0 / 16.0
        ));
    }

    #[test]
    fn day_fraction_wraps_after_one_day() {
        assert!(near(cal().day_fraction(DAY_TICKS).unwrap(), 7.0 / 16.0));
    }

    #[test]
    fn current_hour_at_tick_0_is_morntide_and_half() {
        assert_eq!(cal().current_hour(0.0), Ok(HourName::MorntideAndHalf));
    }

    #[test]
    fn current_hour_at_midnight_is_darktide() {
        assert_eq!(
            cal().current_hour(DAY_TICKS * (9.0 / 16.0)),
            Ok(HourName::Darktide)
        );
    }

    #[test]
    fn current_hour_one_slot_on_is_midsong() {
        assert_eq!(
            cal().current_hour(DAY_TICKS * (1.0 / 16.0)),
            Ok(HourName::Midsong)
        );
    }

    #[test]
    fn is_daytime_tick_0_true() {
        assert_eq!(cal().is_daytime(0.0), Ok(true));
    }

    #[test]
    fn is_daytime_darktide_false() {
        assert_eq!(cal().is_daytime(DAY_TICKS * (9.0 / 16.0)), Ok(false));
    }

    #[test]
    fn to_calendar_py10_day1_morningthaw() {
        let date = cal().to_calendar(0.0).unwrap();
        assert_eq!(date.year, ZERO_YEAR);
        assert_eq!(date.month, MonthName::Morningthaw);
        assert_eq!(date.day, 1);
    }

    #[test]
    fn to_calendar_advances_correctly() {
        let date = cal().to_calendar(YEAR_TICKS).unwrap();
        assert_eq!(date.year, ZERO_YEAR + 1);
        assert_eq!(date.month, MonthName::Morningthaw);
        assert_eq!(date.day, 1);

        let date = cal().to_calendar(YEAR_TICKS + MONTH_TICKS).unwrap();
        assert_eq!(date.year, ZERO_YEAR + 1);
        assert_eq!(date.month, MonthName::Solclaim);
    }

    #[test]
    fn to_calendar_tick_at_seedsow_24_year_106_matches_retail_format() {
        let calendar = DerethCalendar::new(3600.0).unwrap();
        let date = calendar.to_calendar(291_408_060.0).unwrap();
        assert_eq!(date.year, ZERO_YEAR + 106);
        assert_eq!(date.month, MonthName::Seedsow);
        assert_eq!(date.day, 24);
    }

    #[test]
    fn derived_lengths() {
        assert_eq!(HOUR_TICKS, 476.25);
        assert_eq!(MONTH_TICKS, 228_600.0);
        assert_eq!(YEAR_TICKS, 2_743_200.0);
        assert_eq!(DEFAULT_ORIGIN_OFFSET_TICKS, 3333.75);
    }

    #[test]
    fn negative_ticks_count_as_zero() {
        assert_eq!(cal().to_calendar(-500.0), cal().to_calendar(0.0));
        assert_eq!(cal().day_of_year(-1.0), Ok(0));
    }

    #[test]
    fn non_finite_input_is_an_error() {
        assert_eq!(cal().day_fraction(f64::NAN), Err(CalendarError::NonFinite));
        assert_eq!(
            cal().to_calendar(f64::INFINITY),
            Err(CalendarError::NonFinite)
        );
        assert_eq!(DerethCalendar::new(f64::NAN), Err(CalendarError::NonFinite));
        assert_eq!(
            DerethCalendar::new(-1.0),
            Err(CalendarError::NegativeOrigin)
        );
        let mut calendar = DerethCalendar::new(3600.0).unwrap();
        assert!(calendar.set_origin_offset(f64::INFINITY).is_err());
        assert_eq!(calendar.origin_offset_ticks(), 3600.0);
    }

    #[test]
    fn year_and_day_of_year() {
        let calendar = DerethCalendar::new(3600.0).unwrap();
        let ticks = 291_408_060.0;
        assert_eq!(calendar.year(ticks), Ok(106));
        assert_eq!(calendar.absolute_year(ticks), Ok(116));
        // Seedsow is month 2, day 24: 2 * 30 + 23.
        assert_eq!(calendar.day_of_year(ticks), Ok(83));
        // The last tick of a year is the year's last day.
        assert_eq!(cal().day_of_year(YEAR_TICKS - 1.0 - 3333.75), Ok(359));
    }

    #[test]
    fn last_day_of_month_stays_in_range() {
        let date = cal().to_calendar(MONTH_TICKS - 1.0 - 3333.75).unwrap();
        assert_eq!(date.month, MonthName::Morningthaw);
        assert_eq!(date.day, 30);
    }
}
