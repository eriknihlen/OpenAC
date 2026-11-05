//! The inventory owner: the one inventory request in flight and the uses
//! outstanding, the auto-wield switch, the open vendor, the viewed ground
//! container, the secure trade and the appraisal slot, and every item
//! request the player makes.
//!
//! One [`Inventory`] per session, owned by the session thread. It reads and
//! marks the world's item table (the world owns it) and queues requests on
//! the session's outbox. Server messages arrive through
//! [`Inventory::apply`]; the item table's events through
//! [`Inventory::observe`]; player requests through [`Inventory::execute`].
//! What the player should read comes out of
//! [`Inventory::drain_notices`]; what a host shows (appraisals, the vendor,
//! the trade) out of [`Inventory::drain_events`].

mod actions;
mod approach;
pub mod house_purchase;
mod interaction;
pub mod pickup;
pub mod shop;
mod trade;
mod views;
mod wield;

#[cfg(test)]
mod tests;

use ac_core::items::{
    ExternalContainerState, ExternalContainerTransition, ExternalContainerTransitionKind,
    InventoryTransactionState, ItemManaState, OpenRequestOutcome, PublicWeenieFlags, ShortcutEntry,
    ShortcutStore, TableEvent, TransactionEvent, VendorShopItem, VendorShopProfile, VendorState,
    VendorTransition,
};
use ac_proto::appraise::IdentifyObjectResponse;
use ac_proto::inventory::{
    SalvageOperationsResult, ViewContents, build_no_longer_viewing_contents, parse_inventory_word,
};
use ac_proto::magic::QueryItemManaResponse;
use ac_proto::trade::{AddToTrade, RegisterTrade, RemoveFromTrade, TradeFailure, parse_trade_word};
use ac_proto::vendor::{ApproachVendor, ItemAmount};
use ac_proto::{GameEventEnvelope, GameEventType, Inbound, ProtoError};

use crate::character::CharacterOptions;
use crate::session::Outbox;
use crate::world::World;
use crate::world::liveness::QueueNote;

pub use actions::{REQUEST_NOT_SENT, plan_join_stack, target_compatible};
pub use approach::{
    APPROACH_NEVER_COMPLETED, ApproachCompletion, ApproachPlan, ApproachSource, ApproachToken,
    CHARGE_DISTANCE, DEFAULT_USE_RADIUS, STALLED_APPROACH_GIVE_UP_TICKS,
};
pub use house_purchase::{
    HousePurchaseCommand, HousePurchaseState, HousePurchaseTab, HousePurchaseView, HouseStageResult,
};
pub use interaction::{
    APPRAISAL_REQUEST_TIMEOUT_MS, AppraisalAcceptance, AppraisalOrigin, Interaction,
    USE_THROTTLE_MS,
};
pub use shop::{ShopItems, StagedRow, VendorLists, VendorListsView};
pub use trade::{SIDE_PARTNER, SIDE_SELF, TradeState, TradeView};
pub use views::{InventoryView, ItemSummary, PackView, ground_view, inventory_view};
pub use wield::{AUTO_EQUIP_ORDER, AUTO_WEAR_MASK, WEAPON_READY_MASK, is_auto_wear_item};

/// What the player is told when an item request is refused because one is
/// already in flight.
pub const REQUEST_BUSY: &str = "You can only move or use one item at a time";
/// What the player is told when the partner or the server closes a trade.
pub const TRADE_CANCELLED: &str = "The trade has been cancelled.";

/// What the player is told when an item is to be placed while another is
/// still on its way into a pack.
#[must_use]
pub fn placement_conflict_text(name: &str) -> String {
    format!("Already attempting to place {name} here")
}

/// An object a command names: by id, by name (the player's items first,
/// then the open ground container's, then the open vendor's, then the
/// nearest object in the world), or the player.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ObjectRef {
    /// A server object id.
    Guid(u32),
    /// A name, matched without case: an exact name before a name that only
    /// starts with it.
    Name(String),
    /// The player.
    Player,
}

impl ObjectRef {
    /// `0x...` or decimal reads as an id, `me`/`self` as the player, anything
    /// else as a name.
    #[must_use]
    pub fn parse(text: &str) -> Self {
        let text = text.trim();
        if text.eq_ignore_ascii_case("me") || text.eq_ignore_ascii_case("self") {
            return Self::Player;
        }
        let id = match text.strip_prefix("0x").or_else(|| text.strip_prefix("0X")) {
            Some(hex) => u32::from_str_radix(hex, 16).ok(),
            None => text.parse::<u32>().ok(),
        };
        id.map_or_else(|| Self::Name(text.to_owned()), Self::Guid)
    }
}

