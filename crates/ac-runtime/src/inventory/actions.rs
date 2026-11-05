//! The item requests: use, use on a target, pick up, move, split, merge,
//! drop, give, wield, appraise, buy, sell and the trade window's buttons.
//! Each checks what the client checks, then goes out as the one request in
//! flight (or, for uses, counts as busy until the server's use-done).

use std::collections::BTreeSet;

use ac_core::items::shortcuts::{SLOT_COUNT as SHORTCUT_SLOTS, plan_full_stack_merge};
use ac_core::items::{
    ClientObject, EquipMask, InventoryRequestKind, ItemPolicyAction, ItemPolicyActionKind,
    ItemPolicyObject, ItemType, ItemUsePolicyInput, ItemUseable, MoveOrigin, PublicWeenieFlags,
    RemovalReason, ShortcutEntry, ShortcutMutation, StackMergeItem, StackMergePlan, TableEvent,
    interaction, placement,
};
use ac_proto::appraise::build_appraise;
use ac_proto::inventory::{
    build_add_shortcut, build_drop_item, build_give_object, build_no_longer_viewing_contents,
    build_put_item_in_container, build_remove_shortcut, build_set_inscription,
    build_stackable_merge, build_stackable_split_to_3d, build_stackable_split_to_container,
    build_use, build_use_with_target,
};
use ac_proto::trade::{
    TradeAcceptance, build_accept_trade, build_add_to_trade, build_close_trade_negotiations,
    build_decline_trade, build_open_trade_negotiations, build_reset_trade,
};
use ac_proto::vendor::{ItemAmount, build_buy, build_sell};

use super::{
    AppraisalOrigin, BackpackPlacement, InteractionMode, Inventory, InventoryEvent, ItemCommand,
    ItemContext, Notice, ObjectRef, REQUEST_BUSY, VendorCommand, VendorViewReason, amount, shop,
};
use crate::character::option_id;
use crate::world::{World, distance};
use ac_core::items::ClientObjectTable;

/// What a request the client refused without a reason of its own reports.
pub const REQUEST_NOT_SENT: &str = "The request was not sent.";

impl Inventory {
    /// Run one item request. False when nothing was sent (the reason, if the
    /// client gives one, is among the notices or events).
    pub fn execute(&mut self, command: &ItemCommand, ctx: &mut ItemContext<'_>) -> bool {
        let notices_before = self.notices.len();
        let events_before = self.events.len();
        let sent = self.run(command, ctx);
        if !sent && self.notices.len() == notices_before && self.events.len() == events_before {
            self.events
                .push(InventoryEvent::Refused(REQUEST_NOT_SENT.to_owned()));
        }
        sent
    }

    fn run(&mut self, command: &ItemCommand, ctx: &mut ItemContext<'_>) -> bool {
        use ItemCommand as C;
        macro_rules! resolve {
            ($reference:expr) => {
                match self.resolve($reference, ctx) {
                    Some(guid) => guid,
                    None => return false,
                }
            };
        }
        match command {
            C::Use(item) => {
                let item = resolve!(item);
                self.activate_item(item, ctx)
            }
            C::UseWorldObject(item) => {
                let item = resolve!(item);
                self.use_world_object(item, ctx)
            }
            C::UseOn { item, target } => {
                let (item, target) = (resolve!(item), resolve!(target));
                self.try_apply_item(item, target, ctx)
            }
            C::PickUp(item) => {
                let item = resolve!(item);
                self.pick_up(item, ctx)
            }
            C::PlaceInPack(item) => {
                let item = resolve!(item);
                self.place_world_item_in_backpack(item, ctx)
            }
            C::MoveToContainer {
                item,
                container,
                slot,
                amount,
                join_stack,
            } => {
                let (item, container) = (resolve!(item), resolve!(container));
                self.try_move_item(item, container, *amount, *slot, *join_stack, ctx)
            }
            C::Merge { from, to, amount } => {
                let (from, to) = (resolve!(from), resolve!(to));
                self.try_merge_items(from, to, *amount, ctx)
            }
            C::Drop { item, amount } => {
                let item = resolve!(item);
                self.try_drop_item(item, *amount, ctx)
            }
            C::Give {
                item,
                target,
                amount,
            } => {
                let (item, target) = (resolve!(item), resolve!(target));
                self.try_give_item(item, target, *amount, ctx)
            }
            C::Wield { item, location } => {
                let item = resolve!(item);
                ctx.world.item(item).is_some()
                    && self.ensure_ready(ctx)
                    && self.try_wield(item, EquipMask(*location), ctx)
            }
            C::WieldSecondary(item) => {
                let item = resolve!(item);
                ctx.world.item(item).is_some()
                    && self.ensure_ready(ctx)
                    && self.try_wield_secondary(item, ctx)
            }
            C::Unwield(item) => {
                let item = resolve!(item);
                // Taking off is the wielded item's primary use: into the
                // backpack the placement search chooses (the main pack,
                // else the first side pack with room).
                let player = ctx.player();
                let worn = ctx.world.item(item).is_some_and(|i| {
                    i.wielder_id == player && i.currently_equipped_location != EquipMask::NONE
                });
                worn && self.place_world_item_in_backpack(item, ctx)
            }
            C::Appraise(object) => {
                let object = resolve!(object);
                self.request_appraisal(object, AppraisalOrigin::User, ctx)
            }
            C::CloseContainer(container) => {
                let container = resolve!(container);
                ctx.send(|seq| build_use(seq, container))
            }
            C::StopViewing(container) => {
                let container = resolve!(container);
                if let Some(transition) = self.external.apply_close(container) {
                    self.probe_external(|| format!("stopped viewing 0x{container:08X}"));
                    Self::retire_external(transition, ctx);
                }
                ctx.send(|seq| build_no_longer_viewing_contents(seq, container))
            }
            C::Buy(list) | C::Sell(list) => {
                let vendor = self.vendor.vendor_id();
                if vendor == 0 {
                    self.events
                        .push(InventoryEvent::Refused("No vendor is open.".to_owned()));
                    return false;
                }
                let mut items = Vec::with_capacity(list.len());
                for (item, count) in list {
                    items.push(amount(resolve!(item), *count));
                }
                if matches!(command, C::Buy(_)) {
                    // The buy button: the purchase must fit in the main pack.
                    let rows: Vec<(i32, u32)> =
                        items.iter().map(|i| (i.amount, i.item_guid)).collect();
                    if !shop::has_room(&rows, &self.vendor, ctx.world) {
                        self.interface(shop::NOT_ENOUGH_ROOM);
                        return false;
                    }
                    let currency = self.vendor.profile().alternate_currency_wcid;
                    let sent = self.try_buy(vendor, &items, currency, ctx);
                    if sent {
                        for item in &items {
                            self.lists
                                .bought_one(&self.vendor, item.item_guid, item.amount);
                        }
                        if currency != 0 {
                            // The purse counts the alternate currency spent
                            // at once (the inventory shows it later).
                            self.vendor_view_event(ctx.world, VendorViewReason::Lists);
                        }
                    }
                    sent
                } else {
                    self.try_sell(vendor, &items, ctx)
                }
            }
            C::Vendor(command) => self.run_vendor(command, ctx),
            C::OpenTrade(partner) => {
                let partner = resolve!(partner);
                self.request_secure_trade(partner, ctx)
            }
            C::AddToTrade(item) => {
                let item = resolve!(item);
                self.trade.view().open && ctx.send(|seq| build_add_to_trade(seq, item, 0))
            }
            C::AcceptTrade => {
                let trade = self.trade.view().clone();
                trade.open
                    && !trade.self_accepted
                    && ctx.send(|seq| {
                        build_accept_trade(
                            seq,
                            &TradeAcceptance {
                                partner_guid: trade.partner_guid,
                                trade_stamp: 0.0,
                                trade_status: 0,
                                initiator_guid: trade.partner_guid,
                                initiator_accepts: true,
                                partner_accepts: trade.partner_accepted,
                            },
                        )
                    })
            }
            C::DeclineTrade => self.trade.view().open && ctx.send(build_decline_trade),
            C::ResetTrade => self.trade.view().open && ctx.send(build_reset_trade),
            C::CloseTrade => self.trade.view().open && ctx.send(build_close_trade_negotiations),
            C::HousePurchase(command) => self.run_house_purchase(*command, ctx),
            C::DropInto {
                item,
                container,
                slot,
                amount,
                pickup,
                join_stack,
            } => {
                let (item, container) = (resolve!(item), resolve!(container));
                if *join_stack
                    && let Some(sent) = self.join_dropped_stack(item, container, *amount, ctx)
                {
                    return sent;
                }
                self.drop_into(item, container, *slot, *amount, *pickup, ctx)
            }
            C::DropOnStack {
                item,
                target,
                merge_amount,
                container,
                slot,
                amount,
                pickup,
            } => {
                let (item, target, container) =
                    (resolve!(item), resolve!(target), resolve!(container));
                if self.merge_stacks(item, target, *merge_amount, ctx) {
                    return true;
                }
                slot.is_some_and(|slot| {
                    self.drop_into(item, container, slot, *amount, *pickup, ctx)
                })
            }
            C::DropOnBackpack(item) => {
                let item = resolve!(item);
                self.drop_on_backpack(item, ctx)
            }
            C::MarkSalvage(items) => {
                self.mark_salvage(items, ctx.world);
                true
            }
            C::AddShortcut(entry) => self.add_shortcut(*entry, ctx),
            C::RemoveShortcut(slot) => self.remove_shortcut(*slot, ctx),
            C::PickUpToShortcut { item, slot } => {
                let item = resolve!(item);
                self.pick_up_to_shortcut(item, *slot, ctx)
            }
            C::EnterUseMode => {
                self.mode = InteractionMode::Use;
                true
            }
            C::EnterExamineMode => {
                self.mode = InteractionMode::Examine;
                true
            }
            C::CancelTargetMode => {
                self.mode = InteractionMode::None;
                true
            }
            C::OfferTarget(target) => {
                let target = resolve!(target);
                self.offer_target(target, ctx)
            }
            C::Inscribe { item, text } => {
                let item = resolve!(item);
                self.try_inscribe(item, text, ctx)
            }
            C::Salvage { tool, items } => {
                let tool = resolve!(tool);
                let mut ids = Vec::with_capacity(items.len());
                for item in items {
                    ids.push(resolve!(item));
                }
                self.try_salvage_items(tool, &ids, ctx)
            }
            C::QueryMana(item) => {
                let item = resolve!(item);
                ctx.send(|seq| ac_proto::magic::build_query_item_mana(seq, item))
            }
            // OpenAC `RuntimeInteractionTransactionState.RefreshCurrentAppraisal`.
            C::RefreshCurrentAppraisal => {
                let current = self.interaction.current_appraisal();
                current != 0 && ctx.send(|seq| build_appraise(seq, current))
            }
            // The session puts the selection in; here nothing is selected.
            C::UseSelected => {
                self.mode = InteractionMode::Use;
                true
            }
            C::ExamineSelected => {
                self.mode = InteractionMode::Examine;
                true
            }
            C::PickUpSelected => {
                self.notices.push(Notice::Interface(
                    super::pickup::NOTHING_SELECTED.to_owned(),
                ));
                true
            }
            C::PlaceSelectedInPack
            | C::PickUpSelectedToMainPack
            | C::GiveSelectedToPrevious { .. }
            | C::DropSelected { .. } => true,
            C::PickUpToMainPack(item) => {
                let item = resolve!(item);
                self.place_world_item_in_pack(item, true, ctx)
            }
            C::SetOpenPack(pack) => {
                self.open_pack = *pack;
                true
            }
            C::Place {
                item,
                target,
                amount,
            } => {
                let item = resolve!(item);
                let target = match target {
                    Some(target) => resolve!(target),
                    None => 0,
                };
                self.place_in_3d(item, target, *amount, ctx)
            }
        }
    }

