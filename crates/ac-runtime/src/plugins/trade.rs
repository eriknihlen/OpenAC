//! API group H: secure trade and vendors (OpenAC `RuntimeTradeAutomation`,
//! `RuntimeVendorAutomation`) over the inventory owner's trade and vendor
//! state. The plugin's vendor buy and sell lists are its own, apart from
//! the vendor window's, as in OpenAC.

use std::collections::HashSet;

use ac_core::items::InventoryRequestKind;
use ac_core::items::vendor::{per_unit_value, sell_price};
use ac_plugin::abi::Event;
use ac_plugin::abi::trade::{
    TradeEvent, TradeResult, TradeState, TradeStatus, VendorEvent, VendorItem, VendorProfile,
    VendorResult, VendorState, VendorStatus, VendorTransaction, VendorTransactionKind,
};
use ac_plugin::abi::world::ItemProperties;

use super::world::properties_of;
use super::{PluginBinding, Ran};
use crate::inventory::{ItemCommand, ObjectRef};
use crate::session::{SessionCommand, SessionState};

/// What the trade and vendor events were last reported from.
#[derive(Debug, Default)]
pub struct TradeWatch {
    open: bool,
    partner: u32,
    partner_accepted: bool,
    revision: u64,
    self_items: HashSet<u32>,
    partner_items: HashSet<u32>,
    vendor: u32,
    /// The plugin buy list: (listing, count).
    buy_list: Vec<(u32, i32)>,
    /// The plugin sell list.
    sell_list: Vec<u32>,
    /// The transaction awaiting its answer.
    pending: Option<VendorTransactionKind>,
    /// The inventory request answers already taken in.
    request_revision: u64,
}

impl TradeWatch {
    fn reset_trade(&mut self) {
        self.self_items.clear();
        self.partner_items.clear();
        self.partner_accepted = false;
    }
}

/// The trade and vendor events of this pass (OpenAC
/// `RuntimeTradeAutomation.Poll`, `RuntimeVendorAutomation.Poll` and its
/// vendor observer): a partner swap is a close then an open; an accepted
/// partner and newly offered items once each; a vendor opening clears the
/// plugin's lists, closing clears them and drops a pending transaction;
/// a shop request's answer ends the pending transaction.
pub fn follow(state: &mut SessionState) -> Vec<Event> {
    let player = state.world.player_guid().unwrap_or(0);
    let view = state.inventory.trade().clone();
    let vendor = state.inventory.vendor().vendor_id();
    let completions = *state.inventory.completions();
    let watch = &mut state.plugin_services.trade;
    let mut out = Vec::new();
    let opened = |partner| Event::Trade(TradeEvent::Opened((player, partner)));
    if view.open && !watch.open {
        watch.open = true;
        watch.partner = view.partner_guid;
        watch.reset_trade();
        out.push(opened(view.partner_guid));
    } else if !view.open && watch.open {
        watch.open = false;
        watch.partner = 0;
        watch.reset_trade();
        out.push(Event::Trade(TradeEvent::Closed));
    } else if view.open && view.partner_guid != watch.partner {
        watch.partner = view.partner_guid;
        watch.reset_trade();
        out.push(Event::Trade(TradeEvent::Closed));
        out.push(opened(view.partner_guid));
    }
    if view.revision != watch.revision {
        watch.revision = view.revision;
        if view.partner_accepted && !watch.partner_accepted {
            out.push(Event::Trade(TradeEvent::PartnerAccepted(view.partner_guid)));
        }
        watch.partner_accepted = view.partner_accepted;
        for &item in &view.self_items {
            if !watch.self_items.contains(&item) {
                out.push(Event::Trade(TradeEvent::ItemAdded((item, true))));
            }
        }
        for &item in &view.partner_items {
            if !watch.partner_items.contains(&item) {
                out.push(Event::Trade(TradeEvent::ItemAdded((item, false))));
            }
        }
        watch.self_items = view.self_items.iter().copied().collect();
        watch.partner_items = view.partner_items.iter().copied().collect();
    }
    if vendor != watch.vendor {
        if watch.vendor != 0 {
            out.push(Event::Vendor(VendorEvent::Closed));
            watch.pending = None;
        }
        watch.buy_list.clear();
        watch.sell_list.clear();
        if vendor != 0 {
            out.push(Event::Vendor(VendorEvent::Opened(vendor)));
        }
        watch.vendor = vendor;
    }
    if completions.request_revision != watch.request_revision {
        watch.request_revision = completions.request_revision;
        if completions.request_kind == Some(InventoryRequestKind::Shop)
            && let Some(kind) = watch.pending.take()
        {
            let error = completions.request_error;
            out.push(Event::Vendor(VendorEvent::TransactionCompleted(
                VendorTransaction {
                    kind,
                    success: error == 0,
                    notice: (error != 0)
                        .then(|| format!("Vendor transaction failed (weenie error {error}).")),
                },
            )));
        }
    }
    out
}

