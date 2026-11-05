//! The open vendor beyond its listing: the listed items in the item table
//! (so they are named, assessed and selected like the player's own), the
//! buy and sell lists the player stages with their prices at the vendor's
//! rates, and the checks a purchase or sale passes before it goes out
//! (money, room, what the vendor buys).

use std::collections::{BTreeMap, BTreeSet};

use ac_core::items::vendor::{
    buy_price, evaluate_sell, per_unit_value, resolve_authored_stack_size, sell_price,
};
use ac_core::items::{
    EquipMask, ItemType, ItemUseable, PublicWeenieFlags, VendorSellCandidate, VendorSellRejection,
    VendorShopItem, VendorShopProfile, VendorStagingAddOutcome, VendorStagingEntry,
    VendorStagingList, VendorState, WeenieData,
};
use ac_core::properties::PropertyInt;

use crate::world::World;

/// What the player is told when a purchase costs more than they hold.
pub const NOT_ENOUGH_MONEY: &str = "You don't have enough money";
/// What the player is told when a purchase would not fit in the main pack.
pub const NOT_ENOUGH_ROOM: &str = "You must empty some slots in your backpack first";
/// What the player is told when only part of a stack is to be sold.
pub const CANNOT_SELL_PARTIAL_STACK: &str = "Cannot sell part of a stack";

// -- The listing in the item table -------------------------------------------

/// The listed items this session put in the item table, each with the
/// vendor that listed it.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ShopItems {
    owned: BTreeMap<u32, u32>,
}

impl ShopItems {
    /// How many listed items are in the table.
    #[must_use]
    pub fn owned_count(&self) -> usize {
        self.owned.len()
    }

    /// The item is a listing this session put in the table.
    #[must_use]
    pub fn owns(&self, guid: u32) -> bool {
        self.owned.contains_key(&guid)
    }

    /// The shop changed (opened, refreshed, closed): a listing no longer
    /// shown leaves the table unless it has moved out of the vendor's hands
    /// (a purchase now in the player's pack stays); every listing shown is
    /// put in (or refreshed) with the vendor as its container, except a guid
    /// the table already holds for something else, which is never
    /// overwritten. The listings that left the table come back as (id,
    /// name), in id order.
    pub fn follow(&mut self, vendor: &VendorState, world: &mut World) -> Vec<(u32, String)> {
        let listed: BTreeSet<u32> = vendor.items().iter().map(|i| i.item_guid).collect();
        let mut removed = Vec::new();
        for (&guid, &owner) in &self.owned {
            if listed.contains(&guid) {
                continue;
            }
            if let Some(name) = world
                .item(guid)
                .filter(|o| o.container_id == owner)
                .map(|o| o.name.clone())
            {
                world.remove_item(guid);
                removed.push((guid, name));
            }
        }
        let vendor_id = vendor.vendor_id();
        let mut next = BTreeMap::new();
        for item in vendor.items() {
            if !self.owned.contains_key(&item.item_guid) && world.item(item.item_guid).is_some() {
                continue;
            }
            world.ingest_item(&listing_data(item, vendor_id));
            next.insert(item.item_guid, vendor_id);
        }
        self.owned = next;
        removed
    }
}

/// A listing as an item record: the vendor holds it and nobody wields it,
/// whatever its description says; its stack is the authored one.
fn listing_data(item: &VendorShopItem, vendor_id: u32) -> WeenieData {
    WeenieData {
        guid: item.item_guid,
        name: item.name.clone(),
        item_type: item.item_type.map(ItemType),
        weenie_class_id: item.weenie_class_id,
        icon_id: item.icon_id,
        icon_overlay_id: item.icon_overlay_id,
        icon_underlay_id: item.icon_underlay_id,
        effects: item.effects,
        value: item.value,
        stack_size: Some(resolve_authored_stack_size(
            item.desc_stack_size,
            item.max_stack_size,
        )),
        stack_size_max: item.max_stack_size,
        burden: item.burden,
        container_id: Some(vendor_id),
        wielder_id: Some(0),
        valid_locations: item.valid_locations.map(EquipMask),
        current_wielded_location: None,
        priority: item.priority,
        items_capacity: item.items_capacity,
        containers_capacity: item.containers_capacity,
        structure: item.structure,
        max_structure: item.max_structure,
        workmanship: item.workmanship,
        useability: item.useability.map(ItemUseable),
        target_type: item.target_type.map(ItemType),
        hook_item_types: item.hook_item_types,
        hook_type: item.hook_type,
        public_weenie_bitfield: item.public_weenie_bitfield.map(PublicWeenieFlags),
        combat_use: item.combat_use,
        plural_name: item.plural_name.clone(),
        ammo_type: item.ammo_type,
        material_type: item.material_type,
        ..WeenieData::default()
    }
}

// -- Range ---------------------------------------------------------------------

/// A place as one frame for the whole world: landblocks are 192 m squares
/// numbered by the top two bytes of the cell id (x then y).
#[must_use]
pub fn global_position(position: &ac_proto::Position) -> glam::Vec3 {
    const LANDBLOCK_METERS: f32 = 192.0;
    glam::Vec3::new(
        f32::from((position.cell_id >> 24) as u8) * LANDBLOCK_METERS + position.origin.x,
        f32::from((position.cell_id >> 16) as u8) * LANDBLOCK_METERS + position.origin.y,
        position.origin.z,
    )
}

/// Whether the vendor's window stays open: with no place for the player
/// nothing is decided (it stays); a vendor no longer in the world closes
/// it; otherwise both bodies are measured as upright cylinders, each with
/// its own girth and height, and the gap must be within the vendor's use
/// radius (a vendor described without one has none).
#[must_use]
pub fn vendor_stays_open(
    player: Option<ac_physics::object_range::RangeBody>,
    vendor: Option<ac_physics::object_range::RangeBody>,
    use_radius: f32,
) -> bool {
    let Some(player) = player else {
        return true;
    };
    let Some(vendor) = vendor else {
        return false;
    };
    ac_physics::object_range::objects_in_range(player, vendor, f64::from(use_radius), true, false)
}

// -- Prices --------------------------------------------------------------------

