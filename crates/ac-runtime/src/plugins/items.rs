//! API group G: items, equipment and loot (OpenAC `IItemAutomation`,
//! `IEquipmentAutomation`, `ILootAutomation`) over the inventory owner and
//! the item table. The checks are OpenAC's, made before anything is sent,
//! so every client refuses the same requests for the same reasons; the
//! request itself goes through the session's own item commands.

use std::collections::HashSet;

use ac_core::items::TinkerValues;
use ac_core::items::vendor::{VendorSellCandidate, evaluate_sell, per_unit_value};
use ac_core::items::{ClientObject, ClientObjectTable, EquipMask, InventoryRequestKind, ItemType};
use ac_core::properties::{
    PropertyBool, PropertyDataId, PropertyFloat, PropertyInt, PropertyString,
};
use ac_plugin::abi::items::{
    AppraisalState, EquipRequest, EquipmentItem, EquipmentPlacement, EquipmentResult,
    EquipmentStatus, InventoryCommandKind, InventoryCompletion, InventoryItem, ItemAmount,
    ItemResult, ItemState, ItemStatus, ItemUseCompletion, LootContainer, LootState, MergeRequest,
    MoveRequest, Palette, PickupRequest, SalvageRequest,
};
use ac_plugin::abi::world::ItemProperties;

use super::world::{classify, is_player_owned, position_of, properties_of};
use super::{PluginBinding, Ran};
use crate::inventory::{BackpackPlacement, ItemCommand, ObjectRef};
use crate::session::SessionCommand;

/// The public flag of a corpse.
const CORPSE_FLAG: u32 = 0x0000_2000;
/// The public flag of something that opens.
const OPENABLE_FLAG: u32 = 0x0000_0001;
/// The off hand's slot bit.
const SHIELD_SLOT: u32 = 0x0020_0000;
/// How recent, in seconds of session time, the server's refusal to
/// appraise an item must be for a plugin to let go of it.
const STALE_ITEM_REFUSAL_WINDOW_SECONDS: f64 = 30.0;

fn int(item: &ClientObject, key: PropertyInt) -> i32 {
    item.properties.get_int(key.0, 0)
}

fn float(item: &ClientObject, key: PropertyFloat) -> f64 {
    item.properties.get_float(key.0, 0.0)
}

fn flags(item: &ClientObject) -> u32 {
    item.public_weenie_bitfield.map_or(0, |f| f.bits())
}

/// An item's palette swaps as a plugin sees them (OpenAC `ProjectPalettes`):
/// each swap with the colour in the middle of the range it recolours, read
/// through `color` (palette id, colour index), black where it has none.
pub(super) fn project_palettes(
    swaps: &[ac_proto::SubPalette],
    color: impl Fn(u32, usize) -> Option<(u8, u8, u8)>,
) -> Vec<Palette> {
    swaps
        .iter()
        .map(|swap| {
            // The swap's offset and length count blocks of eight colours, so
            // this is the colour in the middle of the recoloured range: the
            // one the macro tools' colour rules compare against (they write
            // it as a byte position in the palette file).
            let index = usize::from(swap.offset) * 8 + usize::from(swap.length) * 4;
            let (red, green, blue) = color(swap.sub_palette_id, index).unwrap_or_default();
            Palette {
                palette_id: swap.sub_palette_id,
                offset: swap.offset,
                length: swap.length,
                red,
                green,
                blue,
            }
        })
        .collect()
}

/// The palettes of the object `guid` the world knows, read from the dats
/// (none without them or without swaps).
fn palettes_of(
    world: &crate::world::World,
    content: Option<&crate::movement::WorldContent>,
    guid: u32,
) -> Vec<Palette> {
    let Some(content) = content else {
        return Vec::new();
    };
    palettes_with(world, guid, |id, index| {
        let palette = content
            .dats()
            .get::<ac_dat::objects::Palette>(id)
            .ok()
            .flatten()?;
        palette.colors.get(index).map(|c| (c.red, c.green, c.blue))
    })
}

/// The palettes of the object `guid` the world knows, each colour read
/// through `color` (palette id, colour index); none for an object the world
/// does not know. Every created object is known, whether it stands in the
/// world or lies inside a container, so an item in a corpse reports the
/// same colours it would on the ground.
pub(super) fn palettes_with(
    world: &crate::world::World,
    guid: u32,
    color: impl Fn(u32, usize) -> Option<(u8, u8, u8)>,
) -> Vec<Palette> {
    world.object(guid).map_or_else(Vec::new, |object| {
        project_palettes(&object.model().sub_palettes, color)
    })
}

