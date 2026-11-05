//! Flex layout, the title bar and content area, and scrolling groups in
//! plugin markup, through the builder, the draw path and the root's input
//! routing. Headless metrics: 7 pixels a character, 14-pixel lines,
//! 24-pixel controls; chrome 5 pixels a side and a 24-pixel bar; scroll
//! bars 16. Ported from OpenAC `tests/AcDream.App.Tests/UI/MarkupFlexTests.cs`,
//! `MarkupFlexWindowTests.cs`, `MarkupTitleBarTests.cs`,
//! `MarkupScrollWindowTests.cs`, `UiScrollGroupTests.cs`,
//! `UiContentOffsetTests.cs` and `UiRootWheelRoutingTests.cs`.

use std::cell::RefCell;
use std::rc::Rc;

use ac_plugin_ui::flex::FlexRect;
use ac_plugin_ui::{BindingKind as K, BindingSchema, PanelDescriptor, PluginUi, UiOwner, UiValue};

use super::layout::{ContentHost, MarkupGroup};
use super::title_bar::TitleBar;
use super::*;
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

const OWNER: &str = "test.flex";

/// A mounted window and the session state behind it.
struct Live {
    state: PluginUi,
    id: u32,
    host: UiHost,
    frame: ElementId,
    name: String,
    revision: u64,
    content: Option<ElementId>,
    title_bar: Option<ElementId>,
}

impl Live {
    fn new(panel: &str, body: &str, schema: BindingSchema, values: Vec<(String, UiValue)>) -> Self {
        Self::try_new(panel, body, schema, values).expect("markup builds")
    }

    fn try_new(
        panel: &str,
        body: &str,
        schema: BindingSchema,
        values: Vec<(String, UiValue)>,
    ) -> Result<Self, String> {
        let mut state = PluginUi::new();
        let markup = format!("<panel x=\"100\" y=\"50\" {panel}>{body}</panel>");
        let id = state
            .register_panel(
                &UiOwner::new(OWNER, "Flex"),
                PanelDescriptor::new("main", "Demo"),
                &markup,
                schema,
            )
            .map_err(|e| e.to_string())?;
        if !values.is_empty() {
            state.set_values(OWNER, id, values).unwrap();
        }
        let view = state.panels().next().unwrap().clone();
        let mut host = UiHost::new();
        host.root.set_size(1280.0, 720.0);
        let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
        let controller = build_panel(&mut host.root, &view, None, outbox);
        let frame = controller.root;
        let name = controller.window_name.clone();
        let (content, title_bar) = (controller.content_root(), controller.title_bar());
        let top = host.root.root();
        host.root.add_child(top, frame);
        host.root
            .register_window(&name, frame, Some(frame), controller.geometry_revision())
            .unwrap();
        host.attach_controller(&name, Box::new(controller)).unwrap();
        let mut live = Self {
            state,
            id,
            host,
            frame,
            name,
            revision: 0,
            content,
            title_bar,
        };
        live.frame();
        Ok(live)
    }

    fn set(&mut self, values: Vec<(String, UiValue)>) {
        self.state.set_values(OWNER, self.id, values).unwrap();
    }

    /// One frame: the tick, the session's values, the draw.
    fn frame(&mut self) {
        self.host.tick(0.0, 0, None);
        let view = self.state.panels().next().unwrap().clone();
        self.revision += 1;
        let revision = self.revision;
        self.host
            .with_controller::<PluginPanelController, _>(&self.name, |c, ui| {
                c.sync(ui, &view, revision, &mut NoIcons, PluginTheme::Classic);
            });
        let _ = self.host.draw((1280.0, 720.0), &crate::draw::NoSprites);
    }

    fn find(&self, name: &str) -> ElementId {
        fn walk(ui: &UiRoot, start: ElementId, name: &str) -> Option<ElementId> {
            if ui.element(start)?.name.as_deref() == Some(name) {
                return Some(start);
            }
            ui.children(start)
                .into_iter()
                .find_map(|c| walk(ui, c, name))
        }
        walk(&self.host.root, self.frame, name).unwrap_or_else(|| panic!("no element {name}"))
    }

