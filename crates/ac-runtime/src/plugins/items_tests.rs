//! Items, equipment and loot against the inventory owner. Cases from
//! OpenAC `tests/AcDream.Runtime.Tests/Plugins/PluginItemBusyTruthfulnessTests.cs`
//! and `LootAutomationCloseTests.cs`, and the item command checks of
//! `RuntimeAutomationSurface` (`DispatchItem`, `MoveToContainer`, `Sell`).

use std::time::Duration;

use ac_plugin::abi::calls;
use ac_plugin::abi::items::{
    EquipRequest, EquipmentStatus, ItemAmount, ItemStatus, MergeRequest, MoveRequest,
    PickupRequest, SalvageRequest,
};

use super::tests::{CLOCKS, PLAYER, call, in_world, with_binding};
use super::{Link, PluginBinding, SessionFacts};
use crate::session::{Clocks, Outbox, SessionState};

const GEM: u32 = 0x8000_0001;
const KIT: u32 = 0x8000_0002;
const SWORD: u32 = 0x8000_0003;
const LOOSE: u32 = 0x8000_0009;
const CORPSE: u32 = 0x7000_0010;

/// An item in the player's main pack.
fn owned(
    guid: u32,
    name: &str,
    item_type: u32,
    configure: impl FnOnce(&mut ac_proto::PublicWeenieDesc),
) -> ac_proto::Inbound<'static> {
    use crate::world::tests::{create, stamps};
    let mut message = create(guid, stamps(1, 1), None);
    if let ac_proto::Inbound::CreateObject(create) = &mut message {
        let mut weenie = ac_proto::PublicWeenieDesc {
            name: name.into(),
            weenie_class_id: 100 + guid,
            item_type,
            container_id: Some(PLAYER),
            stack_size: Some(5),
            ..ac_proto::PublicWeenieDesc::default()
        };
        configure(&mut weenie);
        create.weenie = Some(weenie);
    }
    message
}

/// A pack with a gem, a healing kit (used on a target) and a sword.
fn packed() -> SessionState {
    let mut state = in_world();
    state.world.apply(&owned(GEM, "Gem", 0x800, |_| {}));
    state.world.apply(&owned(KIT, "Healing Kit", 0x80, |w| {
        // A targeted use (source contained, target self or others).
        w.useability = Some(0x0006_0004 | 0x0008_0000);
    }));
    state.world.apply(&owned(SWORD, "Sword", 0x1, |w| {
        w.valid_locations = Some(0x0010_0000);
        w.stack_size = None;
    }));
    state.world.drain_changes().for_each(drop);
    state
}

/// [`with_binding`] at a later session time.
fn at<R>(
    state: &mut SessionState,
    outbox: &mut Outbox,
    later: Duration,
    body: impl FnOnce(&mut PluginBinding<'_>) -> R,
) -> R {
    let (events, _host) = std::sync::mpsc::channel();
    let clocks = Clocks {
        session: CLOCKS.session + later,
        ..CLOCKS
    };
    let mut binding = PluginBinding {
        state,
        events: &events,
        window_attached: false,
        character: None,
        wall_unix_ms: clocks.wall_unix_ms,
        link: Some(Link {
            outbox,
            active: true,
            clocks,
        }),
        facts: SessionFacts {
            in_world: true,
            ..SessionFacts::none()
        },
        deferred: Vec::new(),
    };
    body(&mut binding)
}

// OpenAC (origin/main) `RuntimeAutomationSurface`'s item projection: the
// icon underlay and overlay, the coverage mask (the object's priority
// bits), the plural name, the use radius and the description header, each
// from the item's description.
#[test]
fn an_owned_item_carries_its_icon_layers_coverage_plural_reach_and_header() {
    let mut state = in_world();
    state.world.apply(&owned(SWORD, "Shirt", 0x2, |w| {
        w.icon_underlay_id = Some(0x0600_1F00);
        w.icon_overlay_id = Some(0x0600_2F00);
        w.priority = Some(0x0000_0408);
        w.plural_name = Some("Shirts".into());
        w.use_radius = Some(1.5);
        w.header_flags = 0x0018_0000;
    }));
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let items = call::<calls::ItemsOwned>(b, &());
        let shirt = items.iter().find(|i| i.object_id == SWORD).unwrap();
        assert_eq!(
            (shirt.icon_underlay_id, shirt.icon_overlay_id),
            (0x0600_1F00, 0x0600_2F00)
        );
        assert_eq!(shirt.coverage_mask, 0x0408);
        assert_eq!(shirt.plural_name, "Shirts");
        assert!((shirt.use_radius - 1.5).abs() < f32::EPSILON);
        let header = shirt.header.as_ref().unwrap();
        assert_eq!(header.weenie_header_flags, Some(0x0018_0000));
        assert_eq!(header.use_radius, Some(1.5));
        assert_eq!(header.weenie_header_flags2, None);
    });
}

// OpenAC PR #270: a house hook's item-type bits reach both records (zero
// for anything that is not a hook). The field came with API 1.2, so a
// plugin built for API 1.1 is sent both records as they were then, and
// what it reads has no such field.
#[test]
fn a_hooks_item_types_reach_both_records_and_older_plugins_get_the_records_they_knew() {
    use ac_plugin::abi::items::InventoryItem;
    use ac_plugin::abi::world::WorldObject;
    use ac_plugin::abi::{API_CURRENT, ApiVersion, Import, Wire};
    use ac_plugin::api::{Caller, SessionApi};
    let mut state = in_world();
    state.world.apply(&owned(SWORD, "Hook", 0x2, |w| {
        w.hook_item_types = Some(0x0000_0080);
    }));
    state.world.apply(&owned(GEM, "Gem", 0x800, |_| {}));
    state.world.drain_changes().for_each(drop);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let items = call::<calls::ItemsOwned>(b, &());
        let hook = |id| items.iter().find(|i| i.object_id == id).unwrap();
        assert_eq!(hook(SWORD).hook_item_types, 0x80);
        assert_eq!(hook(GEM).hook_item_types, 0);
        let object = call::<calls::WorldObject>(b, &SWORD).unwrap();
        assert_eq!(object.hook_item_types, 0x80);

        let v1_1 = ApiVersion::new(1, 1);
        let mut ask = |import, version, request: &[u8]| {
            let caller = Caller {
                plugin_id: "test.plugin",
                api_version: version,
            };
            b.call(caller, import, request).unwrap()
        };
        let older = ask(Import::ItemsOwned, v1_1, &[]);
        assert_ne!(older, ask(Import::ItemsOwned, API_CURRENT, &[]));
        assert_eq!(older, items.to_bytes_at(v1_1));
        assert_eq!(ask(Import::ItemsOwned, API_CURRENT, &[]), items.to_bytes());
        // Each record is one `u32` shorter, and reads as API 1.1's.
        assert_eq!(older.len() + 2 * 4, items.to_bytes().len());
        assert!(Vec::<InventoryItem>::from_bytes(&older).is_err());
        let read = Vec::<InventoryItem>::from_bytes_at(&older, v1_1).unwrap();
        assert_eq!(read.len(), 2);
        assert!(read.iter().all(|i| i.hook_item_types == 0));
        let older = ask(Import::WorldObject, v1_1, &SWORD.to_bytes());
        let read = Option::<WorldObject>::from_bytes_at(&older, v1_1).unwrap();
        assert_eq!(
            read.map(|o| (o.object_id, o.hook_item_types)),
            Some((SWORD, 0))
        );
    });
}

