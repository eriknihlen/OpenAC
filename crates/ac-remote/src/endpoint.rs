//! What a session's remote endpoint does, as pure logic: it is told what
//! happened (a control message from the relay, an input frame, the session's
//! events, time passing) and answers with what to do. The thread that owns
//! the link and the session's channels ([`crate::remote`]) carries the
//! answers out; nothing here touches a socket, a channel or a clock.
//!
//! The rules it keeps:
//!
//! - One request is attached at a time; a new one replaces it.
//! - The session's events go out only while a request is attached, less
//!   what its subscription leaves out.
//! - Input is taken only while the browser has control, which it must be
//!   allowed to take and loses after [`INPUT_SILENCE`] without an input
//!   frame; losing it lets go of the movement keys.
//! - Every command is checked here, whatever the relay let through: it must
//!   be stamped with its generation, and commands that end or delete are
//!   taken only with their permission, or never.
//! - Nothing that goes wrong on the link or in a frame does more than end
//!   the attach. The session is never asked to stop.

use std::time::{Duration, Instant};

use ac_runtime::movement::MovementCommand;
use ac_runtime::session::world_tap::WorldTapBatch;
use ac_runtime::session::{LifecycleCommand, PresentationEvent, SessionCommand};

use crate::link::SendError;
use crate::protocol::{
    ControlReason, DetachReason, EndpointMessage, LifecyclePermission, Permissions, RelayMessage,
    Subscription,
};

/// What a frame of the world's own messages starts with. A stream frame
/// starts with its format number, a small one, so the two never look alike.
const WORLD_FRAME_MAGIC: &[u8; 4] = b"ACRW";
/// The magic and the frame's time.
const WORLD_FRAME_HEADER: usize = 12;
/// What comes before a record's body.
const WORLD_RECORD_HEADER: usize = 16;

/// How long the browser keeps control without sending an input frame.
pub const INPUT_SILENCE: Duration = Duration::from_secs(1);

/// The encoding of the stream and of the commands.
pub trait Codec {
    /// One frame holding `events`, in order.
    fn encode(&mut self, events: &[PresentationEvent]) -> Result<Vec<u8>, String>;
    /// The command one input frame holds.
    fn decode(&mut self, frame: &[u8]) -> Result<SessionCommand, String>;
}

/// What the endpoint's thread is to do, in order.
#[derive(Debug)]
pub enum Action {
    /// Attach to the session as a remote presentation, then report with
    /// [`Endpoint::attach_done`].
    Attach,
    /// Detach from the session.
    Detach,
    /// Send the session this command.
    Command(SessionCommand),
    /// Send the relay this control message.
    Control(EndpointMessage),
    /// Send the relay this frame for the browser.
    Binary(Vec<u8>),
    /// A line for the client's own console: why an attach ended by a fault.
    Note(String),
    /// Listen to the session's world messages from the start (in place of
    /// any earlier listening).
    ListenWorld,
}

/// The request attached now.
#[derive(Debug)]
struct Attached {
    request_id: u64,
    subscription: Subscription,
    permissions: Permissions,
    /// The session took the attach.
    accepted: bool,
    /// When the browser's control lapses; `None` while it only watches.
    control_until: Option<Instant>,
}

/// One session's remote endpoint.
#[derive(Debug)]
pub struct Endpoint<C> {
    codec: C,
    /// The largest frame the link takes.
    max_frame: usize,
    attached: Option<Attached>,
}

impl<C: Codec> Endpoint<C> {
    /// An endpoint with nothing attached, sending frames of at most
    /// `max_frame` bytes.
    pub fn new(codec: C, max_frame: usize) -> Self {
        Self {
            codec,
            max_frame,
            attached: None,
        }
    }

    /// Whether a request is attached (or attaching).
    pub fn is_attached(&self) -> bool {
        self.attached.is_some()
    }

    /// A control message arrived from the relay.
    pub fn relay(&mut self, message: RelayMessage, now: Instant) -> Vec<Action> {
        match message {
            RelayMessage::Attach {
                request_id,
                subscription,
                permissions,
            } => {
                let mut actions = self.end(DetachReason::Replaced);
                self.attached = Some(Attached {
                    request_id,
                    subscription,
                    permissions,
                    accepted: false,
                    control_until: None,
                });
                actions.push(Action::Attach);
                actions
            }
            RelayMessage::Detach { request_id } if self.is_request(request_id) => {
                self.end(DetachReason::Browser)
            }
            RelayMessage::Control { request_id, on } if self.is_request(request_id) => {
                self.control(on, now)
            }
            // The link answers the hello; another request's messages and
            // types this version does not know are not ours.
            RelayMessage::Detach { .. }
            | RelayMessage::Control { .. }
            | RelayMessage::HelloOk { .. }
            | RelayMessage::Refused { .. }
            | RelayMessage::Unknown { .. } => Vec::new(),
        }
    }

