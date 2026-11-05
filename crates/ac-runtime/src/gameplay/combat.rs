//! Combat's state machines: the combat-mode request (sent at once, or
//! parked until the body is ready), the attack request and its power bar,
//! the target kept after a death or a deselect, which creatures are known
//! dead, and the selected creature's health stream.
//!
//! Each machine reads the rest of the client through a small trait, so the
//! session wires them to the world and the outbox and the tests to fakes.

use std::collections::HashMap;

use ac_core::combat::{
    ATTACK_POWER_UP_SECONDS, AttackHeight, CarriedItem, CombatMode, DUAL_WIELD_POWER_UP_SECONDS,
    default_combat_mode_decision, supports_targeted_attack, toggle_mode,
};
use ac_core::items::{ClientObject, CombatUse};
use ac_proto::MovementData;

use super::selection::{SelectionReason, SelectionTransition};

// -- Combat mode --------------------------------------------------------------

/// What happened to a combat-mode request.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CombatModeRequestStatus {
    /// Not in the world: nothing happened.
    Inactive,
    /// Refused (see the notice).
    Rejected,
    /// Sent; the mode changed.
    Sent,
    /// Parked until the body is ready; the mode has not changed yet.
    Deferred,
}

/// A combat-mode request's outcome.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CombatModeRequestResult {
    /// What happened.
    pub status: CombatModeRequestStatus,
    /// The mode now (or the one sent).
    pub mode: CombatMode,
    /// What the player is told, if anything.
    pub notice: Option<String>,
}

impl CombatModeRequestResult {
    fn new(status: CombatModeRequestStatus, mode: CombatMode) -> Self {
        Self {
            status,
            mode,
            notice: None,
        }
    }
}

/// What a mode change asks the body and the session before it goes out.
pub trait CombatModeReadiness {
    /// The body is in position for the mode it is in (asked about the mode
    /// being left, never the one requested).
    fn is_in_ready_position(&mut self, mode: CombatMode) -> bool;
    /// A teleport is under way: no mode change is accepted.
    fn is_teleport_in_progress(&self) -> bool;
}

/// What a mode change asks of the rest of the client.
pub trait CombatModeOperations {
    /// In the world.
    fn is_in_world(&self) -> bool;
    /// What the player wields, in inventory order.
    fn ordered_equipment(&mut self) -> Vec<ClientObject>;
    /// The player asked for a stance: a pending wield settlement stands down.
    fn notify_explicit_combat_mode_request(&mut self);
    /// Send the change.
    fn send_change_combat_mode(&mut self, mode: CombatMode);
}

/// The combat-mode request, with the change parked while the body is busy.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CombatModeState {
    pending: Option<CombatMode>,
}

impl CombatModeState {
    /// The change parked until the body is ready.
    #[must_use]
    pub fn pending_mode(&self) -> Option<CombatMode> {
        self.pending
    }

    /// Forget a parked change (the session ended).
    pub fn reset(&mut self) {
        self.pending = None;
    }

    /// Once a pass: send a parked change the moment the body is ready.
    /// Returns the mode to set locally when one went out.
    pub fn apply_pending_mode(
        &mut self,
        current: CombatMode,
        ops: &mut impl CombatModeOperations,
        readiness: Option<&mut dyn CombatModeReadiness>,
    ) -> Option<CombatMode> {
        let mode = self.pending?;
        if !ops.is_in_world() {
            self.pending = None;
            return None;
        }
        if let Some(r) = readiness
            && !r.is_in_ready_position(current)
        {
            return None;
        }
        self.pending = None;
        if current == mode {
            return None;
        }
        ops.send_change_combat_mode(mode);
        Some(mode)
    }

