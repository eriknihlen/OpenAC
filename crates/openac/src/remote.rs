//! A session's remote endpoint: the session offers itself to a relay, to be
//! shown and played somewhere else, when its settings say so and only then.
//! The settings come from the session's document or the client's store
//! (see [`crate::remote_settings`]), and `/remoteac` changes them while the
//! session runs. Nothing here can stop a session: a relay that cannot be
//! reached, a token that cannot be read or an address that is no address
//! leaves the session running without an endpoint, with a line saying why.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::mpsc::Sender;

use ac_core::chat::LogTextType;
use ac_host::{
    CredentialProvider, CredentialReference, CredentialResolver, SessionDescriptor, SystemPlatform,
};
use ac_remote::protocol::{Capability, Hello, SessionState, Status, new_instance_id};
use ac_remote::remote::{LinkState, Remote};
use ac_remote::wire::WireCodec;
use ac_remote::{LinkConfig, RelayUrl, Secret};
use ac_runtime::communication::remote_command::RemoteCommand;
use ac_runtime::session::{HostEvent, SessionCommand, SessionEvent};

use crate::remote_settings::{Place, Settings, StoreKey, write_token};
use crate::stdio::errln;

/// The version of the browser interface this client's panels are built
/// for (the web module's interface number).
const UI_INTERFACE: u32 = 1;

/// A session's relay and its token for it.
#[derive(Debug)]
pub(crate) struct Relay {
    url: RelayUrl,
    token: Secret,
}

/// The relay of every session whose document names one and leaves it on,
/// by session id, read before the sessions start (a token may come from
/// standard input). A session whose relay cannot be used runs without one.
pub(crate) fn resolve(
    sessions: &[SessionDescriptor],
    resolver: &mut CredentialResolver<'_>,
) -> BTreeMap<String, Relay> {
    let mut relays = BTreeMap::new();
    for session in sessions {
        let Some(remote) = session.remote.as_ref().filter(|r| r.enabled) else {
            continue;
        };
        let url = match RelayUrl::parse(&remote.url) {
            Ok(url) => url,
            Err(error) => {
                errln!(
                    "openac: session {}: no remote endpoint: {error}",
                    session.id
                );
                continue;
            }
        };
        match resolver.resolve(&session.id, &remote.credential) {
            Ok(token) => {
                let token = Secret::new(token.reveal());
                relays.insert(session.id.clone(), Relay { url, token });
            }
            Err(error) => errln!(
                "openac: session {}: no remote endpoint: {error}",
                session.id
            ),
        }
    }
    relays
}

/// What a session says of itself when it connects to its relay, before it
/// has logged in. Never the account.
fn hello(session_id: &str, host: &str, port: u16) -> Hello {
    Hello {
        shape: WireCodec::shape(),
        ui_interface: UI_INTERFACE,
        client_version: env!("CARGO_PKG_VERSION").to_owned(),
        client_commit: None,
        instance_id: new_instance_id(),
        config_session_id: session_id.to_owned(),
        generation: 0,
        character_name: String::new(),
        character_guid: 0,
        server: format!("{host}:{port}"),
        state: SessionState::Pregame,
        capabilities: vec![Capability::View, Capability::Control, Capability::Lifecycle],
    }
}

/// A session's remote window: its settings, where they are kept, and the
/// endpoint while it runs. `/remoteac` reaches it as a host event.
pub(crate) struct RemoteControl {
    session_id: String,
    place: Place,
    config_directory: PathBuf,
    /// The session's own token file, relative to the config directory.
    token_reference: String,
    settings: Settings,
    /// Why the kept settings could not be read: nothing is saved over them.
    unreadable: Option<String>,
    /// What the relay is told on connecting, kept up with the session.
    hello: Hello,
    commands: Sender<SessionCommand>,
    running: Option<Remote>,
}

