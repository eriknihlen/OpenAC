//! What using an item, or dropping one somewhere, means: the primary use
//! (pick up, wield, sort onto the body, trade, salvage, play), the use
//! request with or without a target, and where a dragged item goes.
//!
//! The rules decide; the runtime carries out the returned actions in order.

use super::{EquipMask, ItemType, ItemUseable, PublicWeenieFlags};

/// The primary meaning of using an item, before any use request.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
#[repr(u16)]
pub enum ItemPrimaryUseResult {
    None = 0,
    ItemUse = 1,
    PlaceInBackpack = 2,
    WieldRight = 3,
    AutoSort = 4,
    OpenSecureTrade = 5,
    OpenSalvage = 6,
    BeginGame = 7,
    WieldLeft = 8,
}

/// The facts about one object the rules read.
#[derive(Debug, Clone, PartialEq)]
pub struct ItemPolicyObject {
    pub id: u32,
    pub item_type: ItemType,
    pub flags: PublicWeenieFlags,
    pub container_id: u32,
    pub wielder_id: u32,
    pub valid_locations: EquipMask,
    pub current_location: EquipMask,
    pub combat_use: u8,
    pub items_capacity: i32,
    pub containers_capacity: i32,
    pub useability: ItemUseable,
    pub target_type: ItemType,
    pub owned_by_player: bool,
    pub is_container: bool,
    pub is_component_pack: bool,
    pub trade_state: i32,
    pub stack_size: i32,
    /// The size the stack splits at (a drag of fewer splits it).
    pub max_split_size: i32,
    /// The item is out in the 3D world already.
    pub is_in_3d_view: bool,
    /// The name the notices use; blank reads as "item".
    pub name: String,
}

impl Default for ItemPolicyObject {
    fn default() -> Self {
        Self {
            id: 0,
            item_type: ItemType::NONE,
            flags: PublicWeenieFlags::NONE,
            container_id: 0,
            wielder_id: 0,
            valid_locations: EquipMask::NONE,
            current_location: EquipMask::NONE,
            combat_use: 0,
            items_capacity: 0,
            containers_capacity: 0,
            useability: ItemUseable::UNDEF,
            target_type: ItemType::NONE,
            owned_by_player: false,
            is_container: false,
            is_component_pack: false,
            trade_state: 0,
            stack_size: 0,
            max_split_size: 0,
            is_in_3d_view: false,
            name: "item".into(),
        }
    }
}

impl ItemPolicyObject {
    /// The object is a player.
    #[must_use]
    pub fn is_player(&self) -> bool {
        self.flags.intersects(PublicWeenieFlags::PLAYER)
    }
}

/// One step the runtime carries out.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ItemPolicyActionKind {
    PlaceInBackpack,
    WieldRight,
    AutoSort,
    OpenSecureTrade,
    OpenSalvage,
    BeginGame,
    WieldLeft,
    OpenContainedContainer,
    SetGroundObject,
    EnterTargetMode,
    SendUse,
    SendUseWithTarget,
    IncrementBusy,
    ConfirmPlayerKillerSwitch,
    ConfirmNonPlayerKillerSwitch,
    ConfirmVolatileRare,
    MergeStack,
    SellToVendor,
    StartSecureTrade,
    GiveToTarget,
    PlaceInContainer,
    SplitToWorld,
    DropToWorld,
    /// Print `message`; nothing else happens.
    Reject,
    /// Print `message`, the client's own line saying what the use is doing
    /// ("Using the ...", "Approaching ..."); the steps around it go on.
    Announce,
    /// Ask before an empty mana stone destroys `target_id` to take its
    /// mana; `message` is the question. The use goes out only after a yes.
    ConfirmManaStoneDrain,
}

/// A step and its operands.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ItemPolicyAction {
    pub kind: ItemPolicyActionKind,
    pub object_id: u32,
    pub target_id: u32,
    pub amount: i32,
    pub message: Option<String>,
}

impl ItemPolicyAction {
    fn on(kind: ItemPolicyActionKind, object_id: u32) -> Self {
        Self {
            kind,
            object_id,
            target_id: 0,
            amount: 0,
            message: None,
        }
    }

    fn with(kind: ItemPolicyActionKind, object_id: u32, target_id: u32, amount: i32) -> Self {
        Self {
            kind,
            object_id,
            target_id,
            amount,
            message: None,
        }
    }

    fn reject(message: String) -> Self {
        Self::say(ItemPolicyActionKind::Reject, message)
    }

    fn announce(message: String) -> Self {
        Self::say(ItemPolicyActionKind::Announce, message)
    }

    fn say(kind: ItemPolicyActionKind, message: String) -> Self {
        Self {
            kind,
            object_id: 0,
            target_id: 0,
            amount: 0,
            message: Some(message),
        }
    }
}

/// Inputs to a use.
#[derive(Debug, Clone, PartialEq)]
pub struct ItemUsePolicyInput {
    pub source: ItemPolicyObject,
    pub player_id: u32,
    /// The ground container being viewed; 0 for none.
    pub ground_object_id: u32,
    pub ready_for_inventory_request: bool,
    /// The open vendor; 0 for none.
    pub active_vendor_id: u32,
    /// The caller already knows what the use means (e.g. using a wielded
    /// item): skip the primary classification.
    pub bypass_classification: bool,
    /// Use on the current selection instead of entering target mode.
    pub use_current_selection: bool,
    pub selected_target: Option<ItemPolicyObject>,
    pub confirm_volatile_rare_uses: bool,
    pub in_non_combat_mode: bool,
}

/// What a use decided: whether the click was consumed, and the steps.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ItemUsePolicyDecision {
    pub consumed: bool,
    pub actions: Vec<ItemPolicyAction>,
}

