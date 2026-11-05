//! The social panel's friends and squelch pages: a list of names (friends
//! lit by their online state), the name entry, and the add, remove and
//! squelch buttons; the friends page's appear-offline checkbox.

use std::collections::HashMap;

use ac_core::social::{FriendsState, SquelchState};
use ac_runtime::character::option_id;
use ac_runtime::session::SessionCommand;

use crate::draw::Color;
use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, link_list_scrollbar};
use crate::root::UiRoot;
use crate::tree::ElementId;
use crate::widgets::{Button, Field, ListBox, Text};

use super::{RowTemplates, find_button, find_text, set_line};

/// The friends page.
pub const FRIENDS_PAGE_ID: u32 = 0x1000_0513;
/// The friends list.
pub const FRIENDS_LIST_ID: u32 = 0x1000_0517;
/// Add the typed name.
pub const ADD_FRIEND_BUTTON_ID: u32 = 0x1000_0514;
/// Remove the selected friend.
pub const REMOVE_FRIEND_BUTTON_ID: u32 = 0x1000_0515;
/// Appear offline.
pub const APPEAR_OFFLINE_ID: u32 = 0x1000_052C;
/// The friend name entry.
pub const FRIEND_NAME_FIELD_ID: u32 = 0x1000_051B;
/// A friend row's name.
pub const FRIEND_ROW_NAME_ID: u32 = 0x1000_051A;
/// The name's state for an online friend.
pub const ONLINE_STATE_ID: u32 = 0x1000_0054;
/// The name's state for an offline friend.
pub const OFFLINE_STATE_ID: u32 = 0x1000_0055;

/// The squelch page.
pub const SQUELCH_PAGE_ID: u32 = 0x1000_054A;
/// The squelch list.
pub const SQUELCH_LIST_ID: u32 = 0x1000_053E;
/// The squelch name entry.
pub const SQUELCH_NAME_FIELD_ID: u32 = 0x1000_0540;
/// Remove the selected squelch.
pub const REMOVE_SQUELCH_BUTTON_ID: u32 = 0x1000_0547;
/// Squelch the typed character.
pub const SQUELCH_CHARACTER_BUTTON_ID: u32 = 0x1000_054B;
/// Squelch the typed character's account.
pub const SQUELCH_ACCOUNT_BUTTON_ID: u32 = 0x1000_054C;

const NAME_COLOR: Color = Color::WHITE;

/// The most names a list takes: at this many its add buttons ghost.
pub const MAX_ENTRIES: usize = 50;

fn session(ui: &mut UiRoot, c: SessionCommand) {
    emit(ui, PanelOutput::Session(c));
}

/// A button live (Normal) or ghosted (the authored default of these
/// pages' buttons is Ghosted, so they act only once enabled here).
fn set_live(ui: &mut UiRoot, button: Option<ElementId>, live: bool) {
    if let Some(b) = button {
        ui.set_retail_state(
            b,
            if live {
                crate::states::NORMAL
            } else {
                crate::states::GHOSTED
            },
        );
    }
}

fn field_is_empty(ui: &UiRoot, field: Option<ElementId>) -> bool {
    field
        .and_then(|f| ui.widget::<Field>(f))
        .is_none_or(|f| f.text().is_empty())
}

fn field(ui: &UiRoot, page: ElementId, id: u32) -> Option<ElementId> {
    ui.find_descendant(page, id)
        .filter(|f| ui.widget::<Field>(*f).is_some())
}

fn take_typed_name(ui: &mut UiRoot, field: Option<ElementId>) -> Option<String> {
    let f = field?;
    let name = ui.widget::<Field>(f).map(Field::text)?.trim().to_owned();
    if name.is_empty() {
        return None;
    }
    if let Some(w) = ui.widget_mut::<Field>(f) {
        w.set_text("");
    }
    Some(name)
}

fn bound_list(ui: &mut UiRoot, page: ElementId, id: u32) -> Option<ElementId> {
    let list = ui
        .find_descendant(page, id)
        .filter(|l| ui.widget::<ListBox>(*l).is_some())?;
    link_list_scrollbar(ui, page, list);
    Some(list)
}

fn template_row(
    ui: &mut UiRoot,
    list: ElementId,
    templates: &mut dyn RowTemplates,
) -> Option<ElementId> {
    let t = ui
        .widget::<ListBox>(list)
        .and_then(|b| b.templates.first().copied())?;
    let row = templates.build_row(ui, t.layout, t.element)?;
    ui.with::<ListBox, _>(list, |b, ui| b.add_prebuilt_row(ui, list, row));
    Some(row)
}

// ── Friends ─────────────────────────────────────────────────────────────

/// The friends page's controller.
#[derive(Debug)]
pub struct FriendsPage {
    list: ElementId,
    name_field: Option<ElementId>,
    add: Option<ElementId>,
    remove: Option<ElementId>,
    appear_offline: Option<ElementId>,
    appear_offline_on: bool,
    last_revision: Option<u64>,
    rows: HashMap<ElementId, (u32, String)>,
    selected: Option<(u32, String)>,
    count: usize,
}

