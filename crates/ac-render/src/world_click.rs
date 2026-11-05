//! What a click on the 3-D view does with the object under the pointer
//! (OpenAC `SelectionInteractionController.PickAndStoreSelection`,
//! `PickSelectAndExamine` and `PlaceDraggedItem`, with the target mode's
//! claim on a click from `RuntimeItemInteraction.OfferPrimaryClick`):
//!
//! - a left click selects the object, or, while a use, examine or
//!   use-on-target mode waits, offers it to the mode instead;
//! - a double click does the same and then uses the object, unless the
//!   player wields it;
//! - a right click selects and examines it (a waiting mode is left alone);
//! - an item dragged out of a window and let go over the world is placed
//!   on the object there, or on the ground over none.
//!
//! The picked object flashes in every case. A click on nothing does
//! nothing. A click offered to a mode claims a second click on the same
//! object within the double-click time, so the double click that follows
//! does not use what the mode just took.

use ac_runtime::gameplay::GameplayCommand;
use ac_runtime::inventory::{ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;
use ac_ui::event::ItemDragSource;

/// How long after a click offered to a target mode a second click on the
/// same object is still the mode's, milliseconds (the double-click time).
pub const CLAIMED_CLICK_MS: u64 = 500;

/// Which click.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WorldClick {
    /// Left button: select.
    Select,
    /// Right button: select and examine.
    Examine,
    /// Left double click: select and use.
    Use,
}

/// What the window knows about the clicked object and the session.
#[derive(Debug, Clone, Copy, Default)]
pub struct ClickFacts {
    /// A use, examine or use-on-target mode waits for a click.
    pub target_mode: bool,
    /// The player wields the object.
    pub wielded_by_player: bool,
}

/// What a click asks for.
#[derive(Debug, Clone, Default)]
pub struct ClickOutcome {
    /// The object to flash.
    pub pulse: Option<u32>,
    /// The session commands, in order.
    pub commands: Vec<SessionCommand>,
}

/// The click memory a target mode's claim needs.
#[derive(Debug, Clone, Copy, Default)]
pub struct WorldClicks {
    /// The last object offered to a target mode, and when.
    claimed: Option<(u32, u64)>,
}

fn select(guid: u32) -> SessionCommand {
    SessionCommand::Gameplay(GameplayCommand::Select(ObjectRef::Guid(guid)))
}

fn item(command: ItemCommand) -> SessionCommand {
    SessionCommand::Item(command)
}

impl WorldClicks {
    /// `click` at `now_ms` found `picked`.
    pub fn click(
        &mut self,
        click: WorldClick,
        picked: Option<u32>,
        facts: ClickFacts,
        now_ms: u64,
    ) -> ClickOutcome {
        let Some(guid) = picked else {
            return ClickOutcome::default();
        };
        let mut out = ClickOutcome {
            pulse: Some(guid),
            commands: Vec::new(),
        };
        if click == WorldClick::Examine {
            out.commands.push(select(guid));
            out.commands
                .push(item(ItemCommand::Appraise(ObjectRef::Guid(guid))));
            return out;
        }
        if facts.target_mode {
            self.claimed = Some((guid, now_ms));
            out.commands
                .push(item(ItemCommand::OfferTarget(ObjectRef::Guid(guid))));
            return out;
        }
        if let Some((claimed, at)) = self.claimed.take()
            && claimed == guid
            && now_ms.saturating_sub(at) <= CLAIMED_CLICK_MS
        {
            // Still the mode's click; the claim stays for the rest of it.
            self.claimed = Some((claimed, at));
            return out;
        }
        out.commands.push(select(guid));
        if click == WorldClick::Use && !facts.wielded_by_player {
            out.commands
                .push(item(ItemCommand::Use(ObjectRef::Guid(guid))));
        }
        out
    }

