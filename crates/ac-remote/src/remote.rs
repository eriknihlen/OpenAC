//! A session's remote endpoint, running: one thread that owns the link to
//! the relay and the session's side of a remote presentation, and carries
//! out what [`crate::endpoint`] decides.
//!
//! It holds a clone of the session's command sender, as a console does. The
//! worst it ever sends is a detach: closing the browser, losing the link or
//! the relay failing leaves the session playing with nothing attached.

use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender, TryRecvError};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use ac_runtime::session::world_tap::{WorldTapBatch, WorldTapCommand};
use ac_runtime::session::{PresentationEvent, SessionCommand};

use crate::endpoint::{Action, Codec, Endpoint};
use crate::link::{Inbound, Link, LinkConfig, LinkLoss};
use crate::protocol::Status;
use crate::ws::HandshakeError;

/// The largest frame sent to the relay.
const MAX_FRAME: usize = 4 * 1024 * 1024;
/// How long one pass waits for the link while a browser is attached: the
/// longest the session's events wait to go out.
const ATTACHED_WAIT: Duration = Duration::from_millis(5);
/// The same with nothing attached, when only the relay can start anything.
const IDLE_WAIT: Duration = Duration::from_millis(50);
/// The most batches of the world's messages taken in one pass.
const WORLD_BATCHES_PER_PASS: usize = 64;
/// The most events taken from the session in one pass, so a flood cannot
/// keep the link's messages waiting.
const EVENTS_PER_PASS: usize = 4096;

/// Where the link to the relay stands.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LinkState {
    /// The first attempt has not ended yet.
    Connecting,
    /// The relay accepted the hello.
    Connected,
    /// The relay turned the endpoint away (the token, or the relay's own
    /// refusal); the link tries again after `retry_in`.
    Refused {
        /// Why, never the token.
        reason: String,
        /// The wait before the next attempt.
        retry_in: Duration,
    },
    /// The relay could not be reached or the link was lost; the link tries
    /// again after `retry_in`.
    BackingOff {
        /// Why, never the token.
        reason: String,
        /// The wait before the next attempt.
        retry_in: Duration,
    },
}

impl LinkState {
    /// The state after a failed attempt or a lost link.
    #[must_use]
    pub fn lost(loss: &LinkLoss, retry_in: Duration) -> Self {
        let reason = loss.to_string();
        let refused = matches!(
            loss,
            LinkLoss::Refused(_) | LinkLoss::Handshake(HandshakeError::Status(401 | 403))
        );
        if refused {
            Self::Refused { reason, retry_in }
        } else {
            Self::BackingOff { reason, retry_in }
        }
    }
}

/// What a running endpoint can say of itself.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RemoteState {
    /// The link to the relay.
    pub link: LinkState,
    /// Whether a browser is attached to the session.
    pub attached: bool,
}

/// A running endpoint. Dropping it stops it.
#[derive(Debug)]
pub struct Remote {
    stop: Arc<AtomicBool>,
    state: Arc<Mutex<RemoteState>>,
    status: Sender<Status>,
    notes: Receiver<String>,
    thread: Option<JoinHandle<()>>,
}