    /// The combat toggle: to peace from any combat mode, else to the
    /// default mode for what the player wields.
    pub fn toggle(
        &mut self,
        current: CombatMode,
        ops: &mut impl CombatModeOperations,
        readiness: Option<&mut dyn CombatModeReadiness>,
    ) -> CombatModeRequestResult {
        if !ops.is_in_world() {
            return CombatModeRequestResult::new(CombatModeRequestStatus::Inactive, current);
        }
        // Every explicit request supersedes a wield settlement, including
        // one the default-mode rule then refuses.
        ops.notify_explicit_combat_mode_request();
        let next = if current == CombatMode::NON_COMBAT {
            let equipment = ops.ordered_equipment();
            let carried: Vec<CarriedItem> = equipment.iter().map(carried_item).collect();
            let decision = default_combat_mode_decision(&carried);
            if let Some(held) = decision.incompatible_held_item {
                return CombatModeRequestResult {
                    status: CombatModeRequestStatus::Rejected,
                    mode: current,
                    notice: Some(format!(
                        "You can't enter combat mode while wielding the {}",
                        equipment[held].appropriate_name()
                    )),
                };
            }
            toggle_mode(current, decision.mode)
        } else {
            CombatMode::NON_COMBAT
        };
        if let Some(refused) = refuse_during_teleport(current, readiness.as_deref()) {
            return refused;
        }
        self.send_or_park(next, current, ops, readiness)
    }

    /// A request for one mode.
    pub fn request(
        &mut self,
        mode: CombatMode,
        current: CombatMode,
        ops: &mut impl CombatModeOperations,
        readiness: Option<&mut dyn CombatModeReadiness>,
    ) -> CombatModeRequestResult {
        if !ops.is_in_world() {
            return CombatModeRequestResult::new(CombatModeRequestStatus::Inactive, current);
        }
        if !matches!(
            mode,
            CombatMode::NON_COMBAT | CombatMode::MELEE | CombatMode::MISSILE | CombatMode::MAGIC
        ) {
            return CombatModeRequestResult {
                status: CombatModeRequestStatus::Rejected,
                mode: current,
                notice: Some("Invalid combat mode.".to_owned()),
            };
        }
        if current == mode {
            return CombatModeRequestResult::new(CombatModeRequestStatus::Sent, mode);
        }
        ops.notify_explicit_combat_mode_request();
        if let Some(refused) = refuse_during_teleport(current, readiness.as_deref()) {
            return refused;
        }
        self.send_or_park(mode, current, ops, readiness)
    }

    fn send_or_park(
        &mut self,
        mode: CombatMode,
        current: CombatMode,
        ops: &mut impl CombatModeOperations,
        readiness: Option<&mut dyn CombatModeReadiness>,
    ) -> CombatModeRequestResult {
        if let Some(r) = readiness
            && !r.is_in_ready_position(current)
        {
            self.pending = Some(mode);
            return CombatModeRequestResult::new(CombatModeRequestStatus::Deferred, current);
        }
        self.pending = None;
        ops.send_change_combat_mode(mode);
        CombatModeRequestResult::new(CombatModeRequestStatus::Sent, mode)
    }
}

fn refuse_during_teleport(
    current: CombatMode,
    readiness: Option<&dyn CombatModeReadiness>,
) -> Option<CombatModeRequestResult> {
    readiness
        .filter(|r| r.is_teleport_in_progress())
        .map(|_| CombatModeRequestResult {
            status: CombatModeRequestStatus::Rejected,
            mode: current,
            notice: Some("You can't change combat mode while teleporting.".to_owned()),
        })
}

/// What the default-mode rule reads from a wielded item.
#[must_use]
pub fn carried_item(item: &ClientObject) -> CarriedItem {
    CarriedItem {
        equipped_location: item.currently_equipped_location,
        combat_use: CombatUse(u32::from(item.combat_use.unwrap_or(0))),
        item_type: item.item_type,
    }
}

// -- Attack -------------------------------------------------------------------

/// The bar setting a new session starts at.
pub const INITIAL_DESIRED_POWER: f32 = 0.5;
/// One step of the bar setting.
pub const DESIRED_POWER_STEP: f32 = 1.0 / 6.0;

/// An attack input.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CombatAttackCommand {
    /// Low attack key.
    LowAttack,
    /// Medium attack key.
    MediumAttack,
    /// High attack key.
    HighAttack,
    /// Power bar setting down a step.
    DecreasePower,
    /// Power bar setting up a step.
    IncreasePower,
    /// The player moved: an automatic attack stops.
    AbortForMovement,
}

