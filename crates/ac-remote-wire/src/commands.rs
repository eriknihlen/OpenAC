//! The commands a window sends its session: every [`SessionCommand`] but
//! the three that only mean something inside one process (`Attach` carries
//! a channel; `Detach` and `Shutdown` end the window or the session). Those
//! have no tag: encoding one is an error, and no bytes decode to one.
//!
//! A generation-stamped command holds one command that is not stamped
//! itself: a stamp inside a stamp is an error both ways.
//!
//! Which commands a remote may send is the endpoint's business; this only
//! carries them faithfully.

use std::time::Duration;

use ac_core::chat::LogTextType;
use ac_nav::motor::{MoveDirection, MovePace, MoveRequest, MoveUnit};
use ac_runtime::character::CharacterCommand;
use ac_runtime::communication::{ChatEntryCommand, JournalCommand};
use ac_runtime::gameplay::selection_cycle::{SelectionCycleDirection, SelectionCycleKind};
use ac_runtime::gameplay::{
    AllegianceCommand, BarberCommand, BookCommand, ChessCommand, CombatAttackCommand,
    FellowshipCommand, GameplayCommand, InputActivation, SelectionSource,
};
use ac_runtime::inventory::{
    HousePurchaseCommand, HousePurchaseTab, ItemCommand, ObjectRef, VendorCommand,
};
use ac_runtime::movement::{MovementCommand, MovementKeys, Posture};
use ac_runtime::session::creation::{AppearanceSlot, ChargenAttribute, CreationCommand, ShadeSlot};
use ac_runtime::session::{
    CharacterSelector, Generation, LifecycleCommand, Recall, SessionCommand,
};

use ac_core::chess::ChessSquare;
use ac_core::combat::{AttackHeight, CombatMode};
use ac_core::items::ShortcutEntry;
use ac_runtime::communication::{ChatChannelKind, ClientConfirmation};
use ac_runtime::world::transit::{DestinationReadiness, DestinationShown};

use crate::codec::{
    DecodeContext, DecodeError, EncodeContext, EncodeError, Reader, Shape, Wire, Writer, record,
    union,
};

record! {
    MovementKeys {
        forward: bool,
        backward: bool,
        strafe_left: bool,
        strafe_right: bool,
        turn_left: bool,
        turn_right: bool,
        walk: bool,
        jump: bool,
    }
    MoveRequest { direction: MoveDirection, pace: MovePace, amount: f32, unit: MoveUnit }
}

