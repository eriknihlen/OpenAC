//! The status pieces of the game window that are not panels: the status
//! box (spew box) fed from the session's client-local text, the frame-rate
//! display, the window layout persistence (restored on login, saved as the
//! player moves windows, `/saveui`, `/loadui`, `/saveautoui`,
//! `/loadautoui`), and the client commands that act on the interface.

use ac_plugin_ui::control_state::ControlStateFile;
use ac_runtime::communication::WindowRequest;
use ac_runtime::session::interface::InterfaceState;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::import::{LayoutSource, import_rooted};
use ac_ui::layout_persistence::{LayoutPersistence, LayoutStore};
use ac_ui::panels::fps::{self, FpsController, FrameRate};
use ac_ui::panels::jump_powerbar::{self as jump_powerbar, JumpPowerbarController};
use ac_ui::panels::selected_object::{SelectionFacts, SelectionHealth};
use ac_ui::panels::spew_box::{self, SpewBoxController};

use super::social_panels::SocialPage;
use super::social_panels::{HOUSE_PURCHASE_WINDOW, TRADE_WINDOW, VENDOR_WINDOW};
use super::window::{COMBAT_WINDOW, Hotkey, RADAR_WINDOW, VITALS_WINDOW, WindowUi};
use crate::scene::Scene;
use ac_ui::input::InputAction;
use ac_ui::panels::character::CharacterTab;
use ac_ui::panels::magic::{SpellKey, SpellbookPage};
use ac_ui::panels::panel_id;
use ac_ui::panels::toolbar::ToolbarKey;

/// The windows whose visibility the game decides (OpenAC's
/// state-managed windows): their visibility is neither restored nor saved.
const STATE_MANAGED: [&str; 9] = [
    super::salvage_window::SALVAGE_WINDOW,
    COMBAT_WINDOW,
    JUMP_POWERBAR_WINDOW,
    super::container_window::EXTERNAL_CONTAINER_WINDOW,
    VITALS_WINDOW,
    super::window::SIDE_VITALS_WINDOW,
    VENDOR_WINDOW,
    TRADE_WINDOW,
    HOUSE_PURCHASE_WINDOW,
];

/// The jump power bar's window.
pub const JUMP_POWERBAR_WINDOW: &str = "jump-powerbar";

/// A clock started with the window's UI.
#[derive(Debug, Clone, Copy)]
pub struct StatusClock(ac_clock::Instant);

impl Default for StatusClock {
    fn default() -> Self {
        Self(ac_clock::Instant::now())
    }
}

impl StatusClock {
    /// Time since the UI was built.
    #[must_use]
    pub fn elapsed(&self) -> std::time::Duration {
        self.0.elapsed()
    }
}

/// The window's status pieces.
#[derive(Debug, Default)]
pub struct StatusUi {
    /// The status box.
    pub spew: Option<SpewBoxController>,
    /// The frame-rate display.
    pub fps: Option<FpsController>,
    /// The frame rate over the last frames, the UI's own (shown while no
    /// world renderer reports its level-of-detail owner).
    pub frame_rate: FrameRate,
    /// The world renderer's level-of-detail owner as of this frame: its
    /// frame rate and the degrade multiplier in force (OpenAC
    /// `FpsRuntimeBindings` read both from `BuildingDegradeController`).
    pub level_of_detail: Option<(f32, f32)>,
    /// The window layouts.
    pub layouts: Option<LayoutPersistence>,
    /// The jump power bar.
    pub jump: Option<JumpPowerbarController>,
    /// The names the dats hold (materials), for composed item names.
    pub names: ac_ui::panels::appraisal::NameTables,
    /// The chat windows' opacity.
    pub opacity: Option<ac_ui::window_chrome::WindowOpacity>,
    /// The lock presentation.
    pub lock: ac_ui::window_chrome::LockPresentation,
    /// The character whose layout was restored.
    restored_for: Option<String>,
    /// The screen the layouts were last placed on.
    screen: Option<(i32, i32)>,
    /// The UI's own clock (the link indicator's timers).
    pub clock: StatusClock,
    /// The link as the session last reported it.
    pub link: Option<ac_runtime::session::LinkStatus>,
    /// The selected object's name in the world (for one not in the item
    /// table).
    selected_world_name: Option<String>,
    /// The mouse cursor's decision.
    pub cursor: ac_ui::cursor::CursorFeedbackController,
    /// The overlays over the world.
    pub overlays: super::overlays::Overlays,
    /// The object the world pick found under the pointer, with its name in
    /// the world (the window sets it each frame).
    pub world_hover: Option<(u32, Option<String>)>,
    /// Items let go over the world (no UI element under them), with the
    /// window pixel, for the window to place.
    pub world_drops: Vec<(ac_ui::event::ItemDragPayload, i32, i32)>,
    /// Press points of left presses on the world that moved past the drag
    /// distance, for the window to pick and maybe lift.
    pub world_drag_starts: Vec<(i32, i32)>,
}

