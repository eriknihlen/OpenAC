//! The window's gameplay input: the keys, buttons and wheel the UI leaves
//! go through the input dispatcher (the retail key map, or the player's
//! bindings), and what fires is routed as OpenAC's gameplay router does:
//! the alternate-camera key, mouse look and the right-button orbit, the
//! wheel, the camera views, the movement presses and the client's own
//! commands. Each action is routed as it fires (a scope change it makes
//! applies to the next one). Held movement is polled from the dispatcher
//! each frame and sent to the session when it changes; held camera keys
//! move the chase camera at the player's adjustment speed, and the mouse
//! turns it by the player's sensitivity (the camera settings).
//!
//! The interface's actions (panel toggles, the compass, the quick slots and
//! the spell bar, the magic scope following the player's combat mode, the
//! split key) go to the window's UI ([`GameplayOutput::Interface`]), the
//! chat entry keys with the key that pressed them
//! ([`GameplayOutput::ChatEntry`]); the mouse selects go to
//! the world pick ([`GameplayOutput::WorldClick`]); the selection keys, the
//! combat toggle and log out go to the session ([`session_output`]); the
//! attack keys and character-option actions are not routed here.

use std::collections::HashSet;

use ac_runtime::gameplay::selection_cycle::{SelectionCycleDirection, SelectionCycleKind};
use ac_runtime::gameplay::{CombatAttackCommand, GameplayCommand, InputActivation};
use ac_runtime::inventory::ItemCommand;
use ac_runtime::movement::{MovementCommand, MovementKeys, Posture};
use ac_ui::input::chord::mouse;
use ac_ui::input::dispatcher::{DispatchEvent, InputDevices};
use ac_ui::input::{ActivationType, InputAction, InputDispatcher, KeyBindings, MouseLookState};
use ac_ui::{Key, Modifiers};

use crate::camera::{
    CameraController, CameraMode, ChaseAdjustment, ChaseTuning, FlyInput, MOUSE_ORBIT_SCALE,
};
use crate::world_click::WorldClick;

#[path = "modern_mouse.rs"]
pub mod modern_mouse;
use modern_mouse::ModernMouse;
/// One wheel notch zooms the chase camera 0.8 steps.
const WHEEL_ZOOM_STEP: f32 = 0.8;

/// What the window carries out for the gameplay input.
#[derive(Debug, Clone, PartialEq)]
pub enum GameplayOutput {
    /// A movement command for the session.
    Movement(MovementCommand),
    /// Step the time of day.
    CycleTimeOfDay,
    /// Step the weather.
    CycleWeather,
    /// Mute or unmute.
    ToggleMute,
    /// The fly camera on or off (an action the retail key map leaves
    /// unbound; the console's `/fly`).
    ToggleFly,
    /// Follow the player or orbit (an action the retail key map leaves
    /// unbound).
    TogglePlayerMode,
    /// The Escape key went down (it gives back a cursor the fly camera
    /// holds).
    Escape,
    /// Hide and hold the cursor (mouse look began).
    GrabCursor,
    /// Give the cursor back (mouse look ended).
    ReleaseCursor,
    /// A line for the window's event log.
    Notice(String),
    /// Save the next frame as `ScreenShotNNNNN.png` in the screenshots
    /// folder (the retail Print Screen command, keypad `*`).
    CaptureScreenshot,
    /// Log what stands near the player (developer command, Ctrl+F3).
    DumpNearby,
    /// An interface action (a panel toggle, the compass) for the window's
    /// UI to answer.
    Interface(InputAction),
    /// A chat entry key (`EnterChatMode`, `ToggleChatEntry`) for the
    /// window's UI, with the keyboard key that pressed it (`None` when no
    /// key did): the entry ignores that key until it is let go, so it does
    /// not type into the entry it focused.
    ChatEntry(InputAction, Option<Key>),
    /// A click on the 3-D view (select, examine or use what is under the
    /// pointer).
    WorldClick(crate::world_click::WorldClick),
    /// A selection or combat request for the session (the selection keys,
    /// the combat toggle).
    Gameplay(GameplayCommand),
    /// An item request for the session (use, examine or pick up the
    /// selection).
    Item(ItemCommand),
    /// Log the character out (the log-out key).
    LogOut,
    /// Flip this character option (a character-option key; the window
    /// reads its value now).
    ToggleCharacterOption(u32),
    /// A key capture for the key configuration screen ended: the chord, or
    /// `None` when it was cancelled (Escape).
    Captured(Option<ac_ui::input::KeyChord>),
}