    fn rect(&self, name: &str) -> FlexRect {
        let e = self.host.root.element(self.find(name)).unwrap();
        FlexRect::new(e.left, e.top, e.width, e.height)
    }

    fn frame_size(&self) -> (f32, f32) {
        let e = self.host.root.element(self.frame).unwrap();
        (e.width, e.height)
    }

    fn frame_min(&self) -> (f32, f32) {
        let e = self.host.root.element(self.frame).unwrap();
        (e.min_width, e.min_height)
    }

    fn content(&self) -> ElementId {
        self.content.expect("a content area")
    }

    fn title_bar(&self) -> Option<ElementId> {
        self.title_bar
    }

    fn size_of(&self, id: ElementId) -> (f32, f32) {
        let e = self.host.root.element(id).unwrap();
        (e.width, e.height)
    }

    fn group_y(&mut self, name: &str) -> crate::text::Scrollable {
        let id = self.find(name);
        let area = self
            .host
            .root
            .widget::<MarkupGroup>(id)
            .and_then(|g| g.scroll.clone())
            .expect("a scrolling group");
        area.y(&mut self.host.root)
    }

    fn wheel(&mut self, x: i32, y: i32, dx: i32, dy: i32, shift: bool) {
        self.host.mouse_move(x, y);
        self.host.root.on_scroll_xy(dx, dy, shift);
        self.host.pump();
    }
}

fn caption_schema() -> BindingSchema {
    BindingSchema::new("Demo")
        .with("Caption", K::Text)
        .with("Shown", K::Bool)
        .with("First", K::Bool)
        .with("Second", K::Bool)
}

fn text(name: &str, value: &str) -> (String, UiValue) {
    (name.to_owned(), UiValue::Text(value.to_owned()))
}

fn flag(name: &str, value: bool) -> (String, UiValue) {
    (name.to_owned(), UiValue::Bool(value))
}

// ── MarkupFlexTests ─────────────────────────────────────────────────────

#[test]
fn a_row_places_its_items_after_padding_with_gaps() {
    // A_row_places_its_items_after_padding_with_gaps
    let live = Live::new(
        "w=\"300\" h=\"200\"",
        r#"<group name="g" x="10" y="10" w="200" h="40" layout="row" gap="4" padding="5">
             <label name="a" text="AB" /><button name="b" text="OK" />
           </group>"#,
        caption_schema(),
        vec![],
    );
    assert_eq!(live.rect("a"), FlexRect::new(5.0, 5.0, 14.0, 30.0));
    assert_eq!(live.rect("b"), FlexRect::new(23.0, 5.0, 38.0, 30.0));
}

#[test]
fn grow_takes_the_free_space_and_justify_end_packs_a_footer() {
    // Grow_takes_the_free_space_and_justify_end_packs_a_footer
    let schema = caption_schema()
        .with("Lines", K::TextList)
        .with("Selected", K::Int);
    let live = Live::new(
        "w=\"300\" h=\"200\"",
        r#"<group x="0" y="0" w="300" h="200" layout="column">
             <group name="bar" layout="row" gap="4">
               <field name="search" grow="1" /><button name="go" text="Go" />
             </group>
             <list name="list" grow="1" items="{Lines}" selected="{Selected}" />
             <group name="footer" layout="row" justify="end" gap="4">
               <button name="ok" text="OK" /><button name="cancel" text="Cancel" />
             </group>
           </group>"#,
        schema,
        vec![(
            "Lines".into(),
            UiValue::TextList(vec!["one".into(), "two".into()]),
        )],
    );
    assert_eq!(live.rect("bar"), FlexRect::new(0.0, 0.0, 300.0, 24.0));
    assert_eq!(live.rect("search"), FlexRect::new(0.0, 0.0, 258.0, 24.0));
    assert_eq!(live.rect("go"), FlexRect::new(262.0, 0.0, 38.0, 24.0));
    assert_eq!(live.rect("list"), FlexRect::new(0.0, 24.0, 300.0, 152.0));
    assert_eq!(live.rect("footer"), FlexRect::new(0.0, 176.0, 300.0, 24.0));
    assert_eq!(live.rect("ok"), FlexRect::new(192.0, 0.0, 38.0, 24.0));
    assert_eq!(live.rect("cancel"), FlexRect::new(234.0, 0.0, 66.0, 24.0));
}

