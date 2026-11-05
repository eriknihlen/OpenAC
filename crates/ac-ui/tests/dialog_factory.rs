//! The retail dialog factory.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/RetailDialogFactoryTests.cs`
//! with its synthetic `BuildDialogLayout` (the fixture cases read the dats
//! in `real_dats`). OpenAC's callbacks and close notices are the closed
//! dialogs the owner takes ([`DialogFactory::take_closed`]), then
//! [`DialogFactory::settle`] moves the queues on (a dialog the owner makes
//! from an answer opens first, as OpenAC's re-entrant callbacks do); a
//! button click is the element message the factory handles. Layouts are
//! made by a closure the owner passes in.

use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::Arc;

use ac_dat::objects::{DIRECT_STATE_ID, PropertyValue, StringInfo};
use ac_ui::element_info::{ElementInfo, StateInfo};
use ac_ui::import::{BuildContext, ImportedLayout, LayoutSource, build};
use ac_ui::panels::dialog_factory::{
    Closed, DEFAULT_QUEUE_KEY, DialogData, DialogFactory, DialogType, DialogValue, property,
};
use ac_ui::root::{ElementMessage, UiRoot};
use ac_ui::tree::ElementId;
use ac_ui::widgets::{Button, Field, Menu, Text};

#[derive(Debug)]
struct NoSource;

impl LayoutSource for NoSource {
    fn layout(&self, _id: u32) -> Option<Arc<ac_dat::objects::LayoutDesc>> {
        None
    }
    fn string(&self, _info: &StringInfo) -> Option<String> {
        None
    }
}

fn info(id: u32, kind: u32, x: f32, y: f32, w: f32, h: f32) -> ElementInfo {
    ElementInfo {
        id,
        kind,
        x,
        y,
        width: w,
        height: h,
        ..ElementInfo::default()
    }
}

/// OpenAC `RetailDialogFactoryTests.BuildDialogLayout`.
fn build_dialog_layout(ui: &mut UiRoot, kind: DialogType) -> ImportedLayout {
    let root_kind = match kind {
        DialogType::Message => 0x17,
        DialogType::ConfirmationTextInput => 0x15,
        DialogType::ConfirmationMenu => 0x14,
        DialogType::Wait => 0x19,
        _ => 0x13,
    };
    let mut root = info(kind.root_element_id(), root_kind, 0.0, 0.0, 800.0, 600.0);
    let popup_h = if kind == DialogType::ConfirmationTextInput {
        125.0
    } else {
        95.0
    };
    let mut popup = info(0x3D, 3, 0.0, 0.0, 400.0, popup_h);
    if kind != DialogType::ConfirmationMenu {
        popup.children.push(info(0x3E, 12, 15.0, 15.0, 370.0, 18.0));
    }
    let button = |id, x, y| info(id, 1, x, y, 80.0, 32.0);
    match kind {
        DialogType::Message => popup.children.push(button(0x26, 160.0, 48.0)),
        DialogType::Confirmation => {
            popup.children.push(button(0x17, 80.0, 48.0));
            popup.children.push(button(0x19, 240.0, 48.0));
        }
        DialogType::ConfirmationTextInput => {
            let mut field = info(0x2C, 12, 4.0, 43.0, 152.0, 16.0);
            let mut direct = StateInfo::named(DIRECT_STATE_ID, "");
            direct.properties.set_value(0x16, PropertyValue::Bool(true));
            field.set_state(DIRECT_STATE_ID, direct);
            popup.children.push(field);
            popup.children.push(button(0x2E, 80.0, 78.0));
            popup.children.push(button(0x2F, 240.0, 78.0));
        }
        DialogType::ConfirmationMenu => {
            popup.children.push(info(0x21, 6, 80.0, 15.0, 240.0, 24.0));
            popup.children.push(button(0x22, 80.0, 48.0));
            popup.children.push(button(0x23, 240.0, 48.0));
        }
        _ => {}
    }
    root.children.push(popup);
    let ctx = BuildContext {
        source: &NoSource,
        default_font: None,
        source_layout_did: 0,
        media_seconds: 0.0,
    };
    build(ui, &root, &ctx)
}

