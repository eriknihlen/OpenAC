//! The combat window's attack controls fed from the session (OpenAC
//! `CombatUiController` over `CombatState` and `RuntimeCombatAttackState`):
//! the mode decides which controls show and whether the window does, the
//! player view carries the height, the power setting and the bar's level,
//! and the three combat options.

use ac_core::combat::CombatMode;
use ac_runtime::character::option_id;
use ac_ui::panels::combat_bar::{CombatBarController, CombatBarView};

use super::window::{COMBAT_WINDOW, WindowUi};

impl WindowUi {
    /// Feed the combat window's attack controls; show or hide the window
    /// when the mode changes it.
    pub(super) fn update_combat_bar(&mut self, mode: CombatMode) {
        let view = self.player_view.unwrap_or_default();
        let bar = CombatBarView {
            mode,
            attack: view.attack,
            auto_repeat_attack: view.option(option_id::AUTO_REPEAT_ATTACK),
            auto_target: view.option(option_id::AUTO_TARGET),
            view_combat_target: view.option(option_id::VIEW_COMBAT_TARGET),
        };
        let visible = self
            .host
            .with_controller::<CombatBarController, _>(COMBAT_WINDOW, |c, ui| c.update(ui, &bar))
            .flatten();
        match visible {
            Some(true) => {
                self.host.root.show_window(COMBAT_WINDOW);
            }
            Some(false) => {
                self.host.root.hide_window(COMBAT_WINDOW);
            }
            None => {}
        }
    }
}