// OpenAC `RuntimeAutomationSurface.MoveToContainer(..., joinStack)`: the
// plain move's checks, then the runtime's choice; with the flag a stack of
// the same thing in the container that takes it all is joined by a merge,
// without it the move is the plain put; a flagged move with nothing to join
// is the plain move too (`ItemInteractionControllerTests`).
#[test]
fn a_move_joining_a_stack_merges_into_the_stack_that_takes_it_all() {
    const COINS: u32 = 0x8000_0020;
    const MORE_COINS: u32 = 0x8000_0021;
    let coins = |size: u16| {
        move |w: &mut ac_proto::PublicWeenieDesc| {
            w.weenie_class_id = 273;
            w.stack_size = Some(size);
            w.max_stack_size = Some(25_000);
        }
    };
    let mut state = packed();
    state.world.apply(&owned(COINS, "Pyreal", 0x40, coins(5)));
    state
        .world
        .apply(&owned(MORE_COINS, "Pyreal", 0x40, coins(7)));
    state.world.drain_changes().for_each(drop);
    let request = |object_id, amount| MoveRequest {
        object_id,
        container_id: PLAYER,
        amount,
        placement: 0,
    };
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::ItemsMoveJoinStack>(
                b,
                &(
                    MoveRequest {
                        container_id: 0x9999,
                        ..request(COINS, 0)
                    },
                    true
                )
            )
            .status,
            ItemStatus::InvalidTarget
        );
        let join = call::<calls::ItemsMoveJoinStack>(b, &(request(COINS, 2), true));
        assert_eq!(join.status, ItemStatus::Started);
    });
    let sent: Vec<Vec<u8>> = outbox.drain().map(|o| o.body).collect();
    assert_eq!(
        sent,
        [ac_proto::inventory::build_stackable_merge(
            1, COINS, MORE_COINS, 2
        )]
    );

    // Nothing to join (the gem's kind is its own): the plain put.
    let mut state = packed();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let moved = call::<calls::ItemsMoveJoinStack>(b, &(request(GEM, 0), true));
        assert_eq!(moved.status, ItemStatus::Started);
    });
    let sent: Vec<Vec<u8>> = outbox.drain().map(|o| o.body).collect();
    assert_eq!(
        sent,
        [ac_proto::inventory::build_put_item_in_container(
            1, GEM, PLAYER, 0
        )]
    );
}

// OpenAC `DispatchItem`: an unknown item, a target that is not known, a
// targeted item used alone.
#[test]
fn item_commands_check_the_item_and_target_first() {
    let mut state = packed();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::ItemsUse>(b, &0x9999).status,
            ItemStatus::InvalidItem
        );
        assert_eq!(
            call::<calls::ItemsApply>(b, &(GEM, 0x9999)).status,
            ItemStatus::InvalidTarget
        );
        let alone = call::<calls::ItemsUse>(b, &KIT);
        assert_eq!(alone.status, ItemStatus::Refused);
        assert_eq!(
            alone.notice.as_deref(),
            Some("This item requires a target; call Apply(objectId, targetObjectId) instead.")
        );
        let too_many = call::<calls::ItemsDrop>(
            b,
            &ItemAmount {
                object_id: GEM,
                target_id: 0,
                amount: 6,
            },
        );
        assert_eq!(too_many.status, ItemStatus::Refused);
        assert_eq!(too_many.notice.as_deref(), Some("Invalid stack quantity."));
        assert_eq!(
            call::<calls::ItemsMove>(
                b,
                &MoveRequest {
                    object_id: GEM,
                    container_id: 0x9999,
                    amount: 0,
                    placement: 0,
                }
            )
            .status,
            ItemStatus::InvalidTarget
        );
        assert_eq!(
            call::<calls::ItemsMerge>(
                b,
                &MergeRequest {
                    source_id: GEM,
                    target_id: LOOSE,
                    amount: 0,
                }
            )
            .status,
            ItemStatus::InvalidTarget
        );
        assert_eq!(
            call::<calls::ItemsGive>(
                b,
                &ItemAmount {
                    object_id: GEM,
                    target_id: 0,
                    amount: 0,
                }
            )
            .status,
            ItemStatus::InvalidTarget
        );
        assert_eq!(
            call::<calls::ItemsSalvage>(
                b,
                &SalvageRequest {
                    tool_id: GEM,
                    item_ids: vec![SWORD],
                }
            )
            .status,
            ItemStatus::InvalidTarget
        );
        let sell = call::<calls::ItemsSell>(
            b,
            &ItemAmount {
                object_id: GEM,
                target_id: 0,
                amount: 0,
            },
        );
        assert_eq!(sell.status, ItemStatus::InvalidTarget);
        assert_eq!(sell.notice.as_deref(), Some("No vendor is open."));
        let owned: Vec<u32> = call::<calls::ItemsOwned>(b, &())
            .iter()
            .map(|i| i.object_id)
            .collect();
        // By name: Gem, Healing Kit, Sword.
        assert_eq!(owned, [GEM, KIT, SWORD]);
        assert!(call::<calls::ItemsProperties>(b, &GEM).is_some());
        assert!(call::<calls::ItemsProperties>(b, &0x9999).is_none());
    });
}

// OpenAC `PluginItemBusyTruthfulnessTests`: after a use the 200 ms pacing
// reads busy on both item surfaces and a use or open inside it is busy,
// not refused; once busy reads false, neither is refused as busy.
#[test]
fn the_pacing_between_two_uses_reads_as_busy_and_lapses() {
    let mut state = packed();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert!(!call::<calls::ItemsState>(b, &()).is_busy);
        assert_eq!(call::<calls::ItemsUse>(b, &GEM).status, ItemStatus::Started);
        let state = call::<calls::ItemsState>(b, &());
        assert!(state.is_busy);
        assert!(call::<calls::LootState>(b, &()).is_busy);
        assert_eq!(call::<calls::ItemsUse>(b, &GEM).status, ItemStatus::Busy);
        assert_eq!(
            call::<calls::ItemsApply>(b, &(KIT, PLAYER)).status,
            ItemStatus::Busy
        );
    });
    assert!(!outbox.is_empty());
}

// OpenAC `LootAutomationCloseTests`: closing needs the open container;
// opening needs a corpse or something that opens; a pickup needs an item
// in the open container.
#[test]
fn loot_commands_need_the_right_container() {
    let mut state = packed();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::LootOpen>(b, &GEM).status,
            ItemStatus::InvalidTarget
        );
        assert_eq!(
            call::<calls::LootClose>(b, &CORPSE).status,
            ItemStatus::InvalidTarget
        );
        assert_eq!(
            call::<calls::LootPickup>(
                b,
                &PickupRequest {
                    object_id: GEM,
                    main_pack: false,
                }
            )
            .status,
            ItemStatus::InvalidItem
        );
        assert_eq!(
            call::<calls::LootIdentify>(b, &0x9999).status,
            ItemStatus::InvalidItem
        );
        let loot = call::<calls::LootState>(b, &());
        assert_eq!(loot.current_container_id, 0);
        assert!(!loot.current_contents_ready);
        assert!(call::<calls::LootContents>(b, &()).is_empty());
        assert!(call::<calls::LootCorpses>(b, &30.0).is_empty());
    });
}

// OpenAC `ContainerInboundParityTests.AnUnsuccessfulAppraisalOfACorpseIsAnAnswerOnBothClients`:
// a corpse the server no longer has is answered with an empty,
// unsuccessful appraisal; it never gains a description, but the plugin is
// told the answer came.
#[test]
fn an_unsuccessful_appraisal_of_a_corpse_is_an_answer() {
    use crate::world::tests::{create, position, stamps};
    let mut state = in_world();
    state.world.apply(&create(
        PLAYER,
        stamps(1, 1),
        Some(position(0xA9B4_0031, 10.0)),
    ));
    let mut corpse = create(CORPSE, stamps(1, 1), Some(position(0xA9B4_0031, 12.0)));
    if let ac_proto::Inbound::CreateObject(create) = &mut corpse {
        create.weenie = Some(ac_proto::PublicWeenieDesc {
            name: "Corpse of Drudge".into(),
            object_description_flags: 0x2000,
            ..ac_proto::PublicWeenieDesc::default()
        });
    }
    state.world.apply(&corpse);
    let mut outbox = Outbox::new();
    let before = with_binding(&mut state, &mut outbox, |b| {
        call::<calls::LootCorpses>(b, &10.0)
    });
    assert_eq!(before.len(), 1);
    assert!(!before[0].is_appraisal_answered);
    assert!(!before[0].is_identified);
    state.world.apply_appraisal(
        &ac_proto::appraise::IdentifyObjectResponse {
            guid: CORPSE,
            success: false,
            ..Default::default()
        },
        1.0,
    );
    let after = with_binding(&mut state, &mut outbox, |b| {
        call::<calls::LootCorpses>(b, &10.0)
    });
    assert!(after[0].is_appraisal_answered);
    assert!(!after[0].is_identified);
}

