//! A session's remote endpoint, running: one thread that owns the link to
//! the relay and the session's side of a remote presentation, and carries
//! out what [`crate::endpoint`] decides.
//!
//! It holds a clone of the session's command sender, as a console does. The
//! worst it ever sends is a detach: closing the browser, losing the link or
//! the relay failing leaves the session playing with nothing attached.

use std::collections::VecDeque;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender, TryRecvError};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use ac_runtime::session::{PresentationEvent, SessionCommand};

use crate::endpoint::{Action, Codec, Endpoint};
use crate::link::{Inbound, Link, LinkConfig};
use crate::protocol::Status;

/// The largest frame sent to the relay.
const MAX_FRAME: usize = 4 * 1024 * 1024;
/// How long one pass waits for the link while a browser is attached: the
/// longest the session's events wait to go out.
const ATTACHED_WAIT: Duration = Duration::from_millis(5);
/// The same with nothing attached, when only the relay can start anything.
const IDLE_WAIT: Duration = Duration::from_millis(50);
/// The most events taken from the session in one pass, so a flood cannot
/// keep the link's messages waiting.
const EVENTS_PER_PASS: usize = 4096;

/// A running endpoint. Dropping it stops it.
#[derive(Debug)]
pub struct Remote {
    stop: Arc<AtomicBool>,
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
        let (status, changes) = mpsc::channel();
        let (to_console, notes) = mpsc::channel();
        let link = Link::start(link)?;
        let mut worker = Worker {
            link,
            endpoint: Endpoint::new(codec, MAX_FRAME),
            commands,
            changes,
            presentation: None,
            to_console,
            stop: Arc::clone(&stop),
        };
        let thread = std::thread::Builder::new()
            .name("remote-endpoint".into())
            .spawn(move || worker.run())?;
        Ok(Self {
            stop,
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
    /// Lines for the client's console.
    to_console: Sender<String>,
    stop: Arc<AtomicBool>,
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
            actions.extend(self.endpoint.tick(Instant::now()));
            self.carry_out(actions);
        }
        // Stopping: the session is left with nothing attached.
        let actions = self.endpoint.link_lost();
        self.carry_out(actions.into());
    }

    fn heard(&mut self, inbound: Inbound) -> Vec<Action> {
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