/// The test owner: the factory, the layouts it made, and a catalog switch.
struct Rig {
    ui: UiRoot,
    f: DialogFactory<&'static str>,
    layouts: Rc<RefCell<Vec<(DialogType, ImportedLayout)>>>,
    available: Rc<RefCell<HashMap<DialogType, bool>>>,
    closed: Vec<Closed<&'static str>>,
}

impl Rig {
    fn new(w: f32, h: f32) -> Self {
        let mut ui = UiRoot::new();
        ui.set_size(w, h);
        Self {
            ui,
            f: DialogFactory::new(),
            layouts: Rc::default(),
            available: Rc::default(),
            closed: Vec::new(),
        }
    }

    fn maker(&self) -> impl FnMut(&mut UiRoot, DialogType) -> Option<ImportedLayout> + use<> {
        let layouts = self.layouts.clone();
        let available = self.available.clone();
        move |ui, kind| {
            if !available.borrow().get(&kind).copied().unwrap_or(true) {
                return None;
            }
            let l = build_dialog_layout(ui, kind);
            layouts
                .borrow_mut()
                .push((kind, ImportedLayout::from_parts(l.root, HashMap::new())));
            Some(l)
        }
    }

    fn make(&mut self, data: DialogData, tag: &'static str) -> u32 {
        let mut m = self.maker();
        self.f.make(&mut self.ui, data, tag, &mut m)
    }

    fn confirm(&mut self, message: &str, tag: &'static str) -> u32 {
        self.make(DialogData::confirmation(message), tag)
    }

    fn settle(&mut self) {
        self.closed.extend(self.f.take_closed());
        let mut m = self.maker();
        self.f.settle(&mut self.ui, &mut m);
    }

    fn tick(&mut self) {
        let mut m = self.maker();
        self.f.tick(&mut self.ui, &mut m);
        self.closed.extend(self.f.take_closed());
    }

    fn close(&mut self, context: u32) -> bool {
        let closed = self.f.close(&mut self.ui, context);
        self.settle();
        closed
    }

    fn layout(&self, i: usize) -> ElementId {
        self.layouts.borrow()[i].1.root
    }

    fn count(&self) -> usize {
        self.layouts.borrow().len()
    }

    fn find(&self, root: ElementId, id: u32) -> ElementId {
        self.ui.find_descendant(root, id).unwrap()
    }

    /// Click button `id` of the dialog rooted at `root`, then act as the
    /// owner does (take the answers, move the queues).
    fn click(&mut self, root: ElementId, id: u32) {
        let b = self.find(root, id);
        assert!(
            self.f
                .handle(&mut self.ui, b, &ElementMessage::Clicked { x: 1, y: 1 })
        );
        self.settle();
    }

    fn accept(&mut self, i: usize) {
        self.click(self.layout(i), 0x17);
    }

    fn reject(&mut self, i: usize) {
        self.click(self.layout(i), 0x19);
    }

    fn message(&self, root: ElementId) -> String {
        let m = self.find(root, 0x3E);
        self.ui
            .widget::<Text>(m)
            .unwrap()
            .content_lines()
            .into_iter()
            .map(|l| l.text)
            .collect::<Vec<_>>()
            .join(" ")
    }

    fn modal_message(&self) -> String {
        self.message(self.ui.modal.expect("a modal dialog"))
    }

    fn results(&self, id: u32) -> Vec<String> {
        self.closed
            .iter()
            .map(|c| format!("{}:{:?}", c.tag, c.data.get_bool(id)))
            .collect()
    }
}

