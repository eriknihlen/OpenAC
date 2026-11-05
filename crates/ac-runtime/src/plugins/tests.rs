//! The binding against the runtime owners, call by call. Golden values and
//! cases come from OpenAC's runtime surface tests, named per test.

use std::sync::mpsc;
use std::time::Duration;

use ac_plugin::abi::character::{AdvancementKind, AdvancementRequest, AdvancementStatus};
use ac_plugin::abi::{Import, Wire, calls};
use ac_plugin::{Caller, SessionApi};
use ac_proto::{AttributeEntry, SkillEntry};

use super::*;
use crate::communication::CommunicationPaths;
use crate::session::Outbox;
use crate::world::tests::empty_description;

pub(super) const PLAYER: u32 = 0x5000_000B;
/// Melee defence, which the fixture character carries (OpenAC
/// `RuntimeAutomationSurfaceAdvancementTests.KnownSkill`).
const KNOWN_SKILL: u32 = 6;
/// A skill the server never mentioned.
const UNKNOWN_SKILL: u32 = 9999;
/// Endurance, as an advancement request names it.
const ENDURANCE: u32 = 2;
/// Stamina at full, as an advancement request names it.
const MAX_STAMINA: u32 = 3;

pub(super) const CLOCKS: Clocks = Clocks {
    wall_unix_ms: 1_790_000_000_000,
    session: Duration::from_secs(30),
};

/// A session in the world with OpenAC's advancement fixture character:
/// melee defence (37 ranks, 4242 experience), endurance (11 ranks, 1234),
/// stamina (9 ranks, 777, 42 now).
pub(super) fn in_world() -> SessionState {
    in_world_with(None)
}

/// [`in_world`], the player's spellbook reading `spells`.
pub(super) fn in_world_with(
    spells: Option<std::sync::Arc<ac_core::spells::SpellTable>>,
) -> SessionState {
    let mut state = SessionState::new(CommunicationPaths::default());
    if let Some(spells) = spells {
        state.world.install_player_rules(spells, None);
    }
    state.world.set_player(PLAYER);
    let mut description = empty_description();
    description.attributes = vec![
        AttributeEntry {
            id: ENDURANCE,
            ranks: 11,
            start: 50,
            xp: 1234,
            current: None,
        },
        AttributeEntry {
            id: 8,
            ranks: 9,
            start: 0,
            xp: 777,
            current: Some(42),
        },
    ];
    description.skills = vec![SkillEntry {
        skill: KNOWN_SKILL,
        ranks: 37,
        adjust_pp: 1,
        advancement_class: 2,
        xp: 4242,
        init: 0,
        resistance: 0,
        last_used: 0.0,
    }];
    state.world.apply_player_description(&description);
    state.world.drain_changes().for_each(drop);
    state
}

/// Runs `body` with a binding over `state`, connected and in the world.
pub(super) fn with_binding<R>(
    state: &mut SessionState,
    outbox: &mut Outbox,
    body: impl FnOnce(&mut PluginBinding<'_>) -> R,
) -> R {
    let (events, _host) = mpsc::channel();
    let mut binding = PluginBinding {
        state,
        events: &events,
        window_attached: false,
        character: Some(CharacterInfo {
            id: PLAYER,
            name: "+Horan".into(),
            world: "Local".into(),
            ..CharacterInfo::default()
        }),
        wall_unix_ms: CLOCKS.wall_unix_ms,
        link: Some(Link {
            outbox,
            active: true,
            clocks: CLOCKS,
        }),
        facts: SessionFacts {
            in_world: true,
            ..SessionFacts::none()
        },
        deferred: Vec::new(),
    };
    body(&mut binding)
}

/// One call through the import dispatch, as a plugin makes it.
pub(super) fn call<C: ac_plugin::abi::Call>(
    binding: &mut PluginBinding<'_>,
    request: &C::Request,
) -> C::Reply {
    let bytes = binding
        .call(
            Caller {
                plugin_id: "test.plugin",
                api_version: ac_plugin::abi::API_CURRENT,
            },
            C::IMPORT,
            &request.to_bytes(),
        )
        .expect("a well-formed request");
    C::Reply::from_bytes(&bytes).expect("a well-formed reply")
}

fn advance(kind: AdvancementKind, stat_id: u32, cost: u64) -> AdvancementRequest {
    AdvancementRequest {
        kind,
        stat_id,
        cost,
    }
}

// OpenAC `RuntimeAutomationSurfaceAdvancementTests.
// RanksAndBankedExperienceReachTheSkillAttributeAndVitalRecords` and
// `TheVitalListIsHealthStaminaAndManaWithTheirRequestNumbers`.
#[test]
fn ranks_and_banked_experience_reach_the_skill_attribute_and_vital_records() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let skill = call::<calls::CharacterSkill>(b, &KNOWN_SKILL).expect("known");
        assert_eq!((skill.ranks, skill.experience_spent), (37, 4242));
        let attributes = call::<calls::CharacterAttributes>(b, &());
        let endurance = attributes
            .iter()
            .find(|a| a.stat_id == ENDURANCE)
            .expect("endurance");
        assert_eq!(
            (
                endurance.ranks,
                endurance.experience_spent,
                endurance.name.as_str()
            ),
            (11, 1234, "Endurance")
        );
        let vitals = call::<calls::CharacterVitals>(b, &());
        let stamina = vitals.iter().find(|v| v.kind == 1).expect("stamina");
        assert_eq!(stamina.stat_id, MAX_STAMINA);
        assert_eq!(stamina.name, "Stamina");
        assert_eq!(
            (stamina.ranks, stamina.experience_spent, stamina.current),
            (9, 777, 42)
        );
        assert!(
            vitals
                .iter()
                .all(|v| v.stat_id == u32::try_from(v.kind).unwrap() * 2 + 1)
        );
    });
}

