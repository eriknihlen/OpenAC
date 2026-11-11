//! The shared item cooldown sweep in the game window, as OpenAC's
//! `RetailUiRuntime.MountItemCooldowns` mounts it (once every window is
//! mounted, the ten overlays of the shared item prototype in layout
//! 0x21000037 go to every item list) and ticks it every frame on the
//! window's copy of the spellbook and the session clock.

use ac_ui::panels::cell_template::cooldown_sprites;
use ac_ui::panels::item_cooldown::ItemCooldownController;

use super::window::WindowUi;

impl WindowUi {
    /// OpenAC `RetailUiRuntime.MountItemCooldowns`.
    pub(super) fn mount_item_cooldowns(&mut self) {
        let Some(sprites) = cooldown_sprites(&self.content) else {
            self.notices
                .push("ui: the shared item cooldown overlays are missing from 0x21000037".into());
            return;
        };
        let root = self.host.root.root();
        self.item_cooldowns = Some(ItemCooldownController::bind(
            &mut self.host.root,
            root,
            &sprites,
        ));
    }

    /// One frame of the sweep.
    pub(super) fn tick_item_cooldowns(&mut self) {
        let Some(controller) = self.item_cooldowns.as_mut() else {
            return;
        };
        let now = self.facts.session_now();
        let objects = &self.facts.objects;
        let book = self
            .facts
            .player
            .as_mut()
            .and_then(|p| p.numbers.spellbook_mut());
        controller.tick(&mut self.host.root, book, objects, || now.unwrap_or(0.0));
    }
}
