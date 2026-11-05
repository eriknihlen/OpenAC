//! The item side of the world: every described object's weenie data,
//! properties and containment, kept in `ac_core`'s object table, and the
//! server messages that change it.
//!
//! The table raises no callbacks; each mutation appends what happened to a
//! list. The world turns that list into [`WorldChange::Item`] entries for
//! presentation and keeps the raw events for the inventory owner, whose
//! request in flight follows them.

use ac_core::items::{
    AppraisalUpdate, ClientArmorProfile, ClientObjectHeader, ClientObjectPlacement,
    ClientWeaponProfile, ContainerContentEntry, EquipMask, EquipmentManifestEntry,
    HouseRestrictionRecord, ItemType, ItemUseable, MoveOrigin, PropertyBundle, PublicWeenieFlags,
    RemovalReason, TableEvent, WeenieData,
};
use ac_proto::appraise::IdentifyObjectResponse;
use ac_proto::inventory::{
    InventoryServerSaveFailed, PutObjInContainer, ViewContents, WieldObject,
};
use ac_proto::{CreateObject, HouseRestrictions, PlayerDescription, PropertyTables};

use super::{World, WorldChange};

/// A change to the item table, by guid. Read the current values from the
/// world when handling one.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ItemChange {
    /// A record was installed.
    Added(u32),
    /// A record's description or properties changed.
    Updated(u32),
    /// An object's placement (container, slot, wielder, worn location)
    /// changed.
    Moved {
        /// The object.
        guid: u32,
        /// Where it was.
        previous: ClientObjectPlacement,
        /// Where it is.
        current: ClientObjectPlacement,
        /// Who moved it.
        origin: MoveOrigin,
    },
    /// A record left the table.
    Removed {
        /// The object.
        guid: u32,
        /// Why.
        reason: RemovalReason,
    },
    /// A container's contents list changed as a whole.
    ContentsChanged(u32),
    /// Every record was dropped.
    Cleared,
}

impl ItemChange {
    /// The change a table event makes visible; `None` for events that are
    /// notices about a request rather than state (a confirmed wield, a
    /// refused move, a stack-size answer).
    #[must_use]
    pub fn of(event: &TableEvent) -> Option<Self> {
        Some(match event {
            TableEvent::Added { object_id, .. } => Self::Added(*object_id),
            TableEvent::Updated { object_id, .. } => Self::Updated(*object_id),
            TableEvent::Moved(m) => Self::Moved {
                guid: m.item_id,
                previous: m.previous,
                current: m.current,
                origin: m.origin,
            },
            TableEvent::Removed { object, reason, .. } => Self::Removed {
                guid: object.object_id(),
                reason: *reason,
            },
            TableEvent::ContainerContentsReplaced(container) => Self::ContentsChanged(*container),
            TableEvent::Cleared => Self::Cleared,
            TableEvent::MoveRolledBack { .. }
            | TableEvent::WieldConfirmed(_)
            | TableEvent::MoveRequestFailed(_)
            | TableEvent::StackSizeUpdated { .. }
            | TableEvent::ContentsViewEnded { .. } => return None,
        })
    }

    /// The object the change is about, if it names one.
    #[must_use]
    pub fn guid(self) -> Option<u32> {
        match self {
            Self::Added(guid) | Self::Updated(guid) => Some(guid),
            Self::Moved { guid, .. } | Self::Removed { guid, .. } => Some(guid),
            Self::ContentsChanged(_) | Self::Cleared => None,
        }
    }
}

