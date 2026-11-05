//! The text entry field.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiFieldTests.cs`. OpenAC's
//! `OnSubmit`/`OnReadOnlyClick` callbacks are [`ElementMessage`]s here (and
//! [`Field::submit`] returns the line); its own key-repeat timer is not
//! ported (the window repeats held keys).

mod common;

use ac_ui::ElementId;
use ac_ui::event::{Key, Modifiers, UiEvent, kind};
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::tree::Element;
use ac_ui::widgets::Field;
use common::*;

fn field_in(ui: &mut UiRoot, field: Field, w: f32, h: f32) -> ElementId {
    let mut e = Element::at(0.0, 0.0, w, h);
    Field::configure(&mut e);
    let r = ui.root();
    ui.add(r, e, field)
}

fn send(ui: &mut UiRoot, id: ElementId, mut e: UiEvent) -> bool {
    e.target = Some(id);
    ui.send_event(id, &e)
}

fn ev(k: i32) -> UiEvent {
    UiEvent {
        kind: k,
        ..UiEvent::default()
    }
}

fn key(k: Key) -> UiEvent {
    UiEvent {
        kind: kind::KEY_DOWN,
        key: k,
        ..UiEvent::default()
    }
}

fn field(ui: &UiRoot, id: ElementId) -> &Field {
    ui.widget::<Field>(id).unwrap()
}

fn messages(ui: &mut UiRoot) -> Vec<ElementMessage> {
    ui.drain_output()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::Element { message, .. } => Some(message),
            _ => None,
        })
        .collect()
}

#[test]
fn insert_char_advances_caret() {
    let mut f = Field::default();
    f.insert_char('h');
    f.insert_char('i');
    assert_eq!(f.text(), "hi");
    assert_eq!(f.caret_pos(), 2);
}

#[test]
fn backspace_deletes_before_caret() {
    let mut f = Field::default();
    for c in "abc".chars() {
        f.insert_char(c);
    }
    f.move_caret(-1);
    f.backspace();
    assert_eq!(f.text(), "ac");
    assert_eq!(f.caret_pos(), 1);
}

#[test]
fn submit_returns_the_line_clears_text_pushes_history() {
    let mut f = Field::default();
    for c in "hello".chars() {
        f.insert_char(c);
    }
    assert_eq!(f.submit().as_deref(), Some("hello"));
    assert_eq!(f.text(), "");
    assert_eq!(f.caret_pos(), 0);
    assert_eq!(f.history_count(), 1);
}

#[test]
fn empty_submit_does_not_fire() {
    let mut f = Field::default();
    assert_eq!(f.submit(), None);
    f.insert_char(' ');
    assert_eq!(f.submit(), None);
    assert_eq!(f.history_count(), 0);
}

#[test]
fn history_up_down_browses_previous_submissions() {
    let mut f = Field::default();
    for c in "first".chars() {
        f.insert_char(c);
    }
    f.submit();
    for c in "second".chars() {
        f.insert_char(c);
    }
    f.submit();
    f.history_prev();
    assert_eq!(f.text(), "second");
    f.history_prev();
    assert_eq!(f.text(), "first");
    f.history_next();
    assert_eq!(f.text(), "second");
    f.history_next();
    assert_eq!(f.text(), "");
}

#[test]
fn history_caps_at_100() {
    let mut f = Field::default();
    for _ in 0..150 {
        f.insert_char('x');
        f.submit();
    }
    assert!(f.history_count() <= 100);
}

#[test]
fn multiline_click_after_text_shrank_since_last_wrap_places_caret_in_new_text() {
    let mut ui = root(400.0, 400.0);
    let mut f = Field::default();
    f.one_line = false;
    f.selectable = true;
    f.set_text(
        "a long inscription that wraps across multiple lines when it \
         is measured with the fallback eight pixel glyph width",
    );
    f.ensure_wrapped_lines_current(120.0);
    f.set_text("hi");
    let id = field_in(&mut ui, f, 120.0, 80.0);
    send(
        &mut ui,
        id,
        UiEvent {
            kind: kind::MOUSE_DOWN,
            data1: 90,
            data2: 60,
            ..UiEvent::default()
        },
    );
    let f = field(&ui, id);
    assert!(f.caret_pos() <= f.text().chars().count());
}

