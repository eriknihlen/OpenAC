//! The gameplay owners of one session: the selection, magic (spellbook,
//! casting), combat (mode, attacks, targets, deaths, the health stream),
//! the open book, the fellowship, the allegiance and the house.
//!
//! [`Gameplay`] takes the server messages these owners read, runs the host
//! commands that name them, sends its requests through the session's
//! [`Outbox`], and hands back what the player reads ([`GameplayNotice`],
//! worded by the communication owner) and what a host shows
//! ([`GameplayEvent`]).

pub mod allegiance;
pub mod book;
pub mod chess;
pub mod combat;
pub mod combat_animation;
pub mod death;
pub mod fellowship;
pub mod house;
pub mod magic;
pub mod selection;
pub mod selection_cycle;
pub mod tables;
pub mod titles;

use std::sync::Arc;

use ac_core::chat::{CombatLineKind, CombatNotice, LogTextType};
use ac_core::combat::{
    CombatEvent, CombatObject, CombatState, is_hostile_monster, motion_command,
    object_is_attackable, player_in_ready_position, player_in_ready_position_for_attack,
    should_query_health,
};
use ac_core::items::{ClientObject, ItemType};
use ac_core::physics::PhysicsState;
use ac_core::properties::{PropertyDataId, PropertyInt};
use ac_proto::book::{
    Inscription, OpenBook, PageDataResponse, PageResponse, build_book_add_page, build_book_data,
    build_book_delete_page, build_book_modify_page, build_book_page_data,
};
use ac_proto::{
    GameEventEnvelope, GameEventType, Inbound, PlayerDescription, ProtoError,
    allegiance as wire_allegiance, chess as wire_chess, combat as wire_combat,
    fellowship as wire_fellowship, house as wire_house, magic as wire_magic,
};

use crate::character::{CharacterOptions, option_id};
use crate::inventory::ObjectRef;
use crate::local_time::LocalZone;
use crate::movement::{BodyMotion, Movement};
use crate::session::{Clocks, Outbox};
use crate::world::{World, distance};

pub use ac_core::combat::{AttackHeight, CombatMode};
pub use allegiance::{AllegianceMemberView, AllegianceState, AllegianceView};
pub use book::{BookFlushAction, BookPageAction, BookPageTurn, BookState, BookView};
pub use chess::{ChessCommand, ChessStage, ChessState, ChessView};
pub use combat::{
    CombatAttackCommand, CombatAttackState, CombatModeRequestResult, CombatModeRequestStatus,
    CombatModeState, CombatTargetState, CreatureDeathState, HealthQuery, InputActivation,
};
pub use death::{
    CorpseSummary, DeathState, LAST_OUTSIDE_DEATH_POSITION, VitaeView, corpses,
    last_outside_corpse_cell, vitae_view,
};
pub use fellowship::{Departure, FellowView, FellowshipState, FellowshipView};
pub use house::{HouseLineColor, HouseState, HouseView};
pub use magic::{
    CastRequestResult, CharacterSheet, EnchantmentView, SheetLine, SpellCast, SpellCastCompletion,
    SpellSummary, SpellbookView,
};
pub use selection::{Selection, SelectionReason, SelectionSource, SelectionTransition};
pub use tables::{ChatTaboo, ComponentDetails, GameTables, TablesError};
pub use titles::{TitleChange, TitleState, TitlesView};

/// The operations over the session for the state machines, borrowing the
/// owner's fields one by one so the machine being driven stays free.
macro_rules! ops {
    ($self:ident, $ctx:ident) => {
        Ops {
            world: &*$ctx.world,
            outbox: &mut *$ctx.outbox,
            options: $ctx.options,
            active: $ctx.active,
            tables: &$self.tables,
            account: &$self.account,
            selection: &mut $self.selection,
            transitions: &mut $self.transitions,
            health: &mut $self.health,
            deaths: &$self.deaths,
            combat: &$self.combat,
            notices: &mut $self.notices,
            busy: &mut $self.busy,
            explicit_mode_request: &mut $self.explicit_mode_request,
            body: $ctx.body.as_deref_mut(),
        }
    };
}

/// The selected object as the world showed it (see
/// [`Gameplay::selected_presence`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SelectedPresence {
    /// The selected object.
    pub guid: u32,
    /// It stood in the world.
    pub placed: bool,
    /// Its physics state hid it.
    pub hidden: bool,
}

/// The most of one component the buy list may ask for.
pub const MAX_DESIRED_COMPONENT_AMOUNT: u32 = 5000;

/// The player's int property holding the combat mode the server set.
pub const COMBAT_MODE_PROPERTY: u32 = 40;

/// A fellowship request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FellowshipCommand {
    /// Found a fellowship.
    Create {
        /// Its name.
        name: String,
        /// Share experience.
        share_xp: bool,
    },
    /// Invite a player.
    Recruit(ObjectRef),
    /// Dismiss a member.
    Dismiss(ObjectRef),
    /// Leave (or, as leader, disband).
    Quit {
        /// Disband instead of leaving.
        disband: bool,
    },
    /// Hand the lead to a member.
    AssignLeader(ObjectRef),
    /// Open or close recruiting to every member.
    SetOpen(bool),
    /// Stream member vitals or stop (the console's request; the panel
    /// declares itself apart, [`FellowshipCommand::SetPanelOpen`]).
    RequestVitals(bool),
    /// The fellowship panel opened or closed: member vitals stream while it
    /// is open or the vitals are requested.
    SetPanelOpen(bool),
    /// Reply with [`GameplayEvent::Fellowship`].
    Show,
}

/// An allegiance request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AllegianceCommand {
    /// Swear allegiance to a patron.
    Swear(ObjectRef),
    /// Break allegiance with a patron or vassal.
    Break(ObjectRef),
    /// Remove a vassal.
    Kick(ObjectRef),
    /// Ask for a player's allegiance (the answer is chat).
    Info(String),
    /// Ask the server to keep the allegiance up to date (or stop).
    Subscribe(bool),
    /// Reply with [`GameplayEvent::Allegiance`].
    Show,
}

/// A book request (the open book).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum BookCommand {
    /// Turn to a page (0-based), saving nothing.
    Turn(i32),
    /// Write the page being read: save the text (blank deletes a page the
    /// player wrote).
    Write(String),
    /// Close the book.
    Close,
    /// Turn to a page (0-based), saving what the reader typed on the page
    /// being left (`None`: the page is not theirs to write, nothing is
    /// saved): the book reader's page buttons and page menu.
    TurnWriting {
        /// The page to turn to.
        page: i32,
        /// What the reader typed on the page being left.
        typed: Option<String>,
    },
    /// Save what the reader typed on the open page (`None`: nothing), then
    /// close the book: the book reader hidden or closed.
    CloseWriting(Option<String>),
    /// Reply with [`GameplayEvent::Book`].
    Show,
}

/// A gameplay request from a host.
#[derive(Debug, Clone, PartialEq)]
pub enum GameplayCommand {
    /// Select an object.
    Select(ObjectRef),
    /// Clear the selection, on purpose.
    ClearSelection,
    /// A login or portal reveal began while the window showed this selected
    /// object in its world (OpenAC `WorldGenerationQuiescence`): the client
    /// clears the selection, if it is still that object.
    ClearRevealedSelection(u32),
    /// The Escape key, in the game's order: finish a jump being charged;
    /// else stop a moving body (and a repeating attack); else leave a
    /// use or examine mode; else clear the selection; else ask the window
    /// for the options window's gameplay page. The session answers it.
    Escape,
    /// Bring back the previous selection.
    SelectPrevious,
    /// Select the nearest monster.
    SelectNearestMonster,
    /// Step the selection to another thing of a kind nearby (the radar's
    /// order).
    CycleSelection {
        /// What to step through.
        kind: selection_cycle::SelectionCycleKind,
        /// Closest, next or previous.
        direction: selection_cycle::SelectionCycleDirection,
    },
    /// Select the nearest (or next) corpse not opened this session; the
    /// session then uses whatever is selected.
    UseUnopenedCorpse(selection_cycle::SelectionCycleDirection),
    /// Select the player's own character (the session offers it to a
    /// use or examine mode instead when one waits for a target).
    SelectSelf,
    /// Select the creature attacking the player, when it is within radar
    /// range.
    SelectLastAttacker,
    /// Step the selection through the fellowship's members, in the
    /// fellowship's order, wrapping round.
    CycleFellow {
        /// Step back rather than on.
        previous: bool,
    },
    /// Cast a known spell (by id or name); a named target is selected
    /// first, as clicking it would.
    Cast {
        /// The spell.
        spell: String,
        /// The target.
        target: Option<ObjectRef>,
    },
    /// Cast a known spell at an object without selecting it: the
    /// selection is neither read nor changed. A self or untargeted spell
    /// ignores the target, as [`GameplayCommand::Cast`] ignores the
    /// selection; 0 is no target.
    CastWithoutSelecting {
        /// The spell id.
        spell: u32,
        /// The target object.
        target: u32,
    },
    /// Reply with [`GameplayEvent::Spellbook`]; the text filters names.
    ListSpells(Option<String>),
    /// Reply with [`GameplayEvent::Enchantments`].
    ShowEnchantments,
    /// Reply with [`GameplayEvent::Character`].
    ShowCharacter,
    /// Forget a spell.
    ForgetSpell(String),
    /// Put a spell on a favorite bar.
    AddFavorite {
        /// Bar (0..8).
        tab: usize,
        /// Position on the bar.
        position: usize,
        /// The spell.
        spell: String,
    },
    /// Take a spell off a favorite bar.
    RemoveFavorite {
        /// Bar.
        tab: usize,
        /// The spell.
        spell: String,
    },
    /// Set the spellbook filter bits.
    SetSpellbookFilter(u32),
    /// Set a desired component amount.
    SetDesiredComponent {
        /// Spell component id.
        component: u32,
        /// How many to keep.
        amount: u32,
    },
    /// Clear every desired component amount.
    ClearDesiredComponents,
    /// The combat toggle.
    ToggleCombat,
    /// Ask for one combat mode.
    SetCombatMode(CombatMode),
    /// Press and release an attack key at a height; a named target is
    /// selected first.
    Attack {
        /// High, medium or low.
        height: AttackHeight,
        /// The target.
        target: Option<ObjectRef>,
    },
    /// An attack key, power key or the movement abort.
    AttackInput(CombatAttackCommand, InputActivation),
    /// Set the power bar (0..=1).
    SetAttackPower(f32),
    /// Reply with [`GameplayEvent::Combat`].
    ShowCombat,
    /// The open book.
    Book(BookCommand),
    /// The chess game.
    Chess(ChessCommand),
    /// The fellowship.
    Fellowship(FellowshipCommand),
    /// The allegiance.
    Allegiance(AllegianceCommand),
    /// Ask the server for the house (the answer updates the house owner).
    QueryHouse,
    /// Reply with [`GameplayEvent::House`].
    ShowHouse,
    /// Reply with [`GameplayEvent::Vitae`].
    ShowVitae,
    /// Display this earned title (the titles panel's choice).
    SetTitle(u32),
    /// Reply with [`GameplayEvent::Titles`].
    ShowTitles,
}

