//! An item on its way into a pack, shown waiting at its slot until the
//! server answers; drops on item windows; the open pack; the salvage marks.
//!
//! The cases follow OpenAC `tests/AcDream.App.Tests/UI/Layout/InventoryControllerTests.cs`
//! (`LootDrop_InsertsWaitingProjectionAtChosenSlotUntilServerConfirms`,
//! `DoubleClickLoot_ReservesFirstOpenPackSlotBeforePickupIsSent`,
//! `GroundDragThenDirectPickupKeepsTheFirstPendingProjection`,
//! `PendingLootRemovalImmediatelyWithdrawsTheProjection`,
//! `LootDrop_ServerFailureRemovesOnlyThePendingProjection`,
//! `Drop_selectedPartialStack_splitsIntoContainer_withoutMovingOriginal`),
//! `ToolbarControllerTests.cs` (`InventoryButton_*`) and
//! `ExternalContainerControllerTests.cs`, at the runtime's half of them
//! (`RuntimeItemInteraction.TryDispatchPendingBackpackPlacement`,
//! `TrySplitToContainer`, `PreferredBackpackContainer`).

use super::*;

const SCROLL: u32 = 0x7000_0009;
const CHEST: u32 = 0x7000_0020;

impl Fixture {
    fn scroll(&mut self) {
        self.world.apply(&create(SCROLL, "Scroll", |d| {
            d.item_type = ItemType::WRITABLE.bits();
        }));
        self.world.take_table_events();
    }

    fn placement(&self) -> Option<(u32, u32, i32)> {
        self.inventory
            .pending_placement()
            .map(|p| (p.item_id, p.container_id, p.placement))
    }

    fn drop_into(
        &mut self,
        item: u32,
        container: u32,
        slot: i32,
        amount: u32,
        pickup: bool,
    ) -> bool {
        self.run(ItemCommand::DropInto {
            item: guid(item),
            container: guid(container),
            slot,
            amount,
            pickup,
            join_stack: false,
        })
    }
}

#[test]
fn a_loose_item_dropped_on_a_pack_waits_at_its_slot_until_the_server_places_it() {
    // LootDrop_InsertsWaitingProjectionAtChosenSlotUntilServerConfirms: a
    // ground item goes out as a pickup into the pack and slot dropped on,
    // and is shown there, waiting, until the server's placement.
    let mut f = Fixture::new();
    assert!(f.drop_into(LOOSE, PACK, 2, 0, true));
    assert_eq!(f.sent(), [build_put_item_in_container(1, LOOSE, PACK, 2)]);
    assert_eq!(f.placement(), Some((LOOSE, PACK, 2)));
    assert_eq!(f.inventory.pending_item(), Some(LOOSE));
    assert!(!f.inventory.is_ready(true));
    f.server(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[LOOSE, PACK, 2, 0],
    );
    assert_eq!(f.placement(), None);
    assert!(f.inventory.is_ready(true));
}

#[test]
fn a_refusal_or_the_item_leaving_ends_the_waiting_placement() {
    // LootDrop_ServerFailureRemovesOnlyThePendingProjection.
    let mut f = Fixture::new();
    f.drop_into(LOOSE, PACK, 0, 0, true);
    f.server(GameEventType::INVENTORY_SERVER_SAVE_FAILED, &[LOOSE, 0x29]);
    assert_eq!(f.placement(), None);
    assert!(f.inventory.is_ready(true));
    // PendingLootRemovalImmediatelyWithdrawsTheProjection.
    let mut f = Fixture::new();
    f.drop_into(LOOSE, PACK, 0, 0, true);
    f.world
        .apply(&Inbound::DeleteObject(ac_proto::DeleteObject {
            guid: LOOSE,
            instance_sequence: 1,
        }));
    let mut inventory = std::mem::take(&mut f.inventory);
    inventory.observe(&mut f.ctx());
    f.inventory = inventory;
    assert_eq!(f.placement(), None);
}

