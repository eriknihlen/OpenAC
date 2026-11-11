//! The encoding of a session's presentation stream and of the commands a
//! window sends back, for a window on the far side of a network link.
//!
//! A session streams [`PresentationEvent`]s to an attached window and takes
//! [`SessionCommand`]s back. Here both become bytes:
//!
//! - a **batch** ([`encode_batch`], [`decode_batch`]) is one buffer of
//!   events: the stream-format version byte ([`STREAM_FORMAT`]), a `u32`
//!   count, then the events;
//! - a **command frame** ([`encode_command`], [`decode_command`]) is the
//!   version byte and one command.
//!
//! Neither carries its own length: a WebSocket frame (or a recorded
//! record) does. Bytes left over after the last value are an error.
//!
//! # The encoding
//!
//! Each type of the stream implements [`Wire`], this crate's own trait: the
//! types live in other crates, and only a trait of this crate may be
//! implemented for them here (the plugin calling convention's trait cannot
//! be). The trait and its macros follow that convention's style and reuse
//! its byte buffer and its compile-time shape hash; the reader is this
//! crate's, because it checks every count against the bytes left before it
//! allocates and stops nesting past [`codec::MAX_DEPTH`], and it reports
//! this stream's own errors.
//!
//! Every struct is taken apart with every field named and built with every
//! field named, and every enum is matched with no wildcard, so a field or
//! variant added in the runtime breaks this crate's build instead of
//! changing the stream unseen. A type whose fields are private goes out
//! through its owning crate's `Parts` view, whose conversions name every
//! field in turn.
//!
//! Integers and floats are little-endian (floats bit-exact, NaN payloads
//! included), maps and sets go out in key order, an `Arc` or `Box` by value
//! (decoded into a fresh one), a [`std::time::Duration`] exactly, and a
//! [`std::time::Instant`] as signed microseconds from the stream's epoch:
//! the encoder's [`EncodeContext`] names the sender's epoch, the decoder's
//! [`DecodeContext`] the receiver's instant for the same moment (taken when
//! the stream starts).
//!
//! # What does not cross
//!
//! - A spellbook's spell table is the game's data, which the receiver has:
//!   it is left out, and a decoded book refers to the
//!   [`DecodeContext::spell_table`].
//! - The function the session installs to recompute skill formula terms has
//!   no encoding; a decoded copy of the player's numbers has none (its
//!   skills carry the terms already computed).
//! - A plugin's baked font ([`ac_plugin_ui::UiChange::FontAdded`]) is
//!   compared by the identity of its bake, so no copy could stand for it:
//!   encoding one is [`EncodeError::Unsupported`].
//! - An instant keeps whole microseconds; a path that is not Unicode cannot
//!   be sent.
//!
//! # Versioning
//!
//! [`shape`] hashes the layout of everything in both directions. Both ends
//! must be built from the same layout: compare it before any data (the
//! recorder writes it into its header). A deliberate change of layout
//! changes the shape and bumps [`STREAM_FORMAT`].

pub mod codec;
mod commands;
mod interface;
mod plugin_ui;
mod pregame;
mod subscription;
mod world;

#[cfg(test)]
mod tests;

pub use ac_runtime::session::{PresentationEvent, SessionCommand};
pub use codec::{
    DecodeContext, DecodeError, EncodeContext, EncodeError, Reader, Wire, from_bytes, to_bytes,
};
pub use subscription::{Objects, Subscription};

use std::time::Instant;

use ac_proto::effects::{PlayScript, PlayScriptType, SoundEvent};
use ac_runtime::communication::{ChatLine, WindowRequest};
use ac_runtime::navigation::NavigationView;
use ac_runtime::session::LinkStatus;
use ac_runtime::session::interface::InterfaceEvent;
use ac_runtime::session::pregame::PreGameEvent;
use ac_runtime::world::transit::PortalSpaceEvent;
use ac_runtime::world::{AppliedMotion, ObjectDelta, PlayerView, WorldSnapshot};

use codec::{Shape, Writer, union};