// OpenAC `Equip`/`EquipSecondary`: an item that cannot be worn is invalid.
#[test]
fn equipment_lists_wearables_and_checks_the_item() {
    let mut state = packed();
    let mut outbox = Outbox::new();
    at(&mut state, &mut outbox, Duration::from_secs(5), |b| {
        let equipment = call::<calls::EquipmentOwned>(b, &());
        let ids: Vec<u32> = equipment.iter().map(|e| e.object_id).collect();
        assert_eq!(ids, [SWORD]);
        assert_eq!(
            call::<calls::EquipmentEquip>(
                b,
                &EquipRequest {
                    object_id: GEM,
                    location: 0,
                }
            )
            .status,
            EquipmentStatus::InvalidItem
        );
        assert_eq!(
            call::<calls::EquipmentEquipSecondary>(b, &0x9999).status,
            EquipmentStatus::InvalidItem
        );
        assert_eq!(
            call::<calls::EquipmentEquip>(
                b,
                &EquipRequest {
                    object_id: SWORD,
                    location: 0,
                }
            )
            .status,
            EquipmentStatus::Started
        );
    });
}

// OpenAC `EquipSecondary` (the dual-wield route): a one-handed sword, whose
// own slots are only the melee slot, goes to the off hand. The plain equip
// would refuse it there (the shield slot is not one of its slots).
#[test]
fn an_off_hand_equip_wields_a_melee_weapon_in_the_shield_slot() {
    const MELEE: u32 = 0x0000_0001;
    let mut state = packed();
    state.world.apply(&owned(SWORD, "Sword", 0x1, |w| {
        w.valid_locations = Some(MELEE);
        w.combat_use = Some(1);
        w.stack_size = None;
    }));
    state.world.drain_changes().for_each(drop);
    let mut outbox = Outbox::new();
    at(&mut state, &mut outbox, Duration::from_secs(5), |b| {
        assert_eq!(
            call::<calls::EquipmentEquipSecondary>(b, &SWORD).status,
            EquipmentStatus::Started
        );
    });
}

// OpenAC v0.1.25 `tests/AcDream.Runtime.Tests/Plugins/
// InventoryCompletenessTests.cs`
// `PluginHostSeesNestedInventoryReadinessAndDisconnect`: complete only once
// the nested pack is listed too, and never outside the world.
#[test]
fn a_plugin_sees_the_inventory_complete_only_with_every_nested_pack_listed() {
    use ac_proto::{GameEventType, Inbound};
    const PACK: u32 = 0x7000_1234;
    let mut state = in_world();
    let mut description = crate::world::tests::empty_description();
    description.inventory = vec![ac_proto::InventoryEntry {
        guid: PACK,
        container_type: 1,
    }];
    state.world.apply_player_description(&description);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert!(!call::<calls::ItemsInventoryComplete>(b, &()));
    });
    let view = crate::world::tests::game_event(GameEventType::VIEW_CONTENTS, &[PACK, 0]);
    state.world.apply(&Inbound::parse(&view).unwrap());
    with_binding(&mut state, &mut outbox, |b| {
        assert!(call::<calls::ItemsInventoryComplete>(b, &()));
        b.facts.in_world = false;
        assert!(!call::<calls::ItemsInventoryComplete>(b, &()));
    });
}

// OpenAC `RuntimeItemInteraction.IsAutoWieldBusy` (bound to
// `IEquipmentAutomation.IsBusy`) and `AutoWieldGenerationTests`
// (`tests/AcDream.Runtime.Tests/Gameplay/AutoWieldGenerationTests.cs`): the
// equipment surface is busy while a switch is in flight (the worn axe
// moved aside, then the sword wielded) and an equip then is busy; the
// server's wield ends it. Only the switch counts: a use in flight makes the
// item surface busy, never the equipment one.
#[test]
fn equipment_is_busy_only_while_a_wield_is_in_flight() {
    use ac_proto::GameEventType;

    use crate::world::tests::game_event;

    const AXE: u32 = 0x8000_0004;
    const MELEE: u32 = 0x0000_0001;
    let server = |state: &mut SessionState, event: GameEventType, payload: &[u32]| {
        let body = game_event(event, payload);
        let message = ac_proto::Inbound::parse(&body).expect("a game event");
        state.apply(&message, &mut Outbox::new(), true, CLOCKS);
    };
    let sword = EquipRequest {
        object_id: SWORD,
        location: 0,
    };
    let mut outbox = Outbox::new();
    let mut using = packed();
    with_binding(&mut using, &mut outbox, |b| {
        assert!(!call::<calls::EquipmentBusy>(b, &()));
        assert_eq!(call::<calls::ItemsUse>(b, &GEM).status, ItemStatus::Started);
        assert!(call::<calls::ItemsState>(b, &()).is_busy);
        assert!(!call::<calls::EquipmentBusy>(b, &()));
    });

    let mut state = packed();
    state.world.apply(&owned(SWORD, "Sword", 0x1, |w| {
        w.valid_locations = Some(MELEE);
        w.combat_use = Some(1);
        w.stack_size = None;
    }));
    state.world.apply(&owned(AXE, "Axe", 0x1, |w| {
        w.valid_locations = Some(MELEE);
        w.combat_use = Some(1);
        w.stack_size = None;
        w.container_id = None;
        w.wielder_id = Some(PLAYER);
        w.currently_wielded_location = Some(MELEE);
    }));
    state.world.drain_changes().for_each(drop);
    at(&mut state, &mut outbox, Duration::from_secs(5), |b| {
        assert_eq!(
            call::<calls::EquipmentEquip>(b, &sword).status,
            EquipmentStatus::Started
        );
        assert!(call::<calls::EquipmentBusy>(b, &()));
    });
    server(
        &mut state,
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[AXE, PLAYER, 0, 0],
    );
    at(&mut state, &mut outbox, Duration::from_secs(6), |b| {
        assert!(call::<calls::EquipmentBusy>(b, &()));
        assert_eq!(
            call::<calls::EquipmentEquip>(b, &sword).status,
            EquipmentStatus::Busy
        );
    });
    server(&mut state, GameEventType::WIELD_OBJECT, &[SWORD, MELEE]);
    at(&mut state, &mut outbox, Duration::from_secs(7), |b| {
        assert!(!call::<calls::EquipmentBusy>(b, &()));
    });
}

/// The server's answer to an appraisal of `guid`, `later` into the session:
/// no properties, and whether it could appraise it.
fn appraisal_answer(state: &mut SessionState, guid: u32, success: bool, later: Duration) {
    use ac_proto::GameEventType;
    let body = crate::world::tests::game_event(
        GameEventType::IDENTIFY_OBJECT_RESPONSE,
        &[guid, 0, u32::from(success)],
    );
    let message = ac_proto::Inbound::parse(&body).expect("a game event");
    let clocks = Clocks {
        session: CLOCKS.session + later,
        ..CLOCKS
    };
    state.apply(&message, &mut Outbox::new(), true, clocks);
}

// OpenAC `RuntimeForgetStaleItemTests.ARefusedCarriedItemLeavesTheClientTheWayAServerDeleteRemovesIt`
// and `AnItemTheServerStillDescribesIsKept`
// (`tests/AcDream.Runtime.Tests/Plugins/RuntimeForgetStaleItemTests.cs`):
// an item the server has just refused to appraise leaves the client as a
// server delete takes it (out of the table, its server record and the
// pack's list), Completed; one the server never refused is kept, Refused.
#[test]
fn a_refused_carried_item_leaves_the_client_the_way_a_server_delete_removes_it() {
    let mut state = packed();
    appraisal_answer(&mut state, GEM, false, Duration::ZERO);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let kept = call::<calls::ItemsForgetStale>(b, &KIT);
        assert_eq!(kept.status, ItemStatus::Refused);
        assert_eq!(
            kept.notice.as_deref(),
            Some("The server has not refused to appraise that item.")
        );
        assert_eq!(
            call::<calls::ItemsForgetStale>(b, &GEM).status,
            ItemStatus::Completed
        );
    });
    assert!(state.world.item(GEM).is_none());
    assert!(state.world.object(GEM).is_none());
    assert!(!state.world.items().get_contents(PLAYER).contains(&GEM));
    assert!(state.world.item(KIT).is_some());
    assert!(outbox.is_empty());
}

// OpenAC `RuntimeForgetStaleItemTests.AnItemWhoseAppraisalIsStillAwaitedIsNotLetGoOfYet`:
// an earlier refusal does not count while a new appraisal of the item is
// on its way, and the item surface does not read busy for it.
#[test]
fn an_item_whose_appraisal_is_still_awaited_is_not_let_go_of_yet() {
    let mut state = packed();
    appraisal_answer(&mut state, GEM, false, Duration::ZERO);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::WorldIdentify>(b, &GEM).status,
            ItemStatus::Started
        );
        assert!(!call::<calls::ItemsState>(b, &()).is_busy);
        let result = call::<calls::ItemsForgetStale>(b, &GEM);
        assert_eq!(result.status, ItemStatus::Busy);
        assert_eq!(
            result.notice.as_deref(),
            Some("An appraisal of that item is still awaited.")
        );
    });
    assert!(state.world.item(GEM).is_some());
}