#[test]
fn dialog_types_name_their_catalog_roots() {
    for (kind, root) in [
        (DialogType::Confirmation, 0x15),
        (DialogType::Wait, 0x31),
        (DialogType::Message, 0x24),
        (DialogType::TextInput, 0x28),
        (DialogType::ConfirmationTextInput, 0x2C),
        (DialogType::Menu, 0x1B),
        (DialogType::ConfirmationMenu, 0x1F),
    ] {
        assert_eq!(kind.root_element_id(), root);
    }
}

#[test]
fn a_confirmation_is_a_fresh_centred_modal_and_returns_its_result() {
    let mut r = Rig::new(1024.0, 768.0);
    r.confirm("Do you really want to kill your character?", "kill");
    assert_eq!(r.count(), 1);
    let root = r.layout(0);
    assert_eq!(r.ui.modal, Some(root));
    let popup = r.find(root, 0x3D);
    let p = r.ui.element(popup).unwrap();
    assert_eq!(p.left, ((1024.0 - p.width) * 0.5_f32).round());
    assert_eq!(p.top, ((768.0 - p.height) * 0.5_f32).round());
    r.accept(0);
    assert_eq!(r.results(property::CONFIRMATION_RESULT), ["kill:true"]);
    assert_eq!(r.ui.modal, None);
    assert!(!r.f.is_open());
}

#[test]
fn a_menu_question_returns_the_chosen_index_and_a_rejection_minus_one() {
    let mut r = Rig::new(800.0, 600.0);
    let items = ["acdream.keymap".to_owned(), "friends.keymap".to_owned()];
    r.make(DialogData::confirmation_menu(&items, 1), "first");
    let root = r.layout(0);
    let menu = r.find(root, 0x21);
    assert_eq!(r.ui.widget::<Menu>(menu).unwrap().selected, Some(1));
    assert!(r.f.handle(&mut r.ui, menu, &ElementMessage::MenuSelected(0)));
    r.click(root, 0x22);
    assert_eq!(r.closed[0].data.get_i32(property::MENU_SELECTION, 99), 0);

    r.make(DialogData::confirmation_menu(&items[..1], 0), "second");
    let root = r.layout(1);
    r.click(root, 0x23);
    assert_eq!(r.closed[1].data.get_i32(property::MENU_SELECTION, 99), -1);
}

#[test]
fn one_queue_shows_its_dialogs_in_order_each_on_a_fresh_root() {
    let mut r = Rig::new(800.0, 600.0);
    r.confirm("first", "first");
    r.confirm("second", "second");
    assert_eq!(r.f.pending_count(), 1);
    r.reject(0);
    assert_eq!(r.results(property::CONFIRMATION_RESULT), ["first:false"]);
    assert_eq!(r.count(), 2);
    assert_ne!(r.layout(0), r.layout(1));
    assert_eq!(r.ui.modal, Some(r.layout(1)));
    assert_eq!(r.message(r.layout(1)), "second");
    r.accept(1);
    assert_eq!(
        r.results(property::CONFIRMATION_RESULT),
        ["first:false", "second:true"]
    );
    assert!(!r.f.is_open());
}

#[test]
fn a_confirmation_without_a_queue_key_shares_the_default_queue() {
    let mut r = Rig::new(800.0, 600.0);
    r.confirm("omitted", "a");
    r.make(
        DialogData::confirmation("explicit").on_queue(DEFAULT_QUEUE_KEY),
        "b",
    );
    assert_eq!((r.f.active_count(), r.f.pending_count()), (1, 1));
}

#[test]
fn queues_and_dialogs_outside_them_are_open_together() {
    let mut r = Rig::new(800.0, 600.0);
    let first = r.make(DialogData::confirmation("queue two").on_queue(2), "a");
    let second = r.make(DialogData::confirmation("queue three").on_queue(3), "b");
    let third = r.make(DialogData::confirmation("nonqueued").on_queue(1), "c");
    assert_eq!((r.f.active_count(), r.f.pending_count()), (3, 0));
    assert_eq!(r.ui.modal, Some(r.layout(2)));
    assert!(r.close(third));
    assert_eq!(r.ui.modal, Some(r.layout(1)));
    assert!(r.close(second));
    assert_eq!(r.ui.modal, Some(r.layout(0)));
    assert!(r.close(first));
    assert_eq!(r.ui.modal, None);
}