/// The combat owner's state as a host shows it.
#[derive(Debug, Clone, PartialEq)]
pub struct CombatView {
    /// The mode.
    pub mode: CombatMode,
    /// The mode the server last set.
    pub server_mode: Option<CombatMode>,
    /// The selection.
    pub selected: Option<u32>,
    /// The selection's last reported health, 0..=1.
    pub selected_health: Option<f32>,
    /// The height the next swing goes at.
    pub height: AttackHeight,
    /// The power bar setting.
    pub desired_power: f32,
    /// The bar's level now.
    pub power_bar: f32,
    /// A key is held.
    pub request_in_progress: bool,
    /// A swing awaits its answer.
    pub response_pending: bool,
    /// Swings repeat.
    pub repeating: bool,
    /// The last swing's error (0 for none) and its answer count.
    pub last_completion: (u64, u32),
}

/// What a host shows.
#[derive(Debug, Clone, PartialEq)]
pub enum GameplayEvent {
    /// The selection changed.
    Selection {
        /// What is selected.
        selected: Option<u32>,
        /// Its name.
        name: Option<String>,
    },
    /// The spellbook (reply to [`GameplayCommand::ListSpells`]).
    Spellbook(SpellbookView),
    /// The active enchantments.
    Enchantments(Vec<EnchantmentView>),
    /// The character's numbers.
    Character(Box<CharacterSheet>),
    /// The server answered a cast.
    CastCompleted(SpellCastCompletion),
    /// The combat mode changed.
    CombatMode(CombatMode),
    /// The combat state (reply to [`GameplayCommand::ShowCombat`]).
    Combat(CombatView),
    /// The selected creature's health was reported.
    Health {
        /// The creature.
        guid: u32,
        /// Health, 0..=1.
        percent: f32,
    },
    /// A creature died.
    CreatureDied(u32),
    /// The player died; the death message.
    LocalPlayerDied(String),
    /// The vitae penalty changed (a death, a recovered percent) or was
    /// asked for.
    Vitae(VitaeView),
    /// The open book changed or was asked for.
    Book(BookView),
    /// The fellowship changed or was asked for.
    Fellowship(FellowshipView),
    /// The allegiance changed or was asked for.
    Allegiance(AllegianceView),
    /// The house changed or was asked for.
    House(HouseView),
    /// The earned titles or the displayed one changed, or were asked for.
    Titles(TitlesView),
    /// A command named nothing known or could not run (host feedback).
    Refused(String),
}

/// Text the player reads, worded here and routed by the communication
/// owner.
#[derive(Debug, Clone, PartialEq)]
pub enum GameplayNotice {
    /// A line of this text class (client-local text goes to the overlay).
    Text(String, LogTextType),
    /// A failure code.
    WeenieError(u32),
    /// A combat notification's line.
    Combat(CombatNotice),
    /// A death message.
    CombatLine(String, CombatLineKind),
}

/// What the gameplay owners read and write besides themselves.
#[derive(Debug)]
pub struct GameplayContext<'a> {
    /// The world (the player's spellbook lives in it).
    pub world: &'a mut World,
    /// Where requests are queued.
    pub outbox: &'a mut Outbox,
    /// The character options (auto-target, auto-repeat).
    pub options: &'a CharacterOptions,
    /// True in the world; outside it nothing is sent.
    pub active: bool,
    /// Both clocks.
    pub clocks: Clocks,
    /// The movement owner, whose body says when a stance is ready and
    /// stops before a swing or a cast. `None` (owners tested alone): no
    /// readiness is asked, and an attack reads the stance the server last
    /// sent.
    pub body: Option<&'a mut Movement>,
    /// A teleport is under way or about to start (no combat mode change is
    /// taken then).
    pub teleporting: bool,
    /// The inventory's outside containers, whose opened corpses the
    /// unopened-corpse keys skip (`None`: none opened).
    pub opened_corpses: Option<&'a ac_core::items::ExternalContainerState>,
}

impl GameplayContext<'_> {
    fn player(&self) -> u32 {
        self.world.player_guid().unwrap_or(0)
    }

    fn now(&self) -> f64 {
        self.clocks.session.as_secs_f64()
    }

    fn send(&mut self, build: impl FnOnce(u32) -> Vec<u8>) -> bool {
        if !self.active {
            return false;
        }
        self.outbox.send_action(build);
        true
    }
}

/// The gameplay owners.
#[derive(Debug)]
pub struct Gameplay {
    tables: Arc<GameTables>,
    account: String,
    zone: LocalZone,
    selection: Selection,
    cast: SpellCast,
    combat: CombatState,
    mode: CombatModeState,
    attack: CombatAttackState,
    target: CombatTargetState,
    deaths: CreatureDeathState,
    death: DeathState,
    health: HealthQuery,
    book: BookState,
    chess: ChessState,
    fellowship: FellowshipState,
    allegiance: AllegianceState,
    house: HouseState,
    titles: TitleState,
    notices: Vec<GameplayNotice>,
    events: Vec<GameplayEvent>,
    busy: u32,
    explicit_mode_request: bool,
    mode_changes: Vec<CombatMode>,
    transitions: Vec<SelectionTransition>,
    last_mode_request: Option<CombatModeRequestResult>,
}

impl Default for Gameplay {
    fn default() -> Self {
        Self::new(Arc::new(GameTables::empty()), String::new())
    }
}

impl Gameplay {
    /// Owners reading `tables`; spell formulas are personalised for
    /// `account`.
    #[must_use]
    pub fn new(tables: Arc<GameTables>, account: String) -> Self {
        Self {
            tables,
            account,
            zone: LocalZone::system(),
            selection: Selection::default(),
            cast: SpellCast::default(),
            combat: CombatState::new(),
            mode: CombatModeState::default(),
            attack: CombatAttackState::default(),
            target: CombatTargetState::default(),
            deaths: CreatureDeathState::default(),
            death: DeathState::default(),
            health: HealthQuery::default(),
            book: BookState::default(),
            chess: ChessState::default(),
            fellowship: FellowshipState::default(),
            allegiance: AllegianceState::default(),
            house: HouseState::default(),
            titles: TitleState::default(),
            notices: Vec::new(),
            events: Vec::new(),
            busy: 0,
            explicit_mode_request: false,
            mode_changes: Vec::new(),
            transitions: Vec::new(),
            last_mode_request: None,
        }
    }

    /// The tables.
    #[must_use]
    pub fn tables(&self) -> &Arc<GameTables> {
        &self.tables
    }

    /// Use a fixed local time zone (tests).
    pub fn set_time_zone(&mut self, zone: LocalZone) {
        self.zone = zone;
    }

    /// The selection.
    #[must_use]
    pub fn selection(&self) -> &Selection {
        &self.selection
    }

    /// The cast in flight and the last answer.
    #[must_use]
    pub fn spell_cast(&self) -> &SpellCast {
        &self.cast
    }

    /// How the last combat-mode request went (a plugin's mode request
    /// reads its answer here).
    #[must_use]
    pub fn last_mode_request(&self) -> Option<&CombatModeRequestResult> {
        self.last_mode_request.as_ref()
    }

    /// A plugin drives combat (it names its own targets and power; no
    /// automatic repeat or retargeting), or hands it back.
    pub fn set_automation_controlled(&mut self, controlled: bool) {
        self.attack.automation_controlled = controlled;
        self.target.automation_controlled = controlled;
    }

    /// Whether a plugin drives combat.
    #[must_use]
    pub fn automation_controlled(&self) -> bool {
        self.attack.automation_controlled
    }

    /// The combat owner's state now, as [`GameplayCommand::ShowCombat`]
    /// reports it, without the report.
    #[must_use]
    pub fn combat_view_at(
        &self,
        world: &World,
        options: &CharacterOptions,
        body: Option<&Movement>,
        now: f64,
    ) -> CombatView {
        let selected = self.selection.selected();
        let ops = ReadyOps {
            world,
            options,
            mode: self.combat.current_mode(),
            body,
        };
        CombatView {
            mode: self.combat.current_mode(),
            server_mode: self.combat.server_mode(),
            selected,
            selected_health: selected
                .filter(|&s| self.combat.has_health(s))
                .map(|s| self.combat.health_percent(s)),
            height: self.attack.requested_height(),
            desired_power: self.attack.desired_power(),
            power_bar: self.attack.power_bar_level(now, &ops),
            request_in_progress: self.attack.attack_request_in_progress(),
            response_pending: self.attack.attack_server_response_pending(),
            repeating: self.attack.repeat_attack_in_progress(),
            last_completion: (
                self.attack.completion_revision(),
                self.attack.completion_weenie_error(),
            ),
        }
    }

    /// The account name spell formulas are personalised for.
    #[must_use]
    pub fn account(&self) -> &str {
        &self.account
    }

    /// Whether the player carries the components `spell_id` needs (the
    /// component book's rule, as a cast checks it).
    #[must_use]
    pub fn has_spell_components(&self, world: &World, spell_id: u32) -> bool {
        self.tables.components().has_required_components(
            &magic::ComponentWorld(world),
            &self.account,
            spell_id,
        )
    }

    /// The combat state.
    #[must_use]
    pub fn combat(&self) -> &CombatState {
        &self.combat
    }

    /// The combat mode.
    #[must_use]
    pub fn combat_mode(&self) -> CombatMode {
        self.combat.current_mode()
    }

    /// What the chase camera keeps in view (the ViewCombatTarget option,
    /// `view_combat_target`): in melee or missile combat, the selection
    /// when it is a creature the player may attack.
    #[must_use]
    pub fn combat_camera_target(&self, world: &World, view_combat_target: bool) -> Option<u32> {
        if !view_combat_target
            || !matches!(self.combat_mode(), CombatMode::MELEE | CombatMode::MISSILE)
        {
            return None;
        }
        let guid = self.selection.selected()?;
        let creature = world
            .item(guid)
            .is_some_and(|i| i.item_type.intersects(ItemType::CREATURE));
        (creature && is_attackable_selection(world, guid, &self.deaths, &self.combat))
            .then_some(guid)
    }

