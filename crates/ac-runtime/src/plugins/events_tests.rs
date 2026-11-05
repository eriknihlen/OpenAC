//! The events plugins hear and the world reads, against the world owner.
//! Cases follow OpenAC's `RuntimeAutomationSurface` observers (`OnEntity`,
//! `OnInventory`, `OnLifecycle`, `OnPortal`, `OnExternalContainerChanged`)
//! and `RuntimeWorldObjectProjection`.

use ac_plugin::abi::Event;
use ac_plugin::abi::calls;
use ac_plugin::abi::world::{ActivationOutcome, ObjectChangeKind, ObjectClass};

use super::events;
use super::tests::{call, in_world, with_binding};
use crate::session::{HostEvent, Outbox, SessionState};

/// An object described with a name, item type and public flags, placed in
/// `cell` (none for one in a container).
pub(super) fn described(
    guid: u32,
    instance: u16,
    name: &str,
    item_type: u32,
    flags: u32,
    cell: Option<u32>,
) -> ac_proto::Inbound<'static> {
    use crate::world::tests::{create, position, stamps};
    let mut message = create(guid, stamps(instance, 1), cell.map(|c| position(c, 5.0)));
    if let ac_proto::Inbound::CreateObject(create) = &mut message {
        create.weenie = Some(ac_proto::PublicWeenieDesc {
            name: name.into(),
            item_type,
            object_description_flags: flags,
            ..ac_proto::PublicWeenieDesc::default()
        });
    }
    message
}

fn moved_to(guid: u32, sequence: u16, cell: u32) -> ac_proto::Inbound<'static> {
    ac_proto::Inbound::UpdatePosition(ac_proto::UpdatePosition {
        guid,
        flags: 0,
        position: crate::world::tests::position(cell, 7.0),
        velocity: None,
        placement_id: None,
        instance_sequence: 1,
        position_sequence: sequence,
        teleport_sequence: 0,
        force_position_sequence: 0,
    })
}

fn kinds(events: &[Event]) -> Vec<(u32, ObjectChangeKind)> {
    events
        .iter()
        .filter_map(|e| match e {
            Event::ObjectChanged(c) => Some((c.object_id, c.kind)),
            _ => None,
        })
        .collect()
}

fn changes_of(state: &mut SessionState, message: &ac_proto::Inbound<'_>) -> Vec<Event> {
    state.world.apply(message);
    let changes: Vec<_> = state.world.drain_changes().collect();
    events::objects_changed(state, &changes)
}

// OpenAC `OnEntity`/`OnInventory` and `PluginObjectChange.FieldsFor`: a
// first sighting is a creation, a move a move only across a cell, a
// re-description a release then a creation, a delete a release; revisions
// count up from 1.
#[test]
fn object_changes_follow_openac_vocabulary() {
    use ObjectChangeKind::{Created, Moved, Released};
    const DOOR: u32 = 0x7000_0001;
    let mut state = in_world();
    let created = changes_of(
        &mut state,
        &described(DOOR, 1, "Door", 0x80, 0x1000, Some(0xA9B4_0031)),
    );
    assert_eq!(kinds(&created), [(DOOR, Created)]);
    let Event::ObjectChanged(first) = &created[0] else {
        panic!("an object change");
    };
    assert_eq!(first.revision, 1);
    assert_eq!(first.changed_fields, 0b111);
    let current = first.current.as_ref().expect("its state");
    assert_eq!(current.object_class, ObjectClass::Door);
    assert!(current.can_activate() && current.is_landscape && !current.is_owned);
    assert_eq!(
        current.position.as_ref().map(|p| p.cell_id),
        Some(0xA9B4_0031)
    );

    // Within the cell: no move. Across: a move.
    assert!(kinds(&changes_of(&mut state, &moved_to(DOOR, 2, 0xA9B4_0031))).is_empty());
    assert_eq!(
        kinds(&changes_of(&mut state, &moved_to(DOOR, 3, 0xA9B4_0032))),
        [(DOOR, Moved)]
    );
    // A new incarnation: released, then created.
    assert_eq!(
        kinds(&changes_of(
            &mut state,
            &described(DOOR, 2, "Door", 0x80, 0x1000, Some(0xA9B4_0032))
        )),
        [(DOOR, Released), (DOOR, Created)]
    );
    // Deleted: released, with no state.
    let gone = changes_of(
        &mut state,
        &ac_proto::Inbound::DeleteObject(ac_proto::DeleteObject {
            guid: DOOR,
            instance_sequence: 2,
        }),
    );
    assert_eq!(kinds(&gone), [(DOOR, Released)]);
    let Event::ObjectChanged(released) = &gone[0] else {
        panic!("an object change");
    };
    assert!(released.current.is_none());
    assert_eq!(released.changed_fields, 0b1);
}

