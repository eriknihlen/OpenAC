//! The per-object quality counters: every single-quality update carries a
//! one-byte sequence for its property, and an object takes an update only
//! when that sequence is not older than the last one it took for the same
//! property. A packet that arrives late (a resend after loss) cannot put an
//! older value over a newer one.
//!
//! One counter per kind and property id, per object. The first update of a
//! property is always taken. The counters live as long as the object's
//! record: a record created again (a new incarnation, a re-sent
//! description) starts with none.

use std::collections::HashMap;

/// The kind of quality a counter belongs to. Its tag, combined with the
/// property id, names the counter, so an int property and a float property
/// with the same id count separately, while a stack-size answer shares the
/// int stack-size property's counter.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum QualityKind {
    /// 32-bit integer properties (and the stack size, int property 12).
    Int,
    /// Decimal properties.
    Float,
    /// Saved positions.
    Position,
    /// Skills.
    Skill,
    /// Text properties.
    String,
    /// Dat-id properties.
    DataId,
    /// Object-id properties.
    InstanceId,
    /// Primary attributes.
    Attribute,
    /// Vitals (health, stamina, mana), whole or current value alone.
    Vital,
    /// True/false properties.
    Bool,
    /// 64-bit integer properties.
    Int64,
}

impl QualityKind {
    /// The tag the game combines with the property id to name a counter.
    #[must_use]
    pub const fn tag(self) -> u32 {
        match self {
            Self::Int => 0x1_0000,
            Self::Float => 0x2_0000,
            Self::Position => 0x3_0000,
            Self::Skill => 0x4_0000,
            Self::String => 0x5_0000,
            Self::DataId => 0x6_0000,
            Self::InstanceId => 0x7_0000,
            Self::Attribute => 0x8_0000,
            Self::Vital => 0x9_0000,
            Self::Bool => 0xD_0000,
            Self::Int64 => 0xE_0000,
        }
    }
}

/// The int property a stack-size answer counts against (its stack size).
pub const STACK_SIZE_PROPERTY: u32 = 12;

/// Whether an update with sequence `incoming` may replace the one taken at
/// `held`. The counters wrap: within half the range (127 apart) a sequence
/// not below the held one is taken; further apart, only a smaller one (it
/// has wrapped past 255). An equal sequence is taken.
#[must_use]
pub fn quality_sequence_accepts(held: u8, incoming: u8) -> bool {
    let distance = (i32::from(incoming) - i32::from(held)).abs();
    if distance > 0x7F {
        held >= incoming
    } else {
        incoming >= held
    }
}

/// One object's quality counters.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct QualityStamps {
    held: HashMap<u32, u8>,
    rejected: u64,
}

impl QualityStamps {
    /// No counters yet.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// An update of `property` of `kind` with `sequence` arrived: whether to
    /// take it. A taken sequence becomes the held one; a refused one changes
    /// nothing but the refusal count.
    pub fn update(&mut self, kind: QualityKind, property: u32, sequence: u8) -> bool {
        let key = property | kind.tag();
        match self.held.get_mut(&key) {
            None => {
                self.held.insert(key, sequence);
                true
            }
            Some(held) if quality_sequence_accepts(*held, sequence) => {
                *held = sequence;
                true
            }
            Some(_) => {
                self.rejected += 1;
                false
            }
        }
    }

    /// How many updates were refused as older than the held one.
    #[must_use]
    pub fn rejected(&self) -> u64 {
        self.rejected
    }
}

#[cfg(test)]
mod tests {
    //! The rule is the game's per-property update counter: the first update
    //! is taken, then only one not older within the 127-wide wrap window.

    use super::*;

    #[test]
    fn the_first_update_of_a_property_is_always_taken() {
        let mut stamps = QualityStamps::new();
        assert!(stamps.update(QualityKind::Int, 25, 200));
        // Another property, or the same id of another kind, has its own.
        assert!(stamps.update(QualityKind::Int, 26, 3));
        assert!(stamps.update(QualityKind::Float, 25, 3));
        assert_eq!(stamps.rejected(), 0);
    }

    #[test]
    fn an_older_sequence_is_refused_and_an_equal_or_newer_one_taken() {
        let mut stamps = QualityStamps::new();
        assert!(stamps.update(QualityKind::Vital, 2, 10));
        assert!(!stamps.update(QualityKind::Vital, 2, 9));
        assert_eq!(stamps.rejected(), 1);
        assert!(stamps.update(QualityKind::Vital, 2, 10));
        assert!(stamps.update(QualityKind::Vital, 2, 11));
        assert!(!stamps.update(QualityKind::Vital, 2, 10));
    }

    #[test]
    fn the_counters_wrap_past_255() {
        // 127 apart is still inside the window; 128 apart counts as wrapped.
        assert!(quality_sequence_accepts(0xF0, 0x05));
        assert!(!quality_sequence_accepts(0x05, 0xF0));
        assert!(quality_sequence_accepts(0x00, 0x7F));
        assert!(!quality_sequence_accepts(0x7F, 0x00));
        assert!(!quality_sequence_accepts(0x00, 0x80));
        assert!(quality_sequence_accepts(0x80, 0x00));
        assert!(quality_sequence_accepts(0xFF, 0xFF));
        let mut stamps = QualityStamps::new();
        assert!(stamps.update(QualityKind::Int, 12, 0xFE));
        assert!(stamps.update(QualityKind::Int, 12, 0x01));
        assert!(!stamps.update(QualityKind::Int, 12, 0xFF));
    }

    #[test]
    fn a_refused_update_keeps_the_held_sequence() {
        let mut stamps = QualityStamps::new();
        assert!(stamps.update(QualityKind::Bool, 1, 50));
        assert!(!stamps.update(QualityKind::Bool, 1, 40));
        // 51 is newer than the held 50 (not than the refused 40 alone).
        assert!(stamps.update(QualityKind::Bool, 1, 51));
        assert!(!stamps.update(QualityKind::Bool, 1, 50));
    }
}