impl RemoteControl {
    /// The remote window of `session` (whose document is `document`),
    /// which takes `commands`: started with `relay` when the document's
    /// block is on and was read, or from the client's store when that is on.
    pub(crate) fn new(
        session: &SessionDescriptor,
        document: &Path,
        config_directory: &Path,
        relay: Option<Relay>,
        commands: Sender<SessionCommand>,
    ) -> Self {
        let (place, found) = Place::find(session, document, config_directory);
        let (settings, unreadable) = match found {
            Ok(settings) => (settings, None),
            Err(error) => {
                errln!("openac: session {}: remote settings: {error}", session.id);
                (Settings::default(), Some(error))
            }
        };
        let mut control = Self {
            session_id: session.id.clone(),
            place,
            config_directory: config_directory.to_path_buf(),
            token_reference: StoreKey::of(session).token_reference(),
            settings,
            unreadable,
            hello: hello(&session.id, &session.endpoint.host, session.endpoint.port),
            commands,
            running: None,
        };
        let started = match relay {
            Some(relay) => control.start(relay),
            // A document's block that is on was read before the session
            // started; one that failed there was reported there.
            None if control.settings.enabled && matches!(control.place, Place::Store { .. }) => {
                control.relay().and_then(|relay| control.start(relay))
            }
            None => Ok(()),
        };
        if let Err(error) = started {
            errln!(
                "openac: session {}: no remote endpoint: {error}",
                session.id
            );
        }
        control
    }

    /// Follow `event`: carry out a `/remoteac` request, and keep the relay
    /// told what the session is doing.
    pub(crate) fn observe(&mut self, event: &HostEvent) {
        if let HostEvent::RemoteCommand(command) = event {
            let text = self.handle(command.clone());
            let _ = self.commands.send(SessionCommand::ShowText {
                text,
                log_text_type: LogTextType::Default,
            });
            return;
        }
        if let Some(status) = status_for(event) {
            status.apply_to(&mut self.hello);
            if let Some(remote) = &self.running {
                remote.status(status);
            }
        }
    }

    /// Print what the endpoint of session `id` has to say.
    pub(crate) fn print_notes(&self, id: &str) {
        if let Some(remote) = &self.running {
            for note in remote.take_notes() {
                errln!("[{id}] {note}");
            }
        }
    }

    /// Carry out `command`; the answer for the chat. No answer names the
    /// token.
    fn handle(&mut self, command: RemoteCommand) -> String {
        match command {
            RemoteCommand::Url(text) => {
                if let Err(error) = RelayUrl::parse(&text) {
                    return format!("remoteac: {error}");
                }
                let before = self.settings.url.replace(text);
                if let Err(error) = self.place.save(&self.settings) {
                    self.settings.url = before;
                    return format!("remoteac: the url was not set: {error}");
                }
                self.reconnect_if_on("url set")
            }
            RemoteCommand::Secret(token) => {
                let reference = self.token_reference.clone();
                if let Err(error) = write_token(&self.config_directory, &reference, token.reveal())
                {
                    return format!("remoteac: {error}");
                }
                drop(token);
                let before = self.settings.credential.replace(CredentialReference {
                    provider: CredentialProvider::File,
                    reference,
                });
                if let Err(error) = self.place.save(&self.settings) {
                    self.settings.credential = before;
                    return format!("remoteac: the secret was not set: {error}");
                }
                self.reconnect_if_on("secret set")
            }
            RemoteCommand::Enable => {
                if self.running.is_some() {
                    return "remoteac: already enabled".to_owned();
                }
                if let Err(error) = self.relay().and_then(|relay| self.start(relay)) {
                    return format!("remoteac: {error}");
                }
                self.settings.enabled = true;
                // The world's messages are kept from the endpoint's start: a
                // browser sees only what came after it, until the server
                // sends the world again.
                let late = if self.hello.state == SessionState::InWorld {
                    "; the 3D world fills in from the next login, portal or recall"
                } else {
                    ""
                };
                match self.place.save(&self.settings) {
                    Ok(()) => format!("remoteac: enabled{late}"),
                    Err(error) => format!("remoteac: enabled for this run only{late}: {error}"),
                }
            }
            RemoteCommand::Disable => {
                self.stop();
                // Nothing is offered any more: the session stops keeping the
                // world's messages.
                let keep = ac_runtime::session::world_tap::WorldTapCommand::Keep(false);
                let _ = self.commands.send(SessionCommand::WorldTap(keep));
                self.settings.enabled = false;
                match self.place.save(&self.settings) {
                    Ok(()) => "remoteac: disabled".to_owned(),
                    Err(error) => format!("remoteac: disabled for this run only: {error}"),
                }
            }
            RemoteCommand::Status => self.status_text(),
        }
    }

