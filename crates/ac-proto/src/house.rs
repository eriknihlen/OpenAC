//! Housing: the house the player owns or rents, its rent, its access list,
//! the houses for sale, and the guest and storage commands.
//!
//! Game-action builders take the session's next action sequence number
//! and are sent on the UI queue. The house-status query itself is
//! [`crate::build_house_query`].

use ac_bits::Reader;

use crate::client_commands::string_u32_action;
use crate::wire::{bare_action, read_bool32, read_count, read_string, string_action, u32_action};
use crate::{GameActionType, HouseRestrictions, Position, Result};

/// One item of a purchase or rent price.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HousePayment {
    /// How many are due.
    pub num: i32,
    /// How many are paid.
    pub paid: i32,
    /// The item's weenie class.
    pub weenie_id: u32,
    /// Its name.
    pub name: String,
    /// Its plural name.
    pub plural_name: String,
}

fn read_payments(r: &mut Reader<'_>) -> Result<Vec<HousePayment>> {
    // Each entry takes at least three words and two string lengths.
    let count = read_count(r, 16, "house payments")?;
    (0..count)
        .map(|_| {
            Ok(HousePayment {
                num: r.i32()?,
                paid: r.i32()?,
                weenie_id: r.u32()?,
                name: read_string(r)?,
                plural_name: read_string(r)?,
            })
        })
        .collect()
}

/// The player's house ([`crate::GameEventType::HOUSE_DATA`]).
#[derive(Debug, Clone, PartialEq)]
pub struct HouseData {
    /// When it was bought, server time in seconds.
    pub buy_time: u32,
    /// When rent was last paid.
    pub rent_time: u32,
    /// House type (1 cottage, 2 villa, 3 mansion, 4 apartment; 0 none).
    pub house_type: u32,
    /// No upkeep is due.
    pub maintenance_free: bool,
    /// The purchase price.
    pub buy: Vec<HousePayment>,
    /// The rent.
    pub rent: Vec<HousePayment>,
    /// Where the house is.
    pub position: Position,
}

impl HouseData {
    /// Parse the event payload: buy time, rent time, type, upkeep-free
    /// word, the buy list, the rent list, the position.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            buy_time: r.u32()?,
            rent_time: r.u32()?,
            house_type: r.u32()?,
            maintenance_free: read_bool32(&mut r)?,
            buy: read_payments(&mut r)?,
            rent: read_payments(&mut r)?,
            position: Position::read(&mut r)?,
        })
    }
}

/// What a slumlord asks for to buy its house or pay its maintenance
/// ([`crate::GameEventType::HOUSE_PROFILE`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HouseProfile {
    /// The slumlord.
    pub slumlord_guid: u32,
    /// The house's dwelling id.
    pub dwelling_id: u32,
    /// The owner (0: nobody owns it).
    pub owner_guid: u32,
    /// The profile's flags.
    pub bitmask: u32,
    /// The lowest level that may buy it (-1: none).
    pub min_level: i32,
    /// The highest level that may buy it (-1: none).
    pub max_level: i32,
    /// The lowest allegiance rank that may buy it (-1: none).
    pub min_allegiance_rank: i32,
    /// The highest allegiance rank that may buy it (-1: none).
    pub max_allegiance_rank: i32,
    /// No maintenance is due.
    pub maintenance_free: bool,
    /// House type (1 cottage, 2 villa, 3 mansion, 4 apartment).
    pub house_type: u32,
    /// The owner's name (empty when nobody owns it).
    pub owner_name: String,
    /// The purchase price.
    pub buy: Vec<HousePayment>,
    /// The maintenance.
    pub rent: Vec<HousePayment>,
}

