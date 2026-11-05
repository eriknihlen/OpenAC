//! The retail identity of each action: which input map (group) and action
//! id the game's key map gives it, the scope and activation that follow
//! from it, and the character-option id of the option toggles.

use std::collections::HashMap;
use std::sync::OnceLock;

use super::action::InputAction;
use super::chord::{ActivationType, InputScope};

use InputAction as A;

/// A retail action: (input map id, action id).
pub type RetailIdentity = (u32, u32);

/// Every mapped action in table order (the emote group is
/// [`EMOTE_INPUT_MAP`], numbered from [`FIRST_EMOTE_ACTION`] in
/// [`EMOTES`] order, between the magic-combat and item-selection groups).
const BEFORE_EMOTES: &[(u32, u32, InputAction)] = &[
    (0x4, 0x29, A::MovementForward),
    (0x4, 0x2A, A::MovementBackup),
    (0x4, 0x2B, A::MovementStop),
    (0x4, 0x2C, A::MovementStrafeRight),
    (0x4, 0x2D, A::MovementStrafeLeft),
    (0x4, 0x2E, A::MovementTurnRight),
    (0x4, 0x2F, A::MovementTurnLeft),
    (0x4, 0x30, A::MovementRunLock),
    (0x4, 0x31, A::MovementJump),
    (0x4, 0x32, A::MovementWalkMode),
    (0x4, 0x10000094, A::Ready),
    (0x4, 0x10000095, A::Crouch),
    (0x4, 0x10000096, A::Sitting),
    (0x4, 0x10000097, A::Sleeping),
    (0x5, 0x33, A::CameraMoveToward),
    (0x5, 0x34, A::CameraMoveAway),
    (0x5, 0x35, A::CameraRotateLeft),
    (0x5, 0x36, A::CameraRotateRight),
    (0x5, 0x37, A::CameraRotateUp),
    (0x5, 0x38, A::CameraRotateDown),
    (0x5, 0x39, A::CameraViewDefault),
    (0x5, 0x3A, A::CameraViewFirstPerson),
    (0x5, 0x3B, A::CameraViewLookDown),
    (0x5, 0x3C, A::CameraViewMapMode),
    (0x5, 0x3D, A::CameraInstantMouseLook),
    (0x5, 0x3E, A::CameraActivateAlternateMode),
    (0x6, 0x33, A::CameraAlternateMoveToward),
    (0x6, 0x34, A::CameraAlternateMoveAway),
    (0x6, 0x35, A::CameraAlternateRotateLeft),
    (0x6, 0x36, A::CameraAlternateRotateRight),
    (0x6, 0x37, A::CameraAlternateRotateUp),
    (0x6, 0x38, A::CameraAlternateRotateDown),
    (0x6, 0x39, A::CameraAlternateViewDefault),
    (0x6, 0x3A, A::CameraAlternateViewFirstPerson),
    (0x6, 0x3B, A::CameraAlternateViewLookDown),
    (0x6, 0x3C, A::CameraAlternateViewMapMode),
    (0x10000002, 0x1000005A, A::CombatToggleCombat),
    (0x10000003, 0x1000005B, A::CombatDecreaseAttackPower),
    (0x10000003, 0x1000005C, A::CombatIncreaseAttackPower),
    (0x10000003, 0x1000005D, A::CombatLowAttack),
    (0x10000003, 0x1000005E, A::CombatMediumAttack),
    (0x10000003, 0x1000005F, A::CombatHighAttack),
    (0x10000004, 0x100000EF, A::CombatDecreaseMissileAccuracy),
    (0x10000004, 0x100000F0, A::CombatIncreaseMissileAccuracy),
    (0x10000004, 0x100000F1, A::CombatAimLow),
    (0x10000004, 0x100000F2, A::CombatAimMedium),
    (0x10000004, 0x100000F3, A::CombatAimHigh),
    (0x10000005, 0x10000060, A::CombatCastCurrentSpell),
    (0x10000005, 0x10000061, A::CombatPrevSpell),
    (0x10000005, 0x10000062, A::CombatNextSpell),
    (0x10000005, 0x10000063, A::CombatPrevSpellTab),
    (0x10000005, 0x10000064, A::CombatNextSpellTab),
    (0x10000005, 0x10000065, A::UseSpellSlot_1),
    (0x10000005, 0x10000066, A::UseSpellSlot_2),
    (0x10000005, 0x10000067, A::UseSpellSlot_3),
    (0x10000005, 0x10000068, A::UseSpellSlot_4),
    (0x10000005, 0x10000069, A::UseSpellSlot_5),
    (0x10000005, 0x1000006A, A::UseSpellSlot_6),
    (0x10000005, 0x1000006B, A::UseSpellSlot_7),
    (0x10000005, 0x1000006C, A::UseSpellSlot_8),
    (0x10000005, 0x1000006D, A::UseSpellSlot_9),
    (0x10000005, 0x1000006E, A::UseSpellSlot_10),
    (0x10000005, 0x1000006F, A::UseSpellSlot_11),
    (0x10000005, 0x10000070, A::UseSpellSlot_12),
    (0x10000005, 0x10000102, A::CombatFirstSpell),
    (0x10000005, 0x10000103, A::CombatLastSpell),
    (0x10000005, 0x10000104, A::CombatFirstSpellTab),
    (0x10000005, 0x10000105, A::CombatLastSpellTab),
];