/// The stream format this build writes and reads: the first byte of every
/// batch, command frame and recording. Bump it with every deliberate change
/// of [`shape`].
pub const STREAM_FORMAT: u8 = 1;

union! {
    PresentationEvent: "presentation event" {
        0 => Snapshot(snapshot: WorldSnapshot),
        1 => Deltas(deltas: Vec<ObjectDelta>),
        2 => GameTime(ticks: f64),
        3 => Sound(sound: SoundEvent),
        4 => PlayScript(script: PlayScript),
        5 => PlayScriptType(script: PlayScriptType),
        6 => DestroyParticles(object: u32),
        7 => AdminEnvirons(environ: u32),
        8 => Chat(lines: Vec<ChatLine>),
        9 => Player(view: PlayerView),
        10 => Interface(event: InterfaceEvent),
        11 => PortalSpace(event: PortalSpaceEvent),
        12 => PreGame(event: PreGameEvent),
        13 => StatusText(text: String),
        14 => WindowRequest(request: WindowRequest),
        15 => Link(status: LinkStatus),
        16 => Navigation(view: NavigationView),
        17 => Motions(motions: Vec<AppliedMotion>),
        18 => SessionClock(epoch: Instant),
    }
}

/// The layout of everything this crate carries, both ways: it changes when
/// any field, variant or tag of the stream's or the commands' types does.
#[must_use]
pub const fn shape() -> u64 {
    Shape::new("ac-remote-wire")
        .tag(STREAM_FORMAT as u64)
        .of(<PresentationEvent as Wire>::SHAPE)
        .of(<SessionCommand as Wire>::SHAPE)
        .finish()
}

/// [`shape`] as sixteen lowercase hex digits.
#[must_use]
pub fn shape_hex() -> String {
    format!("{:016x}", shape())
}

fn framed(
    write: impl FnOnce(&mut Writer) -> Result<(), EncodeError>,
) -> Result<Vec<u8>, EncodeError> {
    let mut w = Writer::new();
    w.put_raw(&[STREAM_FORMAT]);
    write(&mut w)?;
    Ok(w.into_bytes())
}

fn unframed<'a>(bytes: &'a [u8]) -> Result<Reader<'a>, DecodeError> {
    let mut r = Reader::new(bytes);
    let found = r.byte()?;
    if found != STREAM_FORMAT {
        return Err(DecodeError::Version {
            found,
            expected: STREAM_FORMAT,
        });
    }
    Ok(r)
}

/// One batch of events in one buffer.
///
/// # Errors
/// An event with no faithful encoding ([`EncodeError::Unsupported`]).
pub fn encode_batch(
    events: &[PresentationEvent],
    cx: &EncodeContext,
) -> Result<Vec<u8>, EncodeError> {
    framed(|w| {
        w.put_count(events.len());
        for event in events {
            event.encode(w, cx)?;
        }
        Ok(())
    })
}

/// The events of one batch.
///
/// # Errors
/// Bytes that are not exactly one batch of this stream format.
pub fn decode_batch(
    bytes: &[u8],
    cx: &DecodeContext,
) -> Result<Vec<PresentationEvent>, DecodeError> {
    let mut r = unframed(bytes)?;
    let events = <Vec<PresentationEvent> as Wire>::decode(&mut r, cx)?;
    r.finish()?;
    Ok(events)
}

/// One command in one frame.
///
/// # Errors
/// [`EncodeError::NotAWireCommand`] for `Attach`, `Detach` and `Shutdown`;
/// [`EncodeError::NestedAtGeneration`] for a stamp inside a stamp.
pub fn encode_command(
    command: &SessionCommand,
    cx: &EncodeContext,
) -> Result<Vec<u8>, EncodeError> {
    framed(|w| command.encode(w, cx))
}

/// The command of one frame.
///
/// # Errors
/// Bytes that are not exactly one command frame of this stream format.
pub fn decode_command(bytes: &[u8], cx: &DecodeContext) -> Result<SessionCommand, DecodeError> {
    let mut r = unframed(bytes)?;
    let command = SessionCommand::decode(&mut r, cx)?;
    r.finish()?;
    Ok(command)
}