/// What a pressed selection, combat or log-out action asks of the session
/// (OpenAC `RuntimeGameplayInputPriorityTargets.HandleSelectionAction`,
/// `SelectionInteractionController.HandleInputAction`,
/// `GameplayInputCommandController.Handle`); `None` for any other action.
#[must_use]
pub fn session_output(action: InputAction) -> Option<GameplayOutput> {
    use GameplayCommand as G;
    use InputAction as A;
    use SelectionCycleDirection as D;
    use SelectionCycleKind as K;
    let cycle = |kind, direction| G::CycleSelection { kind, direction };
    let gameplay = match action {
        A::SelectionClosestMonster => G::SelectNearestMonster,
        A::SelectionPreviousSelection => G::SelectPrevious,
        A::SelectionSelf => G::SelectSelf,
        A::SelectionLastAttacker => G::SelectLastAttacker,
        A::SelectionClosestCompassItem => cycle(K::CompassItem, D::Closest),
        A::SelectionPreviousCompassItem => cycle(K::CompassItem, D::Previous),
        A::SelectionNextCompassItem => cycle(K::CompassItem, D::Next),
        A::SelectionClosestItem => cycle(K::Item, D::Closest),
        A::SelectionPreviousItem => cycle(K::Item, D::Previous),
        A::SelectionNextItem => cycle(K::Item, D::Next),
        A::SelectionPreviousMonster => cycle(K::Monster, D::Previous),
        A::SelectionNextMonster => cycle(K::Monster, D::Next),
        A::SelectionClosestPlayer => cycle(K::Player, D::Closest),
        A::SelectionPreviousPlayer => cycle(K::Player, D::Previous),
        A::SelectionNextPlayer => cycle(K::Player, D::Next),
        A::SelectionClosestUnopenedCorpse => cycle(K::UnopenedCorpse, D::Closest),
        A::SelectionNextUnopenedCorpse => cycle(K::UnopenedCorpse, D::Next),
        A::SelectionUseClosestUnopenedCorpse => G::UseUnopenedCorpse(D::Closest),
        A::SelectionUseNextUnopenedCorpse => G::UseUnopenedCorpse(D::Next),
        A::SelectionPreviousFellow => G::CycleFellow { previous: true },
        A::SelectionNextFellow => G::CycleFellow { previous: false },
        A::CombatToggleCombat => G::ToggleCombat,
        A::SelectionExamine => return Some(GameplayOutput::Item(ItemCommand::ExamineSelected)),
        A::UseSelected => return Some(GameplayOutput::Item(ItemCommand::UseSelected)),
        A::SelectionPickUp => return Some(GameplayOutput::Item(ItemCommand::PickUpSelected)),
        A::SelectionPlaceInInventory => {
            return Some(GameplayOutput::Item(ItemCommand::PlaceSelectedInPack));
        }
        A::SelectionPlaceInMainPack => {
            return Some(GameplayOutput::Item(ItemCommand::PickUpSelectedToMainPack));
        }
        // The window fills in the split quantity of the selected stack.
        A::SelectionGiveToTarget => {
            return Some(GameplayOutput::Item(ItemCommand::GiveSelectedToPrevious {
                amount: 0,
            }));
        }
        A::SelectionDrop => {
            return Some(GameplayOutput::Item(ItemCommand::DropSelected {
                amount: 0,
            }));
        }
        A::LOGOUT => return Some(GameplayOutput::LogOut),
        _ => return None,
    };
    Some(GameplayOutput::Gameplay(gameplay))
}

/// The attack request an attack, aim or power key makes (OpenAC
/// `CombatAttackInputFrameAdapter.HandleInputAction`; the missile accuracy
/// keys move the power bar, the aim keys attack at their height).
#[must_use]
pub fn combat_attack_command(action: InputAction) -> Option<CombatAttackCommand> {
    use CombatAttackCommand as C;
    use InputAction as A;
    Some(match action {
        A::CombatLowAttack | A::CombatAimLow => C::LowAttack,
        A::CombatMediumAttack | A::CombatAimMedium => C::MediumAttack,
        A::CombatHighAttack | A::CombatAimHigh => C::HighAttack,
        A::CombatDecreaseAttackPower | A::CombatDecreaseMissileAccuracy => C::DecreasePower,
        A::CombatIncreaseAttackPower | A::CombatIncreaseMissileAccuracy => C::IncreasePower,
        _ => return None,
    })
}

/// A press of this key becomes a motion, which ends a repeating attack
/// (OpenAC `CombatAttackInputFrameAdapter.EndsRepeatingAttack`): the held
/// directions and jump, the run lock, stop and posture keys, the emotes.
/// Walk mode only changes a later command's speed.
#[must_use]
pub fn ends_repeating_attack(action: InputAction) -> bool {
    use InputAction as A;
    matches!(
        action,
        A::MovementForward
            | A::MovementBackup
            | A::MovementStrafeLeft
            | A::MovementStrafeRight
            | A::MovementTurnLeft
            | A::MovementTurnRight
            | A::MovementJump
            | A::MovementRunLock
            | A::MovementStop
            | A::Ready
            | A::Sitting
            | A::Crouch
            | A::Sleeping
    ) || crate::emotes::motion(action).is_some()
}