    /// The attack request.
    #[must_use]
    pub fn attack(&self) -> &CombatAttackState {
        &self.attack
    }

    /// What the combat bar shows at `now` (session seconds): the height
    /// the next swing goes at, the bar's setting and its level.
    #[must_use]
    pub fn attack_bar(
        &self,
        world: &World,
        options: &CharacterOptions,
        body: Option<&Movement>,
        now: f64,
    ) -> crate::world::AttackBarView {
        let ops = ReadyOps {
            world,
            options,
            mode: self.combat.current_mode(),
            body,
        };
        crate::world::AttackBarView {
            height: self.attack.requested_height(),
            desired_power: self.attack.desired_power(),
            power_bar: self.attack.power_bar_level(now, &ops),
        }
    }

    /// The creatures known dead.
    #[must_use]
    pub fn deaths(&self) -> &CreatureDeathState {
        &self.deaths
    }

    /// The health stream.
    #[must_use]
    pub fn health_query(&self) -> &HealthQuery {
        &self.health
    }

    /// The open book.
    #[must_use]
    pub fn book(&self) -> &BookState {
        &self.book
    }

    /// The chess game.
    #[must_use]
    pub fn chess(&self) -> &ChessState {
        &self.chess
    }

    /// The fellowship.
    #[must_use]
    pub fn fellowship(&self) -> &FellowshipState {
        &self.fellowship
    }

    /// The fellowship, to seed a scenario in the session's routing tests.
    #[cfg(test)]
    pub(crate) fn fellowship_mut(&mut self) -> &mut FellowshipState {
        &mut self.fellowship
    }

    /// The allegiance.
    #[must_use]
    pub fn allegiance(&self) -> &AllegianceState {
        &self.allegiance
    }

    /// The allegiance, to seed a scenario in the plugin tests.
    #[cfg(test)]
    pub(crate) fn allegiance_mut(&mut self) -> &mut AllegianceState {
        &mut self.allegiance
    }

    /// The house.
    #[must_use]
    pub fn house(&self) -> &HouseState {
        &self.house
    }

    /// The character's titles.
    #[must_use]
    pub fn titles(&self) -> &TitleState {
        &self.titles
    }