union! {
    Recall: "recall" {
        0 => Lifestone,
        1 => Marketplace,
        2 => House,
        3 => Mansion,
        4 => AllegianceHometown,
    }
    ChatEntryCommand: "chat entry command" {
        0 => Submit(text: Option<String>),
        1 => SetDraft(text: String),
        2 => Compose(text: String),
        3 => SetChannel(channel: ChatChannelKind),
        4 => SetTellTarget { name: String, guid: u32 },
        5 => RecallPrevious,
        6 => RecallNext,
        7 => ResetRecall,
        8 => Clear,
    }
    JournalCommand: "journal command" {
        0 => NewPage,
        1 => DeletePage(page: usize),
        2 => GotoPage(page: usize),
        3 => Edit { label: String, title: String, notes: String },
        4 => RecordLocation { x: f32, y: f32 },
        5 => SetTimer { days: i32, hours: i32, minutes: i32 },
        6 => StartTimer,
        7 => ResetTimer,
    }
    ObjectRef: "object reference" {
        0 => Guid(guid: u32),
        1 => Name(name: String),
        2 => Player,
    }
    VendorCommand: "vendor command" {
        0 => Show,
        1 => Close,
        2 => AddToBuyList { item: ObjectRef, amount: i32 },
        3 => RemoveFromBuyList { item: ObjectRef, amount: i32 },
        4 => ClearBuyList,
        5 => BuyAll,
        6 => AddToSellList(item: ObjectRef),
        7 => RemoveFromSellList(item: ObjectRef),
        8 => ClearSellList,
        9 => SellAll,
    }
    HousePurchaseTab: "house purchase tab" {
        0 => Buy,
        1 => Rent,
    }
    HousePurchaseCommand: "house purchase command" {
        0 => Stage { tab: HousePurchaseTab, item: u32 },
        1 => Unstage { tab: HousePurchaseTab, item: u32 },
        2 => Submit { tab: HousePurchaseTab, slumlord: u32 },
        3 => Close,
    }
    ItemCommand: "item command" {
        0 => Use(item: ObjectRef),
        1 => UseWorldObject(item: ObjectRef),
        2 => UseOn { item: ObjectRef, target: ObjectRef },
        3 => PickUp(item: ObjectRef),
        4 => PlaceInPack(item: ObjectRef),
        5 => PickUpToMainPack(item: ObjectRef),
        6 => SetOpenPack(pack: u32),
        7 => MoveToContainer {
            item: ObjectRef,
            container: ObjectRef,
            slot: i32,
            amount: u32,
            join_stack: bool,
        },
        8 => Merge { from: ObjectRef, to: ObjectRef, amount: u32 },
        9 => Drop { item: ObjectRef, amount: u32 },
        10 => Give { item: ObjectRef, target: ObjectRef, amount: u32 },
        11 => Wield { item: ObjectRef, location: u32 },
        12 => WieldSecondary(item: ObjectRef),
        13 => Unwield(item: ObjectRef),
        14 => Appraise(item: ObjectRef),
        15 => StopViewing(item: ObjectRef),
        16 => Buy(items: Vec<(ObjectRef, i32)>),
        17 => Sell(items: Vec<(ObjectRef, i32)>),
        18 => Vendor(command: VendorCommand),
        19 => OpenTrade(target: ObjectRef),
        20 => AddToTrade(item: ObjectRef),
        21 => AcceptTrade,
        22 => DeclineTrade,
        23 => ResetTrade,
        24 => CloseTrade,
        25 => HousePurchase(command: HousePurchaseCommand),
        26 => DropInto {
            item: ObjectRef,
            container: ObjectRef,
            slot: i32,
            amount: u32,
            pickup: bool,
            join_stack: bool,
        },
        27 => DropOnStack {
            item: ObjectRef,
            target: ObjectRef,
            merge_amount: u32,
            container: ObjectRef,
            slot: Option<i32>,
            amount: u32,
            pickup: bool,
        },
        28 => DropOnBackpack(item: ObjectRef),
        29 => MarkSalvage(items: Vec<u32>),
        30 => AddShortcut(entry: ShortcutEntry),
        31 => RemoveShortcut(slot: i32),
        32 => PickUpToShortcut { item: ObjectRef, slot: i32 },
        33 => EnterUseMode,
        34 => EnterExamineMode,
        35 => CancelTargetMode,
        36 => OfferTarget(target: ObjectRef),
        37 => CloseContainer(container: ObjectRef),
        38 => Inscribe { item: ObjectRef, text: String },
        39 => Salvage { tool: ObjectRef, items: Vec<ObjectRef> },
        40 => QueryMana(item: ObjectRef),
        41 => RefreshCurrentAppraisal,
        42 => UseSelected,
        43 => ExamineSelected,
        44 => PickUpSelected,
        45 => PlaceSelectedInPack,
        46 => PickUpSelectedToMainPack,
        47 => GiveSelectedToPrevious { amount: u32 },
        48 => DropSelected { amount: u32 },
        49 => Place { item: ObjectRef, target: Option<ObjectRef>, amount: u32 },
    }
    SelectionSource: "selection source" {
        0 => System,
        1 => Keyboard,
        2 => Mouse,
        3 => Host,
    }
    SelectionCycleKind: "selection cycle kind" {
        0 => Item,
        1 => CompassItem,
        2 => Monster,
        3 => Player,
        4 => UnopenedCorpse,
    }
    SelectionCycleDirection: "selection cycle direction" {
        0 => Closest,
        1 => Previous,
        2 => Next,
    }
    CombatAttackCommand: "combat attack command" {
        0 => LowAttack,
        1 => MediumAttack,
        2 => HighAttack,
        3 => DecreasePower,
        4 => IncreasePower,
        5 => AbortForMovement,
    }
    InputActivation: "input activation" {
        0 => Press,
        1 => Release,
    }
    BookCommand: "book command" {
        0 => Turn(page: i32),
        1 => Write(text: String),
        2 => Close,
        3 => TurnWriting { page: i32, typed: Option<String> },
        4 => CloseWriting(typed: Option<String>),
        5 => Show,
    }
    ChessCommand: "chess command" {
        0 => Move { from: ChessSquare, to: ChessSquare },
        1 => Pass,
        2 => ToggleStalemate,
        3 => Resign,
    }
    AppearanceSlot: "appearance slot" {
        0 => EyesStrip,
        1 => NoseStrip,
        2 => MouthStrip,
        3 => HairStyle,
        4 => HairColor,
        5 => EyeColor,
        6 => HeadgearStyle,
        7 => HeadgearColor,
        8 => ShirtStyle,
        9 => ShirtColor,
        10 => TrousersStyle,
        11 => TrousersColor,
        12 => FootwearStyle,
        13 => FootwearColor,
    }
    ShadeSlot: "shade slot" {
        0 => Skin,
        1 => Hair,
        2 => Headgear,
        3 => Shirt,
        4 => Trousers,
        5 => Footwear,
    }
    BarberCommand: "barber command" {
        0 => SetAppearance(slot: AppearanceSlot, value: u32),
        1 => SetShade(slot: ShadeSlot, shade: f64),
        2 => SetToggle(on: bool),
        3 => Finish,
        4 => Cancel,
    }
    FellowshipCommand: "fellowship command" {
        0 => Create { name: String, share_xp: bool },
        1 => Recruit(target: ObjectRef),
        2 => Dismiss(target: ObjectRef),
        3 => Quit { disband: bool },
        4 => AssignLeader(target: ObjectRef),
        5 => SetOpen(open: bool),
        6 => RequestVitals(on: bool),
        7 => SetPanelOpen(open: bool),
        8 => Show,
    }
    AllegianceCommand: "allegiance command" {
        0 => Swear(target: ObjectRef),
        1 => Break(target: ObjectRef),
        2 => Kick(target: ObjectRef),
        3 => Info(name: String),
        4 => Subscribe(on: bool),
        5 => Show,
    }
    GameplayCommand: "gameplay command" {
        0 => Select(target: ObjectRef),
        1 => SelectAs(target: ObjectRef, source: SelectionSource),
        2 => ClearSelection,
        3 => ClearRevealedSelection(object: u32),
        4 => Escape,
        5 => SelectPrevious,
        6 => SelectNearestMonster,
        7 => CycleSelection { kind: SelectionCycleKind, direction: SelectionCycleDirection },
        8 => UseUnopenedCorpse(direction: SelectionCycleDirection),
        9 => SelectSelf,
        10 => SelectLastAttacker,
        11 => CycleFellow { previous: bool },
        12 => Cast { spell: String, target: Option<ObjectRef> },
        13 => CastWithoutSelecting { spell: u32, target: u32 },
        14 => ListSpells(filter: Option<String>),
        15 => ShowEnchantments,
        16 => ShowCharacter,
        17 => ForgetSpell(spell: String),
        18 => AddFavorite { tab: usize, position: usize, spell: String },
        19 => RemoveFavorite { tab: usize, spell: String },
        20 => SetSpellbookFilter(filter: u32),
        21 => SetDesiredComponent { component: u32, amount: u32 },
        22 => ClearDesiredComponents,
        23 => ToggleCombat,
        24 => SetCombatMode(mode: CombatMode),
        25 => Attack { height: AttackHeight, target: Option<ObjectRef> },
        26 => AttackInput(command: CombatAttackCommand, activation: InputActivation),
        27 => SetAttackPower(power: f32),
        28 => ShowCombat,
        29 => Book(command: BookCommand),
        30 => Chess(command: ChessCommand),
        31 => Barber(command: BarberCommand),
        32 => Fellowship(command: FellowshipCommand),
        33 => Allegiance(command: AllegianceCommand),
        34 => QueryHouse,
        35 => ShowHouse,
        36 => ShowVitae,
        37 => SetTitle(title: u32),
        38 => ShowTitles,
    }
    CharacterSelector: "character selector" {
        0 => FirstAvailable,
        1 => Index(index: usize),
        2 => Id(id: u32),
        3 => Name(name: String),
    }
    ChargenAttribute: "chargen attribute" {
        1 => Strength,
        2 => Endurance,
        3 => Quickness,
        4 => Coordination,
        5 => Focus,
        6 => Self_,
    }
    CreationCommand: "creation command" {
        0 => Show,
        1 => SelectHeritage(heritage: u32),
        2 => SelectGender(gender: u32),
        3 => SelectTemplate(template: u32),
        4 => SetAttribute(attribute: ChargenAttribute, value: i32),
        5 => SetAttributeLock(attribute: ChargenAttribute, locked: bool),
        6 => TrainSkill(skill: u32),
        7 => SpecializeSkill(skill: u32),
        8 => UntrainSkill(skill: u32),
        9 => SetAppearance(slot: AppearanceSlot, value: u32),
        10 => SetShade(slot: ShadeSlot, shade: f64),
        11 => SelectStartArea(area: i32),
        12 => SetName(name: String),
        13 => SetSlot(slot: u32),
        14 => Finish { confirm_unspent_credits: bool },
        15 => AcknowledgeRejection,
        16 => RandomizeCharacter,
        17 => RandomizeAppearance,
        18 => RandomizeClothing,
    }
    LifecycleCommand: "lifecycle command" {
        0 => Reconnect,
        1 => LogOff,
        2 => EndCharacterSession,
        3 => ShowSelection,
        4 => Highlight(character: CharacterSelector),
        5 => Enter(character: Option<CharacterSelector>),
        6 => RequestDelete(character: Option<CharacterSelector>),
        7 => ConfirmDelete,
        8 => Restore(character: Option<CharacterSelector>),
        9 => Cancel,
        10 => SetNextLogin(character: Option<CharacterSelector>),
        11 => Create(command: CreationCommand),
    }
    Posture: "posture" {
        0 => Ready,
        1 => Sit,
        2 => Crouch,
        3 => Sleep,
    }
    MoveDirection: "move direction" {
        0 => Forward,
        1 => Backward,
        2 => StrafeLeft,
        3 => StrafeRight,
        4 => TurnLeft,
        5 => TurnRight,
    }
    MovePace: "move pace" {
        0 => Walk,
        1 => Run,
    }
    MoveUnit: "move unit" {
        0 => MetersOrDegrees,
        1 => Seconds,
    }
    MovementCommand: "movement command" {
        0 => Keys(keys: MovementKeys),
        1 => ToggleRunLock,
        2 => Stop,
        3 => Posture(posture: Posture),
        4 => Move(request: MoveRequest),
        5 => Jump { power: f32, leave_at: Option<MovePace> },
        6 => StopMoves,
        7 => TurnToHeading(heading: f32),
        8 => FirstPersonTurn(degrees: f32),
        9 => Status,
        10 => BeginMouseLook,
        11 => MouseTurn(delta: f32),
        12 => StopMouseDrift,
        13 => EndMouseLook,
        14 => BeginModernMouseLook,
        15 => MouseLookFacing(heading: f32),
        16 => Motion(motion: u32),
    }
    CharacterCommand: "character command" {
        0 => RaiseAttribute { attribute: u32, xp: u64 },
        1 => RaiseVital { vital: u32, xp: u64 },
        2 => RaiseSkill { skill: u32, xp: u64 },
        3 => TrainSkill { skill: u32, credits: u32 },
    }
    LogTextType: "log text type" {
        0x00 => Default,
        0x01 => All,
        0x02 => Speech,
        0x03 => Tell,
        0x04 => SpeechDirectSend,
        0x05 => System,
        0x06 => Combat,
        0x07 => Magic,
        0x08 => Channel,
        0x09 => ChannelSend,
        0x0A => Social,
        0x0B => SocialSend,
        0x0C => Emote,
        0x0D => Advancement,
        0x0E => Abuse,
        0x0F => Help,
        0x10 => Appraisal,
        0x11 => Spellcasting,
        0x12 => Allegiance,
        0x13 => Fellowship,
        0x14 => WorldBroadcast,
        0x15 => CombatEnemy,
        0x16 => CombatSelf,
        0x17 => Recall,
        0x18 => Craft,
        0x19 => Salvaging,
        0x1A => ClientLocal,
        0x1B => TurbineGeneral,
        0x1C => TurbineTrade,
        0x1D => TurbineLfg,
        0x1E => TurbineRoleplay,
        0x1F => AdminTell,
        0x20 => TurbineSociety,
        0x21 => Reserved21,
    }
}

