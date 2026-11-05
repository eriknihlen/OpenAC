//! Ported from OpenAC `tests/AcDream.Runtime.Tests/Gameplay/CharacterOptionTableTests.cs`
//! and the options half of `RuntimeCharacterStateTests.cs`
//! (`RuntimeCharacterOptionsState`). The C# tests of a flush callback that
//! dirties the options again, or throws, have no counterpart: the callback
//! borrows the options immutably and cannot fail.

use super::*;

const SECOND: Duration = Duration::from_secs(1);

fn no_send() -> impl FnMut(u32, bool) {
    |_, _| {}
}

const AUTO_SAVE_IDS: [u32; 21] = [
    option_id::AUTO_REPEAT_ATTACK,
    option_id::IGNORE_ALLEGIANCE_REQUESTS,
    option_id::IGNORE_FELLOWSHIP_REQUESTS,
    option_id::FELLOWSHIP_SHARE_XP,
    option_id::ACCEPT_LOOT_PERMITS,
    option_id::FELLOWSHIP_SHARE_LOOT,
    option_id::FELLOWSHIP_AUTO_ACCEPT_REQUESTS,
    option_id::USE_CHARGE_ATTACK,
    option_id::LISTEN_TO_ALLEGIANCE_CHAT,
    option_id::LISTEN_TO_GENERAL_CHAT,
    option_id::LISTEN_TO_TRADE_CHAT,
    option_id::LISTEN_TO_LFG_CHAT,
    option_id::LISTEN_TO_ROLEPLAY_CHAT,
    option_id::APPEAR_OFFLINE,
    option_id::LEAD_MISSILE_TARGETS,
    option_id::USE_FAST_MISSILES,
    option_id::LISTEN_TO_SOCIETY_CHAT,
    option_id::SHOW_HELM,
    option_id::USE_MOUSE_TURNING,
    option_id::SHOW_CLOAK,
    option_id::LOCK_UI,
];

const CLIENT_DEFAULT_ON_IDS: [u32; 16] = [
    option_id::AUTO_REPEAT_ATTACK,
    option_id::IGNORE_FELLOWSHIP_REQUESTS,
    option_id::ALLOW_GIVE,
    option_id::SHOW_TOOLTIPS,
    option_id::TOGGLE_RUN,
    option_id::AUTO_TARGET,
    option_id::VIVID_TARGETING_INDICATOR,
    option_id::FELLOWSHIP_SHARE_XP,
    option_id::COORDINATES_ON_RADAR,
    option_id::SPELL_DURATION,
    option_id::USE_CHARGE_ATTACK,
    option_id::LISTEN_TO_ALLEGIANCE_CHAT,
    option_id::LISTEN_TO_GENERAL_CHAT,
    option_id::LISTEN_TO_TRADE_CHAT,
    option_id::LISTEN_TO_LFG_CHAT,
    option_id::LEAD_MISSILE_TARGETS,
];

#[test]
fn the_table_has_53_contiguous_ids() {
    assert_eq!(OPTIONS.len(), 53);
    for id in 0x00..=0x34 {
        assert_eq!(option(id).map(|e| e.id), Some(id), "id {id:#04x}");
    }
}

#[test]
fn auto_save_and_client_default_splits_match() {
    for entry in OPTIONS {
        assert_eq!(
            entry.auto_save,
            AUTO_SAVE_IDS.contains(&entry.id),
            "{}",
            entry.name
        );
        assert_eq!(
            entry.client_default,
            CLIENT_DEFAULT_ON_IDS.contains(&entry.id),
            "{}",
            entry.name
        );
    }
    assert_eq!(OPTIONS.iter().filter(|e| e.auto_save).count(), 21);
    assert_eq!(OPTIONS.iter().filter(|e| e.client_default).count(), 16);
}

#[test]
fn reconstructed_client_default_words_match_the_constants() {
    let (mut options1, mut options2) = (0u32, 0u32);
    for entry in OPTIONS.iter().filter(|e| e.client_default) {
        if entry.in_options1 {
            options1 |= entry.mask;
        } else {
            options2 |= entry.mask;
        }
    }
    assert_eq!(options1, 0x50C4_A54A);
    assert_eq!(options2, 0x0000_8700);
}

#[test]
fn unknown_and_reserved_ids_are_rejected() {
    for id in [0x35, 0x36, 0xFFFF, 0xFFFF_FFFF] {
        assert!(option(id).is_none());
    }
}

