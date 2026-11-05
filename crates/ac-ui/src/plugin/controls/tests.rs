//! The newer markup controls in a mounted window, driven through the UI
//! host. Behaviour from OpenAC `tests/AcDream.App.Tests/UI/MarkupCheckboxTests.cs`,
//! `MarkupRadioTests.cs`, `MarkupNumberTests.cs`, `MarkupHotkeyTests.cs`,
//! `UiMarkupHotkeyTests.cs`, `MarkupExpanderTests.cs`, `MarkupSplitTests.cs`,
//! `MarkupContextMenuTests.cs`, `UiMarkupContextMenuTests.cs`,
//! `MarkupColorTests.cs` and `UiMarkupColorTests.cs`.

use std::cell::RefCell;
use std::rc::Rc;

use ac_plugin_ui::{
    ActionValue, BindingKind as K, BindingSchema, PanelDescriptor, PanelView, PluginUi, UiInput,
    UiOwner, UiValue,
};

use super::super::fold::{ExpanderHeader, MarkupExpander, MarkupSplit};
use super::super::{MarkupIcons, PluginTheme, build_panel_with_state};
use super::*;
use crate::event::MouseButton;
use crate::host::UiHost;

struct NoIcons;

impl MarkupIcons for NoIcons {
    fn surface(&mut self, _id: u32) -> Option<super::super::Icon> {
        None
    }
    fn spell(&mut self, _spell: u32) -> Option<super::super::Icon> {
        None
    }
    fn item(&mut self, _object: u32) -> Option<super::super::Icon> {
        None
    }
}

struct Mounted {
    host: UiHost,
    outbox: PluginOutbox,
    state: PluginUi,
    panel: u32,
    _frame: ElementId,
    revision: u64,
}

impl Mounted {
    fn new(body: &str, schema: BindingSchema, values: Vec<(&str, UiValue)>) -> Self {
        Self::with_store(body, schema, values, None)
    }

