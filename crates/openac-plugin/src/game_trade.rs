//! Typed [`Host`] methods for API group H: secure trade and vendors.

use ac_plugin_abi::calls;
use ac_plugin_abi::trade::{TradeResult, TradeState, VendorResult, VendorState};
use ac_plugin_abi::world::ItemProperties;

use crate::Host;

/// H. Trade and vendor.
impl Host<'_> {
    /// The secure trade window.
    pub fn trade(&mut self) -> TradeState {
        self.call::<calls::TradeState>(&())
    }

    /// Offer an item in the open trade.
    pub fn trade_add(&mut self, item_id: u32) -> TradeResult {
        self.call::<calls::TradeAdd>(&item_id)
    }

    /// Accept the trade as it stands.
    pub fn trade_accept(&mut self) -> TradeResult {
        self.call::<calls::TradeAccept>(&())
    }

    /// Withdraw the acceptance.
    pub fn trade_decline(&mut self) -> TradeResult {
        self.call::<calls::TradeDecline>(&())
    }

    /// Clear both sides.
    pub fn trade_reset(&mut self) -> TradeResult {
        self.call::<calls::TradeReset>(&())
    }

    /// End the trade.
    pub fn trade_end(&mut self) -> TradeResult {
        self.call::<calls::TradeEnd>(&())
    }

    /// The open vendor and this plugin's buy and sell lists.
    pub fn vendor(&mut self) -> VendorState {
        self.call::<calls::VendorState>(&())
    }

    /// The properties of an item the vendor lists.
    pub fn vendor_item_properties(&mut self, template_id: u32) -> Option<ItemProperties> {
        self.call::<calls::VendorProperties>(&template_id)
    }

    /// Add `count` of a listing to the buy list.
    pub fn vendor_add_to_buy_list(&mut self, template_id: u32, count: i32) -> VendorResult {
        self.call::<calls::VendorAddBuy>(&(template_id, count))
    }

    /// Add an owned item to the sell list.
    pub fn vendor_add_to_sell_list(&mut self, item_id: u32) -> VendorResult {
        self.call::<calls::VendorAddSell>(&item_id)
    }

    /// Take a listing off the buy list.
    pub fn vendor_remove_from_buy_list(&mut self, template_id: u32) -> VendorResult {
        self.call::<calls::VendorRemoveBuy>(&template_id)
    }

    /// Take an item off the sell list.
    pub fn vendor_remove_from_sell_list(&mut self, item_id: u32) -> VendorResult {
        self.call::<calls::VendorRemoveSell>(&item_id)
    }

    /// Empty the buy list.
    pub fn vendor_clear_buy_list(&mut self) -> VendorResult {
        self.call::<calls::VendorClearBuy>(&())
    }

    /// Empty the sell list.
    pub fn vendor_clear_sell_list(&mut self) -> VendorResult {
        self.call::<calls::VendorClearSell>(&())
    }

    /// Buy everything on the buy list.
    pub fn vendor_buy_all(&mut self) -> VendorResult {
        self.call::<calls::VendorBuyAll>(&())
    }

    /// Sell everything on the sell list.
    pub fn vendor_sell_all(&mut self) -> VendorResult {
        self.call::<calls::VendorSellAll>(&())
    }
}