// OpenAC `RuntimeForgetStaleItemTests.ARefusalThatIsNoLongerRecentIsNotActedOnUntilTheServerRefusesAgain`:
// a refusal more than 30 s old is not acted on; a fresh one is.
#[test]
fn a_refusal_that_is_no_longer_recent_is_not_acted_on_until_the_server_refuses_again() {
    let mut state = packed();
    appraisal_answer(&mut state, GEM, false, Duration::ZERO);
    let later = Duration::from_secs(31);
    let mut outbox = Outbox::new();
    at(&mut state, &mut outbox, later, |b| {
        assert_eq!(
            call::<calls::ItemsForgetStale>(b, &GEM).status,
            ItemStatus::Refused
        );
    });
    assert!(state.world.item(GEM).is_some());
    appraisal_answer(&mut state, GEM, false, later);
    at(&mut state, &mut outbox, later, |b| {
        assert_eq!(
            call::<calls::ItemsForgetStale>(b, &GEM).status,
            ItemStatus::Completed
        );
    });
    assert!(state.world.item(GEM).is_none());
}

// OpenAC `RuntimeForgetStaleItemTests.AnItemTheServerSpeaksOfAfterRefusingItIsKept`:
// a stack-size update after the refusal shows the server still has it.
#[test]
fn an_item_the_server_speaks_of_after_refusing_it_is_kept() {
    let mut state = packed();
    appraisal_answer(&mut state, GEM, false, Duration::ZERO);
    state
        .world
        .apply(&ac_proto::Inbound::SetStackSize(ac_proto::SetStackSize {
            sequence: 1,
            guid: GEM,
            stack_size: 2,
            value: 10,
        }));
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::ItemsForgetStale>(b, &GEM).status,
            ItemStatus::Refused
        );
    });
    assert!(state.world.item(GEM).is_some());
}

// OpenAC `RuntimeForgetStaleItemTests.AWornItemIsKept` and
// `AnItemWornByEitherMarkIsKept`: an item in a slot, or with a wielder, or
// both, is on the character and is kept.
#[test]
fn a_worn_item_by_either_mark_is_kept() {
    const HAT: u32 = 0x8000_0005;
    const HEAD: u32 = 0x0000_0001;
    for (in_a_slot, wielded) in [(true, true), (true, false), (false, true)] {
        let mut state = in_world();
        state.world.apply(&owned(HAT, "Hat", 0x2, |w| {
            w.valid_locations = Some(HEAD);
            if wielded {
                w.container_id = None;
                w.wielder_id = Some(PLAYER);
            }
            if in_a_slot {
                w.currently_wielded_location = Some(HEAD);
            }
        }));
        appraisal_answer(&mut state, HAT, false, Duration::ZERO);
        let mut outbox = Outbox::new();
        with_binding(&mut state, &mut outbox, |b| {
            let result = call::<calls::ItemsForgetStale>(b, &HAT);
            assert_eq!(result.status, ItemStatus::Refused, "{in_a_slot} {wielded}");
            assert_eq!(result.notice.as_deref(), Some("That item is equipped."));
        });
        assert!(state.world.item(HAT).is_some());
    }
}

// OpenAC `RuntimeForgetStaleItemTests.APackSomethingStillNamesAsItsContainerIsKept`
// and `APackThatListsContentsIsKept`: a pack its listing or an object
// inside names as a container is kept, and so is what it holds.
#[test]
fn a_pack_that_still_holds_something_is_kept() {
    use ac_proto::GameEventType;
    const PACK: u32 = 0x8000_0006;
    const INSIDE: u32 = 0x8000_0007;
    for listed_empty in [false, true] {
        let mut state = in_world();
        state.world.apply(&owned(PACK, "Pack", 0x200, |w| {
            w.items_capacity = Some(24);
        }));
        state.world.apply(&owned(INSIDE, "Gem", 0x800, |w| {
            w.container_id = Some(PACK);
        }));
        if listed_empty {
            let view = crate::world::tests::game_event(GameEventType::VIEW_CONTENTS, &[PACK, 0]);
            state.world.apply(&ac_proto::Inbound::parse(&view).unwrap());
            assert!(state.world.items().get_contents(PACK).is_empty());
            assert_eq!(state.world.item(INSIDE).unwrap().container_id, PACK);
        }
        appraisal_answer(&mut state, PACK, false, Duration::ZERO);
        let mut outbox = Outbox::new();
        with_binding(&mut state, &mut outbox, |b| {
            let result = call::<calls::ItemsForgetStale>(b, &PACK);
            assert_eq!(result.status, ItemStatus::Refused, "{listed_empty}");
            assert_eq!(
                result.notice.as_deref(),
                Some("That pack still holds something.")
            );
        });
        assert!(state.world.item(PACK).is_some());
        assert!(state.world.item(INSIDE).is_some());
    }
}

// OpenAC `RuntimeForgetStaleItemTests.AnItemWithNoServerRecordIsNotAnItemToLetGoOf`
// and `NothingIsLetGoOfBeforeTheSurfaceIsBound`: an item the client lists
// without a server record is not one to let go of; outside the world
// nothing is.
#[test]
fn an_item_with_no_server_record_is_not_an_item_to_let_go_of() {
    const LISTED: u32 = 0x8000_0008;
    let mut state = in_world();
    state.world.ingest_item(&ac_core::items::WeenieData {
        guid: LISTED,
        name: Some("a listed item".into()),
        container_id: Some(PLAYER),
        ..ac_core::items::WeenieData::default()
    });
    appraisal_answer(&mut state, LISTED, false, Duration::ZERO);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::ItemsForgetStale>(b, &LISTED);
        assert_eq!(result.status, ItemStatus::InvalidItem);
        assert_eq!(
            result.notice.as_deref(),
            Some("The client holds no server record of that item.")
        );
        b.facts.in_world = false;
        assert_eq!(
            call::<calls::ItemsForgetStale>(b, &LISTED).status,
            ItemStatus::Unavailable
        );
    });
    assert!(state.world.item(LISTED).is_some());
}

// OpenAC `StaleItemParityTests.AnItemTheServerRefusesToAppraiseIsLetGoOfTheSameOnBothClients`
// (`tests/AcDream.HostParity.Tests/Scenarios/StaleItemParityTests.cs`): the
// refusal reads on the appraisal slot and on the object, an answered
// appraisal reads not refused; the kept item is refused, the lost one let
// go of without a word to the server; a second time, the character and
// something not carried are not items to let go of.
#[test]
fn an_item_the_server_refuses_to_appraise_is_let_go_of() {
    const GHOST: u32 = GEM;
    const KEEPER: u32 = KIT;
    const MONSTER: u32 = 0x8000_00F0;
    let mut state = packed();
    state.world.apply(&crate::world::tests::create(
        MONSTER,
        crate::world::tests::stamps(1, 1),
        None,
    ));
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::WorldIdentify>(b, &GHOST).status,
            ItemStatus::Started
        );
    });
    appraisal_answer(&mut state, GHOST, false, Duration::ZERO);
    with_binding(&mut state, &mut outbox, |b| {
        let appraisal = call::<calls::LootState>(b, &()).appraisal;
        assert_eq!(appraisal.current_id, GHOST);
        assert!(appraisal.current_object_unsuccessful);
        let ghost = call::<calls::WorldObject>(b, &GHOST).expect("known");
        assert!(ghost.last_appraisal_unsuccessful);
    });
    let later = Duration::from_secs(6);
    at(&mut state, &mut outbox, later, |b| {
        assert_eq!(
            call::<calls::WorldIdentify>(b, &KEEPER).status,
            ItemStatus::Started
        );
    });
    appraisal_answer(&mut state, KEEPER, true, later);
    outbox.drain().for_each(drop);
    at(&mut state, &mut outbox, later, |b| {
        let appraisal = call::<calls::LootState>(b, &()).appraisal;
        assert_eq!(appraisal.current_id, KEEPER);
        assert!(!appraisal.current_object_unsuccessful);
        assert_eq!(
            call::<calls::ItemsForgetStale>(b, &KEEPER).status,
            ItemStatus::Refused
        );
        assert_eq!(
            call::<calls::ItemsForgetStale>(b, &GHOST).status,
            ItemStatus::Completed
        );
        assert!(call::<calls::WorldObject>(b, &GHOST).is_none());
        assert!(
            !call::<calls::ItemsOwned>(b, &())
                .iter()
                .any(|i| i.object_id == GHOST)
        );
        assert!(call::<calls::WorldObject>(b, &KEEPER).is_some());
        assert_eq!(
            call::<calls::ItemsForgetStale>(b, &GHOST).status,
            ItemStatus::InvalidItem
        );
        assert_eq!(
            call::<calls::ItemsForgetStale>(b, &PLAYER).status,
            ItemStatus::InvalidItem
        );
        assert_eq!(
            call::<calls::ItemsForgetStale>(b, &MONSTER).status,
            ItemStatus::InvalidItem
        );
    });
    assert!(state.world.object(GHOST).is_none());
    assert!(outbox.is_empty());
}