#[test]
fn word_and_mask_match_the_retail_enums() {
    // OpenAC CharacterOptionTableTests: each option's word and mask, from an
    // independent transcription of the game's option enums.
    let golden: [(u32, bool, u32); 53] = [
        (0x00, true, 0x0000_0002),
        (0x01, true, 0x0000_0004),
        (0x02, true, 0x0000_0008),
        (0x03, true, 0x0002_0000),
        (0x04, true, 0x0001_0000),
        (0x05, false, 0x0000_0001),
        (0x06, true, 0x0000_0040),
        (0x07, true, 0x0000_0080),
        (0x08, true, 0x0000_0100),
        (0x09, true, 0x0000_0200),
        (0x0A, true, 0x0000_0400),
        (0x0B, true, 0x0000_0800),
        (0x0C, true, 0x0000_1000),
        (0x0D, true, 0x0000_2000),
        (0x0E, true, 0x0000_8000),
        (0x0F, true, 0x0004_0000),
        (0x10, true, 0x0008_0000),
        (0x11, true, 0x0010_0000),
        (0x12, true, 0x2000_0000),
        (0x13, true, 0x0020_0000),
        (0x14, true, 0x0040_0000),
        (0x15, true, 0x0080_0000),
        (0x16, true, 0x0200_0000),
        (0x17, true, 0x0400_0000),
        (0x18, true, 0x0800_0000),
        (0x19, true, 0x1000_0000),
        (0x1A, true, 0x8000_0000),
        (0x1B, true, 0x4000_0000),
        (0x1C, false, 0x0000_0002),
        (0x1D, false, 0x0000_0020),
        (0x1E, false, 0x0000_0004),
        (0x1F, false, 0x0000_0008),
        (0x20, false, 0x0000_0010),
        (0x21, false, 0x0000_0040),
        (0x22, false, 0x0000_0080),
        (0x23, false, 0x0000_0100),
        (0x24, false, 0x0000_0200),
        (0x25, false, 0x0000_0400),
        (0x26, false, 0x0000_0800),
        (0x27, false, 0x0000_1000),
        (0x28, false, 0x0000_2000),
        (0x29, false, 0x0000_4000),
        (0x2A, false, 0x0000_8000),
        (0x2B, false, 0x0001_0000),
        (0x2C, false, 0x0002_0000),
        (0x2D, false, 0x0004_0000),
        (0x2E, false, 0x0008_0000),
        (0x2F, false, 0x0010_0000),
        (0x30, false, 0x0020_0000),
        (0x31, false, 0x0040_0000),
        (0x32, false, 0x0080_0000),
        (0x33, false, 0x0100_0000),
        (0x34, false, 0x0200_0000),
    ];
    for (id, in_options1, mask) in golden {
        let entry = option(id).unwrap();
        assert_eq!(
            (entry.in_options1, entry.mask),
            (in_options1, mask),
            "{id:#04x}"
        );
    }
    let mut pairs: Vec<(bool, u32)> = OPTIONS.iter().map(|e| (e.in_options1, e.mask)).collect();
    pairs.sort_unstable();
    pairs.dedup();
    assert_eq!(pairs.len(), 53);
}

#[test]
fn names_resolve_ignoring_case() {
    assert_eq!(
        option_by_name("listentogeneralchat").map(|e| e.id),
        Some(0x23)
    );
    assert!(option_by_name("NoSuchOption").is_none());
}

#[test]
fn options2_bits_toggle_only_their_own_bit() {
    let mut options = CharacterOptions::new();
    options.replace(options.options1(), 0, true);
    options.set_bit(option_id::LISTEN_TO_ROLEPLAY_CHAT, true);
    assert_eq!(options.options2(), 0x0000_0800);
    assert_eq!(options.options1(), DEFAULT_OPTIONS1);
    options.set_bit(option_id::LISTEN_TO_ROLEPLAY_CHAT, false);
    assert_eq!(options.options2(), 0);
}

#[test]
fn an_unknown_id_changes_nothing() {
    let mut options = CharacterOptions::new();
    let revision = options.revision();
    options.set_bit(0xFFFF, true);
    assert_eq!(
        (options.options1(), options.options2()),
        (DEFAULT_OPTIONS1, DEFAULT_OPTIONS2)
    );
    assert_eq!(options.revision(), revision);
    assert!(!options.get(0xFFFF));
    let mut invoked = false;
    assert!(!options.try_set(0x35, true, Duration::ZERO, &mut |_, _| invoked = true));
    assert!(!invoked);
    assert!(!options.is_dirty());
}

#[test]
fn get_reflects_replace() {
    let mut options = CharacterOptions::new();
    assert!(options.get(option_id::LISTEN_TO_GENERAL_CHAT));
    options.replace(options.options1(), 0, true);
    assert!(!options.get(option_id::LISTEN_TO_GENERAL_CHAT));
}

