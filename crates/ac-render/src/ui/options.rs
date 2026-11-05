//! The options window in the game window, as OpenAC's
//! `RetailUiRuntime.MountOptionsPanel` mounts it (a main panel in the
//! nine-piece frame at (150, 80), resizable downward), F11 and the toolbar's
//! button opening it, and what its requests do: settings changed in it are
//! applied here when they belong to the UI (the chat font and filters) and
//! handed to the window to apply and save; character options and the
//! Apply's block save go to the session; the Gameplay buttons act.

use ac_core::chat::{ChatWindowId, ChatWindowState, LogTextType};
use ac_dat::objects::EnumIdMap;
use ac_host::settings::{PlayerSettings, mouse_turning_macro};
use ac_runtime::character::option_id;
use ac_runtime::session::{LifecycleCommand, SessionCommand};
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::geometry::{AnchorEdges, ResizeEdges};
use ac_ui::import::{LayoutSource, import_infos_rooted, import_rooted};
use ac_ui::panels::PanelOutput;
use ac_ui::panels::chat::ChatWindowController;
use ac_ui::panels::main_panels::RESTORE_PREVIOUS_PROPERTY;
use ac_ui::panels::options::{
    self, LiveValues, OptionsOutput, OptionsPage, OptionsSources, OptionsWindowController, Section,
    chat_dat, resolutions,
};
use ac_ui::panels::panel_id;
use ac_ui::root::UiOutput;
use ac_ui::strings::compute_hash;

use super::mount::{Chrome, MountOptions, mount_window};
use super::window::{CHAT_WINDOW, DialogAction, UI_STRING_TABLE, WindowUi};

/// The options window's name.
pub const OPTIONS_WINDOW: &str = "options";

/// The interface enumeration's chat font map.
const INTERFACE_CATEGORY: u32 = 2;
const CHAT_FONT_MAP_KEY: u32 = 0x1000_0001;
const CHAT_FONT_FACES: [&str; 5] = [
    "Arial",
    "CourierNew",
    "PalatinoLinotype",
    "Tahoma",
    "TimesNewRoman",
];
const CHAT_FONT_SIZES: [&str; 5] = ["Tiny", "Small", "Medium", "Large", "XL"];

/// The chat font for face `face` and size `size` (indices of the options
/// menus): the font map the interface enumeration names, entry
/// `Chat_<face>_<size>`.
#[must_use]
pub fn chat_font_id(dats: &ac_dat::DatCollection, face: i32, size: i32) -> Option<u32> {
    let map_id = chat_dat::resolve_data_id(dats, CHAT_FONT_MAP_KEY, INTERFACE_CATEGORY);
    let map = dats.get::<EnumIdMap>(map_id).ok().flatten()?;
    chat_font_in_map(&map, face, size)
}

/// The chat font for face `face` and size `size` in the chat font map
/// `map`: the id of its entry `Chat_<face>_<size>`.
#[must_use]
pub fn chat_font_in_map(map: &EnumIdMap, face: i32, size: i32) -> Option<u32> {
    let face = CHAT_FONT_FACES.get(usize::try_from(face).ok()?)?;
    let size = CHAT_FONT_SIZES.get(usize::try_from(size).ok()?)?;
    let name = format!("Chat_{face}_{size}");
    let key = map
        .client_enum_to_name
        .iter()
        .find(|(_, n)| *n == name)
        .map(|(k, _)| *k)?;
    map.client_id(key)
}

/// The chat windows' filters as the settings keep them.
#[must_use]
pub fn chat_filters(settings: &PlayerSettings) -> ChatWindowState {
    let mut state = ChatWindowState::new();
    for id in ChatWindowId::all() {
        if let Some(filter) = settings.chat.filter(usize::from(id.get())) {
            state.set_filter(id, filter);
        }
    }
    state
}

/// Section `section` of `from` copied into `into`, the rest of `into` kept:
/// an options window change applied to the settings in force (each of its
/// rows writes the fields of the one section it reports).
fn take_section(into: &mut PlayerSettings, from: &PlayerSettings, section: Section) {
    match section {
        Section::Display => into.display.clone_from(&from.display),
        Section::Audio => into.audio.clone_from(&from.audio),
        Section::Mixer => into.mixer.clone_from(&from.mixer),
        Section::Camera => into.camera.clone_from(&from.camera),
        Section::Chat => into.chat.clone_from(&from.chat),
        Section::PluginUi => into.plugin_ui.clone_from(&from.plugin_ui),
        // The character's options are the session's, not the settings.
        Section::Character(_) => {}
    }
}

