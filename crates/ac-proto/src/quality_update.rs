//! Single-quality updates: the server changes one property, vital,
//! attribute, skill or saved position on an object, and sends just that
//! value.
//!
//! Every message here starts with the opcode and a one-byte quality
//! sequence. The "public" forms name the object; the "private" forms are
//! about the player's own character and carry no object id. Property ids
//! stay raw `u32`; naming them is the world model's job. Bodies may carry
//! trailing bytes after the last field; they are ignored.

use ac_bits::Reader;

use crate::wire::read_string;
use crate::{Opcode, Position, Result, expect_opcode};

/// Check the opcode and read the one-byte quality sequence that follows it.
fn read_header<'a>(body: &'a [u8], opcode: Opcode) -> Result<(Reader<'a>, u8)> {
    let mut r = Reader::new(body);
    expect_opcode(&mut r, opcode)?;
    let sequence = r.u8()?;
    Ok((r, sequence))
}

/// One integer property changed on a visible object
/// ([`Opcode::PublicUpdatePropertyInt`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PublicUpdatePropertyInt {
    /// Quality sequence.
    pub sequence: u8,
    /// The object.
    pub guid: u32,
    /// Integer property id.
    pub property: u32,
    /// New value.
    pub value: i32,
}

impl PublicUpdatePropertyInt {
    /// Parse the body: opcode, sequence, object, property, value.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PublicUpdatePropertyInt)?;
        Ok(Self {
            sequence,
            guid: r.u32()?,
            property: r.u32()?,
            value: r.i32()?,
        })
    }
}

/// One true/false property changed on the player
/// ([`Opcode::PrivateUpdatePropertyBool`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PrivateUpdatePropertyBool {
    /// Quality sequence.
    pub sequence: u8,
    /// Bool property id.
    pub property: u32,
    /// New value (the wire carries a 32-bit word; non-zero is true).
    pub value: bool,
}

impl PrivateUpdatePropertyBool {
    /// Parse the body: opcode, sequence, property, value word.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdatePropertyBool)?;
        Ok(Self {
            sequence,
            property: r.u32()?,
            value: r.u32()? != 0,
        })
    }
}

/// One true/false property changed on a visible object
/// ([`Opcode::PublicUpdatePropertyBool`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PublicUpdatePropertyBool {
    /// Quality sequence.
    pub sequence: u8,
    /// The object.
    pub guid: u32,
    /// Bool property id.
    pub property: u32,
    /// New value (non-zero word is true).
    pub value: bool,
}

impl PublicUpdatePropertyBool {
    /// Parse the body: opcode, sequence, object, property, value word.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PublicUpdatePropertyBool)?;
        Ok(Self {
            sequence,
            guid: r.u32()?,
            property: r.u32()?,
            value: r.u32()? != 0,
        })
    }
}

/// One integer property changed on the player
/// ([`Opcode::PrivateUpdatePropertyInt`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PrivateUpdatePropertyInt {
    /// Quality sequence.
    pub sequence: u8,
    /// Integer property id.
    pub property: u32,
    /// New value.
    pub value: i32,
}

impl PrivateUpdatePropertyInt {
    /// Parse the body: opcode, sequence, property, value.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdatePropertyInt)?;
        Ok(Self {
            sequence,
            property: r.u32()?,
            value: r.i32()?,
        })
    }
}

/// One 64-bit integer property changed on the player, such as total or
/// available experience ([`Opcode::PrivateUpdatePropertyInt64`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PrivateUpdatePropertyInt64 {
    /// Quality sequence.
    pub sequence: u8,
    /// 64-bit property id.
    pub property: u32,
    /// New value, signed.
    pub value: i64,
}

impl PrivateUpdatePropertyInt64 {
    /// Parse the body: opcode, sequence, property, value.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdatePropertyInt64)?;
        Ok(Self {
            sequence,
            property: r.u32()?,
            value: r.i64()?,
        })
    }
}