#[test]
fn an_auto_save_option_writes_then_sends_and_never_dirties() {
    let mut options = CharacterOptions::new();
    options.replace(options.options1(), 0, true);
    let mut sent = Vec::new();
    assert!(options.try_set(
        option_id::LISTEN_TO_GENERAL_CHAT,
        true,
        Duration::ZERO,
        &mut |id, value| sent.push((id, value))
    ));
    assert!(options.get(option_id::LISTEN_TO_GENERAL_CHAT));
    assert_eq!(sent, [(option_id::LISTEN_TO_GENERAL_CHAT, true)]);
    assert!(!options.is_dirty());
    assert_eq!(options.first_dirtied_at(), None);
}

#[test]
fn a_batched_option_writes_and_dirties_and_never_sends() {
    let mut options = CharacterOptions::new();
    let mut sent = Vec::new();
    assert!(
        options.try_set(option_id::AUTO_TARGET, false, SECOND, &mut |id, value| sent
            .push((id, value)))
    );
    assert_eq!(options.options1() & 0x0000_2000, 0);
    assert!(sent.is_empty());
    assert!(options.is_dirty());
    assert_eq!(options.first_dirtied_at(), Some(SECOND));
}

#[test]
fn an_unchanged_value_is_a_no_op() {
    let mut options = CharacterOptions::new();
    let mut sent = Vec::new();
    assert!(options.try_set(
        option_id::AUTO_TARGET,
        true,
        Duration::ZERO,
        &mut |id, value| sent.push((id, value))
    ));
    assert!(sent.is_empty());
    assert!(!options.is_dirty());
}

#[test]
fn ignoring_fellowship_requests_clears_auto_accept_first() {
    let mut options = CharacterOptions::new();
    options.try_set(
        option_id::FELLOWSHIP_AUTO_ACCEPT_REQUESTS,
        true,
        Duration::ZERO,
        &mut no_send(),
    );
    let mut sent = Vec::new();
    options.try_set(
        option_id::IGNORE_FELLOWSHIP_REQUESTS,
        true,
        Duration::ZERO,
        &mut |id, value| sent.push((id, value)),
    );
    assert_eq!(
        sent,
        [
            (option_id::FELLOWSHIP_AUTO_ACCEPT_REQUESTS, false),
            (option_id::IGNORE_FELLOWSHIP_REQUESTS, true),
        ]
    );
    assert_ne!(options.options1() & 0x0000_0008, 0);
    assert_eq!(options.options1() & 0x2000_0000, 0);

    // And the other way round.
    let mut sent = Vec::new();
    options.try_set(
        option_id::FELLOWSHIP_AUTO_ACCEPT_REQUESTS,
        true,
        Duration::ZERO,
        &mut |id, value| sent.push((id, value)),
    );
    assert_eq!(
        sent,
        [
            (option_id::IGNORE_FELLOWSHIP_REQUESTS, false),
            (option_id::FELLOWSHIP_AUTO_ACCEPT_REQUESTS, true),
        ]
    );
}

#[test]
fn turning_a_fellowship_option_off_never_clears_the_other() {
    let mut options = CharacterOptions::new();
    options.set_bit(option_id::IGNORE_FELLOWSHIP_REQUESTS, true);
    options.set_bit(option_id::FELLOWSHIP_AUTO_ACCEPT_REQUESTS, true);
    let mut sent = Vec::new();
    options.try_set(
        option_id::IGNORE_FELLOWSHIP_REQUESTS,
        false,
        Duration::ZERO,
        &mut |id, value| sent.push((id, value)),
    );
    assert_eq!(sent, [(option_id::IGNORE_FELLOWSHIP_REQUESTS, false)]);
    assert_ne!(options.options1() & 0x2000_0000, 0);
}

#[test]
fn only_the_first_change_starts_the_auto_save_clock() {
    let mut options = CharacterOptions::new();
    options.try_set(option_id::AUTO_TARGET, false, SECOND, &mut no_send());
    options.try_set(option_id::SHOW_TOOLTIPS, false, SECOND * 11, &mut no_send());
    assert_eq!(options.first_dirtied_at(), Some(SECOND));
}

#[test]
fn a_flush_needs_a_server_seed() {
    let mut options = CharacterOptions::new();
    assert!(!options.has_server_seed());
    options.try_set(
        option_id::AUTO_TARGET,
        false,
        Duration::ZERO,
        &mut no_send(),
    );
    let mut flushes = 0;
    assert!(!options.try_flush(|_| flushes += 1));
    assert_eq!(flushes, 0);
    assert!(options.is_dirty());

    options.replace(options.options1(), options.options2(), true);
    assert!(options.has_server_seed());
    assert!(!options.is_dirty());
    options.try_set(option_id::AUTO_TARGET, true, Duration::ZERO, &mut no_send());
    assert!(options.try_flush(|_| flushes += 1));
    assert_eq!(flushes, 1);
    assert!(!options.is_dirty());
    assert!(!options.try_flush(|_| flushes += 1));
}

