//! Motion command values: stances, cycles, modifiers, actions, emotes and
//! UI commands, numbered as the server and the dat files number them.
//!
//! The top byte classifies a command: bit 31 a stance (style), bit 30 a
//! looping state (substate), bit 29 a modifier, bit 28 an action; the low
//! 16 bits are what the wire carries. Generated from the game's motion
//! command enum; do not edit by hand.

/// `Invalid`.
pub const INVALID: u32 = 0x00000000;
/// `Cancel`.
pub const CANCEL: u32 = 0x080000A2;
/// `CreateShortcutToSelected`.
pub const CREATE_SHORTCUT_TO_SELECTED: u32 = 0x080000A9;
/// `EnterChat`.
pub const ENTER_CHAT: u32 = 0x080000B5;
/// `ToggleChat`.
pub const TOGGLE_CHAT: u32 = 0x080000B6;
/// `SavePosition`.
pub const SAVE_POSITION: u32 = 0x080000B7;
/// `UseSelected`.
pub const USE_SELECTED: u32 = 0x090000A3;
/// `AutosortSelected`.
pub const AUTOSORT_SELECTED: u32 = 0x090000A4;
/// `DropSelected`.
pub const DROP_SELECTED: u32 = 0x090000A5;
/// `GiveSelected`.
pub const GIVE_SELECTED: u32 = 0x090000A6;
/// `SplitSelected`.
pub const SPLIT_SELECTED: u32 = 0x090000A7;
/// `ExamineSelected`.
pub const EXAMINE_SELECTED: u32 = 0x090000A8;
/// `PreviousCompassItem`.
pub const PREVIOUS_COMPASS_ITEM: u32 = 0x090000AA;
/// `NextCompassItem`.
pub const NEXT_COMPASS_ITEM: u32 = 0x090000AB;
/// `ClosestCompassItem`.
pub const CLOSEST_COMPASS_ITEM: u32 = 0x090000AC;
/// `PreviousSelection`.
pub const PREVIOUS_SELECTION: u32 = 0x090000AD;
/// `LastAttacker`.
pub const LAST_ATTACKER: u32 = 0x090000AE;
/// `PreviousFellow`.
pub const PREVIOUS_FELLOW: u32 = 0x090000AF;
/// `NextFellow`.
pub const NEXT_FELLOW: u32 = 0x090000B0;
/// `ToggleCombat`.
pub const TOGGLE_COMBAT: u32 = 0x090000B1;
/// `OptionsPanel`.
pub const OPTIONS_PANEL: u32 = 0x090000B8;
/// `ResetView`.
pub const RESET_VIEW: u32 = 0x090000B9;
/// `FloorView`.
pub const FLOOR_VIEW: u32 = 0x090000C0;
/// `PreviousItem`.
pub const PREVIOUS_ITEM: u32 = 0x090000C2;
/// `NextItem`.
pub const NEXT_ITEM: u32 = 0x090000C3;
/// `ClosestItem`.
pub const CLOSEST_ITEM: u32 = 0x090000C4;
/// `MapView`.
pub const MAP_VIEW: u32 = 0x090000C6;
/// `AutoRun`.
pub const AUTO_RUN: u32 = 0x090000C7;
/// `DecreasePowerSetting`.
pub const DECREASE_POWER_SETTING: u32 = 0x090000C8;
/// `IncreasePowerSetting`.
pub const INCREASE_POWER_SETTING: u32 = 0x090000C9;
/// `FirstPersonView`.
pub const FIRST_PERSON_VIEW: u32 = 0x090000D5;
/// `AllegiancePanel`.
pub const ALLEGIANCE_PANEL: u32 = 0x090000D6;
/// `FellowshipPanel`.
pub const FELLOWSHIP_PANEL: u32 = 0x090000D7;
/// `SpellbookPanel`.
pub const SPELLBOOK_PANEL: u32 = 0x090000D8;
/// `SpellComponentsPanel`.
pub const SPELL_COMPONENTS_PANEL: u32 = 0x090000D9;
/// `HousePanel`.
pub const HOUSE_PANEL: u32 = 0x090000DA;
/// `AttributesPanel`.
pub const ATTRIBUTES_PANEL: u32 = 0x090000DB;
/// `SkillsPanel`.
pub const SKILLS_PANEL: u32 = 0x090000DC;
/// `MapPanel`.
pub const MAP_PANEL: u32 = 0x090000DD;
/// `InventoryPanel`.
pub const INVENTORY_PANEL: u32 = 0x090000DE;
/// `CaptureScreenshotToFile`.
pub const CAPTURE_SCREENSHOT_TO_FILE: u32 = 0x090000E7;
/// `AutoCreateShortcuts`.
pub const AUTO_CREATE_SHORTCUTS: u32 = 0x090000FE;
/// `AutoRepeatAttacks`.
pub const AUTO_REPEAT_ATTACKS: u32 = 0x090000FF;
/// `AutoTarget`.
pub const AUTO_TARGET: u32 = 0x09000100;
/// `AdvancedCombatInterface`.
pub const ADVANCED_COMBAT_INTERFACE: u32 = 0x09000101;
/// `IgnoreAllegianceRequests`.
pub const IGNORE_ALLEGIANCE_REQUESTS: u32 = 0x09000102;
/// `IgnoreFellowshipRequests`.
pub const IGNORE_FELLOWSHIP_REQUESTS: u32 = 0x09000103;
/// `InvertMouseLook`.
pub const INVERT_MOUSE_LOOK: u32 = 0x09000104;
/// `LetPlayersGiveYouItems`.
pub const LET_PLAYERS_GIVE_YOU_ITEMS: u32 = 0x09000105;
/// `AutoTrackCombatTargets`.
pub const AUTO_TRACK_COMBAT_TARGETS: u32 = 0x09000106;
/// `DisplayTooltips`.
pub const DISPLAY_TOOLTIPS: u32 = 0x09000107;
/// `AttemptToDeceivePlayers`.
pub const ATTEMPT_TO_DECEIVE_PLAYERS: u32 = 0x09000108;
/// `RunAsDefaultMovement`.
pub const RUN_AS_DEFAULT_MOVEMENT: u32 = 0x09000109;
/// `StayInChatModeAfterSend`.
pub const STAY_IN_CHAT_MODE_AFTER_SEND: u32 = 0x0900010A;
/// `RightClickToMouseLook`.
pub const RIGHT_CLICK_TO_MOUSE_LOOK: u32 = 0x0900010B;
/// `VividTargetIndicator`.
pub const VIVID_TARGET_INDICATOR: u32 = 0x0900010C;
/// `SelectSelf`.
pub const SELECT_SELF: u32 = 0x0900010D;
/// `PreviousMonster`.
pub const PREVIOUS_MONSTER: u32 = 0x09000110;
/// `ClosestMonster`.
pub const CLOSEST_MONSTER: u32 = 0x09000111;
/// `NextPlayer`.
pub const NEXT_PLAYER: u32 = 0x09000112;
/// `PreviousPlayer`.
pub const PREVIOUS_PLAYER: u32 = 0x09000113;
/// `ClosestPlayer`.
pub const CLOSEST_PLAYER: u32 = 0x09000114;
/// `TradePanel`.
pub const TRADE_PANEL: u32 = 0x0900011D;
/// `CharacterOptionsPanel`.
pub const CHARACTER_OPTIONS_PANEL: u32 = 0x09000154;
/// `SoundAndGraphicsPanel`.
pub const SOUND_AND_GRAPHICS_PANEL: u32 = 0x09000155;
/// `HelpfulSpellsPanel`.
pub const HELPFUL_SPELLS_PANEL: u32 = 0x09000156;
/// `HarmfulSpellsPanel`.
pub const HARMFUL_SPELLS_PANEL: u32 = 0x09000157;
/// `CharacterInformationPanel`.
pub const CHARACTER_INFORMATION_PANEL: u32 = 0x09000158;
/// `LinkStatusPanel`.
pub const LINK_STATUS_PANEL: u32 = 0x09000159;
/// `VitaePanel`.
pub const VITAE_PANEL: u32 = 0x0900015A;
/// `ShareFellowshipXP`.
pub const SHARE_FELLOWSHIP_XP: u32 = 0x0900015B;
/// `ShareFellowshipLoot`.
pub const SHARE_FELLOWSHIP_LOOT: u32 = 0x0900015C;
/// `AcceptCorpseLooting`.
pub const ACCEPT_CORPSE_LOOTING: u32 = 0x0900015D;
/// `IgnoreTradeRequests`.
pub const IGNORE_TRADE_REQUESTS: u32 = 0x0900015E;
/// `DisableWeather`.
pub const DISABLE_WEATHER: u32 = 0x0900015F;
/// `DisableHouseEffect`.
pub const DISABLE_HOUSE_EFFECT: u32 = 0x09000160;
/// `SideBySideVitals`.
pub const SIDE_BY_SIDE_VITALS: u32 = 0x09000161;
/// `ShowRadarCoordinates`.
pub const SHOW_RADAR_COORDINATES: u32 = 0x09000162;
/// `ShowSpellDurations`.
pub const SHOW_SPELL_DURATIONS: u32 = 0x09000163;
/// `MuteOnLosingFocus`.
pub const MUTE_ON_LOSING_FOCUS: u32 = 0x09000164;
/// `AllegianceChat`.
pub const ALLEGIANCE_CHAT: u32 = 0x09000168;
/// `AutomaticallyAcceptFellowshipRequests`.
pub const AUTOMATICALLY_ACCEPT_FELLOWSHIP_REQUESTS: u32 = 0x09000169;
/// `Reply`.
pub const REPLY: u32 = 0x0900016A;
/// `MonarchReply`.
pub const MONARCH_REPLY: u32 = 0x0900016B;
/// `PatronReply`.
pub const PATRON_REPLY: u32 = 0x0900016C;
/// `ToggleCraftingChanceOfSuccessDialog`.
pub const TOGGLE_CRAFTING_CHANCE_OF_SUCCESS_DIALOG: u32 = 0x0900016D;
/// `UseClosestUnopenedCorpse`.
pub const USE_CLOSEST_UNOPENED_CORPSE: u32 = 0x0900016E;
/// `UseNextUnopenedCorpse`.
pub const USE_NEXT_UNOPENED_CORPSE: u32 = 0x0900016F;
/// `IssueSlashCommand`.
pub const ISSUE_SLASH_COMMAND: u32 = 0x09000170;
/// `MouseLook`.
pub const MOUSE_LOOK: u32 = 0x0C0000C1;
/// `HighAttack`.
pub const HIGH_ATTACK: u32 = 0x0D0000B2;
/// `MediumAttack`.
pub const MEDIUM_ATTACK: u32 = 0x0D0000B3;
/// `LowAttack`.
pub const LOW_ATTACK: u32 = 0x0D0000B4;
/// `CameraLeftRotate`.
pub const CAMERA_LEFT_ROTATE: u32 = 0x0D0000BA;
/// `CameraRightRotate`.
pub const CAMERA_RIGHT_ROTATE: u32 = 0x0D0000BB;
/// `CameraRaise`.
pub const CAMERA_RAISE: u32 = 0x0D0000BC;
/// `CameraLower`.
pub const CAMERA_LOWER: u32 = 0x0D0000BD;
/// `CameraCloser`.
pub const CAMERA_CLOSER: u32 = 0x0D0000BE;
/// `CameraFarther`.
pub const CAMERA_FARTHER: u32 = 0x0D0000BF;
/// `ShiftView`.
pub const SHIFT_VIEW: u32 = 0x0D0000C5;
/// `Hop`.
pub const HOP: u32 = 0x1000004A;
/// `Jumpup`.
pub const JUMPUP: u32 = 0x1000004B;
/// `ChestBeat`.
pub const CHEST_BEAT: u32 = 0x1000004D;
/// `TippedLeft`.
pub const TIPPED_LEFT: u32 = 0x1000004E;
/// `TippedRight`.
pub const TIPPED_RIGHT: u32 = 0x1000004F;
/// `FallDown`.
pub const FALL_DOWN: u32 = 0x10000050;
/// `Twitch1`.
pub const TWITCH1: u32 = 0x10000051;
/// `Twitch2`.
pub const TWITCH2: u32 = 0x10000052;
/// `Twitch3`.
pub const TWITCH3: u32 = 0x10000053;
/// `Twitch4`.
pub const TWITCH4: u32 = 0x10000054;
/// `StaggerBackward`.
pub const STAGGER_BACKWARD: u32 = 0x10000055;
/// `StaggerForward`.
pub const STAGGER_FORWARD: u32 = 0x10000056;
/// `Sanctuary`.
pub const SANCTUARY: u32 = 0x10000057;
/// `ThrustMed`.
pub const THRUST_MED: u32 = 0x10000058;
/// `ThrustLow`.
pub const THRUST_LOW: u32 = 0x10000059;
/// `ThrustHigh`.
pub const THRUST_HIGH: u32 = 0x1000005A;
/// `SlashHigh`.
pub const SLASH_HIGH: u32 = 0x1000005B;
/// `SlashMed`.
pub const SLASH_MED: u32 = 0x1000005C;
/// `SlashLow`.
pub const SLASH_LOW: u32 = 0x1000005D;
/// `BackhandHigh`.
pub const BACKHAND_HIGH: u32 = 0x1000005E;
/// `BackhandMed`.
pub const BACKHAND_MED: u32 = 0x1000005F;
/// `BackhandLow`.
pub const BACKHAND_LOW: u32 = 0x10000060;
/// `Shoot`.
pub const SHOOT: u32 = 0x10000061;
/// `AttackHigh1`.
pub const ATTACK_HIGH1: u32 = 0x10000062;
/// `AttackMed1`.
pub const ATTACK_MED1: u32 = 0x10000063;
/// `AttackLow1`.
pub const ATTACK_LOW1: u32 = 0x10000064;
/// `AttackHigh2`.
pub const ATTACK_HIGH2: u32 = 0x10000065;
/// `AttackMed2`.
pub const ATTACK_MED2: u32 = 0x10000066;
/// `AttackLow2`.
pub const ATTACK_LOW2: u32 = 0x10000067;
/// `AttackHigh3`.
pub const ATTACK_HIGH3: u32 = 0x10000068;
/// `AttackMed3`.
pub const ATTACK_MED3: u32 = 0x10000069;
/// `AttackLow3`.
pub const ATTACK_LOW3: u32 = 0x1000006A;
/// `HeadThrow`.
pub const HEAD_THROW: u32 = 0x1000006B;
/// `FistSlam`.
pub const FIST_SLAM: u32 = 0x1000006C;
/// `BreatheFlame`.
pub const BREATHE_FLAME: u32 = 0x1000006D;
/// `SpinAttack`.
pub const SPIN_ATTACK: u32 = 0x1000006E;
/// `MagicPowerUp01`.
pub const MAGIC_POWER_UP01: u32 = 0x1000006F;
/// `MagicPowerUp02`.
pub const MAGIC_POWER_UP02: u32 = 0x10000070;
/// `MagicPowerUp03`.
pub const MAGIC_POWER_UP03: u32 = 0x10000071;
/// `MagicPowerUp04`.
pub const MAGIC_POWER_UP04: u32 = 0x10000072;
/// `MagicPowerUp05`.
pub const MAGIC_POWER_UP05: u32 = 0x10000073;
/// `MagicPowerUp06`.
pub const MAGIC_POWER_UP06: u32 = 0x10000074;
/// `MagicPowerUp07`.
pub const MAGIC_POWER_UP07: u32 = 0x10000075;
/// `MagicPowerUp08`.
pub const MAGIC_POWER_UP08: u32 = 0x10000076;
/// `MagicPowerUp09`.
pub const MAGIC_POWER_UP09: u32 = 0x10000077;
/// `MagicPowerUp10`.
pub const MAGIC_POWER_UP10: u32 = 0x10000078;
/// `EnterGame`.
pub const ENTER_GAME: u32 = 0x1000009C;
/// `ExitGame`.
pub const EXIT_GAME: u32 = 0x1000009D;
/// `OnCreation`.
pub const ON_CREATION: u32 = 0x1000009E;
/// `OnDestruction`.
pub const ON_DESTRUCTION: u32 = 0x1000009F;
/// `EnterPortal`.
pub const ENTER_PORTAL: u32 = 0x100000A0;
/// `ExitPortal`.
pub const EXIT_PORTAL: u32 = 0x100000A1;
/// `SpecialAttack1`.
pub const SPECIAL_ATTACK1: u32 = 0x100000CD;
/// `SpecialAttack2`.
pub const SPECIAL_ATTACK2: u32 = 0x100000CE;
/// `SpecialAttack3`.
pub const SPECIAL_ATTACK3: u32 = 0x100000CF;
/// `MissileAttack1`.
pub const MISSILE_ATTACK1: u32 = 0x100000D0;
/// `MissileAttack2`.
pub const MISSILE_ATTACK2: u32 = 0x100000D1;
/// `MissileAttack3`.
pub const MISSILE_ATTACK3: u32 = 0x100000D2;
/// `Blink`.
pub const BLINK: u32 = 0x100000E2;
/// `Bite`.
pub const BITE: u32 = 0x100000E3;
/// `SkillHealSelf`.
pub const SKILL_HEAL_SELF: u32 = 0x1000010E;
/// `SkillHealOther`.
pub const SKILL_HEAL_OTHER: u32 = 0x1000010F;
/// `LogOut`.
pub const LOG_OUT: u32 = 0x1000011E;
/// `DoubleSlashLow`.
pub const DOUBLE_SLASH_LOW: u32 = 0x1000011F;
/// `DoubleSlashMed`.
pub const DOUBLE_SLASH_MED: u32 = 0x10000120;
/// `DoubleSlashHigh`.
pub const DOUBLE_SLASH_HIGH: u32 = 0x10000121;
/// `TripleSlashLow`.
pub const TRIPLE_SLASH_LOW: u32 = 0x10000122;
/// `TripleSlashMed`.
pub const TRIPLE_SLASH_MED: u32 = 0x10000123;
/// `TripleSlashHigh`.
pub const TRIPLE_SLASH_HIGH: u32 = 0x10000124;
/// `DoubleThrustLow`.
pub const DOUBLE_THRUST_LOW: u32 = 0x10000125;
/// `DoubleThrustMed`.
pub const DOUBLE_THRUST_MED: u32 = 0x10000126;
/// `DoubleThrustHigh`.
pub const DOUBLE_THRUST_HIGH: u32 = 0x10000127;
/// `TripleThrustLow`.
pub const TRIPLE_THRUST_LOW: u32 = 0x10000128;
/// `TripleThrustMed`.
pub const TRIPLE_THRUST_MED: u32 = 0x10000129;
/// `TripleThrustHigh`.
pub const TRIPLE_THRUST_HIGH: u32 = 0x1000012A;
/// `MagicPowerUp01Purple`.
pub const MAGIC_POWER_UP01_PURPLE: u32 = 0x1000012B;
/// `MagicPowerUp02Purple`.
pub const MAGIC_POWER_UP02_PURPLE: u32 = 0x1000012C;
/// `MagicPowerUp03Purple`.
pub const MAGIC_POWER_UP03_PURPLE: u32 = 0x1000012D;
/// `MagicPowerUp04Purple`.
pub const MAGIC_POWER_UP04_PURPLE: u32 = 0x1000012E;
/// `MagicPowerUp05Purple`.
pub const MAGIC_POWER_UP05_PURPLE: u32 = 0x1000012F;
/// `MagicPowerUp06Purple`.
pub const MAGIC_POWER_UP06_PURPLE: u32 = 0x10000130;
/// `MagicPowerUp07Purple`.
pub const MAGIC_POWER_UP07_PURPLE: u32 = 0x10000131;
/// `MagicPowerUp08Purple`.
pub const MAGIC_POWER_UP08_PURPLE: u32 = 0x10000132;
/// `MagicPowerUp09Purple`.
pub const MAGIC_POWER_UP09_PURPLE: u32 = 0x10000133;
/// `MagicPowerUp10Purple`.
pub const MAGIC_POWER_UP10_PURPLE: u32 = 0x10000134;
/// `HouseRecall`.
pub const HOUSE_RECALL: u32 = 0x1000013A;
/// `LifestoneRecall`.
pub const LIFESTONE_RECALL: u32 = 0x10000153;
/// `Fishing`.
pub const FISHING: u32 = 0x10000165;
/// `MarketplaceRecall`.
pub const MARKETPLACE_RECALL: u32 = 0x10000166;
/// `EnterPKLite`.
pub const ENTER_PK_LITE: u32 = 0x10000167;
/// `AllegianceHometownRecall`.
pub const ALLEGIANCE_HOMETOWN_RECALL: u32 = 0x10000171;
/// `PKArenaRecall`.
pub const PK_ARENA_RECALL: u32 = 0x10000172;
/// `OffhandSlashHigh`.
pub const OFFHAND_SLASH_HIGH: u32 = 0x10000173;
/// `OffhandSlashMed`.
pub const OFFHAND_SLASH_MED: u32 = 0x10000174;
/// `OffhandSlashLow`.
pub const OFFHAND_SLASH_LOW: u32 = 0x10000175;
/// `OffhandThrustHigh`.
pub const OFFHAND_THRUST_HIGH: u32 = 0x10000176;
/// `OffhandThrustMed`.
pub const OFFHAND_THRUST_MED: u32 = 0x10000177;
/// `OffhandThrustLow`.
pub const OFFHAND_THRUST_LOW: u32 = 0x10000178;
/// `OffhandDoubleSlashLow`.
pub const OFFHAND_DOUBLE_SLASH_LOW: u32 = 0x10000179;
/// `OffhandDoubleSlashMed`.
pub const OFFHAND_DOUBLE_SLASH_MED: u32 = 0x1000017A;
/// `OffhandDoubleSlashHigh`.
pub const OFFHAND_DOUBLE_SLASH_HIGH: u32 = 0x1000017B;
/// `OffhandTripleSlashLow`.
pub const OFFHAND_TRIPLE_SLASH_LOW: u32 = 0x1000017C;
/// `OffhandTripleSlashMed`.
pub const OFFHAND_TRIPLE_SLASH_MED: u32 = 0x1000017D;
/// `OffhandTripleSlashHigh`.
pub const OFFHAND_TRIPLE_SLASH_HIGH: u32 = 0x1000017E;
/// `OffhandDoubleThrustLow`.
pub const OFFHAND_DOUBLE_THRUST_LOW: u32 = 0x1000017F;
/// `OffhandDoubleThrustMed`.
pub const OFFHAND_DOUBLE_THRUST_MED: u32 = 0x10000180;
/// `OffhandDoubleThrustHigh`.
pub const OFFHAND_DOUBLE_THRUST_HIGH: u32 = 0x10000181;
/// `OffhandTripleThrustLow`.
pub const OFFHAND_TRIPLE_THRUST_LOW: u32 = 0x10000182;
/// `OffhandTripleThrustMed`.
pub const OFFHAND_TRIPLE_THRUST_MED: u32 = 0x10000183;
/// `OffhandTripleThrustHigh`.
pub const OFFHAND_TRIPLE_THRUST_HIGH: u32 = 0x10000184;
/// `OffhandKick`.
pub const OFFHAND_KICK: u32 = 0x10000185;
/// `AttackHigh4`.
pub const ATTACK_HIGH4: u32 = 0x10000186;
/// `AttackMed4`.
pub const ATTACK_MED4: u32 = 0x10000187;
/// `AttackLow4`.
pub const ATTACK_LOW4: u32 = 0x10000188;
/// `AttackHigh5`.
pub const ATTACK_HIGH5: u32 = 0x10000189;
/// `AttackMed5`.
pub const ATTACK_MED5: u32 = 0x1000018A;
/// `AttackLow5`.
pub const ATTACK_LOW5: u32 = 0x1000018B;
/// `AttackHigh6`.
pub const ATTACK_HIGH6: u32 = 0x1000018C;
/// `AttackMed6`.
pub const ATTACK_MED6: u32 = 0x1000018D;
/// `AttackLow6`.
pub const ATTACK_LOW6: u32 = 0x1000018E;
/// `PunchFastHigh`.
pub const PUNCH_FAST_HIGH: u32 = 0x1000018F;
/// `PunchFastMed`.
pub const PUNCH_FAST_MED: u32 = 0x10000190;
/// `PunchFastLow`.
pub const PUNCH_FAST_LOW: u32 = 0x10000191;
/// `PunchSlowHigh`.
pub const PUNCH_SLOW_HIGH: u32 = 0x10000192;
/// `PunchSlowMed`.
pub const PUNCH_SLOW_MED: u32 = 0x10000193;
/// `PunchSlowLow`.
pub const PUNCH_SLOW_LOW: u32 = 0x10000194;
/// `OffhandPunchFastHigh`.
pub const OFFHAND_PUNCH_FAST_HIGH: u32 = 0x10000195;
/// `OffhandPunchFastMed`.
pub const OFFHAND_PUNCH_FAST_MED: u32 = 0x10000196;
/// `OffhandPunchFastLow`.
pub const OFFHAND_PUNCH_FAST_LOW: u32 = 0x10000197;
/// `OffhandPunchSlowHigh`.
pub const OFFHAND_PUNCH_SLOW_HIGH: u32 = 0x10000198;
/// `OffhandPunchSlowMed`.
pub const OFFHAND_PUNCH_SLOW_MED: u32 = 0x10000199;
/// `OffhandPunchSlowLow`.
pub const OFFHAND_PUNCH_SLOW_LOW: u32 = 0x1000019A;
/// `WoahDuplicate2`.
pub const WOAH_DUPLICATE2: u32 = 0x1000019B;
/// `YMCA`.
pub const YMCA: u32 = 0x1200009B;
/// `Flatulence`.
pub const FLATULENCE: u32 = 0x120000D4;
/// `Demonet`.
pub const DEMONET: u32 = 0x120000DF;
/// `Cheer`.
pub const CHEER: u32 = 0x1300004C;
/// `ShakeFist`.
pub const SHAKE_FIST: u32 = 0x13000079;
/// `Beckon`.
pub const BECKON: u32 = 0x1300007A;
/// `BeSeeingYou`.
pub const BE_SEEING_YOU: u32 = 0x1300007B;
/// `BlowKiss`.
pub const BLOW_KISS: u32 = 0x1300007C;
/// `BowDeep`.
pub const BOW_DEEP: u32 = 0x1300007D;
/// `ClapHands`.
pub const CLAP_HANDS: u32 = 0x1300007E;
/// `Cry`.
pub const CRY: u32 = 0x1300007F;
/// `Laugh`.
pub const LAUGH: u32 = 0x13000080;
/// `MimeEat`.
pub const MIME_EAT: u32 = 0x13000081;
/// `MimeDrink`.
pub const MIME_DRINK: u32 = 0x13000082;
/// `Nod`.
pub const NOD: u32 = 0x13000083;
/// `Point`.
pub const POINT: u32 = 0x13000084;
/// `ShakeHead`.
pub const SHAKE_HEAD: u32 = 0x13000085;
/// `Shrug`.
pub const SHRUG: u32 = 0x13000086;
/// `Wave`.
pub const WAVE: u32 = 0x13000087;
/// `Akimbo`.
pub const AKIMBO: u32 = 0x13000088;
/// `HeartyLaugh`.
pub const HEARTY_LAUGH: u32 = 0x13000089;
/// `Salute`.
pub const SALUTE: u32 = 0x1300008A;
/// `ScratchHead`.
pub const SCRATCH_HEAD: u32 = 0x1300008B;
/// `SmackHead`.
pub const SMACK_HEAD: u32 = 0x1300008C;
/// `TapFoot`.
pub const TAP_FOOT: u32 = 0x1300008D;
/// `WaveHigh`.
pub const WAVE_HIGH: u32 = 0x1300008E;
/// `WaveLow`.
pub const WAVE_LOW: u32 = 0x1300008F;
/// `YawnStretch`.
pub const YAWN_STRETCH: u32 = 0x13000090;
/// `Cringe`.
pub const CRINGE: u32 = 0x13000091;
/// `Kneel`.
pub const KNEEL: u32 = 0x13000092;
/// `Plead`.
pub const PLEAD: u32 = 0x13000093;
/// `Shiver`.
pub const SHIVER: u32 = 0x13000094;
/// `Shoo`.
pub const SHOO: u32 = 0x13000095;
/// `Slouch`.
pub const SLOUCH: u32 = 0x13000096;
/// `Spit`.
pub const SPIT: u32 = 0x13000097;
/// `Surrender`.
pub const SURRENDER: u32 = 0x13000098;
/// `Woah`.
pub const WOAH: u32 = 0x13000099;
/// `Winded`.
pub const WINDED: u32 = 0x1300009A;
/// `Pray`.
pub const PRAY: u32 = 0x130000CA;
/// `Mock`.
pub const MOCK: u32 = 0x130000CB;
/// `Teapot`.
pub const TEAPOT: u32 = 0x130000CC;
/// `WarmHands`.
pub const WARM_HANDS: u32 = 0x13000119;
/// `Helper`.
pub const HELPER: u32 = 0x13000135;
/// `NudgeLeft`.
pub const NUDGE_LEFT: u32 = 0x1300014A;
/// `NudgeRight`.
pub const NUDGE_RIGHT: u32 = 0x1300014B;
/// `PointLeft`.
pub const POINT_LEFT: u32 = 0x1300014C;
/// `PointRight`.
pub const POINT_RIGHT: u32 = 0x1300014D;
/// `PointDown`.
pub const POINT_DOWN: u32 = 0x1300014E;
/// `Knock`.
pub const KNOCK: u32 = 0x1300014F;
/// `ScanHorizon`.
pub const SCAN_HORIZON: u32 = 0x13000150;
/// `DrudgeDance`.
pub const DRUDGE_DANCE: u32 = 0x13000151;
/// `HaveASeat`.
pub const HAVE_A_SEAT: u32 = 0x13000152;
/// `StopTurning`.
pub const STOP_TURNING: u32 = 0x2000003A;
/// `Jump`.
pub const JUMP: u32 = 0x2500003B;
/// `Stop`.
pub const STOP: u32 = 0x40000004;
/// `Fallen`.
pub const FALLEN: u32 = 0x40000008;
/// `Interpolating`.
pub const INTERPOLATING: u32 = 0x40000009;
/// `Hover`.
pub const HOVER: u32 = 0x4000000A;
/// `On`.
pub const ON: u32 = 0x4000000B;
/// `Off`.
pub const OFF: u32 = 0x4000000C;
/// `Dead`.
pub const DEAD: u32 = 0x40000011;
/// `Falling`.
pub const FALLING: u32 = 0x40000015;
/// `Reload`.
pub const RELOAD: u32 = 0x40000016;
/// `Unload`.
pub const UNLOAD: u32 = 0x40000017;
/// `Pickup`.
pub const PICKUP: u32 = 0x40000018;
/// `StoreInBackpack`.
pub const STORE_IN_BACKPACK: u32 = 0x40000019;
/// `Eat`.
pub const EAT: u32 = 0x4000001A;
/// `Drink`.
pub const DRINK: u32 = 0x4000001B;
/// `Reading`.
pub const READING: u32 = 0x4000001C;
/// `JumpCharging`.
pub const JUMP_CHARGING: u32 = 0x4000001D;
/// `AimLevel`.
pub const AIM_LEVEL: u32 = 0x4000001E;
/// `AimHigh15`.
pub const AIM_HIGH15: u32 = 0x4000001F;
/// `AimHigh30`.
pub const AIM_HIGH30: u32 = 0x40000020;
/// `AimHigh45`.
pub const AIM_HIGH45: u32 = 0x40000021;
/// `AimHigh60`.
pub const AIM_HIGH60: u32 = 0x40000022;
/// `AimHigh75`.
pub const AIM_HIGH75: u32 = 0x40000023;
/// `AimHigh90`.
pub const AIM_HIGH90: u32 = 0x40000024;
/// `AimLow15`.
pub const AIM_LOW15: u32 = 0x40000025;
/// `AimLow30`.
pub const AIM_LOW30: u32 = 0x40000026;
/// `AimLow45`.
pub const AIM_LOW45: u32 = 0x40000027;
/// `AimLow60`.
pub const AIM_LOW60: u32 = 0x40000028;
/// `AimLow75`.
pub const AIM_LOW75: u32 = 0x40000029;
/// `AimLow90`.
pub const AIM_LOW90: u32 = 0x4000002A;
/// `MagicBlast`.
pub const MAGIC_BLAST: u32 = 0x4000002B;
/// `MagicSelfHead`.
pub const MAGIC_SELF_HEAD: u32 = 0x4000002C;
/// `MagicSelfHeart`.
pub const MAGIC_SELF_HEART: u32 = 0x4000002D;
/// `MagicBonus`.
pub const MAGIC_BONUS: u32 = 0x4000002E;
/// `MagicClap`.
pub const MAGIC_CLAP: u32 = 0x4000002F;
/// `MagicHarm`.
pub const MAGIC_HARM: u32 = 0x40000030;
/// `MagicHeal`.
pub const MAGIC_HEAL: u32 = 0x40000031;
/// `MagicThrowMissile`.
pub const MAGIC_THROW_MISSILE: u32 = 0x40000032;
/// `MagicRecoilMissile`.
pub const MAGIC_RECOIL_MISSILE: u32 = 0x40000033;
/// `MagicPenalty`.
pub const MAGIC_PENALTY: u32 = 0x40000034;
/// `MagicTransfer`.
pub const MAGIC_TRANSFER: u32 = 0x40000035;
/// `MagicVision`.
pub const MAGIC_VISION: u32 = 0x40000036;
/// `MagicEnchantItem`.
pub const MAGIC_ENCHANT_ITEM: u32 = 0x40000037;
/// `MagicPortal`.
pub const MAGIC_PORTAL: u32 = 0x40000038;
/// `MagicPray`.
pub const MAGIC_PRAY: u32 = 0x40000039;
/// `CastSpell`.
pub const CAST_SPELL: u32 = 0x400000D3;
/// `UseMagicStaff`.
pub const USE_MAGIC_STAFF: u32 = 0x400000E0;
/// `UseMagicWand`.
pub const USE_MAGIC_WAND: u32 = 0x400000E1;
/// `TwitchSubstate1`.
pub const TWITCH_SUBSTATE1: u32 = 0x400000E4;
/// `TwitchSubstate2`.
pub const TWITCH_SUBSTATE2: u32 = 0x400000E5;
/// `TwitchSubstate3`.
pub const TWITCH_SUBSTATE3: u32 = 0x400000E6;
/// `Pickup5`.
pub const PICKUP5: u32 = 0x40000136;
/// `Pickup10`.
pub const PICKUP10: u32 = 0x40000137;
/// `Pickup15`.
pub const PICKUP15: u32 = 0x40000138;
/// `Pickup20`.
pub const PICKUP20: u32 = 0x40000139;
/// `Ready`.
pub const READY: u32 = 0x41000003;
/// `Crouch`.
pub const CROUCH: u32 = 0x41000012;
/// `Sitting`.
pub const SITTING: u32 = 0x41000013;
/// `Sleeping`.
pub const SLEEPING: u32 = 0x41000014;
/// `ATOYOT`.
pub const ATOYOT: u32 = 0x420000F9;
/// `ShakeFistState`.
pub const SHAKE_FIST_STATE: u32 = 0x430000EA;
/// `PrayState`.
pub const PRAY_STATE: u32 = 0x430000EB;
/// `BowDeepState`.
pub const BOW_DEEP_STATE: u32 = 0x430000EC;
/// `ClapHandsState`.
pub const CLAP_HANDS_STATE: u32 = 0x430000ED;
/// `CrossArmsState`.
pub const CROSS_ARMS_STATE: u32 = 0x430000EE;
/// `ShiverState`.
pub const SHIVER_STATE: u32 = 0x430000EF;
/// `PointState`.
pub const POINT_STATE: u32 = 0x430000F0;
/// `WaveState`.
pub const WAVE_STATE: u32 = 0x430000F1;
/// `AkimboState`.
pub const AKIMBO_STATE: u32 = 0x430000F2;
/// `SaluteState`.
pub const SALUTE_STATE: u32 = 0x430000F3;
/// `ScratchHeadState`.
pub const SCRATCH_HEAD_STATE: u32 = 0x430000F4;
/// `TapFootState`.
pub const TAP_FOOT_STATE: u32 = 0x430000F5;
/// `LeanState`.
pub const LEAN_STATE: u32 = 0x430000F6;
/// `KneelState`.
pub const KNEEL_STATE: u32 = 0x430000F7;
/// `PleadState`.
pub const PLEAD_STATE: u32 = 0x430000F8;
/// `SlouchState`.
pub const SLOUCH_STATE: u32 = 0x430000FA;
/// `SurrenderState`.
pub const SURRENDER_STATE: u32 = 0x430000FB;
/// `WoahState`.
pub const WOAH_STATE: u32 = 0x430000FC;
/// `WindedState`.
pub const WINDED_STATE: u32 = 0x430000FD;
/// `SnowAngelState`.
pub const SNOW_ANGEL_STATE: u32 = 0x43000118;
/// `CurtseyState`.
pub const CURTSEY_STATE: u32 = 0x4300011A;
/// `AFKState`.
pub const AFK_STATE: u32 = 0x4300011B;
/// `MeditateState`.
pub const MEDITATE_STATE: u32 = 0x4300011C;
/// `SitState`.
pub const SIT_STATE: u32 = 0x4300013D;
/// `SitCrossleggedState`.
pub const SIT_CROSSLEGGED_STATE: u32 = 0x4300013E;
/// `SitBackState`.
pub const SIT_BACK_STATE: u32 = 0x4300013F;
/// `PointLeftState`.
pub const POINT_LEFT_STATE: u32 = 0x43000140;
/// `PointRightState`.
pub const POINT_RIGHT_STATE: u32 = 0x43000141;
/// `TalktotheHandState`.
pub const TALKTOTHE_HAND_STATE: u32 = 0x43000142;
/// `PointDownState`.
pub const POINT_DOWN_STATE: u32 = 0x43000143;
/// `DrudgeDanceState`.
pub const DRUDGE_DANCE_STATE: u32 = 0x43000144;
/// `PossumState`.
pub const POSSUM_STATE: u32 = 0x43000145;
/// `ReadState`.
pub const READ_STATE: u32 = 0x43000146;
/// `ThinkerState`.
pub const THINKER_STATE: u32 = 0x43000147;
/// `HaveASeatState`.
pub const HAVE_A_SEAT_STATE: u32 = 0x43000148;
/// `AtEaseState`.
pub const AT_EASE_STATE: u32 = 0x43000149;
/// `RunForward`.
pub const RUN_FORWARD: u32 = 0x44000007;
/// `WalkForward`.
pub const WALK_FORWARD: u32 = 0x45000005;
/// `WalkBackwards`.
pub const WALK_BACKWARDS: u32 = 0x45000006;
/// `TurnRight`.
pub const TURN_RIGHT: u32 = 0x6500000D;
/// `TurnLeft`.
pub const TURN_LEFT: u32 = 0x6500000E;
/// `SideStepRight`.
pub const SIDE_STEP_RIGHT: u32 = 0x6500000F;
/// `SideStepLeft`.
pub const SIDE_STEP_LEFT: u32 = 0x65000010;
/// `HandCombat`.
pub const HAND_COMBAT: u32 = 0x8000003C;
/// `NonCombat`.
pub const NON_COMBAT: u32 = 0x8000003D;
/// `SwordCombat`.
pub const SWORD_COMBAT: u32 = 0x8000003E;
/// `BowCombat`.
pub const BOW_COMBAT: u32 = 0x8000003F;
/// `SwordShieldCombat`.
pub const SWORD_SHIELD_COMBAT: u32 = 0x80000040;
/// `CrossbowCombat`.
pub const CROSSBOW_COMBAT: u32 = 0x80000041;
/// `UnusedCombat`.
pub const UNUSED_COMBAT: u32 = 0x80000042;
/// `SlingCombat`.
pub const SLING_COMBAT: u32 = 0x80000043;
/// `TwoHandedSwordCombat`.
pub const TWO_HANDED_SWORD_COMBAT: u32 = 0x80000044;
/// `TwoHandedStaffCombat`.
pub const TWO_HANDED_STAFF_COMBAT: u32 = 0x80000045;
/// `DualWieldCombat`.
pub const DUAL_WIELD_COMBAT: u32 = 0x80000046;
/// `ThrownWeaponCombat`.
pub const THROWN_WEAPON_COMBAT: u32 = 0x80000047;
/// `Graze`.
pub const GRAZE: u32 = 0x80000048;
/// `Magic`.
pub const MAGIC: u32 = 0x80000049;
/// `BowNoAmmo`.
pub const BOW_NO_AMMO: u32 = 0x800000E8;
/// `CrossBowNoAmmo`.
pub const CROSS_BOW_NO_AMMO: u32 = 0x800000E9;
/// `AtlatlCombat`.
pub const ATLATL_COMBAT: u32 = 0x8000013B;
/// `ThrownShieldCombat`.
pub const THROWN_SHIELD_COMBAT: u32 = 0x8000013C;
/// `HoldRun`.
pub const HOLD_RUN: u32 = 0x85000001;
/// `HoldSidestep`.
pub const HOLD_SIDESTEP: u32 = 0x85000002;

