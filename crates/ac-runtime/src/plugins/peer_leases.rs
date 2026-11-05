//! Peer subscriptions (OpenAC `ScopedPeerAutomation`): a plugin takes part
//! in the peer features a capability mask names for as long as it holds the
//! lease it was handed, and gives the lease back when it stops sharing. The
//! leases belong to the plugin, so whatever it still holds when it is
//! unloaded is given back for it, and a lease cannot be given back twice or
//! by another plugin.
//!
//! The leases come from the session's peer hub connection
//! ([`super::peer_transport::PeerTransport`]), which every plugin of the
//! session shares; this is only the bookkeeping of who holds which.

pub use super::peer_wire::{ALL_CAPABILITIES, CASTS, CLIENT_STATE, COMMANDS, SEND_COMMANDS};

/// Where leases come from: the session's peer hub connection.
pub trait PeerHub {
    /// Take part in `capabilities` (already checked): the hub's lease, or
    /// none when the hub refuses.
    fn subscribe(&mut self, capabilities: u32) -> Option<u64>;
    /// Give back a lease `subscribe` handed out.
    fn release(&mut self, lease: u64);
}

/// One lease a plugin holds.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Held {
    handle: u32,
    plugin: String,
    lease: u64,
}

/// The leases the session's plugins hold.
#[derive(Debug, Default)]
pub struct PeerLeases {
    next_handle: u32,
    held: Vec<Held>,
}

impl PeerLeases {
    /// Plugin `plugin` takes part in `capabilities` through `hub`: the
    /// handle it gives back with [`Self::unsubscribe`], or none for no
    /// capability, one this client does not know, or a hub that refuses.
    pub fn subscribe(
        &mut self,
        plugin: &str,
        capabilities: u32,
        hub: &mut dyn PeerHub,
    ) -> Option<u32> {
        if capabilities == 0 || capabilities & !ALL_CAPABILITIES != 0 {
            return None;
        }
        let lease = hub.subscribe(capabilities)?;
        self.next_handle = self.next_handle.checked_add(1).unwrap_or(1);
        let handle = self.next_handle;
        self.held.push(Held {
            handle,
            plugin: plugin.to_owned(),
            lease,
        });
        Some(handle)
    }

    /// Plugin `plugin` gives back lease `handle`: false when it holds no
    /// such lease (never handed out, already given back, or another
    /// plugin's).
    pub fn unsubscribe(&mut self, plugin: &str, handle: u32, hub: &mut dyn PeerHub) -> bool {
        let Some(index) = self
            .held
            .iter()
            .position(|h| h.handle == handle && h.plugin.eq_ignore_ascii_case(plugin))
        else {
            return false;
        };
        let held = self.held.remove(index);
        hub.release(held.lease);
        true
    }

    /// Plugin `plugin` was unloaded: every lease it still holds is given
    /// back.
    pub fn release_plugin(&mut self, plugin: &str, hub: &mut dyn PeerHub) {
        let (gone, kept): (Vec<Held>, Vec<Held>) = std::mem::take(&mut self.held)
            .into_iter()
            .partition(|h| h.plugin.eq_ignore_ascii_case(plugin));
        self.held = kept;
        for held in gone {
            hub.release(held.lease);
        }
    }

    /// How many leases plugin `plugin` holds.
    #[must_use]
    pub fn held_by(&self, plugin: &str) -> usize {
        self.held
            .iter()
            .filter(|h| h.plugin.eq_ignore_ascii_case(plugin))
            .count()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A hub counting the leases it has out (OpenAC
    /// `ScopedAutomationSurfaceTests.FakeNetworkAutomation`).
    #[derive(Default)]
    struct CountingHub {
        active: i32,
        next: u64,
    }

    impl PeerHub for CountingHub {
        fn subscribe(&mut self, _capabilities: u32) -> Option<u64> {
            self.active += 1;
            self.next += 1;
            Some(self.next)
        }

        fn release(&mut self, _lease: u64) {
            self.active -= 1;
        }
    }

    // OpenAC (origin/main) `tests/AcDream.Core.Tests/Plugins/
    // ScopedAutomationSurfaceTests.cs`
    // `NetworkLeasesAreReleasedOnUnloadAndCannotRestartAfterDisposal`: a
    // lease the plugin still holds is given back when it is unloaded, and
    // giving it back afterwards changes nothing. (The C# scope also refuses
    // calls after its disposal; an unloaded plugin here makes none, and a
    // reloaded one starts with no leases.)
    #[test]
    fn leases_are_given_back_when_the_plugin_is_unloaded() {
        let (mut leases, mut hub) = (PeerLeases::default(), CountingHub::default());
        let lease = leases.subscribe("mm", CASTS, &mut hub).expect("a lease");
        assert_eq!(hub.active, 1);
        leases.release_plugin("mm", &mut hub);
        assert_eq!(hub.active, 0);
        assert!(!leases.unsubscribe("mm", lease, &mut hub));
        assert_eq!(hub.active, 0);
        assert_eq!(leases.held_by("mm"), 0);
    }

    // OpenAC `ScopedPeerAutomation.Lease.Dispose` (once only) and
    // MosswartMassacre `VitalSharing.Stop` (`_peerSubscription?.Dispose()`):
    // stopping sharing gives the lease back; a second give-back is nothing,
    // and another plugin cannot give it back.
    #[test]
    fn a_lease_is_given_back_once_and_only_by_its_plugin() {
        let (mut leases, mut hub) = (PeerLeases::default(), CountingHub::default());
        let lease = leases
            .subscribe("mm", CLIENT_STATE | CASTS, &mut hub)
            .expect("a lease");
        let other = leases
            .subscribe("tank", COMMANDS, &mut hub)
            .expect("a lease");
        assert_ne!(lease, other);
        assert_eq!(hub.active, 2);
        assert!(!leases.unsubscribe("tank", lease, &mut hub));
        assert!(leases.unsubscribe("MM", lease, &mut hub));
        assert_eq!(hub.active, 1);
        assert!(!leases.unsubscribe("mm", lease, &mut hub));
        assert_eq!(hub.active, 1);
        leases.release_plugin("mm", &mut hub);
        assert_eq!(hub.active, 1);
        assert_eq!(leases.held_by("tank"), 1);
    }

    // OpenAC `LocalPeerTransport.Subscribe`: no capability, or one outside
    // ClientState | Casts | Commands | SendCommands, is refused before the
    // hub is asked.
    #[test]
    fn a_mask_naming_nothing_or_an_unknown_capability_is_refused() {
        let (mut leases, mut hub) = (PeerLeases::default(), CountingHub::default());
        assert_eq!(leases.subscribe("mm", 0, &mut hub), None);
        assert_eq!(leases.subscribe("mm", 16, &mut hub), None);
        assert_eq!(
            leases.subscribe("mm", ALL_CAPABILITIES | 32, &mut hub),
            None
        );
        assert_eq!(hub.active, 0);
        assert!(leases.subscribe("mm", ALL_CAPABILITIES, &mut hub).is_some());
    }
}
