//! What a plugin is told, on both arms (OpenAC
//! `Scenarios/PluginEventParityTests.cs`): the arrival, an object coming
//! and going (and sent again), something entering the packs, a description
//! coming back, a corpse opening and closing, a death and a question.

use ac_plugin::abi::Event;
use ac_plugin::abi::calls;
use ac_plugin::abi::items::ItemStatus;
use ac_plugin::abi::world::{ObjectChange, ObjectChangeKind};

use crate::host_parity::arm::{Arm, PACING, run, run_from_login};
use crate::host_parity::transcript::Transcript;
use crate::host_parity::wire::{self, Object};
use crate::host_parity::world;

const ARRIVAL: u32 = 0x5000_0060;

fn changes(events: &[Event]) -> Vec<ObjectChange> {
    events
        .iter()
        .filter_map(|e| match e {
            Event::ObjectChanged(change) => Some(change.clone()),
            _ => None,
        })
        .collect()
}

fn record(transcript: &mut Transcript, key: &str, changes: &[ObjectChange]) {
    transcript.record(&format!("{key}.count"), changes.len());
    for (index, change) in changes.iter().enumerate() {
        transcript.record_id(&format!("{key}[{index}].id"), change.object_id);
        transcript.record(&format!("{key}[{index}].kind"), change.kind);
    }
}

fn has(changes: &[ObjectChange], id: u32, kind: ObjectChangeKind) -> bool {
    changes.iter().any(|c| c.object_id == id && c.kind == kind)
}

fn arrival(instance: u16) -> Object {
    Object {
        instance_seq: instance,
        ..world::on_the_ground(
            ARRIVAL,
            &format!("{ARRIVAL:08X}"),
            world::MISC,
            world::PLAYER_X + 4.0,
        )
    }
}

// OpenAC `PluginEventParityTests.ArrivingInTheWorldTellsAPluginOnBothClients`.
#[test]
fn arriving_in_the_world_tells_a_plugin_on_both_clients() {
    run_from_login(|arm, transcript| {
        let arrivals = |arm: &mut Arm| {
            arm.events()
                .iter()
                .filter(|e| matches!(e, Event::LoginComplete))
                .count()
        };
        transcript.step("not in yet");
        let before = arrivals(arm);
        transcript.record("arrivals", before);
        assert_eq!(before, 0);

        transcript.step("the server lets the character in");
        arm.enter_world();
        let after = arrivals(arm);
        transcript.record("arrivals", after);
        // Exactly once: a plugin that is told twice starts twice.
        assert_eq!(after, 1, "{}", arm.name());
    });
}

// OpenAC `PluginEventParityTests.AnObjectArrivingAndLeavingTellsAPluginOnBothClients`.
#[test]
fn an_object_arriving_and_leaving_tells_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("something arrives");
        arm.server.send(&arrival(1).build());
        arm.sync();
        let arrived = changes(&arm.events());
        record(transcript, "arrived", &arrived);
        assert!(
            has(&arrived, ARRIVAL, ObjectChangeKind::Created),
            "{arrived:?}"
        );

        transcript.step("and goes away again");
        arm.server.send(&wire::delete_object(ARRIVAL, 1));
        arm.sync();
        let left = changes(&arm.events());
        record(transcript, "left", &left);
        assert!(has(&left, ARRIVAL, ObjectChangeKind::Released), "{left:?}");
    });
}

// OpenAC `PluginEventParityTests.ReSendingAnObjectReleasesAndCreatesItOnBothClients`.
#[test]
fn re_sending_an_object_releases_and_creates_it_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("something arrives");
        arm.server.send(&arrival(1).build());
        arm.sync();
        let _ = arm.events();

        transcript.step("the server sends it again");
        arm.server.send(&arrival(2).build());
        arm.sync();
        let resent = changes(&arm.events());
        record(transcript, "resent", &resent);
        let kinds: Vec<ObjectChangeKind> = resent
            .iter()
            .filter(|c| c.object_id == ARRIVAL)
            .map(|c| c.kind)
            .collect();
        // Released, then created: a handover, not a departure.
        assert_eq!(
            kinds,
            [ObjectChangeKind::Released, ObjectChangeKind::Created],
            "{}",
            arm.name()
        );
        let known = arm.call::<calls::WorldObject>(&ARRIVAL).is_some();
        transcript.record("known", known);
        assert!(known);
    });
}

// OpenAC `PluginEventParityTests.SomethingEnteringThePacksTellsAPluginOnBothClients`.
#[test]
fn something_entering_the_packs_tells_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("the packs are filled");
        world::stage_carried_items(arm);
        let all = arm.events();
        let carried = changes(&all);
        record(transcript, "carried", &carried);
        transcript.record("all", &all);
        assert!(
            has(&carried, world::KIT, ObjectChangeKind::Created),
            "{carried:?}"
        );
    });
}

