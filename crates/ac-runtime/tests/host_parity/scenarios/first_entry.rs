//! Login complete waits for the character's first entry into the world
//! (OpenAC's no-window host: `HeadlessSessionEventRoute` sends login
//! complete and the house query from the first-entry drive's completion,
//! `RuntimeFirstEntryDriveController`'s local-player callback). A character
//! the server creates where its placement finds no clear spot waits out of
//! the world, placed again each frame, and the server does not hear it is
//! logged in. Neither arm's window presents the login, so both take that
//! path.

use std::time::Duration;

use ac_plugin::abi::Event;

use crate::host_parity::arm::{Setup, run_with};
use crate::host_parity::server::Outbound;

/// Inside the Holtburg building 0x01000BC3 (31, 159) the placement finds
/// no clear spot within 4 m.
const NOWHERE: (f32, f32) = (31.0, 159.0);

/// The login complete action.
const LOGIN_COMPLETE: u32 = 0x00A1;
/// The house query action.
const HOUSE_QUERY: u32 = 0x021E;

fn sent(outbound: &[Outbound], action: u32) -> bool {
    outbound.iter().any(|m| m.game_action() == Some(action))
}

#[test]
#[ignore = "needs the retail dats: a body that enters the world"]
fn a_character_with_no_clear_spot_to_enter_at_is_not_logged_in() {
    let setup = Setup {
        dats: true,
        ..Setup::default()
    };
    run_with(&setup, &[], false, |arm, transcript| {
        arm.arrive_at(NOWHERE.0, NOWHERE.1);
        arm.wait(Duration::from_millis(1500));
        let outbound = arm.outbound();
        let login_complete = sent(&outbound, LOGIN_COMPLETE);
        let house_query = sent(&outbound, HOUSE_QUERY);
        let arrived = arm
            .parity_events()
            .any(|e| matches!(e, Event::LoginComplete));
        transcript.record("login complete sent", login_complete);
        transcript.record("house query sent", house_query);
        transcript.record("plugins heard the arrival", arrived);
        assert!(
            !login_complete && !house_query,
            "{}: the character never entered, yet the server heard {outbound:#?}",
            arm.name()
        );
        assert!(!arrived, "{}: the plugins heard an arrival", arm.name());
    });
}

/// Where the teleport puts the character: the same landblock, 40 m off,
/// on open ground.
const DESTINATION: (f32, f32) = (56.0, 57.0);

fn count(outbound: &[Outbound], action: u32) -> usize {
    outbound
        .iter()
        .filter(|m| m.game_action() == Some(action))
        .count()
}

/// A teleport the server starts before the client said its login is
/// complete (an admin's command typed at once; here heard ahead of the
/// character's own object, so the entry is surely still under way): login
/// complete waits for the teleport's arrival and goes once, with the house
/// query. Sent when the entry is done instead, it would tell the server
/// the teleport ended while the character still stood where it left (the
/// next position sent being that old spot), and the arrival would send a
/// second one.
#[test]
#[ignore = "needs the retail dats: a body that enters the world"]
fn a_teleport_during_the_entry_sends_login_complete_once_on_arrival() {
    use crate::host_parity::{dats, wire, world};
    let setup = Setup {
        dats: true,
        ..Setup::default()
    };
    run_with(&setup, &[], false, |arm, transcript| {
        arm.server.send(&wire::player_teleport(1));
        arm.sync();
        arm.arrive_at(world::PLAYER_X, world::PLAYER_Y);
        arm.sync();
        arm.wait(Duration::from_millis(1500));
        let before = arm.outbound();
        let early = count(&before, LOGIN_COMPLETE);
        transcript.record("login complete before the arrival", early);
        assert_eq!(
            early,
            0,
            "{}: login complete went before the teleport arrived: {before:#?}",
            arm.name()
        );

        let (x, y) = DESTINATION;
        let cell = world::outdoor_cell(world::LANDBLOCK, x, y);
        let z = dats::ground_z(world::LANDBLOCK, x, y);
        arm.server.send(&wire::update_position(
            world::PLAYER,
            (cell, x, y, z),
            1,
            2,
            1,
            0,
        ));
        arm.until("the plugins hear the arrival", |a| {
            a.parity_events().any(|e| matches!(e, Event::LoginComplete))
        });
        arm.wait(Duration::from_millis(500));
        let after = arm.outbound();
        let login_complete = count(&after, LOGIN_COMPLETE);
        let house_query = count(&after, HOUSE_QUERY);
        transcript.record("login complete at the arrival", login_complete);
        transcript.record("house query at the arrival", house_query);
        assert_eq!(
            (login_complete, house_query),
            (1, 1),
            "{}: one login complete and one house query at the arrival: {after:#?}",
            arm.name()
        );
    });
}
