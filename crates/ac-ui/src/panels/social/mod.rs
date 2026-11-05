//! The social panel (layout 0x2100006E, slot 0x1000018F): a tab panel with
//! the allegiance, fellowship, friends and squelch pages.
//!
//! The panel shows the [`SocialView`] handed to
//! [`SocialPanelController::update`] each frame: the fellowship and
//! allegiance pages always follow it, the friends and squelch pages only
//! while the window shows. The fellowship page declares itself open (the
//! server streams the fellows' vitals) while the window shows on its tab;
//! the allegiance page asks for allegiance updates while it shows.

pub mod allegiance;
pub mod fellowship;
pub mod friends;
pub mod rank_titles;

use ac_core::social::{FriendsState, SquelchState};
use ac_runtime::character::{CharacterOptions, option_id};
use ac_runtime::gameplay::FellowshipView;

use crate::draw::Color;
use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, PanelStrings, RowTemplates, panel_id};
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{Button, TabPanel, Text};

pub use allegiance::{AllegiancePage, AllegianceSource};
pub use fellowship::FellowshipPage;
pub use friends::{FriendsPage, SquelchPage};

/// The layout hosting the main panels.
pub const HOST_LAYOUT_ID: u32 = 0x2100_006E;
/// The social panel's slot in it.
pub const SLOT_ELEMENT_ID: u32 = 0x1000_018F;
/// The close button.
pub const CLOSE_BUTTON_ID: u32 = 0x1000_0290;

/// Everything the social panel reads, borrowed for one update.
#[derive(Clone, Copy)]
pub struct SocialView<'a> {
    /// The player's object id.
    pub player: u32,
    /// The selected object.
    pub selected: Option<u32>,
    /// The character is in the world (a declaration to the server counts
    /// only then).
    pub in_world: bool,
    /// The character options (the checkboxes).
    pub options: &'a CharacterOptions,
    /// The friends list.
    pub friends: &'a FriendsState,
    /// The squelch list.
    pub squelch: &'a SquelchState,
    /// The fellowship.
    pub fellowship: &'a FellowshipView,
    /// The allegiance.
    pub allegiance: AllegianceSource<'a>,
    /// The experience a level costs to raise to the next (the fellowship's
    /// uneven split).
    pub experience_to_raise: Option<&'a dyn Fn(u32) -> i64>,
    /// Authored strings.
    pub strings: Option<&'a dyn PanelStrings>,
    /// A world object's displayed name (the swear question).
    pub name_of: Option<&'a dyn Fn(u32) -> Option<String>>,
}

impl std::fmt::Debug for SocialView<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SocialView")
            .field("player", &self.player)
            .field("selected", &self.selected)
            .field("in_world", &self.in_world)
            .finish_non_exhaustive()
    }
}

/// The button `id` under `scope`, its clicks reported.
pub(crate) fn find_button(ui: &mut UiRoot, scope: ElementId, id: u32) -> Option<ElementId> {
    ui.find_descendant(scope, id)
        .filter(|b| ui.widget::<Button>(*b).is_some() && bind_click(ui, *b))
}

/// The text `id` under `scope`.
pub(crate) fn find_text(ui: &UiRoot, scope: ElementId, id: u32) -> Option<ElementId> {
    ui.find_descendant(scope, id)
        .filter(|t| ui.widget::<Text>(*t).is_some())
}

/// Show `s` as text `id`'s one line (written only when it changed).
pub(crate) fn set_line(ui: &mut UiRoot, id: Option<ElementId>, s: &str, color: Color) {
    if let Some(t) = id.and_then(|id| ui.widget_mut::<Text>(id)) {
        let lines = vec![Line::new(s, color)];
        if t.content_lines() != lines {
            t.set_lines(lines);
        }
    }
}

/// The social panel's controller.
#[derive(Debug)]
pub struct SocialPanelController {
    tab_panel: ElementId,
    close: Option<ElementId>,
    fellowship: Option<FellowshipPage>,
    allegiance: Option<AllegiancePage>,
    friends: Option<FriendsPage>,
    squelch: Option<SquelchPage>,
    visible: bool,
    in_world: bool,
}

