//! The UI automation probe and its script runner.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/RetailUiAutomationProbeTests.cs`
//! (the snapshot, item assertions and the runner's commands, timing and
//! world lifecycle waits) over a fake target that records what the runner
//! asks of the window.

mod common;

use std::collections::{HashMap, HashSet};

use ac_core::items::{ClientObject, ClientObjectTable, EquipMask};
use ac_ui::automation::{
    self, AutomationTarget, CheckpointStatus, ScriptRunner, assert_item, dump_text, snapshot,
};
use ac_ui::event::ItemDragSource;
use ac_ui::input::InputAction;
use ac_ui::root::UiRoot;
use ac_ui::tree::Element;
use ac_ui::widgets::{ItemList, ItemSlot};
use common::{at, panel};

#[derive(Debug, Clone, PartialEq)]
enum Event {
    Move(i32, i32),
    Button(i32, i32, bool),
    Command(String),
    Press(InputAction),
    Held(InputAction, bool),
    Look(f32, f32),
    Resize(u32, u32),
    Close,
}

#[derive(Default)]
struct Fake {
    ui: UiRoot,
    objects: ClientObjectTable,
    events: Vec<Event>,
    world: Option<(bool, bool, u32)>,
    framebuffer: Option<(u32, u32)>,
    screenshots: HashSet<String>,
    signals: HashSet<String>,
    checkpoints: HashMap<u64, (CheckpointStatus, Option<String>)>,
    cancelled: Vec<u64>,
    next_checkpoint: u64,
    resize_applies: bool,
}

impl AutomationTarget for Fake {
    fn ui(&self) -> &UiRoot {
        &self.ui
    }
    fn objects(&self) -> Option<&ClientObjectTable> {
        Some(&self.objects)
    }
    fn mouse_move(&mut self, x: i32, y: i32) {
        self.events.push(Event::Move(x, y));
    }
    fn mouse_button(&mut self, x: i32, y: i32, pressed: bool) {
        self.events.push(Event::Button(x, y, pressed));
    }
    fn submit_command(&mut self, text: &str) -> Result<(), String> {
        self.events.push(Event::Command(text.to_owned()));
        Ok(())
    }
    fn press_input(&mut self, action: InputAction) -> Result<(), String> {
        self.events.push(Event::Press(action));
        Ok(())
    }
    fn set_input_held(&mut self, action: InputAction, held: bool) -> Result<(), String> {
        self.events.push(Event::Held(action, held));
        Ok(())
    }
    fn mouse_look(&mut self, dx: f32, dy: f32) -> Result<(), String> {
        self.events.push(Event::Look(dx, dy));
        Ok(())
    }
    fn world_ready(&self) -> Option<bool> {
        self.world.map(|w| w.0)
    }
    fn world_visible(&self) -> Option<bool> {
        self.world.map(|w| w.1)
    }
    fn portal_materializations(&self) -> Option<u32> {
        self.world.map(|w| w.2)
    }
    fn framebuffer(&self) -> Option<(u32, u32)> {
        self.framebuffer
    }
    fn request_checkpoint(&mut self, name: &str) -> Result<u64, String> {
        if name.contains('/') {
            return Err("artifact name may contain only letters, digits, '-' and '_'".into());
        }
        self.next_checkpoint += 1;
        self.checkpoints
            .insert(self.next_checkpoint, (CheckpointStatus::Pending, None));
        Ok(self.next_checkpoint)
    }
    fn checkpoint_status(&self, sequence: u64) -> (CheckpointStatus, Option<String>) {
        self.checkpoints
            .get(&sequence)
            .cloned()
            .unwrap_or((CheckpointStatus::Failed, None))
    }
    fn cancel_checkpoint(&mut self, sequence: u64) {
        self.cancelled.push(sequence);
    }
    fn request_screenshot(&mut self, _name: &str) -> Result<(), String> {
        Ok(())
    }
    fn screenshot_complete(&self, name: &str) -> bool {
        self.screenshots.contains(name)
    }
    fn signal_published(&self, name: &str) -> Result<bool, String> {
        Ok(self.signals.contains(name))
    }
    fn resize(&mut self, width: u32, height: u32) -> Result<(), String> {
        self.events.push(Event::Resize(width, height));
        if self.resize_applies {
            self.framebuffer = Some((width, height));
        }
        Ok(())
    }
    fn close_client(&mut self) -> Result<(), String> {
        self.events.push(Event::Close);
        Ok(())
    }
}

