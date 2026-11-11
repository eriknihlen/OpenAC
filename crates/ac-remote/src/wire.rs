//! The endpoint's codec: the stream and the commands in the remote-window
//! wire encoding.

use std::time::Instant;

use ac_remote_wire::{DecodeContext, EncodeContext};
use ac_runtime::session::{PresentationEvent, SessionCommand};

use crate::endpoint::Codec;

/// The remote-window wire encoding, for one endpoint. Instants in the
/// stream count from when it was made.
#[derive(Debug)]
pub struct WireCodec {
    encode: EncodeContext,
    decode: DecodeContext,
}

impl WireCodec {
    /// A codec whose stream clock starts now.
    #[must_use]
    pub fn new() -> Self {
        let epoch = Instant::now();
        Self {
            encode: EncodeContext::new(epoch),
            decode: DecodeContext::new(epoch),
        }
    }

    /// The encoding's shape fingerprint, as the hello carries it.
    #[must_use]
    pub fn shape() -> String {
        ac_remote_wire::shape_hex()
    }
}

impl Default for WireCodec {
    fn default() -> Self {
        Self::new()
    }
}

impl Codec for WireCodec {
    fn encode(&mut self, events: &[PresentationEvent]) -> Result<Vec<u8>, String> {
        ac_remote_wire::encode_batch(events, &self.encode).map_err(|error| error.to_string())
    }

    fn decode(&mut self, frame: &[u8]) -> Result<SessionCommand, String> {
        ac_remote_wire::decode_command(frame, &self.decode).map_err(|error| error.to_string())
    }
}

#[cfg(test)]
mod tests {
    use ac_runtime::movement::{MovementCommand, MovementKeys};
    use ac_runtime::session::Generation;

    use super::*;

    #[test]
    fn a_frame_holds_the_events_in_the_wire_encoding() {
        let mut codec = WireCodec::new();
        let events = vec![
            PresentationEvent::StatusText("one".to_owned()),
            PresentationEvent::GameTime(12.5),
        ];
        let frame = codec.encode(&events).unwrap();
        let decoded = ac_remote_wire::decode_batch(&frame, &codec.decode).unwrap();
        assert_eq!(decoded, events);
    }

    #[test]
    fn an_input_frame_is_one_command_in_the_wire_encoding() {
        let mut codec = WireCodec::new();
        let keys = MovementKeys {
            forward: true,
            ..MovementKeys::default()
        };
        let command = SessionCommand::AtGeneration {
            generation: Generation(3),
            command: Box::new(SessionCommand::Movement(MovementCommand::Keys(keys))),
        };
        let frame = ac_remote_wire::encode_command(&command, &codec.encode).unwrap();
        match codec.decode(&frame).unwrap() {
            SessionCommand::AtGeneration {
                generation,
                command,
            } => {
                assert_eq!(generation, Generation(3));
                assert!(matches!(
                    *command,
                    SessionCommand::Movement(MovementCommand::Keys(held)) if held == keys
                ));
            }
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn bytes_that_are_no_command_are_an_error() {
        let mut codec = WireCodec::new();
        assert!(codec.decode(&[]).is_err());
        assert!(codec.decode(&[0xFF; 64]).is_err());
    }

    #[test]
    fn the_shape_is_the_encodings() {
        assert_eq!(WireCodec::shape(), ac_remote_wire::shape_hex());
        assert_eq!(WireCodec::shape().len(), 16);
    }
}