/// The emote group's input map.
pub const EMOTE_INPUT_MAP: u32 = 0x1000_0006;
/// The first emote's action id.
pub const FIRST_EMOTE_ACTION: u32 = 0x1000_0098;

/// The emotes, in action-id order.
const EMOTES: &[InputAction] = &[
    A::EmoteAfkState,
    A::EmoteAkimbo,
    A::EmoteAToyotState,
    A::EmoteAkimboState,
    A::EmoteAtEaseState,
    A::EmoteBeckon,
    A::EmoteBeSeeingYou,
    A::EmoteBlowKiss,
    A::EmoteBowDeep,
    A::EmoteBowDeepState,
    A::Cheer,
    A::EmoteClapHands,
    A::EmoteClapHandsState,
    A::EmoteCringe,
    A::EmoteCrossArmsState,
    A::Cry,
    A::EmoteCurtseyState,
    A::EmoteDrudgeDance,
    A::EmoteDrudgeDanceState,
    A::EmoteHaveASeat,
    A::EmoteHaveASeatState,
    A::EmoteHeartyLaugh,
    A::EmoteHelper,
    A::EmoteKneel,
    A::EmoteKneelState,
    A::EmoteKnock,
    A::Laugh,
    A::EmoteLeanState,
    A::EmoteMeditateState,
    A::EmoteMimeDrinking,
    A::EmoteMimeEating,
    A::EmoteMock,
    A::EmoteNod,
    A::EmoteNudgeLeft,
    A::EmoteNudgeRight,
    A::EmotePlead,
    A::EmotePleadState,
    A::EmotePoint,
    A::PointState,
    A::EmotePointDown,
    A::EmotePointDownState,
    A::EmotePointLeft,
    A::EmotePointLeftState,
    A::EmotePointRight,
    A::EmotePointRightState,
    A::EmotePossumState,
    A::EmotePray,
    A::EmotePrayState,
    A::EmoteReadState,
    A::EmoteSalute,
    A::EmoteSaluteState,
    A::EmoteScanHorizon,
    A::EmoteScratchHead,
    A::EmoteScratchHeadState,
    A::EmoteShakeFist,
    A::EmoteShakeFistState,
    A::EmoteShakeHead,
    A::EmoteShiver,
    A::EmoteShiverState,
    A::EmoteShoo,
    A::EmoteShrug,
    A::EmoteSitState,
    A::EmoteSitBackState,
    A::EmoteSitCrossleggedState,
    A::EmoteSlouch,
    A::EmoteSlouchState,
    A::EmoteSmackHead,
    A::EmoteSnowAngelState,
    A::EmoteSpit,
    A::EmoteSurrender,
    A::EmoteSurrenderState,
    A::EmoteTalkToTheHandState,
    A::EmoteTapFoot,
    A::EmoteTapFootState,
    A::EmoteTeapot,
    A::EmoteThinkerState,
    A::EmoteWarmHands,
    A::Wave,
    A::EmoteWaveState,
    A::EmoteWaveLow,
    A::EmoteWaveHigh,
    A::EmoteWinded,
    A::EmoteWindedState,
    A::EmoteWoah,
    A::EmoteWoahState,
    A::EmoteYawnAndStretch,
    A::EmoteYmca,
];

