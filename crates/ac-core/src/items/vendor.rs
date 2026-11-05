//! Vendors: the open shop, its prices, what it will buy, and how many of a
//! listed item a selection stands for.

use super::{ItemType, PublicWeenieFlags};

/// What a shop deals in and at what rates.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct VendorShopProfile {
    /// The item kinds it buys.
    pub merchandise_item_types: u32,
    /// The lowest per-unit value it buys; [`NO_LIMIT`] for none.
    pub merchandise_min_value: u32,
    /// The highest per-unit value it buys; [`NO_LIMIT`] for none.
    pub merchandise_max_value: u32,
    pub deal_magical_items: bool,
    /// The fraction of value it pays the player.
    pub buy_price: f32,
    /// The multiple of value it charges the player.
    pub sell_price: f32,
    pub alternate_currency_wcid: u32,
    pub alternate_currency_amount: u32,
    pub alternate_currency_plural_name: String,
}

/// One listed item with its full description, so assessing it reads like
/// assessing an item in the pack.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct VendorShopItem {
    pub item_guid: u32,
    /// How many the shop stocks; [`UNLIMITED_STOCK`] for no limit.
    pub stack_size: i32,
    pub weenie_class_id: u32,
    pub name: Option<String>,
    pub item_type: Option<u32>,
    pub icon_id: u32,
    /// The value of the item's own authored stack (see `desc_stack_size`).
    pub value: Option<i32>,
    /// The item's own authored stack depth: `value` divided by it is the
    /// per-unit value.
    pub desc_stack_size: Option<i32>,
    pub max_stack_size: Option<i32>,
    pub icon_underlay_id: u32,
    pub icon_overlay_id: u32,
    pub effects: u32,
    pub plural_name: Option<String>,
    pub valid_locations: Option<u32>,
    pub priority: Option<u32>,
    pub items_capacity: Option<i32>,
    pub containers_capacity: Option<i32>,
    pub structure: Option<i32>,
    pub max_structure: Option<i32>,
    pub workmanship: Option<f32>,
    pub burden: Option<i32>,
    pub material_type: Option<u32>,
    pub target_type: Option<u32>,
    pub combat_use: Option<u8>,
    pub ammo_type: Option<u16>,
    pub public_weenie_bitfield: Option<u32>,
    pub useability: Option<u32>,
    pub hook_item_types: Option<u32>,
    pub hook_type: Option<u32>,
}

/// The listed stack size meaning "stocked without limit".
pub const UNLIMITED_STOCK: i32 = -1;

/// What happened to the open shop.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VendorTransitionKind {
    /// A shop other than the open one (or none) opened.
    Opened,
    /// The open shop's listing was sent again.
    Refreshed,
    Closed,
    /// Session teardown.
    Reset,
}

/// A change of the open shop.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VendorTransition {
    pub kind: VendorTransitionKind,
    pub previous_vendor_id: u32,
    pub vendor_id: u32,
}

/// The open shop: its vendor, profile and listing.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct VendorState {
    vendor_id: u32,
    profile: VendorShopProfile,
    items: Vec<VendorShopItem>,
}

impl VendorState {
    /// No shop open.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The open vendor; 0 for none.
    #[must_use]
    pub fn vendor_id(&self) -> u32 {
        self.vendor_id
    }

    #[must_use]
    pub fn profile(&self) -> &VendorShopProfile {
        &self.profile
    }

    #[must_use]
    pub fn items(&self) -> &[VendorShopItem] {
        &self.items
    }

    /// A shop listing arrived: it opens (or refreshes, for the open
    /// vendor). Vendor 0 is ignored.
    pub fn apply(
        &mut self,
        vendor_guid: u32,
        profile: VendorShopProfile,
        items: Vec<VendorShopItem>,
    ) -> Option<VendorTransition> {
        if vendor_guid == 0 {
            return None;
        }
        let previous = self.vendor_id;
        let same_vendor = previous != 0 && previous == vendor_guid;
        self.vendor_id = vendor_guid;
        self.profile = profile;
        self.items = items;
        Some(VendorTransition {
            kind: if same_vendor {
                VendorTransitionKind::Refreshed
            } else {
                VendorTransitionKind::Opened
            },
            previous_vendor_id: previous,
            vendor_id: vendor_guid,
        })
    }

    /// The shop closed; nothing when none was open.
    pub fn close(&mut self) -> Option<VendorTransition> {
        self.end(VendorTransitionKind::Closed)
    }

    /// Session teardown; nothing when no shop was open.
    pub fn reset(&mut self) -> Option<VendorTransition> {
        self.end(VendorTransitionKind::Reset)
    }

