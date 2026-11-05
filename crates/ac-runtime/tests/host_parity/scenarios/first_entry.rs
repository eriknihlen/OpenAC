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