/// The tag of a generation-stamped command.
const AT_GENERATION: u8 = 23;

/// [`SessionCommand`]: every variant but the three in-process ones, and a
/// generation stamp around one command that is not stamped.
macro_rules! session_command {
    (
        $(
            $tag:literal => $variant:ident
                $( ( $($a:ident : $at:ty),* $(,)? ) )?
                $( { $($f:ident : $ft:ty),* $(,)? } )?
        ),+ $(,)?
    ) => {
        impl Wire for SessionCommand {
            const SHAPE: u64 = Shape::new("union")
                $(
                    .tag($tag)
                    $( $( .of(<$at as Wire>::SHAPE) )* )?
                    $( $( .of(<$ft as Wire>::SHAPE) )* )?
                )+
                .tag(AT_GENERATION as u64)
                .of(<Generation as Wire>::SHAPE)
                .name("one command of this union, not stamped")
                .finish();
            fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
                encode_command(self, w, cx, false)
            }
            fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
                decode_command(r, cx, false)
            }
        }

        fn encode_command(
            command: &SessionCommand,
            w: &mut Writer,
            cx: &EncodeContext,
            stamped: bool,
        ) -> Result<(), EncodeError> {
            match command {
                $(
                    SessionCommand::$variant $( ( $($a),* ) )? $( { $($f),* } )? => {
                        w.put_raw(&[$tag]);
                        $( $( <$at as Wire>::encode($a, w, cx)?; )* )?
                        $( $( <$ft as Wire>::encode($f, w, cx)?; )* )?
                    }
                )+
                SessionCommand::AtGeneration { generation, command } => {
                    if stamped {
                        return Err(EncodeError::NestedAtGeneration);
                    }
                    w.put_raw(&[AT_GENERATION]);
                    generation.encode(w, cx)?;
                    encode_command(command, w, cx, true)?;
                }
                SessionCommand::Attach(_) => return Err(EncodeError::NotAWireCommand("Attach")),
                SessionCommand::Detach => return Err(EncodeError::NotAWireCommand("Detach")),
                SessionCommand::Shutdown => return Err(EncodeError::NotAWireCommand("Shutdown")),
            }
            Ok(())
        }

        fn decode_command(
            r: &mut Reader<'_>,
            cx: &DecodeContext,
            stamped: bool,
        ) -> Result<SessionCommand, DecodeError> {
            Ok(match r.byte()? {
                $(
                    $tag => SessionCommand::$variant
                        $( ( $( <$at as Wire>::decode(r, cx)? ),* ) )?
                        $( { $( $f: <$ft as Wire>::decode(r, cx)? ),* } )?,
                )+
                AT_GENERATION => {
                    if stamped {
                        return Err(DecodeError::NestedAtGeneration);
                    }
                    SessionCommand::AtGeneration {
                        generation: Generation::decode(r, cx)?,
                        command: Box::new(decode_command(r, cx, true)?),
                    }
                }
                tag => {
                    return Err(DecodeError::UnknownTag {
                        what: "session command",
                        tag: u32::from(tag),
                    })
                }
            })
        }
    };
}

