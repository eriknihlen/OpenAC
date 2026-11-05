//! The Configure Keyboard screen (layout 0x21000009): one tab per action
//! class (movement, camera, combat, interface, character settings,
//! emotes), each a list of the dat action map's bindable actions grouped
//! under their input map's name, each row with three key buttons.
//!
//! A key button's click captures the next key (an instruction dialog says
//! so; Escape cancels), its right click erases that slot. A captured key
//! already bound to an action that conflicts (the same input map, or one
//! the dat lists as conflicting) asks before taking it from there; a key
//! the client keeps for itself is refused with a message. Changes are live
//! at once; Revert puts back the saved keys, Defaults the dat's, OK saves
//! and closes, Cancel reverts and closes. Load and Save As pick a keymap
//! profile.
//!
//! The controller keeps a working copy of the bindings and asks the client
//! for everything outside the panel through [`KeyboardConfigOutput`].

use std::collections::HashMap;
use std::sync::Arc;

use crate::draw::Color;
use crate::font::UiFont;
use crate::geometry::AnchorEdges;
use crate::input::action_map::{
    INPUT_MAP_NAMES, INPUT_MAP_STRING_TABLE_ID, RetailActionClass, RetailActionMapRow,
    RetailActionMapSnapshot, RetailKeyChord,
};
use crate::input::key_names::{RetailKeyNames, is_empty};
use crate::input::unmapped::RetailUnmappedKeyBindings;
use crate::input::{
    ActivationType, Binding, InputAction, InputKey, InputScope, KeyBindings, KeyChord, identity,
    scan_codes,
};
use crate::panels::items::bind_click;
use crate::panels::options::STORE_ONLY_CAPTION_COLOR;
use crate::panels::{PanelOutput, RowTemplates, link_list_scrollbar};
use crate::root::{ElementMessage, UiOutput, UiRoot};
use crate::strings::compute_hash;
use crate::text::Line;
use crate::tree::{Element, ElementId};
use crate::widgets::{Button, ListBox, TabPanel, Text};

/// The screen's layout.
pub const LAYOUT_ID: u32 = 0x2100_0009;
/// The window's root element.
pub const WINDOW_ROOT_ID: u32 = 0x1000_001F;
/// Load a keymap profile.
pub const LOAD_BUTTON_ID: u32 = 0x1000_0027;
/// The active profile's file name.
pub const FILENAME_LABEL_ID: u32 = 0x1000_0028;
/// Save the bindings as a new profile.
pub const SAVE_AS_BUTTON_ID: u32 = 0x1000_0029;
/// Every row to the dat's default keys.
pub const DEFAULTS_BUTTON_ID: u32 = 0x1000_002A;
/// Every changed row back to its saved keys.
pub const REVERT_BUTTON_ID: u32 = 0x1000_002B;
/// Save and close.
pub const OK_BUTTON_ID: u32 = 0x1000_002C;
/// Revert and close.
pub const CANCEL_BUTTON_ID: u32 = 0x1000_002D;
/// Each page's list.
pub const LIST_BOX_ID: u32 = 0x1000_0025;
/// Each page's scrollbar.
pub const SCROLLBAR_ID: u32 = 0x1000_0026;
/// The tab control.
pub const TAB_HOST_ID: u32 = 0x1000_049B;
/// The key buttons of a row.
pub const KEY_BUTTON_IDS: [u32; 3] = [0x1000_0030, 0x1000_0031, 0x1000_0032];
/// The list's header template.
pub const HEADER_TEMPLATE_INDEX: usize = 0;
/// The list's action row template.
pub const ROW_TEMPLATE_INDEX: usize = 1;
/// The string table of the screen's templates (labels, tooltips, dialogs).
pub const TEMPLATE_STRING_TABLE_ID: u32 = 0x2300_0004;
/// The width of a row's caption, pixels.
pub const CAPTION_WIDTH: f32 = 260.0;