/// One item as a plugin sees it (OpenAC `ProjectInventoryItem`): the tinker
/// values from the appraisal, the requirement and gear numbers from the
/// properties, the palettes from its look.
pub(super) fn project_item(item: &ClientObject, palettes: Vec<Palette>) -> InventoryItem {
    let tinker = TinkerValues::of(item);
    let p = &item.properties;
    InventoryItem {
        object_id: item.object_id(),
        weenie_class_id: item.weenie_class_id,
        name: item.name.clone(),
        item_type: item.item_type.bits(),
        container_id: item.container_id,
        wielder_id: item.wielder_id,
        valid_locations: item.valid_locations.bits(),
        equipped_location: item.currently_equipped_location.bits(),
        useability: item.useability.map_or(0, |u| u.bits()),
        target_type: item.target_type.map_or(0, |t| t.bits()),
        public_flags: flags(item),
        stack_size: item.stack_size,
        structure: item.structure,
        maximum_structure: item.max_structure,
        spell_id: item.spell_id.unwrap_or_else(|| {
            p.data_ids
                .get(&PropertyDataId::SPELL.0)
                .copied()
                .unwrap_or(0)
        }),
        pet_class: int(item, PropertyInt::PET_CLASS),
        summoning_mastery: int(item, PropertyInt::SUMMONING_MASTERY),
        // The shared cooldown comes with the object and again in an
        // appraisal; the table keeps the newest of the two.
        shared_cooldown_id: item.cooldown_id.unwrap_or(0),
        cooldown_seconds: item.cooldown_duration.unwrap_or(0.0),
        proc_spell_id: p
            .data_ids
            .get(&PropertyDataId::PROC_SPELL.0)
            .copied()
            .unwrap_or(0),
        proc_spell_self_targeted: p.get_bool(PropertyBool::PROC_SPELL_SELF_TARGETED.0, false),
        proc_spell_rate: float(item, PropertyFloat::PROC_SPELL_RATE),
        weapon_skill: item.weapon_profile.map_or_else(
            || int(item, PropertyInt::WEAPON_SKILL),
            |w| w.weapon_skill.cast_signed(),
        ),
        damage_type: tinker.damage_type,
        damage: tinker.damage,
        damage_variance: tinker.damage_variance,
        use_requires_skill: int(item, PropertyInt::USE_REQUIRES_SKILL),
        use_requires_skill_level: int(item, PropertyInt::USE_REQUIRES_SKILL_LEVEL),
        use_requires_skill_specialized: int(item, PropertyInt::USE_REQUIRES_SKILL_SPEC),
        combat_use: item.combat_use.map_or(0, i32::from),
        item_spellcraft: int(item, PropertyInt::ITEM_SPELLCRAFT),
        wield_requirements: int(item, PropertyInt::WIELD_REQUIREMENTS),
        wield_skill_type: int(item, PropertyInt::WIELD_SKILLTYPE),
        wield_difficulty: int(item, PropertyInt::WIELD_DIFFICULTY),
        attack_type: int(item, PropertyInt::ATTACK_TYPE),
        weapon_type: int(item, PropertyInt::WEAPON_TYPE),
        ammo_type: item.ammo_type.map_or_else(
            || int(item, PropertyInt::AMMO_TYPE).max(0).cast_unsigned(),
            u32::from,
        ),
        booster_vital: int(item, PropertyInt::BOOSTER_ENUM),
        boost_value: int(item, PropertyInt::BOOST_VALUE),
        heal_kit_modifier: float(item, PropertyFloat::HEALKIT_MOD),
        appraised_spell_ids: item.appraised_spell_ids.clone(),
        gear_damage: int(item, PropertyInt::GEAR_DAMAGE),
        gear_damage_resistance: int(item, PropertyInt::GEAR_DAMAGE_RESIST),
        gear_critical_chance: int(item, PropertyInt::GEAR_CRIT),
        gear_critical_resistance: int(item, PropertyInt::GEAR_CRIT_RESIST),
        gear_critical_damage: int(item, PropertyInt::GEAR_CRIT_DAMAGE),
        gear_critical_damage_resistance: int(item, PropertyInt::GEAR_CRIT_DAMAGE_RESIST),
        maximum_stack_size: item.stack_size_max,
        container_slot: item.container_slot,
        items_capacity: item.items_capacity,
        containers_capacity: item.containers_capacity,
        burden: item.burden,
        value: item.value,
        item_current_mana: int(item, PropertyInt::ITEM_CUR_MANA),
        item_maximum_mana: int(item, PropertyInt::ITEM_MAX_MANA),
        workmanship: item.workmanship,
        salvage_workmanship: tinker.salvage_workmanship,
        num_times_tinkered: tinker.num_times_tinkered,
        imbued_effect: tinker.imbued_effect,
        armor_level: tinker.armor_level,
        max_damage: tinker.max_damage,
        wand_elemental_damage_type: tinker.wand_elemental_damage_type,
        retained: tinker.retained,
        material_type: tinker.material_type,
        object_class: classify(Some(item)),
        palettes,
        icon_id: item.icon_id,
        effects: item.effects,
        icon_underlay_id: item.icon_underlay_id,
        icon_overlay_id: item.icon_overlay_id,
        coverage_mask: item.priority,
        hook_item_types: item.hook_item_types,
        plural_name: item.plural_name.clone(),
        // The description's distance (OpenAC prefers the live entity's,
        // which the same description and its updates set).
        use_radius: item.header.and_then(|h| h.use_radius).unwrap_or(0.0),
        header: item.header.as_ref().map(project_header),
    }
}

/// The description header as a plugin reads it (OpenAC
/// `RuntimeWorldObjectProjection.ProjectHeader`).
pub(super) fn project_header(
    h: &ac_core::items::ClientObjectHeader,
) -> ac_plugin::abi::items::ObjectHeader {
    ac_plugin::abi::items::ObjectHeader {
        weenie_header_flags: h.weenie_header_flags,
        weenie_header_flags2: h.weenie_header_flags2,
        physics_description_flags: h.physics_description_flags,
        physics_state: h.physics_state,
        object_description_flags: h.object_description_flags,
        setup_id: h.setup_id,
        scale: h.scale,
        hook_type: h.hook_type,
        parent_id: h.parent_id,
        parent_location: h.parent_location,
        use_radius: h.use_radius,
    }
}

/// An owned item, by id.
fn owned(items: &ClientObjectTable, player: u32, id: u32) -> Option<&ClientObject> {
    let item = (id != 0).then(|| items.get(id)).flatten()?;
    is_player_owned(item, player, items).then_some(item)
}

/// A stack quantity is 0 (all) or no more than the stack.
fn valid_amount(item: &ClientObject, amount: u32) -> bool {
    amount == 0 || amount <= item.stack_size.max(1).cast_unsigned()
}

/// Every id inside `root`, packs inside it included (OpenAC
/// `CaptureContainerIds`).
fn container_ids(items: &ClientObjectTable, root: u32) -> HashSet<u32> {
    let mut found = HashSet::new();
    let mut pending = vec![root];
    while let Some(container) = pending.pop() {
        for &child in items.get_contents(container) {
            if !found.insert(child) {
                continue;
            }
            if items.get(child).is_some_and(|c| {
                c.items_capacity != 0
                    || c.containers_capacity != 0
                    || c.item_type.intersects(ItemType::CONTAINER)
            }) {
                pending.push(child);
            }
        }
    }
    found
}