/// One data-file id property (an icon, a palette, ...) changed on a visible
/// object ([`Opcode::PublicUpdatePropertyDataId`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PublicUpdatePropertyDataId {
    /// Quality sequence.
    pub sequence: u8,
    /// The object.
    pub guid: u32,
    /// Data-id property id.
    pub property: u32,
    /// New data-file id.
    pub value: u32,
}

impl PublicUpdatePropertyDataId {
    /// Parse the body: opcode, sequence, object, property, value.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PublicUpdatePropertyDataId)?;
        Ok(Self {
            sequence,
            guid: r.u32()?,
            property: r.u32()?,
            value: r.u32()?,
        })
    }
}

/// One data-file id property changed on the player
/// ([`Opcode::PrivateUpdatePropertyDataId`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PrivateUpdatePropertyDataId {
    /// Quality sequence.
    pub sequence: u8,
    /// Data-id property id.
    pub property: u32,
    /// New data-file id.
    pub value: u32,
}

impl PrivateUpdatePropertyDataId {
    /// Parse the body: opcode, sequence, property, value.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdatePropertyDataId)?;
        Ok(Self {
            sequence,
            property: r.u32()?,
            value: r.u32()?,
        })
    }
}

/// Read the string that ends a string update: padding to a 4-byte boundary
/// (measured from the opcode), then the u16-length Windows-1252 string;
/// padding after the text is not required.
fn read_update_string(r: &mut Reader<'_>) -> Result<String> {
    r.align(4)?;
    read_string(r)
}

/// One string property changed on a visible object
/// ([`Opcode::PublicUpdatePropertyString`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PublicUpdatePropertyString {
    /// Quality sequence.
    pub sequence: u8,
    /// The object.
    pub guid: u32,
    /// String property id.
    pub property: u32,
    /// New value.
    pub value: String,
}

impl PublicUpdatePropertyString {
    /// Parse the body: opcode, sequence, object, property, padding to 4,
    /// string. The object comes first, as the protocol defines it; a server
    /// that writes the property first is not understood.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PublicUpdatePropertyString)?;
        let guid = r.u32()?;
        let property = r.u32()?;
        Ok(Self {
            sequence,
            guid,
            property,
            value: read_update_string(&mut r)?,
        })
    }
}

/// One string property changed on the player
/// ([`Opcode::PrivateUpdatePropertyString`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PrivateUpdatePropertyString {
    /// Quality sequence.
    pub sequence: u8,
    /// String property id.
    pub property: u32,
    /// New value.
    pub value: String,
}

impl PrivateUpdatePropertyString {
    /// Parse the body: opcode, sequence, property, padding to 4, string.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdatePropertyString)?;
        let property = r.u32()?;
        Ok(Self {
            sequence,
            property,
            value: read_update_string(&mut r)?,
        })
    }
}

/// One object-id property (a container, a wielder, ...) changed on a
/// visible object ([`Opcode::PublicUpdatePropertyInstanceId`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PublicUpdatePropertyInstanceId {
    /// Quality sequence.
    pub sequence: u8,
    /// The object.
    pub guid: u32,
    /// Instance-id property id.
    pub property: u32,
    /// New object id.
    pub value: u32,
}

impl PublicUpdatePropertyInstanceId {
    /// Parse the body: opcode, sequence, object, property, value.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PublicUpdatePropertyInstanceId)?;
        Ok(Self {
            sequence,
            guid: r.u32()?,
            property: r.u32()?,
            value: r.u32()?,
        })
    }
}

/// One object-id property changed on the player
/// ([`Opcode::PrivateUpdatePropertyInstanceId`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PrivateUpdatePropertyInstanceId {
    /// Quality sequence.
    pub sequence: u8,
    /// Instance-id property id.
    pub property: u32,
    /// New object id.
    pub value: u32,
}

impl PrivateUpdatePropertyInstanceId {
    /// Parse the body: opcode, sequence, property, value.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdatePropertyInstanceId)?;
        Ok(Self {
            sequence,
            property: r.u32()?,
            value: r.u32()?,
        })
    }
}

