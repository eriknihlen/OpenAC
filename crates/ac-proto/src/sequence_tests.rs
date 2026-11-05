//! One table over every game-action builder: each takes the next value of
//! the session's counter and writes it, with its action type, into the
//! game-action header. Ported from the wrapper tests of OpenAC
//! `tests/AcDream.Core.Net.Tests/WorldSession*Tests.cs`
//! (`*_UsesNextSequenceAndExactBuilderBytes`,
//! `EveryAdministrationWrapper_UsesSharedSequenceAndExactBuilder`), which
//! check that `WorldSession.Send*` hands `NextGameActionSequence()` to the
//! builder.

use crate::movement_out::{MotionTimestamps, RawMotionState};
use crate::vendor::ItemAmount;
use crate::{
    GameActionSequence, Position, Shortcut, Vector3, allegiance, appraise, book, character, chat,
    chess, client_commands, combat, contract, fellowship, house, inventory, magic, movement_out,
    social, trade,
};

type Builder = Box<dyn Fn(u32) -> Vec<u8>>;

fn builders() -> Vec<(u32, Builder)> {
    let pos = Position::default();
    let ts = MotionTimestamps::default();
    vec![
        (0x0015, Box::new(|s| chat::build_talk(s, "hi").unwrap())),
        (
            0x005D,
            Box::new(|s| chat::build_tell(s, "Al", "hi").unwrap()),
        ),
        (
            0x0032,
            Box::new(|s| chat::build_talk_direct(s, 1, "hi").unwrap()),
        ),
        (
            0x0147,
            Box::new(|s| chat::build_chat_channel(s, 1, "hi").unwrap()),
        ),
        (0x01DF, Box::new(|s| chat::build_emote(s, "x").unwrap())),
        (
            0x01E1,
            Box::new(|s| chat::build_soul_emote(s, "x").unwrap()),
        ),
        (0x0063, Box::new(client_commands::build_tele_to_lifestone)),
        (0x028D, Box::new(client_commands::build_tele_to_marketplace)),
        (0x0279, Box::new(client_commands::build_suicide)),
        (0x01C2, Box::new(|s| client_commands::build_query_age(s, 0))),
        (
            0x000F,
            Box::new(|s| client_commands::build_set_afk_mode(s, true)),
        ),
        (0x0149, Box::new(client_commands::build_index_channels)),
        (0x01E9, Box::new(social::build_ping_request)),
        (0x002C, Box::new(|s| social::build_set_display_title(s, 1))),
        (
            0x0005,
            Box::new(|s| social::build_set_single_character_option(s, 1, true)),
        ),
        (
            0x0018,
            Box::new(|s| social::build_add_friend(s, "Al").unwrap()),
        ),
        (
            0x0275,
            Box::new(|s| social::build_confirmation_response(s, 1, 2, true)),
        ),
        (
            0x00A2,
            Box::new(|s| fellowship::build_create(s, "F", true).unwrap()),
        ),
        (0x00A3, Box::new(|s| fellowship::build_quit(s, false))),
        (0x001D, Box::new(|s| allegiance::build_swear(s, 1))),
        (0x001E, Box::new(|s| allegiance::build_kick(s, 1))),
        (
            0x0277,
            Box::new(|s| allegiance::build_break_allegiance_boot(s, "Bob", true).unwrap()),
        ),
        (
            0x0254,
            Box::new(|s| allegiance::build_set_motd(s, "Welcome").unwrap()),
        ),
        (
            0x0247,
            Box::new(|s| house::build_set_open_house_status(s, true)),
        ),
        (0x025F, Box::new(house::build_boot_everyone)),
        (0x0316, Box::new(|s| contract::build_abandon_contract(s, 1))),
        (
            0x01F6,
            Box::new(|s| trade::build_open_trade_negotiations(s, 1)),
        ),
        (0x0204, Box::new(trade::build_reset_trade)),
        (0x0269, Box::new(|s| chess::build_join(s, 1))),
        (0x026A, Box::new(chess::build_quit)),
        (0x026B, Box::new(|s| chess::build_move(s, 4, 1, 4, 3))),
        (0x026D, Box::new(chess::build_move_pass)),
        (0x026E, Box::new(|s| chess::build_stalemate(s, true))),
        (
            0x005F,
            Box::new(|s| {
                crate::vendor::build_buy(
                    s,
                    1,
                    &[ItemAmount {
                        amount: 1,
                        item_guid: 2,
                    }],
                    0,
                )
            }),
        ),
        (0x0060, Box::new(|s| crate::vendor::build_sell(s, 1, &[]))),
        (0x00AA, Box::new(|s| book::build_book_data(s, 1))),
        (
            0x00AB,
            Box::new(|s| book::build_book_modify_page(s, 1, 0, "p").unwrap()),
        ),
        (0x0036, Box::new(|s| inventory::build_use(s, 1))),
        (
            0x0019,
            Box::new(|s| inventory::build_put_item_in_container(s, 1, 2, 0)),
        ),
        (
            0x0054,
            Box::new(|s| inventory::build_stackable_merge(s, 1, 2, 3)),
        ),
        (
            0x00CD,
            Box::new(|s| inventory::build_give_object(s, 1, 2, 3)),
        ),
        (
            0x019C,
            Box::new(|s| {
                inventory::build_add_shortcut(
                    s,
                    &Shortcut {
                        index: 0,
                        object_id: 1,
                        spell_id: 0,
                    },
                )
            }),
        ),
        (
            0x00BF,
            Box::new(|s| inventory::build_set_inscription(s, 1, "x").unwrap()),
        ),
        (
            0x027D,
            Box::new(|s| inventory::build_create_tinkering_tool(s, 1, &[2]).unwrap()),
        ),
        (0x0053, Box::new(|s| combat::build_change_combat_mode(s, 2))),
        (
            0x0008,
            Box::new(|s| combat::build_targeted_melee_attack(s, 1, 2, 0.5)),
        ),
        (
            0x000A,
            Box::new(|s| combat::build_targeted_missile_attack(s, 1, 2, 0.5)),
        ),
        (0x01B7, Box::new(combat::build_cancel_attack)),
        (0x01BF, Box::new(|s| combat::build_query_health(s, 1))),
        (
            0x0048,
            Box::new(|s| magic::build_cast_untargeted_spell(s, 1)),
        ),
        (
            0x004A,
            Box::new(|s| magic::build_cast_targeted_spell(s, 1, 2)),
        ),
        (0x0224, Box::new(magic::build_clear_desired_components)),
        (0x0263, Box::new(|s| magic::build_query_item_mana(s, 1))),
        (0x00C8, Box::new(|s| appraise::build_appraise(s, 1))),
        (
            0x0045,
            Box::new(|s| character::build_raise_attribute(s, 1, 100)),
        ),
        (0x0047, Box::new(|s| character::build_train_skill(s, 1, 4))),
        (
            0xF61C,
            Box::new(move |s| {
                movement_out::build_move_to_state(
                    s,
                    &RawMotionState::default(),
                    &pos,
                    &ts,
                    true,
                    false,
                )
            }),
        ),
        (
            0xF753,
            Box::new(move |s| movement_out::build_autonomous_position(s, &pos, &ts, true)),
        ),
        (
            0xF61B,
            Box::new(move |s| movement_out::build_jump(s, 1.0, Vector3::default(), &pos, &ts)),
        ),
        (0x021E, Box::new(crate::build_house_query)),
    ]
}

#[test]
fn every_builder_takes_the_next_sequence_number() {
    let mut sequence = GameActionSequence::new();
    for (index, (action_type, build)) in builders().into_iter().enumerate() {
        let body = build(sequence.advance());
        let action = crate::GameAction::parse(&body).unwrap();
        assert_eq!(
            action.sequence,
            index as u32 + 1,
            "action {action_type:#06x}"
        );
        assert_eq!(action.action_type.0, action_type);
    }
}

#[test]
fn login_complete_takes_no_number() {
    let mut sequence = GameActionSequence::new();
    assert_eq!(
        crate::GameAction::parse(&crate::build_login_complete())
            .unwrap()
            .sequence,
        0
    );
    assert_eq!(sequence.advance(), 1);
}
