//! The runtime side of plugins: the one implementation of every client
//! function a plugin calls ([`PluginBinding`], over the session's own state
//! on its own thread), the events plugins hear, the typed lines that name a
//! plugin verb, and what the host hears about plugins.
//!
//! The plugin host itself (`ac-plugin`) is owned by the session runner;
//! nothing here depends on a window. A query reads the runtime owner it
//! names; a command runs the owner's own operation through the session's
//! commands, at once while a connection is up (so a busy item is busy for
//! the very next call), and its answer is OpenAC's (started, refused, busy,
//! unavailable). What only the runner can do (log off, end the session,
//! another typed line) is held in [`PluginBinding::deferred`] and carried
//! out right after the plugin calls return.
//!
//! One module per API group, each an `impl PluginBinding` block:
//! [`host`] (A), [`events`] (B, the events plugins hear), [`world`] (C),
//! [`character`] (D).

use std::sync::Arc;
use std::sync::mpsc::Sender;

use ac_core::chat::{ChatKind, CombatLineKind, LogTextType};
use ac_plugin::abi::verbs::split_command_line;
use ac_plugin::abi::{CharacterInfo, Import, LogLevel, calls, serve_at};
use ac_plugin::{Caller, PluginEngine, PluginReport, PluginSessionOptions, SessionApi};

use crate::communication::{ChatLine, HeldText};
use crate::session::{Clocks, HostEvent, Outbox, SessionCommand, SessionState};

mod character;
pub(crate) mod chat;
mod client;
mod combat;
pub mod events;
mod host;
pub mod hotkey_file;
mod items;
mod magic;
pub mod navigation;
pub mod peer_hub;
pub mod peer_leases;
pub mod peer_link;
pub mod peer_transport;
pub mod peer_wire;
pub mod peers;
pub mod projectile;
pub mod social;
pub mod trade;
pub mod ui;
pub(crate) mod world;

pub use events::PluginServices;

/// What a session needs to run plugins: the process's engine and this
/// session's plugin options.
#[derive(Debug, Clone)]
pub struct PluginSetup {
    /// The engine every session of the process shares.
    pub engine: Arc<PluginEngine>,
    /// Where this session's plugins are and which to load.
    pub options: PluginSessionOptions,
    /// The labels this client answers to among the clients on this
    /// computer (the session document's `pluginTags`).
    pub peer_tags: Vec<String>,
    /// The player's plugin hotkey overrides (`plugin-hotkeys.json` beside
    /// the key bindings), read at the start and written on a rebind;
    /// `None` keeps them in memory.
    pub hotkey_overrides: Option<std::path::PathBuf>,
    /// Where the clients on this computer leave notes for one another (the
    /// cache's `plugin-peers`); `None`: this client finds no peers.
    pub peer_directory: Option<std::path::PathBuf>,
}

/// What the host hears about plugins.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PluginEvent {
    /// A log line from (or about) a plugin.
    Log {
        /// The plugin's id (`plugins` for the plugin manager itself).
        plugin: String,
        /// How serious.
        level: LogLevel,
        /// The text.
        message: String,
    },
    /// A plugin loaded, or failed (status file `pluginLoaded`,
    /// `pluginFailed`).
    Report(PluginReport),
}

/// Typed lines held by the session state until the runner hands them to
/// the plugins (the state's chat path cannot call into them itself): lines
/// naming a plugin verb, and, while a plugin intercepts input, every line
/// past the client's own commands.
#[derive(Debug, Clone, Default)]
pub struct PluginLines {
    verbs: Vec<String>,
    lines: Vec<PluginLine>,
    intercepting: bool,
    origin: LineOrigin,
    /// Lines the runner sent on after the interceptors: (text, rewrites so
    /// far, the interceptors passed it).
    resubmitted: Vec<(String, u32, bool)>,
}

/// Where a typed line came from, so a line sent on after the interceptors
/// keeps the chat entry's talk focus.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub enum LineOrigin {
    /// A line with no talk focus of its own (a command, a login line).
    #[default]
    Chat,
    /// The chat entry, talking on `channel` (to `tell_target`).
    Entry {
        /// The entry's channel.
        channel: crate::communication::ChatChannelKind,
        /// The entry's tell target.
        tell_target: Option<String>,
        /// The tell target's object id.
        tell_target_guid: u32,
    },
}