    /// `done`, after connecting again with the new setting when the remote
    /// window is on.
    fn reconnect_if_on(&mut self, done: &str) -> String {
        if !self.settings.enabled {
            return format!("remoteac: {done}");
        }
        match self.relay().and_then(|relay| self.start(relay)) {
            Ok(()) => format!("remoteac: {done}; connecting again"),
            Err(error) => format!("remoteac: {done}; not connected: {error}"),
        }
    }

    /// The relay the settings name, with its token read.
    fn relay(&self) -> Result<Relay, String> {
        let (url, credential) = match (&self.settings.url, &self.settings.credential) {
            (Some(url), Some(credential)) => (url, credential),
            (None, Some(_)) => return Err(NO_URL.to_owned()),
            (Some(_), None) => return Err(NO_SECRET.to_owned()),
            (None, None) => return Err(format!("{NO_URL}, and {NO_SECRET}")),
        };
        let url = RelayUrl::parse(url).map_err(|error| error.to_string())?;
        // A token is never read from standard input while the session runs.
        let mut input = std::io::empty();
        let platform = SystemPlatform;
        let mut resolver = CredentialResolver::new(&mut input, &self.config_directory, &platform);
        let token = resolver
            .resolve(&self.session_id, credential)
            .map_err(|error| format!("the secret cannot be read: {error}"))?;
        Ok(Relay {
            url,
            token: Secret::new(token.reveal()),
        })
    }

    /// Start the endpoint on `relay`, in place of one already running.
    fn start(&mut self, relay: Relay) -> Result<(), String> {
        self.stop();
        let mut hello = self.hello.clone();
        hello.instance_id = new_instance_id();
        let link = LinkConfig::new(relay.url, relay.token, hello);
        let remote = Remote::start(link, WireCodec::new(), self.commands.clone())
            .map_err(|error| format!("the endpoint cannot start: {error}"))?;
        self.running = Some(remote);
        Ok(())
    }

    /// Stop the endpoint: an attached browser is let go, and its keys with
    /// it; the session goes on.
    fn stop(&mut self) {
        if let Some(remote) = self.running.take() {
            remote.shutdown();
        }
    }

    /// `/remoteac status`: never the token.
    fn status_text(&self) -> String {
        let state = self.running.as_ref().map(Remote::state);
        let link = match state.as_ref().map(|s| &s.link) {
            None => "off".to_owned(),
            Some(LinkState::Connecting) => "connecting".to_owned(),
            Some(LinkState::Connected) => "connected".to_owned(),
            Some(LinkState::Refused { reason, retry_in }) => format!(
                "refused ({reason}); trying again in {} s",
                retry_in.as_secs()
            ),
            Some(LinkState::BackingOff { reason, retry_in }) => format!(
                "not connected ({reason}); trying again in {} s",
                retry_in.as_secs()
            ),
        };
        let attached = state.as_ref().is_some_and(|s| s.attached);
        let on = if self.settings.enabled {
            "enabled"
        } else {
            "disabled"
        };
        let secret = if self.settings.credential.is_some() {
            "set"
        } else {
            "not set"
        };
        let mut lines = vec![
            format!("remoteac: {on}"),
            format!("url: {}", self.settings.url.as_deref().unwrap_or("not set")),
            format!("secret: {secret}"),
            format!("link: {link}"),
            format!("browser: {}", if attached { "attached" } else { "none" }),
            format!(
                "world sent: {} frames, {} KB",
                state.as_ref().map_or(0, |s| s.world_frames),
                state.as_ref().map_or(0, |s| s.world_bytes) / 1024
            ),
            format!("kept in: {}", self.place.describe()),
        ];
        if let Some(error) = &self.unreadable {
            lines.push(format!("the kept settings cannot be read: {error}"));
        }
        lines.join("\n")
    }
}