    // -- Placing in the world -------------------------------------------------

    /// Put an item into the world, as a drag out of a window drops it: the
    /// placement rules decide between the pack, a merge, the vendor's sell
    /// list, a secure trade, a gift, an open container and the ground.
    pub(super) fn place_in_3d(
        &mut self,
        item_id: u32,
        target_id: u32,
        amount: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let Some(item) = ctx.world.item(item_id).cloned() else {
            return false;
        };
        if item_id == 0 || !self.ensure_ready(ctx) {
            return false;
        }
        let full = item.stack_size.max(1).unsigned_abs();
        let split = if amount == 0 { full } else { amount };
        let target = (target_id != 0)
            .then(|| ctx.world.item(target_id).cloned())
            .flatten()
            .map(|t| Self::snapshot(&t, ctx));
        let airborne = ctx.approach.as_deref().is_some_and(|a| a.is_airborne());
        let decision = interaction::decide_placement(&interaction::ItemPlacementPolicyInput {
            item: Self::snapshot(&item, ctx),
            player_id: ctx.player(),
            ground_object_id: self.external.current_container_id(),
            ready_for_inventory_request: self.can_make_request(ctx),
            target_id,
            target,
            allow_ground_fallback: true,
            merge_accepted: false,
            drag_on_player_opens_secure_trade: ctx
                .options
                .get(option_id::DRAG_ITEM_ON_PLAYER_OPENS_SECURE_TRADE),
            player_on_ground: !airborne,
            split_size: i32::try_from(split).unwrap_or(i32::MAX),
        });
        self.execute_placement_actions(&decision.actions, ctx);
        decision.return_value
    }

    fn execute_placement_actions(
        &mut self,
        actions: &[ItemPolicyAction],
        ctx: &mut ItemContext<'_>,
    ) {
        use ItemPolicyActionKind as K;
        for action in actions {
            let amount = u32::try_from(action.amount.max(1)).unwrap_or(1);
            match action.kind {
                K::PlaceInBackpack => {
                    self.place_world_item_in_backpack(action.object_id, ctx);
                }
                K::StartSecureTrade => {
                    // The trade window stages the item once it opens.
                    self.request_secure_trade(action.target_id, ctx);
                }
                K::DropToWorld => {
                    self.try_drop_item(action.object_id, 0, ctx);
                }
                K::SplitToWorld => {
                    self.try_drop_item(action.object_id, amount, ctx);
                }
                K::GiveToTarget => {
                    self.try_give_item(action.object_id, action.target_id, amount, ctx);
                }
                K::PlaceInContainer => {
                    self.try_place_in_container(action.object_id, action.target_id, amount, ctx);
                }
                K::SellToVendor => {
                    // The vendor window's sell list takes it.
                    self.run_vendor(
                        &VendorCommand::AddToSellList(ObjectRef::Guid(action.object_id)),
                        ctx,
                    );
                }
                K::Reject => {
                    if let Some(message) = action.message.as_ref().filter(|m| !m.trim().is_empty())
                    {
                        self.notices.push(Notice::Interface(message.clone()));
                    }
                }
                _ => self.notices.push(Notice::Interface(
                    policy_action_message(action.kind).to_owned(),
                )),
            }
        }
    }

    /// Put an item into an open ground container: the whole stack, or the
    /// part asked for as a split.
    fn try_place_in_container(
        &mut self,
        item_id: u32,
        container_id: u32,
        amount: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let full = ctx
            .world
            .item(item_id)
            .map_or(amount, |i| i.stack_size.max(1).unsigned_abs());
        if amount < full {
            return self.dispatch_request(
                InventoryRequestKind::SplitToContainer,
                item_id,
                ctx,
                |ctx| {
                    ctx.send(|seq| {
                        build_stackable_split_to_container(seq, item_id, container_id, 0, amount)
                    })
                },
            );
        }
        self.dispatch_request(InventoryRequestKind::PutInContainer, item_id, ctx, |ctx| {
            ctx.send(|seq| build_put_item_in_container(seq, item_id, container_id, 0))
        })
    }

    // -- Salvage --------------------------------------------------------------

    /// Salvage `items` with `tool`, as the salvage panel's button does: when
    /// an item request may be made, the tool is a tinkering tool the player
    /// carries, and every item is distinct, not the tool, the player's and
    /// suitable for salvaging. Nothing is said when it is refused.
    pub(super) fn try_salvage_items(
        &mut self,
        tool: u32,
        items: &[u32],
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let is_tool = |world: &World| {
            world
                .item(tool)
                .is_some_and(|t| t.item_type.intersects(ItemType::TINKERING_TOOL))
        };
        if tool == 0
            || items.is_empty()
            || !self.can_make_request(ctx)
            || !self.is_owned_by_player(tool, ctx)
            || !is_tool(ctx.world)
        {
            return false;
        }
        let mut distinct = BTreeSet::new();
        for &item in items {
            let suitable = ctx
                .world
                .item(item)
                .is_some_and(|i| ac_core::items::is_salvage_suitable(i, 0, true));
            if item == 0
                || item == tool
                || !distinct.insert(item)
                || !self.is_owned_by_player(item, ctx)
                || !suitable
            {
                return false;
            }
        }
        if !ctx.active {
            return false;
        }
        match ctx.outbox.try_send_action(|seq| {
            ac_proto::inventory::build_create_tinkering_tool(seq, tool, items)
        }) {
            Ok(_) => true,
            Err(error) => {
                self.events.push(InventoryEvent::Refused(format!(
                    "That could not be sent: {error}."
                )));
                false
            }
        }
    }

    // -- Inscription ----------------------------------------------------------

