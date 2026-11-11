//! The emote keys: each of the key map's 87 emote actions plays one motion
//! as the player's own (OpenAC `RetailEmoteMotionTable`). The motions are
//! the server's numbering, which sits three above the older client's own
//! table from Helper onward.

use ac_motion::motion_command as mc;
use ac_ui::input::InputAction;
use ac_ui::input::identity::{EMOTE_INPUT_MAP, FIRST_EMOTE_ACTION, retail_identity};

/// The emote motions in action-id order.
const MOTIONS: [u32; 87] = [
    mc::AFK_STATE,
    mc::AKIMBO,
    mc::ATOYOT,
    mc::AKIMBO_STATE,
    mc::AT_EASE_STATE,
    mc::BECKON,
    mc::BE_SEEING_YOU,
    mc::BLOW_KISS,
    mc::BOW_DEEP,
    mc::BOW_DEEP_STATE,
    mc::CHEER,
    mc::CLAP_HANDS,
    mc::CLAP_HANDS_STATE,
    mc::CRINGE,
    mc::CROSS_ARMS_STATE,
    mc::CRY,
    mc::CURTSEY_STATE,
    mc::DRUDGE_DANCE,
    mc::DRUDGE_DANCE_STATE,
    mc::HAVE_A_SEAT,
    mc::HAVE_A_SEAT_STATE,
    mc::HEARTY_LAUGH,
    mc::HELPER,
    mc::KNEEL,
    mc::KNEEL_STATE,
    mc::KNOCK,
    mc::LAUGH,
    mc::LEAN_STATE,
    mc::MEDITATE_STATE,
    mc::MIME_DRINK,
    mc::MIME_EAT,
    mc::MOCK,
    mc::NOD,
    mc::NUDGE_LEFT,
    mc::NUDGE_RIGHT,
    mc::PLEAD,
    mc::PLEAD_STATE,
    mc::POINT,
    mc::POINT_STATE,
    mc::POINT_DOWN,
    mc::POINT_DOWN_STATE,
    mc::POINT_LEFT,
    mc::POINT_LEFT_STATE,
    mc::POINT_RIGHT,
    mc::POINT_RIGHT_STATE,
    mc::POSSUM_STATE,
    mc::PRAY,
    mc::PRAY_STATE,
    mc::READ_STATE,
    mc::SALUTE,
    mc::SALUTE_STATE,
    mc::SCAN_HORIZON,
    mc::SCRATCH_HEAD,
    mc::SCRATCH_HEAD_STATE,
    mc::SHAKE_FIST,
    mc::SHAKE_FIST_STATE,
    mc::SHAKE_HEAD,
    mc::SHIVER,
    mc::SHIVER_STATE,
    mc::SHOO,
    mc::SHRUG,
    mc::SIT_STATE,
    mc::SIT_BACK_STATE,
    mc::SIT_CROSSLEGGED_STATE,
    mc::SLOUCH,
    mc::SLOUCH_STATE,
    mc::SMACK_HEAD,
    mc::SNOW_ANGEL_STATE,
    mc::SPIT,
    mc::SURRENDER,
    mc::SURRENDER_STATE,
    mc::TALKTOTHE_HAND_STATE,
    mc::TAP_FOOT,
    mc::TAP_FOOT_STATE,
    mc::TEAPOT,
    mc::THINKER_STATE,
    mc::WARM_HANDS,
    mc::WAVE,
    mc::WAVE_STATE,
    mc::WAVE_LOW,
    mc::WAVE_HIGH,
    mc::WINDED,
    mc::WINDED_STATE,
    mc::WOAH,
    mc::WOAH_STATE,
    mc::YAWN_STRETCH,
    mc::YMCA,
];

/// How many emotes.
pub const COUNT: usize = MOTIONS.len();

/// The motion an emote action plays (`None` for any other action).
#[must_use]
pub fn motion(action: InputAction) -> Option<u32> {
    let (map, id) = retail_identity(action)?;
    if map != EMOTE_INPUT_MAP {
        return None;
    }
    let index = id.checked_sub(FIRST_EMOTE_ACTION)?;
    MOTIONS.get(usize::try_from(index).ok()?).copied()
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Input/RetailEmoteMotionTableTests.cs`.

    use super::*;
    use InputAction as A;

    #[test]
    fn every_retail_emote_action_has_a_motion() {
        assert_eq!(COUNT, 87);
        let emotes: Vec<InputAction> = InputAction::ALL
            .iter()
            .copied()
            .filter(|a| retail_identity(*a).is_some_and(|(map, _)| map == EMOTE_INPUT_MAP))
            .collect();
        assert_eq!(emotes.len(), 87);
        for emote in emotes {
            assert!(motion(emote).is_some(), "{emote:?}");
        }
    }

    #[test]
    fn representative_actions_match_the_retail_values() {
        for (action, expected) in [
            (A::EmoteAfkState, 0x4300_011B),
            (A::Cheer, 0x1300_004C),
            (A::Cry, 0x1300_007F),
            (A::Laugh, 0x1300_0080),
            (A::PointState, 0x4300_00F0),
            (A::Wave, 0x1300_0087),
            (A::EmoteYmca, 0x1200_009B),
        ] {
            assert_eq!(motion(action), Some(expected), "{action:?}");
        }
    }

    /// The later emotes in the server's numbering (the older client's
    /// numbers would send At Ease as ReadState and Helper as nothing).
    #[test]
    fn later_emotes_use_the_servers_numbering() {
        for (action, expected) in [
            (A::EmoteAtEaseState, 0x4300_0149),
            (A::EmoteReadState, 0x4300_0146),
            (A::EmoteHelper, 0x1300_0135),
            (A::EmoteSitState, 0x4300_013D),
            (A::EmoteSitCrossleggedState, 0x4300_013E),
            (A::EmoteSitBackState, 0x4300_013F),
            (A::EmoteNudgeLeft, 0x1300_014A),
            (A::EmoteDrudgeDance, 0x1300_0151),
            (A::EmoteThinkerState, 0x4300_0147),
        ] {
            assert_eq!(motion(action), Some(expected), "{action:?}");
        }
    }

    #[test]
    fn other_actions_are_not_emotes() {
        for action in [A::Ready, A::ToggleOptionsPanel, A::UseQuickSlot_1] {
            assert_eq!(motion(action), None, "{action:?}");
        }
    }
}