/// Inputs to a drop of a dragged item.
#[derive(Debug, Clone, PartialEq)]
pub struct ItemPlacementPolicyInput {
    pub item: ItemPolicyObject,
    pub player_id: u32,
    pub ground_object_id: u32,
    pub ready_for_inventory_request: bool,
    /// What it was dropped on; 0 for the ground.
    pub target_id: u32,
    pub target: Option<ItemPolicyObject>,
    pub allow_ground_fallback: bool,
    /// The drop is a stack merge the caller already accepted.
    pub merge_accepted: bool,
    pub drag_on_player_opens_secure_trade: bool,
    pub player_on_ground: bool,
    pub split_size: i32,
}

/// What a drop decided: the drag handler's return value, and the steps.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ItemPlacementPolicyDecision {
    pub return_value: bool,
    pub actions: Vec<ItemPolicyAction>,
}

/// Body armor slots.
const BODY_LOCATIONS: EquipMask = EquipMask(0x0000_7E00);
/// Clothing slots plus the cloak.
const CLOTHING_LOCATIONS: EquipMask = EquipMask(0x0800_01FF);
/// Jewelry, trinket and sigil slots.
const HELD_LOCATIONS: EquipMask = EquipMask(0x7C0F_8000);

/// Whether the toolbar's use button is enabled for a selection: anything
/// with a combat use, armor, clothing or jewelry, or anything usable.
#[must_use]
pub fn is_toolbar_use_enabled(
    item_type: ItemType,
    combat_use: u8,
    useability: ItemUseable,
) -> bool {
    combat_use != 0
        || item_type.intersects(ItemType::ARMOR | ItemType::CLOTHING | ItemType::JEWELRY)
        || useability.is_useable()
}

/// The primary meaning of using `item`: pick up a loose item (or one in the
/// viewed ground container); for an owned item, wield a weapon, caster or
/// wield-on-use item, sort it onto a free body slot, or open salvage for a
/// tinkering tool; for someone else's game board, start a game; otherwise
/// a plain use if usable, or a trade with another player.
#[must_use]
pub fn determine_use_result(
    item: &ItemPolicyObject,
    player_id: u32,
    ground_object_id: u32,
) -> ItemPrimaryUseResult {
    let loose_or_viewed = (item.container_id == 0
        && !item.flags.intersects(PublicWeenieFlags::STUCK))
        || (ground_object_id != 0 && item.container_id == ground_object_id);

    if loose_or_viewed && (item.wielder_id == 0 || item.wielder_id == player_id) {
        let ordinary_loose_item = !item.flags.intersects(PublicWeenieFlags::REQUIRES_PACK_SLOT)
            && item.items_capacity == 0
            && item.containers_capacity == 0;
        if ordinary_loose_item || item.is_component_pack {
            return ItemPrimaryUseResult::PlaceInBackpack;
        }
    }

    if item.owned_by_player {
        let wield_on_primary_use = item.combat_use != 0
            || item.item_type.intersects(ItemType::CASTER)
            || item.flags.intersects(PublicWeenieFlags::WIELD_ON_USE);
        if wield_on_primary_use && item.wielder_id != player_id {
            return if item.flags.intersects(PublicWeenieFlags::WIELD_LEFT) {
                ItemPrimaryUseResult::WieldLeft
            } else {
                ItemPrimaryUseResult::WieldRight
            };
        }
        if has_unused_location(item.valid_locations, item.current_location) {
            return ItemPrimaryUseResult::AutoSort;
        }
        if item.item_type.intersects(ItemType::TINKERING_TOOL) {
            return ItemPrimaryUseResult::OpenSalvage;
        }
    } else if item.item_type.intersects(ItemType::GAMEBOARD) {
        return ItemPrimaryUseResult::BeginGame;
    }

    if item.useability.is_useable() {
        return ItemPrimaryUseResult::ItemUse;
    }
    if item.is_player() && item.id != player_id {
        return ItemPrimaryUseResult::OpenSecureTrade;
    }
    ItemPrimaryUseResult::None
}

