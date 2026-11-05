//! The character selection screen: the roster as one row per character
//! (pending deletion in red, the highlighted one selected), the world's
//! name, and the create, enter, delete, restore, credits and exit buttons
//! with their dialogs: the delete confirmation that wants the word typed,
//! the waits while entering or deleting, the server's errors, and the exit
//! question.
//!
//! The screen shows while the session is at character selection (or
//! entering the world from it), unless the session enters its configured
//! character by itself or another screen covers it.

use ac_runtime::session::selection::{
    SelectionButtons, SelectionEntry, SelectionLifecycle, SelectionOperation, SelectionSnapshot,
};
use ac_runtime::session::{CharacterSelector, Generation, LifecycleCommand, SessionCommand};

use super::DialogResult;
use super::{Dialogs, PreGameRequest};
use crate::draw::Color;
use crate::import::ImportedLayout;
use crate::panels::RowTemplates;
use crate::panels::dialog_factory::DialogData;
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::{Element, ElementId};
use crate::widgets::{Button, ListBox, Text};

/// The screen's entry in the interface enumeration (category 5).
pub const ROOT_ENUM: u32 = 0x1000_0005;
/// The screen's root.
pub const ROOT_ELEMENT_ID: u32 = 0x1000_039A;
/// The world's name.
pub const WORLD_TEXT_ID: u32 = 0x1000_039B;
/// The roster list.
pub const LIST_ID: u32 = 0x1000_039D;
/// Create a character.
pub const CREATE_ID: u32 = 0x1000_03A0;
/// Enter the world.
pub const ENTER_ID: u32 = 0x1000_03A2;
/// Delete the highlighted character.
pub const DELETE_ID: u32 = 0x1000_039F;
/// Restore the highlighted character.
pub const RESTORE_ID: u32 = 0x1000_039E;
/// Show the credits.
pub const CREDITS_ID: u32 = 0x1000_03A3;
/// Leave.
pub const EXIT_ID: u32 = 0x1000_03A4;
/// A row's caption colour for a character pending deletion.
pub const PENDING_DELETE_COLOR: Color = Color::rgba(1.0, 0.0, 0.0, 1.0);
/// The client version line's colour.
pub const VERSION_COLOR: Color = Color::rgba(0.65, 0.65, 0.65, 1.0);

/// The fixed-canvas owner number of this screen.
pub(crate) const CANVAS_OWNER: u64 = 2;

/// The screen's dialog texts (table 0x23000002).
pub struct CharacterStrings {
    /// The delete question for a character's name.
    pub delete_confirmation: Box<dyn Fn(&str) -> String>,
    /// The word the player types to delete ("DELETE").
    pub delete_response: String,
    /// "Please Wait".
    pub please_wait: String,
    /// "Entering World".
    pub entering_world: String,
    /// "Are you sure you want to leave?".
    pub confirm_exit: String,
}

impl std::fmt::Debug for CharacterStrings {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CharacterStrings")
            .field("delete_response", &self.delete_response)
            .finish_non_exhaustive()
    }
}

/// A row's height: the list's height over the larger of the roster and
/// the account's slots, never under a tenth of the list.
#[must_use]
pub fn row_height(list_height: f32, roster_count: usize, allowed_slot_count: i32) -> i32 {
    let height = list_height.trunc() as i32;
    let roster = i32::try_from(roster_count).unwrap_or(i32::MAX);
    let denominator = roster.max(allowed_slot_count);
    if denominator <= 0 {
        return height / 10;
    }
    (height / denominator).max(height / 10)
}

/// The snapshot a request was made under: while it is still the one shown
/// the answer has not come.
type Key = (Generation, u64);

fn key(snapshot: &SelectionSnapshot) -> Key {
    (snapshot.generation, snapshot.revision)
}

/// The character selection screen. See the module documentation.
#[derive(Debug)]
pub struct CharacterScreen {
    root: ElementId,
    world_text: ElementId,
    list: ElementId,
    create: ElementId,
    enter: ElementId,
    delete: ElementId,
    restore: ElementId,
    credits: ElementId,
    exit: ElementId,
    strings: CharacterStrings,
    rows: Vec<(ElementId, u32)>,
    built: Option<Key>,
    world_name: String,
    active: bool,
    suppressed: bool,
    delete_dialog: u32,
    operation_wait: u32,
    enter_wait: u32,
    error_dialog: u32,
    exit_dialog: u32,
    enter_sent: Option<Key>,
    restore_sent: Option<Key>,
    shown: Option<SelectionSnapshot>,
}