/// The character option bit of world tooltips (options 1).
const SHOW_TOOLTIPS_BIT: u32 = 0x0000_0100;

/// What the world hover tooltip reads (OpenAC's `WorldTooltipRuntimeBindings`:
/// the pick at the pointer, the object's appropriate name, the ShowTooltips
/// character option).
struct WorldHoverFacts<'a> {
    hovered: Option<(u32, Option<String>)>,
    objects: &'a ac_core::items::ClientObjectTable,
    enabled: bool,
}

impl ac_ui::tooltip::WorldHover for WorldHoverFacts<'_> {
    fn hovered(&self) -> Option<u32> {
        self.hovered.as_ref().map(|(guid, _)| *guid)
    }

    fn name(&self, guid: u32) -> Option<String> {
        match self.objects.get(guid) {
            Some(object) => Some(object.appropriate_name().into_owned()),
            None => self
                .hovered
                .as_ref()
                .filter(|(hovered, _)| *hovered == guid)
                .and_then(|(_, name)| name.clone()),
        }
    }

    fn tooltips_enabled(&self) -> bool {
        self.enabled
    }
}

/// The selected-object strip's facts: the selection's health and mana from
/// the session's interaction facts and its name in the world.
#[must_use]
pub fn selection_facts(
    state: &InterfaceState,
    selected: Option<u32>,
    status: &StatusUi,
) -> SelectionFacts {
    let health = state
        .selected_health
        .filter(|h| Some(h.object_id) == selected)
        .map_or_else(SelectionHealth::default, |h| SelectionHealth {
            health_target: true,
            percent: h.percent,
        });
    let mana = state
        .selected_mana
        .filter(|m| Some(m.object_id) == selected)
        .map_or(0.0, |m| m.fraction);
    SelectionFacts {
        health,
        world_name: status.selected_world_name.clone(),
        mana,
    }
}

impl WindowUi {
    /// Mount the frame-rate display and the status box over everything,
    /// and start following the window layouts (every window at its
    /// default place until a character's layout is restored).
    pub(super) fn mount_status(&mut self) {
        self.status.names = ac_ui::panels::appraisal::NameTables::load(self.content.dats());
        self.mount_indicator_panels();
        if let Err(e) = self.mount_jump_powerbar() {
            self.notices
                .push(format!("ui: {JUMP_POWERBAR_WINDOW}: {e}"));
        } else {
            self.mounted.push(JUMP_POWERBAR_WINDOW);
        }
        let font = self.content.font(DEFAULT_FONT_ID);
        if let Some(layout) = import_rooted(
            &mut self.host.root,
            &self.content,
            fps::LAYOUT_ID,
            fps::DISPLAY_ELEMENT_ID,
            font,
        ) {
            let root = self.host.root.root();
            self.host.root.add_child(root, layout.root);
            self.status.fps = FpsController::bind(&mut self.host.root, &layout);
        }
        if self.status.fps.is_none() {
            self.notices
                .push("ui: the frame-rate display is not in the dats".to_owned());
        }
        let font = self.content.font(spew_box::RETAIL_FONT_ID);
        self.status.spew = Some(SpewBoxController::mount(&mut self.host.root, font));
        self.mount_tooltips();
        self.mount_overlays();
        let (w, h) = self.host.root.size();
        let screen = (w as i32, h as i32);
        let mut layouts =
            LayoutPersistence::new(&mut self.host, None, "default", screen, &STATE_MANAGED);
        layouts.reset_to_defaults(&mut self.host);
        self.status.layouts = Some(layouts);
        self.status.screen = Some(screen);
    }

