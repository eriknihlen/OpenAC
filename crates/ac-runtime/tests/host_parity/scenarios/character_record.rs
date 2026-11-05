//! What a plugin reads about its own character beyond the stats: the vitae
//! penalty and the titles (OpenAC `Scenarios/CharacterRecordParityTests.cs`),
//! each said by the server and asserted per arm.

use ac_plugin::abi::calls;

use crate::host_parity::arm::run;
use crate::host_parity::{wire, world};

/// The vitae spell, and the stat-mod type bit that files an enchantment
/// under vitae (`ac-runtime` `gameplay::magic::classify_bucket`).
const VITAE_SPELL: u16 = 666;
const VITAE_TYPE: u32 = 0x0080_0000;

// OpenAC `CharacterRecordParityTests.EitherClientReportsTheVitaePenalty`.
#[test]
fn either_client_reports_the_vitae_penalty() {
    run(|arm, transcript| {
        world::stage(arm);

        transcript.step("none");
        let info = arm.call::<calls::CharacterInfo>(&()).expect("in the world");
        transcript.record("multiplier", info.vitae_multiplier);
        assert!((info.vitae_multiplier - 1.0).abs() < f32::EPSILON);

        transcript.step("died");
        let vitae = wire::Enchantment {
            spell_id: VITAE_SPELL,
            layer: 1,
            duration: -1.0,
            stat_mod_type: VITAE_TYPE,
            stat_mod_value: 0.95,
            ..wire::Enchantment::default()
        };
        arm.server
            .game_event(world::PLAYER, 0x02C2, &wire::enchantment(&vitae));
        arm.sync();
        let info = arm.call::<calls::CharacterInfo>(&()).expect("in the world");
        transcript.record("multiplier", info.vitae_multiplier);
        assert!(
            (info.vitae_multiplier - 0.95).abs() < 1e-6,
            "{}: {}",
            arm.name(),
            info.vitae_multiplier
        );
    });
}

// OpenAC `CharacterRecordParityTests.EitherClientReportsTheTitlesTheServerSent`.
#[test]
fn either_client_reports_the_titles_the_server_sent() {
    run(|arm, transcript| {
        world::stage(arm);
        let titles = |info: &ac_plugin::abi::CharacterInfo| -> Vec<u32> {
            info.titles.iter().map(|(id, _)| *id).collect()
        };

        transcript.step("listed");
        // Version 1, displayed title 5, then the list: 2 titles, 7 and 5.
        arm.server
            .game_event(world::PLAYER, 0x0029, &wire::payload(&[1, 5, 2, 7, 5]));
        arm.sync();
        let info = arm.call::<calls::CharacterInfo>(&()).expect("in the world");
        transcript.record("current", info.current_title_id);
        transcript.record("titles", titles(&info));
        assert_eq!(info.current_title_id, 5, "{}", arm.name());
        // The server's order, not the client's: 7 was listed first.
        assert_eq!(titles(&info), [7, 5]);

        transcript.step("earned");
        arm.server
            .game_event(world::PLAYER, 0x002B, &wire::payload(&[9, 1]));
        arm.sync();
        let info = arm.call::<calls::CharacterInfo>(&()).expect("in the world");
        transcript.record("current", info.current_title_id);
        transcript.record("titles", titles(&info));
        assert_eq!(info.current_title_id, 9);
        assert_eq!(titles(&info), [7, 5, 9]);
    });
}
