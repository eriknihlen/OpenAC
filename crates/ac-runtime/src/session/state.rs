//! Every owner of one session's game state, together: the world, chat and
//! social state, the character options, the inventory and the gameplay
//! owners (magic, combat, book, fellowship, allegiance, house). Server messages
//! are applied to each in turn, host commands go to the one that owns them,
//! and what the host should see comes back as [`HostEvent`]s.
//!
//! Nothing here touches a socket: the runner hands in parsed messages and
//! the session's outbox, which keeps this testable message by message.

use std::sync::Arc;
use std::time::Duration;

use ac_core::combat::CombatMode;
use ac_core::items::PublicWeenieFlags;
use ac_core::player::{LiveSkillCreditResolver, SkillFormulaBonusResolver};
use ac_core::properties::{PropertyBool, PropertyInt};
use ac_proto::appraise::IdentifyObjectResponse;
use ac_proto::social::{ConfirmationDone, ConfirmationRequest};
use ac_proto::{GameEventType, Inbound, PlayerDescription, build_login_complete};

use super::Outbox;
use super::Queue;
use super::runner::Recall;
use super::runner::{HostEvent, ObjectSummary, SessionCommand};
use crate::character::{CharacterOptions, OPTIONS, option_id};
use crate::communication::{
    ChatChannelKind, ClientCommandId, Communication, CommunicationEvent, CommunicationPaths,
    Defaults, LocalRequest, Membership, Outbound, PlayerFacts, SendChat,
};
use crate::gameplay::{
    GameTables, Gameplay, GameplayCommand, GameplayContext, GameplayEvent, GameplayNotice,
};
use crate::inventory::{
    Inventory, InventoryEvent, ItemCommand, ItemContext, Notice, inventory_view,
};
use crate::movement::Movement;
use crate::navigation::{Navigation, NavigationContext};
use crate::world::transit::{
    DestinationReadiness, DestinationShown, LogoutStage, PortalSnapshot, PortalSpaceEvent,
    WorldTransit,
};
use crate::world::{PlayerView, World, distance};

/// The two clocks the owners read.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Clocks {
    /// Wall clock, UTC milliseconds since the Unix epoch (chat lines, the
    /// journal, the options' auto-save).
    pub wall_unix_ms: i64,
    /// Session time (use pacing, appraisal limits, appraisal stamps).
    pub session: Duration,
}

impl Clocks {
    fn session_ms(self) -> i64 {
        i64::try_from(self.session.as_millis()).unwrap_or(i64::MAX)
    }

    fn wall(self) -> Duration {
        Duration::from_millis(u64::try_from(self.wall_unix_ms).unwrap_or(0))
    }
}

/// A portal arrival the session holds for the attached window.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct HeldArrival {
    generation: u64,
    teleport_sequence: u16,
    destination_cell: u32,
}

impl HeldArrival {
    fn event(self) -> PortalSpaceEvent {
        PortalSpaceEvent::Destination {
            generation: self.generation,
            teleport_sequence: self.teleport_sequence,
            destination_cell: self.destination_cell,
        }
    }
}

/// The character's entry into the world, as an attached window presents
/// it: portal space from the enter request until the login destination is
/// shown (OpenAC's login tunnel).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
enum LoginEntry {
    /// No entry under way.
    #[default]
    Idle,
    /// The enter request went out; the player is not in the world yet.
    Entering,
    /// The player is in the world; login complete waits for the window.
    Held {
        generation: u64,
        destination_cell: u32,
    },
    /// The entry's presentation is over (shown, released, or replaced by a
    /// teleport) while the entry itself still runs.
    Passed,
}

/// One session's owners.
#[derive(Debug)]
pub struct SessionState {
    /// Objects, items and the player's numbers.
    pub world: World,
    /// Chat and social state.
    pub communication: Communication,
    /// The character options.
    pub options: CharacterOptions,
    /// The inventory requests, vendor, trade and appraisals.
    pub inventory: Inventory,
    /// Selection, magic, combat, the book, fellowship, allegiance, house.
    pub gameplay: Gameplay,
    /// The generation the owners hold state for.
    pub(crate) generation: super::Generation,
    /// Log out and portal space.
    pub transit: WorldTransit,
    last_portal: PortalSnapshot,
    last_logout: LogoutStage,
    /// Portal space as an attached window shows it, oldest first.
    portal_events: Vec<PortalSpaceEvent>,
    /// A window is attached: a portal arrival waits for it.
    window_attached: bool,
    /// The attached window draws the world (it presents the login).
    window_presents_world: bool,
    /// The portal arrival waiting for the attached window to show its
    /// destination.
    held_arrival: Option<HeldArrival>,
    /// The portal arrival no window holds, waiting for its destination's
    /// collision (OpenAC's headless pending portal completion).
    destination_hold: Option<HeldArrival>,
    /// The destination hold began in the message being applied: its first
    /// step runs once that message has placed the player.
    destination_hold_began: bool,
    /// The character's entry as a window presents it.
    login_entry: LoginEntry,
    /// The held login complete may go out (the runner sends it).
    login_release: bool,
    /// A teleport replaced the held login: its arrival releases the held
    /// login complete instead of sending its own.
    login_release_on_arrival: bool,
    last_tick: Option<Duration>,
    /// Passes and the world's simulation time (still while no world is up).
    pub frame_clock: crate::frame_clock::FrameClock,
    /// The physics, the local player's movement and other bodies.
    pub movement: Movement,
    /// The 25-second expiry of objects out of range.
    pub liveness: crate::world::liveness::LivenessController,
    /// Walks the client plans and walks, and moves asked for by chat.
    pub navigation: Navigation,
    host_events: Vec<HostEvent>,
    /// The wall clock of the call being handled (notices are stamped with it).
    wall_unix_ms: i64,
    /// The link panel's ping and its round trip.
    pub ping: super::link::PingClock,
    /// The session document's declared character options.
    options_seeder: crate::character::OptionsSeeder,
    /// The session document's login commands.
    login_commands: crate::communication::LoginCommandSequence,
    /// The generation the login commands were started in.
    login_commands_started: Option<super::Generation>,
    /// Login complete went out in this generation.
    login_complete_seen: bool,
    /// Where the session is in its life, as of the runner's last pass.
    pub(crate) lifecycle_state: super::LifecycleState,
    /// Typed lines naming a plugin verb, for the runner to hand to the
    /// session's plugins (client commands always win over them).
    pub plugin_lines: crate::plugins::PluginLines,
    /// What the runtime keeps only for plugins (revisions, what was last
    /// reported, activations and walks they started).
    pub plugin_services: crate::plugins::PluginServices,
    /// The plugins' UI: their windows, canvases, images, labels and lines
    /// (session state a window only draws, `crate::plugins::ui`).
    pub plugin_ui: ac_plugin_ui::PluginUi,
}

impl SessionState {
    /// Fresh owners keeping chat files where `paths` says.
    #[must_use]
    pub fn new(paths: CommunicationPaths) -> Self {
        Self {
            world: World::new(),
            communication: Communication::new(paths),
            options: CharacterOptions::new(),
            inventory: Inventory::new(),
            gameplay: Gameplay::default(),
            generation: super::Generation::default(),
            transit: WorldTransit::new(),
            last_portal: PortalSnapshot::IDLE,
            last_logout: LogoutStage::None,
            portal_events: Vec::new(),
            window_attached: false,
            window_presents_world: false,
            held_arrival: None,
            destination_hold: None,
            destination_hold_began: false,
            login_entry: LoginEntry::Idle,
            login_release: false,
            login_release_on_arrival: false,
            last_tick: None,
            frame_clock: crate::frame_clock::FrameClock::default(),
            movement: Movement::default(),
            liveness: crate::world::liveness::LivenessController::default(),
            navigation: Navigation::default(),
            host_events: Vec::new(),
            wall_unix_ms: 0,
            ping: super::link::PingClock::default(),
            options_seeder: crate::character::OptionsSeeder::default(),
            login_commands: crate::communication::LoginCommandSequence::default(),
            login_commands_started: None,
            login_complete_seen: false,
            lifecycle_state: super::LifecycleState::Stopped,
            plugin_lines: crate::plugins::PluginLines::default(),
            plugin_services: crate::plugins::PluginServices::default(),
            plugin_ui: ac_plugin_ui::PluginUi::new(),
        }
    }

    /// What the host will hear next and has not taken yet, oldest first (a
    /// plugin's command reads its owner's answer here in the same pass).
    #[must_use]
    pub fn queued_host_events(&self) -> &[HostEvent] {
        &self.host_events
    }

    /// Gathers what the owners produced since into the queued host events
    /// (their notices become chat lines) without taking them.
    pub(crate) fn collect_host_events(&mut self) {
        self.forward();
    }

    /// Queues `event` for the host (a line the session itself has to
    /// report, such as the peer hub connection's).
    pub(crate) fn push_host_event(&mut self, event: HostEvent) {
        self.host_events.push(event);
    }

    /// Take what the host should see, oldest first.
    pub fn drain_host_events(&mut self) -> Vec<HostEvent> {
        self.forward();
        std::mem::take(&mut self.host_events)
    }

    /// Apply one parsed server message to every owner, in order. Events more
    /// than one owner reads are parsed here, once. `active` is true in the
    /// world.
    pub fn apply(
        &mut self,
        message: &Inbound<'_>,
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
    ) {
        self.wall_unix_ms = clocks.wall_unix_ms;
        let selected = self.gameplay.selected_presence(&self.world);
        let mark = self.world.change_count();
        self.world.apply(message);
        self.follow_transit(message);
        self.movement
            .observe(&mut self.world, message, mark, outbox, clocks.session);
        // A destination hold begun by this message takes its first step
        // now that the message has offered the player's placement (OpenAC
        // advances the portal completion in the network handler).
        if std::mem::take(&mut self.destination_hold_began) {
            self.advance_portal_completion(outbox);
        }
        let mut description = None;
        let mut cast_use_done = None;
        if let Inbound::GameEvent(event) = message {
            let failure = 0xF7B0_0000 | event.event_type.0;
            match event.event_type {
                // A use-done names no action: it answers a cast only while
                // no item action is outstanding (an unreadable one is
                // recorded by the inventory, which reads it too).
                GameEventType::USE_DONE => {
                    if let Ok(code) = ac_proto::inventory::parse_inventory_word(event.payload)
                        && self.inventory.transactions().item_busy_count() == 0
                    {
                        cast_use_done = Some(code);
                    }
                }
                GameEventType::PLAYER_DESCRIPTION => {
                    match PlayerDescription::parse(event.payload) {
                        Ok(parsed) => {
                            self.world.apply_player_description(&parsed);
                            self.options.apply_player_description(&parsed);
                            self.inventory.load_shortcuts(&parsed.shortcuts);
                            description = Some(parsed);
                        }
                        Err(_) => self.world.record_parse_failure(failure),
                    }
                }
                GameEventType::IDENTIFY_OBJECT_RESPONSE => {
                    match IdentifyObjectResponse::parse(event.payload) {
                        Ok(response) => {
                            self.world
                                .apply_appraisal(&response, clocks.session.as_secs_f64());
                            self.inventory.accept_appraisal(&response, &self.world);
                        }
                        Err(_) => self.world.record_parse_failure(failure),
                    }
                }
                GameEventType::CHARACTER_CONFIRMATION_REQUEST => {
                    match ConfirmationRequest::parse(event.payload) {
                        Ok(request) => {
                            self.communication.server_confirmation_request(&request);
                        }
                        Err(_) => self.world.record_parse_failure(failure),
                    }
                }
                GameEventType::CHARACTER_CONFIRMATION_DONE => {
                    match ConfirmationDone::parse(event.payload) {
                        Ok(done) => {
                            self.communication.server_confirmation_done(&done);
                        }
                        Err(_) => self.world.record_parse_failure(failure),
                    }
                }
                GameEventType::PING_RESPONSE => {
                    match ac_proto::social::parse_ping_response(event.payload) {
                        Ok(()) => self.ping.answered(clocks.session),
                        Err(_) => self.world.record_parse_failure(failure),
                    }
                }
                _ => {}
            }
        }
        let mut ctx = ItemContext {
            world: &mut self.world,
            outbox,
            options: &self.options,
            active,
            now_ms: clocks.session_ms(),
            in_peace: self.gameplay.combat_mode() == CombatMode::NON_COMBAT,
            approach: None,
        };
        self.inventory.apply(message, &mut ctx);
        if let Some(code) = cast_use_done {
            self.gameplay.complete_cast_use(code);
        }
        let teleporting =
            self.transit.is_teleport_active() || self.transit.has_pending_teleport_start();
        let mut ctx = GameplayContext {
            world: &mut self.world,
            outbox,
            options: &self.options,
            active,
            clocks,
            body: Some(&mut self.movement),
            teleporting,
            opened_corpses: Some(self.inventory.external_containers()),
        };
        if let Some(description) = &description {
            self.gameplay
                .apply_player_description(description, &mut ctx);
        }
        self.gameplay.apply(message, &mut ctx);
        // The selection lets go of an object that left the world.
        self.gameplay.follow_selected_object(selected, &mut ctx);
        self.follow_combat(outbox, active, clocks);
        self.communication.apply(message, clocks.wall_unix_ms);
        if description.is_some() {
            // The server said what the character's options are: the
            // declared ones go on now if login complete is already out.
            let steps = self.options_seeder.note_options_seeded(&self.options);
            self.seed_options(&steps, outbox, active, clocks);
        }
        self.forward();
    }

    /// Portal space follows the server: its teleport start queues and (with
    /// no presentation to wait for) activates at once, the player's accepted
    /// positions offer the destination, and an arrival completes (with a
    /// window attached, once the window has shown the destination; without
    /// one, once the destination's collision is loaded).
    fn follow_transit(&mut self, message: &Inbound<'_>) {
        if let Inbound::PlayerTeleport(teleport) = message
            && self
                .transit
                .try_queue_teleport_start(teleport.teleport_sequence)
        {
            // A newer start replaces an arrival still held: the server has
            // sent the player on, so the old one is abandoned unannounced.
            if let Some(held) = self.held_arrival.take() {
                self.transit.cancel(held.generation);
                self.movement.set_arrival_held(false);
            }
            if let Some(held) = self.destination_hold.take() {
                self.transit.cancel(held.generation);
                self.movement.release_destination_hold();
            }
            // A teleport replaces the login presentation: its reveal is
            // abandoned and login complete goes out when the teleport
            // arrives (OpenAC `OnTeleportStarted` resets the login tunnel).
            match self.login_entry {
                LoginEntry::Held { generation, .. } => {
                    self.transit.cancel(generation);
                    self.login_release_on_arrival = true;
                    self.login_entry = LoginEntry::Passed;
                }
                LoginEntry::Entering => self.login_entry = LoginEntry::Passed,
                LoginEntry::Idle | LoginEntry::Passed => {}
            }
            self.transit.activate_queued_teleport();
            self.portal_events.push(PortalSpaceEvent::Entered {
                teleport_sequence: teleport.teleport_sequence,
            });
        }
        for (destination, advanced) in self.world.take_player_moves() {
            self.transit
                .offer_teleport_destination(destination, advanced);
        }
        self.complete_portal();
    }

    /// Begin the reveal of the accepted destination. With a window attached
    /// the arrival waits for the window to show it (the body, placed at the
    /// destination, stays in portal space); without one it waits until the
    /// destination's collision is loaded and the player placed on it, the
    /// way OpenAC's host without a window completes it.
    fn complete_portal(&mut self) {
        let Some(destination) = self.transit.accepted_teleport_destination() else {
            return;
        };
        let (sequence, cell) = (destination.teleport_sequence, destination.cell_id());
        let Some(generation) = self.transit.try_begin_portal_reveal(sequence, cell) else {
            return;
        };
        // The reveal has begun, which is a transition of its own: it is
        // told before an arrival completes it, so a session without a
        // window (whose arrival may complete in this same pass) tells the
        // same two transitions one with a window tells.
        self.publish_portal();
        let held = HeldArrival {
            generation,
            teleport_sequence: sequence,
            destination_cell: cell,
        };
        if self.window_holds() {
            self.held_arrival = Some(held);
            self.movement.set_arrival_held(true);
            self.portal_events.push(held.event());
            return;
        }
        self.destination_hold = Some(held);
        self.destination_hold_began = true;
        self.movement.hold_destination();
    }

    /// The arrival no window holds takes a step toward its destination
    /// and completes once the destination's collision is loaded and the
    /// player placed on it (OpenAC
    /// `RuntimeLiveEntitySessionController.TryAdvancePortalCompletion`):
    /// in the pass the destination arrives, then once a pass.
    fn advance_portal_completion(&mut self, outbox: &mut Outbox) {
        let Some(held) = self.destination_hold else {
            return;
        };
        if !self.movement.prepare_destination(&mut self.world, outbox) {
            return;
        }
        self.destination_hold = None;
        let readiness = DestinationReadiness::immediate(held.generation, held.destination_cell);
        self.arrive(held, readiness, outbox);
    }

    /// The arrival completes: the destination's readiness is acknowledged,
    /// the player materializes and enters the world, the reveal completes,
    /// and the server hears login complete, which ends its portal space.
    fn arrive(&mut self, held: HeldArrival, readiness: DestinationReadiness, outbox: &mut Outbox) {
        let HeldArrival {
            generation,
            teleport_sequence,
            destination_cell,
        } = held;
        let refused = if !self.transit.acknowledge_destination_readiness(readiness) {
            Some("its readiness")
        } else if !self.transit.acknowledge_portal_materialized(
            generation,
            teleport_sequence,
            destination_cell,
        ) {
            Some("its materialization")
        } else if !self.transit.acknowledge_world_viewport_visible(generation) {
            Some("its world view")
        } else if !self.transit.complete(generation) {
            Some("its completion")
        } else {
            None
        };
        self.movement.arrive();
        if let Some(step) = refused {
            // No newer teleport replaced this one (that cancels the held
            // arrival before it gets here), so the server never hears that
            // this teleport ended and keeps the character in portal space.
            let snapshot = self.transit.snapshot();
            self.movement.refuse(&format!(
                "portal arrival {generation} (teleport {teleport_sequence}) to \
                 0x{destination_cell:08X} refused at {step}: the server is not told \
                 the teleport ended (reveal {} to 0x{:08X}, {} stale, {} invariant failures)",
                snapshot.generation,
                snapshot.destination_cell(),
                self.transit.rejection_count(),
                snapshot.invariant_failure_count,
            ));
            self.transit.cancel(generation);
        } else {
            if std::mem::take(&mut self.login_release_on_arrival) {
                // The login complete a window held at login goes out now.
                self.login_release = true;
            } else {
                // Login complete carries sequence 0 and does not use the
                // counter.
                outbox.send(build_login_complete(), Queue::Ui);
            }
            self.portal_events.push(PortalSpaceEvent::Arrived {
                teleport_sequence,
                destination_cell,
            });
        }
        self.transit.end_teleport();
    }

    /// The attached window showed the destination: the held arrival
    /// completes with the readiness the window found. An answer for any
    /// other reveal (one already released or replaced) changes nothing.
    fn destination_shown(&mut self, shown: DestinationShown, outbox: &mut Outbox) {
        let Some(held) = self.held_arrival else {
            return;
        };
        if shown.teleport_sequence != held.teleport_sequence
            || shown.readiness.generation != held.generation
            || shown.readiness.destination_cell != held.destination_cell
        {
            return;
        }
        self.held_arrival = None;
        self.arrive(held, shown.readiness, outbox);
    }

    /// Whether a window is attached. A held arrival (and a held login) is
    /// released the moment the window goes: nobody is left to show the
    /// destination, so it completes as it does without a window.
    pub fn set_window_attached(&mut self, attached: bool, outbox: &mut Outbox) {
        let changed = attached != self.window_attached;
        self.window_attached = attached;
        if changed {
            // A window reports its own minimized state once it is up.
            self.plugin_services.window_minimized = false;
        }
        self.navigation.set_drawable(attached);
        if !attached {
            // The window's inventory showed the open pack and its salvage
            // window held the staged items; both went with it.
            self.inventory.window_detached(&mut self.world);
            self.release_holds(outbox);
        }
    }

    /// The attached window draws the world (it has the dats): it presents
    /// the login, and login complete waits for it (OpenAC holds login
    /// complete only for a window with a world to show).
    fn set_window_presents_world(&mut self, presents: bool) {
        self.window_presents_world = presents;
    }

    /// Whether an attached window holds the arrivals: one that is not
    /// minimized (a minimized window draws nothing, while OpenAC's update
    /// loop runs its presentation on and completes the arrival).
    fn window_holds(&self) -> bool {
        self.window_attached && !self.plugin_services.window_minimized
    }

    /// Whether the attached window presents the login (login complete
    /// waits for it).
    #[must_use]
    pub fn window_holds_login(&self) -> bool {
        self.window_holds() && self.window_presents_world
    }

    /// A held arrival and a held login complete as they do without a
    /// window.
    fn release_holds(&mut self, outbox: &mut Outbox) {
        if let Some(held) = self.held_arrival.take() {
            let readiness = DestinationReadiness::immediate(held.generation, held.destination_cell);
            self.arrive(held, readiness, outbox);
            self.forward();
        }
        if let LoginEntry::Held {
            generation,
            destination_cell,
        } = self.login_entry
        {
            self.complete_login(DestinationReadiness::immediate(
                generation,
                destination_cell,
            ));
            self.forward();
        }
    }

    /// Once a pass, after the window learned who the player is: the
    /// character's entry runs while `entering` (the enter request is out
    /// and login complete is not). A window shows portal space from the
    /// start of the entry; an entry that goes back to selection before the
    /// player is in the world withdraws it.
    pub fn follow_entry(&mut self, entering: bool) {
        match (entering, self.login_entry) {
            (true, LoginEntry::Idle) => {
                self.login_entry = LoginEntry::Entering;
                self.portal_events.push(PortalSpaceEvent::LoginEntering);
            }
            (false, LoginEntry::Entering) => {
                self.login_entry = LoginEntry::Idle;
                self.portal_events.push(PortalSpaceEvent::LoginWithdrawn);
            }
            (false, LoginEntry::Held { .. } | LoginEntry::Passed) => {
                self.login_entry = LoginEntry::Idle;
            }
            _ => {}
        }
    }