#[test]
fn part_of_a_stack_dropped_from_anywhere_splits_and_is_not_shown_waiting() {
    // InventoryController.HandleDropRelease: the selected stack's split
    // quantity goes through TrySplitToContainer, whatever the drag's source
    // (a ground stack included); only the whole item is a placement.
    let mut f = Fixture::new();
    assert!(f.drop_into(LOOSE, PACK, 3, 1, true));
    assert_eq!(
        f.sent(),
        [build_stackable_split_to_container(1, LOOSE, PACK, 3, 1)]
    );
    assert_eq!(f.placement(), None);
    assert_eq!(f.inventory.pending_item(), Some(LOOSE));
    // An owned stack the same way.
    let mut f = Fixture::new();
    assert!(f.drop_into(GEM, PACK, 0, 2, false));
    assert_eq!(
        f.sent(),
        [build_stackable_split_to_container(1, GEM, PACK, 0, 2)]
    );
}

#[test]
fn an_owned_item_dropped_into_a_chest_or_pack_is_a_waiting_move() {
    // ExternalContainerController.HandleDropRelease and the inventory's
    // drop: the whole item is TryDispatchPendingBackpackPlacement with a
    // put-in-container, into a container the player need not own.
    let mut f = Fixture::new();
    f.world.apply(&create(CHEST, "Chest", |d| {
        d.item_type = ItemType::CONTAINER.bits();
        d.items_capacity = Some(10);
    }));
    f.world.take_table_events();
    assert!(f.drop_into(SWORD, CHEST, 0, 0, false));
    assert_eq!(f.sent(), [build_put_item_in_container(1, SWORD, CHEST, 0)]);
    assert_eq!(f.placement(), Some((SWORD, CHEST, 0)));
    assert_eq!(
        f.inventory.transactions().pending().map(|p| p.kind),
        Some(ac_core::items::InventoryRequestKind::PutInContainer)
    );
}

#[test]
fn a_pickup_by_use_waits_in_the_open_pack_and_a_second_is_refused() {
    // DoubleClickLoot_ReservesFirstOpenPackSlotBeforePickupIsSent and
    // GroundDragThenDirectPickupKeepsTheFirstPendingProjection: the pickup
    // is shown at the top of the pack the window has open; another
    // placement meanwhile is refused with "Already attempting to place X
    // here" and sends nothing.
    let mut f = Fixture::new();
    f.scroll();
    f.run(ItemCommand::SetOpenPack(PACK));
    assert!(f.run(ItemCommand::PlaceInPack(guid(SCROLL))));
    assert_eq!(f.sent(), [build_put_item_in_container(1, SCROLL, PACK, 0)]);
    assert_eq!(f.placement(), Some((SCROLL, PACK, 0)));
    assert_eq!(
        f.inventory.take_backpack_placement(),
        Some(BackpackPlacement::Sent)
    );
    f.world.apply(&create(0x7000_000A, "Book", |d| {
        d.item_type = ItemType::WRITABLE.bits();
    }));
    f.world.take_table_events();
    assert!(!f.run(ItemCommand::PlaceInPack(guid(0x7000_000A))));
    assert!(f.sent().is_empty());
    assert_eq!(
        f.notices(),
        [Notice::Interface(
            "Already attempting to place Scroll here".into()
        )]
    );
    assert_eq!(
        f.inventory.take_backpack_placement(),
        Some(BackpackPlacement::AlreadyPending)
    );
    assert_eq!(f.placement(), Some((SCROLL, PACK, 0)));
}