// OpenAC `PluginEventParityTests.ADescriptionComingBackTellsAPluginOnBothClients`.
#[test]
fn a_description_coming_back_tells_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        world::stage_corpse(arm, world::WITHIN_ARMS_REACH);
        world::stage_corpse_contents(arm);
        let _ = arm.events();

        transcript.step("ask about the gem");
        let identify = arm.call::<calls::WorldIdentify>(&world::CORPSE_GEM);
        transcript.record("identify", &identify);
        assert_eq!(identify.status, ItemStatus::Started, "{}", arm.name());

        transcript.step("the server answers");
        let _ = arm.events();
        arm.server.game_event(
            world::PLAYER,
            0x00C9,
            &wire::appraisal_response(world::CORPSE_GEM, &[], true),
        );
        arm.sync();
        let identified = changes(&arm.events());
        record(transcript, "identified", &identified);
        assert!(
            has(
                &identified,
                world::CORPSE_GEM,
                ObjectChangeKind::IdentReceived
            ),
            "{identified:?}"
        );
    });
}

// OpenAC `PluginEventParityTests.ACorpseOpeningAndClosingTellsAPluginOnBothClients`.
#[test]
fn a_corpse_opening_and_closing_tells_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        world::stage_corpse(arm, world::WITHIN_ARMS_REACH);
        world::stage_corpse_contents(arm);
        let _ = arm.events();

        transcript.step("open it");
        let _ = arm.call::<calls::LootOpen>(&world::CORPSE);
        arm.server.game_event(
            world::PLAYER,
            0x0196,
            &wire::view_contents(
                world::CORPSE,
                &[(world::CORPSE_COIN, 0), (world::CORPSE_GEM, 0)],
            ),
        );
        arm.server
            .game_event(world::PLAYER, 0x01C7, &wire::payload(&[0]));
        arm.sync();
        let opened: Vec<u32> = arm
            .events()
            .iter()
            .filter_map(|e| match e {
                Event::ContainerOpened(id) => Some(*id),
                _ => None,
            })
            .collect();
        transcript.record("opened", &opened);
        assert!(
            opened.contains(&world::CORPSE),
            "{}: {opened:?}",
            arm.name()
        );

        transcript.step("close it");
        // The pacing between two uses has to lapse first.
        arm.wait(PACING);
        let close = arm.call::<calls::LootClose>(&world::CORPSE);
        transcript.record("close.status", close.status);
        arm.server
            .game_event(world::PLAYER, 0x0052, &wire::payload(&[world::CORPSE]));
        arm.sync();
        let closed: Vec<u32> = arm
            .events()
            .iter()
            .filter_map(|e| match e {
                Event::ContainerClosed(id) => Some(*id),
                _ => None,
            })
            .collect();
        transcript.record("closed", &closed);
        assert!(
            closed.contains(&world::CORPSE),
            "{}: {closed:?}",
            arm.name()
        );
    });
}

// OpenAC `PluginEventParityTests.DyingTellsAPluginOnBothClients`.
#[test]
fn dying_tells_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("the server says the character died");
        arm.server.game_event(
            world::PLAYER,
            0x01AC,
            &wire::text("You were slain by a Drudge Slinker!"),
        );
        arm.sync();
        let deaths: Vec<String> = arm
            .events()
            .into_iter()
            .filter_map(|e| match e {
                Event::LocalPlayerDied(text) => Some(text),
                _ => None,
            })
            .collect();
        transcript.record("deaths", &deaths);
        assert_eq!(
            deaths,
            ["You were slain by a Drudge Slinker!"],
            "{}",
            arm.name()
        );
    });
}

// OpenAC `PluginEventParityTests.BeingAskedSomethingTellsAPluginOnBothClients`.
// OpenAC raises the client's own question from where the client raises it;
// here the question comes from where this client takes one: the server's
// confirmation request.
#[test]
fn being_asked_something_tells_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("the player is asked");
        let mut payload = wire::payload(&[5, 77]);
        payload.extend(wire::text("Allow the swap?"));
        arm.server.game_event(world::PLAYER, 0x0274, &payload);
        arm.sync();
        let asked: Vec<_> = arm
            .events()
            .into_iter()
            .filter_map(|e| match e {
                Event::ConfirmationRequested(c) => Some(c),
                _ => None,
            })
            .collect();
        transcript.record("asked.count", asked.len());
        transcript.record("asked", &asked);
        // Exactly once: a question raised twice is answered twice.
        assert_eq!(asked.len(), 1, "{}", arm.name());
        assert_eq!(asked[0].context_id, 77);
        assert_eq!(asked[0].text, "Allow the swap?");
    });
}
