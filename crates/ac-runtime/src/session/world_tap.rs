//! The world's own messages, kept as the server sent them, for a viewer
//! that runs the client's world logic somewhere else: it is fed the game
//! messages that create, describe, move and remove objects (and the
//! player's own movement, which the server does not echo), not the
//! session's view of them.
//!
//! While the tap is on, the latest message of each kind is kept for every
//! object, so a listener that arrives later can be given the world as it
//! stands: each object's creation, then what changed it since.

use std::collections::HashMap;
use std::sync::mpsc::Sender;

use ac_proto::Inbound;

/// One game message as it crossed the wire, opcode first.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TappedMessage {
    /// Sent by the client (the player's own movement); else by the server.
    pub client_to_server: bool,
    /// The whole message body.
    pub body: Vec<u8>,
}

/// Messages for a listener, in order, and the player they were seen by.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorldTapBatch {
    /// The session's player object (0 before it has one).
    pub player: u32,
    /// The messages.
    pub messages: Vec<TappedMessage>,
}

/// What a host asks of the tap.
#[derive(Debug, Clone)]
pub enum WorldTapCommand {
    /// Keep the world's messages from now on, or stop and forget them.
    Keep(bool),
    /// A listener arrives (it is sent the world as it stands, then what
    /// happens, once a pass) or, with `None`, leaves.
    Listen(Option<Sender<WorldTapBatch>>),
}

/// The opcode of a game action from the client.
const GAME_ACTION: u32 = 0xF7B1;
/// The player's own movement actions.
const MOVEMENT_ACTIONS: [u32; 3] = [0xF61C, 0xF753, 0xF61B];

/// What is kept of one object: the latest message of each kind.
#[derive(Debug, Default)]
struct Kept {
    create: Option<Vec<u8>>,
    update: Option<Vec<u8>>,
    description: Option<Vec<u8>>,
    state: Option<Vec<u8>>,
    position: Option<Vec<u8>>,
    /// The latest of being parented or picked up.
    held: Option<Vec<u8>>,
    motion: Option<Vec<u8>>,
    vector: Option<Vec<u8>>,
}

/// The tap: off until [`WorldTap::keep`] turns it on.
#[derive(Debug, Default)]
pub struct WorldTap {
    keeping: bool,
    objects: HashMap<u32, Kept>,
    listener: Option<Sender<WorldTapBatch>>,
    /// Messages for the listener since the last [`WorldTap::flush`].
    pending: Vec<TappedMessage>,
}

/// A message of the world's, and the object it is about.
enum About {
    /// The object is created (again): everything kept of it starts over.
    Create(u32),
    /// The object's whole description, again.
    Update(u32),
    /// Its look changed.
    Description(u32),
    State(u32),
    Position(u32),
    /// It was parented or picked up.
    Held(u32),
    Motion(u32),
    Vector(u32),
    /// The object is gone.
    Delete(u32),
    /// Passed on, nothing kept (an effect, the player's teleport).
    Passing,
}

/// What `parsed` is to the tap; `None` for a message that is not the
/// world's.
fn about(parsed: &Inbound<'_>) -> Option<About> {
    Some(match parsed {
        Inbound::CreateObject(create) => About::Create(create.guid),
        Inbound::UpdateObject(update) => About::Update(update.guid),
        Inbound::ObjDescEvent(event) => About::Description(event.guid),
        Inbound::SetState(state) => About::State(state.guid),
        Inbound::UpdatePosition(update) => About::Position(update.guid),
        Inbound::ParentEvent(event) => About::Held(event.child_guid),
        Inbound::PickupEvent(event) => About::Held(event.guid),
        Inbound::UpdateMotion(motion) => About::Motion(motion.guid),
        Inbound::VectorUpdate(vector) => About::Vector(vector.guid),
        Inbound::DeleteObject(delete) => About::Delete(delete.guid),
        Inbound::PlayerTeleport(_) | Inbound::PlayScript(_) | Inbound::PlayScriptType(_) => {
            About::Passing
        }
        _ => return None,
    })
}

impl Kept {
    /// What is kept, in the order a viewer must hear it: the creation
    /// first.
    fn messages(&self) -> impl Iterator<Item = &Vec<u8>> {
        [
            &self.create,
            &self.update,
            &self.description,
            &self.state,
            &self.position,
            &self.held,
            &self.motion,
            &self.vector,
        ]
        .into_iter()
        .flatten()
    }
}

impl WorldTap {
    /// Keep the world's messages from now on (a session that may be
    /// viewed). Off, the tap costs nothing and remembers nothing.
    pub fn keep(&mut self, keeping: bool) {
        self.keeping = keeping;
        if !keeping {
            self.objects.clear();
            self.listener = None;
            self.pending.clear();
        }
    }