// ── Pet devices: OpenAC `tests/AcDream.Runtime.Tests/Plugins/RuntimeInventoryItemPetDeviceTests.cs`.
// A summoner finds its essences by what the client is sent: the shared
// cooldown every essence belongs to, with the item and again in an
// appraisal; the pet an essence summons the server keeps to itself.

const ESSENCE: u32 = 0x7000_5101;
const PET: u32 = 0x8000_5102;
const OTHER_PET: u32 = 0x8000_5103;
const SOMEONE_ELSE: u32 = 0x5000_9999;

/// The essence as a plugin sees it, after `then` reaches the world.
fn essence(
    configure: impl FnOnce(&mut ac_proto::PublicWeenieDesc),
    then: &[ac_proto::Inbound<'static>],
) -> ac_plugin::abi::items::InventoryItem {
    let mut state = in_world();
    state
        .world
        .apply(&owned(ESSENCE, "Mud Golem Essence", 0x800, configure));
    for message in then {
        state.world.apply(message);
    }
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        call::<calls::ItemsOwned>(b, &())
            .into_iter()
            .find(|i| i.object_id == ESSENCE)
            .expect("the essence is owned")
    })
}

fn int_update(guid: u32, property: u32, value: i32) -> ac_proto::Inbound<'static> {
    ac_proto::Inbound::PublicUpdatePropertyInt(ac_proto::PublicUpdatePropertyInt {
        sequence: 0,
        guid,
        property,
        value,
    })
}

// OpenAC `AnEssenceSentWithTheSummoningCooldownIsAPetDevice`.
#[test]
fn an_essence_sent_with_the_summoning_cooldown_is_a_pet_device() {
    let item = essence(
        |w| {
            w.cooldown_id = Some(213);
            w.cooldown_duration = Some(45.0);
        },
        &[],
    );

    assert!(item.is_pet_device());
    assert_eq!(item.pet_class, 0);
    assert_eq!(
        item.shared_cooldown_id,
        ac_plugin::abi::items::InventoryItem::SUMMONING_COOLDOWN_ID
    );
    assert!((item.cooldown_seconds - 45.0).abs() < f64::EPSILON);
}

// OpenAC `AnItemOnAnyOtherSharedCooldownIsNotAPetDevice`.
#[test]
fn an_item_on_any_other_shared_cooldown_is_not_a_pet_device() {
    let item = essence(
        |w| {
            w.cooldown_id = Some(100);
            w.cooldown_duration = Some(30.0);
        },
        &[],
    );

    assert!(!item.is_pet_device());
    assert_eq!(item.shared_cooldown_id, 100);
}

// OpenAC `AnItemWithNoCooldownReadsInert`.
#[test]
fn an_item_with_no_cooldown_reads_inert() {
    let item = essence(|_| {}, &[]);

    assert!(!item.is_pet_device());
    assert_eq!(item.shared_cooldown_id, 0);
    assert!(item.cooldown_seconds.abs() < f64::EPSILON);
}

// OpenAC `TheCooldownAnAppraisalOrUpdateSendsMakesItAPetDevice`.
#[test]
fn the_cooldown_an_appraisal_or_update_sends_makes_it_a_pet_device() {
    let item = essence(
        |_| {},
        &[int_update(
            ESSENCE,
            ac_core::properties::PropertyInt::SHARED_COOLDOWN.0,
            213,
        )],
    );

    assert!(item.is_pet_device());
    assert_eq!(item.shared_cooldown_id, 213);
}

// OpenAC `APetClassFromTheServerStillMarksAPetDevice`.
#[test]
fn a_pet_class_from_the_server_still_marks_a_pet_device() {
    let item = essence(
        |_| {},
        &[int_update(
            ESSENCE,
            ac_core::properties::PropertyInt::PET_CLASS.0,
            7,
        )],
    );

    assert!(item.is_pet_device());
}

// OpenAC `ThePetsCountedAreTheLivingCreaturesTheServerSaysTheCharacterOwns`.
#[test]
fn the_pets_counted_are_the_living_creatures_the_server_says_the_character_owns() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(call::<calls::ItemsState>(b, &()).active_owned_pet_count, 0);
    });
    let pet = |guid: u32, name: &str, owner: u32| {
        owned(guid, name, 0x10, |w| {
            w.container_id = None;
            w.pet_owner_id = Some(owner);
        })
    };
    state.world.apply(&pet(PET, "+Horan's Golem", PLAYER));
    state
        .world
        .apply(&pet(OTHER_PET, "Someone else's Golem", SOMEONE_ELSE));

    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(call::<calls::ItemsState>(b, &()).active_owned_pet_count, 1);
    });
}

// ── A world object's coverage, header and portal details (OpenAC
// `RuntimeWorldObjectProjection.Project`: `CoverageMask`, `Header`,
// `PortalDestination`, `PortalMinimumLevel`, `PortalMaximumLevel`) ────────

mod world_object_details {
    use ac_core::properties::{PropertyInt, PropertyString};
    use ac_plugin::abi::calls;
    use ac_plugin::abi::world::WorldObject;
    use ac_proto::appraise::IdentifyObjectResponse;

    use super::super::tests::{call, in_world, with_binding};
    use super::owned;
    use crate::session::{Outbox, SessionState};

    const ITEM_TYPE_PORTAL: u32 = 0x0001_0000;
    const ITEM_TYPE_MISC: u32 = 0x0000_0800;

    /// An object lying in the world (no container), described as
    /// `item_type`, its properties those of an appraisal.
    fn appraised(
        guid: u32,
        item_type: u32,
        strings: &[(u32, &str)],
        ints: &[(u32, i32)],
    ) -> SessionState {
        let mut state = in_world();
        state.world.apply(&owned(guid, "Thing", item_type, |w| {
            w.container_id = None;
        }));
        let mut response = IdentifyObjectResponse {
            guid,
            success: true,
            ..IdentifyObjectResponse::default()
        };
        for &(key, value) in strings {
            response.properties.strings.insert(key, value.to_owned());
        }
        for &(key, value) in ints {
            response.properties.ints.insert(key, value);
        }
        state.world.apply_appraisal(&response, 1.0);
        state
    }

    fn project(state: &mut SessionState, guid: u32) -> WorldObject {
        let mut outbox = Outbox::new();
        with_binding(state, &mut outbox, |b| {
            call::<calls::WorldObject>(b, &guid).expect("known")
        })
    }

    // OpenAC `RuntimeWorldObjectPortalTests.PortalProjectionSharesKnownDestinationAndLevelLimits`
    // (`tests/AcDream.Runtime.Tests/Gameplay/RuntimeWorldObjectPortalTests.cs`).
    #[test]
    fn a_portal_shares_its_known_destination_and_level_limits() {
        let mut state = appraised(
            1,
            ITEM_TYPE_PORTAL,
            &[(PropertyString::APPRAISAL_PORTAL_DESTINATION.0, "Holtburg")],
            &[
                (PropertyInt::MIN_LEVEL.0, 10),
                (PropertyInt::MAX_LEVEL.0, 30),
            ],
        );
        let portal = project(&mut state, 1);
        assert_eq!(portal.portal_destination.as_deref(), Some("Holtburg"));
        assert_eq!(portal.portal_minimum_level, Some(10));
        assert_eq!(portal.portal_maximum_level, Some(30));
    }