    /// The player's object is in the world at `destination_cell` and login
    /// complete waits for the attached window: the login reveal begins and
    /// the window hears the destination. False when a teleport already
    /// replaced the login presentation (login complete then waits for its
    /// arrival).
    pub fn hold_login(&mut self, destination_cell: u32) -> bool {
        if self.login_entry == LoginEntry::Passed {
            return self.login_release_on_arrival;
        }
        let generation = self.transit.begin_login_reveal(destination_cell);
        self.login_entry = LoginEntry::Held {
            generation,
            destination_cell,
        };
        self.portal_events.push(PortalSpaceEvent::LoginDestination {
            generation,
            destination_cell,
        });
        self.forward();
        true
    }

    /// Login complete waits for the window's login presentation.
    #[must_use]
    pub fn is_login_held(&self) -> bool {
        matches!(self.login_entry, LoginEntry::Held { .. }) || self.login_release_on_arrival
    }

    /// The character's entry is being presented (a log out is refused
    /// meanwhile, as OpenAC refuses it while its login tunnel runs).
    #[must_use]
    pub fn is_login_presenting(&self) -> bool {
        matches!(
            self.login_entry,
            LoginEntry::Entering | LoginEntry::Held { .. }
        )
    }

    /// The held login complete may go out now; true once per release.
    pub fn take_login_release(&mut self) -> bool {
        std::mem::take(&mut self.login_release)
    }

    /// The attached window showed the login destination (its presentation
    /// ended): the reveal completes with the readiness the window found and
    /// login complete may go out. An answer for another reveal changes
    /// nothing.
    fn login_shown(&mut self, readiness: DestinationReadiness) {
        let LoginEntry::Held {
            generation,
            destination_cell,
        } = self.login_entry
        else {
            return;
        };
        if readiness.generation != generation || readiness.destination_cell != destination_cell {
            return;
        }
        self.complete_login(readiness);
    }

    /// The login reveal completes: the destination's readiness, the
    /// materialization, the world seen again and the completion are
    /// acknowledged together, and login complete is released.
    fn complete_login(&mut self, readiness: DestinationReadiness) {
        let generation = readiness.generation;
        let completed = self.transit.acknowledge_destination_readiness(readiness)
            && self.transit.acknowledge_login_materialized(generation)
            && self.transit.acknowledge_world_viewport_visible(generation)
            && self.transit.complete(generation);
        if !completed {
            self.transit.cancel(generation);
        }
        self.login_entry = LoginEntry::Passed;
        self.login_release = true;
    }

    /// The attached window has held a reveal (`generation`) for `waited`
    /// without its destination being ready: from five seconds on the
    /// reveal records that its wait cue showed.
    fn reveal_waited(&mut self, generation: u64, waited: Duration) {
        self.transit.observe_wait(generation, waited);
    }

    /// The portal arrival waiting for the attached window, if any.
    #[must_use]
    pub fn held_arrival(&self) -> Option<PortalSpaceEvent> {
        self.held_arrival.map(HeldArrival::event)
    }

    /// Portal space since the last call, oldest first (an attached window
    /// shows it).
    pub fn take_portal_events(&mut self) -> Vec<PortalSpaceEvent> {
        std::mem::take(&mut self.portal_events)
    }

    /// The portal space a window attaching now joins: the start of the
    /// teleport under way, if any, and its destination when the arrival is
    /// held; the character's entry under way (and its destination when
    /// login complete is held); the log out's portal space once its hold
    /// ended.
    #[must_use]
    pub fn portal_space_now(&self) -> Vec<PortalSpaceEvent> {
        if self.transit.logout_stage() == LogoutStage::PresentationActive {
            return vec![PortalSpaceEvent::LogoutStarted];
        }
        if !self.transit.is_teleport_active() {
            return match self.login_entry {
                LoginEntry::Entering => vec![PortalSpaceEvent::LoginEntering],
                LoginEntry::Held {
                    generation,
                    destination_cell,
                } => vec![
                    PortalSpaceEvent::LoginEntering,
                    PortalSpaceEvent::LoginDestination {
                        generation,
                        destination_cell,
                    },
                ],
                LoginEntry::Idle | LoginEntry::Passed => Vec::new(),
            };
        }
        let entered = PortalSpaceEvent::Entered {
            teleport_sequence: self.transit.active_teleport_sequence(),
        };
        std::iter::once(entered)
            .chain(self.held_arrival())
            .collect()
    }

    /// Whether the player is a player killer (the longer log-out hold).
    #[must_use]
    pub fn is_player_killer(&self) -> bool {
        let flags = self
            .world
            .player_guid()
            .and_then(|guid| self.world.item(guid))
            .and_then(|item| item.public_weenie_bitfield)
            .unwrap_or_default();
        flags.contains(PublicWeenieFlags::PLAYER_KILLER)
            || flags.contains(PublicWeenieFlags::PLAYER_KILLER_LITE)
    }

    /// A generation ended (log off to selection, reconnect, stop): the
    /// owners forget the server's state, the world empties and portal space
    /// resets.
    pub fn end_generation(&mut self, wall_unix_ms: i64) {
        self.login_commands.cancel(self.generation);
        self.options_seeder.reset_session();
        self.login_complete_seen = false;
        self.left_world(wall_unix_ms);
        self.world.reset_session();
        self.liveness.clear();
        self.transit.reset_session();
        self.held_arrival = None;
        self.destination_hold = None;
        self.destination_hold_began = false;
        self.login_entry = LoginEntry::Idle;
        self.login_release = false;
        self.login_release_on_arrival = false;
        self.portal_events.clear();
        self.last_tick = None;
        self.forward();
    }

    /// The inventory's auto-wield follows the combat owner: a stance the
    /// player asked for stands its settlement down, and each mode change
    /// moves a wield made in combat along.
    fn follow_combat(&mut self, outbox: &mut Outbox, active: bool, clocks: Clocks) {
        let (explicit, modes) = self.gameplay.take_wield_signals();
        if explicit {
            self.inventory.notify_explicit_combat_mode_request();
        }
        if modes.is_empty() {
            return;
        }
        let mut ctx = ItemContext {
            world: &mut self.world,
            outbox,
            options: &self.options,
            active,
            now_ms: clocks.session_ms(),
            in_peace: self.gameplay.combat_mode() == CombatMode::NON_COMBAT,
            approach: None,
        };
        for mode in modes {
            self.inventory.on_combat_mode_changed(mode, &mut ctx);
        }
    }

    /// Give the gameplay owners the game tables and the account name (spell
    /// formulas are personalised per account), the player's numbers the
    /// spell table and the skill formulas, and the quest tracker the
    /// contract table.
    pub fn install_tables(&mut self, tables: Arc<GameTables>, account: &str) {
        let skills = LiveSkillCreditResolver::new(tables.skills().clone());
        let resolver: SkillFormulaBonusResolver =
            Arc::new(move |skill, class, attributes| skills.resolve(skill, class, attributes));
        let resolver = (!tables.skills().is_empty()).then_some(resolver);
        self.world
            .install_player_rules(Arc::clone(tables.spells()), resolver);
        self.communication
            .install_contract_catalog(Arc::clone(tables.contracts()));
        self.communication
            .set_filter_language_patterns(tables.chat_taboo().patterns());
        self.communication
            .install_chat_poses(Arc::clone(tables.chat_poses()));
        self.inventory
            .install_trade_notes(Arc::clone(tables.trade_notes()));
        self.gameplay = Gameplay::new(tables, account.to_owned());
    }

    /// Give the movement owner the dats it loads collision and motion from.
    pub fn install_content(&mut self, content: Arc<crate::movement::WorldContent>) {
        self.movement.install_content(content);
    }

    /// Once per pass, before the network pass: the local player's input
    /// moves its body, its movement event and jump go out, other bodies
    /// advance, and the positions reach the world.
    pub fn advance_movement(&mut self, outbox: &mut Outbox, in_world: bool, clocks: Clocks) {
        self.wall_unix_ms = clocks.wall_unix_ms;
        if in_world {
            // A walk moves the body through the scripted moves the input
            // source reads next.
            let Self {
                world,
                movement,
                inventory,
                gameplay,
                transit,
                navigation,
                ..
            } = self;
            let mut cx = navigation_context(
                world, movement, inventory, gameplay, transit, outbox, in_world, clocks,
            );
            navigation.tick(&mut cx);
            self.use_doors(outbox, in_world, clocks);
            // OpenAC `RunAsDefaultMovementSource`: the live ToggleRun option.
            self.movement
                .set_run_as_default(self.options.get(option_id::TOGGLE_RUN));
            self.movement
                .advance_before_network(&mut self.world, outbox, clocks.session);
            self.drive_approaches(outbox, in_world, clocks);
            self.enforce_vendor_range(outbox, in_world, clocks);
            self.enforce_house_purchase_range();
        }
        self.forward();
    }

    /// After the character's step: the walks to objects that ended send or
    /// release what was armed for them (OpenAC drives this from the same
    /// per-frame step on every host).
    fn drive_approaches(&mut self, outbox: &mut Outbox, active: bool, clocks: Clocks) {
        let mut ctx = ItemContext {
            world: &mut self.world,
            outbox,
            options: &self.options,
            active,
            now_ms: clocks.session_ms(),
            in_peace: self.gameplay.combat_mode() == CombatMode::NON_COMBAT,
            approach: Some(&mut self.movement),
        };
        self.inventory.drive_approaches(&mut ctx);
        self.inventory.observe(&mut ctx);
    }

    /// The doors a walk asked to use are used as a player's click uses
    /// them, which walks the character into the door's use range first.
    fn use_doors(&mut self, outbox: &mut Outbox, active: bool, clocks: Clocks) {
        for door in self.navigation.take_door_uses() {
            let mut ctx = ItemContext {
                world: &mut self.world,
                outbox,
                options: &self.options,
                active,
                now_ms: clocks.session_ms(),
                in_peace: self.gameplay.combat_mode() == CombatMode::NON_COMBAT,
                approach: Some(&mut self.movement),
            };
            self.inventory.execute(
                &crate::inventory::ItemCommand::Use(crate::inventory::ObjectRef::Guid(door)),
                &mut ctx,
            );
        }
    }

    /// A typed line the runtime's own verbs (`/nav`, `/motor`) or a plugin's
    /// verb answer, after the client's command catalog and before the rest
    /// of the chat route.
    fn runtime_verb(
        &mut self,
        line: &str,
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
    ) -> bool {
        if crate::communication::is_client_command(line) {
            return false;
        }
        if self.plugin_lines.claim(line) {
            return true;
        }
        if is_status_verb(line) {
            let text = self.status_text();
            self.communication.show_text(
                clocks.wall_unix_ms,
                &text,
                ac_core::chat::LogTextType::Default,
            );
            return true;
        }
        let in_world = active && self.world.player_guid().is_some();
        let Self {
            world,
            movement,
            inventory,
            gameplay,
            transit,
            navigation,
            ..
        } = self;
        let mut cx = navigation_context(
            world, movement, inventory, gameplay, transit, outbox, in_world, clocks,
        );
        navigation.chat_command(line, &mut cx)
    }

    /// Runs `act` over navigation's walks and moves as `owner` (a plugin's
    /// id) asks for them.
    pub(crate) fn navigate<R>(
        &mut self,
        owner: &str,
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
        act: impl FnOnce(&mut crate::navigation::RuntimeSurface<'_, '_>) -> R,
    ) -> R {
        let in_world = active && self.world.player_guid().is_some();
        let Self {
            world,
            movement,
            inventory,
            gameplay,
            transit,
            navigation,
            ..
        } = self;
        let mut cx = navigation_context(
            world, movement, inventory, gameplay, transit, outbox, in_world, clocks,
        );
        navigation.as_owner(owner, &mut cx, act)
    }

    /// Once per pass: the chat-display options are read as they stand, and
    /// the options block saves itself when its time is due.
    pub fn tick(&mut self, outbox: &mut Outbox, in_world: bool, clocks: Clocks) {
        self.wall_unix_ms = clocks.wall_unix_ms;
        let elapsed = self
            .last_tick
            .map_or(Duration::ZERO, |last| clocks.session.saturating_sub(last));
        self.last_tick = Some(clocks.session);
        self.frame_clock.advance(
            elapsed.as_secs_f64(),
            self.transit.is_world_simulation_available(),
        );
        // A held portal completion is retried once a pass (OpenAC
        // `PumpPortalCompletion`).
        self.advance_portal_completion(outbox);
        if in_world {
            self.movement.after_network(&self.world, outbox);
        }
        let selected = self.gameplay.selected_presence(&self.world);
        if in_world {
            self.expire_out_of_range();
        }
        self.communication
            .set_display_timestamps(self.options.get(option_id::DISPLAY_TIME_STAMPS));
        self.communication
            .set_filter_language(self.options.get(option_id::FILTER_LANGUAGE));
        self.communication.tick(clocks.session.as_secs_f64());
        if self.transit.advance_logout_hold(elapsed.as_secs_f64()) {
            // The hold ended (the server's log-out motion has played): a
            // window warps the world away into portal space.
            self.portal_events.push(PortalSpaceEvent::LogoutStarted);
        }
        if in_world {
            self.echo_spellbook();
            self.options.try_flush_if_auto_save_due(clocks.wall(), |o| {
                outbox.send_action(|seq| o.build_block(seq));
            });
        }
        let teleporting =
            self.transit.is_teleport_active() || self.transit.has_pending_teleport_start();
        let mut ctx = GameplayContext {
            world: &mut self.world,
            outbox,
            options: &self.options,
            active: in_world,
            clocks,
            body: Some(&mut self.movement),
            teleporting,
            opened_corpses: Some(self.inventory.external_containers()),
        };
        // An object forgotten out of range leaves the selection too.
        self.gameplay.follow_selected_object(selected, &mut ctx);
        self.gameplay.tick(&mut ctx);
        self.follow_combat(outbox, in_world, clocks);
        self.forward();
    }

    /// Objects out of range for 25 seconds are forgotten, as the server
    /// forgets them (checked once a second on the world's simulation clock,
    /// which stands still while no world is up, as OpenAC's liveness).
    fn expire_out_of_range(&mut self) {
        let mark = self.world.change_count();
        let trade = self.inventory.trade();
        let kept = crate::world::liveness::KeptPlaces {
            ground_container: self.inventory.external_containers().current_container_id(),
            trade_partner: if trade.open { trade.partner_guid } else { 0 },
            partner_items: &trade.partner_items,
        };
        let removed = self.liveness.pass(
            self.frame_clock.simulation_seconds(),
            &mut self.world,
            self.movement.indoor_cell(),
            kept,
        );
        if removed.is_empty() {
            return;
        }
        for candidate in removed {
            self.gameplay
                .forget_expired(candidate.guid, candidate.generation);
        }
        self.movement.observe_expiry(&mut self.world, mark);
    }

    /// The destruction queue follows what the owners did since the last
    /// call (its deadlines count from the last pass).
    fn follow_destruction_queue(&mut self) {
        let trade = self.inventory.trade();
        let kept = crate::world::liveness::KeptPlaces {
            ground_container: self.inventory.external_containers().current_container_id(),
            trade_partner: if trade.open { trade.partner_guid } else { 0 },
            partner_items: &trade.partner_items,
        };
        self.liveness.follow(&mut self.world, kept);
    }

    /// The session time of the last pass, seconds (0 before the first).
    #[must_use]
    pub fn session_seconds(&self) -> f64 {
        self.last_tick.unwrap_or_default().as_secs_f64()
    }

    /// What an attached window shows of the player: vitals, selection and
    /// the options the always-shown panels read.
    #[must_use]
    pub fn player_view(&self) -> PlayerView {
        PlayerView {
            coordinates_on_radar: self.options.get(option_id::COORDINATES_ON_RADAR),
            ui_locked: self.options.get(option_id::LOCK_UI),
            stay_in_chat_mode: self.options.get(option_id::STAY_IN_CHAT_MODE),
            disable_most_weather_effects: self.options.get(option_id::DISABLE_MOST_WEATHER_EFFECTS),
            options1: self.options.options1(),
            options2: self.options.options2(),
            options_seeded: self.options.has_server_seed(),
            jump_charge: self
                .movement
                .controller()
                .map(|c| c.jump_charge())
                .filter(|j| j.is_charging)
                .map(|j| j.power),
            combat_camera_target: self
                .gameplay
                .combat_camera_target(&self.world, self.options.get(option_id::VIEW_COMBAT_TARGET)),
            attack: self.gameplay.attack_bar(
                &self.world,
                &self.options,
                Some(&self.movement),
                self.session_seconds(),
            ),
            ..self.world.player_view(self.gameplay.selection().selected())
        }
    }

    /// Send the options block if a change waits for it (before log off, or
    /// when asked).
    pub fn save_options(&mut self, outbox: &mut Outbox) {
        self.echo_spellbook();
        self.options.try_flush(|o| {
            outbox.send_action(|seq| o.build_block(seq));
        });
    }

    /// A block about to go out repeats the live spellbook's bars, desired
    /// components and filter.
    fn echo_spellbook(&mut self) {
        if !self.options.is_dirty() {
            return;
        }
        if let Some(book) = self.world.spellbook() {
            self.options.echo_spellbook(book);
        }
        self.options.echo_shortcuts(self.inventory.shortcuts());
    }

    /// The character left the world: the journal is saved, every piece of
    /// server-owned state is forgotten and the plugins' labels and markers
    /// over objects come down (what they draw at fixed positions stays).
    pub fn left_world(&mut self, wall_unix_ms: i64) {
        self.wall_unix_ms = wall_unix_ms;
        self.plugin_ui.clear_labels();
        self.plugin_ui.clear_object_markers();
        self.communication.left_world(wall_unix_ms);
        self.options.reset_session();
        self.inventory.reset_session(&mut self.world);
        self.gameplay.left_world(&mut self.world);
        self.movement.left_world();
        self.navigation.left_world();
        self.world.take_table_events();
        self.forward();
    }

    /// Run a host command that a state owner answers; any other command
    /// (attach, detach, shutdown, game time) comes back for the caller.
    pub fn command(
        &mut self,
        command: SessionCommand,
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
    ) -> Option<SessionCommand> {
        let wall = clocks.wall_unix_ms;
        self.wall_unix_ms = wall;
        match command {
            SessionCommand::Item(ItemCommand::GiveSelectedToPrevious { amount }) => {
                self.give_selection_to_previous(amount, outbox, active, clocks);
            }
            SessionCommand::Item(ItemCommand::DropSelected { amount }) => {
                self.drop_selection(amount, outbox, active, clocks);
            }
            SessionCommand::Item(item) => {
                let item = self.with_selection(item);
                self.run_item(&item, outbox, active, clocks);
            }
            SessionCommand::Gameplay(GameplayCommand::Escape) => {
                return self.escape(outbox, active, clocks);
            }
            // The self key while a use or examine mode waits for a target
            // offers the character to the mode.
            SessionCommand::Gameplay(GameplayCommand::SelectSelf)
                if self.inventory.interaction_mode() != crate::inventory::InteractionMode::None
                    && let Some(player) = self.world.player_guid() =>
            {
                let offer = ItemCommand::OfferTarget(crate::inventory::ObjectRef::Guid(player));
                return self.command(SessionCommand::Item(offer), outbox, active, clocks);
            }
            // The use-corpse keys select a corpse, then use what is selected.
            SessionCommand::Gameplay(GameplayCommand::UseUnopenedCorpse(direction)) => {
                let cycle = GameplayCommand::CycleSelection {
                    kind: crate::gameplay::selection_cycle::SelectionCycleKind::UnopenedCorpse,
                    direction,
                };
                self.command(SessionCommand::Gameplay(cycle), outbox, active, clocks);
                if self.gameplay.selection().selected().is_some() {
                    let item = SessionCommand::Item(ItemCommand::UseSelected);
                    return self.command(item, outbox, active, clocks);
                }
            }
            SessionCommand::Gameplay(command) => {
                let teleporting =
                    self.transit.is_teleport_active() || self.transit.has_pending_teleport_start();
                let mut ctx = GameplayContext {
                    world: &mut self.world,
                    outbox,
                    options: &self.options,
                    active,
                    clocks,
                    body: Some(&mut self.movement),
                    teleporting,
                    opened_corpses: Some(self.inventory.external_containers()),
                };
                self.gameplay.execute(command, &mut ctx);
                for _ in 0..self.gameplay.take_busy() {
                    self.inventory.increment_cast_busy();
                }
                self.follow_combat(outbox, active, clocks);
            }
            SessionCommand::Movement(command) => {
                self.movement.command(command, &self.world, outbox);
            }
            SessionCommand::Character(request) => {
                if active {
                    outbox.send_action(|seq| request.build(seq));
                } else {
                    self.host_events
                        .push(HostEvent::CommandRefused("Not in the world.".to_owned()));
                }
            }
            SessionCommand::Ping => {
                if active {
                    self.ping.requested(clocks.session);
                    outbox.send_action(ac_proto::social::build_ping_request);
                } else {
                    self.host_events
                        .push(HostEvent::CommandRefused("Not in the world.".to_owned()));
                }
            }
            // The connection's numbers are the runner's.
            SessionCommand::ShowLinkStatus => return Some(SessionCommand::ShowLinkStatus),
            SessionCommand::Detach => {
                self.movement.release_keys();
                // The next window says again whether it draws the world (a
                // window attaching may say so before its attach arrives).
                self.window_presents_world = false;
                return Some(SessionCommand::Detach);
            }
            SessionCommand::ListInventory => {
                let mut view = inventory_view(&self.world);
                let ground = self.inventory.external_containers().current_container_id();
                if ground != 0 {
                    view.ground = crate::inventory::ground_view(&self.world, ground);
                }
                self.host_events.push(HostEvent::Inventory(view));
            }
            SessionCommand::ShowCharacterOptions => {
                let values = OPTIONS
                    .iter()
                    .map(|entry| (entry.name, self.options.get(entry.id)))
                    .collect();
                self.host_events.push(HostEvent::CharacterOptions(values));
            }
            SessionCommand::SaveCharacterOptions => self.save_options(outbox),
            SessionCommand::PluginUi(input) => self.plugin_ui.input(input),
            SessionCommand::DestinationShown(shown) => self.destination_shown(shown, outbox),
            SessionCommand::LoginShown(readiness) => self.login_shown(readiness),
            SessionCommand::RevealWaited { generation, waited } => {
                self.reveal_waited(generation, waited);
            }
            SessionCommand::ShowInterfaceText(text) => {
                self.communication.show_interface_text(wall, &text);
            }
            SessionCommand::ShowText {
                text,
                log_text_type,
            } => self.communication.show_text(wall, &text, log_text_type),
            SessionCommand::WindowMinimized(minimized) => {
                self.plugin_services.window_minimized = minimized;
                // A minimized window draws nothing: what it held completes
                // as it does without a window (OpenAC's update loop runs
                // the presentation on while minimized and completes it).
                if minimized {
                    self.release_holds(outbox);
                }
            }
            SessionCommand::PresentsWorld(presents) => self.set_window_presents_world(presents),
            SessionCommand::ChatLinkClicked {
                east_west,
                north_south,
                text,
            } => self
                .plugin_services
                .chat
                .link_clicked(east_west, north_south, text),
            SessionCommand::ListObjects => {
                self.host_events
                    .push(HostEvent::Objects(list_objects(&self.world)));
            }
            SessionCommand::ListContracts => {
                self.host_events.push(HostEvent::Contracts(
                    self.communication.contract_summaries(wall),
                ));
            }
            SessionCommand::ShowJournal => {
                let journal = self.communication.journal();
                self.host_events.push(HostEvent::Journal {
                    pages: journal.pages().to_vec(),
                    current_page: journal.current_page(),
                });
            }
            SessionCommand::Journal(change) => self.communication.journal_command(&change, wall),
            SessionCommand::ChatEntry(command) => {
                self.chat_entry_command(command, outbox, active, clocks);
            }
            SessionCommand::DismissGhost(guid) => {
                if !self.dismiss_ghost(guid, outbox, active, clocks) {
                    self.host_events.push(HostEvent::CommandRefused(format!(
                        "0x{guid:08X} is not an object that can be let go of."
                    )));
                }
            }
            SessionCommand::ListCorpses(maximum_distance) => {
                self.host_events
                    .push(HostEvent::Corpses(crate::gameplay::corpses(
                        &self.world,
                        self.inventory.external_containers(),
                        maximum_distance,
                    )));
            }
            SessionCommand::Chat(line) if self.runtime_verb(&line, outbox, active, clocks) => {}
            SessionCommand::ChatInput {
                text,
                channel,
                tell_target,
                tell_target_guid,
            } if {
                // A line the plugins' interceptors send on keeps the
                // entry's talk focus.
                self.plugin_lines
                    .set_origin(crate::plugins::LineOrigin::Entry {
                        channel,
                        tell_target: tell_target.clone(),
                        tell_target_guid,
                    });
                let taken = self.runtime_verb(&text, outbox, active, clocks);
                self.plugin_lines
                    .set_origin(crate::plugins::LineOrigin::Chat);
                taken
            } => {}
            other => {
                let rest = self.communication_command(other, outbox, active, wall);
                self.run_local_requests(outbox, active, clocks);
                return rest;
            }
        }
        self.forward();
        None
    }