/// The contents of `container` in tree order (OpenAC
/// `CaptureContainerTree`).
fn container_tree(
    world: &crate::world::World,
    content: Option<&crate::movement::WorldContent>,
    container: u32,
    seen: &mut HashSet<u32>,
    out: &mut Vec<InventoryItem>,
) {
    let items = world.items();
    for &child_id in items.get_contents(container) {
        if !seen.insert(child_id) {
            continue;
        }
        let Some(child) = items.get(child_id) else {
            continue;
        };
        out.push(project_item(child, palettes_of(world, content, child_id)));
        if child.items_capacity != 0
            || child.containers_capacity != 0
            || child.item_type.intersects(ItemType::CONTAINER)
        {
            container_tree(world, content, child_id, seen, out);
        }
    }
}

fn request_kind(kind: Option<InventoryRequestKind>) -> InventoryCommandKind {
    use InventoryRequestKind as K;
    match kind {
        Some(K::Pickup) => InventoryCommandKind::Pickup,
        Some(K::PutInContainer) => InventoryCommandKind::PutInContainer,
        Some(K::SplitToContainer) => InventoryCommandKind::SplitToContainer,
        Some(K::Merge) => InventoryCommandKind::Merge,
        Some(K::Move) => InventoryCommandKind::Move,
        Some(K::DropToWorld) => InventoryCommandKind::DropToWorld,
        Some(K::SplitToWorld) => InventoryCommandKind::SplitToWorld,
        Some(K::Wield) => InventoryCommandKind::Wield,
        Some(K::Give) => InventoryCommandKind::Give,
        Some(K::Shop) | None => InventoryCommandKind::Unknown,
    }
}

fn revision(value: u64) -> i64 {
    i64::try_from(value).unwrap_or(i64::MAX)
}

