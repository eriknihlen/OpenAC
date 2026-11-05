//! The player's house: the house data the server sends (or the notice that
//! there is none), rent updates, and the house panel's lines.

use ac_core::physics::gid_to_lcoord;
use ac_proto::house::{HouseData, HousePayment};

/// After buying a landscape house, how long before another may be bought,
/// seconds (30 days).
pub const PURCHASE_WAIT_PERIOD_SECONDS: i64 = 0x0027_8D00;

/// The tint of a house panel line.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HouseLineColor {
    /// Ordinary text.
    Normal,
    /// Rent is paid.
    RentPaid,
    /// Rent is due.
    RentNotPaid,
}

/// The house as a host shows it.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct HouseView {
    /// Counts changes.
    pub revision: u64,
    /// A house notice arrived this session.
    pub has_received_notice: bool,
    /// The player owns a house.
    pub owns_house: bool,
    /// The panel's lines.
    pub lines: Vec<(String, HouseLineColor)>,
    /// Where the house stands (its cell), for the map's marker: none for an
    /// apartment or a house without a place.
    pub position_cell: Option<u32>,
}

/// The house owner.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct HouseState {
    has_received_notice: bool,
    owns_house: bool,
    revision: u64,
    data: Option<HouseData>,
    lines: Vec<(String, HouseLineColor)>,
}

/// What the panel's lines read beyond the house data: the player's purchase
/// timestamp, the wall clock and the local time zone.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HouseClock {
    /// The player's `HousePurchaseTimestamp` (0 when never bought).
    pub purchase_timestamp: i32,
    /// Now, Unix seconds.
    pub now_unix_s: i64,
    /// The local offset from UTC at `now_unix_s`, seconds.
    pub utc_offset_seconds: i32,
}

impl HouseState {
    /// The panel's lines.
    #[must_use]
    pub fn lines(&self) -> &[(String, HouseLineColor)] {
        &self.lines
    }

    /// A house notice arrived this session.
    #[must_use]
    pub fn has_received_notice(&self) -> bool {
        self.has_received_notice
    }

    /// Counts changes.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// The house as owned data.
    #[must_use]
    pub fn view(&self) -> HouseView {
        HouseView {
            revision: self.revision,
            has_received_notice: self.has_received_notice,
            owns_house: self.owns_house,
            lines: self.lines.clone(),
            position_cell: self.position_cell(),
        }
    }

    /// Where the house stands (the same rule as [`Self::position_cell`]).
    #[must_use]
    pub fn position(&self) -> Option<&ac_proto::Position> {
        self.data
            .as_ref()
            .filter(|d| d.house_type != 4 && d.position.cell_id != 0)
            .map(|d| &d.position)
    }

    /// Where the house stands, for the map: an apartment (type 4) or a
    /// house without a place has none.
    #[must_use]
    pub fn position_cell(&self) -> Option<u32> {
        self.data
            .as_ref()
            .filter(|d| d.house_type != 4 && d.position.cell_id != 0)
            .map(|d| d.position.cell_id)
    }

    /// The house data.
    pub fn apply_house_data(&mut self, data: &HouseData, clock: HouseClock) {
        self.has_received_notice = true;
        self.owns_house = true;
        self.revision += 1;
        self.data = Some(data.clone());
        self.recompute(clock);
    }

    /// The rent period restarted: every rent item is unpaid again.
    pub fn apply_rent_time(&mut self, rent_time: u32, clock: HouseClock) {
        let Some(data) = &mut self.data else {
            return;
        };
        for payment in &mut data.rent {
            payment.paid = 0;
        }
        data.rent_time = rent_time;
        self.revision += 1;
        self.recompute(clock);
    }

    /// The rent as paid so far.
    pub fn apply_rent_payment(&mut self, rent: &[HousePayment], clock: HouseClock) {
        let Some(data) = &mut self.data else {
            return;
        };
        data.rent = rent.to_vec();
        self.revision += 1;
        self.recompute(clock);
    }

    /// The server's house status (no house, or a failed transaction).
    pub fn apply_house_status(&mut self, clock: HouseClock) {
        self.has_received_notice = true;
        self.owns_house = false;
        self.revision += 1;
        self.data = None;
        self.recompute(clock);
    }