// OpenAC `RuntimeWorldEntityProjection.OnEntity`: an object entering the
// world with a place is an entity spawned (its local id, the server id,
// where it stands); a move or an update is not; a new incarnation is.
#[test]
fn an_object_appearing_in_the_world_is_an_entity_spawned() {
    const DOOR: u32 = 0x7000_0001;
    let mut state = in_world();
    let spawns = |events: &[Event]| -> Vec<(u32, (f32, f32, f32))> {
        events
            .iter()
            .filter_map(|e| match e {
                Event::EntitySpawned(s) => Some((s.source_id, s.position)),
                _ => None,
            })
            .collect()
    };
    let created = changes_of(
        &mut state,
        &described(DOOR, 1, "Door", 0x80, 0x1000, Some(0xA9B4_0031)),
    );
    let first = spawns(&created);
    assert_eq!(first.len(), 1);
    assert_eq!(first[0].0, DOOR);
    let Some(Event::EntitySpawned(entity)) = created.last() else {
        panic!("the spawn follows the object change");
    };
    assert_eq!(entity.id, state.world.object(DOOR).unwrap().key().local_id);
    assert!(spawns(&changes_of(&mut state, &moved_to(DOOR, 3, 0xA9B4_0032))).is_empty());
    let again = changes_of(
        &mut state,
        &described(DOOR, 2, "Door", 0x80, 0x1000, Some(0xA9B4_0032)),
    );
    assert_eq!(spawns(&again).len(), 1);
}

// OpenAC `OnLifecycle` and `OnLeavingWorld`: login complete once per stay;
// the leaving, announced by the runner before the teardown, interrupts the
// pending activations, then logoff, once. The arrival waits for the server
// to listen: entering the world alone is not it.
#[test]
fn entering_and_leaving_the_world_are_followed() {
    let mut state = in_world();
    // The navigation snapshot and the experience, published on their own,
    // are not this case.
    let follow = |state: &mut SessionState, in_world, listening| {
        let mut events = events::follow(state, in_world, listening);
        events.retain(|e| {
            !matches!(
                e,
                Event::NavigationSnapshotChanged(_) | Event::ExperienceChanged(_)
            )
        });
        events
    };
    assert!(follow(&mut state, true, false).is_empty());
    assert!(!events::in_stay(&state));
    assert_eq!(follow(&mut state, true, true), [Event::LoginComplete]);
    assert!(events::in_stay(&state));
    assert!(follow(&mut state, true, true).is_empty());
    state.plugin_services.activations.started(0x7000_0005);
    // Leaving the phase is not the leaving: the runner announces it.
    assert!(follow(&mut state, false, false).is_empty());
    let left = events::leave(&mut state);
    assert!(matches!(
        left.as_slice(),
        [Event::ActivationCompleted(done), Event::Logoff, Event::ExperienceChanged(None)]
            if done.object_id == 0x7000_0005 && done.outcome == ActivationOutcome::Interrupted
    ));
    assert!(!events::in_stay(&state));
    assert!(events::leave(&mut state).is_empty());
    // The next stay (a logoff straight into the next character).
    assert_eq!(follow(&mut state, true, true), [Event::LoginComplete]);
}

// The plugins' first tick in a stay: a tick before the
// arrival is not one, the first after it is, and the leaving forgets it, so
// the next stay waits for its own.
#[test]
fn the_first_tick_after_the_arrival_is_followed_per_stay() {
    let mut state = in_world();
    events::note_ticked(&mut state);
    assert!(!events::ticked_in_stay(&state));
    let _ = events::follow(&mut state, true, true);
    assert!(!events::ticked_in_stay(&state));
    events::note_ticked(&mut state);
    assert!(events::ticked_in_stay(&state));
    let _ = events::leave(&mut state);
    assert!(!events::ticked_in_stay(&state));
    let _ = events::follow(&mut state, true, true);
    assert!(!events::ticked_in_stay(&state));
    events::note_ticked(&mut state);
    assert!(events::ticked_in_stay(&state));
}