impl SocialPanelController {
    /// Bind to the imported social panel (`None` unless its root is a tab
    /// panel). The pages read their captions from `strings`.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        strings: Option<&dyn PanelStrings>,
    ) -> Option<Self> {
        let tab_panel = layout.root;
        ui.widget::<TabPanel>(tab_panel)?;
        let close = find_button(ui, tab_panel, CLOSE_BUTTON_ID);
        let page = |ui: &UiRoot, id| ui.find_descendant(tab_panel, id);
        let fellowship =
            page(ui, fellowship::PAGE_ID).and_then(|p| FellowshipPage::bind(ui, p, strings));
        let allegiance =
            page(ui, allegiance::PAGE_ID).and_then(|p| AllegiancePage::bind(ui, p, strings));
        let friends = page(ui, friends::FRIENDS_PAGE_ID).and_then(|p| FriendsPage::bind(ui, p));
        let squelch = page(ui, friends::SQUELCH_PAGE_ID).and_then(|p| SquelchPage::bind(ui, p));
        Some(Self {
            tab_panel,
            close,
            fellowship,
            allegiance,
            friends,
            squelch,
            visible: false,
            in_world: false,
        })
    }

    /// The tab panel.
    #[must_use]
    pub fn tab_panel(&self) -> ElementId {
        self.tab_panel
    }

    /// The fellowship page.
    #[must_use]
    pub fn fellowship(&self) -> Option<&FellowshipPage> {
        self.fellowship.as_ref()
    }

    /// The allegiance page.
    #[must_use]
    pub fn allegiance(&self) -> Option<&AllegiancePage> {
        self.allegiance.as_ref()
    }

    /// The friends page.
    #[must_use]
    pub fn friends(&self) -> Option<&FriendsPage> {
        self.friends.as_ref()
    }

    /// The squelch page.
    #[must_use]
    pub fn squelch(&self) -> Option<&SquelchPage> {
        self.squelch.as_ref()
    }

    /// Start the tabs: the authored default page (allegiance) shows.
    pub fn activate_tabs(&mut self, ui: &mut UiRoot) {
        let t = self.tab_panel;
        ui.with::<TabPanel, _>(t, |w, ui| w.activate(ui, t));
        self.update_page_visibility(ui);
    }

    fn switch_to(&mut self, ui: &mut UiRoot, page: u32) {
        let t = self.tab_panel;
        ui.with::<TabPanel, _>(t, |w, ui| w.switch_to(ui, t, page));
        self.update_page_visibility(ui);
    }

    /// Show the allegiance page.
    pub fn show_allegiance(&mut self, ui: &mut UiRoot) {
        self.switch_to(ui, allegiance::PAGE_ID);
    }

    /// Show the fellowship page (the fellowship key's tab half).
    pub fn show_fellowship(&mut self, ui: &mut UiRoot) {
        self.switch_to(ui, fellowship::PAGE_ID);
    }

    /// Show the friends page.
    pub fn show_friends(&mut self, ui: &mut UiRoot) {
        self.switch_to(ui, friends::FRIENDS_PAGE_ID);
    }

    fn active_page(&self, ui: &UiRoot) -> u32 {
        ui.widget::<TabPanel>(self.tab_panel)
            .map_or(0, TabPanel::active_page)
    }

    /// The allegiance page is the active tab.
    #[must_use]
    pub fn is_showing_allegiance(&self, ui: &UiRoot) -> bool {
        self.active_page(ui) == allegiance::PAGE_ID
    }

    /// The fellowship page is the active tab.
    #[must_use]
    pub fn is_showing_fellowship(&self, ui: &UiRoot) -> bool {
        self.active_page(ui) == fellowship::PAGE_ID
    }

    /// The friends page is the active tab.
    #[must_use]
    pub fn is_showing_friends(&self, ui: &UiRoot) -> bool {
        self.active_page(ui) == friends::FRIENDS_PAGE_ID
    }

    fn update_page_visibility(&mut self, ui: &mut UiRoot) {
        let fellowship_shown = self.visible && self.is_showing_fellowship(ui);
        let allegiance_shown = self.visible && self.is_showing_allegiance(ui);
        let in_world = self.in_world;
        if let Some(f) = self.fellowship.as_mut() {
            f.set_page_visible(ui, fellowship_shown, in_world);
        }
        if let Some(a) = self.allegiance.as_mut() {
            a.set_page_visible(ui, allegiance_shown, in_world);
        }
    }

    /// The session ended: the pages' declarations are sent again.
    pub fn reset_session_declaration(&mut self) {
        if let Some(f) = self.fellowship.as_mut() {
            f.reset_page_visible_latch();
        }
        if let Some(a) = self.allegiance.as_mut() {
            a.reset_page_visible_latch();
        }
        self.in_world = false;
    }

    /// The character entered the world: the fellowship page declares
    /// itself again if it shows, and allegiance updates are asked for
    /// again whether or not the panel shows.
    pub fn redeclare_after_world_entry(&mut self, ui: &mut UiRoot) {
        self.in_world = true;
        let fellowship_shown = self.visible && self.is_showing_fellowship(ui);
        if let Some(f) = self.fellowship.as_mut() {
            f.set_page_visible(ui, fellowship_shown, true);
        }
        if let Some(a) = self.allegiance.as_mut() {
            a.redeclare_after_world_entry(ui);
        }
    }

    /// Show `view`.
    pub fn update(&mut self, ui: &mut UiRoot, view: &SocialView<'_>, rows: &mut dyn RowTemplates) {
        let was_in_world = self.in_world;
        self.in_world = view.in_world;
        if view.in_world && !was_in_world {
            // A declaration dropped before the world counts now.
            self.update_page_visibility(ui);
        }
        if let Some(f) = self.fellowship.as_mut() {
            f.update(ui, view, rows);
        }
        if let Some(a) = self.allegiance.as_mut() {
            a.update(ui, view, rows);
        }
        if self.visible {
            if let Some(f) = self.friends.as_mut() {
                f.update(
                    ui,
                    view.friends,
                    view.options.get(option_id::APPEAR_OFFLINE),
                    rows,
                );
            }
            if let Some(s) = self.squelch.as_mut() {
                s.update(ui, view.squelch, rows);
            }
        }
    }

    fn click(&mut self, ui: &mut UiRoot, element: ElementId) -> bool {
        if Some(element) == self.close {
            emit(ui, PanelOutput::ClosePanel(panel_id::SOCIAL));
            return true;
        }
        if self
            .fellowship
            .as_mut()
            .is_some_and(|f| f.on_click(ui, element))
        {
            return true;
        }
        if self
            .allegiance
            .as_mut()
            .is_some_and(|a| a.on_click(ui, element))
        {
            return true;
        }
        if self
            .friends
            .as_mut()
            .is_some_and(|f| f.on_click(ui, element))
        {
            return true;
        }
        self.squelch
            .as_mut()
            .is_some_and(|s| s.on_click(ui, element))
    }
}

impl PanelController for SocialPanelController {
    fn on_shown(&mut self, ui: &mut UiRoot) {
        self.visible = true;
        self.update_page_visibility(ui);
    }

    fn on_hidden(&mut self, ui: &mut UiRoot) {
        self.visible = false;
        self.update_page_visibility(ui);
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        match message {
            ElementMessage::TabChanged { .. } if element == self.tab_panel => {
                self.update_page_visibility(ui);
                true
            }
            ElementMessage::Clicked { .. } => self.click(ui, element),
            ElementMessage::TextChanged(_) => {
                self.fellowship
                    .as_ref()
                    .is_some_and(|f| f.on_text_changed(ui, element))
                    || self
                        .friends
                        .as_ref()
                        .is_some_and(|f| f.on_text_changed(ui, element))
                    || self
                        .squelch
                        .as_ref()
                        .is_some_and(|s| s.on_text_changed(ui, element))
            }
            _ => false,
        }
    }
}