impl WindowUi {
    /// OpenAC `RetailUiRuntime.MountOptionsPanel`.
    pub(super) fn mount_options(&mut self, resolution_choices: Vec<String>) -> Result<(), String> {
        let font = self.content.font(DEFAULT_FONT_ID);
        let root_info = import_infos_rooted(
            &self.content,
            options::HOST_LAYOUT_ID,
            options::SLOT_ELEMENT_ID,
        )
        .ok_or("layout 0x2100006E element 0x1000018D is not in the dats")?;
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            options::HOST_LAYOUT_ID,
            options::SLOT_ELEMENT_ID,
            font.clone(),
        )
        .ok_or("layout 0x2100006E is not in the dats")?;
        let dats = self.content.dats().clone();
        let master = self.content.master_property().clone();
        let strings = self.content.strings();
        let lookup = |table: u32, key: &str| strings.resolve(table, compute_hash(key), 0);
        let defaults = chat_dat::opacity_defaults(&dats, &master);
        if defaults.is_none() {
            self.notices.push(
                "ui: options: the chat opacity defaults are not in the dats; using 0.5 and 1.0"
                    .to_owned(),
            );
        }
        let captions = chat_dat::opacity_captions(&dats, &master, strings);
        if captions.is_none() {
            self.notices.push(
                "ui: options: the chat opacity captions are not in the dats; the rows have none"
                    .to_owned(),
            );
        }
        let sources = OptionsSources {
            layouts: &self.content,
            font,
            strings: &lookup,
            resolutions: resolution_choices,
            opacity_defaults: defaults.unwrap_or((
                chat_dat::FALLBACK_DEFAULT_OPACITY,
                chat_dat::FALLBACK_ACTIVE_OPACITY,
            )),
            opacity_captions: captions.unwrap_or_default(),
        };
        let live = LiveValues {
            settings: self.settings().clone(),
            options1: self
                .player_view
                .map_or(ac_runtime::character::DEFAULT_OPTIONS1, |v| v.options1),
            options2: self
                .player_view
                .map_or(ac_runtime::character::DEFAULT_OPTIONS2, |v| v.options2),
        };
        let mut controller =
            OptionsWindowController::bind(&mut self.host.root, &layout, &sources, live)
                .ok_or("the window's root is not a tab panel")?;
        for notice in controller.take_notices() {
            self.notices.push(format!("ui: {notice}"));
        }
        let restore_previous =
            root_info.effective_bool(RESTORE_PREVIOUS_PROPERTY, None) == Some(true);
        mount_window(
            &mut self.host.root,
            OPTIONS_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 150.0,
                top: 80.0,
                visible: false,
                resize_x: false,
                resize_y: true,
                resizable_edges: ResizeEdges::BOTTOM,
                constrain_resize_to_parent: true,
                content_anchors: AnchorEdges::ALL,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        self.host
            .attach_controller(OPTIONS_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        self.panels.register(
            &mut self.host.root,
            panel_id::OPTIONS,
            OPTIONS_WINDOW,
            restore_previous,
        );
        Ok(())
    }

    /// F11: the options window opens on its page, or closes.
    pub(super) fn toggle_options(&mut self) -> bool {
        self.panels.toggle(&mut self.host.root, panel_id::OPTIONS);
        true
    }

    /// Open the options window on `page` (closed when it was showing it),
    /// as OpenAC's `OpenOptionsPage`.
    pub fn open_options_page(&mut self, page: OptionsPage) -> bool {
        let visible = self.host.root.is_window_visible(OPTIONS_WINDOW);
        let Some(on_page) = self
            .host
            .with_controller::<OptionsWindowController, _>(OPTIONS_WINDOW, |c, ui| {
                c.active_page(ui) == Some(page)
            })
        else {
            return false;
        };
        if visible && on_page {
            self.panels
                .set_visibility(&mut self.host.root, panel_id::OPTIONS, false);
            return true;
        }
        self.host
            .with_controller::<OptionsWindowController, _>(OPTIONS_WINDOW, |c, ui| {
                c.show_page(ui, page);
            });
        self.panels
            .set_visibility(&mut self.host.root, panel_id::OPTIONS, true);
        true
    }

    /// The settings changed outside the window (a console command): the
    /// window and its options window read them from now on, and the plugin
    /// dock and theme follow them.
    pub fn settings_changed_elsewhere(&mut self, settings: &PlayerSettings) {
        self.replace_settings(settings);
        self.follow_plugin_theme_setting();
        self.follow_plugin_dock_setting();
    }

    /// The player's view changed: the options window's character words, and
    /// when the server's words just arrived, the page shown re-reads them
    /// and the chat settings' hear flags follow them (OpenAC
    /// `SyncChatFromServerOptions`).
    pub(super) fn follow_options_words(&mut self, seeded_now: bool) {
        let Some(view) = self.player_view else {
            return;
        };
        let (options1, options2) = (view.options1, view.options2);
        self.host
            .with_controller::<OptionsWindowController, _>(OPTIONS_WINDOW, |c, ui| {
                let mut live = c.live().clone();
                live.options1 = options1;
                live.options2 = options2;
                c.set_live(live);
                if seeded_now {
                    c.on_server_options_seeded(ui);
                }
            });
        if seeded_now {
            self.sync_chat_from_server_options(options2);
        }
    }

    fn sync_chat_from_server_options(&mut self, options2: u32) {
        let hear = |id| {
            ac_runtime::character::option(id)
                .is_some_and(|e| !e.in_options1 && options2 & e.mask != 0)
        };
        let mut chat = self.settings().chat;
        chat.hear_general_chat = hear(option_id::LISTEN_TO_GENERAL_CHAT);
        chat.hear_trade_chat = hear(option_id::LISTEN_TO_TRADE_CHAT);
        chat.hear_lfg_chat = hear(option_id::LISTEN_TO_LFG_CHAT);
        chat.hear_roleplay_chat = hear(option_id::LISTEN_TO_ROLEPLAY_CHAT);
        chat.hear_society_chat = hear(option_id::LISTEN_TO_SOCIETY_CHAT);
        if chat == self.settings().chat {
            return;
        }
        self.edit_settings(Section::Chat, true, |s| s.chat = chat);
    }

    /// Change the display settings as the options window would (applied
    /// and saved by the window): automation's Potato Mode and UI Only.
    pub fn change_display(&mut self, change: impl FnOnce(&mut ac_host::settings::DisplaySettings)) {
        self.edit_settings(Section::Display, true, |s| change(&mut s.display));
    }

    /// A client-local system line (the window's own messages, such as
    /// where a screenshot went): OpenAC's `DisplaySystemMessage` adds it to
    /// the session's communication as client-local text, which shows where
    /// the client's own interface text goes and which the plugins' filters
    /// are offered.
    pub fn client_line(&mut self, text: &str) {
        self.local_line(LogTextType::ClientLocal, text);
    }

    /// Text of `text_type` the interface adds to the session's chat (OpenAC
    /// `RuntimeCommunicationState.AddText`): it outlives the window.
    fn local_line(&mut self, text_type: LogTextType, text: &str) {
        self.host.root.emit(UiOutput::Panel(PanelOutput::Session(
            SessionCommand::ShowText {
                text: text.to_owned(),
                log_text_type: text_type,
            },
        )));
        self.host.pump();
    }

    /// The chat font and filters of the settings, on the chat window.
    pub(super) fn apply_chat_settings(&mut self) {
        let chat = self.settings().chat;
        let font = chat_font_id(
            self.content.dats(),
            chat.chat_font_face,
            chat.chat_font_size_index,
        )
        .and_then(|id| self.content.font(id));
        let filters = chat_filters(self.settings());
        self.host
            .with_controller::<ChatWindowController, _>(CHAT_WINDOW, |c, ui| {
                if let Some(font) = &font {
                    c.apply_chat_font(ui, font.clone());
                }
                c.set_filters(filters.clone());
                c.refresh_transcript(ui);
            });
        self.apply_floating_chat_settings(&filters, font.as_ref());
    }

    /// Act on the options window's request.
    pub(super) fn options_output(
        &mut self,
        output: OptionsOutput,
        commands: &mut Vec<SessionCommand>,
    ) {
        match output {
            OptionsOutput::Changed { section, persist } => {
                let Some(live) = self
                    .host
                    .with_controller::<OptionsWindowController, _>(OPTIONS_WINDOW, |c, _| {
                        c.live().clone()
                    })
                else {
                    return;
                };
                if let Section::Character(id) = section {
                    if let Some(value) = live.character_option(id) {
                        commands.push(SessionCommand::SetCharacterOption { option: id, value });
                    }
                    return;
                }
                // Only the section the window changed is taken from its
                // copy, onto the settings in force.
                self.edit_settings(section, persist, |s| {
                    take_section(s, &live.settings, section);
                });
                if section == Section::Chat {
                    self.apply_chat_settings();
                }
            }
            OptionsOutput::Applied => commands.push(SessionCommand::SaveCharacterOptions),
            OptionsOutput::ExitToCharacterSelection => {
                let text = self
                    .content
                    .strings()
                    .resolve(
                        UI_STRING_TABLE,
                        compute_hash("ID_Client_EndCharacterSessionConfirm"),
                        0,
                    )
                    .unwrap_or_else(|| {
                        "Are you sure you want to end this character session?".to_owned()
                    });
                self.ask(
                    text,
                    DialogAction::Panel(PanelOutput::Session(SessionCommand::Lifecycle(
                        LifecycleCommand::EndCharacterSession,
                    ))),
                );
            }
            OptionsOutput::ExitGame => commands.push(SessionCommand::Shutdown),
            OptionsOutput::UseMouseTurningSettings => self.use_mouse_turning_settings(commands),
            OptionsOutput::SystemMessage(text) => self.local_line(LogTextType::ClientLocal, &text),
            OptionsOutput::ConfigureKeyboard => {
                self.toggle_keyboard_config();
            }
        }
    }

    /// OpenAC `ApplyMouseTurningSettingsMacro`: the camera to the mouse
    /// turning targets, the character option on, a line per change, then
    /// the Configuration page applied.
    fn use_mouse_turning_settings(&mut self, commands: &mut Vec<SessionCommand>) {
        let use_mouse_turning = self.player_view.is_some_and(|v| {
            ac_runtime::character::option(option_id::USE_MOUSE_TURNING)
                .is_some_and(|e| v.options2 & e.mask != 0)
        });
        let result = mouse_turning_macro(&self.settings().camera, use_mouse_turning);
        let camera = result.updated;
        self.edit_settings(Section::Camera, true, |s| s.camera = camera);
        if result.use_mouse_turning_changed {
            commands.push(SessionCommand::SetCharacterOption {
                option: option_id::USE_MOUSE_TURNING,
                value: true,
            });
        }
        for line in &result.chat_lines {
            self.local_line(LogTextType::Magic, line);
        }
        self.host
            .with_controller::<OptionsWindowController, _>(OPTIONS_WINDOW, |c, ui| {
                c.apply_page(ui, OptionsPage::Configuration);
            });
    }
}

/// The resolutions to offer for a monitor with `modes` and a `desktop`
/// size (the standard sizes when the monitor says nothing).
#[must_use]
pub fn resolution_choices(modes: &[(i32, i32)], desktop: Option<(i32, i32)>) -> Vec<String> {
    let standard = || {
        resolutions::AVAILABLE_RESOLUTIONS
            .iter()
            .map(|s| (*s).to_owned())
            .collect()
    };
    let Some(desktop) = desktop.filter(|(w, h)| *w > 0 && *h > 0) else {
        return standard();
    };
    let curated = resolutions::curate(modes, desktop);
    if curated.is_empty() {
        return standard();
    }
    resolutions::windowed_offering(&curated, desktop)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_chat_filters_follow_the_settings() {
        let mut s = PlayerSettings::default();
        s.chat.set_filter(0, 0x10);
        s.chat.set_filter(4, 0x20);
        let f = chat_filters(&s);
        assert_eq!(f.filter(ChatWindowId::MAIN), 0x10);
        assert_eq!(f.filter(ChatWindowId::new(4).unwrap()), 0x20);
    }

    #[test]
    fn without_a_monitor_the_standard_sizes_are_offered() {
        assert_eq!(
            resolution_choices(&[], None),
            resolutions::AVAILABLE_RESOLUTIONS
        );
        assert_eq!(
            resolution_choices(&[(1920, 1080), (2560, 1440)], Some((2560, 1440))),
            ["1280x720", "1366x768", "1600x900", "1920x1080", "2560x1440"]
        );
    }
}