#[test]
fn a_priority_dialog_suspends_the_open_one_which_comes_back_before_older_work() {
    let mut r = Rig::new(800.0, 600.0);
    r.confirm("current", "a");
    r.confirm("ordinary pending", "b");
    r.make(DialogData::confirmation("priority").priority(), "c");
    assert_eq!(r.f.pending_count(), 2);
    assert_eq!(r.message(r.layout(1)), "priority");
    assert!(
        !r.ui.exists(r.layout(0)),
        "the suspended dialog left the screen"
    );
    r.reject(1);
    assert_eq!(r.count(), 3);
    assert_eq!(r.message(r.layout(2)), "current");
    assert_eq!(r.f.pending_count(), 1);
}

#[test]
fn custom_captions_show_and_the_answer_comes_back_once() {
    let mut r = Rig::new(800.0, 600.0);
    let data = DialogData::confirmation("Proceed?")
        .with(property::ACCEPT_LABEL, DialogValue::Str("Yes".into()))
        .with(property::REJECT_LABEL, DialogValue::Str("No".into()));
    r.make(data, "proceed");
    let root = r.layout(0);
    let label = |r: &Rig, id| {
        r.ui.widget::<Button>(r.find(root, id))
            .unwrap()
            .label
            .clone()
    };
    assert_eq!(label(&r, 0x17).as_deref(), Some("Yes"));
    assert_eq!(label(&r, 0x19).as_deref(), Some("No"));
    r.accept(0);
    assert_eq!(r.closed.len(), 1);
}

#[test]
fn a_waiting_dialog_can_be_closed_without_disturbing_the_open_one() {
    let mut r = Rig::new(800.0, 600.0);
    let active = r.confirm("active", "active");
    let pending = r.confirm("pending", "pending");
    assert!(r.close(pending));
    assert_eq!(
        r.closed.iter().map(|c| c.context).collect::<Vec<_>>(),
        [pending]
    );
    assert_eq!(r.f.pending_count(), 0);
    assert_eq!(r.ui.modal, Some(r.layout(0)));
    assert!(r.close(active));
}

#[test]
fn reset_completes_the_open_and_the_waiting_dialogs() {
    let mut r = Rig::new(800.0, 600.0);
    let active = r.confirm("active", "active");
    let pending = r.confirm("pending", "pending");
    r.f.reset(&mut r.ui);
    r.settle();
    let tags: Vec<&str> = r.closed.iter().map(|c| c.tag).collect();
    assert_eq!(tags, ["active", "pending"]);
    let contexts: Vec<u32> = r.closed.iter().map(|c| c.context).collect();
    assert_eq!(contexts, [active, pending]);
    assert!(!r.f.is_open());
    assert_eq!(r.f.pending_count(), 0);
    assert_eq!(r.ui.modal, None);
}

#[test]
fn reset_keeps_the_context_numbers_rising() {
    let mut r = Rig::new(800.0, 600.0);
    let first = r.confirm("active", "a");
    r.confirm("pending", "b");
    r.f.reset(&mut r.ui);
    r.settle();
    assert_eq!(first, 1);
    assert_eq!(r.closed.len(), 2);
    assert!(r.ui.children(r.ui.root()).is_empty());
    assert_eq!(r.confirm("new session", "c"), 3);
}

