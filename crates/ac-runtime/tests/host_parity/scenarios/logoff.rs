//! A log off reaching a plugin before the session is torn down, and a log
//! off into the next character (OpenAC `Scenarios/LogoffParityTests.cs`).
//! What a plugin reads during the event is read inside it: the relay reacts
//! to the event with the call.
//!
//! OpenAC completes the log off on the runtime directly; here the plugin
//! asks for it (`login_logout`), the client sends its log off and the server
//! confirms it, which is how a log off to the character list happens.

use ac_plugin::abi::calls;
use ac_plugin::abi::items::InventoryItem;

use crate::host_parity::arm::{Arm, Setup, run_with};
use crate::host_parity::wire::Object;
use crate::host_parity::world;

const SECOND_CHARACTER: u32 = 0x5000_0002;
const SECOND_CHARACTER_NAME: &str = "Parity Second";

/// Event tags (`ac-plugin-abi` `Event`).
const LOGIN_COMPLETE: u32 = 3;
const LOGOFF: u32 = 4;

fn second_character() -> Object {
    Object {
        name: SECOND_CHARACTER_NAME.into(),
        ..Object {
            guid: SECOND_CHARACTER,
            weenie_class_id: SECOND_CHARACTER & 0xFFFF,
            ..world::player_object()
        }
    }
}

fn kits(items: &[InventoryItem]) -> usize {
    items.iter().filter(|i| i.object_id == world::KIT).count()
}

/// What a plugin heard in which order: logoff and login, each with what it
/// read inside the event.
fn heard(arm: &Arm) -> Vec<String> {
    arm.event_order()
        .into_iter()
        .filter(|line| {
            [
                format!("event {LOGOFF}"),
                format!("event {LOGIN_COMPLETE}"),
                format!("reaction {LOGOFF}"),
                format!("reaction {LOGIN_COMPLETE}"),
            ]
            .contains(line)
        })
        .collect()
}

// OpenAC `LogoffParityTests.LogoffReachesAPluginBeforeTheSessionIsTornDownOnBothClients`.
#[test]
fn logoff_reaches_a_plugin_before_the_session_is_torn_down_on_both_clients() {
    run_with(&Setup::default(), &[], true, |arm, transcript| {
        world::stage_carried_items(arm);
        arm.react::<calls::ItemsOwned>(LOGOFF, &());

        transcript.step("the character logs off to the character list");
        let asked = arm.call::<calls::LoginLogout>(&());
        transcript.record("asked", asked);
        assert!(asked, "{}", arm.name());
        arm.confirm_logoff();
        let read = arm.reactions::<calls::ItemsOwned>(LOGOFF);
        let carrying: Vec<usize> = read.iter().map(|items| kits(items)).collect();
        transcript.record("logoff carrying", &carrying);
        // Told once, while the kit could still be read.
        assert_eq!(carrying, [1], "{}: {:#?}", arm.name(), arm.logs);
        // The teardown came after: the kit the server never sent again is
        // gone.
        let held = kits(&arm.call::<calls::ItemsOwned>(&()));
        transcript.record("after, kit held", held);
        assert_eq!(held, 0);
    });
}

// OpenAC `RetailUiRuntime.EndCharacterSessionWithRetailGates`: the
// player's own end of the character session (the log out key, the
// indicator bar, Exit to Character Selection) on the ground is a log off.
#[test]
#[ignore = "needs the retail dats: a body that stands"]
fn the_players_end_of_session_on_the_ground_logs_off() {
    use ac_runtime::session::{LifecycleCommand, SessionCommand};
    let setup = Setup {
        dats: true,
        ..Setup::default()
    };
    run_with(&setup, &[], true, |arm, transcript| {
        transcript.step("the player ends the character session, standing");
        arm.command(SessionCommand::Lifecycle(
            LifecycleCommand::EndCharacterSession,
        ));
        arm.confirm_logoff();
        let in_world = arm.call::<calls::CharacterInfo>(&()).is_some();
        transcript.record("inWorld", in_world);
        assert!(!in_world, "{}", arm.name());
    });
}

// The same gate's third case: with no moving character (OpenAC: not in
// player mode, `IsGrounded` is null) the request does nothing.
#[test]
fn the_players_end_of_session_without_a_moving_character_does_nothing() {
    use ac_runtime::session::{LifecycleCommand, SessionCommand};
    run_with(&Setup::default(), &[], true, |arm, transcript| {
        transcript.step("the player ends the character session, no body");
        arm.command(SessionCommand::Lifecycle(
            LifecycleCommand::EndCharacterSession,
        ));
        let asked = arm.outbound().iter().any(|m| m.opcode == 0xF653);
        transcript.record("asked", asked);
        assert!(!asked, "{}", arm.name());
    });
}

// OpenAC `LogoffParityTests.ALogoffIntoTheNextCharacterIsLogoffThenLoginOnBothClients`.
#[test]
fn a_logoff_into_the_next_character_is_logoff_then_login_on_both_clients() {
    let setup = Setup {
        more_characters: vec![(SECOND_CHARACTER, SECOND_CHARACTER_NAME)],
        ..Setup::default()
    };
    run_with(&setup, &[], false, |arm, transcript| {
        arm.enter_world();
        arm.mark_events();
        arm.react::<calls::CharacterInfo>(LOGOFF, &());
        arm.react::<calls::CharacterInfo>(LOGIN_COMPLETE, &());

        transcript.step("the next character is chosen");
        let set = arm.call::<calls::LoginSetNext>(&SECOND_CHARACTER);
        transcript.record("set", set);
        assert!(set, "{}", arm.name());

        transcript.step("the character logs off into it");
        assert!(arm.call::<calls::LoginLogout>(&()));
        arm.confirm_logoff_and_enter(&second_character());
        let order = heard(arm);
        transcript.record("order", &order);
        let names = |tag| -> Vec<String> {
            arm.reactions::<calls::CharacterInfo>(tag)
                .into_iter()
                .map(|c| c.map_or_else(|| "none".to_owned(), |c| c.name))
                .collect()
        };
        let (left, arrived) = (names(LOGOFF), names(LOGIN_COMPLETE));
        transcript.record("logoff", &left);
        transcript.record("login", &arrived);
        assert_eq!(
            order,
            [
                format!("event {LOGOFF}"),
                format!("reaction {LOGOFF}"),
                format!("event {LOGIN_COMPLETE}"),
                format!("reaction {LOGIN_COMPLETE}"),
            ],
            "{}",
            arm.name()
        );
        assert_eq!(left, [world::PLAYER_NAME]);
        assert_eq!(arrived, [SECOND_CHARACTER_NAME]);
        let in_world = arm.call::<calls::CharacterInfo>(&()).is_some();
        transcript.record("inWorld", in_world);
        assert!(in_world);
    });
}