impl PluginBinding<'_> {
    /// Whether a use or inventory request offered now would come back busy
    /// (OpenAC `IsItemCommandBusy`): a request of the client's own still in
    /// flight, or the 200 ms pacing between two uses not yet lapsed. Both
    /// mean "not yet", and both are visible here, so a plugin that waits for
    /// this to clear is never refused for it.
    pub(super) fn items_busy(&self) -> bool {
        let now = self.session_ms();
        !self.state.inventory.transactions().can_begin_request()
            || !self
                .state
                .inventory
                .interaction()
                .is_use_throttle_ready(now)
    }

    fn player(&self) -> u32 {
        self.state.world.player_guid().unwrap_or(0)
    }

    /// Runs one item command and answers as OpenAC's bound functions do:
    /// started when it went through, refused with the owner's words.
    fn run_item(&mut self, command: ItemCommand) -> ItemResult {
        match self.run(SessionCommand::Item(command)) {
            Ran::NoConnection => ItemResult::of(ItemStatus::Unavailable),
            Ran::Refused(why) => ItemResult::because(ItemStatus::Refused, &why),
            Ran::Done => ItemResult::of(ItemStatus::Started),
        }
    }

    /// The last awaited use the server answered.
    pub(super) fn last_use_completion(&self) -> ItemUseCompletion {
        let c = self.state.inventory.completions();
        ItemUseCompletion {
            revision: revision(c.use_revision),
            source_id: c.use_source,
            target_id: c.use_target,
            weenie_error: c.use_error,
        }
    }

    fn last_inventory_completion(&self) -> InventoryCompletion {
        let c = self.state.inventory.completions();
        InventoryCompletion {
            revision: revision(c.request_revision),
            kind: request_kind(c.request_kind),
            source_id: c.request_item,
            weenie_error: c.request_error,
        }
    }

    /// Everything the character owns, by name then id (OpenAC
    /// `CaptureOwnedItems`).
    pub(super) fn items_owned(&self) -> Vec<InventoryItem> {
        let player = self.player();
        if !self.facts.in_world || player == 0 {
            return Vec::new();
        }
        let items = self.state.world.items();
        let mut owned: Vec<InventoryItem> = items
            .objects()
            .filter(|i| is_player_owned(i, player, items))
            .map(|i| {
                project_item(
                    i,
                    palettes_of(
                        &self.state.world,
                        self.state.movement.content().map(AsRef::as_ref),
                        i.object_id(),
                    ),
                )
            })
            .collect();
        owned.sort_by(|a, b| a.name.cmp(&b.name).then(a.object_id.cmp(&b.object_id)));
        owned
    }

    /// The server has listed the inventory and every nested pack and no
    /// inventory move or request is pending (OpenAC
    /// `IsOwnedInventoryComplete`).
    pub(super) fn items_inventory_complete(&self) -> bool {
        self.facts.in_world
            && !self.state.inventory.transactions().has_pending_request()
            && self
                .state
                .world
                .items()
                .has_complete_inventory(self.player())
    }

    /// Where the item commands stand.
    pub(super) fn items_state(&self) -> ItemState {
        let player = self.player();
        let pets = self
            .state
            .world
            .items()
            .objects()
            .filter(|i| {
                player != 0
                    && i.pet_owner_id == player
                    && i.item_type.intersects(ItemType::CREATURE)
            })
            .count();
        ItemState {
            is_available: self.facts.in_world,
            is_busy: self.items_busy(),
            active_owned_pet_count: i32::try_from(pets).unwrap_or(i32::MAX),
            active_vendor_id: self.state.inventory.vendor().vendor_id(),
            last_completion: self.last_use_completion(),
            last_inventory_completion: self.last_inventory_completion(),
        }
    }

    /// The properties of an owned item.
    pub(super) fn items_properties(&self, object_id: u32) -> Option<ItemProperties> {
        if !self.facts.in_world {
            return None;
        }
        owned(self.state.world.items(), self.player(), object_id).map(properties_of)
    }

    /// Use an owned item, alone or on a target; a world object is walked to
    /// and used (OpenAC `DispatchItem`).
    pub(super) fn items_apply(&mut self, (object_id, target_id): (u32, u32)) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let player = self.player();
        let items = self.state.world.items();
        let Some(item) = (object_id != 0).then(|| items.get(object_id)).flatten() else {
            return ItemResult::of(ItemStatus::InvalidItem);
        };
        if !is_player_owned(item, player, items) {
            if target_id != 0 {
                return ItemResult::of(ItemStatus::InvalidItem);
            }
            return self.world_activate(object_id);
        }
        if target_id != 0 && items.get(target_id).is_none() {
            return ItemResult::of(ItemStatus::InvalidTarget);
        }
        if target_id == 0 && item.useability.is_some_and(|u| u.is_targeted()) {
            return ItemResult::because(
                ItemStatus::Refused,
                "This item requires a target; call Apply(objectId, targetObjectId) instead.",
            );
        }
        if self.items_busy() {
            return ItemResult::of(ItemStatus::Busy);
        }
        let item = ObjectRef::Guid(object_id);
        self.run_item(if target_id == 0 {
            ItemCommand::Use(item)
        } else {
            ItemCommand::UseOn {
                item,
                target: ObjectRef::Guid(target_id),
            }
        })
    }

    /// Put an owned item into an owned container (OpenAC
    /// `MoveToContainer`).
    pub(super) fn items_move(&mut self, request: MoveRequest) -> ItemResult {
        self.items_move_join_stack(request, false)
    }

    /// The same move, joining a stack in the container when `join_stack`
    /// is set (OpenAC `MoveToContainer(..., joinStack)`): the checks are
    /// the plain move's, the runtime chooses the merge or the move.
    pub(super) fn items_move_join_stack(
        &mut self,
        request: MoveRequest,
        join_stack: bool,
    ) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let player = self.player();
        let items = self.state.world.items();
        let Some(item) = owned(items, player, request.object_id) else {
            return ItemResult::of(ItemStatus::InvalidItem);
        };
        let container_ok = request.container_id != 0
            && items.get(request.container_id).is_some_and(|c| {
                request.container_id == player || is_player_owned(c, player, items)
            });
        if !container_ok {
            return ItemResult::of(ItemStatus::InvalidTarget);
        }
        if !valid_amount(item, request.amount) {
            return ItemResult::because(ItemStatus::Refused, "Invalid stack quantity.");
        }
        if !self.state.inventory.transactions().can_begin_request() {
            return ItemResult::of(ItemStatus::Busy);
        }
        self.run_item(ItemCommand::MoveToContainer {
            item: ObjectRef::Guid(request.object_id),
            container: ObjectRef::Guid(request.container_id),
            slot: request.placement,
            amount: request.amount,
            join_stack,
        })
    }

    /// Merge two owned stacks (OpenAC `Merge`).
    pub(super) fn items_merge(&mut self, request: MergeRequest) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let player = self.player();
        let items = self.state.world.items();
        let Some(source) = owned(items, player, request.source_id) else {
            return ItemResult::of(ItemStatus::InvalidItem);
        };
        if owned(items, player, request.target_id).is_none() {
            return ItemResult::of(ItemStatus::InvalidTarget);
        }
        if !valid_amount(source, request.amount) {
            return ItemResult::because(ItemStatus::Refused, "Invalid stack quantity.");
        }
        if !self.state.inventory.transactions().can_begin_request() {
            return ItemResult::of(ItemStatus::Busy);
        }
        self.run_item(ItemCommand::Merge {
            from: ObjectRef::Guid(request.source_id),
            to: ObjectRef::Guid(request.target_id),
            amount: request.amount,
        })
    }

    /// Drop an owned item (OpenAC `Drop`).
    pub(super) fn items_drop(&mut self, request: ItemAmount) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let Some(item) = owned(self.state.world.items(), self.player(), request.object_id) else {
            return ItemResult::of(ItemStatus::InvalidItem);
        };
        if !valid_amount(item, request.amount) {
            return ItemResult::because(ItemStatus::Refused, "Invalid stack quantity.");
        }
        if !self.state.inventory.transactions().can_begin_request() {
            return ItemResult::of(ItemStatus::Busy);
        }
        self.run_item(ItemCommand::Drop {
            item: ObjectRef::Guid(request.object_id),
            amount: request.amount,
        })
    }

    /// Give an owned item to someone known (OpenAC `Give`).
    pub(super) fn items_give(&mut self, request: ItemAmount) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let items = self.state.world.items();
        let Some(item) = owned(items, self.player(), request.object_id) else {
            return ItemResult::of(ItemStatus::InvalidItem);
        };
        if request.target_id == 0 || items.get(request.target_id).is_none() {
            return ItemResult::of(ItemStatus::InvalidTarget);
        }
        if !valid_amount(item, request.amount) {
            return ItemResult::because(ItemStatus::Refused, "Invalid stack quantity.");
        }
        if !self.state.inventory.transactions().can_begin_request() {
            return ItemResult::of(ItemStatus::Busy);
        }
        self.run_item(ItemCommand::Give {
            item: ObjectRef::Guid(request.object_id),
            target: ObjectRef::Guid(request.target_id),
            amount: request.amount,
        })
    }

    /// Salvage owned items with an owned tinkering tool (OpenAC `Salvage`).
    pub(super) fn items_salvage(&mut self, request: SalvageRequest) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        if request.item_ids.is_empty() {
            return ItemResult::of(ItemStatus::InvalidItem);
        }
        let player = self.player();
        let items = self.state.world.items();
        let tool_ok = owned(items, player, request.tool_id)
            .is_some_and(|t| t.item_type.intersects(ItemType::TINKERING_TOOL));
        if !tool_ok {
            return ItemResult::of(ItemStatus::InvalidTarget);
        }
        if request
            .item_ids
            .iter()
            .any(|&id| owned(items, player, id).is_none())
        {
            return ItemResult::of(ItemStatus::InvalidItem);
        }
        if !self.state.inventory.transactions().can_begin_request() {
            return ItemResult::of(ItemStatus::Busy);
        }
        self.run_item(ItemCommand::Salvage {
            tool: ObjectRef::Guid(request.tool_id),
            items: request.item_ids.into_iter().map(ObjectRef::Guid).collect(),
        })
    }

    /// Sell an owned item to the open vendor, checked against the shop's
    /// rules first (OpenAC `Sell`).
    pub(super) fn items_sell(&mut self, request: ItemAmount) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let vendor = self.state.inventory.vendor();
        if vendor.vendor_id() == 0 {
            return ItemResult::because(ItemStatus::InvalidTarget, "No vendor is open.");
        }
        let items = self.state.world.items();
        let Some(item) = owned(items, self.player(), request.object_id) else {
            return ItemResult::of(ItemStatus::InvalidItem);
        };
        if !valid_amount(item, request.amount) {
            return ItemResult::because(ItemStatus::Refused, "Invalid stack quantity.");
        }
        let quantity = if request.amount == 0 {
            item.stack_size.max(1)
        } else {
            i32::try_from(request.amount).unwrap_or(i32::MAX)
        };
        let candidate = VendorSellCandidate {
            owned_by_player: true,
            contained_item_count: i32::try_from(items.get_contents(request.object_id).len())
                .unwrap_or(i32::MAX),
            item_type: item.item_type.bits(),
            per_unit_value: per_unit_value(item.value, Some(item.stack_size)),
            public_weenie_bitfield: flags(item),
        };
        if let Err(rejection) = evaluate_sell(&candidate, vendor.profile()) {
            return ItemResult::because(ItemStatus::Refused, rejection.message());
        }
        if !self.state.inventory.transactions().can_begin_request() {
            return ItemResult::of(ItemStatus::Busy);
        }
        self.run_item(ItemCommand::Sell(vec![(
            ObjectRef::Guid(request.object_id),
            quantity,
        )]))
    }

    /// Let go of a carried item the server no longer has (OpenAC
    /// `ForgetStaleItem`): it leaves as a server delete of its record would
    /// take it, and nothing is sent. Only under guards that keep a plugin
    /// from wiping real inventory: carried, not worn, an empty pack, with a
    /// server record, refused by the server's latest appraisal answer
    /// within the last 30 seconds, and no appraisal of it awaited.
    pub(super) fn items_forget_stale(&mut self, object_id: u32) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let player = self.player();
        let items = self.state.world.items();
        let Some(item) = (object_id != player)
            .then(|| owned(items, player, object_id))
            .flatten()
        else {
            return ItemResult::of(ItemStatus::InvalidItem);
        };
        // The item leaves by the route a server delete takes, which is
        // keyed on the server's record of the object; without one, there
        // is nothing a server delete would have removed either.
        if self.state.world.object(object_id).is_none() {
            return ItemResult::because(
                ItemStatus::InvalidItem,
                "The client holds no server record of that item.",
            );
        }
        if item.currently_equipped_location != EquipMask::NONE || item.wielder_id != 0 {
            return ItemResult::because(ItemStatus::Refused, "That item is equipped.");
        }
        // A pack's listing is only what the server last showed of it; an
        // object can name the pack as its container without being listed.
        // Letting go of such a pack would strand those objects.
        if !items.get_contents(object_id).is_empty()
            || items.objects().any(|c| c.container_id == object_id)
        {
            return ItemResult::because(ItemStatus::Refused, "That pack still holds something.");
        }
        // An appraisal of this very item on its way is the answer the
        // decision rests on; an earlier answer no longer counts until it
        // lands.
        let interaction = self.state.inventory.interaction();
        if interaction.awaiting_appraisal() == object_id
            && !interaction.is_awaiting_expired(self.session_ms())
        {
            return ItemResult::because(
                ItemStatus::Busy,
                "An appraisal of that item is still awaited.",
            );
        }
        if !item.last_appraisal_unsuccessful {
            return ItemResult::because(
                ItemStatus::Refused,
                "The server has not refused to appraise that item.",
            );
        }
        // Only a refusal the caller has just seen counts: an old one may be
        // the repeat-request refusal of a real item nobody asked about
        // since, and nothing else would ever overturn it.
        if self.session_seconds() - item.last_appraisal_unsuccessful_at_seconds
            > STALE_ITEM_REFUSAL_WINDOW_SECONDS
        {
            return ItemResult::because(
                ItemStatus::Refused,
                "The server refused to appraise that item too long ago; ask again.",
            );
        }
        if self.items_busy() {
            return ItemResult::of(ItemStatus::Busy);
        }
        match self.run(SessionCommand::DismissGhost(object_id)) {
            Ran::NoConnection => ItemResult::of(ItemStatus::Unavailable),
            Ran::Done if self.state.world.item(object_id).is_none() => {
                ItemResult::of(ItemStatus::Completed)
            }
            Ran::Done | Ran::Refused(_) => ItemResult::of(ItemStatus::Refused),
        }
    }

    /// Everything owned that can be worn or wielded: equipped first, then
    /// by name, then by id (OpenAC `BuildOwnedEquipment`).
    pub(super) fn equipment_owned(&self) -> Vec<EquipmentItem> {
        let player = self.player();
        if !self.facts.in_world || player == 0 {
            return Vec::new();
        }
        let items = self.state.world.items();
        let mut built: Vec<EquipmentItem> = items
            .objects()
            .filter(|i| i.valid_locations.bits() != 0 && is_player_owned(i, player, items))
            .map(|item| EquipmentItem {
                object_id: item.object_id(),
                name: item.name.clone(),
                item_type: item.item_type.bits(),
                valid_locations: item.valid_locations.bits(),
                equipped_location: item.currently_equipped_location.bits(),
                container_id: item.container_id,
                wielder_id: item.wielder_id,
                combat_use: item.combat_use.unwrap_or(0),
                damage_type: int(item, PropertyInt::DAMAGE_TYPE),
                weapon_skill: int(item, PropertyInt::WEAPON_SKILL),
                damage: int(item, PropertyInt::DAMAGE),
                damage_variance: float(item, PropertyFloat::DAMAGE_VARIANCE),
                ammo_type: item.ammo_type.map_or_else(
                    || int(item, PropertyInt::AMMO_TYPE).max(0).cast_unsigned(),
                    u32::from,
                ),
                stack_size: item.stack_size,
                object_class: classify(Some(item)),
                weapon_type: int(item, PropertyInt::WEAPON_TYPE),
                cleaving: int(item, PropertyInt::CLEAVING),
                imbued_effect: int(item, PropertyInt::IMBUED_EFFECT),
                resistance_cleaving: int(item, PropertyInt::RESISTANCE_MODIFIER_TYPE),
                slayer_creature_type: int(item, PropertyInt::SLAYER_CREATURE_TYPE),
                crushing_blow: float(item, PropertyFloat::CRITICAL_MULTIPLIER) > 0.0,
                biting_strike: float(item, PropertyFloat::CRITICAL_FREQUENCY) > 0.0,
                armor_cleaving: float(item, PropertyFloat::IGNORE_ARMOR) > 0.0,
            })
            .collect();
        built.sort_by(|a, b| {
            (b.equipped_location != 0)
                .cmp(&(a.equipped_location != 0))
                .then_with(|| a.name.cmp(&b.name))
                .then(a.object_id.cmp(&b.object_id))
        });
        built
    }

    /// Every known item's worn location, in the table's order.
    pub(super) fn equipment_placements(&self) -> Vec<EquipmentPlacement> {
        if !self.facts.in_world {
            return Vec::new();
        }
        self.state
            .world
            .items()
            .objects()
            .map(|i| EquipmentPlacement {
                object_id: i.object_id(),
                equipped_location: i.currently_equipped_location.bits(),
            })
            .collect()
    }

    /// An equip the client started is still in flight (OpenAC
    /// `IEquipmentAutomation.IsBusy`, bound to the auto-wield owner's
    /// switch): that and nothing else. A pending item request is not a
    /// wield in flight; an equip offered then is refused at dispatch.
    pub(super) fn equipment_busy(&self) -> bool {
        self.state.inventory.is_wield_busy()
    }

    fn run_equip(&mut self, command: ItemCommand) -> EquipmentResult {
        let answer = |status, notice: Option<String>| EquipmentResult { status, notice };
        if self.state.inventory.is_wield_busy() || self.items_busy() {
            return answer(EquipmentStatus::Busy, None);
        }
        match self.run(SessionCommand::Item(command)) {
            Ran::NoConnection => answer(EquipmentStatus::Unavailable, None),
            Ran::Refused(why) => answer(EquipmentStatus::Refused, Some(why)),
            Ran::Done => answer(EquipmentStatus::Started, None),
        }
    }

    /// Wear or wield an item (OpenAC `Equip`): already there is accepted.
    pub(super) fn equipment_equip(&mut self, request: EquipRequest) -> EquipmentResult {
        let answer = |status| EquipmentResult {
            status,
            notice: None,
        };
        if !self.in_world() {
            return answer(EquipmentStatus::Unavailable);
        }
        let Some(item) = (request.object_id != 0)
            .then(|| self.state.world.item(request.object_id))
            .flatten()
            .filter(|i| i.valid_locations.bits() != 0)
        else {
            return answer(EquipmentStatus::InvalidItem);
        };
        let worn = item.currently_equipped_location.bits();
        if worn != 0 && (request.location == 0 || worn & request.location == request.location) {
            return answer(EquipmentStatus::AlreadyEquipped);
        }
        self.run_equip(ItemCommand::Wield {
            item: ObjectRef::Guid(request.object_id),
            location: request.location,
        })
    }

    /// Wield an item in the off hand (OpenAC `EquipSecondary`).
    pub(super) fn equipment_equip_secondary(&mut self, object_id: u32) -> EquipmentResult {
        let answer = |status| EquipmentResult {
            status,
            notice: None,
        };
        if !self.in_world() {
            return answer(EquipmentStatus::Unavailable);
        }
        let Some(item) = (object_id != 0)
            .then(|| self.state.world.item(object_id))
            .flatten()
            .filter(|i| i.valid_locations.bits() != 0)
        else {
            return answer(EquipmentStatus::InvalidItem);
        };
        if item.currently_equipped_location.bits() == SHIELD_SLOT {
            return answer(EquipmentStatus::AlreadyEquipped);
        }
        // The dual-wield route: a weapon goes to the off hand although the
        // shield slot is not one of its own slots.
        self.run_equip(ItemCommand::WieldSecondary(ObjectRef::Guid(object_id)))
    }

    /// Where looting stands (OpenAC `ILootAutomation`'s state members).
    pub(super) fn loot_state(&self) -> LootState {
        let external = self.state.inventory.external_containers();
        let interaction = self.state.inventory.interaction();
        let now = self.session_ms();
        let expired = interaction.is_awaiting_expired(now);
        LootState {
            is_available: self.facts.in_world,
            is_busy: self.items_busy(),
            requested_container_id: external.requested_container_id(),
            current_container_id: external.current_container_id(),
            current_contents_ready: self.contents_ready(),
            last_item_use_completion: self.last_use_completion(),
            last_inventory_completion: self.last_inventory_completion(),
            appraisal: AppraisalState {
                revision: revision(interaction.appraisal_revision()),
                awaiting_id: if expired {
                    0
                } else {
                    interaction.awaiting_appraisal()
                },
                current_id: interaction.last_completed_appraisal(),
                last_abandoned_id: if expired {
                    interaction.awaiting_appraisal()
                } else {
                    interaction.last_abandoned_appraisal()
                },
                // The answer is recorded on the object in the same inbound
                // message that completes the slot, so the object's latest
                // outcome is the completion's.
                current_object_unsuccessful: interaction.last_completed_appraisal() != 0
                    && self
                        .state
                        .world
                        .item(interaction.last_completed_appraisal())
                        .is_some_and(|i| i.last_appraisal_unsuccessful),
            },
        }
    }

    /// Every item in the open container is described (OpenAC
    /// `CurrentContentsReady`).
    fn contents_ready(&self) -> bool {
        if !self.facts.in_world {
            return false;
        }
        let root = self
            .state
            .inventory
            .external_containers()
            .current_container_id();
        if root == 0 {
            return false;
        }
        let items = self.state.world.items();
        container_ids(items, root).into_iter().all(|id| {
            items
                .get(id)
                .is_some_and(|i| !i.name.is_empty() && i.weenie_class_id != 0)
        })
    }

    /// The corpses on the ground within `maximum` metres, nearest first
    /// (OpenAC `CaptureCorpses`).
    pub(super) fn loot_corpses(&self, maximum: f32) -> Vec<LootContainer> {
        if !self.facts.in_world || maximum.is_nan() || maximum <= 0.0 {
            return Vec::new();
        }
        let world = &self.state.world;
        let Some(here) = world
            .player_guid()
            .and_then(|p| world.object(p))
            .and_then(|o| o.position())
        else {
            return Vec::new();
        };
        let external = self.state.inventory.external_containers();
        let mut corpses: Vec<LootContainer> = world
            .items()
            .objects()
            .filter(|c| flags(c) & CORPSE_FLAG != 0 && c.container_id == 0)
            .filter_map(|c| {
                let id = c.object_id();
                let place = world.object(id).and_then(|o| o.position())?;
                let distance = crate::world::distance(here, place);
                (distance <= maximum).then(|| LootContainer {
                    object_id: id,
                    weenie_class_id: c.weenie_class_id,
                    name: c.name.clone(),
                    distance,
                    has_been_opened: external.has_corpse_been_opened(id),
                    is_requested: external.requested_container_id() == id,
                    is_current: external.current_container_id() == id,
                    long_description: c
                        .properties
                        .get_string(PropertyString::LONG_DESC.0, "")
                        .to_owned(),
                    is_generated_rare: c
                        .properties
                        .get_bool(PropertyBool::CORPSE_GENERATED_RARE.0, false),
                    is_identified: c
                        .properties
                        .strings
                        .contains_key(&PropertyString::LONG_DESC.0),
                    is_appraisal_answered: c.appraisal_answered
                        || c.properties
                            .strings
                            .contains_key(&PropertyString::LONG_DESC.0),
                    position: Some(position_of(place)),
                })
            })
            .collect();
        corpses.sort_by(|a, b| {
            a.distance
                .total_cmp(&b.distance)
                .then(a.object_id.cmp(&b.object_id))
        });
        corpses
    }

    /// What the open container holds, packs inside included.
    pub(super) fn loot_contents(&self) -> Vec<InventoryItem> {
        if !self.facts.in_world {
            return Vec::new();
        }
        let root = self
            .state
            .inventory
            .external_containers()
            .current_container_id();
        if root == 0 {
            return Vec::new();
        }
        let mut out = Vec::new();
        let mut seen = HashSet::from([root]);
        container_tree(
            &self.state.world,
            self.state.movement.content().map(AsRef::as_ref),
            root,
            &mut seen,
            &mut out,
        );
        out
    }

    /// The properties of an item in the open container.
    pub(super) fn loot_properties(&self, object_id: u32) -> Option<ItemProperties> {
        if !self.facts.in_world {
            return None;
        }
        let root = self
            .state
            .inventory
            .external_containers()
            .current_container_id();
        let items = self.state.world.items();
        if root == 0 || !container_ids(items, root).contains(&object_id) {
            return None;
        }
        items.get(object_id).map(properties_of)
    }

    /// Open a corpse or chest, walking there when it is out of reach (OpenAC
    /// `Loot.Open`): early is busy, never a failure.
    pub(super) fn loot_open(&mut self, container_id: u32) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let opens = (container_id != 0)
            .then(|| self.state.world.item(container_id))
            .flatten()
            .is_some_and(|c| flags(c) & (CORPSE_FLAG | OPENABLE_FLAG) != 0);
        if !opens {
            return ItemResult::of(ItemStatus::InvalidTarget);
        }
        if self.items_busy() {
            return ItemResult::of(ItemStatus::Busy);
        }
        // A corpse or chest out in the world is reached as any world object
        // a plugin uses: walked to when out of reach, and asked open once the
        // use goes out.
        let player = self.player();
        let items = self.state.world.items();
        if let Some(container) = items
            .get(container_id)
            .filter(|c| !is_player_owned(c, player, items))
        {
            let useable = container.useability.is_some_and(|u| u.is_useable());
            return self.use_world_object(container_id, useable);
        }
        self.run_item(ItemCommand::Use(ObjectRef::Guid(container_id)))
    }

    /// Close the open container (OpenAC `Loot.Close`).
    pub(super) fn loot_close(&mut self, container_id: u32) -> ItemResult {
        let (result, why) = self.loot_close_checked(container_id);
        self.state.inventory.probe_external(|| {
            format!(
                "plugin close 0x{container_id:08X}: {:?}, {why}",
                result.status
            )
        });
        result
    }

    /// [`Self::loot_close`]'s answer and, for the probe, why.
    fn loot_close_checked(&mut self, container_id: u32) -> (ItemResult, &'static str) {
        if !self.in_world() {
            return (
                ItemResult::of(ItemStatus::Unavailable),
                "session not in the world",
            );
        }
        let open = self
            .state
            .inventory
            .external_containers()
            .current_container_id();
        let item = (container_id != 0)
            .then(|| self.state.world.item(container_id))
            .flatten();
        let why = if container_id == 0 || open != container_id {
            "not the open container"
        } else if item.is_none() {
            "object not in the world"
        } else if item.is_some_and(|c| flags(c) & (CORPSE_FLAG | OPENABLE_FLAG) == 0) {
            "neither a corpse nor openable"
        } else {
            ""
        };
        if !why.is_empty() {
            return (ItemResult::of(ItemStatus::InvalidTarget), why);
        }
        if self.items_busy() {
            return (ItemResult::of(ItemStatus::Busy), "items busy");
        }
        let result = self.run_item(ItemCommand::CloseContainer(ObjectRef::Guid(container_id)));
        (result, "use of it")
    }

    /// Ask for the appraisal of a corpse, an item in the open container, or
    /// an owned item (OpenAC `Loot.Identify`).
    pub(super) fn loot_identify(&mut self, object_id: u32) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let player = self.player();
        let root = self
            .state
            .inventory
            .external_containers()
            .current_container_id();
        let items = self.state.world.items();
        let Some(item) = (object_id != 0).then(|| items.get(object_id)).flatten() else {
            return ItemResult::of(ItemStatus::InvalidItem);
        };
        let corpse = flags(item) & CORPSE_FLAG != 0;
        let content = root != 0 && container_ids(items, root).contains(&object_id);
        let own = owned(items, player, object_id).is_some();
        if !(corpse || content || own) {
            return ItemResult::of(ItemStatus::InvalidItem);
        }
        self.world_identify(object_id)
    }

    /// Pick an item out of the open container (OpenAC `Pickup`).
    pub(super) fn loot_pickup(&mut self, request: PickupRequest) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let root = self
            .state
            .inventory
            .external_containers()
            .current_container_id();
        let items = self.state.world.items();
        if request.object_id == 0
            || root == 0
            || items.get(request.object_id).is_none()
            || !container_ids(items, root).contains(&request.object_id)
        {
            return ItemResult::of(ItemStatus::InvalidItem);
        }
        if !self.state.inventory.transactions().can_begin_request() {
            return ItemResult::of(ItemStatus::Busy);
        }
        let item = ObjectRef::Guid(request.object_id);
        let _ = self.state.inventory.take_backpack_placement();
        let ran = self.run_item(if request.main_pack {
            ItemCommand::PickUpToMainPack(item)
        } else {
            ItemCommand::PlaceInPack(item)
        });
        // The placement's endings that send nothing are not a start: a
        // plugin looting into full packs would wait for a completion that
        // never comes.
        match self.state.inventory.take_backpack_placement() {
            Some(BackpackPlacement::NoRoom) => ItemResult::because(
                ItemStatus::Refused,
                "no pack the player has open has room for it",
            ),
            Some(BackpackPlacement::AlreadyPending) => ItemResult::of(ItemStatus::Busy),
            Some(BackpackPlacement::NotDispatched) => ItemResult::because(
                ItemStatus::Refused,
                "the merge this would have become did not go out",
            ),
            Some(BackpackPlacement::Sent) | None => ran,
        }
    }

    /// Put an item into the open container at a slot, as a drop on that
    /// container's window does: the whole stack, or `amount` of it split
    /// off. The item is the player's own or already in the open container
    /// (a reorder).
    pub(super) fn loot_put(&mut self, request: MoveRequest) -> ItemResult {
        if !self.in_world() {
            return ItemResult::of(ItemStatus::Unavailable);
        }
        let root = self
            .state
            .inventory
            .external_containers()
            .current_container_id();
        if request.container_id == 0 || request.container_id != root {
            return ItemResult::of(ItemStatus::InvalidTarget);
        }
        let player = self.player();
        let items = self.state.world.items();
        let item = owned(items, player, request.object_id).or_else(|| {
            container_ids(items, root)
                .contains(&request.object_id)
                .then(|| items.get(request.object_id))
                .flatten()
        });
        let Some(item) = item else {
            return ItemResult::of(ItemStatus::InvalidItem);
        };
        if !valid_amount(item, request.amount) {
            return ItemResult::because(ItemStatus::Refused, "Invalid stack quantity.");
        }
        // Everything the drop waits on is busy here, before anything runs: a
        // request in flight, an item shown waiting at a slot, a wield.
        let inventory = &self.state.inventory;
        if !inventory.transactions().can_begin_request()
            || inventory.pending_placement().is_some()
            || inventory.is_wield_busy()
        {
            return ItemResult::of(ItemStatus::Busy);
        }
        let ran = self.run_item(ItemCommand::DropInto {
            item: ObjectRef::Guid(request.object_id),
            container: ObjectRef::Guid(root),
            slot: request.placement,
            amount: request.amount,
            pickup: false,
            join_stack: false,
        });
        // A put the inventory did not send is never a start, or a plugin
        // would wait for an answer that never comes.
        let sent = self
            .state
            .inventory
            .transactions()
            .pending()
            .is_some_and(|p| p.dispatched && p.item_id == request.object_id);
        if ran.status == ItemStatus::Started && !sent {
            return ItemResult::because(ItemStatus::Refused, "the put did not go out");
        }
        ran
    }
}