    fn end(&mut self, kind: VendorTransitionKind) -> Option<VendorTransition> {
        if self.vendor_id == 0 {
            return None;
        }
        let previous = self.vendor_id;
        *self = Self::default();
        Some(VendorTransition {
            kind,
            previous_vendor_id: previous,
            vendor_id: 0,
        })
    }
}

/// The value of one unit: a listing's value covers its authored stack.
#[must_use]
pub fn per_unit_value(stack_total_value: i32, desc_stack_size: Option<i32>) -> i32 {
    match desc_stack_size {
        Some(size) if size > 0 => stack_total_value / size,
        _ => stack_total_value,
    }
}

/// What the vendor pays the player for `quantity` units: rounded down
/// (with a 0.1 allowance), at least 1, -1 for a negative result. Promissory
/// notes always go at face value.
#[must_use]
pub fn buy_price(per_unit_value: i32, item_type: u32, buy_rate: f32, quantity: i32) -> i32 {
    let rate = if item_type == ItemType::PROMISSORY_NOTE.bits() {
        1.0
    } else {
        buy_rate
    };
    let raw = f64::from(rate) * f64::from(per_unit_value) * f64::from(quantity);
    let floored = (raw + 0.1).floor() as i32;
    match floored {
        0 => 1,
        f if f > 0 => f,
        _ => -1,
    }
}

/// What the player pays the vendor for `quantity` units: rounded up (with
/// a 0.1 allowance), at least 1, -1 for a negative result. Promissory
/// notes always cost 1.15 times face value.
#[must_use]
pub fn sell_price(per_unit_value: i32, item_type: u32, sell_rate: f32, quantity: i32) -> i32 {
    let rate = if item_type == ItemType::PROMISSORY_NOTE.bits() {
        1.15f32
    } else {
        sell_rate
    };
    let raw = f64::from(rate) * f64::from(per_unit_value) * f64::from(quantity);
    let ceiled = (raw - 0.1).ceil() as i32;
    match ceiled {
        0 => 1,
        c if c > 0 => c,
        _ => -1,
    }
}

/// The value-limit sentinel meaning "no limit".
pub const NO_LIMIT: u32 = u32::MAX;

/// Why a vendor will not take an item.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VendorSellRejection {
    NotOwnedByPlayer,
    WrongType,
    CannotBeSoldHere,
    /// The per-unit value is zero.
    NoValue,
    TooValuable,
    TooCheap,
}

impl VendorSellRejection {
    /// The notice for the refusal.
    #[must_use]
    pub fn message(self) -> &'static str {
        match self {
            Self::NotOwnedByPlayer => "You can only sell items you are carrying",
            Self::CannotBeSoldHere => "That item cannot be sold here",
            Self::NoValue => "That item has no value and cannot be sold",
            Self::TooCheap => "That item is too cheap to sell here",
            Self::TooValuable => "That item is too valuable to sell here",
            Self::WrongType => "You cannot sell that here",
        }
    }
}

/// The facts about an item dropped on the sell list.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct VendorSellCandidate {
    pub owned_by_player: bool,
    /// Objects in its item slots (a container, or the main pack): side
    /// packs and spell foci take pack slots and are not counted.
    pub contained_item_count: i32,
    pub item_type: u32,
    pub per_unit_value: i32,
    pub public_weenie_bitfield: u32,
}

/// Whether the shop buys the item: it must be carried; a non-empty
/// container always goes; otherwise the kind must match and the item not
/// be retained, the value non-zero, not above the maximum (promissory
/// notes excepted) and not below the minimum.
pub fn evaluate_sell(
    item: &VendorSellCandidate,
    profile: &VendorShopProfile,
) -> Result<(), VendorSellRejection> {
    if !item.owned_by_player {
        return Err(VendorSellRejection::NotOwnedByPlayer);
    }
    if item.contained_item_count > 0 {
        return Ok(());
    }
    let retained = item.public_weenie_bitfield & PublicWeenieFlags::RETAINED.bits() != 0;
    if item.item_type & profile.merchandise_item_types == 0 || retained {
        return Err(VendorSellRejection::WrongType);
    }
    if item.per_unit_value == 0 {
        return Err(VendorSellRejection::NoValue);
    }
    let value = i64::from(item.per_unit_value);
    if profile.merchandise_max_value != NO_LIMIT && value > i64::from(profile.merchandise_max_value)
    {
        return if item.item_type & ItemType::PROMISSORY_NOTE.bits() != 0 {
            Ok(())
        } else {
            Err(VendorSellRejection::TooValuable)
        };
    }
    if profile.merchandise_min_value != NO_LIMIT && value < i64::from(profile.merchandise_min_value)
    {
        return Err(VendorSellRejection::TooCheap);
    }
    Ok(())
}

