//! Stacks: how much of one stack merges into another, and the split
//! quantity the player picks with the slider or the text box.

/// One side of a merge.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct StackMergeItem {
    pub object_id: u32,
    pub weenie_class_id: u32,
    pub stack_size: i32,
    pub max_stack_size: i32,
    pub trade_state: i32,
}

/// A legal merge: move `amount` from the source stack to the target.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StackMergePlan {
    pub source_object_id: u32,
    pub target_object_id: u32,
    pub amount: u32,
}

impl StackMergePlan {
    /// The merge of `requested_amount` (0 for the whole source stack) from
    /// `source` into `target`, limited by the room left in the target; none
    /// when the merge is not legal: not ready, a missing or identical
    /// object, either side unstackable or being traded, different kinds,
    /// or a full target.
    #[must_use]
    pub fn plan(
        source: &StackMergeItem,
        target: &StackMergeItem,
        ready_for_inventory_request: bool,
        requested_amount: i32,
    ) -> Option<Self> {
        if !ready_for_inventory_request
            || source.object_id == 0
            || target.object_id == 0
            || source.object_id == target.object_id
            || source.max_stack_size <= 1
            || target.max_stack_size <= 1
            || source.trade_state == 1
            || target.trade_state == 1
            || source.weenie_class_id != target.weenie_class_id
        {
            return None;
        }

        let target_size = target.stack_size.max(1);
        let available = target.max_stack_size.saturating_sub(target_size);
        if available <= 0 {
            return None;
        }

        let source_size = source.stack_size.max(1);
        let desired = if requested_amount > 0 {
            requested_amount.min(source_size)
        } else {
            source_size
        };
        let transfer = desired.min(available) as u32;
        (transfer != 0).then_some(Self {
            source_object_id: source.object_id,
            target_object_id: target.object_id,
            amount: transfer,
        })
    }
}

/// The split quantity: between 1 and the selected stack's size.
#[derive(Debug, Clone, PartialEq)]
pub struct StackSplitQuantity {
    value: u32,
    maximum: u32,
}

impl Default for StackSplitQuantity {
    fn default() -> Self {
        Self {
            value: 1,
            maximum: 1,
        }
    }
}

impl StackSplitQuantity {
    /// A quantity of 1 out of 1.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The chosen quantity.
    #[must_use]
    pub fn value(&self) -> u32 {
        self.value
    }

    /// The selected stack's size (at least 1).
    #[must_use]
    pub fn maximum(&self) -> u32 {
        self.maximum
    }

    /// Where the slider sits, 0..=1.
    #[must_use]
    pub fn ratio(&self) -> f32 {
        self.value as f32 / self.maximum as f32
    }

    /// A new selection: the maximum becomes the stack size (at least 1) and
    /// the quantity `initial_value`, or the whole stack. Returns whether
    /// anything changed.
    pub fn reset(&mut self, stack_size: u32, initial_value: Option<u32>) -> bool {
        let maximum = stack_size.max(1);
        self.set(initial_value.unwrap_or(maximum), maximum)
    }

    /// The quantity typed into the box: digits only, anything else (or a
    /// number too large for 32 bits) reads as 0, then the quantity is
    /// clamped into range. Returns whether it changed.
    pub fn set_from_text(&mut self, text: Option<&str>) -> bool {
        self.set_value(parse_digits(text.unwrap_or("")).unwrap_or(0))
    }

    /// The quantity at a slider position (0..=1, clamped): the slider has
    /// a thousand steps, and step `s` of a stack of `n` is `1 + s * n /
    /// 1000`. Returns whether it changed.
    pub fn set_from_slider_ratio(&mut self, ratio: f32) -> bool {
        let clamped = ratio.clamp(0.0, 1.0);
        let position_millis = (clamped * 1000.0).trunc() as u32;
        let scaled = u64::from(position_millis) * u64::from(self.maximum);
        let value = 1u32.wrapping_add((scaled / 1000) as u32);
        self.set_value(value)
    }

    /// Sets the quantity, clamped into 1..=maximum. Returns whether it changed.
    pub fn set_value(&mut self, value: u32) -> bool {
        self.set(value, self.maximum)
    }

    /// How many of `object_id` a drag moves: the chosen quantity for the
    /// selected object, the whole stack (at least 1) for anything else.
    #[must_use]
    pub fn object_split_size(
        &self,
        object_id: u32,
        selected_object_id: u32,
        object_stack_size: u32,
    ) -> u32 {
        if object_id == selected_object_id {
            self.value
        } else {
            object_stack_size.max(1)
        }
    }

    fn set(&mut self, value: u32, maximum: u32) -> bool {
        let maximum = maximum.max(1);
        let value = value.clamp(1, maximum);
        if self.value == value && self.maximum == maximum {
            return false;
        }
        self.value = value;
        self.maximum = maximum;
        true
    }
}

