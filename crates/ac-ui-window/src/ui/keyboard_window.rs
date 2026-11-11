//! The Configure Keyboard screen in the game window, as OpenAC's
//! `RetailUiRuntime.MountKeyboardConfig` mounts it (layout 0x21000009,
//! centred, hidden until the key map's Configure Keyboard key or the
//! options window's button), with its dialogs (the capture instructions,
//! the overwrite question, the refusal message, the profile menu and the
//! profile name) on their own queue, and the profiles and bindings it
//! saves through the window's storage. The window that owns the key
//! dispatcher takes the screen's requests ([`KeyboardRequest`]): live
//! bindings and key captures.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use ac_dat::objects::StringTable;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::import::{LayoutSource, import, import_infos, import_infos_rooted};
use ac_ui::input::key_names::RetailKeyNames;
use ac_ui::input::keymap_file::{self, KeymapSaveResult, KeymapSaveStatus};
use ac_ui::input::unmapped::RetailUnmappedKeyBindings;
use ac_ui::input::{KeyBindings, KeyChord, action_map};
use ac_ui::panels::DatRowTemplates;
use ac_ui::panels::dialog_factory::{DialogData, DialogFactory, property};
use ac_ui::panels::keyboard_config::{
    self, CaptureReply, KeyboardConfigController, KeyboardConfigOutput, KeyboardConfigSources,
    TEMPLATE_STRING_TABLE_ID,
};
use ac_ui::root::ElementMessage;
#[cfg(not(target_arch = "wasm32"))]
use ac_ui::storage::FileUiStorage;
use ac_ui::storage::{UiStorage, keys};
use ac_ui::strings::{StringResolver, compute_hash};
use ac_ui::tree::ElementId;

use super::mount::{Chrome, MountOptions, mount_window};
use super::window::{WindowUi, make_dialog};

/// The screen's window name.
pub const KEYBOARD_WINDOW: &str = "keyboard-config";
/// The queue its dialogs go on.
pub const DIALOG_QUEUE_KEY: u32 = 0x1000_0001;

/// Where the screen saves: the bindings file (the mirror of the active
/// profile) and the folder of the `.keymap` profiles.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KeyboardFiles {
    /// The player's bindings file.
    pub bindings_file: PathBuf,
    /// The folder of the keymap profiles.
    pub keymap_directory: PathBuf,
}

impl KeyboardFiles {
    /// The bindings of retail actions the client has none for, beside the
    /// bindings file (OpenAC `UnmappedKeyBindingsPath`).
    #[must_use]
    pub fn unmapped_file(&self) -> PathBuf {
        unmapped_path(&self.bindings_file)
    }

    /// The storage over these files, for [`WindowUi::build_with`].
    #[cfg(not(target_arch = "wasm32"))]
    #[must_use]
    pub fn storage(&self) -> FileUiStorage {
        FileUiStorage::for_key_bindings(&self.bindings_file, &self.keymap_directory)
    }
}

/// `keybinds.json` → `keybinds.unmapped.json`, beside it.
#[must_use]
pub fn unmapped_path(bindings_file: &Path) -> PathBuf {
    ac_ui::input::unmapped::unmapped_path(bindings_file)
}

/// What the screen asks of the window owning the key dispatcher.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum KeyboardRequest {
    /// These bindings are live now.
    Apply(KeyBindings),
    /// Report the next key as a capture.
    BeginCapture,
}

#[derive(Debug, Clone)]
enum KeyboardDialog {
    Instructions,
    Message,
    Overwrite,
    Load(Vec<String>),
    SaveAs,
    ProfileOverwrite(String),
}

/// The screen's state in the window.
#[derive(Debug, Default)]
pub struct KeyboardUi {
    unmapped: RetailUnmappedKeyBindings,
    requests: Vec<KeyboardRequest>,
    dialogs: DialogFactory<KeyboardDialog>,
    instructions: u32,
}

impl KeyboardUi {
    /// The roots of the screen's open dialogs (the UI probe lists them).
    pub(super) fn open_dialog_roots(&self) -> Vec<ElementId> {
        self.dialogs
            .open_contexts()
            .iter()
            .filter_map(|&c| self.dialogs.view(c).map(|v| v.root()))
            .collect()
    }
}