impl HouseProfile {
    /// Parse the event payload: slumlord, dwelling id, owner, flags, the
    /// level and allegiance-rank limits, the maintenance-free word, the
    /// type, the owner's name, the buy list and the rent list.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            slumlord_guid: r.u32()?,
            dwelling_id: r.u32()?,
            owner_guid: r.u32()?,
            bitmask: r.u32()?,
            min_level: r.i32()?,
            max_level: r.i32()?,
            min_allegiance_rank: r.i32()?,
            max_allegiance_rank: r.i32()?,
            maintenance_free: read_bool32(&mut r)?,
            house_type: r.u32()?,
            owner_name: read_string(&mut r)?,
            buy: read_payments(&mut r)?,
            rent: read_payments(&mut r)?,
        })
    }
}

/// Buy the house at `slumlord` with the `items` offered: the slumlord,
/// then the count and each item.
#[must_use]
pub fn build_buy_house(sequence: u32, slumlord: u32, items: &[u32]) -> Vec<u8> {
    guid_list_action(sequence, GameActionType::BUY_HOUSE, slumlord, items)
}

/// Pay the maintenance of the house at `slumlord` with the `items`
/// offered: the same body as [`build_buy_house`].
#[must_use]
pub fn build_rent_house(sequence: u32, slumlord: u32, items: &[u32]) -> Vec<u8> {
    guid_list_action(sequence, GameActionType::RENT_HOUSE, slumlord, items)
}

fn guid_list_action(sequence: u32, action: GameActionType, target: u32, guids: &[u32]) -> Vec<u8> {
    let mut w = crate::wire::action(sequence, action);
    w.u32(target);
    w.u32(u32::try_from(guids.len()).unwrap_or(u32::MAX));
    for &guid in guids {
        w.u32(guid);
    }
    w.into_vec()
}

/// The rent now due ([`crate::GameEventType::UPDATE_RENT_PAYMENT`]).
pub fn parse_rent_payment(payload: &[u8]) -> Result<Vec<HousePayment>> {
    read_payments(&mut Reader::new(payload))
}

/// The single word of a house-status event
/// ([`crate::GameEventType::HOUSE_STATUS`], an error code) or a rent-time
/// event ([`crate::GameEventType::UPDATE_RENT_TIME`], server time).
pub fn parse_house_word(payload: &[u8]) -> Result<u32> {
    Ok(Reader::new(payload).u32()?)
}

/// A house's access list changed
/// ([`crate::GameEventType::HOUSE_UPDATE_RESTRICTIONS`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HouseUpdateRestrictions {
    /// Update sequence.
    pub sequence: u8,
    /// The house object.
    pub sender_id: u32,
    /// The access list.
    pub restrictions: HouseRestrictions,
}

impl HouseUpdateRestrictions {
    /// Parse the event payload: a sequence byte, the house, then the access
    /// list as in an object description.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            sequence: r.u8()?,
            sender_id: r.u32()?,
            restrictions: HouseRestrictions::read(&mut r)?,
        })
    }
}

/// One permanent guest in a house access record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HouseGuest {
    /// The guest's character.
    pub guid: u32,
    /// The guest's name.
    pub name: String,
    /// The guest may also use the house's storage.
    pub storage: bool,
}

/// The owner's house access record, i.e. the guest list
/// ([`crate::GameEventType::UPDATE_HAR`]). Roommates are the owner's other
/// characters on the same account.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HouseAccess {
    /// Bit 0x1 open house, 0x2 allegiance guests, 0x4 allegiance storage.
    pub bitmask: u32,
    /// The allegiance's monarch.
    pub monarch_id: u32,
    /// The guests, in wire order.
    pub guests: Vec<HouseGuest>,
    /// The roommates.
    pub roommates: Vec<u32>,
}

impl HouseAccess {
    /// The house is open to everyone.
    #[must_use]
    pub fn open_to_everyone(&self) -> bool {
        self.bitmask & 0x1 != 0
    }

    /// The owner's allegiance may enter.
    #[must_use]
    pub fn allegiance_guests(&self) -> bool {
        self.bitmask & 0x2 != 0
    }