#[test]
fn a_dialog_made_from_an_answer_opens_before_the_ones_waiting() {
    let mut r = Rig::new(800.0, 600.0);
    r.confirm("stage1", "stage1");
    r.confirm("queued behind chain", "queued");
    assert_eq!(r.f.pending_count(), 1);
    // The owner answers stage1 by asking stage2 (OpenAC's callback).
    let b = r.find(r.layout(0), 0x17);
    assert!(r.f.handle(&mut r.ui, b, &ElementMessage::Clicked { x: 1, y: 1 }));
    r.closed.extend(r.f.take_closed());
    r.confirm("stage2", "stage2");
    r.settle();
    assert_eq!(r.count(), 2);
    assert_eq!(r.message(r.layout(1)), "stage2");
    assert_eq!(r.ui.modal, Some(r.layout(1)));
    assert_eq!(r.f.pending_count(), 1);
    r.accept(1);
    assert_eq!(r.count(), 3);
    assert_eq!(r.message(r.layout(2)), "queued behind chain");
    assert_eq!(r.f.pending_count(), 0);
    r.accept(2);
    let tags: Vec<&str> = r.closed.iter().map(|c| c.tag).collect();
    assert_eq!(tags, ["stage1", "stage2", "queued"]);
    assert!(!r.f.is_open());
}

#[test]
fn a_wait_dialog_is_a_text_only_modal_its_opener_closes() {
    let mut r = Rig::new(1024.0, 768.0);
    let context = r.make(
        DialogData::wait("The next key you press will be mapped.").on_queue(0x1000_0001),
        "wait",
    );
    assert_ne!(context, 0);
    assert_eq!(r.ui.modal, Some(r.layout(0)));
    assert_eq!(r.modal_message(), "The next key you press will be mapped.");
    assert!(r.close(context));
    assert_eq!(r.ui.modal, None);
    assert!(!r.f.is_open());
}

#[test]
fn the_wait_data_has_the_retail_property_shape() {
    let d = DialogData::wait("text");
    assert_eq!(d.get_u32(property::TYPE, 0), DialogType::Wait as u32);
    assert!(d.get_bool(property::ELEMENT_ATTRIBUTE_40));
    assert_eq!(d.get_str(property::MESSAGE), Some("text"));
}

#[test]
fn a_message_dialog_closes_on_its_ok_button() {
    let mut r = Rig::new(800.0, 600.0);
    r.make(DialogData::message("Character selection failed."), "msg");
    assert_eq!(r.layouts.borrow()[0].0, DialogType::Message);
    assert_eq!(r.message(r.layout(0)), "Character selection failed.");
    r.click(r.layout(0), 0x26);
    assert_eq!(r.closed.len(), 1);
    assert!(!r.f.is_open());
    assert_eq!(r.ui.modal, None);
}

#[test]
fn a_text_question_returns_the_typed_text_or_nothing_when_cancelled() {
    let mut r = Rig::new(800.0, 600.0);
    let context = r.make(
        DialogData::confirmation_text_input("Type DELETE."),
        "accepted",
    );
    let root = r.layout(0);
    // The field shares the root's id (0x2C) in the catalog.
    let field = r.f.view(context).unwrap().input().unwrap();
    assert_eq!(r.ui.keyboard_focus(), Some(field));
    r.ui.widget_mut::<Field>(field).unwrap().set_text("delete");
    r.click(root, 0x2E);

    r.make(
        DialogData::confirmation_text_input("Type DELETE."),
        "rejected",
    );
    let root = r.layout(1);
    assert!(r.f.handle(&mut r.ui, root, &ElementMessage::Cancel));
    r.settle();
    let results: Vec<String> = r
        .closed
        .iter()
        .map(|c| {
            c.data
                .get_str(property::TEXT_INPUT_RESULT)
                .unwrap_or("<none>")
                .to_owned()
        })
        .collect();
    assert_eq!(results, ["delete", ""]);
    assert!(!r.f.is_open());
    assert_eq!(r.ui.keyboard_focus(), None);
}

