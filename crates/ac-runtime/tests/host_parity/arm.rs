//! One of the two arms (OpenAC `ParityArm`, `WindowedArm`,
//! `WindowlessArm`): a whole session against its own loopback server, with
//! or without a window attached, driven through the `parity` plugin.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};

use ac_plugin::abi::{Call, Event, Wire};
use ac_plugin::{
    CALL_DEADLINE, HostVersion, PluginEngine, PluginSessionOptions, PluginSessionSettings,
    TimeLimits,
};
use ac_runtime::communication::ChatLine;
use ac_runtime::plugins::{PluginEvent, PluginSetup};
use ac_runtime::session::interface::InterfaceEvent;
use ac_runtime::session::{
    CharacterSelector, HostEvent, LoginAutomation, NetDiagnostics, PresentationEvent,
    SessionCommand, SessionConfig, SessionHandle, spawn,
};
use ac_runtime::world::transit::{DestinationReadiness, DestinationShown, PortalSpaceEvent};

use super::server::{Outbound, Server};
use super::transcript::{Difference, Transcript, compare};
use super::world;

/// The plugin every arm runs: the relay.
pub const PARITY: &str = "openac.parity";

/// Comfortably past the pacing between two uses (200 ms), as OpenAC's
/// scenarios wait 40 ticks of 15 ms.
pub const PACING: Duration = Duration::from_millis(600);

/// The words both arms are configured to be found by (OpenAC
/// `ParityArm.ConfiguredPluginTags`).
pub const CONFIGURED_PLUGIN_TAGS: [&str; 2] = ["parity-tag", "parity-second"];