#[test]
fn a_nested_flex_group_is_laid_out_by_its_root() {
    // A_nested_flex_group_is_laid_out_by_its_root
    let live = Live::new(
        "w=\"300\" h=\"200\"",
        r#"<group name="root" x="0" y="0" w="100" h="200" layout="column" align="start">
             <group name="grid" layout="row" wrap="true" gap="4">
               <icon name="i0" did="1" /><icon name="i1" did="1" /><icon name="i2" did="1" />
             </group>
             <label name="after" text="A" />
           </group>"#,
        caption_schema(),
        vec![],
    );
    assert_eq!(live.rect("grid"), FlexRect::new(0.0, 0.0, 100.0, 68.0));
    assert_eq!(live.rect("i2"), FlexRect::new(0.0, 36.0, 32.0, 32.0));
    assert_eq!(live.rect("after"), FlexRect::new(0.0, 68.0, 7.0, 14.0));
}

#[test]
fn an_absolute_group_in_flex_is_its_authored_size_and_its_children_follow_their_anchors() {
    // An_absolute_group_in_flex_is_its_authored_size_and_its_children_follow_their_anchors
    let live = Live::new(
        "w=\"300\" h=\"200\"",
        r#"<group x="0" y="0" w="300" h="50" layout="row">
             <group name="abs" w="100" h="40" grow="1">
               <button name="pin" x="70" y="0" w="20" h="20" anchor="right top" />
             </group>
             <label text="AB" />
           </group>"#,
        caption_schema(),
        vec![],
    );
    assert_eq!(live.rect("abs").width, 286.0);
    assert_eq!(live.rect("pin").x, 256.0);
}

#[test]
fn a_hidden_item_takes_no_space_and_showing_it_reflows() {
    // A_hidden_item_takes_no_space_and_showing_it_reflows
    let mut live = Live::new(
        "w=\"300\" h=\"200\"",
        r#"<group x="0" y="0" w="300" h="40" layout="row" gap="4">
             <button name="a" text="AB" visible="{Shown}" /><button name="b" text="OK" />
           </group>"#,
        caption_schema(),
        vec![flag("Shown", false)],
    );
    assert_eq!(live.rect("b").x, 0.0);
    live.set(vec![flag("Shown", true)]);
    live.frame();
    assert_eq!(live.rect("b").x, 38.0 + 4.0);
}

#[test]
fn a_longer_caption_reflows_its_row() {
    // A_longer_caption_reflows_its_row
    let mut live = Live::new(
        "w=\"300\" h=\"200\"",
        r#"<group x="0" y="0" w="300" h="40" layout="row">
             <button name="a" text="{Caption}" /><button name="b" text="OK" />
           </group>"#,
        caption_schema(),
        vec![text("Caption", "AB")],
    );
    assert_eq!(live.rect("b").x, 38.0);
    live.set(vec![text("Caption", "ABCD")]);
    live.frame();
    assert_eq!(live.rect("b").x, 52.0);
}

#[test]
fn a_label_in_flex_keeps_the_size_it_is_given() {
    // A_label_in_flex_keeps_the_size_it_is_given
    let mut live = Live::new(
        "w=\"300\" h=\"200\"",
        r#"<group x="0" y="0" w="300" h="40" layout="row">
             <label name="l" text="AB" grow="1" />
           </group>"#,
        caption_schema(),
        vec![],
    );
    live.frame();
    assert_eq!(live.rect("l"), FlexRect::new(0.0, 0.0, 300.0, 40.0));
}