/// An item request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ItemCommand {
    /// Use an object (the primary use: pick up, wield, open, use).
    Use(ObjectRef),
    /// A plugin's use of a world object the character does not own: a
    /// loose item is picked up; anything else goes out now within reach,
    /// else the character walks there first. Unlike [`ItemCommand::Use`]
    /// (a person's double-click), the container a corpse or chest opens is
    /// asked for only once the use goes out, and a walk given up says
    /// nothing to the character.
    UseWorldObject(ObjectRef),
    /// Use an item on a target.
    UseOn {
        /// The item.
        item: ObjectRef,
        /// The target.
        target: ObjectRef,
    },
    /// The pick-up key's request on an object: what may not be picked up
    /// (a creature, something stuck or another's) is refused in the game's
    /// words first, whatever room the packs have; anything else is placed
    /// as [`ItemCommand::PlaceInPack`] places it.
    PickUp(ObjectRef),
    /// Place a loose item in the inventory: the pack a window shows open
    /// (else the main pack, or the main pack when the MainPackPreferred
    /// option is on), then wherever there is room; with no room the
    /// "full" refusal, else the pickup (which refuses what may not be
    /// picked up).
    PlaceInPack(ObjectRef),
    /// Place a loose item in the inventory, naming the main pack first.
    PickUpToMainPack(ObjectRef),
    /// The side pack a window shows open (0: none); a pickup names it
    /// first.
    SetOpenPack(u32),
    /// Put an item (or `amount` of a stack; 0 for all) into a container at
    /// a slot.
    MoveToContainer {
        /// The item.
        item: ObjectRef,
        /// The container (the player for the main pack).
        container: ObjectRef,
        /// The slot.
        slot: i32,
        /// How many; 0 for the whole stack.
        amount: u32,
        /// Join a stack of the same thing in the container (its own items,
        /// then each pack inside it) that has room for all that moves,
        /// with a merge, instead; the plain move when none has.
        join_stack: bool,
    },
    /// Merge `amount` (0 for all that fits) of one stack into another.
    Merge {
        /// The source stack.
        from: ObjectRef,
        /// The target stack.
        to: ObjectRef,
        /// How many.
        amount: u32,
    },
    /// Drop an item (or `amount` of a stack) on the ground.
    Drop {
        /// The item.
        item: ObjectRef,
        /// How many; 0 for the whole stack.
        amount: u32,
    },
    /// Give an item to someone.
    Give {
        /// The item.
        item: ObjectRef,
        /// Who to give it to.
        target: ObjectRef,
        /// How many; 0 for the whole stack.
        amount: u32,
    },
    /// Wield an item, in the given slots or (0) where it fits.
    Wield {
        /// The item.
        item: ObjectRef,
        /// The slot bits; 0 to choose.
        location: u32,
    },
    /// Wield a weapon in the off hand (the shield slot); whatever already
    /// sits there goes to the backpack first. Whether the character may
    /// dual wield is the server's decision.
    WieldSecondary(ObjectRef),
    /// Take a wielded item off into the main pack.
    Unwield(ObjectRef),
    /// Appraise an object.
    Appraise(ObjectRef),
    /// Stop viewing a container's contents.
    StopViewing(ObjectRef),
    /// Buy from the open vendor.
    Buy(Vec<(ObjectRef, i32)>),
    /// Sell to the open vendor.
    Sell(Vec<(ObjectRef, i32)>),
    /// The open vendor's window: its lists, closing it.
    Vendor(VendorCommand),
    /// Open a secure trade with a player.
    OpenTrade(ObjectRef),
    /// Offer an item in the open trade.
    AddToTrade(ObjectRef),
    /// Accept the trade as it stands.
    AcceptTrade,
    /// Withdraw the acceptance.
    DeclineTrade,
    /// Clear both sides of the trade.
    ResetTrade,
    /// Close the trade window.
    CloseTrade,
    /// The slumlord window: staging its payment, sending it, closing it.
    HousePurchase(HousePurchaseCommand),
    /// Put an item into a pack at a slot, as a drop on an item window does
    /// (the inventory's grid and pack column, an outside container's
    /// contents): part of a stack (`amount` short of it) splits off; the
    /// whole of it is shown waiting at that slot until the server answers
    /// ([`Inventory::pending_placement`]). A loose item (`pickup`) goes as a
    /// pickup, anything else as a move.
    DropInto {
        /// The item.
        item: ObjectRef,
        /// The pack (the player for the main pack).
        container: ObjectRef,
        /// The slot.
        slot: i32,
        /// How many of a stack (the split chooser); 0 for the whole stack.
        amount: u32,
        /// The item is not the player's (it lies in the world or in an
        /// outside container).
        pickup: bool,
        /// A drop on a pack's icon: join the first stack in the pack (its
        /// own items, then each pack inside it) with room for all that
        /// moves, with a merge, before any room check; the plain drop when
        /// none has room.
        join_stack: bool,
    },
    /// A drop on a stack in a pack's grid: the dragged stack (from a pack,
    /// the paperdoll, an outside container or the ground) merges into
    /// that stack when the merge is legal; otherwise it is the plain drop
    /// into the pack.
    DropOnStack {
        /// The dragged item.
        item: ObjectRef,
        /// The stack dropped on.
        target: ObjectRef,
        /// How many the merge moves (the split chooser's quantity of the
        /// selected stack, else the whole stack; capped at the room).
        merge_amount: u32,
        /// The plain drop's pack (the player for the main pack).
        container: ObjectRef,
        /// The plain drop's slot; `None` when the item is already there
        /// (no plain drop).
        slot: Option<i32>,
        /// The plain drop's amount; 0 for the whole stack.
        amount: u32,
        /// The item is not the player's (it lies in the world or in an
        /// outside container).
        pickup: bool,
    },
    /// An item dropped on the toolbar's backpack button: one of the
    /// player's goes into the preferred pack (the open pack, or the main
    /// pack when MainPackPreferred is on), shown waiting there; anything
    /// else is picked up into the main pack at once.
    DropOnBackpack(ObjectRef),
    /// The items the salvage window holds staged (empty: none): they are
    /// marked as in a trade until unstaged, as the salvage window marks
    /// them (moves, uses and merges refuse them; the inventory draws the
    /// trade overlay on them).
    MarkSalvage(Vec<u32>),
    /// Put a shortcut on the toolbar (its slot is in the entry).
    AddShortcut(ShortcutEntry),
    /// Take the shortcut in this slot off the toolbar.
    RemoveShortcut(i32),
    /// A loose item (on the ground or in an outside container) dropped on a
    /// toolbar slot: it is picked up into the preferred pack, as the use key
    /// picks it up, and once that request has gone out it gets the shortcut
    /// in `slot`.
    PickUpToShortcut {
        /// The item.
        item: ObjectRef,
        /// The toolbar slot.
        slot: i32,
    },
    /// The next object clicked is used (the toolbar's use button with
    /// nothing selected).
    EnterUseMode,
    /// The next object clicked is examined (the examine button with nothing
    /// selected).
    EnterExamineMode,
    /// Leave the target mode.
    CancelTargetMode,
    /// An object was clicked while a target mode waits: it is used,
    /// examined, or the waiting item is used on it.
    OfferTarget(ObjectRef),
    /// Close the open outside container: a use of it, as is (the server
    /// closes the view; no policy, no busy count).
    CloseContainer(ObjectRef),
    /// Write (or, empty, clear) an item's inscription, as the appraisal
    /// panel's inscription field does when it loses focus.
    Inscribe {
        /// The item.
        item: ObjectRef,
        /// The new inscription.
        text: String,
    },
    /// Salvage the player's items with a tinkering tool the player carries
    /// (the salvage panel's button).
    Salvage {
        /// The salvaging tool.
        tool: ObjectRef,
        /// What to salvage.
        items: Vec<ObjectRef>,
    },
    /// Ask the server how much mana an item holds
    /// ([`InventoryEvent::ItemMana`] follows).
    QueryMana(ObjectRef),
    /// Ask again about the object the examination shows (the creature
    /// page refreshes in combat); its answer refreshes the page and leaves
    /// the asking slot alone. Nothing when nothing is shown.
    RefreshCurrentAppraisal,
    /// Use the selected object (the Use key and button); with nothing
    /// selected, the next object clicked is used. The session puts the
    /// selection in.
    UseSelected,
    /// Examine (appraise) the selected object; with nothing selected, the
    /// next object clicked is examined.
    ExamineSelected,
    /// The pick-up key: [`ItemCommand::PickUp`] on the selected object;
    /// with nothing selected the player is told so.
    PickUpSelected,
    /// The place-in-inventory key: [`ItemCommand::PlaceInPack`] on the
    /// selected object; nothing with nothing selected.
    PlaceSelectedInPack,
    /// The place-in-main-pack key: [`ItemCommand::PickUpToMainPack`] on
    /// the selected object; nothing with nothing selected.
    PickUpSelectedToMainPack,
    /// Give the selected object to the object selected before it (the give
    /// key), as a drop on it would: `amount` of a stack (the split
    /// chooser; 0 for the whole stack). Refused unless that one is a
    /// creature; once it is placed the selection moves to it.
    GiveSelectedToPrevious {
        /// How many of a stack; 0 for the whole stack.
        amount: u32,
    },
    /// Put the selected object on the ground (the drop key): `amount` of a
    /// stack (0 for the whole stack); refused unless the player carries
    /// it; nothing with nothing selected.
    DropSelected {
        /// How many of a stack; 0 for the whole stack.
        amount: u32,
    },
    /// Put an item into the world, as dragging it out of a window does:
    /// onto the ground (no target), or onto an object (the player: into
    /// the pack; a creature: a gift; a vendor: the sell list; a player: a
    /// secure trade; an open ground container: into it).
    Place {
        /// The item.
        item: ObjectRef,
        /// What it is dropped on; `None` for the ground.
        target: Option<ObjectRef>,
        /// How many of a stack (the split chooser); 0 for the whole stack.
        amount: u32,
    },
}