    /// The owner's allegiance may use the storage.
    #[must_use]
    pub fn allegiance_storage(&self) -> bool {
        self.bitmask & 0x4 != 0
    }

    /// Parse the event payload: version, bitmask, monarch, guest count and
    /// bucket count (two u16), each guest (id, storage word, name), then the
    /// roommate count and ids.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        r.u32()?; // version
        let bitmask = r.u32()?;
        let monarch_id = r.u32()?;
        let guest_count = r.u16()?;
        r.u16()?; // bucket count
        let guests = (0..guest_count)
            .map(|_| {
                let guid = r.u32()?;
                let storage = read_bool32(&mut r)?;
                Ok(HouseGuest {
                    guid,
                    name: read_string(&mut r)?,
                    storage,
                })
            })
            .collect::<Result<_>>()?;
        let roommate_count = read_count(&mut r, 4, "house roommates")?;
        let roommates = (0..roommate_count)
            .map(|_| r.u32())
            .collect::<ac_bits::Result<_>>()?;
        Ok(Self {
            bitmask,
            monarch_id,
            guests,
            roommates,
        })
    }
}

/// Houses for sale ([`crate::GameEventType::AVAILABLE_HOUSES`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AvailableHouses {
    /// The house type asked about.
    pub house_type: u32,
    /// Landblock ids of the first houses for sale.
    pub locations: Vec<u32>,
    /// How many are for sale in all.
    pub total_available: i32,
}

impl AvailableHouses {
    /// Parse the event payload: type, location count and ids, total.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let house_type = r.u32()?;
        let count = read_count(&mut r, 4, "house locations")?;
        let locations = (0..count)
            .map(|_| r.u32())
            .collect::<ac_bits::Result<_>>()?;
        Ok(Self {
            house_type,
            locations,
            total_available: r.i32()?,
        })
    }
}

/// List the houses of one type for sale.
#[must_use]
pub fn build_list_available_houses(sequence: u32, house_type: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::LIST_AVAILABLE_HOUSES, house_type)
}

/// Give a player access to the house.
pub fn build_add_player_permission(sequence: u32, player_name: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::ADD_PLAYER_PERMISSION, player_name)
}

/// Take a player's access away.
pub fn build_remove_player_permission(sequence: u32, player_name: &str) -> Result<Vec<u8>> {
    string_action(
        sequence,
        GameActionType::REMOVE_PLAYER_PERMISSION,
        player_name,
    )
}

/// Abandon the house.
#[must_use]
pub fn build_abandon_house(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::ABANDON_HOUSE)
}

/// Open the house to the public or close it.
#[must_use]
pub fn build_set_open_house_status(sequence: u32, open: bool) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::SET_OPEN_HOUSE_STATUS,
        u32::from(open),
    )
}

/// Add a permanent guest.
pub fn build_add_permanent_guest(sequence: u32, player_name: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::ADD_PERMANENT_GUEST, player_name)
}

/// Remove a permanent guest.
pub fn build_remove_permanent_guest(sequence: u32, player_name: &str) -> Result<Vec<u8>> {
    string_action(
        sequence,
        GameActionType::REMOVE_PERMANENT_GUEST,
        player_name,
    )
}

/// Remove every permanent guest.
#[must_use]
pub fn build_remove_all_permanent_guests(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::REMOVE_ALL_PERMANENT_GUESTS)
}

/// Give (`true`) or take a guest's storage access: name, then 1 or 0.
pub fn build_change_storage_permission(
    sequence: u32,
    player_name: &str,
    enabled: bool,
) -> Result<Vec<u8>> {
    string_u32_action(
        sequence,
        GameActionType::CHANGE_STORAGE_PERMISSION,
        player_name,
        u32::from(enabled),
    )
}

/// Give every guest storage access.
#[must_use]
pub fn build_add_all_storage_permission(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::ADD_ALL_STORAGE_PERMISSION)
}

/// Take storage access from everyone.
#[must_use]
pub fn build_remove_all_storage_permission(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::REMOVE_ALL_STORAGE_PERMISSION)
}