#[test]
fn an_unarmed_replace_does_not_authorize_a_flush() {
    let mut options = CharacterOptions::new();
    options.replace(0, 0, false);
    options.try_set(option_id::AUTO_TARGET, true, Duration::ZERO, &mut no_send());
    assert!(!options.try_flush(|_| panic!("an unseeded block must not be sent")));
}

#[test]
fn the_auto_save_fires_at_480_seconds_and_not_before() {
    let mut options = CharacterOptions::new();
    options.try_set(
        option_id::AUTO_TARGET,
        false,
        Duration::ZERO,
        &mut no_send(),
    );
    // Unseeded: not even at the threshold.
    assert!(!options.try_flush_if_auto_save_due(AUTO_SAVE_DELAY + SECOND, |_| {}));
    options.replace(options.options1(), options.options2(), true);
    options.try_set(option_id::AUTO_TARGET, true, SECOND * 10, &mut no_send());
    let mut flushes = 0;
    assert!(
        !options.try_flush_if_auto_save_due(SECOND * 10 + AUTO_SAVE_DELAY - SECOND, |_| {
            flushes += 1;
        })
    );
    assert!(options.is_dirty());
    assert!(options.try_flush_if_auto_save_due(SECOND * 10 + AUTO_SAVE_DELAY, |_| flushes += 1));
    assert_eq!(flushes, 1);
    assert!(!options.is_dirty());
}

#[test]
fn replace_supersedes_pending_local_changes() {
    let mut options = CharacterOptions::new();
    options.try_set(
        option_id::AUTO_TARGET,
        false,
        Duration::ZERO,
        &mut no_send(),
    );
    options.replace(0x1111_1111, 0x2222_2222, true);
    assert!(!options.is_dirty());
    assert_eq!(options.first_dirtied_at(), None);
    assert_eq!(
        (options.options1(), options.options2()),
        (0x1111_1111, 0x2222_2222)
    );
}

#[test]
fn a_reconnect_needs_a_new_seed_before_saving_again() {
    let mut options = CharacterOptions::new();
    options.replace(options.options1(), options.options2(), true);
    options.try_set(
        option_id::AUTO_TARGET,
        false,
        Duration::ZERO,
        &mut no_send(),
    );
    assert!(options.try_flush(|_| {}));
    options.reset_session();
    assert!(!options.has_server_seed());
    assert_eq!(
        (options.options1(), options.options2()),
        (DEFAULT_OPTIONS1, DEFAULT_OPTIONS2)
    );
    options.try_set(
        option_id::AUTO_TARGET,
        false,
        Duration::ZERO,
        &mut no_send(),
    );
    assert!(!options.try_flush(|_| {}));
    options.replace(options.options1(), options.options2(), true);
    options.try_set(
        option_id::SHOW_TOOLTIPS,
        false,
        Duration::ZERO,
        &mut no_send(),
    );
    assert!(options.try_flush(|_| {}));
}

#[test]
fn a_truncated_description_trailer_seeds_nothing() {
    // OpenAC LiveSessionEventRouter: a trailer-truncated description
    // carries placeholder words and is ignored.
    let mut description = crate::world::tests::empty_description();
    description.options1 = 0x1234;
    description.trailer_truncated = true;
    let mut options = CharacterOptions::new();
    options.apply_player_description(&description);
    assert!(!options.has_server_seed());
    assert_eq!(options.options1(), DEFAULT_OPTIONS1);
    description.trailer_truncated = false;
    options.apply_player_description(&description);
    assert!(options.has_server_seed());
    assert_eq!(options.options1(), 0x1234);
}

#[test]
fn the_block_echoes_the_description_trailer() {
    let mut description = crate::world::tests::empty_description();
    description.options1 = 0x50C4_A54A;
    description.options2 = 0x0094_8700;
    description.hotbar_spells = vec![vec![1, 2], vec![], vec![3]];
    description.desired_comps = vec![DesiredComponent { id: 7, amount: 50 }];
    description.spellbook_filters = 0x3FFF;
    let shortcuts = [Shortcut {
        index: 0,
        object_id: 0x5000_0001,
        spell_id: 0,
    }];
    description.shortcuts = shortcuts.to_vec();
    let mut options = CharacterOptions::new();
    options.apply_player_description(&description);
    let favorites: [&[u32]; 8] = [&[1, 2], &[], &[3], &[], &[], &[], &[], &[]];
    assert_eq!(
        options.build_block(9),
        build_set_character_options(
            9,
            &OptionsBlock {
                options1: 0x50C4_A54A,
                options2: 0x0094_8700,
                shortcuts: &shortcuts,
                favorite_spells: favorites,
                desired_components: &[DesiredComponent { id: 7, amount: 50 }],
                spellbook_filters: 0x3FFF,
            }
        )
    );
}