/// What the next object clicked does.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum InteractionMode {
    /// Clicks select.
    #[default]
    None,
    /// The object clicked is used.
    Use,
    /// The object clicked is examined.
    Examine,
    /// This item is used on the object clicked.
    UseItemOnTarget(u32),
}

impl InteractionMode {
    /// The item waiting to be used, if any.
    #[must_use]
    pub fn source(self) -> Option<u32> {
        match self {
            Self::UseItemOnTarget(source) => Some(source),
            _ => None,
        }
    }
}

/// What the vendor window's buttons and lists do.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum VendorCommand {
    /// Reply with the shop ([`InventoryEvent::Vendor`]).
    Show,
    /// Close the window (the shop is forgotten until used again).
    Close,
    /// Stage a listed item on the buy list: `amount` units, 0 for its
    /// authored stack.
    AddToBuyList {
        /// The listing.
        item: ObjectRef,
        /// How many; 0 for the authored stack.
        amount: i32,
    },
    /// Take `amount` (-1 for the row) of a listing off the buy list.
    RemoveFromBuyList {
        /// The listing.
        item: ObjectRef,
        /// How many; -1 for the whole row.
        amount: i32,
    },
    /// Empty the buy list.
    ClearBuyList,
    /// Buy everything on the buy list as one purchase.
    BuyAll,
    /// Stage one of the player's items (a pack: its contents) on the sell
    /// list, if the vendor buys it.
    AddToSellList(ObjectRef),
    /// Take an item off the sell list.
    RemoveFromSellList(ObjectRef),
    /// Empty the sell list.
    ClearSellList,
    /// Sell everything on the sell list.
    SellAll,
}

/// Why the vendor view was sent.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VendorViewReason {
    /// A shop opened.
    Opened,
    /// The open shop's listing came again (after a purchase or sale).
    Refreshed,
    /// The buy or sell list changed.
    Lists,
    /// `/fillcomps` filled the buy list from the component book: the
    /// window shows the spell components and its buying page, as the Add
    /// button does.
    ComponentsFilled,
    /// The host asked.
    Asked,
}

/// Something the player should read, routed as the client routes it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Notice {
    /// Client-local text (the status overlay).
    Interface(String),
    /// A failure code, worded and routed by its text class.
    WeenieError(u32),
    /// Text in the chat, in a text class (the salvage result).
    Text(String, ac_core::chat::LogTextType),
    /// Ask before using an item.
    Confirm(crate::communication::ClientConfirmation),
}

/// The open vendor's shop as a host shows it.
#[derive(Debug, Clone, PartialEq)]
pub struct VendorView {
    /// The vendor.
    pub vendor_guid: u32,
    /// Its name, if described.
    pub name: Option<String>,
    /// What it trades in and at what rates.
    pub profile: VendorShopProfile,
    /// Its listing.
    pub items: Vec<VendorShopItem>,
    /// The buy and sell lists and the purse.
    pub lists: VendorListsView,
    /// Why this view was sent.
    pub reason: VendorViewReason,
}

impl VendorView {
    /// What the player pays for `quantity` of a listing at this vendor's
    /// selling rate; `None` when it is not listed.
    #[must_use]
    pub fn price(&self, item_guid: u32, quantity: i32) -> Option<i32> {
        self.items
            .iter()
            .find(|i| i.item_guid == item_guid)
            .map(|i| shop::listing_price(i, &self.profile, quantity))
    }
}

/// An appraisal answer as a host shows it.
#[derive(Debug, Clone, PartialEq)]
pub struct AppraisalView {
    /// The object's name, if described.
    pub name: Option<String>,
    /// The answer as sent.
    pub response: IdentifyObjectResponse,
    /// The examination window takes it (the player's own answer, or one
    /// about the object it shows); an automation answer about another
    /// object (a walk checking a door) only updates that object.
    pub present_in_ui: bool,
    /// The first answer to the player's own question: the window opens on
    /// it. Any other answer it takes only refreshes what it shows.
    pub opens_window: bool,
}

impl AppraisalView {
    /// Every property the answer carried as (name, value) text, by table
    /// (ints, 64-bit ints, bools, floats, strings, dat ids); a property this
    /// client has no name for shows its number.
    #[must_use]
    pub fn property_lines(&self) -> Vec<(String, String)> {
        use ac_core::properties::{
            PropertyBool, PropertyDataId, PropertyFloat, PropertyInt, PropertyInt64, PropertyString,
        };
        fn named(name: Option<&str>, table: &str, key: u32) -> String {
            name.map_or_else(|| format!("{table}{key}"), str::to_owned)
        }
        let p = &self.response.properties;
        let mut lines = Vec::new();
        for (&key, value) in &p.ints {
            lines.push((
                named(PropertyInt(key).name(), "Int", key),
                value.to_string(),
            ));
        }
        for (&key, value) in &p.int64s {
            lines.push((
                named(PropertyInt64(key).name(), "Int64_", key),
                value.to_string(),
            ));
        }
        for (&key, value) in &p.bools {
            lines.push((
                named(PropertyBool(key).name(), "Bool", key),
                value.to_string(),
            ));
        }
        for (&key, value) in &p.floats {
            lines.push((
                named(PropertyFloat(key).name(), "Float", key),
                format!("{value:.3}"),
            ));
        }
        for (&key, value) in &p.strings {
            lines.push((
                named(PropertyString(key).name(), "String", key),
                value.clone(),
            ));
        }
        for (&key, value) in &p.data_ids {
            lines.push((
                named(PropertyDataId(key).name(), "DataId", key),
                format!("0x{value:08X}"),
            ));
        }
        lines
    }
}

/// What a host should show.
#[derive(Debug, Clone, PartialEq)]
pub enum InventoryEvent {
    /// An appraisal answered a question.
    Appraisal(Box<AppraisalView>),
    /// The vendor opened or refreshed (`Some`), or closed (`None`).
    Vendor(Option<Box<VendorView>>),
    /// The trade changed.
    Trade(TradeView),
    /// A command named something that is not there.
    Refused(String),
    /// The server answered a mana query.
    ItemMana(ac_core::items::ItemManaChange),
    /// A salvaging tool the player carries was used: the salvage panel
    /// opens on it.
    OpenSalvage(u32),
    /// A pack the player carries was used: the inventory window shows and
    /// opens on it (the main pack when it is the player).
    OpenContainedContainer(u32),
}

/// What the owner reads and changes outside itself for one call.
#[derive(Debug)]
pub struct ItemContext<'a> {
    /// The world (its item table is marked by trades).
    pub world: &'a mut World,
    /// Where requests are queued.
    pub outbox: &'a mut Outbox,
    /// The character options (main pack preferred, volatile rare confirm).
    pub options: &'a CharacterOptions,
    /// True in the world; outside it nothing is sent.
    pub active: bool,
    /// Session time, milliseconds (use pacing, appraisal limit).
    pub now_ms: i64,
    /// The combat owner has the character at peace (the use policy reads it).
    pub in_peace: bool,
    /// The character's body, to walk it to what it uses; `None` where the
    /// call walks nothing (the request then goes out at once).
    pub approach: Option<&'a mut dyn ApproachSource>,
}