/// An object description as the item table takes it: every field the
/// description carried, nothing it did not.
#[must_use]
pub fn weenie_data(create: &CreateObject) -> WeenieData {
    let p = &create.physics;
    let w = create.weenie.as_ref();
    let header = ClientObjectHeader {
        weenie_header_flags: w.map(|w| w.header_flags),
        weenie_header_flags2: w.and_then(|w| w.header_flags2),
        physics_description_flags: Some(p.flags),
        physics_state: Some(p.state),
        object_description_flags: w.map(|w| w.object_description_flags),
        setup_id: p.setup_id,
        scale: p.scale,
        hook_type: w.and_then(|w| w.hook_type).map(u32::from),
        parent_id: p.parent.as_ref().map(|a| a.guid),
        parent_location: p.parent.as_ref().map(|a| a.location_id),
        use_radius: w.and_then(|w| w.use_radius),
    };
    let Some(w) = w else {
        return WeenieData {
            guid: create.guid,
            header: Some(header),
            ..WeenieData::default()
        };
    };
    WeenieData {
        header: Some(header),
        guid: create.guid,
        name: Some(w.name.clone()),
        item_type: Some(ItemType(w.item_type)),
        weenie_class_id: w.weenie_class_id,
        icon_id: w.icon_id,
        icon_overlay_id: w.icon_overlay_id.unwrap_or(0),
        icon_underlay_id: w.icon_underlay_id.unwrap_or(0),
        effects: w.ui_effects.unwrap_or(0),
        value: w.value,
        stack_size: w.stack_size.map(i32::from),
        stack_size_max: w.max_stack_size.map(i32::from),
        burden: w.burden.map(i32::from),
        container_id: w.container_id,
        wielder_id: w.wielder_id,
        valid_locations: w.valid_locations.map(EquipMask),
        current_wielded_location: w.currently_wielded_location.map(EquipMask),
        priority: w.priority,
        items_capacity: w.items_capacity.map(i32::from),
        containers_capacity: w.containers_capacity.map(i32::from),
        structure: w.structure.map(i32::from),
        max_structure: w.max_structure.map(i32::from),
        workmanship: w.workmanship,
        useability: w.useability.map(ItemUseable),
        target_type: w.target_type.map(ItemType),
        radar_blip_color: w.radar_blip_color,
        radar_behavior: w.radar_behavior,
        public_weenie_bitfield: Some(PublicWeenieFlags(w.object_description_flags)),
        combat_use: w.combat_use,
        plural_name: w.plural_name.clone(),
        pet_owner_id: w.pet_owner_id,
        ammo_type: w.ammo_type,
        spell_id: w.spell_id.map(u32::from),
        cooldown_id: w.cooldown_id,
        cooldown_duration: w.cooldown_duration,
        hook_item_types: w.hook_item_types,
        hook_type: w.hook_type.map(u32::from),
        material_type: w.material_type,
        house_owner_id: w.house_owner_id,
        monarch_id: w.monarch_id,
        restrictions: w.house_restrictions.as_ref().map(restriction_record),
    }
}

/// A house's access list as the item table keeps it: open when any access
/// flag is set, the allegiance's monarch, guest id to permission (a guest
/// listed twice keeps its last permission).
#[must_use]
pub fn restriction_record(wire: &HouseRestrictions) -> HouseRestrictionRecord {
    HouseRestrictionRecord {
        open_to_public: wire.flags != 0,
        allegiance_monarch_id: wire.monarch_id,
        guests: wire
            .guests
            .iter()
            .map(|g| (g.guest_id, g.permission))
            .collect(),
    }
}

/// A full property listing as the item table keeps it.
#[must_use]
pub fn property_bundle(tables: &PropertyTables) -> PropertyBundle {
    PropertyBundle {
        ints: tables.ints.clone(),
        int64s: tables.int64s.clone(),
        bools: tables.bools.clone(),
        floats: tables.floats.clone(),
        strings: tables.strings.clone(),
        data_ids: tables.data_ids.clone(),
        instance_ids: tables.instance_ids.clone(),
    }
}

/// One successful appraisal as the item table applies it, received at
/// `received_at_seconds` of session time.
#[must_use]
pub fn appraisal_update(
    response: &IdentifyObjectResponse,
    received_at_seconds: f64,
) -> AppraisalUpdate {
    AppraisalUpdate {
        properties: property_bundle(&response.properties),
        spell_ids: response.spell_book.clone(),
        received_at_seconds,
        weapon_profile: response.weapon_profile.map(|w| ClientWeaponProfile {
            damage_type: w.damage_type,
            weapon_time: w.weapon_time,
            weapon_skill: w.weapon_skill,
            damage: w.damage,
            damage_variance: w.damage_variance,
            damage_mod: w.damage_mod,
            weapon_length: w.weapon_length,
            max_velocity: w.max_velocity,
            weapon_offense: w.weapon_offense,
            max_velocity_estimated: w.max_velocity_estimated,
        }),
        armor_profile: response.armor_profile.map(|a| ClientArmorProfile {
            slashing_protection: a.slashing,
            piercing_protection: a.piercing,
            bludgeoning_protection: a.bludgeoning,
            cold_protection: a.cold,
            fire_protection: a.fire,
            acid_protection: a.acid,
            nether_protection: a.nether,
            lightning_protection: a.lightning,
        }),
    }
}

