//! Ported from OpenAC
//! `tests/AcDream.Runtime.Tests/Gameplay/RuntimeHousePurchaseStateTests.cs`
//! (#244 with its review fixes on `pr-fix/244`), with the button and question
//! rules of OpenAC `src/AcDream.App/UI/Layout/HousePurchaseUiController.cs`
//! (`CanSubmit`) and `HousePurchaseText.Confirmation`
//! (`tests/AcDream.App.Tests/UI/Layout/HousePurchaseTextTests.cs`).

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::{ClientObject, ClientObjectTable, EquipMask, ItemType};
use ac_proto::house::{HousePayment, HouseProfile};

use super::*;

const PLAYER: u32 = 0x5000_0001;
const PACK: u32 = 0x6000_0900;
const GROUND: u32 = 0x7000_0999;
const SLUMLORD: u32 = 0x7000_0100;
const OTHER_SLUMLORD: u32 = 0x7000_0200;
const PYREAL: u32 = 273;
const TOTEM: u32 = 9000;
const NOTE_1000: u32 = 2623;
const NOTE_5000: u32 = 2624;
const NOTE_10000: u32 = 2625;

/// The trade-note classes and their denominations, as the portal data has
/// them.
fn notes() -> HashMap<u32, i32> {
    HashMap::from([
        (2621, 100),
        (2622, 500),
        (NOTE_1000, 1_000),
        (NOTE_5000, 5_000),
        (NOTE_10000, 10_000),
    ])
}

struct Fixture {
    objects: ClientObjectTable,
    state: HousePurchaseState,
}

fn object(id: u32, item_type: ItemType) -> ClientObject {
    let mut o = ClientObject::new(id);
    o.item_type = item_type;
    o
}

impl Fixture {
    fn new() -> Self {
        let mut objects = ClientObjectTable::new();
        let mut ev = Vec::new();
        objects.add_or_update(object(PLAYER, ItemType::CREATURE), &mut ev);
        objects.add_or_update(object(GROUND, ItemType::CONTAINER), &mut ev);
        objects.add_or_update(object(PACK, ItemType::CONTAINER), &mut ev);
        objects.move_item(PACK, PLAYER, 0, EquipMask::NONE, None, &mut ev);
        let mut state = HousePurchaseState::default();
        state.install_trade_notes(Arc::new(notes()));
        Self { objects, state }
    }

    fn item(&mut self, id: u32, wcid: u32, stack: i32) {
        self.item_full(id, wcid, stack, 0, ItemType::MISC, PLAYER);
    }

    fn note(&mut self, id: u32, wcid: u32, stack: i32) {
        let value = notes()[&wcid] * stack;
        self.item_full(id, wcid, stack, value, ItemType::PROMISSORY_NOTE, PLAYER);
    }

    fn item_full(
        &mut self,
        id: u32,
        wcid: u32,
        stack: i32,
        value: i32,
        item_type: ItemType,
        container: u32,
    ) {
        let mut o = object(id, item_type);
        o.weenie_class_id = wcid;
        o.stack_size = stack;
        o.value = value;
        let mut ev = Vec::new();
        self.objects.add_or_update(o, &mut ev);
        self.objects
            .move_item(id, container, 0, EquipMask::NONE, None, &mut ev);
    }

    fn apply(&mut self, profile: HouseProfile) {
        self.state.apply_profile(profile);
    }

    fn stage(&mut self, tab: HousePurchaseTab, item: u32) -> HouseStageResult {
        self.state.stage(tab, item, &self.objects, PLAYER)
    }

    fn unstage(&mut self, tab: HousePurchaseTab, item: u32) -> bool {
        self.state.unstage(tab, item, &self.objects)
    }

    fn revalidate(&mut self) -> bool {
        self.state.revalidate(&self.objects, PLAYER)
    }

    fn view(&self) -> HousePurchaseView {
        self.state.view(&self.objects, PLAYER)
    }

