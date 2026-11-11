//! The floating chat windows 1..=4 in the game window, as OpenAC's
//! `RetailUiRuntime.MountFloatingChatWindows` mounts them (hidden, at
//! x 440 down the screen), fed every chat line, filtered and lettered from
//! the chat settings, toggled by Alt+1..4 and the main window's
//! indicators.

use ac_core::chat::ChatWindowId;
use ac_runtime::communication::ChatLine;
use ac_ui::element_info::ElementInfo;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::import::{LayoutSource, import, import_infos};
use ac_ui::input::InputAction;
use ac_ui::panels::chat::ChatWindowController;
use ac_ui::panels::floating_chat::{self, FloatingChatController};
use ac_ui::window_chrome::CHAT_WINDOW_NAMES;

use super::mount::{MountOptions, mount_window};
use super::window::{CHAT_WINDOW, WindowUi};

/// The floating chat windows' names, window 1 first (the faded chat
/// windows after the main one).
pub const FLOATING_CHAT_WINDOWS: [&str; 4] = [
    CHAT_WINDOW_NAMES[1],
    CHAT_WINDOW_NAMES[2],
    CHAT_WINDOW_NAMES[3],
    CHAT_WINDOW_NAMES[4],
];

/// Where each floating window's frame starts (OpenAC's slots).
const SLOTS: [(f32, f32); 4] = [
    (440.0, 40.0),
    (440.0, 170.0),
    (440.0, 300.0),
    (440.0, 430.0),
];

/// The floating window (1..=4) a key-map action toggles (OpenAC
/// `GameplayInputCommandController.Handle`; the retail key map binds them
/// to Alt+1..4, chords that also use quick slots 10..13, both actions
/// fire).
#[must_use]
pub fn floating_chat_for_action(action: InputAction) -> Option<u8> {
    match action {
        InputAction::ToggleFloatingChatWindow1 => Some(1),
        InputAction::ToggleFloatingChatWindow2 => Some(2),
        InputAction::ToggleFloatingChatWindow3 => Some(3),
        InputAction::ToggleFloatingChatWindow4 => Some(4),
        _ => None,
    }
}

/// The floating window number of window `name`, if it is one.
#[must_use]
pub fn floating_chat_number(name: &str) -> Option<usize> {
    FLOATING_CHAT_WINDOWS
        .iter()
        .position(|n| *n == name)
        .map(|i| i + 1)
}

fn find_info(info: &ElementInfo, id: u32) -> Option<&ElementInfo> {
    if info.id == id {
        return Some(info);
    }
    info.children.iter().find_map(|c| find_info(c, id))
}

impl WindowUi {
    /// OpenAC `RetailUiRuntime.MountFloatingChatWindows`: four copies of
    /// layout 0x2100005B, hidden.
    pub(super) fn mount_floating_chat(&mut self) {
        let Some(info) = import_infos(&self.content, floating_chat::LAYOUT_ID) else {
            self.notices
                .push("ui: floating chat: layout 0x2100005B is not in the dats".to_owned());
            return;
        };
        let constraint = find_info(&info, floating_chat::ROOT_ID)
            .cloned()
            .unwrap_or(info);
        for (index, name) in FLOATING_CHAT_WINDOWS.into_iter().enumerate() {
            let result = self.mount_one_floating_chat(index, name, &constraint);
            match result {
                Ok(()) => self.mounted.push(name),
                Err(e) => self.notices.push(format!("ui: {name}: {e}")),
            }
        }
    }

    fn mount_one_floating_chat(
        &mut self,
        index: usize,
        name: &'static str,
        constraint: &ElementInfo,
    ) -> Result<(), String> {
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import(
            &mut self.host.root,
            &self.content,
            floating_chat::LAYOUT_ID,
            font.clone(),
        )
        .ok_or("layout 0x2100005B is not in the dats")?;
        let window = ChatWindowId::new(index as i32 + 1).ok_or("no such chat window")?;
        let controller =
            FloatingChatController::bind(&mut self.host.root, &layout, window, name, font)
                .ok_or("required role elements are missing")?;
        let (left, top) = SLOTS[index];
        mount_window(
            &mut self.host.root,
            name,
            controller.root(),
            &MountOptions {
                left,
                top,
                dat_constraint_source: Some(constraint.clone()),
                authored_geometry_revision: 1,
                visible: false,
                ..MountOptions::default()
            },
        )?;
        self.host
            .attach_controller(name, Box::new(controller))
            .map_err(|e| e.to_string())
    }

    /// Every floating window hears the chat line.
    pub(super) fn feed_floating_chat(&mut self, lines: &[ChatLine]) {
        for name in FLOATING_CHAT_WINDOWS {
            self.host
                .with_controller::<FloatingChatController, _>(name, |c, _| {
                    for line in lines {
                        c.push_chat_line(line);
                    }
                });
        }
    }

