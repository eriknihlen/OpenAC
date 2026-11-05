//! `<repeat>` in a mounted window: rows built per item into the flex
//! container after the marker, each reading its own item and naming its
//! row in its handlers; kept, moved, added and removed as the list changes;
//! rows' remembered state per key, forgotten when the key leaves. Ported
//! from OpenAC `tests/AcDream.App.Tests/UI/MarkupRepeatBuildTests.cs` and
//! `MarkupRepeatReconcileTests.cs` (each test names its source). A C# row
//! object becomes an item record; a C# list re-assigned on the binding is a
//! new items value pushed by the plugin. Headless metrics: 7 pixels a
//! character, 14-pixel lines.

use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Arc;

use ac_plugin_ui::control_state::{ControlState, ControlStateStore};
use ac_plugin_ui::{
    ActionValue, BindingKind as K, BindingSchema, ItemRecord, PanelDescriptor, PluginUi, UiInput,
    UiOwner, UiValue,
};

use super::fold::{MarkupExpander, SharedControlState};
use super::layout::ContentHost;
use super::*;
use crate::event::{Key, Modifiers, MouseButton};
use crate::host::UiHost;

struct NoIcons;

impl MarkupIcons for NoIcons {
    fn surface(&mut self, _id: u32) -> Option<Icon> {
        None
    }
    fn spell(&mut self, _spell: u32) -> Option<Icon> {
        None
    }
    fn item(&mut self, _object: u32) -> Option<Icon> {
        None
    }
}

const OWNER: &str = "test.repeat";
const WINDOW: &str = "plugin:test.repeat:main";

fn row_type() -> BindingSchema {
    BindingSchema::new("Row")
        .with("Id", K::Text)
        .with("Label", K::Text)
        .with("On", K::Bool)
        .with("Click", K::Action)
        .with("SetOn", K::BoolAction)
        .with("Pick", K::IntAction)
}

fn schema() -> BindingSchema {
    BindingSchema::new("Binding")
        .with_items("Rows", row_type())
        .with_items(
            "Sections",
            BindingSchema::new("Section")
                .with("Id", K::Text)
                .with("Title", K::Text)
                .with_items("Rows", row_type()),
        )
}

fn row(id: &str, label: &str) -> ItemRecord {
    ItemRecord::new()
        .with("Id", UiValue::Text(id.to_owned()))
        .with("Label", UiValue::Text(label.to_owned()))
}

/// Rows labelled by their ids (OpenAC `Rows(params string[] ids)`).
fn rows(ids: &[&str]) -> UiValue {
    UiValue::Items(Arc::new(ids.iter().map(|id| row(id, id)).collect()))
}

fn list(records: Vec<ItemRecord>) -> UiValue {
    UiValue::Items(Arc::new(records))
}

/// A section titled by its upper-cased id, with rows `id + r`
/// (OpenAC `Sec`).
fn section(id: &str, inner: &[&str]) -> ItemRecord {
    let ids: Vec<String> = inner.iter().map(|r| format!("{id}{r}")).collect();
    let refs: Vec<&str> = ids.iter().map(String::as_str).collect();
    ItemRecord::new()
        .with("Id", UiValue::Text(id.to_owned()))
        .with("Title", UiValue::Text(id.to_uppercase()))
        .with("Rows", rows(&refs))
}

fn store() -> SharedControlState {
    Rc::new(RefCell::new(ControlStateStore::new(
        None,
        || "alice".to_owned(),
        || 0,
    )))
}

fn remembered(store: &SharedControlState, control: &str, kind: &str) -> Option<String> {
    store.borrow_mut().window(WINDOW).get(control, kind)
}

fn remember(store: &SharedControlState, control: &str, kind: &str, value: &str) {
    store.borrow_mut().window(WINDOW).set(control, kind, value);
}

/// A mounted window and the session state behind it (OpenAC `Mount`).
struct Rig {
    state: PluginUi,
    panel: u32,
    host: UiHost,
    outbox: PluginOutbox,
    content: ElementId,
    revision: u64,
    now: i64,
    theme: PluginTheme,
}

impl Rig {
    fn new(body: &str, values: Vec<(&str, UiValue)>) -> Self {
        Self::mount(body, values, "h=\"400\" layout=\"column\"", None)
    }

    fn mount(
        body: &str,
        values: Vec<(&str, UiValue)>,
        attrs: &str,
        store: Option<SharedControlState>,
    ) -> Self {
        Self::mount_with(schema(), body, values, attrs, store)
    }

    fn mount_with(
        schema: BindingSchema,
        body: &str,
        values: Vec<(&str, UiValue)>,
        attrs: &str,
        store: Option<SharedControlState>,
    ) -> Self {
        let mut state = PluginUi::new();
        let panel = state
            .register_panel(
                &UiOwner::new(OWNER, "Repeat"),
                PanelDescriptor::new("main", "Demo"),
                &format!("<panel x=\"0\" y=\"0\" w=\"300\" {attrs}>{body}</panel>"),
                schema,
            )
            .unwrap();
        if !values.is_empty() {
            state
                .set_values(
                    OWNER,
                    panel,
                    values.into_iter().map(|(n, v)| (n.to_owned(), v)).collect(),
                )
                .unwrap();
        }
        let view = state.panels().next().unwrap().clone();
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
        let controller =
            build_panel_with_state(&mut host.root, &view, None, Rc::clone(&outbox), store);
        let frame = controller.root;
        let content = controller.content_root().unwrap();
        let top = host.root.root();
        host.root.add_child(top, frame);
        host.root
            .register_window(WINDOW, frame, Some(content), controller.geometry_revision())
            .unwrap();
        host.attach_controller(WINDOW, Box::new(controller))
            .unwrap();
        let mut rig = Self {
            state,
            panel,
            host,
            outbox,
            content,
            revision: 0,
            now: 0,
            theme: PluginTheme::Classic,
        };
        rig.frame();
        rig
    }