/// What the player pays for `quantity` of a listed item at the vendor's
/// selling rate.
#[must_use]
pub fn listing_price(item: &VendorShopItem, profile: &VendorShopProfile, quantity: i32) -> i32 {
    sell_price(
        per_unit_value(item.value.unwrap_or(0), item.desc_stack_size),
        item.item_type.unwrap_or(0),
        profile.sell_price,
        quantity,
    )
}

/// What the vendor pays for `quantity` of one of the player's items at its
/// buying rate.
#[must_use]
pub fn offer_price(world: &World, guid: u32, profile: &VendorShopProfile, quantity: i32) -> i32 {
    world.item(guid).map_or(0, |item| {
        buy_price(
            per_unit_value(item.value, Some(item.stack_size)),
            item.item_type.bits(),
            profile.buy_price,
            quantity,
        )
    })
}

/// How many units buying a listed item means: its authored stack for a
/// stackable kind, else one.
#[must_use]
pub fn buy_quantity(item: &VendorShopItem) -> i32 {
    let stack = resolve_authored_stack_size(item.desc_stack_size, item.max_stack_size);
    stack.max(1)
}

// -- The buy and sell lists ----------------------------------------------------

/// One staged row as a host shows it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StagedRow {
    /// The item (a listing on the buy list, the player's item on the sell
    /// list).
    pub item_guid: u32,
    /// Its name.
    pub name: String,
    /// How many.
    pub quantity: i32,
    /// The row's price at the vendor's rate.
    pub price: i32,
}

/// The buy and sell lists, and what the player's money stands at.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct VendorListsView {
    /// The buy list, oldest first.
    pub buying: Vec<StagedRow>,
    /// The sell list, oldest first.
    pub selling: Vec<StagedRow>,
    /// "Buying N items worth Xp" as the vendor window words it.
    pub buying_text: String,
    /// "Selling N items worth Xp".
    pub selling_text: String,
    /// "You have Xp" (or the alternate currency held).
    pub purse_text: String,
    /// The money a purchase is measured against: the player's pyreals, or
    /// the alternate currency held less what a purchase spent that the
    /// inventory does not show yet (the vendor window's cost line).
    pub money: i32,
}

/// The vendor window's lists: what the player stages to buy and to sell,
/// and the alternate currency a purchase is known to have spent before the
/// inventory shows it.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct VendorLists {
    buy: VendorStagingList,
    sell: VendorStagingList,
    sell_marked: BTreeSet<u32>,
    last_alternate_currency_purchase: i32,
    alternate_currency_inventory_observed: bool,
}

impl VendorLists {
    /// The buy list.
    #[must_use]
    pub fn buy(&self) -> &VendorStagingList {
        &self.buy
    }

    /// The sell list.
    #[must_use]
    pub fn sell(&self) -> &VendorStagingList {
        &self.sell
    }

    /// A shop opened, or the open one closed or reset: both lists empty
    /// (their sale marks come off) and the currency tracking starts over.
    pub fn restart(&mut self, vendor: &VendorState, world: &mut World) {
        self.buy.clear();
        self.sell.clear();
        self.sync_sell_marks(world);
        self.reset_alternate_currency(vendor, world);
    }

    /// The open shop's listing was sent again: the currency tracking starts
    /// over; the lists stay.
    pub fn reset_alternate_currency(&mut self, vendor: &VendorState, world: &World) {
        self.last_alternate_currency_purchase = 0;
        let wcid = vendor.profile().alternate_currency_wcid;
        self.alternate_currency_inventory_observed =
            wcid != 0 && owned_of_class(world, wcid).next().is_some();
    }

    /// An item of the alternate currency appeared, moved or changed stack
    /// in the player's inventory: from now on the inventory is the count.
    pub fn observe_alternate_currency(&mut self) {
        self.alternate_currency_inventory_observed = true;
        self.last_alternate_currency_purchase = 0;
    }