impl World {
    /// Run one table mutation and publish what it did.
    pub(super) fn with_items<R>(
        &mut self,
        mutate: impl FnOnce(&mut ac_core::items::ClientObjectTable, &mut Vec<TableEvent>) -> R,
    ) -> R {
        let mut events = Vec::new();
        let result = mutate(&mut self.items, &mut events);
        for event in events {
            if let Some(change) = ItemChange::of(&event) {
                self.changes.push(WorldChange::Item(change));
            }
            if let Some(note) = super::liveness::QueueNote::of(&event) {
                self.queue_notes.push(note);
            }
            self.table_events.push(event);
        }
        result
    }

    /// A table mutation from a test elsewhere in the crate (one the session
    /// itself never makes, such as a local projection of a move), published
    /// as the session's own are.
    #[cfg(test)]
    pub(crate) fn mutate_items_for_test<R>(
        &mut self,
        mutate: impl FnOnce(&mut ac_core::items::ClientObjectTable, &mut Vec<TableEvent>) -> R,
    ) -> R {
        self.with_items(mutate)
    }

    /// A description of a new object, or a re-sent one of the same
    /// incarnation: merged into the record (created when absent).
    pub(super) fn ingest_description(&mut self, create: &CreateObject) {
        let data = weenie_data(create);
        self.with_items(|table, events| {
            table.ingest(&data, events);
        });
    }

    /// A re-sent description of a held object, which the game deletes and
    /// creates again: merged into the record, and the object placed first
    /// in the container the description names.
    pub(super) fn ingest_recreated_description(&mut self, create: &CreateObject) {
        let data = weenie_data(create);
        self.with_items(|table, events| {
            table.ingest_recreated(&data, events);
        });
    }

    /// A newer incarnation: the old record goes and the new description is
    /// installed fresh, dropping every quality the old one had.
    pub(super) fn replace_description(&mut self, create: &CreateObject, generation: u16) {
        let data = weenie_data(create);
        self.with_items(|table, events| {
            table.replace_generation(&data, generation, true, events);
        });
    }

    /// The server wielded an item on the player.
    pub(super) fn apply_wield(&mut self, wield: &WieldObject) {
        let wielder = self.player_guid.unwrap_or(0);
        self.with_items(|table, events| {
            table.apply_confirmed_server_wield(
                wield.item_guid,
                wielder,
                EquipMask(wield.equip_location),
                events,
            );
        });
    }

    /// The server put an item into a container.
    pub(super) fn apply_put_in_container(&mut self, put: &PutObjInContainer) {
        let placement = ClientObjectPlacement::in_container(
            put.container_guid,
            i32::try_from(put.placement).unwrap_or(i32::MAX),
        );
        self.with_items(|table, events| {
            table.apply_confirmed_server_move(
                put.item_guid,
                placement,
                Some(put.container_type),
                events,
            );
        });
    }

    /// The server put an item out into the world.
    pub(super) fn apply_put_in_3d(&mut self, item_guid: u32) {
        self.with_items(|table, events| {
            table.apply_confirmed_server_move(
                item_guid,
                ClientObjectPlacement::default(),
                None,
                events,
            );
        });
    }

    /// The server refused an inventory move.
    pub(super) fn apply_save_failed(&mut self, failed: &InventoryServerSaveFailed) {
        self.with_items(|table, events| {
            table.reject_move(failed.item_guid, failed.weenie_error, events);
        });
    }

    /// A container's contents as the server listed them.
    pub(super) fn apply_view_contents(&mut self, view: &ViewContents) {
        let entries: Vec<ContainerContentEntry> = view
            .items
            .iter()
            .map(|e| ContainerContentEntry {
                guid: e.guid,
                container_type: e.container_type,
            })
            .collect();
        self.with_items(|table, events| {
            table.replace_contents(view.container_guid, &entries, events);
        });
    }

    /// The server closed a ground container: its contents list and every
    /// list inside it are dropped.
    pub(super) fn apply_close_ground_container(&mut self, container_guid: u32) {
        self.with_items(|table, events| {
            table.stop_viewing_contents_tree(container_guid, events);
        });
    }

    /// The client stopped looking into a container (it closed it or asked
    /// for another): its contents list and every list inside it are
    /// dropped; the objects stay.
    pub(crate) fn stop_viewing_contents_tree(&mut self, container_guid: u32) {
        self.apply_close_ground_container(container_guid);
    }

    /// A house's new entry restrictions.
    pub(super) fn apply_house_restrictions(&mut self, house: u32, record: HouseRestrictionRecord) {
        self.with_items(|table, events| {
            table.update_house_restrictions(house, record, events);
        });
    }