    /// OpenAC `RetailUiRuntime.MountTooltips`: the presenter builds each
    /// element's authored popup from the dats (its own content reader, the
    /// popups being made while the window's is borrowed).
    fn mount_tooltips(&mut self) {
        match ac_ui::content::DatContent::new(self.content.dats().clone()) {
            Ok(popups) => {
                let font = popups.font(DEFAULT_FONT_ID);
                let popups = std::rc::Rc::new(popups);
                self.miss_caches
                    .contents
                    .push(std::rc::Rc::downgrade(&popups));
                let presenter =
                    ac_ui::tooltip::TooltipPresenter::new(Box::new(move |ui, layout, root| {
                        import_rooted(ui, &*popups, layout, root, font.clone())
                    }));
                self.host.set_tooltips(presenter);
                self.apply_tooltip_settings();
            }
            Err(e) => self.notices.push(format!("ui: tooltips: {e}")),
        }
    }

    /// The tooltip settings (OpenAC applies `MiscSettings` to the
    /// presenter and the root's hover delay).
    pub(super) fn apply_tooltip_settings(&mut self) {
        let misc = self.settings().misc;
        if let Some(t) = self.host.tooltips_mut() {
            t.enabled = misc.tooltip_enable;
        }
        self.host.root.tooltip_delay_ms = (misc.tooltip_delay_seconds * 1000.0) as i64;
    }

    /// Where window layouts and the plugin windows' remembered control
    /// values are kept (natively both in the settings file; none: they last
    /// the session).
    pub fn set_stores(
        &mut self,
        layouts: Option<Box<dyn LayoutStore>>,
        control_state: Option<Box<dyn ControlStateFile>>,
    ) {
        self.plugins.set_control_state_file(control_state);
        if let Some(persistence) = self.status.layouts.as_mut() {
            persistence.set_store(layouts);
        }
    }

    /// The session's status text and interface requests, and the
    /// character: on entering the world the character's layout is
    /// restored.
    pub(super) fn feed_status(&mut self, scene: &mut Scene) {
        let in_world = scene.in_world();
        let charge = scene.player_view().and_then(|v| v.jump_charge);
        if let Some(jump) = self.status.jump.as_mut()
            && let Some(visible) = jump.update(&mut self.host.root, charge, false)
        {
            if visible {
                self.host.root.show_window(JUMP_POWERBAR_WINDOW);
            } else {
                self.host.root.hide_window(JUMP_POWERBAR_WINDOW);
            }
        }
        if let Some(link) = scene.link() {
            self.status.link = Some(link);
        }
        if let Some(spew) = self.status.spew {
            let ui = &mut self.host.root;
            spew.set_gameplay_active(ui, in_world);
            for text in scene.take_status_text() {
                spew.enqueue(ui, &text);
            }
        }
        self.status.selected_world_name = scene
            .player_view()
            .and_then(|v| v.selected)
            .and_then(|guid| scene.object_by_guid(guid))
            .and_then(|o| o.name.clone());
        // The game shows once the character is in the world and no pre-game
        // screen covers it (its in-game windows hidden under one are not the
        // player's choice; `hide_in_game_windows`).
        let covered = self.pregame_active();
        let character = (in_world && !covered)
            .then(|| scene.player().and_then(|p| p.name.clone()))
            .flatten();
        if let Some(layouts) = self.status.layouts.as_mut() {
            match character {
                Some(name) if self.status.restored_for.as_deref() != Some(name.as_str()) => {
                    self.plugins.set_control_state_character(&name);
                    layouts.set_gameplay_active(&mut self.host, true);
                    layouts.set_character(&mut self.host, &name);
                    layouts.restore_all(&mut self.host);
                    self.status.restored_for = Some(name);
                }
                Some(_) if !layouts.gameplay_active() => {
                    // A screen covered the world for a while (the windows
                    // under it shown again): the player's changes count
                    // again.
                    layouts.set_gameplay_active(&mut self.host, true);
                }
                Some(_) => {}
                None if !in_world => {
                    layouts.set_gameplay_active(&mut self.host, false);
                    self.status.restored_for = None;
                }
                None => {}
            }
        }
        for request in scene.take_window_requests() {
            self.window_request(&request);
        }
    }

    /// Take what plugins asked of the OS window: minimize (`true`) or
    /// restore, oldest first.
    pub fn take_minimize_requests(&mut self) -> Vec<bool> {
        std::mem::take(&mut self.minimize_requests)
    }