    /// Stage `quantity` of a listed item on the buy list; the vendor's
    /// refusal of a row past the cap comes back as text.
    pub fn add_to_buy(&mut self, item_guid: u32, quantity: i32) -> Option<&'static str> {
        (self.buy.add(item_guid, quantity) == VendorStagingAddOutcome::Capped)
            .then_some(ac_core::items::TOO_MUCH_MESSAGE)
    }

    /// Take `amount` (-1 for all) of an item off the buy list.
    pub fn remove_from_buy(&mut self, item_guid: u32, amount: i32) -> bool {
        self.buy.remove(item_guid, amount)
    }

    /// Empty the buy list.
    pub fn clear_buy(&mut self) -> bool {
        self.buy.clear()
    }

    /// Stage one of the player's items on the sell list, checked as the
    /// vendor window checks a drop: a refusal comes back as its text. A
    /// pack holding items (the main pack included) sells its contents
    /// instead: each object in its item slots that the vendor takes is
    /// staged (one that is itself a pack holding something is not opened),
    /// the rest are skipped without a word. What fills its pack slots (side
    /// packs and spell foci) is never walked.
    pub fn add_to_sell(
        &mut self,
        item_guid: u32,
        vendor: &VendorState,
        world: &mut World,
        notices: &mut Vec<String>,
    ) -> bool {
        if let Err(rejection) = evaluate(item_guid, vendor.profile(), world) {
            notices.push(rejection.message().to_owned());
            return false;
        }
        if world.items().get_contents(item_guid).is_empty() {
            let quantity = world.item(item_guid).map_or(1, |i| i.stack_size.max(1));
            self.sell.stage(item_guid, quantity);
        }
        let contents = world.items().get_contained_items(item_guid);
        if !contents.is_empty() {
            let name = world
                .item(item_guid)
                .map(|i| i.name.clone())
                .filter(|n| !n.trim().is_empty())
                .unwrap_or_else(|| "item".to_owned());
            notices.push(format!("Selling contents of {name}"));
            for child in contents {
                if !world.items().get_contents(child).is_empty()
                    || evaluate(child, vendor.profile(), world).is_err()
                {
                    continue;
                }
                let quantity = world.item(child).map_or(1, |i| i.stack_size.max(1));
                self.sell.stage(child, quantity);
            }
        }
        self.sync_sell_marks(world);
        true
    }

    /// Take an item off the sell list.
    pub fn remove_from_sell(&mut self, item_guid: u32, world: &mut World) -> bool {
        let removed = self.sell.remove(item_guid, -1);
        self.sync_sell_marks(world);
        removed
    }

    /// Empty the sell list.
    pub fn clear_sell(&mut self, world: &mut World) -> bool {
        let cleared = self.sell.clear();
        self.sync_sell_marks(world);
        cleared
    }

    /// An item left the table: it leaves both lists; a listing leaving the
    /// buy list is named.
    pub fn item_removed(
        &mut self,
        item_guid: u32,
        name: &str,
        world: &mut World,
    ) -> Option<String> {
        self.sell.remove(item_guid, -1);
        self.sell_marked.remove(&item_guid);
        let _ = world;
        self.buy.remove(item_guid, -1).then(|| {
            let name = if name.trim().is_empty() {
                "that item"
            } else {
                name
            };
            format!("Removing {name} from shopping list")
        })
    }

    /// The buy list as one purchase: the rows, after the money and room
    /// checks; `Err` carries what the player is told.
    pub fn buy_all_request(
        &self,
        vendor: &VendorState,
        world: &World,
    ) -> Result<Vec<(i32, u32)>, &'static str> {
        let items: Vec<(i32, u32)> = self
            .buy
            .entries()
            .iter()
            .map(|e| (e.quantity, e.item_guid))
            .collect();
        let total = self.buy_total(vendor);
        if total > self.money(vendor, world) {
            return Err(NOT_ENOUGH_MONEY);
        }
        if !has_room(&items, vendor, world) {
            return Err(NOT_ENOUGH_ROOM);
        }
        Ok(items)
    }

    /// The buy list went out: it empties, and an alternate-currency price
    /// counts as spent until the inventory shows it.
    pub fn bought_all(&mut self, vendor: &VendorState) {
        let total = self.buy_total(vendor);
        self.record_alternate_currency_purchase(vendor.profile(), total);
        self.buy.clear();
    }

    /// A single purchase went out (the listing's buy button): its price
    /// counts as spent; the buy list is untouched.
    pub fn bought_one(&mut self, vendor: &VendorState, item_guid: u32, quantity: i32) {
        if let Some(item) = vendor.items().iter().find(|i| i.item_guid == item_guid) {
            let price = listing_price(item, vendor.profile(), quantity);
            self.record_alternate_currency_purchase(vendor.profile(), price);
        }
    }

    /// The sell list as one sale.
    #[must_use]
    pub fn sell_all_request(&self) -> Vec<(i32, u32)> {
        self.sell
            .entries()
            .iter()
            .map(|e| (e.quantity, e.item_guid))
            .collect()
    }

    /// The sell list went out: it empties.
    pub fn sold_all(&mut self, world: &mut World) {
        self.sell.clear();
        self.sync_sell_marks(world);
    }

    /// Fill the buy list from the component book: stage what `plan` says,
    /// drop the newest row when the price ceiling stopped it, and say what
    /// was short. Returns the texts in order.
    pub fn apply_component_fill(
        &mut self,
        plan: &ac_core::items::component_fill::ComponentFillPlan,
    ) -> Vec<String> {
        use ac_core::items::component_fill::{ABORTED_ON_PRICE_MESSAGE, format_short_components};
        let mut texts = Vec::new();
        for add in &plan.adds {
            if let Some(text) = self.add_to_buy(add.item_guid, add.quantity) {
                texts.push(text.to_owned());
            }
        }
        if plan.aborted_on_price {
            self.buy.remove_tail();
            texts.push(ABORTED_ON_PRICE_MESSAGE.to_owned());
        }
        if !plan.short_components.is_empty() {
            texts.push(format_short_components(&plan.short_components));
            texts.push(String::new());
        }
        texts
    }

    /// The buy list's price at the vendor's selling rate.
    #[must_use]
    pub fn buy_total(&self, vendor: &VendorState) -> i32 {
        self.buy
            .entries()
            .iter()
            .filter_map(|e| {
                vendor
                    .items()
                    .iter()
                    .find(|i| i.item_guid == e.item_guid)
                    .map(|i| listing_price(i, vendor.profile(), e.quantity))
            })
            .fold(0i32, i32::wrapping_add)
    }

    /// What the vendor pays for the sell list at its buying rate.
    #[must_use]
    pub fn sell_total(&self, vendor: &VendorState, world: &World) -> i32 {
        self.sell
            .entries()
            .iter()
            .filter(|e| world.item(e.item_guid).is_some())
            .map(|e| offer_price(world, e.item_guid, vendor.profile(), e.quantity))
            .fold(0i32, i32::wrapping_add)
    }

    /// The money a purchase is measured against: the player's pyreals, or
    /// the alternate currency held (less what a purchase spent that the
    /// inventory does not show yet).
    #[must_use]
    pub fn money(&self, vendor: &VendorState, world: &World) -> i32 {
        let profile = vendor.profile();
        if profile.alternate_currency_wcid == 0 {
            return coin_value(world);
        }
        self.alternate_currency_amount(profile, world)
    }

    fn alternate_currency_amount(&self, profile: &VendorShopProfile, world: &World) -> i32 {
        if profile.alternate_currency_wcid == 0 {
            return 0;
        }
        let mut found = false;
        let mut live = 0i64;
        for item in owned_of_class(world, profile.alternate_currency_wcid) {
            found = true;
            live += i64::from(item.stack_size.max(1));
        }
        let baseline = if found || self.alternate_currency_inventory_observed {
            live
        } else {
            i64::from(profile.alternate_currency_amount)
        };
        (baseline - i64::from(self.last_alternate_currency_purchase)).clamp(0, i64::from(i32::MAX))
            as i32
    }

    fn record_alternate_currency_purchase(&mut self, profile: &VendorShopProfile, price: i32) {
        if profile.alternate_currency_wcid == 0 || price <= 0 {
            return;
        }
        self.last_alternate_currency_purchase = price;
    }

    /// The lists as a host shows them.
    #[must_use]
    pub fn view(&self, vendor: &VendorState, world: &World) -> VendorListsView {
        let profile = vendor.profile();
        let buying = self
            .buy
            .entries()
            .iter()
            .filter_map(|e| {
                let item = vendor.items().iter().find(|i| i.item_guid == e.item_guid)?;
                Some(StagedRow {
                    item_guid: e.item_guid,
                    name: item.name.clone().unwrap_or_default(),
                    quantity: e.quantity,
                    price: listing_price(item, profile, e.quantity),
                })
            })
            .collect();
        let selling = self
            .sell
            .entries()
            .iter()
            .filter_map(|e| {
                let item = world.item(e.item_guid)?;
                Some(StagedRow {
                    item_guid: e.item_guid,
                    name: item.appropriate_name().into_owned(),
                    quantity: e.quantity,
                    price: offer_price(world, e.item_guid, profile, e.quantity),
                })
            })
            .collect();
        let count = |list: &VendorStagingList| {
            list.entries()
                .iter()
                .map(|e: &VendorStagingEntry| e.quantity)
                .fold(0i32, i32::wrapping_add)
        };
        VendorListsView {
            buying,
            selling,
            buying_text: transaction_text(
                "Buying",
                count(&self.buy),
                self.buy_total(vendor),
                profile,
            ),
            selling_text: transaction_text(
                "Selling",
                count(&self.sell),
                self.sell_total(vendor, world),
                profile,
            ),
            purse_text: self.purse_text(profile, world),
            money: self.money(vendor, world),
        }
    }

    fn purse_text(&self, profile: &VendorShopProfile, world: &World) -> String {
        if profile.alternate_currency_wcid != 0 {
            return format!(
                "You have {} {}.",
                self.alternate_currency_amount(profile, world),
                profile.alternate_currency_plural_name
            );
        }
        format!("You have {}p", group_thousands(coin_value(world)))
    }

    /// The sale mark on each item follows the sell list exactly.
    fn sync_sell_marks(&mut self, world: &mut World) {
        let staged: BTreeSet<u32> = self.sell.entries().iter().map(|e| e.item_guid).collect();
        for guid in self
            .sell_marked
            .difference(&staged)
            .copied()
            .collect::<Vec<_>>()
        {
            self.sell_marked.remove(&guid);
            world.set_sell_state(guid, 0);
        }
        for guid in staged {
            if self.sell_marked.insert(guid) {
                world.set_sell_state(guid, 1);
            }
        }
    }
}