    /// A listener arrives (it is sent the world as it stands, as seen by
    /// `player`, restricted to the objects `known` says the session still
    /// has) or, with `None`, leaves.
    pub fn listen(
        &mut self,
        listener: Option<Sender<WorldTapBatch>>,
        player: u32,
        known: impl Fn(u32) -> bool,
    ) {
        self.pending.clear();
        self.listener = None;
        let Some(listener) = listener.filter(|_| self.keeping) else {
            return;
        };
        // What the session let go of without the server saying so is not
        // part of the world any more.
        self.objects.retain(|&guid, _| known(guid));
        let mut guids: Vec<u32> = self.objects.keys().copied().collect();
        guids.sort_unstable();
        let messages = guids
            .iter()
            .filter_map(|guid| self.objects.get(guid))
            .flat_map(Kept::messages)
            .map(|body| TappedMessage {
                client_to_server: false,
                body: body.clone(),
            })
            .collect();
        if listener.send(WorldTapBatch { player, messages }).is_ok() {
            self.listener = Some(listener);
        }
    }

    /// Whether a listener is attached.
    #[must_use]
    pub fn listening(&self) -> bool {
        self.listener.is_some()
    }

    /// The server sent `body`, read as `parsed`.
    pub fn inbound(&mut self, parsed: &Inbound<'_>, body: &[u8]) {
        if !self.keeping {
            return;
        }
        let Some(about) = about(parsed) else {
            return;
        };
        let keep = Some(body.to_vec());
        match about {
            About::Create(guid) => {
                self.objects.insert(
                    guid,
                    Kept {
                        create: keep,
                        ..Kept::default()
                    },
                );
            }
            About::Delete(guid) => {
                self.objects.remove(&guid);
            }
            About::Update(guid) => self.objects.entry(guid).or_default().update = keep,
            About::Description(guid) => self.objects.entry(guid).or_default().description = keep,
            About::State(guid) => self.objects.entry(guid).or_default().state = keep,
            About::Position(guid) => self.objects.entry(guid).or_default().position = keep,
            About::Held(guid) => self.objects.entry(guid).or_default().held = keep,
            About::Motion(guid) => self.objects.entry(guid).or_default().motion = keep,
            About::Vector(guid) => self.objects.entry(guid).or_default().vector = keep,
            About::Passing => {}
        }
        if self.listener.is_some() {
            self.pending.push(TappedMessage {
                client_to_server: false,
                body: body.to_vec(),
            });
        }
    }

    /// The client sends `body`.
    pub fn outbound(&mut self, body: &[u8]) {
        if self.listener.is_none() {
            return;
        }
        let word = |at: usize| {
            body.get(at..at + 4)
                .and_then(|bytes| bytes.try_into().ok())
                .map(u32::from_le_bytes)
        };
        // A game action: its opcode, its sequence, then which action.
        if word(0) == Some(GAME_ACTION) && word(8).is_some_and(|a| MOVEMENT_ACTIONS.contains(&a)) {
            self.pending.push(TappedMessage {
                client_to_server: true,
                body: body.to_vec(),
            });
        }
    }

    /// Forget every object (the connection ended).
    pub fn forget(&mut self) {
        self.objects.clear();
    }

    /// Send the listener what was seen since the last call. A listener
    /// that went away is dropped.
    pub fn flush(&mut self, player: u32) {
        if self.pending.is_empty() {
            return;
        }
        let messages = std::mem::take(&mut self.pending);
        if let Some(listener) = &self.listener
            && listener.send(WorldTapBatch { player, messages }).is_err()
        {
            self.listener = None;
        }
    }

    /// How many objects are kept.
    #[must_use]
    pub fn kept(&self) -> usize {
        self.objects.len()
    }
}

#[cfg(test)]
mod tests {
    use std::sync::mpsc;

    use super::*;

    fn words(words: &[u32]) -> Vec<u8> {
        words.iter().flat_map(|w| w.to_le_bytes()).collect()
    }

    const PLAYER: u32 = 0x5000_000A;

    fn delete(guid: u32) -> Vec<u8> {
        words(&[0xF747, guid, 0x0003])
    }

    fn state(guid: u32, physics_state: u32) -> Vec<u8> {
        words(&[0xF74B, guid, physics_state, 0x0002_0001])
    }

    fn create(guid: u32) -> Vec<u8> {
        // The smallest creation the client reads.
        words(&[0xF745, guid, 0x11, 0, 0, 0, 0, 0, 0, 0])
    }