    // OpenAC `RuntimeWorldObjectPortalTests.MissingOrUnrestrictedPortalDetailsRemainUnknown`.
    #[test]
    fn missing_or_unrestricted_portal_details_remain_unknown() {
        let mut state = appraised(
            2,
            ITEM_TYPE_PORTAL,
            &[(PropertyString::APPRAISAL_PORTAL_DESTINATION.0, " ")],
            &[(PropertyInt::MIN_LEVEL.0, 0)],
        );
        let portal = project(&mut state, 2);
        assert_eq!(portal.portal_destination, None);
        assert_eq!(portal.portal_minimum_level, None);
        assert_eq!(portal.portal_maximum_level, None);
    }

    // OpenAC `RuntimeWorldObjectPortalTests.OtherObjectsDoNotExposePortalDetails`.
    #[test]
    fn other_objects_do_not_expose_portal_details() {
        let mut state = appraised(
            3,
            ITEM_TYPE_MISC,
            &[(PropertyString::APPRAISAL_PORTAL_DESTINATION.0, "Holtburg")],
            &[(PropertyInt::MIN_LEVEL.0, 10)],
        );
        let item = project(&mut state, 3);
        assert_eq!(item.portal_destination, None);
        assert_eq!(item.portal_minimum_level, None);
    }

    // OpenAC `ObjectHeaderParityTests.EitherClientReportsAWorldObjectsHeader`
    // (`tests/AcDream.HostParity.Tests/Scenarios/ObjectHeaderParityTests.cs`)
    // and the world object's `CoverageMask`: the description's words and
    // optional values reach a thing out in the world as sent (a value left
    // out reads as absent), and its coverage bits are known before any
    // appraisal.
    #[test]
    fn a_world_object_reports_its_header_and_coverage() {
        const SHIRT: u32 = 0x5000_0091;
        let mut state = in_world();
        state.world.apply(&owned(SHIRT, "Shirt", 0x2, |w| {
            w.container_id = None;
            w.priority = Some(0x0000_0408);
            w.use_radius = Some(3.0);
            w.header_flags = 0x0028_0010;
        }));
        let shirt = project(&mut state, SHIRT);
        assert_eq!(shirt.coverage_mask, 0x0408);
        let header = shirt.header.expect("described");
        assert_eq!(header.weenie_header_flags, Some(0x0028_0010));
        assert_eq!(header.weenie_header_flags2, None);
        assert_eq!(header.use_radius, Some(3.0));
        assert_eq!(header.parent_id, None);
    }
}

// OPENAC_PROBE_EXTERNAL_CONTAINER (content/docs/launch-options.md): each plugin
// close of a container is one line with its answer and why.
#[test]
fn the_external_container_probe_names_a_refused_plugin_close_and_why() {
    let mut state = packed();
    state.inventory.enable_external_container_probe();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            call::<calls::LootClose>(b, &CORPSE).status,
            ItemStatus::InvalidTarget
        );
    });
    let lines = state.inventory.take_probe_lines();
    assert_eq!(lines.len(), 1);
    assert!(
        lines[0].contains(
            "plugin close 0x70000010: InvalidTarget, not the open container \
             (requested 0x00000000, open 0x00000000)"
        ),
        "{}",
        lines[0]
    );
    assert!(outbox.is_empty());
}

// OpenAC `RuntimeLiveEntitySessionControllerTests
// .AnItemInsideACorpseReportsTheColoursItWouldOnTheGround`: an item the
// server creates inside an open corpse has no place in the world, but loot
// rules test its colours before it is picked up, so it reports the same
// colour swaps it would lying on the ground. Every colour reads as its own
// index in the red channel.
#[test]
fn an_item_inside_a_corpse_reports_the_colours_it_would_on_the_ground() {
    use crate::world::tests::{create, position, stamps};
    const IN_CORPSE: u32 = 0x7000_0071;
    const ON_GROUND: u32 = 0x7000_0072;
    let swaps = vec![
        ac_proto::SubPalette {
            sub_palette_id: 0x0400_0123,
            offset: 3,
            length: 4,
        },
        ac_proto::SubPalette {
            sub_palette_id: 0x0400_0456,
            offset: 0,
            length: 2,
        },
    ];
    let item = |guid: u32, at, container_id| {
        let mut message = create(guid, stamps(1, 1), at);
        if let ac_proto::Inbound::CreateObject(create) = &mut message {
            create.model.base_palette_id = Some(0x0400_007E);
            create.model.sub_palettes.clone_from(&swaps);
            create.weenie = Some(ac_proto::PublicWeenieDesc {
                name: "Robe".into(),
                item_type: 0x2,
                container_id,
                ..ac_proto::PublicWeenieDesc::default()
            });
        }
        message
    };
    let mut state = in_world();
    state
        .world
        .apply(&item(ON_GROUND, Some(position(0xA9B4_0031, 5.0)), None));
    state.world.apply(&item(IN_CORPSE, None, Some(CORPSE)));

    let index_as_red = |_: u32, index: usize| Some((u8::try_from(index).unwrap_or(0), 0, 0));
    let lying = super::items::palettes_with(&state.world, ON_GROUND, index_as_red);
    let contained = super::items::palettes_with(&state.world, IN_CORPSE, index_as_red);

    assert_eq!(lying.len(), 2);
    assert_eq!(contained, lying);
    assert_eq!(
        contained[0],
        ac_plugin::abi::items::Palette {
            palette_id: 0x0400_0123,
            offset: 3,
            length: 4,
            red: 40,
            green: 0,
            blue: 0,
        }
    );
}

/// A chest on the ground holding a ruby and an emerald (its loose items, in
/// that order) and a sack with a pearl in it.
const CHEST: u32 = 0x7000_00C1;
const RUBY: u32 = 0x8000_00C2;
const EMERALD: u32 = 0x8000_00C3;
const SACK: u32 = 0x8000_00C4;
const PEARL: u32 = 0x8000_00C5;

/// [`open_chest_of`] with room to spare.
fn with_open_chest() -> (SessionState, Outbox) {
    open_chest_of(24)
}

/// [`packed`] with [`CHEST`] (taking `capacity` loose items) open: its use
/// answered and its contents listed. The outbox is emptied.
fn open_chest_of(capacity: i8) -> (SessionState, Outbox) {
    open_chest(capacity, false)
}

/// [`open_chest_of`], with the chest's listing arriving before or after its
/// items' descriptions (`listed_first`: the server's order on an open).
fn open_chest(capacity: i8, listed_first: bool) -> (SessionState, Outbox) {
    use ac_core::items::{ItemType, ItemUseable, PublicWeenieFlags};
    use ac_proto::{GameEventType, Inbound};

    use crate::inventory::{ItemCommand, ObjectRef};
    use crate::session::SessionCommand;
    use crate::world::tests::{create, game_event, position, stamps};

    let mut state = packed();
    let mut outbox = Outbox::new();
    let mut chest = create(CHEST, stamps(1, 1), Some(position(0xA9B4_0031, 5.0)));
    if let Inbound::CreateObject(create) = &mut chest {
        create.weenie = Some(ac_proto::PublicWeenieDesc {
            name: "Chest".into(),
            item_type: ItemType::CONTAINER.bits(),
            object_description_flags: PublicWeenieFlags::OPENABLE.bits(),
            useability: Some(ItemUseable::REMOTE.bits()),
            items_capacity: Some(capacity),
            containers_capacity: Some(4),
            ..ac_proto::PublicWeenieDesc::default()
        });
    }
    state.apply(&chest, &mut outbox, true, CLOCKS);
    let describe_items = |state: &mut SessionState, outbox: &mut Outbox| {
        for (guid, name, item_type, container, items) in [
            (RUBY, "Ruby", ItemType::GEM, CHEST, None),
            (EMERALD, "Emerald", ItemType::GEM, CHEST, None),
            (SACK, "Sack", ItemType::CONTAINER, CHEST, Some(8)),
            (PEARL, "Pearl", ItemType::GEM, SACK, None),
        ] {
            let mut item = create(guid, stamps(1, 1), None);
            if let Inbound::CreateObject(create) = &mut item {
                create.weenie = Some(ac_proto::PublicWeenieDesc {
                    name: name.into(),
                    item_type: item_type.bits(),
                    container_id: Some(container),
                    items_capacity: items,
                    ..ac_proto::PublicWeenieDesc::default()
                });
            }
            state.apply(&item, outbox, true, CLOCKS);
        }
    };
    if !listed_first {
        describe_items(&mut state, &mut outbox);
    }
    state.command(
        SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(CHEST))),
        &mut outbox,
        true,
        CLOCKS,
    );
    for (event, payload) in [
        (
            GameEventType::VIEW_CONTENTS,
            &[CHEST, 3, RUBY, 0, EMERALD, 0, SACK, 1][..],
        ),
        (GameEventType::USE_DONE, &[0][..]),
    ] {
        let body = game_event(event, payload);
        state.apply(&Inbound::parse(&body).unwrap(), &mut outbox, true, CLOCKS);
    }
    if listed_first {
        describe_items(&mut state, &mut outbox);
    }
    assert_eq!(
        state.inventory.external_containers().current_container_id(),
        CHEST
    );
    assert_eq!(
        state.world.items().get_contents(CHEST),
        [RUBY, EMERALD, SACK]
    );
    assert_eq!(state.world.items().get_contents(SACK), [PEARL]);
    outbox.drain().for_each(drop);
    (state, outbox)
}