impl ItemContext<'_> {
    fn player(&self) -> u32 {
        self.world.player_guid().unwrap_or(0)
    }

    /// Queue a game action; false outside the world.
    fn send(&mut self, build: impl FnOnce(u32) -> Vec<u8>) -> bool {
        if !self.active {
            return false;
        }
        self.outbox.send_action(build);
        true
    }
}

/// The session's inventory state.
#[derive(Debug, Default)]
pub struct Inventory {
    transactions: InventoryTransactionState,
    interaction: Interaction,
    wield: wield::AutoWield,
    vendor: VendorState,
    /// The open vendor's listing in the item table.
    shop_items: ShopItems,
    /// The open vendor's buy and sell lists.
    lists: VendorLists,
    /// Each object's use radius from its description (how close a use
    /// walks the character; a vendor's window stays open within it); a
    /// vendor described without one has none.
    use_radii: std::collections::HashMap<u32, f32>,
    /// The use and pickup armed for a walk's end.
    approaches: approach::Approaches,
    external: ExternalContainerState,
    /// The side pack a window shows open (0: none).
    open_pack: u32,
    trade: TradeState,
    /// The slumlord window.
    house_purchase: HousePurchaseState,
    /// What the next object clicked does.
    mode: InteractionMode,
    /// The toolbar's shortcut bar (character state: the description loads
    /// it, the player's changes go to the server as they are made).
    shortcuts: ShortcutStore,
    /// The mana of each item the server answered a query about.
    item_mana: ItemManaState,
    /// The last use and inventory request the server answered.
    completions: ItemCompletions,
    /// How the last pickup into the packs went, until taken.
    backpack_placement: Option<BackpackPlacement>,
    /// The item shown waiting at the pack slot its request names.
    placement: Option<PendingPlacement>,
    /// The items the salvage window marked as staged.
    salvage_marked: std::collections::BTreeSet<u32>,
    /// Game boards used, oldest first, whose games the session begins.
    begun_games: Vec<u32>,
    /// The stacks merges were just sent into, oldest first, which the
    /// session selects.
    merged_into: Vec<u32>,
    notices: Vec<Notice>,
    events: Vec<InventoryEvent>,
    /// `OPENAC_PROBE_EXTERNAL_CONTAINER=1`: the viewed container's probe
    /// lines, waiting for the session thread (`None`: the probe is off).
    probe: Option<Vec<String>>,
}

/// An item on its way into a pack: shown at the slot its request names,
/// waiting, until the server answers that request or it is called off
/// (OpenAC `PendingBackpackPlacement`). One at a time; it holds the one
/// request in flight from the moment it is made, also while the character
/// turns to the item first.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PendingPlacement {
    /// The request's token (never 0).
    pub token: u64,
    /// The item.
    pub item_id: u32,
    /// The pack it goes into (the player for the main pack).
    pub container_id: u32,
    /// The slot it goes to.
    pub placement: i32,
}

/// How a pickup into the packs went (OpenAC
/// `RuntimeBackpackPlacementOutcome`): the one request went out, there was
/// no room anywhere, a request is already in flight, or the merge it would
/// have become did not go out.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BackpackPlacement {
    /// The pickup (or the merge into a held stack) went out.
    Sent,
    /// No pack the player has open has room for it.
    NoRoom,
    /// A request is already in flight.
    AlreadyPending,
    /// It was not sent.
    NotDispatched,
}

/// The last awaited use and the last inventory request the server
/// answered, each counted (what a plugin waiting on its own request reads).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ItemCompletions {
    /// Counts answered uses; 0 before the first.
    pub use_revision: u64,
    /// The item the answered use was of.
    pub use_source: u32,
    /// What it was used on (0 for nothing).
    pub use_target: u32,
    /// The server's error for it (0 for none).
    pub use_error: u32,
    /// Counts answered inventory requests; 0 before the first.
    pub request_revision: u64,
    /// What the answered request was.
    pub request_kind: Option<ac_core::items::InventoryRequestKind>,
    /// Its item.
    pub request_item: u32,
    /// The server's error for it (0 for none).
    pub request_error: u32,
}

impl Inventory {
    /// The last use and inventory request the server answered.
    #[must_use]
    pub fn completions(&self) -> &ItemCompletions {
        &self.completions
    }

    /// How the last pickup into the packs went, since last taken.
    pub fn take_backpack_placement(&mut self) -> Option<BackpackPlacement> {
        self.backpack_placement.take()
    }

    /// The item shown waiting at the pack slot its request names, if any.
    #[must_use]
    pub fn pending_placement(&self) -> Option<PendingPlacement> {
        self.placement
    }

    /// The side pack a window shows open (0: none, the main pack).
    #[must_use]
    pub fn open_pack(&self) -> u32 {
        self.open_pack
    }

    /// The window that showed the open pack went: pickups name the main
    /// pack again, and the salvage window's marks are lifted (its staging
    /// went with it).
    pub fn window_detached(&mut self, world: &mut World) {
        self.open_pack = 0;
        self.mark_salvage(&[], world);
    }

    /// Mark `staged` as the salvage window's staged items: the ones no
    /// longer staged lose the mark, the new ones get it.
    fn mark_salvage(&mut self, staged: &[u32], world: &mut World) {
        let staged: std::collections::BTreeSet<u32> = staged.iter().copied().collect();
        for guid in self
            .salvage_marked
            .difference(&staged)
            .copied()
            .collect::<Vec<_>>()
        {
            self.salvage_marked.remove(&guid);
            world.set_trade_state(guid, 0);
        }
        for guid in staged {
            if self.salvage_marked.insert(guid) {
                world.set_trade_state(guid, 1);
            }
        }
    }

    /// "Already attempting to place X here": a placement is under way.
    fn report_placement_conflict(&mut self, world: &World) {
        let Some(pending) = self.placement else {
            return;
        };
        let name = world
            .item(pending.item_id)
            .map(|i| i.name.clone())
            .filter(|n| !n.trim().is_empty())
            .unwrap_or_else(|| "that item".to_owned());
        self.interface(placement_conflict_text(&name));
    }

    /// Call the placement with `token` off: it is no longer shown, and its
    /// request, if not sent yet, is released.
    fn cancel_placement(&mut self, token: u64) {
        if token == 0 {
            return;
        }
        if self.placement.is_some_and(|p| p.token == token) {
            self.placement = None;
        }
        self.transactions
            .cancel_before_dispatch(token, &mut Vec::new());
    }

    /// Nothing in flight.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The request in flight and the use and appraisal counts.
    #[must_use]
    pub fn transactions(&self) -> &InventoryTransactionState {
        &self.transactions
    }

    /// The use pacing and appraisal slot.
    #[must_use]
    pub fn interaction(&self) -> &Interaction {
        &self.interaction
    }

    /// Recovery: drops one busy reference as a use completion would;
    /// the busy count before and after.
    pub(crate) fn clear_one_busy(&mut self) -> (u32, u32) {
        let before = self.transactions.busy_count();
        // The only event is a state change, which nothing here follows.
        self.transactions.complete_use(&mut Vec::new());
        (before, self.transactions.busy_count())
    }