/// What `/remoteac enable` says without an address.
const NO_URL: &str = "no url set (/remoteac url \"wss://...\")";
/// What `/remoteac enable` says without a token.
const NO_SECRET: &str = "no secret set (/remoteac secret \"<token>\")";

/// What `event` changes in what the relay is told, if anything.
fn status_for(event: &HostEvent) -> Option<Status> {
    match event {
        HostEvent::Lifecycle(lifecycle) => Some(ac_remote::status::lifecycle(lifecycle)),
        HostEvent::Session(SessionEvent::EnteredWorld(character)) => Some(
            ac_remote::status::entered_world(character.id, &character.name),
        ),
        HostEvent::Portal(snapshot) => ac_remote::status::portal(snapshot),
        HostEvent::Relogin(_) => Some(ac_remote::status::relogin()),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use ac_runtime::session::{Generation, Lifecycle, LifecycleState, Relogin};

    use super::*;

    #[test]
    fn the_hello_names_the_session_and_its_server_and_offers_everything() {
        let hello = hello("main", "play.example", 9000);
        assert_eq!(hello.config_session_id, "main");
        assert_eq!(hello.server, "play.example:9000");
        assert_eq!(hello.state, SessionState::Pregame);
        assert_eq!(hello.character_name, "");
        assert_eq!(hello.character_guid, 0);
        assert_eq!(hello.client_version, env!("CARGO_PKG_VERSION"));
        assert_eq!(hello.shape, WireCodec::shape());
        assert_eq!(
            hello.capabilities,
            [Capability::View, Capability::Control, Capability::Lifecycle]
        );
        // Each start is its own instance.
        assert_ne!(hello.instance_id, super::hello("main", "h", 1).instance_id);
    }

    #[test]
    fn the_relay_hears_the_lifecycle_and_a_relogin_and_nothing_of_chatter() {
        let in_world = HostEvent::Lifecycle(Lifecycle {
            generation: Generation(2),
            state: LifecycleState::InWorld,
            player_guid: 0x5000_0001,
            has_transport: true,
        });
        let status = status_for(&in_world).unwrap();
        assert_eq!(status.generation, Some(2));
        assert_eq!(status.state, Some(SessionState::InWorld));
        let lost = HostEvent::Relogin(Relogin::Scheduled {
            after: std::time::Duration::from_secs(30),
        });
        assert_eq!(
            status_for(&lost).unwrap().state,
            Some(SessionState::Reconnecting)
        );
        assert_eq!(status_for(&HostEvent::InterfaceText("hi".into())), None);
    }

    use crate::remote_settings::tests::{scratch, session};
    use crate::remote_settings::{STORE_FILE_NAME, StoreKey};
    use ac_host::CharacterSelector;
    use ac_runtime::communication::remote_command::parse;
    use std::sync::mpsc::{self, Receiver};

    /// Type `line` into `control` as the session hands it over; the answer
    /// it puts in the chat.
    fn typed(control: &mut RemoteControl, chat: &Receiver<SessionCommand>, line: &str) -> String {
        let command = parse(line).unwrap().unwrap();
        control.observe(&HostEvent::RemoteCommand(command));
        chat.try_iter()
            .find_map(|c| match c {
                SessionCommand::ShowText { text, .. } => Some(text),
                _ => None,
            })
            .expect("an answer in the chat")
    }

    #[test]
    fn remoteac_sets_enables_disables_and_keeps_it_all_without_the_token() {
        let dir = scratch("control");
        let me = session(Some(CharacterSelector::Name("A guy".into())));
        let document = dir.join("session.json");
        let (to_session, chat) = mpsc::channel();
        let mut control = RemoteControl::new(&me, &document, &dir, None, to_session.clone());
        // Off by default, nothing set, nothing connecting.
        let status = typed(&mut control, &chat, "/remoteac status");
        assert!(status.contains("remoteac: disabled"), "{status}");
        assert!(status.contains("url: not set"), "{status}");
        assert!(status.contains("secret: not set"), "{status}");
        assert!(status.contains("link: off"), "{status}");
        assert!(status.contains("browser: none"), "{status}");
        // Enable says what is missing.
        let answer = typed(&mut control, &chat, "/remoteac enable");
        assert!(answer.contains("no url set") && answer.contains("no secret set"));
        // The address rules are the link's.
        for bad in ["https://relay.example/", "ws://relay.example/ws"] {
            let answer = typed(&mut control, &chat, &format!("/remoteac url \"{bad}\""));
            assert!(answer.starts_with("remoteac: "), "{answer}");
            assert!(!answer.contains("url set"), "{bad}: {answer}");
        }
        assert_eq!(
            typed(&mut control, &chat, "/remoteac url \"ws://127.0.0.1:9/ws\""),
            "remoteac: url set"
        );
        let answer = typed(&mut control, &chat, "/remoteac enable");
        assert!(answer.contains("no secret set") && !answer.contains("no url"));
        // The secret: confirmed, never shown, kept in its own file only.
        assert_eq!(
            typed(&mut control, &chat, "/remoteac secret \"hunter2-token\""),
            "remoteac: secret set"
        );
        let status = typed(&mut control, &chat, "/remoteac status");
        assert!(status.contains("secret: set"), "{status}");
        assert!(!status.contains("hunter2"), "{status}");
        let store = std::fs::read_to_string(dir.join(STORE_FILE_NAME)).unwrap();
        assert!(!store.contains("hunter2"), "{store}");
        let token = dir.join(StoreKey::of(&me).token_reference());
        assert_eq!(std::fs::read_to_string(token).unwrap(), "hunter2-token");
        // On: the link runs (a closed loopback port: it backs off).
        assert_eq!(
            typed(&mut control, &chat, "/remoteac enable"),
            "remoteac: enabled"
        );
        let status = typed(&mut control, &chat, "/remoteac status");
        assert!(status.contains("remoteac: enabled"), "{status}");
        assert!(!status.contains("link: off"), "{status}");
        assert!(!status.contains("hunter2"), "{status}");
        // Off: the link stops; the url and the secret are kept.
        assert_eq!(
            typed(&mut control, &chat, "/remoteac disable"),
            "remoteac: disabled"
        );
        assert!(typed(&mut control, &chat, "/remoteac status").contains("link: off"));
        drop(control);
        // A restart reads it all back, off.
        let mut again = RemoteControl::new(&me, &document, &dir, None, to_session);
        let status = typed(&mut again, &chat, "/remoteac status");
        assert!(status.contains("remoteac: disabled"), "{status}");
        assert!(status.contains("url: ws://127.0.0.1:9/ws"), "{status}");
        assert!(status.contains("secret: set"), "{status}");
        assert!(status.contains("link: off"), "{status}");
        // Another character of the same account has nothing set.
        let other = session(Some(CharacterSelector::Name("Someone".into())));
        let (to_other, other_chat) = mpsc::channel();
        let mut theirs = RemoteControl::new(&other, &document, &dir, None, to_other);
        let status = typed(&mut theirs, &other_chat, "/remoteac status");
        assert!(status.contains("url: not set"), "{status}");
        drop((again, theirs));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_stored_switch_that_is_on_connects_at_start() {
        let dir = scratch("autostart");
        let me = session(Some(CharacterSelector::Id(0x5000_0001)));
        let document = dir.join("session.json");
        let (to_session, chat) = mpsc::channel();
        let mut first = RemoteControl::new(&me, &document, &dir, None, to_session.clone());
        typed(&mut first, &chat, "/remoteac url ws://127.0.0.1:9/ws");
        typed(&mut first, &chat, "/remoteac secret t");
        assert_eq!(
            typed(&mut first, &chat, "/remoteac enable"),
            "remoteac: enabled"
        );
        drop(first);
        let mut second = RemoteControl::new(&me, &document, &dir, None, to_session);
        let status = typed(&mut second, &chat, "/remoteac status");
        assert!(status.contains("remoteac: enabled"), "{status}");
        assert!(!status.contains("link: off"), "{status}");
        drop(second);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