/// An unsigned decimal number with nothing but ASCII digits.
fn parse_digits(text: &str) -> Option<u32> {
    if text.is_empty() || !text.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    text.parse().ok()
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Items/StackMergePlannerTests.cs`
    //! and `StackSplitQuantityStateTests.cs`.

    use super::*;

    fn item(id: u32, size: i32) -> StackMergeItem {
        StackMergeItem {
            object_id: id,
            weenie_class_id: 10,
            stack_size: size,
            max_stack_size: 100,
            trade_state: 0,
        }
    }

    fn plan(source: u32, target: u32, amount: u32) -> Option<StackMergePlan> {
        Some(StackMergePlan {
            source_object_id: source,
            target_object_id: target,
            amount,
        })
    }

    #[test]
    fn matching_stacks_transfer_requested_amount() {
        assert_eq!(
            StackMergePlan::plan(&item(1, 40), &item(2, 20), true, 15),
            plan(1, 2, 15)
        );
    }

    #[test]
    fn transfer_is_limited_by_destination_capacity() {
        assert_eq!(
            StackMergePlan::plan(&item(1, 40), &item(2, 95), true, 40),
            plan(1, 2, 5)
        );
    }

    #[test]
    fn zero_requested_amount_uses_whole_source_stack() {
        assert_eq!(
            StackMergePlan::plan(&item(1, 7), &item(2, 20), true, 0),
            plan(1, 2, 7)
        );
    }

    #[test]
    fn illegal_merge_returns_no_plan() {
        for (ready, target_wcid, target_size, source_trade, source_max, target_max) in [
            (false, 10, 10, 0, 100, 100),
            (true, 11, 10, 0, 100, 100),
            (true, 10, 10, 1, 100, 100),
            (true, 10, 10, 0, 1, 100),
            (true, 10, 10, 0, 100, 1),
            (true, 10, 100, 0, 100, 100),
        ] {
            let source = StackMergeItem {
                object_id: 1,
                weenie_class_id: 10,
                stack_size: 10,
                max_stack_size: source_max,
                trade_state: source_trade,
            };
            let target = StackMergeItem {
                object_id: 2,
                weenie_class_id: target_wcid,
                stack_size: target_size,
                max_stack_size: target_max,
                trade_state: 0,
            };
            assert_eq!(StackMergePlan::plan(&source, &target, ready, 10), None);
        }
    }

    #[test]
    fn reset_initializes_to_full_stack_and_clamps_zero_maximum_to_one() {
        let mut state = StackSplitQuantity::new();
        state.reset(17, None);
        assert_eq!(state.value(), 17);
        assert_eq!(state.maximum(), 17);
        assert_eq!(state.ratio(), 1.0);
        state.reset(0, None);
        assert_eq!(state.value(), 1);
        assert_eq!(state.maximum(), 1);
    }

    #[test]
    fn text_uses_digit_only_parse_then_clamps() {
        for (text, expected) in [
            (None, 1),
            (Some(""), 1),
            (Some("invalid"), 1),
            (Some("0"), 1),
            (Some("7"), 7),
            (Some("999"), 10),
        ] {
            let mut state = StackSplitQuantity::new();
            state.reset(10, None);
            state.set_from_text(text);
            assert_eq!(state.value(), expected, "{text:?}");
        }
    }

    #[test]
    fn slider_matches_thousand_step_conversion() {
        for (ratio, expected) in [
            (-1.0, 1),
            (0.0, 1),
            (0.099, 1),
            (0.1, 2),
            (0.5, 6),
            (0.999, 10),
            (1.0, 10),
            (2.0, 10),
        ] {
            let mut state = StackSplitQuantity::new();
            state.reset(10, None);
            state.set_from_slider_ratio(ratio);
            assert_eq!(state.value(), expected, "{ratio}");
        }
    }

    #[test]
    fn object_split_size_uses_slider_only_for_selected_object() {
        let mut state = StackSplitQuantity::new();
        state.reset(10, None);
        state.set_value(3);
        assert_eq!(state.object_split_size(0xA, 0xA, 10), 3);
        assert_eq!(state.object_split_size(0xB, 0xA, 7), 7);
        assert_eq!(state.object_split_size(0xC, 0xC, 0), 3);
        assert_eq!(state.object_split_size(0xC, 0xD, 0), 1);
    }

    #[test]
    fn changes_are_reported_only_when_something_changed() {
        let mut state = StackSplitQuantity::new();
        assert!(state.reset(10, None));
        assert!(!state.reset(10, None));
        assert!(state.set_value(4));
        assert!(!state.set_value(4));
    }
}