    fn with_store(
        body: &str,
        schema: BindingSchema,
        values: Vec<(&str, UiValue)>,
        store: Option<super::super::fold::SharedControlState>,
    ) -> Self {
        let mut state = PluginUi::new();
        let panel = state
            .register_panel(
                &UiOwner::new("test.controls", "Controls"),
                PanelDescriptor::new("main", "Controls"),
                &format!(r#"<panel x="0" y="0" w="400" h="300">{body}</panel>"#),
                schema,
            )
            .unwrap();
        if !values.is_empty() {
            state
                .set_values(
                    "test.controls",
                    panel,
                    values.into_iter().map(|(n, v)| (n.to_owned(), v)).collect(),
                )
                .unwrap();
        }
        let view = state.panels().next().unwrap().clone();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
        let mut controller =
            build_panel_with_state(&mut host.root, &view, None, Rc::clone(&outbox), store);
        let frame = controller.root;
        let root = host.root.root();
        host.root.add_child(root, frame);
        host.root
            .register_window(&controller.window_name, frame, Some(frame), 0)
            .unwrap();
        controller.sync(&mut host.root, &view, 1, &mut NoIcons, PluginTheme::Classic);
        host.attach_controller(&view.window_name(), Box::new(controller))
            .unwrap();
        Self {
            host,
            outbox,
            state,
            panel,
            _frame: frame,
            revision: 1,
        }
    }

    fn view(&self) -> PanelView {
        self.state.panels().next().unwrap().clone()
    }

    /// Takes what the window reported, and lets the session apply it as
    /// the plugin would answer a control by storing what it asks for.
    fn take(&mut self) -> Vec<UiInput> {
        self.outbox.borrow_mut().drain(..).collect()
    }

    fn set(&mut self, name: &str, value: UiValue) {
        self.state
            .set_values("test.controls", self.panel, vec![(name.to_owned(), value)])
            .unwrap();
        self.revision += 1;
        let view = self.view();
        let revision = self.revision;
        self.host
            .with_controller::<super::super::PluginPanelController, _>(
                &view.window_name(),
                |c, root| {
                    c.sync(root, &view, revision, &mut NoIcons, PluginTheme::Classic);
                },
            );
    }

    fn element(&mut self, node: usize) -> ElementId {
        let name = self.view().window_name();
        self.host
            .with_controller::<super::super::PluginPanelController, _>(&name, |c, _| {
                c.element(node)
            })
            .flatten()
            .unwrap()
    }

    fn click(&mut self, x: i32, y: i32) {
        self.host.mouse_down(MouseButton::Left, x, y, 0);
        self.host.mouse_up(MouseButton::Left, x, y, 0);
    }

    fn key(&mut self, key: Key) {
        self.host.key_down(key, Modifiers::NONE);
        self.host.key_up(key, Modifiers::NONE);
    }

    fn tick(&mut self, dt: f64) {
        self.host.tick(dt, 0, None);
    }
}

fn action(panel: u32, handler: &str, value: ActionValue) -> UiInput {
    UiInput::Action {
        panel,
        handler: handler.to_owned(),
        value,
    }
}

#[test]
fn a_checkbox_reports_the_value_a_click_asks_for() {
    // MarkupCheckboxTests.Checkbox_ReportsTheNewValue, Checkbox_IndeterminateClicksToTrue,
    // Checkbox_ClickThroughUiRootActivatesWithoutTakingFocus, Checkbox_DisabledFiresNothing
    let schema = BindingSchema::new("B")
        .with("Loot", K::OptionalBool)
        .with("SetLoot", K::BoolAction)
        .with("On", K::Bool);
    let mut m = Mounted::new(
        r#"<checkbox x="8" y="8" w="140" h="16" text="Loot" checked="{Loot}" onchange="{SetLoot}" enabled="{On}"/>"#,
        schema,
        vec![
            ("Loot", UiValue::OptionalBool(Some(true))),
            ("On", UiValue::Bool(true)),
        ],
    );
    let panel = m.panel;
    m.click(12, 12);
    assert_eq!(
        m.take(),
        [action(panel, "SetLoot", ActionValue::Bool(false))]
    );
    assert_eq!(m.host.root.keyboard_focus(), None);
    m.set("Loot", UiValue::OptionalBool(None));
    m.click(12, 12);
    assert_eq!(
        m.take(),
        [action(panel, "SetLoot", ActionValue::Bool(true))]
    );
    m.set("On", UiValue::Bool(false));
    m.click(12, 12);
    assert!(m.take().is_empty());
}

#[test]
fn checkbox_next_values() {
    // MarkupCheckboxTests (NextValue)
    assert!(MarkupCheckbox::next_value(Some(false)));
    assert!(MarkupCheckbox::next_value(None));
    assert!(!MarkupCheckbox::next_value(Some(true)));
}

#[test]
fn a_radio_reports_the_option_picked_on_release_over_it() {
    // MarkupRadioTests.Radio_ClickReportsTheOptionWithoutTakingFocus,
    // Radio_ClickingTheSelectedOptionReportsNothing, Radio_PressAndReleaseMustBeOnTheSameOption,
    // Radio_HorizontalCellsSplitTheWidth
    let schema = BindingSchema::new("B")
        .with("Mode", K::Int)
        .with("SetMode", K::IntAction);
    let mut m = Mounted::new(
        r#"<radio x="0" y="0" w="90" h="20" items="A|B|C" selected="{Mode}" onchange="{SetMode}" orientation="horizontal"/>"#,
        schema,
        vec![("Mode", UiValue::Int(0))],
    );
    let panel = m.panel;
    m.click(45, 10);
    m.click(5, 10);
    m.host.mouse_down(MouseButton::Left, 75, 10, 0);
    m.host.mouse_up(MouseButton::Left, 45, 10, 0);
    assert_eq!(m.take(), [action(panel, "SetMode", ActionValue::Int(1))]);
    assert_eq!(radio_edge(1, 100.0, 3), 33.0);
    assert_eq!(radio_edge(2, 100.0, 3), 66.0);
    assert_eq!(radio_edge(3, 100.0, 3), 100.0);
}

#[test]
fn radio_arrow_keys_move_and_wrap() {
    // MarkupRadioTests.Radio_ArrowKeysMoveAndWrap
    let schema = BindingSchema::new("B")
        .with("Mode", K::Int)
        .with("SetMode", K::IntAction);
    let mut m = Mounted::new(
        r#"<radio x="0" y="0" w="90" h="60" items="A|B|C" selected="{Mode}" onchange="{SetMode}"/>"#,
        schema,
        vec![("Mode", UiValue::Int(2))],
    );
    let panel = m.panel;
    let radio = m.element(0);
    m.host.root.set_keyboard_focus(Some(radio));
    m.key(Key::Down);
    m.key(Key::Up);
    m.set("Mode", UiValue::Int(-1));
    m.key(Key::Right);
    m.key(Key::Left);
    assert_eq!(
        m.take(),
        [
            action(panel, "SetMode", ActionValue::Int(0)),
            action(panel, "SetMode", ActionValue::Int(1)),
            action(panel, "SetMode", ActionValue::Int(0)),
            action(panel, "SetMode", ActionValue::Int(2)),
        ]
    );
}

fn number() -> Mounted {
    let schema = BindingSchema::new("B")
        .with("Count", K::Int)
        .with("SetCount", K::IntAction);
    Mounted::new(
        r#"<number x="8" y="8" w="80" h="20" value="{Count}" min="0" max="100" step="5" onchange="{SetCount}"/>"#,
        schema,
        vec![("Count", UiValue::Int(10))],
    )
}

#[test]
fn a_number_shows_its_value_and_commits_typed_text() {
    // MarkupNumberTests.Number_ShowsTheBoundValueFormatted, Number_FocusLossCommitsTheTypedValue,
    // Number_EnterCommits, Number_EscapeRestoresWithoutReporting
    let mut m = number();
    let panel = m.panel;
    let el = m.element(0);
    assert_eq!(
        m.host.root.widget::<MarkupNumber>(el).unwrap().inner.text(),
        "10"
    );
    m.host.root.set_keyboard_focus(Some(el));
    m.host
        .root
        .with::<MarkupNumber, _>(el, |w, _| w.inner.set_text("42"));
    m.host.root.set_keyboard_focus(None);
    assert_eq!(m.take(), [action(panel, "SetCount", ActionValue::Int(42))]);
    m.host.root.set_keyboard_focus(Some(el));
    m.host
        .root
        .with::<MarkupNumber, _>(el, |w, _| w.inner.set_text("250"));
    m.key(Key::Enter);
    assert_eq!(m.take(), [action(panel, "SetCount", ActionValue::Int(100))]);
    assert_eq!(m.host.root.keyboard_focus(), None);
    m.host.root.set_keyboard_focus(Some(el));
    m.host
        .root
        .with::<MarkupNumber, _>(el, |w, _| w.inner.set_text("77"));
    m.key(Key::Escape);
    assert!(m.take().is_empty());
    assert_eq!(
        m.host.root.widget::<MarkupNumber>(el).unwrap().inner.text(),
        "10"
    );
}

#[test]
fn number_spins_step_and_repeat_while_held() {
    // MarkupNumberTests.Number_SpinButtonsStepAndRepeatWhileHeld, Number_UpDownStepAndShiftStepsTen
    let mut m = number();
    let panel = m.panel;
    // The spin buttons span x 74-88; up is y 8-18.
    m.host.mouse_down(MouseButton::Left, 81, 11, 0);
    assert_eq!(m.take(), [action(panel, "SetCount", ActionValue::Int(15))]);
    m.tick(0.39);
    assert!(m.take().is_empty());
    m.tick(0.02);
    assert_eq!(m.take(), [action(panel, "SetCount", ActionValue::Int(15))]);
    m.host.mouse_up(MouseButton::Left, 81, 11, 0);
    m.tick(1.0);
    assert!(m.take().is_empty());
    let el = m.element(0);
    m.host.root.set_keyboard_focus(Some(el));
    m.host.key_down(Key::Up, Modifiers::SHIFT);
    assert_eq!(m.take(), [action(panel, "SetCount", ActionValue::Int(60))]);
}

fn hotkey() -> Mounted {
    let schema = BindingSchema::new("B")
        .with("NavKey", K::Text)
        .with("SetNavKey", K::TextAction);
    Mounted::new(
        r#"<hotkey x="8" y="8" w="140" h="20" value="{NavKey}" onchange="{SetNavKey}"/>"#,
        schema,
        vec![("NavKey", UiValue::Text("Ctrl+N".into()))],
    )
}

#[test]
fn a_hotkey_box_records_the_next_chord() {
    // UiMarkupHotkeyTests: a click starts recording and takes focus; a
    // modifier alone keeps it waiting; the first other key ends it and the
    // plugin hears the chord; the focus goes back when that key is released.
    let mut m = hotkey();
    let panel = m.panel;
    let el = m.element(0);
    assert_eq!(
        m.host
            .root
            .widget::<MarkupHotkey>(el)
            .unwrap()
            .display_text(),
        "Ctrl+N"
    );
    m.click(20, 12);
    assert_eq!(m.host.root.keyboard_focus(), Some(el));
    assert!(m.host.root.widget::<MarkupHotkey>(el).unwrap().capturing);
    m.host.key_down(Key::ControlLeft, Modifiers::CTRL);
    assert!(m.host.root.widget::<MarkupHotkey>(el).unwrap().capturing);
    m.host
        .key_down(Key::Keypad5, Modifiers::CTRL | Modifiers::SHIFT);
    assert_eq!(
        m.take(),
        [action(
            panel,
            "SetNavKey",
            ActionValue::Text("Ctrl+Shift+Numpad5".into())
        )]
    );
    assert_eq!(m.host.root.keyboard_focus(), Some(el));
    m.host.key_up(Key::Keypad5, Modifiers::NONE);
    assert_eq!(m.host.root.keyboard_focus(), None);
}

#[test]
fn a_hotkey_box_unbinds_with_backspace_and_escape_changes_nothing() {
    // UiMarkupHotkeyTests: Backspace or Delete unbinds (""), Escape stops
    // without a change, the same chord as bound is not reported.
    let mut m = hotkey();
    let panel = m.panel;
    m.click(20, 12);
    m.key(Key::Escape);
    m.click(20, 12);
    m.host.key_down(Key::N, Modifiers::CTRL);
    m.host.key_up(Key::N, Modifiers::NONE);
    m.click(20, 12);
    m.key(Key::Backspace);
    assert_eq!(
        m.take(),
        [action(panel, "SetNavKey", ActionValue::Text(String::new()))]
    );
}

fn expander(extra: &str) -> Mounted {
    let schema = BindingSchema::new("B")
        .with("Open", K::Bool)
        .with("SetOpen", K::BoolAction);
    Mounted::new(
        &format!(
            r#"<expander name="adv" x="8" y="60" w="240" h="120" text="Advanced" onchange="{{SetOpen}}" {extra}>
                 <label x="4" y="4" text="inside"/>
               </expander>
               <label name="below" x="8" y="190" text="Below"/>
               <label name="beside" x="260" y="70" text="Beside"/>"#
        ),
        schema,
        vec![("Open", UiValue::Bool(true))],
    )
}

fn top(m: &mut Mounted, node: usize) -> f32 {
    let el = m.element(node);
    m.host.root.element(el).unwrap().top
}

#[test]
fn folding_an_expander_moves_what_is_below_it_up() {
    // MarkupExpanderTests: a header click folds the content; the elements
    // at or below its bottom move up by what it gave up, those beside it
    // stay; opening puts them back; onchange hears each state.
    let mut m = expander("");
    let panel = m.panel;
    let header_y = 60 + 6;
    m.click(30, header_y);
    m.tick(0.016);
    assert_eq!(
        {
            let e = m.element(0);
            m.host.root.element(e)
        }
        .unwrap()
        .height,
        18.0
    );
    assert_eq!(top(&mut m, 2), 190.0 - 102.0);
    assert_eq!(top(&mut m, 3), 70.0);
    m.click(30, header_y);
    m.tick(0.016);
    assert_eq!(top(&mut m, 2), 190.0);
    assert_eq!(
        {
            let e = m.element(0);
            m.host.root.element(e)
        }
        .unwrap()
        .height,
        120.0
    );
    assert_eq!(
        m.take(),
        [
            action(panel, "SetOpen", ActionValue::Bool(false)),
            action(panel, "SetOpen", ActionValue::Bool(true)),
        ]
    );
}

fn click_header(m: &mut Mounted, x: i32, node: usize) {
    let y = top(m, node) as i32 + 6;
    m.click(x, y);
    m.tick(0.016);
}

fn height(m: &mut Mounted, node: usize) -> f32 {
    let el = m.element(node);
    m.host.root.element(el).unwrap().height
}

#[test]
fn side_by_side_expanders_give_back_only_what_both_left_free() {
    // What is below moves up by what the lowest thing above it gave up, not
    // by each expander's height, in whatever order they fold and open.
    let mut m = Mounted::new(
        r#"<expander x="8" y="20" w="150" h="100" text="Tall"/>
           <expander x="170" y="20" w="150" h="60" text="Short"/>
           <group x="8" y="130" w="312" h="20"/>"#,
        BindingSchema::new("B"),
        Vec::new(),
    );
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 2), 90.0, "the short one is still open");
    click_header(&mut m, 190, 1);
    assert_eq!(top(&mut m, 2), 48.0, "both folded");
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 2), 130.0, "the tall one is open again");
    click_header(&mut m, 190, 1);
    assert_eq!(top(&mut m, 2), 130.0);

    click_header(&mut m, 190, 1);
    assert_eq!(top(&mut m, 2), 130.0, "the tall one still reaches as far");
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 2), 48.0);
    click_header(&mut m, 190, 1);
    assert_eq!(top(&mut m, 2), 90.0);
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 2), 130.0);
}