    /// Take what the player should read, oldest first.
    pub fn drain_notices(&mut self) -> std::vec::Drain<'_, GameplayNotice> {
        self.notices.drain(..)
    }

    /// Take what a host should show, oldest first.
    pub fn drain_events(&mut self) -> std::vec::Drain<'_, GameplayEvent> {
        self.events.drain(..)
    }

    /// How many uses the casts started (each counts the player busy until
    /// the server's use-done); the inventory owner keeps the count, as a
    /// cast's part that does not hold up item requests.
    pub fn take_busy(&mut self) -> u32 {
        std::mem::take(&mut self.busy)
    }

    /// The server's use-done, when it answers a cast (the session decides:
    /// it is a cast's while no item action is outstanding).
    pub fn complete_cast_use(&mut self, weenie_error: u32) {
        if self.cast.complete_use(weenie_error) {
            self.events
                .push(GameplayEvent::CastCompleted(self.cast.last_completion()));
        }
    }

    /// What the inventory's auto-wield follows: whether the player asked
    /// for a stance, then each combat mode the character went into, in
    /// order.
    pub fn take_wield_signals(&mut self) -> (bool, Vec<CombatMode>) {
        (
            std::mem::take(&mut self.explicit_mode_request),
            std::mem::take(&mut self.mode_changes),
        )
    }

    fn text(&mut self, text: impl Into<String>) {
        self.notices
            .push(GameplayNotice::Text(text.into(), LogTextType::Default));
    }

    fn departure_notice(&mut self, departure: Departure, player: u32) {
        if let Some(line) = self.fellowship.departure_notice(departure, player) {
            self.text(line);
        }
    }

    /// An object the world forgot out of range left with its death.
    pub fn forget_expired(&mut self, object_id: u32, incarnation: u16) {
        self.deaths.forget_incarnation(object_id, incarnation);
    }

    fn refuse(&mut self, text: impl Into<String>) {
        self.events.push(GameplayEvent::Refused(text.into()));
    }

    // -- Inbound ----------------------------------------------------------

    /// The character description (after the world took it): the spellbook.
    pub fn apply_player_description(
        &mut self,
        description: &PlayerDescription,
        ctx: &mut GameplayContext<'_>,
    ) {
        magic::apply_description(ctx.world, description, ctx.now());
        // The character has arrived: the client asks for the allegiance now
        // (the server says nothing about one until asked), once a session.
        if self.allegiance.note_entered_world() {
            ctx.send(|seq| wire_allegiance::build_update_request(seq, true));
        }
    }

    /// One server message (after the world took it).
    pub fn apply(&mut self, message: &Inbound<'_>, ctx: &mut GameplayContext<'_>) {
        match message {
            Inbound::CreateObject(create) => {
                let motion = match &create.physics.movement {
                    Some(ac_proto::PhysicsMovement::Present { data, .. }) => Some(data),
                    _ => None,
                };
                if self.deaths.observe_spawn(
                    create.guid,
                    create.physics.timestamps.instance,
                    motion,
                ) {
                    self.on_creature_died(create.guid, ctx);
                }
            }
            Inbound::UpdateMotion(update)
                if self.deaths.observe_motion(
                    update.guid,
                    update.instance_sequence,
                    Some(&update.movement),
                ) =>
            {
                self.on_creature_died(update.guid, ctx);
            }
            // A deleted corpse takes its death with it, window or not.
            Inbound::DeleteObject(delete) => {
                self.deaths
                    .forget_incarnation(delete.guid, delete.instance_sequence);
            }
            Inbound::PrivateUpdatePropertyInt(update)
                if update.property == COMBAT_MODE_PROPERTY =>
            {
                let mode = CombatMode(update.value.cast_unsigned());
                if matches!(
                    mode,
                    CombatMode::NON_COMBAT
                        | CombatMode::MELEE
                        | CombatMode::MISSILE
                        | CombatMode::MAGIC
                ) {
                    let event = self.combat.set_server_combat_mode(mode);
                    self.combat_event(event, ctx);
                }
            }
            Inbound::GameEvent(event) if self.apply_game_event(event, ctx).is_err() => {
                ctx.world
                    .record_parse_failure(0xF7B0_0000 | event.event_type.0);
            }
            _ => {}
        }
        if let Some(vitae) = self.death.observe(ctx.world) {
            self.events.push(GameplayEvent::Vitae(vitae));
        }
    }

    /// What the client remembers of the player's deaths this session.
    #[must_use]
    pub fn death(&self) -> &DeathState {
        &self.death
    }

    #[allow(clippy::too_many_lines)]
    fn apply_game_event(
        &mut self,
        event: &GameEventEnvelope<'_>,
        ctx: &mut GameplayContext<'_>,
    ) -> Result<(), ProtoError> {
        let payload = event.payload;
        let player = ctx.player();
        match event.event_type {
            GameEventType::UPDATE_HEALTH => {
                let update = wire_combat::UpdateHealth::parse(payload)?;
                self.combat
                    .on_update_health(update.target_guid, update.health_percent);
                if self.selection.selected() == Some(update.target_guid) {
                    self.events.push(GameplayEvent::Health {
                        guid: update.target_guid,
                        percent: update.health_percent,
                    });
                }
            }
            GameEventType::VICTIM_NOTIFICATION => {
                let message = ac_proto::chat::parse_string_event(payload)?;
                self.notices.push(GameplayNotice::CombatLine(
                    message.clone(),
                    CombatLineKind::Error,
                ));
                self.death.on_local_death(&message);
                self.events.push(GameplayEvent::LocalPlayerDied(message));
            }
            GameEventType::KILLER_NOTIFICATION => {
                let message = ac_proto::chat::parse_string_event(payload)?;
                self.notices
                    .push(GameplayNotice::CombatLine(message, CombatLineKind::Info));
            }
            GameEventType::DEFENDER_NOTIFICATION => {
                let p = wire_combat::DefenderNotification::parse(payload)?;
                self.notices
                    .push(GameplayNotice::Combat(CombatNotice::DamageTaken {
                        attacker_name: p.attacker_name,
                        damage_type: p.damage_type,
                        damage: p.damage,
                        damage_percent: p.health_percent,
                        hit_quadrant: p.hit_quadrant,
                        critical: p.critical != 0,
                        attack_conditions: p.attack_conditions,
                    }));
            }
            GameEventType::ATTACKER_NOTIFICATION => {
                let p = wire_combat::AttackerNotification::parse(payload)?;
                self.notices
                    .push(GameplayNotice::Combat(CombatNotice::DamageDealt {
                        defender_name: p.defender_name,
                        damage_type: p.damage_type,
                        damage: p.damage,
                        damage_percent: p.health_percent,
                        critical: p.critical != 0,
                        attack_conditions: p.attack_conditions,
                    }));
            }
            GameEventType::EVASION_ATTACKER_NOTIFICATION => {
                let defender_name = ac_proto::chat::parse_string_event(payload)?;
                self.notices
                    .push(GameplayNotice::Combat(CombatNotice::MissedOutgoing {
                        defender_name,
                    }));
            }
            GameEventType::EVASION_DEFENDER_NOTIFICATION => {
                let attacker_name = ac_proto::chat::parse_string_event(payload)?;
                self.notices
                    .push(GameplayNotice::Combat(CombatNotice::EvadedIncoming {
                        attacker_name,
                    }));
            }
            GameEventType::ATTACK_DONE => {
                let error = wire_combat::parse_attack_done(payload)?;
                // The sequence the server numbered the swing with is not
                // on the wire this client reads; the error is.
                let now = ctx.now();
                let mut ops = ops!(self, ctx);
                self.attack.on_attack_done(0, error, &mut ops, now);
                self.finish(ctx);
            }
            GameEventType::COMBAT_COMMENCE_ATTACK => {
                wire_combat::parse_combat_commence_attack(payload)?;
                self.attack.on_attack_commenced();
            }
            GameEventType::MAGIC_UPDATE_SPELL
            | GameEventType::MAGIC_REMOVE_SPELL
            | GameEventType::MAGIC_UPDATE_ENCHANTMENT
            | GameEventType::MAGIC_UPDATE_MULTIPLE_ENCHANTMENTS
            | GameEventType::MAGIC_REMOVE_ENCHANTMENT
            | GameEventType::MAGIC_REMOVE_MULTIPLE_ENCHANTMENTS
            | GameEventType::MAGIC_DISPEL_ENCHANTMENT
            | GameEventType::MAGIC_DISPEL_MULTIPLE_ENCHANTMENTS
            | GameEventType::MAGIC_PURGE_ENCHANTMENTS
            | GameEventType::MAGIC_PURGE_BAD_ENCHANTMENTS => {
                for text in magic::apply_event(ctx.world, event, ctx.now())? {
                    self.notices
                        .push(GameplayNotice::Text(text, magic::EXPIRY_TEXT_TYPE));
                }
            }
            GameEventType::BOOK_DATA_RESPONSE => {
                let book = OpenBook::parse(payload)?;
                let turn = self.book.apply_open_book(&book, player);
                self.send_page_request(book.book_guid, turn, ctx);
                self.events.push(GameplayEvent::Book(self.book.view()));
            }
            GameEventType::BOOK_PAGE_DATA_RESPONSE => {
                let page = PageDataResponse::parse(payload)?;
                if self.book.apply_page_data(&page) {
                    self.events.push(GameplayEvent::Book(self.book.view()));
                }
            }
            GameEventType::BOOK_ADD_PAGE_RESPONSE => {
                let response = PageResponse::parse(payload)?;
                let author = ctx.world.name(player).unwrap_or_default().to_owned();
                if !self
                    .book
                    .apply_add_page_response(&response, player, &author)
                {
                    // Retail reads the whole book again when it cannot fold
                    // the answer in.
                    ctx.send(|seq| build_book_data(seq, response.book_guid));
                    self.book.mark_request_pending();
                }
                self.events.push(GameplayEvent::Book(self.book.view()));
            }
            GameEventType::GET_INSCRIPTION_RESPONSE => {
                let inscription = Inscription::parse(payload)?;
                if self.book.apply_inscription(&inscription) {
                    self.events.push(GameplayEvent::Book(self.book.view()));
                }
            }
            GameEventType::JOIN_GAME_RESPONSE => {
                let response = wire_chess::JoinGameResponse::parse(payload)?;
                self.chess.apply_join_response(&response);
                self.chess_said();
            }
            GameEventType::START_GAME => {
                let start = wire_chess::StartGame::parse(payload)?;
                self.chess.apply_start_game(&start);
                self.chess_said();
            }
            GameEventType::MOVE_RESPONSE => {
                let response = wire_chess::MoveResponse::parse(payload)?;
                self.chess.apply_move_response(&response);
                self.chess_said();
            }
            GameEventType::OPPONENT_TURN => {
                let turn = wire_chess::OpponentTurn::parse(payload)?;
                self.chess.apply_opponent_turn(&turn);
                self.chess_said();
            }
            GameEventType::OPPONENT_STALEMATE => {
                let stalemate = wire_chess::OpponentStalemate::parse(payload)?;
                self.chess.apply_opponent_stalemate(&stalemate);
                self.chess_said();
            }
            GameEventType::GAME_OVER => {
                let over = wire_chess::GameOver::parse(payload)?;
                self.chess.apply_game_over(&over);
                self.chess_said();
            }
            GameEventType::FELLOWSHIP_FULL_UPDATE => {
                let update = wire_fellowship::FellowshipFullUpdate::parse(payload)?;
                self.fellowship.apply_full_update(&update);
                self.push_fellowship(ctx);
            }
            GameEventType::FELLOWSHIP_UPDATE_FELLOW => {
                let update = wire_fellowship::FellowshipUpdateFellow::parse(payload)?;
                self.fellowship.apply_update_fellow(
                    &update,
                    ctx.clocks.wall_unix_ms.div_euclid(1000),
                    ctx.now(),
                );
                self.push_fellowship(ctx);
            }
            // Each departure is said in the chat before the roster changes,
            // worded from the roster as it was.
            GameEventType::FELLOWSHIP_QUIT => {
                let guid = wire_fellowship::parse_member_event(payload)?;
                self.departure_notice(Departure::Quit(guid), player);
                self.fellowship.apply_quit(guid, player);
                self.push_fellowship(ctx);
            }
            GameEventType::FELLOWSHIP_DISMISS => {
                let guid = wire_fellowship::parse_member_event(payload)?;
                self.departure_notice(Departure::Dismiss(guid), player);
                self.fellowship.apply_dismiss(guid, player);
                self.push_fellowship(ctx);
            }
            GameEventType::FELLOWSHIP_DISBAND => {
                self.departure_notice(Departure::Disband, player);
                self.fellowship.apply_disband();
                self.push_fellowship(ctx);
            }
            GameEventType::ALLEGIANCE_UPDATE => {
                let update = wire_allegiance::AllegianceUpdate::parse(payload)?;
                self.allegiance.apply_update(&update);
                self.events
                    .push(GameplayEvent::Allegiance(self.allegiance.view(player)));
            }
            GameEventType::ALLEGIANCE_UPDATE_DONE | GameEventType::ALLEGIANCE_UPDATE_ABORTED => {
                wire_allegiance::parse_update_result(payload)?;
                self.allegiance.apply_update_done();
            }
            GameEventType::ALLEGIANCE_LOGIN_NOTIFICATION => {
                wire_allegiance::AllegianceLoginNotification::parse(payload)?;
                self.allegiance.apply_login_notification();
            }
            GameEventType::HOUSE_DATA => {
                let data = wire_house::HouseData::parse(payload)?;
                let clock = self.house_clock(ctx);
                self.house.apply_house_data(&data, clock);
                self.events.push(GameplayEvent::House(self.house.view()));
            }
            GameEventType::HOUSE_STATUS => {
                wire_house::parse_house_word(payload)?;
                let clock = self.house_clock(ctx);
                self.house.apply_house_status(clock);
                self.events.push(GameplayEvent::House(self.house.view()));
            }
            GameEventType::UPDATE_RENT_TIME => {
                let rent_time = wire_house::parse_house_word(payload)?;
                let clock = self.house_clock(ctx);
                self.house.apply_rent_time(rent_time, clock);
            }
            GameEventType::UPDATE_HAR => {
                let access = wire_house::HouseAccess::parse(payload)?;
                self.text(house::guest_list_text(&access));
            }
            GameEventType::UPDATE_RENT_PAYMENT => {
                let rent = wire_house::parse_rent_payment(payload)?;
                let clock = self.house_clock(ctx);
                self.house.apply_rent_payment(&rent, clock);
            }
            GameEventType::CHARACTER_TITLE => {
                let table = ac_proto::social::CharacterTitleTable::parse(payload)?;
                self.titles
                    .replace_table(table.display_title_id, &table.title_ids);
                self.events.push(GameplayEvent::Titles(self.titles.view()));
            }
            GameEventType::UPDATE_TITLE => {
                let update = ac_proto::social::UpdateTitle::parse(payload)?;
                let change = self
                    .titles
                    .apply_update_title(update.title_id, update.set_as_display);
                if change != TitleChange::default() {
                    self.events.push(GameplayEvent::Titles(self.titles.view()));
                }
            }
            _ => {}
        }
        Ok(())
    }

    fn house_clock(&self, ctx: &GameplayContext<'_>) -> house::HouseClock {
        let player = ctx.player();
        house::HouseClock {
            purchase_timestamp: ctx.world.item(player).map_or(0, |p| {
                p.properties
                    .get_int(PropertyInt::HOUSE_PURCHASE_TIMESTAMP.0, 0)
            }),
            now_unix_s: ctx.clocks.wall_unix_ms.div_euclid(1000),
            utc_offset_seconds: self.zone.utc_offset_seconds(ctx.clocks.wall_unix_ms),
        }
    }

    fn push_fellowship(&mut self, ctx: &GameplayContext<'_>) {
        self.events
            .push(GameplayEvent::Fellowship(self.fellowship.view(ctx.now())));
    }

    fn on_creature_died(&mut self, guid: u32, ctx: &mut GameplayContext<'_>) {
        self.events.push(GameplayEvent::CreatureDied(guid));
        // The death the server announces lets the selection go, whether or
        // not anything draws the creature.
        if self.selection.selected() == Some(guid)
            && let Some(t) = self
                .selection
                .clear(SelectionSource::System, SelectionReason::CombatTargetDied)
        {
            self.after_selection(vec![t], ctx);
        }
    }

    fn send_page_request(&mut self, book: u32, turn: BookPageTurn, ctx: &mut GameplayContext<'_>) {
        match turn.action {
            BookPageAction::RequestPageText => {
                ctx.send(|seq| build_book_page_data(seq, book, turn.page));
            }
            BookPageAction::AddPage => {
                ctx.send(|seq| build_book_add_page(seq, book));
            }
            _ => {}
        }
    }

    // -- Once a pass --------------------------------------------------------

    /// Once a pass: the power bar and a parked mode change.
    pub fn tick(&mut self, ctx: &mut GameplayContext<'_>) {
        let now = ctx.now();
        let current = self.combat.current_mode();
        let mut readiness = mode_readiness(ctx.body.as_deref(), ctx.world, ctx.teleporting);
        let mut ops = ops!(self, ctx);
        if let Some(mode) = self
            .mode
            .apply_pending_mode(current, &mut ops, readiness.as_dyn())
        {
            let event = self.combat.set_combat_mode(mode);
            self.finish(ctx);
            self.combat_event(event, ctx);
        }
        let mut ops = ops!(self, ctx);
        self.attack.tick(&mut ops, now);
        self.finish(ctx);
    }

    /// The character left the world: every owner forgets the session.
    pub fn left_world(&mut self, world: &mut World) {
        let _ = world;
        self.cast.reset();
        self.attack.reset_session();
        // The reset is a selection change like any other, even with
        // nothing selected: whatever follows the selection (an open
        // examination window) lets the session's go.
        let reset = self.selection.reset();
        self.events.push(GameplayEvent::Selection {
            selected: reset.selected,
            name: None,
        });
        self.combat.clear();
        self.mode.reset();
        self.target = CombatTargetState::default();
        self.deaths.clear();
        self.death = DeathState::default();
        self.health.reset();
        self.book.reset_session();
        self.chess.reset_session();
        self.fellowship.reset_session();
        self.allegiance.reset_session();
        self.house.reset_session();
        self.titles.reset_session();
        self.busy = 0;
        self.explicit_mode_request = false;
        self.mode_changes.clear();
    }

    // -- Commands -------------------------------------------------------------

    /// Run one gameplay command.
    #[allow(clippy::too_many_lines)]
    pub fn execute(&mut self, command: GameplayCommand, ctx: &mut GameplayContext<'_>) {
        let player = ctx.player();
        match command {
            GameplayCommand::Select(reference) => {
                if let Some(guid) = self.resolve(&reference, ctx) {
                    self.select(guid, SelectionSource::Host, ctx);
                }
            }
            // The session answers the Escape key: it reads the movement,
            // the use mode and the window's request too.
            GameplayCommand::Escape => {}
            GameplayCommand::ClearSelection => {
                // A deselect the player asks for stands as given.
                self.target.notify_target_willingly_lost();
                if let Some(t) = self
                    .selection
                    .clear(SelectionSource::Keyboard, SelectionReason::Cleared)
                {
                    self.after_selection(vec![t], ctx);
                }
            }
            GameplayCommand::ClearRevealedSelection(guid) => {
                if self.selection.selected() == Some(guid)
                    && let Some(t) = self
                        .selection
                        .clear(SelectionSource::System, SelectionReason::Cleared)
                {
                    self.after_selection(vec![t], ctx);
                }
            }
            GameplayCommand::SelectPrevious => {
                if let Some(t) = self.selection.select_previous(SelectionSource::Keyboard) {
                    self.after_selection(vec![t], ctx);
                }
            }
            GameplayCommand::SelectNearestMonster => {
                let mut ops = ops!(self, ctx);
                ops.select_closest();
                self.finish(ctx);
            }
            GameplayCommand::CycleSelection { kind, direction } => {
                self.cycle_selection(kind, direction, ctx);
            }
            GameplayCommand::UseUnopenedCorpse(direction) => {
                let kind = selection_cycle::SelectionCycleKind::UnopenedCorpse;
                self.cycle_selection(kind, direction, ctx);
            }
            GameplayCommand::SelectSelf => match ctx.world.player_guid() {
                Some(player) => self.select(player, SelectionSource::Keyboard, ctx),
                None => self.refuse("The character is not in the world."),
            },
            GameplayCommand::SelectLastAttacker => {
                match selection_cycle::find_last_attacker(ctx.world) {
                    Some(attacker) => self.select(attacker, SelectionSource::Keyboard, ctx),
                    None => self.refuse("Nothing is attacking you nearby."),
                }
            }
            GameplayCommand::CycleFellow { previous } => {
                let mut fellows: Vec<u32> = Vec::new();
                for guid in self.fellowship.member_guids() {
                    if guid != 0 && !fellows.contains(&guid) {
                        fellows.push(guid);
                    }
                }
                if fellows.is_empty() {
                    self.refuse("You are not in a fellowship.");
                } else {
                    let current = self
                        .selection
                        .selected()
                        .and_then(|s| fellows.iter().position(|&f| f == s));
                    let next = match (previous, current) {
                        (true, Some(i)) if i > 0 => i - 1,
                        (true, _) => fellows.len() - 1,
                        (false, Some(i)) if i + 1 < fellows.len() => i + 1,
                        (false, _) => 0,
                    };
                    self.select(fellows[next], SelectionSource::Keyboard, ctx);
                }
            }
            GameplayCommand::Cast { spell, target } => self.cast(&spell, target, ctx),
            GameplayCommand::CastWithoutSelecting { spell, target } => {
                self.cast_without_selecting(spell, target, ctx);
            }
            GameplayCommand::ListSpells(filter) => {
                let view = magic::spellbook_view(
                    ctx.world,
                    &self.tables,
                    &self.account,
                    filter.as_deref(),
                );
                self.events.push(GameplayEvent::Spellbook(view));
            }
            GameplayCommand::ShowEnchantments => {
                self.events
                    .push(GameplayEvent::Enchantments(magic::enchantments_view(
                        ctx.world,
                        ctx.now(),
                    )));
            }
            GameplayCommand::ShowCharacter => {
                self.events
                    .push(GameplayEvent::Character(Box::new(magic::character_sheet(
                        ctx.world,
                    ))));
            }
            GameplayCommand::ForgetSpell(spell) => {
                if let Some(id) = self.known_spell(&spell, ctx) {
                    ctx.send(|seq| wire_magic::build_remove_spell(seq, id));
                }
            }
            GameplayCommand::AddFavorite {
                tab,
                position,
                spell,
            } => {
                let Some(id) = self.known_spell(&spell, ctx) else {
                    return;
                };
                if tab >= ac_core::spells::FAVORITE_BAR_COUNT || !ctx.active {
                    self.refuse("That favorite bar does not exist.");
                    return;
                }
                ctx.world
                    .update_spellbook(|b| b.set_favorite(tab, position, id));
                let (tab, position) = (to_i32(tab), to_i32(position));
                ctx.send(|seq| wire_magic::build_add_spell_favorite(seq, id, position, tab));
            }
            GameplayCommand::RemoveFavorite { tab, spell } => {
                let Some(id) = self.known_spell(&spell, ctx) else {
                    return;
                };
                if tab >= ac_core::spells::FAVORITE_BAR_COUNT || !ctx.active {
                    self.refuse("That favorite bar does not exist.");
                    return;
                }
                ctx.world.update_spellbook(|b| b.remove_favorite(tab, id));
                let tab = to_i32(tab);
                ctx.send(|seq| wire_magic::build_remove_spell_favorite(seq, id, tab));
            }
            GameplayCommand::SetSpellbookFilter(filters) => {
                // An unchanged filter is not sent again.
                let unchanged = ctx
                    .world
                    .spellbook()
                    .is_some_and(|b| b.spellbook_filters() == filters);
                if ctx.active && !unchanged {
                    ctx.world
                        .update_spellbook(|b| b.set_spellbook_filters(filters));
                    ctx.send(|seq| wire_magic::build_spellbook_filter(seq, filters));
                }
            }
            GameplayCommand::SetDesiredComponent { component, amount } => {
                if component == 0 || amount > MAX_DESIRED_COMPONENT_AMOUNT {
                    self.refuse("That is not a component amount the client can ask for.");
                    return;
                }
                if ctx.active {
                    ctx.world
                        .update_spellbook(|b| b.set_desired_component(component, amount));
                    ctx.send(|seq| {
                        wire_magic::build_set_desired_component_level(seq, component, amount)
                    });
                }
            }
            GameplayCommand::ClearDesiredComponents => {
                if ctx.active {
                    ctx.world
                        .update_spellbook(ac_core::spells::Spellbook::clear_desired_components);
                    ctx.send(wire_magic::build_clear_desired_components);
                }
            }
            GameplayCommand::ToggleCombat => {
                let current = self.combat.current_mode();
                let mut readiness = mode_readiness(ctx.body.as_deref(), ctx.world, ctx.teleporting);
                let mut ops = ops!(self, ctx);
                let result = self.mode.toggle(current, &mut ops, readiness.as_dyn());
                self.finish(ctx);
                self.mode_result(result, ctx);
            }
            GameplayCommand::SetCombatMode(mode) => {
                let current = self.combat.current_mode();
                let mut readiness = mode_readiness(ctx.body.as_deref(), ctx.world, ctx.teleporting);
                let mut ops = ops!(self, ctx);
                let result = self
                    .mode
                    .request(mode, current, &mut ops, readiness.as_dyn());
                self.finish(ctx);
                self.mode_result(result, ctx);
            }
            GameplayCommand::Attack { height, target } => {
                if let Some(reference) = target {
                    let Some(guid) = self.resolve(&reference, ctx) else {
                        return;
                    };
                    self.select(guid, SelectionSource::Host, ctx);
                }
                if !ac_core::combat::supports_targeted_attack(self.combat.current_mode()) {
                    self.refuse("Enter melee or missile combat first.");
                    return;
                }
                let now = ctx.now();
                let mut ops = ops!(self, ctx);
                self.attack.press_attack(height, &mut ops, now);
                let started = self.attack.attack_request_in_progress();
                self.attack.release_attack(&mut ops, now);
                self.finish(ctx);
                if !started {
                    self.refuse("There is nothing to attack.");
                }
            }
            GameplayCommand::AttackInput(command, activation) => {
                let now = ctx.now();
                let mut ops = ops!(self, ctx);
                self.attack
                    .handle_command(command, activation, &mut ops, now);
                self.finish(ctx);
            }
            GameplayCommand::SetAttackPower(power) => self.attack.set_desired_power(power),
            GameplayCommand::ShowCombat => {
                let view = self.combat_view(ctx);
                self.events.push(GameplayEvent::Combat(view));
            }
            GameplayCommand::Book(command) => self.book_command(command, ctx),
            GameplayCommand::Chess(command) => self.chess_command(command, ctx),
            GameplayCommand::Fellowship(command) => self.fellowship_command(command, ctx),
            GameplayCommand::Allegiance(command) => self.allegiance_command(command, ctx),
            GameplayCommand::QueryHouse => {
                ctx.send(ac_proto::build_house_query);
            }
            GameplayCommand::ShowVitae => {
                self.events
                    .push(GameplayEvent::Vitae(death::vitae_view(ctx.world)));
            }
            GameplayCommand::SetTitle(title_id) => {
                // The server answers with a title notice that moves the
                // displayed title; nothing changes here until it does.
                ctx.send(|seq| ac_proto::social::build_set_display_title(seq, title_id));
            }
            GameplayCommand::ShowTitles => {
                self.events.push(GameplayEvent::Titles(self.titles.view()));
            }
            GameplayCommand::ShowHouse => {
                if !self.house.has_received_notice() {
                    let clock = self.house_clock(ctx);
                    // Nothing known yet: the panel reads as houseless until
                    // the server answers.
                    let mut fresh = HouseState::default();
                    fresh.apply_house_status(clock);
                    let mut view = fresh.view();
                    view.has_received_notice = false;
                    self.events.push(GameplayEvent::House(view));
                } else {
                    self.events.push(GameplayEvent::House(self.house.view()));
                }
            }
        }
        let _ = player;
    }

    fn mode_result(&mut self, result: CombatModeRequestResult, ctx: &mut GameplayContext<'_>) {
        self.last_mode_request = Some(result.clone());
        match result.status {
            CombatModeRequestStatus::Sent => {
                let event = self.combat.set_combat_mode(result.mode);
                self.combat_event(event, ctx);
            }
            CombatModeRequestStatus::Rejected => {
                if let Some(notice) = result.notice {
                    self.text(notice);
                }
            }
            CombatModeRequestStatus::Inactive => {
                self.refuse("Not in the world.");
            }
            CombatModeRequestStatus::Deferred => {}
        }
    }

    fn combat_event(&mut self, event: Option<CombatEvent>, ctx: &mut GameplayContext<'_>) {
        let _ = ctx;
        if let Some(CombatEvent::CombatModeChanged(mode)) = event {
            self.attack.on_combat_mode_changed(mode);
            self.mode_changes.push(mode);
            self.events.push(GameplayEvent::CombatMode(mode));
        }
    }

    fn combat_view(&self, ctx: &GameplayContext<'_>) -> CombatView {
        self.combat_view_at(ctx.world, ctx.options, ctx.body.as_deref(), ctx.now())
    }

    fn cast(&mut self, spell: &str, target: Option<ObjectRef>, ctx: &mut GameplayContext<'_>) {
        let Some(id) = self.known_spell(spell, ctx) else {
            return;
        };
        if let Some(reference) = target {
            let Some(guid) = self.resolve(&reference, ctx) else {
                return;
            };
            self.select(guid, SelectionSource::Host, ctx);
        }
        let selected = self.selection.selected();
        let mut ops = ops!(self, ctx);
        let world: &World = ops.world;
        match world.spellbook() {
            Some(book) => {
                self.cast.cast(book, id, selected, &mut ops);
            }
            None => ops.display_message_now("You do not know that spell."),
        }
        self.finish(ctx);
    }

    /// The cast of [`GameplayCommand::CastWithoutSelecting`]: the same
    /// request as [`Self::cast`], with the target given instead of read
    /// from the selection.
    fn cast_without_selecting(&mut self, spell: u32, target: u32, ctx: &mut GameplayContext<'_>) {
        let target = (target != 0).then_some(target);
        let mut ops = ops!(self, ctx);
        let world: &World = ops.world;
        match world.spellbook() {
            Some(book) => {
                self.cast.cast(book, spell, target, &mut ops);
            }
            None => ops.display_message_now("You do not know that spell."),
        }
        self.finish(ctx);
    }

    fn known_spell(&mut self, text: &str, ctx: &GameplayContext<'_>) -> Option<u32> {
        let id = magic::resolve_spell(ctx.world, text);
        if id.is_none() {
            self.refuse(format!("No known spell is called \"{}\".", text.trim()));
        }
        id
    }

    /// The local player used a game board in the world (the item owner's
    /// primary use of someone else's board): its game begins here, a join
    /// going out when the player is not already at a board, so every host
    /// joins the same way.
    pub fn begin_game(&mut self, board_guid: u32, ctx: &mut GameplayContext<'_>) {
        if self.chess.begin(board_guid) {
            ctx.send(|seq| wire_chess::build_join(seq, board_guid));
        }
        self.chess_said();
    }

    /// The chess game's lines into the notices.
    fn chess_said(&mut self) {
        for (text, class) in self.chess.drain_lines() {
            self.notices.push(GameplayNotice::Text(text, class));
        }
    }

    fn chess_command(&mut self, command: ChessCommand, ctx: &mut GameplayContext<'_>) {
        match command {
            ChessCommand::Move { from, to } => {
                if self.chess.try_move(from, to).is_accepted() {
                    ctx.send(|seq| wire_chess::build_move(seq, from.x, from.y, to.x, to.y));
                }
            }
            ChessCommand::Pass => {
                if self.chess.try_pass() {
                    ctx.send(wire_chess::build_move_pass);
                }
            }
            ChessCommand::ToggleStalemate => {
                if let Some(offer) = self.chess.try_toggle_stalemate() {
                    ctx.send(|seq| wire_chess::build_stalemate(seq, offer));
                }
            }
            ChessCommand::Resign => {
                ctx.send(wire_chess::build_quit);
                self.chess.resign();
            }
        }
        self.chess_said();
    }

    fn book_command(&mut self, command: BookCommand, ctx: &mut GameplayContext<'_>) {
        let player = ctx.player();
        let book = self.book.book_guid();
        if book == 0 && command != BookCommand::Show {
            self.refuse("No book is open.");
            return;
        }
        match command {
            BookCommand::Turn(page) => {
                let turn = self.book.turn_page(page, None, player);
                if turn.action == BookPageAction::None {
                    self.refuse("That page cannot be turned to now.");
                    return;
                }
                self.send_page_request(book, turn, ctx);
            }
            BookCommand::Write(text) => {
                let (flush, page) = self.book.flush_current_page(&text, player);
                match flush {
                    BookFlushAction::ModifyPage => {
                        if ctx
                            .outbox
                            .try_send_action(|seq| build_book_modify_page(seq, book, page, &text))
                            .is_err()
                        {
                            self.refuse("That text cannot be sent.");
                        }
                    }
                    BookFlushAction::DeletePage => {
                        ctx.send(|seq| build_book_delete_page(seq, book, page));
                    }
                    BookFlushAction::None => {
                        self.refuse("You cannot write on this page.");
                        return;
                    }
                }
            }
            BookCommand::Close => self.book.close_book(),
            BookCommand::TurnWriting { page, typed } => {
                // A refused turn (a request in flight, the end of the book,
                // a blank last page) does nothing, as the reader's buttons
                // do.
                let turn = self.book.turn_page(page, typed.as_deref(), player);
                self.send_flush(book, turn.flush, turn.flush_page, typed.as_deref(), ctx);
                self.send_page_request(book, turn, ctx);
            }
            BookCommand::CloseWriting(typed) => {
                if let Some(text) = typed.as_deref() {
                    let (flush, page) = self.book.flush_current_page(text, player);
                    self.send_flush(book, flush, page, Some(text), ctx);
                }
                self.book.close_book();
            }
            BookCommand::Show => {}
        }
        self.events.push(GameplayEvent::Book(self.book.view()));
    }

    /// Send what closing a page asked for: its text saved, or the page
    /// deleted.
    fn send_flush(
        &mut self,
        book: u32,
        flush: BookFlushAction,
        page: i32,
        text: Option<&str>,
        ctx: &mut GameplayContext<'_>,
    ) {
        match flush {
            BookFlushAction::ModifyPage => {
                let text = text.unwrap_or_default();
                if ctx
                    .outbox
                    .try_send_action(|seq| build_book_modify_page(seq, book, page, text))
                    .is_err()
                {
                    self.refuse("That text cannot be sent.");
                }
            }
            BookFlushAction::DeletePage => {
                ctx.send(|seq| build_book_delete_page(seq, book, page));
            }
            BookFlushAction::None => {}
        }
    }

    fn fellowship_command(&mut self, command: FellowshipCommand, ctx: &mut GameplayContext<'_>) {
        let player = ctx.player();
        match command {
            FellowshipCommand::Create { name, share_xp } => {
                if name.trim().is_empty() {
                    self.refuse("A fellowship needs a name.");
                } else if ctx.active
                    && ctx
                        .outbox
                        .try_send_action(|seq| wire_fellowship::build_create(seq, &name, share_xp))
                        .is_err()
                {
                    self.refuse("That name cannot be sent.");
                }
            }
            FellowshipCommand::Recruit(who) => {
                if let Some(guid) = self.resolve(&who, ctx) {
                    ctx.send(|seq| wire_fellowship::build_recruit(seq, guid));
                }
            }
            FellowshipCommand::Dismiss(who) => {
                if let Some(guid) = self.resolve(&who, ctx) {
                    ctx.send(|seq| wire_fellowship::build_dismiss(seq, guid));
                }
            }
            FellowshipCommand::Quit { disband } => {
                if let Some(leader) = self
                    .fellowship
                    .requires_leader_handoff_before_quit(player, disband)
                {
                    ctx.send(|seq| wire_fellowship::build_assign_new_leader(seq, leader));
                }
                ctx.send(|seq| wire_fellowship::build_quit(seq, disband));
            }
            FellowshipCommand::AssignLeader(who) => {
                if let Some(guid) = self.resolve(&who, ctx) {
                    ctx.send(|seq| wire_fellowship::build_assign_new_leader(seq, guid));
                }
            }
            FellowshipCommand::SetOpen(open) => {
                ctx.send(|seq| wire_fellowship::build_change_openness(seq, open));
            }
            FellowshipCommand::RequestVitals(on) => {
                if let Some(subscribe) = self.fellowship.set_vitals_requested(on) {
                    ctx.send(|seq| wire_fellowship::build_update_request(seq, subscribe));
                }
            }
            FellowshipCommand::SetPanelOpen(open) => {
                if let Some(subscribe) = self.fellowship.set_panel_visible(open) {
                    ctx.send(|seq| wire_fellowship::build_update_request(seq, subscribe));
                }
            }
            FellowshipCommand::Show => self.push_fellowship(ctx),
        }
    }

    fn allegiance_command(&mut self, command: AllegianceCommand, ctx: &mut GameplayContext<'_>) {
        let player = ctx.player();
        match command {
            AllegianceCommand::Swear(who) => {
                if let Some(guid) = self.resolve(&who, ctx) {
                    ctx.send(|seq| wire_allegiance::build_swear(seq, guid));
                }
            }
            AllegianceCommand::Break(who) => {
                if let Some(guid) = self.resolve(&who, ctx) {
                    ctx.send(|seq| wire_allegiance::build_break(seq, guid));
                }
            }
            AllegianceCommand::Kick(who) => {
                if let Some(guid) = self.resolve(&who, ctx) {
                    ctx.send(|seq| wire_allegiance::build_kick(seq, guid));
                }
            }
            AllegianceCommand::Info(name) => {
                if ctx.active
                    && ctx
                        .outbox
                        .try_send_action(|seq| wire_allegiance::build_info_request(seq, &name))
                        .is_err()
                {
                    self.refuse("That name cannot be sent.");
                }
            }
            AllegianceCommand::Subscribe(on) => {
                ctx.send(|seq| wire_allegiance::build_update_request(seq, on));
            }
            AllegianceCommand::Show => {
                self.events
                    .push(GameplayEvent::Allegiance(self.allegiance.view(player)));
            }
        }
    }

    // -- Selection --------------------------------------------------------------

    /// The selected object as the world shows it now, to compare with after
    /// a change to the world ([`Gameplay::follow_selected_object`]).
    #[must_use]
    pub fn selected_presence(&self, world: &World) -> Option<SelectedPresence> {
        let guid = self.selection.selected()?;
        let object = world.object(guid)?;
        Some(SelectedPresence {
            guid,
            placed: object.position().is_some(),
            hidden: object.physics().state & PhysicsState::HIDDEN.bits() != 0,
        })
    }

    /// The selection lets go of an object that left the world (deleted,
    /// picked up, taken in hand: removed) or that the world stopped showing
    /// (hidden: cleared). A new incarnation of the same object keeps it, and
    /// a selection that never stood in the world (an item in a pack) is
    /// not touched.
    pub fn follow_selected_object(
        &mut self,
        before: Option<SelectedPresence>,
        ctx: &mut GameplayContext<'_>,
    ) {
        let Some(before) = before.filter(|b| b.placed) else {
            return;
        };
        if self.selection.selected() != Some(before.guid) {
            return;
        }
        let reason = match ctx.world.object(before.guid) {
            None => SelectionReason::SelectedObjectRemoved,
            Some(object) if object.position().is_none() => SelectionReason::SelectedObjectRemoved,
            Some(object)
                if !before.hidden && object.physics().state & PhysicsState::HIDDEN.bits() != 0 =>
            {
                SelectionReason::Cleared
            }
            Some(_) => return,
        };
        if let Some(t) = self.selection.clear(SelectionSource::System, reason) {
            self.after_selection(vec![t], ctx);
        }
    }

    /// Step the selection to the next thing of `kind` (the keyboard's
    /// selection keys); nothing found leaves the selection alone.
    fn cycle_selection(
        &mut self,
        kind: selection_cycle::SelectionCycleKind,
        direction: selection_cycle::SelectionCycleDirection,
        ctx: &mut GameplayContext<'_>,
    ) {
        use selection_cycle::{SelectionCycleDirection as D, SelectionCycleKind as K};
        let anchor = self.selection.selected().or(self.selection.previous());
        let world: &World = ctx.world;
        let (deaths, combat, fellowship) = (&self.deaths, &self.combat, &self.fellowship);
        let opened = ctx.opened_corpses;
        let is_fellow = |guid: u32| fellowship.member(guid).is_some();
        let is_attackable = |guid: u32| {
            world
                .item(guid)
                .is_some_and(|i| i.item_type.intersects(ItemType::CREATURE))
                && is_attackable_selection(world, guid, deaths, combat)
        };
        let corpse_opened = |guid: u32| opened.is_some_and(|o| o.has_corpse_been_opened(guid));
        let facts = selection_cycle::CycleFacts {
            combat_mode: self.combat_mode(),
            is_fellow: &is_fellow,
            is_attackable: &is_attackable,
            corpse_opened: &corpse_opened,
            // Only the closest-item key leaves out what the player carries.
            exclude_owned_by_player: kind == K::Item && direction == D::Closest,
        };
        let found = selection_cycle::find_target(world, kind, direction, anchor, &facts);
        match found {
            Some(guid) => self.select(guid, SelectionSource::Keyboard, ctx),
            None => self.refuse("Nothing of that kind is nearby."),
        }
    }

    fn select(&mut self, guid: u32, source: SelectionSource, ctx: &mut GameplayContext<'_>) {
        if let Some(t) = self
            .selection
            .select(guid, source, SelectionReason::Selected)
        {
            self.after_selection(vec![t], ctx);
        }
    }

    /// The selection changed: the health stream follows it, and an emptied
    /// selection in combat may pick the nearest monster.
    fn after_selection(
        &mut self,
        mut queue: Vec<SelectionTransition>,
        ctx: &mut GameplayContext<'_>,
    ) {
        self.follow_selection(&mut queue, true, ctx);
    }

    /// The selection changes `queue` holds, in order; `stream_health` false
    /// when the operations already moved the health stream for the first.
    fn follow_selection(
        &mut self,
        queue: &mut Vec<SelectionTransition>,
        mut stream_health: bool,
        ctx: &mut GameplayContext<'_>,
    ) {
        let auto_target = ctx.options.get(option_id::AUTO_TARGET);
        while !queue.is_empty() {
            let t = queue.remove(0);
            if stream_health {
                follow_health(
                    &mut self.health,
                    ctx.world,
                    ctx.outbox,
                    ctx.active,
                    t.selected,
                );
            }
            stream_health = true;
            self.events.push(GameplayEvent::Selection {
                selected: t.selected,
                name: t
                    .selected
                    .and_then(|s| ctx.world.name(s).map(str::to_owned)),
            });
            if self
                .target
                .on_selection_changed(&t, auto_target, self.combat.current_mode())
            {
                let closest = find_closest_monster(ctx.world, &self.deaths, &self.combat);
                let next = match closest {
                    Some(guid) => self.selection.select(
                        guid,
                        SelectionSource::Keyboard,
                        SelectionReason::Selected,
                    ),
                    None => self
                        .selection
                        .clear(SelectionSource::Keyboard, SelectionReason::Cleared),
                };
                queue.extend(next);
            }
        }
    }

    /// Run the rest of the selection changes the operations made (they moved
    /// the health stream themselves, as the change happened).
    fn finish(&mut self, ctx: &mut GameplayContext<'_>) {
        for t in std::mem::take(&mut self.transitions) {
            self.follow_selection(&mut vec![t], false, ctx);
        }
    }

    /// An object named by a command: by id, the player, or by name (the
    /// nearest object of that name; exact before prefix).
    fn resolve(&mut self, reference: &ObjectRef, ctx: &GameplayContext<'_>) -> Option<u32> {
        let player = ctx.player();
        let found = match reference {
            ObjectRef::Guid(guid) => Some(*guid),
            ObjectRef::Player => (player != 0).then_some(player),
            ObjectRef::Name(name) => find_by_name(ctx.world, name),
        };
        if found.is_none() {
            self.refuse(match reference {
                ObjectRef::Name(name) => format!("Nothing called \"{name}\" is known."),
                _ => "The player is not in the world.".to_owned(),
            });
        }
        found
    }
}