impl Remote {
    /// Start the endpoint of the session that takes `commands`, offering
    /// it to the relay `link` names.
    pub fn start<C: Codec + Send + 'static>(
        link: LinkConfig,
        codec: C,
        commands: Sender<SessionCommand>,
    ) -> std::io::Result<Self> {
        let stop = Arc::new(AtomicBool::new(false));
        let state = Arc::new(Mutex::new(RemoteState {
            link: LinkState::Connecting,
            attached: false,
        }));
        let (status, changes) = mpsc::channel();
        let (to_console, notes) = mpsc::channel();
        let link = Link::start(link)?;
        // A session that offers itself keeps the world's own messages from
        // its start, so a browser that comes later can be given the world
        // as it stands.
        let keep = WorldTapCommand::Keep(true);
        let _ = commands.send(SessionCommand::WorldTap(keep));
        let mut worker = Worker {
            link,
            endpoint: Endpoint::new(codec, MAX_FRAME),
            commands,
            changes,
            presentation: None,
            world: None,
            to_console,
            stop: Arc::clone(&stop),
            state: Arc::clone(&state),
        };
        let thread = std::thread::Builder::new()
            .name("remote-endpoint".into())
            .spawn(move || worker.run())?;
        Ok(Self {
            stop,
            state,
            status,
            notes,
            thread: Some(thread),
        })
    }

    /// What the session is doing changed: the relay hears it now, or with
    /// the next hello.
    pub fn status(&self, change: Status) {
        let _ = self.status.send(change);
    }

    /// Lines for the client's console since the last call: why an attach
    /// ended by a fault.
    pub fn take_notes(&self) -> Vec<String> {
        self.notes.try_iter().collect()
    }

    /// Where the link stands and whether a browser is attached.
    #[must_use]
    pub fn state(&self) -> RemoteState {
        match self.state.lock() {
            Ok(state) => state.clone(),
            Err(poisoned) => poisoned.into_inner().clone(),
        }
    }

    /// Detach from the session and stop.
    pub fn shutdown(mut self) {
        self.stop();
    }

    fn stop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

impl Drop for Remote {
    fn drop(&mut self) {
        self.stop();
    }
}

/// The endpoint's thread.
struct Worker<C> {
    link: Link,
    endpoint: Endpoint<C>,
    /// To the session.
    commands: Sender<SessionCommand>,
    /// Status changes from the host.
    changes: Receiver<Status>,
    /// The session's events while attached.
    presentation: Option<Receiver<PresentationEvent>>,
    /// The world's own messages, while a browser that asked for them is
    /// attached.
    world: Option<Receiver<WorldTapBatch>>,
    /// Lines for the client's console.
    to_console: Sender<String>,
    stop: Arc<AtomicBool>,
    /// What [`Remote::state`] reads.
    state: Arc<Mutex<RemoteState>>,
}

impl<C: Codec> Worker<C> {
    fn run(&mut self) {
        while !self.stop.load(Ordering::Relaxed) {
            let wait = if self.presentation.is_some() {
                ATTACHED_WAIT
            } else {
                IDLE_WAIT
            };
            let mut actions = VecDeque::new();
            match self.link.events().recv_timeout(wait) {
                Ok(inbound) => {
                    actions.extend(self.heard(inbound));
                    while let Ok(inbound) = self.link.events().try_recv() {
                        actions.extend(self.heard(inbound));
                    }
                }
                Err(RecvTimeoutError::Timeout) => {}
                // The link's own thread is gone: nothing more can arrive.
                Err(RecvTimeoutError::Disconnected) => break,
            }
            while let Ok(change) = self.changes.try_recv() {
                self.link.update_hello(|hello| change.apply_to(hello));
            }
            actions.extend(self.session_events());
            actions.extend(self.world_messages());
            actions.extend(self.endpoint.tick(Instant::now()));
            self.carry_out(actions);
            let attached = self.presentation.is_some();
            self.set_state(|state| state.attached = attached);
        }
        // Stopping: the session is left with nothing attached, and keeps the
        // world's messages no longer.
        let actions = self.endpoint.link_lost();
        self.carry_out(actions.into());
        let keep = WorldTapCommand::Keep(false);
        let _ = self.commands.send(SessionCommand::WorldTap(keep));
    }

    fn set_state(&self, change: impl FnOnce(&mut RemoteState)) {
        match self.state.lock() {
            Ok(mut state) => change(&mut state),
            Err(poisoned) => change(&mut poisoned.into_inner()),
        }
    }

