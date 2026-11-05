//! Text selections anchored to the text they were made on.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiTextSelectionAnchorTests.cs`
//! (the chat window's transcript keys are covered in `panels.rs` and the
//! transcript unit tests).
//! OpenAC's line providers are lines and keys set on the widget.

mod common;

use ac_ui::event::{UiEvent, kind};
use ac_ui::root::UiRoot;
use ac_ui::text::{Line, LineKey, Pos, resolve_anchor};
use ac_ui::widgets::Text;
use ac_ui::{Color, ElementId};
use common::*;

fn lines(texts: &[&str]) -> Vec<Line> {
    texts.iter().map(|t| Line::new(*t, Color::WHITE)).collect()
}

fn keys(k: &[(i64, usize)]) -> Vec<LineKey> {
    k.iter()
        .map(|&(source, start)| LineKey { source, start })
        .collect()
}

#[test]
fn an_anchor_finds_its_character_after_everything_shifted() {
    let l = lines(&["newest", "newer", "hello world"]);
    assert_eq!(
        resolve_anchor(&keys(&[(9, 0), (8, 0), (7, 0)]), &l, 7, 6),
        Some(Pos::new(2, 6))
    );
    assert_eq!(
        resolve_anchor(&keys(&[(1, 0)]), &lines(&["hello"]), 1, 5),
        Some(Pos::new(0, 5))
    );
}

#[test]
fn an_anchor_follows_its_character_across_a_rewrap() {
    let narrow = lines(&["hello", "world!"]);
    let p = resolve_anchor(&keys(&[(4, 0), (4, 6)]), &narrow, 4, 8).unwrap();
    assert_eq!(p, Pos::new(1, 2));
    assert_eq!(narrow[p.line].text.chars().nth(p.col), Some('r'));
    let wide = lines(&["hello world!"]);
    let p = resolve_anchor(&keys(&[(4, 0)]), &wide, 4, 8).unwrap();
    assert_eq!(p, Pos::new(0, 8));
}

#[test]
fn an_anchor_reports_text_no_longer_shown_and_clamps_to_what_is() {
    assert_eq!(
        resolve_anchor(&keys(&[(9, 0), (8, 0)]), &lines(&["newest", "newer"]), 7, 0),
        None
    );
    assert_eq!(
        resolve_anchor(&keys(&[(3, 0)]), &lines(&["hi"]), 3, 40),
        Some(Pos::new(0, 2))
    );
    assert_eq!(
        resolve_anchor(&keys(&[(3, 20)]), &lines(&["tail only"]), 3, 4),
        Some(Pos::new(0, 0))
    );
}

struct Transcript {
    ui: UiRoot,
    id: ElementId,
    keyed: bool,
}

impl Transcript {
    fn new(keyed: bool) -> Self {
        let mut ui = root(800.0, 600.0);
        let r = ui.root();
        let id = ui.add(r, at(0.0, 0.0, 400.0, 300.0), Text::default());
        ui.with::<Text, _>(id, |t, ui| {
            t.set_selectable(ui.element_mut(id).unwrap(), true)
        });
        Self { ui, id, keyed }
    }

    fn text(&mut self) -> &mut Text {
        self.ui.widget_mut::<Text>(self.id).unwrap()
    }

    fn set(&mut self, rows: Vec<Line>, row_keys: Vec<LineKey>) {
        let keyed = self.keyed;
        let t = self.text();
        t.set_lines(rows);
        t.line_keys = if keyed { row_keys } else { Vec::new() };
        t.refresh_lines(400.0);
    }

    fn show(&mut self, sources: &[i64]) {
        let rows = sources
            .iter()
            .map(|s| Line::new(format!("message {s}"), Color::WHITE))
            .collect();
        let k = sources
            .iter()
            .map(|&s| LineKey {
                source: s,
                start: 0,
            })
            .collect();
        self.set(rows, k);
    }