/// The health stream follows the selection: the open stream is closed and
/// the new selection asked about when it is a player, a pet or attackable.
fn follow_health(
    health: &mut HealthQuery,
    world: &World,
    outbox: &mut Outbox,
    active: bool,
    selected: Option<u32>,
) {
    let player = world.player_guid().unwrap_or(0);
    let should_query = selected.is_some_and(|s| {
        should_query_health(
            player,
            combat_object(world.item(player)).as_ref(),
            combat_object(world.item(s)).as_ref(),
        )
    });
    for target in health.on_selection_changed(selected, should_query) {
        if active {
            outbox.send_action(|seq| wire_combat::build_query_health(seq, target));
        }
    }
}

fn to_i32(value: usize) -> i32 {
    i32::try_from(value).unwrap_or(i32::MAX)
}

/// The facts the attack rules read about an object.
pub(crate) fn combat_object(item: Option<&ClientObject>) -> Option<CombatObject> {
    item.map(|i| CombatObject {
        object_id: i.object_id(),
        item_type: i.item_type,
        public_weenie_bitfield: i.public_weenie_bitfield.map(|f| f.bits()),
        pet_owner_id: i.pet_owner_id,
    })
}

/// The nearest object with this name (exact before prefix, without case).
fn find_by_name(world: &World, name: &str) -> Option<u32> {
    let wanted = name.to_lowercase();
    let here = world
        .player_guid()
        .and_then(|p| world.object(p))
        .and_then(|o| o.position().copied());
    let mut best: Option<(bool, u64, u32)> = None;
    for object in world.objects() {
        let Some(candidate) = world.name(object.guid()) else {
            continue;
        };
        let candidate = candidate.to_lowercase();
        let exact = candidate == wanted;
        if !exact && !candidate.starts_with(&wanted) {
            continue;
        }
        let metres = match (object.position(), here) {
            (Some(p), Some(me)) => distance(p, &me),
            _ => f32::MAX,
        };
        #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
        let rank = (!exact, (metres * 100.0) as u64, object.guid());
        if best.is_none_or(|b| rank < b) {
            best = Some(rank);
        }
    }
    best.map(|(_, _, guid)| guid)
}