    /// Show or hide floating chat window `n` (1..=4).
    pub(super) fn toggle_floating_chat(&mut self, n: u8) -> bool {
        match FLOATING_CHAT_WINDOWS.get(usize::from(n).wrapping_sub(1)) {
            Some(name) => self.host.root.toggle_window(name),
            None => false,
        }
    }

    /// A floating window shown or hidden: the main window's indicator
    /// follows (OpenAC `OnWindowVisibilityChanged`).
    pub(super) fn floating_chat_shown(&mut self, name: &str, visible: bool) {
        let Some(n) = floating_chat_number(name) else {
            return;
        };
        self.host
            .with_controller::<ChatWindowController, _>(CHAT_WINDOW, |c, ui| {
                c.set_indicator_open(ui, n, visible);
            });
    }

    /// The floating windows' filters and font from the chat settings.
    pub(super) fn apply_floating_chat_settings(
        &mut self,
        filters: &ac_core::chat::ChatWindowState,
        font: Option<&std::sync::Arc<ac_ui::font::UiFont>>,
    ) {
        for name in FLOATING_CHAT_WINDOWS {
            self.host
                .with_controller::<FloatingChatController, _>(name, |c, ui| {
                    c.set_filters(filters.clone());
                    match font {
                        Some(f) => c.apply_chat_font(ui, f.clone()),
                        None => c.refresh_transcript(ui),
                    }
                });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_four_floating_window_actions_toggle_their_windows() {
        // GameplayInputCommandControllerTests.RecognizedCommand_RoutesToTypedOwner
        // (chat-window-1..4).
        use InputAction as A;
        assert_eq!(
            floating_chat_for_action(A::ToggleFloatingChatWindow1),
            Some(1)
        );
        assert_eq!(
            floating_chat_for_action(A::ToggleFloatingChatWindow4),
            Some(4)
        );
        assert_eq!(floating_chat_for_action(A::UseQuickSlot_10), None);
        assert_eq!(floating_chat_number("chat-window-3"), Some(3));
        assert_eq!(floating_chat_number("chat"), None);
    }

    // OpenAC `ChatFontResolverTests` (the map cases).
    use super::super::options::chat_font_in_map;
    use ac_dat::objects::EnumIdMap;

    const FACES: [&str; 5] = [
        "Arial",
        "CourierNew",
        "PalatinoLinotype",
        "Tahoma",
        "TimesNewRoman",
    ];
    const SIZES: [&str; 5] = ["Tiny", "Small", "Medium", "Large", "XL"];

    fn map(names: &[(u32, &str)], ids: &[(u32, u32)]) -> EnumIdMap {
        EnumIdMap {
            client_enum_to_name: names.iter().map(|(k, n)| (*k, (*n).to_owned())).collect(),
            client_enum_to_id: ids.to_vec(),
            id: 0,
            server_enum_to_id: Vec::new(),
            server_enum_to_name: Vec::new(),
        }
    }

    #[test]
    fn a_known_face_and_size_resolve_their_font() {
        let m = map(&[(1, "Chat_PalatinoLinotype_Small")], &[(1, 0x4000_0000)]);
        assert_eq!(chat_font_in_map(&m, 2, 1), Some(0x4000_0000));
        for (face, size) in [(-1, 0), (5, 0), (0, -1), (0, 5)] {
            assert_eq!(chat_font_in_map(&m, face, size), None, "{face} {size}");
        }
        let other = map(&[(1, "Chat_Arial_Tiny")], &[(1, 0x4000_0000)]);
        assert_eq!(
            chat_font_in_map(&other, 2, 1),
            None,
            "the name is not in the map"
        );
        let no_id = map(&[(1, "Chat_PalatinoLinotype_Small")], &[]);
        assert_eq!(chat_font_in_map(&no_id, 2, 1), None, "no id for the name");
    }

    #[test]
    fn each_face_and_size_picks_its_own_name() {
        let mut names = Vec::new();
        let mut ids = Vec::new();
        let mut key = 0;
        for face in FACES {
            for size in SIZES {
                names.push((key, format!("Chat_{face}_{size}")));
                ids.push((key, 0x5000_0000 + key));
                key += 1;
            }
        }
        let m = EnumIdMap {
            client_enum_to_name: names,
            client_enum_to_id: ids,
            id: 0,
            server_enum_to_id: Vec::new(),
            server_enum_to_name: Vec::new(),
        };
        for face in 0..5 {
            for size in 0..5 {
                let expected = 0x5000_0000 + (face * 5 + size) as u32;
                assert_eq!(chat_font_in_map(&m, face, size), Some(expected));
            }
        }
    }
}