/// The device state the dispatcher reads, for one call.
#[derive(Debug, Clone, Copy, Default)]
pub struct DeviceState<'a> {
    /// Keyboard keys held.
    pub keys: Option<&'a HashSet<Key>>,
    /// Mouse buttons held, one bit per button number.
    pub buttons: u32,
    /// The modifiers held.
    pub modifiers: Modifiers,
    /// The UI has the pointer.
    pub want_mouse: bool,
    /// A UI field has the keyboard.
    pub want_keyboard: bool,
}

impl InputDevices for DeviceState<'_> {
    fn is_key_held(&self, key: Key) -> bool {
        self.keys.is_some_and(|k| k.contains(&key))
    }
    fn is_button_held(&self, button: u8) -> bool {
        u32::from(button) < 32 && self.buttons & (1 << button) != 0
    }
    fn modifiers(&self) -> Modifiers {
        self.modifiers
    }
    fn want_capture_mouse(&self) -> bool {
        self.want_mouse
    }
    fn want_capture_keyboard(&self) -> bool {
        self.want_keyboard
    }
}

/// The window's bindings: the active retail `.keymap` profile over the
/// player's bindings file (each optional; the retail key map without
/// them). What could not be read is reported in `notices`.
#[must_use]
pub fn load(
    bindings_file: Option<&std::path::Path>,
    keymap_directory: Option<&std::path::Path>,
    notices: &mut Vec<String>,
) -> GameplayInput {
    let bindings = match (bindings_file, keymap_directory) {
        (Some(file), Some(dir)) => {
            let (bindings, mut more) = ac_ui::input::keymap_file::load_active_or_json(file, dir);
            notices.append(&mut more);
            bindings
        }
        (Some(file), None) => {
            let (bindings, error) = KeyBindings::load_or_default(file);
            if let Some(error) = error {
                notices.push(format!("{error}; using the retail key map"));
            }
            bindings
        }
        _ => KeyBindings::retail_defaults(),
    };
    GameplayInput::new(bindings)
}

/// The gameplay input of one window.
#[derive(Debug)]
pub struct GameplayInput {
    dispatcher: InputDispatcher,
    held: HashSet<Key>,
    router: Router,
}

/// What routes the dispatcher's actions (everything but the dispatcher and
/// the held keys it reads).
#[derive(Debug)]
struct Router {
    mouse_look: MouseLookState,
    rmb_orbit_held: bool,
    /// Modern mouse turning (an OpenAC option).
    modern: ModernMouse,
    fly_sensitivity: f32,
    orbit_sensitivity: f32,
    movement_keys: MovementKeys,
    /// The actions fired since the window last took them (for the
    /// plugins that listen), without a held action's per-tick repeats.
    fired: Vec<(InputAction, ActivationType)>,
}

/// One routing call's surroundings.
struct Frame<'c, 'o> {
    cameras: Option<&'c mut CameraController>,
    now: f32,
    want_mouse: bool,
    out: &'o mut Vec<GameplayOutput>,
}

impl GameplayInput {
    /// Gameplay input over `bindings`.
    #[must_use]
    pub fn new(bindings: KeyBindings) -> Self {
        Self {
            dispatcher: InputDispatcher::new(bindings),
            held: HashSet::new(),
            router: Router {
                mouse_look: MouseLookState::new(),
                rmb_orbit_held: false,
                modern: ModernMouse::default(),
                fly_sensitivity: 1.0,
                orbit_sensitivity: 1.0,
                movement_keys: MovementKeys::default(),
                fired: Vec::new(),
            },
        }
    }

    /// The actions the dispatcher fired since the last call, oldest first
    /// (a held action's per-tick repeats left out): what plugins listening
    /// for input actions hear.
    pub fn take_fired(&mut self) -> Vec<(InputAction, ActivationType)> {
        std::mem::take(&mut self.router.fired)
    }

    /// Mouse look is on.
    #[must_use]
    pub fn mouse_look_active(&self) -> bool {
        self.router.mouse_look.active()
    }

    /// The right-button orbit is held.
    #[must_use]
    pub fn rmb_orbit_held(&self) -> bool {
        self.router.rmb_orbit_held
    }

    /// A modern mouse turning drag is on.
    #[must_use]
    pub fn modern_drag_active(&self) -> bool {
        self.router.modern.dragging()
    }

    /// The window holds the cursor for the gameplay input (mouse look or a
    /// modern mouse turning drag): the interface gets no pointer input and
    /// raw motion comes here.
    #[must_use]
    pub fn holds_cursor(&self) -> bool {
        self.router.mouse_look.active() || self.router.modern.dragging()
    }

    /// The dispatcher (bindings, scopes).
    #[must_use]
    pub fn dispatcher(&self) -> &InputDispatcher {
        &self.dispatcher
    }

    /// New bindings, live at once (the key configuration screen).
    pub fn set_bindings(&mut self, bindings: KeyBindings) {
        self.dispatcher.set_bindings(bindings);
    }

    /// Report the next key or button as a capture instead of firing it.
    pub fn begin_capture(&mut self) {
        self.dispatcher.begin_capture();
    }