/// The pages and the class each lists.
pub const PAGES: [(u32, RetailActionClass); 6] = [
    (0x1000_049D, RetailActionClass::Movement),
    (0x1000_049F, RetailActionClass::Camera),
    (0x1000_04A1, RetailActionClass::Combat),
    (0x1000_04A3, RetailActionClass::Ui),
    (0x1000_0211, RetailActionClass::CharacterSettings),
    (0x1000_04A5, RetailActionClass::Emote),
];

/// What the screen asks the client to do.
#[derive(Debug, Clone, PartialEq)]
pub enum KeyboardConfigOutput {
    /// The working bindings changed: make them live (the dispatcher's).
    Apply {
        /// The bindings of the client's actions.
        bindings: KeyBindings,
        /// The bindings of retail actions the client has none for.
        unmapped: RetailUnmappedKeyBindings,
    },
    /// Capture the next key for the action named `action` (show the
    /// instruction dialog, then answer with
    /// [`KeyboardConfigController::captured`], or
    /// [`KeyboardConfigController::capture_refused`] when the dialog cannot
    /// show).
    BeginCapture {
        /// The action's caption.
        action: String,
    },
    /// Show this message.
    ShowMessage(String),
    /// Ask this yes/no question; answer with
    /// [`KeyboardConfigController::overwrite_answered`].
    ConfirmOverwrite(String),
    /// Save the bindings to the active profile.
    Save,
    /// Close the screen.
    Close,
    /// Let the player pick a profile to load; then
    /// [`KeyboardConfigController::reload`].
    OpenLoad,
    /// Ask the player for a profile name to save as; then
    /// [`KeyboardConfigController::set_filename`].
    OpenSaveAs,
}

/// A template lookup in the screen's string table: the key and its
/// variables (by hashed name).
pub type ResolveTemplate = dyn Fn(&str, &HashMap<u32, String>) -> Option<String>;

/// One action row.
#[derive(Debug, Clone)]
pub struct KeyRow {
    /// Input map.
    pub input_map: u32,
    /// Action id.
    pub action_id: u32,
    /// The client's action for it.
    pub mapped: Option<InputAction>,
    /// The caption.
    pub label: Option<String>,
    /// The caption element.
    pub caption: ElementId,
    /// The key buttons.
    pub key_buttons: Vec<ElementId>,
    /// The keys now (live, maybe unsaved).
    pub current: Vec<KeyChord>,
    /// The keys last saved.
    pub saved: Vec<KeyChord>,
    /// The dat's default keys.
    pub defaults: Vec<KeyChord>,
    /// How a new key fires and where (its first live binding's, else the
    /// retail identity's).
    template: (ActivationType, InputScope),
}