/// Item kinds a vendor-owned selection always counts as one unit of.
pub const SPLIT_EXEMPT_MASK: u32 = 0x0DC4_1CB0;

/// The kind always counts as one unit when selected in a shop.
#[must_use]
pub fn is_split_exempt(item_type: ItemType) -> bool {
    item_type.bits() & SPLIT_EXEMPT_MASK != 0
}

/// How many units a selected shop item stands for: 1 for exempt kinds,
/// else its authored stack size (at least 1).
#[must_use]
pub fn seed_quantity(item_type: ItemType, authored_stack_size: Option<i32>) -> i32 {
    if is_split_exempt(item_type) {
        return 1;
    }
    match authored_stack_size {
        Some(size) if size > 0 => size,
        _ => 1,
    }
}

/// A listing's authored stack size: its maximum stack, else its own stack
/// depth, else 1.
#[must_use]
pub fn resolve_authored_stack_size(
    desc_stack_size: Option<i32>,
    max_stack_size: Option<i32>,
) -> i32 {
    match (max_stack_size, desc_stack_size) {
        (Some(max), _) if max > 0 => max,
        (_, Some(desc)) if desc > 0 => desc,
        _ => 1,
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/VendorStateTests.cs`,
    //! `VendorPricingTests.cs` and `VendorSellAcceptabilityTests.cs`. The
    //! throwing-observer tests have no counterpart (no observers).

    use super::*;

    fn item(guid: u32, stack: i32, wcid: u32, name: &str, item_type: ItemType) -> VendorShopItem {
        VendorShopItem {
            item_guid: guid,
            stack_size: stack,
            weenie_class_id: wcid,
            name: Some(name.into()),
            item_type: Some(item_type.bits()),
            ..VendorShopItem::default()
        }
    }

    #[test]
    fn apply_zero_guid_is_a_no_op() {
        let mut state = VendorState::new();
        assert!(
            state
                .apply(0, VendorShopProfile::default(), Vec::new())
                .is_none()
        );
        assert_eq!(state.vendor_id(), 0);
    }

    #[test]
    fn apply_new_vendor_reports_opened_and_stores_snapshot() {
        let mut state = VendorState::new();
        let profile = VendorShopProfile {
            merchandise_item_types: ItemType::MELEE_WEAPON.bits(),
            merchandise_min_value: 1,
            merchandise_max_value: 5000,
            deal_magical_items: true,
            buy_price: 0.5,
            sell_price: 1.5,
            ..VendorShopProfile::default()
        };
        let items = vec![VendorShopItem {
            icon_id: 0x0600_1234,
            value: Some(25),
            ..item(0x5000_0A01, 3, 42, "Iron Dagger", ItemType::MELEE_WEAPON)
        }];
        let change = state.apply(0x4000_0001, profile.clone(), items.clone());
        assert_eq!(state.vendor_id(), 0x4000_0001);
        assert_eq!(state.profile(), &profile);
        assert_eq!(state.items(), items.as_slice());
        assert_eq!(
            change,
            Some(VendorTransition {
                kind: VendorTransitionKind::Opened,
                previous_vendor_id: 0,
                vendor_id: 0x4000_0001
            })
        );
    }

    #[test]
    fn apply_same_vendor_again_reports_refreshed() {
        let mut state = VendorState::new();
        let _ = state.apply(0x4000_0002, VendorShopProfile::default(), Vec::new());
        assert_eq!(
            state.apply(0x4000_0002, VendorShopProfile::default(), Vec::new()),
            Some(VendorTransition {
                kind: VendorTransitionKind::Refreshed,
                previous_vendor_id: 0x4000_0002,
                vendor_id: 0x4000_0002
            })
        );
    }

    #[test]
    fn apply_different_vendor_reports_opened_with_previous_id() {
        let mut state = VendorState::new();
        let _ = state.apply(0x4000_0003, VendorShopProfile::default(), Vec::new());
        assert_eq!(
            state.apply(0x4000_0004, VendorShopProfile::default(), Vec::new()),
            Some(VendorTransition {
                kind: VendorTransitionKind::Opened,
                previous_vendor_id: 0x4000_0003,
                vendor_id: 0x4000_0004
            })
        );
        assert_eq!(state.vendor_id(), 0x4000_0004);
    }

    #[test]
    fn close_with_nothing_open_is_a_no_op() {
        assert!(VendorState::new().close().is_none());
    }

    #[test]
    fn close_clears_snapshot_and_reports_closed() {
        let mut state = VendorState::new();
        let _ = state.apply(
            0x4000_0005,
            VendorShopProfile::default(),
            vec![item(0x5000_0A02, 1, 7, "Rock", ItemType::MISC)],
        );
        assert_eq!(
            state.close(),
            Some(VendorTransition {
                kind: VendorTransitionKind::Closed,
                previous_vendor_id: 0x4000_0005,
                vendor_id: 0
            })
        );
        assert_eq!(state.vendor_id(), 0);
        assert_eq!(state.profile(), &VendorShopProfile::default());
        assert!(state.items().is_empty());
    }

    #[test]
    fn reset_reports_once_and_not_again_with_no_vendor_open() {
        let mut state = VendorState::new();
        let _ = state.apply(0x4000_0006, VendorShopProfile::default(), Vec::new());
        assert_eq!(
            state.reset().map(|t| t.kind),
            Some(VendorTransitionKind::Reset)
        );
        assert_eq!(state.vendor_id(), 0);
        assert!(state.reset().is_none());
    }

    const MISC: u32 = 0x80;

    #[test]
    fn rate_one_whole_number_value_passes_through_unchanged() {
        assert_eq!(buy_price(100, MISC, 1.0, 1), 100);
        assert_eq!(sell_price(100, MISC, 1.0, 1), 100);
    }

    #[test]
    fn fractional_rate_with_stack_quantity_rounds_per_direction() {
        assert_eq!(buy_price(37, MISC, 0.75, 2), 55);
        assert_eq!(sell_price(37, MISC, 0.75, 2), 56);
    }

    #[test]
    fn zero_value_clamps_to_minimum_one() {
        assert_eq!(buy_price(0, MISC, 1.0, 1), 1);
        assert_eq!(sell_price(0, MISC, 1.0, 1), 1);
    }

    #[test]
    fn halfway_raw_value_buy_rounds_down_sell_rounds_up() {
        assert_eq!(buy_price(41, MISC, 0.5, 1), 20);
        assert_eq!(sell_price(41, MISC, 0.5, 1), 21);
    }

    #[test]
    fn large_value_with_stack_multiplier_computes_exactly() {
        assert_eq!(buy_price(1000, MISC, 2.5, 5), 12500);
        assert_eq!(sell_price(1000, MISC, 2.5, 5), 12500);
    }

    #[test]
    fn promissory_note_ignores_supplied_rate() {
        let note = ItemType::PROMISSORY_NOTE.bits();
        assert_eq!(buy_price(100, note, 3.0, 1), 100);
        assert_eq!(sell_price(100, note, 3.0, 1), 115);
    }

    #[test]
    fn negative_value_returns_sentinel_not_one() {
        assert_eq!(buy_price(-50, MISC, 1.0, 1), -1);
        assert_eq!(sell_price(-50, MISC, 1.0, 1), -1);
    }

    #[test]
    fn per_unit_value_divides_by_the_authored_stack() {
        assert_eq!(per_unit_value(500, Some(50)), 10);
        assert_eq!(per_unit_value(250, Some(0)), 250);
        assert_eq!(per_unit_value(250, Some(-1)), 250);
        assert_eq!(per_unit_value(250, None), 250);
        assert_eq!(per_unit_value(100, Some(3)), 33);
    }

    const ARMOR: u32 = 0x2;
    const WEAPON: u32 = 0x101;

    fn profile(types: u32, min: u32, max: u32) -> VendorShopProfile {
        VendorShopProfile {
            merchandise_item_types: types,
            merchandise_min_value: min,
            merchandise_max_value: max,
            ..VendorShopProfile::default()
        }
    }

    fn candidate(item_type: u32, per_unit_value: i32) -> VendorSellCandidate {
        VendorSellCandidate {
            owned_by_player: true,
            contained_item_count: 0,
            item_type,
            per_unit_value,
            public_weenie_bitfield: 0,
        }
    }

    #[test]
    fn not_owned_is_rejected_before_any_other_check() {
        let item = VendorSellCandidate {
            owned_by_player: false,
            ..candidate(ARMOR, 100)
        };
        assert_eq!(
            evaluate_sell(&item, &profile(ARMOR, 0, NO_LIMIT)),
            Err(VendorSellRejection::NotOwnedByPlayer)
        );
    }

    #[test]
    fn a_non_empty_container_bypasses_the_type_and_value_filter() {
        let item = VendorSellCandidate {
            contained_item_count: 3,
            ..candidate(WEAPON, 0)
        };
        assert_eq!(evaluate_sell(&item, &profile(ARMOR, 0, NO_LIMIT)), Ok(()));
    }

    #[test]
    fn value_and_type_gates() {
        assert_eq!(
            evaluate_sell(&candidate(WEAPON, 100), &profile(ARMOR, 0, NO_LIMIT)),
            Err(VendorSellRejection::WrongType)
        );
        assert_eq!(
            evaluate_sell(&candidate(ARMOR, 0), &profile(ARMOR, 0, NO_LIMIT)),
            Err(VendorSellRejection::NoValue)
        );
        assert_eq!(
            evaluate_sell(&candidate(ARMOR, 1001), &profile(ARMOR, 0, 1000)),
            Err(VendorSellRejection::TooValuable)
        );
        assert_eq!(
            evaluate_sell(&candidate(ARMOR, 1000), &profile(ARMOR, 0, 1000)),
            Ok(())
        );
        assert_eq!(
            evaluate_sell(&candidate(ARMOR, 4), &profile(ARMOR, 5, NO_LIMIT)),
            Err(VendorSellRejection::TooCheap)
        );
        assert_eq!(
            evaluate_sell(
                &candidate(ARMOR, i32::MAX - 1),
                &profile(ARMOR, NO_LIMIT, NO_LIMIT)
            ),
            Ok(())
        );
        assert_eq!(
            evaluate_sell(&candidate(ARMOR, 999_999), &profile(ARMOR, 0, 1000)),
            Err(VendorSellRejection::TooValuable)
        );
        assert_eq!(
            evaluate_sell(&candidate(ARMOR, 500), &profile(ARMOR, 1, 10_000)),
            Ok(())
        );
    }

    #[test]
    fn promissory_note_above_max_value_is_exempt() {
        let note = ItemType::PROMISSORY_NOTE.bits();
        assert_eq!(
            evaluate_sell(&candidate(note, 999_999), &profile(note, 0, 1000)),
            Ok(())
        );
    }

    #[test]
    fn retained_item_is_wrong_type_and_other_bits_do_not_matter() {
        let retained = VendorSellCandidate {
            public_weenie_bitfield: PublicWeenieFlags::RETAINED.bits(),
            ..candidate(ARMOR, 100)
        };
        assert_eq!(
            evaluate_sell(&retained, &profile(ARMOR, 0, NO_LIMIT)),
            Err(VendorSellRejection::WrongType)
        );
        let stuck = VendorSellCandidate {
            public_weenie_bitfield: PublicWeenieFlags::STUCK.bits(),
            ..candidate(ARMOR, 100)
        };
        assert_eq!(evaluate_sell(&stuck, &profile(ARMOR, 0, NO_LIMIT)), Ok(()));
    }

    #[test]
    fn messages_are_verbatim() {
        for (rejection, expected) in [
            (
                VendorSellRejection::NotOwnedByPlayer,
                "You can only sell items you are carrying",
            ),
            (
                VendorSellRejection::CannotBeSoldHere,
                "That item cannot be sold here",
            ),
            (
                VendorSellRejection::NoValue,
                "That item has no value and cannot be sold",
            ),
            (
                VendorSellRejection::TooCheap,
                "That item is too cheap to sell here",
            ),
            (
                VendorSellRejection::TooValuable,
                "That item is too valuable to sell here",
            ),
            (VendorSellRejection::WrongType, "You cannot sell that here"),
        ] {
            assert_eq!(rejection.message(), expected);
        }
    }

    #[test]
    fn split_policy_seeds_one_for_exempt_kinds() {
        // The mask covers creature, food, misc, useless, gem, spell
        // components and higher bits; not armor or weapons.
        assert!(is_split_exempt(ItemType::SPELL_COMPONENTS));
        assert!(is_split_exempt(ItemType::FOOD | ItemType::ARMOR));
        assert!(!is_split_exempt(ItemType::ARMOR | ItemType::MISSILE_WEAPON));
        assert_eq!(seed_quantity(ItemType::SPELL_COMPONENTS, Some(5)), 1);
        assert_eq!(seed_quantity(ItemType::MISSILE_WEAPON, Some(5)), 5);
        assert_eq!(seed_quantity(ItemType::MISSILE_WEAPON, Some(0)), 1);
        assert_eq!(resolve_authored_stack_size(Some(10), Some(100)), 100);
        assert_eq!(resolve_authored_stack_size(Some(10), None), 10);
        assert_eq!(resolve_authored_stack_size(None, Some(0)), 1);
    }
}
