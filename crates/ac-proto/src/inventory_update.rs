//! Inventory bookkeeping messages: a stack's size changed, an object left
//! the player's inventory view.

use ac_bits::Reader;

use crate::{Opcode, Result, expect_opcode};

/// A stack's count and value changed after a merge or split
/// ([`Opcode::SetStackSize`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SetStackSize {
    /// Quality sequence.
    pub sequence: u8,
    /// The stack.
    pub guid: u32,
    /// New number of items in the stack.
    pub stack_size: i32,
    /// New total value.
    pub value: i32,
}

impl SetStackSize {
    /// Parse the body: opcode, sequence, object, stack size, value.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::SetStackSize)?;
        Ok(Self {
            sequence: r.u8()?,
            guid: r.u32()?,
            stack_size: r.i32()?,
            value: r.i32()?,
        })
    }
}

/// An object left the player's inventory view: given away, sold or
/// destroyed ([`Opcode::InventoryRemoveObject`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InventoryRemoveObject {
    /// The object.
    pub guid: u32,
}

impl InventoryRemoveObject {
    /// Parse the body: opcode, object.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::InventoryRemoveObject)?;
        Ok(Self { guid: r.u32()? })
    }
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/SetStackSizeTests.cs` and
    //! `InventoryRemoveObjectTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::ProtoError;
    use crate::test_util::{assert_every_prefix_fails, words};

    fn stack_body(guid: u32, stack_size: i32, value: i32, opcode: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(opcode);
        w.u8(1);
        w.u32(guid);
        w.i32(stack_size);
        w.i32(value);
        w.into_vec()
    }

    #[test]
    fn set_stack_size_returns_guid_stack_and_value() {
        let bytes: &[u8] = &[
            0x97, 0x01, 0x00, 0x00, // opcode 0x0197
            0x01, // sequence
            0x01, 0x0A, 0x00, 0x50, // guid 0x50000A01
            0x19, 0x00, 0x00, 0x00, // 25 items
            0x7D, 0x00, 0x00, 0x00, // value 125
        ];
        assert_eq!(stack_body(0x5000_0A01, 25, 125, 0x0197), bytes);
        assert_eq!(
            SetStackSize::parse(bytes),
            Ok(SetStackSize {
                sequence: 1,
                guid: 0x5000_0A01,
                stack_size: 25,
                value: 125
            })
        );
    }

    #[test]
    fn set_stack_size_rejects_wrong_opcode_and_truncation() {
        assert!(matches!(
            SetStackSize::parse(&stack_body(1, 1, 1, 0x0196)),
            Err(ProtoError::WrongOpcode { .. })
        ));
        assert_every_prefix_fails(&stack_body(1, 1, 1, 0x0197), SetStackSize::parse);
    }

    #[test]
    fn inventory_remove_returns_guid() {
        let bytes: &[u8] = &[0x24, 0x00, 0x00, 0x00, 0x07, 0x0A, 0x00, 0x50];
        assert_eq!(words(&[0x0024, 0x5000_0A07]), bytes);
        assert_eq!(
            InventoryRemoveObject::parse(bytes),
            Ok(InventoryRemoveObject { guid: 0x5000_0A07 })
        );
    }

    #[test]
    fn inventory_remove_rejects_wrong_opcode_and_truncation() {
        assert!(matches!(
            InventoryRemoveObject::parse(&words(&[0x0023, 1])),
            Err(ProtoError::WrongOpcode { .. })
        ));
        assert_every_prefix_fails(&words(&[0x0024, 1]), InventoryRemoveObject::parse);
    }
}