    /// Recovery: gives up on the description the server never answered;
    /// false when none is awaited.
    pub(crate) fn abandon_awaiting_appraisal(&mut self) -> bool {
        self.interaction
            .abandon_awaiting(&mut self.transactions, &mut Vec::new())
    }

    /// The open vendor.
    #[must_use]
    pub fn vendor(&self) -> &VendorState {
        &self.vendor
    }

    /// The open vendor's listing in the item table.
    #[must_use]
    pub fn shop_items(&self) -> &ShopItems {
        &self.shop_items
    }

    /// The open vendor's buy and sell lists.
    #[must_use]
    pub fn vendor_lists(&self) -> &VendorLists {
        &self.lists
    }

    /// The open vendor's use radius, metres (0 when it has none).
    #[must_use]
    pub fn vendor_use_radius(&self) -> f32 {
        self.use_radii
            .get(&self.vendor.vendor_id())
            .copied()
            .unwrap_or(0.0)
    }

    /// The use radius `guid` described, metres (0 when it named none or is
    /// not described).
    #[must_use]
    pub fn use_radius(&self, guid: u32) -> f32 {
        self.use_radii.get(&guid).copied().unwrap_or(0.0)
    }

    /// Close the open vendor's window (out of range, a teleport, the
    /// player's close); nothing when none is open.
    pub fn close_vendor(&mut self, ctx: &mut ItemContext<'_>) {
        if let Some(transition) = self.vendor.close() {
            let mut tx = Vec::new();
            self.vendor_changed(&transition, ctx.world, &mut tx);
            self.handle_transaction_events(tx, ctx);
            self.observe(ctx);
        }
    }

    /// The slumlord window's state.
    #[must_use]
    pub fn house_purchase(&self) -> &HousePurchaseState {
        &self.house_purchase
    }

    /// The slumlord window as a host shows it.
    #[must_use]
    pub fn house_purchase_view(&self, world: &World) -> HousePurchaseView {
        self.house_purchase
            .view(world.items(), world.player_guid().unwrap_or(0))
    }

    /// The trade-note classes the slumlord window pays with, each with its
    /// value in pyreals (the dats' trade-note map).
    pub fn install_trade_notes(
        &mut self,
        notes: std::sync::Arc<std::collections::HashMap<u32, i32>>,
    ) {
        self.house_purchase.install_trade_notes(notes);
    }

    /// Close the slumlord window (out of range, the slumlord gone);
    /// nothing when none is open.
    pub fn close_house_purchase(&mut self) {
        self.house_purchase.close();
    }

    /// An item-table change while the slumlord window is open: staged items
    /// the player no longer carries leave the strips, and a staged item
    /// changed in place (a stack merged or split) is read again.
    fn follow_house_purchase(&mut self, event: &TableEvent, world: &World) {
        if !self.house_purchase.is_open() {
            return;
        }
        let player = world.player_guid().unwrap_or(0);
        if self.house_purchase.revalidate(world.items(), player) {
            return;
        }
        if let TableEvent::Updated { object_id, .. }
        | TableEvent::StackSizeUpdated { object_id, .. } = event
            && self.house_purchase.is_staged(*object_id)
        {
            self.house_purchase.touch();
        }
    }

    /// The slumlord window's request.
    fn run_house_purchase(
        &mut self,
        command: HousePurchaseCommand,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let player = ctx.player();
        match command {
            HousePurchaseCommand::Stage { tab, item } => {
                let result = self
                    .house_purchase
                    .stage(tab, item, ctx.world.items(), player);
                // Of the refusals only an item the player does not carry is
                // said.
                if result == HouseStageResult::NotInInventory {
                    self.notices.push(Notice::Text(
                        house_purchase::NOT_CARRYING.to_owned(),
                        ac_core::chat::LogTextType::ClientLocal,
                    ));
                }
                result == HouseStageResult::Staged
            }
            HousePurchaseCommand::Unstage { tab, item } => {
                self.house_purchase.unstage(tab, item, ctx.world.items())
            }
            HousePurchaseCommand::Submit { tab, slumlord } => {
                // Checked again here: the window may have closed or moved on
                // to another slumlord while a question was answered.
                let view = self.house_purchase_view(ctx.world);
                if self.house_purchase.slumlord() != slumlord
                    || slumlord == 0
                    || !view.can_submit(tab)
                {
                    return false;
                }
                let items = view.staged(tab).to_vec();
                let sent = ctx.send(|seq| match tab {
                    HousePurchaseTab::Buy => {
                        ac_proto::house::build_buy_house(seq, slumlord, &items)
                    }
                    HousePurchaseTab::Rent => {
                        ac_proto::house::build_rent_house(seq, slumlord, &items)
                    }
                });
                if sent {
                    self.house_purchase.clear_staged(tab);
                }
                sent
            }
            HousePurchaseCommand::Close => self.house_purchase.close(),
        }
    }

    /// The viewed ground container.
    #[must_use]
    pub fn external_containers(&self) -> &ExternalContainerState {
        &self.external
    }

    /// Ask to open `container`. Another container still open is retired:
    /// the server hears the player stopped viewing it, and its contents
    /// lists are dropped.
    fn request_external_open(&mut self, container: u32, ctx: &mut ItemContext<'_>) {
        let corpse = ctx
            .world
            .item(container)
            .is_some_and(|c| c.flags().intersects(PublicWeenieFlags::CORPSE));
        let outcome = self.external.request_open(container, corpse);
        if outcome != OpenRequestOutcome::Ignored {
            self.probe_external(|| format!("open requested 0x{container:08X}"));
        }
        if let OpenRequestOutcome::ReplacementRequested(transition) = outcome {
            Self::retire_external(transition, ctx);
        }
    }

    /// Turn on the viewed container's probe
    /// (`OPENAC_PROBE_EXTERNAL_CONTAINER`).
    pub fn enable_external_container_probe(&mut self) {
        self.probe.get_or_insert_with(Vec::new);
    }

    /// The probe lines since the last call (none while the probe is off).
    pub fn take_probe_lines(&mut self) -> Vec<String> {
        self.probe.as_mut().map(std::mem::take).unwrap_or_default()
    }

    /// One probe line about the viewed container, with the time and the
    /// requested and open containers after it. Built only while the probe
    /// is on.
    pub(crate) fn probe_external(&mut self, what: impl FnOnce() -> String) {
        if let Some(lines) = &mut self.probe {
            lines.push(format!(
                "[external-container] {} {} (requested 0x{:08X}, open 0x{:08X})",
                crate::world::motion_log_stamp(),
                what(),
                self.external.requested_container_id(),
                self.external.current_container_id(),
            ));
        }
    }

    /// What a change of the viewed container leaves behind: the container
    /// it left stops being viewed, and a replaced one is also told to the
    /// server (a close or a reset is the server's or the session's own).
    fn retire_external(transition: ExternalContainerTransition, ctx: &mut ItemContext<'_>) {
        let previous = transition.previous_container_id;
        if previous == 0 {
            return;
        }
        ctx.world.stop_viewing_contents_tree(previous);
        if transition.kind == ExternalContainerTransitionKind::ReplacementRequested {
            ctx.send(|seq| build_no_longer_viewing_contents(seq, previous));
        }
    }