/// Two item lists: the pack (dat 0x10000010, the item 0x5001 in slot 0)
/// and the paperdoll (dat 0x10000020, empty).
fn fake() -> Fake {
    let mut ui = UiRoot::new();
    ui.set_size(400.0, 300.0);
    let r = ui.root();
    let window = panel(&mut ui, r, at(0.0, 0.0, 400.0, 300.0));
    let mut e = Element::at(10.0, 20.0, 64.0, 32.0);
    e.dat_element_id = 0x1000_0010;
    let pack = ItemList::create(&mut ui, window, e);
    let cell = ui.widget::<ItemList>(pack).unwrap().cells()[0];
    ui.with::<ItemSlot, _>(cell, |s, _| {
        s.set_item(0x5001, None, None, None);
        s.slot_index = 0;
        s.source_kind = ItemDragSource::Inventory;
    });
    let mut e = Element::at(100.0, 20.0, 64.0, 32.0);
    e.dat_element_id = 0x1000_0020;
    ItemList::create(&mut ui, window, e);
    let mut o = ClientObject::new(0x5001);
    o.container_id = 0x7001;
    o.container_slot = 3;
    o.currently_equipped_location = EquipMask::CHEST_ARMOR | EquipMask::UPPER_ARM_ARMOR;
    let mut objects = ClientObjectTable::default();
    objects.add_or_update(o, &mut Vec::new());
    Fake {
        ui,
        objects,
        ..Fake::default()
    }
}

fn run(script: &str, target: &mut Fake) -> (ScriptRunner, Vec<String>) {
    let mut runner = ScriptRunner::parse(script, false);
    let mut log = Vec::new();
    runner.tick(0.016, target, &mut log);
    (runner, log)
}

#[test]
fn the_snapshot_lists_dat_elements_and_item_cells() {
    let f = fake();
    let rows = snapshot(&f.ui, true);
    assert!(
        rows.iter()
            .any(|r| r.dat_element_id == 0x1000_0010 && r.type_name == "ItemList")
    );
    let item: Vec<_> = rows.iter().filter(|r| r.item_id == 0x5001).collect();
    assert_eq!(item.len(), 1);
    assert_eq!(item[0].source_kind, Some(ItemDragSource::Inventory));
    assert_eq!(item[0].slot_index, 0);
    assert!(item[0].rect.2 > 0.0 && item[0].rect.3 > 0.0);
    let text = dump_text(&f.ui, true);
    assert!(text.contains("ItemList dat=0x10000010"), "{text}");
    assert!(
        text.contains("item=0x00005001 slot=0 source=Inventory"),
        "{text}"
    );
}

#[test]
fn an_item_assertion_checks_the_item_table() {
    let f = fake();
    assert!(
        assert_item(
            &f.objects,
            0x5001,
            Some(EquipMask::CHEST_ARMOR | EquipMask::UPPER_ARM_ARMOR),
            Some(0x7001),
            Some(3)
        )
        .is_ok()
    );
    assert!(assert_item(&f.objects, 0x5001, None, None, Some(4)).is_err());
    assert!(assert_item(&f.objects, 0x9999, None, None, None).is_err());
}

#[test]
fn wait_item_then_double_click_runs_in_one_frame() {
    let mut f = fake();
    let (runner, log) = run(
        "wait item 0x5001 inventory 100\ndoubleclick item 0x5001 inventory",
        &mut f,
    );
    assert!(runner.completed());
    assert!(log.iter().any(|l| l.contains("UI probe script complete")));
    let clicks: Vec<_> = f
        .events
        .iter()
        .filter(|e| matches!(e, Event::Button(_, _, true)))
        .collect();
    // Twice at the cell's centre.
    assert_eq!(clicks.len(), 2);
    let center = automation::find_by_item_id(&f.ui, 0x5001, None)
        .unwrap()
        .center();
    assert_eq!(*clicks[0], Event::Button(center.0, center.1, true));
}

#[test]
fn drag_item_to_element_presses_at_the_item_and_lets_go_on_the_element() {
    let mut f = fake();
    let (runner, _) = run("drag item 0x5001 element 0x10000020 inventory", &mut f);
    assert!(runner.completed());
    let from = automation::find_by_item_id(&f.ui, 0x5001, None)
        .unwrap()
        .center();
    let to = automation::find_by_dat_element_id(&f.ui, 0x1000_0020)
        .unwrap()
        .center();
    assert_eq!(
        f.events,
        [
            Event::Move(from.0, from.1),
            Event::Button(from.0, from.1, true),
            Event::Move(from.0 + 5, from.1 + 5),
            Event::Move(to.0, to.1),
            Event::Button(to.0, to.1, false),
        ]
    );
}