    /// An appraisal answer, received at `now_seconds` of session time: a
    /// successful one about an object the table holds is kept; an
    /// unsuccessful one is recorded on the object (nothing else changes).
    pub fn apply_appraisal(&mut self, response: &IdentifyObjectResponse, now_seconds: f64) {
        if !response.success {
            self.with_items(|table, events| {
                table.record_unsuccessful_appraisal(response.guid, now_seconds, events)
            });
            return;
        }
        if self.items.get(response.guid).is_none() {
            return;
        }
        let update = appraisal_update(response, now_seconds);
        self.with_items(|table, events| {
            table.update_appraisal(response.guid, &update, events);
        });
    }

    /// The character description's item half: the player's properties, and
    /// its inventory and equipment lists.
    pub(super) fn apply_description_items(&mut self, description: &PlayerDescription) {
        let owner = self.player_guid.unwrap_or(0);
        let properties = property_bundle(&description.properties);
        let inventory: Vec<ContainerContentEntry> = description
            .inventory
            .iter()
            .map(|e| ContainerContentEntry {
                guid: e.guid,
                container_type: e.container_type,
            })
            .collect();
        let equipment: Vec<EquipmentManifestEntry> = description
            .equipped
            .iter()
            .map(|e| EquipmentManifestEntry {
                guid: e.guid,
                equip_location: EquipMask(e.equip_location),
                priority: e.priority,
            })
            .collect();
        self.with_items(|table, events| {
            if owner != 0 {
                table.upsert_properties(owner, &properties, events);
                table.initialize_inventory_manifest(owner, &inventory, events);
                table.initialize_equipment_manifest(owner, &equipment, events);
            } else {
                for entry in &inventory {
                    table.record_membership(
                        entry.guid,
                        0,
                        EquipMask::NONE,
                        Some(entry.container_type),
                        None,
                        events,
                    );
                }
                for entry in &equipment {
                    table.record_membership(
                        entry.guid,
                        0,
                        entry.equip_location,
                        None,
                        Some(entry.priority),
                        events,
                    );
                }
            }
        });
    }

    /// Mark an item as staged in (1) or out of (0) a secure trade; nothing
    /// happens when the mark is already so.
    pub fn set_trade_state(&mut self, guid: u32, state: i32) {
        self.with_items(|table, events| {
            if let Some(item) = table.get_mut(guid)
                && item.trade_state != state
            {
                item.trade_state = state;
                table.notify_object_updated(guid, events);
            }
        });
    }

    /// Mark an item as listed in (non-zero) or out of (0) a vendor's sell
    /// list; nothing happens when the mark is already so.
    pub fn set_sell_state(&mut self, guid: u32, state: i32) {
        self.with_items(|table, events| {
            if let Some(item) = table.get_mut(guid)
                && item.sell_state != state
            {
                item.sell_state = state;
                table.notify_object_updated(guid, events);
            }
        });
    }

    /// Put an item record the client made itself (a vendor's listing) in
    /// the table, or refresh it in place.
    pub fn ingest_item(&mut self, data: &WeenieData) {
        self.with_items(|table, events| {
            table.ingest(data, events);
        });
    }