/// "Buying 3 items worth 1,200p", or with the alternate currency's name.
#[must_use]
pub fn transaction_text(verb: &str, count: i32, total: i32, profile: &VendorShopProfile) -> String {
    let noun = if count == 1 { "item" } else { "items" };
    if profile.alternate_currency_wcid != 0 {
        return format!(
            "{verb} {count} {noun} worth {total} {}",
            profile.alternate_currency_plural_name
        );
    }
    format!("{verb} {count} {noun} worth {}p", group_thousands(total))
}

/// A number with comma thousands separators.
#[must_use]
pub fn group_thousands(value: i32) -> String {
    let digits = value.unsigned_abs().to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    if value < 0 { format!("-{out}") } else { out }
}

/// The player's pyreals (the coin value the server keeps on them).
#[must_use]
pub fn coin_value(world: &World) -> i32 {
    world
        .player_guid()
        .and_then(|p| world.item(p))
        .map_or(0, |p| p.properties.get_int(PropertyInt::COIN_VALUE.0, 0))
}

fn owned_of_class(world: &World, wcid: u32) -> impl Iterator<Item = &ac_core::items::ClientObject> {
    let player = world.player_guid().unwrap_or(0);
    world.items().objects().filter(move |item| {
        item.weenie_class_id == wcid && world.items().is_owned_by_object(item.object_id(), player)
    })
}

/// Whether the vendor takes the item, as the vendor window checks a drop.
fn evaluate(
    item_guid: u32,
    profile: &VendorShopProfile,
    world: &World,
) -> Result<(), VendorSellRejection> {
    let Some(item) = world.item(item_guid) else {
        return Err(VendorSellRejection::WrongType);
    };
    let player = world.player_guid().unwrap_or(0);
    evaluate_sell(
        &VendorSellCandidate {
            owned_by_player: world.items().is_owned_by_object(item_guid, player),
            contained_item_count: i32::try_from(world.items().get_contained_items(item_guid).len())
                .unwrap_or(i32::MAX),
            item_type: item.item_type.bits(),
            per_unit_value: per_unit_value(item.value, Some(item.stack_size)),
            public_weenie_bitfield: item.public_weenie_bitfield.map_or(0, |f| f.bits()),
        },
        profile,
    )
}

/// Whether a purchase fits in the player's own pack: the pack slots it
/// takes first, then the item slots; side packs do not count.
#[must_use]
pub fn has_room(items: &[(i32, u32)], vendor: &VendorState, world: &World) -> bool {
    has_room_in(
        items,
        vendor.items(),
        world.items(),
        world.player_guid().unwrap_or(0),
    )
}

/// [`has_room`] over a listing and an object table (the vendor window
/// checks a single purchase with it before taking the row off the buy
/// list).
#[must_use]
pub fn has_room_in(
    items: &[(i32, u32)],
    listings: &[VendorShopItem],
    table: &ac_core::items::ClientObjectTable,
    player: u32,
) -> bool {
    let (mut item_slots, mut container_slots) = (0i32, 0i32);
    for &(amount, guid) in items {
        let Some(item) = listings.iter().find(|i| i.item_guid == guid) else {
            continue;
        };
        let is_container = item.item_type.unwrap_or(0) & ItemType::CONTAINER.bits() != 0;
        let take = if item.max_stack_size.unwrap_or(1) > 1 {
            1
        } else {
            amount
        };
        if is_container {
            container_slots = container_slots.wrapping_add(take);
        } else {
            item_slots = item_slots.wrapping_add(take);
        }
    }
    let (mut items_used, mut containers_used) = (0i32, 0i32);
    for &guid in table.get_contents(player) {
        let is_container = table.get(guid).is_some_and(|o| {
            o.container_type_hint != 0 || o.item_type.bits() & ItemType::CONTAINER.bits() != 0
        });
        if is_container {
            containers_used += 1;
        } else {
            items_used += 1;
        }
    }
    let me = table.get(player);
    let free_containers = me.map_or(0, |p| p.containers_capacity) - containers_used;
    if container_slots > free_containers {
        return false;
    }
    let free_items = me.map_or(0, |p| p.items_capacity) - items_used;
    item_slots <= free_items
}