    /// Write an item's inscription, as the appraisal panel's field does when
    /// it loses focus: only an inscribable item the player carries, written
    /// by nobody or by the player; an unchanged text, or an empty one on an
    /// item nobody inscribed, sends nothing. Who inscribed it and what it
    /// says come from the item's last appraisal.
    fn try_inscribe(&mut self, item_id: u32, text: &str, ctx: &mut ItemContext<'_>) -> bool {
        use ac_core::properties::PropertyString;
        let player = ctx.player();
        let Some(item) = ctx.world.item(item_id) else {
            return false;
        };
        let strings = &item.properties.strings;
        let scribe = strings
            .get(&PropertyString::SCRIBE_NAME.0)
            .cloned()
            .unwrap_or_default();
        let old = strings
            .get(&PropertyString::INSCRIPTION.0)
            .cloned()
            .unwrap_or_default();
        let inscribable = item.flags().intersects(PublicWeenieFlags::INSCRIBABLE);
        let player_name = ctx
            .world
            .item(player)
            .map(|p| p.name.clone())
            .unwrap_or_default();
        let owned = self.is_owned_by_player(item_id, ctx);
        let by_another = !scribe.is_empty() && !scribe.eq_ignore_ascii_case(&player_name);
        if !inscribable || !owned || by_another {
            let refusal = if by_another {
                format!("Only {scribe} can change the inscription")
            } else if !owned {
                "Item must be in your inventory to inscribe.".to_owned()
            } else {
                "This item is not inscribable.".to_owned()
            };
            self.notices.push(Notice::Interface(refusal));
            return false;
        }
        if (text.is_empty() && scribe.is_empty()) || text == old {
            return false;
        }
        if !ctx.active {
            return false;
        }
        match ctx
            .outbox
            .try_send_action(|seq| build_set_inscription(seq, item_id, text))
        {
            Ok(_) => true,
            Err(error) => {
                self.events.push(InventoryEvent::Refused(format!(
                    "That could not be sent: {error}."
                )));
                false
            }
        }
    }

    // -- Naming ---------------------------------------------------------------

    /// The object a reference names: an id as given, the player, or the best
    /// name match (the player's items, then the open ground container's,
    /// then the open vendor's listing, then the nearest object in the world;
    /// an exact name before a prefix).
    fn resolve(&mut self, reference: &ObjectRef, ctx: &ItemContext<'_>) -> Option<u32> {
        let player = ctx.player();
        let found = match reference {
            ObjectRef::Guid(guid) => Some(*guid),
            ObjectRef::Player => (player != 0).then_some(player),
            ObjectRef::Name(name) => self.find_by_name(name, ctx),
        };
        if found.is_none() {
            let text = match reference {
                ObjectRef::Name(name) => format!("Nothing called \"{name}\" is known."),
                _ => "The player is not in the world.".to_owned(),
            };
            self.events.push(InventoryEvent::Refused(text));
        }
        found
    }

    fn find_by_name(&self, name: &str, ctx: &ItemContext<'_>) -> Option<u32> {
        let wanted = name.to_lowercase();
        let player = ctx.player();
        let world = &*ctx.world;
        let here = world.object(player).and_then(|o| o.position().copied());
        // (tier, prefix-only, distance in centimetres, guid)
        let mut best: Option<(u8, bool, u64, u32)> = None;
        let mut consider = |tier: u8, guid: u32, candidate: &str, distance_m: f32| {
            let candidate = candidate.to_lowercase();
            let exact = candidate == wanted;
            if !exact && !candidate.starts_with(&wanted) {
                return;
            }
            let rank = (tier, !exact, (distance_m * 100.0) as u64, guid);
            if best.is_none_or(|b| rank < b) {
                best = Some(rank);
            }
        };
        for item in world.items().objects() {
            if item.object_id() != player
                && world.items().is_owned_by_object(item.object_id(), player)
            {
                consider(0, item.object_id(), &item.name, 0.0);
            }
        }
        // What lies in the open ground container (a corpse being looted).
        let ground = self.external.current_container_id();
        if ground != 0 {
            for &id in world.items().get_contents(ground) {
                if let Some(item) = world.items().get(id) {
                    consider(1, id, &item.name, 0.0);
                }
            }
        }
        for item in self.vendor.items() {
            if let Some(name) = &item.name {
                consider(2, item.item_guid, name, 0.0);
            }
        }
        for object in world.objects() {
            let (Some(position), Some(name)) = (object.position(), world.name(object.guid()))
            else {
                continue;
            };
            let metres = here.map_or(f32::MAX, |me| distance(position, &me));
            consider(3, object.guid(), name, metres);
        }
        best.map(|(_, _, _, guid)| guid)
    }

    // -- Readiness ------------------------------------------------------------

    fn can_make_request(&self, ctx: &ItemContext<'_>) -> bool {
        ctx.active && self.transactions.can_begin_request() && !self.wield.is_busy()
    }

    /// One request at a time: refused, with the busy notice, while another
    /// request, a use or an auto-wield switch is in flight.
    pub(super) fn ensure_ready(&mut self, ctx: &ItemContext<'_>) -> bool {
        if self.can_make_request(ctx) {
            return true;
        }
        if self.transactions.has_pending_request()
            || self.transactions.item_busy_count() != 0
            || self.wield.is_busy()
        {
            self.notices
                .push(Notice::Interface(REQUEST_BUSY.to_owned()));
        }
        false
    }