    fn show_with_one_unkeyed_trailing_row(&mut self, sources: &[i64]) {
        self.show(sources);
        let t = self.text();
        t.line_keys.pop();
        t.refresh_lines(400.0);
    }

    /// Drag from the top of one row to the top of another; without a font
    /// every hit is column 0, so whole rows are selected.
    fn drag(&mut self, from: i32, to: i32) {
        let id = self.id;
        for (k, row) in [
            (kind::MOUSE_DOWN, from),
            (kind::MOUSE_MOVE, to),
            (kind::MOUSE_UP, to),
        ] {
            let e = UiEvent::new(0, id, k).with_data(0, 0, row * 16);
            self.ui.send_event(id, &e);
        }
    }

    fn selected(&mut self) -> String {
        self.text().selected_text()
    }
}

#[test]
fn a_keyed_selection_survives_new_lines_pushing_it_up() {
    let mut t = Transcript::new(true);
    t.show(&[3, 4, 5, 6]);
    t.drag(1, 3);
    assert_eq!(t.selected(), "message 4\nmessage 5\n");
    t.show(&[4, 5, 6, 7]);
    assert_eq!(t.selected(), "message 4\nmessage 5\n");
}

#[test]
fn a_keyed_selection_tracks_its_text_across_a_rewrap() {
    let mut t = Transcript::new(true);
    t.show(&[1, 2, 3]);
    t.drag(1, 2);
    assert_eq!(t.selected(), "message 2\n");
    t.set(
        lines(&["message 1", "message", " 2", "message 3"]),
        keys(&[(1, 0), (2, 0), (2, 7), (3, 0)]),
    );
    assert_eq!(t.selected(), "message\n 2\n");
}

#[test]
fn a_keyed_selection_goes_away_with_its_text() {
    let mut t = Transcript::new(true);
    t.show(&[3, 4, 5, 6]);
    t.drag(0, 1);
    assert_eq!(t.selected(), "message 3\n");
    t.show(&[7, 8, 9, 10]);
    assert_eq!(t.selected(), "");

    let mut t = Transcript::new(true);
    t.show(&[1, 2, 3]);
    t.drag(0, 2);
    assert_ne!(t.selected(), "");
    t.show(&[]);
    assert_eq!(t.selected(), "");
}

#[test]
fn an_unkeyed_selection_follows_the_row_index() {
    let mut t = Transcript::new(false);
    t.show(&[3, 4, 5, 6]);
    t.drag(1, 3);
    assert_eq!(t.selected(), "message 4\nmessage 5\n");
    t.show(&[4, 5, 6, 7]);
    assert_eq!(t.selected(), "message 5\nmessage 6\n");
}

#[test]
fn a_row_without_identity_borrows_the_nearest() {
    let mut t = Transcript::new(true);
    t.show_with_one_unkeyed_trailing_row(&[1, 2, 3, 4]);
    t.drag(1, 3);
    assert_eq!(t.selected(), "message 2\nmessage 3\n");
    t.show_with_one_unkeyed_trailing_row(&[2, 3, 4, 5]);
    assert_eq!(t.selected(), "message 2\n");
    assert_ne!(t.selected(), "message 3\nmessage 4\n");
}

#[test]
fn ctrl_c_copies_the_selection_to_the_clipboard_and_ctrl_a_selects_all() {
    use ac_ui::event::{Key, Modifiers};
    use ac_ui::root::UiOutput;
    let mut t = Transcript::new(false);
    t.show(&[1, 2]);
    let id = t.id;
    t.ui.set_keyboard_focus(Some(id));
    t.ui.on_key_down(Key::A, Modifiers::CTRL);
    t.ui.drain_output();
    t.ui.on_key_down(Key::C, Modifiers::CTRL);
    let copied: Vec<String> =
        t.ui.drain_output()
            .into_iter()
            .filter_map(|o| match o {
                UiOutput::SetClipboard(s) => Some(s),
                _ => None,
            })
            .collect();
    assert_eq!(copied, ["message 1\nmessage 2"]);
}
