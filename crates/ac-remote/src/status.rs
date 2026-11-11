//! What the relay is told a session is doing, from what the session tells
//! its host.

use ac_runtime::session::{Lifecycle, LifecycleState};
use ac_runtime::world::transit::PortalSnapshot;

use crate::protocol::{SessionState, Status};

fn nothing() -> Status {
    Status {
        generation: None,
        character_name: None,
        character_guid: None,
        state: None,
    }
}

/// The session's lifecycle changed. Outside the world there is no
/// character to name.
#[must_use]
pub fn lifecycle(lifecycle: &Lifecycle) -> Status {
    let generation = Some(lifecycle.generation.0);
    match lifecycle.state {
        LifecycleState::InWorld => Status {
            generation,
            character_guid: Some(lifecycle.player_guid),
            state: Some(SessionState::InWorld),
            ..nothing()
        },
        LifecycleState::Starting | LifecycleState::Stopping | LifecycleState::Faulted => {
            no_character(generation, SessionState::Pregame)
        }
        // Between two connections (the endpoint of a session that ended
        // for good is stopped with it).
        LifecycleState::Stopped => no_character(generation, SessionState::Reconnecting),
    }
}

fn no_character(generation: Option<u64>, state: SessionState) -> Status {
    Status {
        generation,
        character_name: Some(String::new()),
        character_guid: Some(0),
        state: Some(state),
    }
}

/// A character entered the world.
#[must_use]
pub fn entered_world(guid: u32, name: &str) -> Status {
    Status {
        character_name: Some(name.to_owned()),
        character_guid: Some(guid),
        state: Some(SessionState::InWorld),
        ..nothing()
    }
}

/// Portal space changed; `None` when the snapshot says nothing (no reveal
/// yet).
#[must_use]
pub fn portal(snapshot: &PortalSnapshot) -> Option<Status> {
    if snapshot.generation == 0 {
        return None;
    }
    let state = if snapshot.completed || snapshot.cancelled {
        SessionState::InWorld
    } else {
        SessionState::PortalSpace
    };
    Some(Status {
        state: Some(state),
        ..nothing()
    })
}

/// The server was lost and the session is logging in again.
#[must_use]
pub fn relogin() -> Status {
    Status {
        state: Some(SessionState::Reconnecting),
        ..nothing()
    }
}

#[cfg(test)]
mod tests {
    use ac_runtime::session::Generation;

    use super::*;

    fn at(state: LifecycleState, player_guid: u32) -> Lifecycle {
        Lifecycle {
            generation: Generation(4),
            state,
            player_guid,
            has_transport: true,
        }
    }

    #[test]
    fn in_the_world_the_lifecycle_names_the_generation_and_the_character() {
        let status = lifecycle(&at(LifecycleState::InWorld, 0x5000_0002));
        assert_eq!(status.generation, Some(4));
        assert_eq!(status.state, Some(SessionState::InWorld));
        assert_eq!(status.character_guid, Some(0x5000_0002));
        // The name comes with the entry.
        assert_eq!(status.character_name, None);
    }

    #[test]
    fn outside_the_world_there_is_no_character() {
        for state in [
            LifecycleState::Starting,
            LifecycleState::Stopping,
            LifecycleState::Faulted,
        ] {
            let status = lifecycle(&at(state, 0));
            assert_eq!(status.state, Some(SessionState::Pregame), "{state:?}");
            assert_eq!(status.character_guid, Some(0));
            assert_eq!(status.character_name.as_deref(), Some(""));
            assert_eq!(status.generation, Some(4));
        }
        let status = lifecycle(&at(LifecycleState::Stopped, 0));
        assert_eq!(status.state, Some(SessionState::Reconnecting));
        assert_eq!(status.character_name.as_deref(), Some(""));
    }

    #[test]
    fn an_entry_names_the_character() {
        let status = entered_world(0x5000_0002, "Second");
        assert_eq!(status.character_name.as_deref(), Some("Second"));
        assert_eq!(status.character_guid, Some(0x5000_0002));
        assert_eq!(status.state, Some(SessionState::InWorld));
        assert_eq!(status.generation, None);
    }

    #[test]
    fn a_reveal_under_way_is_portal_space_until_it_ends() {
        assert_eq!(portal(&PortalSnapshot::IDLE), None);
        let under_way = PortalSnapshot {
            generation: 3,
            ..PortalSnapshot::IDLE
        };
        let state = |snapshot: &PortalSnapshot| portal(snapshot).and_then(|s| s.state);
        assert_eq!(state(&under_way), Some(SessionState::PortalSpace));
        let completed = PortalSnapshot {
            completed: true,
            ..under_way
        };
        assert_eq!(state(&completed), Some(SessionState::InWorld));
        let cancelled = PortalSnapshot {
            cancelled: true,
            ..under_way
        };
        assert_eq!(state(&cancelled), Some(SessionState::InWorld));
    }

    #[test]
    fn a_relogin_is_reconnecting() {
        assert_eq!(relogin().state, Some(SessionState::Reconnecting));
    }
}