/// Key down or key up.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InputActivation {
    /// Pressed.
    Press,
    /// Released.
    Release,
}

/// What the attack asks of the rest of the client.
pub trait CombatAttackOperations {
    /// An attack can start now; with `allow_auto_target` the nearest
    /// monster may stand in for an empty selection.
    fn can_start_attack(&mut self, allow_auto_target: bool) -> bool;
    /// Stop the player before the request (movement).
    fn prepare_attack_request(&mut self);
    /// Send the attack. False when it could not go (no target).
    fn send_attack(&mut self, height: AttackHeight, power: f32, allow_auto_target: bool) -> bool;
    /// Send the cancel.
    fn send_cancel_attack(&mut self);
    /// The player holds a weapon in each hand.
    fn is_dual_wield(&self) -> bool;
    /// The body is in its attack stance.
    fn player_ready_for_attack(&self) -> bool;
    /// The auto-repeat option is on.
    fn auto_repeat_attack(&self) -> bool;
}

/// The attack request and the power bar.
#[derive(Debug, Clone, PartialEq)]
pub struct CombatAttackState {
    mode: CombatMode,
    requested_height: AttackHeight,
    desired_power: f32,
    build_in_progress: bool,
    build_start: f64,
    request_in_progress: bool,
    server_response_pending: bool,
    attack_when_response_received: bool,
    attack_when_response_received_power: f32,
    current_build_is_automatic: bool,
    repeat_attacking: bool,
    requested_power: f32,
    latest_power_bar_level: f32,
    completion_revision: u64,
    completion_sequence: u32,
    completion_weenie_error: u32,
    /// Whether the last key-up sent (or will send) a swing.
    last_release: Option<bool>,
    /// A plugin or script drives combat: it names its own targets and power.
    pub automation_controlled: bool,
}

impl Default for CombatAttackState {
    fn default() -> Self {
        Self {
            mode: CombatMode::NON_COMBAT,
            requested_height: AttackHeight::MEDIUM,
            desired_power: INITIAL_DESIRED_POWER,
            build_in_progress: false,
            build_start: 0.0,
            request_in_progress: false,
            server_response_pending: false,
            attack_when_response_received: false,
            attack_when_response_received_power: 0.0,
            current_build_is_automatic: false,
            repeat_attacking: false,
            requested_power: 0.0,
            latest_power_bar_level: 0.0,
            completion_revision: 0,
            completion_sequence: 0,
            completion_weenie_error: 0,
            last_release: None,
            automation_controlled: false,
        }
    }
}

impl CombatAttackState {
    /// The height the next swing goes at.
    #[must_use]
    pub fn requested_height(&self) -> AttackHeight {
        self.requested_height
    }

    /// The bar setting.
    #[must_use]
    pub fn desired_power(&self) -> f32 {
        self.desired_power
    }

    /// A key is held (a request is being built).
    #[must_use]
    pub fn attack_request_in_progress(&self) -> bool {
        self.request_in_progress
    }

    /// A swing went out and its answer is awaited.
    #[must_use]
    pub fn attack_server_response_pending(&self) -> bool {
        self.server_response_pending
    }

    /// Swings repeat on their own.
    #[must_use]
    pub fn repeat_attack_in_progress(&self) -> bool {
        self.repeat_attacking
    }

    /// The level the last swing was asked at.
    #[must_use]
    pub fn requested_attack_power(&self) -> f32 {
        self.requested_power
    }

    /// The bar is loading.
    #[must_use]
    pub fn build_in_progress(&self) -> bool {
        self.build_in_progress
    }

    /// Counts the server's attack-done answers.
    #[must_use]
    pub fn completion_revision(&self) -> u64 {
        self.completion_revision
    }

    /// The last answer's attack sequence.
    #[must_use]
    pub fn completion_sequence(&self) -> u32 {
        self.completion_sequence
    }

    /// The last answer's error (0 for none).
    #[must_use]
    pub fn completion_weenie_error(&self) -> u32 {
        self.completion_weenie_error
    }

    /// The bar's level at `now`.
    #[must_use]
    pub fn power_bar_level(&self, now: f64, ops: &impl CombatAttackOperations) -> f32 {
        if self.build_in_progress {
            self.level(now, ops)
        } else {
            self.latest_power_bar_level
        }
    }