#[test]
fn coordinates_on_a_flex_item_fail_the_build() {
    // Coordinates_on_a_flex_item_fail_the_build
    let error = Live::try_new(
        "w=\"300\" h=\"200\"",
        r#"<group x="0" y="0" w="300" h="40" layout="row"><label x="4" text="A" /></group>"#,
        caption_schema(),
        vec![],
    )
    .err()
    .expect("the build fails");
    assert!(
        error.contains("x=\"4\"") && error.contains("flex item"),
        "{error}"
    );
}

// ── MarkupFlexWindowTests / MarkupTitleBarTests ─────────────────────────

const BODY: &str = r#"<button text="OK" /><label text="ABCD" />"#;

#[test]
fn a_layout_root_has_the_title_bar_unless_it_says_otherwise() {
    // A_layout_root_has_the_title_bar_unless_it_says_otherwise
    for (attrs, bar) in [
        ("layout=\"column\"", true),
        ("layout=\"column\" titlebar=\"true\"", true),
        ("layout=\"column\" titlebar=\"false\"", false),
    ] {
        let live = Live::new(attrs, BODY, caption_schema(), vec![]);
        assert_eq!(live.title_bar().is_some(), bar, "{attrs}");
        let content = live.host.root.element(live.content()).unwrap();
        assert_eq!(content.left, 5.0);
        assert_eq!(content.top, if bar { 24.0 } else { 5.0 });
    }
}

#[test]
fn a_root_without_a_size_opens_at_its_content_size_plus_chrome() {
    // A_root_without_a_size_opens_at_its_content_size_plus_chrome
    let live = Live::new(
        "layout=\"column\" gap=\"4\" padding=\"8\"",
        BODY,
        caption_schema(),
        vec![],
    );
    assert_eq!(live.size_of(live.content()), (86.0, 58.0));
    assert_eq!(live.frame_size(), (96.0, 87.0));
    assert_eq!(live.frame_min(), (96.0, 87.0));
    let bar = live.title_bar().unwrap();
    assert_eq!(live.size_of(bar).0, 96.0);
    let close = live.host.root.widget::<TitleBar>(bar).unwrap().close;
    assert_eq!(live.host.root.element(close).unwrap().left, 96.0 - 24.0);
}

#[test]
fn without_the_bar_the_chrome_is_the_border_alone() {
    // Without_the_bar_the_chrome_is_the_border_alone
    let live = Live::new(
        "layout=\"column\" titlebar=\"false\" gap=\"4\" padding=\"8\"",
        BODY,
        caption_schema(),
        vec![],
    );
    assert_eq!(live.frame_size(), (64.0, 68.0));
}

#[test]
fn an_authored_size_is_kept_and_children_are_laid_out_in_the_content_area() {
    // An_authored_size_is_kept_and_children_are_laid_out_in_the_content_area
    let live = Live::new(
        "layout=\"row\" w=\"300\" h=\"100\" padding=\"8\"",
        r#"<button name="ok" text="OK" /><label text="ABCD" />"#,
        caption_schema(),
        vec![],
    );
    assert_eq!(live.frame_size(), (310.0, 129.0));
    assert_eq!(live.rect("ok"), FlexRect::new(8.0, 8.0, 38.0, 84.0));
    assert_eq!(
        live.host.root.screen_position(live.find("ok")),
        (100.0 + 5.0 + 8.0, 50.0 + 24.0 + 8.0)
    );
}

#[test]
fn an_authored_size_below_the_content_minimum_opens_at_the_minimum() {
    // An_authored_size_below_the_content_minimum_opens_at_the_minimum,
    // Authored_minimums_win_over_the_content_minimum
    let live = Live::new(
        "layout=\"row\" w=\"20\" h=\"10\"",
        BODY,
        caption_schema(),
        vec![],
    );
    assert_eq!(live.frame_size(), (96.0, 53.0));
    assert_eq!(live.frame_min(), (96.0, 53.0));
    let live = Live::new(
        "layout=\"row\" w=\"300\" h=\"100\" minw=\"30\" minh=\"200\"",
        BODY,
        caption_schema(),
        vec![],
    );
    assert_eq!(live.frame_min(), (96.0, 229.0));
    assert_eq!(live.frame_size(), (310.0, 229.0));
}