#[test]
fn a_loose_item_dropped_on_a_toolbar_slot_is_picked_up_and_then_gets_the_shortcut() {
    // GitHub #45: a ground item dropped on the hotbar is picked up into the
    // preferred pack, as the use key picks it up, and the shortcut follows
    // the request; when the pickup cannot go out the bar is left alone.
    use ac_proto::inventory::build_add_shortcut;
    let mut f = Fixture::new();
    f.scroll();
    f.run(ItemCommand::SetOpenPack(PACK));
    assert!(f.run(ItemCommand::PickUpToShortcut {
        item: guid(SCROLL),
        slot: 4,
    }));
    let wire = ac_proto::Shortcut {
        index: 4,
        object_id: SCROLL,
        spell_id: 0,
    };
    assert_eq!(
        f.sent(),
        [
            build_put_item_in_container(1, SCROLL, PACK, 0),
            build_add_shortcut(2, &wire)
        ]
    );
    assert_eq!(f.inventory.shortcuts().get(4), SCROLL);
    assert_eq!(f.placement(), Some((SCROLL, PACK, 0)));
    // A second while the first is in flight: refused, nothing sent, the
    // bar unchanged.
    f.world.apply(&create(0x7000_000A, "Book", |d| {
        d.item_type = ItemType::WRITABLE.bits();
    }));
    f.world.take_table_events();
    f.notices();
    assert!(!f.run(ItemCommand::PickUpToShortcut {
        item: guid(0x7000_000A),
        slot: 5,
    }));
    assert!(f.sent().is_empty());
    assert!(f.inventory.shortcuts().is_empty(5));
    // A slot off the bar is refused before anything is asked.
    assert!(!f.run(ItemCommand::PickUpToShortcut {
        item: guid(0x7000_000A),
        slot: 18,
    }));
    assert!(f.sent().is_empty());
}

#[test]
fn a_pickup_that_joins_a_held_stack_waits_in_the_pack_it_would_take() {
    // TryPlaceWorldItemInBackpack's merge: the join is the placement, shown
    // in the pack the search chose (the main pack here).
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::PlaceInPack(guid(LOOSE))));
    assert_eq!(f.sent(), [build_stackable_merge(1, LOOSE, GEM, 2)]);
    assert_eq!(f.placement(), Some((LOOSE, PLAYER, 0)));
}

#[test]
fn the_backpack_button_moves_owned_items_to_the_preferred_pack_and_picks_up_the_rest() {
    // InventoryButton_ownedDropPublishesDestinationAndGloballyRejectsSecondRequest,
    // InventoryButton_ownedDropHonorsMainPackPreferenceLikeGroundPickupDoes,
    // InventoryButton_unownedGroundDropUsesDirectAttemptWithoutDestinationProjection.
    let mut f = Fixture::new();
    f.run(ItemCommand::SetOpenPack(PACK));
    assert!(f.run(ItemCommand::DropOnBackpack(guid(SWORD))));
    assert_eq!(f.sent(), [build_put_item_in_container(1, SWORD, PACK, 0)]);
    assert_eq!(f.placement(), Some((SWORD, PACK, 0)));
    // A second request meanwhile: the busy notice, nothing sent.
    assert!(!f.run(ItemCommand::DropOnBackpack(guid(GEM))));
    assert!(f.sent().is_empty());
    assert_eq!(f.notices(), [Notice::Interface(REQUEST_BUSY.into())]);

    let mut f = Fixture::new();
    f.run(ItemCommand::SetOpenPack(PACK));
    f.options
        .set_bit(crate::character::option_id::MAIN_PACK_PREFERRED, true);
    assert!(f.run(ItemCommand::DropOnBackpack(guid(SWORD))));
    assert_eq!(f.sent(), [build_put_item_in_container(1, SWORD, PLAYER, 0)]);

    let mut f = Fixture::new();
    f.run(ItemCommand::SetOpenPack(PACK));
    assert!(f.run(ItemCommand::DropOnBackpack(guid(LOOSE))));
    assert_eq!(f.sent(), [build_put_item_in_container(1, LOOSE, PLAYER, 0)]);
    assert_eq!(f.placement(), None);
    assert_eq!(
        f.inventory.transactions().pending().map(|p| p.kind),
        Some(ac_core::items::InventoryRequestKind::Pickup)
    );
}