    /// Let go of an object the client still believes in although the
    /// server stopped talking about it: it leaves exactly as a server
    /// delete of its current incarnation would take it (never the
    /// character). True when the client no longer knows it.
    pub fn dismiss_ghost(
        &mut self,
        guid: u32,
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
    ) -> bool {
        if guid == 0 || self.world.player_guid() == Some(guid) {
            return false;
        }
        let Some(incarnation) = self.world.object(guid).map(|o| o.key().incarnation) else {
            return false;
        };
        let delete = Inbound::DeleteObject(ac_proto::DeleteObject {
            guid,
            instance_sequence: incarnation,
        });
        self.apply(&delete, outbox, active, clocks);
        self.world.object(guid).is_none()
    }

    /// The Escape key (OpenAC `GameplayInputActionRouter`'s escape
    /// movement, `SelectionInteractionController` and
    /// `GameplayInputCommandController.HandleEscape`, in the order the
    /// router asks them): a jump being charged ends; else a moving body or
    /// a repeating attack stops completely (the attack's repeat ends);
    /// else a use or examine mode is left; else the selection is cleared;
    /// else the window opens the options window's gameplay page.
    fn escape(
        &mut self,
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
    ) -> Option<SessionCommand> {
        use crate::gameplay::{CombatAttackCommand, InputActivation};
        if self.movement.jump_charging() {
            self.movement.finish_jump();
            return None;
        }
        let repeating = self.gameplay.attack().repeat_attack_in_progress();
        if !self.movement.is_standing_still() || repeating {
            self.movement.stop_completely(&self.world);
            if repeating {
                let abort = GameplayCommand::AttackInput(
                    CombatAttackCommand::AbortForMovement,
                    InputActivation::Press,
                );
                return self.command(SessionCommand::Gameplay(abort), outbox, active, clocks);
            }
            return None;
        }
        if self.inventory.interaction_mode() != crate::inventory::InteractionMode::None {
            let cancel = SessionCommand::Item(ItemCommand::CancelTargetMode);
            return self.command(cancel, outbox, active, clocks);
        }
        if self.gameplay.selection().selected().is_some() {
            let clear = SessionCommand::Gameplay(GameplayCommand::ClearSelection);
            return self.command(clear, outbox, active, clocks);
        }
        self.communication
            .request_window(crate::communication::WindowRequest::GameplayOptionsPage);
        None
    }

    /// The Use, Examine, Pick-up and place-in-pack keys act on the
    /// selection: the item request for the selected object, or (nothing
    /// selected) the mode that waits for one. What may not be picked up (a
    /// creature, something stuck or another's) is refused by the pickup
    /// itself, in the game's words; the Pick-up key asks before a pack is
    /// chosen.
    fn with_selection(&self, item: ItemCommand) -> ItemCommand {
        use crate::inventory::ObjectRef;
        let selected = self.gameplay.selection().selected();
        match (item, selected) {
            (ItemCommand::UseSelected, Some(guid)) => ItemCommand::Use(ObjectRef::Guid(guid)),
            (ItemCommand::ExamineSelected, Some(guid)) => {
                ItemCommand::Appraise(ObjectRef::Guid(guid))
            }
            (ItemCommand::PickUpSelected, Some(guid)) => ItemCommand::PickUp(ObjectRef::Guid(guid)),
            (ItemCommand::PlaceSelectedInPack, Some(guid)) => {
                ItemCommand::PlaceInPack(ObjectRef::Guid(guid))
            }
            (ItemCommand::PickUpSelectedToMainPack, Some(guid)) => {
                ItemCommand::PickUpToMainPack(ObjectRef::Guid(guid))
            }
            (other, _) => other,
        }
    }

    /// The give key: the selection goes to the object selected before it,
    /// as a drop on it would, when that is a creature other than the
    /// selection itself (else the player is told to pick one); once placed,
    /// that creature is selected.
    fn give_selection_to_previous(
        &mut self,
        amount: u32,
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
    ) {
        use crate::inventory::{ObjectRef, pickup};
        let selection = self.gameplay.selection();
        let (selected, previous) = (selection.selected(), selection.previous());
        let pair = selected
            .zip(previous)
            .filter(|(s, t)| s != t && pickup::is_creature(&self.world, *t));
        let Some((selected, target)) = pair else {
            self.communication
                .show_interface_text(clocks.wall_unix_ms, pickup::GIVE_NEEDS_A_CREATURE);
            return;
        };
        let place = ItemCommand::Place {
            item: ObjectRef::Guid(selected),
            target: Some(ObjectRef::Guid(target)),
            amount,
        };
        if self.run_item(&place, outbox, active, clocks) {
            let select = GameplayCommand::Select(ObjectRef::Guid(target));
            self.command(SessionCommand::Gameplay(select), outbox, active, clocks);
        }
    }

    /// The drop key: the selection goes to the ground when the player
    /// carries it (else the player is told to pick it up first); nothing
    /// with nothing selected.
    fn drop_selection(&mut self, amount: u32, outbox: &mut Outbox, active: bool, clocks: Clocks) {
        use crate::inventory::{ObjectRef, pickup};
        let Some(selected) = self.gameplay.selection().selected() else {
            return;
        };
        let player = self.world.player_guid().unwrap_or(0);
        if !self.world.items().is_owned_by_object(selected, player) {
            self.communication
                .show_interface_text(clocks.wall_unix_ms, pickup::DROP_NEEDS_IT_CARRIED);
            return;
        }
        let place = ItemCommand::Place {
            item: ObjectRef::Guid(selected),
            target: None,
            amount,
        };
        self.run_item(&place, outbox, active, clocks);
    }

    /// Run one item request as a host command does; whether it went out.
    fn run_item(
        &mut self,
        item: &ItemCommand,
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
    ) -> bool {
        let mut ctx = ItemContext {
            world: &mut self.world,
            outbox,
            options: &self.options,
            active,
            now_ms: clocks.session_ms(),
            in_peace: self.gameplay.combat_mode() == CombatMode::NON_COMBAT,
            approach: Some(&mut self.movement),
        };
        let sent = self.inventory.execute(item, &mut ctx);
        self.inventory.observe(&mut ctx);
        self.begin_games(outbox, active, clocks);
        self.select_merged_stacks(outbox, active, clocks);
        sent
    }

    /// Each merge sent selects the stack it joins (a pickup that joins a
    /// carried stack shows that stack and its count, not the one picked
    /// up), here so a session with no window follows it as one with a
    /// window does.
    fn select_merged_stacks(&mut self, outbox: &mut Outbox, active: bool, clocks: Clocks) {
        use crate::inventory::ObjectRef;
        for target in self.inventory.take_merged_into() {
            let select = GameplayCommand::Select(ObjectRef::Guid(target));
            self.command(SessionCommand::Gameplay(select), outbox, active, clocks);
        }
    }

    /// The game boards the item owner was asked to use begin their games
    /// here, so a session with no window joins as one with a window does;
    /// a window then shows the game.
    fn begin_games(&mut self, outbox: &mut Outbox, active: bool, clocks: Clocks) {
        for board in self.inventory.take_begun_games() {
            let teleporting =
                self.transit.is_teleport_active() || self.transit.has_pending_teleport_start();
            let mut ctx = GameplayContext {
                world: &mut self.world,
                outbox: &mut *outbox,
                options: &self.options,
                active,
                clocks,
                body: Some(&mut self.movement),
                teleporting,
                opened_corpses: Some(self.inventory.external_containers()),
            };
            self.gameplay.begin_game(board, &mut ctx);
            self.host_events.push(HostEvent::BeginGame(board));
        }
    }

    /// `/status`: the session's generation, where it is in its life, and
    /// where the character stands.
    #[must_use]
    pub fn status_text(&self) -> String {
        let position = self.movement.controller().map_or_else(
            || "unknown".to_owned(),
            |controller| {
                let at = crate::movement::cell_position_of(controller);
                let o = at.frame.origin;
                format!(
                    "cell=0x{:08X} local=({:.2},{:.2},{:.2})",
                    at.obj_cell_id, o.x, o.y, o.z
                )
            },
        );
        format!(
            "generation={} state={:?} position={position}",
            self.generation.0, self.lifecycle_state
        )
    }

    /// What client commands read about the player now.
    fn player_facts_now(&self) -> PlayerFacts {
        let mut player = player_facts(
            &self.world,
            &self.options,
            self.inventory.vendor().vendor_id() != 0,
            self.window_attached,
        );
        // `/loc` reads the body where the movement owner has it.
        player.current_position = self.movement.controller().map(|controller| {
            let at = crate::movement::cell_position_of(controller);
            let (origin, q) = (at.frame.origin, at.frame.orientation);
            (
                at.obj_cell_id,
                [origin.x, origin.y, origin.z],
                [q.w, q.x, q.y, q.z],
            )
        });
        player
    }

    /// Type one line into the chat box (the chat entry's, or a login
    /// command): the runtime's own verbs first, then the chat box's
    /// routing. A line the plugins' interceptors hold is sent on later with
    /// `origin`'s talk focus. How it was routed.
    fn submit_chat_line(
        &mut self,
        line: &str,
        defaults: Defaults<'_>,
        origin: crate::plugins::LineOrigin,
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
    ) -> crate::communication::SubmitOutcome {
        self.plugin_lines.set_origin(origin);
        let taken = self.runtime_verb(line, outbox, active, clocks);
        self.plugin_lines
            .set_origin(crate::plugins::LineOrigin::Chat);
        if taken {
            self.forward();
            return crate::communication::SubmitOutcome::ClientHandled;
        }
        let player = self.player_facts_now();
        let mut out = Outbound {
            outbox,
            player,
            now_unix_ms: clocks.wall_unix_ms,
            active,
            options: &mut self.options,
        };
        let outcome = self.communication.submit(line, defaults, &mut out);
        self.run_local_requests(outbox, active, clocks);
        self.forward();
        outcome
    }

    /// The chat entry every front end types into: a line sent goes with
    /// the entry's talk focus, is remembered, and clears the draft.
    fn chat_entry_command(
        &mut self,
        command: crate::communication::ChatEntryCommand,
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
    ) {
        use crate::communication::ChatEntryCommand as C;
        let mut entry = std::mem::take(self.communication.chat_entry_mut());
        match command {
            C::Submit(text) => {
                entry.submit(text.as_deref(), |line, defaults| {
                    // The line keeps the entry's talk focus through the
                    // plugins' interceptors (a tell stays a tell).
                    let origin = crate::plugins::LineOrigin::Entry {
                        channel: defaults.channel,
                        tell_target: defaults.tell_target.map(str::to_owned),
                        tell_target_guid: defaults.tell_target_guid,
                    };
                    self.submit_chat_line(line, defaults, origin, outbox, active, clocks)
                });
            }
            C::SetDraft(text) => entry.type_draft(&text),
            C::Compose(text) => {
                entry.compose(&text);
            }
            C::SetChannel(channel) => entry.set_channel(channel),
            C::SetTellTarget { name, guid } => entry.set_tell_target(&name, guid),
            C::RecallPrevious => {
                entry.recall_previous();
            }
            C::RecallNext => {
                entry.recall_next();
            }
            C::ResetRecall => entry.reset_recall(),
            C::Clear => entry.clear(),
        }
        *self.communication.chat_entry_mut() = entry;
    }

    /// The session document's login commands and declared character
    /// options (none by default).
    pub fn install_login_automation(
        &mut self,
        declared_options: &[(u32, bool)],
        login_commands: Vec<String>,
        login_command_delay: Duration,
    ) {
        self.options_seeder = crate::character::OptionsSeeder::new(declared_options);
        self.login_commands =
            crate::communication::LoginCommandSequence::new(login_commands, login_command_delay);
    }

    /// Once a pass: the declared options go onto the character once login
    /// complete is out, and the login commands run once the world is
    /// entered, the server is listening (`listening`: login complete sent
    /// in this generation) and the session's plugins, if it has any, have
    /// heard the arrival and been ticked since (`plugins_arrived`).
    pub fn follow_login(
        &mut self,
        outbox: &mut Outbox,
        in_world: bool,
        listening: bool,
        plugins_arrived: bool,
        clocks: Clocks,
    ) {
        let generation = self.generation;
        if listening && !self.login_complete_seen {
            self.login_complete_seen = true;
            let steps = self.options_seeder.note_login_complete_sent(&self.options);
            self.seed_options(&steps, outbox, in_world, clocks);
        }
        let start = in_world && self.login_commands_started != Some(generation);
        if start {
            self.login_commands_started = Some(generation);
        }
        let commands_heard = listening && plugins_arrived;
        let mut sequence = std::mem::take(&mut self.login_commands);
        let mut failures = Vec::new();
        {
            let now = clocks.session;
            let mut submit = |line: &str| {
                self.submit_chat_line(
                    line,
                    Defaults::default(),
                    crate::plugins::LineOrigin::Chat,
                    outbox,
                    in_world,
                    clocks,
                )
            };
            if start {
                sequence.entered_world(
                    generation,
                    commands_heard,
                    &mut || now,
                    &mut submit,
                    &mut failures,
                );
            } else {
                sequence.tick(
                    generation,
                    commands_heard,
                    &mut || now,
                    &mut submit,
                    &mut failures,
                );
            }
        }
        self.login_commands = sequence;
        for failure in failures {
            self.host_events
                .push(HostEvent::LoginCommandFailed(failure));
        }
    }

    /// Carry out the seeder's steps as the options panel would.
    fn seed_options(
        &mut self,
        steps: &[crate::character::SeedStep],
        outbox: &mut Outbox,
        active: bool,
        clocks: Clocks,
    ) {
        for step in steps {
            match *step {
                crate::character::SeedStep::Set { option, value } => {
                    self.command(
                        SessionCommand::SetCharacterOption { option, value },
                        outbox,
                        active,
                        clocks,
                    );
                }
                crate::character::SeedStep::Save => self.save_options(outbox),
            }
        }
    }

    fn communication_command(
        &mut self,
        command: SessionCommand,
        outbox: &mut Outbox,
        active: bool,
        wall: i64,
    ) -> Option<SessionCommand> {
        let player = self.player_facts_now();
        let mut out = Outbound {
            outbox,
            player,
            now_unix_ms: wall,
            active,
            options: &mut self.options,
        };
        let communication = &mut self.communication;
        let chat = |channel, target_name: Option<String>, text: String| SendChat {
            channel,
            target_name,
            text,
            target_guid: 0,
        };
        match command {
            SessionCommand::Chat(line) => {
                communication.submit(&line, Defaults::default(), &mut out);
            }
            SessionCommand::ChatInput {
                text,
                channel,
                tell_target,
                tell_target_guid,
            } => {
                let defaults = Defaults {
                    channel,
                    tell_target: tell_target.as_deref(),
                    tell_target_guid,
                };
                communication.submit(&text, defaults, &mut out);
            }
            SessionCommand::Say(text) => {
                communication.send_chat(chat(ChatChannelKind::Say, None, text), &mut out);
            }
            SessionCommand::Tell { target, text } => {
                communication.send_chat(chat(ChatChannelKind::Tell, Some(target), text), &mut out);
            }
            SessionCommand::Reply(text) => {
                communication.submit(&format!("/r {text}"), Defaults::default(), &mut out);
            }
            SessionCommand::Emote(text) => {
                communication.execute(ClientCommandId::Emote, &text, &mut out);
            }
            SessionCommand::Recall(recall) => {
                let id = match recall {
                    Recall::Lifestone => ClientCommandId::LifestoneRecall,
                    Recall::Marketplace => ClientCommandId::MarketplaceRecall,
                    Recall::House => ClientCommandId::HouseRecall,
                    Recall::Mansion => ClientCommandId::MansionRecall,
                    Recall::AllegianceHometown => ClientCommandId::AllegianceHometown,
                };
                communication.execute(id, "", &mut out);
            }
            SessionCommand::Channel { channel, text } => {
                communication.send_chat(chat(channel, None, text), &mut out);
            }
            SessionCommand::AddFriend(name) => {
                communication.execute(ClientCommandId::FriendsAdd, &name, &mut out);
            }
            SessionCommand::RemoveFriend(name) => {
                communication.execute(ClientCommandId::FriendsRemove, &name, &mut out);
            }
            SessionCommand::Squelch { add, name, account } => {
                let id = if add {
                    ClientCommandId::Squelch
                } else {
                    ClientCommandId::Unsquelch
                };
                let arguments = if account {
                    format!("-account {name}")
                } else {
                    name
                };
                communication.execute(id, &arguments, &mut out);
            }
            SessionCommand::AbandonContract(id) => communication.abandon_contract(id, &mut out),
            SessionCommand::SetCharacterOption { option, value } => {
                communication.set_character_option(option, value, &mut out);
            }
            SessionCommand::AnswerConfirmation {
                confirmation,
                accepted,
            } => communication.answer_confirmation(confirmation, accepted, &mut out),
            SessionCommand::AnswerOpenConfirmation { accepted } => {
                if let Some(confirmation) = communication.open_confirmation() {
                    communication.answer_confirmation(confirmation, accepted, &mut out);
                }
            }
            other => return Some(other),
        }
        self.forward();
        None
    }

    /// Carry out the work client commands handed to other owners
    /// (`/fillcomps`).
    fn run_local_requests(&mut self, outbox: &mut Outbox, active: bool, clocks: Clocks) {
        for request in self.communication.take_local_requests() {
            match request {
                LocalRequest::ConfirmedUse(object) => {
                    let mut ctx = ItemContext {
                        world: &mut self.world,
                        outbox,
                        options: &self.options,
                        active,
                        now_ms: clocks.session_ms(),
                        in_peace: self.gameplay.combat_mode() == CombatMode::NON_COMBAT,
                        approach: None,
                    };
                    self.inventory.execute_confirmed_use(object, &mut ctx);
                }
                LocalRequest::ConfirmedManaStoneDrain { stone, target } => {
                    let mut ctx = ItemContext {
                        world: &mut self.world,
                        outbox,
                        options: &self.options,
                        active,
                        now_ms: clocks.session_ms(),
                        in_peace: self.gameplay.combat_mode() == CombatMode::NON_COMBAT,
                        approach: None,
                    };
                    self.inventory
                        .execute_confirmed_mana_stone_drain(stone, target, &mut ctx);
                }
                LocalRequest::ClearDesiredComponents => {
                    let teleporting = self.transit.is_teleport_active()
                        || self.transit.has_pending_teleport_start();
                    let mut ctx = GameplayContext {
                        world: &mut self.world,
                        outbox,
                        options: &self.options,
                        active,
                        clocks,
                        body: Some(&mut self.movement),
                        teleporting,
                        opened_corpses: Some(self.inventory.external_containers()),
                    };
                    self.gameplay
                        .execute(GameplayCommand::ClearDesiredComponents, &mut ctx);
                }
                LocalRequest::FillComponents {
                    category,
                    maximum_price,
                } => {
                    let tables = Arc::clone(self.gameplay.tables());
                    let mut ctx = ItemContext {
                        world: &mut self.world,
                        outbox,
                        options: &self.options,
                        active,
                        now_ms: clocks.session_ms(),
                        in_peace: self.gameplay.combat_mode() == CombatMode::NON_COMBAT,
                        approach: None,
                    };
                    self.inventory.fill_components(
                        category,
                        maximum_price,
                        |wcid| tables.component_description(wcid),
                        &mut ctx,
                    );
                }
            }
        }
        self.forward();
    }

    /// The character stands on the ground (`Some(false)`: in the air);
    /// `None` without a moving character (OpenAC's `IsGrounded` binding:
    /// not in player mode).
    #[must_use]
    pub fn player_grounded(&self) -> Option<bool> {
        self.movement.controller().map(|c| !c.is_airborne())
    }

