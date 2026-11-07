//! What the game says when the server boots or bans the account: the
//! sentence its disconnected screen shows.

use ac_proto::{AccountBanned, AccountBooted};

use crate::local_time::LocalZone;

/// The reason a boot names when the server gave none.
const DEFAULT_BOOT_REASON: &str = " for Code of Conduct Violations";

/// The sentence for a boot: the server's reason appended as sent.
#[must_use]
pub fn booted_message(boot: &AccountBooted) -> String {
    let reason = boot.reason.as_deref().unwrap_or(DEFAULT_BOOT_REASON);
    format!("You have been booted from Asheron's Call{reason}.")
}

/// The sentence for a ban received at `now_unix_seconds` (UTC). A ban with
/// time left names its end in local time: the moment of receipt rounded up
/// to the whole minute, plus the time left, in the C runtime's `asctime`
/// form without its line break.
#[must_use]
pub fn banned_message(ban: &AccountBanned, now_unix_seconds: i64, zone: &LocalZone) -> String {
    let reason = ban.reason.as_deref().unwrap_or_default();
    if ban.seconds_remaining > 0 {
        let past_minute = now_unix_seconds.rem_euclid(60);
        let rounded = if past_minute == 0 {
            now_unix_seconds
        } else {
            now_unix_seconds + (60 - past_minute)
        };
        let end = rounded + i64::from(ban.seconds_remaining);
        let offset = zone.utc_offset_seconds(end.saturating_mul(1000));
        let until = asctime(end + i64::from(offset));
        format!(
            "You have been banned until {until}{reason}. For ban appeals, please visit \
             support.turbine.com"
        )
    } else {
        format!("You have been banned from Asheron's Call{reason}.")
    }
}

/// `local_seconds` (seconds since 1970-01-01 in local time) as the game's C
/// runtime writes it: `Www Mmm DD HH:MM:SS YYYY`, every number zero-padded.
fn asctime(local_seconds: i64) -> String {
    const DAYS: [&str; 7] = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    const MONTHS: [&str; 12] = [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];
    let days = local_seconds.div_euclid(86_400);
    let second_of_day = local_seconds.rem_euclid(86_400);
    let (year, month, day) = ac_host::status::civil_from_days(days);
    // 1970-01-01 was a Thursday.
    let weekday = DAYS[usize::try_from((days + 4).rem_euclid(7)).unwrap_or(0)];
    let month_name = MONTHS[usize::try_from(month - 1).unwrap_or(0).min(11)];
    format!(
        "{weekday} {month_name} {day:02} {:02}:{:02}:{:02} {year}",
        second_of_day / 3600,
        second_of_day / 60 % 60,
        second_of_day % 60
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn boot(reason: Option<&str>) -> AccountBooted {
        AccountBooted {
            reason: reason.map(str::to_owned),
        }
    }

    fn ban(seconds_remaining: i32, reason: Option<&str>) -> AccountBanned {
        AccountBanned {
            seconds_remaining,
            reason: reason.map(str::to_owned),
        }
    }

    // The game's boot sentence, with its default reason when the server
    // gives none.
    #[test]
    fn a_boot_appends_the_reason_or_the_code_of_conduct() {
        assert_eq!(
            booted_message(&boot(Some(
                " because the password entered for this account was not correct"
            ))),
            "You have been booted from Asheron's Call because the password entered for this \
             account was not correct."
        );
        assert_eq!(
            booted_message(&boot(None)),
            "You have been booted from Asheron's Call for Code of Conduct Violations."
        );
    }

    // The game's ban sentences: with no time left it names no end; with
    // time left the end is the receipt rounded up to the minute plus the
    // seconds, in local time, written as the C runtime's asctime (day of
    // the month zero-padded) without its line break.
    #[test]
    fn a_ban_names_its_end_in_local_time() {
        let utc = LocalZone::fixed(0);
        assert_eq!(
            banned_message(&ban(0, Some(" - cheating")), 1_000, &utc),
            "You have been banned from Asheron's Call - cheating."
        );
        assert_eq!(
            banned_message(&ban(-5, None), 1_000, &utc),
            "You have been banned from Asheron's Call."
        );
        // 2026-11-07 09:30:20 UTC rounds up to 09:31:00; one hour more.
        let now = 1_794_043_820;
        assert_eq!(asctime(now), "Sat Nov 07 09:30:20 2026");
        assert_eq!(
            banned_message(&ban(3_600, Some(" - cheating")), now, &utc),
            "You have been banned until Sat Nov 07 10:31:00 2026 - cheating. For ban appeals, \
             please visit support.turbine.com"
        );
        // On the minute it stays; the zone's offset applies.
        let on_minute = now - 20;
        assert_eq!(
            banned_message(&ban(59, None), on_minute, &LocalZone::fixed(-5 * 3600)),
            "You have been banned until Sat Nov 07 04:30:59 2026. For ban appeals, please visit \
             support.turbine.com"
        );
    }

    #[test]
    fn asctime_pads_every_number() {
        assert_eq!(asctime(0), "Thu Jan 01 00:00:00 1970");
        assert_eq!(asctime(315_626_635), "Wed Jan 02 02:03:55 1980");
        assert_eq!(asctime(951_782_400), "Tue Feb 29 00:00:00 2000");
    }
}