// OpenAC `OnPortal`: one transition per changed snapshot; its completion
// completes pending activations. A death and the server's question pass
// through.
#[test]
fn portals_deaths_and_questions_reach_plugins() {
    use crate::world::transit::{PortalKind, PortalSnapshot};
    let mut state = in_world();
    state.plugin_services.activations.started(0x7000_0009);
    let snapshot = PortalSnapshot {
        generation: 3,
        kind: PortalKind::Portal,
        completed: true,
        ..PortalSnapshot::IDLE
    };
    let host = [
        HostEvent::Portal(snapshot),
        HostEvent::Portal(snapshot),
        HostEvent::Gameplay(Box::new(crate::gameplay::GameplayEvent::LocalPlayerDied(
            "You died.".into(),
        ))),
        HostEvent::Confirmation(crate::communication::ClientConfirmation::Server {
            confirmation_type: 5,
            context_id: 77,
            message: "Join?".into(),
        }),
    ];
    let heard = events::host_events(&mut state, &host);
    assert!(matches!(
        heard.as_slice(),
        [
            Event::PortalTransition(t),
            Event::ActivationCompleted(a),
            Event::LocalPlayerDied(m),
            Event::ConfirmationRequested(c),
        ] if t.generation == 3 && t.kind == 2 && t.is_completed && t.revision == 1
            && a.outcome == ActivationOutcome::Completed
            && m == "You died."
            && c.context_id == 77 && c.kind == 5 && c.text == "Join?"
    ));
}

// OpenAC `RuntimeWorldObjectProjection.Project` and `CaptureObjects`:
// classes from the type and flags, sorted by id; identify needs a known
// object.
#[test]
fn world_objects_are_projected_with_their_class_in_id_order() {
    const PACK_ITEM: u32 = 0x8000_0001;
    let mut state = in_world();
    state.world.apply(&described(
        0x7000_0002,
        1,
        "Drudge",
        0x10,
        0x10,
        Some(0xA9B4_0031),
    ));
    state.world.apply(&described(
        0x7000_0003,
        1,
        "Tumerok",
        0x10,
        0x0,
        Some(0xA9B4_0031),
    ));
    state
        .world
        .apply(&described(PACK_ITEM, 1, "Gem", 0x800, 0, None));
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let objects = call::<calls::WorldObjects>(b, &());
        let class = |id: u32| {
            objects
                .iter()
                .find(|o| o.object_id == id)
                .map(|o| o.object_class)
        };
        assert_eq!(class(0x7000_0002), Some(ObjectClass::Monster));
        assert_eq!(class(0x7000_0003), Some(ObjectClass::Npc));
        assert_eq!(class(PACK_ITEM), Some(ObjectClass::Gem));
        let ids: Vec<u32> = objects.iter().map(|o| o.object_id).collect();
        let mut sorted = ids.clone();
        sorted.sort_unstable();
        assert_eq!(ids, sorted);
        assert_eq!(call::<calls::WorldObject>(b, &0), None);
        assert_eq!(
            call::<calls::WorldIdentify>(b, &0x9999).status,
            ac_plugin::abi::items::ItemStatus::InvalidItem
        );
    });
}

// ── The character's experience (OpenAC v0.1.25,
// `RuntimeAutomationSurfaceExperienceTests`) ──────────────────────────────

mod experience {
    use ac_plugin::abi::Event;
    use ac_plugin::abi::calls;
    use ac_plugin::abi::character::CharacterExperience;
    use ac_proto::Inbound;

    use super::super::events;
    use super::super::tests::{PLAYER, call, with_binding};
    use crate::communication::CommunicationPaths;
    use crate::gameplay::GameTables;
    use crate::session::{Outbox, SessionState};

    /// A short level curve: level 3 is the top of the table.
    const LEVELS: [u64; 4] = [0, 1_000, 3_000, 6_000];

    /// In the world, the server listening, before the description.
    fn world(levels: bool) -> SessionState {
        let mut state = SessionState::new(CommunicationPaths::default());
        let mut tables = GameTables::empty();
        if levels {
            tables = tables.with_experience_levels(LEVELS.to_vec());
        }
        state.install_tables(std::sync::Arc::new(tables), "account");
        state.world.set_player(PLAYER);
        heard(&mut state);
        state
    }

    /// The PlayerDescription's properties, as login applies them.
    fn describe(
        state: &mut SessionState,
        level: i32,
        total: i64,
        unassigned: i64,
        lum: (i64, i64),
    ) {
        let mut d = crate::world::tests::empty_description();
        d.properties.ints.insert(25, level);
        d.properties.int64s.insert(1, total);
        d.properties.int64s.insert(2, unassigned);
        d.properties.int64s.insert(6, lum.0);
        d.properties.int64s.insert(7, lum.1);
        state.world.apply_player_description(&d);
    }

    fn int64(state: &mut SessionState, property: u32, value: i64) {
        state.world.apply(&Inbound::PrivateUpdatePropertyInt64(
            ac_proto::PrivateUpdatePropertyInt64 {
                sequence: 0,
                property,
                value,
            },
        ));
    }