impl KeyRow {
    /// The keys differ from the saved ones.
    #[must_use]
    pub fn changed(&self) -> bool {
        self.current != self.saved
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Capture {
    row: usize,
    slot: usize,
}

#[derive(Debug, Clone, PartialEq)]
struct PendingOverwrite {
    row: usize,
    slot: usize,
    chord: KeyChord,
    conflicts: Vec<usize>,
}

/// See the module documentation.
pub struct KeyboardConfigController {
    rows: Vec<KeyRow>,
    snapshot: RetailActionMapSnapshot,
    bindings: KeyBindings,
    unmapped: RetailUnmappedKeyBindings,
    names: RetailKeyNames,
    templates: Box<ResolveTemplate>,
    capture: Option<Capture>,
    pending: Option<PendingOverwrite>,
    revert: Option<ElementId>,
    filename: Option<ElementId>,
    buttons: HashMap<ElementId, ScreenButton>,
}

impl std::fmt::Debug for KeyboardConfigController {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("KeyboardConfigController")
            .field("rows", &self.rows.len())
            .field("capture", &self.capture)
            .finish_non_exhaustive()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ScreenButton {
    Load,
    SaveAs,
    Defaults,
    Revert,
    Ok,
    Cancel,
}

/// What [`KeyboardConfigController::captured`] needs from the client.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureReply {
    /// No capture was open.
    NotCapturing,
    /// The key cannot be bound (a left or right mouse button, a device
    /// the key map cannot name): keep capturing, the dialog stays.
    StillCapturing,
    /// The capture ended: close the instruction dialog.
    Ended,
}

/// What the screen is bound with.
#[allow(missing_debug_implementations)]
pub struct KeyboardConfigSources<'a> {
    /// The dat's action map.
    pub snapshot: RetailActionMapSnapshot,
    /// Builds the lists' row templates.
    pub rows: &'a mut dyn RowTemplates,
    /// A string by table and hash.
    pub resolve_string: &'a dyn Fn(u32, u32) -> Option<String>,
    /// The screen's templates.
    pub templates: Box<ResolveTemplate>,
    /// The key names.
    pub names: RetailKeyNames,
    /// The font of a row template (layout, element), asked once per
    /// template.
    pub template_font: &'a dyn Fn(u32, u32) -> Option<Arc<UiFont>>,
    /// The bindings now.
    pub bindings: KeyBindings,
    /// The unmapped bindings now.
    pub unmapped: RetailUnmappedKeyBindings,
    /// The active profile's file name.
    pub filename: Option<String>,
}

impl KeyboardConfigController {
    /// Bind the screen under `root` (`None` without its window root).
    pub fn bind(
        ui: &mut UiRoot,
        root: ElementId,
        sources: KeyboardConfigSources<'_>,
    ) -> Option<Self> {
        if ui.find_descendant(root, WINDOW_ROOT_ID).is_none()
            && ui.element(root).map(|e| e.dat_element_id) != Some(WINDOW_ROOT_ID)
        {
            return None;
        }
        let KeyboardConfigSources {
            snapshot,
            rows: templates_builder,
            resolve_string,
            templates,
            names,
            template_font,
            bindings,
            unmapped,
            filename,
        } = sources;
        let mut c = Self {
            rows: Vec::new(),
            snapshot: snapshot.clone(),
            bindings,
            unmapped,
            names,
            templates,
            capture: None,
            pending: None,
            revert: None,
            filename: None,
            buttons: HashMap::new(),
        };
        let mut fonts: HashMap<(u32, u32), Option<Arc<UiFont>>> = HashMap::new();
        for (page_id, class) in PAGES {
            let Some(page) = ui.find_descendant(root, page_id) else {
                continue;
            };
            let Some(list) = ui
                .find_descendant(page, LIST_BOX_ID)
                .filter(|l| ui.widget::<ListBox>(*l).is_some())
            else {
                continue;
            };
            link_list_scrollbar(ui, page, list);
            let mut class_rows: Vec<&RetailActionMapRow> =
                snapshot.rows.iter().filter(|r| r.class == class).collect();
            // Grouped by input map, in ascending order; dat order within.
            class_rows.sort_by_key(|r| r.input_map);
            let mut last_map = None;
            for row in class_rows {
                if last_map != Some(row.input_map) {
                    last_map = Some(row.input_map);
                    build_header(ui, list, row.input_map, templates_builder, resolve_string);
                }
                c.build_row(
                    ui,
                    list,
                    row,
                    templates_builder,
                    resolve_string,
                    &mut |l, e| {
                        fonts
                            .entry((l, e))
                            .or_insert_with(|| template_font(l, e))
                            .clone()
                    },
                );
            }
        }
        c.bind_screen_buttons(ui, root);
        if let Some(t) = ui.find_descendant(root, TAB_HOST_ID)
            && ui.widget::<TabPanel>(t).is_some()
        {
            ui.with::<TabPanel, _>(t, |w, ui| w.activate(ui, t));
        }
        if let Some(name) = filename {
            c.set_filename(ui, &name);
        }
        c.refresh_revert(ui);
        Some(c)
    }

    /// The rows, in build order.
    #[must_use]
    pub fn rows(&self) -> &[KeyRow] {
        &self.rows
    }

    /// The working bindings.
    #[must_use]
    pub fn bindings(&self) -> &KeyBindings {
        &self.bindings
    }