    fn amount(&self, tab: HousePurchaseTab) -> i32 {
        self.view().staged_amount(tab, 0)
    }
}

fn pay(wcid: u32, num: i32, paid: i32) -> HousePayment {
    HousePayment {
        num,
        paid,
        weenie_id: wcid,
        name: "Thing".into(),
        plural_name: "Things".into(),
    }
}

fn profile(slumlord: u32, owner: u32, maintenance_free: bool) -> HouseProfile {
    HouseProfile {
        slumlord_guid: slumlord,
        dwelling_id: 0x1234_0001,
        owner_guid: owner,
        bitmask: 1,
        min_level: -1,
        max_level: -1,
        min_allegiance_rank: -1,
        max_allegiance_rank: -1,
        maintenance_free,
        house_type: 1,
        owner_name: if owner == 0 {
            String::new()
        } else {
            "Owner".into()
        },
        buy: vec![pay(PYREAL, 50_000, 0), pay(TOTEM, 1, 0)],
        rent: vec![pay(PYREAL, 5_000, 0)],
    }
}

fn for_sale() -> HouseProfile {
    profile(SLUMLORD, 0, false)
}

/// The player's house, its maintenance one line of `rent`.
fn owned_with_rent(rent: HousePayment) -> HouseProfile {
    let mut p = profile(SLUMLORD, PLAYER, false);
    p.rent = vec![rent];
    p
}

const BUY: HousePurchaseTab = HousePurchaseTab::Buy;
const RENT: HousePurchaseTab = HousePurchaseTab::Rent;

// ApplyProfile_OpensAndRaisesRevision
#[test]
fn a_profile_opens_the_window_and_raises_the_revision() {
    let mut f = Fixture::new();
    let before = f.state.revision();
    f.apply(for_sale());
    let view = f.view();
    assert!(view.is_open());
    assert_eq!(view.profile.unwrap().slumlord_guid, SLUMLORD);
    assert!(f.state.revision() > before);
}

// Stage_WithNoProfile_IsNotOpen
#[test]
fn nothing_is_staged_with_no_profile() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 100);
    assert_eq!(f.stage(BUY, 1), HouseStageResult::NotOpen);
}

// Stage_MatchingItem_IsStagedAndCountsItsStack
#[test]
fn a_matching_item_is_staged_and_counts_its_stack() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 20_000);
    f.apply(for_sale());
    assert_eq!(f.stage(BUY, 1), HouseStageResult::Staged);
    let view = f.view();
    assert_eq!(view.staged(BUY), [1]);
    assert_eq!(view.staged_amount(BUY, 0), 20_000);
    assert!(view.staged(RENT).is_empty());
}

// Stage_ItemInASidePack_IsStaged
#[test]
fn an_item_in_a_side_pack_is_staged() {
    let mut f = Fixture::new();
    f.item_full(1, TOTEM, 1, 0, ItemType::MISC, PACK);
    f.apply(for_sale());
    assert_eq!(f.stage(BUY, 1), HouseStageResult::Staged);
}

// Stage_APack_StagesTheItemsInItThatAreNeeded
#[test]
fn a_pack_stages_the_items_in_it_that_are_needed() {
    let mut f = Fixture::new();
    f.item_full(1, TOTEM, 1, 0, ItemType::MISC, PACK);
    f.item_full(2, 1234, 1, 0, ItemType::MISC, PACK);
    f.item_full(3, PYREAL, 100, 0, ItemType::MONEY, PACK);
    f.apply(for_sale());
    assert_eq!(f.stage(BUY, PACK), HouseStageResult::Staged);
    let mut staged = f.view().buy;
    staged.sort_unstable();
    assert_eq!(staged, [1, 3]);
}

// Stage_APackWithNothingNeeded_IsNotRequired
#[test]
fn a_pack_with_nothing_needed_is_not_required() {
    let mut f = Fixture::new();
    f.item_full(1, 1234, 1, 0, ItemType::MISC, PACK);
    f.apply(for_sale());
    assert_eq!(f.stage(BUY, PACK), HouseStageResult::NotRequired);
    assert!(f.view().buy.is_empty());
}