/// Using an item: the readiness and vendor gates, the primary
/// classification, then trade and wield gates, target mode or a use with
/// the selected target, the confirmations, a plain use, and finally the
/// notices for things that cannot be used.
#[must_use]
pub fn decide_use(input: &ItemUsePolicyInput) -> ItemUsePolicyDecision {
    let source = &input.source;
    if !input.ready_for_inventory_request {
        return consumed(Vec::new());
    }
    if input.active_vendor_id != 0 && source.container_id == input.active_vendor_id {
        return consumed(Vec::new());
    }

    if !input.bypass_classification {
        let primary = determine_use_result(source, input.player_id, input.ground_object_id);
        if (ItemPrimaryUseResult::PlaceInBackpack..=ItemPrimaryUseResult::BeginGame)
            .contains(&primary)
        {
            return consumed(build_using_item_actions(
                source,
                input.ground_object_id,
                primary,
                true,
            ));
        }
    }

    if source.trade_state == 1 {
        return reject(format!(
            "You cannot use the {} because you are trading it",
            name_of(source)
        ));
    }

    if source.current_location == EquipMask::NONE
        && source.useability.least_limited_source_use() == ItemUseable::WIELDED
    {
        return reject(format!("You must wield the {} to use it", name_of(source)));
    }

    let classify = !input.bypass_classification;
    let primary = || determine_use_result(source, input.player_id, input.ground_object_id);

    if source.useability.is_targeted() {
        if !input.use_current_selection {
            return consumed(vec![ItemPolicyAction::on(
                ItemPolicyActionKind::EnterTargetMode,
                source.id,
            )]);
        }
        let Some(target) = &input.selected_target else {
            return reject(format!(
                "Select your target before using the {}",
                name_of(source)
            ));
        };
        let announcement = match use_on_target_line(source, target, input.player_id) {
            Ok(line) => line,
            Err(failure) => return reject(failure),
        };
        let mut actions = vec![
            ItemPolicyAction::announce(announcement),
            ItemPolicyAction::with(
                ItemPolicyActionKind::SendUseWithTarget,
                source.id,
                target.id,
                0,
            ),
            ItemPolicyAction::on(ItemPolicyActionKind::IncrementBusy, 0),
        ];
        actions.extend(build_using_item_actions(
            source,
            input.ground_object_id,
            primary(),
            classify,
        ));
        return consumed(actions);
    }

    if source.useability.is_useable() {
        if source
            .flags
            .intersects(PublicWeenieFlags::PLAYER_KILLER_SWITCH)
        {
            return consumed(vec![ItemPolicyAction::on(
                ItemPolicyActionKind::ConfirmPlayerKillerSwitch,
                source.id,
            )]);
        }
        if source
            .flags
            .intersects(PublicWeenieFlags::NON_PLAYER_KILLER_SWITCH)
        {
            return consumed(vec![ItemPolicyAction::on(
                ItemPolicyActionKind::ConfirmNonPlayerKillerSwitch,
                source.id,
            )]);
        }
        if source.flags.intersects(PublicWeenieFlags::VOLATILE_RARE)
            && input.confirm_volatile_rare_uses
        {
            return consumed(vec![ItemPolicyAction::on(
                ItemPolicyActionKind::ConfirmVolatileRare,
                source.id,
            )]);
        }
        let mut actions = vec![
            ItemPolicyAction::on(ItemPolicyActionKind::SendUse, source.id),
            ItemPolicyAction::on(ItemPolicyActionKind::IncrementBusy, 0),
        ];
        actions.extend(build_using_item_actions(
            source,
            input.ground_object_id,
            primary(),
            classify,
        ));
        actions.push(ItemPolicyAction::announce(using_line(source)));
        return consumed(actions);
    }

    let fallback = build_using_item_actions(source, input.ground_object_id, primary(), classify);
    if !fallback.is_empty() {
        return consumed(fallback);
    }
    if source.id == input.player_id {
        return not_consumed();
    }
    if source.flags.intersects(PublicWeenieFlags::DOOR) {
        return reject(format!(
            "You can't open or close this {} that way",
            name_of(source)
        ));
    }
    let attackable = source.flags.intersects(PublicWeenieFlags::ATTACKABLE);
    if attackable && input.in_non_combat_mode {
        return reject(format!(
            "To attack {}, click on the dove icon first",
            name_of(source)
        ));
    }
    if !attackable || input.in_non_combat_mode {
        return reject(format!("The {} cannot be used", name_of(source)));
    }
    not_consumed()
}

/// Whether `source` may be used on `target`.
#[must_use]
pub fn is_target_compatible(
    source: &ItemPolicyObject,
    target: &ItemPolicyObject,
    player_id: u32,
) -> bool {
    target_compatibility_failure(source, target, player_id).is_none()
}

/// The line a plain use of `source` shows once it has gone out: a creature
/// (a vendor, an NPC, which the character walks to) is "Approaching
/// <name>"; anything else is "Using the <name>".
#[must_use]
pub fn using_line(source: &ItemPolicyObject) -> String {
    if source.item_type.intersects(ItemType::CREATURE) {
        format!("Approaching {}", name_of(source))
    } else {
        format!("Using the {}", name_of(source))
    }
}

/// Using `source` on `target`: `Ok` with the line the use shows ("Using
/// the <source> on <player>", else "... with the <target>"), or `Err` with
/// why it cannot be used on it.
///
/// # Errors
/// The refusal text when `source` may not be used on `target`.
pub fn use_on_target_line(
    source: &ItemPolicyObject,
    target: &ItemPolicyObject,
    player_id: u32,
) -> Result<String, String> {
    if let Some(failure) = target_compatibility_failure(source, target, player_id) {
        return Err(failure);
    }
    let joint = if target.is_player() {
        " on "
    } else {
        " with the "
    };
    Ok(format!(
        "Using the {}{joint}{}",
        name_of(source),
        name_of(target)
    ))
}

fn target_compatibility_failure(
    source: &ItemPolicyObject,
    target: &ItemPolicyObject,
    player_id: u32,
) -> Option<String> {
    if source.trade_state == 1 {
        return Some(format!(
            "You cannot use the {} because you are trading it",
            name_of(source)
        ));
    }
    if target.trade_state == 1 {
        return Some(format!(
            "You can't use the {} on an item you are trading",
            name_of(source)
        ));
    }

    let flags = source.useability.target_flags();
    if !target.owned_by_player {
        let least = source.useability.least_limited_target_use();
        if least.intersects(ItemUseable::CONTAINED) {
            if !(target.id == player_id && flags.intersects(ItemUseable::SELF)) {
                return Some(format!(
                    "You can't use the {} on what you don't own",
                    name_of(source)
                ));
            }
        } else if least.intersects(ItemUseable::WIELDED) {
            return Some(format!(
                "You can't use the {} on what you aren't wielding",
                name_of(source)
            ));
        }
    }

    if target.id == player_id && !flags.intersects(ItemUseable::SELF) {
        return Some(format!("Cannot use the {} on yourself", name_of(source)));
    }

    if source.target_type.intersects(target.item_type) {
        None
    } else {
        Some(format!(
            "Cannot use the {} with the {}",
            name_of(source),
            name_of(target)
        ))
    }
}