impl CharacterScreen {
    /// Bind the imported screen (root 0x1000039A) and add it to the screen
    /// root, hidden. `None` (and nothing mounted) when the list, the world
    /// text or a button is missing. `version` is the client version line
    /// drawn at the top left.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        strings: CharacterStrings,
        version: &str,
    ) -> Option<Self> {
        let root = layout.root;
        if ui.element(root)?.dat_element_id != ROOT_ELEMENT_ID {
            return None;
        }
        let button = |ui: &UiRoot, id| {
            layout
                .find(id)
                .filter(|b| ui.widget::<Button>(*b).is_some())
        };
        let world_text = layout
            .find(WORLD_TEXT_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some())?;
        let list = layout
            .find(LIST_ID)
            .filter(|l| ui.widget::<ListBox>(*l).is_some())?;
        let create = button(ui, CREATE_ID)?;
        let enter = button(ui, ENTER_ID)?;
        let delete = button(ui, DELETE_ID)?;
        let restore = button(ui, RESTORE_ID)?;
        let credits = button(ui, CREDITS_ID)?;
        let exit = button(ui, EXIT_ID)?;
        let (width, font) = {
            let font = ui.widget::<Text>(world_text).and_then(|t| t.font.clone());
            let e = ui.element_mut(root)?;
            e.left = 0.0;
            e.top = 0.0;
            e.click_through = false;
            e.visible = false;
            (if e.width > 0.0 { e.width } else { 800.0 }, font)
        };
        for b in [create, enter, delete, restore, credits, exit] {
            if let Some(w) = ui.widget_mut::<Button>(b) {
                w.listeners.click = true;
            }
        }
        ui.set_visible(create, true);
        ui.set_enabled(create, false);
        ui.set_visible(credits, true);
        // The client's version at the top left, over everything, taking no
        // input.
        let mut line = Element::at(8.0, 6.0, width - 16.0, 24.0);
        Text::configure(&mut line);
        line.name = Some("ClientVersion".to_owned());
        line.z_order = i32::MAX;
        let version_text = ui.add(root, line, Text::default());
        if let Some(t) = ui.widget_mut::<Text>(version_text) {
            t.font = font;
            t.one_line = true;
            t.outline = true;
            t.set_lines(vec![Line::new(version, VERSION_COLOR)]);
        }
        let screen = ui.root();
        ui.add_child(screen, root);
        Some(Self {
            root,
            world_text,
            list,
            create,
            enter,
            delete,
            restore,
            credits,
            exit,
            strings,
            rows: Vec::new(),
            built: None,
            world_name: String::new(),
            active: false,
            suppressed: false,
            delete_dialog: 0,
            operation_wait: 0,
            enter_wait: 0,
            error_dialog: 0,
            exit_dialog: 0,
            enter_sent: None,
            restore_sent: None,
            shown: None,
        })
    }

    /// The screen's root.
    #[must_use]
    pub fn root(&self) -> ElementId {
        self.root
    }

    /// The rows, top first, with their character ids.
    #[must_use]
    pub fn rows(&self) -> &[(ElementId, u32)] {
        &self.rows
    }

    /// A button by its dat id.
    #[must_use]
    pub fn button(&self, dat_id: u32) -> Option<ElementId> {
        [
            self.create,
            self.enter,
            self.delete,
            self.restore,
            self.credits,
            self.exit,
        ]
        .into_iter()
        .zip([
            CREATE_ID, ENTER_ID, DELETE_ID, RESTORE_ID, CREDITS_ID, EXIT_ID,
        ])
        .find_map(|(e, id)| (id == dat_id).then_some(e))
    }

    /// The world text.
    #[must_use]
    pub fn world_text(&self) -> ElementId {
        self.world_text
    }

    /// Whether the screen shows.
    #[must_use]
    pub fn is_active(&self) -> bool {
        self.active
    }

    /// The open dialogs' contexts: delete, operation wait, enter wait,
    /// error, exit (0 for none).
    #[must_use]
    pub fn dialog_contexts(&self) -> [u32; 5] {
        [
            self.delete_dialog,
            self.operation_wait,
            self.enter_wait,
            self.error_dialog,
            self.exit_dialog,
        ]
    }

    /// Another screen (the credits) covers this one, or stops covering it.
    pub fn set_presentation_suppressed(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        suppressed: bool,
    ) {
        if self.suppressed != suppressed {
            self.suppressed = suppressed;
            self.built = None;
            if suppressed {
                self.deactivate(ui, dialogs);
            }
        }
    }

    /// Follow character selection. `direct_launch`: the session enters its
    /// configured character by itself; `blocked`: the connecting screen
    /// covers this one; `refused`: the session refused a request of this
    /// window since the last update.
    #[allow(clippy::too_many_arguments)]
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        view: Option<&SelectionSnapshot>,
        direct_launch: bool,
        blocked: bool,
        refused: bool,
        templates: &mut dyn RowTemplates,
    ) {
        if refused {
            // What waited on the refused request closes.
            if self.enter_sent.take().is_some() {
                dialogs.close(ui, &mut self.enter_wait);
            }
            if self.restore_sent.take().is_some() {
                dialogs.close(ui, &mut self.operation_wait);
            }
        }
        // The session entering its configured character keeps selection
        // out of the way, until the world or an error.
        let lifecycle = view.map_or(SelectionLifecycle::Inactive, |v| v.lifecycle);
        let pending = direct_launch
            && lifecycle != SelectionLifecycle::InWorld
            && view.is_none_or(|v| v.error.is_none());
        if self.suppressed || blocked || pending {
            self.deactivate(ui, dialogs);
            self.built = None;
            return;
        }
        let Some(view) = view.filter(|v| {
            matches!(
                v.lifecycle,
                SelectionLifecycle::AwaitingSelection | SelectionLifecycle::EnteringWorld
            )
        }) else {
            self.deactivate(ui, dialogs);
            self.built = None;
            return;
        };
        if !self.active {
            self.active = true;
            ui.set_visible(self.root, true);
            let size = ui
                .element(self.root)
                .map_or((800.0, 600.0), |e| (e.width, e.height));
            let _ = ui.declare_fixed_canvas(CANVAS_OWNER, size);
            ui.bring_to_front(self.root);
        }
        if self.world_name != view.world_name || self.shown.is_none() {
            self.world_name.clone_from(&view.world_name);
            let name = self.world_name.clone();
            if let Some(t) = ui.widget_mut::<Text>(self.world_text) {
                let color = t.default_color;
                t.set_lines(vec![Line::new(name, color)]);
            }
        }
        if self.built != Some(key(view)) {
            let ready = if self.rows_match(ui, &view.entries, view.slot_count) {
                self.apply_highlight(ui, view.highlighted_character_id);
                true
            } else {
                self.rebuild_rows(
                    ui,
                    templates,
                    &view.entries,
                    view.slot_count,
                    view.highlighted_character_id,
                )
            };
            if ready {
                self.built = Some(key(view));
            }
        }
        self.apply_buttons(ui, &view.buttons);
        self.reconcile_dialogs(ui, dialogs, view);
        self.shown = Some(view.clone());
    }

    /// A message from inside the screen: the requests it makes. `None`
    /// when it is not the screen's.
    pub fn on_message(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        element: ElementId,
        message: &ElementMessage,
    ) -> Option<Vec<PreGameRequest>> {
        if !self.active {
            return None;
        }
        if let Some(&(_, id)) = self.rows.iter().find(|(row, _)| *row == element) {
            return match message {
                ElementMessage::Clicked { .. } => {
                    self.apply_highlight(ui, id);
                    Some(vec![lifecycle(LifecycleCommand::Highlight(
                        CharacterSelector::Id(id),
                    ))])
                }
                ElementMessage::DoubleClicked => Some(self.enter_selected(ui, dialogs)),
                _ => Some(Vec::new()),
            };
        }
        if !matches!(message, ElementMessage::Clicked { .. }) {
            return ui.is_within(Some(element), self.root).then(Vec::new);
        }
        let requests = if element == self.create {
            vec![PreGameRequest::OpenCreation]
        } else if element == self.enter {
            self.enter_selected(ui, dialogs)
        } else if element == self.delete {
            vec![lifecycle(LifecycleCommand::RequestDelete(None))]
        } else if element == self.restore {
            dialogs.ensure(
                ui,
                &mut self.operation_wait,
                DialogData::wait(&self.strings.please_wait),
            );
            self.restore_sent = self.shown.as_ref().map(key);
            vec![lifecycle(LifecycleCommand::Restore(None))]
        } else if element == self.credits {
            vec![PreGameRequest::OpenCredits]
        } else if element == self.exit {
            if self.exit_dialog == 0 {
                self.exit_dialog =
                    dialogs.make(ui, DialogData::confirmation(&self.strings.confirm_exit));
            }
            Vec::new()
        } else {
            return ui.is_within(Some(element), self.root).then(Vec::new);
        };
        Some(requests)
    }

    /// The player answered dialog `context`: the requests it makes. `None`
    /// when the dialog is not the screen's.
    pub fn on_dialog(
        &mut self,
        context: u32,
        result: &DialogResult,
    ) -> Option<Vec<PreGameRequest>> {
        if context == 0 {
            return None;
        }
        if context == self.exit_dialog {
            self.exit_dialog = 0;
            return Some(if *result == DialogResult::Confirmed(true) {
                vec![PreGameRequest::Session(SessionCommand::Shutdown)]
            } else {
                Vec::new()
            });
        }
        if context == self.delete_dialog {
            self.delete_dialog = 0;
            let typed = match result {
                DialogResult::Text(t) => t.as_str(),
                _ => "",
            };
            self.built = None;
            return Some(vec![lifecycle(
                if typed.eq_ignore_ascii_case(&self.strings.delete_response) {
                    LifecycleCommand::ConfirmDelete
                } else {
                    LifecycleCommand::Cancel
                },
            )]);
        }
        if context == self.error_dialog {
            self.error_dialog = 0;
            self.built = None;
            return Some(vec![lifecycle(LifecycleCommand::Cancel)]);
        }
        None
    }

    fn enter_selected(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
    ) -> Vec<PreGameRequest> {
        dialogs.ensure(
            ui,
            &mut self.enter_wait,
            DialogData::wait(&self.strings.entering_world),
        );
        self.enter_sent = self.shown.as_ref().map(key);
        vec![lifecycle(LifecycleCommand::Enter(None))]
    }

    fn rows_match(&self, ui: &UiRoot, roster: &[SelectionEntry], slots: i32) -> bool {
        if self.rows.len() != roster.len() {
            return false;
        }
        let list_height = ui.element(self.list).map_or(0.0, |e| e.height);
        let height = row_height(list_height, roster.len(), slots) as f32;
        self.rows.iter().zip(roster).all(|((row, id), entry)| {
            let Some(b) = ui.widget::<Button>(*row) else {
                return false;
            };
            *id == entry.character_id
                && b.label.as_deref() == Some(entry.name.as_str())
                && ui.element(*row).is_some_and(|e| e.height == height)
                && b.label_color == row_color(entry)
        })
    }

    fn rebuild_rows(
        &mut self,
        ui: &mut UiRoot,
        templates: &mut dyn RowTemplates,
        roster: &[SelectionEntry],
        slots: i32,
        highlighted: u32,
    ) -> bool {
        self.rows.clear();
        let template = ui
            .widget::<ListBox>(self.list)
            .and_then(|l| l.templates.first().cloned());
        ui.with::<ListBox, _>(self.list, |l, ui| l.flush(ui));
        let list_height = ui.element(self.list).map_or(0.0, |e| e.height);
        let height = row_height(list_height, roster.len(), slots);
        ui.with::<ListBox, _>(self.list, |l, ui| l.set_line_height(ui, height));
        let Some(template) = template else {
            return false;
        };
        for entry in roster {
            let Some(row) = templates
                .build_row(ui, template.layout, template.element)
                .filter(|r| ui.widget::<Button>(*r).is_some())
            else {
                self.rows.clear();
                ui.with::<ListBox, _>(self.list, |l, ui| l.flush(ui));
                return false;
            };
            if let Some(e) = ui.element_mut(row) {
                e.height = height as f32;
            }
            let list = self.list;
            ui.with::<ListBox, _>(list, |l, ui| l.add_prebuilt_row(ui, list, row));
            ui.set_enabled(row, true);
            ui.with::<Button, _>(row, |b, ui| {
                b.label = Some(entry.name.clone());
                b.label_color = row_color(entry);
                b.suppress_self_toggle = true;
                b.listeners.click = true;
                b.listeners.double_click = true;
                b.set_selected(ui, row, entry.character_id == highlighted);
            });
            self.rows.push((row, entry.character_id));
        }
        true
    }

    fn apply_highlight(&self, ui: &mut UiRoot, highlighted: u32) {
        for &(row, id) in &self.rows {
            ui.with::<Button, _>(row, |b, ui| b.set_selected(ui, row, id == highlighted));
        }
    }

    fn apply_buttons(&self, ui: &mut UiRoot, buttons: &SelectionButtons) {
        ui.set_visible(self.create, true);
        ui.set_enabled(self.create, buttons.can_create);
        ui.set_enabled(self.enter, buttons.can_enter);
        ui.set_visible(self.delete, buttons.delete_visible);
        ui.set_enabled(self.delete, buttons.can_delete);
        ui.set_visible(self.restore, buttons.restore_visible);
        ui.set_enabled(self.restore, buttons.can_restore);
    }

    fn reconcile_dialogs(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        view: &SelectionSnapshot,
    ) {
        let current = key(view);
        if let Some(error) = &view.error {
            dialogs.close(ui, &mut self.delete_dialog);
            dialogs.close(ui, &mut self.operation_wait);
            dialogs.close(ui, &mut self.enter_wait);
            self.enter_sent = None;
            self.restore_sent = None;
            if self.error_dialog == 0 {
                self.error_dialog = dialogs.make(ui, DialogData::message(&error.message));
            }
            return;
        }
        dialogs.close(ui, &mut self.error_dialog);
        if view.lifecycle == SelectionLifecycle::EnteringWorld {
            dialogs.close(ui, &mut self.delete_dialog);
            dialogs.close(ui, &mut self.operation_wait);
            self.restore_sent = None;
            self.enter_sent = None;
            dialogs.ensure(
                ui,
                &mut self.enter_wait,
                DialogData::wait(&self.strings.entering_world),
            );
            return;
        }
        // An enter not answered yet keeps its wait.
        if self.enter_sent != Some(current) {
            self.enter_sent = None;
            dialogs.close(ui, &mut self.enter_wait);
        }
        let pending = (view.pending_delete_character_id != 0)
            .then(|| {
                view.entries
                    .iter()
                    .find(|e| e.character_id == view.pending_delete_character_id)
            })
            .flatten();
        match pending {
            Some(entry) => {
                if self.delete_dialog == 0 {
                    let text = (self.strings.delete_confirmation)(&entry.name);
                    self.delete_dialog =
                        dialogs.make(ui, DialogData::confirmation_text_input(&text));
                }
            }
            None => dialogs.close(ui, &mut self.delete_dialog),
        }
        if self.restore_sent.is_some() && self.restore_sent != Some(current) {
            self.restore_sent = None;
        }
        if self.restore_sent.is_some()
            || matches!(
                view.operation,
                SelectionOperation::DeleteRequested
                    | SelectionOperation::DeleteAcknowledged
                    | SelectionOperation::RestoreRequested
            )
        {
            dialogs.ensure(
                ui,
                &mut self.operation_wait,
                DialogData::wait(&self.strings.please_wait),
            );
        } else {
            dialogs.close(ui, &mut self.operation_wait);
        }
    }

    fn deactivate(&mut self, ui: &mut UiRoot, dialogs: &mut Dialogs<'_>) {
        if self.active {
            self.active = false;
            ui.set_visible(self.root, false);
            ui.revoke_fixed_canvas(CANVAS_OWNER);
        }
        if !self.rows.is_empty() {
            self.rows.clear();
            ui.with::<ListBox, _>(self.list, |l, ui| l.flush(ui));
        }
        for context in [
            &mut self.delete_dialog,
            &mut self.operation_wait,
            &mut self.enter_wait,
            &mut self.error_dialog,
            &mut self.exit_dialog,
        ] {
            dialogs.close(ui, context);
        }
        self.enter_sent = None;
        self.restore_sent = None;
        self.shown = None;
    }

    /// Take the screen down (the window goes).
    pub fn dispose(&mut self, ui: &mut UiRoot, dialogs: &mut Dialogs<'_>) {
        self.deactivate(ui, dialogs);
        ui.destroy(self.root);
    }
}

fn row_color(entry: &SelectionEntry) -> Color {
    if entry.is_pending_delete() {
        PENDING_DELETE_COLOR
    } else {
        Color::WHITE
    }
}

fn lifecycle(command: LifecycleCommand) -> PreGameRequest {
    PreGameRequest::Session(SessionCommand::Lifecycle(command))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rows_share_the_list_by_the_larger_of_roster_and_slots_at_least_a_tenth() {
        // OpenAC `CharacterManagementUiControllerTests.
        // RowHeight_UsesAllowedSlotsAndClampsAtOneTenthForLargeRosters`.
        assert_eq!(row_height(320.0, 3, 5), 64);
        assert_eq!(row_height(319.0, 5, 5), 63);
        assert_eq!(row_height(319.0, 11, 5), 31);
        assert_eq!(row_height(320.0, 12, 5), 32);
        assert_eq!(row_height(320.0, 0, 0), 32);
    }
}