    fn set(&mut self, name: &str, value: UiValue) {
        self.state
            .set_values(OWNER, self.panel, vec![(name.to_owned(), value)])
            .unwrap();
    }

    /// One frame: the tick, the session's values, the draw (which lays
    /// out).
    fn frame(&mut self) {
        self.now += 16;
        self.host.tick(0.016, self.now, None);
        let view = self.state.panels().next().unwrap().clone();
        self.revision += 1;
        let (revision, theme) = (self.revision, self.theme);
        self.host
            .with_controller::<PluginPanelController, _>(WINDOW, |c, ui| {
                c.sync(ui, &view, revision, &mut NoIcons, theme);
            });
        let _ = self.host.draw((800.0, 600.0), &crate::draw::NoSprites);
    }

    fn controller<R>(&mut self, f: impl FnOnce(&mut PluginPanelController) -> R) -> R {
        self.host
            .with_controller::<PluginPanelController, _>(WINDOW, |c, _| f(c))
            .unwrap()
    }

    /// The content area's children after the marker (OpenAC `Copies`).
    fn copies(&self) -> Vec<ElementId> {
        self.host
            .root
            .children(self.content)
            .into_iter()
            .skip(1)
            .collect()
    }

    /// The content area's flex items (OpenAC `Items`).
    fn items(&self) -> Vec<ElementId> {
        self.host
            .root
            .widget::<ContentHost>(self.content)
            .and_then(|h| h.flex.clone())
            .map(|f| f.borrow().elements(None))
            .unwrap_or_default()
    }

    fn labels(&self, elements: &[ElementId]) -> Vec<String> {
        elements
            .iter()
            .filter_map(|e| self.host.root.widget::<MarkupLabel>(*e))
            .map(|l| l.text.clone())
            .collect()
    }

    fn top(&self, element: ElementId) -> f32 {
        self.host.root.element(element).unwrap().top
    }

    fn take(&mut self) -> Vec<UiInput> {
        self.outbox.borrow_mut().drain(..).collect()
    }

    fn click(&mut self, element: ElementId, button: MouseButton) {
        let (x, y) = self.host.root.screen_position(element);
        let (x, y) = (x as i32 + 4, y as i32 + 4);
        self.host.mouse_down(button, x, y, 0);
        self.host.mouse_up(button, x, y, 0);
    }

    fn notices(&mut self) -> Vec<String> {
        self.controller(PluginPanelController::take_notices)
    }
}

fn action(panel: u32, handler: &str, value: ActionValue) -> UiInput {
    UiInput::Action {
        panel,
        handler: handler.to_owned(),
        value,
    }
}

const LABEL_TEMPLATE: &str = "<repeat items=\"{Rows}\"><label text=\"{Label}\"/></repeat>";
const KEYED: &str =
    "<repeat items=\"{Rows}\" key=\"{Id}\"><label text=\"{Label}\" h=\"20\"/></repeat>";
const KEYLESS: &str = "<repeat items=\"{Rows}\"><label text=\"{Label}\" h=\"20\"/></repeat>";

#[test]
fn one_copy_per_item_in_list_order_after_the_marker() {
    // MarkupRepeatBuildTests.One_copy_per_item_in_list_order_after_the_marker
    for count in [0, 1, 3] {
        let labels = &["a", "b", "c"][..count];
        let m = Rig::new(LABEL_TEMPLATE, vec![("Rows", rows(labels))]);
        let children = m.host.root.children(m.content);
        assert_eq!(children.len(), count + 1);
        assert_eq!(m.labels(&children[1..]), labels);
        assert_eq!(m.labels(&m.items()), labels);
        assert_eq!(m.items().len(), count);
        let marker = m.host.root.element(children[0]).unwrap();
        assert_eq!((marker.width, marker.height), (0.0, 0.0));
        assert!(marker.click_through);
    }
}

#[test]
fn a_copy_reads_its_item_and_calls_back_on_its_item() {
    // MarkupRepeatBuildTests.A_copy_reads_its_item_and_calls_back_on_its_item:
    // a handler in a row names its row, so the plugin calls back on that
    // row's item only.
    let mut m = Rig::new(
        "<repeat items=\"{Rows}\"><button text=\"{Label}\" onclick=\"{Click}\" h=\"20\"/>\
         <checkbox checked=\"{On}\" onchange=\"{SetOn}\" w=\"20\" h=\"16\"/></repeat>",
        vec![(
            "Rows",
            list(vec![
                row("1", "a"),
                row("2", "b"),
                row("3", "c").with("On", UiValue::Bool(true)),
            ]),
        )],
    );
    let copies = m.copies();
    let buttons: Vec<ElementId> = copies.iter().step_by(2).copied().collect();
    let boxes: Vec<ElementId> = copies.iter().skip(1).step_by(2).copied().collect();
    assert_eq!(
        buttons
            .iter()
            .map(|b| m.host.root.widget::<MarkupButton>(*b).unwrap().text.clone())
            .collect::<Vec<_>>(),
        ["a", "b", "c"]
    );
    assert_eq!(
        boxes
            .iter()
            .map(|b| m
                .host
                .root
                .widget::<controls::MarkupCheckbox>(*b)
                .unwrap()
                .state)
            .collect::<Vec<_>>(),
        [Some(false), Some(false), Some(true)]
    );
    m.click(buttons[1], MouseButton::Left);
    m.click(boxes[2], MouseButton::Left);
    let panel = m.panel;
    assert_eq!(
        m.take(),
        [
            action(panel, "Rows[1]/Click", ActionValue::None),
            action(panel, "Rows[2]/SetOn", ActionValue::Bool(false)),
        ]
    );
}