/// Every named command with its name, in the reference enum's order.
pub const ALL: [(&str, u32); 409] = [
    ("Invalid", INVALID),
    ("Cancel", CANCEL),
    ("CreateShortcutToSelected", CREATE_SHORTCUT_TO_SELECTED),
    ("EnterChat", ENTER_CHAT),
    ("ToggleChat", TOGGLE_CHAT),
    ("SavePosition", SAVE_POSITION),
    ("UseSelected", USE_SELECTED),
    ("AutosortSelected", AUTOSORT_SELECTED),
    ("DropSelected", DROP_SELECTED),
    ("GiveSelected", GIVE_SELECTED),
    ("SplitSelected", SPLIT_SELECTED),
    ("ExamineSelected", EXAMINE_SELECTED),
    ("PreviousCompassItem", PREVIOUS_COMPASS_ITEM),
    ("NextCompassItem", NEXT_COMPASS_ITEM),
    ("ClosestCompassItem", CLOSEST_COMPASS_ITEM),
    ("PreviousSelection", PREVIOUS_SELECTION),
    ("LastAttacker", LAST_ATTACKER),
    ("PreviousFellow", PREVIOUS_FELLOW),
    ("NextFellow", NEXT_FELLOW),
    ("ToggleCombat", TOGGLE_COMBAT),
    ("OptionsPanel", OPTIONS_PANEL),
    ("ResetView", RESET_VIEW),
    ("FloorView", FLOOR_VIEW),
    ("PreviousItem", PREVIOUS_ITEM),
    ("NextItem", NEXT_ITEM),
    ("ClosestItem", CLOSEST_ITEM),
    ("MapView", MAP_VIEW),
    ("AutoRun", AUTO_RUN),
    ("DecreasePowerSetting", DECREASE_POWER_SETTING),
    ("IncreasePowerSetting", INCREASE_POWER_SETTING),
    ("FirstPersonView", FIRST_PERSON_VIEW),
    ("AllegiancePanel", ALLEGIANCE_PANEL),
    ("FellowshipPanel", FELLOWSHIP_PANEL),
    ("SpellbookPanel", SPELLBOOK_PANEL),
    ("SpellComponentsPanel", SPELL_COMPONENTS_PANEL),
    ("HousePanel", HOUSE_PANEL),
    ("AttributesPanel", ATTRIBUTES_PANEL),
    ("SkillsPanel", SKILLS_PANEL),
    ("MapPanel", MAP_PANEL),
    ("InventoryPanel", INVENTORY_PANEL),
    ("CaptureScreenshotToFile", CAPTURE_SCREENSHOT_TO_FILE),
    ("AutoCreateShortcuts", AUTO_CREATE_SHORTCUTS),
    ("AutoRepeatAttacks", AUTO_REPEAT_ATTACKS),
    ("AutoTarget", AUTO_TARGET),
    ("AdvancedCombatInterface", ADVANCED_COMBAT_INTERFACE),
    ("IgnoreAllegianceRequests", IGNORE_ALLEGIANCE_REQUESTS),
    ("IgnoreFellowshipRequests", IGNORE_FELLOWSHIP_REQUESTS),
    ("InvertMouseLook", INVERT_MOUSE_LOOK),
    ("LetPlayersGiveYouItems", LET_PLAYERS_GIVE_YOU_ITEMS),
    ("AutoTrackCombatTargets", AUTO_TRACK_COMBAT_TARGETS),
    ("DisplayTooltips", DISPLAY_TOOLTIPS),
    ("AttemptToDeceivePlayers", ATTEMPT_TO_DECEIVE_PLAYERS),
    ("RunAsDefaultMovement", RUN_AS_DEFAULT_MOVEMENT),
    ("StayInChatModeAfterSend", STAY_IN_CHAT_MODE_AFTER_SEND),
    ("RightClickToMouseLook", RIGHT_CLICK_TO_MOUSE_LOOK),
    ("VividTargetIndicator", VIVID_TARGET_INDICATOR),
    ("SelectSelf", SELECT_SELF),
    ("PreviousMonster", PREVIOUS_MONSTER),
    ("ClosestMonster", CLOSEST_MONSTER),
    ("NextPlayer", NEXT_PLAYER),
    ("PreviousPlayer", PREVIOUS_PLAYER),
    ("ClosestPlayer", CLOSEST_PLAYER),
    ("TradePanel", TRADE_PANEL),
    ("CharacterOptionsPanel", CHARACTER_OPTIONS_PANEL),
    ("SoundAndGraphicsPanel", SOUND_AND_GRAPHICS_PANEL),
    ("HelpfulSpellsPanel", HELPFUL_SPELLS_PANEL),
    ("HarmfulSpellsPanel", HARMFUL_SPELLS_PANEL),
    ("CharacterInformationPanel", CHARACTER_INFORMATION_PANEL),
    ("LinkStatusPanel", LINK_STATUS_PANEL),
    ("VitaePanel", VITAE_PANEL),
    ("ShareFellowshipXP", SHARE_FELLOWSHIP_XP),
    ("ShareFellowshipLoot", SHARE_FELLOWSHIP_LOOT),
    ("AcceptCorpseLooting", ACCEPT_CORPSE_LOOTING),
    ("IgnoreTradeRequests", IGNORE_TRADE_REQUESTS),
    ("DisableWeather", DISABLE_WEATHER),
    ("DisableHouseEffect", DISABLE_HOUSE_EFFECT),
    ("SideBySideVitals", SIDE_BY_SIDE_VITALS),
    ("ShowRadarCoordinates", SHOW_RADAR_COORDINATES),
    ("ShowSpellDurations", SHOW_SPELL_DURATIONS),
    ("MuteOnLosingFocus", MUTE_ON_LOSING_FOCUS),
    ("AllegianceChat", ALLEGIANCE_CHAT),
    (
        "AutomaticallyAcceptFellowshipRequests",
        AUTOMATICALLY_ACCEPT_FELLOWSHIP_REQUESTS,
    ),
    ("Reply", REPLY),
    ("MonarchReply", MONARCH_REPLY),
    ("PatronReply", PATRON_REPLY),
    (
        "ToggleCraftingChanceOfSuccessDialog",
        TOGGLE_CRAFTING_CHANCE_OF_SUCCESS_DIALOG,
    ),
    ("UseClosestUnopenedCorpse", USE_CLOSEST_UNOPENED_CORPSE),
    ("UseNextUnopenedCorpse", USE_NEXT_UNOPENED_CORPSE),
    ("IssueSlashCommand", ISSUE_SLASH_COMMAND),
    ("MouseLook", MOUSE_LOOK),
    ("HighAttack", HIGH_ATTACK),
    ("MediumAttack", MEDIUM_ATTACK),
    ("LowAttack", LOW_ATTACK),
    ("CameraLeftRotate", CAMERA_LEFT_ROTATE),
    ("CameraRightRotate", CAMERA_RIGHT_ROTATE),
    ("CameraRaise", CAMERA_RAISE),
    ("CameraLower", CAMERA_LOWER),
    ("CameraCloser", CAMERA_CLOSER),
    ("CameraFarther", CAMERA_FARTHER),
    ("ShiftView", SHIFT_VIEW),
    ("Hop", HOP),
    ("Jumpup", JUMPUP),
    ("ChestBeat", CHEST_BEAT),
    ("TippedLeft", TIPPED_LEFT),
    ("TippedRight", TIPPED_RIGHT),
    ("FallDown", FALL_DOWN),
    ("Twitch1", TWITCH1),
    ("Twitch2", TWITCH2),
    ("Twitch3", TWITCH3),
    ("Twitch4", TWITCH4),
    ("StaggerBackward", STAGGER_BACKWARD),
    ("StaggerForward", STAGGER_FORWARD),
    ("Sanctuary", SANCTUARY),
    ("ThrustMed", THRUST_MED),
    ("ThrustLow", THRUST_LOW),
    ("ThrustHigh", THRUST_HIGH),
    ("SlashHigh", SLASH_HIGH),
    ("SlashMed", SLASH_MED),
    ("SlashLow", SLASH_LOW),
    ("BackhandHigh", BACKHAND_HIGH),
    ("BackhandMed", BACKHAND_MED),
    ("BackhandLow", BACKHAND_LOW),
    ("Shoot", SHOOT),
    ("AttackHigh1", ATTACK_HIGH1),
    ("AttackMed1", ATTACK_MED1),
    ("AttackLow1", ATTACK_LOW1),
    ("AttackHigh2", ATTACK_HIGH2),
    ("AttackMed2", ATTACK_MED2),
    ("AttackLow2", ATTACK_LOW2),
    ("AttackHigh3", ATTACK_HIGH3),
    ("AttackMed3", ATTACK_MED3),
    ("AttackLow3", ATTACK_LOW3),
    ("HeadThrow", HEAD_THROW),
    ("FistSlam", FIST_SLAM),
    ("BreatheFlame", BREATHE_FLAME),
    ("SpinAttack", SPIN_ATTACK),
    ("MagicPowerUp01", MAGIC_POWER_UP01),
    ("MagicPowerUp02", MAGIC_POWER_UP02),
    ("MagicPowerUp03", MAGIC_POWER_UP03),
    ("MagicPowerUp04", MAGIC_POWER_UP04),
    ("MagicPowerUp05", MAGIC_POWER_UP05),
    ("MagicPowerUp06", MAGIC_POWER_UP06),
    ("MagicPowerUp07", MAGIC_POWER_UP07),
    ("MagicPowerUp08", MAGIC_POWER_UP08),
    ("MagicPowerUp09", MAGIC_POWER_UP09),
    ("MagicPowerUp10", MAGIC_POWER_UP10),
    ("EnterGame", ENTER_GAME),
    ("ExitGame", EXIT_GAME),
    ("OnCreation", ON_CREATION),
    ("OnDestruction", ON_DESTRUCTION),
    ("EnterPortal", ENTER_PORTAL),
    ("ExitPortal", EXIT_PORTAL),
    ("SpecialAttack1", SPECIAL_ATTACK1),
    ("SpecialAttack2", SPECIAL_ATTACK2),
    ("SpecialAttack3", SPECIAL_ATTACK3),
    ("MissileAttack1", MISSILE_ATTACK1),
    ("MissileAttack2", MISSILE_ATTACK2),
    ("MissileAttack3", MISSILE_ATTACK3),
    ("Blink", BLINK),
    ("Bite", BITE),
    ("SkillHealSelf", SKILL_HEAL_SELF),
    ("SkillHealOther", SKILL_HEAL_OTHER),
    ("LogOut", LOG_OUT),
    ("DoubleSlashLow", DOUBLE_SLASH_LOW),
    ("DoubleSlashMed", DOUBLE_SLASH_MED),
    ("DoubleSlashHigh", DOUBLE_SLASH_HIGH),
    ("TripleSlashLow", TRIPLE_SLASH_LOW),
    ("TripleSlashMed", TRIPLE_SLASH_MED),
    ("TripleSlashHigh", TRIPLE_SLASH_HIGH),
    ("DoubleThrustLow", DOUBLE_THRUST_LOW),
    ("DoubleThrustMed", DOUBLE_THRUST_MED),
    ("DoubleThrustHigh", DOUBLE_THRUST_HIGH),
    ("TripleThrustLow", TRIPLE_THRUST_LOW),
    ("TripleThrustMed", TRIPLE_THRUST_MED),
    ("TripleThrustHigh", TRIPLE_THRUST_HIGH),
    ("MagicPowerUp01Purple", MAGIC_POWER_UP01_PURPLE),
    ("MagicPowerUp02Purple", MAGIC_POWER_UP02_PURPLE),
    ("MagicPowerUp03Purple", MAGIC_POWER_UP03_PURPLE),
    ("MagicPowerUp04Purple", MAGIC_POWER_UP04_PURPLE),
    ("MagicPowerUp05Purple", MAGIC_POWER_UP05_PURPLE),
    ("MagicPowerUp06Purple", MAGIC_POWER_UP06_PURPLE),
    ("MagicPowerUp07Purple", MAGIC_POWER_UP07_PURPLE),
    ("MagicPowerUp08Purple", MAGIC_POWER_UP08_PURPLE),
    ("MagicPowerUp09Purple", MAGIC_POWER_UP09_PURPLE),
    ("MagicPowerUp10Purple", MAGIC_POWER_UP10_PURPLE),
    ("HouseRecall", HOUSE_RECALL),
    ("LifestoneRecall", LIFESTONE_RECALL),
    ("Fishing", FISHING),
    ("MarketplaceRecall", MARKETPLACE_RECALL),
    ("EnterPKLite", ENTER_PK_LITE),
    ("AllegianceHometownRecall", ALLEGIANCE_HOMETOWN_RECALL),
    ("PKArenaRecall", PK_ARENA_RECALL),
    ("OffhandSlashHigh", OFFHAND_SLASH_HIGH),
    ("OffhandSlashMed", OFFHAND_SLASH_MED),
    ("OffhandSlashLow", OFFHAND_SLASH_LOW),
    ("OffhandThrustHigh", OFFHAND_THRUST_HIGH),
    ("OffhandThrustMed", OFFHAND_THRUST_MED),
    ("OffhandThrustLow", OFFHAND_THRUST_LOW),
    ("OffhandDoubleSlashLow", OFFHAND_DOUBLE_SLASH_LOW),
    ("OffhandDoubleSlashMed", OFFHAND_DOUBLE_SLASH_MED),
    ("OffhandDoubleSlashHigh", OFFHAND_DOUBLE_SLASH_HIGH),
    ("OffhandTripleSlashLow", OFFHAND_TRIPLE_SLASH_LOW),
    ("OffhandTripleSlashMed", OFFHAND_TRIPLE_SLASH_MED),
    ("OffhandTripleSlashHigh", OFFHAND_TRIPLE_SLASH_HIGH),
    ("OffhandDoubleThrustLow", OFFHAND_DOUBLE_THRUST_LOW),
    ("OffhandDoubleThrustMed", OFFHAND_DOUBLE_THRUST_MED),
    ("OffhandDoubleThrustHigh", OFFHAND_DOUBLE_THRUST_HIGH),
    ("OffhandTripleThrustLow", OFFHAND_TRIPLE_THRUST_LOW),
    ("OffhandTripleThrustMed", OFFHAND_TRIPLE_THRUST_MED),
    ("OffhandTripleThrustHigh", OFFHAND_TRIPLE_THRUST_HIGH),
    ("OffhandKick", OFFHAND_KICK),
    ("AttackHigh4", ATTACK_HIGH4),
    ("AttackMed4", ATTACK_MED4),
    ("AttackLow4", ATTACK_LOW4),
    ("AttackHigh5", ATTACK_HIGH5),
    ("AttackMed5", ATTACK_MED5),
    ("AttackLow5", ATTACK_LOW5),
    ("AttackHigh6", ATTACK_HIGH6),
    ("AttackMed6", ATTACK_MED6),
    ("AttackLow6", ATTACK_LOW6),
    ("PunchFastHigh", PUNCH_FAST_HIGH),
    ("PunchFastMed", PUNCH_FAST_MED),
    ("PunchFastLow", PUNCH_FAST_LOW),
    ("PunchSlowHigh", PUNCH_SLOW_HIGH),
    ("PunchSlowMed", PUNCH_SLOW_MED),
    ("PunchSlowLow", PUNCH_SLOW_LOW),
    ("OffhandPunchFastHigh", OFFHAND_PUNCH_FAST_HIGH),
    ("OffhandPunchFastMed", OFFHAND_PUNCH_FAST_MED),
    ("OffhandPunchFastLow", OFFHAND_PUNCH_FAST_LOW),
    ("OffhandPunchSlowHigh", OFFHAND_PUNCH_SLOW_HIGH),
    ("OffhandPunchSlowMed", OFFHAND_PUNCH_SLOW_MED),
    ("OffhandPunchSlowLow", OFFHAND_PUNCH_SLOW_LOW),
    ("WoahDuplicate2", WOAH_DUPLICATE2),
    ("YMCA", YMCA),
    ("Flatulence", FLATULENCE),
    ("Demonet", DEMONET),
    ("Cheer", CHEER),
    ("ShakeFist", SHAKE_FIST),
    ("Beckon", BECKON),
    ("BeSeeingYou", BE_SEEING_YOU),
    ("BlowKiss", BLOW_KISS),
    ("BowDeep", BOW_DEEP),
    ("ClapHands", CLAP_HANDS),
    ("Cry", CRY),
    ("Laugh", LAUGH),
    ("MimeEat", MIME_EAT),
    ("MimeDrink", MIME_DRINK),
    ("Nod", NOD),
    ("Point", POINT),
    ("ShakeHead", SHAKE_HEAD),
    ("Shrug", SHRUG),
    ("Wave", WAVE),
    ("Akimbo", AKIMBO),
    ("HeartyLaugh", HEARTY_LAUGH),
    ("Salute", SALUTE),
    ("ScratchHead", SCRATCH_HEAD),
    ("SmackHead", SMACK_HEAD),
    ("TapFoot", TAP_FOOT),
    ("WaveHigh", WAVE_HIGH),
    ("WaveLow", WAVE_LOW),
    ("YawnStretch", YAWN_STRETCH),
    ("Cringe", CRINGE),
    ("Kneel", KNEEL),
    ("Plead", PLEAD),
    ("Shiver", SHIVER),
    ("Shoo", SHOO),
    ("Slouch", SLOUCH),
    ("Spit", SPIT),
    ("Surrender", SURRENDER),
    ("Woah", WOAH),
    ("Winded", WINDED),
    ("Pray", PRAY),
    ("Mock", MOCK),
    ("Teapot", TEAPOT),
    ("WarmHands", WARM_HANDS),
    ("Helper", HELPER),
    ("NudgeLeft", NUDGE_LEFT),
    ("NudgeRight", NUDGE_RIGHT),
    ("PointLeft", POINT_LEFT),
    ("PointRight", POINT_RIGHT),
    ("PointDown", POINT_DOWN),
    ("Knock", KNOCK),
    ("ScanHorizon", SCAN_HORIZON),
    ("DrudgeDance", DRUDGE_DANCE),
    ("HaveASeat", HAVE_A_SEAT),
    ("StopTurning", STOP_TURNING),
    ("Jump", JUMP),
    ("Stop", STOP),
    ("Fallen", FALLEN),
    ("Interpolating", INTERPOLATING),
    ("Hover", HOVER),
    ("On", ON),
    ("Off", OFF),
    ("Dead", DEAD),
    ("Falling", FALLING),
    ("Reload", RELOAD),
    ("Unload", UNLOAD),
    ("Pickup", PICKUP),
    ("StoreInBackpack", STORE_IN_BACKPACK),
    ("Eat", EAT),
    ("Drink", DRINK),
    ("Reading", READING),
    ("JumpCharging", JUMP_CHARGING),
    ("AimLevel", AIM_LEVEL),
    ("AimHigh15", AIM_HIGH15),
    ("AimHigh30", AIM_HIGH30),
    ("AimHigh45", AIM_HIGH45),
    ("AimHigh60", AIM_HIGH60),
    ("AimHigh75", AIM_HIGH75),
    ("AimHigh90", AIM_HIGH90),
    ("AimLow15", AIM_LOW15),
    ("AimLow30", AIM_LOW30),
    ("AimLow45", AIM_LOW45),
    ("AimLow60", AIM_LOW60),
    ("AimLow75", AIM_LOW75),
    ("AimLow90", AIM_LOW90),
    ("MagicBlast", MAGIC_BLAST),
    ("MagicSelfHead", MAGIC_SELF_HEAD),
    ("MagicSelfHeart", MAGIC_SELF_HEART),
    ("MagicBonus", MAGIC_BONUS),
    ("MagicClap", MAGIC_CLAP),
    ("MagicHarm", MAGIC_HARM),
    ("MagicHeal", MAGIC_HEAL),
    ("MagicThrowMissile", MAGIC_THROW_MISSILE),
    ("MagicRecoilMissile", MAGIC_RECOIL_MISSILE),
    ("MagicPenalty", MAGIC_PENALTY),
    ("MagicTransfer", MAGIC_TRANSFER),
    ("MagicVision", MAGIC_VISION),
    ("MagicEnchantItem", MAGIC_ENCHANT_ITEM),
    ("MagicPortal", MAGIC_PORTAL),
    ("MagicPray", MAGIC_PRAY),
    ("CastSpell", CAST_SPELL),
    ("UseMagicStaff", USE_MAGIC_STAFF),
    ("UseMagicWand", USE_MAGIC_WAND),
    ("TwitchSubstate1", TWITCH_SUBSTATE1),
    ("TwitchSubstate2", TWITCH_SUBSTATE2),
    ("TwitchSubstate3", TWITCH_SUBSTATE3),
    ("Pickup5", PICKUP5),
    ("Pickup10", PICKUP10),
    ("Pickup15", PICKUP15),
    ("Pickup20", PICKUP20),
    ("Ready", READY),
    ("Crouch", CROUCH),
    ("Sitting", SITTING),
    ("Sleeping", SLEEPING),
    ("ATOYOT", ATOYOT),
    ("ShakeFistState", SHAKE_FIST_STATE),
    ("PrayState", PRAY_STATE),
    ("BowDeepState", BOW_DEEP_STATE),
    ("ClapHandsState", CLAP_HANDS_STATE),
    ("CrossArmsState", CROSS_ARMS_STATE),
    ("ShiverState", SHIVER_STATE),
    ("PointState", POINT_STATE),
    ("WaveState", WAVE_STATE),
    ("AkimboState", AKIMBO_STATE),
    ("SaluteState", SALUTE_STATE),
    ("ScratchHeadState", SCRATCH_HEAD_STATE),
    ("TapFootState", TAP_FOOT_STATE),
    ("LeanState", LEAN_STATE),
    ("KneelState", KNEEL_STATE),
    ("PleadState", PLEAD_STATE),
    ("SlouchState", SLOUCH_STATE),
    ("SurrenderState", SURRENDER_STATE),
    ("WoahState", WOAH_STATE),
    ("WindedState", WINDED_STATE),
    ("SnowAngelState", SNOW_ANGEL_STATE),
    ("CurtseyState", CURTSEY_STATE),
    ("AFKState", AFK_STATE),
    ("MeditateState", MEDITATE_STATE),
    ("SitState", SIT_STATE),
    ("SitCrossleggedState", SIT_CROSSLEGGED_STATE),
    ("SitBackState", SIT_BACK_STATE),
    ("PointLeftState", POINT_LEFT_STATE),
    ("PointRightState", POINT_RIGHT_STATE),
    ("TalktotheHandState", TALKTOTHE_HAND_STATE),
    ("PointDownState", POINT_DOWN_STATE),
    ("DrudgeDanceState", DRUDGE_DANCE_STATE),
    ("PossumState", POSSUM_STATE),
    ("ReadState", READ_STATE),
    ("ThinkerState", THINKER_STATE),
    ("HaveASeatState", HAVE_A_SEAT_STATE),
    ("AtEaseState", AT_EASE_STATE),
    ("RunForward", RUN_FORWARD),
    ("WalkForward", WALK_FORWARD),
    ("WalkBackwards", WALK_BACKWARDS),
    ("TurnRight", TURN_RIGHT),
    ("TurnLeft", TURN_LEFT),
    ("SideStepRight", SIDE_STEP_RIGHT),
    ("SideStepLeft", SIDE_STEP_LEFT),
    ("HandCombat", HAND_COMBAT),
    ("NonCombat", NON_COMBAT),
    ("SwordCombat", SWORD_COMBAT),
    ("BowCombat", BOW_COMBAT),
    ("SwordShieldCombat", SWORD_SHIELD_COMBAT),
    ("CrossbowCombat", CROSSBOW_COMBAT),
    ("UnusedCombat", UNUSED_COMBAT),
    ("SlingCombat", SLING_COMBAT),
    ("TwoHandedSwordCombat", TWO_HANDED_SWORD_COMBAT),
    ("TwoHandedStaffCombat", TWO_HANDED_STAFF_COMBAT),
    ("DualWieldCombat", DUAL_WIELD_COMBAT),
    ("ThrownWeaponCombat", THROWN_WEAPON_COMBAT),
    ("Graze", GRAZE),
    ("Magic", MAGIC),
    ("BowNoAmmo", BOW_NO_AMMO),
    ("CrossBowNoAmmo", CROSS_BOW_NO_AMMO),
    ("AtlatlCombat", ATLATL_COMBAT),
    ("ThrownShieldCombat", THROWN_SHIELD_COMBAT),
    ("HoldRun", HOLD_RUN),
    ("HoldSidestep", HOLD_SIDESTEP),
];