fn put(object_id: u32, container_id: u32, amount: u32, placement: i32) -> MoveRequest {
    MoveRequest {
        object_id,
        container_id,
        amount,
        placement,
    }
}

/// What went out, as message bodies.
fn sent(outbox: &mut Outbox) -> Vec<Vec<u8>> {
    outbox.drain().map(|o| o.body).collect()
}

// A put into the open container needs one open, and names it.
#[test]
fn a_put_with_no_container_open_is_an_invalid_target() {
    let mut state = packed();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(GEM, CHEST, 0, 3));
        assert_eq!(result.status, ItemStatus::InvalidTarget);
        let result = call::<calls::LootPut>(b, &put(GEM, 0, 0, 3));
        assert_eq!(result.status, ItemStatus::InvalidTarget);
    });
    assert!(outbox.is_empty());
}

#[test]
fn a_put_into_a_container_that_is_not_the_open_one_is_an_invalid_target() {
    let (mut state, mut outbox) = with_open_chest();
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(GEM, PLAYER, 0, 3));
        assert_eq!(result.status, ItemStatus::InvalidTarget);
        let result = call::<calls::LootPut>(b, &put(GEM, 0x9999, 0, 3));
        assert_eq!(result.status, ItemStatus::InvalidTarget);
    });
    assert!(outbox.is_empty());
}

// The item is the character's own or already in the open container.
#[test]
fn a_put_of_an_item_neither_owned_nor_in_the_open_container_is_an_invalid_item() {
    let (mut state, mut outbox) = with_open_chest();
    state.world.apply(&crate::world::tests::create(
        LOOSE,
        crate::world::tests::stamps(1, 1),
        Some(crate::world::tests::position(0xA9B4_0031, 7.0)),
    ));
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(LOOSE, CHEST, 0, 3));
        assert_eq!(result.status, ItemStatus::InvalidItem);
        let result = call::<calls::LootPut>(b, &put(0x9999, CHEST, 0, 3));
        assert_eq!(result.status, ItemStatus::InvalidItem);
        let result = call::<calls::LootPut>(b, &put(CHEST, CHEST, 0, 3));
        assert_eq!(result.status, ItemStatus::InvalidItem);
    });
    assert!(outbox.is_empty());
}

// The whole stack goes as the container window's drop does: a put into the
// chest at that slot.
#[test]
fn a_whole_owned_stack_is_put_into_the_open_chest_at_its_slot() {
    let (mut state, mut outbox) = with_open_chest();
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(GEM, CHEST, 0, 3));
        assert_eq!(result.status, ItemStatus::Started);
    });
    assert_eq!(
        sent(&mut outbox),
        [ac_proto::inventory::build_put_item_in_container(
            2, GEM, CHEST, 3
        )]
    );
}

// Part of a stack splits off into the chest at the slot.
#[test]
fn part_of_an_owned_stack_splits_into_the_open_chest() {
    let (mut state, mut outbox) = with_open_chest();
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(GEM, CHEST, 2, 3));
        assert_eq!(result.status, ItemStatus::Started);
    });
    assert_eq!(
        sent(&mut outbox),
        [ac_proto::inventory::build_stackable_split_to_container(
            2, GEM, CHEST, 3, 2
        )]
    );
}

// An item already in the chest moves to another slot of it.
#[test]
fn an_item_in_the_open_chest_is_reordered_within_it() {
    let (mut state, mut outbox) = with_open_chest();
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(RUBY, CHEST, 0, 1));
        assert_eq!(result.status, ItemStatus::Started);
    });
    assert_eq!(
        sent(&mut outbox),
        [ac_proto::inventory::build_put_item_in_container(
            2, RUBY, CHEST, 1
        )]
    );
}

// One request at a time: a second put while the first is in flight is
// busy and sends nothing more.
#[test]
fn a_put_while_a_request_is_in_flight_is_busy() {
    let (mut state, mut outbox) = with_open_chest();
    with_binding(&mut state, &mut outbox, |b| {
        let first = call::<calls::LootPut>(b, &put(GEM, CHEST, 0, 3));
        assert_eq!(first.status, ItemStatus::Started);
        let second = call::<calls::LootPut>(b, &put(RUBY, CHEST, 0, 1));
        assert_eq!(second.status, ItemStatus::Busy);
    });
    assert_eq!(sent(&mut outbox).len(), 1);
}

#[test]
fn a_put_of_more_than_the_stack_is_refused() {
    let (mut state, mut outbox) = with_open_chest();
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(GEM, CHEST, 6, 3));
        assert_eq!(result.status, ItemStatus::Refused);
        assert_eq!(result.notice.as_deref(), Some("Invalid stack quantity."));
    });
    assert!(outbox.is_empty());
}

// The open container's window takes only its own loose items for a
// reorder: a pack in it, or an item inside such a pack, is not one.
#[test]
fn a_pack_in_the_open_chest_or_an_item_inside_it_is_an_invalid_item() {
    let (mut state, mut outbox) = with_open_chest();
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(PEARL, CHEST, 0, 0));
        assert_eq!(result.status, ItemStatus::InvalidItem);
        let result = call::<calls::LootPut>(b, &put(SACK, CHEST, 0, 0));
        assert_eq!(result.status, ItemStatus::InvalidItem);
    });
    assert!(outbox.is_empty());
}

// The window refuses a pack dropped on the container's item list, in its
// own words, and sends nothing.
#[test]
fn an_owned_pack_is_refused_as_the_window_refuses_it() {
    const POUCH: u32 = 0x8000_0030;
    let (mut state, mut outbox) = with_open_chest();
    state.world.apply(&owned(POUCH, "Pouch", 0x200, |w| {
        w.items_capacity = Some(8);
        w.stack_size = None;
    }));
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(POUCH, CHEST, 0, 0));
        assert_eq!(result.status, ItemStatus::Refused);
        assert_eq!(
            result.notice.as_deref(),
            Some("The Chest can fit no more containers!")
        );
    });
    assert!(outbox.is_empty());
}

// A chest with no free item slot takes nothing more, but still lets its own
// items be reordered.
#[test]
fn a_full_chest_refuses_a_new_item_but_takes_a_reorder() {
    let (mut state, mut outbox) = open_chest_of(2);
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(GEM, CHEST, 0, 0));
        assert_eq!(result.status, ItemStatus::Refused);
        assert_eq!(
            result.notice.as_deref(),
            Some("The Chest is completely full!")
        );
    });
    assert!(outbox.is_empty());
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(EMERALD, CHEST, 0, 0));
        assert_eq!(result.status, ItemStatus::Started);
    });
    assert_eq!(
        sent(&mut outbox),
        [ac_proto::inventory::build_put_item_in_container(
            2, EMERALD, CHEST, 0
        )]
    );
}

// An item staged in a trade goes nowhere; the notice names the stack as
// the client does (a stack of five gems is "Gems").
#[test]
fn an_item_in_a_trade_is_refused() {
    let (mut state, mut outbox) = with_open_chest();
    state.world.set_trade_state(GEM, 1);
    with_binding(&mut state, &mut outbox, |b| {
        let result = call::<calls::LootPut>(b, &put(GEM, CHEST, 0, 0));
        assert_eq!(result.status, ItemStatus::Refused);
        assert_eq!(result.notice.as_deref(), Some("The Gems is being traded"));
    });
    assert!(outbox.is_empty());
}