const AFTER_EMOTES: &[(u32, u32, InputAction)] = &[
    (0x10000007, 0x1000002A, A::SelectionSelf),
    (0x10000007, 0x1000002C, A::SelectionPlaceInInventory),
    (0x10000007, 0x1000002D, A::SelectionSplitStack),
    (0x10000007, 0x1000002E, A::SelectionPreviousSelection),
    (0x10000007, 0x1000002F, A::SelectionClosestCompassItem),
    (0x10000007, 0x10000030, A::SelectionPreviousCompassItem),
    (0x10000007, 0x10000031, A::SelectionNextCompassItem),
    (0x10000007, 0x10000032, A::SelectionClosestItem),
    (0x10000007, 0x10000033, A::SelectionPreviousItem),
    (0x10000007, 0x10000034, A::SelectionNextItem),
    (0x10000007, 0x10000035, A::SelectionClosestMonster),
    (0x10000007, 0x10000036, A::SelectionPreviousMonster),
    (0x10000007, 0x10000037, A::SelectionNextMonster),
    (0x10000007, 0x10000038, A::SelectionLastAttacker),
    (0x10000007, 0x10000039, A::SelectionClosestPlayer),
    (0x10000007, 0x1000003A, A::SelectionPreviousPlayer),
    (0x10000007, 0x1000003B, A::SelectionNextPlayer),
    (0x10000007, 0x1000003C, A::SelectionPreviousFellow),
    (0x10000007, 0x1000003D, A::SelectionNextFellow),
    (0x10000007, 0x1000003E, A::SelectionUseClosestUnopenedCorpse),
    (0x10000007, 0x1000003F, A::SelectionUseNextUnopenedCorpse),
    (0x10000007, 0x10000040, A::SelectionGiveToTarget),
    (0x10000007, 0x10000041, A::SelectionDrop),
    (0x10000007, 0x1000011C, A::SelectionPlaceInMainPack),
    (0x10000007, 0x10000121, A::SelectionClosestUnopenedCorpse),
    (0x10000007, 0x10000122, A::SelectionNextUnopenedCorpse),
    (0x10000009, 0x55, A::CaptureScreenshot),
    (0x10000009, 0x7B, A::ToggleHelp),
    (0x10000009, 0x7C, A::TogglePluginManager),
    (0x10000009, 0x10000003, A::ToggleAbuseReportingPanel),
    (0x10000009, 0x10000005, A::ToggleCharacterInfoPanel),
    (0x10000009, 0x10000006, A::TogglePositiveMagicPanel),
    (0x10000009, 0x10000007, A::ToggleNegativeMagicPanel),
    (0x10000009, 0x10000009, A::ToggleLinkStatusPanel),
    (0x10000009, 0x1000000B, A::ToggleUrgentAssistancePanel),
    (0x10000009, 0x1000000C, A::ToggleVitaePanel),
    (0x10000009, 0x1000000D, A::ToggleSocialPanel),
    (0x10000009, 0x1000000E, A::ToggleAllegiancePanel),
    (0x10000009, 0x1000000F, A::ToggleFellowshipPanel),
    (0x10000009, 0x10000010, A::ToggleSpellManagementPanel),
    (0x10000009, 0x10000011, A::ToggleSpellbookPanel),
    (0x10000009, 0x10000012, A::ToggleSpellComponentsPanel),
    (0x10000009, 0x10000013, A::ToggleCharacterDetailPanel),
    (0x10000009, 0x10000014, A::ToggleAttributesPanel),
    (0x10000009, 0x10000015, A::ToggleSkillsPanel),
    (0x10000009, 0x10000016, A::ToggleWorldPanel),
    (0x10000009, 0x10000017, A::ToggleMapPage),
    (0x10000009, 0x10000018, A::ToggleHousePage),
    (0x10000009, 0x1000001A, A::ToggleOptionsPanel),
    (0x10000009, 0x10000019, A::ToggleInventoryPanel),
    (0x10000009, 0x1000001B, A::ToggleGameplayOptionsPage),
    (0x10000009, 0x1000001C, A::ToggleCharacterSettingsPage),
    (0x10000009, 0x1000001D, A::ToggleConfigurationPage),
    (0x10000009, 0x1000001E, A::ToggleCompass),
    (0x10000009, 0x1000001F, A::ToggleKeyboardConfiguration),
    (0x10000009, 0x10000114, A::ToggleFloatingChatWindow1),
    (0x10000009, 0x10000115, A::ToggleFloatingChatWindow2),
    (0x10000009, 0x10000116, A::ToggleFloatingChatWindow3),
    (0x10000009, 0x10000117, A::ToggleFloatingChatWindow4),
    (0x10000009, 0x10000025, A::UseSelected),
    (0x10000009, 0x10000026, A::LOGOUT),
    (0x10000009, 0x1000002B, A::SelectionExamine),
    (0x10000009, 0x10000118, A::ToggleFriendsPage),
    (0x10000009, 0x1000011A, A::ToggleCharacterTitlesPage),
    (0x10000009, 0x10000127, A::ToggleQuestDetailPanel),
    (0x10000009, 0x10000128, A::ToggleQuestJournalPage),
    (0x10000009, 0x10000129, A::ToggleJournalPageList),
    (0x10000009, 0x1000012E, A::ToggleContractsPage),
    (0x1000000A, 0x10000020, A::ChatMonarchReply),
    (0x1000000A, 0x10000021, A::ChatPatronReply),
    (0x1000000A, 0x10000022, A::ChatReply),
    (0x1000000A, 0x10000023, A::EnterChatMode),
    (0x1000000A, 0x10000028, A::ChatStartCommand),
    (0x1000000A, 0x10000119, A::ChatTellToSelected),
    (0x1000000D, 0x10000024, A::ToggleChatEntry),
    (0x1000000C, 0x10000042, A::UseQuickSlot_1),
    (0x1000000C, 0x10000043, A::UseQuickSlot_2),
    (0x1000000C, 0x10000044, A::UseQuickSlot_3),
    (0x1000000C, 0x10000045, A::UseQuickSlot_4),
    (0x1000000C, 0x10000046, A::UseQuickSlot_5),
    (0x1000000C, 0x10000047, A::UseQuickSlot_6),
    (0x1000000C, 0x10000048, A::UseQuickSlot_7),
    (0x1000000C, 0x10000049, A::UseQuickSlot_8),
    (0x1000000C, 0x1000004A, A::UseQuickSlot_9),
    (0x1000000C, 0x1000004B, A::UseQuickSlot_10),
    (0x1000000C, 0x1000004C, A::UseQuickSlot_11),
    (0x1000000C, 0x1000004D, A::UseQuickSlot_12),
    (0x1000000C, 0x1000004E, A::SelectQuickSlot_1),
    (0x1000000C, 0x1000004F, A::SelectQuickSlot_2),
    (0x1000000C, 0x10000050, A::SelectQuickSlot_3),
    (0x1000000C, 0x10000051, A::SelectQuickSlot_4),
    (0x1000000C, 0x10000052, A::SelectQuickSlot_5),
    (0x1000000C, 0x10000053, A::SelectQuickSlot_6),
    (0x1000000C, 0x10000054, A::SelectQuickSlot_7),
    (0x1000000C, 0x10000055, A::SelectQuickSlot_8),
    (0x1000000C, 0x10000056, A::SelectQuickSlot_9),
    (0x1000000C, 0x1000010D, A::CreateShortcut),
    (0x1000000C, 0x10000132, A::UseQuickSlot_13),
    (0x1000000C, 0x10000133, A::UseQuickSlot_14),
    (0x1000000C, 0x10000134, A::UseQuickSlot_15),
    (0x1000000C, 0x10000135, A::UseQuickSlot_16),
    (0x1000000C, 0x10000136, A::UseQuickSlot_17),
    (0x1000000C, 0x10000137, A::UseQuickSlot_18),
    (
        0x10000008,
        0x10000071,
        A::ToggleCharacterOptionAutoRepeatAttack,
    ),
    (
        0x10000008,
        0x10000072,
        A::ToggleCharacterOptionIgnoreAllegianceRequests,
    ),
    (
        0x10000008,
        0x10000073,
        A::ToggleCharacterOptionIgnoreFellowshipRequests,
    ),
    (
        0x10000008,
        0x10000074,
        A::ToggleCharacterOptionIgnoreTradeRequests,
    ),
    (
        0x10000008,
        0x10000076,
        A::ToggleCharacterOptionPersistentAtDay,
    ),
    (0x10000008, 0x10000077, A::ToggleCharacterOptionAllowGive),
    (
        0x10000008,
        0x10000078,
        A::ToggleCharacterOptionViewCombatTarget,
    ),
    (0x10000008, 0x10000079, A::ToggleCharacterOptionShowTooltips),
    (0x10000008, 0x1000007A, A::ToggleCharacterOptionUseDeception),
    (0x10000008, 0x1000007B, A::ToggleCharacterOptionToggleRun),
    (
        0x10000008,
        0x1000007C,
        A::ToggleCharacterOptionStayInChatMode,
    ),
    (
        0x10000008,
        0x1000007D,
        A::ToggleCharacterOptionAdvancedCombatUi,
    ),
    (0x10000008, 0x1000007E, A::ToggleCharacterOptionAutoTarget),
    (
        0x10000008,
        0x1000007F,
        A::ToggleCharacterOptionVividTargetingIndicator,
    ),
    (
        0x10000008,
        0x10000080,
        A::ToggleCharacterOptionFellowshipShareXp,
    ),
    (
        0x10000008,
        0x10000081,
        A::ToggleCharacterOptionAcceptLootPermits,
    ),
    (
        0x10000008,
        0x10000082,
        A::ToggleCharacterOptionFellowshipShareLoot,
    ),
    (
        0x10000008,
        0x10000083,
        A::ToggleCharacterOptionFellowshipAutoAcceptRequests,
    ),
    (
        0x10000008,
        0x10000085,
        A::ToggleCharacterOptionCoordinatesOnRadar,
    ),
    (
        0x10000008,
        0x10000086,
        A::ToggleCharacterOptionSpellDuration,
    ),
    (
        0x10000008,
        0x10000087,
        A::ToggleCharacterOptionDisableHouseRestrictionEffects,
    ),
    (
        0x10000008,
        0x10000088,
        A::ToggleCharacterOptionDragItemOnPlayerOpensSecureTrade,
    ),
    (
        0x10000008,
        0x10000089,
        A::ToggleCharacterOptionDisplayAllegianceLogonNotifications,
    ),
    (
        0x10000008,
        0x1000008A,
        A::ToggleCharacterOptionUseChargeAttack,
    ),
    (
        0x10000008,
        0x1000008B,
        A::ToggleCharacterOptionUseCraftSuccessDialog,
    ),
    (
        0x10000008,
        0x1000008C,
        A::ToggleCharacterOptionListenToAllegianceChat,
    ),
    (
        0x10000008,
        0x1000008D,
        A::ToggleCharacterOptionDisplayDateOfBirth,
    ),
    (0x10000008, 0x1000008E, A::ToggleCharacterOptionDisplayAge),
    (
        0x10000008,
        0x1000008F,
        A::ToggleCharacterOptionDisplayChessRank,
    ),
    (
        0x10000008,
        0x10000090,
        A::ToggleCharacterOptionDisplayFishingSkill,
    ),
    (
        0x10000008,
        0x10000091,
        A::ToggleCharacterOptionDisplayNumberDeaths,
    ),
    (
        0x10000008,
        0x10000092,
        A::ToggleCharacterOptionDisplayTimeStamps,
    ),
    (
        0x10000008,
        0x10000093,
        A::ToggleCharacterOptionSalvageMultiple,
    ),
    (
        0x10000008,
        0x1000010E,
        A::ToggleCharacterOptionListenToGeneralChat,
    ),
    (
        0x10000008,
        0x1000010F,
        A::ToggleCharacterOptionListenToTradeChat,
    ),
    (
        0x10000008,
        0x10000110,
        A::ToggleCharacterOptionListenToLfgChat,
    ),
    (
        0x10000008,
        0x10000112,
        A::ToggleCharacterOptionListenToRoleplayChat,
    ),
    (
        0x10000008,
        0x1000011B,
        A::ToggleCharacterOptionDisplayNumberCharacterTitles,
    ),
    (
        0x10000008,
        0x1000011D,
        A::ToggleCharacterOptionMainPackPreferred,
    ),
    (
        0x10000008,
        0x1000011E,
        A::ToggleCharacterOptionLeadMissileTargets,
    ),
    (
        0x10000008,
        0x1000011F,
        A::ToggleCharacterOptionUseFastMissiles,
    ),
    (
        0x10000008,
        0x10000120,
        A::ToggleCharacterOptionFilterLanguage,
    ),
    (
        0x10000008,
        0x10000123,
        A::ToggleCharacterOptionConfirmVolatileRareUse,
    ),
    (
        0x10000008,
        0x10000125,
        A::ToggleCharacterOptionListenToSocietyChat,
    ),
    (0x10000008, 0x1000012A, A::ToggleCharacterOptionShowHelm),
    (
        0x10000008,
        0x1000012C,
        A::ToggleCharacterOptionDisableDistanceFog,
    ),
    (0x10000008, 0x1000012F, A::ToggleCharacterOptionShowCloak),
    (
        0x10000008,
        0x1000013E,
        A::ToggleCharacterOptionSideBySideVitals,
    ),
];