/// Dropping a dragged item on something (or on the ground): onto the
/// player puts it in the backpack; otherwise it must be owned and not being
/// traded, then a merge, a sale to a vendor, a secure trade, a gift to a
/// creature, a container that must be open, or the ground.
#[must_use]
pub fn decide_placement(input: &ItemPlacementPolicyInput) -> ItemPlacementPolicyDecision {
    if !input.ready_for_inventory_request {
        return placement(false, Vec::new());
    }
    let item = &input.item;
    if input.target_id == input.player_id {
        return placement(
            true,
            vec![ItemPolicyAction::on(
                ItemPolicyActionKind::PlaceInBackpack,
                item.id,
            )],
        );
    }
    if !item.owned_by_player {
        return placement_reject(format!("You must first pick up the {}", name_of(item)));
    }
    if item.trade_state != 0 {
        return placement_reject(format!(
            "You are trading the {}, it cannot be dropped",
            name_of(item)
        ));
    }

    if input.target_id == 0 {
        return if input.allow_ground_fallback {
            place_on_ground(input)
        } else {
            placement(false, Vec::new())
        };
    }
    if input.merge_accepted {
        return placement(
            true,
            vec![ItemPolicyAction::with(
                ItemPolicyActionKind::MergeStack,
                item.id,
                input.target_id,
                input.split_size,
            )],
        );
    }
    let Some(target) = &input.target else {
        return if input.allow_ground_fallback {
            place_on_ground(input)
        } else {
            placement(false, Vec::new())
        };
    };

    if target.flags.intersects(PublicWeenieFlags::VENDOR) {
        if input.split_size >= item.max_split_size {
            return placement(
                false,
                vec![ItemPolicyAction::with(
                    ItemPolicyActionKind::SellToVendor,
                    item.id,
                    target.id,
                    input.split_size,
                )],
            );
        }
        return placement_reject("You must split the stack before selling it.".into());
    }

    if input.drag_on_player_opens_secure_trade && target.is_player() {
        return placement(
            false,
            vec![ItemPolicyAction::with(
                ItemPolicyActionKind::StartSecureTrade,
                item.id,
                target.id,
                input.split_size,
            )],
        );
    }

    if target.item_type == ItemType::CREATURE {
        return placement(
            true,
            vec![ItemPolicyAction::with(
                ItemPolicyActionKind::GiveToTarget,
                item.id,
                target.id,
                input.split_size,
            )],
        );
    }

    if target.is_container {
        if !target.flags.intersects(PublicWeenieFlags::OPENABLE) {
            return placement_reject(format!("The {} is locked", name_of(target)));
        }
        if target.id != input.ground_object_id {
            return placement_reject(format!("You must open the {} first", name_of(target)));
        }
        return placement(
            true,
            vec![ItemPolicyAction::with(
                ItemPolicyActionKind::PlaceInContainer,
                item.id,
                target.id,
                input.split_size,
            )],
        );
    }

    if input.allow_ground_fallback {
        return place_on_ground(input);
    }
    placement_reject(format!(
        "Cannot give {} to {}",
        name_of(item),
        name_of(target)
    ))
}

/// The steps a primary classification turns into. With `classify` false
/// the primary step is skipped (it would strip a wielded item off the
/// player the moment it is used); opening an owned container and viewing
/// a ground container always apply.
fn build_using_item_actions(
    item: &ItemPolicyObject,
    ground_object_id: u32,
    primary: ItemPrimaryUseResult,
    classify: bool,
) -> Vec<ItemPolicyAction> {
    let mut actions = Vec::with_capacity(3);
    let primary_action = match primary {
        _ if !classify => None,
        ItemPrimaryUseResult::PlaceInBackpack => Some(ItemPolicyActionKind::PlaceInBackpack),
        ItemPrimaryUseResult::WieldRight => Some(ItemPolicyActionKind::WieldRight),
        ItemPrimaryUseResult::AutoSort => Some(ItemPolicyActionKind::AutoSort),
        ItemPrimaryUseResult::OpenSecureTrade => Some(ItemPolicyActionKind::OpenSecureTrade),
        ItemPrimaryUseResult::OpenSalvage => Some(ItemPolicyActionKind::OpenSalvage),
        ItemPrimaryUseResult::BeginGame => Some(ItemPolicyActionKind::BeginGame),
        ItemPrimaryUseResult::WieldLeft => Some(ItemPolicyActionKind::WieldLeft),
        ItemPrimaryUseResult::None | ItemPrimaryUseResult::ItemUse => None,
    };
    if let Some(kind) = primary_action {
        actions.push(ItemPolicyAction::on(kind, item.id));
    }
    if item.owned_by_player && item.is_container {
        actions.push(ItemPolicyAction::on(
            ItemPolicyActionKind::OpenContainedContainer,
            item.id,
        ));
    }
    if !item.owned_by_player
        && item.is_container
        && item.useability.is_useable()
        && !item.useability.is_targeted()
    {
        actions.push(ItemPolicyAction::with(
            ItemPolicyActionKind::SetGroundObject,
            item.id,
            ground_object_id,
            0,
        ));
    }
    actions
}

/// What asking to open a container on the ground comes to.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum GroundObjectDecision {
    /// It is described as openable: view it.
    Open,
    /// Leave it unopened, with the client-local line that says why (none
    /// for a creature).
    Refuse(Option<String>),
}

/// Whether the use of a container on the ground (`container`, `None` when
/// the client has no object for it) also views it. Only a container
/// described as openable is viewed; a locked one is not (its use still went
/// to the server) and the client says so itself, since the server answers
/// a locked container's use with no text of its own.
#[must_use]
pub fn decide_set_ground_object(container: Option<&ItemPolicyObject>) -> GroundObjectDecision {
    let Some(container) = container else {
        return GroundObjectDecision::Refuse(Some(
            "The object has no representation on the client.".into(),
        ));
    };
    if container.flags.intersects(PublicWeenieFlags::OPENABLE) {
        return GroundObjectDecision::Open;
    }
    if container.item_type.intersects(ItemType::CREATURE) {
        return GroundObjectDecision::Refuse(None);
    }
    GroundObjectDecision::Refuse(Some(format!("The {} is locked", name_of(container))))
}

fn has_unused_location(valid: EquipMask, current: EquipMask) -> bool {
    (valid.intersects(BODY_LOCATIONS) && !current.intersects(BODY_LOCATIONS))
        || (valid.intersects(CLOTHING_LOCATIONS) && !current.intersects(CLOTHING_LOCATIONS))
        || (valid.intersects(HELD_LOCATIONS) && !current.intersects(HELD_LOCATIONS))
}