    /// A client command acting on the interface.
    pub(super) fn window_request(&mut self, request: &WindowRequest) {
        // What a plugin asks of the window (OpenAC `IPluginClipboard`,
        // `IHostWindow`).
        match request {
            WindowRequest::SetClipboard(text) => {
                if let Err(Some(e)) = self.clipboard.set_text(text) {
                    self.notices.push(format!("ui: clipboard: {e}"));
                }
                return;
            }
            WindowRequest::Minimize => {
                self.minimize_requests.push(true);
                return;
            }
            WindowRequest::Restore => {
                self.minimize_requests.push(false);
                return;
            }
            _ => {}
        }
        // OpenAC `GameplayInputCommandController.HandleEscape`: the options
        // window's gameplay page, as its key opens it.
        if *request == WindowRequest::GameplayOptionsPage {
            self.handle_input_action(InputAction::ToggleGameplayOptionsPage);
            return;
        }
        let mut display = self.settings().display.clone();
        let changed = match *request {
            // OpenAC `RuntimeSettingsController.ToggleFrameRate`: the
            // display option flips and is saved.
            WindowRequest::ToggleFrameRate => {
                display.show_fps = !display.show_fps;
                true
            }
            // OpenAC `LiveSessionRuntimeFactory`'s `/render` hooks: the
            // display setting is saved and applied.
            WindowRequest::SetLandscapeRadius(radius) => {
                display.landscape_draw_distance = radius;
                true
            }
            WindowRequest::SetFieldOfView(degrees) => {
                display.field_of_view = degrees as f32;
                true
            }
            _ => false,
        };
        if changed {
            self.edit_settings(ac_ui::panels::options::Section::Display, true, |s| {
                s.display = display;
            });
            return;
        }
        let Some(layouts) = self.status.layouts.as_mut() else {
            return;
        };
        match request {
            WindowRequest::SaveUi(name) => layouts.save_named(&mut self.host, name),
            WindowRequest::LoadUi(name) => layouts.restore_named(&mut self.host, name),
            WindowRequest::SaveAutoUi => layouts.save_all(&mut self.host),
            WindowRequest::LoadAutoUi => layouts.restore_all(&mut self.host),
            WindowRequest::ToggleFrameRate
            | WindowRequest::SetLandscapeRadius(_)
            | WindowRequest::SetFieldOfView(_)
            | WindowRequest::SetClipboard(_)
            | WindowRequest::Minimize
            | WindowRequest::Restore
            | WindowRequest::GameplayOptionsPage => {}
        }
    }

    /// One frame of the status pieces on a screen of `size`: the frame
    /// rate, and the layouts (what the player changed is saved; a new
    /// screen size reflows every window).
    pub(super) fn tick_status(&mut self, dt: f64, size: (u32, u32)) {
        self.status.frame_rate.tick(dt);
        let (frames_per_second, degrade_multiplier) = self
            .status
            .level_of_detail
            .unwrap_or((self.status.frame_rate.fps(), 0.0));
        let show_fps = self.settings().display.show_fps;
        if let Some(fps) = self.status.fps {
            fps.update(
                &mut self.host.root,
                f64::from(frames_per_second),
                f64::from(degrade_multiplier),
                show_fps,
            );
        }
        self.tick_overlays();
        self.apply_tooltip_settings();
        // OpenAC `RetailWindowOpacityController` and
        // `RetailWindowLockPresentationController`, from the chat settings.
        let chat = self.settings().chat;
        let opacity = self.status.opacity.get_or_insert_with(|| {
            ac_ui::window_chrome::WindowOpacity::new(chat.default_opacity, chat.active_opacity)
        });
        if (opacity.default_opacity(), opacity.active_opacity())
            != (chat.default_opacity, chat.active_opacity)
        {
            opacity.set_opacity(chat.default_opacity, chat.active_opacity);
        }
        opacity.apply(&mut self.host.root);
        self.status.lock.follow(&mut self.host.root);
        let screen = (
            i32::try_from(size.0).unwrap_or(i32::MAX),
            i32::try_from(size.1).unwrap_or(i32::MAX),
        );
        if let Some(layouts) = self.status.layouts.as_mut() {
            layouts.follow(&mut self.host);
            if self.status.screen != Some(screen) {
                self.status.screen = Some(screen);
                self.host.root.set_size(size.0 as f32, size.1 as f32);
                layouts.set_screen_size(screen.0, screen.1);
                layouts.reflow_to_screen(&mut self.host);
            }
            for failure in layouts.take_failures() {
                self.notices.push(failure);
            }
        }
    }
}