// The window sends nothing for a drop that leaves an item where it is; a
// put to the slot the item has (or past the end, for the last) is done
// already.
#[test]
fn a_put_that_leaves_the_item_where_it_is_is_completed_and_sends_nothing() {
    let (mut state, mut outbox) = with_open_chest();
    with_binding(&mut state, &mut outbox, |b| {
        let ruby = call::<calls::LootPut>(b, &put(RUBY, CHEST, 0, 0));
        assert_eq!(ruby.status, ItemStatus::Completed);
        let emerald = call::<calls::LootPut>(b, &put(EMERALD, CHEST, 0, 1));
        assert_eq!(emerald.status, ItemStatus::Completed);
        let past_the_end = call::<calls::LootPut>(b, &put(EMERALD, CHEST, 0, 7));
        assert_eq!(past_the_end.status, ItemStatus::Completed);
    });
    assert!(outbox.is_empty());
}

// With the session's link down nothing can go out, and the put says so
// rather than reporting a start.
#[test]
fn a_put_with_the_link_down_is_unavailable_and_sends_nothing() {
    let (mut state, mut outbox) = with_open_chest();
    with_binding(&mut state, &mut outbox, |b| {
        b.link.as_mut().unwrap().active = false;
        let result = call::<calls::LootPut>(b, &put(GEM, CHEST, 0, 3));
        assert_eq!(result.status, ItemStatus::Unavailable);
    });
    assert!(outbox.is_empty());
}

// An item shown waiting at a slot holds the next put up until the server
// answers it.
#[test]
fn a_put_while_an_item_waits_at_a_slot_is_busy() {
    let (mut state, mut outbox) = with_open_chest();
    with_binding(&mut state, &mut outbox, |b| {
        let first = call::<calls::LootPut>(b, &put(GEM, CHEST, 0, 1));
        assert_eq!(first.status, ItemStatus::Started);
        assert!(b.state.inventory.pending_placement().is_some());
        let second = call::<calls::LootPut>(b, &put(KIT, CHEST, 0, 0));
        assert_eq!(second.status, ItemStatus::Busy);
    });
    assert_eq!(sent(&mut outbox).len(), 1);
}

// A weapon switch in flight (the worn axe being moved aside for the sword)
// holds a put up.
#[test]
fn a_put_while_a_wield_is_in_flight_is_busy() {
    const AXE: u32 = 0x8000_0004;
    const MELEE: u32 = 0x0000_0001;
    let (mut state, mut outbox) = with_open_chest();
    state.world.apply(&owned(SWORD, "Sword", 0x1, |w| {
        w.valid_locations = Some(MELEE);
        w.combat_use = Some(1);
        w.stack_size = None;
    }));
    state.world.apply(&owned(AXE, "Axe", 0x1, |w| {
        w.valid_locations = Some(MELEE);
        w.combat_use = Some(1);
        w.stack_size = None;
        w.container_id = None;
        w.wielder_id = Some(PLAYER);
        w.currently_wielded_location = Some(MELEE);
    }));
    state.world.drain_changes().for_each(drop);
    at(&mut state, &mut outbox, Duration::from_secs(5), |b| {
        let sword = EquipRequest {
            object_id: SWORD,
            location: 0,
        };
        assert_eq!(
            call::<calls::EquipmentEquip>(b, &sword).status,
            EquipmentStatus::Started
        );
        assert!(b.state.inventory.is_wield_busy());
    });
    outbox.drain().for_each(drop);
    at(&mut state, &mut outbox, Duration::from_secs(6), |b| {
        let result = call::<calls::LootPut>(b, &put(GEM, CHEST, 0, 0));
        assert_eq!(result.status, ItemStatus::Busy);
    });
    assert!(outbox.is_empty());
}

// An item's container slot is its place in its container's list among its
// own kind (loose items, or packs), whatever slot the server last named:
// the number the game shows for it. Side-pack items arrive listed first
// and described after, naming no slot.

/// The character's description listing `inventory`, then `listings` (each
/// a container and its contents as guid, container type pairs), then the
/// items described.
fn logged_in_with(
    inventory: &[(u32, u32)],
    listings: &[(u32, &[u32])],
    items: &[ac_proto::Inbound<'static>],
) -> SessionState {
    use ac_proto::{GameEventType, Inbound};
    let mut state = in_world();
    let mut description = crate::world::tests::empty_description();
    description.inventory = inventory
        .iter()
        .map(|&(guid, container_type)| ac_proto::InventoryEntry {
            guid,
            container_type,
        })
        .collect();
    state.world.apply_player_description(&description);
    for &(container, payload) in listings {
        let mut words = vec![container, u32::try_from(payload.len() / 2).unwrap()];
        words.extend_from_slice(payload);
        let view = crate::world::tests::game_event(GameEventType::VIEW_CONTENTS, &words);
        state.world.apply(&Inbound::parse(&view).unwrap());
    }
    for item in items {
        state.world.apply(item);
    }
    state.world.drain_changes().for_each(drop);
    state
}

/// The container slots a plugin reads for `ids` among the owned items.
fn slots_seen(state: &mut SessionState, ids: &[u32]) -> Vec<i32> {
    let mut outbox = Outbox::new();
    with_binding(state, &mut outbox, |b| {
        let items = call::<calls::ItemsOwned>(b, &());
        ids.iter()
            .map(|id| {
                items
                    .iter()
                    .find(|i| i.object_id == *id)
                    .unwrap()
                    .container_slot
            })
            .collect()
    })
}

const SIDE_PACK: u32 = 0x8000_0040;
const INNER_PACK: u32 = 0x8000_0041;

fn pack_in(guid: u32, container: u32) -> ac_proto::Inbound<'static> {
    owned(guid, "Pack", 0x200, |w| {
        w.container_id = Some(container);
        w.items_capacity = Some(24);
        w.stack_size = None;
    })
}

fn gem_in(guid: u32, container: u32) -> ac_proto::Inbound<'static> {
    owned(guid, "Gem", 0x800, |w| w.container_id = Some(container))
}

#[test]
fn a_side_pack_item_listed_at_login_reports_its_place_in_the_pack() {
    let mut state = logged_in_with(
        &[(SIDE_PACK, 1)],
        &[(SIDE_PACK, &[GEM, 0, INNER_PACK, 1, KIT, 0])],
        &[
            pack_in(SIDE_PACK, PLAYER),
            gem_in(KIT, SIDE_PACK),
            pack_in(INNER_PACK, SIDE_PACK),
            gem_in(GEM, SIDE_PACK),
        ],
    );
    // The pack between them is counted among the packs, not the items.
    assert_eq!(slots_seen(&mut state, &[GEM, KIT, INNER_PACK]), [0, 1, 0]);
}

#[test]
fn main_pack_items_report_the_slots_they_did_and_a_wielded_one_none() {
    let mut state = logged_in_with(
        &[(GEM, 0), (KIT, 0), (SIDE_PACK, 1)],
        &[(SIDE_PACK, &[])],
        &[
            gem_in(KIT, PLAYER),
            gem_in(GEM, PLAYER),
            pack_in(SIDE_PACK, PLAYER),
            owned(SWORD, "Sword", 0x1, |w| {
                w.container_id = None;
                w.wielder_id = Some(PLAYER);
                w.valid_locations = Some(0x0010_0000);
                w.currently_wielded_location = Some(0x0010_0000);
                w.stack_size = None;
            }),
        ],
    );
    let stored: Vec<i32> = [GEM, KIT]
        .iter()
        .map(|&id| state.world.items().get(id).unwrap().container_slot)
        .collect();
    assert_eq!(stored, [0, 1]);
    assert_eq!(slots_seen(&mut state, &[GEM, KIT]), stored);
    // The pack is the first of the packs (the description's flat list put
    // it third), and the sword is in no container.
    assert_eq!(slots_seen(&mut state, &[SIDE_PACK, SWORD]), [0, -1]);
}

#[test]
fn an_item_in_the_open_chest_reports_its_place_among_the_loose_items() {
    let (mut state, mut outbox) = open_chest(24, true);
    with_binding(&mut state, &mut outbox, |b| {
        let contents = call::<calls::LootContents>(b, &());
        let slot = |id: u32| {
            contents
                .iter()
                .find(|i| i.object_id == id)
                .unwrap()
                .container_slot
        };
        assert_eq!(
            [slot(RUBY), slot(EMERALD), slot(SACK), slot(PEARL)],
            [0, 1, 0, 0]
        );
    });
}
