//! Retail UI state ids: the button state machine and the named custom
//! states the client switches between.

/// Inputs to the button state choice.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ButtonVisualInput {
    /// Ghosted (disabled).
    pub disabled: bool,
    /// Toggled on.
    pub selected: bool,
    /// Hover shows the rollover state.
    pub rollover_enabled: bool,
    /// The button is held down.
    pub pressed: bool,
    /// The pointer is over the button.
    pub pointer_over: bool,
}

/// Normal.
pub const NORMAL: u32 = 1;
/// Normal, hovered (or held with the pointer outside).
pub const NORMAL_ROLLOVER: u32 = 2;
/// Normal, held with the pointer over it.
pub const NORMAL_PRESSED: u32 = 3;
/// Selected.
pub const HIGHLIGHT: u32 = 6;
/// Selected, hovered.
pub const HIGHLIGHT_ROLLOVER: u32 = 7;
/// Selected, held.
pub const HIGHLIGHT_PRESSED: u32 = 8;
/// Disabled.
pub const GHOSTED: u32 = 13;

/// Closed (tabs, collapsible frames).
pub const CLOSED: u32 = 11;
/// Open.
pub const OPEN: u32 = 12;
/// Compact view.
pub const HIDE_DETAIL: u32 = 0x1000_0006;
/// Detailed view.
pub const SHOW_DETAIL: u32 = 0x1000_0007;
/// An object is selected.
pub const OBJECT_SELECTED: u32 = 0x1000_000B;
/// A stacked item is selected.
pub const STACKED_ITEM_SELECTED: u32 = 0x1000_000C;
/// An empty item slot.
pub const ITEM_SLOT_EMPTY: u32 = 0x1000_001C;
/// Maximized.
pub const MAXIMIZED: u32 = 0x1000_0047;
/// Minimized.
pub const MINIMIZED: u32 = 0x1000_0048;
/// UI locked.
pub const LOCKED_UI: u32 = 0x1000_0063;
/// UI unlocked.
pub const UNLOCKED_UI: u32 = 0x1000_0064;
/// Custom selection pair: off.
pub const UNSELECTED: u32 = 0x1000_0016;
/// Custom selection pair: on.
pub const SELECTED: u32 = 0x1000_0017;

/// The state a button asks for: ghosted when disabled; otherwise normal or
/// highlight (selected), rollover when held or hovered with rollover on,
/// pressed when held with the pointer over it.
#[must_use]
pub fn requested_button_state(input: ButtonVisualInput) -> u32 {
    if input.disabled {
        return GHOSTED;
    }
    let mut requested = if input.selected { HIGHLIGHT } else { NORMAL };
    if input.pressed || (input.rollover_enabled && input.pointer_over) {
        requested = if input.selected {
            HIGHLIGHT_ROLLOVER
        } else {
            NORMAL_ROLLOVER
        };
    }
    if input.pressed && input.pointer_over {
        requested = if input.selected {
            HIGHLIGHT_PRESSED
        } else {
            NORMAL_PRESSED
        };
    }
    requested
}

/// The requested state when the element has it, else the current state.
#[must_use]
pub fn resolve_button_state(current: u32, input: ButtonVisualInput, available: &[u32]) -> u32 {
    let requested = requested_button_state(input);
    if available.contains(&requested) {
        requested
    } else {
        current
    }
}

/// Name of a standard button state ("" for others).
#[must_use]
pub fn button_state_name(id: u32) -> &'static str {
    match id {
        NORMAL => "Normal",
        NORMAL_ROLLOVER => "Normal_rollover",
        NORMAL_PRESSED => "Normal_pressed",
        HIGHLIGHT => "Highlight",
        HIGHLIGHT_ROLLOVER => "Highlight_rollover",
        HIGHLIGHT_PRESSED => "Highlight_pressed",
        GHOSTED => "Ghosted",
        _ => "",
    }
}

/// Id of a standard button state name.
#[must_use]
pub fn button_state_id(name: &str) -> Option<u32> {
    Some(match name {
        "Normal" => NORMAL,
        "Normal_rollover" => NORMAL_ROLLOVER,
        "Normal_pressed" => NORMAL_PRESSED,
        "Highlight" => HIGHLIGHT,
        "Highlight_rollover" => HIGHLIGHT_ROLLOVER,
        "Highlight_pressed" => HIGHLIGHT_PRESSED,
        "Ghosted" => GHOSTED,
        _ => return None,
    })
}