    /// Whether the player is within use range of `guid` (the open outside
    /// container's window closes when not): both bodies measured as upright
    /// cylinders against the object's use radius. Without a place for
    /// either, or an object that named no use radius, the server decides
    /// (in range).
    #[must_use]
    pub fn within_use_range(&self, guid: u32) -> bool {
        use crate::inventory::shop::global_position;
        use ac_physics::object_range::{RangeBody, objects_in_range};
        let radius = self.inventory.use_radius(guid);
        if radius <= 0.0 {
            return true;
        }
        let body = |guid: u32| {
            let position = self.world.object(guid)?.position()?;
            // A shape not to hand measures as a point.
            let (radius, height) = self
                .movement
                .body_shape(&self.world, guid)
                .unwrap_or((0.0, 0.0));
            Some(RangeBody {
                position: global_position(position),
                radius,
                height,
            })
        };
        let (Some(player), Some(target)) = (self.world.player_guid().and_then(body), body(guid))
        else {
            return true;
        };
        objects_in_range(player, target, f64::from(radius), true, false)
    }

    /// Once a frame: the open vendor's window closes when the player is out
    /// of its use range (or the vendor left the world).
    fn enforce_vendor_range(&mut self, outbox: &mut Outbox, active: bool, clocks: Clocks) {
        use crate::inventory::shop::{global_position, vendor_stays_open};
        use ac_physics::object_range::RangeBody;
        let vendor = self.inventory.vendor().vendor_id();
        if vendor == 0 {
            return;
        }
        let body = |guid: u32| {
            let position = self.world.object(guid)?.position()?;
            // A shape not to hand measures as a point.
            let (radius, height) = self
                .movement
                .body_shape(&self.world, guid)
                .unwrap_or((0.0, 0.0));
            Some(RangeBody {
                position: global_position(position),
                radius,
                height,
            })
        };
        let player = self.world.player_guid().and_then(body);
        if vendor_stays_open(player, body(vendor), self.inventory.vendor_use_radius()) {
            return;
        }
        let mut ctx = ItemContext {
            world: &mut self.world,
            outbox,
            options: &self.options,
            active,
            now_ms: clocks.session_ms(),
            in_peace: self.gameplay.combat_mode() == CombatMode::NON_COMBAT,
            approach: None,
        };
        self.inventory.close_vendor(&mut ctx);
    }

    /// Once a frame: the slumlord window closes when the player is more
    /// than its fixed reach from the slumlord (measured as the vendor's
    /// is), or the slumlord left the world.
    fn enforce_house_purchase_range(&mut self) {
        use crate::inventory::house_purchase::stays_open;
        use crate::inventory::shop::global_position;
        use ac_physics::object_range::RangeBody;
        let slumlord = self.inventory.house_purchase().slumlord();
        if slumlord == 0 {
            return;
        }
        let body = |guid: u32| {
            let position = self.world.object(guid)?.position()?;
            // A shape not to hand measures as a point.
            let (radius, height) = self
                .movement
                .body_shape(&self.world, guid)
                .unwrap_or((0.0, 0.0));
            Some(RangeBody {
                position: global_position(position),
                radius,
                height,
            })
        };
        let player = self.world.player_guid().and_then(body);
        if !stays_open(player, body(slumlord)) {
            self.inventory.close_house_purchase();
        }
    }

    /// Move what the owners produced into the host events: chat lines and
    /// overlay text from communication, the inventory's notices (worded by
    /// communication, as the client routes them) and its events.
    /// The portal snapshot, when it changed since it was last told.
    fn publish_portal(&mut self) {
        let portal = self.transit.snapshot();
        if portal != self.last_portal {
            self.last_portal = portal;
            self.host_events.push(HostEvent::Portal(portal));
        }
    }

    fn forward(&mut self) {
        self.follow_destruction_queue();
        // A pose in speech plays on the body; a refusal is silent.
        for motion in self.communication.take_pose_motions() {
            self.movement.execute_motion(motion, &self.world);
        }
        self.publish_portal();
        let logout = self.transit.logout_stage();
        if logout != self.last_logout {
            self.last_logout = logout;
            self.host_events.push(HostEvent::Logout(logout));
        }
        let now = self.wall_unix_ms;
        for text in self.movement.drain_interface_text() {
            self.communication.show_interface_text(now, &text);
        }
        for notice in self.inventory.drain_notices() {
            match notice {
                Notice::Interface(text) => self.communication.show_interface_text(now, &text),
                Notice::WeenieError(code) => self.communication.show_weenie_error_code(now, code),
                Notice::Text(text, class) => self.communication.show_text(now, &text, class),
                Notice::Confirm(question) => self.communication.ask(question),
            }
        }
        for notice in self.gameplay.drain_notices() {
            match notice {
                GameplayNotice::Text(text, class) => {
                    self.communication.show_text(now, &text, class)
                }
                GameplayNotice::WeenieError(code) => {
                    self.communication.show_weenie_error_code(now, code);
                }
                GameplayNotice::Combat(combat) => {
                    self.communication.show_combat_notice(now, &combat)
                }
                GameplayNotice::CombatLine(text, kind) => {
                    self.communication.show_combat_line(now, &text, kind);
                }
            }
        }
        for event in self.inventory.drain_events() {
            self.host_events.push(match event {
                InventoryEvent::Appraisal(view) => HostEvent::Appraisal(view),
                InventoryEvent::Vendor(view) => HostEvent::Vendor(view),
                InventoryEvent::Trade(view) => HostEvent::Trade(view),
                InventoryEvent::Refused(text) => HostEvent::CommandRefused(text),
                InventoryEvent::ItemMana(change) => HostEvent::ItemMana(change),
                InventoryEvent::OpenSalvage(tool) => HostEvent::OpenSalvage(tool),
                InventoryEvent::OpenContainedContainer(pack) => {
                    HostEvent::OpenContainedContainer(pack)
                }
            });
        }
        for event in self.movement.drain_events() {
            self.host_events.push(HostEvent::Movement(event));
        }
        for line in self.navigation.drain_replies() {
            // Chat rather than an on-screen notice, so a walk report and its
            // narration can be read back and copied.
            self.communication
                .show_text(now, &line, ac_core::chat::LogTextType::Default);
        }
        for event in self.communication.drain_events() {
            self.host_events.push(match event {
                CommunicationEvent::Line(line) => HostEvent::Chat(line),
                CommunicationEvent::InterfaceText(text) => HostEvent::InterfaceText(text),
                CommunicationEvent::StatusNotice { sequence, text } => {
                    HostEvent::StatusNotice { sequence, text }
                }
                CommunicationEvent::Confirmation(question) => HostEvent::Confirmation(question),
            });
        }
        for event in self.navigation.drain_events() {
            self.host_events.push(HostEvent::Navigation(event));
        }
        for event in self.gameplay.drain_events() {
            self.host_events.push(match event {
                GameplayEvent::Refused(text) => HostEvent::CommandRefused(text),
                other => HostEvent::Gameplay(Box::new(other)),
            });
        }
    }
}

/// `/status` or `@status` (any case, arguments ignored): the client's own
/// verb, answered on every host.
fn is_status_verb(line: &str) -> bool {
    let line = line.trim();
    let Some(rest) = line.strip_prefix('/').or_else(|| line.strip_prefix('@')) else {
        return false;
    };
    let verb = rest.split([' ', '\t']).next().unwrap_or("");
    verb.eq_ignore_ascii_case("status")
}

/// What navigation reads and moves for one call.
#[allow(clippy::too_many_arguments)]
fn navigation_context<'a>(
    world: &'a World,
    movement: &'a mut Movement,
    inventory: &'a mut Inventory,
    gameplay: &Gameplay,
    transit: &WorldTransit,
    outbox: &'a mut Outbox,
    in_world: bool,
    clocks: Clocks,
) -> NavigationContext<'a> {
    let portal = transit.snapshot();
    let attack = gameplay.attack();
    NavigationContext {
        world,
        movement,
        inventory,
        outbox,
        in_world,
        in_portal_space: portal.kind != crate::world::transit::PortalKind::None
            && !portal.completed
            && !portal.cancelled,
        attacking: attack.attack_request_in_progress()
            || attack.attack_server_response_pending()
            || attack.repeat_attack_in_progress(),
        selected: gameplay.selection().selected(),
        now: clocks.session,
        wall_unix_ms: clocks.wall_unix_ms,
    }
}

/// What the communication owner reads about the player: from the world,
/// and the chat-room membership from the live option words.
fn player_facts(
    world: &World,
    options: &CharacterOptions,
    has_open_vendor: bool,
    has_window: bool,
) -> PlayerFacts {
    let guid = world.player_guid().unwrap_or(0);
    let item = world.item(guid);
    let heritage = world
        .player()
        .map(|p| p.numbers().int_property(PropertyInt::HERITAGE_GROUP));
    PlayerFacts {
        guid,
        public_weenie_bitfield: item
            .and_then(|i| i.public_weenie_bitfield)
            .map(|flags| flags.bits()),
        is_away: item.is_some_and(|i| i.properties.get_bool(PropertyBool::AFK.0, false)),
        membership: Membership {
            options1: options.options1(),
            options2: options.options2(),
            // Heritage groups 12 and 13 are the Olthoi.
            is_olthoi: matches!(heritage, Some(12 | 13)),
        },
        last_outside_corpse_cell: crate::gameplay::last_outside_corpse_cell(world),
        has_open_vendor,
        current_position: None,
        // Gender 1 is male.
        male: world
            .player()
            .is_some_and(|p| p.numbers().int_property(PropertyInt::GENDER) == 1),
        has_window,
    }
}

/// Every known object with its place and distance from the player, nearest
/// first; objects not placed in the world come last.
fn list_objects(world: &World) -> Vec<ObjectSummary> {
    let player = world
        .player_guid()
        .and_then(|guid| world.object(guid))
        .and_then(|o| o.position().copied());
    let mut list: Vec<ObjectSummary> = world
        .objects()
        .map(|o| {
            let position = o.position();
            ObjectSummary {
                guid: o.guid(),
                local_id: o.key().local_id,
                name: world.name(o.guid()).map(str::to_owned),
                cell_id: position.map(|p| p.cell_id),
                origin: position.map(|p| [p.origin.x, p.origin.y, p.origin.z]),
                distance: position.zip(player).map(|(p, me)| distance(p, &me)),
                parent: o.parent().map(|p| p.guid),
                monarch: world
                    .item(o.guid())
                    .and_then(ac_core::items::ClientObject::monarch_id),
            }
        })
        .collect();
    list.sort_by(|a, b| {
        a.distance
            .unwrap_or(f32::INFINITY)
            .total_cmp(&b.distance.unwrap_or(f32::INFINITY))
            .then(a.guid.cmp(&b.guid))
    });
    list
}

#[cfg(test)]
mod tests {
    //! The owners wired together: a command reaches its owner and what it
    //! produces comes back as host events. Option behaviour follows OpenAC
    //! `RuntimeClientCommandBindings` (local write, then the single-option
    //! request) and `GameRuntime.FlushCharacterOptions`.

    use super::*;
    use crate::inventory::{ItemCommand, ObjectRef};
    use ac_proto::social::build_set_single_character_option;

    const CLOCKS: Clocks = Clocks {
        wall_unix_ms: 1_790_000_000_000,
        session: Duration::from_secs(30),
    };

    fn state() -> (SessionState, Outbox) {
        (
            SessionState::new(CommunicationPaths::default()),
            Outbox::new(),
        )
    }

    fn bodies(outbox: &mut Outbox) -> Vec<Vec<u8>> {
        outbox.drain().map(|o| o.body).collect()
    }