/// A line held for the plugins.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PluginLine {
    /// It names a plugin verb.
    Verb(String),
    /// The interceptors are asked about it first.
    Intercept {
        /// The line, trimmed.
        line: String,
        /// How often it was rewritten already.
        rewrites: u32,
        /// Where it came from.
        origin: LineOrigin,
    },
}

impl PluginLines {
    /// The verbs (lower case) the plugins answer now.
    pub fn set_verbs(&mut self, verbs: Vec<String>) {
        self.verbs = verbs;
    }

    /// Whether a plugin intercepts typed lines now.
    pub fn set_intercepting(&mut self, intercepting: bool) {
        self.intercepting = intercepting;
    }

    /// Where the lines claimed next come from.
    pub(crate) fn set_origin(&mut self, origin: LineOrigin) {
        self.origin = origin;
    }

    /// Takes `line` for the plugins: for the interceptors while any
    /// intercepts and the line has not been through them (nor rewritten
    /// past the bound), else when it names a plugin verb.
    pub fn claim(&mut self, line: &str) -> bool {
        let trimmed = line.trim();
        let (rewrites, passed) = match self.resubmitted.iter().position(|(t, ..)| t == trimmed) {
            Some(at) => {
                let (_, rewrites, passed) = self.resubmitted.remove(at);
                (rewrites, passed)
            }
            None => (0, false),
        };
        if self.intercepting
            && !passed
            && rewrites < ac_plugin::interceptors::MAXIMUM_REWRITE_PASSES
            && !trimmed.is_empty()
        {
            self.lines.push(PluginLine::Intercept {
                line: trimmed.to_owned(),
                rewrites,
                origin: self.origin.clone(),
            });
            return true;
        }
        let claimed = split_command_line(line)
            .is_some_and(|(_, verb, _)| self.verbs.contains(&verb.to_lowercase()));
        if claimed {
            self.lines.push(PluginLine::Verb(line.to_owned()));
        }
        claimed
    }

    /// The runner sends `line` on after the interceptors (`passed`: they
    /// let it through; else it is a rewrite, the `rewrites`th).
    pub fn resubmit(&mut self, line: &str, rewrites: u32, passed: bool) {
        self.resubmitted
            .push((line.trim().to_owned(), rewrites, passed));
    }

    /// The lines claimed since the last call, oldest first.
    pub fn take(&mut self) -> Vec<PluginLine> {
        std::mem::take(&mut self.lines)
    }
}

/// The connection, while one is up: what a plugin command goes out through.
#[derive(Debug)]
pub struct Link<'a> {
    /// The session's outbox.
    pub outbox: &'a mut Outbox,
    /// The character is in the world (game actions may go out).
    pub active: bool,
    /// Both clocks, this pass.
    pub clocks: Clocks,
}

/// One character on the account's list.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RosterEntry {
    /// Its object id.
    pub id: u32,
    /// Its name.
    pub name: String,
    /// Its slot on the list.
    pub index: i32,
    /// It is waiting to be deleted.
    pub pending_delete: bool,
}

/// What the session flow knows that the state owners do not: the account,
/// the character list, the world's population, the next login.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SessionFacts {
    /// The character is in the world.
    pub in_world: bool,
    /// The account, as the server spells it.
    pub account: String,
    /// The population the server reported at login, -1 before.
    pub server_population: i32,
    /// The account's characters.
    pub roster: Vec<RosterEntry>,
    /// The character entered automatically at the next return to
    /// character selection, 0 for none.
    pub next_login: u32,
}

impl SessionFacts {
    /// Nothing known (before a connection, or after the session).
    #[must_use]
    pub fn none() -> Self {
        Self {
            server_population: -1,
            ..Self::default()
        }
    }
}

/// The session as plugins reach it during one pass of the session loop.
#[derive(Debug)]
pub struct PluginBinding<'a> {
    /// The session's state.
    pub state: &'a mut SessionState,
    /// Where host events go.
    pub events: &'a Sender<HostEvent>,
    /// A window is attached.
    pub window_attached: bool,
    /// The character in the world, if one is (its identity: id, name,
    /// world; the rest is read from the state when asked).
    pub character: Option<CharacterInfo>,
    /// The wall clock now, UTC milliseconds.
    pub wall_unix_ms: i64,
    /// The connection, when one is up.
    pub link: Option<Link<'a>>,
    /// What the session flow knows.
    pub facts: SessionFacts,
    /// Commands only the runner can carry out (log off, end the session),
    /// run right after the plugin calls return, in order.
    pub deferred: Vec<SessionCommand>,
}