#[cfg(test)]
mod tests {
    //! The materializer cases from OpenAC
    //! `tests/AcDream.Runtime.Tests/Gameplay/VendorShopItemMaterializerTests.cs`
    //! and `RuntimeVendorLifecycleTests.cs`; the list arithmetic from
    //! `src/AcDream.App/UI/Layout/VendorUiController.cs` (its money, room and
    //! transaction-text rules).

    use ac_core::items::{ItemType, VendorShopItem, VendorShopProfile, VendorState};
    use ac_proto::{CreateObject, Inbound, ModelData, PhysicsTimestamps, PublicWeenieDesc};

    use super::*;
    use crate::world::tests::physics;

    const VENDOR: u32 = 0x4000_1000;
    const OTHER_VENDOR: u32 = 0x4000_2000;
    const ITEM_A: u32 = 0x5000_2000;
    const ITEM_B: u32 = 0x5000_2001;

    fn item(guid: u32, name: &str, desc_stack_size: Option<i32>) -> VendorShopItem {
        VendorShopItem {
            item_guid: guid,
            stack_size: -1,
            weenie_class_id: 1,
            name: Some(name.to_owned()),
            item_type: Some(ItemType::MISC.bits()),
            icon_id: 0x1234,
            value: Some(10),
            desc_stack_size,
            ..VendorShopItem::default()
        }
    }

    fn open(vendor: &mut VendorState, id: u32, items: Vec<VendorShopItem>) {
        vendor.apply(id, VendorShopProfile::default(), items);
    }

    #[test]
    fn each_listed_item_enters_the_table_held_by_the_vendor() {
        // Apply_MaterializesEachShopItemWithVendorAsContainer.
        let (mut world, mut vendor, mut shop) =
            (World::new(), VendorState::new(), ShopItems::default());
        open(
            &mut vendor,
            VENDOR,
            vec![
                item(ITEM_A, "Iron Sword", None),
                item(ITEM_B, "Bread", None),
            ],
        );
        shop.follow(&vendor, &mut world);
        assert_eq!(world.item(ITEM_A).unwrap().container_id, VENDOR);
        assert_eq!(world.item(ITEM_B).unwrap().container_id, VENDOR);
        assert_eq!(world.item(ITEM_A).unwrap().name, "Iron Sword");
        assert_eq!(shop.owned_count(), 2);
        assert!(shop.owns(ITEM_A) && shop.owns(ITEM_B));
    }

    #[test]
    fn close_and_reset_take_every_listing_out() {
        // Close_RemovesEveryMaterializedItem, Reset_RemovesEveryMaterializedItem.
        for reset in [false, true] {
            let (mut world, mut vendor, mut shop) =
                (World::new(), VendorState::new(), ShopItems::default());
            open(
                &mut vendor,
                VENDOR,
                vec![item(ITEM_A, "A", None), item(ITEM_B, "B", None)],
            );
            shop.follow(&vendor, &mut world);
            if reset {
                vendor.reset();
            } else {
                vendor.close();
            }
            shop.follow(&vendor, &mut world);
            assert!(world.item(ITEM_A).is_none() && world.item(ITEM_B).is_none());
            assert_eq!(shop.owned_count(), 0);
        }
    }

    #[test]
    fn another_vendor_replaces_the_first_vendors_listing() {
        // DifferentVendorSupersedes_RemovesPriorVendorsItemsBeforeMaterializingTheNewOnes.
        let (mut world, mut vendor, mut shop) =
            (World::new(), VendorState::new(), ShopItems::default());
        open(
            &mut vendor,
            VENDOR,
            vec![item(ITEM_A, "First Vendor Item", None)],
        );
        shop.follow(&vendor, &mut world);
        const NEW_ITEM: u32 = 0x5000_3000;
        open(
            &mut vendor,
            OTHER_VENDOR,
            vec![item(NEW_ITEM, "Second Vendor Item", None)],
        );
        shop.follow(&vendor, &mut world);
        assert!(world.item(ITEM_A).is_none());
        assert_eq!(world.item(NEW_ITEM).unwrap().container_id, OTHER_VENDOR);
        assert_eq!(shop.owned_count(), 1);
    }

    #[test]
    fn a_refresh_keeps_a_still_listed_item_and_updates_it_in_place() {
        // Refreshed_SameVendor_DoesNotFireObjectRemovedForStillListedItems.
        let (mut world, mut vendor, mut shop) =
            (World::new(), VendorState::new(), ShopItems::default());
        open(&mut vendor, VENDOR, vec![item(ITEM_A, "Chainmail", None)]);
        shop.follow(&vendor, &mut world);
        world.take_table_events();
        open(
            &mut vendor,
            VENDOR,
            vec![item(ITEM_A, "Chainmail", Some(5))],
        );
        shop.follow(&vendor, &mut world);
        assert!(
            !world
                .take_table_events()
                .iter()
                .any(|e| matches!(e, ac_core::items::TableEvent::Removed { .. }))
        );
        assert_eq!(shop.owned_count(), 1);
        assert_eq!(world.item(ITEM_A).unwrap().stack_size, 5);
    }

    #[test]
    fn the_stack_is_the_authored_one() {
        // Apply_UnlimitedStockNoDescStackSize_FallsBackToMaxStackSize,
        // Apply_LiveAceWireShape_DescOneMaxHundred_ResolvesToTheAuthoredCeiling,
        // Apply_UnlimitedSupplySentinel_FallsBackToNonSplittableDefault.
        for (desc, max, expected) in [
            (None, Some(1000), 1000),
            (Some(1), Some(100), 100),
            (None, None, 1),
        ] {
            let (mut world, mut vendor, mut shop) =
                (World::new(), VendorState::new(), ShopItems::default());
            let mut listing = item(ITEM_A, "Taper", desc);
            listing.max_stack_size = max;
            open(&mut vendor, VENDOR, vec![listing]);
            shop.follow(&vendor, &mut world);
            assert_eq!(world.item(ITEM_A).unwrap().stack_size, expected);
        }
    }