#[test]
fn a_longer_caption_raises_the_minimum_and_grows_a_registered_window_and_shrinks_back() {
    // A_longer_caption_raises_the_minimum_and_grows_a_registered_window,
    // A_window_grown_by_a_caption_goes_back_when_the_caption_shortens
    let mut live = Live::new(
        "layout=\"row\" w=\"120\" h=\"40\"",
        r#"<button text="{Caption}" />"#,
        caption_schema(),
        vec![text("Caption", "AB")],
    );
    assert_eq!(live.frame_size().0, 130.0);
    live.set(vec![text("Caption", "ABCDEFGHIJKLMNOPQRST")]);
    live.frame();
    live.frame();
    assert_eq!(live.frame_size().0, 174.0);
    live.set(vec![text("Caption", "ABCDEFGHIJKLMNOP")]);
    live.frame();
    live.frame();
    assert_eq!(live.frame_size().0, 146.0);
    live.set(vec![text("Caption", "AB")]);
    live.frame();
    live.frame();
    assert_eq!(live.frame_size().0, 130.0);
}

#[test]
fn the_first_measurement_skips_a_page_whose_bound_visible_is_false() {
    // The_first_measurement_skips_a_page_whose_bound_visible_is_false
    let live = Live::new(
        "layout=\"column\" padding=\"8\"",
        r#"<group name="first" layout="column" w="100" h="300" minh="300" visible="{First}" />
           <group name="second" layout="column" w="100" h="500" minh="500" visible="{Second}" />"#,
        caption_schema(),
        vec![flag("First", true), flag("Second", false)],
    );
    assert_eq!(live.size_of(live.content()).1, 316.0);
}

#[test]
fn the_bar_shows_the_markup_title_else_the_registration_title() {
    // TheBarShowsTheMarkupTitleElseTheRegistrationTitle
    for (attrs, title) in [
        ("layout=\"row\" title=\"Mine\"", "Mine"),
        ("layout=\"row\"", "Demo"),
    ] {
        let live = Live::new(attrs, BODY, caption_schema(), vec![]);
        let bar = live.title_bar().unwrap();
        assert_eq!(live.host.root.widget::<TitleBar>(bar).unwrap().title, title);
    }
}

#[test]
fn the_revision_comes_from_the_markup_not_the_content() {
    // The_revision_comes_from_the_markup_not_the_content
    let revision = |attrs: &str, caption: &str| {
        let live = Live::new(
            attrs,
            r#"<label text="{Caption}" />"#,
            caption_schema(),
            vec![text("Caption", caption)],
        );
        live.host
            .root
            .window(&live.name)
            .unwrap()
            .authored_geometry_revision
    };
    assert_eq!(
        revision("layout=\"row\"", "AB"),
        revision("layout=\"row\"", "ABCDEFGH")
    );
    assert_ne!(
        revision("layout=\"row\"", "AB"),
        revision("layout=\"column\"", "AB")
    );
    let inputs = ac_plugin_ui::chrome::authored_inputs(|n| (n == "layout").then_some("row"));
    assert_eq!(
        revision("layout=\"row\"", "AB"),
        ac_plugin_ui::chrome::authored_geometry_revision(&inputs)
    );
}

#[test]
fn the_close_button_closes_the_window() {
    // MarkupTitleBarTests: the close button closes the window as the player would.
    let mut live = Live::new("layout=\"row\"", BODY, caption_schema(), vec![]);
    assert!(live.host.root.is_window_visible(&live.name));
    let bar = live.title_bar().unwrap();
    let close = live.host.root.widget::<TitleBar>(bar).unwrap().close;
    let (x, y) = live.host.root.screen_position(close);
    live.host.mouse_down(
        crate::event::MouseButton::Left,
        x as i32 + 4,
        y as i32 + 4,
        0,
    );
    live.host.mouse_up(
        crate::event::MouseButton::Left,
        x as i32 + 4,
        y as i32 + 4,
        0,
    );
    assert!(!live.host.root.is_window_visible(&live.name));
}