impl FriendsPage {
    /// Bind to the friends page (`None` without its list).
    pub fn bind(ui: &mut UiRoot, page: ElementId) -> Option<Self> {
        let list = bound_list(ui, page, FRIENDS_LIST_ID)?;
        let appear_offline = find_button(ui, page, APPEAR_OFFLINE_ID);
        if let Some(w) = appear_offline.and_then(|b| ui.widget_mut::<Button>(b)) {
            w.suppress_self_toggle = true;
        }
        Some(Self {
            list,
            name_field: field(ui, page, FRIEND_NAME_FIELD_ID),
            add: find_button(ui, page, ADD_FRIEND_BUTTON_ID),
            remove: find_button(ui, page, REMOVE_FRIEND_BUTTON_ID),
            appear_offline,
            appear_offline_on: false,
            last_revision: None,
            rows: HashMap::new(),
            selected: None,
            count: 0,
        })
    }

    /// As the retail client sets them: remove with a friend selected,
    /// add below the most friends (the tell button is not ported and stays
    /// ghosted).
    fn update_buttons(&self, ui: &mut UiRoot) {
        set_live(ui, self.remove, self.selected.is_some());
        set_live(ui, self.add, self.count < MAX_ENTRIES);
    }

    /// The name entry changed (as the retail client answers it,
    /// text changed): add is live with a name typed, below the most
    /// friends. True when `element` is the entry.
    pub fn on_text_changed(&self, ui: &mut UiRoot, element: ElementId) -> bool {
        if Some(element) != self.name_field {
            return false;
        }
        if self.count < MAX_ENTRIES {
            let typed = !field_is_empty(ui, self.name_field);
            set_live(ui, self.add, typed);
        }
        true
    }

    /// The friend selected (id and name).
    #[must_use]
    pub fn selected(&self) -> Option<&(u32, String)> {
        self.selected.as_ref()
    }

    /// Show `friends` (rebuilt when its revision moved) and the
    /// appear-offline option.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        friends: &FriendsState,
        appear_offline: bool,
        templates: &mut dyn RowTemplates,
    ) {
        self.appear_offline_on = appear_offline;
        if let Some(b) = self.appear_offline {
            ui.with::<Button, _>(b, |w, ui| w.set_selected(ui, b, appear_offline));
        }
        if self.last_revision == Some(friends.revision()) {
            return;
        }
        let list = self.list;
        ui.with::<ListBox, _>(list, |b, ui| b.flush(ui));
        self.rows.clear();
        let mut all_built = true;
        let mut selected_present = false;
        for friend in friends.entries() {
            let Some(row) = template_row(ui, list, templates) else {
                all_built = false;
                continue;
            };
            if self
                .selected
                .as_ref()
                .is_some_and(|(id, _)| *id == friend.id)
            {
                selected_present = true;
            }
            let Some(name) = find_text(ui, row, FRIEND_ROW_NAME_ID) else {
                all_built = false;
                continue;
            };
            set_line(ui, Some(name), &friend.name, NAME_COLOR);
            bind_click(ui, name);
            ui.set_retail_state(
                name,
                if friend.online {
                    ONLINE_STATE_ID
                } else {
                    OFFLINE_STATE_ID
                },
            );
            self.rows.insert(name, (friend.id, friend.name.clone()));
        }
        if !selected_present {
            self.selected = None;
        }
        self.count = friends.len();
        self.update_buttons(ui);
        // A row that failed to build must not swallow a later revision.
        if all_built {
            self.last_revision = Some(friends.revision());
        }
    }

    /// Handle a click on `element`; true when it is the page's.
    pub fn on_click(&mut self, ui: &mut UiRoot, element: ElementId) -> bool {
        if let Some(row) = self.rows.get(&element) {
            self.selected = Some(row.clone());
            self.update_buttons(ui);
            return true;
        }
        if Some(element) == self.add {
            if let Some(name) = take_typed_name(ui, self.name_field) {
                session(ui, SessionCommand::AddFriend(name));
            }
            // The entry is cleared and add ghosts until a name is typed.
            set_live(ui, self.add, false);
            return true;
        }
        if Some(element) == self.remove {
            if let Some((_, name)) = self.selected.clone() {
                session(ui, SessionCommand::RemoveFriend(name));
            }
            return true;
        }
        if Some(element) == self.appear_offline {
            let next = !self.appear_offline_on;
            self.appear_offline_on = next;
            session(
                ui,
                SessionCommand::SetCharacterOption {
                    option: option_id::APPEAR_OFFLINE,
                    value: next,
                },
            );
            return true;
        }
        false
    }
}

// ── Squelch ─────────────────────────────────────────────────────────────

/// One squelch row: a character (id and name) or an account (name).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SquelchRow {
    /// The character's id (0 for an account).
    pub guid: u32,
    /// The name.
    pub name: String,
    /// An account squelch.
    pub account: bool,
}

/// The squelch page's controller.
#[derive(Debug)]
pub struct SquelchPage {
    list: ElementId,
    name_field: Option<ElementId>,
    remove: Option<ElementId>,
    squelch_character: Option<ElementId>,
    squelch_account: Option<ElementId>,
    last_revision: Option<u64>,
    rows: HashMap<ElementId, SquelchRow>,
    selected: Option<SquelchRow>,
    count: usize,
}