fn place_on_ground(input: &ItemPlacementPolicyInput) -> ItemPlacementPolicyDecision {
    if !input.player_on_ground {
        return placement_reject("You cannot do that in mid air".into());
    }
    if input.split_size < input.item.max_split_size {
        return placement(
            true,
            vec![ItemPolicyAction::with(
                ItemPolicyActionKind::SplitToWorld,
                input.item.id,
                0,
                input.split_size,
            )],
        );
    }
    if !input.item.is_in_3d_view {
        return placement(
            true,
            vec![ItemPolicyAction::on(
                ItemPolicyActionKind::DropToWorld,
                input.item.id,
            )],
        );
    }
    placement_reject("Move cancelled".into())
}

fn name_of(item: &ItemPolicyObject) -> &str {
    if item.name.trim().is_empty() {
        "item"
    } else {
        &item.name
    }
}

fn consumed(actions: Vec<ItemPolicyAction>) -> ItemUsePolicyDecision {
    ItemUsePolicyDecision {
        consumed: true,
        actions,
    }
}

fn not_consumed() -> ItemUsePolicyDecision {
    ItemUsePolicyDecision {
        consumed: false,
        actions: Vec::new(),
    }
}

fn reject(message: String) -> ItemUsePolicyDecision {
    consumed(vec![ItemPolicyAction::reject(message)])
}

fn placement(return_value: bool, actions: Vec<ItemPolicyAction>) -> ItemPlacementPolicyDecision {
    ItemPlacementPolicyDecision {
        return_value,
        actions,
    }
}