/// What a scenario asks of its arms beyond the defaults.
#[derive(Clone, Default)]
pub struct Setup {
    /// More copies of the relay, by id (each answers its own verb).
    pub more_plugins: Vec<&'static str>,
    /// Per-plugin startup settings (the session document's
    /// `pluginSettings`).
    pub settings: BTreeMap<String, BTreeMap<String, String>>,
    /// The game tables the session reads (`None`: none, as with no dats).
    pub tables: Option<Arc<ac_runtime::gameplay::GameTables>>,
    /// The character's description, said on arrival.
    pub description: super::wire::Description,
    /// More characters on the account, after the one played.
    pub more_characters: Vec<(u32, &'static str)>,
    /// The session loads the retail dats (collision, the character's
    /// shape and motion, the game tables): the character has a body.
    pub dats: bool,
    /// The session document's login commands.
    pub login_commands: Vec<String>,
}

/// Plays `script` on a windowed arm and a windowless arm, each in the
/// world, and compares what each wrote down (OpenAC `ParityScenario.Run`).
pub fn run(script: impl Fn(&mut Arm, &mut Transcript) + Sync) {
    run_with(&Setup::default(), &[], true, script);
}

/// The same, for a scenario about the arrival itself: the script runs with
/// the character not yet in and walks it in with [`Arm::enter_world`]
/// (OpenAC `ParityScenario.RunFromLogin`).
pub fn run_from_login(script: impl Fn(&mut Arm, &mut Transcript) + Sync) {
    run_with(&Setup::default(), &[], false, script);
}

/// [`run`] with a setup and the differences the scenario allows.
pub fn run_with(
    setup: &Setup,
    expected: &[Difference],
    enter_world: bool,
    script: impl Fn(&mut Arm, &mut Transcript) + Sync,
) {
    let play = |windowed: bool| {
        let mut arm = Arm::start(windowed, setup);
        let mut transcript = Transcript::new();
        if enter_world {
            arm.enter_world();
            // Arriving is itself something a client does out loud; what a
            // scenario reads off the wire afterwards is its own.
            let _ = arm.outbound();
            arm.mark_events();
        }
        script(&mut arm, &mut transcript);
        arm.finish();
        transcript
    };
    let (windowed, windowless) = std::thread::scope(|scope| {
        let windowed = scope.spawn(|| play(true));
        let windowless = scope.spawn(|| play(false));
        (windowed.join(), windowless.join())
    });
    let windowed = windowed.unwrap_or_else(|panic| std::panic::resume_unwind(panic));
    let windowless = windowless.unwrap_or_else(|panic| std::panic::resume_unwind(panic));
    compare(&windowed, &windowless, expected);
}

/// The engine every session of the test process shares (as the program's
/// sessions do).
///
/// Its slow-call warning is moved out to the call deadline. The warning is
/// a wall-clock diagnostic: on a loaded machine a call of the relay can
/// pass 100 ms on one arm and not the other, and the warning's chat line
/// would then appear in one transcript only. What the arms are compared on
/// is what the plugins were told, not how fast this machine ran them; the
/// deadline that stops a stuck plugin is kept as it is.
fn engine() -> Arc<PluginEngine> {
    static ENGINE: OnceLock<Arc<PluginEngine>> = OnceLock::new();
    Arc::clone(ENGINE.get_or_init(|| {
        Arc::new(PluginEngine::with_time_limits(TimeLimits {
            slow_call: CALL_DEADLINE,
            ..TimeLimits::default()
        }))
    }))
}

/// A fresh scratch folder for one arm.
fn scratch(tag: &str) -> PathBuf {
    static NEXT: AtomicU32 = AtomicU32::new(0);
    let n = NEXT.fetch_add(1, Ordering::Relaxed);
    let dir = std::env::temp_dir().join(format!("openac-parity-{}-{n}-{tag}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

/// Installs the relay into `root` under `id`.
fn install_relay(root: &std::path::Path, id: &str) {
    let folder = root.join(id);
    std::fs::create_dir_all(&folder).unwrap();
    let manifest =
        std::fs::read_to_string(ac_plugin::fixtures::source_directory().join("parity/plugin.json"))
            .unwrap()
            .replace("\"openac.parity\"", &format!("\"{id}\""));
    std::fs::write(folder.join("plugin.json"), manifest).unwrap();
    std::fs::copy(
        ac_plugin::fixtures::wasm_directory().join("parity.wasm"),
        folder.join("parity.wasm"),
    )
    .unwrap();
}

/// The line that has the relay `plugin` call `C` with `request`, its answer
/// logged under `seq`.
pub fn call_line<C: Call>(plugin: &str, seq: u32, request: &C::Request) -> String {
    let hex: String = request
        .to_bytes()
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect();
    format!("/{} call {seq} {} {hex}", verb_of(plugin), C::IMPORT.name())
}

/// The verb a copy of the relay answers to.
pub fn verb_of(id: &str) -> String {
    id.strip_prefix("openac.")
        .unwrap_or(id)
        .chars()
        .filter(char::is_ascii_alphanumeric)
        .collect()
}

pub struct Arm {
    /// Whether a window is attached.
    pub windowed: bool,
    pub server: Server,
    handle: Option<SessionHandle>,
    commands: Sender<SessionCommand>,
    window: Option<Receiver<PresentationEvent>>,
    /// Everything the stand-in window was sent (the windowed arm only).
    pub presented: Vec<PresentationEvent>,
    /// Plugin log lines, in order: (plugin id, message).
    pub logs: Vec<(String, String)>,
    /// Chat lines the host heard.
    pub chat: Vec<ChatLine>,
    /// Every other host event, in order.
    pub host_events: Vec<HostEvent>,
    /// Where the scenario's reading of events resumes (an index into
    /// [`Self::logs`]).
    events_from: usize,
    root: PathBuf,
    seq: u32,
    syncs: u32,
    in_world: bool,
    description: super::wire::Description,
    /// The session loads the retail dats.
    pub dats: bool,
}

impl Arm {
    pub fn name(&self) -> &'static str {
        if self.windowed {
            "windowed"
        } else {
            "windowless"
        }
    }

    /// The folder this arm's peer notes go in (OpenAC `PeerDirectory`).
    pub fn peer_directory(&self) -> PathBuf {
        self.root.join("plugin-peers")
    }

    /// Starts the session and logs it in up to the character's arrival.
    fn start(windowed: bool, setup: &Setup) -> Self {
        let root = scratch(if windowed { "windowed" } else { "windowless" });
        let plugins = root.join("plugins");
        install_relay(&plugins, PARITY);
        for id in &setup.more_plugins {
            install_relay(&plugins, id);
        }
        let mut server = Server::bind();
        let config = SessionConfig {
            server: server.address(),
            account: "parity".into(),
            password: "parity".into(),
            character: CharacterSelector::Name(world::PLAYER_NAME.into()),
            loss: ac_net::LossOptions::default(),
            enter_world: true,
            await_character_selection: false,
            data_directory: None,
            tables: setup
                .tables
                .clone()
                .or_else(|| setup.dats.then(super::dats::tables)),
            content: setup.dats.then(super::dats::content),
            diagnostics: NetDiagnostics::default(),
            login: LoginAutomation {
                commands: setup.login_commands.clone(),
                ..LoginAutomation::default()
            },
            plugins: Some(PluginSetup {
                engine: engine(),
                peer_tags: CONFIGURED_PLUGIN_TAGS
                    .iter()
                    .map(|t| (*t).to_owned())
                    .collect(),
                hotkey_overrides: None,
                peer_directory: Some(root.join("plugin-peers")),
                options: PluginSessionOptions {
                    roots: vec![plugins],
                    allow_list: None,
                    settings: PluginSessionSettings::from_map(&setup.settings),
                    host_version: HostVersion::new(0, 1, 0),
                    storage: ac_plugin::storage::StorageRoots {
                        plugin_data: Some(root.join("plugin-data")),
                        profiles: None,
                    },
                    slow_log: None,
                },
            }),
        };
        let handle = spawn(config).unwrap();
        let commands = handle.commands();
        let window = windowed.then(|| {
            let (to_window, window) = mpsc::channel();
            commands.send(SessionCommand::Attach(to_window)).unwrap();
            window
        });
        let mut characters = vec![(world::PLAYER, world::PLAYER_NAME)];
        characters.extend(setup.more_characters.iter().copied());
        if let Err(why) = server.log_in(&characters) {
            let mut events = Vec::new();
            while let Some(event) = handle.next_event(Duration::from_millis(50)) {
                events.push(event);
            }
            panic!(
                "{}: the login did not get through: {why}\nthe session said: {events:#?}",
                if windowed { "windowed" } else { "windowless" },
            );
        }
        let mut arm = Self {
            windowed,
            server,
            handle: Some(handle),
            commands,
            window,
            presented: Vec::new(),
            logs: Vec::new(),
            chat: Vec::new(),
            host_events: Vec::new(),
            events_from: 0,
            root,
            seq: 0,
            syncs: 0,
            in_world: false,
            description: setup.description.clone(),
            dats: setup.dats,
        };
        arm.until("the relay is loaded", |a| {
            a.logs
                .iter()
                .any(|(p, m)| p == PARITY && m.starts_with("parity enable"))
        });
        let _ = arm.server.take();
        // The arm is what it says it is.
        let attached = arm.call::<ac_plugin::abi::calls::HostWindowAttached>(&());
        assert_eq!(
            attached, windowed,
            "the window is attached on the windowed arm only"
        );
        arm
    }

    /// The character's own object arrives: the client says its login is
    /// complete and the plugins hear the arrival.
    pub fn enter_world(&mut self) {
        self.arrive_at(world::PLAYER_X, world::PLAYER_Y);
        self.until("the plugins hear the arrival", |a| {
            a.parity_events().any(|e| matches!(e, Event::LoginComplete))
        });
        self.in_world = true;
        self.sync();
    }

    /// The character's own object arrives at (`x`, `y`) in the scenarios'
    /// landblock, its description after it, as a server sends them on
    /// every login; nothing waits for the arrival.
    pub fn arrive_at(&mut self, x: f32, y: f32) {
        let mut player = world::player_object();
        if let Some((_, px, py, _)) = player.position.as_mut() {
            *px = x;
            *py = y;
        }
        let mut player = self.grounded(player);
        if self.dats {
            // A body that stands on the ground and moves as a human does.
            player.setup = Some(super::dats::HUMAN_SETUP);
            player.motion_table = Some(super::dats::HUMAN_MOTION);
            player.physics_state = super::dats::BODY_STATE;
        }
        self.server.send(&player.build());
        self.server
            .game_event(world::PLAYER, 0x0013, &self.description.build());
    }

    /// `object` standing on the ground where it is: with the dats, on the
    /// terrain's height there, in its own outdoor cell; without them, on
    /// the flat ground the scenarios assume (OpenAC's harness lays one).
    pub fn grounded(&self, mut object: super::wire::Object) -> super::wire::Object {
        if self.dats
            && let Some((cell, x, y, z)) = object.position.as_mut()
        {
            let landblock = *cell & 0xFFFF_0000;
            *z = super::dats::ground_z(landblock, *x, *y);
            *cell = world::outdoor_cell(landblock, *x, *y);
        }
        object
    }

    /// The server creates `object`, standing on the ground.
    pub fn say_grounded(&mut self, object: &super::wire::Object) {
        let object = self.grounded(object.clone());
        self.server.send(&object.build());
    }

    /// Reads what the session, the window and the server have for us, once.
    fn pump(&mut self) {
        self.server.poll();
        self.server.repair();
        if let Some(handle) = &self.handle
            && let Some(event) = handle.next_event(Duration::from_millis(2))
        {
            self.take(event);
            while let Some(event) = self
                .handle
                .as_ref()
                .and_then(|h| h.next_event(Duration::ZERO))
            {
                self.take(event);
            }
        }
        let mut shown = Vec::new();
        if let Some(window) = &self.window {
            while let Ok(event) = window.try_recv() {
                // A window without a drawn world has nothing to load or
                // show: the held arrival goes at once (`ac-render`
                // `surface.rs`).
                if let PresentationEvent::PortalSpace(PortalSpaceEvent::Destination {
                    generation,
                    teleport_sequence,
                    destination_cell,
                }) = &event
                {
                    shown.push(DestinationShown {
                        teleport_sequence: *teleport_sequence,
                        readiness: DestinationReadiness::immediate(*generation, *destination_cell),
                    });
                }
                self.presented.push(event);
            }
        }
        for shown in shown {
            let _ = self.commands.send(SessionCommand::DestinationShown(shown));
        }
    }

    fn take(&mut self, event: HostEvent) {
        match event {
            HostEvent::Chat(line) => self.chat.push(line),
            HostEvent::Plugin(PluginEvent::Log {
                plugin, message, ..
            }) => self.logs.push((plugin, message)),
            other => self.host_events.push(other),
        }
    }

    /// Pumps until `done` holds.
    pub fn until(&mut self, what: &str, done: impl Fn(&Self) -> bool) {
        let deadline = Instant::now() + Duration::from_secs(20);
        while !done(self) {
            assert!(
                Instant::now() < deadline,
                "{}: timed out waiting until {what}\nlogs: {:#?}\nchat: {:#?}",
                self.name(),
                self.logs,
                self.chat.iter().map(|c| &c.text).collect::<Vec<_>>()
            );
            self.pump();
        }
    }

    /// Pumps for `duration` of real time.
    pub fn wait(&mut self, duration: Duration) {
        let end = Instant::now() + duration;
        while Instant::now() < end {
            self.pump();
        }
    }

    /// Hands the session a command (as a window or console would).
    pub fn command(&mut self, command: SessionCommand) {
        self.commands.send(command).unwrap();
    }

    /// Types a line into the chat box.
    pub fn type_line(&mut self, line: &str) {
        self.command(SessionCommand::Chat(line.to_owned()));
    }

    /// Calls a client function through the relay, as a plugin does, and
    /// returns its answer.
    pub fn call<C: Call>(&mut self, request: &C::Request) -> C::Reply {
        self.call_as::<C>(PARITY, request)
    }

    /// [`Self::call`] through another copy of the relay.
    pub fn call_as<C: Call>(&mut self, plugin: &str, request: &C::Request) -> C::Reply {
        self.seq += 1;
        let seq = self.seq;
        self.type_line(&call_line::<C>(plugin, seq, request));
        let reply = format!("parity reply {seq} ");
        let refused = format!("parity refused {seq} ");
        self.until(&format!("{} answers", C::IMPORT.name()), |a| {
            a.logs
                .iter()
                .any(|(p, m)| p == plugin && (m.starts_with(&reply) || m.starts_with(&refused)))
        });
        let line = self
            .logs
            .iter()
            .rev()
            .find(|(p, m)| p == plugin && (m.starts_with(&reply) || m.starts_with(&refused)))
            .map(|(_, m)| m.clone())
            .unwrap();
        let hex = line
            .strip_prefix(&reply)
            .unwrap_or_else(|| panic!("{}: {line}", self.name()));
        C::Reply::from_bytes(&unhex(hex)).expect("a well-formed reply")
    }

    /// Makes the relay call `C` inside every event with this tag (what a
    /// plugin reads at that moment).
    pub fn react<C: Call>(&mut self, event_tag: u32, request: &C::Request) {
        let hex: String = request
            .to_bytes()
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect();
        self.type_line(&format!(
            "/parity react {event_tag} {} {hex}",
            C::IMPORT.name()
        ));
        let said = format!("parity react {event_tag}");
        self.until("the reaction is set", |a| {
            a.logs.iter().any(|(p, m)| p == PARITY && *m == said)
        });
    }

    /// What the relay's reactions to events with this tag answered so far.
    pub fn reactions<C: Call>(&self, event_tag: u32) -> Vec<C::Reply> {
        let prefix = format!("parity reaction {event_tag} ");
        self.logs
            .iter()
            .filter(|(p, _)| p == PARITY)
            .filter_map(|(_, m)| m.strip_prefix(&prefix))
            .map(|hex| C::Reply::from_bytes(&unhex(hex)).expect("a well-formed reply"))
            .collect()
    }

    /// The events the relay heard since the scenario's reading started, each
    /// with what it logged right after it (its reactions), as lines in order
    /// (`event <tag>`, `reaction <tag>`): the order a plugin saw them in.
    pub fn event_order(&self) -> Vec<String> {
        self.logs[self.events_from..]
            .iter()
            .filter(|(p, _)| p == PARITY)
            .filter_map(|(_, m)| {
                if let Some(hex) = m.strip_prefix("parity event ") {
                    let bytes = unhex(hex);
                    Some(format!(
                        "event {}",
                        u32::from_le_bytes(bytes[..4].try_into().unwrap())
                    ))
                } else {
                    m.strip_prefix("parity reaction ")
                        .map(|rest| format!("reaction {}", rest.split(' ').next().unwrap_or("")))
                }
            })
            .collect()
    }

    /// The server confirms the log off the client asked for; the plugins
    /// hear the leaving.
    pub fn confirm_logoff(&mut self) {
        self.until("the client asks to log off", |a| {
            a.server.pending().iter().any(|m| m.opcode == 0xF653)
        });
        let _ = self.server.take();
        let leavings = |a: &Self| {
            a.parity_events()
                .filter(|e| matches!(e, Event::Logoff))
                .count()
        };
        let before = leavings(self);
        self.server.send(&0xF653u32.to_le_bytes());
        self.until("the plugins hear the leaving", |a| leavings(a) > before);
        // A call after it: everything the plugins did inside the event is
        // in.
        let _ = self.call::<ac_plugin::abi::calls::HostSettings>(&());
        self.in_world = false;
    }

    /// The character logs off to the character list: the server confirms
    /// the client's log off, lets the next character in and creates it
    /// (`next` is that character's object and name).
    pub fn confirm_logoff_and_enter(&mut self, next: &super::wire::Object) {
        self.confirm_logoff();
        if let Err(why) = self.server.allow_entering() {
            panic!("{}: {why}", self.name());
        }
        let arrivals = self
            .parity_events()
            .filter(|e| matches!(e, Event::LoginComplete))
            .count();
        self.server.send(&next.build());
        self.server
            .game_event(next.guid, 0x0013, &self.description.build());
        self.until("the plugins hear the next arrival", |a| {
            a.parity_events()
                .filter(|e| matches!(e, Event::LoginComplete))
                .count()
                > arrivals
        });
        self.in_world = true;
        self.sync();
    }

    /// The relay is reloaded (`/plugin reload`), as a plugin the player
    /// reloads: the scenario's reading of events starts at its new enable.
    pub fn reload_relay(&mut self) {
        let enables = |a: &Self| {
            a.logs
                .iter()
                .filter(|(p, m)| p == PARITY && m.starts_with("parity enable"))
                .count()
        };
        let before = enables(self);
        self.type_line(&format!("/plugin reload {PARITY}"));
        self.until("the relay is enabled again", |a| enables(a) > before);
        // From the new enable on.
        self.events_from = self
            .logs
            .iter()
            .rposition(|(p, m)| p == PARITY && m.starts_with("parity enable"))
            .unwrap_or(0);
        self.sync();
    }

    /// Sets the relay's answer to a callback (see the plugin's docs).
    pub fn rule(&mut self, tag: u32, handle: u32, mode: &str, args: &[&[u8]]) {
        let args: Vec<String> = args
            .iter()
            .map(|a| a.iter().map(|b| format!("{b:02x}")).collect())
            .collect();
        let line = format!("/parity rule {tag} {handle} {mode} {}", args.join(" "));
        self.type_line(line.trim_end());
        let said = format!("parity rule {tag} {handle} {mode}");
        self.until("the rule is set", |a| {
            a.logs.iter().any(|(p, m)| p == PARITY && *m == said)
        });
    }

    /// Everything the server said so far has been handled, and what the
    /// plugins heard of it has been delivered (OpenAC's arms are
    /// synchronous; this is the same point in a live session).
    pub fn sync(&mut self) {
        self.syncs += 1;
        let marker = format!("{} {}", world::SYNC_MARKER, self.syncs);
        // What was said since the last sync goes again just before the
        // marker: a packet the client lost is in again ahead of it, so the
        // marker heard means everything before it was handled.
        self.server.resend_unconfirmed();
        self.server.send(&super::wire::hear_speech(
            "Parity Server",
            &marker,
            0x5000_0FFF,
            2,
        ));
        self.until("the server's marker is heard", |a| {
            a.chat.iter().any(|c| c.text.contains(&marker))
        });
        self.server.confirm();
        // A call after it: the plugins' events of that pass are delivered.
        let _ = self.call::<ac_plugin::abi::calls::HostSettings>(&());
    }

    /// Everything the client sent since last asked (OpenAC
    /// `ParitySessionOperations.TakeOutbound`): a ping goes out behind it,
    /// and once the server has the ping it has everything before it.
    pub fn outbound(&mut self) -> Vec<Outbound> {
        if self.in_world {
            let pings = self.server.pings();
            self.command(SessionCommand::Ping);
            self.until("the outbound barrier arrives", |a| a.server.pings() > pings);
        }
        self.server.take()
    }

    /// Records the outbound since last asked (OpenAC
    /// `ParityTranscript.RecordOutbound`), and returns it.
    pub fn record_outbound(&mut self, transcript: &mut Transcript) -> Vec<Outbound> {
        let sent = self.outbound();
        transcript.record("outbound.count", sent.len());
        for (index, message) in sent.iter().enumerate() {
            transcript.record(&format!("outbound[{index}]"), message);
        }
        sent
    }

    /// [`Self::record_outbound`] while the character walks: its position
    /// updates (0xF753) are left out of the transcript. Where a walking body
    /// stands when one goes out, and how many go out (one a second while it
    /// moves), is real time, not what the client was asked; the rest is
    /// recorded as it is, and all of it is returned.
    pub fn record_outbound_while_walking(&mut self, transcript: &mut Transcript) -> Vec<Outbound> {
        const AUTONOMOUS_POSITION: u32 = 0xF753;
        let sent = self.outbound();
        let said: Vec<&Outbound> = sent
            .iter()
            .filter(|m| m.game_action() != Some(AUTONOMOUS_POSITION))
            .collect();
        transcript.record("outbound.count", said.len());
        for (index, message) in said.iter().enumerate() {
            transcript.record(&format!("outbound[{index}]"), message);
        }
        sent
    }

    /// The relay's events, every one so far.
    pub fn parity_events(&self) -> impl Iterator<Item = Event> + '_ {
        self.logs
            .iter()
            .filter(|(p, _)| p == PARITY)
            .filter_map(|(_, m)| m.strip_prefix("parity event "))
            .map(|hex| Event::from_bytes(&unhex(hex)).expect("a well-formed event"))
    }

    /// The scenario's reading of events starts here.
    pub fn mark_events(&mut self) {
        self.events_from = self.logs.len();
    }

    /// The events the relay heard since the last time, without the server's
    /// sync markers.
    pub fn events(&mut self) -> Vec<Event> {
        let from = self.events_from;
        self.events_from = self.logs.len();
        self.logs[from..]
            .iter()
            .filter(|(p, _)| p == PARITY)
            .filter_map(|(_, m)| m.strip_prefix("parity event "))
            .map(|hex| Event::from_bytes(&unhex(hex)).expect("a well-formed event"))
            .filter(|e| !matches!(e, Event::ChatReceived(line) if line.text.contains(world::SYNC_MARKER)))
            .collect()
    }

    /// What the stand-in window holds of the plugins' UI (the windowed arm
    /// only): every change it was sent, applied.
    pub fn plugin_ui_view(&self) -> Option<ac_plugin_ui::PluginUiView> {
        self.windowed.then(|| {
            let mut view = ac_plugin_ui::PluginUiView::new();
            for event in &self.presented {
                if let PresentationEvent::Interface(InterfaceEvent::PluginUi(changes)) = event {
                    view.apply_all(changes.iter().cloned());
                }
            }
            view
        })
    }

    /// Logs the session off and ends it, the server confirming.
    pub fn finish(&mut self) {
        let Some(handle) = self.handle.take() else {
            return;
        };
        // Only the log off asked for from here is answered.
        self.server.poll();
        let _ = self.server.take();
        let _ = self.commands.send(SessionCommand::Shutdown);
        let deadline = Instant::now() + Duration::from_secs(30);
        let mut confirmed = false;
        loop {
            self.server.poll();
            self.server.repair();
            if !confirmed && self.server.pending().iter().any(|m| m.opcode == 0xF653) {
                confirmed = true;
                self.server.send(&0xF653u32.to_le_bytes());
            }
            match handle.next_event(Duration::from_millis(5)) {
                Some(HostEvent::Ended(summary)) => {
                    self.host_events.push(HostEvent::Ended(summary));
                    break;
                }
                Some(event) => self.take(event),
                None if handle.is_finished() => break,
                None => {}
            }
            if Instant::now() > deadline {
                break;
            }
        }
        handle.join();
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

impl Drop for Arm {
    fn drop(&mut self) {
        self.finish();
    }
}

pub fn unhex(text: &str) -> Vec<u8> {
    (0..text.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap())
        .collect()
}
