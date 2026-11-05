//! Vendors: the shop the server opens when the player uses a vendor, and
//! the buy and sell requests.
//!
//! Game-action builders take the session's next action sequence number
//! and are sent on the UI queue.

use ac_bits::Reader;

use crate::wire::{action, read_string};
use crate::{GameActionType, ProtoError, PublicWeenieDesc, Result};

/// Largest item list the parser accepts.
pub const MAX_ITEMS: u32 = 8192;

/// What a vendor trades in and at what rates.
#[derive(Debug, Clone, PartialEq)]
pub struct VendorProfile {
    /// Item-type bits the vendor buys.
    pub merchandise_item_types: u32,
    /// Lowest value it buys.
    pub merchandise_min_value: u32,
    /// Highest value it buys.
    pub merchandise_max_value: u32,
    /// It deals in magical items.
    pub deal_magical_items: bool,
    /// Rate it pays when buying from the player.
    pub buy_price: f32,
    /// Rate it charges when selling.
    pub sell_price: f32,
    /// Weenie class of its alternate currency, 0 for pyreals.
    pub alternate_currency_wcid: u32,
    /// How much of the alternate currency the player holds.
    pub alternate_currency_amount: u32,
    /// The currency's plural name.
    pub alternate_currency_plural_name: String,
}

/// One item for sale.
#[derive(Debug, Clone, PartialEq)]
pub struct VendorItem {
    /// How many are stocked; -1 for unlimited.
    pub stack_size: i32,
    /// The item.
    pub item_guid: u32,
    /// Its description.
    pub desc: PublicWeenieDesc,
}

/// A vendor's shop ([`crate::GameEventType::APPROACH_VENDOR`]).
#[derive(Debug, Clone, PartialEq)]
pub struct ApproachVendor {
    /// The vendor.
    pub vendor_guid: u32,
    /// What it trades in.
    pub profile: VendorProfile,
    /// The items for sale.
    pub items: Vec<VendorItem>,
}

impl ApproachVendor {
    /// Parse the event payload: vendor, the profile, the item count, then
    /// per item a word whose low 24 bits are the signed stock count, the
    /// item id and its public description padded to 4 bytes. The payload
    /// must start 4-aligned in the message, as game-event payloads do.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let vendor_guid = r.u32()?;
        let profile = VendorProfile {
            merchandise_item_types: r.u32()?,
            merchandise_min_value: r.u32()?,
            merchandise_max_value: r.u32()?,
            deal_magical_items: r.u32()? != 0,
            buy_price: r.f32()?,
            sell_price: r.f32()?,
            alternate_currency_wcid: r.u32()?,
            alternate_currency_amount: r.u32()?,
            alternate_currency_plural_name: read_string(&mut r)?,
        };
        let count = r.u32()?;
        if count > MAX_ITEMS {
            return Err(ProtoError::CountTooLarge {
                what: "vendor items",
                count,
                max: MAX_ITEMS,
            });
        }
        crate::wire::check_count(&r, count, 12, "vendor items")?;
        let mut items = Vec::with_capacity(count as usize);
        for _ in 0..count {
            let packed = r.u32()?;
            let stack_size = ((packed << 8) as i32) >> 8;
            let item_guid = r.u32()?;
            let desc = PublicWeenieDesc::read(&mut r)?;
            let pad = (4 - (r.position() & 3)) & 3;
            r.skip(pad.min(r.remaining()))?;
            items.push(VendorItem {
                stack_size,
                item_guid,
                desc,
            });
        }
        Ok(Self {
            vendor_guid,
            profile,
            items,
        })
    }
}

/// An amount of one item, for a buy or sell list.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ItemAmount {
    /// How many.
    pub amount: i32,
    /// The item.
    pub item_guid: u32,
}

/// Buy items: vendor, count, amount and item per entry, then the
/// vendor's alternate currency (0 for pyreals).
#[must_use]
pub fn build_buy(
    sequence: u32,
    vendor_guid: u32,
    items: &[ItemAmount],
    alternate_currency_id: u32,
) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::BUY);
    w.u32(vendor_guid);
    write_items(&mut w, items);
    w.u32(alternate_currency_id);
    w.into_vec()
}

/// Sell items: vendor, count, amount and item per entry.
#[must_use]
pub fn build_sell(sequence: u32, vendor_guid: u32, items: &[ItemAmount]) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::SELL);
    w.u32(vendor_guid);
    write_items(&mut w, items);
    w.into_vec()
}

