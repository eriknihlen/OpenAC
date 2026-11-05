//! The world as a plugin lists it (OpenAC
//! `Scenarios/WorldObjectListParityTests.cs`): what the server sent is
//! listed, a create, delete and create again reads the same, an item going
//! into a pack leaves the list, a late watcher is replayed the world, and the
//! landscape's decoration is not in the list.
//!
//! OpenAC writes its newcomer and the carried item straight into the
//! runtime's tables; here the server says them. A plugin joining late is
//! the relay reloaded (`/plugin reload`): this API replays the world to a
//! plugin when it is enabled.

use ac_plugin::abi::Event;
use ac_plugin::abi::calls;
use ac_plugin::abi::world::EntitySnapshot;

use crate::host_parity::arm::{Arm, run};
use crate::host_parity::transcript::Transcript;
use crate::host_parity::wire::{self, Object};
use crate::host_parity::world;

const NEWCOMER: u32 = 0x5000_0031;
const GROUND_ITEM: u32 = 0x5000_0032;

fn entities(arm: &mut Arm) -> Vec<EntitySnapshot> {
    arm.call::<calls::WorldEntities>(&())
}

/// The local id the list gives the object the server calls `guid` (0 when
/// it is not listed).
fn local_id(arm: &mut Arm, guid: u32) -> u32 {
    entities(arm)
        .iter()
        .find(|e| e.source_id == guid)
        .map_or(0, |e| e.id)
}

fn record_presence(transcript: &mut Transcript, arm: &mut Arm, guid: u32) {
    let id = local_id(arm, guid);
    transcript.record("id", id);
    transcript.record("listed", id != 0);
    transcript.record("count", entities(arm).len());
}

fn record_list(transcript: &mut Transcript, arm: &mut Arm) {
    let list = entities(arm);
    transcript.record("count", list.len());
    let mut ids: Vec<u32> = list.iter().map(|e| e.id).collect();
    ids.sort_unstable();
    transcript.record("ids", &ids);
    let mut sources: Vec<(u32, u32)> = list.iter().map(|e| (e.id, e.source_id)).collect();
    sources.sort_unstable();
    transcript.record("sources", &sources);
}

fn spawned(events: &[Event]) -> Vec<u32> {
    events
        .iter()
        .filter_map(|e| match e {
            Event::EntitySpawned(s) => Some(s.id),
            _ => None,
        })
        .collect()
}

fn ground_item(x: f32, instance: u16) -> Object {
    Object {
        instance_seq: instance,
        ..world::on_the_ground(GROUND_ITEM, "ground item", world::GEM, x)
    }
}

// OpenAC `WorldObjectListParityTests.AnObjectTheServerSentIsInTheListOnBothClients`.
#[test]
fn an_object_the_server_sent_is_in_the_list_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        transcript.step("the world as a plugin reads it");
        record_list(transcript, arm);
        assert_ne!(local_id(arm, world::MONSTER), 0, "{}", arm.name());
    });
}

// OpenAC `WorldObjectListParityTests.CreateDeleteAndCreateAgainReadTheSameOnBothClients`.
#[test]
fn create_delete_and_create_again_read_the_same_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        let _ = arm.events();

        transcript.step("a creature appears");
        arm.server
            .send(&world::monster(NEWCOMER, world::PLAYER_X + 5.0).build());
        arm.sync();
        let first = local_id(arm, NEWCOMER);
        record_presence(transcript, arm, NEWCOMER);
        let announced = spawned(&arm.events());
        transcript.record("announced", &announced);
        assert!(announced.contains(&first), "{}", arm.name());

        transcript.step("the server takes it out of the world");
        arm.server.send(&wire::delete_object(NEWCOMER, 1));
        arm.sync();
        record_presence(transcript, arm, NEWCOMER);
        transcript.record("announced", spawned(&arm.events()));
        assert!(!entities(arm).iter().any(|e| e.id == first));

        transcript.step("the server sends it again");
        let again = Object {
            instance_seq: 2,
            ..world::monster(NEWCOMER, world::PLAYER_X + 6.0)
        };
        arm.server.send(&again.build());
        arm.sync();
        record_presence(transcript, arm, NEWCOMER);
        let announced = spawned(&arm.events());
        transcript.record("announced", &announced);
        let id = local_id(arm, NEWCOMER);
        assert_ne!(id, 0, "{}", arm.name());
        assert_eq!(announced.last(), Some(&id));
    });
}

// OpenAC `WorldObjectListParityTests.AnItemGoingIntoAPackLeavesTheListOnBothClients`.
#[test]
fn an_item_going_into_a_pack_leaves_the_list_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        world::stage_carried_items(arm);
        arm.server
            .send(&ground_item(world::PLAYER_X + 1.0, 1).build());
        arm.sync();

        transcript.step("it is lying on the ground");
        record_presence(transcript, arm, GROUND_ITEM);
        assert_ne!(local_id(arm, GROUND_ITEM), 0, "{}", arm.name());

        transcript.step("it goes into a pack");
        // The server takes the thing out of the world and describes it as
        // carried, which is what carrying is.
        arm.server.send(&wire::delete_object(GROUND_ITEM, 1));
        let carried = Object {
            position: None,
            setup: None,
            container: Some(world::PLAYER),
            ..ground_item(0.0, 1)
        };
        arm.server.send(&carried.build_update());
        arm.sync();
        record_presence(transcript, arm, GROUND_ITEM);
        let owned = arm
            .call::<calls::ItemsOwned>(&())
            .iter()
            .any(|i| i.object_id == GROUND_ITEM);
        transcript.record("carried", owned);
        assert_eq!(local_id(arm, GROUND_ITEM), 0, "{}", arm.name());
    });
}

// OpenAC `WorldObjectListParityTests.ALateWatcherIsReplayedTheSameWorldOnBothClients`:
// a plugin starting to watch late is the relay reloaded once the world is
// there; a plugin enabled hears every object already there.
#[test]
fn a_late_watcher_is_replayed_the_same_world_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);

        transcript.step("a plugin starts watching late");
        arm.reload_relay();
        let replayed = spawned(&arm.events());
        let mut told = replayed.clone();
        told.sort_unstable();
        let mut listed: Vec<u32> = entities(arm).iter().map(|e| e.id).collect();
        listed.sort_unstable();
        transcript.record("replayed.count", replayed.len());
        transcript.record("replayed", &told);
        let mut distinct = told.clone();
        distinct.dedup();
        assert_eq!(told, listed, "{}", arm.name());
        assert_eq!(distinct.len(), told.len());

        transcript.step("then something new appears");
        let _ = arm.events();
        arm.server
            .send(&world::monster(NEWCOMER, world::PLAYER_X + 5.0).build());
        arm.sync();
        let announced = spawned(&arm.events());
        transcript.record("announced", &announced);
        assert_eq!(announced.last(), Some(&local_id(arm, NEWCOMER)));
    });
}

// OpenAC `WorldObjectListParityTests.TheDecorationOfTheLandscapeIsNotInTheObjectList`.
#[test]
fn the_decoration_of_the_landscape_is_not_in_the_object_list() {
    run(|arm, transcript| {
        world::stage(arm);
        transcript.step("scenery");
        let scenery = arm.call::<calls::WorldScenery>(&());
        transcript.record("scenery.count", scenery.len());
        assert!(scenery.is_empty());
    });
}