// Stage_TradeNote_CountsItsValueTowardsPyreals
#[test]
fn a_trade_note_counts_its_value_towards_pyreals() {
    let mut f = Fixture::new();
    f.note(1, NOTE_10000, 1);
    f.apply(for_sale());
    assert_eq!(f.stage(BUY, 1), HouseStageResult::Staged);
    assert_eq!(f.amount(BUY), 10_000);
}

// Stage_TradeNote_OnlyIfItFitsInWhatIsStillOwed
#[test]
fn a_trade_note_is_taken_only_if_it_fits_in_what_is_still_owed() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 45_000);
    f.note(2, NOTE_10000, 1);
    f.note(3, NOTE_5000, 1);
    f.apply(for_sale());
    f.stage(BUY, 1);
    assert_eq!(f.stage(BUY, 2), HouseStageResult::NoteTooLarge);
    assert_eq!(f.stage(BUY, 3), HouseStageResult::Staged);
}

// Stage_AStackOfNotes_CountsOnlyTheNotesThatFit (review of #244, defect 2)
#[test]
fn a_stack_of_notes_counts_only_the_notes_that_fit() {
    let mut f = Fixture::new();
    f.note(1, NOTE_1000, 3);
    f.apply(owned_with_rent(pay(PYREAL, 2_500, 0)));
    assert_eq!(f.stage(RENT, 1), HouseStageResult::Staged);
    assert_eq!(f.amount(RENT), 2_000);
}

// Stage_ANoteStack_IsRefusedOnlyWhenOneNoteIsMoreThanOwed (defect 2)
#[test]
fn a_note_stack_is_refused_only_when_one_note_is_more_than_owed() {
    let mut f = Fixture::new();
    f.note(1, NOTE_1000, 5);
    f.note(2, NOTE_1000, 5);
    f.apply(owned_with_rent(pay(PYREAL, 1_500, 0)));
    assert_eq!(f.stage(RENT, 1), HouseStageResult::Staged);
    assert_eq!(f.amount(RENT), 1_000);
    assert_eq!(f.stage(RENT, 2), HouseStageResult::NoteTooLarge);
}

// Stage_ATradeNoteIsKnownByItsClass_NotByItsItemType (defect 2)
#[test]
fn a_trade_note_is_known_by_its_class_not_by_its_item_type() {
    let mut f = Fixture::new();
    // A promissory note of a class the portal data does not list pays
    // nothing.
    f.item_full(1, 99_999, 1, 1_000, ItemType::PROMISSORY_NOTE, PLAYER);
    // A listed class pays its denomination, whatever its type and value say.
    f.item_full(2, NOTE_1000, 2, 7, ItemType::MISC, PLAYER);
    f.apply(for_sale());
    assert_eq!(f.stage(BUY, 1), HouseStageResult::NotRequired);
    assert_eq!(f.stage(BUY, 2), HouseStageResult::Staged);
    assert_eq!(f.amount(BUY), 2_000);
}

// Stage_AStackMayRunOverWhatIsOwed_ButCountsOnlyWhatIsOwed
#[test]
fn a_stack_may_run_over_what_is_owed_but_counts_only_what_is_owed() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 60_000);
    f.apply(for_sale());
    assert_eq!(f.stage(BUY, 1), HouseStageResult::Staged);
    assert_eq!(f.amount(BUY), 50_000);
}

// Unstage_TakesOffTheWholeStack_EvenThePartThatDidNotCount
#[test]
fn unstaging_takes_off_the_whole_stack_even_the_part_that_did_not_count() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 2_000);
    f.item(2, PYREAL, 1_000);
    f.apply(owned_with_rent(pay(PYREAL, 2_500, 0)));
    f.stage(RENT, 1);
    f.stage(RENT, 2);
    assert_eq!(f.amount(RENT), 2_500);
    f.unstage(RENT, 2);
    assert_eq!(f.amount(RENT), 1_500);
}