/// Whether a creature is drawn and alive: not hidden, not known dead, and
/// with health left when health was reported.
pub(crate) fn can_be_seen_and_is_alive(
    world: &World,
    guid: u32,
    deaths: &CreatureDeathState,
    combat: &CombatState,
) -> bool {
    let Some(object) = world.object(guid) else {
        return false;
    };
    let hidden = PhysicsState::HIDDEN.bits() | PhysicsState::NO_DRAW.bits();
    if object.physics().state & hidden != 0 {
        return false;
    }
    if deaths.is_dead_incarnation(guid, object.key().incarnation) {
        return false;
    }
    !combat.has_health(guid) || combat.health_percent(guid) > 0.0
}

/// The selected object may be attacked: placed in the world, attackable by
/// the wider selected-object rule (a player with a shared player-killer
/// status counts), drawn and alive.
#[must_use]
pub fn is_attackable_selection(
    world: &World,
    guid: u32,
    deaths: &CreatureDeathState,
    combat: &CombatState,
) -> bool {
    let player = world.player_guid().unwrap_or(0);
    if guid == 0 || player == 0 || guid == player {
        return false;
    }
    if world.object(guid).and_then(|o| o.position()).is_none() {
        return false;
    }
    object_is_attackable(
        player,
        combat_object(world.item(player)).as_ref(),
        guid,
        combat_object(world.item(guid)).as_ref(),
    ) && can_be_seen_and_is_alive(world, guid, deaths, combat)
}