#[test]
fn a_context_menu_in_a_template_belongs_to_its_copy() {
    // MarkupRepeatBuildTests.A_context_menu_in_a_template_belongs_to_its_copy
    let mut m = Rig::new(
        "<repeat items=\"{Rows}\" key=\"{Id}\"><button text=\"{Label}\" w=\"80\" h=\"20\">\
         <contextmenu items=\"Edit|Delete\" onselect=\"{Pick}\"/></button></repeat>",
        vec![("Rows", rows(&["a", "b"]))],
    );
    let buttons = m.copies();
    let menu = m
        .host
        .root
        .element(buttons[1])
        .unwrap()
        .context_menu
        .unwrap();
    assert_eq!(m.host.root.parent(menu), Some(buttons[1]));
    m.click(buttons[1], MouseButton::Right);
    let (x, y) = m.host.root.screen_position(buttons[1]);
    let (x, y) = (x as i32 + 4 + 10, y as i32 + 4 + 3 + 18 + 5);
    m.host.mouse_down(MouseButton::Left, x, y, 0);
    m.host.mouse_up(MouseButton::Left, x, y, 0);
    let panel = m.panel;
    assert_eq!(
        m.take(),
        [action(panel, "Rows[1:b]/Pick", ActionValue::Int(1))]
    );
}

#[test]
fn a_stray_attribute_in_a_template_is_reported_once_for_all_its_rows() {
    // MarkupRepeatBuildTests.A_stray_attribute_in_a_template_is_reported_once_for_all_its_rows
    let mut m = Rig::new(
        "<repeat items=\"{Rows}\"><group w=\"40\" h=\"20\"><label text=\"{Label}\" grow=\"1\"/></group></repeat>",
        vec![("Rows", rows(&["a", "b", "c"]))],
    );
    m.set("Rows", rows(&["a", "b", "c", "d"]));
    m.frame();
    let warnings = m.controller(|c| c.warnings().to_vec());
    assert_eq!(warnings.len(), 1, "{warnings:?}");
    assert!(warnings[0].contains("grow=\"1\""));
}

#[test]
fn repeats_nest_and_inner_bindings_read_the_inner_item() {
    // MarkupRepeatBuildTests.Repeats_nest_and_inner_bindings_read_the_inner_item
    let m = Rig::new(
        "<repeat items=\"{Sections}\" key=\"{Id}\"><group layout=\"column\"><label text=\"{Title}\"/>\
         <repeat items=\"{Rows}\" key=\"{Id}\"><label text=\"{Label}\"/></repeat></group></repeat>",
        vec![(
            "Sections",
            list(vec![section("a", &["1", "2"]), section("b", &["1"])]),
        )],
    );
    let groups = m.copies();
    assert_eq!(groups.len(), 2);
    let first = m.host.root.children(groups[0]);
    assert_eq!(m.labels(&first), ["A", "a1", "a2"]);
    assert_eq!(m.labels(&m.host.root.children(groups[1])), ["B", "b1"]);
    assert!(
        m.host.root.widget::<MarkupLabel>(first[1]).is_none(),
        "the inner marker"
    );
}

#[test]
fn a_repeat_directly_inside_a_repeat_keeps_each_section_together() {
    // MarkupRepeatBuildTests.A_repeat_directly_inside_a_repeat_keeps_each_section_together
    for (tail, order) in [
        ("", vec!["A", "a1", "a2", "B", "b1", "end"]),
        (
            "<label text=\".\"/>",
            vec!["A", "a1", "a2", ".", "B", "b1", ".", "end"],
        ),
    ] {
        let m = Rig::new(
            &format!(
                "<repeat items=\"{{Sections}}\"><label text=\"{{Title}}\"/>\
                 <repeat items=\"{{Rows}}\"><label text=\"{{Label}}\"/></repeat>{tail}</repeat>\
                 <label text=\"end\"/>"
            ),
            vec![(
                "Sections",
                list(vec![section("a", &["1", "2"]), section("b", &["1"])]),
            )],
        );
        let children = m.host.root.children(m.content);
        assert_eq!(m.labels(&children), order);
        assert_eq!(m.labels(&m.items()), order);
    }
}

#[test]
fn a_copy_follows_the_theme() {
    // MarkupRepeatBuildTests.A_copy_follows_the_theme and
    // Nested_repeats_in_a_themed_window_follow_the_theme
    let mut m = Rig::mount(
        "<repeat items=\"{Sections}\"><group layout=\"column\"><label text=\"{Title}\"/>\
         <repeat items=\"{Rows}\"><label text=\"{Label}\"/></repeat></group></repeat>",
        vec![(
            "Sections",
            list(vec![section("a", &["1", "2"]), section("b", &[])]),
        )],
        "h=\"400\" layout=\"column\" theme=\"plugin\"",
        None,
    );
    let inner = *m.host.root.children(m.copies()[0]).last().unwrap();
    m.theme = PluginTheme::Moss;
    m.frame();
    let label = m.host.root.widget::<MarkupLabel>(inner).unwrap();
    assert_eq!(label.text, "a2");
    assert_eq!(
        label.color,
        PluginTheme::Moss.palette().unwrap().text,
        "the inner row takes the theme"
    );
}