// OpenAC `ASpendReachesTheRuntimeCommandUnchanged` (one row per kind): the
// request reaches the wire as the character window's raise does.
#[test]
fn a_spend_reaches_the_server_unchanged() {
    use crate::character::CharacterCommand as C;
    let rows = [
        (
            advance(AdvancementKind::Attribute, ENDURANCE, 1500),
            C::RaiseAttribute {
                attribute: ENDURANCE,
                xp: 1500,
            },
        ),
        (
            advance(AdvancementKind::Vital, MAX_STAMINA, 900),
            C::RaiseVital {
                vital: MAX_STAMINA,
                xp: 900,
            },
        ),
        (
            advance(AdvancementKind::Skill, KNOWN_SKILL, 2500),
            C::RaiseSkill {
                skill: KNOWN_SKILL,
                xp: 2500,
            },
        ),
        (
            advance(AdvancementKind::TrainSkill, KNOWN_SKILL, 4),
            C::TrainSkill {
                skill: KNOWN_SKILL,
                credits: 4,
            },
        ),
    ];
    for (request, expected) in rows {
        let mut state = in_world();
        let mut outbox = Outbox::new();
        let result = with_binding(&mut state, &mut outbox, |b| {
            call::<calls::CharacterAdvance>(b, &request)
        });
        assert_eq!(result.status, AdvancementStatus::Sent, "{request:?}");
        let sent: Vec<Vec<u8>> = outbox.drain().map(|o| o.body).collect();
        assert_eq!(sent, vec![expected.build(1)], "{request:?}");
    }
}

// OpenAC `ASpendOnStatZeroIsRefusedAndNeverSent`,
// `ASpendOnAStatTheCharacterHasNotGotIsRefusedAndNeverSent`,
// `ASpendOfNothingIsRefusedAndNeverSent`, `AnAbsurdCostIsRefusedAndNeverSent`,
// `ACostTooWideForTheRequestFieldIsRefusedRatherThanCutDown`.
#[test]
fn spends_on_nothing_or_too_much_are_refused_and_never_sent() {
    let rows = [
        (
            advance(AdvancementKind::Skill, 0, 100),
            AdvancementStatus::UnknownStat,
        ),
        (
            advance(AdvancementKind::Attribute, 7, 100),
            AdvancementStatus::UnknownStat,
        ),
        (
            advance(AdvancementKind::Vital, 2, 100),
            AdvancementStatus::UnknownStat,
        ),
        (
            advance(AdvancementKind::Skill, UNKNOWN_SKILL, 100),
            AdvancementStatus::UnknownStat,
        ),
        (
            advance(AdvancementKind::TrainSkill, UNKNOWN_SKILL, 100),
            AdvancementStatus::UnknownStat,
        ),
        (
            advance(AdvancementKind::Skill, KNOWN_SKILL, 0),
            AdvancementStatus::InvalidCost,
        ),
        (
            advance(AdvancementKind::Skill, KNOWN_SKILL, 5_000_000_000),
            AdvancementStatus::InvalidCost,
        ),
        (
            advance(AdvancementKind::TrainSkill, KNOWN_SKILL, 1001),
            AdvancementStatus::InvalidCost,
        ),
        (
            advance(AdvancementKind::Skill, KNOWN_SKILL, u64::from(u32::MAX) + 1),
            AdvancementStatus::InvalidCost,
        ),
    ];
    for (request, status) in rows {
        let mut state = in_world();
        let mut outbox = Outbox::new();
        let result = with_binding(&mut state, &mut outbox, |b| {
            call::<calls::CharacterAdvance>(b, &request)
        });
        assert_eq!(result.status, status, "{request:?}");
        assert!(result.notice.is_some_and(|n| !n.trim().is_empty()));
        assert!(outbox.is_empty(), "{request:?} went out");
    }
    // At the ceiling the whole cost goes out.
    let mut state = in_world();
    let mut outbox = Outbox::new();
    let widest = advance(AdvancementKind::Skill, KNOWN_SKILL, u64::from(u32::MAX));
    let result = with_binding(&mut state, &mut outbox, |b| {
        call::<calls::CharacterAdvance>(b, &widest)
    });
    assert_eq!(result.status, AdvancementStatus::Sent);
}