#[test]
fn a_command_goes_to_the_submit_path() {
    let mut f = fake();
    let (runner, _) = run("command /ls  # a comment", &mut f);
    assert!(runner.completed());
    assert_eq!(f.events, [Event::Command("/ls".into())]);
}

#[test]
fn sleep_keeps_fractional_frame_time_and_releases_on_the_exact_deadline() {
    let mut f = fake();
    let mut runner = ScriptRunner::parse("sleep 10\ncommand /ls", false);
    let mut log = Vec::new();
    runner.tick(0.0, &mut f, &mut log);
    for _ in 0..1000 {
        runner.tick(0.000_001, &mut f, &mut log);
    }
    assert!(f.events.is_empty());
    runner.tick(0.009, &mut f, &mut log);
    assert_eq!(f.events, [Event::Command("/ls".into())]);
    assert!(runner.completed());

    // Sixty 1/60 s frames make exactly one second.
    let mut f = fake();
    let mut runner = ScriptRunner::parse("sleep 1000\ncommand /ls", false);
    runner.tick(0.0, &mut f, &mut log);
    for _ in 0..60 {
        runner.tick(1.0 / 60.0, &mut f, &mut log);
    }
    assert!(runner.completed());
}

#[test]
fn sleep_ignores_invalid_or_overflowing_frame_times() {
    let mut f = fake();
    let mut runner = ScriptRunner::parse("sleep 1\ncommand /ls", false);
    let mut log = Vec::new();
    runner.tick(0.0, &mut f, &mut log);
    for dt in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY, f64::MAX, -1.0] {
        runner.tick(dt, &mut f, &mut log);
    }
    assert!(f.events.is_empty());
    runner.tick(0.001, &mut f, &mut log);
    assert!(runner.completed());
}

#[test]
fn a_wait_does_not_time_out_on_its_exact_fractional_deadline() {
    for fps in [30u32, 120] {
        let mut f = fake();
        let mut runner =
            ScriptRunner::parse("sleep 1000000\nwait item 0xDEAD inventory 1000", false);
        let mut log = Vec::new();
        runner.tick(0.0, &mut f, &mut log);
        runner.tick(1000.0, &mut f, &mut log);
        for _ in 0..fps {
            runner.tick(1.0 / f64::from(fps), &mut f, &mut log);
        }
        assert!(!runner.completed(), "{fps}");
        assert!(!log.iter().any(|l| l.contains("timed out")), "{fps}");
        runner.tick(1.0 / f64::from(fps), &mut f, &mut log);
        assert!(runner.completed(), "{fps}");
        assert!(log.iter().any(|l| l.contains("timed out")), "{fps}");
    }
}

#[test]
fn input_drives_presses_and_held_edges_and_dispose_lets_go() {
    let mut f = fake();
    let mut runner = ScriptRunner::parse(
        "input down MovementForward\nsleep 10\ninput up MovementForward\ninput press CombatToggleCombat",
        false,
    );
    let mut log = Vec::new();
    runner.tick(0.0, &mut f, &mut log);
    assert_eq!(f.events, [Event::Held(InputAction::MovementForward, true)]);
    runner.tick(0.010, &mut f, &mut log);
    assert!(runner.completed());
    assert_eq!(
        f.events,
        [
            Event::Held(InputAction::MovementForward, true),
            Event::Held(InputAction::MovementForward, false),
            Event::Press(InputAction::CombatToggleCombat),
        ]
    );

    let mut f = fake();
    let mut runner = ScriptRunner::parse("input down MovementForward\nsleep 1000", false);
    runner.tick(0.0, &mut f, &mut log);
    runner.dispose(&mut f);
    assert_eq!(
        f.events,
        [
            Event::Held(InputAction::MovementForward, true),
            Event::Held(InputAction::MovementForward, false),
        ]
    );
}

#[test]
fn mouselook_takes_two_numbers() {
    let mut f = fake();
    let (runner, _) = run("mouselook 12 -3.5", &mut f);
    assert!(runner.completed());
    assert_eq!(f.events, [Event::Look(12.0, -3.5)]);
    let mut f = fake();
    let (_, log) = run("mouselook 12", &mut f);
    assert!(log.iter().any(|l| l.contains("usage: mouselook <dx> <dy>")));
    assert!(f.events.is_empty());
}