#[test]
fn the_first_measurement_includes_the_copies() {
    // MarkupRepeatBuildTests.The_first_measurement_includes_the_copies
    let height = |values: UiValue| {
        let m = Rig::mount(
            LABEL_TEMPLATE,
            vec![("Rows", values)],
            "layout=\"column\"",
            None,
        );
        m.host
            .root
            .element(m.host.root.parent(m.content).unwrap())
            .unwrap()
            .height
    };
    let empty = height(rows(&[]));
    let three = height(rows(&["a", "b", "c"]));
    assert!(three > empty, "{three} > {empty}");
}

#[test]
fn editing_an_item_in_place_rebuilds_nothing() {
    // MarkupRepeatReconcileTests.Editing_an_item_in_place_rebuilds_nothing
    // (and A_new_list_with_the_same_keys_keeps_every_copy_and_reads_the_new_objects)
    let mut m = Rig::new(KEYED, vec![("Rows", rows(&["a", "b"]))]);
    let before = m.copies();
    m.set("Rows", list(vec![row("a", "edited"), row("b", "b")]));
    m.frame();
    assert_eq!(m.copies(), before);
    assert_eq!(m.labels(&m.copies()), ["edited", "b"]);
    // A key changed in place is a change: that item gets a row of its own.
    m.set("Rows", list(vec![row("a", "edited"), row("c", "c")]));
    m.frame();
    let after = m.copies();
    assert_eq!(after[0], before[0]);
    assert_ne!(after[1], before[1]);
    assert!(!m.host.root.exists(before[1]));
}

#[test]
fn a_focused_field_in_a_kept_copy_keeps_its_focus_and_text() {
    // MarkupRepeatReconcileTests.A_focused_field_in_a_kept_copy_keeps_its_focus_and_text
    let mut m = Rig::new(
        "<repeat items=\"{Rows}\" key=\"{Id}\"><field w=\"120\" h=\"20\"/></repeat>",
        vec![("Rows", rows(&["a", "b"]))],
    );
    let field = m.copies()[1];
    m.click(field, MouseButton::Left);
    assert_eq!(m.host.root.keyboard_focus(), Some(field));
    for c in "hal".chars() {
        m.host.char_typed(c);
    }
    m.set("Rows", rows(&["b", "a"]));
    m.frame();
    assert_eq!(m.copies()[0], field);
    assert_eq!(m.host.root.keyboard_focus(), Some(field));
    m.host.char_typed('f');
    assert_eq!(
        m.host
            .root
            .widget::<MarkupField>(field)
            .unwrap()
            .inner
            .text(),
        "half"
    );
}

#[test]
fn a_removed_key_drops_its_copy_its_flex_item_and_its_focus() {
    // MarkupRepeatReconcileTests.A_removed_key_drops_its_copy_its_theme_actions_and_its_focus
    // (a window has no theme actions to drop: the theme is applied at each
    // sync, to the elements there are)
    let mut m = Rig::new(
        "<repeat items=\"{Rows}\" key=\"{Id}\"><group layout=\"row\" h=\"22\">\
         <field w=\"100\" h=\"20\"/><label text=\"{Label}\" grow=\"1\"/></group></repeat>",
        vec![("Rows", rows(&["a", "b", "c"]))],
    );
    let copies = m.copies();
    let field = m.host.root.children(copies[1])[0];
    m.click(field, MouseButton::Left);
    assert_eq!(m.host.root.keyboard_focus(), Some(field));
    m.set("Rows", rows(&["a", "c"]));
    m.frame();
    assert_eq!(m.copies(), [copies[0], copies[2]]);
    assert_eq!(m.items(), [copies[0], copies[2]]);
    assert!(!m.host.root.exists(copies[1]));
    assert_eq!(m.host.root.keyboard_focus(), None);
    m.host.key_down(Key::Up, Modifiers::default());
    assert!(m.take().is_empty(), "the removed row's handlers are gone");
}

#[test]
fn a_reorder_moves_the_copies() {
    // MarkupRepeatReconcileTests.A_reorder_moves_the_copies
    let mut m = Rig::new(KEYED, vec![("Rows", rows(&["a", "b", "c"]))]);
    let before = m.copies();
    m.set("Rows", rows(&["c", "a", "b"]));
    m.frame();
    let expected = [before[2], before[0], before[1]];
    assert_eq!(m.copies(), expected);
    assert_eq!(m.items(), expected);
    assert!(m.top(expected[0]) < m.top(expected[1]) && m.top(expected[1]) < m.top(expected[2]));
}

#[test]
fn a_key_added_in_the_middle_inserts_a_copy_there() {
    // MarkupRepeatReconcileTests.A_key_added_in_the_middle_inserts_a_copy_there
    let mut m = Rig::new(KEYED, vec![("Rows", rows(&["a", "c"]))]);
    let before = m.copies();
    m.set("Rows", rows(&["a", "b", "c"]));
    m.frame();
    let after = m.copies();
    assert_eq!(after.len(), 3);
    assert_eq!(after[0], before[0]);
    assert_eq!(after[2], before[1]);
    assert_eq!(m.labels(&after), ["a", "b", "c"]);
    assert_eq!(m.items(), after);
    assert!(m.top(after[0]) < m.top(after[1]) && m.top(after[1]) < m.top(after[2]));
}

#[test]
fn without_a_key_copies_match_by_index() {
    // MarkupRepeatReconcileTests.Without_a_key_copies_match_by_index
    let mut m = Rig::new(KEYLESS, vec![("Rows", rows(&["a", "b", "c"]))]);
    let before = m.copies();
    m.set("Rows", rows(&["x", "y"]));
    m.frame();
    assert_eq!(m.copies(), before[..2]);
    assert_eq!(m.labels(&m.copies()), ["x", "y"]);
    assert_eq!(m.items(), before[..2]);
    m.set("Rows", rows(&["p", "q", "r", "s"]));
    m.frame();
    let after = m.copies();
    assert_eq!(after[..2], before[..2]);
    assert_eq!(m.labels(&after), ["p", "q", "r", "s"]);
    assert_eq!(m.items(), after);
    assert!(m.top(after[1]) < m.top(after[2]) && m.top(after[2]) < m.top(after[3]));
}