// ── UiScrollGroupTests / MarkupScrollWindowTests ────────────────────────

fn scroll_live(body: &str) -> Live {
    Live::new("w=\"300\" h=\"200\"", body, caption_schema(), vec![])
}

#[test]
fn content_that_fits_shows_no_bar_and_overflow_shows_it_beside_the_viewport() {
    // Content_that_fits_shows_no_bar,
    // Absolute_content_past_the_bottom_shows_the_vertical_bar_beside_the_viewport
    let mut live = scroll_live(
        r#"<group name="g" x="0" y="0" w="100" h="100" scroll="y">
             <button x="0" y="0" w="80" h="24" text="OK" />
           </group>"#,
    );
    let g = live.find("g");
    assert_eq!(
        live.host.root.element(g).unwrap().content_viewport,
        Some((100.0, 100.0))
    );
    assert_eq!(live.group_y("g").content_height, 24);
    let mut live2 = scroll_live(
        r#"<group name="g" x="0" y="0" w="100" h="100" scroll="y">
             <button x="0" y="150" w="50" h="24" text="OK" />
           </group>"#,
    );
    let g = live2.find("g");
    let y = live2.group_y("g");
    assert_eq!((y.content_height, y.view_height), (174, 100));
    assert_eq!(
        live2.host.root.element(g).unwrap().content_viewport,
        Some((84.0, 100.0))
    );
    let bar = live2
        .host
        .root
        .widget::<MarkupGroup>(g)
        .unwrap()
        .scroll
        .as_ref()
        .unwrap()
        .vertical
        .unwrap();
    let e = live2.host.root.element(bar).unwrap();
    assert!(e.visible);
    assert_eq!((e.left, e.top, e.width, e.height), (84.0, 0.0, 16.0, 100.0));
    live.frame();
}

#[test]
fn children_at_negative_coordinates_are_not_reachable() {
    // Children_at_negative_coordinates_are_not_reachable
    let mut live = scroll_live(
        r#"<group name="g" x="0" y="0" w="100" h="100" scroll="y">
             <button x="0" y="-60" w="50" h="24" text="Up" />
             <button x="0" y="60" w="50" h="24" text="Down" />
           </group>"#,
    );
    assert_eq!(live.group_y("g").content_height, 84);
}

#[test]
fn a_right_anchored_child_moves_in_beside_the_bar() {
    // A_right_anchored_child_moves_in_beside_the_bar
    let live = scroll_live(
        r#"<group name="g" x="0" y="0" w="100" h="100" scroll="y">
             <button name="r" x="70" y="0" w="30" h="24" anchor="top right" text="R" />
             <button x="0" y="200" w="30" h="24" text="Far" />
           </group>"#,
    );
    assert_eq!(live.rect("r").x, 54.0);
}

#[test]
fn the_wheel_scrolls_a_group_by_three_lines_and_moves_its_content() {
    // The_wheel_scrolls_a_group_by_three_lines_over_empty_space,
    // Scrolling_moves_the_content_in_screen_positions_and_hit_tests_but_not_the_bar
    let mut live = scroll_live(
        r#"<group name="g" x="0" y="0" w="100" h="100" scroll="y">
             <button name="far" x="0" y="250" w="50" h="24" text="Far" />
           </group>"#,
    );
    live.wheel(100 + 5 + 50, 50 + 5 + 50, 0, -1, false);
    assert_eq!(live.group_y("g").scroll_y(), 48);
    let far = live.find("far");
    let g = live.find("g");
    let (gx, gy) = live.host.root.screen_position(g);
    assert_eq!(live.host.root.screen_position(far), (gx, gy + 250.0 - 48.0));
}