#[test]
fn salvage_marks_hold_staged_items_as_in_a_trade_until_unstaged_or_the_window_goes() {
    // SalvageUiController.AddRecursive / RemoveItem / ClearItems: a staged
    // item's trade state is 1 (StackMergePlanner and the placement policy
    // refuse it; the inventory draws the trade overlay), 0 again when it
    // leaves the staging.
    let mut f = Fixture::new();
    assert!(f.run(ItemCommand::MarkSalvage(vec![GEM2, SWORD])));
    assert_eq!(f.world.item(GEM2).unwrap().trade_state, 1);
    assert_eq!(f.world.item(SWORD).unwrap().trade_state, 1);
    // A staged stack does not merge.
    assert!(!f.run(ItemCommand::Merge {
        from: guid(GEM2),
        to: guid(GEM),
        amount: 0,
    }));
    assert!(f.sent().is_empty());
    f.events();
    assert!(f.run(ItemCommand::MarkSalvage(vec![SWORD])));
    assert_eq!(f.world.item(GEM2).unwrap().trade_state, 0);
    assert_eq!(f.world.item(SWORD).unwrap().trade_state, 1);
    // The window going takes the marks and the open pack with it.
    f.run(ItemCommand::SetOpenPack(PACK));
    f.inventory.window_detached(&mut f.world);
    assert_eq!(f.world.item(SWORD).unwrap().trade_state, 0);
    assert_eq!(f.inventory.open_pack(), 0);
}

#[test]
fn a_closing_shop_names_each_listing_it_takes_off_the_buy_list() {
    // OpenAC: the listing materializer (`VendorShopItemMaterializer`,
    // subscribed when the runtime's inventory state is built) retires the
    // listings before the vendor panel hears the close, so the panel's
    // `OnObjectRemoved` names each staged listing ("Removing X from
    // shopping list", `ShopItemLeavingClientObjectTable_UnstagesTheBuyEntryWithRetailsNotice`)
    // before its `OnVendorChanged(Closed)` empties the lists.
    let mut f = Fixture::new();
    f.at_the_shop();
    assert!(f.run(ItemCommand::Vendor(VendorCommand::AddToBuyList {
        item: guid(DIAMOND),
        amount: 1,
    })));
    f.notices();
    assert!(f.run(ItemCommand::Vendor(VendorCommand::Close)));
    assert_eq!(
        f.notices(),
        [Notice::Interface(
            "Removing Diamond from shopping list".into()
        )]
    );
    assert!(f.inventory.vendor_lists().buy().is_empty());
}

// A drop on a pack's icon (OpenAC `InventoryController.TryJoinStackInPack`):
// the moving amount joins the first stack in that pack, or in a pack inside
// it, with room for all of it, before any room check; a loose stack too.
fn join_drop(item: u32, container: u32, amount: u32, pickup: bool) -> ItemCommand {
    ItemCommand::DropInto {
        item: guid(item),
        container: guid(container),
        slot: 0,
        amount,
        pickup,
        join_stack: true,
    }
}

#[test]
fn a_stack_dropped_on_a_pack_icon_joins_the_first_stack_there_with_room_for_all_of_it() {
    let mut f = Fixture::new();
    let (nearly_full, roomy) = (0x6100_0050, 0x6100_0051);
    add_stack(&mut f, nearly_full, JOIN_KIND, 98, PACK);
    add_stack(&mut f, roomy, JOIN_KIND, 50, PACK);
    let source = 0x6100_0052;
    add_stack(&mut f, source, JOIN_KIND, 5, PLAYER);
    assert!(f.run(join_drop(source, PACK, 0, false)));
    assert_eq!(f.sent(), [build_stackable_merge(1, source, roomy, 5)]);
    assert_eq!(f.placement(), None);
    // The main pack's icon searches each side pack too.
    let mut f = Fixture::new();
    add_stack(&mut f, roomy, JOIN_KIND, 50, PACK);
    add_stack(&mut f, source, JOIN_KIND, 5, PLAYER);
    assert!(f.run(join_drop(source, PLAYER, 0, false)));
    assert_eq!(f.sent(), [build_stackable_merge(1, source, roomy, 5)]);
    // A loose stack (a pickup) joins the same way.
    let mut f = Fixture::new();
    assert!(f.run(join_drop(LOOSE, PLAYER, 0, true)));
    assert_eq!(f.sent(), [build_stackable_merge(1, LOOSE, GEM, 2)]);
}