impl<'a> PluginBinding<'a> {
    /// The binding with no connection (while plugins start before the first
    /// connection, and while they stop after the last).
    pub fn offline(
        state: &'a mut SessionState,
        events: &'a Sender<HostEvent>,
        wall_unix_ms: i64,
    ) -> Self {
        Self {
            state,
            events,
            window_attached: false,
            character: None,
            wall_unix_ms,
            link: None,
            facts: SessionFacts::none(),
            deferred: Vec::new(),
        }
    }

    /// The character is in the world and a connection is up: game commands
    /// can go out.
    fn in_world(&self) -> bool {
        self.facts.in_world
            && self.link.as_ref().is_some_and(|link| link.active)
            && self.state.world.player_guid().is_some()
    }

    /// The session's time now (seconds since the connection's clock
    /// started); 0 with no connection.
    fn session_seconds(&self) -> f64 {
        self.link.as_ref().map_or_else(
            || self.state.session_seconds(),
            |l| l.clocks.session.as_secs_f64(),
        )
    }

    /// Runs a session command now through the state's owners, and reads
    /// their answer: a refusal they raised while running it. A command only
    /// the runner can carry out is deferred.
    fn run(&mut self, command: SessionCommand) -> Ran {
        let Some(link) = self.link.as_mut() else {
            return Ran::NoConnection;
        };
        let mark = self.state.queued_host_events().len();
        if let Some(rest) = self
            .state
            .command(command, link.outbox, link.active, link.clocks)
        {
            self.deferred.push(rest);
        }
        let refusal = self
            .state
            .queued_host_events()
            .get(mark..)
            .unwrap_or(&[])
            .iter()
            .find_map(|event| match event {
                HostEvent::CommandRefused(text) => Some(text.clone()),
                _ => None,
            });
        refusal.map_or(Ran::Done, Ran::Refused)
    }

    /// Runs a typed line (or another session command) now, between plugin
    /// calls, as the runner would; what only the runner can do is deferred.
    pub fn run_now(&mut self, command: SessionCommand) {
        if self.link.is_some() {
            self.run(command);
        } else {
            self.deferred.push(command);
        }
    }

    /// The session's time now in milliseconds, as the owners' pacing reads
    /// it.
    fn session_ms(&self) -> i64 {
        self.link.as_ref().map_or(0, |link| {
            i64::try_from(link.clocks.session.as_millis()).unwrap_or(i64::MAX)
        })
    }
}

/// How a command the binding ran went.
#[derive(Debug, Clone, PartialEq, Eq)]
enum Ran {
    /// No connection: nothing ran.
    NoConnection,
    /// An owner refused it, saying why.
    Refused(String),
    /// It ran.
    Done,
}

/// Routes one import to its handler: `Variant => |request| reply`. An
/// import the session does not serve (the plugin host's own) answers its
/// inert reply.
macro_rules! route {
    ($import:expr, $request:expr, $version:expr, { $( $variant:ident => $handler:expr ),* $(,)? }) => {
        match $import {
            $( Import::$variant => serve_at::<calls::$variant>($request, $version, $handler), )*
            other => Ok(other.default_reply_at($version)),
        }
    };
}