struct Tables {
    ordered: Vec<(RetailIdentity, InputAction)>,
    forward: HashMap<RetailIdentity, InputAction>,
    reverse: HashMap<InputAction, RetailIdentity>,
}

fn tables() -> &'static Tables {
    static TABLES: OnceLock<Tables> = OnceLock::new();
    TABLES.get_or_init(|| {
        let mut ordered: Vec<(RetailIdentity, InputAction)> = Vec::new();
        for &(map, action_id, action) in BEFORE_EMOTES {
            ordered.push(((map, action_id), action));
        }
        for (i, &action) in EMOTES.iter().enumerate() {
            ordered.push(((EMOTE_INPUT_MAP, FIRST_EMOTE_ACTION + i as u32), action));
        }
        for &(map, action_id, action) in AFTER_EMOTES {
            ordered.push(((map, action_id), action));
        }
        let forward = ordered.iter().copied().collect();
        let reverse = ordered.iter().map(|&(id, a)| (a, id)).collect();
        Tables {
            ordered,
            forward,
            reverse,
        }
    })
}

/// Every mapped (identity, action), in table order.
#[must_use]
pub fn all() -> &'static [(RetailIdentity, InputAction)] {
    &tables().ordered
}

/// The action a retail identity names.
#[must_use]
pub fn resolve(input_map: u32, action_id: u32) -> Option<InputAction> {
    tables().forward.get(&(input_map, action_id)).copied()
}