impl PluginBinding<'_> {
    fn trade_command(&mut self, command: ItemCommand) -> TradeResult {
        let answer = |status, notice| TradeResult { status, notice };
        match self.run(SessionCommand::Item(command)) {
            Ran::NoConnection => answer(TradeStatus::Unavailable, None),
            Ran::Refused(why) => answer(TradeStatus::Refused, Some(why)),
            Ran::Done => answer(TradeStatus::Sent, None),
        }
    }

    fn trade_gate(&self) -> Option<TradeResult> {
        let status = if !self.in_world() {
            TradeStatus::Unavailable
        } else if !self.state.inventory.trade().open {
            TradeStatus::NotOpen
        } else {
            return None;
        };
        Some(TradeResult {
            status,
            notice: None,
        })
    }

    /// The secure trade window.
    pub(super) fn trade_state(&self) -> TradeState {
        let view = self.state.inventory.trade_view(&self.state.world);
        TradeState {
            is_available: self.facts.in_world,
            is_open: view.open,
            partner_id: view.partner_guid,
            partner_name: if view.partner_guid == 0 {
                String::new()
            } else {
                view.partner_name.unwrap_or_default()
            },
            my_items: view.self_items,
            partner_items: view.partner_items,
            my_accepted: view.self_accepted,
            partner_accepted: view.partner_accepted,
        }
    }

    /// Offer an item (OpenAC `Trade.Add`).
    pub(super) fn trade_add(&mut self, item: u32) -> TradeResult {
        if let Some(refused) = self.trade_gate() {
            return refused;
        }
        if item == 0 {
            return TradeResult {
                status: TradeStatus::InvalidItem,
                notice: None,
            };
        }
        self.trade_command(ItemCommand::AddToTrade(ObjectRef::Guid(item)))
    }

    /// Accept the trade; a second accept is not sent again (OpenAC
    /// `Trade.Accept`).
    pub(super) fn trade_accept(&mut self) -> TradeResult {
        if let Some(refused) = self.trade_gate() {
            return refused;
        }
        if self.state.inventory.trade().self_accepted {
            return TradeResult {
                status: TradeStatus::AlreadyAccepted,
                notice: None,
            };
        }
        self.trade_command(ItemCommand::AcceptTrade)
    }

    /// Withdraw the acceptance.
    pub(super) fn trade_decline(&mut self) -> TradeResult {
        self.trade_gate()
            .unwrap_or_else(|| self.trade_command(ItemCommand::DeclineTrade))
    }

    /// Clear both sides.
    pub(super) fn trade_reset(&mut self) -> TradeResult {
        self.trade_gate()
            .unwrap_or_else(|| self.trade_command(ItemCommand::ResetTrade))
    }

    /// End the trade.
    pub(super) fn trade_end(&mut self) -> TradeResult {
        self.trade_gate()
            .unwrap_or_else(|| self.trade_command(ItemCommand::CloseTrade))
    }

    /// The open vendor, priced at its rate, and the plugin's lists (OpenAC
    /// `Vendor.Items`, `Profile`, `BuyList`, `SellList`).
    pub(super) fn vendor_state(&self) -> VendorState {
        let vendor = self.state.inventory.vendor();
        let id = vendor.vendor_id();
        let watch = &self.state.plugin_services.trade;
        if id == 0 {
            return VendorState {
                is_available: self.facts.in_world,
                profile: VendorProfile {
                    minimum_value: u32::MAX,
                    maximum_value: u32::MAX,
                    ..VendorProfile::default()
                },
                ..VendorState::default()
            };
        }
        let profile = vendor.profile();
        let items = vendor
            .items()
            .iter()
            .map(|item| {
                let item_type = item.item_type.unwrap_or(0);
                let per_unit = per_unit_value(item.value.unwrap_or(0), item.desc_stack_size);
                VendorItem {
                    template_id: item.item_guid,
                    weenie_class_id: item.weenie_class_id,
                    name: item.name.clone().unwrap_or_default(),
                    object_class: ac_plugin::abi::world::classify(
                        item_type,
                        item.public_weenie_bitfield.unwrap_or(0),
                    ),
                    unit_price: sell_price(per_unit, item_type, profile.sell_price, 1),
                    stack_size: item.stack_size,
                    max_stack_size: item.max_stack_size.unwrap_or(1),
                    item_type,
                }
            })
            .collect();
        let alternate = profile.alternate_currency_wcid != 0;
        VendorState {
            is_available: self.facts.in_world,
            is_open: true,
            vendor_id: id,
            vendor_name: self.state.world.name(id).unwrap_or_default().to_owned(),
            items,
            profile: VendorProfile {
                buy_rate: profile.buy_price,
                deals_in_item_types: profile.merchandise_item_types,
                minimum_value: profile.merchandise_min_value,
                maximum_value: profile.merchandise_max_value,
                deals_in_magical_items: profile.deal_magical_items,
                alternate_currency_weenie_class_id: profile.alternate_currency_wcid,
                alternate_currency_amount: if alternate {
                    profile.alternate_currency_amount
                } else {
                    0
                },
                alternate_currency_name: alternate
                    .then(|| profile.alternate_currency_plural_name.clone()),
            },
            is_busy: watch.pending.is_some(),
            buy_list: watch.buy_list.clone(),
            sell_list: watch.sell_list.clone(),
        }
    }

    /// The properties of an item the client holds a record for (a vendor's
    /// listing among them).
    pub(super) fn vendor_properties(&self, template: u32) -> Option<ItemProperties> {
        self.state.world.item(template).map(properties_of)
    }

    fn vendor_answer(status: VendorStatus) -> VendorResult {
        VendorResult {
            status,
            notice: None,
        }
    }

    fn vendor_lists_template(&self, template: u32) -> bool {
        self.state
            .inventory
            .vendor()
            .items()
            .iter()
            .any(|i| i.item_guid == template)
    }

    fn vendor_open(&self) -> Option<VendorResult> {
        if !self.in_world() {
            return Some(Self::vendor_answer(VendorStatus::Unavailable));
        }
        if self.state.inventory.vendor().vendor_id() == 0 {
            return Some(Self::vendor_answer(VendorStatus::NotOpen));
        }
        None
    }

    /// Add a listing to the buy list; one already there adds up (OpenAC
    /// `AddToBuyList`).
    pub(super) fn vendor_add_buy(&mut self, (template, count): (u32, i32)) -> VendorResult {
        if let Some(refused) = self.vendor_open() {
            return refused;
        }
        if template == 0 || count <= 0 || !self.vendor_lists_template(template) {
            return Self::vendor_answer(VendorStatus::InvalidItem);
        }
        let list = &mut self.state.plugin_services.trade.buy_list;
        match list.iter_mut().find(|(t, _)| *t == template) {
            Some(entry) => entry.1 += count,
            None => list.push((template, count)),
        }
        Self::vendor_answer(VendorStatus::Sent)
    }

    /// Add an owned, unworn item that is not a listing to the sell list
    /// (OpenAC `AddToSellList`).
    pub(super) fn vendor_add_sell(&mut self, item_id: u32) -> VendorResult {
        if let Some(refused) = self.vendor_open() {
            return refused;
        }
        let player = self.state.world.player_guid().unwrap_or(0);
        let items = self.state.world.items();
        let valid = item_id != 0
            && items.get(item_id).is_some_and(|item| {
                item.currently_equipped_location.bits() == 0
                    && items.is_owned_by_object(item_id, player)
            })
            && !self.vendor_lists_template(item_id);
        if !valid {
            return Self::vendor_answer(VendorStatus::InvalidItem);
        }
        let list = &mut self.state.plugin_services.trade.sell_list;
        if !list.contains(&item_id) {
            list.push(item_id);
        }
        Self::vendor_answer(VendorStatus::Sent)
    }

    /// Take a listing off the buy list.
    pub(super) fn vendor_remove_buy(&mut self, template: u32) -> VendorResult {
        if !self.in_world() {
            return Self::vendor_answer(VendorStatus::Unavailable);
        }
        self.state
            .plugin_services
            .trade
            .buy_list
            .retain(|(t, _)| *t != template);
        Self::vendor_answer(VendorStatus::Sent)
    }

    /// Take an item off the sell list.
    pub(super) fn vendor_remove_sell(&mut self, item_id: u32) -> VendorResult {
        if !self.in_world() {
            return Self::vendor_answer(VendorStatus::Unavailable);
        }
        self.state
            .plugin_services
            .trade
            .sell_list
            .retain(|i| *i != item_id);
        Self::vendor_answer(VendorStatus::Sent)
    }

    /// Empty the buy list.
    pub(super) fn vendor_clear_buy(&mut self) -> VendorResult {
        if !self.in_world() {
            return Self::vendor_answer(VendorStatus::Unavailable);
        }
        self.state.plugin_services.trade.buy_list.clear();
        Self::vendor_answer(VendorStatus::Sent)
    }

    /// Empty the sell list.
    pub(super) fn vendor_clear_sell(&mut self) -> VendorResult {
        if !self.in_world() {
            return Self::vendor_answer(VendorStatus::Unavailable);
        }
        self.state.plugin_services.trade.sell_list.clear();
        Self::vendor_answer(VendorStatus::Sent)
    }

    fn vendor_transaction(
        &mut self,
        kind: VendorTransactionKind,
        command: ItemCommand,
    ) -> VendorResult {
        match self.run(SessionCommand::Item(command)) {
            Ran::NoConnection => Self::vendor_answer(VendorStatus::Unavailable),
            Ran::Refused(why) => VendorResult {
                status: VendorStatus::Busy,
                notice: Some(why),
            },
            Ran::Done => {
                let watch = &mut self.state.plugin_services.trade;
                watch.pending = Some(kind);
                match kind {
                    VendorTransactionKind::Buy => watch.buy_list.clear(),
                    VendorTransactionKind::Sell => watch.sell_list.clear(),
                }
                Self::vendor_answer(VendorStatus::Sent)
            }
        }
    }

    /// Buy everything on the buy list (OpenAC `BuyAll`).
    pub(super) fn vendor_buy_all(&mut self) -> VendorResult {
        if let Some(refused) = self.vendor_open() {
            return refused;
        }
        let watch = &self.state.plugin_services.trade;
        if watch.pending.is_some() || !self.state.inventory.transactions().can_begin_request() {
            return Self::vendor_answer(VendorStatus::Busy);
        }
        if watch.buy_list.is_empty() {
            return Self::vendor_answer(VendorStatus::InvalidItem);
        }
        let items = watch
            .buy_list
            .iter()
            .map(|(t, n)| (ObjectRef::Guid(*t), *n))
            .collect();
        self.vendor_transaction(VendorTransactionKind::Buy, ItemCommand::Buy(items))
    }

    /// Sell everything on the sell list, whole stacks (OpenAC `SellAll`).
    pub(super) fn vendor_sell_all(&mut self) -> VendorResult {
        if let Some(refused) = self.vendor_open() {
            return refused;
        }
        let watch = &self.state.plugin_services.trade;
        if watch.pending.is_some() || !self.state.inventory.transactions().can_begin_request() {
            return Self::vendor_answer(VendorStatus::Busy);
        }
        if watch.sell_list.is_empty() {
            return Self::vendor_answer(VendorStatus::InvalidItem);
        }
        let items = watch
            .sell_list
            .iter()
            .map(|&id| {
                let amount = self
                    .state
                    .world
                    .item(id)
                    .map_or(1, |item| item.stack_size)
                    .max(1);
                (ObjectRef::Guid(id), amount)
            })
            .collect();
        self.vendor_transaction(VendorTransactionKind::Sell, ItemCommand::Sell(items))
    }
}