#[test]
fn a_group_with_nothing_to_scroll_lets_the_wheel_through() {
    // A_group_with_nothing_to_scroll_lets_the_wheel_through
    let mut live = scroll_live(
        r#"<group name="outer" x="0" y="0" w="100" h="100" scroll="y">
             <group name="inner" x="0" y="0" w="100" h="50" scroll="y" />
             <button x="0" y="250" w="50" h="24" text="Far" />
           </group>"#,
    );
    let (fx, fy) = live.host.root.screen_position(live.frame);
    live.wheel(fx as i32 + 50, fy as i32 + 20, 0, -1, false);
    assert_eq!(live.group_y("outer").scroll_y(), 48);
}

#[test]
fn shift_turns_the_wheel_horizontal() {
    // Shift_turns_a_vertical_step_horizontal,
    // A_horizontal_step_scrolls_a_horizontal_group_and_passes_a_vertical_one
    let mut live = scroll_live(
        r#"<group name="g" x="0" y="0" w="100" h="100" scroll="x">
             <button x="250" y="0" w="50" h="24" text="Far" />
           </group>"#,
    );
    let (fx, fy) = live.host.root.screen_position(live.frame);
    let g = live.find("g");
    let x_of = |live: &mut Live| {
        let area = live
            .host
            .root
            .widget::<MarkupGroup>(g)
            .unwrap()
            .scroll
            .clone()
            .unwrap();
        area.x(&mut live.host.root).scroll_y()
    };
    live.wheel(fx as i32 + 50, fy as i32 + 50, 0, -1, false);
    assert_eq!(x_of(&mut live), 0);
    live.wheel(fx as i32 + 50, fy as i32 + 50, 0, -1, true);
    assert_eq!(x_of(&mut live), 48);
    live.wheel(fx as i32 + 50, fy as i32 + 50, 1, 0, false);
    assert_eq!(x_of(&mut live), 0);
}

#[test]
fn focus_scrolls_a_field_below_the_viewport_into_view() {
    // Focus_scrolls_a_field_below_the_viewport_into_view
    let mut live = scroll_live(
        r#"<group name="g" x="0" y="0" w="200" h="100" scroll="y">
             <field name="f1" x="0" y="0" w="100" h="24" />
             <field name="f2" x="0" y="200" w="100" h="24" />
           </group>"#,
    );
    let (f1, f2) = (live.find("f1"), live.find("f2"));
    live.host.root.set_keyboard_focus(Some(f2));
    assert_eq!(live.group_y("g").scroll_y(), 124);
    live.host.root.set_keyboard_focus(Some(f1));
    assert_eq!(live.group_y("g").scroll_y(), 0);
}

#[test]
fn a_scrolling_flex_root_scrolls_its_content_area() {
    // A_scrolling_flex_root_scrolls_its_content_area
    let live = Live::new(
        "layout=\"column\" w=\"100\" h=\"60\" scroll=\"y\"",
        r#"<button text="A" h="40" /><button text="B" h="40" />"#,
        caption_schema(),
        vec![],
    );
    let content = live.content();
    let host = live.host.root.widget::<ContentHost>(content).unwrap();
    assert!(host.scroll.is_some());
    assert_eq!(
        live.host.root.element(content).unwrap().content_viewport,
        Some((84.0, 60.0))
    );
}

#[test]
fn scroll_on_a_root_without_a_content_area_is_reported_and_ignored() {
    // Scroll_on_a_root_without_a_content_area_is_reported_and_ignored
    let doc = ac_plugin_ui::markup::parse(
        r#"<panel w="100" h="100" scroll="y"></panel>"#,
        &BindingSchema::new("Demo"),
    )
    .unwrap();
    assert_eq!(doc.warnings.len(), 1);
    assert!(doc.warnings[0].contains("scroll=\"y\""));
    assert!(!doc.panel.scroll_y);
}

// ── UiContentOffsetTests ────────────────────────────────────────────────