    /// The session took the attach, or could not.
    pub fn attach_done(&mut self, result: Result<(), String>) -> Vec<Action> {
        let Some(attached) = self.attached.as_mut() else {
            return Vec::new();
        };
        let request_id = attached.request_id;
        match result {
            Ok(()) => {
                attached.accepted = true;
                vec![Action::Control(EndpointMessage::Attached { request_id })]
            }
            Err(reason) => {
                self.attached = None;
                vec![Action::Control(EndpointMessage::AttachFailed {
                    request_id,
                    reason,
                })]
            }
        }
    }

    /// An input frame arrived from the browser.
    pub fn frame(&mut self, frame: &[u8], now: Instant) -> Vec<Action> {
        let Some(attached) = self.attached.as_mut() else {
            return Vec::new();
        };
        if attached.control_until.is_none() {
            return Vec::new();
        }
        // Any frame from the browser in control says it is still there.
        attached.control_until = Some(now + INPUT_SILENCE);
        let command = match self.codec.decode(frame) {
            Ok(command) => command,
            Err(error) => {
                let why = format!("the browser sent bytes that are no command ({error})");
                return self.fault(DetachReason::Protocol, why);
            }
        };
        match &command {
            SessionCommand::AtGeneration { command: inner, .. }
                if allowed(inner, &attached.permissions) =>
            {
                vec![Action::Command(command)]
            }
            _ => Vec::new(),
        }
    }

    /// The session's events since the last call, in order.
    pub fn events(&mut self, mut events: Vec<PresentationEvent>) -> Vec<Action> {
        let Some(attached) = self.attached.as_ref() else {
            return Vec::new();
        };
        let subscription = attached.subscription;
        // A snapshot is a world that starts over.
        let restart = subscription.world
            && events
                .iter()
                .any(|event| matches!(event, PresentationEvent::Snapshot(_)));
        events.retain(|event| wanted(&subscription, event));
        let mut actions = Vec::new();
        if restart {
            actions.push(Action::ListenWorld);
        }
        match self.encode(&events, &mut actions) {
            Ok(()) => actions,
            Err((reason, why)) => self.fault(reason, why),
        }
    }

    /// `events` as frames no larger than the link takes, halving a batch
    /// that is.
    fn encode(
        &mut self,
        events: &[PresentationEvent],
        frames: &mut Vec<Action>,
    ) -> Result<(), (DetachReason, String)> {
        if events.is_empty() {
            return Ok(());
        }
        let frame = self.codec.encode(events).map_err(|error| {
            let why = format!("the session's events could not be encoded ({error})");
            (DetachReason::Protocol, why)
        })?;
        if frame.len() <= self.max_frame {
            frames.push(Action::Binary(frame));
            return Ok(());
        }
        if events.len() == 1 {
            let why = format!(
                "one event of {} bytes is larger than a frame ({} bytes)",
                frame.len(),
                self.max_frame
            );
            return Err((DetachReason::Overflow, why));
        }
        let (first, second) = events.split_at(events.len() / 2);
        self.encode(first, frames)?;
        self.encode(second, frames)
    }