#[test]
fn a_copy_appended_at_the_end_is_laid_out() {
    // MarkupRepeatReconcileTests.A_copy_appended_at_the_end_is_laid_out
    let mut m = Rig::new(
        "<repeat items=\"{Rows}\"><field w=\"100\" h=\"20\"/></repeat>",
        vec![("Rows", rows(&["a"]))],
    );
    m.set("Rows", rows(&["a", "b"]));
    m.frame();
    let fields = m.copies();
    assert_eq!(fields.len(), 2);
    assert_eq!(m.host.root.element(fields[1]).unwrap().height, 20.0);
    assert!(m.top(fields[1]) > m.top(fields[0]));
}

#[test]
fn duplicate_keys_log_once_and_match_by_index_until_the_keys_are_unique_again() {
    // MarkupRepeatReconcileTests.Duplicate_keys_log_once_and_match_by_index_until_the_keys_are_unique_again
    let mut m = Rig::new(KEYED, vec![("Rows", rows(&["a", "b"]))]);
    let before = m.copies();
    let dup = list(vec![row("a", "a1"), row("a", "a2"), row("b", "b1")]);
    m.set("Rows", dup.clone());
    m.frame();
    m.frame();
    m.set("Rows", dup);
    m.frame();
    assert_eq!(
        m.notices(),
        [
            "markup <repeat items=\"{Rows}\">: duplicate or missing keys; rows are matched by position"
        ]
    );
    let indexed = m.copies();
    assert_eq!(indexed[..2], before);
    assert_eq!(m.labels(&indexed), ["a1", "a2", "b1"]);

    // Unique again: keyed matching. The row built for "b" follows "b".
    m.set("Rows", list(vec![row("b", "B"), row("a", "A")]));
    m.frame();
    assert_eq!(m.copies(), [before[1], before[0]]);
    assert_eq!(m.labels(&m.copies()), ["B", "A"]);
    assert_eq!(m.copies(), m.items());

    // A missing key also falls back, and the notice comes back once.
    m.set(
        "Rows",
        list(vec![
            row("b", "B"),
            ItemRecord::new().with("Label", UiValue::Text("N".into())),
        ]),
    );
    m.frame();
    assert_eq!(m.notices().len(), 1);
    assert_eq!(m.copies(), [before[1], before[0]]);
    assert_eq!(m.labels(&m.copies()), ["B", "N"]);
}

const NESTED_IN_GROUP: &str = "<repeat items=\"{Sections}\" key=\"{Id}\"><group layout=\"column\">\
     <label text=\"{Title}\" h=\"20\"/><repeat items=\"{Rows}\" key=\"{Id}\">\
     <label text=\"{Label}\" h=\"20\"/></repeat></group></repeat>";

#[test]
fn nested_repeats_reconcile_at_both_levels() {
    // MarkupRepeatReconcileTests.Nested_repeats_reconcile_at_both_levels
    let a = section("a", &["1", "2"]);
    let b = section("b", &["1"]);
    let mut m = Rig::new(
        NESTED_IN_GROUP,
        vec![("Sections", list(vec![a.clone(), b.clone()]))],
    );
    let groups = m.copies();
    let a_children = m.host.root.children(groups[0]);
    let b_children = m.host.root.children(groups[1]);

    // An inner list change touches only its own section's rows.
    let b2 = b
        .clone()
        .with("Rows", list(vec![row("b1", "b1"), row("b2", "b2")]));
    m.set("Sections", list(vec![a.clone(), b2.clone()]));
    m.frame();
    assert_eq!(m.copies(), groups);
    assert_eq!(m.host.root.children(groups[0]), a_children);
    assert_eq!(m.host.root.children(groups[1])[..3], b_children[..]);
    assert_eq!(
        m.labels(&m.host.root.children(groups[1])),
        ["B", "b1", "b2"]
    );

    // Removing a section removes its inner rows.
    m.set("Sections", list(vec![b2]));
    m.frame();
    assert_eq!(m.copies(), [groups[1]]);
    assert!(a_children.iter().all(|e| !m.host.root.exists(*e)));
}

#[test]
fn a_repeat_directly_inside_a_repeat_moves_and_drops_with_its_section() {
    // MarkupRepeatReconcileTests.A_repeat_directly_inside_a_repeat_moves_and_drops_with_its_section
    let body = "<repeat items=\"{Sections}\" key=\"{Id}\"><label text=\"{Title}\" h=\"20\"/>\
         <repeat items=\"{Rows}\" key=\"{Id}\"><label text=\"{Label}\" h=\"20\"/></repeat></repeat>\
         <label text=\"end\" h=\"20\"/>";
    let a = section("a", &["1", "2"]);
    let b = section("b", &["1"]);
    let mut m = Rig::new(body, vec![("Sections", list(vec![a.clone(), b.clone()]))]);
    let all = |m: &Rig| m.labels(&m.host.root.children(m.content));
    assert_eq!(all(&m), ["A", "a1", "a2", "B", "b1", "end"]);

    m.set("Sections", list(vec![b.clone(), a.clone()]));
    m.frame();
    assert_eq!(all(&m), ["B", "b1", "A", "a1", "a2", "end"]);
    assert_eq!(m.labels(&m.items()), ["B", "b1", "A", "a1", "a2", "end"]);

    let a2 = a.with("Rows", list(vec![row("a2", "a2")]));
    let c = section("c", &["1"]);
    m.set("Sections", list(vec![b, a2, c.clone()]));
    m.frame();
    assert_eq!(all(&m), ["B", "b1", "A", "a2", "C", "c1", "end"]);
    assert_eq!(
        m.labels(&m.items()),
        ["B", "b1", "A", "a2", "C", "c1", "end"]
    );

    m.set("Sections", list(vec![c]));
    m.frame();
    assert_eq!(all(&m), ["C", "c1", "end"]);
    assert_eq!(m.labels(&m.items()), ["C", "c1", "end"]);
}