#[test]
fn hit_testing_follows_the_offset_and_stops_at_the_viewport() {
    // Hit_testing_follows_the_offset_and_stops_at_the_viewport,
    // Content_scrolled_out_of_the_viewport_is_not_hit
    let mut root = UiRoot::new();
    root.set_size(400.0, 400.0);
    let top = root.root();
    let host = root.add(top, Element::at(10.0, 10.0, 100.0, 100.0), Panel::default());
    let child = root.add(host, Element::at(0.0, 80.0, 50.0, 50.0), Panel::default());
    if let Some(e) = root.element_mut(host) {
        e.content_viewport = Some((100.0, 100.0));
        e.content_offset = (0.0, 40.0);
    }
    assert_eq!(root.screen_position(child), (10.0, 50.0));
    assert_eq!(root.pick(20, 60), Some(child));
    // Inside the child's rectangle but below the viewport.
    if let Some(e) = root.element_mut(host) {
        e.content_offset = (0.0, 0.0);
    }
    assert_eq!(root.pick(20, 115), None);
}

// ── RetailWindowManagerMinimumTests ─────────────────────────────────────

fn minimum_window(left: f32, top: f32, w: f32, h: f32, min: (f32, f32)) -> (UiRoot, ElementId) {
    let mut root = UiRoot::new();
    root.set_size(800.0, 600.0);
    let top_id = root.root();
    let mut e = Element::at(left, top, w, h);
    e.min_width = min.0;
    e.min_height = min.1;
    let frame = root.add(top_id, e, Panel::default());
    root.register_window("w", frame, Some(frame), 0).unwrap();
    (root, frame)
}

#[test]
fn a_window_that_cannot_be_resized_still_grows_to_its_minimum() {
    // A_window_that_cannot_be_resized_still_grows_to_its_minimum
    let (mut root, frame) = minimum_window(10.0, 10.0, 100.0, 50.0, (150.0, 80.0));
    assert!(root.enforce_minimum_size("w", 100.0, 50.0));
    let e = root.element(frame).unwrap();
    assert_eq!((e.width, e.height), (150.0, 80.0));
}

#[test]
fn a_window_grown_past_the_screen_edge_moves_back_onto_it() {
    // A_window_grown_past_the_screen_edge_moves_back_onto_it,
    // A_minimum_larger_than_the_screen_takes_the_screen_at_its_origin
    let (mut root, frame) = minimum_window(700.0, 550.0, 50.0, 30.0, (200.0, 100.0));
    root.enforce_minimum_size("w", 50.0, 30.0);
    let e = root.element(frame).unwrap();
    assert_eq!(
        (e.left, e.top, e.width, e.height),
        (600.0, 500.0, 200.0, 100.0)
    );
    let (mut root, frame) = minimum_window(100.0, 100.0, 50.0, 30.0, (1000.0, 100.0));
    root.enforce_minimum_size("w", 50.0, 30.0);
    let e = root.element(frame).unwrap();
    assert_eq!((e.left, e.width), (0.0, 800.0));
}

#[test]
fn a_short_list_inside_a_scrolling_group_lets_the_group_scroll() {
    // A_short_list_inside_a_scrolling_group_lets_the_group_scroll,
    // A_list_inside_a_scrolling_group_scrolls_itself
    let schema = caption_schema()
        .with("Lines", K::TextList)
        .with("Selected", K::Int);
    let rows = |n: usize| UiValue::TextList((0..n).map(|i| i.to_string()).collect());
    for (count, group_scroll) in [(2, 48), (10, 0)] {
        let mut live = Live::new(
            "w=\"300\" h=\"200\"",
            r#"<group name="g" x="0" y="0" w="200" h="100" scroll="y">
                 <list name="l" x="0" y="0" w="150" h="80" items="{Lines}" selected="{Selected}" />
                 <button x="0" y="250" w="50" h="24" text="Far" />
               </group>"#,
            schema.clone(),
            vec![("Lines".into(), rows(count))],
        );
        let (fx, fy) = live.host.root.screen_position(live.frame);
        live.wheel(fx as i32 + 20, fy as i32 + 20, 0, -1, false);
        assert_eq!(live.group_y("g").scroll_y(), group_scroll, "{count} rows");
    }
}
