//! Typed [`Host`] methods for API group G: items, equipment, loot and the
//! loot classifiers plugins share.

use ac_plugin_abi::calls;
use ac_plugin_abi::items::{
    ClassifierInfo, ClassifierRegistration, ClassifyRequest, EquipRequest, EquipmentItem,
    EquipmentPlacement, EquipmentResult, InventoryItem, ItemAmount, ItemResult, ItemState,
    LootClassification, LootContainer, LootContext, LootState, LootedItem, LootedNotice,
    MergeRequest, MoveRequest, PickupRequest, RemovedNotice, SalvageRequest,
};
use ac_plugin_abi::world::ItemProperties;

use crate::Host;

/// G. Items, equipment, loot.
impl Host<'_> {
    /// Everything the character owns, by name then id: worn, wielded, in
    /// the main pack and inside every side pack. Each item's
    /// `container_slot` is its place in its own container (the main pack
    /// or a side pack) among its kind, loose items and packs counted
    /// apart; -1 for one in no container, such as a worn or wielded item,
    /// or one its container's list does not hold; 0 when that container
    /// has no list at all.
    ///
    /// The server lists a pack's contents before it describes them, so
    /// shortly after login an item may be listed but not yet described: it
    /// is here, in the pack that lists it at its place, but with an empty
    /// name, class `Unknown`, zeroed numbers and `header: None`. Its
    /// description fills in the same id later; wait for `header` (or a
    /// name) before judging an item by what it is. Until then it cannot be
    /// sold, moved or used: the vendor's sell list and moves refuse it, and
    /// a use sends nothing.
    pub fn owned_items(&mut self) -> Vec<InventoryItem> {
        self.call::<calls::ItemsOwned>(&())
    }

    /// Where the item commands stand (busy, completions).
    pub fn item_state(&mut self) -> ItemState {
        self.call::<calls::ItemsState>(&())
    }

    /// True when the server has listed the character's inventory and every
    /// nested pack and no inventory move or request is pending. A plugin
    /// that remembers item ids must not treat an item missing from
    /// [`Host::owned_items`] as sold or dropped while this is false. Once it
    /// is true every owned item is in [`Host::owned_items`], so one missing
    /// from it is gone. It says every item is listed, not that each is
    /// described (some may still have no name or header) or appraised.
    pub fn owned_inventory_complete(&mut self) -> bool {
        self.call::<calls::ItemsInventoryComplete>(&())
    }

    /// The properties of an owned item.
    pub fn item_properties(&mut self, object_id: u32) -> Option<ItemProperties> {
        self.call::<calls::ItemsProperties>(&object_id)
    }

    /// Use an owned item (or walk to and use a world object).
    pub fn use_item(&mut self, object_id: u32) -> ItemResult {
        self.call::<calls::ItemsUse>(&object_id)
    }

    /// Use an owned item on a target.
    pub fn apply_item(&mut self, object_id: u32, target_id: u32) -> ItemResult {
        self.call::<calls::ItemsApply>(&(object_id, target_id))
    }

    /// Put an owned item (or `amount` of a stack, 0 for all) into an owned
    /// container at a slot.
    pub fn move_item(
        &mut self,
        object_id: u32,
        container_id: u32,
        amount: u32,
        placement: i32,
    ) -> ItemResult {
        self.call::<calls::ItemsMove>(&MoveRequest {
            object_id,
            container_id,
            amount,
            placement,
        })
    }

    /// The same move, able to join a stack the way dropping a stack onto a
    /// pack does: with `join_stack` the client first looks for a stack of
    /// the same thing in the container (its own items first, then those in
    /// each pack inside it) with room for everything being moved, and joins
    /// the first one found with a merge, no slot needed; a stack that could
    /// take only part of it is passed over. With none, or without
    /// `join_stack`, this is [`Host::move_item`].
    pub fn move_item_joining_stack(
        &mut self,
        object_id: u32,
        container_id: u32,
        amount: u32,
        placement: i32,
        join_stack: bool,
    ) -> ItemResult {
        self.call::<calls::ItemsMoveJoinStack>(&(
            MoveRequest {
                object_id,
                container_id,
                amount,
                placement,
            },
            join_stack,
        ))
    }

    /// Merge `amount` (0: all that fit) of one owned stack into another.
    pub fn merge_items(&mut self, source_id: u32, target_id: u32, amount: u32) -> ItemResult {
        self.call::<calls::ItemsMerge>(&MergeRequest {
            source_id,
            target_id,
            amount,
        })
    }

    /// Drop an owned item.
    pub fn drop_item(&mut self, object_id: u32, amount: u32) -> ItemResult {
        self.call::<calls::ItemsDrop>(&ItemAmount {
            object_id,
            target_id: 0,
            amount,
        })
    }

    /// Give an owned item to someone.
    pub fn give_item(&mut self, object_id: u32, target_id: u32, amount: u32) -> ItemResult {
        self.call::<calls::ItemsGive>(&ItemAmount {
            object_id,
            target_id,
            amount,
        })
    }

    /// Salvage owned items with a tinkering tool.
    pub fn salvage(&mut self, tool_id: u32, item_ids: Vec<u32>) -> ItemResult {
        self.call::<calls::ItemsSalvage>(&SalvageRequest { tool_id, item_ids })
    }

    /// Sell an owned item to the open vendor.
    pub fn sell_item(&mut self, object_id: u32, amount: u32) -> ItemResult {
        self.call::<calls::ItemsSell>(&ItemAmount {
            object_id,
            target_id: 0,
            amount,
        })
    }

    /// Let go of an item the client lists but the server no longer has, as
    /// a server delete would take it; nothing is sent. Only an item the
    /// server refused to appraise within the last 30 seconds (and said
    /// nothing else about since) that is carried, not worn, and not a pack
    /// holding anything: `Completed` once it is gone, `Refused` with a
    /// notice for the guards, `InvalidItem` for anything not carried or
    /// with no server record, `Busy` while an appraisal of it is awaited or
    /// an item request is in flight. The appraisal slot's
    /// `current_object_unsuccessful` ([`Host::loot_state`]) says whether
    /// the answer to an identify was a refusal.
    pub fn forget_stale_item(&mut self, object_id: u32) -> ItemResult {
        self.call::<calls::ItemsForgetStale>(&object_id)
    }

    /// True while an equip the client started is still in flight (an item
    /// being moved aside for it, or the wield itself). Only the equip
    /// itself counts: whether an item request is pending is
    /// [`ItemState::is_busy`] in [`Host::item_state`].
    pub fn equipment_busy(&mut self) -> bool {
        self.call::<calls::EquipmentBusy>(&())
    }

    /// Everything owned that can be worn or wielded.
    pub fn owned_equipment(&mut self) -> Vec<EquipmentItem> {
        self.call::<calls::EquipmentOwned>(&())
    }

    /// Every known item's worn location.
    pub fn equipment_placements(&mut self) -> Vec<EquipmentPlacement> {
        self.call::<calls::EquipmentPlacements>(&())
    }

    /// Wear or wield an owned item (slot bits, 0 to choose).
    pub fn equip(&mut self, object_id: u32, location: u32) -> EquipmentResult {
        self.call::<calls::EquipmentEquip>(&EquipRequest {
            object_id,
            location,
        })
    }

    /// Wield an item in the off hand.
    pub fn equip_secondary(&mut self, object_id: u32) -> EquipmentResult {
        self.call::<calls::EquipmentEquipSecondary>(&object_id)
    }

    /// Where looting stands.
    pub fn loot_state(&mut self) -> LootState {
        self.call::<calls::LootState>(&())
    }

    /// The corpses within `maximum_metres`, nearest first.
    pub fn corpses(&mut self, maximum_metres: f32) -> Vec<LootContainer> {
        self.call::<calls::LootCorpses>(&maximum_metres)
    }

    /// What the open container holds, packs inside included. Each item's
    /// `container_slot` is its place in the container that holds it (the
    /// open one or a pack inside it) among its kind, loose items and packs
    /// counted apart.
    pub fn loot_contents(&mut self) -> Vec<InventoryItem> {
        self.call::<calls::LootContents>(&())
    }

    /// The properties of an item in the open container.
    pub fn loot_properties(&mut self, object_id: u32) -> Option<ItemProperties> {
        self.call::<calls::LootProperties>(&object_id)
    }

    /// Open a corpse or chest.
    pub fn open_loot(&mut self, container_id: u32) -> ItemResult {
        self.call::<calls::LootOpen>(&container_id)
    }

    /// Close the open container.
    pub fn close_loot(&mut self, container_id: u32) -> ItemResult {
        self.call::<calls::LootClose>(&container_id)
    }

    /// Ask for the appraisal of a corpse, loot or an owned item.
    pub fn identify_loot(&mut self, object_id: u32) -> ItemResult {
        self.call::<calls::LootIdentify>(&object_id)
    }

    /// Pick an item out of the open container.
    pub fn pick_up(&mut self, object_id: u32, main_pack: bool) -> ItemResult {
        self.call::<calls::LootPickup>(&PickupRequest {
            object_id,
            main_pack,
        })
    }

    /// Put an item into the open container at `placement` (API 1.5), as a
    /// drop on that container's window does, with that window's checks:
    /// the whole stack (`amount` 0, or the stack's size) or that many split
    /// off. `container_id` must be the open container
    /// ([`LootState::current_container_id`]). The item is one the
    /// character owns, or one of the open container's own loose items (a
    /// reorder): an item directly in it that is not a pack. A pack inside
    /// the container, or anything inside such a pack, is not one.
    ///
    /// `placement` is the slot sent to the server: an index among the open
    /// container's loose items, in their order there (in
    /// [`Host::loot_contents`], the items whose `container_id` is the open
    /// container and that are not packs; packs are not counted). The item
    /// lands at that index, and the ones from there on move one slot
    /// later; past the end appends. A reorder counts as if the item were
    /// taken out first: to move the item at index 1 so it sits at index 3
    /// of the list it ends in, send 3, and the items that were at 2 and 3
    /// move one slot earlier. A split leaves its stack where it is and
    /// counts it.
    ///
    /// `Completed`, with nothing sent, when the whole of a loose item is
    /// put where it already is (its own index, or past the end for the
    /// last one). `InvalidTarget` when the container is not the open one,
    /// `InvalidItem` for any other item. `Refused` with the client's own
    /// notice for a pack, a container with no free slot (a reorder of its
    /// own items still fits), an item in a trade, or an amount over the
    /// stack, and for a put that did not go out. `Busy` while another item
    /// request, an item waiting at a slot or a wield is in flight.
    pub fn put_in_open_container(
        &mut self,
        object_id: u32,
        container_id: u32,
        amount: u32,
        placement: i32,
    ) -> ItemResult {
        self.call::<calls::LootPut>(&MoveRequest {
            object_id,
            container_id,
            amount,
            placement,
        })
    }

    /// Register a loot classifier other plugins can ask; it is answered
    /// through [`crate::Plugin::callback`] with the returned handle. Its
    /// scoped id is `<plugin id>/<id>`.
    ///
    /// # Errors
    /// A blank id or name, or one already registered.
    pub fn register_loot_classifier(
        &mut self,
        id: &str,
        display_name: &str,
    ) -> Result<u32, String> {
        self.call::<calls::LootClassifierRegister>(&ClassifierRegistration {
            id: id.to_owned(),
            display_name: display_name.to_owned(),
        })
    }

    /// Remove one of this plugin's classifiers.
    pub fn unregister_loot_classifier(&mut self, handle: u32) -> bool {
        self.call::<calls::LootClassifierUnregister>(&handle)
    }

    /// Every registered classifier.
    pub fn loot_classifiers(&mut self) -> Vec<ClassifierInfo> {
        self.call::<calls::LootClassifiers>(&())
    }

    /// Ask another plugin's classifier (with a stored profile when
    /// `profile` is not empty). A plugin's own classifiers answer `None`
    /// here: call your own code directly.
    pub fn classify(
        &mut self,
        classifier_id: &str,
        profile: &str,
        context: LootContext,
    ) -> Option<LootClassification> {
        self.call::<calls::LootClassify>(&ClassifyRequest {
            classifier_id: classifier_id.to_owned(),
            profile: profile.to_owned(),
            context,
        })
    }

    /// Ask another plugin's classifier whether it needs an appraisal first.
    pub fn classifier_needs_identification(
        &mut self,
        classifier_id: &str,
        context: LootContext,
    ) -> bool {
        self.call::<calls::LootNeedsIdentification>(&ClassifyRequest {
            classifier_id: classifier_id.to_owned(),
            profile: String::new(),
            context,
        })
    }

    /// Tell a classifier an item was looted.
    pub fn notify_looted(&mut self, classifier_id: &str, item: LootedItem) -> bool {
        self.call::<calls::LootNotifyLooted>(&LootedNotice {
            classifier_id: classifier_id.to_owned(),
            item,
        })
    }

    /// Tell a classifier an item went away.
    pub fn notify_item_removed(&mut self, classifier_id: &str, object_id: u32) -> bool {
        self.call::<calls::LootNotifyRemoved>(&RemovedNotice {
            classifier_id: classifier_id.to_owned(),
            object_id,
        })
    }
}