/// Name of a named custom state ("" for others).
#[must_use]
pub fn custom_state_name(id: u32) -> &'static str {
    match id {
        CLOSED => "Closed",
        OPEN => "Open",
        HIDE_DETAIL => "HideDetail",
        SHOW_DETAIL => "ShowDetail",
        OBJECT_SELECTED => "ObjectSelected",
        STACKED_ITEM_SELECTED => "StackedItemSelected",
        ITEM_SLOT_EMPTY => "ItemSlot_Empty",
        MAXIMIZED => "Maximized",
        MINIMIZED => "Minimized",
        LOCKED_UI => "LockedUI",
        UNLOCKED_UI => "UnlockedUI",
        UNSELECTED => "Unselected",
        SELECTED => "Selected",
        _ => "",
    }
}

/// Id of a named custom state.
#[must_use]
pub fn custom_state_id(name: &str) -> Option<u32> {
    Some(match name {
        "Closed" => CLOSED,
        "Open" => OPEN,
        "HideDetail" => HIDE_DETAIL,
        "ShowDetail" => SHOW_DETAIL,
        "ObjectSelected" => OBJECT_SELECTED,
        "StackedItemSelected" => STACKED_ITEM_SELECTED,
        "ItemSlot_Empty" => ITEM_SLOT_EMPTY,
        "Maximized" => MAXIMIZED,
        "Minimized" => MINIMIZED,
        "LockedUI" => LOCKED_UI,
        "UnlockedUI" => UNLOCKED_UI,
        "Unselected" => UNSELECTED,
        "Selected" => SELECTED,
        _ => return None,
    })
}

/// The name a state id is drawn under: the button name, else the custom
/// name ("" for neither).
#[must_use]
pub fn standard_state_name(id: u32) -> &'static str {
    let name = button_state_name(id);
    if name.is_empty() {
        custom_state_name(id)
    } else {
        name
    }
}

/// The id of a standard (button or custom) state name; 0 for neither.
#[must_use]
pub fn standard_state_id(name: &str) -> u32 {
    button_state_id(name)
        .or_else(|| custom_state_id(name))
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiButtonStateMachineTests.cs`.

    use super::*;

    #[test]
    fn requested_states_follow_the_retail_table() {
        let cases = [
            ((false, false, false, false, false), NORMAL),
            ((false, false, true, false, true), NORMAL_ROLLOVER),
            ((false, false, true, true, false), NORMAL_ROLLOVER),
            ((false, false, true, true, true), NORMAL_PRESSED),
            ((false, true, false, false, false), HIGHLIGHT),
            ((false, true, true, false, true), HIGHLIGHT_ROLLOVER),
            ((false, true, true, true, false), HIGHLIGHT_ROLLOVER),
            ((false, true, true, true, true), HIGHLIGHT_PRESSED),
            ((true, false, true, true, true), GHOSTED),
            ((true, true, true, true, true), GHOSTED),
        ];
        for ((disabled, selected, rollover_enabled, pressed, pointer_over), expected) in cases {
            let input = ButtonVisualInput {
                disabled,
                selected,
                rollover_enabled,
                pressed,
                pointer_over,
            };
            assert_eq!(requested_button_state(input), expected, "{input:?}");
        }
    }

    #[test]
    fn a_missing_requested_state_keeps_the_current_one() {
        let input = ButtonVisualInput {
            rollover_enabled: true,
            pointer_over: true,
            ..ButtonVisualInput::default()
        };
        assert_eq!(
            resolve_button_state(0x1000_0053, input, &[0x1000_0053]),
            0x1000_0053
        );
        assert_eq!(
            resolve_button_state(42, input, &[NORMAL_ROLLOVER]),
            NORMAL_ROLLOVER
        );
    }

    #[test]
    fn names_and_ids_round_trip() {
        for id in [
            NORMAL,
            NORMAL_ROLLOVER,
            NORMAL_PRESSED,
            HIGHLIGHT,
            HIGHLIGHT_ROLLOVER,
            HIGHLIGHT_PRESSED,
            GHOSTED,
        ] {
            assert_eq!(button_state_id(button_state_name(id)), Some(id));
        }
        for id in [
            CLOSED,
            OPEN,
            HIDE_DETAIL,
            SHOW_DETAIL,
            ITEM_SLOT_EMPTY,
            LOCKED_UI,
            SELECTED,
        ] {
            assert_eq!(custom_state_id(custom_state_name(id)), Some(id));
        }
        assert_eq!(standard_state_name(99), "");
        assert_eq!(standard_state_id("Nope"), 0);
    }
}
