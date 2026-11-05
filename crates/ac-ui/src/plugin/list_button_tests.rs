//! A list column of buttons: the press, the release that fires it, and
//! what cancels it; hover, pressed and disabled looks; where the button and
//! its caption are drawn. Ported from OpenAC PR #223
//! `tests/AcDream.App.Tests/UI/ListButtonColumnTests.cs`; its markup cases
//! are in `ac-plugin-ui`'s `markup/tests.rs`, and the firing through a
//! plugin window in `tests.rs`.

use std::sync::Arc;

use ac_plugin_ui::markup::{Column, ColumnKind};

use super::widgets::{ButtonLook, Cell, DISABLED_ALPHA, MarkupList};
use crate::draw::{Color, DrawCmd, DrawList, NoSprites, TextureRef};
use crate::event::{MouseButton, UiEvent, kind};
use crate::font::UiFont;
use crate::root::{ElementMessage, UiOutput, UiRoot};
use crate::tree::{Element, ElementId};
use crate::widgets::Panel;

const FACE: Color = Color::rgba(0.2, 0.4, 0.6, 1.0);
const BORDER: Color = Color::rgba(0.7, 0.1, 0.3, 1.0);

fn text_column(width: f32) -> Column {
    Column {
        width,
        auto: false,
        kind: ColumnKind::Text {
            items: "Names".into(),
            colors: None,
            onclick: None,
        },
        header: ac_plugin_ui::markup::ColumnHeader::None,
        sort: ac_plugin_ui::markup::SortKind::None,
        sort_keys: None,
    }
}

fn button_column(width: f32, button_width: f32, button_height: f32) -> Column {
    Column {
        width,
        auto: false,
        kind: ColumnKind::Button {
            items: "Captions".into(),
            onclick: "Act".into(),
            button_width,
            button_height,
        },
        header: ac_plugin_ui::markup::ColumnHeader::None,
        sort: ac_plugin_ui::markup::SortKind::None,
        sort_keys: None,
    }
}

fn rows(names: &[&str], captions: &[&str]) -> Vec<Vec<Cell>> {
    let count = names.len().max(captions.len());
    (0..count)
        .map(|i| {
            vec![
                names
                    .get(i)
                    .map_or(Cell::Empty, |n| Cell::Text((*n).to_owned(), Color::WHITE)),
                captions
                    .get(i)
                    .map_or(Cell::Empty, |c| Cell::Button((*c).to_owned())),
            ]
        })
        .collect()
}

/// A 100 by 60 list of 20-high rows at the screen's corner: names in x
/// 0..50, buttons in 50..100 (OpenAC's `MakeList` and `MakeLiveList`).
struct ListUnderTest {
    ui: UiRoot,
    list: ElementId,
}

impl ListUnderTest {
    fn new(captions: &[&str], button: (f32, f32)) -> Self {
        let names = ["a", "b", "c", "d", "e"];
        Self::with(&names[..captions.len().min(5)], captions, button)
    }

    fn with(names: &[&str], captions: &[&str], button: (f32, f32)) -> Self {
        let mut ui = UiRoot::new();
        ui.set_size(800.0, 600.0);
        let mut list = MarkupList::new(None, 20.0);
        list.columns = vec![text_column(50.0), button_column(50.0, button.0, button.1)];
        list.rows = rows(names, captions);
        list.background = Color::TRANSPARENT;
        list.border = Color::TRANSPARENT;
        list.button_color = FACE;
        list.button_border_color = BORDER;
        let root = ui.root();
        let id = ui.add(root, Element::at(0.0, 0.0, 100.0, 60.0), list);
        let mut t = Self { ui, list: id };
        t.draw();
        t
    }

    fn list(&mut self) -> &mut MarkupList {
        self.ui.widget_mut::<MarkupList>(self.list).unwrap()
    }

    /// The plugin's values changed: the window's copy of the rows follows.
    fn set_rows(&mut self, names: &[&str], captions: &[&str]) {
        self.list().rows = rows(names, captions);
    }

    fn draw(&mut self) -> DrawList {
        self.ui.draw((800.0, 600.0), &NoSprites)
    }