#[test]
fn a_pack_icon_drop_with_no_stack_to_join_is_the_plain_drop() {
    let mut f = Fixture::new();
    let (nearly_full, source) = (0x6100_0053, 0x6100_0054);
    add_stack(&mut f, nearly_full, JOIN_KIND, 98, PACK);
    add_stack(&mut f, source, JOIN_KIND, 5, PLAYER);
    assert!(f.run(join_drop(source, PACK, 0, false)));
    assert_eq!(f.sent(), [build_put_item_in_container(1, source, PACK, 0)]);
    assert_eq!(f.placement(), Some((source, PACK, 0)));
}

// A drop on a stack in the pack's grid (OpenAC
// `InventoryController.HandleDropRelease`, `TryMergeStacks`): a legal merge
// takes the drop, wherever the dragged stack lies, the ground included;
// with no legal merge the drop goes on as the plain one into the pack.
fn stack_drop(item: u32, target: u32, slot: Option<i32>, pickup: bool) -> ItemCommand {
    ItemCommand::DropOnStack {
        item: guid(item),
        target: guid(target),
        merge_amount: 100,
        container: guid(PACK),
        slot,
        amount: 0,
        pickup,
    }
}

#[test]
fn a_ground_stack_dropped_on_a_matching_stack_in_the_grid_merges_into_it() {
    let mut f = Fixture::new();
    assert!(f.run(stack_drop(LOOSE, GEM, Some(1), true)));
    assert_eq!(f.sent(), [build_stackable_merge(1, LOOSE, GEM, 2)]);
    assert_eq!(f.placement(), None);
}

#[test]
fn a_stack_drop_with_no_legal_merge_is_the_plain_drop() {
    let mut f = Fixture::new();
    let (full, other_kind) = (0x6100_0055, 0x6100_0056);
    // The gems' own kind (class 10), its stack full.
    add_stack(&mut f, full, 10, 100, PACK);
    add_stack(&mut f, other_kind, JOIN_KIND + 1, 5, PACK);
    // A full stack takes nothing: the ground stack is picked up into the
    // pack at the slot dropped on.
    assert!(f.run(stack_drop(LOOSE, full, Some(1), true)));
    assert_eq!(f.sent(), [build_put_item_in_container(1, LOOSE, PACK, 1)]);
    assert_eq!(f.placement(), Some((LOOSE, PACK, 1)));
    // A stack of another kind: the plain move.
    let mut f = Fixture::new();
    add_stack(&mut f, other_kind, JOIN_KIND + 1, 5, PACK);
    assert!(f.run(stack_drop(GEM, other_kind, Some(0), false)));
    assert_eq!(f.sent(), [build_put_item_in_container(1, GEM, PACK, 0)]);
    // Already where it was dropped: nothing goes.
    let mut f = Fixture::new();
    add_stack(&mut f, other_kind, JOIN_KIND + 1, 5, PACK);
    assert!(!f.run(stack_drop(GEM, other_kind, None, false)));
    assert!(f.sent().is_empty());
}

#[test]
fn a_plugins_merge_still_needs_both_stacks_carried() {
    // OpenAC `RuntimeItemInteraction.TryMergeItemsForAutomation`.
    let mut f = Fixture::new();
    assert!(!f.run(ItemCommand::Merge {
        from: guid(LOOSE),
        to: guid(GEM),
        amount: 0,
    }));
    assert!(f.sent().is_empty());
}