    /// Reserve the request slot for `kind` on `item_id`, run `send`, and
    /// keep the reservation only when it reports a send.
    pub(super) fn dispatch(
        &mut self,
        kind: InventoryRequestKind,
        item_id: u32,
        ctx: &mut ItemContext<'_>,
        send: impl FnOnce(&mut ItemContext<'_>) -> bool,
    ) -> bool {
        let identity = ctx.world.item(item_id).map(ClientObject::identity);
        let mut tx = Vec::new();
        let sent = self
            .transactions
            .try_dispatch(kind, item_id, identity, || send(ctx), &mut tx);
        self.handle_transaction_events(tx, ctx);
        sent
    }

    fn dispatch_request(
        &mut self,
        kind: InventoryRequestKind,
        item_id: u32,
        ctx: &mut ItemContext<'_>,
        send: impl FnOnce(&mut ItemContext<'_>) -> bool,
    ) -> bool {
        item_id != 0 && self.ensure_ready(ctx) && self.dispatch(kind, item_id, ctx, send)
    }

    // -- Ownership and the rules' view of an object ---------------------------

    pub(super) fn is_owned_by_player(&self, item: u32, ctx: &ItemContext<'_>) -> bool {
        ctx.world.items().is_owned_by_object(item, ctx.player())
    }

    fn snapshot(item: &ClientObject, ctx: &ItemContext<'_>) -> ItemPolicyObject {
        policy_object(ctx.world.items(), item, ctx.player())
    }

    /// View `container` on the ground if it is described as openable. A
    /// locked one stays unviewed (its use has already gone to the server,
    /// which answers it with no text), and when `announce` the client says
    /// why itself. Returns whether the view was asked for.
    pub(super) fn attempt_set_ground_object(
        &mut self,
        container: u32,
        announce: bool,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let object = ctx
            .world
            .item(container)
            .map(|item| Self::snapshot(item, ctx));
        match interaction::decide_set_ground_object(object.as_ref()) {
            interaction::GroundObjectDecision::Open => {
                self.request_external_open(container, ctx);
                true
            }
            interaction::GroundObjectDecision::Refuse(message) => {
                if announce && let Some(message) = message {
                    self.notices.push(Notice::Interface(message));
                }
                false
            }
        }
    }
}

/// Whether the item `source` may be used on `target` (the target cursor's
/// valid or invalid), as the inventory judges a target pick.
#[must_use]
pub fn target_compatible(
    objects: &ClientObjectTable,
    player: u32,
    source: u32,
    target: u32,
) -> bool {
    let (Some(source), Some(target)) = (objects.get(source), objects.get(target)) else {
        return false;
    };
    source.useability.is_some()
        && interaction::is_target_compatible(
            &policy_object(objects, source, player),
            &policy_object(objects, target, player),
            player,
        )
}

/// An empty mana stone (no icon effect yet) destroys the item it drains, so
/// its use is asked first; a charged stone gives its mana without asking.
fn requires_mana_stone_confirmation(source: &ClientObject) -> bool {
    source.item_type.intersects(ItemType::MANA_STONE) && source.effects & 1 == 0
}

/// A retained item cannot be destroyed by a mana stone.
fn is_retained(item: &ClientObject) -> bool {
    item.flags().intersects(PublicWeenieFlags::RETAINED)
}

/// The rules' view of `item` for `player`, from the object table.
fn policy_object(
    objects: &ClientObjectTable,
    item: &ClientObject,
    player: u32,
) -> ItemPolicyObject {
    let mut flags = item.flags();
    if item.object_id() == player {
        flags |= PublicWeenieFlags::PLAYER;
    }
    let equipped = item.currently_equipped_location != EquipMask::NONE
        && (item.wielder_id == player || item.container_id == player);
    let owned = item.object_id() == player || is_carried_by(objects, item, player) || equipped;
    let stack_size = item.stack_size.max(1);
    ItemPolicyObject {
        id: item.object_id(),
        item_type: item.item_type,
        flags,
        container_id: item.container_id,
        wielder_id: item.wielder_id,
        valid_locations: item.valid_locations,
        current_location: item.currently_equipped_location,
        combat_use: item.combat_use.unwrap_or(0),
        items_capacity: item.items_capacity,
        containers_capacity: item.containers_capacity,
        useability: item.useability.unwrap_or(ItemUseable::UNDEF),
        target_type: item.target_type.unwrap_or(ItemType::NONE),
        owned_by_player: owned,
        is_container: item.container_type_hint != 0
            || item.item_type.intersects(ItemType::CONTAINER)
            || item.items_capacity > 0,
        is_component_pack: item.is_component_pack,
        trade_state: item.trade_state,
        stack_size,
        max_split_size: stack_size,
        is_in_3d_view: item.container_id == 0 && item.wielder_id == 0 && item.object_id() != player,
        name: item.appropriate_name().into_owned(),
    }
}

impl Inventory {
    pub(super) fn use_input(
        &self,
        item: &ClientObject,
        ctx: &ItemContext<'_>,
        bypass_classification: bool,
        selected_target: Option<ItemPolicyObject>,
    ) -> ItemUsePolicyInput {
        ItemUsePolicyInput {
            source: Self::snapshot(item, ctx),
            player_id: ctx.player(),
            ground_object_id: self.external.current_container_id(),
            ready_for_inventory_request: self.can_make_request(ctx),
            active_vendor_id: self.vendor.vendor_id(),
            bypass_classification,
            use_current_selection: selected_target.is_some(),
            selected_target,
            confirm_volatile_rare_uses: ctx.options.get(option_id::CONFIRM_VOLATILE_RARE_USE),
            in_non_combat_mode: ctx.in_peace,
        }
    }

    // -- Use ------------------------------------------------------------------

    /// The primary use of an object, as a double-click: pick it up, wield
    /// it, open it, use it, or (in target mode) use the waiting item on it.
    pub(super) fn activate_item(&mut self, item_id: u32, ctx: &mut ItemContext<'_>) -> bool {
        if item_id == 0 {
            return false;
        }
        if self.mode.source().is_some() {
            return self.offer_target(item_id, ctx);
        }
        let Some(item) = ctx.world.item(item_id).cloned() else {
            return false;
        };
        if !self.interaction.try_consume_use_throttle(ctx.now_ms) {
            return true;
        }
        if !self.ensure_ready(ctx) {
            return false;
        }
        let decision = interaction::decide_use(&self.use_input(&item, ctx, false, None));
        self.execute_use_actions(&decision.actions, ctx)
    }

    /// A plugin's use of a world object (OpenAC
    /// `RuntimeWorldObjectUse.TryUse`): a loose item is picked up into the
    /// pack; anything else is a use that goes out now within reach, or on
    /// arrival of a walk there. The ground container a corpse or chest
    /// opens is asked for when the use goes out, not before the walk (a
    /// walk given up never sent it), and giving the walk up is not said to
    /// the character.
    pub(super) fn use_world_object(&mut self, object: u32, ctx: &mut ItemContext<'_>) -> bool {
        let Some(item) = (object != 0)
            .then(|| ctx.world.item(object).cloned())
            .flatten()
        else {
            return false;
        };
        if !self.interaction.try_consume_use_throttle(ctx.now_ms) {
            return false;
        }
        if !self.ensure_ready(ctx) {
            return false;
        }
        let primary = interaction::determine_use_result(
            &Self::snapshot(&item, ctx),
            ctx.player(),
            self.external.current_container_id(),
        );
        // Never cuts in front of a walk already armed, a person's or an
        // earlier call's.
        if self.has_pending_use() || self.has_pending_pickup() {
            return false;
        }
        if primary == interaction::ItemPrimaryUseResult::PlaceInBackpack {
            return self.place_world_item_in_backpack(object, ctx);
        }
        // A game board out in the world is used by joining its game, the
        // same as when a person clicks it.
        if primary == interaction::ItemPrimaryUseResult::BeginGame {
            self.begun_games.push(object);
            return true;
        }
        self.request_use_for(object, false, ctx)
    }

    /// Use an item on a target (a key on a door, a kit on the player).
    pub(super) fn try_apply_item(
        &mut self,
        item_id: u32,
        target_id: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let (Some(item), Some(target)) = (
            ctx.world.item(item_id).cloned(),
            ctx.world.item(target_id).cloned(),
        ) else {
            return false;
        };
        if !self.interaction.try_consume_use_throttle(ctx.now_ms) {
            return true;
        }
        if !self.ensure_ready(ctx) {
            return false;
        }
        let target = Self::snapshot(&target, ctx);
        let decision = interaction::decide_use(&self.use_input(&item, ctx, true, Some(target)));
        decision
            .actions
            .iter()
            .any(|a| a.kind == ItemPolicyActionKind::SendUseWithTarget)
            && self.execute_use_actions(&decision.actions, ctx)
    }

    /// A click on `target_id` while a mode waits for one: the use mode uses
    /// it, the examine mode appraises it, the target mode uses the waiting
    /// item on it. The use and examine modes end with the click. False
    /// outside a mode (the click is the caller's).
    pub(super) fn offer_target(&mut self, target_id: u32, ctx: &mut ItemContext<'_>) -> bool {
        match self.mode {
            InteractionMode::None => false,
            InteractionMode::Use => {
                self.mode = InteractionMode::None;
                self.activate_item(target_id, ctx)
            }
            InteractionMode::Examine => {
                self.mode = InteractionMode::None;
                target_id != 0 && self.request_appraisal(target_id, AppraisalOrigin::User, ctx)
            }
            InteractionMode::UseItemOnTarget(_) => self.acquire_target(target_id, ctx),
        }
    }

    /// An item dropped into `container` at `slot` on an item window: part of
    /// a stack splits off into it; the whole of it goes as a placement shown
    /// waiting at that slot (a pickup when it is not the player's, else a
    /// move).
    pub(super) fn drop_into(
        &mut self,
        item_id: u32,
        container_id: u32,
        slot: i32,
        amount: u32,
        pickup: bool,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let Some(item) = ctx.world.item(item_id) else {
            return false;
        };
        if container_id == 0 || container_id == item_id {
            return false;
        }
        let full = item.stack_size.max(1).unsigned_abs();
        if amount != 0 && amount < full {
            let placement = slot.max(0).unsigned_abs();
            return self.dispatch_request(
                InventoryRequestKind::SplitToContainer,
                item_id,
                ctx,
                |ctx| {
                    ctx.send(|seq| {
                        build_stackable_split_to_container(
                            seq,
                            item_id,
                            container_id,
                            placement,
                            amount,
                        )
                    })
                },
            );
        }
        let kind = if pickup {
            InventoryRequestKind::Pickup
        } else {
            InventoryRequestKind::PutInContainer
        };
        self.dispatch_placement(item_id, container_id, slot, kind, ctx, |ctx| {
            ctx.send(|seq| build_put_item_in_container(seq, item_id, container_id, slot))
        })
    }

    /// A drop on a pack's icon joins the moving amount (`amount`, 0 for the
    /// whole stack) to the first stack in the pack with room for all of it,
    /// with a merge: settled before any room check, as joining takes no
    /// slot. `None` when no stack takes it (the drop goes on as a plain
    /// one); else whether the merge was sent.
    fn join_dropped_stack(
        &mut self,
        item_id: u32,
        container_id: u32,
        amount: u32,
        ctx: &mut ItemContext<'_>,
    ) -> Option<bool> {
        let full = ctx.world.item(item_id)?.stack_size.max(1).unsigned_abs();
        let requested = if amount == 0 { full } else { amount.min(full) };
        let join = self.plan_auto_merge_into(item_id, container_id, requested, ctx)?;
        let sent = self.dispatch_request(InventoryRequestKind::Merge, item_id, ctx, |ctx| {
            ctx.send(|seq| {
                build_stackable_merge(
                    seq,
                    join.source_object_id,
                    join.target_object_id,
                    join.amount,
                )
            })
        });
        if sent {
            self.merge_sent(join.source_object_id, join.target_object_id, ctx);
        }
        Some(sent)
    }

    /// Merge `amount` of one stack into another when the merge is legal
    /// (the same kind, room in the target, neither in a trade), wherever
    /// the source lies: a drop on a stack in a pack's grid merges a stack
    /// from the ground too, as the server moves a stack from anywhere the
    /// player may take it. False when the merge is not legal or could not
    /// be sent.
    fn merge_stacks(
        &mut self,
        source_id: u32,
        target_id: u32,
        amount: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let items = ctx.world.items();
        let (Some(source), Some(target)) = (items.get(source_id), items.get(target_id)) else {
            return false;
        };
        let requested = i32::try_from(amount).unwrap_or(i32::MAX);
        let Some(plan) = StackMergePlan::plan(
            &merge_item(source),
            &merge_item(target),
            self.can_make_request(ctx),
            requested,
        ) else {
            return false;
        };
        let sent = self.dispatch_request(InventoryRequestKind::Merge, source_id, ctx, |ctx| {
            ctx.send(|seq| {
                build_stackable_merge(
                    seq,
                    plan.source_object_id,
                    plan.target_object_id,
                    plan.amount,
                )
            })
        });
        if sent {
            self.merge_sent(plan.source_object_id, plan.target_object_id, ctx);
        }
        sent
    }

    /// An item dropped on the toolbar's backpack button: the player's goes
    /// into the preferred pack's top, shown waiting there; anything else is
    /// picked up into the main pack at once.
    pub(super) fn drop_on_backpack(&mut self, item_id: u32, ctx: &mut ItemContext<'_>) -> bool {
        let player = ctx.player();
        if player == 0 || item_id == 0 || ctx.world.item(item_id).is_none() {
            return false;
        }
        if self.is_owned_by_player(item_id, ctx) {
            let target = self.preferred_backpack_container(false, ctx);
            return self.dispatch_placement(
                item_id,
                target,
                0,
                InventoryRequestKind::PutInContainer,
                ctx,
                |ctx| ctx.send(|seq| build_put_item_in_container(seq, item_id, target, 0)),
            );
        }
        self.dispatch_request(InventoryRequestKind::Pickup, item_id, ctx, |ctx| {
            ctx.send(|seq| build_put_item_in_container(seq, item_id, player, 0))
        })
    }

    /// Reserve the one request for a placement of `item_id` into
    /// `container_id` at `placement`, shown waiting there from now on; its
    /// token, or `None` (with what the player is told) when another
    /// placement is under way or a request may not be made now.
    pub(super) fn begin_placement(
        &mut self,
        item_id: u32,
        container_id: u32,
        placement: i32,
        kind: InventoryRequestKind,
        ctx: &mut ItemContext<'_>,
    ) -> Option<u64> {
        if item_id == 0 || container_id == 0 {
            return None;
        }
        if self.placement.is_some() {
            self.report_placement_conflict(ctx.world);
            return None;
        }
        if !self.ensure_ready(ctx) {
            return None;
        }
        let identity = ctx.world.item(item_id).map(ClientObject::identity);
        let mut tx = Vec::new();
        let reserved = self
            .transactions
            .try_reserve(kind, item_id, identity, &mut tx);
        self.handle_transaction_events(tx, ctx);
        let reserved = reserved?;
        self.placement = Some(super::PendingPlacement {
            token: reserved.token,
            item_id,
            container_id,
            placement,
        });
        Some(reserved.token)
    }

    /// Send the request a placement reserved; a send that does not go out
    /// calls the placement off.
    pub(super) fn send_placement(
        &mut self,
        token: u64,
        kind: InventoryRequestKind,
        item_id: u32,
        ctx: &mut ItemContext<'_>,
        send: impl FnOnce(&mut ItemContext<'_>) -> bool,
    ) -> bool {
        let mut tx = Vec::new();
        let sent =
            self.transactions
                .try_dispatch_reserved(token, kind, item_id, || send(ctx), &mut tx);
        self.handle_transaction_events(tx, ctx);
        if !sent {
            self.cancel_placement(token);
        }
        sent
    }

    /// A placement made and sent at once.
    fn dispatch_placement(
        &mut self,
        item_id: u32,
        container_id: u32,
        placement: i32,
        kind: InventoryRequestKind,
        ctx: &mut ItemContext<'_>,
        send: impl FnOnce(&mut ItemContext<'_>) -> bool,
    ) -> bool {
        if !self.ensure_ready(ctx) {
            return false;
        }
        let Some(token) = self.begin_placement(item_id, container_id, placement, kind, ctx) else {
            return false;
        };
        self.send_placement(token, kind, item_id, ctx, send)
    }

    /// Put a shortcut on the bar: the request goes out and the bar holds
    /// it. A slot off the bar, or an entry naming neither an object nor a
    /// spell, is refused.
    pub(super) fn add_shortcut(&mut self, entry: ShortcutEntry, ctx: &mut ItemContext<'_>) -> bool {
        let on_bar = usize::try_from(entry.index).is_ok_and(|i| i < SHORTCUT_SLOTS);
        if !on_bar || (entry.object_id == 0 && entry.spell_id == 0) {
            return false;
        }
        let shortcut = ac_proto::Shortcut {
            index: entry.index,
            object_id: entry.object_id,
            spell_id: entry.spell_id,
        };
        ctx.send(|seq| build_add_shortcut(seq, &shortcut));
        self.shortcuts.set(entry);
        true
    }

    /// A loose item dropped on the toolbar's `slot`: it is picked up into
    /// the preferred pack as the use key picks it up, and once that request
    /// has gone out it gets the shortcut in `slot` (any other shortcut to it
    /// is taken off first). When the pickup does not go out (no room, a
    /// request already in flight) the bar is left as it is.
    pub(super) fn pick_up_to_shortcut(
        &mut self,
        item_id: u32,
        slot: i32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let on_bar = usize::try_from(slot).is_ok_and(|i| i < SHORTCUT_SLOTS);
        if item_id == 0 || !on_bar || ctx.world.item(item_id).is_none() {
            return false;
        }
        let acted = self.place_world_item_in_backpack(item_id, ctx);
        if self.backpack_placement != Some(BackpackPlacement::Sent) {
            return acted;
        }
        if let Some(existing) =
            (0..SHORTCUT_SLOTS as i32).find(|&s| self.shortcuts.get(s) == item_id)
        {
            self.remove_shortcut(existing, ctx);
        }
        self.add_shortcut(
            ShortcutEntry {
                index: slot,
                object_id: item_id,
                spell_id: 0,
            },
            ctx,
        )
    }

    /// Take the shortcut in `slot` off the bar.
    pub(super) fn remove_shortcut(&mut self, slot: i32, ctx: &mut ItemContext<'_>) -> bool {
        let Some(index) = u32::try_from(slot)
            .ok()
            .filter(|&i| (i as usize) < SHORTCUT_SLOTS)
        else {
            return false;
        };
        ctx.send(|seq| build_remove_shortcut(seq, index));
        self.shortcuts.remove(slot);
        true
    }

    /// A stack merge has just been sent, by any caller (a drag, a plugin,
    /// an automatic join): the poured stack's shortcut follows it, and the
    /// stack it joins becomes the selection ([`Inventory::take_merged_into`]).
    fn merge_sent(&mut self, source_id: u32, target_id: u32, ctx: &mut ItemContext<'_>) {
        self.replace_fully_merged_shortcut(source_id, target_id, ctx);
        self.merged_into.push(target_id);
    }

    /// After a stack merge was sent: the shortcut of the stack being poured
    /// moves to the stack it is poured into, keeping its slot, and the
    /// server is told of both changes. The poured stack's later removal then finds nothing
    /// of it on the bar.
    pub(super) fn replace_fully_merged_shortcut(
        &mut self,
        old_object_id: u32,
        new_object_id: u32,
        ctx: &mut ItemContext<'_>,
    ) {
        let plan = plan_full_stack_merge(&self.shortcuts.snapshot(), old_object_id, new_object_id);
        for mutation in plan {
            match mutation {
                ShortcutMutation::Remove(slot) => {
                    self.remove_shortcut(slot, ctx);
                }
                ShortcutMutation::Add(entry) => {
                    self.add_shortcut(entry, ctx);
                }
            }
        }
    }

    /// The server moved or removed `event`'s item: when it sits on the bar
    /// and the player no longer holds it (dropped, given, sold, put in a
    /// container that is not the player's, destroyed, used up), the first
    /// slot holding it is cleared and the server told, as a drag off the bar
    /// does. An item still held (moved between the player's own packs,
    /// wielded) keeps its slot. Only the server's word counts: a local
    /// projection of a drag may still be refused and put back, and a newer
    /// copy of the same object replacing the old one is not the item leaving.
    pub(super) fn follow_shortcut_ownership(
        &mut self,
        event: &TableEvent,
        ctx: &mut ItemContext<'_>,
    ) {
        let item = match event {
            TableEvent::Moved(moved) if moved.origin == MoveOrigin::AuthoritativeResponse => {
                moved.item_id
            }
            TableEvent::Removed { object, reason, .. }
                if *reason != RemovalReason::GenerationReplacement =>
            {
                object.object_id()
            }
            _ => return,
        };
        if item == 0 || ctx.player() == 0 || self.is_owned_by_player(item, ctx) {
            return;
        }
        let first = (0..SHORTCUT_SLOTS as i32).find(|&slot| self.shortcuts.get(slot) == item);
        if let Some(slot) = first {
            self.remove_shortcut(slot, ctx);
        }
    }

    /// Use the item waiting in target mode on `target_id`.
    fn acquire_target(&mut self, target_id: u32, ctx: &mut ItemContext<'_>) -> bool {
        let Some(source_id) = self.mode.source() else {
            return false;
        };
        self.mode = InteractionMode::None;
        let (Some(source), Some(target)) = (
            ctx.world.item(source_id).cloned(),
            ctx.world.item(target_id).cloned(),
        ) else {
            return false;
        };
        if source.useability.is_none() {
            return false;
        }
        // The pick says what it does, or why it cannot, before anything
        // else (a mana stone's question comes after the line).
        match interaction::use_on_target_line(
            &Self::snapshot(&source, ctx),
            &Self::snapshot(&target, ctx),
            ctx.player(),
        ) {
            Ok(line) => self.notices.push(Notice::Interface(line)),
            Err(failure) => {
                self.notices.push(Notice::Interface(failure));
                return false;
            }
        }
        // An empty mana stone destroys the item it drains: ask first, and
        // never for a retained item.
        if requires_mana_stone_confirmation(&source) {
            if is_retained(&target) {
                self.notices.push(Notice::Interface(
                    "You cannot drain the mana of this item because it is retained.".to_owned(),
                ));
                return false;
            }
            return self.execute_use_actions(
                &[ItemPolicyAction {
                    kind: ItemPolicyActionKind::ConfirmManaStoneDrain,
                    object_id: source_id,
                    target_id,
                    amount: 0,
                    message: Some(format!(
                        "Are you sure you want to attempt to destroy your {} and drain its mana into this stone?",
                        target.name
                    )),
                }],
                ctx,
            );
        }
        self.dispatch_acquired_target(source_id, target_id, ctx)
    }

    fn dispatch_acquired_target(
        &mut self,
        source: u32,
        target: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        if !self.ensure_ready(ctx) {
            return false;
        }
        self.dispatch_targeted_use(source, target, true, ctx)
    }

    /// The player said yes to an item-use question: the use goes out once
    /// the inventory may make a request (the gate is checked again).
    pub fn execute_confirmed_use(&mut self, object: u32, ctx: &mut ItemContext<'_>) -> bool {
        if object == 0 || !self.ensure_ready(ctx) {
            return false;
        }
        let sent = self.request_use(object, ctx);
        // An altar or a rare said yes to is always "Using the <name>".
        if let Some(name) = ctx.world.item(object).map(|i| i.appropriate_name()) {
            self.notices
                .push(Notice::Interface(format!("Using the {name}")));
        }
        sent
    }

    /// The player said yes to charging a mana stone from an item: the stone
    /// must still be empty and the item still a valid, unretained target
    /// (both may have changed while the question was open).
    pub fn execute_confirmed_mana_stone_drain(
        &mut self,
        stone: u32,
        target: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let still_valid = ctx
            .world
            .item(stone)
            .is_some_and(requires_mana_stone_confirmation)
            && ctx.world.item(target).is_some_and(|t| !is_retained(t))
            && target_compatible(ctx.world.items(), ctx.player(), stone, target);
        still_valid && self.dispatch_acquired_target(stone, target, ctx)
    }

    fn dispatch_targeted_use(
        &mut self,
        source: u32,
        target: u32,
        increment_busy: bool,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        if source == 0 || target == 0 || !ctx.send(|seq| build_use_with_target(seq, source, target))
        {
            return false;
        }
        self.interaction.note_use_dispatched(source, target);
        if increment_busy {
            let mut tx = Vec::new();
            self.transactions.increment_busy_count(&mut tx);
        }
        true
    }

    fn execute_use_actions(
        &mut self,
        actions: &[ItemPolicyAction],
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let mut acted = false;
        let mut busy_owned_by_use = false;
        for action in actions {
            use ItemPolicyActionKind as K;
            match action.kind {
                K::PlaceInBackpack => {
                    acted |= self.place_world_item_in_backpack(action.object_id, ctx);
                }
                K::WieldRight | K::WieldLeft | K::AutoSort => {
                    acted |= self.try_wield(action.object_id, EquipMask::NONE, ctx);
                }
                K::OpenContainedContainer => {
                    // A pack the character carries opens in the inventory
                    // window, and that asks nothing of the server: the use
                    // request a useable pack needs is its own SendUse
                    // action. The main pack is the character itself, which
                    // is not useable; asking the server to use it takes a
                    // busy count that some servers never release.
                    self.events
                        .push(InventoryEvent::OpenContainedContainer(action.object_id));
                    acted = true;
                }
                K::SendUse => {
                    // The use's reservation owns the busy count whether it
                    // went out, was armed for a walk or was refused.
                    acted |= self.request_use(action.object_id, ctx);
                    busy_owned_by_use = true;
                }
                K::SendUseWithTarget => {
                    acted |=
                        self.dispatch_targeted_use(action.object_id, action.target_id, false, ctx);
                }
                K::SetGroundObject => {
                    self.attempt_set_ground_object(action.object_id, true, ctx);
                    acted = true;
                }
                K::EnterTargetMode => {
                    self.mode = InteractionMode::UseItemOnTarget(action.object_id);
                    if let Some(name) = ctx
                        .world
                        .item(action.object_id)
                        .map(|i| i.name.clone())
                        .filter(|n| !n.trim().is_empty())
                    {
                        self.notices
                            .push(Notice::Interface(format!("Choose a target for the {name}")));
                    }
                    acted = true;
                }
                K::IncrementBusy => {
                    if busy_owned_by_use {
                        busy_owned_by_use = false;
                    } else {
                        let mut tx = Vec::new();
                        self.transactions.increment_busy_count(&mut tx);
                    }
                }
                K::Reject | K::Announce => {
                    if let Some(message) = action.message.as_ref().filter(|m| !m.trim().is_empty())
                    {
                        self.notices.push(Notice::Interface(message.clone()));
                    }
                }
                K::OpenSecureTrade => {
                    acted |= self.request_secure_trade(action.object_id, ctx);
                }
                K::OpenSalvage => {
                    self.events
                        .push(InventoryEvent::OpenSalvage(action.object_id));
                    acted = true;
                }
                K::BeginGame => {
                    self.begun_games.push(action.object_id);
                    acted = true;
                }
                K::ConfirmPlayerKillerSwitch
                | K::ConfirmNonPlayerKillerSwitch
                | K::ConfirmVolatileRare => {
                    use crate::communication::{ClientConfirmation, ItemUseConfirmation as Q};
                    let kind = match action.kind {
                        K::ConfirmPlayerKillerSwitch => Q::PlayerKillerSwitch,
                        K::ConfirmNonPlayerKillerSwitch => Q::NonPlayerKillerSwitch,
                        _ => Q::VolatileRare,
                    };
                    self.notices
                        .push(Notice::Confirm(ClientConfirmation::ItemUse {
                            kind,
                            object_id: action.object_id,
                        }));
                    acted = true;
                }
                K::ConfirmManaStoneDrain => {
                    self.notices.push(Notice::Confirm(
                        crate::communication::ClientConfirmation::ManaStoneDrain {
                            stone: action.object_id,
                            target: action.target_id,
                            message: action
                                .message
                                .clone()
                                .unwrap_or_else(|| policy_action_message(action.kind).to_owned()),
                        },
                    ));
                    acted = true;
                }
                _ => {
                    self.notices.push(Notice::Interface(
                        policy_action_message(action.kind).to_owned(),
                    ));
                    acted = true;
                }
            }
        }
        acted
    }

    // -- Placement ------------------------------------------------------------

    /// Pick up a loose item into the pack the player has open.
    pub(super) fn place_world_item_in_backpack(
        &mut self,
        item_id: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        self.place_world_item_in_pack(item_id, false, ctx)
    }

    /// The pack a pickup names first: the main pack when asked for or when
    /// the MainPackPreferred option is on, else the side pack a window
    /// shows open, else the main pack.
    fn preferred_backpack_container(&self, main_pack: bool, ctx: &ItemContext<'_>) -> u32 {
        let root = ctx.player();
        let target = if main_pack || ctx.options.get(option_id::MAIN_PACK_PREFERRED) {
            root
        } else {
            self.open_pack
        };
        if target == 0 { root } else { target }
    }

    /// Pick up a loose item: a quantity that joins a stack the character
    /// holds goes as that merge (it needs no free slot); otherwise into the
    /// preferred pack, else the main pack, else the first side pack with
    /// room; with no room anywhere the "full" notice is shown and nothing
    /// is sent.
    pub(super) fn place_world_item_in_pack(
        &mut self,
        item_id: u32,
        main_pack: bool,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let root = ctx.player();
        if item_id == 0 || root == 0 {
            return false;
        }
        let target = self.preferred_backpack_container(main_pack, ctx);
        let chosen =
            placement::choose_container(ctx.world.items(), item_id, root, target, root, true);
        if let Some(merge) = self.plan_auto_merge(item_id, ctx) {
            // The joined stack's pack is where the quantity is shown
            // waiting (the main pack when that pack is not known).
            let holding = ctx
                .world
                .item(merge.target_object_id)
                .map_or(0, |t| t.container_id);
            let shown_in = match chosen {
                Ok(container) => container,
                Err(_) if holding != 0 => holding,
                Err(_) => root,
            };
            let sent = self.dispatch_placement(
                item_id,
                shown_in,
                0,
                InventoryRequestKind::Merge,
                ctx,
                |ctx| {
                    ctx.send(|seq| {
                        build_stackable_merge(
                            seq,
                            merge.source_object_id,
                            merge.target_object_id,
                            merge.amount,
                        )
                    })
                },
            );
            self.backpack_placement = Some(if sent {
                BackpackPlacement::Sent
            } else {
                BackpackPlacement::NotDispatched
            });
            if sent {
                self.merge_sent(merge.source_object_id, merge.target_object_id, ctx);
            }
            return sent;
        }
        let container = match chosen {
            Ok(container) => container,
            Err(rejection) => {
                let items = ctx.world.items();
                let text = placement::placement_rejection_text(
                    rejection,
                    items.get(item_id),
                    items.get(root),
                    root,
                );
                self.notices.push(Notice::Interface(text));
                self.backpack_placement = Some(BackpackPlacement::NoRoom);
                return true;
            }
        };
        let Some(token) =
            self.begin_placement(item_id, container, 0, InventoryRequestKind::Pickup, ctx)
        else {
            self.backpack_placement = Some(BackpackPlacement::AlreadyPending);
            return false;
        };
        let sent = self.send_pickup(item_id, container, 0, token, ctx);
        self.backpack_placement = Some(if sent {
            BackpackPlacement::Sent
        } else {
            BackpackPlacement::NotDispatched
        });
        sent
    }

    /// The merge of the whole picked-up stack into a stack the character
    /// holds; a merge that would take only part is not one.
    fn plan_auto_merge(&self, source_id: u32, ctx: &ItemContext<'_>) -> Option<StackMergePlan> {
        let requested = ctx.world.item(source_id)?.stack_size.max(1).unsigned_abs();
        let player = ctx.player();
        if player == 0 {
            return None;
        }
        self.plan_auto_merge_into(source_id, player, requested, ctx)
    }

    /// The first stack in `container_id` that can take all `requested` of
    /// the source in one merge, searching the container's own items first
    /// and then each pack inside it; a stack that could take only part of
    /// it is passed over.
    fn plan_auto_merge_into(
        &self,
        source_id: u32,
        container_id: u32,
        requested: u32,
        ctx: &ItemContext<'_>,
    ) -> Option<StackMergePlan> {
        plan_join_stack(
            ctx.world.items(),
            source_id,
            container_id,
            requested,
            self.can_make_request(ctx),
        )
    }

    /// Put an owned item into an owned container (or the main pack); a
    /// quantity short of the whole stack splits it off. With `join_stack`
    /// a stack in the container that takes all of it is joined first, by a
    /// merge: that is settled before any room check, joining takes no slot.
    pub(super) fn try_move_item(
        &mut self,
        item_id: u32,
        container_id: u32,
        amount: u32,
        slot: i32,
        join_stack: bool,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let player = ctx.player();
        let Some(item) = ctx.world.item(item_id) else {
            return false;
        };
        if item_id == 0
            || container_id == 0
            || !self.is_owned_by_player(item_id, ctx)
            || (container_id != player && !self.is_owned_by_player(container_id, ctx))
        {
            return false;
        }
        let full = item.stack_size.max(1).unsigned_abs();
        let requested = if amount == 0 { full } else { amount };
        if requested > full {
            return false;
        }
        if join_stack
            && let Some(join) = self.plan_auto_merge_into(item_id, container_id, requested, ctx)
        {
            let sent = self.dispatch_request(InventoryRequestKind::Merge, item_id, ctx, |ctx| {
                ctx.send(|seq| {
                    build_stackable_merge(
                        seq,
                        join.source_object_id,
                        join.target_object_id,
                        join.amount,
                    )
                })
            });
            if sent {
                self.merge_sent(join.source_object_id, join.target_object_id, ctx);
            }
            return sent;
        }
        if requested < full {
            let placement = slot.max(0).unsigned_abs();
            return self.dispatch_request(
                InventoryRequestKind::SplitToContainer,
                item_id,
                ctx,
                |ctx| {
                    ctx.send(|seq| {
                        build_stackable_split_to_container(
                            seq,
                            item_id,
                            container_id,
                            placement,
                            requested,
                        )
                    })
                },
            );
        }
        self.dispatch_request(InventoryRequestKind::PutInContainer, item_id, ctx, |ctx| {
            ctx.send(|seq| build_put_item_in_container(seq, item_id, container_id, slot))
        })
    }

    /// Merge `amount` (0 for as much as fits) of one owned stack into
    /// another.
    pub(super) fn try_merge_items(
        &mut self,
        source_id: u32,
        target_id: u32,
        amount: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        if !self.is_owned_by_player(source_id, ctx) || !self.is_owned_by_player(target_id, ctx) {
            return false;
        }
        self.merge_stacks(source_id, target_id, amount, ctx)
    }

    /// Drop an owned item (or part of a stack) on the ground.
    pub(super) fn try_drop_item(
        &mut self,
        item_id: u32,
        amount: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        if !self.is_owned_by_player(item_id, ctx) {
            return false;
        }
        let Some(item) = ctx.world.item(item_id) else {
            return false;
        };
        let full = item.stack_size.max(1).unsigned_abs();
        let requested = if amount == 0 { full } else { amount };
        if requested > full {
            return false;
        }
        if requested < full {
            return self.dispatch_request(
                InventoryRequestKind::SplitToWorld,
                item_id,
                ctx,
                |ctx| ctx.send(|seq| build_stackable_split_to_3d(seq, item_id, requested)),
            );
        }
        self.dispatch_request(InventoryRequestKind::DropToWorld, item_id, ctx, |ctx| {
            ctx.send(|seq| build_drop_item(seq, item_id))
        })
    }

    /// Give an owned item (or part of a stack) to another object.
    pub(super) fn try_give_item(
        &mut self,
        item_id: u32,
        target_id: u32,
        amount: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        if target_id == 0
            || target_id == ctx.player()
            || ctx.world.item(target_id).is_none()
            || !self.is_owned_by_player(item_id, ctx)
        {
            return false;
        }
        let Some(item) = ctx.world.item(item_id) else {
            return false;
        };
        let full = item.stack_size.max(1).unsigned_abs();
        let requested = if amount == 0 { full } else { amount };
        if requested > full {
            return false;
        }
        self.dispatch_request(InventoryRequestKind::Give, item_id, ctx, |ctx| {
            ctx.send(|seq| build_give_object(seq, target_id, item_id, requested))
        })
    }

    // -- Appraisal ------------------------------------------------------------

    /// Asks the server to appraise an object for something acting for the
    /// player, without its answer becoming the appraisal the player looks
    /// at (a walk checking whether a door is locked). False when the client
    /// would not ask.
    pub fn appraise_quietly(
        &mut self,
        object_id: u32,
        now_ms: i64,
        active: bool,
        outbox: &mut crate::session::Outbox,
    ) -> bool {
        if object_id == 0 || !active {
            return false;
        }
        let mut tx = Vec::new();
        let Self {
            interaction,
            transactions,
            ..
        } = self;
        interaction.try_request_appraisal(
            object_id,
            AppraisalOrigin::Automation,
            now_ms,
            transactions,
            &mut tx,
            |id| {
                outbox.send_action(|seq| build_appraise(seq, id));
            },
        )
    }

    pub(super) fn request_appraisal(
        &mut self,
        object_id: u32,
        origin: AppraisalOrigin,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        if object_id == 0 || !ctx.active {
            return false;
        }
        let mut tx = Vec::new();
        let Self {
            interaction,
            transactions,
            ..
        } = self;
        interaction.try_request_appraisal(
            object_id,
            origin,
            ctx.now_ms,
            transactions,
            &mut tx,
            |id| {
                ctx.send(|seq| build_appraise(seq, id));
            },
        )
    }

    // -- Vendor ---------------------------------------------------------------

    fn try_buy(
        &mut self,
        vendor: u32,
        items: &[ItemAmount],
        alternate_currency: u32,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        if vendor == 0 || items.is_empty() || items.iter().any(|i| i.amount <= 0) {
            return false;
        }
        self.ensure_ready(ctx)
            && self.dispatch_shop(vendor, ctx, |ctx| {
                ctx.send(|seq| build_buy(seq, vendor, items, alternate_currency))
            })
    }

    fn try_sell(&mut self, vendor: u32, items: &[ItemAmount], ctx: &mut ItemContext<'_>) -> bool {
        if vendor == 0 || items.is_empty() {
            return false;
        }
        self.ensure_ready(ctx)
            && self.dispatch_shop(vendor, ctx, |ctx| {
                ctx.send(|seq| build_sell(seq, vendor, items))
            })
    }

    /// A shop request: the request slot names the vendor (the next listing
    /// completes it) and the send counts as a use until the use-done.
    fn dispatch_shop(
        &mut self,
        vendor: u32,
        ctx: &mut ItemContext<'_>,
        send: impl FnOnce(&mut ItemContext<'_>) -> bool,
    ) -> bool {
        let mut tx = Vec::new();
        let Some(reserved) =
            self.transactions
                .try_reserve(InventoryRequestKind::Shop, vendor, None, &mut tx)
        else {
            return false;
        };
        let mut reservation = self.transactions.begin_use_request_reservation(&mut tx);
        let sent = send(ctx);
        if sent {
            self.transactions.mark_use_dispatched(&mut reservation);
        } else {
            self.transactions
                .cancel_use_before_dispatch(&mut reservation, &mut tx);
        }
        let dispatched = self.transactions.try_dispatch_reserved(
            reserved.token,
            InventoryRequestKind::Shop,
            vendor,
            || sent,
            &mut tx,
        );
        self.handle_transaction_events(tx, ctx);
        dispatched
    }

    // -- The vendor window ------------------------------------------------------

    fn run_vendor(&mut self, command: &VendorCommand, ctx: &mut ItemContext<'_>) -> bool {
        use VendorCommand as V;
        let vendor = self.vendor.vendor_id();
        if vendor == 0 {
            self.events
                .push(InventoryEvent::Refused("No vendor is open.".to_owned()));
            return false;
        }
        match command {
            V::Show => {
                self.vendor_view_event(ctx.world, VendorViewReason::Asked);
                true
            }
            V::Close => {
                self.close_vendor(ctx);
                true
            }
            V::AddToBuyList { item, amount } => {
                let Some(guid) = self.resolve(item, ctx) else {
                    return false;
                };
                let Some(listing) = self.vendor.items().iter().find(|i| i.item_guid == guid) else {
                    self.events.push(InventoryEvent::Refused(
                        "That is not for sale here.".to_owned(),
                    ));
                    return false;
                };
                let quantity = if *amount > 0 {
                    *amount
                } else {
                    shop::buy_quantity(listing)
                };
                if let Some(text) = self.lists.add_to_buy(guid, quantity) {
                    self.interface(text);
                }
                self.vendor_view_event(ctx.world, VendorViewReason::Lists);
                true
            }
            V::RemoveFromBuyList { item, amount } => {
                let Some(guid) = self.resolve(item, ctx) else {
                    return false;
                };
                let removed = self.lists.remove_from_buy(guid, *amount);
                if removed {
                    self.vendor_view_event(ctx.world, VendorViewReason::Lists);
                }
                removed
            }
            V::ClearBuyList => {
                self.lists.clear_buy();
                self.vendor_view_event(ctx.world, VendorViewReason::Lists);
                true
            }
            V::BuyAll => {
                if self.lists.buy().is_empty() {
                    return false;
                }
                match self.lists.buy_all_request(&self.vendor, ctx.world) {
                    Err(text) => {
                        self.interface(text);
                        false
                    }
                    Ok(rows) => {
                        let items: Vec<ItemAmount> =
                            rows.iter().map(|&(n, guid)| amount(guid, n)).collect();
                        let currency = self.vendor.profile().alternate_currency_wcid;
                        let sent = self.try_buy(vendor, &items, currency, ctx);
                        if sent {
                            self.lists.bought_all(&self.vendor);
                            self.vendor_view_event(ctx.world, VendorViewReason::Lists);
                        }
                        sent
                    }
                }
            }
            V::AddToSellList(item) => {
                let Some(guid) = self.resolve(item, ctx) else {
                    return false;
                };
                let mut notices = Vec::new();
                let staged = self
                    .lists
                    .add_to_sell(guid, &self.vendor, ctx.world, &mut notices);
                for text in notices {
                    self.interface(text);
                }
                if staged {
                    self.vendor_view_event(ctx.world, VendorViewReason::Lists);
                }
                staged
            }
            V::RemoveFromSellList(item) => {
                let Some(guid) = self.resolve(item, ctx) else {
                    return false;
                };
                let removed = self.lists.remove_from_sell(guid, ctx.world);
                if removed {
                    self.vendor_view_event(ctx.world, VendorViewReason::Lists);
                }
                removed
            }
            V::ClearSellList => {
                self.lists.clear_sell(ctx.world);
                self.vendor_view_event(ctx.world, VendorViewReason::Lists);
                true
            }
            V::SellAll => {
                let rows = self.lists.sell_all_request();
                if rows.is_empty() {
                    return false;
                }
                let items: Vec<ItemAmount> =
                    rows.iter().map(|&(n, guid)| amount(guid, n)).collect();
                let sent = self.try_sell(vendor, &items, ctx);
                if sent {
                    self.lists.sold_all(ctx.world);
                    self.vendor_view_event(ctx.world, VendorViewReason::Lists);
                }
                sent
            }
        }
    }

    /// `/fillcomps`: fill the buy list from the component book's desired
    /// amounts, `describe` naming each component's category and name.
    pub fn fill_components(
        &mut self,
        category: u32,
        maximum_price: u32,
        describe: impl Fn(u32) -> Option<ac_core::items::component_fill::ComponentDescription>,
        ctx: &mut ItemContext<'_>,
    ) {
        use ac_core::items::component_fill::{build_component_desires, plan_component_fill};
        if self.vendor.vendor_id() == 0 {
            return;
        }
        let player = ctx.player();
        let desires = match ctx.world.spellbook() {
            Some(book) => build_component_desires(
                book.desired_components(),
                ctx.world.items(),
                player,
                describe,
                self.vendor.items(),
            ),
            None => Vec::new(),
        };
        let plan = plan_component_fill(
            &desires,
            category,
            maximum_price.cast_signed(),
            self.vendor.items(),
            self.vendor.profile(),
            self.lists.buy().entries(),
        );
        for text in self.lists.apply_component_fill(&plan) {
            self.interface(text);
        }
        self.vendor_view_event(ctx.world, VendorViewReason::ComponentsFilled);
    }

    // -- Trade ----------------------------------------------------------------

    /// Ask a player to trade (nothing when a trade with them is open).
    fn request_secure_trade(&mut self, partner: u32, ctx: &mut ItemContext<'_>) -> bool {
        let is_player = partner != 0
            && partner != ctx.player()
            && ctx
                .world
                .item(partner)
                .is_some_and(|p| p.flags().intersects(PublicWeenieFlags::PLAYER));
        if !is_player {
            return false;
        }
        let trade = self.trade.view();
        if trade.open && trade.partner_guid == partner {
            return true;
        }
        ctx.send(|seq| build_open_trade_negotiations(seq, partner))
    }
}

fn merge_item(item: &ClientObject) -> StackMergeItem {
    StackMergeItem {
        object_id: item.object_id(),
        weenie_class_id: item.weenie_class_id,
        stack_size: item.stack_size,
        max_stack_size: item.stack_size_max,
        trade_state: item.trade_state,
    }
}

/// The stack a moving stack joins when it is put into `container_id` with
/// joining asked for (a drop on a pack's icon, a pickup into the backpack,
/// a move with the join flag): the first stack of the same thing, among the
/// container's own items and then each pack inside it, with room for all
/// `requested` of `source_id`, as the merge that joins it. A stack with room
/// for only part of it is passed over, so the move stays whole.
#[must_use]
pub fn plan_join_stack(
    objects: &ClientObjectTable,
    source_id: u32,
    container_id: u32,
    requested: u32,
    ready: bool,
) -> Option<StackMergePlan> {
    let source = objects.get(source_id)?;
    if container_id == 0 || source.stack_size_max <= 1 {
        return None;
    }
    let requested_amount = i32::try_from(requested).unwrap_or(i32::MAX);
    let source = merge_item(source);
    exhaustive_contained_items(objects, container_id)
        .into_iter()
        .find_map(|target_id| {
            let target = objects.get(target_id)?;
            StackMergePlan::plan(&source, &merge_item(target), ready, requested_amount)
                .filter(|plan| plan.amount == requested)
        })
}

/// Everything a container holds, for finding a stack to join: its own items
/// in order, then the items of each pack inside it, pack by pack (each
/// container visited once).
fn exhaustive_contained_items(objects: &ClientObjectTable, container_id: u32) -> Vec<u32> {
    fn walk(
        objects: &ClientObjectTable,
        container_id: u32,
        visited: &mut BTreeSet<u32>,
        out: &mut Vec<u32>,
    ) {
        if !visited.insert(container_id) {
            return;
        }
        let mut packs = Vec::new();
        for &id in objects.get_contents(container_id) {
            if objects.get(id).is_some_and(placement::is_container) {
                packs.push(id);
            } else {
                out.push(id);
            }
        }
        for pack in packs {
            walk(objects, pack, visited, out);
        }
    }
    let mut out = Vec::new();
    walk(objects, container_id, &mut BTreeSet::new(), &mut out);
    out
}

fn is_carried_by(objects: &ClientObjectTable, item: &ClientObject, player: u32) -> bool {
    let mut container = item.container_id;
    for _ in 0..8 {
        if container == 0 {
            return false;
        }
        if container == player {
            return true;
        }
        container = objects.get(container).map_or(0, |c| c.container_id);
    }
    false
}

/// What the client says for a step this client has no window for.
fn policy_action_message(kind: ItemPolicyActionKind) -> &'static str {
    use ItemPolicyActionKind as K;
    match kind {
        K::ConfirmPlayerKillerSwitch => "Confirm using this Player Killer altar before continuing.",
        K::ConfirmNonPlayerKillerSwitch => {
            "Confirm using this Non-Player Killer altar before continuing."
        }
        K::ConfirmVolatileRare => "Confirm using this volatile rare before continuing.",
        K::ConfirmManaStoneDrain => {
            "Confirm destroying the item to charge the mana stone before continuing."
        }
        K::OpenSecureTrade | K::StartSecureTrade => "Secure trade is not open.",
        K::OpenSalvage => "Open the salvage panel to use that item.",
        _ => "That item action is not available here.",
    }
}