    /// A capture is open: keys go to it, not to the interface.
    #[must_use]
    pub fn is_capturing(&self) -> bool {
        self.dispatcher.is_capturing()
    }

    /// Follow the player's combat mode: its scope's bindings (the spell
    /// bar's keys in magic) take the keys while it lasts (OpenAC
    /// `GameplayInputActionRouter.SetCombatScope`).
    pub fn set_combat_mode(&mut self, mode: ac_core::combat::CombatMode) {
        use ac_core::combat::CombatMode;
        use ac_ui::input::InputScope;
        self.dispatcher.set_combat_scope(match mode {
            CombatMode::MELEE => Some(InputScope::MeleeCombat),
            CombatMode::MISSILE => Some(InputScope::MissileCombat),
            CombatMode::MAGIC => Some(InputScope::MagicCombat),
            _ => None,
        });
    }

    /// A key went down or up (presses only when the UI let them through,
    /// releases always).
    pub fn key(
        &mut self,
        key: Key,
        pressed: bool,
        state: &DeviceState<'_>,
        cameras: Option<&mut CameraController>,
        now: f32,
        out: &mut Vec<GameplayOutput>,
    ) {
        if pressed {
            self.held.insert(key);
        } else {
            self.held.remove(&key);
        }
        let devices = DeviceState {
            keys: Some(&self.held),
            ..*state
        };
        let mut frame = Frame {
            cameras,
            now,
            want_mouse: state.want_mouse,
            out,
        };
        let router = &mut self.router;
        let mut sink = |d: &mut InputDispatcher, e: DispatchEvent| router.route(d, e, &mut frame);
        if pressed {
            self.dispatcher
                .key_down(key, state.modifiers, &devices, &mut sink);
        } else {
            self.dispatcher.key_up(key, state.modifiers, &mut sink);
        }
    }

    /// Automation presses `action` (`hold`: `None`), or holds or lets go of
    /// it (`Some(held)`), as a key would; false when the dispatcher refuses
    /// it (a capture or a text field has the keyboard).
    pub fn automation_action(
        &mut self,
        action: InputAction,
        hold: Option<bool>,
        state: &DeviceState<'_>,
        cameras: Option<&mut CameraController>,
        now: f32,
        out: &mut Vec<GameplayOutput>,
    ) -> bool {
        let devices = DeviceState {
            keys: Some(&self.held),
            ..*state
        };
        let mut frame = Frame {
            cameras,
            now,
            want_mouse: state.want_mouse,
            out,
        };
        let router = &mut self.router;
        let mut sink = |d: &mut InputDispatcher, e: DispatchEvent| router.route(d, e, &mut frame);
        match hold {
            None => self
                .dispatcher
                .try_invoke_automation_action(action, &devices, &mut sink),
            Some(held) => self
                .dispatcher
                .try_set_automation_action_held(action, held, &devices, &mut sink),
        }
    }

    /// A mouse button (by number) went down or up off the UI, at `now_ms`
    /// on a millisecond clock.
    #[allow(clippy::too_many_arguments)]
    pub fn button(
        &mut self,
        button: u8,
        pressed: bool,
        now_ms: u64,
        state: &DeviceState<'_>,
        cameras: Option<&mut CameraController>,
        now: f32,
        out: &mut Vec<GameplayOutput>,
    ) {
        let devices = DeviceState {
            keys: Some(&self.held),
            ..*state
        };
        if pressed {
            self.router.modern.button_pressed();
        }
        let mut frame = Frame {
            cameras,
            now,
            want_mouse: state.want_mouse,
            out,
        };
        let router = &mut self.router;
        let mut sink = |d: &mut InputDispatcher, e: DispatchEvent| router.route(d, e, &mut frame);
        if pressed {
            self.dispatcher
                .mouse_down(button, state.modifiers, now_ms, &devices, &mut sink);
        } else {
            self.dispatcher
                .mouse_up(button, state.modifiers, &devices, &mut sink);
        }
    }

    /// A wheel step off the UI.
    pub fn wheel(
        &mut self,
        delta: f32,
        state: &DeviceState<'_>,
        cameras: Option<&mut CameraController>,
        now: f32,
        out: &mut Vec<GameplayOutput>,
    ) {
        let devices = DeviceState {
            keys: Some(&self.held),
            ..*state
        };
        let mut frame = Frame {
            cameras,
            now,
            want_mouse: state.want_mouse,
            out,
        };
        let router = &mut self.router;
        let mut sink = |d: &mut InputDispatcher, e: DispatchEvent| router.route(d, e, &mut frame);
        self.dispatcher.scroll(delta, &devices, &mut sink);
    }