    /// The working unmapped bindings.
    #[must_use]
    pub fn unmapped(&self) -> &RetailUnmappedKeyBindings {
        &self.unmapped
    }

    /// Any row differs from its saved keys.
    #[must_use]
    pub fn changed(&self) -> bool {
        self.rows.iter().any(KeyRow::changed)
    }

    /// A capture is open.
    #[must_use]
    pub fn is_capturing(&self) -> bool {
        self.capture.is_some()
    }

    #[allow(clippy::too_many_arguments)]
    fn build_row(
        &mut self,
        ui: &mut UiRoot,
        list: ElementId,
        row: &RetailActionMapRow,
        templates: &mut dyn RowTemplates,
        resolve_string: &dyn Fn(u32, u32) -> Option<String>,
        font: &mut dyn FnMut(u32, u32) -> Option<Arc<UiFont>>,
    ) {
        let Some(t) = ui
            .widget::<ListBox>(list)
            .and_then(|b| b.templates.get(ROW_TEMPLATE_INDEX).copied())
        else {
            return;
        };
        let Some(built) = templates.build_row(ui, t.layout, t.element) else {
            return;
        };
        ui.with::<ListBox, _>(list, |b, ui| b.add_prebuilt_row(ui, list, built));
        let key_buttons: Vec<ElementId> = KEY_BUTTON_IDS
            .iter()
            .filter_map(|id| ui.find_descendant(built, *id))
            .filter(|b| ui.widget::<Button>(*b).is_some())
            .collect();
        for &b in &key_buttons {
            ui.with::<Button, _>(b, |w, _| {
                w.listeners.click = true;
                w.listeners.right_click = true;
            });
        }
        let label = resolve_string(INPUT_MAP_STRING_TABLE_ID, row.label_hash);
        let tooltip = resolve_string(INPUT_MAP_STRING_TABLE_ID, row.tooltip_hash);
        let mapped = identity::resolve(row.input_map, row.action_id);
        let height = ui.element(built).map_or(0.0, |e| e.height);
        let color = if mapped.is_some() {
            Color::WHITE
        } else {
            STORE_ONLY_CAPTION_COLOR
        };
        let mut caption_element = Element::at(0.0, 0.0, CAPTION_WIDTH, height)
            .with_anchors(AnchorEdges::LEFT | AnchorEdges::TOP);
        caption_element.click_through = true;
        caption_element.authored.tooltip_enabled = tooltip.is_some();
        caption_element.authored.tooltip_text = tooltip;
        let mut caption_text = Text::default();
        caption_text.padding = 2.0;
        caption_text.default_color = color;
        caption_text.font = font(t.layout, t.element);
        if let Some(label) = &label {
            caption_text.set_lines(vec![Line::new(label.clone(), color)]);
        }
        let caption = ui.add(built, caption_element, caption_text);

        let live: Vec<Binding> = mapped
            .map(|a| self.bindings.for_action(a).copied().collect())
            .unwrap_or_default();
        let template = live.first().map_or_else(
            || {
                (
                    identity::activation_for(row.input_map, row.action_id),
                    identity::scope_for_input_map(row.input_map),
                )
            },
            |b| (b.activation, b.scope),
        );
        let defaults = dat_defaults_to_chords(&row.default_bindings);
        let initial = match mapped {
            Some(_) => live.iter().map(|b| b.chord).collect(),
            None => {
                let stored = self.unmapped.get(row.input_map, row.action_id);
                if stored.is_empty() {
                    defaults.clone()
                } else {
                    stored.to_vec()
                }
            }
        };
        self.rows.push(KeyRow {
            input_map: row.input_map,
            action_id: row.action_id,
            mapped,
            label,
            caption,
            key_buttons,
            current: initial.clone(),
            saved: initial,
            defaults,
            template,
        });
        let index = self.rows.len() - 1;
        self.refresh_row(ui, index);
    }