#[test]
fn multiline_click_after_backspaces_since_last_wrap_stays_in_range() {
    let mut ui = root(400.0, 400.0);
    let mut f = Field::default();
    f.one_line = false;
    f.selectable = true;
    f.set_text("wrapped inscription text under edit right now");
    f.ensure_wrapped_lines_current(96.0);
    for _ in 0..30 {
        f.backspace();
    }
    let id = field_in(&mut ui, f, 96.0, 60.0);
    send(
        &mut ui,
        id,
        UiEvent {
            kind: kind::MOUSE_DOWN,
            data1: 80,
            data2: 40,
            ..UiEvent::default()
        },
    );
    let f = field(&ui, id);
    assert!(f.caret_pos() <= f.text().chars().count());
}

#[test]
fn character_filter_rejects_disallowed_input() {
    let mut f = Field::default();
    f.character_filter = Some(Box::new(|c: char| c.is_ascii_digit()));
    f.insert_char('4');
    f.insert_char('x');
    f.insert_char('2');
    assert_eq!(f.text(), "42");
}

#[test]
fn select_all_on_focus_survives_initiating_mouse_down_and_typing_replaces_value() {
    let mut ui = root(400.0, 400.0);
    let mut f = Field::default();
    f.select_all_on_focus = true;
    f.selectable = true;
    f.set_text("17");
    let id = field_in(&mut ui, f, 100.0, 20.0);
    send(&mut ui, id, ev(kind::FOCUS_GAINED));
    send(
        &mut ui,
        id,
        UiEvent {
            kind: kind::MOUSE_DOWN,
            data1: 2,
            ..UiEvent::default()
        },
    );
    ui.widget_mut::<Field>(id).unwrap().insert_char('5');
    assert_eq!(field(&ui, id).text(), "5");
}

#[test]
fn read_only_field_rejects_mutations_but_reports_click() {
    let mut ui = root(400.0, 400.0);
    let id = field_in(&mut ui, Field::default(), 100.0, 20.0);
    ui.with::<Field, _>(id, |f, ui| {
        f.set_text("fixed");
        f.set_editable(ui, id, false);
    });
    {
        let f = ui.widget_mut::<Field>(id).unwrap();
        f.insert_char('!');
        f.backspace();
    }
    ui.drain_output();
    assert!(send(&mut ui, id, ev(kind::CLICK)));
    assert_eq!(field(&ui, id).text(), "fixed");
    assert!(matches!(
        messages(&mut ui).as_slice(),
        [ElementMessage::ReadOnlyClicked]
    ));
    let e = ui.element(id).unwrap();
    assert!(!e.accepts_focus);
    assert!(!e.is_edit_control);
}

#[test]
fn multi_line_field_enter_adds_newline_instead_of_submitting() {
    let mut ui = root(400.0, 400.0);
    let mut f = Field::default();
    f.one_line = false;
    f.insert_char('a');
    let id = field_in(&mut ui, f, 100.0, 60.0);
    send(&mut ui, id, key(Key::Enter));
    send(
        &mut ui,
        id,
        UiEvent {
            kind: kind::CHAR,
            data0: '\r' as i32,
            ..UiEvent::default()
        },
    );
    ui.widget_mut::<Field>(id).unwrap().insert_char('b');
    assert_eq!(field(&ui, id).text(), "a\nb");
    assert!(
        !messages(&mut ui)
            .iter()
            .any(|m| matches!(m, ElementMessage::Submitted(_)))
    );
}

#[test]
fn enter_on_one_line_field_keeps_focus_only_when_asked() {
    for stay in [true, false] {
        let mut ui = root(400.0, 400.0);
        let mut f = Field::default();
        f.stay_focused_after_submit = stay;
        f.insert_char('a');
        let id = field_in(&mut ui, f, 100.0, 20.0);
        ui.set_keyboard_focus(Some(id));
        send(&mut ui, id, key(Key::Enter));
        assert_eq!(ui.keyboard_focus(), stay.then_some(id), "stay {stay}");
        assert!(
            messages(&mut ui)
                .iter()
                .any(|m| matches!(m, ElementMessage::Submitted(s) if s == "a"))
        );
    }
}

#[test]
fn typing_a_space_offers_the_text_to_the_replacer() {
    let mut f = Field::default();
    f.text_replacer = Some(Box::new(|t: &str| {
        (t == "/r ").then(|| "@tell Dww, ".to_owned())
    }));
    for c in "/r ".chars() {
        f.insert_char(c);
    }
    assert_eq!(f.text(), "@tell Dww, ");
    assert_eq!(f.caret_pos(), "@tell Dww, ".len());
}

#[test]
fn a_non_space_character_never_triggers_the_replacer() {
    let calls = std::rc::Rc::new(std::cell::Cell::new(0));
    let c2 = calls.clone();
    let mut f = Field::default();
    f.text_replacer = Some(Box::new(move |_t: &str| {
        c2.set(c2.get() + 1);
        None
    }));
    for c in "/reply".chars() {
        f.insert_char(c);
    }
    assert_eq!(calls.get(), 0);
}