    fn auto_repeat_allowed(&self, ops: &impl CombatAttackOperations) -> bool {
        !self.automation_controlled && ops.auto_repeat_attack()
    }

    /// One attack input. True when it was an attack input this state takes.
    pub fn handle_command(
        &mut self,
        command: CombatAttackCommand,
        activation: InputActivation,
        ops: &mut impl CombatAttackOperations,
        now: f64,
    ) -> bool {
        let height = match command {
            CombatAttackCommand::LowAttack => Some(AttackHeight::LOW),
            CombatAttackCommand::MediumAttack => Some(AttackHeight::MEDIUM),
            CombatAttackCommand::HighAttack => Some(AttackHeight::HIGH),
            _ => None,
        };
        if let Some(height) = height {
            match activation {
                InputActivation::Press => self.press_attack(height, ops, now),
                InputActivation::Release => {
                    self.release_attack(ops, now);
                }
            }
            return true;
        }
        if activation != InputActivation::Press {
            return false;
        }
        match command {
            CombatAttackCommand::DecreasePower => self.step_desired_power(-1),
            CombatAttackCommand::IncreasePower => self.step_desired_power(1),
            CombatAttackCommand::AbortForMovement => self.abort_automatic_attack(ops),
            _ => return false,
        }
        true
    }

    /// Set the bar setting (clamped to 0..=1).
    pub fn set_desired_power(&mut self, power: f32) {
        self.desired_power = power.clamp(0.0, 1.0);
    }

    /// An attack key went down.
    pub fn press_attack(
        &mut self,
        height: AttackHeight,
        ops: &mut impl CombatAttackOperations,
        now: f64,
    ) {
        let height_changed = self.requested_height != height;
        self.requested_height = height;
        if height_changed || !self.request_in_progress {
            self.start_attack_request(ops, now);
        }
    }

    /// The attack key came up. True when a swing is on its way (now, or
    /// when the bar reaches the setting, or when the server answers).
    pub fn release_attack(&mut self, ops: &mut impl CombatAttackOperations, now: f64) -> bool {
        let sent = self.end_attack_request(None, ops, now);
        self.last_release = Some(sent);
        sent
    }

    /// Whether the last key-up sent (or will send) a swing; `None` before
    /// any (a plugin's release reads its answer here).
    #[must_use]
    pub fn last_release(&self) -> Option<bool> {
        self.last_release
    }

    fn end_attack_request(
        &mut self,
        committed_power: Option<f32>,
        ops: &mut impl CombatAttackOperations,
        now: f64,
    ) -> bool {
        if !self.request_in_progress {
            return false;
        }
        self.request_in_progress = false;
        let current = self.level(now, ops);
        // Key-up commits the larger of the setting and the level reached;
        // an owner that names its power gets exactly that level.
        self.requested_power = committed_power.unwrap_or(if self.automation_controlled {
            self.desired_power
        } else {
            self.desired_power.max(current)
        });

        if self.server_response_pending {
            if self.automation_controlled {
                self.reset_power_bar(now, ops);
                return false;
            }
            self.attack_when_response_received = true;
            self.attack_when_response_received_power = self.requested_power;
            return true;
        }

        if self.desired_power <= current || self.repeat_attacking {
            let sent = self.execute_attack(true, ops, now);
            // A charged swing is followed at once by a request at the bar
            // setting: the server holds one requested level per swing.
            if sent && self.requested_power > self.desired_power {
                self.requested_power = self.desired_power;
                self.execute_attack(true, ops, now);
            }
            sent
        } else {
            // Released early: the bar loads on to the setting and the swing
            // commits when it gets there.
            true
        }
    }

    /// The player moved: stop repeating, cancel at the server, and end the
    /// whole attack.
    pub fn abort_automatic_attack(&mut self, ops: &mut impl CombatAttackOperations) {
        if !self.server_response_pending
            && !self.request_in_progress
            && !self.repeat_attacking
            && !self.attack_when_response_received
        {
            return;
        }
        ops.send_cancel_attack();
        self.repeat_attacking = false;
        self.request_in_progress = false;
        self.server_response_pending = false;
        self.attack_when_response_received = false;
        self.attack_when_response_received_power = 0.0;
        self.stop_build_at(0.0);
        self.latest_power_bar_level = 0.0;
        self.current_build_is_automatic = false;
    }

