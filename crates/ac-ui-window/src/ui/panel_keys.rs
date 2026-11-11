//! The key map's interface actions beyond the panel toggles and hotkeys of
//! `status`: the page keys (friends, titles, map, house, the options pages,
//! the journal pages) and the keys that answer with a line (help, abuse
//! reporting, urgent assistance, the plugin dock), as OpenAC's
//! `RetailUiRuntime.HandleInputAction` routes them.

use super::social_panels::SocialPage;
use ac_ui::input::InputAction;
use ac_ui::panels::character::CharacterTab;
use ac_ui::panels::journal::JournalPanelController;
use ac_ui::panels::map_house::MapHousePanelController;
use ac_ui::panels::options::{
    OptionsPage, report_abuse_unavailable, urgent_assistance_unavailable,
};
use ac_ui::panels::panel_id;

use super::map_window::MAP_HOUSE_WINDOW;
use super::social_panels::JOURNAL_WINDOW;
use super::window::WindowUi;

/// What F1 answers: the retail help is a plugin this client does not have.
pub const HELP_UNAVAILABLE: &str =
    "In-game help is unavailable because the retail help plugin is not installed.";

/// What the plugin dock key answers while no plugin window is registered.
pub const NO_PLUGIN_WINDOWS: &str = "No plugin windows are registered.";

/// A page of the journal.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum JournalPage {
    /// The contracts (quest) page.
    Contracts,
    /// The notes page.
    Notes,
    /// The list of the journal's pages.
    PageList,
}

/// What an interface action of this module does.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PageKey {
    /// A line in the chat.
    Message(MessageKey),
    /// The social panel on its friends page.
    Friends,
    /// The character window on its titles tab.
    Titles,
    /// The map and house panel on the map (false) or the house (true).
    World {
        /// The house page.
        house: bool,
    },
    /// The options window on a page.
    Options(OptionsPage),
    /// The journal on a page.
    Journal(JournalPage),
    /// The Configure Keyboard screen opens or closes.
    KeyboardConfig,
}

/// The keys that answer with a line.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MessageKey {
    /// F1: help.
    Help,
    /// The plugin dock (no plugin windows yet).
    PluginManager,
    /// Report abuse.
    AbuseReporting,
    /// Urgent assistance.
    UrgentAssistance,
}

/// The page key `action` names, if it is one of this module's.
#[must_use]
pub fn page_key_for_action(action: InputAction) -> Option<PageKey> {
    use InputAction as A;
    Some(match action {
        A::ToggleHelp => PageKey::Message(MessageKey::Help),
        A::TogglePluginManager => PageKey::Message(MessageKey::PluginManager),
        A::ToggleAbuseReportingPanel => PageKey::Message(MessageKey::AbuseReporting),
        A::ToggleUrgentAssistancePanel => PageKey::Message(MessageKey::UrgentAssistance),
        A::ToggleFriendsPage => PageKey::Friends,
        A::ToggleCharacterTitlesPage => PageKey::Titles,
        A::ToggleMapPage => PageKey::World { house: false },
        A::ToggleHousePage => PageKey::World { house: true },
        A::ToggleGameplayOptionsPage => PageKey::Options(OptionsPage::Gameplay),
        A::ToggleCharacterSettingsPage => PageKey::Options(OptionsPage::Character),
        A::ToggleConfigurationPage => PageKey::Options(OptionsPage::Configuration),
        A::ToggleQuestJournalPage => PageKey::Journal(JournalPage::Notes),
        A::ToggleQuestDetailPanel | A::ToggleContractsPage => {
            PageKey::Journal(JournalPage::Contracts)
        }
        A::ToggleJournalPageList => PageKey::Journal(JournalPage::PageList),
        A::ToggleKeyboardConfiguration => PageKey::KeyboardConfig,
        _ => return None,
    })
}

/// The line a message key answers with.
#[must_use]
pub fn message_text(key: MessageKey) -> String {
    match key {
        MessageKey::Help => HELP_UNAVAILABLE.to_owned(),
        MessageKey::PluginManager => NO_PLUGIN_WINDOWS.to_owned(),
        MessageKey::AbuseReporting => report_abuse_unavailable(),
        MessageKey::UrgentAssistance => urgent_assistance_unavailable(),
    }
}