/// The hotkey an interface action of the key map stands for (the panels
/// the F-keys open by default), if it is one.
#[must_use]
pub fn hotkey_for_action(action: InputAction) -> Option<Hotkey> {
    use InputAction as A;
    Some(match action {
        A::ToggleAllegiancePanel => Hotkey::Social(SocialPage::Allegiance),
        A::ToggleFellowshipPanel => Hotkey::Social(SocialPage::Fellowship),
        A::ToggleSpellbookPanel => Hotkey::Spellbook(SpellbookPage::Spells),
        A::ToggleSpellComponentsPanel => Hotkey::Spellbook(SpellbookPage::Components),
        A::ToggleAttributesPanel => Hotkey::Character(CharacterTab::Attributes),
        A::ToggleSkillsPanel => Hotkey::Character(CharacterTab::Skills),
        A::ToggleWorldPanel => Hotkey::WorldPanel,
        A::ToggleOptionsPanel => Hotkey::Options,
        A::ToggleInventoryPanel => Hotkey::Inventory,
        _ => {
            if let Some(key) = toolbar_key_for_action(action) {
                return Some(Hotkey::Toolbar(key));
            }
            return spell_key_for_action(action).map(Hotkey::Spell);
        }
    })
}

/// The toolbar key a quick-slot action names (OpenAC
/// `ToolbarInputController.TryMapShortcut`): use slots 1-18, select slots
/// 1-9, the new shortcut.
#[must_use]
pub fn toolbar_key_for_action(action: InputAction) -> Option<ToolbarKey> {
    use InputAction as A;
    const USE: [InputAction; 18] = [
        A::UseQuickSlot_1,
        A::UseQuickSlot_2,
        A::UseQuickSlot_3,
        A::UseQuickSlot_4,
        A::UseQuickSlot_5,
        A::UseQuickSlot_6,
        A::UseQuickSlot_7,
        A::UseQuickSlot_8,
        A::UseQuickSlot_9,
        A::UseQuickSlot_10,
        A::UseQuickSlot_11,
        A::UseQuickSlot_12,
        A::UseQuickSlot_13,
        A::UseQuickSlot_14,
        A::UseQuickSlot_15,
        A::UseQuickSlot_16,
        A::UseQuickSlot_17,
        A::UseQuickSlot_18,
    ];
    const SELECT: [InputAction; 9] = [
        A::SelectQuickSlot_1,
        A::SelectQuickSlot_2,
        A::SelectQuickSlot_3,
        A::SelectQuickSlot_4,
        A::SelectQuickSlot_5,
        A::SelectQuickSlot_6,
        A::SelectQuickSlot_7,
        A::SelectQuickSlot_8,
        A::SelectQuickSlot_9,
    ];
    if action == A::CreateShortcut {
        return Some(ToolbarKey::CreateShortcut);
    }
    if let Some(slot) = USE.iter().position(|a| *a == action) {
        return Some(ToolbarKey::Use(slot));
    }
    SELECT
        .iter()
        .position(|a| *a == action)
        .map(ToolbarKey::Select)
}

/// The spell-bar key a magic action names (OpenAC
/// `SpellcastingUiController.Handle`): spell slots 1-12, the bar and spell
/// steps, cast the selected spell.
#[must_use]
pub fn spell_key_for_action(action: InputAction) -> Option<SpellKey> {
    use InputAction as A;
    const SLOTS: [InputAction; 12] = [
        A::UseSpellSlot_1,
        A::UseSpellSlot_2,
        A::UseSpellSlot_3,
        A::UseSpellSlot_4,
        A::UseSpellSlot_5,
        A::UseSpellSlot_6,
        A::UseSpellSlot_7,
        A::UseSpellSlot_8,
        A::UseSpellSlot_9,
        A::UseSpellSlot_10,
        A::UseSpellSlot_11,
        A::UseSpellSlot_12,
    ];
    if let Some(slot) = SLOTS.iter().position(|a| *a == action) {
        return Some(SpellKey::UseSlot(slot));
    }
    Some(match action {
        A::CombatPrevSpellTab => SpellKey::PrevTab,
        A::CombatNextSpellTab => SpellKey::NextTab,
        A::CombatFirstSpellTab => SpellKey::FirstTab,
        A::CombatLastSpellTab => SpellKey::LastTab,
        A::CombatPrevSpell => SpellKey::PrevSpell,
        A::CombatNextSpell => SpellKey::NextSpell,
        A::CombatFirstSpell => SpellKey::FirstSpell,
        A::CombatLastSpell => SpellKey::LastSpell,
        A::CombatCastCurrentSpell => SpellKey::CastCurrent,
        _ => return None,
    })
}