const EXPANDERS: &str = "<repeat items=\"{Rows}\" key=\"{Id}\">\
     <expander name=\"section\" open=\"false\" h=\"60\" text=\"{Label}\"/></repeat>";

fn toggle(m: &mut Rig, expander: ElementId) {
    m.host
        .root
        .with::<MarkupExpander, _>(expander, |x, ui| x.toggle(ui, expander));
}

fn expanded(m: &mut Rig, expander: ElementId) -> bool {
    m.host
        .root
        .widget_mut::<MarkupExpander>(expander)
        .unwrap()
        .expanded()
}

#[test]
fn a_named_expander_in_a_keyed_template_is_remembered_per_key() {
    // MarkupRepeatReconcileTests.A_named_expander_in_a_keyed_template_is_remembered_per_key
    // and MarkupRepeatBuildTests.A_keyed_copy_remembers_its_named_controls_under_its_key
    let s = store();
    let mut m = Rig::mount(
        EXPANDERS,
        vec![("Rows", rows(&["a", "b"]))],
        "h=\"400\" layout=\"column\"",
        Some(Rc::clone(&s)),
    );
    let b = m.copies()[1];
    toggle(&mut m, b);
    assert_eq!(
        remembered(&s, "section#b", "expanded").as_deref(),
        Some("true")
    );
    assert_eq!(remembered(&s, "section#a", "expanded"), None);
    assert_eq!(remembered(&s, "section", "expanded"), None);

    let mut rebuilt = Rig::mount(
        EXPANDERS,
        vec![("Rows", rows(&["a", "b"]))],
        "h=\"400\" layout=\"column\"",
        Some(Rc::clone(&s)),
    );
    let copies = rebuilt.copies();
    assert!(!expanded(&mut rebuilt, copies[0]));
    assert!(expanded(&mut rebuilt, copies[1]));

    // A row built later reads its key's state too.
    let mut later = Rig::mount(
        EXPANDERS,
        vec![("Rows", rows(&["a"]))],
        "h=\"400\" layout=\"column\"",
        Some(Rc::clone(&s)),
    );
    later.set("Rows", rows(&["a", "b"]));
    later.frame();
    let copy = later.copies()[1];
    assert!(expanded(&mut later, copy));

    let mut nested = Rig::mount(
        "<repeat items=\"{Sections}\" key=\"{Id}\"><group layout=\"column\">\
         <repeat items=\"{Rows}\" key=\"{Id}\"><expander name=\"inner\" open=\"false\" h=\"60\" text=\"{Label}\"/>\
         </repeat></group></repeat>",
        vec![(
            "Sections",
            list(vec![
                ItemRecord::new()
                    .with("Id", UiValue::Text("o".into()))
                    .with("Rows", rows(&["i"])),
            ]),
        )],
        "h=\"400\" layout=\"column\"",
        Some(Rc::clone(&s)),
    );
    let inner = nested.host.root.children(nested.copies()[0])[1];
    toggle(&mut nested, inner);
    assert_eq!(
        remembered(&s, "inner#o/i", "expanded").as_deref(),
        Some("true")
    );
}

#[test]
fn a_removed_key_forgets_its_remembered_state() {
    // MarkupRepeatReconcileTests.A_removed_key_forgets_its_remembered_state_in_one_call
    let s = store();
    remember(&s, "other", "expanded", "true");
    let mut m = Rig::mount(
        EXPANDERS,
        vec![("Rows", rows(&["a", "b", "c"]))],
        "h=\"400\" layout=\"column\"",
        Some(Rc::clone(&s)),
    );
    for copy in m.copies() {
        toggle(&mut m, copy);
    }
    m.set("Rows", rows(&["b"]));
    m.frame();
    assert_eq!(remembered(&s, "other", "expanded").as_deref(), Some("true"));
    assert_eq!(
        remembered(&s, "section#b", "expanded").as_deref(),
        Some("true")
    );
    assert_eq!(remembered(&s, "section#a", "expanded"), None);
    assert_eq!(remembered(&s, "section#c", "expanded"), None);
}

#[test]
fn a_removed_section_forgets_its_nested_rows_state_and_a_removed_row_only_its_own() {
    // MarkupRepeatReconcileTests.A_removed_section_forgets_its_nested_rows_state_and_a_removed_row_only_its_own
    let s = store();
    let sec = |id: &str, inner: &[&str]| {
        ItemRecord::new()
            .with("Id", UiValue::Text(id.into()))
            .with("Rows", rows(inner))
    };
    let mut m = Rig::mount(
        "<repeat items=\"{Sections}\" key=\"{Id}\"><group layout=\"column\">\
         <repeat items=\"{Rows}\" key=\"{Id}\"><expander name=\"inner\" open=\"false\" h=\"60\" text=\"{Label}\"/>\
         </repeat></group></repeat>",
        vec![(
            "Sections",
            list(vec![sec("o", &["i", "j"]), sec("p", &["i"])]),
        )],
        "h=\"400\" layout=\"column\"",
        Some(Rc::clone(&s)),
    );
    let expanders: Vec<ElementId> = m
        .copies()
        .iter()
        .flat_map(|g| m.host.root.children(*g))
        .filter(|e| m.host.root.widget::<MarkupExpander>(*e).is_some())
        .collect();
    assert_eq!(expanders.len(), 3);
    for x in expanders {
        toggle(&mut m, x);
    }
    m.set("Sections", list(vec![sec("o", &["i"])]));
    m.frame();
    assert_eq!(
        remembered(&s, "inner#o/i", "expanded").as_deref(),
        Some("true")
    );
    assert_eq!(remembered(&s, "inner#o/j", "expanded"), None);
    assert_eq!(remembered(&s, "inner#p/i", "expanded"), None);
}