// Unstage_ANoteStack_TakesOffEveryNoteInIt_AndNeverBelowZero
#[test]
fn unstaging_a_note_stack_takes_off_every_note_and_never_below_zero() {
    let mut f = Fixture::new();
    f.note(1, NOTE_1000, 3);
    f.item(2, PYREAL, 2_000);
    f.apply(owned_with_rent(pay(PYREAL, 2_500, 0)));
    f.stage(RENT, 1);
    assert_eq!(f.amount(RENT), 2_000);
    f.stage(RENT, 2);
    assert_eq!(f.amount(RENT), 2_500);
    // Three notes are 3000, more than the 2500 counted: the line drops to 0.
    f.unstage(RENT, 1);
    assert_eq!(f.amount(RENT), 0);
}

// AStagedItemLeavingTheInventory_TakesOffItsPayment
#[test]
fn a_staged_item_leaving_the_inventory_takes_off_its_payment() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 2_000);
    f.apply(owned_with_rent(pay(PYREAL, 5_000, 0)));
    f.stage(RENT, 1);
    f.objects.remove(1, &mut Vec::new());
    f.revalidate();
    assert_eq!(f.amount(RENT), 0);
}

// AStagedStackChangingSize_KeepsWhatItPaidWhenStaged
#[test]
fn a_staged_stack_changing_size_keeps_what_it_paid_when_staged() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 2_000);
    f.apply(owned_with_rent(pay(PYREAL, 5_000, 0)));
    f.stage(RENT, 1);
    f.objects.get_mut(1).unwrap().stack_size = 4_000;
    f.objects.notify_object_updated(1, &mut Vec::new());
    f.revalidate();
    assert_eq!(f.amount(RENT), 2_000);
}

// Stage_RefusesTheWrongClass
#[test]
fn the_wrong_class_is_refused() {
    let mut f = Fixture::new();
    f.item(1, 1234, 1);
    f.apply(for_sale());
    assert_eq!(f.stage(BUY, 1), HouseStageResult::NotRequired);
}

// Stage_RefusesTheSameItemTwice
#[test]
fn the_same_item_is_refused_twice() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 10);
    f.apply(for_sale());
    f.stage(BUY, 1);
    assert_eq!(f.stage(BUY, 1), HouseStageResult::AlreadyStaged);
}

// Stage_RefusesALineAlreadyCovered
#[test]
fn a_line_already_covered_is_refused() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 50_000);
    f.item(2, PYREAL, 5);
    let mut p = for_sale();
    p.buy = vec![pay(PYREAL, 50_000, 0)];
    f.apply(p);
    assert_eq!(f.stage(BUY, 1), HouseStageResult::Staged);
    assert_eq!(f.stage(BUY, 2), HouseStageResult::AlreadyCovered);
}

// Stage_CountsFromZero_NotFromWhatTheServerSaysIsPaid (review of #244,
// defect 1)
#[test]
fn the_staging_counts_from_zero_not_from_what_the_server_says_is_paid() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 5_000);
    f.item(2, PYREAL, 5_000);
    f.apply(owned_with_rent(pay(PYREAL, 5_000, 4_000)));
    assert_eq!(f.stage(RENT, 1), HouseStageResult::Staged);
    assert_eq!(f.amount(RENT), 5_000);
    // Maintenance already paid in full still takes an item.
    f.apply(owned_with_rent(pay(PYREAL, 5_000, 5_000)));
    assert_eq!(f.stage(RENT, 2), HouseStageResult::Staged);
}

// Stage_RefusesBuyingAnOwnedHouse
#[test]
fn buying_an_owned_house_is_refused() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 10);
    f.apply(profile(SLUMLORD, 0x5000_0042, false));
    assert_eq!(f.stage(BUY, 1), HouseStageResult::AlreadyOwned);
}

