//! Secure trade and vendors (API group H).

use crate::world::ObjectClass;
use crate::{wire_enum, wire_record, wire_union};

wire_enum! {
    /// The answer to a trade command.
    pub enum TradeStatus: "trade status" {
        /// Not in the world.
        Unavailable = 0,
        /// No trade is open.
        NotOpen = 1,
        /// Not an item that can be offered.
        InvalidItem = 2,
        /// Another request is in flight.
        Busy = 3,
        /// Sent.
        Sent = 4,
        /// The client declined.
        Refused = 5,
        /// The trade is already accepted on this side.
        AlreadyAccepted = 6,
    }
}

wire_record! {
    /// The answer to a trade command.
    pub struct TradeResult {
        /// The verdict.
        pub status: TradeStatus,
        /// Why, in words.
        pub notice: Option<String>,
    }
}

wire_record! {
    /// The secure trade window.
    pub struct TradeState {
        /// The character is in the world.
        pub is_available: bool,
        /// A trade is open.
        pub is_open: bool,
        /// The other player.
        pub partner_id: u32,
        /// Their name.
        pub partner_name: String,
        /// The character's offered items, in order.
        pub my_items: Vec<u32>,
        /// The partner's offered items, in order.
        pub partner_items: Vec<u32>,
        /// The character accepted.
        pub my_accepted: bool,
        /// The partner accepted.
        pub partner_accepted: bool,
    }
}

wire_union! {
    /// The trade changed ([`crate::Event::Trade`]).
    pub enum TradeEvent: "trade event" {
        /// A trade opened: (initiator, partner).
        0 => Opened((u32, u32)),
        /// The trade closed.
        1 => Closed,
        /// The partner accepted (the partner's id).
        2 => PartnerAccepted(u32),
        /// An item was offered: (item, offered by the character).
        3 => ItemAdded((u32, bool)),
    }
}

wire_enum! {
    /// The answer to a vendor command.
    pub enum VendorStatus: "vendor status" {
        /// Not in the world.
        Unavailable = 0,
        /// No vendor is open.
        NotOpen = 1,
        /// Not an item that can go on that list.
        InvalidItem = 2,
        /// A transaction is in flight.
        Busy = 3,
        /// Done (for a list) or sent (a purchase or sale).
        Sent = 4,
        /// The client declined.
        Refused = 5,
    }
}

wire_record! {
    /// The answer to a vendor command.
    pub struct VendorResult {
        /// The verdict.
        pub status: VendorStatus,
        /// Why, in words.
        pub notice: Option<String>,
    }
}

wire_record! {
    /// One item the open vendor sells.
    pub struct VendorItem {
        /// The listing's object id (buy with it).
        pub template_id: u32,
        /// Its weenie class.
        pub weenie_class_id: u32,
        /// Its name.
        pub name: String,
        /// What kind of thing it is.
        pub object_class: ObjectClass,
        /// What one costs, at the vendor's rate.
        pub unit_price: i32,
        /// How many the vendor stocks (-1 unlimited).
        pub stack_size: i32,
        /// The largest stack.
        pub max_stack_size: i32,
        /// Item type bits.
        pub item_type: u32,
    }
}

/// [`VendorProfile`]'s value limits when there is none.
pub const NO_VALUE_LIMIT: u32 = u32::MAX;

wire_record! {
    /// What the open vendor buys and pays.
    pub struct VendorProfile {
        /// The fraction of value it pays.
        pub buy_rate: f32,
        /// The item kinds it buys.
        pub deals_in_item_types: u32,
        /// The lowest value it buys ([`NO_VALUE_LIMIT`] for none).
        pub minimum_value: u32,
        /// The highest value it buys ([`NO_VALUE_LIMIT`] for none).
        pub maximum_value: u32,
        /// It buys magical items.
        pub deals_in_magical_items: bool,
        /// The alternate currency's weenie class, 0 for pyreals.
        pub alternate_currency_weenie_class_id: u32,
        /// How much of it.
        pub alternate_currency_amount: u32,
        /// Its name.
        pub alternate_currency_name: Option<String>,
    }
}

wire_record! {
    /// The open vendor and the plugin's own buy and sell lists.
    pub struct VendorState {
        /// The character is in the world.
        pub is_available: bool,
        /// A vendor is open.
        pub is_open: bool,
        /// The vendor.
        pub vendor_id: u32,
        /// Its name.
        pub vendor_name: String,
        /// What it sells.
        pub items: Vec<VendorItem>,
        /// What it buys.
        pub profile: VendorProfile,
        /// A purchase or sale awaits its answer.
        pub is_busy: bool,
        /// The buy list: (listing, count).
        pub buy_list: Vec<(u32, i32)>,
        /// The sell list.
        pub sell_list: Vec<u32>,
    }
}

wire_enum! {
    /// Which way a vendor transaction went.
    pub enum VendorTransactionKind: "vendor transaction kind" {
        /// A purchase.
        Buy = 0,
        /// A sale.
        Sell = 1,
    }
}

wire_record! {
    /// A vendor transaction ended.
    pub struct VendorTransaction {
        /// Which way.
        pub kind: VendorTransactionKind,
        /// It went through.
        pub success: bool,
        /// Why not, in words.
        pub notice: Option<String>,
    }
}

wire_union! {
    /// The vendor changed ([`crate::Event::Vendor`]).
    pub enum VendorEvent: "vendor event" {
        /// A vendor opened (its id).
        0 => Opened(u32),
        /// It closed.
        1 => Closed,
        /// A purchase or sale ended.
        2 => TransactionCompleted(VendorTransaction),
    }
}
