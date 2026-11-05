//! The journal's notes page and page list in the game window: fed the
//! session's journal when it changes and every frame the journal shows
//! (the countdown, a field the player left), committing the notes page
//! when the journal is hidden (OpenAC `JournalPanelController`'s
//! `OnHidden` on leaving the notes tab and on disposal).

use std::time::{SystemTime, UNIX_EPOCH};

use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::import::LayoutSource;
use ac_ui::panels::DatRowTemplates;
use ac_ui::panels::journal::JournalPanelController;

use super::facts::Dirty;
use super::social_panels::JOURNAL_WINDOW;
use super::window::WindowUi;
use crate::scene::Scene;

impl WindowUi {
    /// Feed the notes page and the page list.
    pub(super) fn update_journal_pages(&mut self, dirty: Dirty, scene: &Scene) {
        let shown = self.host.root.is_window_visible(JOURNAL_WINDOW);
        if !(dirty.journal || shown) {
            return;
        }
        let Some(view) = self.facts.journal.clone() else {
            return;
        };
        let player = self.facts.state.player;
        let player_cell = scene
            .object_by_guid(player)
            .and_then(|o| o.position.as_ref())
            .map_or(0, |p| p.cell_id);
        let now_unix_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX));
        let Self { host, content, .. } = self;
        let mut rows = DatRowTemplates::new(&*content, content.font(DEFAULT_FONT_ID));
        host.with_controller::<JournalPanelController, _>(JOURNAL_WINDOW, |c, ui| {
            c.update_journal(ui, &view, player_cell, now_unix_ms, &mut rows);
        });
    }

    /// The journal was hidden: the notes page commits what was typed.
    pub(super) fn journal_shown(&mut self, name: &str, visible: bool) {
        if name != JOURNAL_WINDOW || visible {
            return;
        }
        self.host
            .with_controller::<JournalPanelController, _>(JOURNAL_WINDOW, |c, ui| c.on_hidden(ui));
    }
}