/// The nearest monster a player could pick: a hostile creature, placed,
/// drawn and alive.
#[must_use]
pub fn find_closest_monster(
    world: &World,
    deaths: &CreatureDeathState,
    combat: &CombatState,
) -> Option<u32> {
    let player = world.player_guid()?;
    let here = world.object(player)?.position().copied()?;
    let player_object = combat_object(world.item(player));
    let mut closest: Option<(f32, u32)> = None;
    for object in world.objects() {
        let guid = object.guid();
        let Some(position) = object.position() else {
            continue;
        };
        if guid == player
            || !is_hostile_monster(
                player,
                player_object.as_ref(),
                combat_object(world.item(guid)).as_ref(),
            )
            || !can_be_seen_and_is_alive(world, guid, deaths, combat)
        {
            continue;
        }
        let d = distance(position, &here);
        if closest.is_none_or(|(best, _)| d < best) {
            closest = Some((d, guid));
        }
    }
    closest.map(|(_, guid)| guid)
}

/// The player's stance and forward motion as the server last sent them
/// (full commands).
fn player_motion(world: &World) -> (u32, u32) {
    let motion = world
        .player_guid()
        .and_then(|p| world.object(p))
        .and_then(|o| o.movement());
    let Some(data) = motion else {
        return (motion_command::NON_COMBAT, motion_command::READY);
    };
    let style = ac_motion::command::reconstruct_full_command(data.current_style);
    let forward = match &data.movement {
        ac_proto::Movement::Interpreted { state, .. } => state.forward_command.map_or(
            motion_command::READY,
            ac_motion::command::reconstruct_full_command,
        ),
        _ => motion_command::READY,
    };
    (style, forward)
}