fn write_items(w: &mut ac_bits::Writer, items: &[ItemAmount]) {
    w.u32(items.len() as u32);
    for item in items {
        w.i32(item.amount);
        w.u32(item.item_guid);
    }
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `VendorApproachTests.cs` and `VendorRequestsTests.cs`, and the body
    //! shapes of `WorldSessionVendorTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    const ITEM_TYPE_WRITABLE: u32 = 0x2000;
    const ITEM_TYPE_KEY: u32 = 0x4000;

    fn profile(w: &mut Writer, vendor: u32, currency_name: &str) {
        w.u32(vendor);
        w.u32(0x42);
        w.u32(10);
        w.u32(99999);
        w.u32(1);
        w.f32(0.75);
        w.f32(1.25);
        w.u32(0x3400_0001);
        w.u32(57);
        w.string16l(currency_name).unwrap();
    }

    fn item(w: &mut Writer, stack: i32, guid: u32, flags: u32, name: &str, ammo: Option<u16>) {
        w.u32((stack as u32 & 0x00FF_FFFF) | 0xFF00_0000);
        w.u32(guid);
        w.u32(flags);
        w.string16l(name).unwrap();
        w.packed_dword(7).unwrap();
        w.packed_dword(8).unwrap();
        w.u32(if ammo.is_some() {
            ITEM_TYPE_WRITABLE
        } else {
            ITEM_TYPE_KEY
        });
        w.u32(0);
        w.align(4);
        if let Some(ammo) = ammo {
            w.u16(ammo);
        }
        w.align(4);
    }

    #[test]
    fn multi_item_vendor() {
        let mut w = Writer::new();
        profile(&mut w, 0x4000_0123, "Trade Notes");
        w.u32(2);
        item(&mut w, 3, 0x5000_1001, 0x100, "Dusty Tome", Some(42));
        item(&mut w, -1, 0x5000_1002, 0, "Iron Key", None);
        let parsed = ApproachVendor::parse(w.as_slice()).unwrap();
        assert_eq!(parsed.vendor_guid, 0x4000_0123);
        let p = &parsed.profile;
        assert_eq!(p.merchandise_item_types, 0x42);
        assert_eq!(
            (p.merchandise_min_value, p.merchandise_max_value),
            (10, 99999)
        );
        assert!(p.deal_magical_items);
        assert_eq!((p.buy_price, p.sell_price), (0.75, 1.25));
        assert_eq!(p.alternate_currency_wcid, 0x3400_0001);
        assert_eq!(p.alternate_currency_amount, 57);
        assert_eq!(p.alternate_currency_plural_name, "Trade Notes");
        assert_eq!(parsed.items.len(), 2);
        assert_eq!(parsed.items[0].stack_size, 3);
        assert_eq!(parsed.items[0].item_guid, 0x5000_1001);
        assert_eq!(parsed.items[0].desc.name, "Dusty Tome");
        assert_eq!(parsed.items[0].desc.ammo_type, Some(42));
        assert_eq!(parsed.items[1].stack_size, -1);
        assert_eq!(parsed.items[1].desc.name, "Iron Key");
        assert_eq!(parsed.items[1].desc.item_type, ITEM_TYPE_KEY);
        assert_every_prefix_fails(w.as_slice(), ApproachVendor::parse);
    }

    #[test]
    fn empty_vendor_and_bad_counts() {
        let mut w = Writer::new();
        profile(&mut w, 0x4000_0200, "");
        w.u32(0);
        let parsed = ApproachVendor::parse(w.as_slice()).unwrap();
        assert!(parsed.items.is_empty());

        // Two items announced, one present.
        let mut w = Writer::new();
        profile(&mut w, 0x4000_0400, "");
        w.u32(2);
        item(&mut w, 1, 0x5000_2001, 0, "Solo Item", None);
        assert!(ApproachVendor::parse(w.as_slice()).is_err());

        let mut w = Writer::new();
        profile(&mut w, 0x4000_0500, "");
        w.u32(MAX_ITEMS + 1);
        assert!(matches!(
            ApproachVendor::parse(w.as_slice()),
            Err(ProtoError::CountTooLarge { .. })
        ));
    }

    #[test]
    fn buy_single_and_list() {
        let one = build_buy(
            9,
            0x4000_1000,
            &[ItemAmount {
                amount: 1,
                item_guid: 0x5000_2000,
            }],
            0,
        );
        assert_eq!(
            one,
            words(&[0xF7B1, 9, 0x5F, 0x4000_1000, 1, 1, 0x5000_2000, 0])
        );
        let currency = build_buy(
            1,
            0x4000_1000,
            &[ItemAmount {
                amount: 25,
                item_guid: 0x5000_2001,
            }],
            0x1234_5678,
        );
        assert_eq!(currency[20..24], 25i32.to_le_bytes());
        assert_eq!(currency[28..], 0x1234_5678u32.to_le_bytes());
        let two = build_buy(
            4,
            0x4000_1000,
            &[
                ItemAmount {
                    amount: 1,
                    item_guid: 0x5000_2000,
                },
                ItemAmount {
                    amount: 10,
                    item_guid: 0x5000_2001,
                },
            ],
            0,
        );
        assert_eq!(two.len(), 40);
        assert_eq!(two[16..], words(&[2, 1, 0x5000_2000, 10, 0x5000_2001, 0]));
    }

    #[test]
    fn sell_has_no_currency_word() {
        let body = build_sell(
            9,
            0x4000_1000,
            &[ItemAmount {
                amount: 1,
                item_guid: 0x5000_2000,
            }],
        );
        assert_eq!(
            body,
            words(&[0xF7B1, 9, 0x60, 0x4000_1000, 1, 1, 0x5000_2000])
        );
        assert_eq!(
            build_sell(1, 0x4000_1000, &[]),
            words(&[0xF7B1, 1, 0x60, 0x4000_1000, 0])
        );
    }
}