    #[test]
    fn closing_a_container_is_a_plain_use_of_it_and_an_unknown_one_is_in_range() {
        // OpenAC `ExternalContainerController.RequestClose` (its `sendUse`)
        // and `WorldSelectionQuery.IsWithinExternalContainerUseRange` (no
        // use radius or place: the server decides).
        let (mut state, mut outbox) = state();
        assert!(state.within_use_range(0x7000_0001));
        state.command(
            SessionCommand::Item(ItemCommand::CloseContainer(ObjectRef::Guid(0x7000_0001))),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::inventory::build_use(1, 0x7000_0001)]
        );
        let facts = crate::session::interface::InterfaceState::of(&state, true);
        assert_eq!(facts.external_container, 0);
        assert!(facts.external_container_in_range);
    }

    #[test]
    fn leave_turns_a_listen_option_off_and_saves_it_at_once() {
        let (mut state, mut outbox) = state();
        assert!(state.options.get(option_id::LISTEN_TO_GENERAL_CHAT));
        let rest = state.command(
            SessionCommand::Chat("/leave general".into()),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(rest.is_none());
        assert!(!state.options.get(option_id::LISTEN_TO_GENERAL_CHAT));
        assert_eq!(
            bodies(&mut outbox),
            [build_set_single_character_option(
                1,
                option_id::LISTEN_TO_GENERAL_CHAT,
                false
            )]
        );
    }

    #[test]
    fn a_batched_option_waits_for_the_seed_and_the_save() {
        let (mut state, mut outbox) = state();
        state.command(
            SessionCommand::SetCharacterOption {
                option: option_id::DISPLAY_TIME_STAMPS,
                value: true,
            },
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(state.options.is_dirty());
        // Not seeded by a description yet: nothing goes out.
        state.command(
            SessionCommand::SaveCharacterOptions,
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(outbox.is_empty());
        let seeded = (state.options.options1(), state.options.options2());
        state.options.replace(seeded.0, seeded.1, true);
        state.command(
            SessionCommand::SetCharacterOption {
                option: option_id::DISPLAY_TIME_STAMPS,
                value: false,
            },
            &mut outbox,
            true,
            CLOCKS,
        );
        state.command(
            SessionCommand::SaveCharacterOptions,
            &mut outbox,
            true,
            CLOCKS,
        );
        assert_eq!(bodies(&mut outbox), [state.options.build_block(1)]);
        assert!(!state.options.is_dirty());
    }

    #[test]
    fn declared_options_go_on_once_login_complete_is_out_and_the_options_are_in() {
        // OpenAC RuntimeCharacterOptionsSeeder wired as HeadlessSessionHost
        // does (NoteLoginCompleteSent, NoteOptionsSeeded from the
        // description), the steps through the options commands
        // (HeadlessCharacterOptionsSeederWiringTests).
        let (mut state, mut outbox) = state();
        state.install_login_automation(
            &[
                (option_id::IGNORE_TRADE_REQUESTS, true),
                (option_id::AUTO_REPEAT_ATTACK, false),
            ],
            Vec::new(),
            Duration::ZERO,
        );
        // Login complete before the server's options: nothing yet.
        state.follow_login(&mut outbox, true, true, true, CLOCKS);
        assert!(outbox.is_empty());
        // The description says auto-repeat is on and trades are allowed.
        let mut description = crate::world::tests::empty_description();
        description.options1 = crate::character::option(option_id::AUTO_REPEAT_ATTACK)
            .unwrap()
            .mask;
        // Applied as `SessionState::apply` applies the parsed description.
        state.world.apply_player_description(&description);
        state.options.apply_player_description(&description);
        let steps = state.options_seeder.note_options_seeded(&state.options);
        state.seed_options(&steps, &mut outbox, true, CLOCKS);
        let sent = bodies(&mut outbox);
        assert_eq!(
            sent[0],
            build_set_single_character_option(1, option_id::AUTO_REPEAT_ATTACK, false)
        );
        assert_eq!(sent[1], state.options.build_block(2));
        assert_eq!(sent.len(), 2);
        assert!(state.options.get(option_id::IGNORE_TRADE_REQUESTS));
        // A second notice finds nothing to change.
        let steps = state.options_seeder.note_options_seeded(&state.options);
        assert!(steps.is_empty());
    }

    #[test]
    fn login_commands_wait_for_the_server_then_run_a_delay_apart() {
        // OpenAC LiveSessionHost (EnteredWorld when the character enters,
        // Tick each pass, listening once login complete is out) over
        // LoginCommandSequence; a line the chat box cannot route is a
        // loginCommandFailed status, not the end of the list.
        let (mut state, mut outbox) = state();
        state.install_login_automation(
            &[],
            vec!["hello".into(), "/".into(), "/day".into()],
            Duration::from_millis(500),
        );
        state.follow_login(&mut outbox, true, false, true, CLOCKS);
        assert!(outbox.is_empty());
        state.follow_login(&mut outbox, true, true, true, CLOCKS);
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::chat::build_talk(1, "hello").unwrap()]
        );
        let later = |ms| Clocks {
            session: CLOCKS.session + Duration::from_millis(ms),
            ..CLOCKS
        };
        state.follow_login(&mut outbox, true, true, true, later(499));
        assert!(
            !state
                .drain_host_events()
                .iter()
                .any(|e| matches!(e, HostEvent::LoginCommandFailed(_)))
        );
        state.follow_login(&mut outbox, true, true, true, later(500));
        let events = state.drain_host_events();
        assert!(events.iter().any(|e| matches!(
            e,
            HostEvent::LoginCommandFailed(f) if f.command_index == 1 && f.command == "/"
        )));
        state.follow_login(&mut outbox, true, true, true, later(1000));
        assert!(state.options.get(option_id::PERSISTENT_AT_DAY));
        // A new generation runs the list again.
        state.end_generation(CLOCKS.wall_unix_ms);
        state.generation = state.generation.next();
        state.follow_login(&mut outbox, true, true, true, later(2000));
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::chat::build_talk(2, "hello").unwrap()]
        );
    }

    #[test]
    fn login_commands_wait_for_the_plugins_first_tick_in_the_world() {
        // The plugins arrive when the server listens, so
        // the commands also wait until they heard the arrival and were
        // ticked once (OpenAC's arrive a round trip before the commands).
        // The first command then goes at once, the next a delay after it.
        let (mut state, mut outbox) = state();
        state.install_login_automation(
            &[],
            vec!["hello".into(), "again".into()],
            Duration::from_millis(500),
        );
        state.follow_login(&mut outbox, true, true, false, CLOCKS);
        assert!(outbox.is_empty());
        let later = |ms| Clocks {
            session: CLOCKS.session + Duration::from_millis(ms),
            ..CLOCKS
        };
        state.follow_login(&mut outbox, true, true, false, later(600));
        assert!(outbox.is_empty());
        state.follow_login(&mut outbox, true, true, true, later(616));
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::chat::build_talk(1, "hello").unwrap()]
        );
        state.follow_login(&mut outbox, true, true, true, later(1115));
        assert!(outbox.is_empty());
        state.follow_login(&mut outbox, true, true, true, later(1116));
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::chat::build_talk(2, "again").unwrap()]
        );
    }

    #[test]
    fn a_line_typed_into_the_chat_entry_goes_where_the_entry_is_aimed() {
        // OpenAC HeadlessConsoleTests.APlainLineGoesToTheChannelTheChatEntryIsAimedAt,
        // APlainLineGoesToTheListenerTheChatEntryIsAimedAt,
        // ALineTypedAtTheConsoleIsRememberedForRecall and
        // ANullLineSendsTheDraftAPluginStaged, over RuntimeChatEntryOwner.
        use crate::communication::ChatEntryCommand as E;
        let (mut state, mut outbox) = state();
        let entry = |state: &mut SessionState, outbox: &mut Outbox, command| {
            state.command(SessionCommand::ChatEntry(command), outbox, true, CLOCKS);
        };
        entry(&mut state, &mut outbox, E::Submit(Some("hello".into())));
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::chat::build_talk(1, "hello").unwrap()]
        );
        entry(
            &mut state,
            &mut outbox,
            E::SetTellTarget {
                name: "Bob".into(),
                guid: 0,
            },
        );
        entry(&mut state, &mut outbox, E::Submit(Some("meet me".into())));
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::chat::build_tell(2, "Bob", "meet me").unwrap()]
        );
        // Remembered for recall, newest first.
        entry(&mut state, &mut outbox, E::RecallPrevious);
        assert_eq!(state.communication.chat_entry().draft(), "meet me");
        entry(&mut state, &mut outbox, E::Clear);
        // A staged draft goes out on an empty Enter.
        entry(&mut state, &mut outbox, E::SetChannel(ChatChannelKind::Say));
        entry(&mut state, &mut outbox, E::Compose("staged".into()));
        entry(&mut state, &mut outbox, E::Submit(None));
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::chat::build_talk(3, "staged").unwrap()]
        );
        assert_eq!(state.communication.chat_entry().draft(), "");
    }

    #[test]
    fn the_status_box_stays_bounded_with_nothing_draining_it() {
        // As in OpenAC: with no console or window the status box
        // still keeps at most four lines, a repeat folded into the newest.
        let (mut state, mut outbox) = state();
        for index in 0..50 {
            state.command(
                SessionCommand::ShowInterfaceText(format!("notice {index}")),
                &mut outbox,
                true,
                CLOCKS,
            );
            state.command(
                SessionCommand::ShowInterfaceText(format!("notice {index}")),
                &mut outbox,
                true,
                CLOCKS,
            );
            state.tick(&mut outbox, true, CLOCKS);
        }
        let shown = state.communication.spew_box().visible();
        assert_eq!(
            shown.iter().map(|e| e.text.as_str()).collect::<Vec<_>>(),
            ["notice 49", "notice 48", "notice 47", "notice 46"]
        );
    }

    #[test]
    fn a_windows_interface_text_goes_where_its_text_class_goes() {
        // OpenAC `InteractionRetainedUiComposition`: the options window's
        // `DisplaySystemMessage` is `Communication.AddText(text,
        // ClientLocal)` (the status box), its mouse-turning macro lines
        // `AddText(text, Magic)` (the chat log, where plugins hear them).
        use ac_core::chat::LogTextType;
        let (mut state, mut outbox) = state();
        state.command(
            SessionCommand::ShowText {
                text: "Screenshot saved to here".into(),
                log_text_type: LogTextType::ClientLocal,
            },
            &mut outbox,
            true,
            CLOCKS,
        );
        state.command(
            SessionCommand::ShowText {
                text: "Mouse turning is on.".into(),
                log_text_type: LogTextType::Magic,
            },
            &mut outbox,
            true,
            CLOCKS,
        );
        state.tick(&mut outbox, true, CLOCKS);
        let shown = state.communication.spew_box().visible();
        assert_eq!(
            shown.iter().map(|e| e.text.as_str()).collect::<Vec<_>>(),
            ["Screenshot saved to here"]
        );
        let lines = state.communication.transcript_lines();
        assert!(
            lines.iter().any(|l| l.text == "Mouse turning is on."
                && l.log_text_type == LogTextType::Magic.as_u32()),
            "{lines:?}"
        );
        assert!(!lines.iter().any(|l| l.text == "Screenshot saved to here"));
    }

    #[test]
    fn status_names_the_generation_the_life_and_the_place() {
        // OpenAC RuntimeStatusCommandTests: /status is the client's own verb
        // on every host, answered in the chat with RuntimeSessionStatusText.
        let (mut state, mut outbox) = state();
        for (line, seconds) in [("/status", 0), ("@STATUS now", 2)] {
            // Two seconds apart: the chat folds a repeated system line.
            let clocks = Clocks {
                wall_unix_ms: CLOCKS.wall_unix_ms + seconds * 1000,
                ..CLOCKS
            };
            state.command(SessionCommand::Chat(line.into()), &mut outbox, true, clocks);
        }
        let lines: Vec<String> = state
            .drain_host_events()
            .into_iter()
            .filter_map(|e| match e {
                HostEvent::Chat(line) => Some(line.text),
                _ => None,
            })
            .collect();
        assert_eq!(
            lines,
            [
                "generation=0 state=Stopped position=unknown",
                "generation=0 state=Stopped position=unknown"
            ]
        );
        assert!(outbox.is_empty());
        assert!(!is_status_verb("/statusx"));
        assert!(!is_status_verb("status"));
    }

    #[test]
    fn the_timestamp_option_reaches_the_chat_lines() {
        let (mut state, mut outbox) = state();
        state
            .communication
            .set_time_zone(crate::local_time::LocalZone::fixed(0));
        state.options.set_bit(option_id::DISPLAY_TIME_STAMPS, true);
        state.tick(&mut outbox, true, CLOCKS);
        state.command(
            SessionCommand::Chat("/day".into()),
            &mut outbox,
            true,
            CLOCKS,
        );
        let lines: Vec<String> = state
            .drain_host_events()
            .into_iter()
            .filter_map(|e| match e {
                HostEvent::Chat(line) => Some(line.text),
                _ => None,
            })
            .collect();
        let prefix = ac_core::chat::format_timestamp_prefix(CLOCKS.wall_unix_ms, 0);
        assert_eq!(lines, [format!("{prefix}Let there be light!")]);
    }

    #[test]
    fn the_language_filter_option_censors_with_the_tables_patterns() {
        // OpenAC ChatLog.FilterLanguageSource (the FilterLanguage option)
        // and FilterLanguagePatterns (the taboo table's chat audience,
        // InteractionRetainedUiComposition.LoadFilterLanguagePatterns).
        use crate::gameplay::{ChatTaboo, GameTables};
        let (mut state, mut outbox) = state();
        state.install_tables(
            Arc::new(
                GameTables::empty().with_chat_taboo(ChatTaboo::Patterns(vec!["zork*".to_owned()])),
            ),
            "testaccount",
        );
        let speech = |text: &str| {
            Inbound::HearSpeech(ac_proto::chat::HearSpeech {
                text: text.to_owned(),
                sender_name: "Alice".to_owned(),
                sender_guid: 0x5000_00AA,
                range: None,
                chat_type: 2,
            })
        };
        let heard = |state: &mut SessionState| -> Vec<String> {
            state
                .drain_host_events()
                .into_iter()
                .filter_map(|e| match e {
                    HostEvent::Chat(line) => Some(line.text),
                    _ => None,
                })
                .collect()
        };
        state.apply(&speech("a zorky b"), &mut outbox, true, CLOCKS);
        assert!(heard(&mut state)[0].contains("a zorky b"));
        state.options.set_bit(option_id::FILTER_LANGUAGE, true);
        state.tick(&mut outbox, true, CLOCKS);
        state.apply(&speech("a zorky b"), &mut outbox, true, CLOCKS);
        let lines = heard(&mut state);
        assert!(lines[0].contains("a **** b"), "{lines:?}");
    }

    #[test]
    fn a_pose_in_speech_reaches_the_body_and_the_transcript() {
        // OpenAC `RuntimeChatCommandBindings`: the tables' poses resolve the
        // word, the motion goes to the movement owner (no body here, so it
        // is refused silently) and the speaker's line is printed.
        use crate::communication::{ChatPose, ChatPoseCatalog};
        use crate::gameplay::GameTables;
        let (mut state, mut outbox) = state();
        let poses = ChatPoseCatalog::of([(
            "wave".to_owned(),
            ChatPose {
                motion_command: ac_motion::motion_command::WAVE,
                self_text: "wave.".to_owned(),
                others_text: "waves %p hand.".to_owned(),
            },
        )]);
        state.install_tables(
            Arc::new(GameTables::empty().with_chat_poses(poses)),
            "testaccount",
        );
        state.command(
            SessionCommand::Say("hello *wave*".to_owned()),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(state.communication.take_pose_motions().is_empty());
        let lines: Vec<String> = state
            .drain_host_events()
            .into_iter()
            .filter_map(|e| match e {
                HostEvent::Chat(line) => Some(line.text),
                _ => None,
            })
            .collect();
        assert!(lines.iter().any(|l| l.contains("You wave.")), "{lines:?}");
    }

    #[test]
    fn item_commands_answer_through_host_events() {
        let (mut state, mut outbox) = state();
        state.command(
            SessionCommand::Item(ItemCommand::Use(ObjectRef::Name("nobody".into()))),
            &mut outbox,
            true,
            CLOCKS,
        );
        state.command(SessionCommand::ListInventory, &mut outbox, true, CLOCKS);
        state.command(
            SessionCommand::ShowCharacterOptions,
            &mut outbox,
            true,
            CLOCKS,
        );
        let events = state.drain_host_events();
        assert!(events.contains(&HostEvent::CommandRefused(
            "Nothing called \"nobody\" is known.".into()
        )));
        assert!(events.contains(&HostEvent::Inventory(
            crate::inventory::InventoryView::default()
        )));
        assert!(events.iter().any(|e| matches!(
            e,
            HostEvent::CharacterOptions(values) if values.len() == 53
        )));
    }

    #[test]
    fn a_block_save_repeats_the_favorites_the_player_set() {
        // OpenAC `CharacterOptionsBlobSource.Capture`: the block echoes the
        // live spellbook's favorite bars.
        use crate::gameplay::GameplayCommand;
        let (mut state, mut outbox) = state();
        state.world.set_player(0x5000_000B);
        let mut description = crate::world::tests::empty_description();
        description.spells.insert(2, 0.0);
        state.world.apply_player_description(&description);
        state.options.replace(0, 0, true);
        state.command(
            SessionCommand::Gameplay(GameplayCommand::AddFavorite {
                tab: 0,
                position: 0,
                spell: "2".into(),
            }),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::magic::build_add_spell_favorite(1, 2, 0, 0)]
        );
        state.options.set_bit(option_id::DISPLAY_TIME_STAMPS, true);
        state.options.mark_dirty(CLOCKS.session);
        state.save_options(&mut outbox);
        let mut expected = CharacterOptions::new();
        expected.replace(state.options.options1(), state.options.options2(), true);
        expected.echo_spellbook(state.world.spellbook().unwrap());
        assert_eq!(bodies(&mut outbox), [expected.build_block(2)]);
        assert_eq!(state.world.spellbook().unwrap().get_favorites(0), [2]);
    }

    fn teleported(guid: u32, sequence: u16, cell: u32) -> Inbound<'static> {
        Inbound::UpdatePosition(ac_proto::UpdatePosition {
            guid,
            flags: 0,
            position: crate::world::tests::position(cell, 5.0),
            velocity: None,
            placement_id: None,
            instance_sequence: 1,
            // Each teleport's position is the next one.
            position_sequence: 1 + sequence,
            teleport_sequence: sequence,
            force_position_sequence: 0,
        })
    }

    #[test]
    fn a_teleport_ends_portal_space_with_login_complete() {
        // OpenAC RuntimeLiveEntitySessionController.TryCompletePortal, the
        // host without a window: the start activates at once, the accepted
        // destination completes the reveal and login complete goes out.
        const PLAYER: u32 = 0x5000_000A;
        let (mut state, mut outbox) = state();
        state.world.set_player(PLAYER);
        let at = crate::world::tests::position(0xA9B4_0031, 1.0);
        state.apply(
            &crate::world::tests::create(PLAYER, crate::world::tests::stamps(1, 1), Some(at)),
            &mut outbox,
            true,
            CLOCKS,
        );
        let start = Inbound::PlayerTeleport(ac_proto::PlayerTeleport {
            teleport_sequence: 1,
        });
        state.apply(&start, &mut outbox, true, CLOCKS);
        assert!(state.transit.is_teleport_active());
        assert!(outbox.is_empty());
        // A window shows the tunnel from the start; one attaching now joins
        // it.
        let entered = PortalSpaceEvent::Entered {
            teleport_sequence: 1,
        };
        assert_eq!(state.take_portal_events(), [entered]);
        assert_eq!(state.portal_space_now(), [entered]);
        // The same start again is no new portal space.
        state.apply(&start, &mut outbox, true, CLOCKS);
        assert!(state.take_portal_events().is_empty());
        state.apply(
            &teleported(PLAYER, 1, 0x7D64_0020),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert_eq!(bodies(&mut outbox), [build_login_complete()]);
        assert_eq!(
            state.take_portal_events(),
            [PortalSpaceEvent::Arrived {
                teleport_sequence: 1,
                destination_cell: 0x7D64_0020,
            }]
        );
        assert!(state.portal_space_now().is_empty());
        assert!(!state.transit.is_teleport_active());
        let portal = state.transit.snapshot();
        assert_eq!(portal.kind, crate::world::transit::PortalKind::Portal);
        assert!(portal.completed && portal.destination_cell() == 0x7D64_0020);
        assert!(
            state
                .drain_host_events()
                .contains(&HostEvent::Portal(portal))
        );
        // An ordinary move afterwards is no teleport.
        state.apply(
            &teleported(PLAYER, 1, 0x7D64_0021),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(outbox.is_empty());
    }

    const TRAVELLER: u32 = 0x5000_000A;
    const DESTINATION: u32 = 0x7D64_0020;

    /// A player in the world, in portal space for teleport `sequence`.
    fn in_portal_space(window: bool, sequence: u16) -> (SessionState, Outbox) {
        let (mut state, mut outbox) = state();
        state.set_window_attached(window, &mut outbox);
        state.world.set_player(TRAVELLER);
        let at = crate::world::tests::position(0xA9B4_0031, 1.0);
        state.apply(
            &crate::world::tests::create(TRAVELLER, crate::world::tests::stamps(1, 1), Some(at)),
            &mut outbox,
            true,
            CLOCKS,
        );
        start_teleport(&mut state, &mut outbox, sequence);
        (state, outbox)
    }

    fn start_teleport(state: &mut SessionState, outbox: &mut Outbox, sequence: u16) {
        let start = Inbound::PlayerTeleport(ac_proto::PlayerTeleport {
            teleport_sequence: sequence,
        });
        state.apply(&start, outbox, true, CLOCKS);
    }

    fn arrive_at(state: &mut SessionState, outbox: &mut Outbox, sequence: u16, cell: u32) {
        state.apply(&teleported(TRAVELLER, sequence, cell), outbox, true, CLOCKS);
    }

    /// The host hears the begun reveal and then its completion, with or
    /// without a window: a session without one completes the arrival at
    /// once, and the begun reveal is still told first (the host-parity
    /// scenario `going_through_a_portal_tells_a_plugin_on_both_clients`,
    /// OpenAC `WorldInteractionEventParityTests`: leaving, then arriving).
    #[test]
    fn the_begun_reveal_is_told_before_its_arrival_with_or_without_a_window() {
        for window in [false, true] {
            let (mut state, mut outbox) = in_portal_space(window, 1);
            state.drain_host_events();
            arrive_at(&mut state, &mut outbox, 1, DESTINATION);
            if window {
                let generation = state.transit.snapshot().generation;
                state.command(shown(generation, 1, DESTINATION), &mut outbox, true, CLOCKS);
            }
            let portals: Vec<(bool, u32)> = state
                .drain_host_events()
                .into_iter()
                .filter_map(|e| match e {
                    HostEvent::Portal(p) => Some((p.completed, p.readiness.destination_cell)),
                    _ => None,
                })
                .collect();
            assert_eq!(portals.len(), 2, "window {window}: {portals:?}");
            assert!(!portals[0].0, "window {window}: begun first");
            assert!(portals[1].0, "window {window}: then completed");
        }
    }

    /// The window's answer: shown, with the destination loaded.
    fn shown(generation: u64, sequence: u16, cell: u32) -> SessionCommand {
        SessionCommand::DestinationShown(DestinationShown {
            teleport_sequence: sequence,
            readiness: DestinationReadiness::immediate(generation, cell),
        })
    }

    /// OpenAC `LoginComplete_EntersWorldThenSendsThenCompletesAndResets` and
    /// `ExitSound_HidesTunnelBeforeReleasingDestinationAndKeepsProtocolActive`
    /// (`LocalPlayerTeleportControllerTests`): with a window attached the
    /// destination is accepted but the server hears login complete only when
    /// the window has shown it; then the reveal completes and the teleport
    /// is over.
    /// OpenAC `FrameClockParityTests.TheWorldClockStandsStillWhileThereIsNoWorldOnBothClients`:
    /// the session's simulation time stands still from giving up the world
    /// to the next one standing up, and carries on from where it stopped.
    #[test]
    fn the_world_clock_stands_still_while_there_is_no_world() {
        let at = |s: u64| Clocks {
            session: CLOCKS.session + Duration::from_secs(s),
            ..CLOCKS
        };
        // With a window the destination's reveal waits for it: the world
        // the character left is given up and the next is not yet up.
        let (mut state, mut outbox) = in_portal_space(true, 1);
        state.tick(&mut outbox, true, at(0));
        state.tick(&mut outbox, true, at(1));
        let before = state.frame_clock.simulation_seconds();
        assert_eq!(before, 1.0);
        arrive_at(&mut state, &mut outbox, 1, DESTINATION);
        assert!(!state.transit.is_world_simulation_available());
        state.tick(&mut outbox, true, at(2));
        state.tick(&mut outbox, true, at(3));
        assert_eq!(state.frame_clock.simulation_seconds(), before, "held");
        assert_eq!(state.frame_clock.frame_number(), 4, "the passes count");
        let generation = state.transit.snapshot().generation;
        state.command(shown(generation, 1, DESTINATION), &mut outbox, true, at(3));
        assert!(state.transit.is_world_simulation_available());
        state.tick(&mut outbox, true, at(4));
        assert_eq!(state.frame_clock.simulation_seconds(), before + 1.0);
    }

    #[test]
    fn a_window_holds_the_arrival_until_it_has_shown_the_destination() {
        let (mut state, mut outbox) = in_portal_space(true, 1);
        drop(state.take_portal_events());
        arrive_at(&mut state, &mut outbox, 1, DESTINATION);
        assert!(outbox.is_empty(), "no login complete while held");
        let portal = state.transit.snapshot();
        let held = PortalSpaceEvent::Destination {
            generation: portal.generation,
            teleport_sequence: 1,
            destination_cell: DESTINATION,
        };
        assert_eq!(state.take_portal_events(), [held]);
        assert_eq!(state.held_arrival(), Some(held));
        assert!(state.transit.is_teleport_active());
        assert!(!portal.completed && !portal.materialized);
        // A window attaching now joins the tunnel with the destination.
        assert_eq!(
            state.portal_space_now(),
            [
                PortalSpaceEvent::Entered {
                    teleport_sequence: 1
                },
                held
            ]
        );
        // A log out waits for the teleport, as it does in OpenAC.
        assert!(!state.transit.try_begin_logout_request(false));
        // An answer for another reveal changes nothing.
        for stale in [
            shown(portal.generation + 1, 1, DESTINATION),
            shown(portal.generation, 2, DESTINATION),
            shown(portal.generation, 1, DESTINATION + 1),
        ] {
            assert!(state.command(stale, &mut outbox, true, CLOCKS).is_none());
        }
        assert!(outbox.is_empty());
        assert!(state.held_arrival().is_some());

        state.command(
            shown(portal.generation, 1, DESTINATION),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert_eq!(bodies(&mut outbox), [build_login_complete()]);
        assert_eq!(
            state.take_portal_events(),
            [PortalSpaceEvent::Arrived {
                teleport_sequence: 1,
                destination_cell: DESTINATION,
            }]
        );
        let portal = state.transit.snapshot();
        assert!(portal.completed && portal.materialized && !portal.cancelled);
        assert_eq!(portal.invariant_failure_count, 0);
        assert!(!state.transit.is_teleport_active());
        assert!(state.held_arrival().is_none());
        // A second answer sends nothing more.
        state.command(
            shown(portal.generation, 1, DESTINATION),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(outbox.is_empty());
    }

    /// An arrival the reveal's checks refuse leaves the server in portal
    /// space (no login complete); the log says so, naming the step, so a
    /// character left stuck that way can be told apart from one the server
    /// never moved.
    #[test]
    fn a_refused_arrival_is_reported_and_tells_the_server_nothing() {
        let (mut state, mut outbox) = in_portal_space(true, 1);
        arrive_at(&mut state, &mut outbox, 1, DESTINATION);
        state.drain_host_events();
        let generation = state.transit.snapshot().generation;
        // An outdoor destination with no render radius fails the readiness
        // check.
        let readiness = DestinationReadiness {
            required_render_radius: 0,
            ..DestinationReadiness::immediate(generation, DESTINATION)
        };
        state.command(
            SessionCommand::DestinationShown(DestinationShown {
                teleport_sequence: 1,
                readiness,
            }),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(outbox.is_empty(), "no login complete");
        assert!(state.transit.snapshot().cancelled);
        assert!(!state.transit.is_teleport_active());
        let refusals: Vec<String> = state
            .drain_host_events()
            .into_iter()
            .filter_map(|e| match e {
                HostEvent::Movement(crate::movement::MovementEvent::Refused(text)) => Some(text),
                _ => None,
            })
            .collect();
        assert_eq!(refusals.len(), 1, "{refusals:?}");
        assert!(
            refusals[0].contains("refused at its readiness")
                && refusals[0].contains("the server is not told the teleport ended"),
            "{}",
            refusals[0]
        );
    }

    /// A window that goes away mid-hold releases the
    /// arrival at once, as a session without a window arrives.
    #[test]
    fn a_window_detaching_mid_hold_releases_the_arrival() {
        let (mut state, mut outbox) = in_portal_space(true, 1);
        arrive_at(&mut state, &mut outbox, 1, DESTINATION);
        assert!(outbox.is_empty());
        state.set_window_attached(false, &mut outbox);
        assert_eq!(bodies(&mut outbox), [build_login_complete()]);
        assert!(state.transit.snapshot().completed);
        assert!(!state.transit.is_teleport_active());
        assert!(
            state
                .drain_host_events()
                .contains(&HostEvent::Portal(state.transit.snapshot()))
        );
        // Detaching again (or a late answer from the window) sends nothing.
        state.set_window_attached(false, &mut outbox);
        let generation = state.transit.snapshot().generation;
        state.command(shown(generation, 1, DESTINATION), &mut outbox, true, CLOCKS);
        assert!(outbox.is_empty());
    }

    /// The hold is armed by the window attached when the destination
    /// arrives: one that left before it does not hold, one that came back
    /// before it does.
    #[test]
    fn the_hold_follows_the_window_attached_when_the_destination_arrives() {
        let (mut state, mut outbox) = in_portal_space(true, 1);
        state.set_window_attached(false, &mut outbox);
        arrive_at(&mut state, &mut outbox, 1, DESTINATION);
        assert_eq!(bodies(&mut outbox), [build_login_complete()]);

        let (mut state, mut outbox) = in_portal_space(false, 1);
        state.set_window_attached(true, &mut outbox);
        arrive_at(&mut state, &mut outbox, 1, DESTINATION);
        assert!(outbox.is_empty());
        assert!(state.held_arrival().is_some());
    }

    /// OpenAC `NewerStart_ReplacesOldDestinationWithoutReusingIt`: a newer
    /// start abandons the held arrival without telling the server, and the
    /// newer destination is held and answered on its own.
    #[test]
    fn a_newer_start_abandons_the_held_arrival() {
        let (mut state, mut outbox) = in_portal_space(true, 1);
        arrive_at(&mut state, &mut outbox, 1, DESTINATION);
        let old = state.transit.snapshot().generation;
        start_teleport(&mut state, &mut outbox, 2);
        assert!(outbox.is_empty());
        assert!(state.held_arrival().is_none());
        assert!(state.transit.snapshot().cancelled);
        assert_eq!(state.transit.active_teleport_sequence(), 2);
        state.command(shown(old, 1, DESTINATION), &mut outbox, true, CLOCKS);
        assert!(outbox.is_empty(), "the old answer is stale");

        arrive_at(&mut state, &mut outbox, 2, DESTINATION + 1);
        let new = state.transit.snapshot().generation;
        assert!(new > old);
        assert!(outbox.is_empty());
        state.command(shown(new, 2, DESTINATION + 1), &mut outbox, true, CLOCKS);
        assert_eq!(bodies(&mut outbox), [build_login_complete()]);
    }

    /// OpenAC `SessionReset_ClearsTransitAndLetsRecenterConvergeAcrossLaterFrames`:
    /// the end of a generation forgets the held arrival.
    #[test]
    fn the_end_of_a_generation_forgets_the_held_arrival() {
        let (mut state, mut outbox) = in_portal_space(true, 1);
        arrive_at(&mut state, &mut outbox, 1, DESTINATION);
        state.end_generation(CLOCKS.wall_unix_ms);
        assert!(state.held_arrival().is_none());
        assert_eq!(state.transit.snapshot(), PortalSnapshot::IDLE);
        state.set_window_attached(false, &mut outbox);
        assert!(outbox.is_empty());
    }

    /// The plugins outlive the stay, but their world labels end with it, so
    /// a label on a lasting id (the character, a carried item) does not
    /// come back on the next stay.
    #[test]
    fn leaving_the_world_takes_the_plugins_labels_down() {
        let (mut state, _outbox) = state();
        let label = ac_plugin_ui::WorldLabel {
            object_id: 0x5000_0001,
            text: "me".into(),
            color: [1.0; 4],
            height_offset: 0.0,
            line: 0,
            max_range: 60.0,
            outline: true,
        };
        assert!(state.plugin_ui.show_labels("edwards.tank", vec![label]));
        state.end_generation(0);
        assert!(state.plugin_ui.merged_labels().is_empty());
    }

    /// Markers over objects end with the stay too, those at fixed positions
    /// stay, and the plugin's layer stays usable for the next one.
    #[test]
    fn leaving_the_world_takes_the_plugins_markers_over_objects_down() {
        let (mut state, _outbox) = state();
        let icon = ac_plugin_ui::WorldIcon {
            anchor: ac_plugin_ui::MarkerAnchor::Object(0x5000_0001),
            image: 1,
            size: 24.0,
            tint_rgba: 0xFFFF_FFFF,
            border_rgba: None,
            max_range: 60.0,
            hidden_by_world: false,
        };
        let here = ac_plugin_ui::MarkerAnchor::Position {
            point: ac_plugin_ui::MapPoint {
                east_west: 40.0,
                north_south: 12.5,
                elevation: 0.3,
            },
            is_outdoor: true,
        };
        let waypoint = ac_plugin_ui::WorldIcon {
            anchor: here,
            ..icon
        };
        let layer = state.plugin_ui.create_marker_layer("edwards.tank");
        assert!(
            state
                .plugin_ui
                .set_icons("edwards.tank", layer, vec![icon, waypoint])
        );
        state.end_generation(0);
        let left = state.plugin_ui.merged_icons();
        assert_eq!(left.len(), 1);
        assert_eq!(left[0].icon.anchor, here);
        assert!(state.plugin_ui.set_icons("edwards.tank", layer, vec![icon]));
    }

    #[test]
    fn the_logout_hold_runs_on_the_session_clock() {
        let (mut state, mut outbox) = state();
        assert!(state.transit.try_begin_logout_request(false));
        state.tick(&mut outbox, true, CLOCKS);
        let later = Clocks {
            session: CLOCKS.session + Duration::from_secs(3),
            ..CLOCKS
        };
        state.tick(&mut outbox, true, later);
        assert_eq!(
            state.transit.logout_stage(),
            LogoutStage::PresentationActive
        );
        let events = state.drain_host_events();
        assert!(events.contains(&HostEvent::Logout(LogoutStage::PresentationActive)));
        state.end_generation(0);
        assert_eq!(state.transit.logout_stage(), LogoutStage::None);
        assert_eq!(state.world.object_count(), 0);
    }

    /// OpenAC `LogoutRequest_SendsWireThenHoldsThreeSeconds_ThenBeginsWormhole`
    /// and `LogoutRequest_UsesThePlayerKillerHold`: the window hears the
    /// log-out portal space when the hold ends (3 s, 23 s for a player
    /// killer), not before; a window attaching then joins it.
    #[test]
    fn the_logout_portal_space_starts_when_the_hold_ends() {
        let at = |s: f64| Clocks {
            session: CLOCKS.session + Duration::from_secs_f64(s),
            ..CLOCKS
        };
        for (player_killer, hold) in [(false, 3.0), (true, 23.0)] {
            let (mut state, mut outbox) = state();
            state.set_window_attached(true, &mut outbox);
            assert!(state.transit.try_begin_logout_request(player_killer));
            state.tick(&mut outbox, true, at(0.0));
            state.tick(&mut outbox, true, at(hold - 0.05));
            assert!(state.take_portal_events().is_empty(), "the hold runs");
            assert!(state.portal_space_now().is_empty());
            state.tick(&mut outbox, true, at(hold + 0.05));
            assert_eq!(
                state.take_portal_events(),
                [PortalSpaceEvent::LogoutStarted]
            );
            assert_eq!(state.portal_space_now(), [PortalSpaceEvent::LogoutStarted]);
            state.tick(&mut outbox, true, at(hold + 1.0));
            assert!(state.take_portal_events().is_empty(), "once");
            // The confirmation ends the generation; nothing is left.
            state.end_generation(0);
            assert!(state.portal_space_now().is_empty());
        }
    }

    const ENTERING: u32 = 0x5000_000C;
    const LOGIN_CELL: u32 = 0xA9B4_0031;

    /// A session at the enter request with a window that draws the world.
    fn entering(window: bool) -> (SessionState, Outbox) {
        let (mut state, mut outbox) = state();
        state.set_window_attached(window, &mut outbox);
        state.command(
            SessionCommand::PresentsWorld(true),
            &mut outbox,
            false,
            CLOCKS,
        );
        state.world.set_player(ENTERING);
        state.follow_entry(true);
        (state, outbox)
    }

    fn login_shown(generation: u64) -> SessionCommand {
        SessionCommand::LoginShown(DestinationReadiness::immediate(generation, LOGIN_CELL))
    }

    /// OpenAC `ArmLoginTunnel_ShowsTunnelAndPlaysCueSynchronously`,
    /// `LoginReveal_ArmsThePortalSpacePresentation_WithRetailCues` and
    /// `LoginPump_SendsLoginCompleteOnlyAtThePresentationEnd`: the entry
    /// shows portal space from the enter request; the login reveal begins
    /// at the player's cell and completes, releasing login complete, only
    /// when the window answers for it.
    #[test]
    fn a_window_holds_login_complete_until_it_has_shown_the_login_destination() {
        let (mut state, mut outbox) = entering(true);
        assert_eq!(
            state.take_portal_events(),
            [PortalSpaceEvent::LoginEntering]
        );
        assert_eq!(state.portal_space_now(), [PortalSpaceEvent::LoginEntering]);
        state.follow_entry(true);
        assert!(state.take_portal_events().is_empty(), "armed once");
        assert!(state.is_login_presenting());
        assert!(state.window_holds_login());

        assert!(state.hold_login(LOGIN_CELL));
        let portal = state.transit.snapshot();
        assert_eq!(portal.kind, crate::world::transit::PortalKind::Login);
        assert!(!portal.completed && !portal.materialized);
        assert!(!state.transit.is_world_simulation_available());
        let held = PortalSpaceEvent::LoginDestination {
            generation: portal.generation,
            destination_cell: LOGIN_CELL,
        };
        assert_eq!(state.take_portal_events(), [held]);
        assert_eq!(
            state.portal_space_now(),
            [PortalSpaceEvent::LoginEntering, held]
        );
        assert!(state.is_login_held());
        // An answer for another reveal changes nothing.
        state.command(
            login_shown(portal.generation + 1),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(!state.take_login_release());

        state.command(login_shown(portal.generation), &mut outbox, true, CLOCKS);
        assert!(state.take_login_release(), "login complete may go");
        assert!(!state.take_login_release(), "once");
        let portal = state.transit.snapshot();
        assert!(portal.completed && portal.materialized && !portal.cancelled);
        assert_eq!(portal.invariant_failure_count, 0);
        assert!(state.transit.is_world_simulation_available());
        assert!(outbox.is_empty(), "the flow sends login complete");
        assert!(!state.is_login_presenting());
        // Login complete is out: the entry is over, silently.
        state.follow_entry(false);
        assert!(state.take_portal_events().is_empty());
        assert!(state.portal_space_now().is_empty());
    }

    /// The rule for a held arrival holds for the held login: a
    /// window that goes away (or is minimized) releases it at once.
    #[test]
    fn a_window_leaving_or_minimizing_releases_the_held_login() {
        let (mut state, mut outbox) = entering(true);
        state.hold_login(LOGIN_CELL);
        state.set_window_attached(false, &mut outbox);
        assert!(state.take_login_release());
        assert!(state.transit.snapshot().completed);
        assert!(!state.window_holds_login(), "a new window says again");

        let (mut state, mut outbox) = entering(true);
        state.hold_login(LOGIN_CELL);
        state.command(
            SessionCommand::WindowMinimized(true),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(state.take_login_release());
        assert!(state.transit.snapshot().completed);
        assert!(!state.window_holds_login());
    }

    /// OpenAC `ArmedLoginTunnel_DisarmsWhenTheEnterFallsBackToSelection`:
    /// an entry that goes back to selection withdraws its portal space; a
    /// later enter arms afresh.
    #[test]
    fn an_entry_back_at_selection_withdraws_its_portal_space() {
        let (mut state, _) = entering(true);
        drop(state.take_portal_events());
        state.follow_entry(false);
        assert_eq!(
            state.take_portal_events(),
            [PortalSpaceEvent::LoginWithdrawn]
        );
        assert!(!state.is_login_presenting());
        state.follow_entry(true);
        assert_eq!(
            state.take_portal_events(),
            [PortalSpaceEvent::LoginEntering]
        );
    }

    /// OpenAC `RealTeleportStart_SupersedesTheLoginPresentation`: a teleport
    /// during the login presentation abandons the login reveal; login
    /// complete goes out once, when the teleport arrives.
    #[test]
    fn a_teleport_replaces_the_login_presentation() {
        let (mut state, mut outbox) = entering(true);
        let at = crate::world::tests::position(LOGIN_CELL, 1.0);
        state.apply(
            &crate::world::tests::create(ENTERING, crate::world::tests::stamps(1, 1), Some(at)),
            &mut outbox,
            true,
            CLOCKS,
        );
        state.hold_login(LOGIN_CELL);
        let login = state.transit.snapshot().generation;
        drop(state.take_portal_events());
        let start = Inbound::PlayerTeleport(ac_proto::PlayerTeleport {
            teleport_sequence: 1,
        });
        state.apply(&start, &mut outbox, true, CLOCKS);
        assert!(state.transit.snapshot().cancelled);
        assert!(!state.is_login_presenting());
        assert!(state.is_login_held(), "login complete still waits");
        // The window's late answer for the login is stale.
        state.command(login_shown(login), &mut outbox, true, CLOCKS);
        assert!(!state.take_login_release());
        state.apply(
            &teleported(ENTERING, 1, DESTINATION),
            &mut outbox,
            true,
            CLOCKS,
        );
        let generation = state.transit.snapshot().generation;
        state.command(shown(generation, 1, DESTINATION), &mut outbox, true, CLOCKS);
        assert!(outbox.is_empty(), "not the arrival's own login complete");
        assert!(state.take_login_release(), "the held one goes instead");
        assert!(!state.is_login_held());
    }

    /// A window that does not draw the world (no dats) does not hold the
    /// login: OpenAC sends login complete at the first entry when there is
    /// no world to show.
    #[test]
    fn a_window_without_a_world_does_not_hold_the_login() {
        let (mut state, mut outbox) = entering(true);
        state.command(
            SessionCommand::PresentsWorld(false),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(!state.window_holds_login());
        let (state, _) = entering(false);
        assert!(!state.window_holds_login());
    }

    /// The launched client's order (a live dungeon login):
    /// the window says it draws the world with its attach request, the
    /// attach follows, and the player is in the world before the window
    /// has opened. The login is presented all the same, as OpenAC's
    /// graphical client always presents it (`LocalPlayerTeleportController`
    /// `ArmLoginTunnel` at the enter, login complete at `FireLoginComplete`):
    /// the window finds the entry and its destination waiting, saying it
    /// again once open changes nothing, and login complete goes when it has
    /// shown the destination.
    #[test]
    fn a_window_still_opening_presents_the_login() {
        let (mut state, mut outbox) = state();
        let presents = SessionCommand::PresentsWorld(true);
        state.command(presents, &mut outbox, false, CLOCKS);
        state.set_window_attached(true, &mut outbox);
        state.world.set_player(ENTERING);
        state.follow_entry(true);
        assert!(state.window_holds_login());
        assert!(state.hold_login(LOGIN_CELL));
        let generation = state.transit.snapshot().generation;

        let presents = SessionCommand::PresentsWorld(true);
        state.command(presents, &mut outbox, true, CLOCKS);
        let held = PortalSpaceEvent::LoginDestination {
            generation,
            destination_cell: LOGIN_CELL,
        };
        assert_eq!(
            state.take_portal_events(),
            [PortalSpaceEvent::LoginEntering, held]
        );
        assert!(state.is_login_held());
        assert!(!state.take_login_release());
        state.command(login_shown(generation), &mut outbox, true, CLOCKS);
        assert!(state.take_login_release());
    }

    /// OpenAC `ReadinessHold_NeverRevealsIncompleteWorldAndShowsWaitCueAfterFiveSeconds`:
    /// the reveal records its wait cue once the window has held it five
    /// seconds, not before, and only for the reveal under way.
    #[test]
    fn a_reveal_held_five_seconds_records_its_wait_cue() {
        let (mut state, mut outbox) = entering(true);
        state.hold_login(LOGIN_CELL);
        let generation = state.transit.snapshot().generation;
        let waited = |generation, ms| SessionCommand::RevealWaited {
            generation,
            waited: Duration::from_millis(ms),
        };
        state.command(waited(generation, 4_999), &mut outbox, true, CLOCKS);
        assert!(!state.transit.snapshot().wait_cue_shown);
        state.command(waited(generation + 1, 5_000), &mut outbox, true, CLOCKS);
        assert!(!state.transit.snapshot().wait_cue_shown);
        state.command(waited(generation, 5_000), &mut outbox, true, CLOCKS);
        assert!(state.transit.snapshot().wait_cue_shown);
        assert!(
            state
                .drain_host_events()
                .contains(&HostEvent::Portal(state.transit.snapshot()))
        );
    }

    /// OpenAC keeps updating a minimized window, so its portal presentation
    /// runs on and the arrival completes: a minimized window holds nothing,
    /// and minimizing mid-hold releases the arrival at once.
    #[test]
    fn a_minimized_window_does_not_hold_the_arrival() {
        let (mut state, mut outbox) = in_portal_space(true, 1);
        arrive_at(&mut state, &mut outbox, 1, DESTINATION);
        assert!(outbox.is_empty());
        state.command(
            SessionCommand::WindowMinimized(true),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert_eq!(bodies(&mut outbox), [build_login_complete()]);
        assert!(state.transit.snapshot().completed);

        // Minimized before the destination: it is not held.
        let (mut state, mut outbox) = in_portal_space(true, 1);
        state.command(
            SessionCommand::WindowMinimized(true),
            &mut outbox,
            true,
            CLOCKS,
        );
        state.set_window_attached(true, &mut outbox);
        arrive_at(&mut state, &mut outbox, 1, DESTINATION);
        assert_eq!(bodies(&mut outbox), [build_login_complete()]);
        // Restored, the next arrival is held again.
        state.command(
            SessionCommand::WindowMinimized(false),
            &mut outbox,
            true,
            CLOCKS,
        );
        start_teleport(&mut state, &mut outbox, 2);
        arrive_at(&mut state, &mut outbox, 2, DESTINATION + 1);
        assert!(outbox.is_empty());
        assert!(state.held_arrival().is_some());
    }

    /// What one command sends and shows, from a fresh state.
    fn outcome(command: SessionCommand) -> (Vec<Vec<u8>>, Vec<HostEvent>) {
        let (mut state, mut outbox) = state();
        state.world.set_player(0x5000_000B);
        state.command(command, &mut outbox, true, CLOCKS);
        (bodies(&mut outbox), state.drain_host_events())
    }

    #[test]
    fn a_chat_window_line_goes_to_its_talk_focus() {
        // OpenAC `ChatWindowController` hands the router the window's
        // default channel and tell target with the typed line: a plain line on a tell focus is a tell to that target, on
        // the Say focus it is speech, and a verb still wins over the focus.
        let typed = |text: &str, channel, target: Option<&str>| SessionCommand::ChatInput {
            text: text.into(),
            channel,
            tell_target: target.map(str::to_owned),
            tell_target_guid: 0,
        };
        assert_eq!(
            outcome(typed("hello", ChatChannelKind::Tell, Some("Bob"))),
            outcome(SessionCommand::Tell {
                target: "Bob".into(),
                text: "hello".into()
            })
        );
        assert_eq!(
            outcome(typed("hello", ChatChannelKind::Say, None)),
            outcome(SessionCommand::Chat("hello".into()))
        );
        let (sent, _) = outcome(typed("hello", ChatChannelKind::Say, None));
        assert!(!sent.is_empty(), "speech goes out");
        assert_eq!(
            outcome(typed("/day", ChatChannelKind::Tell, Some("Bob"))),
            outcome(SessionCommand::Chat("/day".into()))
        );
    }

    #[test]
    fn the_player_view_reads_vitals_selection_and_panel_options() {
        let (mut state, _) = state();
        state
            .options
            .set_bit(option_id::COORDINATES_ON_RADAR, false);
        state.options.set_bit(option_id::LOCK_UI, false);
        let empty = state.player_view();
        assert_eq!(empty.health, crate::world::VitalReading::default());
        assert!(!empty.coordinates_on_radar && !empty.ui_locked);
        assert_eq!(empty.selected, None);

        state.world.set_player(0x5000_000B);
        let mut description = crate::world::tests::empty_description();
        for (id, current) in [(7, 40), (8, 50), (9, 60)] {
            description.attributes.push(ac_proto::AttributeEntry {
                id,
                ranks: 10,
                start: 90,
                xp: 0,
                current: Some(current),
            });
        }
        state.world.apply_player_description(&description);
        state.options.set_bit(option_id::COORDINATES_ON_RADAR, true);
        state.options.set_bit(option_id::LOCK_UI, true);
        let view = state.player_view();
        let numbers = state.world.player().unwrap().numbers();
        assert_eq!((view.health, view.stamina, view.mana), {
            let v = PlayerView::default().with_vitals(numbers);
            (v.health, v.stamina, v.mana)
        });
        assert_eq!(
            [view.health.current, view.stamina.current, view.mana.current],
            [Some(40), Some(50), Some(60)]
        );
        assert!(view.health.percent.is_some() && view.health.max.is_some());
        assert!(view.coordinates_on_radar && view.ui_locked);
    }

    #[test]
    fn die_asks_the_player_and_only_a_yes_sends_the_suicide() {
        // OpenAC RuntimeClientCommandDispatcherTests.Die_RequiresRetailConfirmationBeforeSuicide,
        // with the dialog's answer coming back as a command.
        use crate::communication::ClientConfirmation;
        let (mut state, mut outbox) = state();
        let answer = |accepted| SessionCommand::AnswerConfirmation {
            confirmation: ClientConfirmation::Suicide,
            accepted,
        };
        // No question asked: an answer does nothing.
        state.command(answer(true), &mut outbox, true, CLOCKS);
        assert!(bodies(&mut outbox).is_empty());
        state.command(
            SessionCommand::Chat("/die".into()),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(
            state
                .drain_host_events()
                .contains(&HostEvent::Confirmation(ClientConfirmation::Suicide))
        );
        assert!(bodies(&mut outbox).is_empty());
        state.command(answer(false), &mut outbox, true, CLOCKS);
        assert!(bodies(&mut outbox).is_empty());
        state.command(
            SessionCommand::Chat("/die".into()),
            &mut outbox,
            true,
            CLOCKS,
        );
        state.command(answer(true), &mut outbox, true, CLOCKS);
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::client_commands::build_suicide(1)]
        );
        // Answered once; a second yes sends nothing.
        state.command(answer(true), &mut outbox, true, CLOCKS);
        assert!(bodies(&mut outbox).is_empty());
    }

    #[test]
    fn the_clients_questions_wait_their_turn_and_a_follow_up_opens_first() {
        // OpenAC `RetailDialogFactory.MakeDialog`: each question the client
        // asks (`RetailItemConfirmationController`, `ShowConfirmation`) goes
        // into the dialog queue, oldest first, one dialog shown; a dialog
        // made from an answer (the house's second question) is shown before
        // those waiting.
        use crate::communication::ClientConfirmation;
        let (mut state, mut outbox) = state();
        state.world.set_player(0x5000_000A);
        let chat = |state: &mut SessionState, outbox: &mut Outbox, text: &str| {
            state.command(SessionCommand::Chat(text.into()), outbox, true, CLOCKS);
            state.drain_host_events()
        };
        let asked = |events: &[HostEvent]| {
            events
                .iter()
                .filter_map(|e| match e {
                    HostEvent::Confirmation(c) => Some(c.clone()),
                    _ => None,
                })
                .collect::<Vec<_>>()
        };
        assert_eq!(
            asked(&chat(&mut state, &mut outbox, "/house abandon")),
            [ClientConfirmation::AbandonHouse]
        );
        // Asked while the first is open: it waits, nothing is shown yet.
        assert!(asked(&chat(&mut state, &mut outbox, "/die")).is_empty());
        assert_eq!(
            state.communication.pending_confirmation(),
            Some(ClientConfirmation::AbandonHouse)
        );
        // A yes to the first: its follow-up opens before the one waiting.
        let answer = |accepted| SessionCommand::AnswerOpenConfirmation { accepted };
        state.command(answer(true), &mut outbox, true, CLOCKS);
        assert_eq!(
            asked(&state.drain_host_events()),
            [ClientConfirmation::AbandonHouseCertain]
        );
        // A no to that: the waiting question opens.
        state.command(answer(false), &mut outbox, true, CLOCKS);
        assert_eq!(
            asked(&state.drain_host_events()),
            [ClientConfirmation::Suicide]
        );
        assert!(bodies(&mut outbox).is_empty());
        state.command(answer(true), &mut outbox, true, CLOCKS);
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::client_commands::build_suicide(1)]
        );
        assert!(state.communication.open_confirmation().is_none());
    }

    #[test]
    fn fillcomps_clear_empties_the_component_book_and_tells_the_server() {
        // FillComponents_ClearWorksWithoutVendorAndBuyingRequiresOne, carried
        // out by the magic owner.
        let (mut state, mut outbox) = state();
        state.world.set_player(0x5000_000A);
        state
            .world
            .apply_player_description(&crate::world::tests::empty_description());
        state
            .world
            .update_spellbook(|book| book.set_desired_component(691, 5));
        state.command(
            SessionCommand::Chat("/fillcomps clear".into()),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::magic::build_clear_desired_components(1)]
        );
        assert!(
            state
                .world
                .spellbook()
                .unwrap()
                .desired_components()
                .is_empty()
        );
    }

    #[test]
    fn raise_and_train_requests_go_out_in_the_world_only() {
        // OpenAC `CharacterSheetProvider.HandleRaiseRequest` through
        // `WorldSession.SendRaiseAttribute` and friends (the experience
        // field is the low 32 bits, `CharacterActions.BuildAttrOrVital`).
        use crate::character::CharacterCommand as C;
        use ac_proto::character::{build_raise_attribute, build_train_skill};
        let (mut state, mut outbox) = state();
        let raise = C::RaiseAttribute {
            attribute: 1,
            xp: 0x1_0000_0010,
        };
        state.command(SessionCommand::Character(raise), &mut outbox, true, CLOCKS);
        state.command(
            SessionCommand::Character(C::TrainSkill {
                skill: 33,
                credits: 4,
            }),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert_eq!(
            bodies(&mut outbox),
            [
                build_raise_attribute(1, 1, 0x10),
                build_train_skill(2, 33, 4)
            ]
        );
        state.command(SessionCommand::Character(raise), &mut outbox, false, CLOCKS);
        assert!(outbox.is_empty());
        assert!(
            state
                .drain_host_events()
                .iter()
                .any(|e| matches!(e, HostEvent::CommandRefused(_)))
        );
    }

    #[test]
    fn a_ping_goes_out_in_the_world_and_its_answer_times_the_round_trip() {
        // OpenAC WorldSessionLinkStatusTests.RequestAndEmptyPingResponse_*:
        // WorldSession.RequestLinkStatusPing sends the 12-byte ping and the
        // empty PingResponse records the round trip.
        let (mut state, mut outbox) = state();
        state.command(SessionCommand::Ping, &mut outbox, false, CLOCKS);
        assert!(outbox.is_empty());
        state.drain_host_events();
        state.command(SessionCommand::Ping, &mut outbox, true, CLOCKS);
        let sent = bodies(&mut outbox);
        assert_eq!(sent, [ac_proto::social::build_ping_request(1)]);
        assert_eq!(sent[0].len(), 12);
        let response = crate::world::tests::game_event(GameEventType::PING_RESPONSE, &[]);
        let later = Clocks {
            session: CLOCKS.session + Duration::from_millis(125),
            ..CLOCKS
        };
        state.apply(
            &Inbound::parse(&response).unwrap(),
            &mut outbox,
            true,
            later,
        );
        assert_eq!(state.ping.round_trip_seconds(), Some(0.125));
        // The connection's numbers are the runner's to add.
        assert!(matches!(
            state.command(SessionCommand::ShowLinkStatus, &mut outbox, true, CLOCKS),
            Some(SessionCommand::ShowLinkStatus)
        ));
    }

    #[test]
    fn the_servers_question_is_held_until_answered_or_withdrawn() {
        // OpenAC `GameplayConfirmationController`: one server question at a
        // time, a done closes only the matching one, an answer echoes the
        // kind and context; the question reaches the host as a
        // confirmation like the client's own.
        use crate::communication::ClientConfirmation;
        let (mut state, mut outbox) = state();
        state.world.set_player(0x5000_000A);
        let ask = |state: &mut SessionState, outbox: &mut Outbox, kind, context| {
            let body = crate::world::tests::game_event(
                GameEventType::CHARACTER_CONFIRMATION_REQUEST,
                &[kind, context, 0],
            );
            state.apply(&Inbound::parse(&body).unwrap(), outbox, true, CLOCKS);
        };
        ask(&mut state, &mut outbox, 4, 77);
        ask(&mut state, &mut outbox, 1, 78);
        let question = ClientConfirmation::Server {
            confirmation_type: 4,
            context_id: 77,
            message: String::new(),
        };
        assert_eq!(state.communication.server_confirmation(), Some(&question));
        assert!(
            state
                .drain_host_events()
                .contains(&HostEvent::Confirmation(question.clone()))
        );
        let done =
            crate::world::tests::game_event(GameEventType::CHARACTER_CONFIRMATION_DONE, &[1, 77]);
        state.apply(&Inbound::parse(&done).unwrap(), &mut outbox, true, CLOCKS);
        assert!(state.communication.server_confirmation().is_some());
        state.command(
            SessionCommand::AnswerConfirmation {
                confirmation: question.clone(),
                accepted: false,
            },
            &mut outbox,
            true,
            CLOCKS,
        );
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::social::build_confirmation_response(
                1, 4, 77, false
            )]
        );
        assert!(state.communication.server_confirmation().is_none());
        // Answered already: nothing more goes out.
        state.command(
            SessionCommand::AnswerConfirmation {
                confirmation: question,
                accepted: true,
            },
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(outbox.is_empty());
    }

    #[test]
    fn an_item_use_question_goes_to_the_host_and_a_yes_sends_the_use() {
        // OpenAC `RetailItemConfirmationControllerTests.VolatileRareSendsUseOnlyAfterPositiveFactoryCallback`
        // and `RejectedPlayerKillerAltarDoesNotSendUse`, through the
        // session: the question reaches the host as a confirmation, and the
        // answer comes back as a command.
        use crate::communication::{ClientConfirmation, ItemUseConfirmation};
        use ac_core::items::{ItemUseable, PublicWeenieFlags};
        use ac_proto::{CreateObject, ModelData, PhysicsTimestamps, PublicWeenieDesc};
        const PLAYER: u32 = 0x5000_000A;
        const RARE: u32 = 0x6000_0010;
        let (mut state, mut outbox) = state();
        state.world.set_player(PLAYER);
        state
            .options
            .set_bit(crate::character::option_id::CONFIRM_VOLATILE_RARE_USE, true);
        for (guid, desc) in [
            (
                PLAYER,
                PublicWeenieDesc {
                    name: "Horan".into(),
                    object_description_flags: PublicWeenieFlags::PLAYER.bits(),
                    ..PublicWeenieDesc::default()
                },
            ),
            (
                RARE,
                PublicWeenieDesc {
                    name: "Rare".into(),
                    object_description_flags: PublicWeenieFlags::VOLATILE_RARE.bits(),
                    useability: Some(ItemUseable::CONTAINED.bits()),
                    container_id: Some(PLAYER),
                    ..PublicWeenieDesc::default()
                },
            ),
        ] {
            let message = Inbound::CreateObject(Box::new(CreateObject {
                guid,
                model: ModelData::default(),
                physics: crate::world::tests::physics(
                    PhysicsTimestamps {
                        instance: 1,
                        ..PhysicsTimestamps::default()
                    },
                    None,
                ),
                weenie: Some(desc),
            }));
            state.apply(&message, &mut outbox, true, CLOCKS);
        }
        state.drain_host_events();
        let question = ClientConfirmation::ItemUse {
            kind: ItemUseConfirmation::VolatileRare,
            object_id: RARE,
        };
        let use_rare = SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(RARE)));
        let answer = |accepted| SessionCommand::AnswerConfirmation {
            confirmation: question.clone(),
            accepted,
        };
        state.command(use_rare.clone(), &mut outbox, true, CLOCKS);
        assert!(bodies(&mut outbox).is_empty());
        assert!(
            state
                .drain_host_events()
                .contains(&HostEvent::Confirmation(question.clone()))
        );
        state.command(answer(false), &mut outbox, true, CLOCKS);
        assert!(bodies(&mut outbox).is_empty(), "a no sends nothing");
        // A second use inside the 200 ms pacing is swallowed; later it asks again.
        let later = Clocks {
            session: CLOCKS.session + Duration::from_secs(1),
            ..CLOCKS
        };
        state.command(use_rare, &mut outbox, true, later);
        state.command(answer(true), &mut outbox, true, later);
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::inventory::build_use(1, RARE)]
        );
    }

    #[test]
    fn a_mana_stone_drain_is_asked_and_a_yes_sends_the_targeted_use() {
        // OpenAC v0.1.25 (#217)
        // `EmptyManaStone_OnlyConfirmedValidTargetIsSent`, through the
        // session: the question reaches the host, a yes comes back as a
        // command and the targeted use goes out.
        use crate::communication::ClientConfirmation;
        use ac_core::items::{ItemType, ItemUseable, PublicWeenieFlags};
        use ac_proto::{CreateObject, ModelData, PhysicsTimestamps, PublicWeenieDesc};
        const PLAYER: u32 = 0x5000_000A;
        const RARE: u32 = 0x6000_0010;
        const STONE: u32 = 0x6000_0011;
        let (mut state, mut outbox) = state();
        state.world.set_player(PLAYER);
        for (guid, desc) in [
            (
                PLAYER,
                PublicWeenieDesc {
                    name: "Horan".into(),
                    object_description_flags: PublicWeenieFlags::PLAYER.bits(),
                    ..PublicWeenieDesc::default()
                },
            ),
            (
                RARE,
                PublicWeenieDesc {
                    name: "Rare".into(),
                    item_type: ItemType::MISC.bits(),
                    useability: Some(ItemUseable::CONTAINED.bits()),
                    container_id: Some(PLAYER),
                    ..PublicWeenieDesc::default()
                },
            ),
            (
                STONE,
                PublicWeenieDesc {
                    name: "Mana Stone".into(),
                    item_type: ItemType::MANA_STONE.bits(),
                    useability: Some(0x0008_0008),
                    target_type: Some(ItemType::MISC.bits()),
                    container_id: Some(PLAYER),
                    ..PublicWeenieDesc::default()
                },
            ),
        ] {
            let message = Inbound::CreateObject(Box::new(CreateObject {
                guid,
                model: ModelData::default(),
                physics: crate::world::tests::physics(
                    PhysicsTimestamps {
                        instance: 1,
                        ..PhysicsTimestamps::default()
                    },
                    None,
                ),
                weenie: Some(desc),
            }));
            state.apply(&message, &mut outbox, true, CLOCKS);
        }
        state.drain_host_events();
        let item = |command| SessionCommand::Item(command);
        state.command(
            item(ItemCommand::Use(ObjectRef::Guid(STONE))),
            &mut outbox,
            true,
            CLOCKS,
        );
        state.command(
            item(ItemCommand::OfferTarget(ObjectRef::Guid(RARE))),
            &mut outbox,
            true,
            CLOCKS,
        );
        assert!(bodies(&mut outbox).is_empty(), "nothing before the answer");
        let question = ClientConfirmation::ManaStoneDrain {
            stone: STONE,
            target: RARE,
            message: "Are you sure you want to attempt to destroy your Rare and drain its mana into this stone?".into(),
        };
        assert!(
            state
                .drain_host_events()
                .contains(&HostEvent::Confirmation(question.clone()))
        );
        state.command(
            SessionCommand::AnswerConfirmation {
                confirmation: question,
                accepted: true,
            },
            &mut outbox,
            true,
            CLOCKS,
        );
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::inventory::build_use_with_target(1, STONE, RARE)]
        );
    }

    #[test]
    fn an_answer_naming_no_question_goes_to_the_dialog_on_top() {
        // The console's `/confirm yes|no` names no question: it answers the
        // newer of the client's own (`/die`) and the server's (a swear, an
        // abandon), with the server's kind and context echoed.
        let (mut state, mut outbox) = state();
        state.world.set_player(0x5000_000A);
        let answer = |accepted| SessionCommand::AnswerOpenConfirmation { accepted };
        // Nothing open: nothing goes out.
        state.command(answer(true), &mut outbox, true, CLOCKS);
        assert!(bodies(&mut outbox).is_empty());
        state.command(
            SessionCommand::Chat("/die".into()),
            &mut outbox,
            true,
            CLOCKS,
        );
        let body = crate::world::tests::game_event(
            GameEventType::CHARACTER_CONFIRMATION_REQUEST,
            &[1, 91, 0],
        );
        state.apply(&Inbound::parse(&body).unwrap(), &mut outbox, true, CLOCKS);
        // The server asked last: its question is answered first.
        state.command(answer(true), &mut outbox, true, CLOCKS);
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::social::build_confirmation_response(
                1, 1, 91, true
            )]
        );
        // Then the client's own: a no sends nothing and closes it.
        state.command(answer(false), &mut outbox, true, CLOCKS);
        assert!(bodies(&mut outbox).is_empty());
        assert!(state.communication.open_confirmation().is_none());
    }

    #[test]
    fn abandoning_a_house_is_asked_twice_and_only_two_yeses_send_it() {
        // OpenAC RuntimeClientCommandDispatcher.Execute (HouseAbandon): the
        // first question, then on a yes the second, then on a yes
        // ClientCommandRequests.BuildAbandonHouse (0x021F).
        use crate::communication::ClientConfirmation;
        let (mut state, mut outbox) = state();
        state.world.set_player(0x5000_000A);
        let ask = |state: &mut SessionState, outbox: &mut Outbox| {
            state.command(
                SessionCommand::Chat("/house abandon".into()),
                outbox,
                true,
                CLOCKS,
            );
            state.drain_host_events()
        };
        let answer = |accepted| SessionCommand::AnswerOpenConfirmation { accepted };
        // A no to the first question ends it.
        assert!(
            ask(&mut state, &mut outbox)
                .contains(&HostEvent::Confirmation(ClientConfirmation::AbandonHouse))
        );
        state.command(answer(false), &mut outbox, true, CLOCKS);
        assert!(bodies(&mut outbox).is_empty());
        assert!(state.communication.open_confirmation().is_none());
        // A yes asks again; a no to that sends nothing.
        ask(&mut state, &mut outbox);
        state.command(answer(true), &mut outbox, true, CLOCKS);
        assert!(state.drain_host_events().contains(&HostEvent::Confirmation(
            ClientConfirmation::AbandonHouseCertain
        )));
        assert!(bodies(&mut outbox).is_empty());
        state.command(answer(false), &mut outbox, true, CLOCKS);
        assert!(bodies(&mut outbox).is_empty());
        // Two yeses abandon it.
        ask(&mut state, &mut outbox);
        state.command(answer(true), &mut outbox, true, CLOCKS);
        state.command(answer(true), &mut outbox, true, CLOCKS);
        assert_eq!(
            bodies(&mut outbox),
            [ac_proto::house::build_abandon_house(1)]
        );
    }

    #[test]
    fn the_description_loads_the_shortcut_bar_and_a_block_repeats_it() {
        // OpenAC `GameEventWiring` hands the description's shortcuts to the
        // store; `CharacterOptionsBlobSource.Capture` reads the live store.
        use crate::inventory::ItemCommand;
        let (mut state, mut outbox) = state();
        state.world.set_player(0x5000_000B);
        let shortcuts = [ac_proto::Shortcut {
            index: 0,
            object_id: 0x6000_0001,
            spell_id: 0,
        }];
        state.inventory.load_shortcuts(&shortcuts);
        state.options.replace(0, 0, true);
        state.command(
            SessionCommand::Item(ItemCommand::AddShortcut(ac_core::items::ShortcutEntry {
                index: 4,
                object_id: 0x6000_0002,
                spell_id: 0,
            })),
            &mut outbox,
            true,
            CLOCKS,
        );
        bodies(&mut outbox);
        state.options.set_bit(option_id::DISPLAY_TIME_STAMPS, true);
        state.options.mark_dirty(CLOCKS.session);
        state.save_options(&mut outbox);
        let mut expected = CharacterOptions::new();
        expected.replace(state.options.options1(), state.options.options2(), true);
        expected.echo_shortcuts(state.inventory.shortcuts());
        assert_eq!(state.inventory.shortcuts().count(), 2);
        assert_eq!(bodies(&mut outbox), [expected.build_block(2)]);
    }
}