    /// The pointer moved by (`dx`, `dy`) pixels over the world: a click
    /// candidate's travel; with the right-button orbit held the chase
    /// camera swings; dragging with the left button turns the orbit
    /// camera.
    pub fn pointer_moved(
        &mut self,
        dx: f32,
        dy: f32,
        state: &DeviceState<'_>,
        cameras: Option<&mut CameraController>,
        now: f32,
    ) {
        let devices = DeviceState {
            keys: Some(&self.held),
            ..*state
        };
        self.dispatcher.mouse_move(dx, dy, &devices);
        // A modern drag turns by the raw motion, wherever the pointer is.
        if state.want_mouse || self.router.modern.dragging() {
            return;
        }
        let Some(cameras) = cameras else {
            return;
        };
        let r = &self.router;
        match cameras.mode() {
            CameraMode::Chase if !r.mouse_look.active() && r.rmb_orbit_held => {
                cameras.chase.orbit_drag(dx, dy, now);
            }
            CameraMode::Orbit if state.is_button_held(mouse::LEFT) => {
                cameras.orbit.drag(dx, dy, r.orbit_sensitivity);
            }
            _ => {}
        }
    }

    /// Raw mouse motion (the cursor held): a modern drag's turn, mouse
    /// look's samples, or the fly camera's look.
    pub fn raw_motion(&mut self, dx: f32, dy: f32, cameras: Option<&mut CameraController>) {
        let r = &mut self.router;
        if r.modern.dragging() {
            r.modern.motion(dx, dy, cameras);
            return;
        }
        if r.mouse_look.active() {
            r.mouse_look.queue_delta(dx, dy);
            return;
        }
        if let Some(cameras) = cameras
            && cameras.mode() == CameraMode::Fly
        {
            cameras
                .fly
                .look(dx * r.fly_sensitivity, dy * r.fly_sensitivity);
        }
    }

    /// One frame of `dt` seconds at `now`: hold ticks, mouse look's sample,
    /// the held camera keys and the held movement (sent when it changed).
    /// Returns the fly camera's keys (none unless it flies).
    pub fn tick(
        &mut self,
        dt: f32,
        now: f32,
        state: &DeviceState<'_>,
        mut cameras: Option<&mut CameraController>,
        out: &mut Vec<GameplayOutput>,
    ) -> FlyInput {
        let devices = DeviceState {
            keys: Some(&self.held),
            ..*state
        };
        {
            let mut frame = Frame {
                cameras: cameras.as_deref_mut(),
                now,
                want_mouse: state.want_mouse,
                out: &mut *out,
            };
            let router = &mut self.router;
            let mut sink =
                |d: &mut InputDispatcher, e: DispatchEvent| router.route(d, e, &mut frame);
            self.dispatcher.tick(&devices, &mut sink);
            let mut frame = Frame {
                cameras: cameras.as_deref_mut(),
                now,
                want_mouse: state.want_mouse,
                out: &mut *out,
            };
            self.router.tick_mouse_look(&mut frame);
        }

        let mode = cameras.as_deref().map(CameraController::mode);
        let held = |a| self.dispatcher.is_action_held(a, &devices);
        let fly = if mode == Some(CameraMode::Fly) {
            FlyInput {
                forward: held(InputAction::MovementForward),
                left: held(InputAction::MovementTurnLeft),
                backward: held(InputAction::MovementBackup),
                right: held(InputAction::MovementTurnRight),
                up: held(InputAction::MovementJump),
                down: held(InputAction::AcdreamFlyDown),
                boost: held(InputAction::MovementRunLock),
            }
        } else {
            FlyInput::default()
        };
        // A key held when a text field took the keyboard is still held
        // (the dispatcher ends it on its release), one pressed into the
        // field is not.
        if let Some(cameras) = cameras.as_deref_mut()
            && mode == Some(CameraMode::Chase)
        {
            use InputAction as A;
            let any = |actions: &[InputAction]| actions.iter().any(|a| held(*a));
            // Keypad 4 swings the camera to the player's left: a negative
            // yaw offset in this frame.
            let keys = ChaseAdjustment {
                zoom_in: any(&[
                    A::CameraZoomIn,
                    A::CameraMoveToward,
                    A::CameraAlternateMoveToward,
                ]),
                zoom_out: any(&[
                    A::CameraZoomOut,
                    A::CameraMoveAway,
                    A::CameraAlternateMoveAway,
                ]),
                raise: any(&[
                    A::CameraRaise,
                    A::CameraRotateUp,
                    A::CameraAlternateRotateUp,
                ]),
                lower: any(&[
                    A::CameraLower,
                    A::CameraRotateDown,
                    A::CameraAlternateRotateDown,
                ]),
                rotate_left: any(&[A::CameraRotateLeft, A::CameraAlternateRotateLeft]),
                rotate_right: any(&[A::CameraRotateRight, A::CameraAlternateRotateRight]),
            };
            if keys != ChaseAdjustment::default() {
                cameras.chase.adjust_held(keys, dt);
            }
        }
        // In the head view a rotate (keys or mouse orbit) turns the
        // character, one turn a frame.
        if let Some(step) = cameras
            .as_deref_mut()
            .and_then(|c| c.chase.take_first_person_turn())
        {
            out.push(GameplayOutput::Movement(MovementCommand::FirstPersonTurn(
                step,
            )));
        }

        let mut keys = if mode == Some(CameraMode::Fly) {
            MovementKeys::default()
        } else {
            MovementKeys {
                forward: held(InputAction::MovementForward),
                backward: held(InputAction::MovementBackup),
                strafe_left: held(InputAction::MovementStrafeLeft),
                strafe_right: held(InputAction::MovementStrafeRight),
                turn_left: held(InputAction::MovementTurnLeft),
                turn_right: held(InputAction::MovementTurnRight),
                walk: held(InputAction::MovementWalkMode),
                jump: held(InputAction::MovementJump),
            }
        };
        // Modern mouse turning: the view heading for the session, and both
        // buttons held running forward (the back-up key cancels that).
        let mouse_forward = self.router.modern.tick(
            dt,
            &devices,
            cameras,
            keys.backward,
            keys.turn_left || keys.turn_right,
            out,
        );
        keys.forward |= mouse_forward;
        if keys != self.router.movement_keys {
            self.router.movement_keys = keys;
            out.push(GameplayOutput::Movement(MovementCommand::Keys(keys)));
        }
        fly
    }