session_command! {
    0 => Chat(line: String),
    1 => ChatInput {
        text: String,
        channel: ChatChannelKind,
        tell_target: Option<String>,
        tell_target_guid: u32,
    },
    2 => ChatEntry(command: ChatEntryCommand),
    3 => Say(text: String),
    4 => Tell { target: String, text: String },
    5 => Reply(text: String),
    6 => Emote(text: String),
    7 => Channel { channel: ChatChannelKind, text: String },
    8 => Recall(recall: Recall),
    9 => AddFriend(name: String),
    10 => RemoveFriend(name: String),
    11 => Squelch { add: bool, name: String, account: bool },
    12 => AbandonContract(contract: u32),
    13 => ListContracts,
    14 => Journal(command: JournalCommand),
    15 => ShowJournal,
    16 => SetCharacterOption { option: u32, value: bool },
    17 => SaveCharacterOptions,
    18 => ShowCharacterOptions,
    19 => Item(command: ItemCommand),
    20 => ListInventory,
    21 => Gameplay(command: GameplayCommand),
    22 => Lifecycle(command: LifecycleCommand),
    // 23 is the generation stamp.
    24 => Movement(command: MovementCommand),
    25 => ListObjects,
    26 => AnswerConfirmation { confirmation: ClientConfirmation, accepted: bool },
    27 => AnswerOpenConfirmation { accepted: bool },
    28 => ListCorpses(metres: f32),
    29 => GameTime,
    30 => Character(command: CharacterCommand),
    31 => Ping,
    32 => ShowLinkStatus,
    33 => DismissGhost(guid: u32),
    34 => ShowInterfaceText(text: String),
    35 => ShowText { text: String, log_text_type: LogTextType },
    36 => WindowMinimized(minimized: bool),
    37 => ChatLinkClicked { east_west: f64, north_south: f64, text: String },
    38 => PluginUi(input: ac_plugin_ui::UiInput),
    39 => DestinationShown(shown: DestinationShown),
    40 => LoginShown(readiness: DestinationReadiness),
    41 => PresentsWorld(presents: bool),
    42 => RevealWaited { generation: u64, waited: Duration },
}