    fn heard(&mut self, inbound: Inbound) -> Vec<Action> {
        match &inbound {
            Inbound::Connected { .. } => self.set_state(|s| s.link = LinkState::Connected),
            Inbound::Disconnected { loss, retry_in }
            | Inbound::ConnectFailed { loss, retry_in } => {
                let link = LinkState::lost(loss, *retry_in);
                self.set_state(|s| s.link = link);
            }
            Inbound::Control(_) | Inbound::Binary(_) => {}
        }
        match inbound {
            Inbound::Control(message) => self.endpoint.relay(message, Instant::now()),
            Inbound::Binary(frame) => self.endpoint.frame(&frame, Instant::now()),
            Inbound::Disconnected { .. } => self.endpoint.link_lost(),
            Inbound::Connected { .. } | Inbound::ConnectFailed { .. } => Vec::new(),
        }
    }

    /// What the session sent since the last pass, and whether it is still
    /// there.
    fn session_events(&mut self) -> Vec<Action> {
        let Some(presentation) = &self.presentation else {
            return Vec::new();
        };
        let mut events = Vec::new();
        let mut gone = false;
        while events.len() < EVENTS_PER_PASS {
            match presentation.try_recv() {
                Ok(event) => events.push(event),
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => {
                    gone = true;
                    break;
                }
            }
        }
        let mut actions = self.endpoint.events(events);
        if gone {
            // The session ended, or attached something else in this one's
            // place: there is nothing of ours left to detach.
            self.presentation = None;
            actions.extend(self.endpoint.session_ended());
        }
        actions
    }

    /// The world's own messages the session tapped since the last pass.
    fn world_messages(&mut self) -> Vec<Action> {
        let Some(world) = &self.world else {
            return Vec::new();
        };
        let epoch_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |since| {
                u64::try_from(since.as_millis()).unwrap_or(u64::MAX)
            });
        let mut actions = Vec::new();
        for _ in 0..WORLD_BATCHES_PER_PASS {
            match world.try_recv() {
                Ok(batch) => actions.extend(self.endpoint.world(&batch, epoch_ms)),
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => {
                    // The session let the listener go (a new login starts
                    // another with its snapshot).
                    self.world = None;
                    break;
                }
            }
        }
        actions
    }

    fn carry_out(&mut self, mut actions: VecDeque<Action>) {
        while let Some(action) = actions.pop_front() {
            match action {
                Action::Attach => {
                    let (to_remote, presentation) = mpsc::channel();
                    let sent = self.commands.send(SessionCommand::AttachRemote(to_remote));
                    let result = match sent {
                        Ok(()) => {
                            self.presentation = Some(presentation);
                            Ok(())
                        }
                        Err(_) => Err("the session has ended".to_owned()),
                    };
                    actions.extend(self.endpoint.attach_done(result));
                }
                Action::Detach => {
                    // Only what is still ours: a channel the session closed
                    // was replaced, and a detach would take its replacement.
                    if self.world.take().is_some() {
                        let stop = WorldTapCommand::Listen(None);
                        let _ = self.commands.send(SessionCommand::WorldTap(stop));
                    }
                    if let Some(presentation) = self.presentation.take()
                        && !matches!(presentation.try_recv(), Err(TryRecvError::Disconnected))
                    {
                        let _ = self.commands.send(SessionCommand::Detach);
                    }
                }
                Action::Command(command) => {
                    let _ = self.commands.send(command);
                }
                Action::Control(message) => {
                    // A link that is down has nobody to tell.
                    let _ = self.link.send_control(message);
                }
                Action::ListenWorld => {
                    let (to_viewer, world) = mpsc::channel();
                    let listen = WorldTapCommand::Listen(Some(to_viewer));
                    if self.commands.send(SessionCommand::WorldTap(listen)).is_ok() {
                        self.world = Some(world);
                    }
                }
                Action::Note(line) => {
                    let _ = self.to_console.send(line);
                }
                Action::Binary(frame) => {
                    if let Err(error) = self.link.send_binary(frame) {
                        actions.extend(self.endpoint.send_failed(&error));
                    }
                }
            }
        }
    }
}