    /// The world's own messages since the last call, seen at `epoch_ms`
    /// (milliseconds since the Unix epoch).
    pub fn world(&mut self, batch: &WorldTapBatch, epoch_ms: u64) -> Vec<Action> {
        let wanted = self
            .attached
            .as_ref()
            .is_some_and(|attached| attached.accepted && attached.subscription.world);
        if !wanted {
            return Vec::new();
        }
        let mut frames = Vec::new();
        let mut frame = Vec::new();
        for message in &batch.messages {
            let padded = message.body.len().next_multiple_of(4);
            let record = WORLD_RECORD_HEADER + padded;
            // A message no frame holds is left out: the viewer misses one
            // object's news, not the world.
            let (Ok(length), true) = (
                u32::try_from(message.body.len()),
                WORLD_FRAME_HEADER + record <= self.max_frame,
            ) else {
                continue;
            };
            if !frame.is_empty() && frame.len() + record > self.max_frame {
                frames.push(Action::Binary(std::mem::take(&mut frame)));
            }
            if frame.is_empty() {
                frame.extend_from_slice(WORLD_FRAME_MAGIC);
                frame.extend_from_slice(&epoch_ms.to_le_bytes());
            }
            // The record: milliseconds since the frame's time, flags (bit
            // 0: from the client), a reserved word, whose session saw it,
            // the length, the body, zeros to a multiple of four.
            frame.extend_from_slice(&0u32.to_le_bytes());
            frame.extend_from_slice(&u16::from(message.client_to_server).to_le_bytes());
            frame.extend_from_slice(&0u16.to_le_bytes());
            frame.extend_from_slice(&batch.player.to_le_bytes());
            frame.extend_from_slice(&length.to_le_bytes());
            frame.extend_from_slice(&message.body);
            frame.resize(frame.len() + padded - message.body.len(), 0);
        }
        if !frame.is_empty() {
            frames.push(Action::Binary(frame));
        }
        frames
    }

    /// Time passed.
    pub fn tick(&mut self, now: Instant) -> Vec<Action> {
        let Some(attached) = self.attached.as_mut() else {
            return Vec::new();
        };
        match attached.control_until {
            Some(until) if now > until => {
                attached.control_until = None;
                vec![
                    release_keys(),
                    Action::Control(EndpointMessage::ControlState {
                        request_id: attached.request_id,
                        controlling: false,
                        reason: ControlReason::Heartbeat,
                    }),
                ]
            }
            _ => Vec::new(),
        }
    }

    /// The link to the relay was lost: nobody is left to tell.
    pub fn link_lost(&mut self) -> Vec<Action> {
        match self.attached.take() {
            Some(_) => vec![Action::Detach],
            None => Vec::new(),
        }
    }

    /// The session ended (its event channel closed).
    pub fn session_ended(&mut self) -> Vec<Action> {
        match self.attached.take() {
            Some(attached) => vec![Action::Control(EndpointMessage::Detached {
                request_id: attached.request_id,
                reason: DetachReason::SessionEnded,
            })],
            None => Vec::new(),
        }
    }

    /// The link refused a frame.
    pub fn send_failed(&mut self, error: &SendError) -> Vec<Action> {
        match error {
            SendError::QueueFull | SendError::TooLarge => self.fault(
                DetachReason::Overflow,
                "the link to the relay could not keep up".to_owned(),
            ),
            // A link that is down says so by itself.
            SendError::NotConnected | SendError::HelloIsTheLinks => Vec::new(),
        }
    }

    fn is_request(&self, request_id: u64) -> bool {
        self.attached
            .as_ref()
            .is_some_and(|attached| attached.request_id == request_id)
    }

    /// The browser takes or releases control.
    fn control(&mut self, on: bool, now: Instant) -> Vec<Action> {
        let Some(attached) = self.attached.as_mut() else {
            return Vec::new();
        };
        if on && !attached.permissions.control {
            return vec![Action::Control(EndpointMessage::ControlState {
                request_id: attached.request_id,
                controlling: false,
                reason: ControlReason::Released,
            })];
        }
        let had = attached.control_until.is_some();
        attached.control_until = on.then(|| now + INPUT_SILENCE);
        if had && !on {
            vec![release_keys()]
        } else {
            Vec::new()
        }
    }

    /// End the attach over a fault, leaving a line for the console saying
    /// what it was.
    fn fault(&mut self, reason: DetachReason, why: String) -> Vec<Action> {
        let mut actions = self.end(reason);
        if !actions.is_empty() {
            actions.push(Action::Note(format!(
                "the remote window was detached: {why}"
            )));
        }
        actions
    }

    /// End the attach, if there is one, and say why. The detach lets go of
    /// whatever the browser held.
    fn end(&mut self, reason: DetachReason) -> Vec<Action> {
        match self.attached.take() {
            Some(attached) => vec![
                Action::Detach,
                Action::Control(EndpointMessage::Detached {
                    request_id: attached.request_id,
                    reason,
                }),
            ],
            None => Vec::new(),
        }
    }
}

fn release_keys() -> Action {
    Action::Command(SessionCommand::Movement(MovementCommand::ReleaseKeys))
}