/// The main panel an interface action toggles (OpenAC
/// `RetailUiRuntime.HandleInputAction`'s `ToggleWindow` cases), if any.
#[must_use]
pub fn panel_for_action(action: InputAction) -> Option<u32> {
    use InputAction as A;
    Some(match action {
        A::ToggleCharacterInfoPanel => panel_id::CHARACTER_INFORMATION,
        A::TogglePositiveMagicPanel => panel_id::POSITIVE_EFFECTS,
        A::ToggleNegativeMagicPanel => panel_id::NEGATIVE_EFFECTS,
        A::ToggleLinkStatusPanel => panel_id::LINK_STATUS,
        A::ToggleVitaePanel => panel_id::VITAE,
        A::ToggleSocialPanel => panel_id::SOCIAL,
        A::ToggleSpellManagementPanel => panel_id::MAGIC,
        A::ToggleCharacterDetailPanel => panel_id::CHARACTER,
        _ => return None,
    })
}

impl WindowUi {
    /// The object the world pick found under the pointer this frame, and
    /// its name in the world.
    pub fn set_world_hover(&mut self, hovered: Option<(u32, Option<String>)>) {
        self.status.world_hover = hovered;
    }

    /// Items let go over the world since last asked.
    pub fn take_world_drops(&mut self) -> Vec<(ac_ui::event::ItemDragPayload, i32, i32)> {
        std::mem::take(&mut self.status.world_drops)
    }

    /// Press points of world drags since last asked.
    pub fn take_world_drag_starts(&mut self) -> Vec<(i32, i32)> {
        std::mem::take(&mut self.status.world_drag_starts)
    }

    /// Start the drag the world pick `hit` lifts, if any (out of combat a
    /// loose item lifts itself, your own character your main pack). True
    /// when a drag began.
    pub fn begin_world_drag(&mut self, hit: u32) -> bool {
        let state = &self.facts.state;
        let Some((payload, ghost)) = ac_ui::world_drag::lift(
            hit,
            state.player,
            state.combat_mode,
            &self.facts.objects,
            state.pending_item,
            &mut self.icons,
        ) else {
            return false;
        };
        self.host.root.begin_world_drag(payload, ghost)
    }

    /// One tick of the element and world tooltips.
    pub(super) fn tick_tooltips(&mut self, dt: f64, now_ms: i64) {
        let enabled = self
            .player_view
            .is_some_and(|v| v.options1 & SHOW_TOOLTIPS_BIT != 0);
        let hover = WorldHoverFacts {
            hovered: self.status.world_hover.clone(),
            objects: &self.facts.objects,
            enabled,
        };
        self.host.tick(dt, now_ms, Some(&hover));
    }

    /// What a click on the world needs of the window's facts: whether a
    /// target mode waits, and whether the player wields `guid`.
    #[must_use]
    pub fn click_facts(&self, guid: u32) -> crate::world_click::ClickFacts {
        let state = &self.facts.state;
        crate::world_click::ClickFacts {
            target_mode: state.mode != ac_runtime::inventory::InteractionMode::None,
            wielded_by_player: state.player != 0
                && self
                    .facts
                    .objects
                    .get(guid)
                    .is_some_and(|o| o.wielder_id == state.player),
        }
    }

    /// How many of `item` a drag moves: the split quantity for the
    /// selection, else the whole stack (0 when the item is not known).
    #[must_use]
    pub fn drag_amount(&mut self, item: u32) -> u32 {
        let Some(object) = self.facts.objects.get(item) else {
            return 0;
        };
        let stack = u32::try_from(object.stack_size.max(1)).unwrap_or(1);
        let selected = self.player_view.and_then(|v| v.selected).unwrap_or(0);
        self.host
            .with_controller::<ac_ui::panels::toolbar::ToolbarWindowController, _>(
                super::window::TOOLBAR_WINDOW,
                |c, _| c.split.quantity().object_split_size(item, selected, stack),
            )
            .unwrap_or(stack)
    }

    /// The cursor the pointer shows now (OpenAC `CursorFeedbackController.Update`
    /// over the session's click mode, busy count and combat mode, and the
    /// object the world pick found under the pointer).
    pub fn cursor_feedback(&mut self) -> ac_ui::cursor::CursorFeedback {
        use ac_runtime::inventory::InteractionMode;
        use ac_ui::cursor::{CursorFacts, CursorTargetMode};
        let state = &self.facts.state;
        let objects = &self.facts.objects;
        let player = state.player;
        let source = state.mode.source();
        let compatible = |target: u32| {
            source.is_some_and(|s| {
                ac_runtime::inventory::target_compatible(objects, player, s, target)
            })
        };
        let facts = CursorFacts {
            target_mode: match state.mode {
                InteractionMode::None => CursorTargetMode::None,
                InteractionMode::Use => CursorTargetMode::Use,
                InteractionMode::Examine => CursorTargetMode::Examine,
                InteractionMode::UseItemOnTarget(_) => CursorTargetMode::UseTarget,
            },
            busy_count: state.busy_count,
            combat_mode: state.combat_mode,
            world_target: self
                .status
                .world_hover
                .as_ref()
                .map_or(0, |(guid, _)| *guid),
            target_compatible: &compatible,
        };
        self.status.cursor.update(&mut self.host.root, &facts)
    }

