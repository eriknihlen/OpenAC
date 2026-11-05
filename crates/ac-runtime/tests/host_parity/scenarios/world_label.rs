//! A plugin hanging labels over things in the world (OpenAC
//! `Scenarios/WorldLabelParityTests.cs`): the set is the session's, so a
//! plugin gets the same answer (taken, refused past the cap, unusable
//! entries dropped) whether or not a window draws it. The window holds what
//! it was sent; a session without one holds the same set and draws nothing.

use ac_plugin::abi::calls;
use ac_plugin::abi::ui::WorldLabel;

use crate::host_parity::arm::Arm;
use crate::host_parity::arm::run;
use crate::host_parity::world;

/// The most labels one plugin may show (`ac-plugin-ui` `MAXIMUM_LABELS`).
const MAXIMUM_LABELS: u32 = 256;

fn label(object_id: u32, text: &str, line: i32) -> WorldLabel {
    WorldLabel {
        object_id,
        text: text.to_owned(),
        r: 1.0,
        g: 1.0,
        b: 1.0,
        a: 1.0,
        line,
        max_range: 60.0,
        ..WorldLabel::default()
    }
}

/// What the drawing side reads back: the windowed arm's stand-in window
/// holds the set it was sent; the windowless arm has nothing to draw.
fn assert_shown(arm: &Arm, expected: &[String]) {
    if let Some(view) = arm.plugin_ui_view() {
        let shown: Vec<String> = view
            .labels
            .iter()
            .map(|l| format!("{:08X} '{}' line {}", l.object_id, l.text, l.line))
            .collect();
        assert_eq!(shown, expected);
    }
}

// OpenAC `WorldLabelParityTests.PushingLabelsIsAnsweredTheSameOnBothClients`.
#[test]
fn pushing_labels_is_answered_the_same_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);

        transcript.step("a usable set, with two entries to drop");
        let taken = arm.call::<calls::UiLabelsShow>(&vec![
            label(world::MONSTER, "Drudge", 0),
            label(world::MONSTER, "12 m", 1),
            label(0, "nobody", 0),
            label(world::SECOND_MONSTER, "", 0),
        ]);
        transcript.record("taken", taken);
        assert!(taken);
        arm.sync();
        let usable = [
            format!("{:08X} 'Drudge' line 0", world::MONSTER),
            format!("{:08X} '12 m' line 1", world::MONSTER),
        ];
        assert_shown(arm, &usable);

        transcript.step("a set past the cap");
        let too_many: Vec<WorldLabel> =
            (1..=MAXIMUM_LABELS + 1).map(|i| label(i, "n", 0)).collect();
        let refused = arm.call::<calls::UiLabelsShow>(&too_many);
        transcript.record("taken", refused);
        assert!(!refused);
        arm.sync();
        assert_shown(arm, &usable);

        transcript.step("an empty set clears");
        let cleared = arm.call::<calls::UiLabelsShow>(&Vec::new());
        transcript.record("taken", cleared);
        assert!(cleared);
        arm.sync();
        assert_shown(arm, &[]);
    });
}