    /// The session ended.
    pub fn reset_session(&mut self) {
        // The count keeps moving, so a window hears the reset.
        *self = Self {
            revision: self.revision + 1,
            ..Self::default()
        };
    }

    fn recompute(&mut self, clock: HouseClock) {
        let mut lines = Vec::new();
        let normal = |text: String| (text, HouseLineColor::Normal);
        let time = |epoch: i64| convert_time(epoch, clock.utc_offset_seconds);
        if let Some(data) = &self.data {
            lines.push(normal(format!(
                "The purchase price for this dwelling is:\n{}",
                compose_payments(&data.buy, false)
            )));
            lines.push(normal(format!(
                "Rent:\n{}",
                compose_payments(&data.rent, true)
            )));
            lines.push(normal(format!(
                "Bought: {}",
                time(i64::from(data.buy_time))
            )));
            let period = rent_period_seconds(data.house_type);
            let paid = data.maintenance_free || is_paid_in_full(&data.rent);
            let rent_time = i64::from(data.rent_time);
            lines.push(normal(format!(
                "This maintenance period ends: {}",
                time(rent_time + period)
            )));
            lines.push(normal(format!(
                "Maintenance is next due: {}",
                time(rent_time + if paid { 2 } else { 1 } * period)
            )));
            if data.house_type != 4
                && let Some((y, x)) = radar_coordinates(data.position.cell_id)
            {
                lines.push(normal(format!("Location: {y}, {x}")));
            }
            lines.push(if paid {
                (
                    "The maintenance has already been paid for this period. You may not prepay next period's maintenance."
                        .to_owned(),
                    HouseLineColor::RentPaid,
                )
            } else {
                (
                    format!(
                        "Warning!  You have not paid your maintenance costs for the last {} day maintenance period.  Please pay these costs by this deadline or you will lose your house, and all your items within it.",
                        period / 86_400
                    ),
                    HouseLineColor::RentNotPaid,
                )
            });
        } else {
            lines.push(normal("You do not currently own a house.".to_owned()));
        }
        let timestamp = i64::from(clock.purchase_timestamp);
        let expired = clock.now_unix_s - timestamp > PURCHASE_WAIT_PERIOD_SECONDS;
        if expired {
            lines.push(normal(if self.owns_house {
                "You may buy another house immediately after you abandon this one.".to_owned()
            } else {
                "You may buy another house immediately.".to_owned()
            }));
        } else {
            lines.push(normal(format!(
                "You may buy another landscape house at {}. This restriction does not apply to apartments.",
                convert_time(
                    timestamp + PURCHASE_WAIT_PERIOD_SECONDS,
                    clock.utc_offset_seconds
                )
            )));
        }
        self.lines = lines;
    }
}

fn is_paid_in_full(payments: &[HousePayment]) -> bool {
    payments.iter().all(|p| p.paid >= p.num)
}

fn compose_payments(payments: &[HousePayment], include_paid: bool) -> String {
    payments
        .iter()
        .map(|p| {
            let quantity = if include_paid {
                format!("{}/{}", p.paid, p.num)
            } else {
                p.num.to_string()
            };
            format!("{quantity} {}", payment_name(p))
        })
        .collect::<Vec<_>>()
        .join(", ")
}

/// A payment line's item name: singular for one, else the plural the
/// server sent, or an English plural.
#[must_use]
pub fn payment_name(p: &HousePayment) -> String {
    if p.num == 1 {
        return p.name.clone();
    }
    if !p.plural_name.is_empty() {
        return p.plural_name.clone();
    }
    if p.name.ends_with('s') || p.name.ends_with('x') {
        format!("{}es", p.name)
    } else {
        format!("{}s", p.name)
    }
}

fn rent_period_seconds(house_type: u32) -> i64 {
    if house_type == 4 {
        7_776_000
    } else {
        2_592_000
    }
}