impl SessionApi for PluginBinding<'_> {
    fn log(&mut self, plugin_id: &str, level: LogLevel, message: &str) {
        // The host may have stopped listening; the session goes on.
        let _ = self.events.send(HostEvent::Plugin(PluginEvent::Log {
            plugin: plugin_id.to_owned(),
            level,
            message: message.to_owned(),
        }));
    }

    fn window_attached(&self) -> bool {
        self.window_attached
    }

    fn post_chat(&mut self, text: &str, log_text_type: u32) {
        let class = LogTextType::from_plugin_value(i32::try_from(log_text_type).unwrap_or(-1));
        self.state
            .communication
            .show_plugin_text(self.wall_unix_ms, text, class);
    }

    fn character(&self) -> Option<CharacterInfo> {
        self.character_info()
    }

    fn in_stay(&self) -> bool {
        events::in_stay(self.state)
    }

    fn plugin_ui(&mut self) -> Option<&mut ac_plugin_ui::PluginUi> {
        Some(&mut self.state.plugin_ui)
    }

    fn spell_icon(&self, spell: u32) -> Option<u32> {
        ui::spell_icon(self.state, spell)
    }

    fn entities(&self) -> Vec<ac_plugin::abi::world::EntitySnapshot> {
        self.world_entities()
    }

    fn object_icon(&self, object: u32) -> Option<u32> {
        ui::object_icon(self.state, object)
    }

    fn submit_now(&mut self, submits: &mut Vec<String>) {
        self.chat_submit_now(submits);
    }

    fn new_chat_events(&mut self) -> Vec<ac_plugin::abi::Event> {
        self.chat_new_events()
    }

    fn call(
        &mut self,
        caller: Caller<'_>,
        import: Import,
        request: &[u8],
    ) -> Result<Vec<u8>, String> {
        let reply = route!(import, request, caller.api_version, {
            // A. Host services.
            HostClock => |()| self.clock(),
            HostRequestClose => |()| self.request_close(),
            HostProcessStats => |()| self.process_stats(caller.plugin_id),
            // D. Character.
            CharacterExperience => |()| character::experience(self.state),
            CharacterSkills => |()| self.character_skills(),
            CharacterSkill => |id| self.character_skill(id),
            CharacterAttributes => |()| self.character_attributes(),
            CharacterVitals => |()| self.character_vitals(),
            CharacterEnchantments => |timed| self.character_enchantments(timed),
            CharacterAllEnchantments => |()| self.character_all_enchantments(),
            CharacterAdvance => |request| self.character_advance(request),
            CharacterOptions => |()| self.character_options(),
            CharacterSetOption => |change| self.character_set_option(change),
            // C. World reads.
            WorldObjects => |()| self.world_objects(),
            WorldObject => |id| self.world_object(id),
            WorldProperties => |id| self.world_properties(id),
            WorldIntProperty => |key| self.world_int_property(key),
            WorldIdentify => |id| self.world_identify(id),
            WorldActivate => |id| self.world_activate(id),
            WorldLastActivation => |()| self.world_last_activation(),
            WorldOpenContainer => |()| self.world_open_container(),
            WorldEntities => |()| self.world_entities(),
            WorldScenery => |()| self.world_scenery(),
            WorldContracts => |()| self.world_contracts(),
            WorldTime => |()| self.world_time(),
            DungeonLandblock => |()| self.dungeon_landblock(),
            DungeonIsSealed => |cell| self.dungeon_is_sealed(cell),
            DungeonFloorplan => |landblock| self.dungeon_floorplan(landblock),
            DungeonIndoorCells => |landblock| self.dungeon_indoor_cells(landblock),
            LandscapeTerrain => |landblocks| self.landscape_terrain(&landblocks),
            LandscapeTerrainPalette => |()| self.landscape_terrain_palette(),
            ProjectilesAvailable => |()| self.projectiles_available(),
            ProjectilesEvaluatePath => |query| self.projectiles_evaluate_path(&query),
            // E. Spells and magic.
            SpellsPage => |(first, count)| self.spells_page(first, count),
            SpellsGet => |id| self.spells_get(id),
            SpellsFind => |query| self.spells_find(query),
            SpellsKnown => |list| self.spells_known(list),
            SpellsIsKnown => |id| self.spells_is_known(id),
            SpellsKnownRevision => |()| self.spells_known_revision(),
            SpellsKnownIds => |list| self.spells_known_ids(list),
            SpellsComponent => |id| self.spells_component(id),
            SpellsCooldown => |id| self.spells_cooldown(id),
            MagicIsCasting => |()| self.magic_is_casting(),
            MagicGate => |request| self.magic_gate(request),
            MagicCast => |request| self.magic_cast(request),
            MagicGateWithoutSelecting => |request| self.magic_gate_without_selecting(request),
            MagicCastWithoutSelecting => |request| self.magic_cast_without_selecting(request),
            MagicHasComponents => |id| self.magic_has_components(id),
            MagicLastCompletion => |()| self.magic_last_completion(),
            EnchantmentsReport => |cast| self.enchantments_report(cast),
            EnchantmentsCapture => |target| self.enchantments_capture(target),
            EnchantmentsForget => |target| self.enchantments_forget(target),
            // F. Combat.
            CombatSnapshot => |()| self.combat_snapshot(),
            CombatHostileTargets => |maximum| self.combat_hostile_targets(maximum),
            CombatEnterMode => |mode| self.combat_enter_mode(mode),
            CombatEnterDefaultMode => |()| self.combat_enter_default_mode(),
            CombatBeginAttack => |request| self.combat_begin_attack(request),
            CombatReleaseAttack => |()| self.combat_release_attack(),
            CombatAbortAttack => |()| self.combat_abort_attack(),
            CombatDismissGhost => |target| self.combat_dismiss_ghost(target),
            CombatControl => |acquire| self.combat_control(caller.plugin_id, acquire),
            // G. Items, equipment, loot.
            ItemsOwned => |()| self.items_owned(),
            ItemsState => |()| self.items_state(),
            ItemsInventoryComplete => |()| self.items_inventory_complete(),
            ItemsProperties => |id| self.items_properties(id),
            ItemsUse => |id| self.items_apply((id, 0)),
            ItemsApply => |pair| self.items_apply(pair),
            ItemsMove => |request| self.items_move(request),
            ItemsMoveJoinStack => |(request, join)| self.items_move_join_stack(request, join),
            ItemsMerge => |request| self.items_merge(request),
            ItemsDrop => |request| self.items_drop(request),
            ItemsGive => |request| self.items_give(request),
            ItemsSalvage => |request| self.items_salvage(request),
            ItemsSell => |request| self.items_sell(request),
            ItemsForgetStale => |id| self.items_forget_stale(id),
            EquipmentBusy => |()| self.equipment_busy(),
            EquipmentOwned => |()| self.equipment_owned(),
            EquipmentPlacements => |()| self.equipment_placements(),
            EquipmentEquip => |request| self.equipment_equip(request),
            EquipmentEquipSecondary => |id| self.equipment_equip_secondary(id),
            LootState => |()| self.loot_state(),
            LootCorpses => |maximum| self.loot_corpses(maximum),
            LootContents => |()| self.loot_contents(),
            LootProperties => |id| self.loot_properties(id),
            LootOpen => |id| self.loot_open(id),
            LootClose => |id| self.loot_close(id),
            LootIdentify => |id| self.loot_identify(id),
            LootPickup => |request| self.loot_pickup(request),
            LootPut => |request| self.loot_put(request),
            // H. Trade and vendor.
            TradeState => |()| self.trade_state(),
            TradeAdd => |item| self.trade_add(item),
            TradeAccept => |()| self.trade_accept(),
            TradeDecline => |()| self.trade_decline(),
            TradeReset => |()| self.trade_reset(),
            TradeEnd => |()| self.trade_end(),
            VendorState => |()| self.vendor_state(),
            VendorProperties => |id| self.vendor_properties(id),
            VendorAddBuy => |entry| self.vendor_add_buy(entry),
            VendorAddSell => |id| self.vendor_add_sell(id),
            VendorRemoveBuy => |id| self.vendor_remove_buy(id),
            VendorRemoveSell => |id| self.vendor_remove_sell(id),
            VendorClearBuy => |()| self.vendor_clear_buy(),
            VendorClearSell => |()| self.vendor_clear_sell(),
            VendorBuyAll => |()| self.vendor_buy_all(),
            VendorSellAll => |()| self.vendor_sell_all(),
            // K. Chat.
            ChatCaptured => |after| self.chat_captured(after),
            ChatCompose => |text| self.chat_compose(&text),
            ChatIsInputActive => |()| self.chat_is_input_active(),
            // M. Peers (the status board is the plugin host's).
            PeersClients => |()| self.peers_clients(),
            PeersAnnounceCast => |cast| self.peers_announce_cast(&cast),
            PeersCasts => |after| self.peers_casts(after),
            PeersSetTags => |tags| self.peers_set_tags(&tags),
            PeersBroadcast => |broadcast| self.peers_broadcast(broadcast),
            PeersCommands => |after| self.peers_commands(after),
            PeersSubscribe => |capabilities| self.peers_subscribe(caller.plugin_id, capabilities),
            PeersUnsubscribe => |handle| self.peers_unsubscribe(caller.plugin_id, handle),
            PeersIsConnected => |()| self.peers_is_connected(),
            PeersCaptureSelf => |()| self.peers_capture_self(),
            PeersOwnCasts => |after| self.peers_own_casts(after),
            PeersOwnCommands => |after| self.peers_own_commands(after),
            PeersImportClient => |client| self.peers_import_client(&client),
            PeersImportCast => |cast| self.peers_import_cast(&cast),
            PeersImportCommand => |command| self.peers_import_command(command),
            // L. The client window.
            ClientSetClipboard => |text| self.client_set_clipboard(text),
            ClientMinimize => |()| self.client_minimize(),
            ClientRestore => |()| self.client_restore(),
            ClientIsMinimized => |()| self.client_is_minimized(),
            // J. Social and session.
            FellowshipState => |()| self.fellowship_state(),
            FellowshipMembers => |()| self.fellowship_members(false),
            FellowshipRoster => |()| self.fellowship_members(true),
            FellowshipCreate => |request| self.fellowship_create(request),
            FellowshipRecruit => |id| self.fellowship_recruit(id),
            FellowshipDismiss => |id| self.fellowship_dismiss(id),
            FellowshipQuit => |disband| self.fellowship_quit(disband),
            FellowshipAssignLeader => |id| self.fellowship_assign_leader(id),
            FellowshipSetOpen => |open| self.fellowship_set_open(open),
            FellowshipRequestVitals => |on| self.fellowship_request_vitals(on),
            AllegianceState => |()| self.allegiance_state(),
            AllegianceSwear => |id| self.allegiance_swear(id),
            AllegianceBreak => |id| self.allegiance_break(id),
            LoginState => |()| self.login_state(),
            LoginSetNext => |id| self.login_set_next(id),
            LoginClearNext => |()| self.login_clear_next(),
            LoginLogout => |()| self.login_logout(),
            RecallState => |()| self.recall_state(),
            RecallGo => |kind| self.recall_go(kind),
            SelectionState => |()| self.selection_state(),
            SelectionSelect => |id| self.selection_select(id),
            SelectionClear => |()| self.selection_clear(),
            SelectionExecute => |action| self.selection_execute(action),
            DialogAnswer => |answer| self.dialog_answer(answer),
            RecoveryBusyState => |()| self.recovery_busy_state(),
            RecoveryClearOneBusy => |()| self.recovery_clear_one_busy(),
            // I. Navigation.
            NavigationSnapshot => |()| self.navigation_snapshot(),
            NavigationObject => |id| self.navigation_object(id),
            NavigationFind => |request| self.navigation_find(&request),
            NavigationObjects => |()| self.navigation_objects(),
            NavigationRoomAhead => |distance| self.navigation_room_ahead(distance),
            NavigationSetIntent => |intent| self.navigation_set_intent(&intent),
            NavigationClearIntent => |()| self.navigation_clear_intent(),
            NavigationFace => |heading| self.navigation_face(caller.plugin_id, heading),
            NavigationMove => |request| self.navigation_move(caller.plugin_id, &request),
            NavigationStopMoving => |channel| self.navigation_stop_moving(caller.plugin_id, channel),
            NavigationJump => |power| self.navigation_jump(caller.plugin_id, power),
            NavigationMoveReport => |()| self.navigation_move_report(),
            NavigationGoTo => |goal| self.navigation_go_to(caller.plugin_id, goal),
            NavigationGoToPlace => |request| self.navigation_go_to_place(caller.plugin_id, &request),
            NavigationStandOn => |goal| self.navigation_stand_on(caller.plugin_id, goal),
            NavigationFollow => |goal| self.navigation_follow(caller.plugin_id, goal),
            NavigationStopGoTo => |()| self.navigation_stop_go_to(caller.plugin_id),
            NavigationGoToReport => |()| self.navigation_go_to_report(),
            NavigationPreviewPath => |request| self.navigation_preview_path(caller.plugin_id, &request),
            NavigationReachableCells => |request| self.navigation_reachable_cells(caller.plugin_id, &request),
            NavigationPause => |reason| self.navigation_pause(caller.plugin_id, reason),
            NavigationPauseSet => |change| self.navigation_pause_set(caller.plugin_id, change),
            NavigationPauseRelease => |handle| self.navigation_pause_release(caller.plugin_id, handle),
        });
        reply.map_err(|error| format!("its request to {} is malformed: {error}", import.name()))
    }

    fn released(&mut self, plugin_id: &str) {
        self.combat_control(plugin_id, false);
        self.navigation_release(plugin_id);
        self.state.plugin_services.peers.release_plugin(plugin_id);
        self.release_process_stats(plugin_id);
    }
}