#[cfg(test)]
mod list_tests {
    //! The object list (`/objects`): every object the world holds, at the
    //! place OpenAC's `RuntimeWorldObjectProjection.Project` reads (the live
    //! body when the session's physics moves it, else the server's last
    //! position), nearest to the player first.

    use super::*;
    use crate::world::tests::{create, physics, position, stamps};
    use ac_proto::{CreateObject, Inbound, ModelData, PhysicsAttachment};

    const PLAYER: u32 = 0x5000_000A;

    fn place(world: &mut World, guid: u32, cell: u32, x: f32) {
        world.apply(&create(guid, stamps(1, 1), Some(position(cell, x))));
    }

    #[test]
    fn objects_list_nearest_first_and_unplaced_last() {
        let mut world = World::new();
        world.set_player(PLAYER);
        place(&mut world, PLAYER, 0xA9B4_0001, 10.0);
        place(&mut world, 0x7000_0003, 0xA9B4_0001, 40.0); // 30 m
        place(&mut world, 0x7000_0001, 0xA9B4_0001, 15.0); // 5 m
        place(&mut world, 0x7000_0004, 0xAAB4_0001, 10.0); // next landblock east: 192 m
        place(&mut world, 0x7000_0002, 0xA9B4_0001, 5.0); // 5 m the other way
        // Held by a parent: no place, listed last with its parent.
        let mut held = physics(stamps(1, 1), None);
        held.parent = Some(PhysicsAttachment {
            guid: 0x7000_0001,
            location_id: 1,
        });
        world.apply(&Inbound::CreateObject(Box::new(CreateObject {
            guid: 0x7000_0005,
            model: ModelData::default(),
            physics: held,
            weenie: None,
        })));

        let list = list_objects(&world);
        let order: Vec<u32> = list.iter().map(|o| o.guid).collect();
        assert_eq!(
            order,
            [
                PLAYER,
                0x7000_0001,
                0x7000_0002,
                0x7000_0003,
                0x7000_0004,
                0x7000_0005
            ],
            "nearest first, equal distances by id, unplaced last"
        );
        assert_eq!(list[0].distance, Some(0.0));
        assert_eq!(list[1].distance, Some(5.0));
        assert_eq!(list[3].distance, Some(30.0));
        assert_eq!(list[4].distance, Some(192.0));
        assert_eq!(list[5].distance, None);
        assert_eq!(list[5].parent, Some(0x7000_0001));
        assert_eq!(list[5].cell_id, None);
        assert_eq!(list[1].name, None, "no description, no name");
    }