#[test]
fn what_is_below_never_covers_a_folded_header() {
    // A group lifted by the sum of the folds would sit over the headers,
    // and the one it covered could not be clicked open again.
    let mut m = Mounted::new(
        r#"<expander x="12" y="100" w="120" h="90" text="One"/>
           <expander x="140" y="100" w="110" h="60" text="Two" open="false"/>
           <expander x="258" y="100" w="110" h="60" text="Three"/>
           <group x="12" y="198" w="356" h="60"/>"#,
        BindingSchema::new("B"),
        Vec::new(),
    );
    m.tick(0.016);
    assert_eq!(height(&mut m, 1), 18.0);
    assert_eq!(top(&mut m, 3), 198.0, "the first is taller than the fold");
    click_header(&mut m, 40, 0);
    click_header(&mut m, 280, 2);
    assert_eq!(top(&mut m, 3), 126.0, "all folded: 8 below the headers");
    click_header(&mut m, 160, 1);
    assert_eq!(height(&mut m, 1), 60.0, "the header could still be clicked");
    assert_eq!(top(&mut m, 3), 168.0);
}

#[test]
fn each_column_follows_its_own_expander() {
    let mut m = Mounted::new(
        r#"<expander x="8" y="20" w="150" h="100" text="Left"/>
           <expander x="170" y="20" w="150" h="100" text="Right"/>
           <group x="8" y="130" w="150" h="20"/>
           <group x="170" y="130" w="150" h="20"/>
           <group x="8" y="160" w="312" h="20"/>"#,
        BindingSchema::new("B"),
        Vec::new(),
    );
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 2), 48.0, "under the fold");
    assert_eq!(top(&mut m, 3), 130.0, "the other column stays");
    assert_eq!(top(&mut m, 4), 160.0, "held by the other column");
    click_header(&mut m, 190, 1);
    assert_eq!(top(&mut m, 3), 48.0);
    assert_eq!(top(&mut m, 4), 78.0, "keeps its gap to both columns");
}