#[test]
fn a_removed_key_leaves_another_repeats_state_under_the_same_key() {
    // MarkupRepeatReconcileTests.A_removed_key_leaves_another_repeats_state_under_the_same_key
    let s = store();
    remember(&s, "x#5", "expanded", "true");
    remember(&s, "y#5", "expanded", "true");
    let mut m = Rig::mount(
        "<repeat items=\"{Rows}\" key=\"{Id}\"><expander name=\"x\" open=\"false\" h=\"60\" text=\"{Label}\"/></repeat>\
         <repeat items=\"{Sections}\" key=\"{Id}\"><expander name=\"y\" open=\"false\" h=\"60\" text=\"{Title}\"/></repeat>",
        vec![
            ("Rows", rows(&["5"])),
            ("Sections", list(vec![section("5", &[])])),
        ],
        "h=\"400\" layout=\"column\"",
        Some(Rc::clone(&s)),
    );
    m.set("Rows", rows(&[]));
    m.frame();
    assert_eq!(remembered(&s, "x#5", "expanded"), None);
    assert_eq!(remembered(&s, "y#5", "expanded").as_deref(), Some("true"));
}

#[test]
fn a_key_with_a_slash_is_not_read_as_a_nested_path() {
    // MarkupRepeatReconcileTests.A_key_with_a_slash_is_not_read_as_a_nested_path
    let s = store();
    let mut m = Rig::mount(
        EXPANDERS,
        vec![("Rows", rows(&["a", "a/b"]))],
        "h=\"400\" layout=\"column\"",
        Some(Rc::clone(&s)),
    );
    let copies = m.copies();
    toggle(&mut m, copies[0]);
    toggle(&mut m, copies[1]);
    assert_eq!(
        remembered(&s, "section#a%2Fb", "expanded").as_deref(),
        Some("true")
    );
    m.set("Rows", rows(&["a/b"]));
    m.frame();
    assert_eq!(remembered(&s, "section#a", "expanded"), None);
    assert_eq!(
        remembered(&s, "section#a%2Fb", "expanded").as_deref(),
        Some("true")
    );
}

#[test]
fn with_duplicate_keys_a_removed_copy_whose_key_is_still_listed_keeps_its_state() {
    // MarkupRepeatReconcileTests.With_duplicate_keys_a_removed_copy_whose_key_is_still_listed_keeps_its_state
    let s = store();
    let mut m = Rig::mount(
        EXPANDERS,
        vec![("Rows", rows(&["a", "b", "a"]))],
        "h=\"400\" layout=\"column\"",
        Some(Rc::clone(&s)),
    );
    let copies = m.copies();
    toggle(&mut m, copies[0]);
    toggle(&mut m, copies[1]);
    m.set("Rows", rows(&["a", "a"]));
    m.frame();
    assert_eq!(
        remembered(&s, "section#a", "expanded").as_deref(),
        Some("true")
    );
    assert_eq!(
        remembered(&s, "section#b", "expanded").as_deref(),
        Some("true")
    );
    m.set("Rows", rows(&["a"]));
    m.frame();
    assert_eq!(
        remembered(&s, "section#a", "expanded").as_deref(),
        Some("true")
    );
}

#[test]
fn a_null_or_empty_list_removes_every_copy_and_swapping_back_builds_again() {
    // MarkupRepeatReconcileTests.A_null_or_empty_list_removes_every_copy_and_swapping_back_builds_again
    // (a list the plugin never pushed reads as empty)
    let mut m = Rig::new(KEYED, vec![("Rows", rows(&["a", "b"]))]);
    m.set("Rows", rows(&[]));
    m.frame();
    assert!(m.copies().is_empty());
    assert!(m.items().is_empty());
    m.set("Rows", rows(&["c", "d"]));
    m.frame();
    assert_eq!(m.labels(&m.copies()), ["c", "d"]);
    assert_eq!(m.copies(), m.items());
    let unset = Rig::new(KEYED, Vec::new());
    assert!(unset.copies().is_empty());
}

#[test]
fn a_row_handler_follows_its_row_as_it_moves() {
    // MarkupRepeatReconcileTests.A_new_list_with_the_same_keys_keeps_every_copy_and_reads_the_new_objects:
    // a kept row reads the item now at its key, so its handlers name its
    // new place.
    let mut m = Rig::new(
        "<repeat items=\"{Rows}\" key=\"{Id}\"><button text=\"{Label}\" onclick=\"{Click}\" h=\"20\"/></repeat>",
        vec![("Rows", rows(&["a", "b"]))],
    );
    let b = m.copies()[1];
    m.set("Rows", rows(&["b", "a"]));
    m.frame();
    m.click(b, MouseButton::Left);
    let panel = m.panel;
    assert_eq!(
        m.take(),
        [action(panel, "Rows[0:b]/Click", ActionValue::None)]
    );
}