    /// The secure trade.
    #[must_use]
    pub fn trade(&self) -> &TradeView {
        self.trade.view()
    }

    /// An auto-wield switch is in flight.
    #[must_use]
    pub fn is_wield_busy(&self) -> bool {
        self.wield.is_busy()
    }

    /// What the next object clicked does.
    #[must_use]
    pub fn interaction_mode(&self) -> InteractionMode {
        self.mode
    }

    /// The shortcut bar.
    #[must_use]
    pub fn shortcuts(&self) -> &ShortcutStore {
        &self.shortcuts
    }

    /// The mana the server reported for each item asked about.
    #[must_use]
    pub fn item_mana(&self) -> &ItemManaState {
        &self.item_mana
    }

    /// The character description's shortcut bar replaces the one held.
    pub fn load_shortcuts(&mut self, shortcuts: &[ac_proto::Shortcut]) {
        self.shortcuts.load(shortcuts.iter().map(|s| ShortcutEntry {
            index: s.index,
            object_id: s.object_id,
            spell_id: s.spell_id,
        }));
    }

    /// An item request may be made now: in the world, none in flight, no
    /// use outstanding and no auto-wield switch under way.
    #[must_use]
    pub fn is_ready(&self, active: bool) -> bool {
        active && self.transactions.can_begin_request() && !self.wield.is_busy()
    }

    /// The item whose request is in flight, if any.
    #[must_use]
    pub fn pending_item(&self) -> Option<u32> {
        self.transactions.pending().map(|p| p.item_id)
    }

    /// Count the player busy until the server's use-done, for an item
    /// action that went out elsewhere.
    pub fn increment_busy(&mut self) {
        // The only event is a state change, which nothing here follows.
        self.transactions.increment_busy_count(&mut Vec::new());
    }

    /// Count the player busy until the server's use-done for a spell cast:
    /// busy for the cursor, but no hold on item requests.
    pub fn increment_cast_busy(&mut self) {
        // The only event is a state change, which nothing here follows.
        self.transactions.increment_cast_busy_count(&mut Vec::new());
    }