/// The deepest text of a row (the last one met walking it).
fn deepest_text(ui: &UiRoot, row: ElementId) -> Option<ElementId> {
    let mut found = ui.widget::<Text>(row).is_some().then_some(row);
    for child in ui.children(row) {
        if let Some(deeper) = deepest_text(ui, child) {
            found = Some(deeper);
        }
    }
    found
}

impl SquelchPage {
    /// Bind to the squelch page (`None` without its list).
    pub fn bind(ui: &mut UiRoot, page: ElementId) -> Option<Self> {
        let list = bound_list(ui, page, SQUELCH_LIST_ID)?;
        Some(Self {
            list,
            name_field: field(ui, page, SQUELCH_NAME_FIELD_ID),
            remove: find_button(ui, page, REMOVE_SQUELCH_BUTTON_ID),
            squelch_character: find_button(ui, page, SQUELCH_CHARACTER_BUTTON_ID),
            squelch_account: find_button(ui, page, SQUELCH_ACCOUNT_BUTTON_ID),
            last_revision: None,
            rows: HashMap::new(),
            selected: None,
            count: 0,
        })
    }

    /// As the retail client sets them: remove with a row selected,
    /// both squelch buttons below the most entries.
    fn update_buttons(&self, ui: &mut UiRoot) {
        set_live(ui, self.remove, self.selected.is_some());
        let room = self.count < MAX_ENTRIES;
        set_live(ui, self.squelch_character, room);
        set_live(ui, self.squelch_account, room);
    }

    /// The name entry changed (as the retail client answers it,
    /// text changed): both squelch buttons are live with a name typed,
    /// below the most entries. True when `element` is the entry.
    pub fn on_text_changed(&self, ui: &mut UiRoot, element: ElementId) -> bool {
        if Some(element) != self.name_field {
            return false;
        }
        if self.count < MAX_ENTRIES {
            let typed = !field_is_empty(ui, self.name_field);
            set_live(ui, self.squelch_character, typed);
            set_live(ui, self.squelch_account, typed);
        }
        true
    }

    /// The row selected.
    #[must_use]
    pub fn selected(&self) -> Option<&SquelchRow> {
        self.selected.as_ref()
    }

    /// Show `squelch` (rebuilt when its revision moved): the characters,
    /// then the accounts.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        squelch: &SquelchState,
        templates: &mut dyn RowTemplates,
    ) {
        if self.last_revision == Some(squelch.revision()) {
            return;
        }
        let list = self.list;
        ui.with::<ListBox, _>(list, |b, ui| b.flush(ui));
        self.rows.clear();
        let db = squelch.database();
        let mut characters: Vec<(&u32, &ac_core::social::SquelchInfo)> =
            db.characters.iter().collect();
        characters.sort_by_key(|(guid, _)| **guid);
        let mut accounts: Vec<&String> = db.accounts.keys().collect();
        accounts.sort();
        let entries = characters
            .into_iter()
            .map(|(guid, info)| SquelchRow {
                guid: *guid,
                name: info.name.clone(),
                account: false,
            })
            .chain(accounts.into_iter().map(|name| SquelchRow {
                guid: 0,
                name: name.clone(),
                account: true,
            }));
        let mut all_built = true;
        let mut selected_present = false;
        for entry in entries {
            let Some(row) = template_row(ui, list, templates) else {
                all_built = false;
                continue;
            };
            if self.selected.as_ref().is_some_and(|s| {
                s.account == entry.account
                    && if entry.account {
                        s.name == entry.name
                    } else {
                        s.guid == entry.guid
                    }
            }) {
                selected_present = true;
            }
            if let Some(t) = deepest_text(ui, row) {
                set_line(ui, Some(t), &entry.name, NAME_COLOR);
                bind_click(ui, t);
                self.rows.insert(t, entry);
            }
        }
        if !selected_present {
            self.selected = None;
        }
        self.count = self.rows.len();
        self.update_buttons(ui);
        if all_built {
            self.last_revision = Some(squelch.revision());
        }
    }

    /// Handle a click on `element`; true when it is the page's.
    pub fn on_click(&mut self, ui: &mut UiRoot, element: ElementId) -> bool {
        if let Some(row) = self.rows.get(&element) {
            self.selected = Some(row.clone());
            self.update_buttons(ui);
            return true;
        }
        if Some(element) == self.squelch_character || Some(element) == self.squelch_account {
            let account = Some(element) == self.squelch_account;
            if let Some(name) = take_typed_name(ui, self.name_field) {
                session(
                    ui,
                    SessionCommand::Squelch {
                        add: true,
                        name,
                        account,
                    },
                );
            }
            // The entry is cleared and the button ghosts until a name is
            // typed.
            set_live(ui, Some(element), false);
            return true;
        }
        if Some(element) == self.remove {
            if let Some(s) = self.selected.clone() {
                session(
                    ui,
                    SessionCommand::Squelch {
                        add: false,
                        name: s.name,
                        account: s.account,
                    },
                );
            }
            return true;
        }
        false
    }
}