    /// Take an item record out of the table; false when it was not there.
    pub fn remove_item(&mut self, guid: u32) -> bool {
        self.with_items(|table, events| table.remove(guid, events))
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Net.Tests/ObjectTableWiringTests.cs`
    //! (`ToWeenieData_CopiesFieldsFromSpawn`).

    use super::*;
    use ac_proto::{ModelData, PublicWeenieDesc};

    #[test]
    fn weenie_data_copies_every_field_of_the_description() {
        // Every field gets a distinct value, so a transposition trips at
        // least one assertion.
        let weenie = PublicWeenieDesc {
            name: "Iron Sword".into(),
            item_type: ItemType::MELEE_WEAPON.bits(),
            weenie_class_id: 0x0000_1001,
            icon_id: 0x0600_1111,
            icon_overlay_id: Some(0x0600_2222),
            icon_underlay_id: Some(0x0600_3333),
            ui_effects: Some(0x4),
            value: Some(7),
            stack_size: Some(1),
            max_stack_size: Some(1),
            burden: Some(300),
            container_id: Some(0xC9),
            wielder_id: Some(0xDA),
            valid_locations: Some(0x12),
            currently_wielded_location: Some(0x2),
            priority: Some(0x5),
            items_capacity: Some(0),
            containers_capacity: Some(0),
            hook_item_types: Some(ItemType::CONTAINER.bits()),
            hook_type: Some(4),
            structure: Some(80),
            max_structure: Some(100),
            workmanship: Some(4.5),
            useability: Some(0x000A_0008),
            target_type: Some(ItemType::CREATURE.bits()),
            radar_blip_color: Some(4),
            radar_behavior: Some(3),
            object_description_flags: 0x2080_0200,
            combat_use: Some(2),
            ammo_type: Some(1),
            plural_name: Some("Iron Swords".into()),
            pet_owner_id: Some(0x5000_0001),
            cooldown_id: Some(42),
            cooldown_duration: Some(30.0),
            material_type: Some(77),
            header_flags: 0xD0C_0FFE,
            header_flags2: Some(0x1),
            use_radius: Some(2.5),
            ..PublicWeenieDesc::default()
        };
        let create = CreateObject {
            guid: 0x600,
            model: ModelData::default(),
            physics: super::super::tests::physics(ac_proto::PhysicsTimestamps::default(), None),
            weenie: Some(weenie),
        };
        let d = weenie_data(&create);
        assert_eq!(d.guid, 0x600);
        assert_eq!(d.name.as_deref(), Some("Iron Sword"));
        assert_eq!(d.item_type, Some(ItemType::MELEE_WEAPON));
        assert_eq!(d.weenie_class_id, 0x1001);
        assert_eq!(d.icon_id, 0x0600_1111);
        assert_eq!(d.icon_overlay_id, 0x0600_2222);
        assert_eq!(d.icon_underlay_id, 0x0600_3333);
        assert_eq!(d.effects, 0x4);
        assert_eq!(d.value, Some(7));
        assert_eq!(d.stack_size, Some(1));
        assert_eq!(d.stack_size_max, Some(1));
        assert_eq!(d.burden, Some(300));
        assert_eq!(d.container_id, Some(0xC9));
        assert_eq!(d.wielder_id, Some(0xDA));
        assert_eq!(d.valid_locations, Some(EquipMask(0x12)));
        assert_eq!(d.current_wielded_location, Some(EquipMask(0x2)));
        assert_eq!(d.priority, Some(5));
        assert_eq!(d.items_capacity, Some(0));
        assert_eq!(d.containers_capacity, Some(0));
        assert_eq!(d.hook_item_types, Some(ItemType::CONTAINER.bits()));
        assert_eq!(d.hook_type, Some(4));
        assert_eq!(d.structure, Some(80));
        assert_eq!(d.max_structure, Some(100));
        assert_eq!(d.workmanship, Some(4.5));
        assert_eq!(d.useability, Some(ItemUseable(0x000A_0008)));
        assert_eq!(d.target_type, Some(ItemType::CREATURE));
        assert_eq!(d.radar_blip_color, Some(4));
        assert_eq!(d.radar_behavior, Some(3));
        assert_eq!(
            d.public_weenie_bitfield,
            Some(PublicWeenieFlags(0x2080_0200))
        );
        assert_eq!(d.combat_use, Some(2));
        assert_eq!(d.ammo_type, Some(1));
        assert_eq!(d.plural_name.as_deref(), Some("Iron Swords"));
        assert_eq!(d.pet_owner_id, Some(0x5000_0001));
        assert_eq!(d.cooldown_id, Some(42));
        assert_eq!(d.cooldown_duration, Some(30.0));
        assert_eq!(d.material_type, Some(77));
        // OpenAC `ObjectTableWiring.ToWeenieData`'s header: the layout words
        // and the optional values, each as sent.
        let header = d.header.unwrap();
        assert_eq!(header.weenie_header_flags, Some(0xD0C_0FFE));
        assert_eq!(header.weenie_header_flags2, Some(0x1));
        assert_eq!(header.object_description_flags, Some(0x2080_0200));
        assert_eq!(header.physics_description_flags, Some(create.physics.flags));
        assert_eq!(header.physics_state, Some(create.physics.state));
        assert_eq!(header.hook_type, Some(4));
        assert_eq!(header.use_radius, Some(2.5));
        assert_eq!(
            (header.setup_id, header.parent_id),
            (create.physics.setup_id, None)
        );
    }

    #[test]
    fn a_house_access_list_becomes_a_restriction_record() {
        let wire = HouseRestrictions {
            version: 1,
            flags: 1,
            monarch_id: 0x5000_0009,
            guests: vec![
                ac_proto::HouseGuest {
                    guest_id: 0x5000_0002,
                    permission: 0,
                },
                ac_proto::HouseGuest {
                    guest_id: 0x5000_0002,
                    permission: 1,
                },
            ],
        };
        let record = restriction_record(&wire);
        assert!(record.open_to_public);
        assert_eq!(record.allegiance_monarch_id, 0x5000_0009);
        assert_eq!(record.guests.get(&0x5000_0002), Some(&1));
    }
}