// Stage_RefusesRentOnAnUnownedHouse
#[test]
fn maintenance_on_an_unowned_house_is_refused() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 10);
    f.apply(for_sale());
    assert_eq!(f.stage(RENT, 1), HouseStageResult::NotOwned);
}

// Stage_RefusesRentWhenMaintenanceIsFree
#[test]
fn maintenance_is_refused_when_it_is_free() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 10);
    f.apply(profile(SLUMLORD, PLAYER, true));
    assert_eq!(f.stage(RENT, 1), HouseStageResult::MaintenanceFree);
}

// Stage_RefusesItemsNotInTheInventory
#[test]
fn items_not_in_the_inventory_are_refused() {
    let mut f = Fixture::new();
    f.item_full(1, PYREAL, 10, 0, ItemType::MONEY, GROUND);
    f.item(2, TOTEM, 1);
    f.objects.get_mut(2).unwrap().wielder_id = PLAYER;
    f.apply(for_sale());
    assert_eq!(f.stage(BUY, 1), HouseStageResult::NotInInventory);
    assert_eq!(f.stage(BUY, 2), HouseStageResult::NotInInventory);
    assert_eq!(f.stage(BUY, 99), HouseStageResult::NotInInventory);
}

// Stage_AnItemNotCarried_IsNotInInventory_BeforeAnyTabRefusal (defect 4)
#[test]
fn an_item_not_carried_is_refused_before_any_tab_refusal() {
    let mut f = Fixture::new();
    f.item_full(1, PYREAL, 10, 0, ItemType::MONEY, GROUND);
    f.apply(profile(SLUMLORD, 0x5000_0042, false));
    assert_eq!(f.stage(BUY, 1), HouseStageResult::NotInInventory);
    f.apply(for_sale());
    assert_eq!(f.stage(RENT, 1), HouseStageResult::NotInInventory);
}

// ApplyProfile_FromAnotherSlumlord_ClearsStagedItems
#[test]
fn another_slumlords_profile_empties_the_strips() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 10);
    f.apply(for_sale());
    f.stage(BUY, 1);
    f.apply(profile(OTHER_SLUMLORD, 0, false));
    assert!(f.view().buy.is_empty());
}

// ApplyProfile_FromTheSameSlumlord_EmptiesBothStrips (defect 1)
#[test]
fn the_same_slumlords_profile_empties_both_strips() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 10);
    f.item(2, PYREAL, 20);
    f.apply(profile(SLUMLORD, PLAYER, false));
    f.stage(RENT, 1);
    f.apply(for_sale());
    f.stage(BUY, 2);
    let mut p = for_sale();
    p.rent = vec![pay(PYREAL, 5_000, 20)];
    f.apply(p);
    let view = f.view();
    assert!(view.rent.is_empty());
    assert!(view.buy.is_empty());
    assert_eq!(view.staged_amount(BUY, 0), 0);
}

// Revalidate_DropsItemsThatLeftTheInventory
#[test]
fn revalidating_drops_items_that_left_the_inventory() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 10);
    f.apply(for_sale());
    f.stage(BUY, 1);
    let before = f.state.revision();
    assert!(!f.revalidate());
    assert_eq!(f.state.revision(), before);

    f.objects
        .move_item(1, GROUND, 0, EquipMask::NONE, None, &mut Vec::new());
    assert!(f.view().buy.is_empty());
    assert!(f.revalidate());
    assert!(f.state.revision() > before);
}

// ClearStaged_EmptiesTheTab
#[test]
fn clearing_a_tab_empties_it_and_its_paid_amounts() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 10);
    f.apply(profile(SLUMLORD, PLAYER, false));
    f.stage(RENT, 1);
    f.state.clear_staged(RENT);
    assert!(f.view().rent.is_empty());
    assert_eq!(f.amount(RENT), 0);
}