    /// Take what the player should read, oldest first.
    pub fn drain_notices(&mut self) -> std::vec::Drain<'_, Notice> {
        self.notices.drain(..)
    }

    /// Take what a host should show, oldest first.
    pub fn drain_events(&mut self) -> std::vec::Drain<'_, InventoryEvent> {
        self.events.drain(..)
    }

    /// Take the game boards used, oldest first: using someone else's board
    /// begins its game rather than sending a use, and the session begins
    /// it (the chess owner's join) on every host.
    pub fn take_begun_games(&mut self) -> Vec<u32> {
        std::mem::take(&mut self.begun_games)
    }

    /// Take the stacks merges were sent into, oldest first. Each merge, by
    /// any caller (a pickup that joins a carried stack, a drag, a plugin),
    /// selects the stack it joins, so the selection shows that stack and
    /// its count as it grows rather than the stack poured away; the
    /// session selects them on every host.
    pub fn take_merged_into(&mut self) -> Vec<u32> {
        std::mem::take(&mut self.merged_into)
    }

    fn interface(&mut self, text: impl Into<String>) {
        self.notices.push(Notice::Interface(text.into()));
    }

    // -- Inbound ----------------------------------------------------------

    /// Apply one parsed server message (after the world applied it).
    pub fn apply(&mut self, message: &Inbound<'_>, ctx: &mut ItemContext<'_>) {
        match message {
            Inbound::GameEvent(event) if self.apply_game_event(event, ctx).is_err() => {
                ctx.world
                    .record_parse_failure(0xF7B0_0000 | event.event_type.0);
            }
            Inbound::CreateObject(create) | Inbound::UpdateObject(create) => {
                if let Some(weenie) = &create.weenie {
                    // A description without a use radius really has none.
                    self.use_radii
                        .insert(create.guid, weenie.use_radius.unwrap_or(0.0));
                }
            }
            // A teleport closes the vendor's window and the slumlord's.
            Inbound::PlayerTeleport(_) => {
                self.close_vendor(ctx);
                self.house_purchase.close();
            }
            _ => {}
        }
        self.observe(ctx);
    }

    fn apply_game_event(
        &mut self,
        event: &GameEventEnvelope<'_>,
        ctx: &mut ItemContext<'_>,
    ) -> Result<(), ProtoError> {
        let payload = event.payload;
        let player = ctx.player();
        let mut tx = Vec::new();
        match event.event_type {
            GameEventType::USE_DONE => {
                let code = parse_inventory_word(payload)?;
                if let Some((source, target)) = self
                    .interaction
                    .complete_use(&mut self.transactions, &mut tx)
                {
                    let c = &mut self.completions;
                    c.use_revision += 1;
                    c.use_source = source;
                    c.use_target = target;
                    c.use_error = code;
                }
                if self.external.apply_use_done(code) {
                    self.probe_external(|| format!("open refused, error 0x{code:X}"));
                }
                if code != 0 {
                    self.notices.push(Notice::WeenieError(code));
                }
            }
            GameEventType::VIEW_CONTENTS => {
                let view = ViewContents::parse(payload)?;
                if let Some(transition) = self.external.apply_view_contents(view.container_guid) {
                    self.probe_external(|| format!("opened 0x{:08X}", view.container_guid));
                    Self::retire_external(transition, ctx);
                }
            }
            GameEventType::CLOSE_GROUND_CONTAINER => {
                let container = parse_inventory_word(payload)?;
                let closed = self.external.apply_close(container).is_some();
                self.probe_external(|| {
                    format!(
                        "server closed 0x{container:08X}{}",
                        if closed { "" } else { ", not the open one" }
                    )
                });
            }
            GameEventType::APPROACH_VENDOR => {
                let approach = ApproachVendor::parse(payload)?;
                self.apply_vendor(&approach, ctx.world, &mut tx);
            }
            GameEventType::REGISTER_TRADE => {
                self.trade
                    .apply_register(&RegisterTrade::parse(payload)?, player);
                self.trade_changed(ctx.world);
            }
            GameEventType::CLOSE_TRADE => {
                parse_trade_word(payload)?;
                let released = self.trade.view().partner_items.clone();
                let marks = self.trade.apply_close();
                mark(ctx.world, &marks);
                release_partner_items(ctx.world, released);
                self.interface(TRADE_CANCELLED);
                self.trade_changed(ctx.world);
            }
            GameEventType::ADD_TO_TRADE => {
                let update = AddToTrade::parse(payload)?;
                let partner_added = self.trade.view().open && update.side == trade::SIDE_PARTNER;
                let marks = self.trade.apply_add(&update);
                mark(ctx.world, &marks);
                if partner_added {
                    ctx.world
                        .note_destruction_queue(QueueNote::PartnerItemAdded(update.item_guid));
                }
                self.trade_changed(ctx.world);
            }
            GameEventType::REMOVE_FROM_TRADE => {
                let update = RemoveFromTrade::parse(payload)?;
                let view = self.trade.view();
                let partner_removed = view.open && view.partner_items.contains(&update.item_guid);
                let marks = self.trade.apply_remove(&update);
                mark(ctx.world, &marks);
                if partner_removed {
                    release_partner_items(ctx.world, vec![update.item_guid]);
                }
                self.trade_changed(ctx.world);
            }
            GameEventType::ACCEPT_TRADE => {
                self.trade.apply_accept(parse_trade_word(payload)?, player);
                self.trade_changed(ctx.world);
            }
            GameEventType::DECLINE_TRADE => {
                self.trade.apply_decline(parse_trade_word(payload)?, player);
                self.trade_changed(ctx.world);
            }
            GameEventType::RESET_TRADE => {
                parse_trade_word(payload)?;
                let released = if self.trade.view().open {
                    self.trade.view().partner_items.clone()
                } else {
                    Vec::new()
                };
                let marks = self.trade.apply_reset();
                mark(ctx.world, &marks);
                release_partner_items(ctx.world, released);
                self.trade_changed(ctx.world);
            }
            GameEventType::TRADE_FAILURE => {
                let marks = self.trade.apply_failure(&TradeFailure::parse(payload)?);
                mark(ctx.world, &marks);
                self.trade_changed(ctx.world);
            }
            GameEventType::CLEAR_TRADE_ACCEPTANCE => {
                self.trade.apply_clear_acceptance();
                self.trade_changed(ctx.world);
            }
            GameEventType::SALVAGE_OPERATIONS_RESULT => {
                let result = SalvageOperationsResult::parse(payload)?;
                self.salvage_result(&result, ctx.world);
            }
            GameEventType::QUERY_ITEM_MANA_RESPONSE => {
                let answer = QueryItemManaResponse::parse(payload)?;
                let change = self.item_mana.on_query_item_mana_response(
                    answer.item_guid,
                    answer.mana_percent,
                    answer.valid,
                );
                self.events.push(InventoryEvent::ItemMana(change));
            }
            GameEventType::HOUSE_PROFILE => {
                let profile = ac_proto::house::HouseProfile::parse(payload)?;
                self.house_purchase.apply_profile(profile);
            }
            _ => {}
        }
        self.handle_transaction_events(tx, ctx);
        Ok(())
    }

    /// What a salvage produced, in the salvaging text class: the bags, the
    /// items the server would not take, or the failure when it did neither.
    fn salvage_result(&mut self, result: &SalvageOperationsResult, world: &World) {
        use ac_core::chat::LogTextType;
        use ac_core::items::{
            SALVAGE_FAILED_TEXT, SalvageBag, salvage_success_text, salvage_unsuitable_text,
        };
        let mut text = |line: String| {
            self.notices
                .push(Notice::Text(line, LogTextType::Salvaging));
        };
        if !result.results.is_empty() {
            let bags: Vec<SalvageBag> = result
                .results
                .iter()
                .map(|r| SalvageBag {
                    material_type: r.material_type,
                    workmanship: r.workmanship,
                    units: r.units,
                })
                .collect();
            text(salvage_success_text(
                result.skill_id,
                &bags,
                result.augmentation_bonus_percent,
            ));
        }
        if !result.unsuitable_item_guids.is_empty() {
            let names: Vec<String> = result
                .unsuitable_item_guids
                .iter()
                .map(|&id| {
                    world
                        .item(id)
                        .map_or_else(|| "item".to_owned(), |i| i.appropriate_name().into_owned())
                })
                .collect();
            text(salvage_unsuitable_text(&names));
        }
        if result.results.is_empty() && result.unsuitable_item_guids.is_empty() {
            text(SALVAGE_FAILED_TEXT.to_owned());
        }
    }

    fn trade_changed(&mut self, world: &World) {
        self.events
            .push(InventoryEvent::Trade(self.trade_view(world)));
    }

    /// The trade as a host shows it: with the partner's and the staged
    /// items' names.
    #[must_use]
    pub fn trade_view(&self, world: &World) -> TradeView {
        let mut view = self.trade.view().clone();
        if view.partner_guid != 0 {
            view.partner_name = world.name(view.partner_guid).map(str::to_owned);
        }
        view.item_names = view
            .self_items
            .iter()
            .chain(&view.partner_items)
            .filter_map(|&guid| {
                world
                    .item(guid)
                    .map(|item| (guid, item.appropriate_name().into_owned()))
            })
            .collect();
        view
    }

    fn apply_vendor(
        &mut self,
        approach: &ApproachVendor,
        world: &mut World,
        tx: &mut Vec<TransactionEvent>,
    ) {
        let profile = VendorShopProfile {
            merchandise_item_types: approach.profile.merchandise_item_types,
            merchandise_min_value: approach.profile.merchandise_min_value,
            merchandise_max_value: approach.profile.merchandise_max_value,
            deal_magical_items: approach.profile.deal_magical_items,
            buy_price: approach.profile.buy_price,
            sell_price: approach.profile.sell_price,
            alternate_currency_wcid: approach.profile.alternate_currency_wcid,
            alternate_currency_amount: approach.profile.alternate_currency_amount,
            alternate_currency_plural_name: approach.profile.alternate_currency_plural_name.clone(),
        };
        let items = approach.items.iter().map(views::shop_item).collect();
        if let Some(transition) = self.vendor.apply(approach.vendor_guid, profile, items) {
            self.vendor_changed(&transition, world, tx);
        }
    }

    fn vendor_changed(
        &mut self,
        transition: &VendorTransition,
        world: &mut World,
        tx: &mut Vec<TransactionEvent>,
    ) {
        use ac_core::items::VendorTransitionKind as K;
        self.transactions.observe_vendor_transition(transition, tx);
        // A vendor's window shows its items, so none of them go.
        world.note_destruction_queue(QueueNote::VendorListed(
            self.vendor.items().iter().map(|i| i.item_guid).collect(),
        ));
        // The listings no longer shown leave the table first, so a staged
        // one is named as it leaves the buy list, before a closing or newly
        // opened shop empties the lists.
        for (guid, name) in self.shop_items.follow(&self.vendor, world) {
            if let Some(text) = self.lists.item_removed(guid, &name, world) {
                self.interface(text);
            }
        }
        match transition.kind {
            K::Opened | K::Closed | K::Reset => self.lists.restart(&self.vendor, world),
            K::Refreshed => self.lists.reset_alternate_currency(&self.vendor, world),
        }
        let reason = if transition.kind == K::Refreshed {
            VendorViewReason::Refreshed
        } else {
            VendorViewReason::Opened
        };
        self.vendor_view_event(world, reason);
    }

    /// The shop as a host shows it, `None` when none is open.
    #[must_use]
    pub fn vendor_view(&self, world: &World, reason: VendorViewReason) -> Option<VendorView> {
        (self.vendor.vendor_id() != 0).then(|| VendorView {
            vendor_guid: self.vendor.vendor_id(),
            name: world.name(self.vendor.vendor_id()).map(str::to_owned),
            profile: self.vendor.profile().clone(),
            items: self.vendor.items().to_vec(),
            lists: self.lists.view(&self.vendor, world),
            reason,
        })
    }

    fn vendor_view_event(&mut self, world: &World, reason: VendorViewReason) {
        let view = self.vendor_view(world, reason).map(Box::new);
        self.events.push(InventoryEvent::Vendor(view));
    }

    /// An appraisal answer (after the world applied it to the item table).
    pub fn accept_appraisal(&mut self, response: &IdentifyObjectResponse, world: &World) {
        let mut tx = Vec::new();
        let acceptance = self.interaction.accept_appraisal_response(
            response.guid,
            &mut self.transactions,
            &mut tx,
        );
        if acceptance.accepted {
            self.events
                .push(InventoryEvent::Appraisal(Box::new(AppraisalView {
                    name: world.name(response.guid).map(str::to_owned),
                    response: response.clone(),
                    present_in_ui: acceptance.present_in_ui,
                    opens_window: acceptance.first_response
                        && acceptance.origin == AppraisalOrigin::User,
                })));
        }
    }

    /// Follow the item table's events since the last call: the request in
    /// flight completes or fails on the server's answer about its object,
    /// and an auto-wield switch takes its next step.
    pub fn observe(&mut self, ctx: &mut ItemContext<'_>) {
        loop {
            let events = ctx.world.take_table_events();
            if events.is_empty() {
                return;
            }
            for event in &events {
                let mut tx = Vec::new();
                self.transactions
                    .observe_table_event(event, ctx.world.items(), &mut tx);
                self.handle_transaction_events(tx, ctx);
                if let TableEvent::Removed { object, .. } = event {
                    let id = object.object_id();
                    self.external.set_corpse_deleted(id);
                    // The open container's object left the world: the
                    // container closes and its contents lists are dropped
                    // as for any close; nothing goes to the server.
                    if let Some(transition) = self.external.apply_close(id) {
                        self.probe_external(|| format!("open 0x{id:08X} left the world, closed"));
                        Self::retire_external(transition, ctx);
                    }
                    self.forget_approach_target(object.object_id());
                    self.follow_vendor_lists(object, ctx.world);
                }
                self.follow_alternate_currency(event, ctx.world);
                self.follow_shortcut_ownership(event, ctx);
                if let TableEvent::MoveRequestFailed(failure) = event
                    && failure.weenie_error != 0
                    && !ac_core::chat::inventory_failure::suppresses_generic_failure_text(
                        failure.weenie_error,
                    )
                {
                    self.notices.push(Notice::WeenieError(failure.weenie_error));
                }
                self.observe_wield(event, ctx);
                self.follow_house_purchase(event, ctx.world);
            }
        }
    }

    /// An item left the table: it leaves the vendor's lists (a staged
    /// listing is named as it goes).
    fn follow_vendor_lists(&mut self, object: &ac_core::items::ClientObject, world: &mut World) {
        if self.vendor.vendor_id() == 0 {
            return;
        }
        let staged = self.lists.buy().get(object.object_id()).is_some()
            || self.lists.sell().get(object.object_id()).is_some();
        if let Some(text) = self
            .lists
            .item_removed(object.object_id(), &object.name, world)
        {
            self.interface(text);
        }
        if staged {
            self.vendor_view_event(world, VendorViewReason::Lists);
        }
    }

    /// The alternate currency appeared, moved or changed stack in the
    /// player's inventory: the inventory is the count from now on.
    fn follow_alternate_currency(&mut self, event: &TableEvent, world: &World) {
        let wcid = self.vendor.profile().alternate_currency_wcid;
        if self.vendor.vendor_id() == 0 || wcid == 0 {
            return;
        }
        let guid = match event {
            TableEvent::Added { object_id, .. }
            | TableEvent::StackSizeUpdated { object_id, .. } => *object_id,
            // A move of the currency counts wherever it went.
            TableEvent::Moved(moved) => {
                if world
                    .item(moved.item_id)
                    .is_some_and(|i| i.weenie_class_id == wcid)
                {
                    self.lists.observe_alternate_currency();
                }
                return;
            }
            TableEvent::Removed { object, .. } if object.weenie_class_id == wcid => {
                self.lists.observe_alternate_currency();
                return;
            }
            _ => return,
        };
        let player = world.player_guid().unwrap_or(0);
        if world.item(guid).is_some_and(|item| {
            item.weenie_class_id == wcid && world.items().is_owned_by_object(guid, player)
        }) {
            self.lists.observe_alternate_currency();
        }
    }

    fn handle_transaction_events(&mut self, events: Vec<TransactionEvent>, ctx: &ItemContext<'_>) {
        for event in events {
            match event {
                // Any answer to the placement's request ends it, a refusal
                // too (the item is drawn where the server keeps it).
                TransactionEvent::RequestCompleted(request)
                    if self.placement.is_some_and(|p| {
                        p.token == request.token && p.item_id == request.item_id
                    }) =>
                {
                    self.placement = None;
                }
                TransactionEvent::ObjectTableCleared => self.placement = None,
                _ => {}
            }
            let answered = match event {
                TransactionEvent::RequestCompleted(request) => Some((request, 0)),
                TransactionEvent::RequestFailed {
                    request,
                    weenie_error,
                } => Some((request, weenie_error)),
                _ => None,
            };
            if let Some((request, error)) = answered {
                let c = &mut self.completions;
                c.request_revision += 1;
                c.request_kind = Some(request.kind);
                c.request_item = request.item_id;
                c.request_error = error;
            }
            if let TransactionEvent::RequestFailed {
                request,
                weenie_error,
            } = event
            {
                use ac_core::items::InventoryRequestKind as K;
                let Some(item) = ctx.world.item(request.item_id) else {
                    continue;
                };
                let plural = matches!(
                    request.kind,
                    K::Merge | K::SplitToContainer | K::SplitToWorld
                );
                let name = if plural && !item.plural_name.is_empty() {
                    item.plural_name.clone()
                } else {
                    item.appropriate_name().into_owned()
                };
                if name.is_empty() {
                    continue;
                }
                if let Some(text) =
                    ac_core::chat::inventory_failure::compose(request.kind, &name, weenie_error)
                {
                    self.interface(text);
                }
            }
        }
    }

    /// Session end: nothing in flight, no vendor, container or trade.
    pub fn reset_session(&mut self, world: &mut World) {
        let mut tx = Vec::new();
        self.transactions.reset_session(&mut tx);
        self.interaction.reset_session();
        self.wield = wield::AutoWield::default();
        self.vendor.reset();
        self.lists.restart(&self.vendor, world);
        self.shop_items.follow(&self.vendor, world);
        self.use_radii.clear();
        self.reset_approaches();
        self.external.reset();
        self.open_pack = 0;
        self.placement = None;
        self.mark_salvage(&[], world);
        let marks = self.trade.clear();
        mark(world, &marks);
        self.house_purchase.close();
        self.mode = InteractionMode::None;
        self.shortcuts.clear();
        self.item_mana.clear();
        self.merged_into.clear();
    }
}

/// Apply trade marks to the item table.
/// The partner's items left the trade window: the destruction queue takes
/// what they hold.
fn release_partner_items(world: &mut World, released: Vec<u32>) {
    if !released.is_empty() {
        world.note_destruction_queue(QueueNote::PartnerItemsReleased(released));
    }
}

fn mark(world: &mut World, marks: &[trade::TradeMark]) {
    for &(guid, state) in marks {
        world.set_trade_state(guid, state);
    }
}

/// A buy or sell list entry.
fn amount(item: u32, amount: i32) -> ItemAmount {
    ItemAmount {
        amount,
        item_guid: item,
    }
}