/// An action's retail identity (`None` for the client's own actions).
#[must_use]
pub fn retail_identity(action: InputAction) -> Option<RetailIdentity> {
    tables().reverse.get(&action).copied()
}

/// The scope an input map's bindings live in.
#[must_use]
pub fn scope_for_input_map(input_map: u32) -> InputScope {
    match input_map {
        0x0000_0006 => InputScope::Camera,
        0x1000_0003 => InputScope::MeleeCombat,
        0x1000_0004 => InputScope::MissileCombat,
        0x1000_0005 => InputScope::MagicCombat,
        _ => InputScope::Game,
    }
}

/// How a retail action fires: held for walk mode, the camera moves and
/// rotations, instant mouse look and the alternate-mode key, the melee
/// attack heights and the missile aim heights; else on the press.
#[must_use]
pub fn activation_for(input_map: u32, action_id: u32) -> ActivationType {
    if input_map == 0x4 && action_id == 0x32 {
        return ActivationType::Hold;
    }
    if matches!(input_map, 0x5 | 0x6) && (0x33..=0x38).contains(&action_id) {
        return ActivationType::Hold;
    }
    if input_map == 0x5 && matches!(action_id, 0x3D | 0x3E) {
        return ActivationType::Hold;
    }
    if input_map == 0x1000_0003 && (0x1000_005D..=0x1000_005F).contains(&action_id) {
        return ActivationType::Hold;
    }
    if input_map == 0x1000_0004 && (0x1000_00F1..=0x1000_00F3).contains(&action_id) {
        return ActivationType::Hold;
    }
    ActivationType::Press
}

