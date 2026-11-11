//! A session's remote endpoint, for the sessions whose document names a
//! relay: the session offers itself there, to be shown and played somewhere
//! else. Nothing here can stop a session: a relay that cannot be reached, a
//! token that cannot be read or an address that is no address leaves the
//! session running without an endpoint, with a line saying why.

use std::collections::BTreeMap;
use std::sync::mpsc::Sender;

use ac_host::{CredentialResolver, SessionDescriptor};
use ac_remote::protocol::{Capability, Hello, SessionState, Status, new_instance_id};
use ac_remote::remote::Remote;
use ac_remote::wire::WireCodec;
use ac_remote::{LinkConfig, RelayUrl, Secret};
use ac_runtime::session::{HostEvent, SessionCommand, SessionEvent};

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

/// The relay of every session that names one, by session id. A session
/// whose relay cannot be used runs without one.
pub(crate) fn resolve(
    sessions: &[SessionDescriptor],
    resolver: &mut CredentialResolver<'_>,
) -> BTreeMap<String, Relay> {
    let mut relays = BTreeMap::new();
    for session in sessions {
        let Some(remote) = &session.remote else {
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

/// Start the endpoint of `session`, which takes `commands`.
pub(crate) fn start(
    relay: Relay,
    session: &SessionDescriptor,
    commands: Sender<SessionCommand>,
) -> Option<Remote> {
    let hello = hello(&session.id, &session.endpoint.host, session.endpoint.port);
    let link = LinkConfig::new(relay.url, relay.token, hello);
    match Remote::start(link, WireCodec::new(), commands) {
        Ok(remote) => Some(remote),
        Err(error) => {
            errln!(
                "openac: session {}: no remote endpoint: {error}",
                session.id
            );
            None
        }
    }
}

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

/// Keep the relay told what the session is doing.
pub(crate) fn follow(remote: &Remote, event: &HostEvent) {
    if let Some(status) = status_for(event) {
        remote.status(status);
    }
}

/// Print what the endpoint of session `id` has to say.
pub(crate) fn print_notes(id: &str, remote: &Remote) {
    for note in remote.take_notes() {
        errln!("[{id}] {note}");
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
}