    /// Once a pass: start a waiting build, follow the stance, commit a
    /// released request once the bar reaches its level.
    pub fn tick(&mut self, ops: &mut impl CombatAttackOperations, now: f64) {
        if self.request_in_progress && !self.build_in_progress && !self.server_response_pending {
            self.attempt_start_building(ops, now);
        }
        if !self.build_in_progress {
            return;
        }
        if !ops.player_ready_for_attack() {
            if self.request_in_progress {
                self.stop_build(now, ops);
                self.latest_power_bar_level = 0.0;
            } else {
                self.repeat_attacking = false;
                self.reset_power_bar(now, ops);
            }
            return;
        }
        let current = self.level(now, ops);
        self.latest_power_bar_level = current;
        if !self.request_in_progress && current >= self.requested_power {
            self.latest_power_bar_level = self.requested_power.min(current);
            if self.current_build_is_automatic {
                self.stop_build(now, ops);
            } else {
                self.execute_attack(true, ops, now);
            }
        }
    }

    fn step_desired_power(&mut self, direction: i32) {
        let sixth = (self.desired_power / DESIRED_POWER_STEP).round();
        #[allow(clippy::cast_precision_loss)]
        self.set_desired_power((sixth + direction.signum() as f32) * DESIRED_POWER_STEP);
    }

    fn start_attack_request(&mut self, ops: &mut impl CombatAttackOperations, now: f64) {
        if !supports_targeted_attack(self.mode)
            || !ops.can_start_attack(!self.automation_controlled)
        {
            return;
        }
        self.request_in_progress = true;
        self.requested_power = 1.0;
        ops.prepare_attack_request();
        self.current_build_is_automatic = false;
        self.attempt_start_building(ops, now);
    }

    fn attempt_start_building(&mut self, ops: &impl CombatAttackOperations, now: f64) {
        if self.build_in_progress || self.server_response_pending || !ops.player_ready_for_attack()
        {
            return;
        }
        self.start_power_bar_build(now);
    }

    fn start_power_bar_build(&mut self, now: f64) {
        self.build_in_progress = true;
        self.build_start = now;
        self.latest_power_bar_level = 0.0;
    }

    fn level(&self, now: f64, ops: &impl CombatAttackOperations) -> f32 {
        if !self.build_in_progress {
            return 0.0;
        }
        let duration = if ops.is_dual_wield() {
            DUAL_WIELD_POWER_UP_SECONDS
        } else {
            ATTACK_POWER_UP_SECONDS
        };
        #[allow(clippy::cast_possible_truncation)]
        let level = ((now - self.build_start) / duration).clamp(0.0, 1.0) as f32;
        level
    }

    fn execute_attack(
        &mut self,
        set_server_pending: bool,
        ops: &mut impl CombatAttackOperations,
        now: f64,
    ) -> bool {
        self.stop_build(now, ops);
        if !ops.send_attack(
            self.requested_height,
            self.requested_power.clamp(0.0, 1.0),
            !self.automation_controlled,
        ) {
            self.reset_power_bar(now, ops);
            return false;
        }
        if self.auto_repeat_allowed(ops) {
            self.repeat_attacking = true;
        }
        self.server_response_pending = set_server_pending;
        true
    }

    /// The server accepted a swing.
    pub fn on_attack_commenced(&mut self) {
        self.server_response_pending = true;
        if !self.request_in_progress {
            self.latest_power_bar_level = self.requested_power;
            // The level is kept as it stands.
            self.build_in_progress = false;
            self.build_start = 0.0;
        }
    }