/// The character option a toggle action flips.
#[must_use]
pub fn character_option_id(action: InputAction) -> Option<u32> {
    let (map, id) = retail_identity(action)?;
    if map != 0x1000_0008 {
        return None;
    }
    Some(match id {
        0x1000_0071..=0x1000_0074 | 0x1000_0076..=0x1000_0083 | 0x1000_0085..=0x1000_0093 => {
            id - 0x1000_0071
        }
        0x1000_010E => 0x23,
        0x1000_010F => 0x24,
        0x1000_0110 => 0x25,
        0x1000_0112 => 0x26,
        0x1000_011B => 0x28,
        0x1000_011D => 0x29,
        0x1000_011E => 0x2A,
        0x1000_011F => 0x2B,
        0x1000_0120 => 0x2C,
        0x1000_0123 => 0x2D,
        0x1000_0125 => 0x2E,
        0x1000_012A => 0x2F,
        0x1000_012C => 0x30,
        0x1000_012F => 0x32,
        0x1000_013E => 0x13,
        _ => return None,
    })
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Input/RetailActionIdentityRoundTripTests.cs`
    //! (the table half; the dat half is `action_map`).

    use super::*;

    #[test]
    fn every_identity_round_trips_and_no_action_appears_twice() {
        assert_eq!(all().len(), 220 - 1 + 87);
        for &((map, id), action) in all() {
            assert_eq!(resolve(map, id), Some(action));
            assert_eq!(retail_identity(action), Some((map, id)));
        }
        assert_eq!(tables().reverse.len(), all().len());
    }

    #[test]
    fn emotes_are_numbered_from_the_first_emote_id() {
        assert_eq!(
            resolve(EMOTE_INPUT_MAP, FIRST_EMOTE_ACTION),
            Some(A::EmoteAfkState)
        );
        assert_eq!(resolve(EMOTE_INPUT_MAP, 0x1000_00A2), Some(A::Cheer));
        assert_eq!(
            retail_identity(A::EmoteYmca),
            Some((EMOTE_INPUT_MAP, FIRST_EMOTE_ACTION + 86))
        );
    }

    #[test]
    fn scopes_activations_and_options_follow_the_input_map() {
        assert_eq!(scope_for_input_map(0x6), InputScope::Camera);
        assert_eq!(scope_for_input_map(0x1000_0005), InputScope::MagicCombat);
        assert_eq!(scope_for_input_map(0x4), InputScope::Game);
        assert_eq!(activation_for(0x4, 0x32), ActivationType::Hold);
        assert_eq!(activation_for(0x4, 0x31), ActivationType::Press);
        assert_eq!(activation_for(0x6, 0x35), ActivationType::Hold);
        assert_eq!(activation_for(0x5, 0x3D), ActivationType::Hold);
        assert_eq!(activation_for(0x5, 0x39), ActivationType::Press);
        assert_eq!(
            activation_for(0x1000_0004, 0x1000_00F2),
            ActivationType::Hold
        );
        assert_eq!(
            character_option_id(A::ToggleCharacterOptionToggleRun),
            Some(0x0A)
        );
        assert_eq!(
            character_option_id(A::ToggleCharacterOptionSideBySideVitals),
            Some(0x13)
        );
        assert_eq!(character_option_id(A::MovementForward), None);
        assert_eq!(retail_identity(A::AcdreamToggleFlyMode), None);
    }
}