    fn level(state: &mut SessionState, value: i32) {
        state.world.apply(&Inbound::PrivateUpdatePropertyInt(
            ac_proto::PrivateUpdatePropertyInt {
                sequence: 0,
                property: 25,
                value,
            },
        ));
    }

    /// The experience events of one pass.
    fn heard(state: &mut SessionState) -> Vec<Option<CharacterExperience>> {
        events::follow(state, true, true)
            .into_iter()
            .filter_map(|e| match e {
                Event::ExperienceChanged(x) => Some(x),
                _ => None,
            })
            .collect()
    }

    fn read(state: &mut SessionState) -> Option<CharacterExperience> {
        let mut outbox = Outbox::new();
        with_binding(state, &mut outbox, |b| {
            call::<calls::CharacterExperience>(b, &())
        })
    }

    #[test]
    fn nothing_is_reported_before_the_description_arrives() {
        let mut state = world(true);
        assert_eq!(read(&mut state), None);
        // An update on its own is not the description.
        int64(&mut state, 1, 500);
        assert!(heard(&mut state).is_empty());
        assert_eq!(read(&mut state), None);
    }

    #[test]
    fn the_description_and_later_updates_are_reported() {
        let mut state = world(true);
        describe(&mut state, 1, 1_400, 150, (20, 1_000));
        let expected = CharacterExperience {
            total: 1_400,
            unassigned: 150,
            to_next_level: Some(1_600),
            available_luminance: 20,
            maximum_luminance: 1_000,
        };
        assert_eq!(heard(&mut state), [Some(expected.clone())]);
        assert_eq!(read(&mut state), Some(expected));
        int64(&mut state, 1, 1_900);
        assert_eq!(heard(&mut state).len(), 1);
        int64(&mut state, 2, 650);
        assert_eq!(heard(&mut state).len(), 1);
        int64(&mut state, 6, 35);
        assert_eq!(
            heard(&mut state),
            [Some(CharacterExperience {
                total: 1_900,
                unassigned: 650,
                to_next_level: Some(1_100),
                available_luminance: 35,
                maximum_luminance: 1_000,
            })]
        );
    }

    #[test]
    fn a_level_up_moves_the_distance_to_the_next_level() {
        let mut state = world(true);
        describe(&mut state, 1, 2_500, 0, (0, 0));
        let to_next = |state: &mut SessionState| {
            heard(state)
                .last()
                .cloned()
                .flatten()
                .and_then(|x| x.to_next_level)
        };
        assert_eq!(to_next(&mut state), Some(500));
        // The total arrives before the level.
        int64(&mut state, 1, 3_200);
        assert_eq!(to_next(&mut state), Some(0));
        level(&mut state, 2);
        assert_eq!(to_next(&mut state), Some(2_800));
    }

    #[test]
    fn the_top_of_the_table_or_no_table_has_no_next_level() {
        let mut state = world(true);
        describe(&mut state, 3, 7_000, 0, (0, 0));
        let x = read(&mut state).expect("described");
        assert_eq!((x.total, x.to_next_level), (7_000, None));
        let mut state = world(false);
        describe(&mut state, 1, 400, 0, (0, 0));
        let x = read(&mut state).expect("described");
        assert_eq!((x.total, x.to_next_level), (400, None));
    }

    #[test]
    fn the_event_is_raised_once_per_real_change_only() {
        let mut state = world(true);
        describe(&mut state, 1, 400, 0, (0, 0));
        assert_eq!(heard(&mut state).len(), 1);
        state
            .world
            .apply(&Inbound::PrivateUpdateSkill(ac_proto::PrivateUpdateSkill {
                sequence: 0,
                skill: 6,
                ranks: 10,
                adjust_pp: 1,
                advancement_class: 2,
                xp: 0,
                init: 10,
                resistance: 0,
                last_used: 0.0,
            }));
        int64(&mut state, 3, 9); // augmentation cost
        int64(&mut state, 1, 400); // the same total again
        level(&mut state, 1);
        assert!(heard(&mut state).is_empty());
        int64(&mut state, 1, 401);
        assert_eq!(heard(&mut state).len(), 1);
    }

