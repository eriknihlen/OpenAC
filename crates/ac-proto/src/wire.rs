//! Small reading and writing helpers shared by the gameplay messages.

use ac_bits::{Reader, Writer, cp1252};

use crate::{GameActionType, Opcode, ProtoError, Result};

/// A u16-length Windows-1252 string padded to 4 bytes from the record start.
/// Padding missing at the very end of the body is tolerated: the cursor
/// stops at the end and any further read fails.
pub(crate) fn read_string(r: &mut Reader<'_>) -> Result<String> {
    let len = usize::from(r.u16()?);
    let text = cp1252::decode(r.bytes(len)?);
    let pad = (4 - ((2 + len) & 3)) & 3;
    r.skip(pad.min(r.remaining()))?;
    Ok(text)
}

/// A `u32` that is true when non-zero.
pub(crate) fn read_bool32(r: &mut Reader<'_>) -> Result<bool> {
    Ok(r.u32()? != 0)
}

/// Read a `u32` count and check that `count` items of at least `min_size`
/// bytes each can still fit in the body.
pub(crate) fn read_count(r: &mut Reader<'_>, min_size: usize, what: &'static str) -> Result<u32> {
    let count = r.u32()?;
    check_count(r, count, min_size, what)?;
    Ok(count)
}

/// Check that `count` items of at least `min_size` bytes each fit in what
/// is left of the body.
pub(crate) fn check_count(
    r: &Reader<'_>,
    count: u32,
    min_size: usize,
    what: &'static str,
) -> Result<()> {
    let needed = (count as usize).checked_mul(min_size);
    if needed.is_none_or(|n| n > r.remaining()) {
        return Err(ProtoError::CountExceedsPayload { what, count });
    }
    Ok(())
}

/// A writer that already holds the game-action header: the envelope
/// opcode, the sequence number and the action type.
pub(crate) fn action(sequence: u32, action: GameActionType) -> Writer {
    let mut w = Writer::new();
    w.u32(Opcode::GameAction.value());
    w.u32(sequence);
    w.u32(action.0);
    w
}

/// A game action with no arguments.
pub(crate) fn bare_action(sequence: u32, action_type: GameActionType) -> Vec<u8> {
    action(sequence, action_type).into_vec()
}

/// A game action whose only argument is one `u32`.
pub(crate) fn u32_action(sequence: u32, action_type: GameActionType, value: u32) -> Vec<u8> {
    let mut w = action(sequence, action_type);
    w.u32(value);
    w.into_vec()
}

/// A game action whose only argument is one string.
pub(crate) fn string_action(
    sequence: u32,
    action_type: GameActionType,
    text: &str,
) -> Result<Vec<u8>> {
    let mut w = action(sequence, action_type);
    w.string16l(text)?;
    Ok(w.into_vec())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn read_string_tolerates_missing_final_padding() {
        // "abc": 2 + 3 = 5 bytes, 3 bytes of padding absent.
        let mut r = Reader::new(&[3, 0, b'a', b'b', b'c']);
        assert_eq!(read_string(&mut r).as_deref(), Ok("abc"));
        assert_eq!(r.remaining(), 0);
        // A body cut inside the text is still an error.
        assert!(read_string(&mut Reader::new(&[3, 0, b'a', b'b'])).is_err());
    }

    #[test]
    fn count_check_rejects_counts_the_body_cannot_hold() {
        let r = Reader::new(&[0; 8]);
        assert!(check_count(&r, 2, 4, "x").is_ok());
        assert_eq!(
            check_count(&r, 3, 4, "x"),
            Err(ProtoError::CountExceedsPayload {
                what: "x",
                count: 3
            })
        );
        assert!(check_count(&r, u32::MAX, usize::MAX, "x").is_err());
    }
}