#[test]
fn world_lifecycle_commands_wait_for_the_runtime() {
    let mut f = fake();
    f.world = Some((false, false, 0));
    let mut runner = ScriptRunner::parse(
        "wait world-ready\nwait world-visible\nwait materialized 1\ncommand /done",
        false,
    );
    let mut log = Vec::new();
    runner.tick(0.016, &mut f, &mut log);
    assert!(!runner.completed());
    f.world = Some((true, false, 0));
    runner.tick(0.016, &mut f, &mut log);
    assert!(!runner.completed());
    f.world = Some((true, true, 0));
    runner.tick(0.016, &mut f, &mut log);
    assert!(!runner.completed());
    f.world = Some((true, true, 1));
    runner.tick(0.016, &mut f, &mut log);
    assert!(runner.completed());
    assert_eq!(f.events, [Event::Command("/done".into())]);
}

#[test]
fn a_world_ready_timeout_stops_with_an_actionable_line() {
    let mut f = fake();
    f.world = Some((false, false, 0));
    let mut runner = ScriptRunner::parse("wait world-ready 100", false);
    let mut log = Vec::new();
    runner.tick(0.0, &mut f, &mut log);
    runner.tick(0.2, &mut f, &mut log);
    assert!(runner.completed());
    assert!(
        log.iter()
            .any(|l| l
                == "line 1: timed out waiting for world readiness; command: wait world-ready 100"),
        "{log:?}"
    );
}

#[test]
fn a_signal_wait_blocks_until_the_harness_publishes_it() {
    let mut f = fake();
    let mut runner = ScriptRunner::parse("wait signal go\ncommand /after", false);
    let mut log = Vec::new();
    runner.tick(0.016, &mut f, &mut log);
    assert!(f.events.is_empty());
    f.signals.insert("go".into());
    runner.tick(0.016, &mut f, &mut log);
    assert_eq!(f.events, [Event::Command("/after".into())]);
}

#[test]
fn a_resize_yields_the_frame_before_the_next_command() {
    let mut f = fake();
    f.resize_applies = true;
    let mut runner = ScriptRunner::parse(
        "resize 800 600\nwait framebuffer 800 600\nscreenshot a",
        false,
    );
    let mut log = Vec::new();
    runner.tick(0.016, &mut f, &mut log);
    assert_eq!(f.events, [Event::Resize(800, 600)]);
    assert!(!runner.completed());
    f.screenshots.insert("a".into());
    runner.tick(0.016, &mut f, &mut log);
    assert!(runner.completed());
    // Out of range sizes are refused.
    let mut f = fake();
    let (_, log) = run("resize 100 100", &mut f);
    assert!(
        log.iter()
            .any(|l| l.contains("within 320x240 and 8192x8192"))
    );
}

#[test]
fn close_client_asks_once() {
    let mut f = fake();
    let (runner, _) = run("close-client", &mut f);
    assert!(runner.completed());
    assert_eq!(f.events, [Event::Close]);
}

#[test]
fn a_failed_checkpoint_stops_with_its_error_and_dispose_cancels_a_pending_one() {
    let mut f = fake();
    let mut runner = ScriptRunner::parse("checkpoint first\ncommand /after", false);
    let mut log = Vec::new();
    runner.tick(0.016, &mut f, &mut log);
    assert!(!runner.completed());
    f.checkpoints.insert(
        1,
        (
            CheckpointStatus::Failed,
            Some("checkpoint 'first' sequence 1 failed: disk".into()),
        ),
    );
    runner.tick(0.016, &mut f, &mut log);
    assert!(runner.completed());
    assert!(
        log.iter()
            .any(|l| l.contains("checkpoint 'first' sequence 1 failed: disk"))
    );
    assert!(f.events.is_empty());

    let mut f = fake();
    let mut runner = ScriptRunner::parse("checkpoint second", false);
    runner.tick(0.016, &mut f, &mut log);
    runner.dispose(&mut f);
    assert_eq!(f.cancelled, [1]);

    let mut f = fake();
    let mut runner = ScriptRunner::parse("checkpoint third\ncommand /after", false);
    runner.tick(0.016, &mut f, &mut log);
    f.checkpoints.insert(1, (CheckpointStatus::Succeeded, None));
    runner.tick(0.016, &mut f, &mut log);
    assert_eq!(f.events, [Event::Command("/after".into())]);
}

#[test]
fn unknown_commands_and_missing_files_stop_with_a_line() {
    let mut f = fake();
    let (runner, log) = run("frobnicate", &mut f);
    assert!(runner.completed());
    assert!(
        log.iter()
            .any(|l| l == "line 1: unknown command 'frobnicate'; command: frobnicate")
    );
    let mut runner = ScriptRunner::load(std::path::Path::new("Z:/no/such/script.txt"), false);
    let mut log = Vec::new();
    runner.tick(0.016, &mut f, &mut log);
    assert!(runner.completed());
    assert!(log[0].starts_with("failed to load UI probe script"));
}