    fn feed(tap: &mut WorldTap, body: &[u8]) {
        let parsed = Inbound::parse(body).expect("a message the client reads");
        tap.inbound(&parsed, body);
    }

    fn bodies(batch: &WorldTapBatch) -> Vec<&[u8]> {
        batch.messages.iter().map(|m| m.body.as_slice()).collect()
    }

    #[test]
    fn off_it_keeps_nothing() {
        let mut tap = WorldTap::default();
        feed(&mut tap, &create(7));
        assert_eq!(tap.kept(), 0);
    }

    #[test]
    fn a_listener_is_given_the_world_as_it_stands_then_what_happens() {
        let mut tap = WorldTap::default();
        tap.keep(true);
        feed(&mut tap, &create(7));
        feed(&mut tap, &state(7, 0x14));
        feed(&mut tap, &state(7, 0x18));
        feed(&mut tap, &create(8));
        feed(&mut tap, &delete(8));
        feed(&mut tap, &create(9));
        assert_eq!(tap.kept(), 2);

        let (to_listener, listener) = mpsc::channel();
        // The session no longer has object 9.
        tap.listen(Some(to_listener), PLAYER, |guid| guid != 9);
        let snapshot = listener.try_recv().expect("the world as it stands");
        assert_eq!(snapshot.player, PLAYER);
        // The creation, then the latest state; nothing of the deleted or
        // the forgotten object.
        assert_eq!(bodies(&snapshot), [&create(7)[..], &state(7, 0x18)[..]]);
        assert!(snapshot.messages.iter().all(|m| !m.client_to_server));

        // Live: in order, once a pass.
        feed(&mut tap, &state(7, 0x20));
        feed(&mut tap, &delete(7));
        assert!(listener.try_recv().is_err(), "nothing before the flush");
        tap.flush(PLAYER);
        let live = listener.try_recv().unwrap();
        assert_eq!(bodies(&live), [&state(7, 0x20)[..], &delete(7)[..]]);
        tap.flush(PLAYER);
        assert!(listener.try_recv().is_err(), "nothing new, nothing sent");
    }

    #[test]
    fn a_new_creation_replaces_what_was_kept_of_the_object() {
        let mut tap = WorldTap::default();
        tap.keep(true);
        feed(&mut tap, &create(7));
        feed(&mut tap, &state(7, 0x14));
        feed(&mut tap, &create(7));
        let (to_listener, listener) = mpsc::channel();
        tap.listen(Some(to_listener), PLAYER, |_| true);
        assert_eq!(bodies(&listener.try_recv().unwrap()), [&create(7)[..]]);
    }

    #[test]
    fn messages_that_are_not_the_worlds_are_not_tapped() {
        let mut tap = WorldTap::default();
        tap.keep(true);
        let (to_listener, listener) = mpsc::channel();
        tap.listen(Some(to_listener), PLAYER, |_| true);
        drop(listener.try_recv());
        // A character list is nobody's business.
        let body = words(&[0xF7E1, 0x0001_0000]);
        if let Ok(parsed) = Inbound::parse(&body) {
            tap.inbound(&parsed, &body);
        }
        // A chat action from the client is not movement.
        tap.outbound(&words(&[0xF7B1, 1, 0x0015, 0]));
        tap.flush(PLAYER);
        assert!(listener.try_recv().is_err());
    }

    #[test]
    fn the_players_own_movement_is_tapped_on_its_way_out() {
        let mut tap = WorldTap::default();
        tap.keep(true);
        let (to_listener, listener) = mpsc::channel();
        tap.listen(Some(to_listener), PLAYER, |_| true);
        drop(listener.try_recv());
        let moves: Vec<Vec<u8>> = [0xF61C, 0xF753, 0xF61B]
            .iter()
            .map(|&action| words(&[0xF7B1, 4, action, 1, 2]))
            .collect();
        for body in &moves {
            tap.outbound(body);
        }
        tap.flush(PLAYER);
        let batch = listener.try_recv().unwrap();
        assert_eq!(batch.messages.len(), 3);
        assert!(batch.messages.iter().all(|m| m.client_to_server));
        assert_eq!(batch.messages[1].body, moves[1]);
    }

    #[test]
    fn a_listener_that_went_away_is_dropped_and_forgetting_empties_the_tap() {
        let mut tap = WorldTap::default();
        tap.keep(true);
        feed(&mut tap, &create(7));
        let (to_listener, listener) = mpsc::channel();
        tap.listen(Some(to_listener), PLAYER, |_| true);
        drop(listener);
        feed(&mut tap, &state(7, 0x14));
        tap.flush(PLAYER);
        assert!(!tap.listening());
        tap.forget();
        assert_eq!(tap.kept(), 0);
    }
}