// OpenAC `MarkupRepeatScaleTests`: least of five rounds, Release, 2026-10-22
// (build 150 rows 7.6 ms, an unchanged frame 0.55 ms, shuffling all 150
// keys 2.9 ms); each bound is 4x the measured value. Here an unchanged
// frame also re-reads every row's values (a sync of a new revision).
const BUILD_BOUND_MS: f64 = 30.4;
const FRAME_BOUND_MS: f64 = 2.2;
const SHUFFLE_BOUND_MS: f64 = 11.6;
const ROW_COUNT: usize = 150;

const SCALE_BODY: &str = r#"<group layout="column" scroll="y" gap="2" grow="1">
  <repeat items="{Rows}" key="{Id}">
    <group layout="row" gap="6" h="22">
      <checkbox checked="{On}" onchange="{SetOn}" w="20"/>
      <label text="{Label}" grow="1"/>
      <menu items="{Choices}" selected="{Picked}" onchange="{Pick}" w="80"/>
      <number value="{Value}" onchange="{SetValue}" w="60"/>
      <button text="Go" onclick="{Press}" w="40"/>
    </group>
  </repeat>
</group>"#;

fn scale_schema() -> BindingSchema {
    BindingSchema::new("Binding").with_items(
        "Rows",
        BindingSchema::new("Row")
            .with("Id", K::Text)
            .with("On", K::Bool)
            .with("Value", K::Int)
            .with("Label", K::Text)
            .with("Choices", K::TextList)
            .with("Picked", K::Text)
            .with("SetOn", K::BoolAction)
            .with("SetValue", K::IntAction)
            .with("Pick", K::TextAction)
            .with("Press", K::Action),
    )
}

fn scale_rows(order: &[usize]) -> UiValue {
    list(
        order
            .iter()
            .map(|&i| {
                ItemRecord::new()
                    .with("Id", UiValue::Text(format!("r{i}")))
                    .with("Label", UiValue::Text(format!("Row {i}")))
                    .with("Value", UiValue::Int(i32::try_from(i).unwrap()))
                    .with("On", UiValue::Bool(false))
                    .with(
                        "Choices",
                        UiValue::TextList(vec!["One".into(), "Two".into(), "Three".into()]),
                    )
                    .with("Picked", UiValue::Text("One".into()))
            })
            .collect(),
    )
}

fn ms(since: std::time::Instant) -> f64 {
    since.elapsed().as_secs_f64() * 1000.0
}

#[test]
#[cfg_attr(
    debug_assertions,
    ignore = "timing: run with `cargo test --release -p ac-ui a_150_row`"
)]
fn a_150_row_repeat_builds_reconciles_and_lays_out_within_bounds() {
    // MarkupRepeatScaleTests.A_150_row_repeat_builds_reconciles_and_lays_out_within_bounds
    let (mut build, mut frame, mut shuffle) = (f64::MAX, f64::MAX, f64::MAX);
    let mut seed: u64 = 12345;
    for _ in 0..5 {
        let order: Vec<usize> = (0..ROW_COUNT).collect();
        let t0 = std::time::Instant::now();
        let mut m = Rig::mount_with(
            scale_schema(),
            SCALE_BODY,
            vec![("Rows", scale_rows(&order))],
            "h=\"300\" layout=\"column\"",
            None,
        );
        build = build.min(ms(t0));
        assert_eq!(m.controller(|c| c.repeat_rows(1).len()), ROW_COUNT);

        m.frame();
        let t0 = std::time::Instant::now();
        m.frame();
        frame = frame.min(ms(t0));

        let mut shuffled = order;
        for i in (1..shuffled.len()).rev() {
            seed = seed
                .wrapping_mul(6_364_136_223_846_793_005)
                .wrapping_add(1_442_695_040_888_963_407);
            let j = usize::try_from(seed >> 33).unwrap() % (i + 1);
            shuffled.swap(i, j);
        }
        m.set("Rows", scale_rows(&shuffled));
        let t0 = std::time::Instant::now();
        m.frame();
        shuffle = shuffle.min(ms(t0));
        assert_eq!(m.controller(|c| c.repeat_rows(1).len()), ROW_COUNT);
    }
    eprintln!("build {build:.2} ms, frame {frame:.2} ms, shuffle {shuffle:.2} ms");
    assert!(build <= BUILD_BOUND_MS, "build {build:.2} ms");
    assert!(frame <= FRAME_BOUND_MS, "frame {frame:.2} ms");
    assert!(shuffle <= SHUFFLE_BOUND_MS, "shuffle {shuffle:.2} ms");
}

#[test]
fn a_150_row_repeat_keeps_every_row_through_a_shuffle() {
    // MarkupRepeatScaleTests.A_150_row_repeat_builds_reconciles_and_lays_out_within_bounds
    // (its counts, in every build): every row is kept and placed in the
    // shuffled order, top to bottom.
    let order: Vec<usize> = (0..ROW_COUNT).collect();
    let mut m = Rig::mount_with(
        scale_schema(),
        SCALE_BODY,
        vec![("Rows", scale_rows(&order))],
        "h=\"300\" layout=\"column\"",
        None,
    );
    let before = m.controller(|c| c.repeat_rows(1));
    assert_eq!(before.len(), ROW_COUNT);
    let reversed: Vec<usize> = order.into_iter().rev().collect();
    m.set("Rows", scale_rows(&reversed));
    m.frame();
    let after = m.controller(|c| c.repeat_rows(1));
    let expected: Vec<Vec<ElementId>> = before.into_iter().rev().collect();
    assert_eq!(after, expected);
    let tops: Vec<f32> = after.iter().map(|r| m.top(r[0])).collect();
    assert!(tops.windows(2).all(|w| w[0] < w[1]), "{tops:?}");
}