/// Whether `subscription` asks for `event`. (Which objects it asks for is
/// not applied yet: every object goes.)
fn wanted(subscription: &Subscription, event: &PresentationEvent) -> bool {
    match event {
        PresentationEvent::Motions(_) => subscription.motions,
        PresentationEvent::Sound(_) => subscription.sound,
        PresentationEvent::PlayScript(_)
        | PresentationEvent::PlayScriptType(_)
        | PresentationEvent::DestroyParticles(_) => subscription.effects,
        PresentationEvent::Navigation(_) => subscription.navigation,
        PresentationEvent::Snapshot(_)
        | PresentationEvent::Deltas(_)
        | PresentationEvent::GameTime(_)
        | PresentationEvent::AdminEnvirons(_)
        | PresentationEvent::Chat(_)
        | PresentationEvent::Player(_)
        | PresentationEvent::Interface(_)
        | PresentationEvent::PortalSpace(_)
        | PresentationEvent::PreGame(_)
        | PresentationEvent::StatusText(_)
        | PresentationEvent::WindowRequest(_)
        | PresentationEvent::Link(_)
        | PresentationEvent::SessionClock(_) => true,
    }
}

/// Whether a browser with `permissions` may send `command` (the command
/// inside its generation stamp). Every kind is named, so a new one is
/// decided here before it can be sent.
fn allowed(command: &SessionCommand, permissions: &Permissions) -> bool {
    let may = |permission| permissions.lifecycle.contains(&permission);
    match command {
        // Ordinary play.
        SessionCommand::Chat(_)
        | SessionCommand::ChatInput { .. }
        | SessionCommand::ChatEntry(_)
        | SessionCommand::Say(_)
        | SessionCommand::Tell { .. }
        | SessionCommand::Reply(_)
        | SessionCommand::Emote(_)
        | SessionCommand::Channel { .. }
        | SessionCommand::Recall(_)
        | SessionCommand::AddFriend(_)
        | SessionCommand::RemoveFriend(_)
        | SessionCommand::Squelch { .. }
        | SessionCommand::AbandonContract(_)
        | SessionCommand::ListContracts
        | SessionCommand::Journal(_)
        | SessionCommand::ShowJournal
        | SessionCommand::SetCharacterOption { .. }
        | SessionCommand::SaveCharacterOptions
        | SessionCommand::ShowCharacterOptions
        | SessionCommand::Item(_)
        | SessionCommand::ListInventory
        | SessionCommand::Gameplay(_)
        | SessionCommand::Movement(_)
        | SessionCommand::ListObjects
        | SessionCommand::AnswerConfirmation { .. }
        | SessionCommand::AnswerOpenConfirmation { .. }
        | SessionCommand::ListCorpses(_)
        | SessionCommand::GameTime
        | SessionCommand::Character(_)
        | SessionCommand::Ping
        | SessionCommand::ShowLinkStatus
        | SessionCommand::DismissGhost(_)
        | SessionCommand::ShowInterfaceText(_)
        | SessionCommand::ShowText { .. }
        | SessionCommand::ChatLinkClicked { .. }
        | SessionCommand::PluginUi(_) => true,
        SessionCommand::Lifecycle(command) => match command {
            LifecycleCommand::Reconnect => may(LifecyclePermission::Reconnect),
            LifecycleCommand::LogOff | LifecycleCommand::EndCharacterSession => {
                may(LifecyclePermission::LogOff)
            }
            // The selection screen a log off leads to.
            LifecycleCommand::ShowSelection
            | LifecycleCommand::Highlight(_)
            | LifecycleCommand::Enter(_)
            | LifecycleCommand::Cancel
            | LifecycleCommand::SetNextLogin(_) => true,
            // Deleting, restoring and creating characters stay with the
            // player at the client.
            LifecycleCommand::RequestDelete(_)
            | LifecycleCommand::ConfirmDelete
            | LifecycleCommand::Restore(_)
            | LifecycleCommand::Create(_) => false,
        },
        // What a window says about itself: the session waits for nothing a
        // remote presentation shows, so it has nothing to say.
        SessionCommand::WindowMinimized(_)
        | SessionCommand::DestinationShown(_)
        | SessionCommand::LoginShown(_)
        | SessionCommand::PresentsWorld(_)
        | SessionCommand::RevealWaited { .. } => false,
        // The endpoint's own, never the browser's; and a stamp inside a
        // stamp.
        SessionCommand::Attach(_)
        | SessionCommand::AttachRemote(_)
        | SessionCommand::Detach
        | SessionCommand::WorldTap(_)
        | SessionCommand::Shutdown
        | SessionCommand::AtGeneration { .. } => false,
    }
}

#[cfg(test)]
mod tests;
