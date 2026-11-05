//! The shared yes/no dialog: a question from the session (the server's
//! confirmation requests) or from a panel (forget a spell, train a skill),
//! shown one at a time over everything, modal, centred on the screen. The
//! layout comes from the dialog catalog (the interface enumeration's dialog
//! entry, root 0x15). The client keeps the queue and decides what an answer
//! does; this module only shows the question and reports the answer.

use std::collections::BTreeSet;

use ac_dat::DatCollection;
use ac_dat::objects::EnumIdMap;

use crate::import::ImportedLayout;
use crate::panels::dialog_factory::{
    DEFAULT_QUEUE_KEY, DialogData, DialogFactory, DialogType, DialogView, property,
};
use crate::root::{ElementMessage, UiRoot};
use crate::tree::ElementId;

/// The confirmation dialog's root in the catalog.
pub const CONFIRMATION_ROOT_ID: u32 = 0x15;
/// Its yes button.
pub const ACCEPT_BUTTON_ID: u32 = 0x17;
/// Its no button.
pub const REJECT_BUTTON_ID: u32 = 0x19;
/// The "more questions waiting" marker.
pub const PENDING_DISPLAY_ID: u32 = 0x33;
/// The box that holds the message and the buttons.
pub const POPUP_ELEMENT_ID: u32 = 0x3D;
/// The message text.
pub const MESSAGE_ELEMENT_ID: u32 = 0x3E;
/// The marker's state while questions wait.
pub const PENDING_STATE: u32 = 0x18;
/// The marker's state while none wait.
pub const NO_PENDING_STATE: u32 = 0x19;

/// The interface enumeration category the dialog catalog is listed under.
pub const DIALOG_ENUM_CATEGORY: u32 = 5;
/// The dialog catalog's entry in it.
pub const DIALOG_CATALOG_ENUM: u32 = 2;

/// The dialog catalog's layout id: master map → category 5 → entry 2.
#[must_use]
pub fn catalog_layout_id(dats: &DatCollection) -> Option<u32> {
    let master_id = dats.portal.header().master_map_id.cast_unsigned();
    let master: EnumIdMap = dats.get(master_id).ok().flatten()?;
    let sub: EnumIdMap = dats
        .get(master.client_id(DIALOG_ENUM_CATEGORY)?)
        .ok()
        .flatten()?;
    sub.client_id(DIALOG_CATALOG_ENUM).filter(|&id| id != 0)
}

/// The training confirmation's question.
#[must_use]
pub fn train_skill_message(credits: u32, skill_name: &str) -> String {
    format!("Are you sure you want to spend {credits} credits to train {skill_name}?")
}

/// The server's question as the dialog words it: the kinds that change
/// the character or take an item (skill, attribute, crafting,
/// augmentation) end with " Continue?"; the others are the server's text,
/// or what `compose` makes of it (swear allegiance and fellowship
/// invitations name the asker from the string table).
#[must_use]
pub fn confirmation_text(
    confirmation_type: u32,
    message: &str,
    compose: impl FnOnce(u32, &str) -> Option<String>,
) -> String {
    match confirmation_type {
        2 | 3 | 5 | 6 => format!("{message} Continue?"),
        _ => compose(confirmation_type, message).unwrap_or_else(|| message.to_owned()),
    }
}

/// What the player did with the open dialog.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DialogAnswer {
    /// Yes.
    Accept,
    /// No (the no button or Escape).
    Reject,
}

/// The window's questions: yes/no dialogs made through the retail dialog
/// factory (one queue, oldest first, each carrying what its answer does,
/// `T`, the client's).
#[derive(Debug)]
pub struct DialogQueue<T> {
    factory: DialogFactory<T>,
    /// The factory's failure lines already reported, kept while a dialog
    /// still waits to be made again.
    reported: BTreeSet<String>,
}

impl<T> Default for DialogQueue<T> {
    fn default() -> Self {
        Self {
            factory: DialogFactory::new(),
            reported: BTreeSet::new(),
        }
    }
}

impl<T> DialogQueue<T> {
    /// The factory under the queue (other dialog kinds).
    pub fn factory_mut(&mut self) -> &mut DialogFactory<T> {
        &mut self.factory
    }

    /// The factory under the queue, to read (every open dialog).
    #[must_use]
    pub fn factory(&self) -> &DialogFactory<T> {
        &self.factory
    }

    /// Ask `text`; `then` is handed back with the answer. The question
    /// shows at once when no other is open.
    pub fn ask(
        &mut self,
        ui: &mut UiRoot,
        text: String,
        then: T,
        mut make: impl FnMut(&mut UiRoot, DialogType) -> Option<ImportedLayout>,
    ) {
        self.factory.make(
            ui,
            DialogData::confirmation(&text).on_queue(DEFAULT_QUEUE_KEY),
            then,
            &mut make,
        );
    }