    fn bind_screen_buttons(&mut self, ui: &mut UiRoot, root: ElementId) {
        for (id, which) in [
            (LOAD_BUTTON_ID, ScreenButton::Load),
            (SAVE_AS_BUTTON_ID, ScreenButton::SaveAs),
            (DEFAULTS_BUTTON_ID, ScreenButton::Defaults),
            (REVERT_BUTTON_ID, ScreenButton::Revert),
            (OK_BUTTON_ID, ScreenButton::Ok),
            (CANCEL_BUTTON_ID, ScreenButton::Cancel),
        ] {
            if let Some(b) = ui.find_descendant(root, id)
                && bind_click(ui, b)
            {
                self.buttons.insert(b, which);
                if which == ScreenButton::Revert {
                    self.revert = Some(b);
                }
            }
        }
        self.filename = ui
            .find_descendant(root, FILENAME_LABEL_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some());
    }

    /// Show `name` as the active profile's file name.
    pub fn set_filename(&mut self, ui: &mut UiRoot, name: &str) {
        if let Some(t) = self.filename.and_then(|t| ui.widget_mut::<Text>(t)) {
            let color = t.default_color;
            t.set_lines(vec![Line::new(name.to_owned(), color)]);
        }
    }

    /// The text of the active profile's file name.
    #[must_use]
    pub fn filename_text(&self, ui: &UiRoot) -> Option<String> {
        let t = ui.widget::<Text>(self.filename?)?;
        Some(
            t.content_lines()
                .into_iter()
                .map(|l| l.text)
                .collect::<Vec<_>>()
                .join("\n"),
        )
    }

    /// A widget sent a message; true when it was the screen's.
    pub fn handle(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if let Some((row, slot)) = self.key_button(element) {
            match message {
                ElementMessage::Clicked { .. } => self.begin_slot_capture(ui, row, slot),
                ElementMessage::RightClicked => self.erase_slot(ui, row, slot),
                _ => return false,
            }
            return true;
        }
        let Some(&which) = self.buttons.get(&element) else {
            return false;
        };
        if !matches!(message, ElementMessage::Clicked { .. }) {
            return false;
        }
        match which {
            ScreenButton::Load => emit(ui, KeyboardConfigOutput::OpenLoad),
            ScreenButton::SaveAs => emit(ui, KeyboardConfigOutput::OpenSaveAs),
            ScreenButton::Defaults => self.defaults(ui),
            ScreenButton::Revert => self.revert(ui),
            ScreenButton::Ok => {
                if self.changed() {
                    emit(ui, KeyboardConfigOutput::Save);
                }
                for row in &mut self.rows {
                    row.saved = row.current.clone();
                }
                self.refresh_revert(ui);
                emit(ui, KeyboardConfigOutput::Close);
            }
            ScreenButton::Cancel => {
                self.revert(ui);
                emit(ui, KeyboardConfigOutput::Close);
            }
        }
        true
    }

    fn key_button(&self, element: ElementId) -> Option<(usize, usize)> {
        self.rows.iter().enumerate().find_map(|(r, row)| {
            row.key_buttons
                .iter()
                .position(|b| *b == element)
                .map(|slot| (r, slot))
        })
    }

    fn begin_slot_capture(&mut self, ui: &mut UiRoot, row: usize, slot: usize) {
        let action = self.rows[row].label.clone().unwrap_or_default();
        self.capture = Some(Capture { row, slot });
        emit(ui, KeyboardConfigOutput::BeginCapture { action });
    }

    /// The instruction dialog could not show: the capture is not armed (as
    /// retail refuses).
    pub fn capture_refused(&mut self) {
        self.capture = None;
    }

