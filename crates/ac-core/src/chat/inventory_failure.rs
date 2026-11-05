//! The line shown when the server refuses an inventory request:
//! `"The Sword can't be picked up - you are too encumbered"`.

use crate::items::InventoryRequestKind;

/// The refusal line for a request of `kind` on `item_name`, with a reason
/// suffix for the failure codes that have one. `None` for a request kind
/// that has no line (a shop purchase).
#[must_use]
pub fn compose(kind: InventoryRequestKind, item_name: &str, weenie_error: u32) -> Option<String> {
    let verb = match kind {
        InventoryRequestKind::Merge => "merged",
        InventoryRequestKind::SplitToContainer | InventoryRequestKind::SplitToWorld => "split",
        InventoryRequestKind::Move => "moved",
        InventoryRequestKind::Pickup => "picked up",
        InventoryRequestKind::PutInContainer => "put in the container",
        InventoryRequestKind::DropToWorld => "dropped",
        InventoryRequestKind::Wield => "wielded",
        InventoryRequestKind::Give => "given",
        InventoryRequestKind::Shop => return None,
    };
    Some(format!(
        "The {item_name} can't be {verb}{}",
        suffix(weenie_error)
    ))
}

fn suffix(weenie_error: u32) -> &'static str {
    match weenie_error {
        0x1D => " - you're too busy",
        0x20 => " - you must control both objects",
        0x28 => " - the item is under someone else's control",
        0x2A => " - you are too encumbered",
        0x36 => " - action cancelled",
        0x37..=0x39 => " - unable to move to object",
        0x3EE => " - the container is closed",
        _ => "",
    }
}

/// True for the failure codes that carry their own message, so the generic
/// refusal line is not shown for them.
#[must_use]
pub fn suppresses_generic_failure_text(weenie_error: u32) -> bool {
    matches!(
        weenie_error,
        0x1E | 0x2B | 0x3EF | 0x43E | 0x4CE | 0x4CF | 0x46A
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/InventoryFailureMessagesTests.cs
    #[test]
    fn compose_matches_the_refusal_lines() {
        use InventoryRequestKind as K;
        let cases = [
            (
                K::DropToWorld,
                "Bloodstone Chunk",
                0x426,
                "The Bloodstone Chunk can't be dropped",
            ),
            (K::Give, "Sword", 0, "The Sword can't be given"),
            (
                K::Pickup,
                "Sword",
                0x2A,
                "The Sword can't be picked up - you are too encumbered",
            ),
            (
                K::PutInContainer,
                "Sword",
                0x3EE,
                "The Sword can't be put in the container - the container is closed",
            ),
            (
                K::Merge,
                "Arrows",
                0x1D,
                "The Arrows can't be merged - you're too busy",
            ),
            (
                K::SplitToWorld,
                "Arrows",
                0x38,
                "The Arrows can't be split - unable to move to object",
            ),
            (
                K::SplitToContainer,
                "Arrows",
                0x36,
                "The Arrows can't be split - action cancelled",
            ),
            (K::Move, "Sword", 0, "The Sword can't be moved"),
            (
                K::Wield,
                "Sword",
                0x1D,
                "The Sword can't be wielded - you're too busy",
            ),
        ];
        for (kind, name, error, expected) in cases {
            assert_eq!(compose(kind, name, error).as_deref(), Some(expected));
        }
        assert_eq!(compose(K::Shop, "Sword", 0), None);
    }

    #[test]
    fn exclusion_set_suppresses_generic_failure_text() {
        for error in [0x1E, 0x2B, 0x3EF, 0x43E, 0x4CE, 0x4CF, 0x46A] {
            assert!(suppresses_generic_failure_text(error), "{error:#X}");
        }
    }

    #[test]
    fn other_errors_do_not_suppress_generic_failure_text() {
        for error in [0x426, 0x1D, 0] {
            assert!(!suppresses_generic_failure_text(error), "{error:#X}");
        }
    }
}