    /// Whether a dialog is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.factory.is_open()
    }

    /// Whether a waiting (or retrying) question's action matches.
    #[must_use]
    pub fn is_waiting(&self, matches: impl Fn(&T) -> bool) -> bool {
        let open: Vec<u32> = self.factory.open_contexts().to_vec();
        self.factory
            .contexts_where(matches)
            .into_iter()
            .any(|c| !open.contains(&c))
    }

    /// The open question's action (the topmost dialog's).
    #[must_use]
    pub fn open_action(&self) -> Option<&T> {
        let context = *self.factory.open_contexts().last()?;
        self.factory.tag(context)
    }

    /// The open dialog.
    #[must_use]
    pub fn dialog(&self) -> Option<&DialogView> {
        let context = *self.factory.open_contexts().last()?;
        self.factory.view(context)
    }

    /// `message` from `element` answered the open dialog: it closes, the
    /// next question shows, and the answer comes back with its action.
    pub fn answer(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
        mut make: impl FnMut(&mut UiRoot, DialogType) -> Option<ImportedLayout>,
    ) -> Option<(DialogAnswer, T)> {
        if !self.factory.handle(ui, element, message) {
            return None;
        }
        let closed = self.factory.take_closed().into_iter().next();
        self.factory.settle(ui, &mut make);
        let closed = closed?;
        let answer = if closed.data.get_bool(property::CONFIRMATION_RESULT) {
            DialogAnswer::Accept
        } else {
            DialogAnswer::Reject
        };
        Some((answer, closed.tag))
    }

    /// Withdraw every question whose action matches (the server closed
    /// it): no answer comes back.
    pub fn withdraw(
        &mut self,
        ui: &mut UiRoot,
        matches: impl Fn(&T) -> bool,
        mut make: impl FnMut(&mut UiRoot, DialogType) -> Option<ImportedLayout>,
    ) {
        for context in self.factory.contexts_where(matches) {
            self.factory.close(ui, context);
        }
        drop(self.factory.take_closed());
        self.factory.settle(ui, &mut make);
    }

    /// The dialogs the catalog could not make since the last call, in the
    /// factory's words, each line once: a dialog retried every frame is
    /// reported at its first failure, and again only after nothing waits
    /// for a retry any more. Draining here keeps the factory's list from
    /// growing.
    pub fn take_failures(&mut self) -> Vec<String> {
        let fresh: Vec<String> = self
            .factory
            .take_failures()
            .into_iter()
            .filter(|line| self.reported.insert(line.clone()))
            .collect();
        if self.factory.retry_count() == 0 {
            self.reported.clear();
        }
        fresh
    }

    /// Keep the open dialog centred, on top and its waiting marker
    /// current; retry a dialog the catalog could not make.
    pub fn tick(
        &mut self,
        ui: &mut UiRoot,
        mut make: impl FnMut(&mut UiRoot, DialogType) -> Option<ImportedLayout>,
    ) {
        self.factory.tick(ui, &mut make);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn server_questions_are_worded_by_kind() {
        // OpenAC `GameplayConfirmationController.HandleRequest`.
        let none = |_: u32, _: &str| None;
        assert_eq!(
            confirmation_text(2, "Raise Melee Defense?", none),
            "Raise Melee Defense? Continue?"
        );
        assert_eq!(confirmation_text(7, "Ready?", none), "Ready?");
        assert_eq!(
            confirmation_text(4, "Bob", |t, n| Some(format!("{t}:{n}"))),
            "4:Bob"
        );
    }

    #[test]
    fn a_question_the_catalog_cannot_make_is_reported_once_and_drained() {
        // OpenAC `RetailDialogFactory.TryCreateDialog` reports the failure
        // and retries each tick; here each failing dialog is reported once
        // and the factory's list is drained every call (smoke 2).
        let mut ui = UiRoot::new();
        let mut queue: DialogQueue<u32> = DialogQueue::default();
        let mut no_catalog = |_: &mut UiRoot, _: DialogType| None;
        queue.ask(&mut ui, "Proceed?".to_owned(), 1, &mut no_catalog);
        for _ in 0..5 {
            queue.tick(&mut ui, &mut no_catalog);
        }
        let first = queue.take_failures();
        assert_eq!(first.len(), 1, "{first:?}");
        assert!(first[0].contains("waits for the catalog"), "{first:?}");
        for _ in 0..5 {
            queue.tick(&mut ui, &mut no_catalog);
        }
        assert!(queue.take_failures().is_empty());
        assert!(queue.factory_mut().take_failures().is_empty());
        assert!(!queue.is_open());

        // Withdrawn, nothing waits; the next question's failure is new.
        queue.withdraw(&mut ui, |&action| action == 1, &mut no_catalog);
        assert!(queue.take_failures().is_empty());
        queue.ask(&mut ui, "And this?".to_owned(), 2, &mut no_catalog);
        queue.tick(&mut ui, &mut no_catalog);
        assert_eq!(queue.take_failures().len(), 1);
        assert!(queue.is_waiting(|&action| action == 2));
    }

    #[test]
    fn the_training_question() {
        // OpenAC `RetailSkillTrainingConfirmationController.MessageFormat`.
        assert_eq!(
            train_skill_message(8, "Alchemy"),
            "Are you sure you want to spend 8 credits to train Alchemy?"
        );
    }
}