    /// The capture ended with `chord` (`None`: Escape).
    pub fn captured(&mut self, ui: &mut UiRoot, chord: Option<KeyChord>) -> CaptureReply {
        let Some(capture) = self.capture else {
            return CaptureReply::NotCapturing;
        };
        if let Some(c) = chord
            && is_unsupported_capture(c)
        {
            return CaptureReply::StillCapturing;
        }
        self.capture = None;
        let Some(chord) = chord else {
            return CaptureReply::Ended;
        };
        if self.rows[capture.row].current.contains(&chord) {
            return CaptureReply::Ended;
        }
        match self.find_conflicts(chord, capture.row) {
            Conflict::NonBindable => {
                let mut v = HashMap::new();
                v.insert(compute_hash("KEY"), self.names.describe(chord));
                if let Some(text) = (self.templates)("ID_ActionKeyMap_NonUserBindableBinding", &v) {
                    emit(ui, KeyboardConfigOutput::ShowMessage(text));
                }
            }
            Conflict::Rows(conflicts) => {
                if let Some(message) = self.overwrite_message(chord, &conflicts) {
                    self.pending = Some(PendingOverwrite {
                        row: capture.row,
                        slot: capture.slot,
                        chord,
                        conflicts,
                    });
                    emit(ui, KeyboardConfigOutput::ConfirmOverwrite(message));
                }
            }
            Conflict::None => self.apply_slot(ui, capture.row, capture.slot, chord),
        }
        CaptureReply::Ended
    }

    /// The overwrite question was answered.
    pub fn overwrite_answered(&mut self, ui: &mut UiRoot, accepted: bool) {
        let Some(p) = self.pending.take() else {
            return;
        };
        if !accepted {
            return;
        }
        for &r in &p.conflicts {
            let value: Vec<KeyChord> = self.rows[r]
                .current
                .iter()
                .copied()
                .filter(|c| *c != p.chord)
                .collect();
            self.replace_slot_value(ui, r, value);
        }
        self.apply_slot(ui, p.row, p.slot, p.chord);
    }

    fn overwrite_message(&self, chord: KeyChord, conflicts: &[usize]) -> Option<String> {
        let key = self.names.describe(chord);
        if let [only] = conflicts {
            let action = self.rows[*only].label.clone()?;
            let mut v = HashMap::new();
            v.insert(compute_hash("KEY"), key);
            v.insert(compute_hash("ACTION"), action);
            return (self.templates)("ID_ActionKeyMap_OverwriteExistingBinding", &v);
        }
        let mut lines = Vec::with_capacity(conflicts.len());
        for &r in conflicts {
            let action = self.rows[r].label.clone()?;
            let mut v = HashMap::new();
            v.insert(compute_hash("ACTION"), action);
            v.insert(compute_hash("KEY"), key.clone());
            lines.push((self.templates)("ID_ActionKeyMap_Binding", &v)?);
        }
        let mut v = HashMap::new();
        v.insert(compute_hash("KEY"), key);
        v.insert(compute_hash("BINDINGS"), lines.join("\n"));
        (self.templates)("ID_ActionKeyMap_OverwriteExistingBindings", &v)
    }

    fn find_conflicts(&self, chord: KeyChord, exclude: usize) -> Conflict {
        // A key the client keeps for an action of its own (no retail
        // identity) cannot be taken.
        if self
            .bindings
            .all()
            .iter()
            .any(|b| b.chord == chord && identity::retail_identity(b.action).is_none())
        {
            return Conflict::NonBindable;
        }
        let map = self.rows[exclude].input_map;
        let rows: Vec<usize> = self
            .rows
            .iter()
            .enumerate()
            .filter(|(i, other)| {
                *i != exclude
                    && other.mapped.is_some()
                    && self.snapshot.input_maps_conflict(map, other.input_map)
                    && other.current.contains(&chord)
            })
            .map(|(i, _)| i)
            .collect();
        if rows.is_empty() {
            Conflict::None
        } else {
            Conflict::Rows(rows)
        }
    }

    fn apply_slot(&mut self, ui: &mut UiRoot, row: usize, slot: usize, chord: KeyChord) {
        let mut updated = self.rows[row].current.clone();
        let target = slot.min(updated.len());
        if target == updated.len() {
            updated.push(chord);
        } else {
            updated[target] = chord;
        }
        self.replace_slot_value(ui, row, updated);
    }

