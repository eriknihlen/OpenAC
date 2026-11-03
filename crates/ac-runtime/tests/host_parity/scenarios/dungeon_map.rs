//! A plugin asking where the character is and what shape the place has
//! (OpenAC `Scenarios/DungeonMapParityTests.cs`): the landblock comes from
//! the character's place, and with no game files in either arm the sealed
//! question, the plan, the indoor cells and a reach search are answered the
//! same inert way.

use ac_plugin::abi::calls;

use crate::host_parity::arm::run;
use crate::host_parity::world;

// OpenAC `DungeonMapParityTests.TheDungeonMapIsAnsweredTheSameOnBothClients`.
#[test]
fn the_dungeon_map_is_answered_the_same_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);

        transcript.step("the landblock the character stands in");
        let landblock = arm.call::<calls::DungeonLandblock>(&());
        transcript.record_id("landblock", landblock);
        assert_eq!(landblock, world::LANDBLOCK);

        transcript.step("a sealed cell, without the files to say");
        let sealed = arm.call::<calls::DungeonIsSealed>(&world::CELL);
        transcript.record("sealed", sealed);
        assert!(!sealed);

        transcript.step("the plan, without the files to read");
        let plan = arm.call::<calls::DungeonFloorplan>(&landblock);
        transcript.record("empty", plan.is_none());
        transcript.record("layers", plan.as_ref().map_or(0, |p| p.layers.len()));
        assert!(plan.is_none());

        transcript.step("the indoor cells, without the files to read");
        let cells = arm.call::<calls::DungeonIndoorCells>(&landblock);
        transcript.record("cells", cells.len());
        assert!(cells.is_empty());

        transcript.step("a reach search, with nothing to search from");
        let handle = arm.call::<calls::NavigationReachableCells>(
            &ac_plugin::abi::navigation::ReachRequest { max_meters: 50.0 },
        );
        transcript.record("handle", handle);
        assert_eq!(handle, 0);
    });
}