    /// An item dragged out of a window let go over the world, over
    /// `picked` (the ground when none): it is placed there, `amount` of a
    /// stack (0 for all). A shortcut dragged off the toolbar, or an item
    /// dragged out of the salvage, vendor or house purchase window, places
    /// nothing. The selection is left alone.
    #[must_use]
    pub fn drop_item(
        item_id: u32,
        source: ItemDragSource,
        picked: Option<u32>,
        amount: u32,
    ) -> ClickOutcome {
        let mut out = ClickOutcome {
            pulse: picked,
            commands: Vec::new(),
        };
        if source.is_alias() || item_id == 0 {
            return out;
        }
        out.commands.push(item(ItemCommand::Place {
            item: ObjectRef::Guid(item_id),
            target: picked.map(ObjectRef::Guid),
            amount,
        }));
        out
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Interaction/SelectionInteractionControllerTests.cs`
    //! (the click cases: TargetModeClickPulsesBeforeItIsConsumedAndIncludesSelf,
    //! RightClickPulsesSelectsAndExaminesPickedWorldObject,
    //! RightClickEmptyWorldIsANoOp, RightClickExaminesWithoutConsumingLeftClickTargetMode,
    //! DoubleClickOnTheOwnWieldedObjectSelectsAndPulsesWithoutUse,
    //! DragReleasePulsesTheDropTargetWithoutChangingSelection,
    //! RejectedTargetModeClickConsumesTheFollowingDoubleClick).

    use super::*;

    const TARGET: u32 = 0x7000_0001;

    fn dbg(commands: &[SessionCommand]) -> Vec<String> {
        commands.iter().map(|c| format!("{c:?}")).collect()
    }

    fn mode() -> ClickFacts {
        ClickFacts {
            target_mode: true,
            ..ClickFacts::default()
        }
    }

    #[test]
    fn a_target_mode_click_flashes_and_goes_to_the_mode_without_selecting() {
        let out = WorldClicks::default().click(WorldClick::Select, Some(TARGET), mode(), 0);
        assert_eq!(out.pulse, Some(TARGET));
        assert_eq!(
            dbg(&out.commands),
            dbg(&[item(ItemCommand::OfferTarget(ObjectRef::Guid(TARGET)))])
        );
    }

    #[test]
    fn a_right_click_flashes_selects_and_examines() {
        let out = WorldClicks::default().click(
            WorldClick::Examine,
            Some(TARGET),
            ClickFacts::default(),
            0,
        );
        assert_eq!(out.pulse, Some(TARGET));
        assert_eq!(
            dbg(&out.commands),
            dbg(&[
                select(TARGET),
                item(ItemCommand::Appraise(ObjectRef::Guid(TARGET)))
            ])
        );
    }

    #[test]
    fn a_right_click_on_nothing_does_nothing() {
        let out = WorldClicks::default().click(WorldClick::Examine, None, ClickFacts::default(), 0);
        assert!(out.pulse.is_none() && out.commands.is_empty());
    }

    #[test]
    fn a_right_click_examines_and_leaves_a_waiting_mode_alone() {
        let out = WorldClicks::default().click(WorldClick::Examine, Some(TARGET), mode(), 0);
        assert!(
            !out.commands
                .iter()
                .any(|c| matches!(c, SessionCommand::Item(ItemCommand::OfferTarget(_))))
        );
        assert_eq!(dbg(&out.commands[..1]), dbg(&[select(TARGET)]));
    }

    #[test]
    fn a_left_click_selects() {
        let out = WorldClicks::default().click(
            WorldClick::Select,
            Some(TARGET),
            ClickFacts::default(),
            0,
        );
        assert_eq!(dbg(&out.commands), dbg(&[select(TARGET)]));
    }

    #[test]
    fn a_double_click_uses_unless_the_player_wields_it() {
        let used =
            WorldClicks::default().click(WorldClick::Use, Some(TARGET), ClickFacts::default(), 0);
        assert_eq!(
            dbg(&used.commands),
            dbg(&[
                select(TARGET),
                item(ItemCommand::Use(ObjectRef::Guid(TARGET)))
            ])
        );
        let wielded = WorldClicks::default().click(
            WorldClick::Use,
            Some(TARGET),
            ClickFacts {
                wielded_by_player: true,
                ..ClickFacts::default()
            },
            0,
        );
        assert_eq!(wielded.pulse, Some(TARGET));
        assert_eq!(dbg(&wielded.commands), dbg(&[select(TARGET)]));
    }

    #[test]
    fn a_click_the_mode_took_claims_the_double_click_that_follows() {
        let mut clicks = WorldClicks::default();
        clicks.click(WorldClick::Select, Some(TARGET), mode(), 1_000);
        let double = clicks.click(WorldClick::Use, Some(TARGET), ClickFacts::default(), 1_200);
        assert!(double.commands.is_empty());
        // Past the double-click time the object clicks as ever.
        let later = clicks.click(WorldClick::Use, Some(TARGET), ClickFacts::default(), 1_800);
        assert_eq!(later.commands.len(), 2);
    }

    #[test]
    fn a_drop_over_an_object_places_it_there_and_selects_nothing() {
        let out = WorldClicks::drop_item(0x7000_0002, ItemDragSource::Inventory, Some(TARGET), 0);
        assert_eq!(out.pulse, Some(TARGET));
        assert_eq!(
            dbg(&out.commands),
            dbg(&[item(ItemCommand::Place {
                item: ObjectRef::Guid(0x7000_0002),
                target: Some(ObjectRef::Guid(TARGET)),
                amount: 0,
            })])
        );
    }

    #[test]
    fn a_drop_over_nothing_goes_to_the_ground_and_a_shortcut_drop_places_nothing() {
        let ground = WorldClicks::drop_item(0x7000_0002, ItemDragSource::Inventory, None, 3);
        assert_eq!(ground.pulse, None);
        assert_eq!(
            dbg(&ground.commands),
            dbg(&[item(ItemCommand::Place {
                item: ObjectRef::Guid(0x7000_0002),
                target: None,
                amount: 3,
            })])
        );
        assert!(
            WorldClicks::drop_item(0x7000_0002, ItemDragSource::ShortcutBar, None, 0)
                .commands
                .is_empty()
        );
    }

    /// Issue #32: a staged item dragged out of the salvage window only
    /// unstages; it is not dropped on the ground or onto an object.
    #[test]
    fn a_salvage_window_drop_places_nothing() {
        for picked in [None, Some(TARGET)] {
            let out = WorldClicks::drop_item(0x7000_0002, ItemDragSource::Salvage, picked, 0);
            assert!(out.commands.is_empty(), "{picked:?}");
        }
    }

    /// Issue #44: an item dragged out of the vendor's sell list (or the
    /// house purchase window) only comes off that list; it is not dropped
    /// on the ground or onto an object.
    #[test]
    fn a_vendor_window_drop_places_nothing() {
        for picked in [None, Some(TARGET)] {
            let out = WorldClicks::drop_item(0x7000_0002, ItemDragSource::Vendor, picked, 0);
            assert!(out.commands.is_empty(), "{picked:?}");
        }
    }
}