    fn erase_slot(&mut self, ui: &mut UiRoot, row: usize, slot: usize) {
        let current = &self.rows[row].current;
        if slot >= current.len() || is_empty(current[slot]) {
            return;
        }
        let mut updated = current.clone();
        updated.remove(slot);
        self.replace_slot_value(ui, row, updated);
    }

    /// The row's keys become `value` (trailing empty slots dropped), live.
    fn replace_slot_value(&mut self, ui: &mut UiRoot, row: usize, value: Vec<KeyChord>) {
        let last_real = value.iter().rposition(|c| !is_empty(*c));
        let value = match last_real {
            Some(i) => value[..=i].to_vec(),
            None => Vec::new(),
        };
        self.set_current(ui, row, value);
    }

    /// Set row `row`'s keys and make them live.
    fn set_current(&mut self, ui: &mut UiRoot, row: usize, value: Vec<KeyChord>) {
        self.rows[row].current = value;
        self.apply_row(row);
        self.refresh_row(ui, row);
        self.refresh_revert(ui);
        emit(
            ui,
            KeyboardConfigOutput::Apply {
                bindings: self.bindings.clone(),
                unmapped: self.unmapped.clone(),
            },
        );
    }

    /// Write row `row`'s current keys into the working bindings: the
    /// action's bindings go and the new ones are appended with the row's
    /// activation and scope.
    fn apply_row(&mut self, row: usize) {
        let r = &self.rows[row];
        let real: Vec<KeyChord> = r
            .current
            .iter()
            .copied()
            .filter(|c| !is_empty(*c))
            .collect();
        match r.mapped {
            Some(action) => {
                let (activation, scope) = r.template;
                let mut updated = KeyBindings::new();
                for b in self.bindings.all() {
                    if b.action != action {
                        updated.add(*b);
                    }
                }
                for chord in real {
                    updated.add(Binding {
                        chord,
                        action,
                        activation,
                        scope,
                    });
                }
                self.bindings = updated;
            }
            None => self.unmapped.set(r.input_map, r.action_id, &real),
        }
    }

    /// Defaults: every row to its dat default, live, not saved.
    pub fn defaults(&mut self, ui: &mut UiRoot) {
        for i in 0..self.rows.len() {
            self.rows[i].current = self.rows[i].defaults.clone();
            self.apply_row(i);
            self.refresh_row(ui, i);
        }
        self.after_bulk_change(ui);
    }

    /// Revert: every changed row back to its saved keys, live.
    pub fn revert(&mut self, ui: &mut UiRoot) {
        for i in 0..self.rows.len() {
            if !self.rows[i].changed() {
                continue;
            }
            self.rows[i].current = self.rows[i].saved.clone();
            self.apply_row(i);
            self.refresh_row(ui, i);
        }
        self.after_bulk_change(ui);
    }

    fn after_bulk_change(&mut self, ui: &mut UiRoot) {
        self.refresh_revert(ui);
        emit(
            ui,
            KeyboardConfigOutput::Apply {
                bindings: self.bindings.clone(),
                unmapped: self.unmapped.clone(),
            },
        );
    }

    /// A profile was loaded (or the bindings changed elsewhere): every row
    /// shows `bindings` as both its current and saved keys.
    pub fn reload(
        &mut self,
        ui: &mut UiRoot,
        bindings: KeyBindings,
        unmapped: RetailUnmappedKeyBindings,
    ) {
        self.bindings = bindings;
        self.unmapped = unmapped;
        for i in 0..self.rows.len() {
            let r = &self.rows[i];
            let chords: Vec<KeyChord> = match r.mapped {
                Some(action) => self.bindings.for_action(action).map(|b| b.chord).collect(),
                None => self.unmapped.get(r.input_map, r.action_id).to_vec(),
            };
            self.rows[i].current = chords.clone();
            self.rows[i].saved = chords;
            self.refresh_row(ui, i);
        }
        self.refresh_revert(ui);
    }