    /// The dats the window reads.
    #[must_use]
    pub fn dats(&self) -> &std::sync::Arc<ac_dat::DatCollection> {
        self.content.dats()
    }

    /// The main panels (the windows the toolbar's panel buttons open).
    #[must_use]
    pub fn main_panels(&self) -> &ac_ui::panels::main_panels::MainPanels {
        &self.panels
    }

    /// The player's combat mode as the window last heard it (the key map's
    /// combat scope follows it).
    #[must_use]
    pub fn combat_mode(&self) -> ac_core::combat::CombatMode {
        self.facts.state.combat_mode
    }

    /// The world renderer's level-of-detail owner this frame: the frame
    /// rate it measured and the degrade multiplier in force, which the
    /// frame-rate display shows.
    pub fn set_level_of_detail(&mut self, frames_per_second: f32, degrade_multiplier: f32) {
        self.status.level_of_detail = Some((frames_per_second, degrade_multiplier));
    }

    /// An action of the key map the interface answers (OpenAC
    /// `RetailUiRuntime.HandleInputAction`): the panel toggles, the compass;
    /// false when it is not the interface's (the world gets it).
    pub fn handle_input_action(&mut self, action: InputAction) -> bool {
        // The examination window takes the examine key first: it closes
        // while it shows (else the key examines the selection).
        if self
            .host
            .with_controller::<ac_ui::panels::appraisal::AppraisalController, _>(
                super::window::EXAMINATION_WINDOW,
                |c, ui| c.handle_input_action(ui, action),
            )
            .unwrap_or(false)
        {
            return true;
        }
        if let Some(hotkey) = hotkey_for_action(action) {
            return self.run_hotkey(hotkey);
        }
        if let Some(panel) = panel_for_action(action) {
            self.panels.toggle(&mut self.host.root, panel);
            return true;
        }
        if action == InputAction::ToggleCompass {
            self.host.root.toggle_window(RADAR_WINDOW);
            return true;
        }
        // OpenAC `GameplayInputCommandController`: the floating chat
        // windows.
        if let Some(n) = super::chat_windows::floating_chat_for_action(action) {
            self.toggle_floating_chat(n);
            return true;
        }
        if let Some(key) = super::panel_keys::page_key_for_action(action) {
            return self.run_page_key(key);
        }
        if matches!(
            action,
            InputAction::EnterChatMode | InputAction::ToggleChatEntry
        ) {
            return self.chat_entry_key(action, None);
        }
        if is_chat_action(action) {
            self.run_chat_action(action);
            return true;
        }
        // OpenAC `SelectionInteractionController.HandleInputAction`'s
        // `SelectionSplitStack`, wired to `SelectedObjectController
        // .FocusSplitStackEntry`: the selected stack's typed quantity takes
        // the keyboard, its text selected (nothing for no stack).
        if action == InputAction::SelectionSplitStack {
            if let Some(selected) = self.player_view.and_then(|v| v.selected) {
                self.host
                    .with_controller::<ac_ui::panels::toolbar::ToolbarWindowController, _>(
                        super::window::TOOLBAR_WINDOW,
                        |c, ui| c.split.focus_entry(ui, selected),
                    );
            }
            return true;
        }
        false
    }

    /// A chat entry key (OpenAC `ChatWindowController.EnterChatMode` and
    /// `ToggleChatEntry`): Enter Chat Mode focuses the entry and selects
    /// its text; Toggle Chat Entry focuses it, or gives the focus up when
    /// it has it. `key` (the keyboard key that pressed it) types nothing
    /// into the entry until it is let go.
    pub fn chat_entry_key(&mut self, action: InputAction, key: Option<ac_ui::Key>) -> bool {
        self.host
            .with_controller::<ac_ui::panels::chat::ChatWindowController, _>(
                super::window::CHAT_WINDOW,
                |c, ui| {
                    if action == InputAction::EnterChatMode {
                        c.enter_chat_mode(ui, key);
                    } else {
                        c.toggle_chat_entry(ui, key);
                    }
                },
            );
        true
    }