    /// The window lost the focus (or the session went): every held key is
    /// released, mouse look and the orbit end.
    pub fn focus_lost(&mut self, out: &mut Vec<GameplayOutput>) {
        self.held.clear();
        self.dispatcher.deactivate();
        self.dispatcher.activate();
        self.router.end_mouse_look(out);
        // The held view is let go at the next tick (no camera here).
        self.router.modern.end(None, out);
        self.router.rmb_orbit_held = false;
        if self.router.movement_keys != MovementKeys::default() {
            self.router.movement_keys = MovementKeys::default();
            out.push(GameplayOutput::Movement(MovementCommand::Keys(
                MovementKeys::default(),
            )));
        }
    }
}

/// Modern mouse turning is on in this camera's settings.
fn modern_mouse_enabled(cameras: Option<&CameraController>) -> bool {
    cameras.is_some_and(|c| c.chase.tuning.modern_mouse_turning)
}

impl Router {
    fn tick_mouse_look(&mut self, frame: &mut Frame<'_, '_>) {
        if frame.want_mouse && self.mouse_look.active() {
            self.end_mouse_look(frame.out);
        }
        self.mouse_look
            .on_want_capture_mouse_changed(frame.want_mouse);
        let Some((raw_x, raw_y)) = self.mouse_look.try_take_raw_sample(frame.now) else {
            return;
        };
        if raw_x == 0.0 && raw_y == 0.0 {
            frame
                .out
                .push(GameplayOutput::Movement(MovementCommand::StopMouseDrift));
        }
        let tuning = frame
            .cameras
            .as_deref()
            .map_or_else(ChaseTuning::default, |c| c.chase.tuning);
        let invert = if tuning.invert_mouse_look_y_axis {
            -1.0
        } else {
            1.0
        };
        let sensitivity = tuning.mouse_look_sensitivity;
        let (fx, fy) = match frame.cameras.as_deref_mut() {
            Some(cameras) => cameras
                .chase
                .filter_mouse_delta(raw_x, raw_y, 0.5, frame.now),
            None => (raw_x, raw_y),
        };
        if let Some(adjustment) = self.mouse_look.apply_delta(fx * invert, sensitivity) {
            frame
                .out
                .push(GameplayOutput::Movement(MovementCommand::MouseTurn(
                    adjustment,
                )));
        }
        if let Some(cameras) = frame.cameras.as_deref_mut() {
            cameras
                .chase
                .adjust_pitch(fy * invert * MOUSE_ORBIT_SCALE * sensitivity);
        }
    }

    fn begin_mouse_look(&mut self, frame: &mut Frame<'_, '_>) {
        if frame.cameras.as_deref().map(CameraController::mode) != Some(CameraMode::Chase) {
            return;
        }
        // A modern drag keeps the mouse; once it is let go, mouse look
        // takes over from its settling.
        if self.modern.dragging() {
            return;
        }
        if self.modern.active() {
            self.modern.end(frame.cameras.as_deref_mut(), frame.out);
        }
        self.mouse_look.press(0.0, 0.0, frame.want_mouse, frame.now);
        if !self.mouse_look.active() {
            return;
        }
        frame
            .out
            .push(GameplayOutput::Movement(MovementCommand::BeginMouseLook));
        frame.out.push(GameplayOutput::GrabCursor);
    }

    fn end_mouse_look(&mut self, out: &mut Vec<GameplayOutput>) {
        let was_active = self.mouse_look.active();
        self.mouse_look.release();
        if was_active {
            out.push(GameplayOutput::Movement(MovementCommand::EndMouseLook));
            out.push(GameplayOutput::ReleaseCursor);
        }
    }