    /// An event straight to the list (OpenAC's `list.OnEvent`).
    fn event(&mut self, k: i32, x: i32, y: i32) {
        let source = self.ui.event_id(self.list);
        let e = UiEvent::new(source, self.list, k).with_data(0, x, y);
        self.ui.send_event(self.list, &e);
    }

    fn scroll(&mut self, notches: i32, x: i32, y: i32) {
        let source = self.ui.event_id(self.list);
        let e = UiEvent::new(source, self.list, kind::SCROLL).with_data(notches, x, y);
        self.ui.send_event(self.list, &e);
    }

    fn look(&mut self, row: usize, column: usize) -> ButtonLook {
        let enabled = self.ui.element(self.list).is_some_and(|e| e.enabled);
        self.list().button_look(row, column, enabled)
    }

    /// The rows whose button fired, in order.
    fn acted(&mut self) -> Vec<usize> {
        self.ui
            .drain_output()
            .into_iter()
            .filter_map(|o| match o {
                UiOutput::Element {
                    message: ElementMessage::ListButtonClicked { column: 1, row },
                    ..
                } => Some(row),
                _ => None,
            })
            .collect()
    }
}

fn fills(list: &DrawList, color: Color) -> Vec<(f32, f32, f32, f32)> {
    list.main
        .iter()
        .filter_map(|c| match c {
            DrawCmd::Fill {
                x,
                y,
                w,
                h,
                color: c,
            } if close(*c, color) => Some((*x, *y, *w, *h)),
            _ => None,
        })
        .collect()
}

fn close(a: Color, b: Color) -> bool {
    (a.r - b.r).abs() < 1e-3
        && (a.g - b.g).abs() < 1e-3
        && (a.b - b.b).abs() < 1e-3
        && (a.a - b.a).abs() < 1e-3
}

fn faded(c: Color) -> Color {
    Color::rgba(c.r, c.g, c.b, c.a * DISABLED_ALPHA)
}