    #[test]
    fn a_simulated_body_is_listed_where_it_is_now() {
        let mut world = World::new();
        world.set_player(PLAYER);
        place(&mut world, PLAYER, 0xA9B4_0001, 10.0);
        place(&mut world, 0x7000_0001, 0xA9B4_0001, 50.0);
        // The session's physics carried the body 20 m closer since the
        // server's last word.
        world.set_live_position(0x7000_0001, position(0xA9B4_0001, 30.0));
        let list = list_objects(&world);
        let body = list.iter().find(|o| o.guid == 0x7000_0001).unwrap();
        assert_eq!(body.origin, Some([30.0, 10.0, 0.0]));
        assert_eq!(body.distance, Some(20.0));
    }

    #[test]
    fn the_session_tick_forgets_an_object_out_of_range_for_twenty_five_seconds() {
        // OpenAC HeadlessEntityLivenessTests: a host without a window
        // expires on the same schedule.
        let mut state = SessionState::new(crate::communication::CommunicationPaths::default());
        let mut outbox = crate::session::Outbox::new();
        state.world.set_player(PLAYER);
        place(&mut state.world, PLAYER, 0x3032_0001, 10.0);
        place(&mut state.world, 0x7000_0001, 0xA9B4_0001, 10.0);
        place(&mut state.world, 0x7000_0002, 0x3133_0020, 10.0);
        let tick = |state: &mut SessionState, outbox: &mut crate::session::Outbox, s: u64| {
            state.tick(
                outbox,
                true,
                Clocks {
                    wall_unix_ms: 0,
                    session: Duration::from_secs(s),
                },
            );
        };
        // The far creature is a corpse: its death leaves with it (OpenAC
        // v0.1.25 2946647f
        // `VisibilityExpiryForgetsTheDepartedCreaturesDeath`).
        let incarnation = state.world.object(0x7000_0001).unwrap().key().incarnation;
        let death = Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
            guid: 0x7000_0001,
            instance_sequence: incarnation,
            movement_sequence: 2,
            server_control_sequence: 0,
            is_autonomous: false,
            movement: ac_proto::MovementData {
                movement_type: 0,
                motion_flags: 0,
                current_style: 0x003D,
                movement: ac_proto::Movement::Interpreted {
                    state: ac_proto::InterpretedMotionState {
                        forward_command: Some(0x0011),
                        ..ac_proto::InterpretedMotionState::default()
                    },
                    sticky_object: None,
                },
            },
        }));
        state.apply(&death, &mut outbox, true, Clocks::default());
        assert!(state.gameplay.deaths().is_dead(0x7000_0001));
        for second in 1..=24 {
            tick(&mut state, &mut outbox, second);
        }
        assert_eq!(state.world.object_count(), 3);
        tick(&mut state, &mut outbox, 25);
        tick(&mut state, &mut outbox, 26);
        assert!(state.world.object(0x7000_0001).is_none());
        assert!(state.world.object(0x7000_0002).is_some());
        assert!(state.world.object(PLAYER).is_some());
        assert!(!state.gameplay.deaths().is_dead(0x7000_0001));
    }

    #[test]
    fn with_no_player_object_every_distance_is_unknown() {
        let mut world = World::new();
        place(&mut world, 0x7000_0002, 0xA9B4_0001, 5.0);
        place(&mut world, 0x7000_0001, 0xA9B4_0001, 50.0);
        let list = list_objects(&world);
        assert!(list.iter().all(|o| o.distance.is_none()));
        assert_eq!(
            list.iter().map(|o| o.guid).collect::<Vec<_>>(),
            [0x7000_0001, 0x7000_0002]
        );
    }

    /// The player at the origin and two monsters placed, the first selected.
    fn selected_monster() -> (SessionState, Outbox) {
        let mut state = SessionState::new(CommunicationPaths::default());
        let outbox = Outbox::new();
        state.world.set_player(PLAYER);
        place(&mut state.world, PLAYER, 0xA9B4_0001, 10.0);
        let Inbound::CreateObject(mut monster) =
            create(0x7000_0001, stamps(1, 1), Some(position(0xA9B4_0001, 15.0)))
        else {
            unreachable!()
        };
        monster.weenie = Some(ac_proto::PublicWeenieDesc {
            name: "Drudge".into(),
            item_type: ac_core::items::ItemType::CREATURE.bits(),
            ..ac_proto::PublicWeenieDesc::default()
        });
        state.world.apply(&Inbound::CreateObject(monster));
        place(&mut state.world, 0x7000_0002, 0xA9B4_0001, 20.0);
        let mut state = state;
        let mut outbox = outbox;
        state.command(
            SessionCommand::Gameplay(crate::gameplay::GameplayCommand::Select(
                crate::inventory::ObjectRef::Guid(0x7000_0001),
            )),
            &mut outbox,
            true,
            Clocks::default(),
        );
        assert_eq!(state.gameplay.selection().selected(), Some(0x7000_0001));
        // Whatever the selection sent (a health query) is not the test's.
        outbox.drain().for_each(drop);
        (state, outbox)
    }

    #[test]
    fn the_selection_keys_that_need_the_inventory_too() {
        // OpenAC `SelectionInteractionController`: the self key offers the
        // character to a mode waiting for a target (here the examine mode,
        // which appraises it) and otherwise selects it; the use-corpse key
        // selects the nearest unopened corpse and uses it.
        use crate::gameplay::GameplayCommand as G;
        use crate::gameplay::selection_cycle::SelectionCycleDirection as D;
        const CORPSE: u32 = 0x7000_00C5;
        let clocks = Clocks::default();
        let (mut state, mut outbox) = selected_monster();
        state.command(
            SessionCommand::Item(ItemCommand::EnterExamineMode),
            &mut outbox,
            true,
            clocks,
        );
        state.command(
            SessionCommand::Gameplay(G::SelectSelf),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(state.gameplay.selection().selected(), Some(0x7000_0001));
        let sent: Vec<Vec<u8>> = outbox.drain().map(|o| o.body).collect();
        assert_eq!(sent, [ac_proto::appraise::build_appraise(1, PLAYER)]);
        state.command(
            SessionCommand::Gameplay(G::SelectSelf),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(state.gameplay.selection().selected(), Some(PLAYER));

        let Inbound::CreateObject(mut corpse) =
            create(CORPSE, stamps(1, 1), Some(position(0xA9B4_0001, 11.0)))
        else {
            unreachable!()
        };
        corpse.weenie = Some(ac_proto::PublicWeenieDesc {
            name: "Corpse of Drudge".into(),
            item_type: ac_core::items::ItemType::CONTAINER.bits(),
            object_description_flags: (ac_core::items::PublicWeenieFlags::CORPSE
                | ac_core::items::PublicWeenieFlags::OPENABLE)
                .bits(),
            useability: Some(ac_core::items::ItemUseable::REMOTE.bits()),
            items_capacity: Some(120),
            ..ac_proto::PublicWeenieDesc::default()
        });
        state.world.apply(&Inbound::CreateObject(corpse));
        outbox.drain().for_each(drop);
        state.command(
            SessionCommand::Gameplay(G::UseUnopenedCorpse(D::Closest)),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(state.gameplay.selection().selected(), Some(CORPSE));
        let sent: Vec<Vec<u8>> = outbox.drain().map(|o| o.body).collect();
        let used = |body: &Vec<u8>| {
            let sequence = u32::from_le_bytes([body[4], body[5], body[6], body[7]]);
            *body == ac_proto::inventory::build_use(sequence, CORPSE)
        };
        assert!(sent.iter().any(used), "{sent:?}");
    }

    #[test]
    fn the_selection_lets_go_of_an_object_that_leaves_the_world() {
        // OpenAC SelectionLifetimeParityTests over RuntimeSelectionEntityFollower:
        // a delete or a hide clears it, another object's delete and a
        // fresh incarnation of the selected one leave it alone.
        use crate::gameplay::SelectionReason;
        let clocks = Clocks::default();
        let (mut state, mut outbox) = selected_monster();
        let delete = |guid| {
            Inbound::DeleteObject(ac_proto::DeleteObject {
                guid,
                instance_sequence: 1,
            })
        };
        state.apply(&delete(0x7000_0002), &mut outbox, true, clocks);
        assert_eq!(state.gameplay.selection().selected(), Some(0x7000_0001));
        // Sent again as a new incarnation: still selected.
        state.apply(
            &create(0x7000_0001, stamps(2, 1), Some(position(0xA9B4_0001, 16.0))),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(state.gameplay.selection().selected(), Some(0x7000_0001));
        state.apply(
            &Inbound::DeleteObject(ac_proto::DeleteObject {
                guid: 0x7000_0001,
                instance_sequence: 2,
            }),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(state.gameplay.selection().selected(), None);
        assert_eq!(state.gameplay.selection().previous(), Some(0x7000_0001));

        // Hidden by its physics state.
        let (mut state, mut outbox) = selected_monster();
        state.apply(
            &Inbound::SetState(ac_proto::SetState {
                guid: 0x7000_0001,
                physics_state: ac_core::physics::PhysicsState::HIDDEN.bits(),
                instance_sequence: 1,
                state_sequence: 2,
            }),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(state.gameplay.selection().selected(), None);
        let _ = SelectionReason::Cleared;

        // Picked up out of the world.
        let (mut state, mut outbox) = selected_monster();
        state.apply(
            &Inbound::PickupEvent(ac_proto::PickupEvent {
                guid: 0x7000_0001,
                instance_sequence: 1,
                position_sequence: 2,
            }),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(state.gameplay.selection().selected(), None);
    }

    #[test]
    fn the_use_examine_and_pickup_keys_act_on_the_selection() {
        // OpenAC RuntimeItemInteraction.UseSelectedOrEnterMode,
        // ExamineSelectedOrEnterMode and DirectGameRuntimeCommandAdapter.PickUpSelected
        // (a creature is refused).
        use crate::inventory::InteractionMode;
        let clocks = Clocks::default();
        let mut state = SessionState::new(CommunicationPaths::default());
        let mut outbox = Outbox::new();
        // Nothing selected: the modes wait for a click.
        state.command(
            SessionCommand::Item(ItemCommand::UseSelected),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(state.inventory.interaction_mode(), InteractionMode::Use);
        state.command(
            SessionCommand::Item(ItemCommand::ExamineSelected),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(state.inventory.interaction_mode(), InteractionMode::Examine);
        // The pick-up key with nothing selected says so
        // (`SelectionInteractionController.HandleInputAction`); the
        // place-in-inventory key does nothing.
        state.command(
            SessionCommand::Item(ItemCommand::PickUpSelected),
            &mut outbox,
            true,
            clocks,
        );
        state.command(
            SessionCommand::Item(ItemCommand::PlaceSelectedInPack),
            &mut outbox,
            true,
            clocks,
        );
        assert!(outbox.is_empty());
        assert_eq!(
            status_lines(&mut state),
            [crate::inventory::pickup::NOTHING_SELECTED]
        );
        // A creature selected is never picked up, in the game's words
        // (`SelectionInteractionController.ValidatePickupTarget`); examining
        // it appraises it.
        let (mut state, mut outbox) = selected_monster();
        state.command(
            SessionCommand::Item(ItemCommand::PickUpSelected),
            &mut outbox,
            true,
            clocks,
        );
        assert!(outbox.is_empty());
        assert_eq!(
            status_lines(&mut state),
            [crate::inventory::pickup::CANNOT_PICK_UP_CREATURES]
        );
        state.command(
            SessionCommand::Item(ItemCommand::ExamineSelected),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(
            outbox.drain().map(|o| o.body).collect::<Vec<_>>(),
            [ac_proto::appraise::build_appraise(1, 0x7000_0001)]
        );
    }

    /// The status box's lines, newest first (a tick shows what waits).
    fn status_lines(state: &mut SessionState) -> Vec<String> {
        state.tick(&mut Outbox::new(), true, Clocks::default());
        state
            .communication
            .spew_box()
            .visible()
            .iter()
            .map(|e| e.text.clone())
            .collect()
    }

    /// [`selected_monster`] with a gem in the player's main pack.
    const GEM: u32 = 0x6000_0001;
    fn monster_and_gem() -> (SessionState, Outbox) {
        let (mut state, outbox) = selected_monster();
        let Inbound::CreateObject(mut gem) = create(GEM, stamps(1, 1), None) else {
            unreachable!()
        };
        gem.weenie = Some(ac_proto::PublicWeenieDesc {
            name: "Gem".into(),
            item_type: ac_core::items::ItemType::GEM.bits(),
            container_id: Some(PLAYER),
            stack_size: Some(5),
            max_stack_size: Some(100),
            ..ac_proto::PublicWeenieDesc::default()
        });
        state.world.apply(&Inbound::CreateObject(gem));
        (state, outbox)
    }

    fn key(state: &mut SessionState, outbox: &mut Outbox, item: ItemCommand) {
        state.command(SessionCommand::Item(item), outbox, true, Clocks::default());
    }

    fn select(state: &mut SessionState, outbox: &mut Outbox, guid: u32) {
        let select = GameplayCommand::Select(crate::inventory::ObjectRef::Guid(guid));
        state.command(
            SessionCommand::Gameplay(select),
            outbox,
            true,
            Clocks::default(),
        );
        outbox.drain().for_each(drop);
    }

    #[test]
    fn the_give_key_gives_the_selection_to_the_creature_selected_before_it() {
        // OpenAC `SelectionInteractionController.GiveSelectionToPreviousTarget`
        // (`SelectionGiveToTarget`): the selection is placed on the previous
        // selection as a drag onto it would be (`PlaceSelectedIn3D`, a gift
        // of the split amount), and the creature becomes the selection.
        let (mut state, mut outbox) = monster_and_gem();
        select(&mut state, &mut outbox, GEM);
        assert_eq!(state.gameplay.selection().previous(), Some(0x7000_0001));
        key(
            &mut state,
            &mut outbox,
            ItemCommand::GiveSelectedToPrevious { amount: 2 },
        );
        assert_eq!(
            outbox.drain().map(|o| o.body).collect::<Vec<_>>(),
            [ac_proto::inventory::build_give_object(
                1,
                0x7000_0001,
                GEM,
                2
            )]
        );
        assert_eq!(state.gameplay.selection().selected(), Some(0x7000_0001));
        assert!(status_lines(&mut state).is_empty());
    }

    #[test]
    fn a_pickup_that_joins_a_carried_stack_selects_that_stack() {
        // OpenAC `InventoryController.OnMergeAttempted` (every merge the
        // runtime sends selects the stack it joins), from the game's merge
        // attempt, which selects the target after sending the merge. The
        // loose gem picked up leaves the world; the joined stack stays the
        // selection and its new size is what the selection shows.
        const LOOSE: u32 = 0x6000_0002;
        let (mut state, mut outbox) = monster_and_gem();
        let Inbound::CreateObject(mut loose) =
            create(LOOSE, stamps(1, 1), Some(position(0xA9B4_0001, 11.0)))
        else {
            unreachable!()
        };
        loose.weenie = Some(ac_proto::PublicWeenieDesc {
            name: "Gem".into(),
            item_type: ac_core::items::ItemType::GEM.bits(),
            stack_size: Some(1),
            max_stack_size: Some(100),
            ..ac_proto::PublicWeenieDesc::default()
        });
        state.world.apply(&Inbound::CreateObject(loose));
        select(&mut state, &mut outbox, LOOSE);
        key(&mut state, &mut outbox, ItemCommand::PickUpSelected);
        assert_eq!(
            outbox.drain().map(|o| o.body).collect::<Vec<_>>(),
            [ac_proto::inventory::build_stackable_merge(1, LOOSE, GEM, 1)]
        );
        assert_eq!(state.gameplay.selection().selected(), Some(GEM));
        // The server's answer: the loose stack goes, the carried one grows.
        let clocks = Clocks::default();
        for message in [
            Inbound::InventoryRemoveObject(ac_proto::InventoryRemoveObject { guid: LOOSE }),
            Inbound::DeleteObject(ac_proto::DeleteObject {
                guid: LOOSE,
                instance_sequence: 1,
            }),
            Inbound::SetStackSize(ac_proto::SetStackSize {
                sequence: 1,
                guid: GEM,
                stack_size: 6,
                value: 60,
            }),
        ] {
            state.apply(&message, &mut outbox, true, clocks);
        }
        assert_eq!(state.gameplay.selection().selected(), Some(GEM));
        assert_eq!(state.world.item(GEM).map(|g| g.stack_size), Some(6));
    }

    #[test]
    fn the_give_key_without_a_creature_before_the_selection_says_so() {
        // OpenAC `GiveSelectionToPreviousTarget`: nothing selected, nothing
        // before it, the same object, or something that is not a creature
        // (here the gem itself, and an unnamed object) is refused with the
        // retail line (sent with its newline, which the status box drops);
        // nothing is sent.
        assert_eq!(
            crate::inventory::pickup::GIVE_NEEDS_A_CREATURE,
            "You must select a creature or a character to give that to.\n"
        );
        let said = "You must select a creature or a character to give that to.";
        // Nothing selected.
        let (mut state, mut outbox) = monster_and_gem();
        state.command(
            SessionCommand::Gameplay(GameplayCommand::ClearSelection),
            &mut outbox,
            true,
            Clocks::default(),
        );
        key(
            &mut state,
            &mut outbox,
            ItemCommand::GiveSelectedToPrevious { amount: 0 },
        );
        assert!(outbox.is_empty());
        assert_eq!(status_lines(&mut state), [said]);
        // The object before it is no creature.
        let (mut state, mut outbox) = monster_and_gem();
        select(&mut state, &mut outbox, 0x7000_0002);
        select(&mut state, &mut outbox, GEM);
        key(
            &mut state,
            &mut outbox,
            ItemCommand::GiveSelectedToPrevious { amount: 0 },
        );
        assert!(outbox.is_empty());
        assert_eq!(status_lines(&mut state), [said]);
        assert_eq!(state.gameplay.selection().selected(), Some(GEM));
    }

    #[test]
    fn the_drop_key_drops_what_the_player_carries_and_refuses_the_rest() {
        // OpenAC `SelectionInteractionController.DropSelection`
        // (`SelectionDrop`): nothing selected does nothing; what the player
        // does not carry is refused with the retail line; what it carries
        // is placed on the ground (`PlaceSelectedIn3D` with no target).
        let (mut state, mut outbox) = monster_and_gem();
        key(
            &mut state,
            &mut outbox,
            ItemCommand::DropSelected { amount: 0 },
        );
        assert!(outbox.is_empty());
        assert_eq!(status_lines(&mut state), ["You must pick that up first"]);

        let (mut state, mut outbox) = monster_and_gem();
        state.command(
            SessionCommand::Gameplay(GameplayCommand::ClearSelection),
            &mut outbox,
            true,
            Clocks::default(),
        );
        key(
            &mut state,
            &mut outbox,
            ItemCommand::DropSelected { amount: 0 },
        );
        assert!(outbox.is_empty());
        assert!(status_lines(&mut state).is_empty());

        let (mut state, mut outbox) = monster_and_gem();
        select(&mut state, &mut outbox, GEM);
        key(
            &mut state,
            &mut outbox,
            ItemCommand::DropSelected { amount: 0 },
        );
        assert_eq!(
            outbox.drain().map(|o| o.body).collect::<Vec<_>>(),
            [ac_proto::inventory::build_drop_item(1, GEM)]
        );
        assert!(status_lines(&mut state).is_empty());
    }

    #[test]
    fn escape_cancels_the_target_mode_then_the_selection_then_opens_gameplay_options() {
        // OpenAC GameplayInputCommandControllerTests
        // .Escape_PreservesRetailTargetThenGameplayOptionsPriority, with the
        // selection controller's deselect between them
        // (SelectionInteractionController.HandleInputAction). No body: it
        // stands still and charges no jump.
        use crate::communication::WindowRequest;
        use crate::inventory::InteractionMode;
        let clocks = Clocks::default();
        let escape = SessionCommand::Gameplay(GameplayCommand::Escape);
        let (mut state, mut outbox) = selected_monster();
        state.command(
            SessionCommand::Item(ItemCommand::EnterExamineMode),
            &mut outbox,
            true,
            clocks,
        );
        assert_eq!(state.inventory.interaction_mode(), InteractionMode::Examine);
        // The target mode goes first; the selection stays.
        state.command(escape.clone(), &mut outbox, true, clocks);
        assert_eq!(state.inventory.interaction_mode(), InteractionMode::None);
        assert!(state.gameplay.selection().selected().is_some());
        assert!(state.communication.take_window_requests().is_empty());
        // Then the selection.
        state.command(escape.clone(), &mut outbox, true, clocks);
        assert_eq!(state.gameplay.selection().selected(), None);
        assert!(state.communication.take_window_requests().is_empty());
        // Then the options window's gameplay page.
        state.command(escape, &mut outbox, true, clocks);
        assert_eq!(
            state.communication.take_window_requests(),
            [WindowRequest::GameplayOptionsPage]
        );
    }

    #[test]
    fn every_owner_forgets_the_session_when_its_generation_ends() {
        // OpenAC RuntimeGameplayOwnershipTests: populated owners converge to
        // empty when the session's life ends (here: the generation's end,
        // which leaves the world and empties it).
        let words = |w: &[u32]| w.iter().flat_map(|x| x.to_le_bytes()).collect::<Vec<u8>>();
        let event = |kind: GameEventType, payload: &[u8]| {
            let mut body = words(&[ac_proto::Opcode::GameEvent.value(), PLAYER, 1, kind.0]);
            body.extend_from_slice(payload);
            body
        };
        let clocks = Clocks::default();
        let (mut state, mut outbox) = selected_monster();
        for body in [
            event(GameEventType::CHARACTER_TITLE, &words(&[1, 13, 1, 13])),
            event(
                GameEventType::QUERY_ITEM_MANA_RESPONSE,
                &words(&[0x7000_0002, 0.5f32.to_bits(), 1]),
            ),
        ] {
            state.apply(&Inbound::parse(&body).unwrap(), &mut outbox, true, clocks);
        }
        state.command(
            SessionCommand::Item(ItemCommand::AddShortcut(ac_core::items::ShortcutEntry {
                index: 0,
                object_id: 0x7000_0002,
                spell_id: 0,
            })),
            &mut outbox,
            true,
            clocks,
        );
        state.options.set_bit(option_id::AUTO_TARGET, false);
        assert_eq!(state.gameplay.titles().count(), 1);
        assert_eq!(state.inventory.item_mana().count(), 1);
        assert_eq!(state.inventory.shortcuts().count(), 1);
        assert!(state.gameplay.selection().selected().is_some());
        assert!(state.world.object_count() > 0);

        state.end_generation(0);

        assert_eq!(state.gameplay.titles().count(), 0);
        assert_eq!(state.inventory.item_mana().count(), 0);
        assert_eq!(state.inventory.shortcuts().count(), 0);
        assert_eq!(state.gameplay.selection().selected(), None);
        assert_eq!(state.gameplay.selection().previous(), None);
        assert!(!state.gameplay.fellowship().is_in_fellowship());
        assert_eq!(state.world.object_count(), 0);
        assert_eq!(state.options.options1(), crate::character::DEFAULT_OPTIONS1);
        assert!(!state.options.has_server_seed());
        assert_eq!(state.communication.contracts().len(), 0);
    }

    #[test]
    fn a_selected_items_mana_query_goes_out_and_its_answers_reach_the_window() {
        // OpenAC `SelectedObjectController` over `WorldSession.SendQueryItemMana`
        // (`SocialActions.BuildQueryItemMana`) and `ItemManaState`: the strip's
        // query is sent; each answer is told to it (`ItemManaChanged`) and its
        // fill reads the selection's known mana (`GetManaPercent`).
        use crate::inventory::ObjectRef;
        use crate::session::interface::{InterfaceEvent, InterfaceFeed, SelectedMana};
        const WAND: u32 = 0x7000_0002;
        let words = |w: &[u32]| w.iter().flat_map(|x| x.to_le_bytes()).collect::<Vec<u8>>();
        let answer = |valid: u32, fraction: f32| {
            let mut body = words(&[
                ac_proto::Opcode::GameEvent.value(),
                PLAYER,
                1,
                GameEventType::QUERY_ITEM_MANA_RESPONSE.0,
            ]);
            body.extend(words(&[WAND, fraction.to_bits(), valid]));
            body
        };
        let clocks = Clocks::default();
        let (mut state, mut outbox) = selected_monster();
        state.command(
            SessionCommand::Gameplay(crate::gameplay::GameplayCommand::Select(ObjectRef::Guid(
                WAND,
            ))),
            &mut outbox,
            true,
            clocks,
        );
        outbox.drain().for_each(drop);
        let mut feed = InterfaceFeed::default();
        feed.snapshot(&state, true);

        state.command(
            SessionCommand::Item(ItemCommand::QueryMana(ObjectRef::Guid(WAND))),
            &mut outbox,
            true,
            clocks,
        );
        let sent: Vec<Vec<u8>> = outbox.drain().map(|o| o.body).collect();
        let sequence = u32::from_le_bytes(sent[0][4..8].try_into().unwrap());
        assert_eq!(
            sent,
            [ac_proto::magic::build_query_item_mana(sequence, WAND)]
        );

        let body = answer(1, 0.75);
        state.apply(&Inbound::parse(&body).unwrap(), &mut outbox, true, clocks);
        let events = state.drain_host_events();
        let told = feed.follow(&state, true, &events);
        let change = ac_core::items::ItemManaChange {
            item_id: WAND,
            fraction: 0.75,
            valid: true,
        };
        assert!(told.contains(&InterfaceEvent::ItemMana(change)), "{told:?}");
        assert!(
            told.iter().any(|e| matches!(e, InterfaceEvent::State(s)
                if s.selected_mana == Some(SelectedMana { object_id: WAND, fraction: 0.75 }))),
            "{told:?}"
        );

        // An invalid answer is told too, and the selection's mana is gone.
        let body = answer(0, 0.0);
        state.apply(&Inbound::parse(&body).unwrap(), &mut outbox, true, clocks);
        let events = state.drain_host_events();
        let told = feed.follow(&state, true, &events);
        assert!(
            told.iter()
                .any(|e| matches!(e, InterfaceEvent::ItemMana(c) if !c.valid)),
            "{told:?}"
        );
        assert!(
            told.iter()
                .any(|e| matches!(e, InterfaceEvent::State(s) if s.selected_mana.is_none())),
            "{told:?}"
        );
    }

    #[test]
    fn a_dismissed_ghost_leaves_alone_and_only_once() {
        // OpenAC GhostDismissalParityTests.DismissingAGhostAnswersTheSameAndDropsTheSameObject
        // over RuntimeGhostDismissal.Dismiss: the object goes as a server
        // delete would take it, another stays, a second dismissal and the
        // character are refused, nothing goes to the server.
        const MONSTER: u32 = 0x7000_0001;
        const SECOND: u32 = 0x7000_0002;
        let mut state = SessionState::new(CommunicationPaths::default());
        let mut outbox = Outbox::new();
        state.world.set_player(PLAYER);
        place(&mut state.world, PLAYER, 0xA9B4_0001, 10.0);
        place(&mut state.world, MONSTER, 0xA9B4_0001, 15.0);
        place(&mut state.world, SECOND, 0xA9B4_0001, 20.0);
        let clocks = Clocks::default();
        assert!(state.dismiss_ghost(MONSTER, &mut outbox, true, clocks));
        assert!(state.world.object(MONSTER).is_none());
        assert!(state.world.object(SECOND).is_some());
        assert!(!state.dismiss_ghost(MONSTER, &mut outbox, true, clocks));
        assert!(!state.dismiss_ghost(PLAYER, &mut outbox, true, clocks));
        assert!(state.world.object(PLAYER).is_some());
        assert!(!state.dismiss_ghost(0, &mut outbox, true, clocks));
        assert!(outbox.is_empty());
        state.command(
            SessionCommand::DismissGhost(MONSTER),
            &mut outbox,
            true,
            clocks,
        );
        assert!(
            state
                .drain_host_events()
                .iter()
                .any(|e| matches!(e, HostEvent::CommandRefused(_)))
        );
    }
}