    fn refresh_revert(&self, ui: &mut UiRoot) {
        if let Some(b) = self.revert {
            ui.set_enabled(b, self.changed());
        }
    }

    fn refresh_row(&self, ui: &mut UiRoot, row: usize) {
        let r = &self.rows[row];
        for (i, &button) in r.key_buttons.iter().enumerate() {
            let bound = r.current.get(i).copied().filter(|c| !is_empty(*c));
            let (label, tooltip) = match bound {
                None => (
                    None,
                    (self.templates)("ID_ActionKeyMap_TT_NewBinding", &HashMap::new()),
                ),
                Some(chord) => {
                    let mut v = HashMap::new();
                    v.insert(compute_hash("LABEL"), self.names.describe(chord));
                    let label = (self.templates)("ID_ActionKeyMap_ButtonLabel", &v);
                    let tooltip = label.as_ref().and_then(|l| {
                        let mut v = HashMap::new();
                        v.insert(compute_hash("VALUE"), l.clone());
                        (self.templates)("ID_ActionKeyMap_TT_ExistingBinding", &v)
                    });
                    (label, tooltip)
                }
            };
            ui.with::<Button, _>(button, |b, _| {
                b.label = label;
                b.tooltip_text = tooltip;
            });
        }
    }
}

impl crate::host::PanelController for KeyboardConfigController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        self.handle(ui, element, message)
    }
}

enum Conflict {
    None,
    NonBindable,
    Rows(Vec<usize>),
}

fn emit(ui: &mut UiRoot, output: KeyboardConfigOutput) {
    ui.emit(UiOutput::Panel(PanelOutput::KeyboardConfig(output)));
}

fn build_header(
    ui: &mut UiRoot,
    list: ElementId,
    input_map: u32,
    templates: &mut dyn RowTemplates,
    resolve_string: &dyn Fn(u32, u32) -> Option<String>,
) {
    let Some(t) = ui
        .widget::<ListBox>(list)
        .and_then(|b| b.templates.get(HEADER_TEMPLATE_INDEX).copied())
    else {
        return;
    };
    let Some(header) = templates.build_row(ui, t.layout, t.element) else {
        return;
    };
    ui.with::<ListBox, _>(list, |b, ui| b.add_prebuilt_row(ui, list, header));
    let Some((_, key)) = INPUT_MAP_NAMES.iter().find(|(id, _)| *id == input_map) else {
        return;
    };
    // No invented English: a name the dat lacks leaves the header blank.
    let Some(label) = resolve_string(INPUT_MAP_STRING_TABLE_ID, compute_hash(key)) else {
        return;
    };
    let text = if ui.widget::<Text>(header).is_some() {
        Some(header)
    } else {
        ui.children(header)
            .into_iter()
            .find(|c| ui.widget::<Text>(*c).is_some())
    };
    if let Some(w) = text.and_then(|t| ui.widget_mut::<Text>(t)) {
        let color = w.default_color;
        w.set_lines(vec![Line::new(label, color)]);
    }
}

/// The dat's default keys as chords (keys the client cannot name are
/// left out).
#[must_use]
pub fn dat_defaults_to_chords(raw: &[RetailKeyChord]) -> Vec<KeyChord> {
    raw.iter()
        .filter_map(|c| {
            let key = scan_codes::to_key(c.scan, c.device)?;
            Some(KeyChord {
                key,
                modifiers: scan_codes::to_modifier_mask(c.modifier),
                device: u8::try_from(c.device).ok()?,
            })
        })
        .collect()
}

/// A key the screen cannot bind: another device than the keyboard and
/// mouse, the left or right mouse button, or one the key map file cannot
/// name.
#[must_use]
pub fn is_unsupported_capture(chord: KeyChord) -> bool {
    use crate::input::chord::mouse;
    if chord.device > 1 {
        return true;
    }
    if chord.device == 1
        && matches!(chord.key, InputKey::Mouse(b) if b == mouse::LEFT || b == mouse::RIGHT)
    {
        return true;
    }
    scan_codes::to_file_control(chord).is_none()
}