    #[test]
    fn leaving_the_world_resets_until_the_next_description() {
        // TheLogoffEdgeResetsBeforeTheCharacterIsCleared and
        // LeavingTheWorldResetsUntilTheNextDescription: the leaving is
        // heard while the session is whole, then the generation ends.
        let mut state = world(true);
        describe(&mut state, 1, 400, 0, (10, 100));
        assert_eq!(heard(&mut state).len(), 1);
        let left = events::leave(&mut state);
        assert_eq!(left.last(), Some(&Event::ExperienceChanged(None)));
        assert_eq!(read(&mut state), None);
        state.world.reset_session();
        state.world.set_player(PLAYER);
        int64(&mut state, 1, 999);
        assert!(heard(&mut state).is_empty());
        assert_eq!(read(&mut state), None);
        describe(&mut state, 2, 1_200, 0, (0, 0));
        let x = heard(&mut state);
        assert_eq!(x.len(), 1);
        assert_eq!(x[0].as_ref().map(|x| x.total), Some(1_200));
    }
}

// OpenAC `ExternalContainerController.OnObjectRemoved` (`ApplyClose`)
// heard through `RuntimeAutomationSurface.OnExternalContainerChanged`:
// the open corpse leaving the world reaches the plugins as its close.
#[test]
fn a_plugin_hears_the_open_corpse_close_when_it_leaves_the_world() {
    use ac_core::items::PublicWeenieFlags;
    use ac_proto::{GameEventType, Inbound};

    use crate::inventory::{ItemCommand, ObjectRef};
    use crate::session::SessionCommand;
    use crate::world::tests::game_event;

    const CORPSE: u32 = 0x7000_0C01;
    const CONTAINER_TYPE: u32 = 0x200;
    let clocks = super::tests::CLOCKS;
    let mut state = in_world();
    let mut outbox = Outbox::new();
    let flags = (PublicWeenieFlags::CORPSE | PublicWeenieFlags::OPENABLE).bits();
    state.apply(
        &described(CORPSE, 1, "Corpse of Drudge", CONTAINER_TYPE, flags, None),
        &mut outbox,
        true,
        clocks,
    );
    events::follow(&mut state, true, true);
    state.command(
        SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(CORPSE))),
        &mut outbox,
        true,
        clocks,
    );
    for (event, payload) in [
        (GameEventType::VIEW_CONTENTS, &[CORPSE, 0][..]),
        (GameEventType::USE_DONE, &[0][..]),
    ] {
        let body = game_event(event, payload);
        state.apply(&Inbound::parse(&body).unwrap(), &mut outbox, true, clocks);
    }
    let opened = events::follow(&mut state, true, true);
    assert!(
        opened.contains(&Event::ContainerOpened(CORPSE)),
        "{opened:?}"
    );
    state.apply(
        &Inbound::DeleteObject(ac_proto::DeleteObject {
            guid: CORPSE,
            instance_sequence: 1,
        }),
        &mut outbox,
        true,
        clocks,
    );
    let closed = events::follow(&mut state, true, true);
    assert!(
        closed.contains(&Event::ContainerClosed(CORPSE)),
        "{closed:?}"
    );
    assert_eq!(
        state.inventory.external_containers().current_container_id(),
        0
    );
}

// OpenAC `SelectionState.Reset` on leaving the world: the window hears the
// reset (its examination window closes) and the plugins hear the
// selection go, from what was selected to nothing.
#[test]
fn leaving_the_world_tells_the_window_and_the_plugins_the_selection_went() {
    use crate::gameplay::{GameplayCommand, GameplayEvent};
    use crate::inventory::ObjectRef;
    use crate::session::SessionCommand;
    const DRUDGE: u32 = 0x8000_0D01;
    let clocks = super::tests::CLOCKS;
    let mut state = in_world();
    let mut outbox = Outbox::new();
    state.apply(
        &described(DRUDGE, 1, "Drudge", 0x10, 0, None),
        &mut outbox,
        true,
        clocks,
    );
    let _ = events::follow(&mut state, true, true);
    state.command(
        SessionCommand::Gameplay(GameplayCommand::Select(ObjectRef::Guid(DRUDGE))),
        &mut outbox,
        true,
        clocks,
    );
    let selected = |events: &[Event]| -> Vec<(Option<u32>, Option<u32>)> {
        events
            .iter()
            .filter_map(|e| match e {
                Event::SelectionChanged(c) => Some((c.previous, c.selected)),
                _ => None,
            })
            .collect()
    };
    assert_eq!(
        selected(&events::follow(&mut state, true, true)),
        [(None, Some(DRUDGE))]
    );
    let _ = state.drain_host_events();
    state.left_world(0);
    let told = state.drain_host_events();
    assert!(
        told.iter().any(|e| matches!(
            e,
            HostEvent::Gameplay(g) if **g == GameplayEvent::Selection { selected: None, name: None }
        )),
        "{told:?}"
    );
    assert_eq!(
        selected(&events::follow(&mut state, false, false)),
        [(Some(DRUDGE), None)]
    );
}