// Unstage_AndClose
#[test]
fn unstaging_and_closing() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 10);
    f.item(2, TOTEM, 1);
    f.apply(for_sale());
    f.stage(BUY, 1);
    f.stage(BUY, 2);
    assert!(f.unstage(BUY, 1));
    assert_eq!(f.view().buy, [2]);
    assert!(f.state.close());
    let view = f.view();
    assert!(!view.is_open());
    assert!(view.profile.is_none());
    assert!(view.buy.is_empty());
}

// HousePurchaseUiControllerTests.Buy_StaysDisabledUntilEveryLineIsCovered and
// the maintenance part-payment: Buy waits for every line, counted from the
// staging alone (defect 1); maintenance may be part-paid; nothing is sent
// with nothing staged or on a refused tab.
#[test]
fn buy_waits_for_every_staged_line_and_maintenance_may_be_part_paid() {
    let mut f = Fixture::new();
    f.item(1, PYREAL, 50_000);
    f.item(2, TOTEM, 1);
    f.item(3, PYREAL, 100);
    f.item(4, PYREAL, 30_000);
    let mut part_bought = for_sale();
    part_bought.buy[0].paid = 20_000;
    f.apply(part_bought);
    f.stage(BUY, 4);
    f.stage(BUY, 2);
    assert!(!f.view().can_submit(BUY), "30000 of 50000 staged");

    f.apply(for_sale());
    assert!(!f.view().can_submit(BUY), "nothing staged");
    f.stage(BUY, 1);
    assert!(!f.view().can_submit(BUY), "the totem is still owed");
    f.stage(BUY, 2);
    assert!(f.view().can_submit(BUY));
    assert!(!f.view().can_submit(RENT), "nobody owns it");

    f.apply(profile(OTHER_SLUMLORD, PLAYER, false));
    f.stage(RENT, 3);
    assert!(f.view().can_submit(RENT), "100 of 5000 may be paid");
    assert!(!f.view().can_submit(BUY), "an owned house is not for sale");
}

// HousePurchaseTextTests.Confirmation_BuyingAHouseButNotAnApartment_AndOthersMaintenance
#[test]
fn a_purchase_and_anothers_maintenance_are_asked_about_first() {
    let mut f = Fixture::new();
    f.apply(for_sale());
    assert_eq!(f.view().confirmation(BUY, PLAYER), Some(BUY_CONFIRMATION));
    let mut apartment = for_sale();
    apartment.house_type = APARTMENT_TYPE;
    f.apply(apartment);
    assert_eq!(f.view().confirmation(BUY, PLAYER), None);
    f.apply(profile(SLUMLORD, 0x5000_0042, false));
    assert_eq!(
        f.view().confirmation(RENT, PLAYER),
        Some(OTHERS_MAINTENANCE_CONFIRMATION)
    );
    f.apply(profile(SLUMLORD, PLAYER, false));
    assert_eq!(f.view().confirmation(RENT, PLAYER), None);
}

fn body_at(x: f32) -> Option<ac_physics::object_range::RangeBody> {
    Some(ac_physics::object_range::RangeBody {
        position: glam::Vec3::new(x, 100.0, 0.0),
        radius: 0.0,
        height: 0.0,
    })
}

// RuntimeHousePurchaseRangeQueryTests (InRange_StaysOpen,
// BeyondTheUseRadius_ButWithinNineMetres_StaysOpen, WalkingAway_Closes,
// SlumlordGone_Closes): the window closes at a fixed 9 m, whatever the
// slumlord's use radius (3 m there); a slumlord gone closes it; with no
// place for the player nothing is decided. (A teleport closes it in the
// inventory owner.)
#[test]
fn the_slumlord_window_closes_at_a_fixed_nine_metres() {
    let slumlord = body_at(102.0);
    assert!(stays_open(body_at(100.0), slumlord));
    assert!(
        stays_open(body_at(110.0), slumlord),
        "8 m: past 3, within 9"
    );
    assert!(!stays_open(body_at(122.0), slumlord), "20 m");
    assert!(!stays_open(body_at(100.0), None), "the slumlord is gone");
    assert!(stays_open(None, slumlord));
}