    #[test]
    fn a_listing_no_longer_shown_leaves_unless_it_was_bought() {
        // Refreshed_ItemNoLongerListed_IsRemoved,
        // Refreshed_ItemPurchased_ReparentedIntoBuyerPack_Survives.
        let (mut world, mut vendor, mut shop) =
            (World::new(), VendorState::new(), ShopItems::default());
        open(
            &mut vendor,
            VENDOR,
            vec![item(ITEM_A, "Unique Sword", None), item(ITEM_B, "B", None)],
        );
        shop.follow(&vendor, &mut world);
        open(&mut vendor, VENDOR, vec![item(ITEM_B, "B", None)]);
        shop.follow(&vendor, &mut world);
        assert!(world.item(ITEM_A).is_none());
        assert!(world.item(ITEM_B).is_some());

        const BUYER: u32 = 0x5000_9000;
        open(
            &mut vendor,
            VENDOR,
            vec![item(ITEM_A, "Unique Sword", None), item(ITEM_B, "B", None)],
        );
        shop.follow(&vendor, &mut world);
        world.ingest_item(&WeenieData {
            guid: ITEM_A,
            container_id: Some(BUYER),
            ..WeenieData::default()
        });
        open(&mut vendor, VENDOR, vec![item(ITEM_B, "B", None)]);
        shop.follow(&vendor, &mut world);
        assert_eq!(world.item(ITEM_A).unwrap().container_id, BUYER);
        assert!(!shop.owns(ITEM_A));
        assert_eq!(shop.owned_count(), 1);
    }

    #[test]
    fn a_guid_the_table_holds_for_something_else_is_never_overwritten() {
        // CollidingGuid_AlreadyOwnedBySomethingElse_IsNeverClobbered.
        let (mut world, mut vendor, mut shop) =
            (World::new(), VendorState::new(), ShopItems::default());
        const LIVE_OWNER: u32 = 0x6000_0001;
        world.ingest_item(&WeenieData {
            guid: ITEM_A,
            name: Some("Definitely Not A Shop Item".into()),
            container_id: Some(LIVE_OWNER),
            ..WeenieData::default()
        });
        open(
            &mut vendor,
            VENDOR,
            vec![item(ITEM_A, "Shop Listing With A Colliding Guid", None)],
        );
        shop.follow(&vendor, &mut world);
        let survivor = world.item(ITEM_A).unwrap();
        assert_eq!(survivor.name, "Definitely Not A Shop Item");
        assert_eq!(survivor.container_id, LIVE_OWNER);
        assert!(!shop.owns(ITEM_A));
        vendor.close();
        shop.follow(&vendor, &mut world);
        assert!(world.item(ITEM_A).is_some());
    }

    #[test]
    fn the_whole_description_reaches_the_item_record() {
        // ApproachVendorEvent_CarriesTheCompleteItemDescriptionThroughToTheObjectTable.
        let (mut world, mut vendor, mut shop) =
            (World::new(), VendorState::new(), ShopItems::default());
        let listing = VendorShopItem {
            item_type: Some(ItemType::MELEE_WEAPON.bits()),
            valid_locations: Some(EquipMask::MELEE_WEAPON.bits()),
            priority: Some(2),
            items_capacity: Some(7),
            containers_capacity: Some(2),
            structure: Some(40),
            max_structure: Some(100),
            workmanship: Some(8.5),
            burden: Some(450),
            material_type: Some(60),
            target_type: Some(0x80),
            combat_use: Some(1),
            ammo_type: Some(3),
            public_weenie_bitfield: Some(PublicWeenieFlags::HEALER.bits()),
            useability: Some(8),
            hook_item_types: Some(0xFFFF),
            hook_type: Some(2),
            ..item(ITEM_A, "Silifi", None)
        };
        open(&mut vendor, VENDOR, vec![listing]);
        shop.follow(&vendor, &mut world);
        let o = world.item(ITEM_A).unwrap();
        assert_eq!(o.valid_locations, EquipMask::MELEE_WEAPON);
        assert_eq!(
            (o.priority, o.items_capacity, o.containers_capacity),
            (2, 7, 2)
        );
        assert_eq!((o.structure, o.max_structure, o.burden), (40, 100, 450));
        assert!((o.workmanship - 8.5).abs() < f32::EPSILON);
        assert_eq!(o.material_type, Some(60));
        assert_eq!(o.target_type, Some(ItemType(0x80)));
        assert_eq!((o.combat_use, o.ammo_type), (Some(1), Some(3)));
        assert_eq!(o.public_weenie_bitfield, Some(PublicWeenieFlags::HEALER));
        assert_eq!(o.useability, Some(ItemUseable(8)));
        assert_eq!((o.hook_item_types, o.hook_type), (0xFFFF, 2));
        assert_eq!((o.container_id, o.wielder_id), (VENDOR, 0));
        assert_eq!(o.currently_equipped_location, EquipMask::NONE);
    }

    // -- Lists -----------------------------------------------------------------

    const PLAYER: u32 = 0x5000_000A;
    const GEM: u32 = 0x6000_0002;
    const PACK: u32 = 0x6000_0001;