fn placement_reject(message: String) -> ItemPlacementPolicyDecision {
    placement(false, vec![ItemPolicyAction::reject(message)])
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/ItemInteractionPolicyTests.cs`.

    use super::*;

    const PLAYER: u32 = 0x5000_0001;
    const GROUND_CONTAINER: u32 = 0x7000_0001;

    fn obj(id: u32) -> ItemPolicyObject {
        ItemPolicyObject {
            id,
            item_type: ItemType::MISC,
            useability: ItemUseable::NO,
            stack_size: 1,
            max_split_size: 1,
            ..ItemPolicyObject::default()
        }
    }

    fn determine(item: &ItemPolicyObject) -> ItemPrimaryUseResult {
        determine_use_result(item, PLAYER, 0)
    }

    fn owned_direct() -> ItemPolicyObject {
        ItemPolicyObject {
            container_id: PLAYER,
            owned_by_player: true,
            flags: PublicWeenieFlags::REQUIRES_PACK_SLOT,
            useability: ItemUseable::CONTAINED,
            ..obj(0x6001)
        }
    }

    fn use_input(source: ItemPolicyObject) -> ItemUsePolicyInput {
        ItemUsePolicyInput {
            source,
            player_id: PLAYER,
            ground_object_id: 0,
            ready_for_inventory_request: true,
            active_vendor_id: 0,
            bypass_classification: false,
            use_current_selection: false,
            selected_target: None,
            confirm_volatile_rare_uses: true,
            in_non_combat_mode: false,
        }
    }

    fn place(item: ItemPolicyObject, target: ItemPolicyObject) -> ItemPlacementPolicyInput {
        ItemPlacementPolicyInput {
            split_size: item.stack_size,
            item,
            player_id: PLAYER,
            ground_object_id: 0,
            ready_for_inventory_request: true,
            target_id: target.id,
            target: Some(target),
            allow_ground_fallback: false,
            merge_accepted: false,
            drag_on_player_opens_secure_trade: true,
            player_on_ground: true,
        }
    }

    fn place_on_ground_input(item: ItemPolicyObject) -> ItemPlacementPolicyInput {
        ItemPlacementPolicyInput {
            split_size: item.stack_size,
            item,
            player_id: PLAYER,
            ground_object_id: 0,
            ready_for_inventory_request: true,
            target_id: 0,
            target: None,
            allow_ground_fallback: true,
            merge_accepted: false,
            drag_on_player_opens_secure_trade: true,
            player_on_ground: true,
        }
    }

    fn kinds(actions: &[ItemPolicyAction]) -> Vec<ItemPolicyActionKind> {
        actions.iter().map(|action| action.kind).collect()
    }

    fn single(actions: &[ItemPolicyAction]) -> &ItemPolicyAction {
        assert_eq!(actions.len(), 1, "{actions:?}");
        &actions[0]
    }

    #[test]
    fn toolbar_use_enabled_matches_selection_predicate() {
        assert!(is_toolbar_use_enabled(
            ItemType::MELEE_WEAPON,
            1,
            ItemUseable::NO
        ));
        assert!(is_toolbar_use_enabled(
            ItemType::ARMOR | ItemType::CLOTHING,
            0,
            ItemUseable::NO
        ));
        assert!(is_toolbar_use_enabled(
            ItemType::MISC,
            0,
            ItemUseable(0x0022_0008)
        ));
        assert!(!is_toolbar_use_enabled(
            ItemType::SPELL_COMPONENTS,
            0,
            ItemUseable::NO
        ));
    }

    #[test]
    fn loose_item_and_component_pack_match_pickup_branch() {
        assert_eq!(
            determine(&obj(0x6001)),
            ItemPrimaryUseResult::PlaceInBackpack
        );
        let requires_slot = ItemPolicyObject {
            flags: PublicWeenieFlags::REQUIRES_PACK_SLOT,
            items_capacity: 12,
            ..obj(0x6002)
        };
        assert_eq!(determine(&requires_slot), ItemPrimaryUseResult::None);
        assert_eq!(
            determine(&ItemPolicyObject {
                is_component_pack: true,
                ..requires_slot
            }),
            ItemPrimaryUseResult::PlaceInBackpack
        );
    }

    #[test]
    fn only_allows_contained_pickup_from_viewed_ground_container() {
        let item = ItemPolicyObject {
            container_id: GROUND_CONTAINER,
            ..obj(0x6001)
        };
        assert_eq!(
            determine_use_result(&item, PLAYER, 0),
            ItemPrimaryUseResult::None
        );
        assert_eq!(
            determine_use_result(&item, PLAYER, GROUND_CONTAINER),
            ItemPrimaryUseResult::PlaceInBackpack
        );
    }

    #[test]
    fn ports_all_owned_primary_classifications() {
        let owned = ItemPolicyObject {
            container_id: PLAYER,
            owned_by_player: true,
            flags: PublicWeenieFlags::REQUIRES_PACK_SLOT,
            ..obj(0x6001)
        };
        assert_eq!(
            determine(&ItemPolicyObject {
                combat_use: 1,
                ..owned.clone()
            }),
            ItemPrimaryUseResult::WieldRight
        );
        assert_eq!(
            determine(&ItemPolicyObject {
                combat_use: 1,
                flags: owned.flags | PublicWeenieFlags::WIELD_LEFT,
                ..owned.clone()
            }),
            ItemPrimaryUseResult::WieldLeft
        );
        assert_eq!(
            determine(&ItemPolicyObject {
                valid_locations: EquipMask::HEAD_WEAR,
                ..owned.clone()
            }),
            ItemPrimaryUseResult::AutoSort
        );
        assert_eq!(
            determine(&ItemPolicyObject {
                item_type: ItemType::TINKERING_TOOL,
                ..owned.clone()
            }),
            ItemPrimaryUseResult::OpenSalvage
        );
        assert_eq!(
            determine(&ItemPolicyObject {
                useability: ItemUseable::CONTAINED,
                ..owned
            }),
            ItemPrimaryUseResult::ItemUse
        );
    }

    #[test]
    fn zero_valued_owned_gem_is_item_use() {
        let favor = ItemPolicyObject {
            item_type: ItemType::GEM,
            container_id: PLAYER,
            owned_by_player: true,
            flags: PublicWeenieFlags::REQUIRES_PACK_SLOT,
            useability: ItemUseable::UNDEF,
            ..obj(0x6002)
        };
        assert_eq!(determine(&favor), ItemPrimaryUseResult::ItemUse);
        let decision = decide_use(&use_input(favor));
        assert_eq!(
            kinds(&decision.actions),
            vec![
                ItemPolicyActionKind::SendUse,
                ItemPolicyActionKind::IncrementBusy,
                ItemPolicyActionKind::Announce
            ]
        );
    }

    #[test]
    fn non_owned_game_and_player_paths_are_distinct() {
        assert_eq!(
            determine(&ItemPolicyObject {
                item_type: ItemType::GAMEBOARD,
                flags: PublicWeenieFlags::REQUIRES_PACK_SLOT,
                ..obj(0x6001)
            }),
            ItemPrimaryUseResult::BeginGame
        );
        assert_eq!(
            determine(&ItemPolicyObject {
                flags: PublicWeenieFlags::PLAYER | PublicWeenieFlags::REQUIRES_PACK_SLOT,
                ..obj(0x6002)
            }),
            ItemPrimaryUseResult::OpenSecureTrade
        );
    }

    #[test]
    fn result_eight_is_outside_the_early_bound_but_still_wields_left() {
        let source = ItemPolicyObject {
            container_id: PLAYER,
            owned_by_player: true,
            combat_use: 1,
            flags: PublicWeenieFlags::REQUIRES_PACK_SLOT | PublicWeenieFlags::WIELD_LEFT,
            ..obj(0x6001)
        };
        let decision = decide_use(&use_input(source));
        assert_eq!(
            kinds(&decision.actions),
            vec![ItemPolicyActionKind::WieldLeft]
        );
    }

    #[test]
    fn ready_vendor_trade_and_wielded_source_gates_are_ordered() {
        let direct = owned_direct();
        assert!(
            decide_use(&ItemUsePolicyInput {
                ready_for_inventory_request: false,
                ..use_input(direct.clone())
            })
            .actions
            .is_empty()
        );
        assert!(
            decide_use(&ItemUsePolicyInput {
                active_vendor_id: 0x7001,
                ..use_input(ItemPolicyObject {
                    container_id: 0x7001,
                    ..direct.clone()
                })
            })
            .actions
            .is_empty()
        );
        let trading = decide_use(&use_input(ItemPolicyObject {
            trade_state: 1,
            ..direct.clone()
        }));
        let action = single(&trading.actions);
        assert_eq!(action.kind, ItemPolicyActionKind::Reject);
        assert_eq!(
            action.message.as_deref(),
            Some("You cannot use the item because you are trading it")
        );
        let wield_first = decide_use(&use_input(ItemPolicyObject {
            useability: ItemUseable::WIELDED,
            ..direct
        }));
        assert_eq!(
            single(&wield_first.actions).message.as_deref(),
            Some("You must wield the item to use it")
        );
    }

    #[test]
    fn target_compatibility_failures_use_verbatim_messages() {
        let source = ItemPolicyObject {
            name: "mana stone".into(),
            useability: ItemUseable(0x0008_0008),
            target_type: ItemType::MISC,
            ..owned_direct()
        };
        let target = ItemPolicyObject {
            name: "armor".into(),
            item_type: ItemType::ARMOR,
            container_id: PLAYER,
            owned_by_player: true,
            ..obj(0x6002)
        };

        let missing = decide_use(&ItemUsePolicyInput {
            use_current_selection: true,
            ..use_input(source.clone())
        });
        assert_eq!(
            single(&missing.actions).message.as_deref(),
            Some("Select your target before using the mana stone")
        );

        let incompatible = decide_use(&ItemUsePolicyInput {
            use_current_selection: true,
            selected_target: Some(target.clone()),
            ..use_input(source.clone())
        });
        assert_eq!(
            single(&incompatible.actions).message.as_deref(),
            Some("Cannot use the mana stone with the armor")
        );

        let traded = decide_use(&ItemUsePolicyInput {
            use_current_selection: true,
            selected_target: Some(ItemPolicyObject {
                trade_state: 1,
                ..target
            }),
            ..use_input(source)
        });
        assert_eq!(
            single(&traded.actions).message.as_deref(),
            Some("You can't use the mana stone on an item you are trading")
        );
    }

    #[test]
    fn targeted_use_applies_kind_and_location_gate_then_sends_and_marks_busy() {
        let source = ItemPolicyObject {
            useability: ItemUseable(0x0008_0008),
            target_type: ItemType::MISC,
            ..owned_direct()
        };
        let target = ItemPolicyObject {
            container_id: PLAYER,
            owned_by_player: true,
            item_type: ItemType::MISC,
            ..obj(0x6002)
        };
        let decision = decide_use(&ItemUsePolicyInput {
            use_current_selection: true,
            selected_target: Some(target),
            ..use_input(source)
        });
        assert_eq!(
            kinds(&decision.actions),
            vec![
                ItemPolicyActionKind::Announce,
                ItemPolicyActionKind::SendUseWithTarget,
                ItemPolicyActionKind::IncrementBusy
            ]
        );
    }

    fn announcements(actions: &[ItemPolicyAction]) -> Vec<&str> {
        actions
            .iter()
            .filter(|a| a.kind == ItemPolicyActionKind::Announce)
            .filter_map(|a| a.message.as_deref())
            .collect()
    }

    #[test]
    fn a_use_says_using_the_item_and_a_creature_is_approached() {
        // Golden text and order: the retail client sends the use, counts it
        // busy, does the item's own steps (viewing a container on the
        // ground), then prints "Approaching %s" for a creature or "Using
        // the %s" for anything else, as client-local text.
        let chest = ItemPolicyObject {
            name: "Legendary Chest".into(),
            item_type: ItemType::CONTAINER,
            is_container: true,
            flags: PublicWeenieFlags::STUCK,
            useability: ItemUseable::REMOTE,
            items_capacity: 120,
            ..obj(GROUND_CONTAINER)
        };
        let decision = decide_use(&use_input(chest));
        assert_eq!(
            kinds(&decision.actions),
            vec![
                ItemPolicyActionKind::SendUse,
                ItemPolicyActionKind::IncrementBusy,
                ItemPolicyActionKind::SetGroundObject,
                ItemPolicyActionKind::Announce
            ]
        );
        assert_eq!(
            announcements(&decision.actions),
            ["Using the Legendary Chest"]
        );

        let vendor = ItemPolicyObject {
            name: "Thorsten the Shopkeeper".into(),
            item_type: ItemType::CREATURE,
            flags: PublicWeenieFlags::STUCK | PublicWeenieFlags::VENDOR,
            useability: ItemUseable::REMOTE,
            ..obj(0x7000_0002)
        };
        let decision = decide_use(&use_input(vendor));
        assert_eq!(
            announcements(&decision.actions),
            ["Approaching Thorsten the Shopkeeper"]
        );

        // Nothing is said for a use that never goes out.
        let not_useable = decide_use(&use_input(ItemPolicyObject {
            name: "Pillar".into(),
            flags: PublicWeenieFlags::STUCK,
            ..obj(0x7000_0003)
        }));
        assert!(announcements(&not_useable.actions).is_empty());
    }

    #[test]
    fn a_use_on_a_target_says_on_for_a_player_and_with_the_for_anything_else() {
        // Golden text: the retail client's "Using the %s" followed by " on "
        // and the target's name when the target is a player, else " with
        // the " and its name; printed before the use goes out.
        let kit = ItemPolicyObject {
            name: "Healing Kit".into(),
            useability: ItemUseable(0x0008_0008 | (ItemUseable::SELF.0 << 16)),
            target_type: ItemType::CREATURE | ItemType::MISC,
            ..owned_direct()
        };
        let me = ItemPolicyObject {
            name: "Acdream".into(),
            item_type: ItemType::CREATURE,
            flags: PublicWeenieFlags::PLAYER,
            owned_by_player: true,
            ..obj(PLAYER)
        };
        let decision = decide_use(&ItemUsePolicyInput {
            use_current_selection: true,
            selected_target: Some(me.clone()),
            ..use_input(kit.clone())
        });
        assert_eq!(
            announcements(&decision.actions),
            ["Using the Healing Kit on Acdream"]
        );
        assert_eq!(
            use_on_target_line(&kit, &me, PLAYER),
            Ok("Using the Healing Kit on Acdream".to_owned())
        );

        let sword = ItemPolicyObject {
            name: "Sword".into(),
            container_id: PLAYER,
            owned_by_player: true,
            ..obj(0x6002)
        };
        assert_eq!(
            use_on_target_line(&kit, &sword, PLAYER),
            Ok("Using the Healing Kit with the Sword".to_owned())
        );
        let armor = ItemPolicyObject {
            item_type: ItemType::ARMOR,
            name: "armor".into(),
            ..sword
        };
        assert_eq!(
            use_on_target_line(&kit, &armor, PLAYER),
            Err("Cannot use the Healing Kit with the armor".to_owned())
        );
    }

    #[test]
    fn confirmation_flags_block_wire_use() {
        for (flag, expected) in [
            (
                PublicWeenieFlags::PLAYER_KILLER_SWITCH,
                ItemPolicyActionKind::ConfirmPlayerKillerSwitch,
            ),
            (
                PublicWeenieFlags::NON_PLAYER_KILLER_SWITCH,
                ItemPolicyActionKind::ConfirmNonPlayerKillerSwitch,
            ),
            (
                PublicWeenieFlags::VOLATILE_RARE,
                ItemPolicyActionKind::ConfirmVolatileRare,
            ),
        ] {
            let decision = decide_use(&use_input(ItemPolicyObject {
                flags: flag | PublicWeenieFlags::REQUIRES_PACK_SLOT,
                ..owned_direct()
            }));
            assert_eq!(single(&decision.actions).kind, expected);
        }
    }

    #[test]
    fn vendor_and_secure_trade_act_but_keep_false_return() {
        let item = ItemPolicyObject {
            stack_size: 10,
            max_split_size: 10,
            ..owned_direct()
        };
        let vendor = ItemPolicyObject {
            flags: PublicWeenieFlags::VENDOR,
            ..obj(0x7001)
        };
        let sale = decide_placement(&place(item.clone(), vendor));
        assert!(!sale.return_value);
        assert_eq!(
            single(&sale.actions).kind,
            ItemPolicyActionKind::SellToVendor
        );

        let player = ItemPolicyObject {
            flags: PublicWeenieFlags::PLAYER,
            ..obj(0x7002)
        };
        let trade = decide_placement(&place(item, player));
        assert!(!trade.return_value);
        assert_eq!(
            single(&trade.actions).kind,
            ItemPolicyActionKind::StartSecureTrade
        );
    }

    #[test]
    fn ground_stack_and_airborne_matrix_matches() {
        let item = ItemPolicyObject {
            stack_size: 10,
            max_split_size: 10,
            ..owned_direct()
        };
        let airborne = decide_placement(&ItemPlacementPolicyInput {
            player_on_ground: false,
            ..place_on_ground_input(item.clone())
        });
        assert!(!airborne.return_value);
        assert_eq!(
            single(&airborne.actions).message.as_deref(),
            Some("You cannot do that in mid air")
        );

        let split = decide_placement(&ItemPlacementPolicyInput {
            split_size: 4,
            ..place_on_ground_input(item.clone())
        });
        assert!(split.return_value);
        assert_eq!(
            single(&split.actions).kind,
            ItemPolicyActionKind::SplitToWorld
        );

        let full = decide_placement(&place_on_ground_input(item.clone()));
        assert!(full.return_value);
        assert_eq!(
            single(&full.actions).kind,
            ItemPolicyActionKind::DropToWorld
        );

        let already_world = decide_placement(&place_on_ground_input(ItemPolicyObject {
            is_in_3d_view: true,
            ..item
        }));
        assert!(!already_world.return_value);
        assert_eq!(
            single(&already_world.actions).message.as_deref(),
            Some("Move cancelled")
        );
    }

    #[test]
    fn container_requires_openable_and_current_ground_object() {
        let item = owned_direct();
        let container = ItemPolicyObject {
            is_container: true,
            ..obj(GROUND_CONTAINER)
        };
        let locked = decide_placement(&ItemPlacementPolicyInput {
            ground_object_id: GROUND_CONTAINER,
            ..place(item.clone(), container.clone())
        });
        assert!(!locked.return_value);

        let open = ItemPolicyObject {
            flags: PublicWeenieFlags::OPENABLE,
            ..container
        };
        let closed_view = decide_placement(&place(item.clone(), open.clone()));
        assert!(!closed_view.return_value);

        let placed = decide_placement(&ItemPlacementPolicyInput {
            ground_object_id: GROUND_CONTAINER,
            ..place(item, open)
        });
        assert!(placed.return_value);
        assert_eq!(
            single(&placed.actions).kind,
            ItemPolicyActionKind::PlaceInContainer
        );
    }

    #[test]
    fn a_ground_container_opens_only_when_openable_and_a_locked_one_says_so() {
        // Golden text: the retail client's own lines when a container on
        // the ground is used: "The %s is locked" with the object's name,
        // nothing for a creature, and "The object has no representation on
        // the client." when the object is unknown.
        let chest = ItemPolicyObject {
            is_container: true,
            item_type: ItemType::CONTAINER,
            name: "Legendary Chest".into(),
            ..obj(GROUND_CONTAINER)
        };
        assert_eq!(
            decide_set_ground_object(Some(&chest)),
            GroundObjectDecision::Refuse(Some("The Legendary Chest is locked".into()))
        );
        let unlocked = ItemPolicyObject {
            flags: PublicWeenieFlags::OPENABLE,
            ..chest.clone()
        };
        assert_eq!(
            decide_set_ground_object(Some(&unlocked)),
            GroundObjectDecision::Open
        );
        let creature = ItemPolicyObject {
            item_type: ItemType::CREATURE,
            ..chest
        };
        assert_eq!(
            decide_set_ground_object(Some(&creature)),
            GroundObjectDecision::Refuse(None)
        );
        assert_eq!(
            decide_set_ground_object(None),
            GroundObjectDecision::Refuse(Some(
                "The object has no representation on the client.".into()
            ))
        );
    }

    #[test]
    fn placement_follows_the_server_unlocking_and_locking_a_chest() {
        // Placement_followsTheServerUnlockingAndLockingAChest: an unlock
        // arrives as the Locked bool going false; the chest's openable bit
        // is what placement reads, so the bool has to move it, and locking
        // it again takes it away.
        use crate::items::{ClientObject, ClientObjectTable};
        use crate::properties::PropertyBool;
        let mut table = ClientObjectTable::new();
        let mut events = Vec::new();
        let mut chest = ClientObject::new(GROUND_CONTAINER);
        chest.item_type = ItemType::CONTAINER;
        chest.public_weenie_bitfield = Some(PublicWeenieFlags::STUCK);
        table.add_or_update(chest, &mut events);
        let place_into_chest = |table: &ClientObjectTable| {
            let chest = ItemPolicyObject {
                is_container: true,
                flags: table
                    .get(GROUND_CONTAINER)
                    .and_then(|c| c.public_weenie_bitfield)
                    .unwrap_or_default(),
                ..obj(GROUND_CONTAINER)
            };
            decide_placement(&ItemPlacementPolicyInput {
                ground_object_id: GROUND_CONTAINER,
                ..place(owned_direct(), chest)
            })
            .return_value
        };
        assert!(!place_into_chest(&table));

        let locked = PropertyBool::LOCKED.0;
        assert!(table.update_bool_property(GROUND_CONTAINER, locked, false, &mut events));
        assert!(place_into_chest(&table));

        assert!(table.update_bool_property(GROUND_CONTAINER, locked, true, &mut events));
        assert!(!place_into_chest(&table));
    }
}