    /// How many of the selected stack the split chooser names (the give
    /// and drop keys move that many); 0 with nothing selected.
    #[must_use]
    pub fn selection_split_amount(&mut self) -> u32 {
        match self.player_view.and_then(|v| v.selected) {
            Some(selected) => self.drag_amount(selected),
            None => 0,
        }
    }

    /// OpenAC `RetailUiRuntime.HandleInputAction`'s chat keys: reply to the
    /// last teller, the monarch or the patron, start a command, or start a
    /// tell to the selected player (by the name the item table holds).
    fn run_chat_action(&mut self, action: InputAction) {
        let tell_to = if action == InputAction::ChatTellToSelected {
            let selected = self.player_view.and_then(|v| v.selected).unwrap_or(0);
            if !ac_core::chat::is_player_object_id(selected) {
                return;
            }
            match self
                .facts
                .objects
                .get(selected)
                .map(|o| o.appropriate_name().into_owned())
                .filter(|n| !n.is_empty())
            {
                Some(name) => Some(name),
                None => return,
            }
        } else {
            None
        };
        self.host
            .with_controller::<ac_ui::panels::chat::ChatWindowController, _>(
                super::window::CHAT_WINDOW,
                |c, ui| {
                    let entry = c.entry().clone();
                    match action {
                        InputAction::ChatReply => {
                            c.start_reply(ui, entry.last_incoming_tell_sender.as_deref());
                        }
                        InputAction::ChatMonarchReply => {
                            c.start_reply(ui, entry.last_monarch_sender.as_deref());
                        }
                        InputAction::ChatPatronReply => {
                            c.start_reply(ui, entry.last_patron_sender.as_deref());
                        }
                        InputAction::ChatStartCommand => c.start_command(ui),
                        _ => {
                            if let Some(name) = &tell_to {
                                c.start_tell(ui, name);
                            }
                        }
                    }
                },
            );
    }
}

/// The chat keys the interface answers.
fn is_chat_action(action: InputAction) -> bool {
    matches!(
        action,
        InputAction::ChatReply
            | InputAction::ChatMonarchReply
            | InputAction::ChatPatronReply
            | InputAction::ChatStartCommand
            | InputAction::ChatTellToSelected
            | InputAction::EnterChatMode
            | InputAction::ToggleChatEntry
    )
}

/// The key map's action is the interface's to answer first. The examine key
/// is offered to the examination window, which closes on it while it shows;
/// what the interface does not take goes on to the session.
#[must_use]
pub fn is_interface_action(action: InputAction) -> bool {
    hotkey_for_action(action).is_some()
        || panel_for_action(action).is_some()
        || action == InputAction::ToggleCompass
        || action == InputAction::SelectionSplitStack
        || action == InputAction::SelectionExamine
        || super::panel_keys::page_key_for_action(action).is_some()
        || super::chat_windows::floating_chat_for_action(action).is_some()
        || is_chat_action(action)
}

impl WindowUi {
    /// OpenAC `RetailUiRuntime.MountJumpPowerbar`: centred, 105 pixels
    /// above the bottom of the screen, hidden until a jump is charged.
    fn mount_jump_powerbar(&mut self) -> Result<(), String> {
        let info = ac_ui::import::import_infos(&self.content, jump_powerbar::LAYOUT_ID)
            .ok_or("layout 0x21000072 is not in the dats")?;
        let layout = self.import(jump_powerbar::LAYOUT_ID)?;
        let mut jump = JumpPowerbarController::bind(&mut self.host.root, &layout)
            .ok_or("the jump meter is missing")?;
        let (sw, sh) = self.host.root.size();
        let (w, h) = self
            .host
            .root
            .element(layout.root)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        super::mount::mount_window(
            &mut self.host.root,
            JUMP_POWERBAR_WINDOW,
            layout.root,
            &super::mount::MountOptions {
                left: ((sw - w) * 0.5).max(0.0),
                top: (sh - h - 105.0).max(0.0),
                visible: false,
                dat_constraint_source: Some(info),
                content_click_through: Some(false),
                ..super::mount::MountOptions::default()
            },
        )?;
        if jump.update(&mut self.host.root, None, true) == Some(false) {
            self.host.root.hide_window(JUMP_POWERBAR_WINDOW);
        }
        self.status.jump = Some(jump);
        Ok(())
    }
}