    /// The server finished a swing.
    pub fn on_attack_done(
        &mut self,
        sequence: u32,
        weenie_error: u32,
        ops: &mut impl CombatAttackOperations,
        now: f64,
    ) {
        self.completion_sequence = sequence;
        self.completion_weenie_error = weenie_error;
        self.completion_revision += 1;
        self.server_response_pending = false;
        if weenie_error != 0 {
            self.repeat_attacking = false;
        }
        let repeat_allowed = self.auto_repeat_allowed(ops);
        // A repeat needs a new request only when the level in flight no
        // longer matches the bar setting.
        if !self.request_in_progress
            && repeat_allowed
            && self.repeat_attacking
            && (self.requested_power - self.desired_power).abs() > 0.01
        {
            self.requested_power = self.desired_power;
            self.execute_attack(false, ops, now);
        }
        if !repeat_allowed || !self.repeat_attacking {
            self.repeat_attacking = false;
            // A request still being charged owns the bar.
            if self.request_in_progress {
                self.attempt_start_building(ops, now);
            } else {
                self.reset_power_bar(now, ops);
            }
        } else if self.request_in_progress {
            self.attempt_start_building(ops, now);
        } else {
            self.start_power_bar_build(now);
            self.current_build_is_automatic = true;
        }
        if self.attack_when_response_received {
            let power = self.attack_when_response_received_power;
            self.start_attack_request(ops, now);
            self.end_attack_request(Some(power), ops, now);
            self.attack_when_response_received = false;
            self.attack_when_response_received_power = 0.0;
        }
    }

    /// The combat mode changed: leaving a targeted mode ends the attack.
    pub fn on_combat_mode_changed(&mut self, mode: CombatMode) {
        self.mode = mode;
        if !supports_targeted_attack(mode) {
            self.reset();
        }
    }

    fn stop_build(&mut self, now: f64, ops: &impl CombatAttackOperations) {
        if self.build_in_progress {
            self.latest_power_bar_level = self.level(now, ops);
        }
        self.build_in_progress = false;
        self.build_start = 0.0;
    }

    fn stop_build_at(&mut self, level: f32) {
        self.latest_power_bar_level = level;
        self.build_in_progress = false;
        self.build_start = 0.0;
    }

    fn reset_power_bar(&mut self, now: f64, ops: &impl CombatAttackOperations) {
        self.stop_build(now, ops);
        self.latest_power_bar_level = 0.0;
        self.current_build_is_automatic = false;
    }

    fn reset(&mut self) {
        self.request_in_progress = false;
        self.server_response_pending = false;
        self.attack_when_response_received = false;
        self.attack_when_response_received_power = 0.0;
        self.repeat_attacking = false;
        self.requested_power = 0.0;
        self.completion_revision = 0;
        self.completion_sequence = 0;
        self.completion_weenie_error = 0;
        self.stop_build_at(0.0);
        self.current_build_is_automatic = false;
    }

    /// The session ended: the retail defaults again (medium height, half
    /// power), no cancel sent.
    pub fn reset_session(&mut self) {
        self.reset();
        self.mode = CombatMode::NON_COMBAT;
        self.requested_height = AttackHeight::MEDIUM;
        self.desired_power = INITIAL_DESIRED_POWER;
    }
}

// -- Target -------------------------------------------------------------------

/// What keeps the player's target after a selection empties: the nearest
/// monster when the auto-target option is on, unless the player emptied it
/// on purpose.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CombatTargetState {
    target_willingly_lost: bool,
    /// A plugin or script drives combat: it names its own targets.
    pub automation_controlled: bool,
}

impl CombatTargetState {
    /// The next emptying of the selection is one the player asked for.
    pub fn notify_target_willingly_lost(&mut self) {
        self.target_willingly_lost = true;
    }

    /// The selection changed. True when the nearest monster should be
    /// selected in its place.
    pub fn on_selection_changed(
        &mut self,
        transition: &SelectionTransition,
        auto_target: bool,
        mode: CombatMode,
    ) -> bool {
        if transition.selected.is_some() {
            return false;
        }
        if self.target_willingly_lost || transition.reason == SelectionReason::SessionReset {
            self.target_willingly_lost = false;
            return false;
        }
        !self.automation_controlled && auto_target && supports_targeted_attack(mode)
    }
}

// -- Deaths -------------------------------------------------------------------

