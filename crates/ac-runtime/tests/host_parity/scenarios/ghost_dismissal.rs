//! A plugin letting go of an object the server stopped talking about
//! (OpenAC `Scenarios/GhostDismissalParityTests.cs`): dismissed once it is
//! dropped, and only it; a second time, the character itself and nothing at
//! all are refused.

use ac_plugin::abi::calls;
use ac_plugin::abi::combat::CombatStatus;

use crate::host_parity::arm::{Arm, run};
use crate::host_parity::transcript::Transcript;
use crate::host_parity::world;

fn known(arm: &mut Arm, id: u32) -> bool {
    arm.call::<calls::NavigationObject>(&id).is_some()
}

fn record(transcript: &mut Transcript, arm: &mut Arm, id: u32) {
    let known = known(arm, id);
    transcript.record(&format!("known[0x{id:08X}]"), known);
}

// OpenAC `GhostDismissalParityTests.DismissingAGhostAnswersTheSameAndDropsTheSameObject`.
#[test]
fn dismissing_a_ghost_answers_the_same_and_drops_the_same_object() {
    run(|arm, transcript| {
        world::stage(arm);

        transcript.step("before");
        record(transcript, arm, world::MONSTER);
        record(transcript, arm, world::SECOND_MONSTER);

        transcript.step("dismiss the monster");
        let dropped = arm.call::<calls::CombatDismissGhost>(&world::MONSTER);
        transcript.record("status", dropped.status);
        arm.sync();
        assert_eq!(dropped.status, CombatStatus::Stopped, "{}", arm.name());
        assert!(
            !known(arm, world::MONSTER),
            "{}: the dismissed object is still known",
            arm.name()
        );
        assert!(
            known(arm, world::SECOND_MONSTER),
            "{}: dismissing one object took another with it",
            arm.name()
        );
        record(transcript, arm, world::MONSTER);
        record(transcript, arm, world::SECOND_MONSTER);

        transcript.step("dismiss it a second time");
        let again = arm.call::<calls::CombatDismissGhost>(&world::MONSTER);
        transcript.record("status", again.status);
        assert_eq!(again.status, CombatStatus::InvalidTarget);
        record(transcript, arm, world::MONSTER);

        transcript.step("dismiss the character itself");
        let itself = arm.call::<calls::CombatDismissGhost>(&world::PLAYER);
        transcript.record("status", itself.status);
        assert_eq!(itself.status, CombatStatus::InvalidTarget);
        // The character is a navigation object only with a body (the dats);
        // that it is still in the world is what says it was not dismissed.
        let still = arm.call::<calls::WorldObject>(&world::PLAYER).is_some();
        transcript.record("character known", still);
        assert!(still, "{}: the character dismissed itself", arm.name());

        transcript.step("dismiss nothing at all");
        let nothing = arm.call::<calls::CombatDismissGhost>(&0);
        transcript.record("status", nothing.status);
        arm.record_outbound(transcript);
    });
}