impl WindowUi {
    /// Run a page key; false when its window is not mounted.
    pub fn run_page_key(&mut self, key: PageKey) -> bool {
        match key {
            PageKey::Message(MessageKey::PluginManager) => self.toggle_plugin_dock(),
            PageKey::Message(message) => {
                self.client_line(&message_text(message));
                true
            }
            PageKey::Friends => self.open_social(SocialPage::Friends),
            PageKey::Titles => self.open_character(CharacterTab::Titles),
            PageKey::World { house } => self.open_world_panel(house),
            PageKey::Options(page) => self.open_options_page(page),
            PageKey::Journal(page) => self.open_journal(page),
            PageKey::KeyboardConfig => self.toggle_keyboard_config(),
        }
    }

    /// OpenAC `OpenWorldPanel`: the map and house panel on the page,
    /// closed when it was already showing that page.
    fn open_world_panel(&mut self, house: bool) -> bool {
        let visible = self.host.root.is_window_visible(MAP_HOUSE_WINDOW);
        let Some(on_page) =
            self.host
                .with_controller::<MapHousePanelController, _>(MAP_HOUSE_WINDOW, |c, ui| {
                    if house {
                        c.is_showing_house(ui)
                    } else {
                        c.is_showing_map(ui)
                    }
                })
        else {
            return false;
        };
        if visible && on_page {
            self.panels
                .set_visibility(&mut self.host.root, panel_id::MAP_HOUSE, false);
            return true;
        }
        self.host
            .with_controller::<MapHousePanelController, _>(MAP_HOUSE_WINDOW, |c, ui| {
                if house {
                    c.show_house(ui);
                } else {
                    c.show_map(ui);
                }
            });
        self.panels
            .set_visibility(&mut self.host.root, panel_id::MAP_HOUSE, true);
        true
    }

    /// OpenAC `OpenJournalPanel`: the journal on the page, shown (a key
    /// never closes it).
    fn open_journal(&mut self, page: JournalPage) -> bool {
        let shown =
            self.host
                .with_controller::<JournalPanelController, _>(JOURNAL_WINDOW, |c, ui| match page {
                    JournalPage::Contracts => c.show_contracts(ui),
                    JournalPage::Notes => c.show_notes(ui),
                    JournalPage::PageList => c.show_page_list(ui),
                });
        if shown.is_none() {
            return false;
        }
        self.panels
            .set_visibility(&mut self.host.root, panel_id::JOURNAL, true);
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_page_keys_name_their_pages_as_openac_routes_them() {
        // OpenAC `RetailUiRuntime.HandleInputAction`.
        use InputAction as A;
        assert_eq!(
            page_key_for_action(A::ToggleMapPage),
            Some(PageKey::World { house: false })
        );
        assert_eq!(
            page_key_for_action(A::ToggleHousePage),
            Some(PageKey::World { house: true })
        );
        assert_eq!(
            page_key_for_action(A::ToggleQuestDetailPanel),
            Some(PageKey::Journal(JournalPage::Contracts))
        );
        assert_eq!(
            page_key_for_action(A::ToggleContractsPage),
            Some(PageKey::Journal(JournalPage::Contracts))
        );
        assert_eq!(
            page_key_for_action(A::ToggleQuestJournalPage),
            Some(PageKey::Journal(JournalPage::Notes))
        );
        assert_eq!(
            page_key_for_action(A::ToggleConfigurationPage),
            Some(PageKey::Options(OptionsPage::Configuration))
        );
        assert_eq!(
            page_key_for_action(A::ToggleFriendsPage),
            Some(PageKey::Friends)
        );
        assert_eq!(
            page_key_for_action(A::ToggleCharacterTitlesPage),
            Some(PageKey::Titles)
        );
        assert_eq!(page_key_for_action(A::ToggleInventoryPanel), None);
    }

    #[test]
    fn the_message_keys_answer_with_openacs_lines() {
        assert_eq!(
            message_text(MessageKey::Help),
            "In-game help is unavailable because the retail help plugin is not installed."
        );
        assert_eq!(
            message_text(MessageKey::PluginManager),
            "No plugin windows are registered."
        );
        assert_eq!(
            message_text(MessageKey::AbuseReporting),
            report_abuse_unavailable()
        );
    }
}