#[test]
fn stacked_expanders_add_up_in_any_order() {
    let mut m = Mounted::new(
        r#"<expander x="8" y="10" w="150" h="100" text="Upper"/>
           <expander x="8" y="120" w="150" h="60" text="Lower"/>
           <group x="8" y="190" w="150" h="10"/>"#,
        BindingSchema::new("B"),
        Vec::new(),
    );
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 1), 38.0);
    assert_eq!(top(&mut m, 2), 108.0);
    click_header(&mut m, 30, 1);
    assert_eq!(top(&mut m, 2), 66.0);
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 1), 120.0);
    assert_eq!(top(&mut m, 2), 148.0);
    click_header(&mut m, 30, 1);
    assert_eq!(top(&mut m, 2), 190.0);
}

#[test]
fn expanders_folded_from_the_start_are_placed_together() {
    let mut m = Mounted::new(
        r#"<expander x="8" y="20" w="150" h="100" text="Tall" open="false"/>
           <expander x="170" y="20" w="150" h="60" text="Short" open="false"/>
           <group x="8" y="130" w="312" h="20"/>"#,
        BindingSchema::new("B"),
        Vec::new(),
    );
    m.tick(0.016);
    assert_eq!(top(&mut m, 2), 48.0);
    click_header(&mut m, 190, 1);
    assert_eq!(top(&mut m, 2), 90.0);
}