/// Ask for the full guest list.
#[must_use]
pub fn build_request_full_guest_list(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::REQUEST_FULL_GUEST_LIST)
}

/// Boot one guest.
pub fn build_boot_specific_house_guest(sequence: u32, player_name: &str) -> Result<Vec<u8>> {
    string_action(
        sequence,
        GameActionType::BOOT_SPECIFIC_HOUSE_GUEST,
        player_name,
    )
}

/// Boot everyone.
#[must_use]
pub fn build_boot_everyone(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::BOOT_EVERYONE)
}

/// Show or hide the house hooks.
#[must_use]
pub fn build_set_hooks_visibility(sequence: u32, visible: bool) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::SET_HOOKS_VISIBILITY,
        u32::from(visible),
    )
}

/// Give or take the allegiance's guest access.
#[must_use]
pub fn build_modify_allegiance_guest_permission(sequence: u32, enabled: bool) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::MODIFY_ALLEGIANCE_GUEST_PERMISSION,
        u32::from(enabled),
    )
}

/// Give or take the allegiance's storage access.
#[must_use]
pub fn build_modify_allegiance_storage_permission(sequence: u32, enabled: bool) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::MODIFY_ALLEGIANCE_STORAGE_PERMISSION,
        u32::from(enabled),
    )
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `HouseEventsTests.cs`, the available-houses cases of
    //! `ClientCommandResponsesTests.cs`, the house cases of
    //! `ClientCommandRequestsTests.cs`, and the restriction layout of
    //! OpenAC `GameEvents.ParseHouseUpdateRestrictions`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{BareBuilder, assert_every_prefix_fails, words};

    fn payment(w: &mut Writer, num: i32, paid: i32, weenie: u32, name: &str, plural: &str) {
        w.i32(num);
        w.i32(paid);
        w.u32(weenie);
        w.string16l(name).unwrap();
        w.string16l(plural).unwrap();
    }

    #[test]
    fn rent_payment_lists() {
        assert_eq!(parse_rent_payment(&words(&[0])), Ok(vec![]));
        let mut w = Writer::new();
        w.u32(1);
        payment(&mut w, 400, 150, 273, "Pyreal", "Pyreals");
        assert_eq!(
            parse_rent_payment(w.as_slice()),
            Ok(vec![HousePayment {
                num: 400,
                paid: 150,
                weenie_id: 273,
                name: "Pyreal".into(),
                plural_name: "Pyreals".into()
            }])
        );
        assert!(parse_rent_payment(&words(&[1000])).is_err());
    }

    #[test]
    fn house_words() {
        assert_eq!(parse_house_word(&words(&[0])), Ok(0));
        assert_eq!(
            parse_house_word(&words(&[1_700_000_000])),
            Ok(1_700_000_000)
        );
        assert!(parse_house_word(&[]).is_err());
    }

    fn house_position(w: &mut Writer, cell: u32, values: [f32; 7]) {
        w.u32(cell);
        for v in values {
            w.f32(v);
        }
    }

    #[test]
    fn house_data_without_a_house() {
        let mut w = Writer::new();
        for word in [0, 0, 0, 0, 0, 0] {
            w.u32(word);
        }
        house_position(&mut w, 0x0012_0001, [10.0, 20.0, 30.0, 1.0, 0.0, 0.0, 0.0]);
        let data = HouseData::parse(w.as_slice()).unwrap();
        assert_eq!(data.buy_time, 0);
        assert!(data.buy.is_empty() && data.rent.is_empty());
        assert_eq!(data.position.cell_id, 0x0012_0001);
        assert_eq!(data.position.origin.x, 10.0);
        assert_eq!(data.position.origin.z, 30.0);
        assert_eq!(data.position.orientation.w, 1.0);
        assert_every_prefix_fails(w.as_slice(), HouseData::parse);
    }

    #[test]
    fn house_data_with_buy_and_rent() {
        let mut w = Writer::new();
        for word in [1_650_000_000, 1_699_000_000, 1, 0] {
            w.u32(word);
        }
        w.u32(1);
        payment(&mut w, 1, 1, 273, "Pyreal", "Pyreals");
        w.u32(2);
        payment(&mut w, 300, 300, 273, "Pyreal", "Pyreals");
        payment(
            &mut w,
            1,
            0,
            1049,
            "Writ of the Chosen",
            "Writs of the Chosen",
        );
        house_position(&mut w, 0x0034_0002, [-15.0, 45.0, 0.0, 0.5, 0.0, 0.0, 0.5]);
        let data = HouseData::parse(w.as_slice()).unwrap();
        assert_eq!(data.buy_time, 1_650_000_000);
        assert_eq!(data.rent_time, 1_699_000_000);
        assert_eq!(data.house_type, 1);
        assert!(!data.maintenance_free);
        assert_eq!(data.buy.len(), 1);
        assert_eq!(data.rent.len(), 2);
        assert_eq!(data.rent[1].name, "Writ of the Chosen");
        assert_eq!(data.position.cell_id, 0x0034_0002);
        assert!(HouseData::parse(&words(&[0, 0])).is_err());
    }

    #[test]
    fn house_restrictions_update() {
        let mut w = Writer::new();
        w.u8(4);
        w.u32(0x7000_0001);
        w.u32(2);
        w.u32(1);
        w.u32(0x5000_0009);
        w.u32(1);
        w.u32(0x5000_0002);
        w.u32(1);
        let update = HouseUpdateRestrictions::parse(w.as_slice()).unwrap();
        assert_eq!(update.sequence, 4);
        assert_eq!(update.sender_id, 0x7000_0001);
        assert!(update.restrictions.open_to_public());
        assert_eq!(update.restrictions.monarch_id, 0x5000_0009);
        assert_eq!(update.restrictions.guests.len(), 1);
        assert_eq!(update.restrictions.guests[0].guest_id, 0x5000_0002);
        assert_every_prefix_fails(w.as_slice(), HouseUpdateRestrictions::parse);
    }

    // OpenAC HouseEventsTests.ParseUpdateHar_ReadsFlagsGuestsAndRoommates.
    #[test]
    fn update_har_reads_flags_guests_and_roommates() {
        let mut w = Writer::new();
        w.u32(0x1000_0002); // version
        w.u32(0x5); // open house + allegiance storage
        w.u32(0x5000_0001); // monarch
        w.u16(2);
        w.u16(64);
        w.u32(0x5000_0002);
        w.u32(0);
        w.string16l("Alice").unwrap();
        w.u32(0x5000_0003);
        w.u32(1);
        w.string16l("Bob").unwrap();
        w.u32(1);
        w.u32(0x5000_0004);
        let har = HouseAccess::parse(w.as_slice()).unwrap();
        assert!(har.open_to_everyone());
        assert!(!har.allegiance_guests());
        assert!(har.allegiance_storage());
        assert_eq!(har.monarch_id, 0x5000_0001);
        assert_eq!(
            har.guests,
            [
                HouseGuest {
                    guid: 0x5000_0002,
                    name: "Alice".into(),
                    storage: false
                },
                HouseGuest {
                    guid: 0x5000_0003,
                    name: "Bob".into(),
                    storage: true
                },
            ]
        );
        assert_eq!(har.roommates, [0x5000_0004]);
        assert_every_prefix_fails(w.as_slice(), HouseAccess::parse);
    }

    // OpenAC HouseEventsTests.ParseUpdateHar_EmptyPrivateHouse_RoundTrips and
    // ParseUpdateHar_TruncatedGuest_ReturnsNull.
    #[test]
    fn update_har_empty_house_and_truncated_guest() {
        let mut w = Writer::new();
        w.u32(0x1000_0002);
        w.u32(0);
        w.u32(0);
        w.u16(0);
        w.u16(64);
        w.u32(0);
        let har = HouseAccess::parse(w.as_slice()).unwrap();
        assert!(!har.open_to_everyone());
        assert!(har.guests.is_empty());
        assert!(har.roommates.is_empty());

        let mut w = Writer::new();
        w.u32(0x1000_0002);
        w.u32(0);
        w.u32(0);
        w.u16(1);
        w.u16(64);
        w.u32(0x5000_0002);
        assert!(HouseAccess::parse(w.as_slice()).is_err());
    }

    #[allow(clippy::too_many_arguments)]
    fn profile_header(
        w: &mut Writer,
        owner: u32,
        min_level: i32,
        max_level: i32,
        min_rank: i32,
        max_rank: i32,
        maintenance_free: bool,
        house_type: u32,
        owner_name: &str,
    ) {
        w.u32(0x7000_0100);
        w.u32(0x1234_0001); // dwelling id
        w.u32(owner);
        w.u32(1); // bitmask: active
        w.i32(min_level);
        w.i32(max_level);
        w.i32(min_rank);
        w.i32(max_rank);
        w.u32(u32::from(maintenance_free));
        w.u32(house_type);
        w.string16l(owner_name).unwrap();
    }

    // HouseEventsTests.ParseHouseProfile_Unowned_EmptyLists
    #[test]
    fn house_profile_unowned_with_empty_lists() {
        let mut w = Writer::new();
        profile_header(&mut w, 0, -1, -1, -1, -1, false, 1, "");
        w.u32(0);
        w.u32(0);
        let p = HouseProfile::parse(w.as_slice()).unwrap();
        assert_eq!(p.slumlord_guid, 0x7000_0100);
        assert_eq!(p.dwelling_id, 0x1234_0001);
        assert_eq!(p.owner_guid, 0);
        assert_eq!(p.bitmask, 1);
        assert_eq!(p.min_level, -1);
        assert_eq!(p.max_allegiance_rank, -1);
        assert!(!p.maintenance_free);
        assert_eq!(p.house_type, 1);
        assert_eq!(p.owner_name, "");
        assert!(p.buy.is_empty() && p.rent.is_empty());
    }

    // HouseEventsTests.ParseHouseProfile_Owned_ReadsHeaderAndBothLists
    #[test]
    fn house_profile_owned_reads_the_header_and_both_lists() {
        let mut w = Writer::new();
        profile_header(&mut w, 0x5000_0042, 20, 99, 6, 10, true, 3, "Sammy");
        w.u32(2);
        payment(&mut w, 50_000, 50_000, 273, "Pyreal", "Pyreals");
        payment(&mut w, 1, 1, 9000, "Bone Totem", "");
        w.u32(1);
        payment(&mut w, 5_000, 1_250, 273, "Pyreal", "Pyreals");
        let p = HouseProfile::parse(w.as_slice()).unwrap();
        assert_eq!(p.owner_guid, 0x5000_0042);
        assert_eq!(p.min_level, 20);
        assert_eq!(p.max_level, 99);
        assert_eq!(p.min_allegiance_rank, 6);
        assert_eq!(p.max_allegiance_rank, 10);
        assert!(p.maintenance_free);
        assert_eq!(p.house_type, 3);
        assert_eq!(p.owner_name, "Sammy");
        assert_eq!(p.buy.len(), 2);
        assert_eq!(
            p.buy[1],
            HousePayment {
                num: 1,
                paid: 1,
                weenie_id: 9000,
                name: "Bone Totem".into(),
                plural_name: String::new(),
            }
        );
        assert_eq!(p.rent.len(), 1);
        assert_eq!((p.rent[0].num, p.rent[0].paid), (5_000, 1_250));
        // The last string's alignment padding is not required (the reader
        // stops at the end of the body); every shorter prefix fails.
        let body = w.as_slice();
        assert_every_prefix_fails(&body[..body.len() - 3], HouseProfile::parse);
    }

    // HouseEventsTests.ParseHouseProfile_TruncatedHeader_ReturnsNull and
    // ParseHouseProfile_TruncatedPayment_ReturnsNull
    #[test]
    fn a_truncated_house_profile_is_refused() {
        assert!(HouseProfile::parse(&words(&[0x7000_0100, 1])).is_err());
        let mut w = Writer::new();
        profile_header(&mut w, 0, -1, -1, -1, -1, false, 1, "");
        w.u32(1);
        w.i32(50_000);
        w.i32(0); // the weenie class and names are missing
        assert!(HouseProfile::parse(w.as_slice()).is_err());
    }

    // HouseEventsTests.BuildBuyHouse_WritesSlumlordCountAndGuids and
    // BuildRentHouse_EmptyList_WritesZeroCount
    #[test]
    fn buy_and_rent_name_the_slumlord_and_the_items_offered() {
        assert_eq!(
            build_buy_house(7, 0x7000_0100, &[0x6000_0001, 0x6000_0002]),
            words(&[0xF7B1, 7, 0x021C, 0x7000_0100, 2, 0x6000_0001, 0x6000_0002])
        );
        assert_eq!(
            build_rent_house(3, 0x7000_0100, &[]),
            words(&[0xF7B1, 3, 0x0221, 0x7000_0100, 0])
        );
    }

    #[test]
    fn available_houses() {
        let wire = words(&[2, 1, 0x0A0A_0001, 5]);
        assert_eq!(
            AvailableHouses::parse(&wire),
            Ok(AvailableHouses {
                house_type: 2,
                locations: vec![0x0A0A_0001],
                total_available: 5
            })
        );
        assert_every_prefix_fails(&wire, AvailableHouses::parse);
    }

    #[test]
    fn house_requests() {
        for (body, action) in [
            (build_set_open_house_status(13, true), 0x0247),
            (build_set_hooks_visibility(13, true), 0x0266),
            (build_modify_allegiance_guest_permission(13, true), 0x0267),
            (build_modify_allegiance_storage_permission(13, true), 0x0268),
        ] {
            assert_eq!(body, words(&[0xF7B1, 13, action, 1]));
        }
        assert_eq!(
            build_list_available_houses(1, 2),
            words(&[0xF7B1, 1, 0x0270, 2])
        );
        let bare: [(BareBuilder, u32); 6] = [
            (build_abandon_house, 0x021F),
            (build_remove_all_permanent_guests, 0x025E),
            (build_add_all_storage_permission, 0x025C),
            (build_remove_all_storage_permission, 0x024C),
            (build_request_full_guest_list, 0x024D),
            (build_boot_everyone, 0x025F),
        ];
        for (build, action) in bare {
            assert_eq!(build(0x1234), words(&[0xF7B1, 0x1234, action]));
        }
        const BJORN: [u8; 8] = [5, 0, 0x42, 0x6A, 0xF8, 0x72, 0x6E, 0];
        for (body, action) in [
            (build_add_permanent_guest(11, "Bjørn").unwrap(), 0x0245),
            (build_remove_permanent_guest(11, "Bjørn").unwrap(), 0x0246),
            (
                build_boot_specific_house_guest(11, "Bjørn").unwrap(),
                0x024A,
            ),
            (build_add_player_permission(11, "Bjørn").unwrap(), 0x0219),
            (build_remove_player_permission(11, "Bjørn").unwrap(), 0x021A),
        ] {
            assert_eq!(body[..12], words(&[0xF7B1, 11, action]));
            assert_eq!(body[12..], BJORN);
        }
        let storage = build_change_storage_permission(16, "Bjørn", true).unwrap();
        assert_eq!(storage.len(), 24);
        assert_eq!(storage[8..12], 0x0249u32.to_le_bytes());
        assert_eq!(storage[12..20], BJORN);
        assert_eq!(storage[20..], 1u32.to_le_bytes());
    }
}
