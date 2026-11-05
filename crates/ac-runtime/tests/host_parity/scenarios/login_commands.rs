//! The session document's login commands against the plugins' arrival.
//! OpenAC's plugins arrive when the enter-world
//! request goes out and its login commands wait for the server to listen,
//! a round trip later, so a plugin has heard the arrival and ticked in the
//! world before a login command reaches it. Here the plugins arrive when
//! the server listens, and the commands wait for their first tick after
//! that: a command a plugin answers never comes before what the plugin
//! does on arriving (live, 2026-10-15: MossTank's first tick in the world
//! cleared a variable the first login command had just set).

use ac_plugin::abi::{Event, Wire, calls};

use crate::host_parity::arm::{Arm, PARITY, Setup, call_line, run_with};

/// The login command's call, numbered clear of the script's own.
const SEQ: u32 = 9000;

/// Where the relay logged the first line that `is` picks out.
fn logged_at(arm: &Arm, what: &str, is: impl Fn(&str) -> bool) -> usize {
    arm.logs
        .iter()
        .position(|(p, m)| p == PARITY && is(m))
        .unwrap_or_else(|| panic!("{}: the relay never logged {what}", arm.name()))
}

#[test]
fn a_login_command_reaches_a_plugin_after_its_first_tick_in_the_world() {
    let setup = Setup {
        login_commands: vec![call_line::<calls::HostSettings>(PARITY, SEQ, &())],
        ..Setup::default()
    };
    run_with(&setup, &[], false, |arm, transcript| {
        arm.enter_world();
        let reply = format!("parity reply {SEQ} ");
        arm.until("the login command is answered", |a| {
            a.logs
                .iter()
                .any(|(p, m)| p == PARITY && m.starts_with(&reply))
        });
        let arrival: String = Event::LoginComplete
            .to_bytes()
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect();
        let arrival = logged_at(arm, "the arrival", |m| {
            m.strip_prefix("parity event ") == Some(arrival.as_str())
        });
        let ticked = logged_at(arm, "a tick after the arrival", |m| {
            m == "parity tick after arrival"
        });
        let answered = logged_at(arm, "the login command's answer", |m| m.starts_with(&reply));
        transcript.record("tick after the arrival", arrival < ticked);
        transcript.record("command after the tick", ticked < answered);
        assert!(
            arrival < ticked && ticked < answered,
            "{}: the arrival at {arrival}, the tick at {ticked}, the login command at {answered}",
            arm.name()
        );
    });
}