#[test]
fn editing_in_the_middle_of_a_line_is_not_rewritten() {
    let mut f = Field::default();
    f.set_text("/r hello");
    f.move_caret(-5);
    f.text_replacer = Some(Box::new(|_t: &str| Some("@tell Dww, ".to_owned())));
    f.insert_char(' ');
    assert_eq!(f.text(), "/r  hello");
}

#[test]
fn a_replacer_returning_none_leaves_the_text_exactly_as_typed() {
    let mut f = Field::default();
    f.text_replacer = Some(Box::new(|_t: &str| None));
    for c in "hi ".chars() {
        f.insert_char(c);
    }
    assert_eq!(f.text(), "hi ");
}

#[test]
fn escape_is_handled_and_keeps_what_was_typed() {
    let mut ui = root(400.0, 400.0);
    let mut f = Field::default();
    f.set_text("half written");
    let id = field_in(&mut ui, f, 100.0, 20.0);
    ui.set_keyboard_focus(Some(id));
    assert!(send(&mut ui, id, key(Key::Escape)));
    assert_eq!(field(&ui, id).text(), "half written");
    assert_eq!(ui.keyboard_focus(), None);
}

#[test]
fn escape_is_ignored_when_the_field_is_not_editable() {
    let mut ui = root(400.0, 400.0);
    let id = field_in(&mut ui, Field::default(), 100.0, 20.0);
    ui.with::<Field, _>(id, |f, ui| f.set_editable(ui, id, false));
    assert!(send(&mut ui, id, key(Key::Escape)));
}

// ── Beyond OpenAC's file: the clipboard through outputs, typing via the root ──

#[test]
fn copy_cut_and_paste_go_through_outputs() {
    let mut ui = root(400.0, 400.0);
    let mut f = Field::default();
    f.selectable = true;
    f.set_text("hello");
    let id = field_in(&mut ui, f, 100.0, 20.0);
    ui.set_keyboard_focus(Some(id));
    ui.drain_output();
    ui.on_key_down(Key::A, Modifiers::CTRL);
    ui.on_key_down(Key::C, Modifiers::CTRL);
    assert!(
        ui.drain_output()
            .iter()
            .any(|o| matches!(o, UiOutput::SetClipboard(s) if s == "hello"))
    );
    ui.on_key_down(Key::X, Modifiers::CTRL);
    assert_eq!(field(&ui, id).text(), "");
    ui.drain_output();
    ui.on_key_down(Key::V, Modifiers::CTRL);
    let asked = ui
        .drain_output()
        .iter()
        .any(|o| matches!(o, UiOutput::PasteRequest(e) if *e == id));
    assert!(asked);
    ui.paste(id, "a\r\nb\u{7}c");
    assert_eq!(field(&ui, id).text(), "abc");
    assert!(
        messages(&mut ui)
            .iter()
            .any(|m| matches!(m, ElementMessage::TextChanged(t) if t == "abc"))
    );
}

#[test]
fn typed_characters_reach_a_focused_field_through_the_root() {
    let mut ui = root(400.0, 400.0);
    let id = field_in(&mut ui, Field::default(), 100.0, 20.0);
    ui.on_mouse_down(ac_ui::MouseButton::Left, 5, 5, 0);
    ui.on_mouse_up(ac_ui::MouseButton::Left, 5, 5, 0);
    assert_eq!(ui.keyboard_focus(), Some(id));
    for c in "hey".chars() {
        ui.on_char(c);
    }
    ui.on_key_down(Key::Left, Modifiers::NONE);
    ui.on_key_down(Key::Backspace, Modifiers::NONE);
    assert_eq!(field(&ui, id).text(), "hy");
    ui.on_key_down(Key::Enter, Modifiers::NONE);
    assert!(
        messages(&mut ui)
            .iter()
            .any(|m| matches!(m, ElementMessage::Submitted(s) if s == "hy"))
    );
    assert_eq!(ui.keyboard_focus(), None);
}

#[test]
fn max_characters_and_shift_selection() {
    let mut f = Field::default();
    f.max_characters = 3;
    f.selectable = true;
    for c in "abcd".chars() {
        f.insert_char(c);
    }
    assert_eq!(f.text(), "abc");
    f.set_text("abcdef");
    assert_eq!(f.text(), "abc");
    f.select_all_text();
    assert_eq!(f.selected_text(), "abc");
    f.insert_char('z');
    assert_eq!(f.text(), "z");
}