fn catalog_failure_recovers_on_the_next_tick(kind: DialogType, data: DialogData) {
    let mut r = Rig::new(800.0, 600.0);
    r.available.borrow_mut().insert(kind, false);
    let context = r.make(data, "x");
    assert_ne!(context, 0);
    assert_eq!(
        (r.f.active_count(), r.f.pending_count(), r.f.retry_count()),
        (0, 0, 1)
    );
    assert_eq!(r.ui.modal, None);
    assert!(r.ui.children(r.ui.root()).is_empty());
    r.available.borrow_mut().insert(kind, true);
    r.tick();
    assert_eq!(
        (r.f.active_count(), r.f.pending_count(), r.f.retry_count()),
        (1, 0, 0)
    );
    assert!(r.ui.modal.is_some());
    assert!(r.close(context));
    assert!(!r.f.is_open());
}

#[test]
fn a_catalog_failure_does_not_poison_the_queue_and_a_tick_recovers() {
    catalog_failure_recovers_on_the_next_tick(DialogType::Wait, DialogData::wait("Please Wait"));
    catalog_failure_recovers_on_the_next_tick(DialogType::Message, DialogData::message("Error"));
    catalog_failure_recovers_on_the_next_tick(
        DialogType::ConfirmationTextInput,
        DialogData::confirmation_text_input("Type DELETE"),
    );
}

#[test]
fn a_waiting_dialog_that_fails_leaves_the_queue_then_recovers_before_later_work() {
    let mut r = Rig::new(800.0, 600.0);
    r.available.borrow_mut().insert(DialogType::Message, false);
    let active = r.make(DialogData::wait("active"), "a");
    let failed = r.make(DialogData::message("recover me"), "b");
    let later = r.make(DialogData::wait("later"), "c");
    assert_eq!(r.f.pending_count(), 2);
    assert!(r.close(active));
    assert_eq!(
        (r.f.active_count(), r.f.pending_count(), r.f.retry_count()),
        (0, 1, 1)
    );
    assert_eq!(r.ui.modal, None);
    r.available.borrow_mut().insert(DialogType::Message, true);
    r.tick();
    assert_eq!(
        (r.f.active_count(), r.f.pending_count(), r.f.retry_count()),
        (1, 1, 0)
    );
    assert_eq!(r.modal_message(), "recover me");
    assert!(r.close(failed));
    assert_eq!(r.modal_message(), "later");
    assert!(r.close(later));
}

#[test]
fn a_priority_request_preempts_an_ordinary_retry_and_keeps_the_ordinary_order() {
    let mut r = Rig::new(800.0, 600.0);
    r.available.borrow_mut().insert(DialogType::Wait, false);
    let failed = r.make(DialogData::wait("failed ordinary"), "a");
    let later = r.make(DialogData::wait("later ordinary"), "b");
    let priority = r.make(
        DialogData::message("priority")
            .priority()
            .on_queue(DEFAULT_QUEUE_KEY),
        "c",
    );
    assert_eq!(
        (r.f.active_count(), r.f.retry_count(), r.f.pending_count()),
        (1, 1, 1)
    );
    assert_eq!(r.modal_message(), "priority");
    assert!(r.close(priority));
    assert_eq!(
        (r.f.active_count(), r.f.retry_count(), r.f.pending_count()),
        (0, 1, 1)
    );
    r.available.borrow_mut().insert(DialogType::Wait, true);
    r.tick();
    assert_eq!(r.modal_message(), "failed ordinary");
    assert!(r.close(failed));
    assert_eq!(r.modal_message(), "later ordinary");
    assert!(r.close(later));
}