/// The full motion command of a death.
pub const DEAD_MOTION: u32 = ac_core::combat::motion_command::DEAD;

/// Which creatures the client has been told are dead, with the incarnation
/// each had when it died.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CreatureDeathState {
    dead: HashMap<u32, u16>,
}

impl CreatureDeathState {
    /// How many creatures are known dead.
    #[must_use]
    pub fn count(&self) -> usize {
        self.dead.len()
    }

    /// The creature holding `object_id` at `incarnation` is known dead.
    #[must_use]
    pub fn is_dead_incarnation(&self, object_id: u32, incarnation: u16) -> bool {
        self.dead.get(&object_id) == Some(&incarnation)
    }

    /// `object_id` is known dead, whatever its incarnation.
    #[must_use]
    pub fn is_dead(&self, object_id: u32) -> bool {
        self.dead.contains_key(&object_id)
    }

    /// A motion the server sent. True the first time it announces a death.
    pub fn observe_motion(
        &mut self,
        object_id: u32,
        incarnation: u16,
        motion: Option<&MovementData>,
    ) -> bool {
        if object_id == 0 || !motion.is_some_and(is_death_motion) {
            return false;
        }
        if self.dead.get(&object_id) == Some(&incarnation) {
            return false;
        }
        self.dead.insert(object_id, incarnation);
        true
    }

    /// An object entered the world: its id belongs to something new; a
    /// corpse arrives already playing its death. True for a new death.
    pub fn observe_spawn(
        &mut self,
        object_id: u32,
        incarnation: u16,
        motion: Option<&MovementData>,
    ) -> bool {
        self.dead.remove(&object_id);
        self.observe_motion(object_id, incarnation, motion)
    }

    /// Forget one object.
    pub fn forget(&mut self, object_id: u32) {
        self.dead.remove(&object_id);
    }

    /// The incarnation of `object_id` left the world (deleted, or
    /// forgotten out of range): its death is forgotten, but a stale delete
    /// leaves a newer incarnation's death alone.
    pub fn forget_incarnation(&mut self, object_id: u32, incarnation: u16) {
        if self.is_dead_incarnation(object_id, incarnation) {
            self.dead.remove(&object_id);
        }
    }

    /// Forget everything.
    pub fn clear(&mut self) {
        self.dead.clear();
    }
}

fn is_death_motion(data: &MovementData) -> bool {
    let ac_proto::Movement::Interpreted { state, .. } = &data.movement else {
        return false;
    };
    state.forward_command.is_some_and(is_death)
        || state.commands.iter().any(|item| is_death(item.command))
}

/// The motion an object is in now is its death (its forward motion; a
/// creature lying dead is no longer one to give to or refuse to lift).
#[must_use]
pub fn is_current_motion_dead(data: &MovementData) -> bool {
    let ac_proto::Movement::Interpreted { state, .. } = &data.movement else {
        return false;
    };
    state.forward_command.is_some_and(is_death)
}

fn is_death(wire: u16) -> bool {
    ac_motion::command::reconstruct_full_command(wire) == DEAD_MOTION
}

// -- Health stream ------------------------------------------------------------

/// The object whose health the server streams (the selected creature).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct HealthQuery {
    streaming: u32,
}

impl HealthQuery {
    /// The object being streamed (0 for none).
    #[must_use]
    pub fn streaming_object_id(&self) -> u32 {
        self.streaming
    }

    /// The selection changed: the open stream is closed (a query for object
    /// 0), and a new one opened when `should_query` says the selection has
    /// health worth asking about. Returns the queries to send, in order.
    pub fn on_selection_changed(&mut self, selected: Option<u32>, should_query: bool) -> Vec<u32> {
        let mut sends = Vec::new();
        if self.streaming != 0 {
            self.streaming = 0;
            sends.push(0);
        }
        if let Some(selected) = selected.filter(|&s| s != 0)
            && should_query
        {
            self.streaming = selected;
            sends.push(selected);
        }
        sends
    }

    /// Forget the stream (the session ended; the server forgets it too).
    pub fn reset(&mut self) {
        self.streaming = 0;
    }
}

#[cfg(test)]
#[path = "combat_tests.rs"]
mod tests;