/// A date as the house panel prints it ("N/A" for none): the C runtime's
/// `%c` in its default locale, `MM/DD/YY HH:MM:SS`, local time.
fn convert_time(epoch_seconds: i64, utc_offset_seconds: i32) -> String {
    if epoch_seconds == 0 {
        return "N/A".to_owned();
    }
    let local = epoch_seconds + i64::from(utc_offset_seconds);
    let (year, month, day) = civil_from_days(local.div_euclid(86_400));
    let second_of_day = local.rem_euclid(86_400);
    format!(
        "{month:02}/{day:02}/{:02} {:02}:{:02}:{:02}",
        year.rem_euclid(100),
        second_of_day / 3600,
        second_of_day / 60 % 60,
        second_of_day % 60
    )
}

/// Days since 1970-01-01 to a proleptic Gregorian (year, month, day).
fn civil_from_days(days: i64) -> (i64, i64, i64) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let day_of_era = z.rem_euclid(146_097);
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_index = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_index + 2) / 5 + 1;
    let month = if month_index < 10 {
        month_index + 3
    } else {
        month_index - 9
    };
    (year_of_era + era * 400 + i64::from(month <= 2), month, day)
}

/// The lines `@hslist` (the available-houses answer) prints: the count,
/// then each house's map coordinates (none for apartments), and a note
/// when there were more than the 400 listed.
#[must_use]
pub fn available_houses_lines(houses: &ac_proto::house::AvailableHouses) -> Vec<String> {
    let kind = match houses.house_type {
        1 => "cottages",
        2 => "villas",
        3 => "mansions",
        4 => "apartments",
        _ => "",
    };
    let mut lines = vec![format!(
        "There are {} {kind} available.",
        houses.total_available
    )];
    if houses.house_type != 4 {
        for &landblock in &houses.locations {
            if let Some((y, x)) = radar_coordinates(landblock) {
                lines.push(format!("     {y}, {x}"));
            }
        }
        if houses.total_available > 0x190 {
            lines.push(
                "There were too many houses to display all the locations. Only the first 400 locations are displayed here."
                    .to_owned(),
            );
        }
    }
    lines
}

/// The text the guest-list reply prints, in green and as one block: a
/// "Guests:" heading, then one guest per line indented by two spaces with
/// " *" after anyone who may also use storage (or "  None" when the list is
/// empty), then the allegiance permissions and the open-house line when
/// their flags are set. Roommates are not printed from this reply. Both
/// `@house guest list` and `@house storage list` ask for it.
#[must_use]
pub fn guest_list_text(access: &ac_proto::house::HouseAccess) -> String {
    let mut text = String::from("Guests:\n");
    if access.guests.is_empty() {
        text.push_str("  None\n");
    } else {
        for guest in &access.guests {
            text.push_str("  ");
            text.push_str(&guest.name);
            if guest.storage {
                text.push_str(" *");
            }
            text.push('\n');
        }
    }
    // The two permission words are written back to back, with nothing
    // between them, when both are set.
    if access.allegiance_guests() || access.allegiance_storage() {
        text.push_str("Allegiance Permissions: ");
        if access.allegiance_guests() {
            text.push_str("guest access");
        }
        if access.allegiance_storage() {
            text.push_str("storage access");
        }
        text.push('\n');
    }
    if access.open_to_everyone() {
        text.push_str("[ open house ]\n");
    }
    text
}

/// A cell's map coordinates as the radar prints them: north-south then
/// east-west, one decimal, with their compass letters (`12.3N`, `45.6E`).
#[must_use]
pub fn radar_coordinates(cell_id: u32) -> Option<(String, String)> {
    let (lx, ly) = gid_to_lcoord(cell_id)?;
    let x = f64::from(lx - 1024) * 0.1 + 0.5;
    let y = f64::from(ly - 1024) * 0.1 + 0.5;
    Some((axis(y, "S", "N"), axis(x, "W", "E")))
}

fn axis(value: f64, negative: &str, positive: &str) -> String {
    let suffix = if value < 0.0 {
        negative
    } else if value > 0.0 {
        positive
    } else {
        ""
    };
    format!("{:.1}{suffix}", value.abs())
}

#[cfg(test)]
#[path = "house_tests.rs"]
mod tests;