/// A transcript line as a plugin sees it.
#[must_use]
pub fn plugin_chat_line(line: &ChatLine) -> ac_plugin::abi::ChatLine {
    let kind = match line.kind {
        ChatKind::LocalSpeech => 0,
        ChatKind::RangedSpeech => 1,
        ChatKind::Channel => 2,
        ChatKind::Tell => 3,
        ChatKind::System => 4,
        ChatKind::Popup => 5,
        ChatKind::Emote => 6,
        ChatKind::SoulEmote => 7,
        ChatKind::Combat => 8,
    };
    let combat_kind = match line.combat_kind {
        None => 0,
        Some(CombatLineKind::Info) => 1,
        Some(CombatLineKind::Warning) => 2,
        Some(CombatLineKind::Error) => 3,
    };
    ac_plugin::abi::ChatLine {
        sequence: line.sequence,
        sender_id: line.sender_guid,
        kind,
        sender: line.sender.clone(),
        text: line.words.clone(),
        channel: line.channel_name.clone(),
        log_text_type: line.log_text_type,
        combat_kind,
        received_unix_ms: line.received_unix_ms,
        channel_id: line.channel_id,
        display_text: line.display_text.clone(),
    }
}

/// What a chat filter is asked about held text (OpenAC
/// `ChatLog.ToFilterCandidate`): the line as a plugin sees it, without the
/// timestamp, sequence 0 (it is not kept yet); a status notice under
/// [`ac_plugin::abi::types::STATUS_TEXT_KIND`] in the client-local class
/// (OpenAC `RuntimeCommunicationState.AddText`).
#[must_use]
pub fn filter_candidate(held: &HeldText) -> ac_plugin::abi::ChatLine {
    match held {
        HeldText::Line(entry) => ac_plugin::abi::ChatLine {
            sequence: 0,
            ..plugin_chat_line(&ChatLine::from_entry(entry, false, 0))
        },
        HeldText::Status {
            text,
            received_unix_ms,
        } => ac_plugin::abi::ChatLine {
            sequence: 0,
            sender_id: 0,
            kind: ac_plugin::abi::types::STATUS_TEXT_KIND,
            sender: String::new(),
            text: text.clone(),
            channel: String::new(),
            log_text_type: LogTextType::ClientLocal.as_u32(),
            combat_kind: 0,
            received_unix_ms: *received_unix_ms,
            channel_id: 0,
            display_text: text.clone(),
        },
    }
}

/// The text held for the plugins' chat filters, each kept or dropped by
/// them in arrival order.
pub fn filter_held_chat(plugins: &mut ac_plugin::PluginSession, binding: &mut PluginBinding<'_>) {
    for held in binding.state.communication.take_held() {
        let candidate = filter_candidate(&held);
        let suppressed = plugins.filter_chat(&candidate, binding);
        binding.state.communication.release(held, suppressed);
    }
}

#[cfg(test)]
mod chat_tests;
#[cfg(test)]
mod combat_tests;
#[cfg(test)]
mod events_tests;
#[cfg(test)]
mod items_tests;
#[cfg(test)]
mod landscape_tests;
#[cfg(test)]
mod magic_tests;
#[cfg(test)]
mod navigation_tests;
#[cfg(test)]
mod peer_hub_tests;
#[cfg(test)]
mod peer_relay_tests;
#[cfg(test)]
mod peers_tests;
#[cfg(test)]
mod portal_hold_tests;
#[cfg(test)]
mod projectile_tests;
#[cfg(test)]
mod social_tests;
#[cfg(test)]
mod tests;
#[cfg(test)]
mod trade_tests;
