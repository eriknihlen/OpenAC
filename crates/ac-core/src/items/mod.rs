//! Items: the object table the runtime keeps for every described object,
//! and the rules that read it (placement, use, paperdoll, vendors,
//! shortcuts, ...).
//!
//! Nothing here raises callbacks: state changes come back as returned
//! events or transitions for the runtime to publish.

pub mod burden;
pub mod component_fill;
mod external_container;
mod house;
pub mod interaction;
mod inventory_request;
mod item_mana;
mod item_type;
mod object;
pub mod paperdoll;
pub mod placement;
mod public_weenie_flags;
pub mod shortcuts;
mod small_rules;
mod stacks;
mod table;
mod tinker_values;
mod transaction;
mod useability;
pub mod vendor;
mod vendor_staging;
mod wire_enums;

pub use external_container::{
    ExternalContainerReset, ExternalContainerState, ExternalContainerTransition,
    ExternalContainerTransitionKind, OpenRequestOutcome,
};
pub use house::HouseRestrictionRecord;
pub use interaction::{
    ItemPlacementPolicyDecision, ItemPlacementPolicyInput, ItemPolicyAction, ItemPolicyActionKind,
    ItemPolicyObject, ItemPrimaryUseResult, ItemUsePolicyDecision, ItemUsePolicyInput,
};
pub use inventory_request::InventoryRequestKind;
pub use item_mana::{ItemManaChange, ItemManaState};
pub use item_type::{EquipMask, ItemType};
pub use object::{
    ClientArmorProfile, ClientObject, ClientObjectHeader, ClientWeaponProfile, ObjectIdentity,
    PropertyBundle, WeenieData, normalize_damage, pk_status,
};
pub use placement::PlacementRejection;
pub use public_weenie_flags::PublicWeenieFlags;
pub use shortcuts::{ShortcutDropSource, ShortcutEntry, ShortcutMutation, ShortcutStore};
pub use small_rules::{
    AETHERIA_UNLOCK_PROPERTY, AetheriaUnlockState, SALVAGE_FAILED_TEXT, SalvageBag, ToolbarAmmo,
    aetheria_unlocks, cooldown_overlay_step, is_salvage_suitable, is_valid_salvage_material,
    salvage_material_name, salvage_success_text, salvage_unsuitable_text,
};
pub use stacks::{StackMergeItem, StackMergePlan, StackSplitQuantity};
pub use table::{
    AppraisalUpdate, ClientObjectPlacement, ClientObjectTable, ContainerContentEntry,
    EquipmentManifestEntry, MoveOrigin, MoveRequestFailure, ObjectMove, RemovalReason, TableEvent,
};
pub use tinker_values::TinkerValues;
pub use transaction::{
    InventoryTransactionState, PendingInventoryRequest, TransactionEvent, UseReservation,
};
pub use vendor::{
    VendorSellCandidate, VendorSellRejection, VendorShopItem, VendorShopProfile, VendorState,
    VendorTransition, VendorTransitionKind,
};
pub use vendor_staging::{
    MAX_STAGED_QUANTITY, TOO_MUCH_MESSAGE, VendorStagingAddOutcome, VendorStagingEntry,
    VendorStagingList,
};
pub use wire_enums::{AmmoType, CombatUse, ItemUseable};