// OpenAC `ASpendBeforeTheCharacterIsInTheWorldIsRefusedAndNeverSent` and
// `AnUnboundSurfaceAnswersEmptyRatherThanThrowing`.
#[test]
fn before_the_world_spends_are_unavailable_and_reads_are_empty() {
    let mut state = SessionState::new(CommunicationPaths::default());
    let (events, _host) = mpsc::channel();
    let mut binding = PluginBinding::offline(&mut state, &events, 0);
    let result = call::<calls::CharacterAdvance>(
        &mut binding,
        &advance(AdvancementKind::Attribute, ENDURANCE, 100),
    );
    assert_eq!(result.status, AdvancementStatus::Unavailable);
    assert!(call::<calls::CharacterSkills>(&mut binding, &()).is_empty());
    assert!(call::<calls::CharacterVitals>(&mut binding, &()).is_empty());
    assert!(call::<calls::WorldObjects>(&mut binding, &()).is_empty());
    assert_eq!(call::<calls::CharacterInfo>(&mut binding, &()), None);
    assert!(!call::<calls::HostRequestClose>(&mut binding, &()));
    assert!(binding.deferred.is_empty());
}

#[test]
fn a_malformed_request_is_a_fault_and_unserved_imports_answer_inert() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let caller = Caller {
            plugin_id: "test.plugin",
            api_version: ac_plugin::abi::API_CURRENT,
        };
        let error = b
            .call(caller, Import::CharacterSkill, &[1, 2])
            .expect_err("truncated");
        assert!(error.contains("character_skill"), "{error}");
        assert_eq!(
            b.call(caller, Import::HostWindowAttached, &[]),
            Ok(Import::HostWindowAttached.default_reply())
        );
    });
}

#[test]
fn closing_the_session_waits_for_the_plugin_call_to_return() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    let deferred = with_binding(&mut state, &mut outbox, |b| {
        assert!(call::<calls::HostRequestClose>(b, &()));
        std::mem::take(&mut b.deferred)
    });
    assert!(matches!(deferred.as_slice(), [SessionCommand::Shutdown]));
}

#[test]
fn the_clock_reads_the_wall_the_zone_and_a_fresh_seed() {
    let mut state = in_world();
    // A fixed zone: the machine's own may change its offset between the
    // test's fixed wall time and the real instant the fine ticks read.
    state
        .plugin_services
        .set_zone(crate::local_time::LocalZone::fixed(3600));
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let first = call::<calls::HostClock>(b, &());
        let second = call::<calls::HostClock>(b, &());
        assert_eq!(first.utc_unix_ms, CLOCKS.wall_unix_ms);
        // The fine ticks are the instant of the call (after the test's
        // fixed pass time), never going back; the offset in seconds agrees
        // with the minutes.
        assert!(first.utc_unix_100ns > 0 && second.utc_unix_100ns >= first.utc_unix_100ns);
        assert_eq!(first.utc_offset_seconds / 60, first.utc_offset_minutes);
        assert_ne!(first.random_seed, second.random_seed);
        assert!(second.monotonic_ms >= first.monotonic_ms);
    });
}

// MosswartMassacre's `ProcessTelemetry` (the C# plugin's telemetry frame):
// the process's working set and handles, and the CPU use since this
// plugin's previous call, the first call setting the baseline (the C#
// constructor's reading); each plugin keeps its own baseline, and an
// unloaded plugin starts again.
#[test]
fn process_stats_measure_cpu_from_each_plugins_previous_call() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    let stats = |b: &mut PluginBinding<'_>, plugin_id| {
        let bytes = b
            .call(
                Caller {
                    plugin_id,
                    api_version: ac_plugin::abi::API_CURRENT,
                },
                Import::HostProcessStats,
                &[],
            )
            .unwrap();
        ac_plugin::abi::host::ProcessStats::from_bytes(&bytes).unwrap()
    };
    with_binding(&mut state, &mut outbox, |b| {
        let first = stats(b, "test.one");
        assert_eq!(first.cpu_percent, 0.0);
        if cfg!(any(windows, target_os = "linux")) {
            assert!(first.working_set_bytes > 1024 * 1024, "{first:?}");
            assert!(first.handle_count > 0, "{first:?}");
        }
        assert!(b.state.plugin_services.cpu_meters.contains_key("test.one"));
        let second = stats(b, "test.one");
        assert!(second.cpu_percent >= 0.0, "{second:?}");
        // Another plugin's first call is its own baseline.
        assert_eq!(stats(b, "test.two").cpu_percent, 0.0);
        b.released("test.one");
        assert!(!b.state.plugin_services.cpu_meters.contains_key("test.one"));
        assert!(b.state.plugin_services.cpu_meters.contains_key("test.two"));
        assert_eq!(stats(b, "test.one").cpu_percent, 0.0);
    });
}