#[test]
fn an_element_beside_an_expander_holds_up_what_is_below_both() {
    let mut m = Mounted::new(
        r#"<expander x="8" y="20" w="150" h="100" text="Fold"/>
           <group x="170" y="30" w="100" h="60"/>
           <group x="8" y="130" w="312" h="20"/>"#,
        BindingSchema::new("B"),
        Vec::new(),
    );
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 1), 30.0, "beside the fold");
    assert_eq!(top(&mut m, 2), 100.0, "keeps its gap below the group");
}

#[test]
fn a_label_reaches_as_far_as_its_text() {
    // A label with no width is as wide as its text (7 px a character with
    // no font: 210 px here), so it spans both columns and stays under the
    // one still open.
    let mut m = Mounted::new(
        r#"<expander x="8" y="20" w="150" h="100" text="Left"/>
           <expander x="170" y="20" w="150" h="100" text="Right"/>
           <label x="8" y="130" text="A caption across both columns"/>"#,
        BindingSchema::new("B"),
        Vec::new(),
    );
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 2), 130.0, "the right column is still open");
    click_header(&mut m, 190, 1);
    assert_eq!(top(&mut m, 2), 48.0, "both folded");
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 2), 130.0, "the left column is open again");
}

#[test]
fn a_hidden_element_beside_an_expander_holds_up_nothing() {
    // Hidden, it takes no room; shown again while the expander is folded,
    // what is below it moves back down under it.
    let mut m = Mounted::new(
        r#"<expander x="8" y="20" w="150" h="100" text="Fold"/>
           <group x="170" y="30" w="100" h="60" visible="{Shown}"/>
           <group x="8" y="130" w="312" h="20"/>"#,
        BindingSchema::new("B").with("Shown", K::Bool),
        vec![("Shown", UiValue::Bool(false))],
    );
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 2), 48.0, "only the fold is above it");
    m.set("Shown", UiValue::Bool(true));
    m.tick(0.016);
    assert_eq!(top(&mut m, 2), 100.0, "below the group again");
    m.set("Shown", UiValue::Bool(false));
    m.tick(0.016);
    assert_eq!(top(&mut m, 2), 48.0);
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 2), 130.0);
}

#[test]
fn a_stretched_sibling_grows_into_the_room_and_gives_it_back() {
    let mut m = Mounted::new(
        r#"<expander x="8" y="20" w="150" h="100" text="Fold"/>
           <group x="8" y="130" w="150" h="100" anchor="left,top,bottom"/>
           <group x="8" y="240" w="150" h="20" anchor="left,bottom"/>"#,
        BindingSchema::new("B"),
        Vec::new(),
    );
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 1), 48.0);
    assert_eq!(height(&mut m, 1), 182.0);
    assert_eq!(top(&mut m, 2), 240.0, "the stretched one's bottom stays");

    let frame = m._frame;
    m.host.root.element_mut(frame).unwrap().height = 350.0;
    for node in [1, 2] {
        let el = m.element(node);
        m.host.root.apply_anchor(el, 400.0, 350.0);
    }
    assert_eq!(height(&mut m, 1), 232.0);
    assert_eq!(top(&mut m, 2), 290.0);

    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 1), 130.0);
    assert_eq!(height(&mut m, 1), 150.0);
    assert_eq!(top(&mut m, 2), 290.0);
}

#[test]
fn a_folded_stretched_expander_moved_from_above_opens_to_the_right_height() {
    let mut m = Mounted::new(
        r#"<expander x="8" y="10" w="150" h="50" text="Upper"/>
           <expander x="8" y="70" w="150" h="100" text="Lower" anchor="left,top,bottom"/>"#,
        BindingSchema::new("B"),
        Vec::new(),
    );
    click_header(&mut m, 30, 1);
    assert_eq!(height(&mut m, 1), 18.0);
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 1), 38.0);
    assert_eq!(height(&mut m, 1), 18.0);
    click_header(&mut m, 30, 1);
    assert_eq!(top(&mut m, 1), 38.0);
    assert_eq!(height(&mut m, 1), 132.0, "its bottom stays where it was");
    click_header(&mut m, 30, 0);
    assert_eq!(top(&mut m, 1), 70.0);
    assert_eq!(height(&mut m, 1), 100.0);
}

#[test]
fn the_header_arrow_follows_a_click() {
    let mut m = expander("");
    let el = m.element(0);
    let arrow = |m: &Mounted| {
        let header = m.host.root.widget::<MarkupExpander>(el).unwrap().header;
        let header = m.host.root.widget::<ExpanderHeader>(header).unwrap();
        header.expanded
    };
    assert!(arrow(&m));
    m.click(30, 66);
    assert!(!arrow(&m), "folded by the click");
    m.tick(0.016);
    assert!(!arrow(&m));
    m.click(30, 66);
    assert!(arrow(&m), "opened by the click");
}