#[cfg(test)]
mod tests {
    use ac_plugin::abi::world::ObjectClass;

    use super::*;

    #[test]
    fn each_swap_is_sampled_in_the_middle_of_its_recoloured_range() {
        // OpenAC `RuntimeAutomationSurfaceTests.ProjectInventoryItem_
        // SamplesTheColourInTheMiddleOfEachSwappedRange`: a swap
        // starting at block 3 (colour 24) covering 4 blocks (32 colours) is
        // sampled at colour 24 + 16 = 40, where the loot tools' byte position
        // 4*16 + 3*32 + 8 = 168 lands in the file. The echo source answers
        // every colour with its own index in the red channel.
        let swaps = [ac_proto::SubPalette {
            sub_palette_id: 0x0400_0123,
            offset: 3,
            length: 4,
        }];
        let asked = std::cell::Cell::new(usize::MAX);
        let palettes = project_palettes(&swaps, |_, index| {
            asked.set(index);
            Some((u8::try_from(index).unwrap(), 0, 0))
        });
        assert_eq!(asked.get(), 40);
        assert_eq!(
            palettes,
            [Palette {
                palette_id: 0x0400_0123,
                offset: 3,
                length: 4,
                red: 40,
                green: 0,
                blue: 0,
            }]
        );
        // A palette without that colour reads black.
        assert_eq!(project_palettes(&swaps, |_, _| None)[0].red, 0);
    }

    #[test]
    fn a_stack_amount_is_zero_or_within_the_stack() {
        let mut item = ClientObject::new(1);
        item.stack_size = 5;
        assert!(valid_amount(&item, 0));
        assert!(valid_amount(&item, 5));
        assert!(!valid_amount(&item, 6));
        item.stack_size = 0;
        assert!(valid_amount(&item, 1));
        assert!(!valid_amount(&item, 2));
        assert_eq!(classify(Some(&item)), ObjectClass::Unknown);
    }
}