    /// OpenAC `GameplayInputActionRouter.Route`, for the owners this window
    /// has.
    fn route(&mut self, d: &mut InputDispatcher, event: DispatchEvent, frame: &mut Frame<'_, '_>) {
        use InputAction as A;
        let (action, activation) = match event {
            DispatchEvent::Fired(action, activation) => (action, activation),
            DispatchEvent::Captured(chord) => {
                frame.out.push(GameplayOutput::Captured(chord));
                return;
            }
        };
        if activation != ActivationType::Hold {
            self.fired.push((action, activation));
        }
        let mode = frame.cameras.as_deref().map(CameraController::mode);
        if action == A::CameraActivateAlternateMode {
            match activation {
                ActivationType::Press => d.set_camera_alternate_scope(true),
                ActivationType::Release => d.set_camera_alternate_scope(false),
                _ => {}
            }
        }

        // Pointer actions: the right-button orbit, mouse look (the middle
        // button or the alternate-mode key held).
        if action == A::AcdreamRmbOrbitHold
            && (self.modern.active() || modern_mouse_enabled(frame.cameras.as_deref()))
        {
            // Modern mouse turning takes the right button instead of the
            // orbit.
            self.rmb_orbit_held = false;
            match activation {
                ActivationType::Press => {
                    let mouse_look = self.mouse_look.active();
                    self.modern.press(
                        frame.cameras.as_deref_mut(),
                        frame.want_mouse,
                        mouse_look,
                        frame.out,
                    );
                }
                ActivationType::Release => self.modern.release(frame.cameras.as_deref(), frame.out),
                _ => {}
            }
            return;
        }
        if action == A::AcdreamRmbOrbitHold {
            match activation {
                ActivationType::Press => self.rmb_orbit_held = mode == Some(CameraMode::Chase),
                ActivationType::Release => self.rmb_orbit_held = false,
                _ => {}
            }
            return;
        }
        if matches!(
            action,
            A::CameraInstantMouseLook | A::CameraActivateAlternateMode
        ) {
            match activation {
                ActivationType::Press => self.begin_mouse_look(frame),
                ActivationType::Release => self.end_mouse_look(frame.out),
                _ => {}
            }
            return;
        }

        // Camera views (presses, in the chase camera) and the wheel (the
        // chase boom, or the orbit distance).
        let scroll = match action {
            A::ScrollUp => Some(1.0),
            A::ScrollDown => Some(-1.0),
            _ => None,
        };
        if activation == ActivationType::Press
            && let Some(cameras) = frame.cameras.as_deref_mut()
        {
            match (cameras.mode(), action) {
                (CameraMode::Chase, A::CameraViewDefault | A::CameraAlternateViewDefault) => {
                    cameras.chase.set_default_view();
                    return;
                }
                (
                    CameraMode::Chase,
                    A::CameraViewFirstPerson | A::CameraAlternateViewFirstPerson,
                ) => {
                    cameras.chase.set_first_person_view();
                    return;
                }
                (CameraMode::Chase, A::CameraViewLookDown | A::CameraAlternateViewLookDown) => {
                    cameras.chase.toggle_look_down_view();
                    return;
                }
                (CameraMode::Chase, A::CameraViewMapMode | A::CameraAlternateViewMapMode) => {
                    cameras.chase.toggle_map_mode_view();
                    return;
                }
                (CameraMode::Chase, _) if scroll.is_some() => {
                    let direction = scroll.unwrap_or_default();
                    cameras.chase.adjust_distance(-direction * WHEEL_ZOOM_STEP);
                }
                (CameraMode::Orbit, _) if scroll.is_some() => {
                    let direction = scroll.unwrap_or_default();
                    cameras.orbit.distance =
                        (cameras.orbit.distance - direction * 20.0).clamp(50.0, 2000.0);
                }
                _ => {}
            }
        }
        if scroll.is_some() {
            return;
        }

        // The combat keys (OpenAC `CombatAttackInputFrameAdapter`): a press
        // that becomes a motion ends a repeating attack and goes on; the
        // attack and power keys go to the session on press and release.
        if activation == ActivationType::Press && ends_repeating_attack(action) {
            frame
                .out
                .push(GameplayOutput::Gameplay(GameplayCommand::AttackInput(
                    CombatAttackCommand::AbortForMovement,
                    InputActivation::Press,
                )));
        }
        if let Some(command) = combat_attack_command(action) {
            let activation = match activation {
                ActivationType::Hold => return,
                ActivationType::Release => InputActivation::Release,
                _ => InputActivation::Press,
            };
            frame
                .out
                .push(GameplayOutput::Gameplay(GameplayCommand::AttackInput(
                    command, activation,
                )));
            return;
        }

        if !matches!(
            activation,
            ActivationType::Press | ActivationType::DoubleClick | ActivationType::Click
        ) {
            return;
        }

        // From here a press, a double click and a click are routed alike
        // (OpenAC `GameplayInputActionRouter.Route`).
        //
        // The chat entry keys carry the key that pressed them, whatever
        // the activation (OpenAC `RetailUiRuntime.HandleInputAction` hands
        // the dispatcher's physical chord to `EnterChatMode` and
        // `ToggleChatEntry`): a keyboard click fires as the key goes down,
        // so the key is still held and must type nothing into the entry it
        // focused. A mouse button is no key to wait out.
        if matches!(action, A::EnterChatMode | A::ToggleChatEntry) {
            let key = d
                .current_physical_chord()
                .and_then(|chord| match chord.key {
                    ac_ui::input::InputKey::Key(key) if chord.device == 0 => Some(key),
                    _ => None,
                });
            frame.out.push(GameplayOutput::ChatEntry(action, key));
            return;
        }

        // The interface's actions (OpenAC `RetailUiRuntime.HandleInputAction`).
        if crate::ui::status::is_interface_action(action) {
            frame.out.push(GameplayOutput::Interface(action));
            return;
        }

        // A character-option key flips its option (OpenAC
        // `HandleCharacterOptionAction`).
        if let Some(option) = ac_ui::input::identity::character_option_id(action) {
            frame
                .out
                .push(GameplayOutput::ToggleCharacterOption(option));
            return;
        }

        // Selection, the combat toggle and log out go to the session (OpenAC:
        // selection before movement, the commands last; none overlap).
        if let Some(output) = session_output(action) {
            frame.out.push(output);
            return;
        }

        // Emotes, then the movement presses (OpenAC
        // `HandlePressedMovementAction`).
        if let Some(motion) = crate::emotes::motion(action) {
            frame
                .out
                .push(GameplayOutput::Movement(MovementCommand::Motion(motion)));
            return;
        }
        let movement = match action {
            A::MovementRunLock => Some(MovementCommand::ToggleRunLock),
            A::MovementStop => Some(MovementCommand::Stop),
            A::Ready => Some(MovementCommand::Posture(Posture::Ready)),
            A::Sitting => Some(MovementCommand::Posture(Posture::Sit)),
            A::Crouch => Some(MovementCommand::Posture(Posture::Crouch)),
            A::Sleeping => Some(MovementCommand::Posture(Posture::Sleep)),
            _ => None,
        };
        if let Some(command) = movement {
            frame.out.push(GameplayOutput::Movement(command));
            return;
        }

        // The client's own commands.
        match action {
            A::AcdreamCycleTimeOfDay => frame.out.push(GameplayOutput::CycleTimeOfDay),
            A::AcdreamCycleWeather => frame.out.push(GameplayOutput::CycleWeather),
            A::AcdreamToggleAudioMute => frame.out.push(GameplayOutput::ToggleMute),
            A::AcdreamToggleFlyMode => frame.out.push(GameplayOutput::ToggleFly),
            A::AcdreamTogglePlayerMode => frame.out.push(GameplayOutput::TogglePlayerMode),
            A::EscapeKey => frame.out.push(GameplayOutput::Escape),
            A::CaptureScreenshot => frame.out.push(GameplayOutput::CaptureScreenshot),
            A::AcdreamDumpNearby => frame.out.push(GameplayOutput::DumpNearby),
            // A modern drag's clicks pick nothing (the cursor was hidden).
            A::SelectLeft | A::SelectRight | A::SelectDblLeft if self.modern.swallows_clicks() => {}
            A::SelectLeft => frame
                .out
                .push(GameplayOutput::WorldClick(WorldClick::Select)),
            A::SelectRight => frame
                .out
                .push(GameplayOutput::WorldClick(WorldClick::Examine)),
            A::SelectDblLeft => frame.out.push(GameplayOutput::WorldClick(WorldClick::Use)),
            A::AcdreamSensitivityDown => self.adjust_sensitivity(1.0 / 1.2, frame),
            A::AcdreamSensitivityUp => self.adjust_sensitivity(1.2, frame),
            _ => {}
        }
    }

    /// Scale the active camera's mouse sensitivity (kept within 0.005 to
    /// 3; the chase camera's is the player's mouse-look setting, for this
    /// window only).
    fn adjust_sensitivity(&mut self, factor: f32, frame: &mut Frame<'_, '_>) {
        let (name, value) = match frame.cameras.as_deref_mut() {
            Some(c) if c.mode() == CameraMode::Chase => {
                ("Chase", &mut c.chase.tuning.mouse_look_sensitivity)
            }
            Some(c) if c.mode() == CameraMode::Fly => ("Fly", &mut self.fly_sensitivity),
            _ => ("Orbit", &mut self.orbit_sensitivity),
        };
        *value = (*value * factor).clamp(0.005, 3.0);
        frame.out.push(GameplayOutput::Notice(format!(
            "{name} sens {:.3}x",
            *value
        )));
    }
}

#[cfg(test)]
#[path = "gameplay_input_tests.rs"]
mod tests;

#[cfg(test)]
#[path = "modern_mouse_tests.rs"]
mod modern_mouse_tests;