#[test]
fn a_press_and_release_on_a_button_fires_with_the_row_and_selects_nothing() {
    // PressAndReleaseOnAButton_FiresOnclickWithTheRow_WithoutSelecting: row
    // 2 (y 40..60), the button column (x 50..100).
    let mut t = ListUnderTest::new(&["Go", "", "Go"], (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 70, 45);
    t.ui.drain_output();
    assert!(t.acted().is_empty());
    t.event(kind::MOUSE_UP, 72, 47);
    assert_eq!(t.acted(), [2]);
    // The press's own message selects nothing: the column is the button's.
    assert_eq!(
        t.list().press(100.0, 60.0, 70.0, 45.0),
        ac_plugin_ui::list::ColumnPress::Nothing
    );
}

#[test]
fn a_release_off_the_pressed_button_does_not_fire() {
    // ReleaseOffThePressedButton_DoesNotFire.
    let mut t = ListUnderTest::new(&["Go", "", "Go"], (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 70, 5);
    t.event(kind::MOUSE_UP, 70, 45);
    t.event(kind::MOUSE_DOWN, 70, 5);
    t.event(kind::MOUSE_UP, 10, 5);
    assert!(t.acted().is_empty());
}

#[test]
fn an_empty_caption_is_no_button_and_a_click_on_it_does_nothing() {
    // EmptyCaption_IsNoButton_AndAClickOnItDoesNothing.
    let mut t = ListUnderTest::new(&["Go", "", "Go"], (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 70, 25);
    t.event(kind::MOUSE_UP, 70, 25);
    assert!(t.acted().is_empty());
    assert_eq!(
        t.list().press(100.0, 60.0, 70.0, 25.0),
        ac_plugin_ui::list::ColumnPress::Nothing
    );
    assert_eq!(t.look(1, 1), ButtonLook::Normal);
}

#[test]
fn a_button_shows_hover_and_press_only_under_the_pointer() {
    // ButtonCell_ShowsHoverAndPressOnlyOnTheCellUnderThePointer.
    let mut t = ListUnderTest::new(&["Go", "", "Go"], (0.0, 0.0));
    assert!(crate::tree::Widget::receives_hover_mouse_move(t.list()));

    t.event(kind::HOVER_ENTER, 70, 5);
    assert_eq!(t.look(0, 1), ButtonLook::Hovered);
    assert_eq!(t.look(2, 1), ButtonLook::Normal);

    t.event(kind::MOUSE_MOVE, 70, 45);
    assert_eq!(t.look(0, 1), ButtonLook::Normal);
    assert_eq!(t.look(2, 1), ButtonLook::Hovered);

    t.event(kind::MOUSE_DOWN, 70, 45);
    assert_eq!(t.look(2, 1), ButtonLook::Pressed);

    // Dragged off the pressed button: it lets go of its pressed look.
    t.event(kind::MOUSE_MOVE, 70, 5);
    assert_eq!(t.look(2, 1), ButtonLook::Normal);
    assert_eq!(t.look(0, 1), ButtonLook::Normal);

    t.event(kind::MOUSE_UP, 70, 5);
    assert_eq!(t.look(0, 1), ButtonLook::Hovered);

    t.event(kind::HOVER_LEAVE, 0, 0);
    assert_eq!(t.look(0, 1), ButtonLook::Normal);
}

#[test]
fn a_list_without_button_columns_takes_no_hover_moves() {
    // ListWithoutButtonColumns_TakesNoHoverMoves.
    let mut list = MarkupList::new(None, 20.0);
    list.columns = vec![text_column(50.0)];
    assert!(!crate::tree::Widget::receives_hover_mouse_move(&list));
}

#[test]
fn a_disabled_list_shows_its_buttons_disabled_and_fires_none() {
    // DisabledList_ShowsItsButtonsDisabled, DisabledList_DoesNotFire.
    let mut t = ListUnderTest::new(&["Go", "", "Go"], (0.0, 0.0));
    t.ui.set_enabled(t.list, false);
    assert_eq!(t.look(0, 1), ButtonLook::Disabled);
    t.event(kind::MOUSE_DOWN, 70, 5);
    t.event(kind::MOUSE_UP, 70, 5);
    assert!(t.acted().is_empty());
}

#[test]
fn a_list_disabled_while_pressed_does_not_fire_even_if_enabled_again() {
    // ListDisabledWhilePressed_DoesNotFire_EvenIfEnabledAgainBeforeRelease.
    let mut t = ListUnderTest::new(&["Go", "", "Go"], (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 70, 5);
    t.ui.set_enabled(t.list, false);
    t.ui.set_enabled(t.list, true);
    t.event(kind::MOUSE_UP, 70, 5);
    assert!(t.acted().is_empty());
}

#[test]
fn a_classic_button_draws_its_face_inside_its_cell_only_where_it_has_a_caption() {
    // ClassicButtonCell_DrawsAFaceInsideItsCell_OnlyWhereThereIsACaption.
    let mut t = ListUnderTest::new(&["Go", "", "Go"], (0.0, 0.0));
    let faces = fills(&t.draw(), FACE);
    assert!(!faces.is_empty());
    assert!(
        faces
            .iter()
            .all(|&(x, _, w, _)| x >= 50.0 && x + w <= 100.0)
    );
    // Rows 0 and 2 only: nothing in row 1 (y 20..40).
    assert!(faces.iter().any(|&(_, y, _, _)| y < 20.0));
    assert!(faces.iter().any(|&(_, y, _, _)| y > 40.0));
    assert!(!faces.iter().any(|&(_, y, _, h)| y < 39.0 && y + h > 21.0));
}

#[test]
fn a_sized_button_is_centred_in_its_cell_and_only_it_takes_clicks() {
    // SizedButton_IsCentredInItsCell_AndOnlyTheButtonTakesClicks: the cell
    // x 50..100, y 0..20; a 30 by 10 button at x 60..90, y 5..15.
    let mut t = ListUnderTest::with(&["a"], &["Go"], (30.0, 10.0));
    let faces = fills(&t.draw(), FACE);
    assert!(!faces.is_empty());
    assert!(
        faces
            .iter()
            .all(|&(x, y, w, h)| x >= 60.0 && x + w <= 90.0 && y >= 5.0 && y + h <= 15.0)
    );

    // Inside the cell but beside the button: nothing.
    t.event(kind::MOUSE_DOWN, 55, 10);
    t.event(kind::MOUSE_UP, 55, 10);
    t.event(kind::MOUSE_MOVE, 75, 2);
    assert_eq!(t.look(0, 1), ButtonLook::Normal);
    assert!(t.acted().is_empty());

    t.event(kind::MOUSE_DOWN, 75, 10);
    t.event(kind::MOUSE_UP, 75, 10);
    assert_eq!(t.acted(), [0]);
}

#[test]
fn a_sized_button_never_grows_past_its_cell() {
    // SizedButton_NeverGrowsPastItsCell.
    let mut t = ListUnderTest::with(&["a"], &["Go"], (500.0, 500.0));
    let faces = fills(&t.draw(), FACE);
    assert!(!faces.is_empty());
    assert!(
        faces
            .iter()
            .all(|&(x, y, w, h)| x >= 51.0 && x + w <= 99.0 && y >= 1.0 && y + h <= 19.0)
    );
}

// ── A press names the row it went down on ──

const GO5: [&str; 5] = ["Go", "Go", "Go", "Go", "Go"];
const NAMES5: [&str; 5] = ["a", "b", "c", "d", "e"];

#[test]
fn rows_redrawn_with_the_same_content_still_fire() {
    // RowsRedrawnWithTheSameContent_StillFire.
    let mut t = ListUnderTest::new(&GO5, (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 60, 25);
    t.set_rows(&NAMES5, &GO5);
    t.draw();
    t.event(kind::MOUSE_UP, 60, 25);
    assert_eq!(t.acted(), [1]);
}

#[test]
fn a_row_removed_above_the_press_before_release_does_not_fire_for_the_row_that_moved_in() {
    // RowRemovedAboveThePress_BeforeRelease_DoesNotFireForTheRowThatMovedIn.
    let mut t = ListUnderTest::new(&GO5, (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 60, 25); // row 1, "b"
    t.set_rows(&["b", "c", "d", "e"], &["Go", "Go", "Go", "Go"]); // "c" is now row 1
    t.event(kind::MOUSE_UP, 60, 25);
    assert!(t.acted().is_empty());
}

#[test]
fn the_pressed_buttons_caption_changed_before_release_does_not_fire_with_no_draw_between() {
    // PressedButtonsCaptionChangedBeforeRelease_DoesNotFire_EvenWithNoDrawBetween.
    let mut t = ListUnderTest::new(&GO5, (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 60, 25); // row 1
    t.set_rows(&NAMES5, &["Go", "Stop", "Go", "Go", "Go"]);
    t.event(kind::MOUSE_UP, 60, 25);
    assert!(t.acted().is_empty());
}

#[test]
fn the_pressed_buttons_caption_emptied_before_release_does_not_fire() {
    // PressedButtonsCaptionEmptiedBeforeRelease_DoesNotFire.
    let mut t = ListUnderTest::new(&GO5, (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 60, 25); // row 1
    t.set_rows(&NAMES5, &["Go", "", "Go", "Go", "Go"]);
    t.draw();
    t.set_rows(&NAMES5, &GO5);
    t.event(kind::MOUSE_UP, 60, 25);
    assert!(t.acted().is_empty());
}

#[test]
fn another_column_changed_between_press_and_release_still_fires() {
    // OtherColumnChangedBetweenPressAndRelease_StillFires.
    let mut t = ListUnderTest::new(&GO5, (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 60, 25); // row 1
    t.set_rows(&["a", "b 12.3m", "c", "d", "e"], &GO5); // a live column ticks
    t.draw();
    t.set_rows(&["a", "b 11.9m", "c", "d", "e"], &GO5);
    t.event(kind::MOUSE_UP, 60, 25);
    assert_eq!(t.acted(), [1]);
}

#[test]
fn a_caption_changed_while_pressed_drops_the_pressed_look_at_the_next_draw() {
    // ButtonCaptionChangedWhilePressed_DropsThePressedLookAtTheNextDraw.
    let mut t = ListUnderTest::new(&GO5, (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 60, 25);
    assert_eq!(t.look(1, 1), ButtonLook::Pressed);
    t.set_rows(&NAMES5, &["Go", "Stop", "Go", "Go", "Go"]);
    t.draw();
    assert_eq!(t.look(1, 1), ButtonLook::Hovered);
}

#[test]
fn a_list_scrolled_while_pressed_does_not_fire() {
    // ListScrolledWhilePressed_DoesNotFire.
    let mut t = ListUnderTest::new(&GO5, (0.0, 0.0));
    t.event(kind::MOUSE_DOWN, 60, 25); // row 1
    t.scroll(-1, 60, 25);
    // Row 1 is now drawn at y 0..20; release on it there.
    t.event(kind::MOUSE_UP, 60, 5);
    assert!(t.acted().is_empty());
}

#[test]
fn a_wheel_scroll_with_no_pointer_move_moves_the_hover_to_the_button_now_under_it() {
    // WheelScroll_WithNoPointerMove_MovesTheHoverToTheButtonNowUnderThePointer.
    let mut t = ListUnderTest::new(&GO5, (0.0, 0.0));
    t.ui.on_mouse_move(60, 5);
    assert_eq!(t.look(0, 1), ButtonLook::Hovered);
    t.ui.on_scroll(-1);
    assert_eq!(t.look(0, 1), ButtonLook::Normal);
    assert_eq!(t.look(1, 1), ButtonLook::Hovered);
}

#[test]
fn through_the_root_a_press_and_release_fires_once() {
    // ThroughUiRoot_PressAndRelease_FiresOnce.
    let mut t = ListUnderTest::new(&GO5, (0.0, 0.0));
    t.ui.on_mouse_move(60, 45);
    t.ui.on_mouse_down(MouseButton::Left, 60, 45, 0);
    t.ui.on_mouse_up(MouseButton::Left, 60, 45, 0);
    assert_eq!(t.acted(), [2]);
}

#[test]
fn through_the_root_capture_taken_away_clears_the_press() {
    // ThroughUiRoot_CaptureTakenAway_ClearsThePress.
    let mut t = ListUnderTest::new(&GO5, (0.0, 0.0));
    let root = t.ui.root();
    let other = t.ui.add(
        root,
        Element::at(300.0, 300.0, 20.0, 20.0),
        Panel::default(),
    );
    t.ui.on_mouse_move(60, 45);
    t.ui.on_mouse_down(MouseButton::Left, 60, 45, 0);
    assert_eq!(t.ui.captured(), Some(t.list));
    assert_eq!(t.look(2, 1), ButtonLook::Pressed);

    t.ui.set_capture(other);

    assert_ne!(t.look(2, 1), ButtonLook::Pressed);
    t.event(kind::MOUSE_UP, 60, 45);
    assert!(t.acted().is_empty());
}

// ── Drawing ──

#[test]
fn a_disabled_list_draws_its_button_face_and_border_faded() {
    // DisabledList_DrawsItsButtonFaceAndBorderFaded.
    let mut t = ListUnderTest::with(&["a"], &["Go"], (0.0, 0.0));
    t.ui.set_enabled(t.list, false);
    let drawn = t.draw();
    assert!(!fills(&drawn, faded(FACE)).is_empty());
    assert!(!fills(&drawn, faded(BORDER)).is_empty());
    assert!(fills(&drawn, FACE).is_empty());
    assert!(fills(&drawn, BORDER).is_empty());
}

#[test]
fn a_caption_wider_than_its_button_is_cut_at_the_buttons_edges() {
    // CaptionWiderThanItsButton_IsCutAtTheButtonEdges: the cell x 50..100;
    // a 20-wide button at x 65..85; the caption is far wider.
    let font = Arc::new(
        crate::bundled_font::bake(16.0, crate::bundled_font::Weight::Regular)
            .unwrap()
            .create_font(TextureRef::Runtime(1)),
    );
    let mut t = ListUnderTest::with(&[""], &["WWWWWWWWWW"], (20.0, 0.0));
    t.list().font = Some(Arc::clone(&font) as Arc<UiFont>);
    let drawn = t.draw();
    let glyphs: Vec<(f32, f32)> = drawn
        .main
        .iter()
        .filter_map(|c| match c {
            DrawCmd::Glyphs {
                texture: TextureRef::Runtime(1),
                quads,
                ..
            } => Some(quads.iter().map(|q| (q.x, q.x + q.w)).collect::<Vec<_>>()),
            _ => None,
        })
        .flatten()
        .collect();
    assert!(!glyphs.is_empty());
    assert!(
        glyphs
            .iter()
            .all(|&(left, right)| left >= 65.0 - 0.01 && right <= 85.0 + 0.01),
        "{glyphs:?}"
    );
}