/// One saved position slot changed on the player: the slot the server
/// rewrites when the character dies, recalls or ties to a lifestone
/// ([`Opcode::PrivateUpdatePosition`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PrivateUpdatePosition {
    /// Quality sequence.
    pub sequence: u8,
    /// Which saved-position slot.
    pub position_type: u32,
    /// The new position.
    pub position: Position,
}

impl PrivateUpdatePosition {
    /// Parse the body: opcode, sequence, slot, then the full position.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdatePosition)?;
        Ok(Self {
            sequence,
            position_type: r.u32()?,
            position: Position::read(&mut r)?,
        })
    }
}

/// A vital (health, stamina, mana) fully restated: its raised ranks,
/// starting value, experience spent and current value
/// ([`Opcode::PrivateUpdateVital`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PrivateUpdateVital {
    /// Quality sequence.
    pub sequence: u8,
    /// Vital id.
    pub vital: u32,
    /// Ranks raised.
    pub ranks: u32,
    /// Starting value.
    pub start: u32,
    /// Experience spent.
    pub xp: u32,
    /// Current value.
    pub current: u32,
}

impl PrivateUpdateVital {
    /// Parse the body: opcode, sequence, vital, ranks, start, xp, current.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdateVital)?;
        Ok(Self {
            sequence,
            vital: r.u32()?,
            ranks: r.u32()?,
            start: r.u32()?,
            xp: r.u32()?,
            current: r.u32()?,
        })
    }
}

/// Only a vital's current value changed: regeneration or drain
/// ([`Opcode::PrivateUpdateVitalCurrent`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PrivateUpdateVitalCurrent {
    /// Quality sequence.
    pub sequence: u8,
    /// Vital id.
    pub vital: u32,
    /// Current value.
    pub current: u32,
}

impl PrivateUpdateVitalCurrent {
    /// Parse the body: opcode, sequence, vital, current.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdateVitalCurrent)?;
        Ok(Self {
            sequence,
            vital: r.u32()?,
            current: r.u32()?,
        })
    }
}

/// A primary attribute (strength .. self) restated after a raise or any
/// other change ([`Opcode::PrivateUpdateAttribute`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PrivateUpdateAttribute {
    /// Quality sequence.
    pub sequence: u8,
    /// Attribute id.
    pub attribute: u32,
    /// Ranks raised.
    pub ranks: u32,
    /// Starting value.
    pub start: u32,
    /// Experience spent.
    pub xp: u32,
}

impl PrivateUpdateAttribute {
    /// Parse the body: opcode, sequence, attribute, ranks, start, xp.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdateAttribute)?;
        Ok(Self {
            sequence,
            attribute: r.u32()?,
            ranks: r.u32()?,
            start: r.u32()?,
            xp: r.u32()?,
        })
    }
}

/// A skill restated after a raise, train, specialize, untrain or reset
/// ([`Opcode::PrivateUpdateSkill`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PrivateUpdateSkill {
    /// Quality sequence.
    pub sequence: u8,
    /// Skill id.
    pub skill: u32,
    /// Ranks raised; a `u16` on the wire.
    pub ranks: u16,
    /// Rank adjustment word; the server always sends 1.
    pub adjust_pp: u16,
    /// Untrained, trained or specialized.
    pub advancement_class: u32,
    /// Experience spent.
    pub xp: u32,
    /// Initial level.
    pub init: u32,
    /// Resistance at the last check.
    pub resistance: u32,
    /// When the skill was last used, game time in seconds.
    pub last_used: f64,
}

