//! Plugin dialogs and modal windows as session state (OpenAC
//! `BufferedUiRegistry.Confirm`/`ShowModal`, `PluginDialogHost`). A dialog
//! is answered exactly once: by the player, by the plugin withdrawing it,
//! or with its last button when its owner window closes or the window
//! detaches. Without a window nothing is shown, so nothing is asked.

use super::{PluginUi, same_owner};
use crate::model::{DialogView, UiChange, UiEvent, UiOwner};

/// The most buttons a plugin's dialog may have.
pub const MAXIMUM_DIALOG_BUTTONS: usize = 4;

/// The buttons of a dialog that names none.
pub const DEFAULT_DIALOG_BUTTONS: [&str; 2] = ["OK", "Cancel"];

impl PluginUi {
    /// Queues a question for the player (OpenAC `Confirm`): its handle,
    /// none without a window (nothing draws it, and it is never answered).
    /// `owner_view` names the plugin window it is centred on and blocks; one
    /// not shown counts as none.
    ///
    /// # Errors
    /// No buttons (after the defaults), more than four, or a blank one.
    pub fn confirm(
        &mut self,
        owner: &UiOwner,
        title: &str,
        message: &str,
        buttons: Vec<String>,
        owner_view: Option<&str>,
    ) -> Result<Option<u32>, String> {
        let buttons = if buttons.is_empty() {
            DEFAULT_DIALOG_BUTTONS
                .iter()
                .map(|b| (*b).to_owned())
                .collect()
        } else {
            buttons
        };
        if !(1..=MAXIMUM_DIALOG_BUTTONS).contains(&buttons.len()) {
            return Err(format!(
                "A dialog has 1 to {MAXIMUM_DIALOG_BUTTONS} buttons, not {}.",
                buttons.len()
            ));
        }
        if buttons.iter().any(|b| b.trim().is_empty()) {
            return Err("Every dialog button needs a caption.".to_owned());
        }
        if !self.window_attached {
            return Ok(None);
        }
        let owner_panel = owner_view
            .and_then(|v| self.find_view(&owner.id, v))
            .map(|i| &self.panels[i].view)
            .filter(|p| p.shown())
            .map(|p| p.id);
        let handle = self.next_id();
        let dialog = DialogView {
            handle,
            owner: owner.clone(),
            title: title.to_owned(),
            message: message.to_owned(),
            buttons,
            owner_panel,
        };
        self.changes
            .push(UiChange::DialogAdded(Box::new(dialog.clone())));
        self.dialogs.push(dialog);
        Ok(Some(handle))
    }

    /// Withdraws one of `owner`'s dialogs, answering it with its last
    /// button; false once it is answered.
    pub fn withdraw_dialog(&mut self, owner: &str, handle: u32) -> bool {
        let Some(dialog) = self
            .dialogs
            .iter()
            .find(|d| d.handle == handle && same_owner(&d.owner.id, owner))
        else {
            return false;
        };
        let cancel = dialog.cancel_index();
        self.answer_dialog(handle, cancel);
        true
    }

    /// The dialogs waiting for an answer, in the order they were asked.
    pub fn dialogs(&self) -> impl Iterator<Item = &DialogView> {
        self.dialogs.iter()
    }

    /// Answers a pending dialog: it goes, and its plugin hears the button.
    pub(super) fn answer_dialog(&mut self, handle: u32, button: i32) {
        let Some(i) = self.dialogs.iter().position(|d| d.handle == handle) else {
            return;
        };
        let dialog = self.dialogs.remove(i);
        let button = button.clamp(0, dialog.cancel_index());
        self.changes.push(UiChange::DialogRemoved(handle));
        self.events
            .push((dialog.owner.id, UiEvent::DialogAnswered { handle, button }));
    }

    /// Shows one of `owner`'s windows, modal over another (OpenAC
    /// `ShowModal`): the owner is blocked until the shown window hides or
    /// goes, or the owner goes. False without a window, or when `owner`
    /// has no such window. No owner, the window itself, or one the window
    /// is already blocked by (through any chain of modals) blocks nothing.
    pub fn show_modal(&mut self, owner: &str, view: &str, owner_view: Option<&str>) -> bool {
        if !self.window_attached {
            return false;
        }
        let Some(i) = self.find_view(owner, view) else {
            return false;
        };
        self.request_visible(i, true);
        let panel = self.panels[i].view.id;
        let blocked = owner_view
            .and_then(|v| self.find_view(owner, v))
            .map(|j| self.panels[j].view.id);
        if let Some(blocked) = blocked
            && self.blocked_by(panel, blocked)
        {
            return true;
        }
        if let Some(existing) = self.modals.iter().position(|(p, _)| *p == panel) {
            if Some(self.modals[existing].1) == blocked {
                return true;
            }
            self.modals.remove(existing);
            self.changes.push(UiChange::Modal { panel, owner: None });
        }
        let Some(blocked) = blocked.filter(|b| *b != panel) else {
            return true;
        };
        self.modals.push((panel, blocked));
        self.changes.push(UiChange::Modal {
            panel,
            owner: Some(blocked),
        });
        true
    }

    /// Whether `window` is blocked by `panel`, directly or through a chain
    /// of modal windows each blocking the next.
    fn blocked_by(&self, window: u32, panel: u32) -> bool {
        let mut pending = vec![panel];
        let mut seen = Vec::new();
        while let Some(blocker) = pending.pop() {
            if seen.contains(&blocker) {
                continue;
            }
            seen.push(blocker);
            for (modal, blocked) in &self.modals {
                if *modal != blocker {
                    continue;
                }
                if *blocked == window {
                    return true;
                }
                pending.push(*blocked);
            }
        }
        false
    }

    /// The windows shown modal and the windows they block.
    pub fn modals(&self) -> impl Iterator<Item = (u32, u32)> + '_ {
        self.modals.iter().copied()
    }

    /// Answers the dialogs whose owner window is no longer shown with their
    /// last button, and ends the modals whose window hid or went, or whose
    /// owner went.
    pub(super) fn follow_dialogs(&mut self) {
        let shown = |ui: &Self, panel: u32| {
            ui.panels
                .iter()
                .any(|p| p.view.id == panel && p.view.shown())
        };
        let exists = |ui: &Self, panel: u32| ui.panels.iter().any(|p| p.view.id == panel);
        let orphaned: Vec<(u32, i32)> = self
            .dialogs
            .iter()
            .filter(|d| d.owner_panel.is_some_and(|p| !shown(self, p)))
            .map(|d| (d.handle, d.cancel_index()))
            .collect();
        for (handle, cancel) in orphaned {
            self.answer_dialog(handle, cancel);
        }
        let ended: Vec<u32> = self
            .modals
            .iter()
            .filter(|(panel, owner)| !shown(self, *panel) || !exists(self, *owner))
            .map(|(panel, _)| *panel)
            .collect();
        for panel in ended {
            self.modals.retain(|(p, _)| *p != panel);
            self.changes.push(UiChange::Modal { panel, owner: None });
        }
    }

    /// The window detached: every dialog is answered with its last button
    /// and every modal ends (a window that attaches again starts clear).
    pub(super) fn detach_dialogs(&mut self) {
        let pending: Vec<(u32, i32)> = self
            .dialogs
            .iter()
            .map(|d| (d.handle, d.cancel_index()))
            .collect();
        for (handle, cancel) in pending {
            self.answer_dialog(handle, cancel);
        }
        self.modals.clear();
    }
}