    fn create(guid: u32, name: &str, edit: impl FnOnce(&mut PublicWeenieDesc)) -> Inbound<'static> {
        let mut desc = PublicWeenieDesc {
            name: name.into(),
            ..PublicWeenieDesc::default()
        };
        edit(&mut desc);
        Inbound::CreateObject(Box::new(CreateObject {
            guid,
            model: ModelData::default(),
            physics: physics(
                PhysicsTimestamps {
                    instance: 1,
                    ..PhysicsTimestamps::default()
                },
                None,
            ),
            weenie: Some(desc),
        }))
    }

    /// A player with 1,000 pyreals, room for two items and one pack, holding
    /// a gem (value 100) and an empty pack, at a gem vendor buying at 0.5
    /// and selling at 1.5 two listings: a diamond (value 100) and a sack.
    fn shop() -> (World, VendorState) {
        let mut world = World::new();
        world.set_player(PLAYER);
        world.apply(&create(PLAYER, "Horan", |d| {
            d.item_type = ItemType::CREATURE.bits();
            d.items_capacity = Some(2);
            d.containers_capacity = Some(2);
            d.container_id = None;
        }));
        world.apply(&create(GEM, "Gem", |d| {
            d.item_type = ItemType::GEM.bits();
            d.value = Some(100);
            d.container_id = Some(PLAYER);
        }));
        world.apply(&create(PACK, "Pack", |d| {
            d.item_type = ItemType::CONTAINER.bits();
            d.container_id = Some(PLAYER);
        }));
        world.apply(&Inbound::PrivateUpdatePropertyInt(
            ac_proto::PrivateUpdatePropertyInt {
                sequence: 1,
                property: PropertyInt::COIN_VALUE.0,
                value: 1000,
            },
        ));
        let mut vendor = VendorState::new();
        vendor.apply(
            0x7000_0003,
            VendorShopProfile {
                merchandise_item_types: ItemType::GEM.bits(),
                merchandise_min_value: 0,
                merchandise_max_value: 1000,
                buy_price: 0.5,
                sell_price: 1.5,
                ..VendorShopProfile::default()
            },
            vec![
                VendorShopItem {
                    item_type: Some(ItemType::GEM.bits()),
                    value: Some(100),
                    ..item(ITEM_A, "Diamond", None)
                },
                VendorShopItem {
                    item_type: Some(ItemType::CONTAINER.bits()),
                    value: Some(10),
                    ..item(ITEM_B, "Sack", None)
                },
            ],
        );
        (world, vendor)
    }

    #[test]
    fn the_lists_price_at_the_vendors_rates_and_word_the_totals() {
        let (mut world, vendor) = shop();
        let mut lists = VendorLists::default();
        assert_eq!(lists.add_to_buy(ITEM_A, 2), None);
        let mut notices = Vec::new();
        assert!(lists.add_to_sell(GEM, &vendor, &mut world, &mut notices));
        assert!(notices.is_empty());
        // Selling price 1.5 x 100 x 2; buying price 0.5 x 100.
        let view = lists.view(&vendor, &world);
        assert_eq!(view.buying[0].price, 300);
        assert_eq!(view.selling[0].price, 50);
        assert_eq!(view.buying_text, "Buying 2 items worth 300p");
        assert_eq!(view.selling_text, "Selling 1 item worth 50p");
        assert_eq!(view.purse_text, "You have 1,000p");
        // The sale mark follows the sell list.
        assert_eq!(world.item(GEM).unwrap().sell_state, 1);
        lists.clear_sell(&mut world);
        assert_eq!(world.item(GEM).unwrap().sell_state, 0);
    }

    #[test]
    fn a_purchase_needs_the_money_and_room_in_the_main_pack() {
        let (world, vendor) = shop();
        let mut lists = VendorLists::default();
        lists.add_to_buy(ITEM_A, 1);
        assert_eq!(
            lists.buy_all_request(&vendor, &world),
            Ok(vec![(1, ITEM_A)])
        );
        // Seven diamonds cost 1,050p: more than the purse.
        lists.add_to_buy(ITEM_A, 6);
        assert_eq!(
            lists.buy_all_request(&vendor, &world),
            Err(NOT_ENOUGH_MONEY)
        );
        // One gem and the pack fill the item slots (2) less one: two more
        // unstackable items do not fit.
        assert!(has_room(&[(1, ITEM_A)], &vendor, &world));
        assert!(!has_room(&[(2, ITEM_A)], &vendor, &world));
        assert!(has_room(&[(1, ITEM_B)], &vendor, &world));
        assert!(!has_room(&[(2, ITEM_B)], &vendor, &world));
    }

    #[test]
    fn the_vendor_refuses_what_it_does_not_buy() {
        let (mut world, vendor) = shop();
        let mut lists = VendorLists::default();
        let mut notices = Vec::new();
        // The pack is empty and not a gem.
        assert!(!lists.add_to_sell(PACK, &vendor, &mut world, &mut notices));
        assert_eq!(notices, ["You cannot sell that here"]);
        // Nothing the player does not carry (a listing in the table is the
        // vendor's).
        notices.clear();
        ShopItems::default().follow(&vendor, &mut world);
        assert!(!lists.add_to_sell(ITEM_A, &vendor, &mut world, &mut notices));
        assert_eq!(notices, ["You can only sell items you are carrying"]);
    }

    /// The main pack as the server lists it: the gem in an item slot, the
    /// pack and a spell focus in pack slots (container types 0, 1 and 2,
    /// as the inventory listing sends them), and a second gem inside the
    /// side pack. The focus is the "Foci of Enchantment" weenie (wcid
    /// 15268): item type Misc, value 500. The vendor buys gems and Misc.
    fn main_pack_with_a_focus() -> (World, VendorState) {
        const FOCUS: u32 = 0x6000_0003;
        const PACKED_GEM: u32 = 0x6000_0004;
        let (mut world, mut vendor) = shop();
        world.apply(&create(FOCUS, "Foci of Enchantment", |d| {
            d.item_type = ItemType::MISC.bits();
            d.value = Some(500);
            d.container_id = Some(PLAYER);
        }));
        world.apply(&create(PACKED_GEM, "Packed Gem", |d| {
            d.item_type = ItemType::GEM.bits();
            d.value = Some(100);
            d.container_id = Some(PACK);
        }));
        world.mutate_items_for_test(|table, events| {
            let entry = |guid, container_type| ac_core::items::ContainerContentEntry {
                guid,
                container_type,
            };
            table.replace_contents(
                PLAYER,
                &[entry(GEM, 0), entry(PACK, 1), entry(FOCUS, 2)],
                events,
            );
            table.replace_contents(PACK, &[entry(PACKED_GEM, 0)], events);
        });
        let profile = VendorShopProfile {
            merchandise_item_types: (ItemType::GEM | ItemType::MISC).bits(),
            ..vendor.profile().clone()
        };
        let items = vendor.items().to_vec();
        vendor.apply(0x7000_0003, profile, items);
        (world, vendor)
    }

    #[test]
    fn the_main_pack_sells_its_item_slots_only() {
        let (mut world, vendor) = main_pack_with_a_focus();
        let mut lists = VendorLists::default();
        let mut notices = Vec::new();
        assert!(lists.add_to_sell(PLAYER, &vendor, &mut world, &mut notices));
        assert_eq!(notices, ["Selling contents of Horan"]);
        // The focus and the side pack take pack slots and are not walked;
        // nothing inside the side pack is either.
        let staged: Vec<u32> = lists.sell().entries().iter().map(|e| e.item_guid).collect();
        assert_eq!(staged, [GEM]);
    }

    #[test]
    fn a_main_pack_with_only_pack_slots_filled_is_judged_as_the_character() {
        let (mut world, vendor) = main_pack_with_a_focus();
        world.mutate_items_for_test(|table, events| {
            table.replace_contents(
                PLAYER,
                &[ac_core::items::ContainerContentEntry {
                    guid: PACK,
                    container_type: 1,
                }],
                events,
            );
        });
        let mut lists = VendorLists::default();
        let mut notices = Vec::new();
        assert!(!lists.add_to_sell(PLAYER, &vendor, &mut world, &mut notices));
        assert_eq!(notices, ["You cannot sell that here"]);
        assert!(lists.sell().is_empty());
    }

    #[test]
    fn a_staged_listing_that_leaves_is_named() {
        let (mut world, _vendor) = shop();
        let mut lists = VendorLists::default();
        lists.add_to_buy(ITEM_A, 1);
        assert_eq!(
            lists.item_removed(ITEM_A, "Diamond", &mut world),
            Some("Removing Diamond from shopping list".to_owned())
        );
        assert_eq!(lists.item_removed(ITEM_A, "Diamond", &mut world), None);
    }

    // -- Range (OpenAC `tests/AcDream.Runtime.Tests/Gameplay/RuntimeVendorRangeQueryTests.cs`)

    fn at(x: f32, z: f32, radius: f32, height: f32) -> Option<ac_physics::object_range::RangeBody> {
        Some(ac_physics::object_range::RangeBody {
            position: glam::Vec3::new(x, 100.0, z),
            radius,
            height,
        })
    }

    #[test]
    fn within_the_use_radius_the_window_stays_open() {
        // EnforceRange_PlayerWithinVendorUseRadius_LeavesSessionOpen,
        // ..._PlayerMovesBeyondVendorUseRadius_ClosesTheSession,
        // ..._PlayerStaysWithinRadiusAfterSmallMove_LeavesSessionOpen.
        assert!(vendor_stays_open(
            at(100.0, 0.0, 0.0, 0.0),
            at(102.0, 0.0, 0.0, 0.0),
            3.0
        ));
        assert!(!vendor_stays_open(
            at(122.0, 0.0, 0.0, 0.0),
            at(102.0, 0.0, 0.0, 0.0),
            3.0
        ));
        assert!(vendor_stays_open(
            at(100.5, 0.0, 0.0, 0.0),
            at(102.0, 0.0, 0.0, 0.0),
            3.0
        ));
    }

    #[test]
    fn a_vendor_without_a_use_radius_has_none() {
        // EnforceRange_VendorUseRadiusAbsent_UsesRawZeroWithNoFallback.
        assert!(vendor_stays_open(
            at(100.0, 0.0, 0.0, 0.0),
            at(100.0, 0.0, 0.0, 0.0),
            0.0
        ));
        assert!(!vendor_stays_open(
            at(100.05, 0.0, 0.0, 0.0),
            at(100.0, 0.0, 0.0, 0.0),
            0.0
        ));
    }

    #[test]
    fn a_vendor_gone_from_the_world_closes_and_an_unplaced_player_decides_nothing() {
        // EnforceRange_VendorEntityRetired_ClosesTheSession; a player with no
        // place returns early in OpenAC.
        assert!(!vendor_stays_open(at(100.0, 0.0, 0.0, 0.0), None, 3.0));
        assert!(vendor_stays_open(None, None, 3.0));
    }

    #[test]
    fn a_vendor_up_a_step_is_measured_with_both_heights() {
        // EnforceRange_VendorUpAStepButWithinItsUseRadius_LeavesSessionOpen,
        // ..._TheStepAloneIsWhatTheZeroHeightMeasureAddedTo,
        // ..._VendorUpAStepAndGenuinelyTooFar_ClosesTheSession: bodies half a
        // metre wide and 1.8 m tall, use radius 2 m.
        let player = at(100.0, 5.0, 0.5, 1.8);
        assert!(vendor_stays_open(player, at(102.9, 5.6, 0.5, 1.8), 2.0));
        assert!(!vendor_stays_open(
            at(100.0, 5.0, 0.5, 0.0),
            at(102.9, 5.6, 0.5, 0.0),
            2.0
        ));
        assert!(!vendor_stays_open(player, at(108.0, 5.6, 0.5, 1.8), 2.0));
    }

    #[test]
    fn an_alternate_currency_counts_from_the_profile_until_the_inventory_shows_it() {
        // VendorUiController.ResolveAlternateCurrencyAmount and
        // RecordAlternateCurrencyPurchase: the shop's count before any
        // currency is seen in the pack, less a purchase not yet shown.
        let (world, mut vendor) = shop();
        let mut profile = vendor.profile().clone();
        profile.alternate_currency_wcid = 0x3400;
        profile.alternate_currency_amount = 57;
        profile.alternate_currency_plural_name = "Trade Notes".into();
        let items = vendor.items().to_vec();
        vendor.apply(0x7000_0003, profile, items);
        let mut lists = VendorLists::default();
        lists.reset_alternate_currency(&vendor, &world);
        assert_eq!(lists.money(&vendor, &world), 57);
        // One diamond at 1.5 x 100.
        lists.bought_one(&vendor, ITEM_A, 1);
        assert_eq!(lists.money(&vendor, &world), 0);
        assert_eq!(
            lists.view(&vendor, &world).purse_text,
            "You have 0 Trade Notes."
        );
        // The inventory is the count once the currency shows in it (none
        // here).
        lists.observe_alternate_currency();
        assert_eq!(lists.money(&vendor, &world), 0);
        assert_eq!(
            transaction_text("Buying", 2, 30, vendor.profile()),
            "Buying 2 items worth 30 Trade Notes"
        );
    }

    #[test]
    fn thousands_are_grouped() {
        assert_eq!(group_thousands(61_000_000), "61,000,000");
        assert_eq!(group_thousands(999), "999");
        assert_eq!(group_thousands(-1234), "-1,234");
    }
}