#[test]
fn a_bound_expander_only_asks_and_follows_its_binding() {
    // MarkupExpanderTests: with expanded bound, a click only calls onchange
    // and the expander follows the property.
    let mut m = expander(r#"expanded="{Open}""#);
    let panel = m.panel;
    m.click(30, 66);
    m.tick(0.016);
    assert_eq!(
        m.take(),
        [action(panel, "SetOpen", ActionValue::Bool(false))]
    );
    assert_eq!(top(&mut m, 2), 190.0);
    m.set("Open", UiValue::Bool(false));
    m.tick(0.016);
    assert_eq!(top(&mut m, 2), 88.0);
}

#[test]
fn an_unbound_expander_remembers_the_players_choice() {
    // MarkupExpanderTests (remembered state): the choice is remembered per
    // character, and forgotten when it matches open.
    let store: super::super::fold::SharedControlState = Rc::new(RefCell::new(
        ac_plugin_ui::control_state::ControlStateStore::new(None, || "alice".to_owned(), || 0),
    ));
    let schema = BindingSchema::new("B");
    let mut m = Mounted::with_store(
        r#"<expander name="adv" x="8" y="60" w="240" h="120" text="Advanced"/>"#,
        schema,
        Vec::new(),
        Some(Rc::clone(&store)),
    );
    m.click(30, 66);
    {
        let mut s = store.borrow_mut();
        let mut w = s.window("plugin:test.controls:main");
        assert_eq!(
            ac_plugin_ui::control_state::ControlState::get(&mut w, "adv", "expanded").as_deref(),
            Some("false")
        );
    }
    m.click(30, 66);
    let mut s = store.borrow_mut();
    let mut w = s.window("plugin:test.controls:main");
    assert_eq!(
        ac_plugin_ui::control_state::ControlState::get(&mut w, "adv", "expanded"),
        None
    );
    let el = m.element(0);
    drop(s);
    assert!(m.host.root.widget::<MarkupExpander>(el).is_some());
}

#[test]
fn a_split_lays_out_its_panes_and_follows_a_drag() {
    // MarkupSplitTests.Split_DefaultsToHalfAndLaysOutThePanes,
    // Split_DragMovesTheBarAndResizesThePanes, Split_MinAndMaxClampADrag
    let schema = BindingSchema::new("B").with("SetAt", K::FloatAction);
    let mut m = Mounted::new(
        r#"<split x="0" y="0" w="400" h="300" min="80" max="320" onchange="{SetAt}"><group/><group/></split>"#,
        schema,
        Vec::new(),
    );
    let panel = m.panel;
    let split = m.element(0);
    m.tick(0.016);
    let first = m.element(1);
    assert_eq!(m.host.root.element(first).unwrap().width, 197.0);
    assert_eq!(
        m.host.root.widget::<MarkupSplit>(split).unwrap().laid_out,
        197.0
    );
    m.host.mouse_down(MouseButton::Left, 199, 100, 0);
    m.host.mouse_move(239, 100);
    m.host.mouse_move(10, 100);
    m.host.mouse_up(MouseButton::Left, 10, 100, 0);
    assert_eq!(
        m.take(),
        [
            action(panel, "SetAt", ActionValue::Float(237.0)),
            action(panel, "SetAt", ActionValue::Float(80.0)),
        ]
    );
    m.tick(0.016);
    assert_eq!(m.host.root.element(first).unwrap().width, 80.0);
}

#[test]
fn a_context_menu_opens_on_a_right_click_and_reports_the_pick() {
    // MarkupContextMenuTests / UiMarkupContextMenuTests: a right click on
    // the element opens the menu at the cursor; onopen hears −1 off a list;
    // separators and disabled items cannot be picked; a pick closes the
    // menu and reports the item's index, separators counted.
    let schema = BindingSchema::new("B")
        .with("Menu", K::TextList)
        .with("On", K::BoolList)
        .with("Pick", K::IntAction)
        .with("Opened", K::IntAction)
        .with("Go", K::Action);
    let mut m = Mounted::new(
        r#"<button x="8" y="8" w="80" h="20" text="Profile" onclick="{Go}">
             <contextmenu items="{Menu}" enabled="{On}" onselect="{Pick}" onopen="{Opened}"/>
           </button>"#,
        schema,
        vec![
            (
                "Menu",
                UiValue::TextList(vec![
                    "Copy".into(),
                    "-".into(),
                    "Paste".into(),
                    "Cut".into(),
                ]),
            ),
            ("On", UiValue::BoolList(vec![true, true, true, false])),
        ],
    );
    let panel = m.panel;
    m.host.mouse_down(MouseButton::Right, 20, 15, 0);
    m.host.mouse_up(MouseButton::Right, 20, 15, 0);
    // Items 18 high from 3 px in; the separator is 7 high.
    m.click(30, 15 + 3 + 18 + 3); // the separator
    m.click(30, 15 + 3 + 18 + 7 + 18 + 5); // Cut (disabled)
    m.click(30, 15 + 3 + 18 + 7 + 5); // Paste
    assert_eq!(
        m.take(),
        [
            action(panel, "Opened", ActionValue::Int(-1)),
            action(panel, "Pick", ActionValue::Int(2)),
        ]
    );
    assert_eq!(super::super::fold::place_popup(1000.0, 80.0, 1024.0), 920.0);
    assert_eq!(super::super::fold::place_popup(10.0, 80.0, 1024.0), 10.0);
}

#[test]
fn a_colour_swatch_opens_a_picker_and_reports_a_palette_pick() {
    // UiMarkupColorTests: a click opens the picker below the swatch; a
    // palette swatch picks its colour, reported on the next tick in the
    // bound type; Escape puts back the opening colour.
    let schema = BindingSchema::new("B")
        .with("Bar", K::Uint)
        .with("SetBar", K::UintAction);
    let mut m = Mounted::new(
        r#"<color x="8" y="8" w="40" h="18" value="{Bar}" onchange="{SetBar}"/>"#,
        schema,
        vec![("Bar", UiValue::Uint(0xFFFF_0000))],
    );
    let panel = m.panel;
    m.click(20, 12);
    let el = m.element(0);
    assert!(
        m.host
            .root
            .widget::<super::super::color::MarkupColor>(el)
            .unwrap()
            .is_open()
    );
    // The popup sits 2 below the swatch (y 28); the palette row at 164,
    // its second swatch (black) at x 6 + 16.
    let (px, py) = (8 + 6 + 16 + 3, 8 + 18 + 2 + 164 + 3);
    m.click(px, py);
    m.tick(0.016);
    assert_eq!(
        m.take(),
        [action(panel, "SetBar", ActionValue::Uint(0xFF00_0000))]
    );
    m.set("Bar", UiValue::Uint(0xFF00_0000));
    m.key(Key::Escape);
    assert_eq!(
        m.take(),
        [action(panel, "SetBar", ActionValue::Uint(0xFFFF_0000))]
    );
}

fn list_schema() -> BindingSchema {
    BindingSchema::new("B")
        .with("Names", K::TextList)
        .with("Dists", K::TextList)
        .with("On", K::BoolList)
        .with("Toggle", K::IntAction)
        .with("SetAll", K::BoolAction)
        .with("Selected", K::Int)
        .with("Pick", K::IntAction)
        .with("SetSort", K::IntBoolAction)
        .with("Selection", K::IntList)
        .with("SetRows", K::IntListAction)
        .with("Levels", K::IntList)
        .with("Keys", K::TextList)
        .with("Expand", K::TextBoolAction)
}

fn list_values() -> Vec<(&'static str, UiValue)> {
    vec![
        (
            "Names",
            UiValue::TextList(vec![
                "mosswart".into(),
                "Drudge".into(),
                "rat".into(),
                "Banderling".into(),
            ]),
        ),
        (
            "Dists",
            UiValue::TextList(vec!["12.5".into(), "3".into(), "40".into(), "7".into()]),
        ),
        ("On", UiValue::BoolList(vec![true, false, true, false])),
        ("Selected", UiValue::Int(-1)),
        ("Selection", UiValue::IntList(Vec::new())),
    ]
}

// Columns: Name 0-120, Dist 120-200, On 200-240, the rest 240-300. The
// header is y 0-18; shown row v spans y 18+18v .. 36+18v.
const LIST_COLUMNS: &str = r#"
  <column type="text" header="Name" width="120" items="{Names}" sort="text"/>
  <column type="text" header="Dist" width="80" items="{Dists}" sort="number"/>
  <column type="checkbox" header="all" width="40" values="{On}" onchange="{Toggle}" onall="{SetAll}"/>
  <column type="text" items="{Names}"/>"#;

#[test]
fn a_header_click_sorts_and_rows_report_plugin_rows() {
    // MarkupListHeaderTests.Sort_NameCyclesAscendingDescendingNone,
    // Sort_CallbacksReceivePluginRows, Sort_NumberParsesCellsAndKeepsUnparseableLast
    let mut m = Mounted::new(
        &format!(
            r#"<list x="0" y="0" w="300" h="108" header="true" selected="{{Selected}}" onchange="{{Pick}}" onsort="{{SetSort}}">{LIST_COLUMNS}</list>"#
        ),
        list_schema(),
        list_values(),
    );
    let panel = m.panel;
    m.click(60, 9);
    m.tick(0.016);
    // Ascending by name: Banderling, Drudge, mosswart, rat.
    m.click(60, 18 + 5);
    m.click(160, 9);
    m.tick(0.016);
    // By distance: 3 (Drudge), 7, 12.5, 40.
    m.click(60, 18 + 18 + 5);
    assert_eq!(
        m.take(),
        [
            action(panel, "SetSort", ActionValue::IntBool(0, false)),
            action(panel, "Pick", ActionValue::Int(3)),
            action(panel, "SetSort", ActionValue::IntBool(1, false)),
            action(panel, "Pick", ActionValue::Int(3)),
        ]
    );
}

#[test]
fn the_select_all_box_ticks_everything_then_clears_it() {
    // MarkupListHeaderTests.SelectAll_ClickTicksEverythingThenClearsIt
    let mut m = Mounted::new(
        &format!(
            r#"<list x="0" y="0" w="300" h="108" header="true" selected="{{Selected}}">{LIST_COLUMNS}</list>"#
        ),
        list_schema(),
        list_values(),
    );
    let panel = m.panel;
    m.click(220, 9);
    m.set("On", UiValue::BoolList(vec![true; 4]));
    m.click(220, 9);
    assert_eq!(
        m.take(),
        [
            action(panel, "SetAll", ActionValue::Bool(true)),
            action(panel, "SetAll", ActionValue::Bool(false)),
        ]
    );
}

#[test]
fn a_multi_select_list_reports_the_rows_a_click_picks() {
    // MarkupListMultiSelectTests: a click selects one row, Ctrl adds or
    // removes, Shift runs from the anchor.
    let mut m = Mounted::new(
        r#"<list x="0" y="0" w="300" h="108" selectmode="multi" selected="{Selection}" onchange="{SetRows}">
             <column type="text" width="120" items="{Names}"/><column type="text" items="{Dists}"/></list>"#,
        list_schema(),
        list_values(),
    );
    let panel = m.panel;
    m.click(30, 5);
    m.set("Selection", UiValue::IntList(vec![0]));
    m.host.mouse_down(MouseButton::Left, 30, 18 * 2 + 5, 0);
    m.host
        .root
        .on_mouse_up(MouseButton::Left, 30, 18 * 2 + 5, 0);
    assert_eq!(
        m.take(),
        [
            action(panel, "SetRows", ActionValue::IntList(vec![0])),
            action(panel, "SetRows", ActionValue::IntList(vec![2])),
        ]
    );
}

#[test]
fn ctrl_or_the_command_key_adds_to_a_multi_select_list() {
    // MarkupListMultiSelectTests with OpenAC's UiSelectModifiers: Add is
    // Ctrl or Cmd (the Windows key elsewhere), for a click and for A.
    for add in [Modifiers::CTRL, Modifiers::WIN] {
        let mut m = Mounted::new(
            r#"<list x="0" y="0" w="300" h="108" selectmode="multi" selected="{Selection}" onchange="{SetRows}">
             <column type="text" width="120" items="{Names}"/><column type="text" items="{Dists}"/></list>"#,
            list_schema(),
            list_values(),
        );
        let panel = m.panel;
        m.click(30, 5);
        m.set("Selection", UiValue::IntList(vec![0]));
        m.host.set_modifiers(add);
        m.click(30, 18 * 2 + 5);
        // A click does not take keyboard focus.
        let list = m.element(0);
        m.host.root.set_keyboard_focus(Some(list));
        m.host.key_down(Key::A, add);
        assert_eq!(
            m.take(),
            [
                action(panel, "SetRows", ActionValue::IntList(vec![0])),
                action(panel, "SetRows", ActionValue::IntList(vec![0, 2])),
                action(panel, "SetRows", ActionValue::IntList(vec![0, 1, 2, 3])),
            ],
            "{add:?}"
        );
    }
}

#[test]
fn a_tree_arrow_folds_a_parent_and_hides_its_rows() {
    // MarkupListTreeTests.ArrowClick_TogglesAParentWithoutSelecting,
    // Clicks_ReportPluginRowsUnderCollapse
    let mut values = list_values();
    values.push(("Levels", UiValue::IntList(vec![0, 1, 1, 0])));
    values.push((
        "Keys",
        UiValue::TextList(vec!["a".into(), "a1".into(), "a2".into(), "b".into()]),
    ));
    let mut m = Mounted::new(
        r#"<list x="0" y="0" w="300" h="108" tree="true" levels="{Levels}" keys="{Keys}" onexpand="{Expand}" selected="{Selected}" onchange="{Pick}">
             <column type="text" width="120" items="{Names}"/><column type="text" items="{Dists}"/></list>"#,
        list_schema(),
        values,
    );
    let panel = m.panel;
    // Row 0's arrow slot starts at 3.
    m.click(6, 5);
    m.tick(0.016);
    // Folded: the second row shown is plugin row 3.
    m.click(60, 18 + 5);
    assert_eq!(
        m.take(),
        [
            action(panel, "Expand", ActionValue::TextBool("a".into(), false)),
            action(panel, "Pick", ActionValue::Int(3)),
        ]
    );
}

#[test]
fn hiding_a_window_closes_a_popup_open_in_it() {
    // OpenAC UiRoot.ClearSubtreeOwnership: a popup whose owner hides closes
    // with it, so it does not come back open.
    let schema = BindingSchema::new("B")
        .with("Menu", K::TextList)
        .with("Pick", K::IntAction);
    let mut m = Mounted::new(
        r#"<button x="8" y="8" w="80" h="20" text="Profile"><contextmenu items="{Menu}" onselect="{Pick}"/></button>"#,
        schema,
        vec![("Menu", UiValue::TextList(vec!["Copy".into()]))],
    );
    m.host.mouse_down(MouseButton::Right, 20, 15, 0);
    m.host.mouse_up(MouseButton::Right, 20, 15, 0);
    assert!(m.host.root.active_popup().is_some());
    m.host.root.hide_window("plugin:test.controls:main");
    assert!(m.host.root.active_popup().is_none());
}