impl WindowUi {
    /// The window's bindings: the screen is built on the first call and
    /// shows these afterwards. It saves through the window's storage
    /// ([`WindowUi::build_with`]); a window without one saves nothing.
    pub fn attach_key_bindings(&mut self, bindings: &KeyBindings) {
        let unmapped = self
            .storage
            .as_deref()
            .map(|storage| {
                let (u, error) =
                    RetailUnmappedKeyBindings::load_from(storage, keys::UNMAPPED_KEY_BINDINGS);
                if let Some(error) = error {
                    self.notices.push(format!("keyboard config: {error}"));
                }
                u
            })
            .unwrap_or_default();
        self.keyboard.unmapped = unmapped.clone();
        if self.host.has_controller(KEYBOARD_WINDOW) {
            self.host
                .with_controller::<KeyboardConfigController, _>(KEYBOARD_WINDOW, |c, ui| {
                    c.reload(ui, bindings.clone(), unmapped);
                });
            return;
        }
        match self.mount_keyboard_config(bindings.clone(), unmapped) {
            Ok(()) => self.mounted.push(KEYBOARD_WINDOW),
            Err(e) => self.notices.push(format!("ui: {KEYBOARD_WINDOW}: {e}")),
        }
    }

    /// OpenAC `RetailUiRuntime.MountKeyboardConfig`.
    fn mount_keyboard_config(
        &mut self,
        bindings: KeyBindings,
        unmapped: RetailUnmappedKeyBindings,
    ) -> Result<(), String> {
        let info = import_infos(&self.content, keyboard_config::LAYOUT_ID)
            .ok_or("layout 0x21000009 is not in the dats")?;
        let snapshot = action_map::read(self.content.dats())
            .map_err(|e| e.to_string())?
            .ok_or("the dat action map 0x26000000 is not in the dats")?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import(
            &mut self.host.root,
            &self.content,
            keyboard_config::LAYOUT_ID,
            font.clone(),
        )
        .ok_or("layout 0x21000009 did not build")?;
        let dats = self.content.dats().clone();
        let template_strings = std::rc::Rc::new(StringResolver::new(move |id| {
            dats.get::<StringTable>(id).ok().flatten()
        }));
        let dats = self.content.dats().clone();
        let name_strings = std::rc::Rc::new(StringResolver::new(move |id| {
            dats.get::<StringTable>(id).ok().flatten()
        }));
        for strings in [&template_strings, &name_strings] {
            self.miss_caches
                .strings
                .push(std::rc::Rc::downgrade(strings));
        }
        let names = RetailKeyNames::new(
            Box::new(move |table, hash| name_strings.resolve(table, hash, 0)),
            (self.os_key_names)(),
        );
        let filename = self
            .storage
            .as_deref()
            .map(keymap_file::active_profile_name);
        let content = &self.content;
        let mut rows = DatRowTemplates::new(content, font);
        let strings = content.strings();
        let resolve_string = |table: u32, hash: u32| strings.resolve(table, hash, 0);
        let template_font = |l: u32, e: u32| {
            let info = import_infos_rooted(content, l, e)?;
            (info.font_did != 0)
                .then(|| content.font(info.font_did))
                .flatten()
        };
        let controller = KeyboardConfigController::bind(
            &mut self.host.root,
            layout.root,
            KeyboardConfigSources {
                snapshot,
                rows: &mut rows,
                resolve_string: &resolve_string,
                templates: Box::new(move |key, variables| {
                    template_strings.resolve_template(TEMPLATE_STRING_TABLE_ID, key, variables)
                }),
                names,
                template_font: &template_font,
                bindings,
                unmapped,
                filename,
            },
        )
        .ok_or("the window root 0x1000001F did not build")?;
        let ui = &mut self.host.root;
        let (sw, sh) = ui.size();
        let (w, h) = ui
            .element(layout.root)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        mount_window(
            ui,
            KEYBOARD_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::Imported,
                left: ((sw - w) * 0.5).max(0.0),
                top: ((sh - h) * 0.5).max(0.0),
                visible: false,
                dat_constraint_source: Some(info),
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        self.host
            .attach_controller(KEYBOARD_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())
    }

    /// The Configure Keyboard key, or the options window's button: the
    /// screen opens or closes.
    pub fn toggle_keyboard_config(&mut self) -> bool {
        if !self.host.has_controller(KEYBOARD_WINDOW) {
            return false;
        }
        self.host.root.toggle_window(KEYBOARD_WINDOW);
        true
    }

    /// What the screen asked of the key dispatcher since the last call.
    pub fn take_keyboard_requests(&mut self) -> Vec<KeyboardRequest> {
        std::mem::take(&mut self.keyboard.requests)
    }

    /// The working bindings of the screen (none when it is not mounted).
    #[must_use]
    pub fn keyboard_bindings(&mut self) -> Option<KeyBindings> {
        self.host
            .with_controller::<KeyboardConfigController, _>(KEYBOARD_WINDOW, |c, _| {
                c.bindings().clone()
            })
    }

    /// A key capture ended (`None`: Escape); true when the dispatcher must
    /// capture again (the key cannot be bound).
    pub fn keyboard_captured(&mut self, chord: Option<KeyChord>) -> bool {
        let reply = self
            .host
            .with_controller::<KeyboardConfigController, _>(KEYBOARD_WINDOW, |c, ui| {
                c.captured(ui, chord)
            })
            .unwrap_or(CaptureReply::NotCapturing);
        match reply {
            CaptureReply::StillCapturing => true,
            CaptureReply::Ended => {
                self.close_instructions();
                false
            }
            CaptureReply::NotCapturing => false,
        }
    }

    fn close_instructions(&mut self) {
        let context = std::mem::take(&mut self.keyboard.instructions);
        if context == 0 {
            return;
        }
        let layout_id = self.dialog_layout;
        let Self {
            host,
            content,
            keyboard,
            ..
        } = self;
        if keyboard.dialogs.close(&mut host.root, context) {
            drop(keyboard.dialogs.take_closed());
            keyboard.dialogs.settle(&mut host.root, &mut |ui, kind| {
                make_dialog(ui, content, layout_id, kind)
            });
        }
    }

    /// A request of the screen.
    pub(super) fn keyboard_output(&mut self, output: KeyboardConfigOutput) {
        match output {
            KeyboardConfigOutput::Apply { bindings, unmapped } => {
                self.keyboard.unmapped = unmapped;
                self.keyboard
                    .requests
                    .push(KeyboardRequest::Apply(bindings));
            }
            KeyboardConfigOutput::BeginCapture { action } => self.begin_capture(&action),
            KeyboardConfigOutput::ShowMessage(text) => {
                self.keyboard_dialog(DialogData::message(&text), KeyboardDialog::Message);
            }
            KeyboardConfigOutput::ConfirmOverwrite(text) => {
                self.keyboard_dialog(DialogData::confirmation(&text), KeyboardDialog::Overwrite);
            }
            KeyboardConfigOutput::Save => self.save_active_keymap(),
            KeyboardConfigOutput::Close => {
                self.toggle_keyboard_config();
            }
            KeyboardConfigOutput::OpenLoad => self.open_load_keymap(),
            KeyboardConfigOutput::OpenSaveAs => {
                self.keyboard_dialog(
                    DialogData::confirmation_text_input(""),
                    KeyboardDialog::SaveAs,
                );
            }
        }
    }

    /// OpenAC's `OpenCaptureInstructions`: the authored instruction text
    /// in a wait dialog, then the capture; no text, no capture (no
    /// invented English).
    fn begin_capture(&mut self, action: &str) {
        let mut variables = HashMap::new();
        variables.insert(compute_hash("ACTION"), action.to_owned());
        let text = self.content.strings().resolve_template(
            TEMPLATE_STRING_TABLE_ID,
            "ID_ActionKeyMap_MapInstructions",
            &variables,
        );
        let Some(text) = text else {
            self.host
                .with_controller::<KeyboardConfigController, _>(KEYBOARD_WINDOW, |c, _| {
                    c.capture_refused();
                });
            return;
        };
        self.keyboard.instructions =
            self.keyboard_dialog(DialogData::wait(&text), KeyboardDialog::Instructions);
        self.keyboard.requests.push(KeyboardRequest::BeginCapture);
    }

    fn keyboard_dialog(&mut self, data: DialogData, tag: KeyboardDialog) -> u32 {
        let layout_id = self.dialog_layout;
        let Self {
            host,
            content,
            keyboard,
            ..
        } = self;
        keyboard.dialogs.make(
            &mut host.root,
            data.on_queue(DIALOG_QUEUE_KEY).priority(),
            tag,
            &mut |ui, kind| make_dialog(ui, content, layout_id, kind),
        )
    }

    /// A message from an element: true when it answered one of the
    /// screen's dialogs.
    pub(super) fn keyboard_dialog_message(
        &mut self,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let layout_id = self.dialog_layout;
        let closed = {
            let Self {
                host,
                content,
                keyboard,
                ..
            } = self;
            if !keyboard.dialogs.handle(&mut host.root, element, message) {
                return false;
            }
            let closed = keyboard.dialogs.take_closed();
            keyboard.dialogs.settle(&mut host.root, &mut |ui, kind| {
                make_dialog(ui, content, layout_id, kind)
            });
            closed
        };
        for c in closed {
            self.keyboard_dialog_closed(c.context, &c.data, c.tag);
        }
        true
    }

    fn keyboard_dialog_closed(&mut self, context: u32, data: &DialogData, tag: KeyboardDialog) {
        match tag {
            KeyboardDialog::Instructions => {
                if self.keyboard.instructions == context {
                    self.keyboard.instructions = 0;
                }
            }
            KeyboardDialog::Message => {}
            KeyboardDialog::Overwrite => {
                let accepted = data.get_bool(property::CONFIRMATION_RESULT);
                self.host.with_controller::<KeyboardConfigController, _>(
                    KEYBOARD_WINDOW,
                    |c, ui| {
                        c.overwrite_answered(ui, accepted);
                    },
                );
            }
            KeyboardDialog::Load(files) => {
                let choice = data.get_i32(property::MENU_SELECTION, -1);
                if let Some(name) = usize::try_from(choice).ok().and_then(|i| files.get(i)) {
                    self.load_keymap(name);
                }
            }
            KeyboardDialog::SaveAs => {
                let name = data
                    .get_str(property::TEXT_INPUT_RESULT)
                    .unwrap_or_default()
                    .to_owned();
                if !name.is_empty() {
                    self.save_keymap(&name, false);
                }
            }
            KeyboardDialog::ProfileOverwrite(name) => {
                if data.get_bool(property::CONFIRMATION_RESULT) {
                    self.save_keymap(&name, true);
                }
            }
        }
    }

    /// One frame of the screen's dialogs (a retried one is made again).
    pub(super) fn tick_keyboard_dialogs(&mut self) {
        let layout_id = self.dialog_layout;
        let Self {
            host,
            content,
            keyboard,
            ..
        } = self;
        keyboard.dialogs.tick(&mut host.root, &mut |ui, kind| {
            make_dialog(ui, content, layout_id, kind)
        });
    }

    fn open_load_keymap(&mut self) {
        let Some(storage) = self.storage.as_deref() else {
            return;
        };
        let names = match keymap_file::list_profiles(storage) {
            Ok(names) => names,
            Err(e) => {
                self.notices.push(format!("keyboard config: keymaps: {e}"));
                return;
            }
        };
        let current = keymap_file::active_profile_name(storage);
        let selected = names
            .iter()
            .position(|n| n.eq_ignore_ascii_case(&current))
            .unwrap_or(0);
        let data = DialogData::confirmation_menu(&names, i32::try_from(selected).unwrap_or(0));
        // The menu goes on the queue without priority (OpenAC).
        let layout_id = self.dialog_layout;
        let Self {
            host,
            content,
            keyboard,
            ..
        } = self;
        keyboard.dialogs.make(
            &mut host.root,
            data.on_queue(DIALOG_QUEUE_KEY),
            KeyboardDialog::Load(names),
            &mut |ui, kind| make_dialog(ui, content, layout_id, kind),
        );
    }

    fn load_keymap(&mut self, name: &str) {
        if self.storage.is_none() {
            return;
        }
        let Some(base) = self.keyboard_bindings() else {
            return;
        };
        let Some(storage) = self.storage.as_deref_mut() else {
            return;
        };
        let loaded = match keymap_file::load_profile(storage, name, &base) {
            Ok(loaded) => loaded,
            Err(e) => {
                self.notices
                    .push(format!("keyboard config: keymap load failed: {e}"));
                return;
            }
        };
        self.keyboard
            .requests
            .push(KeyboardRequest::Apply(loaded.clone()));
        if let Err(e) = save_mirrors(storage, &loaded, &self.keyboard.unmapped) {
            self.notices.push(format!(
                "keyboard config: loaded profile mirror failed: {e}"
            ));
        }
        let unmapped = self.keyboard.unmapped.clone();
        let current = keymap_file::active_profile_name(storage);
        self.host
            .with_controller::<KeyboardConfigController, _>(KEYBOARD_WINDOW, |c, ui| {
                c.reload(ui, loaded, unmapped);
                c.set_filename(ui, &current);
            });
    }

    /// OK with changes: the active profile and the mirrors.
    fn save_active_keymap(&mut self) {
        let Some(storage) = self.storage.as_deref() else {
            return;
        };
        let name = keymap_file::active_profile_name(storage);
        self.save_keymap(&name, true);
    }

    fn save_keymap(&mut self, name: &str, overwrite: bool) {
        if self.storage.is_none() {
            return;
        }
        let Some(bindings) = self.keyboard_bindings() else {
            return;
        };
        let Some(storage) = self.storage.as_deref_mut() else {
            return;
        };
        let result: KeymapSaveResult =
            keymap_file::save_profile(storage, name, &bindings, overwrite);
        match result.status {
            KeymapSaveStatus::Saved => {
                if let Err(e) = save_mirrors(storage, &bindings, &self.keyboard.unmapped) {
                    self.notices
                        .push(format!("keyboard config: JSON mirror save failed: {e}"));
                }
                let current = keymap_file::active_profile_name(storage);
                self.host.with_controller::<KeyboardConfigController, _>(
                    KEYBOARD_WINDOW,
                    |c, ui| {
                        c.set_filename(ui, &current);
                    },
                );
            }
            KeymapSaveStatus::Exists => {
                if let Some(text) =
                    self.keymap_template("ID_KeyMapOverwriteKeymap_Label", &result.file_name)
                {
                    self.keyboard_dialog(
                        DialogData::confirmation(&text),
                        KeyboardDialog::ProfileOverwrite(name.to_owned()),
                    );
                }
            }
            KeymapSaveStatus::ReadOnly => {
                if let Some(text) = self.keymap_template(
                    "ID_KeyMapCantOverwriteReadOnlyKeymap_Label",
                    &result.file_name,
                ) {
                    self.keyboard_dialog(DialogData::message(&text), KeyboardDialog::Message);
                }
            }
            KeymapSaveStatus::InvalidName | KeymapSaveStatus::Failed => {
                self.notices.push(format!(
                    "keyboard config: keymap save failed ({:?}): {}",
                    result.status,
                    result.error.unwrap_or_default()
                ));
            }
        }
    }

    /// A keymap message template with the file name in every variable it
    /// may name (OpenAC `ResolveKeymapTemplate`).
    fn keymap_template(&self, key: &str, file_name: &str) -> Option<String> {
        let variables: HashMap<u32, String> = ["LABEL", "KEYMAP", "FILENAME", "NAME", "VALUE"]
            .iter()
            .map(|v| (compute_hash(v), file_name.to_owned()))
            .collect();
        self.content
            .strings()
            .resolve_template(TEMPLATE_STRING_TABLE_ID, key, &variables)
    }
}

/// The bindings file and the unmapped file mirror the active profile.
fn save_mirrors(
    storage: &mut dyn UiStorage,
    bindings: &KeyBindings,
    unmapped: &RetailUnmappedKeyBindings,
) -> Result<(), String> {
    bindings
        .save_to(storage, keys::KEY_BINDINGS)
        .map_err(|e| e.to_string())?;
    unmapped
        .save_to(storage, keys::UNMAPPED_KEY_BINDINGS)
        .map_err(|e| e.to_string())
}