#[test]
fn a_failed_priority_retries_ahead_of_the_restored_open_and_waiting_dialogs() {
    let mut r = Rig::new(800.0, 600.0);
    r.available.borrow_mut().insert(DialogType::Message, false);
    let active = r.make(DialogData::wait("active"), "a");
    let queued = r.make(DialogData::wait("queued"), "b");
    let priority = r.make(
        DialogData::message("priority")
            .priority()
            .on_queue(DEFAULT_QUEUE_KEY),
        "c",
    );
    assert_eq!(
        (r.f.active_count(), r.f.retry_count(), r.f.pending_count()),
        (1, 1, 1)
    );
    assert_eq!(r.modal_message(), "active");
    r.available.borrow_mut().insert(DialogType::Message, true);
    r.tick();
    assert_eq!(
        (r.f.active_count(), r.f.retry_count(), r.f.pending_count()),
        (1, 0, 2)
    );
    assert_eq!(r.modal_message(), "priority");
    assert!(r.close(priority));
    assert_eq!(r.modal_message(), "active");
    assert_eq!(r.f.pending_count(), 1);
    assert!(r.close(active));
    assert_eq!(r.modal_message(), "queued");
    assert!(r.close(queued));
}

#[test]
fn retries_run_newest_priority_first_then_older_priority_then_ordinary_order() {
    let mut r = Rig::new(800.0, 600.0);
    for k in [
        DialogType::Wait,
        DialogType::Message,
        DialogType::ConfirmationTextInput,
    ] {
        r.available.borrow_mut().insert(k, false);
    }
    let ordinary = r.make(DialogData::wait("ordinary"), "a");
    let later = r.make(DialogData::wait("later"), "b");
    let older = r.make(
        DialogData::message("older priority")
            .priority()
            .on_queue(DEFAULT_QUEUE_KEY),
        "c",
    );
    let newer = r.make(
        DialogData::confirmation_text_input("newer priority")
            .priority()
            .on_queue(DEFAULT_QUEUE_KEY),
        "d",
    );
    assert_eq!(
        (r.f.active_count(), r.f.retry_count(), r.f.pending_count()),
        (0, 3, 1)
    );
    r.available.borrow_mut().clear();
    r.tick();
    assert_eq!((r.f.active_count(), r.f.retry_count()), (1, 2));
    assert_eq!(r.modal_message(), "newer priority");
    assert!(r.close(newer));
    assert_eq!(r.modal_message(), "older priority");
    assert!(r.close(older));
    assert_eq!(r.modal_message(), "ordinary");
    assert!(r.close(ordinary));
    assert_eq!(r.modal_message(), "later");
    assert!(r.close(later));
}

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::import_rooted;
    use ac_ui::panels::dialog::catalog_layout_id;
    use std::path::PathBuf;

    #[test]
    #[ignore = "needs the retail dat files"]
    fn every_presented_kind_opens_from_the_retail_catalog() {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let dats = Arc::new(ac_dat::DatCollection::open(dir).unwrap());
        let catalog = catalog_layout_id(&dats).expect("the dialog catalog");
        let c = DatContent::new(dats).unwrap();
        let mut ui = UiRoot::new();
        ui.set_size(800.0, 600.0);
        let mut f: DialogFactory<()> = DialogFactory::new();
        let mut make = |ui: &mut UiRoot, kind: DialogType| {
            import_rooted(
                ui,
                &c,
                catalog,
                kind.root_element_id(),
                c.font(DEFAULT_FONT_ID),
            )
        };
        let items = ["one".to_owned(), "two".to_owned()];
        for data in [
            DialogData::confirmation("Proceed?"),
            DialogData::wait("Please wait").on_queue(3),
            DialogData::message("Done").on_queue(4),
            DialogData::confirmation_text_input("Type DELETE").on_queue(5),
            DialogData::confirmation_menu(&items, 1).on_queue(6),
        ] {
            let kind = data.kind().unwrap();
            let context = f.make(&mut ui, data, (), &mut make);
            assert!(
                f.view(context).is_some(),
                "{kind:?}: {:?}",
                f.take_failures()
            );
        }
        assert_eq!(f.active_count(), 5);
        f.reset(&mut ui);
        assert_eq!(f.take_closed().len(), 5);
        assert_eq!(ui.modal, None);
    }
}