impl PrivateUpdateSkill {
    /// Parse the body: opcode, sequence, skill, ranks, adjustment, class,
    /// xp, init, resistance, last-used time.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let (mut r, sequence) = read_header(body, Opcode::PrivateUpdateSkill)?;
        Ok(Self {
            sequence,
            skill: r.u32()?,
            ranks: r.u16()?,
            adjust_pp: r.u16()?,
            advancement_class: r.u32()?,
            xp: r.u32()?,
            init: r.u32()?,
            resistance: r.u32()?,
            last_used: r.f64()?,
        })
    }
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/PublicUpdatePropertyIntTests.cs`,
    //! `PrivateUpdatePropertyIntTests.cs`, `PrivateUpdatePropertyInt64Tests.cs`,
    //! `UpdatePropertyDataIdTests.cs`, `UpdatePropertyStringTests.cs`, and
    //! `tests/AcDream.Core.Net.Tests/PrivateUpdatePositionTests.cs`,
    //! `PrivateUpdateVitalTests.cs`, `PrivateUpdateAttributeSkillTests.cs`
    //! (the parser parts; applying updates belongs to the world model).

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::assert_every_prefix_fails;
    use crate::{ProtoError, Quaternion, Vector3};

    fn header(opcode: u32, sequence: u8) -> Writer {
        let mut w = Writer::new();
        w.u32(opcode);
        w.u8(sequence);
        w
    }

    fn public_u32(opcode: u32, guid: u32, property: u32, value: u32, sequence: u8) -> Vec<u8> {
        let mut w = header(opcode, sequence);
        w.u32(guid);
        w.u32(property);
        w.u32(value);
        w.into_vec()
    }

    fn private_u32(opcode: u32, property: u32, value: u32, sequence: u8) -> Vec<u8> {
        let mut w = header(opcode, sequence);
        w.u32(property);
        w.u32(value);
        w.into_vec()
    }

    #[test]
    fn bool_updates_read_their_word_as_true_when_non_zero() {
        // Private: property at +5, value word at +9; public: object at +5,
        // property at +9, value word at +0x0D.
        let private = private_u32(0x02D1, 0x5D, 0, 3);
        assert_eq!(
            PrivateUpdatePropertyBool::parse(&private).unwrap(),
            PrivateUpdatePropertyBool {
                sequence: 3,
                property: 0x5D,
                value: false,
            }
        );
        let public = public_u32(0x02D2, 0x5000_000B, 0x5D, 2, 4);
        assert_eq!(
            PublicUpdatePropertyBool::parse(&public).unwrap(),
            PublicUpdatePropertyBool {
                sequence: 4,
                guid: 0x5000_000B,
                property: 0x5D,
                value: true,
            }
        );
        assert_every_prefix_fails(&private, PrivateUpdatePropertyBool::parse);
        assert_every_prefix_fails(&public, PublicUpdatePropertyBool::parse);
        wrong_opcode(PrivateUpdatePropertyBool::parse(&public));
    }

    fn wrong_opcode<T: core::fmt::Debug>(result: Result<T>) {
        assert!(
            matches!(result, Err(ProtoError::WrongOpcode { .. })),
            "{result:?}"
        );
    }

    // --- public int -------------------------------------------------------

    #[test]
    fn public_int_golden_bytes() {
        // UiEffects (18) = 9 on object 0x50000001, sequence 1.
        let bytes: &[u8] = &[
            0xCE, 0x02, 0x00, 0x00, // opcode 0x02CE
            0x01, // sequence
            0x01, 0x00, 0x00, 0x50, // guid
            0x12, 0x00, 0x00, 0x00, // property 18
            0x09, 0x00, 0x00, 0x00, // value 9
        ];
        assert_eq!(public_u32(0x02CE, 0x5000_0001, 18, 9, 1), bytes);
        assert_eq!(
            PublicUpdatePropertyInt::parse(bytes),
            Ok(PublicUpdatePropertyInt {
                sequence: 1,
                guid: 0x5000_0001,
                property: 18,
                value: 9
            })
        );
    }

    #[test]
    fn public_int_value_is_signed() {
        let p =
            PublicUpdatePropertyInt::parse(&public_u32(0x02CE, 1, 18, (-7i32).cast_unsigned(), 1))
                .unwrap();
        assert_eq!(p.value, -7);
    }

    #[test]
    fn public_int_rejects_wrong_opcode_and_truncation() {
        wrong_opcode(PublicUpdatePropertyInt::parse(&public_u32(
            0x02CD, 1, 18, 1, 1,
        )));
        assert_every_prefix_fails(
            &public_u32(0x02CE, 1, 18, 1, 1),
            PublicUpdatePropertyInt::parse,
        );
    }

    // --- private int ------------------------------------------------------

    #[test]
    fn private_int_reads_property_and_value_without_guid() {
        // EncumbranceVal (5) = 1500.
        let bytes: &[u8] = &[
            0xCD, 0x02, 0x00, 0x00, 0x01, 0x05, 0x00, 0x00, 0x00, 0xDC, 0x05, 0x00, 0x00,
        ];
        assert_eq!(private_u32(0x02CD, 5, 1500, 1), bytes);
        assert_eq!(
            PrivateUpdatePropertyInt::parse(bytes),
            Ok(PrivateUpdatePropertyInt {
                sequence: 1,
                property: 5,
                value: 1500
            })
        );
    }

    #[test]
    fn private_int_rejects_wrong_opcode_and_truncation() {
        wrong_opcode(PrivateUpdatePropertyInt::parse(&private_u32(
            0x02CE, 5, 1, 1,
        )));
        assert_every_prefix_fails(
            &private_u32(0x02CD, 5, 1, 1),
            PrivateUpdatePropertyInt::parse,
        );
    }

    // --- private int64 ----------------------------------------------------

    fn int64_body(opcode: u32, property: u32, value: i64, sequence: u8) -> Vec<u8> {
        let mut w = header(opcode, sequence);
        w.u32(property);
        w.i64(value);
        w.into_vec()
    }

    #[test]
    fn private_int64_returns_signed_values() {
        for (property, value) in [(1u32, 1_234_567_890i64), (2, 987_654_321), (6, -1)] {
            let p = PrivateUpdatePropertyInt64::parse(&int64_body(0x02CF, property, value, 0x7B))
                .unwrap();
            assert_eq!(p.sequence, 0x7B);
            assert_eq!(p.property, property);
            assert_eq!(p.value, value);
        }
    }

    #[test]
    fn private_int64_golden_bytes() {
        let bytes: &[u8] = &[
            0xCF, 0x02, 0x00, 0x00, 0x7B, // opcode, sequence
            0x01, 0x00, 0x00, 0x00, // property 1
            0xD2, 0x02, 0x96, 0x49, 0x00, 0x00, 0x00, 0x00, // 1_234_567_890
        ];
        assert_eq!(int64_body(0x02CF, 1, 1_234_567_890, 0x7B), bytes);
        assert_eq!(
            PrivateUpdatePropertyInt64::parse(bytes).unwrap().value,
            1_234_567_890
        );
    }

    #[test]
    fn private_int64_rejects_wrong_opcode_and_truncation() {
        wrong_opcode(PrivateUpdatePropertyInt64::parse(&int64_body(
            0x02D0, 1, 100, 1,
        )));
        assert_every_prefix_fails(
            &int64_body(0x02CF, 1, 100, 1),
            PrivateUpdatePropertyInt64::parse,
        );
    }

    // --- string -----------------------------------------------------------
    // OpenAC `tests/AcDream.Core.Net.Tests/Messages/UpdatePropertyStringTests.cs`.

    fn text(s: &str) -> Vec<u8> {
        let mut v = vec![s.len() as u8, 0];
        v.extend_from_slice(s.as_bytes());
        v
    }

    // Opcode, sequence, object 0x5000002A, property 1, three bytes of
    // padding, then an 18-byte name (2 + 18 = 20, so no padding after it).
    fn public_string_sample() -> Vec<u8> {
        let mut v = vec![0xD6, 0x02, 0, 0, 7, 0x2A, 0, 0, 0x50, 1, 0, 0, 0, 0, 0, 0];
        v.extend(text("RabidDee's Cottage"));
        v
    }

    // Opcode, sequence, property 1, three bytes of padding, the name.
    fn private_string_sample() -> Vec<u8> {
        let mut v = vec![0xD5, 0x02, 0, 0, 7, 1, 0, 0, 0, 0, 0, 0];
        v.extend(text("RabidDee's Cottage"));
        v
    }

    #[test]
    fn public_string_reads_the_object_before_the_property() {
        assert_eq!(
            PublicUpdatePropertyString::parse(&public_string_sample()),
            Ok(PublicUpdatePropertyString {
                sequence: 7,
                guid: 0x5000_002A,
                property: 1,
                value: "RabidDee's Cottage".into(),
            })
        );
    }

    #[test]
    fn private_string_reads_property_and_value() {
        assert_eq!(
            PrivateUpdatePropertyString::parse(&private_string_sample()),
            Ok(PrivateUpdatePropertyString {
                sequence: 7,
                property: 1,
                value: "RabidDee's Cottage".into(),
            })
        );
    }

    #[test]
    fn public_string_may_be_empty() {
        let body = [
            0xD6, 2, 0, 0, 7, 0x2A, 0, 0, 0x50, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        ];
        assert_eq!(
            PublicUpdatePropertyString::parse(&body),
            Ok(PublicUpdatePropertyString {
                sequence: 7,
                guid: 0x5000_002A,
                property: 5,
                value: String::new(),
            })
        );
    }

    #[test]
    fn string_updates_reject_truncation_and_the_wrong_opcode() {
        let public = public_string_sample();
        let private = private_string_sample();
        assert_every_prefix_fails(&public, PublicUpdatePropertyString::parse);
        assert_every_prefix_fails(&private, PrivateUpdatePropertyString::parse);
        wrong_opcode(PublicUpdatePropertyString::parse(&private));
        wrong_opcode(PrivateUpdatePropertyString::parse(&public));
    }

    // --- data id and instance id -------------------------------------------

    #[test]
    fn public_data_id_reads_guid_property_and_value() {
        let body = public_u32(0x02D8, 0x5000_0001, 52, 0x0600_1234, 1);
        assert_eq!(
            PublicUpdatePropertyDataId::parse(&body),
            Ok(PublicUpdatePropertyDataId {
                sequence: 1,
                guid: 0x5000_0001,
                property: 52,
                value: 0x0600_1234
            })
        );
        wrong_opcode(PublicUpdatePropertyDataId::parse(&public_u32(
            0x02D7, 1, 52, 2, 1,
        )));
        assert_every_prefix_fails(&body, PublicUpdatePropertyDataId::parse);
    }

    #[test]
    fn private_data_id_reads_property_and_value() {
        let body = private_u32(0x02D7, 8, 0x0600_5678, 1);
        assert_eq!(
            PrivateUpdatePropertyDataId::parse(&body),
            Ok(PrivateUpdatePropertyDataId {
                sequence: 1,
                property: 8,
                value: 0x0600_5678
            })
        );
        wrong_opcode(PrivateUpdatePropertyDataId::parse(&private_u32(
            0x02D8, 8, 1, 1,
        )));
        assert_every_prefix_fails(&body, PrivateUpdatePropertyDataId::parse);
    }

    #[test]
    fn public_instance_id_reads_guid_property_and_value() {
        let body = public_u32(0x02DA, 0x5000_0009, 3, 0x5000_0001, 1);
        assert_eq!(
            PublicUpdatePropertyInstanceId::parse(&body),
            Ok(PublicUpdatePropertyInstanceId {
                sequence: 1,
                guid: 0x5000_0009,
                property: 3,
                value: 0x5000_0001
            })
        );
        wrong_opcode(PublicUpdatePropertyInstanceId::parse(&public_u32(
            0x02D8, 1, 3, 2, 1,
        )));
        assert_every_prefix_fails(&body, PublicUpdatePropertyInstanceId::parse);
    }

    #[test]
    fn private_instance_id_reads_property_and_value() {
        let body = private_u32(0x02D9, 2, 0x5000_0002, 1);
        assert_eq!(
            PrivateUpdatePropertyInstanceId::parse(&body),
            Ok(PrivateUpdatePropertyInstanceId {
                sequence: 1,
                property: 2,
                value: 0x5000_0002
            })
        );
        wrong_opcode(PrivateUpdatePropertyInstanceId::parse(&private_u32(
            0x02DA, 2, 1, 1,
        )));
        assert_every_prefix_fails(&body, PrivateUpdatePropertyInstanceId::parse);
    }

    // --- private position ------------------------------------------------

    const LAST_OUTSIDE_DEATH_SLOT: u32 = 0x0E;

    fn position_body(sequence: u8, slot: u32, cell: u32, floats: [f32; 7]) -> Vec<u8> {
        let mut w = header(0x02DB, sequence);
        w.u32(slot);
        w.u32(cell);
        for f in floats {
            w.f32(f);
        }
        w.into_vec()
    }

    #[test]
    fn private_position_reads_sequence_slot_and_position() {
        let body = position_body(
            7,
            LAST_OUTSIDE_DEATH_SLOT,
            0xC6A9_001F,
            [12.5, -34.25, 6.125, 0.5, 1.5, 2.5, 3.5],
        );
        assert_eq!(body.len(), 41);
        assert_eq!(
            PrivateUpdatePosition::parse(&body),
            Ok(PrivateUpdatePosition {
                sequence: 7,
                position_type: LAST_OUTSIDE_DEATH_SLOT,
                position: Position {
                    cell_id: 0xC6A9_001F,
                    origin: Vector3 {
                        x: 12.5,
                        y: -34.25,
                        z: 6.125
                    },
                    // The wire writes the real part first.
                    orientation: Quaternion {
                        w: 0.5,
                        x: 1.5,
                        y: 2.5,
                        z: 3.5
                    },
                },
            })
        );
    }

    #[test]
    fn private_position_rejects_foreign_opcode_and_truncation() {
        let body = position_body(1, LAST_OUTSIDE_DEATH_SLOT, 0xC6A9_001F, [0.0; 7]);
        let mut foreign = body.clone();
        foreign[..4].copy_from_slice(&0x02DCu32.to_le_bytes());
        wrong_opcode(PrivateUpdatePosition::parse(&foreign));
        assert_every_prefix_fails(&body, PrivateUpdatePosition::parse);
    }

    // --- vitals -------------------------------------------------------------

    fn vital_body(
        sequence: u8,
        vital: u32,
        ranks: u32,
        start: u32,
        xp: u32,
        current: u32,
    ) -> Vec<u8> {
        let mut w = header(0x02E7, sequence);
        for v in [vital, ranks, start, xp, current] {
            w.u32(v);
        }
        w.into_vec()
    }

    fn vital_current_body(sequence: u8, vital: u32, current: u32) -> Vec<u8> {
        private_u32(0x02E9, vital, current, sequence)
    }

    #[test]
    fn vital_full_round_trip() {
        let body = vital_body(12, 2, 100, 12345, 67890, 100);
        assert_eq!(body.len(), 25);
        assert_eq!(
            PrivateUpdateVital::parse(&body),
            Ok(PrivateUpdateVital {
                sequence: 12,
                vital: 2,
                ranks: 100,
                start: 12345,
                xp: 67890,
                current: 100
            })
        );
    }

    #[test]
    fn vital_current_round_trip() {
        let bytes: &[u8] = &[
            0xE9, 0x02, 0x00, 0x00, 0x0C, 0x02, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00,
        ];
        assert_eq!(vital_current_body(12, 2, 100), bytes);
        assert_eq!(
            PrivateUpdateVitalCurrent::parse(bytes),
            Ok(PrivateUpdateVitalCurrent {
                sequence: 12,
                vital: 2,
                current: 100
            })
        );
    }

    #[test]
    fn vital_full_parses_stamina_and_mana_ids() {
        let stamina = PrivateUpdateVital::parse(&vital_body(7, 4, 50, 100, 0, 75)).unwrap();
        assert_eq!((stamina.vital, stamina.current), (4, 75));
        let mana = PrivateUpdateVital::parse(&vital_body(8, 6, 30, 80, 0, 110)).unwrap();
        assert_eq!((mana.vital, mana.current), (6, 110));
    }

    #[test]
    fn vitals_reject_wrong_opcode_and_truncation() {
        let mut full = vital_body(12, 2, 100, 12345, 67890, 100);
        let mut current = vital_current_body(12, 2, 100);
        assert_every_prefix_fails(&full, PrivateUpdateVital::parse);
        assert_every_prefix_fails(&current, PrivateUpdateVitalCurrent::parse);
        // Each is the other's wrong opcode, as is anything else.
        wrong_opcode(PrivateUpdateVital::parse(&current));
        wrong_opcode(PrivateUpdateVitalCurrent::parse(&full));
        full[..4].copy_from_slice(&0xDEAD_BEEFu32.to_le_bytes());
        current[..4].copy_from_slice(&0xDEAD_BEEFu32.to_le_bytes());
        wrong_opcode(PrivateUpdateVital::parse(&full));
        wrong_opcode(PrivateUpdateVitalCurrent::parse(&current));
    }

    // --- attribute and skill --------------------------------------------------

    fn attribute_body(sequence: u8, attribute: u32, ranks: u32, start: u32, xp: u32) -> Vec<u8> {
        let mut w = header(0x02E3, sequence);
        for v in [attribute, ranks, start, xp] {
            w.u32(v);
        }
        w.into_vec()
    }

    #[allow(clippy::too_many_arguments)]
    fn skill_body(
        sequence: u8,
        skill: u32,
        ranks: u16,
        adjust_pp: u16,
        class: u32,
        xp: u32,
        init: u32,
        resistance: u32,
        last_used: f64,
    ) -> Vec<u8> {
        let mut w = header(0x02DD, sequence);
        w.u32(skill);
        w.u16(ranks);
        w.u16(adjust_pp);
        for v in [class, xp, init, resistance] {
            w.u32(v);
        }
        w.f64(last_used);
        w.into_vec()
    }

    #[test]
    fn attribute_round_trip() {
        // A Quickness (3) raise: 41 ranks over a 100 start, 1,010,895 xp.
        let body = attribute_body(7, 3, 41, 100, 1_010_895);
        assert_eq!(body.len(), 21);
        assert_eq!(
            PrivateUpdateAttribute::parse(&body),
            Ok(PrivateUpdateAttribute {
                sequence: 7,
                attribute: 3,
                ranks: 41,
                start: 100,
                xp: 1_010_895
            })
        );
    }

    #[test]
    fn attribute_rejects_wrong_opcode_and_truncation() {
        let good = attribute_body(1, 1, 1, 10, 100);
        let mut bad = good.clone();
        bad[..4].copy_from_slice(&0x02E7u32.to_le_bytes());
        wrong_opcode(PrivateUpdateAttribute::parse(&bad));
        assert_every_prefix_fails(&good, PrivateUpdateAttribute::parse);
    }

    #[test]
    fn skill_round_trip_preserves_all_fields() {
        let body = skill_body(12, 6, 50, 1, 3, 1000, 10, 0, 0.0);
        assert_eq!(body.len(), 37);
        assert_eq!(
            PrivateUpdateSkill::parse(&body),
            Ok(PrivateUpdateSkill {
                sequence: 12,
                skill: 6,
                ranks: 50,
                adjust_pp: 1,
                advancement_class: 3,
                xp: 1000,
                init: 10,
                resistance: 0,
                last_used: 0.0
            })
        );
    }

    #[test]
    fn skill_last_used_survives_as_double_bits() {
        let body = skill_body(1, 14, 3, 1, 2, 42, 0, 5, 12345.678);
        assert_eq!(
            PrivateUpdateSkill::parse(&body)
                .unwrap()
                .last_used
                .to_bits(),
            12345.678f64.to_bits()
        );
    }

    #[test]
    fn skill_rejects_wrong_opcode_and_truncation() {
        let good = skill_body(1, 6, 1, 1, 2, 0, 0, 0, 0.0);
        let mut bad = good.clone();
        bad[..4].copy_from_slice(&0x02DFu32.to_le_bytes());
        wrong_opcode(PrivateUpdateSkill::parse(&bad));
        assert_every_prefix_fails(&good, PrivateUpdateSkill::parse);
    }

    #[test]
    fn trailing_bytes_are_ignored() {
        let mut body = private_u32(0x02CD, 5, 1500, 1);
        body.extend_from_slice(&[0xAA; 3]);
        assert_eq!(PrivateUpdatePropertyInt::parse(&body).unwrap().value, 1500);
    }
}