/// The readiness checks that only read.
struct ReadyOps<'a> {
    world: &'a World,
    options: &'a CharacterOptions,
    mode: CombatMode,
    body: Option<&'a Movement>,
}

impl combat::CombatAttackOperations for ReadyOps<'_> {
    fn can_start_attack(&mut self, _: bool) -> bool {
        false
    }
    fn prepare_attack_request(&mut self) {}
    fn send_attack(&mut self, _: AttackHeight, _: f32, _: bool) -> bool {
        false
    }
    fn send_cancel_attack(&mut self) {}
    fn is_dual_wield(&self) -> bool {
        is_dual_wield(self.body, self.world)
    }
    fn player_ready_for_attack(&self) -> bool {
        ready_for_attack(self.body, self.world, self.mode)
    }
    fn auto_repeat_attack(&self) -> bool {
        self.options.get(option_id::AUTO_REPEAT_ATTACK)
    }
}

/// The operations the state machines call, over the session's world and
/// outbox.
struct Ops<'a> {
    world: &'a World,
    outbox: &'a mut Outbox,
    options: &'a CharacterOptions,
    active: bool,
    tables: &'a GameTables,
    account: &'a str,
    selection: &'a mut Selection,
    /// Selection changes made here, run once the state machine returns.
    transitions: &'a mut Vec<SelectionTransition>,
    health: &'a mut HealthQuery,
    deaths: &'a CreatureDeathState,
    combat: &'a CombatState,
    notices: &'a mut Vec<GameplayNotice>,
    busy: &'a mut u32,
    explicit_mode_request: &'a mut bool,
    body: Option<&'a mut Movement>,
}

impl Ops<'_> {
    fn send(&mut self, build: impl FnOnce(u32) -> Vec<u8>) -> bool {
        if !self.active {
            return false;
        }
        self.outbox.send_action(build);
        true
    }

    /// A selection change made here: the health stream follows at once (as
    /// the change happens, before whatever this operation sends next); the
    /// rest runs when the state machine returns.
    fn record(&mut self, transition: Option<SelectionTransition>) {
        if let Some(t) = transition {
            follow_health(
                self.health,
                self.world,
                self.outbox,
                self.active,
                t.selected,
            );
            self.transitions.push(t);
        }
    }

    /// Select the nearest monster (or clear the selection when there is
    /// none), as the select-nearest key does.
    fn select_closest(&mut self) -> Option<u32> {
        let closest = find_closest_monster(self.world, self.deaths, self.combat);
        let t = match closest {
            Some(guid) => {
                self.selection
                    .select(guid, SelectionSource::Keyboard, SelectionReason::Selected)
            }
            None => self
                .selection
                .clear(SelectionSource::Keyboard, SelectionReason::Cleared),
        };
        self.record(t);
        closest
    }

    /// Which creature an attack goes to: the selection when it may be
    /// attacked, else (when allowed and the option is on) the nearest
    /// monster, which becomes the selection.
    fn resolve_attack_target(&mut self, allow_auto_target: bool) -> Option<u32> {
        if let Some(selected) = self.selection.selected()
            && is_attackable_selection(self.world, selected, self.deaths, self.combat)
        {
            return Some(selected);
        }
        if !allow_auto_target || !self.options.get(option_id::AUTO_TARGET) {
            return None;
        }
        self.select_closest()
    }

    /// Stop the body before a swing or a cast and tell the server.
    fn stop_body(&mut self) {
        if self.active
            && let Some(body) = self.body.as_deref_mut()
        {
            body.prepare_for_attack_request(self.world, self.outbox);
        }
    }

    fn display_message_now(&mut self, message: &str) {
        self.notices.push(GameplayNotice::Text(
            message.to_owned(),
            LogTextType::Default,
        ));
    }
}

impl combat::CombatModeOperations for Ops<'_> {
    fn is_in_world(&self) -> bool {
        self.active
    }

    fn ordered_equipment(&mut self) -> Vec<ClientObject> {
        let player = self.world.player_guid().unwrap_or(0);
        self.world
            .items()
            .get_equipped_by(player)
            .into_iter()
            .cloned()
            .collect()
    }

    fn notify_explicit_combat_mode_request(&mut self) {
        *self.explicit_mode_request = true;
    }

    fn send_change_combat_mode(&mut self, mode: CombatMode) {
        self.send(|seq| wire_combat::build_change_combat_mode(seq, mode.bits()));
    }
}

impl combat::CombatAttackOperations for Ops<'_> {
    fn can_start_attack(&mut self, allow_auto_target: bool) -> bool {
        self.active
            && ac_core::combat::supports_targeted_attack(self.combat.current_mode())
            && self.resolve_attack_target(allow_auto_target).is_some()
    }

    fn prepare_attack_request(&mut self) {
        self.stop_body();
    }

    fn send_attack(&mut self, height: AttackHeight, power: f32, allow_auto_target: bool) -> bool {
        let Some(target) = self.resolve_attack_target(allow_auto_target) else {
            return false;
        };
        let power = power.clamp(0.0, 1.0);
        let missile = self.combat.current_mode() == CombatMode::MISSILE;
        self.send(|seq| {
            if missile {
                wire_combat::build_targeted_missile_attack(seq, target, height.0, power)
            } else {
                wire_combat::build_targeted_melee_attack(seq, target, height.0, power)
            }
        })
    }

    fn send_cancel_attack(&mut self) {
        self.send(wire_combat::build_cancel_attack);
    }

    fn is_dual_wield(&self) -> bool {
        is_dual_wield(self.body.as_deref(), self.world)
    }

    fn player_ready_for_attack(&self) -> bool {
        ready_for_attack(self.body.as_deref(), self.world, self.combat.current_mode())
    }

    fn auto_repeat_attack(&self) -> bool {
        self.options.get(option_id::AUTO_REPEAT_ATTACK)
    }
}

/// The attack builders' readiness: the lenient ready-position rule over
/// the body's stance (not ready with no body). With no movement owner
/// (owners tested alone) it reads the stance the server last sent, melee
/// needing the character's combat maneuver table.
fn ready_for_attack(body: Option<&Movement>, world: &World, mode: CombatMode) -> bool {
    if let Some(body) = body {
        return body.body_motion().is_some_and(|m| {
            player_in_ready_position_for_attack(mode, m.current_style, m.forward_command)
        });
    }
    if !matches!(mode, CombatMode::MELEE | CombatMode::MISSILE) {
        return false;
    }
    let (style, forward) = player_motion(world);
    player_in_ready_position(mode, style, forward, has_combat_table(world), false, true)
}

/// The body holds the dual-wield stance (with no movement owner: the
/// stance the server last sent).
fn is_dual_wield(body: Option<&Movement>, world: &World) -> bool {
    let style = match body {
        Some(body) => body.body_motion().map(|m| m.current_style),
        None => Some(player_motion(world).0),
    };
    style == Some(ac_core::combat::DUAL_WIELD_COMBAT_STYLE)
}

/// What a combat mode change asks before it goes out, read from the
/// session's owners as the pass stands: the body's motion, the combat
/// table and the transit owner's teleport.
struct ModeReadiness {
    motion: Option<BodyMotion>,
    has_combat_table: bool,
    teleporting: bool,
}

impl combat::CombatModeReadiness for ModeReadiness {
    fn is_in_ready_position(&mut self, mode: CombatMode) -> bool {
        self.motion.is_some_and(|m| {
            player_in_ready_position(
                mode,
                m.current_style,
                m.forward_command,
                self.has_combat_table,
                m.motions_pending,
                false,
            )
        })
    }

    fn is_teleport_in_progress(&self) -> bool {
        self.teleporting
    }
}

/// The readiness, when a movement owner is bound.
fn mode_readiness(body: Option<&Movement>, world: &World, teleporting: bool) -> BoundReadiness {
    BoundReadiness(body.map(|body| ModeReadiness {
        motion: body.body_motion(),
        has_combat_table: has_combat_table(world),
        teleporting,
    }))
}

/// [`ModeReadiness`] when bound.
struct BoundReadiness(Option<ModeReadiness>);

impl BoundReadiness {
    fn as_dyn(&mut self) -> Option<&mut dyn combat::CombatModeReadiness> {
        self.0
            .as_mut()
            .map(|r| r as &mut dyn combat::CombatModeReadiness)
    }
}

fn has_combat_table(world: &World) -> bool {
    let player = world.player_guid().unwrap_or(0);
    world.item(player).is_some_and(|p| {
        p.properties
            .data_ids
            .get(&PropertyDataId::COMBAT_TABLE.0)
            .is_some_and(|&t| t != 0)
    })
}

impl magic::SpellCastOperations for Ops<'_> {
    fn local_player_id(&self) -> u32 {
        self.world.player_guid().unwrap_or(0)
    }

    fn can_send(&self) -> bool {
        self.active
    }

    fn has_required_components(&self, spell_id: u32) -> bool {
        self.tables.components().has_required_components(
            &magic::ComponentWorld(self.world),
            self.account,
            spell_id,
        )
    }

    fn is_target_compatible(
        &mut self,
        target_id: u32,
        spell: &ac_core::spells::SpellMetadata,
        show_message: bool,
    ) -> bool {
        match magic::target_check(self.world, target_id, spell) {
            None => true,
            Some(message) => {
                if show_message && !message.is_empty() {
                    self.display_message_now(&message);
                }
                false
            }
        }
    }

    fn stop_completely(&mut self) {
        self.stop_body();
    }

    fn send_untargeted(&mut self, spell_id: u32) {
        self.send(|seq| wire_magic::build_cast_untargeted_spell(seq, spell_id));
    }

    fn send_targeted(&mut self, target_id: u32, spell_id: u32) {
        self.send(|seq| wire_magic::build_cast_targeted_spell(seq, target_id, spell_id));
    }

    fn display_message(&mut self, message: &str) {
        self.display_message_now(message);
    }

    fn increment_busy(&mut self) {
        *self.busy += 1;
    }
}

#[cfg(test)]
#[path = "gameplay_tests.rs"]
mod tests;
