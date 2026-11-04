//! Typed [`Host`] methods for API group M: the other clients on this
//! computer, and the session's status board.

use ac_plugin_abi::calls;
use ac_plugin_abi::peers::{
    Broadcast, CastAnnouncement, NetworkClient, PeerCast, PeerCommand, RemoteBroadcast, RemoteCast,
};

use crate::Host;

/// M. Peers and the status board.
impl Host<'_> {
    /// The other clients on this computer, by name.
    pub fn peer_clients(&mut self) -> Vec<NetworkClient> {
        self.call::<calls::PeersClients>(&())
    }

    /// Tell the other clients a cast is being attempted.
    pub fn announce_cast_attempt(
        &mut self,
        target_id: u32,
        spell_id: u32,
        effective_skill: i32,
    ) -> bool {
        self.call::<calls::PeersAnnounceCast>(&CastAnnouncement {
            target_id,
            spell_id,
            effective_skill,
            duration_seconds: 0.0,
            landed: false,
        })
    }

    /// Tell the other clients a cast landed and lasts `duration_seconds`.
    pub fn announce_cast_success(
        &mut self,
        target_id: u32,
        spell_id: u32,
        effective_skill: i32,
        duration_seconds: f64,
    ) -> bool {
        self.call::<calls::PeersAnnounceCast>(&CastAnnouncement {
            target_id,
            spell_id,
            effective_skill,
            duration_seconds,
            landed: true,
        })
    }

    /// The casts other clients announced above `after_sequence`.
    pub fn peer_casts(&mut self, after_sequence: i64) -> Vec<PeerCast> {
        self.call::<calls::PeersCasts>(&after_sequence)
    }

    /// The labels this client answers to; false for one too long.
    pub fn set_peer_tags(&mut self, tags: &[String]) -> bool {
        self.call::<calls::PeersSetTags>(&tags.to_vec())
    }

    /// Ask the other clients (wearing one of `tags`; all with none) to run
    /// `line`, `delay_ms` apart.
    pub fn broadcast_command(&mut self, line: &str, tags: &[String], delay_ms: i32) -> bool {
        self.call::<calls::PeersBroadcast>(&Broadcast {
            line: line.to_owned(),
            tags: tags.to_vec(),
            delay_ms,
        })
    }

    /// The lines other clients broadcast to this one above
    /// `after_sequence` (the client runs them itself).
    pub fn peer_commands(&mut self, after_sequence: i64) -> Vec<PeerCommand> {
        self.call::<calls::PeersCommands>(&after_sequence)
    }

    /// Take part in the peer features `capabilities` names over the peer
    /// hub (OpenAC `Subscribe`): nothing is shared until a plugin holds a
    /// lease. None for no capability or an unknown one.
    pub fn peer_subscribe(&mut self, capabilities: u32) -> Option<u32> {
        self.call::<calls::PeersSubscribe>(&capabilities)
    }

    /// Give back a lease [`Self::peer_subscribe`] handed out, when the
    /// plugin stops sharing (OpenAC disposing the lease); false for a
    /// handle this plugin does not hold. Leases still held are given back
    /// when the plugin is unloaded.
    pub fn peer_unsubscribe(&mut self, handle: u32) -> bool {
        self.call::<calls::PeersUnsubscribe>(&handle)
    }

    /// Whether this client is registered with the peer hub and has heard
    /// back (OpenAC `IsConnected`).
    pub fn peers_connected(&mut self) -> bool {
        self.call::<calls::PeersIsConnected>(&())
    }

    /// What this client tells the others about its own character now
    /// (OpenAC `TryCaptureSelf`).
    pub fn peer_self(&mut self) -> Option<NetworkClient> {
        self.call::<calls::PeersCaptureSelf>(&())
    }

    /// The casts this client announced above `after_sequence`, for a relay
    /// to pass on (OpenAC `CaptureOwnCasts`).
    pub fn own_peer_casts(&mut self, after_sequence: i64) -> Vec<PeerCast> {
        self.call::<calls::PeersOwnCasts>(&after_sequence)
    }

    /// The lines this client broadcast above `after_sequence`, for a relay
    /// to pass on (OpenAC `CaptureOwnCommands`).
    pub fn own_peer_commands(&mut self, after_sequence: i64) -> Vec<PeerCommand> {
        self.call::<calls::PeersOwnCommands>(&after_sequence)
    }

    /// Add or refresh a peer from elsewhere (OpenAC `ImportRemoteClient`).
    pub fn import_peer_client(&mut self, client: &NetworkClient) -> bool {
        self.call::<calls::PeersImportClient>(client)
    }

    /// Add a cast an imported peer made (OpenAC `ImportRemoteCast`).
    pub fn import_peer_cast(&mut self, cast: &RemoteCast) -> bool {
        self.call::<calls::PeersImportCast>(cast)
    }

    /// Add a line an imported peer broadcast; this client runs it when it
    /// is aimed at its labels (OpenAC `ImportRemoteCommand`). Import lines
    /// only from senders the relay trusts: they run as typed here.
    pub fn import_peer_command(&mut self, command: &RemoteBroadcast) -> bool {
        self.call::<calls::PeersImportCommand>(command)
    }

    /// Publish a value under `key` on the session's status board (none
    /// withdraws it).
    pub fn status_publish(&mut self, key: &str, value: Option<&str>) -> bool {
        self.call::<calls::StatusPublish>(&(key.to_owned(), value.map(str::to_owned)))
    }

    /// What plugin `plugin_id` published under `key`.
    pub fn status_read(&mut self, plugin_id: &str, key: &str) -> Option<String> {
        self.call::<calls::StatusRead>(&(plugin_id.to_owned(), key.to_owned()))
    }

    /// Everything plugin `plugin_id` published.
    pub fn status_capture(&mut self, plugin_id: &str) -> Vec<(String, String)> {
        self.call::<calls::StatusCapture>(&plugin_id.to_owned())
    }

    /// Hear [`Event::StatusChanged`](crate::Event::StatusChanged) when a
    /// value plugin `plugin_id` publishes changes: at most one per key per
    /// pass, with its value at the pass's end (API 1.8). The plugin need not
    /// be loaded yet. Subscribing tells nothing of the values already
    /// there: read them with [`Self::status_capture`]. False for a blank
    /// id, this plugin's own, or past
    /// [`MAX_SUBSCRIPTIONS`](crate::status::MAX_SUBSCRIPTIONS)
    /// plugins.
    pub fn status_subscribe(&mut self, plugin_id: &str) -> bool {
        self.call::<calls::StatusSubscribe>(&plugin_id.to_owned())
    }

    /// Stop hearing plugin `plugin_id`'s changes; false when not subscribed
    /// to it. Subscriptions also end when this plugin stops.
    pub fn status_unsubscribe(&mut self, plugin_id: &str) -> bool {
        self.call::<calls::StatusUnsubscribe>(&plugin_id.to_owned())
    }
}