#[test]
fn typed_lines_naming_a_plugin_verb_are_claimed() {
    let mut lines = PluginLines::default();
    assert!(!lines.claim("/hello"));
    lines.set_verbs(vec!["hello".into(), "plugin".into()]);
    assert!(lines.claim("/HELLO there"));
    assert!(lines.claim("@plugin reload openac.hello"));
    assert!(!lines.claim("hello"));
    assert!(!lines.claim("/say hello"));
    assert_eq!(
        lines.take(),
        vec![
            PluginLine::Verb("/HELLO there".into()),
            PluginLine::Verb("@plugin reload openac.hello".into())
        ]
    );
    assert!(lines.take().is_empty());
}

#[test]
fn options_are_read_and_set_by_name() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let options = call::<calls::CharacterOptions>(b, &());
        assert!(options.iter().any(|(name, _)| name == "AutoRepeatAttack"));
        let unknown = call::<calls::CharacterSetOption>(b, &("NoSuchOption".to_owned(), true));
        assert_eq!(
            unknown.status,
            ac_plugin::abi::character::OptionStatus::UnknownOption
        );
        let set = call::<calls::CharacterSetOption>(b, &("IgnoreTradeRequests".to_owned(), true));
        assert_eq!(
            set.status,
            ac_plugin::abi::character::OptionStatus::Accepted
        );
        let options = call::<calls::CharacterOptions>(b, &());
        assert!(options.contains(&("IgnoreTradeRequests".to_owned(), true)));
    });
}

// OpenAC `CharacterOptionNames`: the chat-channel options go by their
// options-page names ("Hear...Chat"); the client's own "ListenTo..."
// spelling is accepted too, without regard to case; a bare number is no
// name.
#[test]
fn the_chat_options_go_by_their_page_names() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let names: Vec<String> = call::<calls::CharacterOptions>(b, &())
            .into_iter()
            .map(|(name, _)| name)
            .collect();
        for page in [
            "HearAllegianceChat",
            "HearGeneralChat",
            "HearTradeChat",
            "HearLFGChat",
            "HearRoleplayChat",
            "HearSocietyChat",
        ] {
            assert!(names.iter().any(|n| n == page), "{page} in {names:?}");
        }
        assert!(!names.iter().any(|n| n.starts_with("ListenTo")));
        for name in ["hearGENERALchat", "ListenToGeneralChat"] {
            let set = call::<calls::CharacterSetOption>(b, &(name.to_owned(), false));
            assert_eq!(
                set.status,
                ac_plugin::abi::character::OptionStatus::Accepted,
                "{name}"
            );
        }
        let bare = call::<calls::CharacterSetOption>(b, &("35".to_owned(), true));
        assert_eq!(
            bare.status,
            ac_plugin::abi::character::OptionStatus::UnknownOption
        );
    });
}

// OpenAC `NoOpPluginClipboard` and `NoOpHostWindow` headless; with a window
// the request reaches it and the minimized state is the window's report.
#[test]
fn the_client_window_calls_need_a_window() {
    use crate::communication::WindowRequest;
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert!(!call::<calls::ClientSetClipboard>(b, &"text".into()));
        assert!(!call::<calls::ClientMinimize>(b, &()));
        assert!(!call::<calls::ClientIsMinimized>(b, &()));
        b.window_attached = true;
        assert!(call::<calls::ClientSetClipboard>(b, &"text".into()));
        assert!(call::<calls::ClientMinimize>(b, &()));
        assert!(call::<calls::ClientRestore>(b, &()));
        assert!(!call::<calls::ClientIsMinimized>(b, &()));
        b.state.plugin_services.window_minimized = true;
        assert!(call::<calls::ClientIsMinimized>(b, &()));
    });
    assert_eq!(
        state.communication.take_window_requests(),
        [
            WindowRequest::SetClipboard("text".into()),
            WindowRequest::Minimize,
            WindowRequest::Restore,
        ]
    );
}
