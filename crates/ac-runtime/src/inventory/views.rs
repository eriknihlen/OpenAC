//! The inventory as owned data for a host: the main pack, each side pack's
//! contents and what is worn, and a vendor listing entry from the wire.

use ac_core::items::{ClientObject, EquipMask, VendorShopItem, placement};
use ac_proto::vendor::VendorItem;

use crate::world::World;

/// One item as a host lists it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ItemSummary {
    /// Server object id.
    pub guid: u32,
    /// The name for its stack size.
    pub name: String,
    /// How many.
    pub stack_size: i32,
    /// Value of the whole stack, pyreals.
    pub value: i32,
    /// Burden of the whole stack.
    pub burden: i32,
    /// Item type bits.
    pub item_type: u32,
    /// The container holding it; 0 when not in one.
    pub container_id: u32,
    /// Its slot in the container; -1 when unknown.
    pub slot: i32,
    /// Who wields it; 0 when not wielded.
    pub wielder_id: u32,
    /// Where it is worn; 0 when not worn.
    pub equip_location: u32,
    /// It is a pack (holds items).
    pub is_container: bool,
    /// 1 while it is staged in a secure trade.
    pub trade_state: i32,
}

impl ItemSummary {
    /// The summary of one item record.
    #[must_use]
    pub fn of(item: &ClientObject) -> Self {
        Self {
            guid: item.object_id(),
            name: item.appropriate_name().into_owned(),
            stack_size: item.stack_size,
            value: item.value,
            burden: item.burden,
            item_type: item.item_type.bits(),
            container_id: item.container_id,
            slot: item.container_slot,
            wielder_id: item.wielder_id,
            equip_location: item.currently_equipped_location.bits(),
            is_container: placement::is_container(item),
            trade_state: item.trade_state,
        }
    }
}

/// A side pack and its contents.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PackView {
    /// The pack.
    pub pack: ItemSummary,
    /// What it holds, in slot order.
    pub items: Vec<ItemSummary>,
}

/// The player's inventory as a host shows it.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct InventoryView {
    /// The player.
    pub player_guid: u32,
    /// Everything carried and wielded, summed.
    pub burden: i32,
    /// The main pack's plain items, in slot order.
    pub main_pack: Vec<ItemSummary>,
    /// The side packs (and foci) in the main pack, in slot order.
    pub packs: Vec<PackView>,
    /// What is worn or wielded.
    pub equipped: Vec<ItemSummary>,
    /// The open ground container (a chest or corpse being looted) and what
    /// it holds, once its contents arrived.
    pub ground: Option<PackView>,
}

/// An open ground container and its contents as the item table has them;
/// `None` when the table does not know the container.
#[must_use]
pub fn ground_view(world: &World, container: u32) -> Option<PackView> {
    let items = world.items();
    let pack = items.get(container)?;
    Some(PackView {
        pack: ItemSummary::of(pack),
        items: items
            .get_contents(container)
            .iter()
            .filter_map(|&id| items.get(id))
            .map(ItemSummary::of)
            .collect(),
    })
}

/// The player's inventory from the item table.
#[must_use]
pub fn inventory_view(world: &World) -> InventoryView {
    let Some(player) = world.player_guid() else {
        return InventoryView::default();
    };
    let items = world.items();
    let contents = |container: u32| -> Vec<&ClientObject> {
        items
            .get_contents(container)
            .iter()
            .filter_map(|&id| items.get(id))
            .collect()
    };
    let mut main_pack = Vec::new();
    let mut packs = Vec::new();
    for item in contents(player) {
        if item.currently_equipped_location != EquipMask::NONE {
            continue;
        }
        if placement::is_container(item) {
            packs.push(PackView {
                pack: ItemSummary::of(item),
                items: contents(item.object_id())
                    .into_iter()
                    .map(ItemSummary::of)
                    .collect(),
            });
        } else {
            main_pack.push(ItemSummary::of(item));
        }
    }
    let mut equipped: Vec<ItemSummary> = items
        .get_equipped_by(player)
        .into_iter()
        .map(ItemSummary::of)
        .collect();
    equipped.sort_by_key(|item| item.equip_location);
    InventoryView {
        player_guid: player,
        burden: items.sum_carried_burden(player),
        main_pack,
        packs,
        equipped,
        ground: None,
    }
}

/// A vendor listing entry as the vendor state keeps it.
#[must_use]
pub fn shop_item(item: &VendorItem) -> VendorShopItem {
    let d = &item.desc;
    VendorShopItem {
        item_guid: item.item_guid,
        stack_size: item.stack_size,
        weenie_class_id: d.weenie_class_id,
        name: Some(d.name.clone()),
        item_type: Some(d.item_type),
        icon_id: d.icon_id,
        value: d.value,
        desc_stack_size: d.stack_size.map(i32::from),
        max_stack_size: d.max_stack_size.map(i32::from),
        icon_underlay_id: d.icon_underlay_id.unwrap_or(0),
        icon_overlay_id: d.icon_overlay_id.unwrap_or(0),
        effects: d.ui_effects.unwrap_or(0),
        plural_name: d.plural_name.clone(),
        valid_locations: d.valid_locations,
        priority: d.priority,
        items_capacity: d.items_capacity.map(i32::from),
        containers_capacity: d.containers_capacity.map(i32::from),
        structure: d.structure.map(i32::from),
        max_structure: d.max_structure.map(i32::from),
        workmanship: d.workmanship,
        burden: d.burden.map(i32::from),
        material_type: d.material_type,
        target_type: d.target_type,
        combat_use: d.combat_use,
        ammo_type: d.ammo_type,
        public_weenie_bitfield: Some(d.object_description_flags),
        useability: d.useability,
        hook_item_types: d.hook_item_types,
        hook_type: d.hook_type.map(u32::from),
    }
}
