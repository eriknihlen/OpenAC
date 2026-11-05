//! Ported from OpenAC `tests/AcDream.Runtime.Tests/Gameplay/`
//! `RuntimeCombatModeStateTests.cs`, `RuntimeCombatAttackStateTests.cs`,
//! `RuntimeCombatTargetStateTests.cs`, `RuntimeCreatureDeathStateTests.cs`
//! (the owner's own tests) and `RuntimeSelectedObjectHealthQueryTests.cs`.

use std::collections::HashSet;

use ac_core::combat::CombatState;
use ac_core::items::{EquipMask, ItemType};
use ac_proto::{InterpretedMotionState, Movement, MovementData};

use super::*;
use crate::gameplay::selection::{Selection, SelectionSource};

// -- Combat mode ----------------------------------------------------------------

#[derive(Default)]
struct ModeOps {
    out_of_world: bool,
    equipment: Vec<ClientObject>,
    trace: Vec<String>,
}

impl CombatModeOperations for ModeOps {
    fn is_in_world(&self) -> bool {
        !self.out_of_world
    }
    fn ordered_equipment(&mut self) -> Vec<ClientObject> {
        self.trace.push("equipment".into());
        self.equipment.clone()
    }
    fn notify_explicit_combat_mode_request(&mut self) {
        self.trace.push("intent".into());
    }
    fn send_change_combat_mode(&mut self, mode: CombatMode) {
        self.trace
            .push(format!("send:{}", mode.name().unwrap_or("?")));
    }
}

struct Ready {
    ready: bool,
    teleporting: bool,
}

impl CombatModeReadiness for Ready {
    fn is_in_ready_position(&mut self, _: CombatMode) -> bool {
        self.ready
    }
    fn is_teleport_in_progress(&self) -> bool {
        self.teleporting
    }
}

/// Ready only in the listed modes; records every mode asked about.
#[derive(Default)]
struct StanceReady {
    ready_modes: HashSet<u32>,
    asked: Vec<CombatMode>,
}

impl CombatModeReadiness for StanceReady {
    fn is_in_ready_position(&mut self, mode: CombatMode) -> bool {
        self.asked.push(mode);
        self.ready_modes.contains(&mode.bits())
    }
    fn is_teleport_in_progress(&self) -> bool {
        false
    }
}

fn bow() -> ClientObject {
    let mut item = ClientObject::new(1);
    item.currently_equipped_location = EquipMask::MISSILE_WEAPON;
    item.item_type = ItemType::MISSILE_WEAPON;
    item.combat_use = Some(2);
    item
}

/// The mode state with its combat state: a sent change sets the mode.
struct Mode {
    combat: CombatState,
    state: CombatModeState,
    ops: ModeOps,
}

impl Mode {
    fn new() -> Self {
        Self {
            combat: CombatState::new(),
            state: CombatModeState::default(),
            ops: ModeOps::default(),
        }
    }

    fn settle(&mut self, result: &CombatModeRequestResult) {
        if result.status == CombatModeRequestStatus::Sent {
            self.combat.set_combat_mode(result.mode);
        }
    }

    fn request(
        &mut self,
        mode: CombatMode,
        readiness: Option<&mut dyn CombatModeReadiness>,
    ) -> CombatModeRequestResult {
        let current = self.combat.current_mode();
        let result = self.state.request(mode, current, &mut self.ops, readiness);
        self.settle(&result);
        result
    }

    fn toggle(
        &mut self,
        readiness: Option<&mut dyn CombatModeReadiness>,
    ) -> CombatModeRequestResult {
        let current = self.combat.current_mode();
        let result = self.state.toggle(current, &mut self.ops, readiness);
        self.settle(&result);
        result
    }

    fn apply_pending(&mut self, readiness: Option<&mut dyn CombatModeReadiness>) {
        let current = self.combat.current_mode();
        if let Some(mode) = self
            .state
            .apply_pending_mode(current, &mut self.ops, readiness)
        {
            self.combat.set_combat_mode(mode);
        }
    }
}

#[test]
fn a_request_while_the_body_is_busy_is_parked_and_sent_when_it_is_ready() {
    let mut m = Mode::new();
    let mut ready = Ready {
        ready: false,
        teleporting: false,
    };
    let result = m.request(CombatMode::MAGIC, Some(&mut ready));
    assert_eq!(result.status, CombatModeRequestStatus::Deferred);
    assert_eq!(m.combat.current_mode(), CombatMode::NON_COMBAT);
    assert_eq!(m.state.pending_mode(), Some(CombatMode::MAGIC));
    m.apply_pending(Some(&mut ready));
    assert!(!m.ops.trace.contains(&"send:Magic".to_owned()));
    ready.ready = true;
    m.apply_pending(Some(&mut ready));
    assert!(m.ops.trace.contains(&"send:Magic".to_owned()));
    assert_eq!(m.combat.current_mode(), CombatMode::MAGIC);
    assert_eq!(m.state.pending_mode(), None);
}

#[test]
fn a_request_while_ready_goes_out_at_once() {
    let mut m = Mode::new();
    let mut ready = Ready {
        ready: true,
        teleporting: false,
    };
    let result = m.request(CombatMode::MAGIC, Some(&mut ready));
    assert_eq!(result.status, CombatModeRequestStatus::Sent);
    assert_eq!(m.combat.current_mode(), CombatMode::MAGIC);
    assert_eq!(m.state.pending_mode(), None);
}

#[test]
fn no_change_is_taken_while_teleporting() {
    let mut m = Mode::new();
    let mut ready = Ready {
        ready: true,
        teleporting: true,
    };
    let result = m.request(CombatMode::MAGIC, Some(&mut ready));
    assert_eq!(result.status, CombatModeRequestStatus::Rejected);
    assert_eq!(
        result.notice.as_deref(),
        Some("You can't change combat mode while teleporting.")
    );
    assert_eq!(m.combat.current_mode(), CombatMode::NON_COMBAT);
}

#[test]
fn a_parked_change_the_server_already_made_is_dropped() {
    let mut m = Mode::new();
    let mut ready = Ready {
        ready: false,
        teleporting: false,
    };
    m.request(CombatMode::MAGIC, Some(&mut ready));
    m.combat.set_combat_mode(CombatMode::MAGIC);
    ready.ready = true;
    m.apply_pending(Some(&mut ready));
    assert!(!m.ops.trace.contains(&"send:Magic".to_owned()));
    assert_eq!(m.state.pending_mode(), None);
}

#[test]
fn peace_with_a_bow_is_asked_against_peace_not_the_missile_stance() {
    // As in OpenAC: the change is asked against the mode being left.
    let mut m = Mode::new();
    m.ops.equipment.push(bow());
    let mut ready = StanceReady::default();
    ready.ready_modes.insert(CombatMode::NON_COMBAT.bits());
    let result = m.toggle(Some(&mut ready));
    assert_eq!(result.status, CombatModeRequestStatus::Sent);
    assert_eq!(m.combat.current_mode(), CombatMode::MISSILE);
    assert!(m.ops.trace.contains(&"send:Missile".to_owned()));
    assert_eq!(ready.asked, [CombatMode::NON_COMBAT]);
}

#[test]
fn a_parked_change_is_retried_against_the_mode_being_left() {
    let mut m = Mode::new();
    let mut ready = StanceReady::default();
    assert_eq!(
        m.request(CombatMode::MISSILE, Some(&mut ready)).status,
        CombatModeRequestStatus::Deferred
    );
    ready.ready_modes.insert(CombatMode::NON_COMBAT.bits());
    m.apply_pending(Some(&mut ready));
    assert!(m.ops.trace.contains(&"send:Missile".to_owned()));
    assert_eq!(m.combat.current_mode(), CombatMode::MISSILE);
    assert!(
        ready
            .asked
            .iter()
            .all(|&mode| mode == CombatMode::NON_COMBAT)
    );
}

#[test]
fn outside_the_world_the_toggle_does_nothing() {
    let mut m = Mode::new();
    m.ops.out_of_world = true;
    assert_eq!(m.toggle(None).status, CombatModeRequestStatus::Inactive);
    assert!(m.ops.trace.is_empty());
    assert_eq!(m.combat.current_mode(), CombatMode::NON_COMBAT);
}

#[test]
fn peace_with_a_bow_states_intent_reads_equipment_then_sends() {
    let mut m = Mode::new();
    m.ops.equipment.push(bow());
    let result = m.toggle(None);
    assert_eq!(
        (result.status, result.mode),
        (CombatModeRequestStatus::Sent, CombatMode::MISSILE)
    );
    assert_eq!(m.ops.trace, ["intent", "equipment", "send:Missile"]);
}

#[test]
fn leaving_combat_reads_no_equipment() {
    let mut m = Mode::new();
    m.combat.set_combat_mode(CombatMode::MAGIC);
    let result = m.toggle(None);
    assert_eq!(
        (result.status, result.mode),
        (CombatModeRequestStatus::Sent, CombatMode::NON_COMBAT)
    );
    assert!(!m.ops.trace.contains(&"equipment".to_owned()));
}

#[test]
fn a_held_item_that_is_not_a_caster_refuses_combat() {
    let mut m = Mode::new();
    let mut torch = ClientObject::new(1);
    torch.name = "Torch".into();
    torch.currently_equipped_location = EquipMask::HELD;
    torch.item_type = ItemType::MISC;
    m.ops.equipment.push(torch);
    let result = m.toggle(None);
    assert_eq!(result.status, CombatModeRequestStatus::Rejected);
    assert_eq!(
        result.notice.as_deref(),
        Some("You can't enter combat mode while wielding the Torch")
    );
    assert_eq!(m.ops.trace, ["intent", "equipment"]);
}

#[test]
fn an_explicit_request_sends_the_mode_without_reading_equipment() {
    let mut m = Mode::new();
    let result = m.request(CombatMode::MAGIC, None);
    assert_eq!(
        (result.status, result.mode),
        (CombatModeRequestStatus::Sent, CombatMode::MAGIC)
    );
    assert_eq!(m.ops.trace, ["intent", "send:Magic"]);
}

// -- Attack -----------------------------------------------------------------------

#[derive(Default)]
struct AttackOps {
    sent: Vec<(AttackHeight, f32)>,
    allowed: Vec<bool>,
    cancels: u32,
    dual_wield: bool,
    not_ready: bool,
    auto_repeat: bool,
    events: Vec<&'static str>,
}

impl CombatAttackOperations for AttackOps {
    fn can_start_attack(&mut self, allow_auto_target: bool) -> bool {
        self.allowed.push(allow_auto_target);
        true
    }
    fn prepare_attack_request(&mut self) {
        self.events.push("prepare");
    }
    fn send_attack(&mut self, height: AttackHeight, power: f32, allow_auto_target: bool) -> bool {
        self.allowed.push(allow_auto_target);
        self.events.push("send");
        self.sent.push((height, power));
        true
    }
    fn send_cancel_attack(&mut self) {
        self.cancels += 1;
    }
    fn is_dual_wield(&self) -> bool {
        self.dual_wield
    }
    fn player_ready_for_attack(&self) -> bool {
        !self.not_ready
    }
    fn auto_repeat_attack(&self) -> bool {
        self.auto_repeat
    }
}

struct Attack {
    state: CombatAttackState,
    ops: AttackOps,
    now: f64,
}

impl Attack {
    fn new(mode: CombatMode) -> Self {
        let mut state = CombatAttackState::default();
        state.on_combat_mode_changed(mode);
        Self {
            state,
            ops: AttackOps::default(),
            now: 0.0,
        }
    }
    fn press(&mut self, height: AttackHeight) {
        self.state.press_attack(height, &mut self.ops, self.now);
    }
    fn release(&mut self) -> bool {
        self.state.release_attack(&mut self.ops, self.now)
    }
    fn tick(&mut self) {
        self.state.tick(&mut self.ops, self.now);
    }
    fn done(&mut self, error: u32) {
        self.state.on_attack_done(1, error, &mut self.ops, self.now);
    }
    fn bar(&self) -> f32 {
        self.state.power_bar_level(self.now, &self.ops)
    }
}

fn close(a: f32, b: f32) -> bool {
    (a - b).abs() < 1e-3
}

#[test]
fn a_normal_stance_charges_over_one_second_and_release_sends_the_level() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.now = 10.0;
    a.press(AttackHeight::HIGH);
    a.now = 10.5;
    assert!(close(a.bar(), 0.5));
    a.release();
    let [(height, power)] = a.ops.sent[..] else {
        panic!("one swing");
    };
    assert_eq!(height, AttackHeight::HIGH);
    assert!(close(power, 0.5));
}

#[test]
fn dual_wield_charges_in_eight_tenths_of_a_second() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.ops.dual_wield = true;
    a.now = 3.0;
    a.press(AttackHeight::MEDIUM);
    a.now = 3.4;
    assert!(close(a.bar(), 0.5));
}

#[test]
fn an_early_release_loads_on_to_the_setting_before_committing() {
    let mut a = Attack::new(CombatMode::MISSILE);
    a.state.set_desired_power(1.0);
    a.press(AttackHeight::LOW);
    a.now = 0.25;
    a.release();
    assert!(a.ops.sent.is_empty());
    for t in [0.26, 0.99] {
        a.now = t;
        a.tick();
        assert!(a.ops.sent.is_empty());
    }
    a.now = 1.0;
    a.tick();
    assert_eq!(a.ops.sent.len(), 1);
    assert_eq!(a.ops.sent[0].0, AttackHeight::LOW);
    assert!(close(a.ops.sent[0].1, 1.0));
}

#[test]
fn a_release_past_the_setting_fires_at_once_and_follows_with_the_setting() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.state.set_desired_power(0.5);
    a.press(AttackHeight::MEDIUM);
    a.now = 0.8;
    a.release();
    assert_eq!(a.ops.sent.len(), 2);
    assert!(close(a.ops.sent[0].1, 0.8) && close(a.ops.sent[1].1, 0.5));
}

#[test]
fn a_charged_release_is_followed_by_the_bar_setting() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.ops.auto_repeat = true;
    a.state.set_desired_power(0.0);
    a.press(AttackHeight::HIGH);
    a.now = 1.2;
    a.release();
    assert_eq!(a.ops.sent.len(), 2);
    assert!(close(a.ops.sent[0].1, 1.0) && close(a.ops.sent[1].1, 0.0));
    assert!(close(a.state.requested_attack_power(), 0.0));
}

#[test]
fn a_repeat_after_a_charged_release_keeps_the_bar_setting() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.ops.auto_repeat = true;
    a.state.set_desired_power(1.0 / 6.0);
    a.press(AttackHeight::MEDIUM);
    a.now = 1.2;
    a.release();
    assert_eq!(a.ops.sent.len(), 2);
    a.state.on_attack_commenced();
    a.now = 1.4;
    a.done(0);
    assert_eq!(a.ops.sent.len(), 2);
    assert!(a.state.repeat_attack_in_progress());
    assert!(close(a.state.requested_attack_power(), 1.0 / 6.0));
}

#[test]
fn moving_the_bar_during_repeats_asks_the_new_level_at_the_next_answer() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.ops.auto_repeat = true;
    a.state.set_desired_power(0.25);
    a.press(AttackHeight::LOW);
    a.now = 0.25;
    a.release();
    assert_eq!(a.ops.sent.len(), 1);
    a.state.on_attack_commenced();
    a.state.set_desired_power(1.0);
    a.now = 0.5;
    a.done(0);
    assert_eq!(a.ops.sent.len(), 2);
    assert_eq!(a.ops.sent[1].0, AttackHeight::LOW);
    assert!(close(a.ops.sent[1].1, 1.0));
}

#[test]
fn a_missile_tap_without_repeat_loads_to_the_setting_before_firing() {
    let mut a = Attack::new(CombatMode::MISSILE);
    a.state.set_desired_power(2.0 / 3.0);
    a.press(AttackHeight::MEDIUM);
    a.now = 0.05;
    a.release();
    a.now = 0.5;
    a.tick();
    assert!(a.ops.sent.is_empty());
    a.now = 0.7;
    a.tick();
    assert_eq!(a.ops.sent.len(), 1);
    assert!(close(a.ops.sent[0].1, 2.0 / 3.0));
    assert!(!a.state.repeat_attack_in_progress());
}

#[test]
fn a_held_key_uses_press_and_release() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.now = 1.0;
    assert!(a.state.handle_command(
        CombatAttackCommand::LowAttack,
        InputActivation::Press,
        &mut a.ops,
        a.now
    ));
    a.now = 1.5;
    assert!(a.state.handle_command(
        CombatAttackCommand::LowAttack,
        InputActivation::Release,
        &mut a.ops,
        a.now
    ));
    assert_eq!(a.ops.sent.len(), 1);
    assert_eq!(a.ops.sent[0].0, AttackHeight::LOW);
}

#[test]
fn leaving_a_targeted_mode_cancels_a_build() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.press(AttackHeight::MEDIUM);
    a.now = 0.4;
    a.state.on_combat_mode_changed(CombatMode::NON_COMBAT);
    assert!(!a.state.build_in_progress());
    assert!(!a.state.attack_request_in_progress());
    assert!(close(a.bar(), 0.0));
}

#[test]
fn a_request_waits_for_the_ready_stance_before_building() {
    let mut a = Attack::new(CombatMode::MISSILE);
    a.ops.not_ready = true;
    a.press(AttackHeight::HIGH);
    assert!(a.state.attack_request_in_progress());
    assert!(!a.state.build_in_progress());
    a.ops.not_ready = false;
    a.now = 5.0;
    a.tick();
    assert!(a.state.build_in_progress());
    assert!(close(a.bar(), 0.0));
}

#[test]
fn moving_during_repeats_cancels_and_stops_the_next_swing() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.ops.auto_repeat = true;
    a.now = 20.0;
    a.press(AttackHeight::MEDIUM);
    a.now += 0.5;
    a.release();
    assert_eq!(a.ops.sent.len(), 1);
    assert!(a.state.repeat_attack_in_progress());
    a.state.handle_command(
        CombatAttackCommand::AbortForMovement,
        InputActivation::Press,
        &mut a.ops,
        a.now,
    );
    a.done(0);
    assert_eq!(a.ops.cancels, 1);
    assert_eq!(a.ops.sent.len(), 1);
    assert!(!a.state.repeat_attack_in_progress());
    assert!(!a.state.build_in_progress());
    assert!(close(a.bar(), 0.0));
}

#[test]
fn moving_while_idle_sends_no_cancel() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.state.abort_automatic_attack(&mut a.ops);
    assert_eq!(a.ops.cancels, 0);
}

#[test]
fn the_attack_done_is_kept_once_and_a_reset_clears_it() {
    let mut a = Attack::new(CombatMode::NON_COMBAT);
    a.state.on_attack_done(47, 0x1234, &mut a.ops, 0.0);
    assert_eq!(
        (
            a.state.completion_revision(),
            a.state.completion_sequence(),
            a.state.completion_weenie_error()
        ),
        (1, 47, 0x1234)
    );
    a.state.reset_session();
    assert_eq!(
        (
            a.state.completion_revision(),
            a.state.completion_sequence(),
            a.state.completion_weenie_error()
        ),
        (0, 0, 0)
    );
}

#[test]
fn starting_a_request_prepares_the_player_before_the_build() {
    let mut a = Attack::new(CombatMode::MISSILE);
    a.now = 5.0;
    a.state.set_desired_power(0.0);
    a.press(AttackHeight::MEDIUM);
    assert_eq!(a.ops.events, ["prepare"]);
    assert!(a.state.attack_request_in_progress() && a.state.build_in_progress());
    a.release();
    assert_eq!(a.ops.events, ["prepare", "send"]);
}

#[test]
fn a_session_reset_restores_the_defaults_without_a_cancel() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.now = 1.0;
    a.state.set_desired_power(1.0);
    a.press(AttackHeight::HIGH);
    a.now = 1.5;
    a.state.reset_session();
    assert!(!a.state.attack_request_in_progress() && !a.state.build_in_progress());
    assert!(close(a.state.requested_attack_power(), 0.0));
    assert!(close(a.bar(), 0.0));
    assert_eq!(a.state.requested_height(), AttackHeight::MEDIUM);
    assert!(close(a.state.desired_power(), INITIAL_DESIRED_POWER));
    assert_eq!(a.ops.cancels, 0);
}

#[test]
fn automation_asks_for_no_auto_target() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.ops.auto_repeat = true;
    a.now = 1.0;
    a.state.automation_controlled = true;
    a.press(AttackHeight::MEDIUM);
    a.now = 2.0;
    a.release();
    assert!(!a.ops.allowed.is_empty() && a.ops.allowed.iter().all(|&x| !x));
    a.ops.allowed.clear();
    a.state.automation_controlled = false;
    a.done(0);
    a.ops.allowed.clear();
    a.press(AttackHeight::MEDIUM);
    a.now = 3.0;
    a.release();
    assert!(!a.ops.allowed.is_empty() && a.ops.allowed.iter().all(|&x| x));
}

#[test]
fn an_owner_that_names_its_power_gets_exactly_that_power() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.state.automation_controlled = true;
    a.state.set_desired_power(0.9);
    a.press(AttackHeight::HIGH);
    a.now = 0.94;
    a.tick();
    assert!(a.release());
    assert_eq!(a.ops.sent.len(), 1);
    assert!(close(a.ops.sent[0].1, 0.9));
}

#[test]
fn a_player_key_up_commits_the_level_held() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.state.set_desired_power(0.5);
    a.press(AttackHeight::MEDIUM);
    a.now = 0.8;
    a.release();
    assert!(close(a.ops.sent[0].1, 0.8));
}

#[test]
fn aborting_ends_the_request_instead_of_leaving_the_bar_loading() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.state.automation_controlled = true;
    a.state.set_desired_power(1.0);
    a.press(AttackHeight::MEDIUM);
    assert!(a.state.attack_request_in_progress());
    a.state.abort_automatic_attack(&mut a.ops);
    assert!(!a.state.attack_request_in_progress());
    assert!(!a.state.attack_server_response_pending());
    assert!(!a.state.build_in_progress());
    for t in [3.0, 6.0] {
        a.now = t;
        a.tick();
    }
    assert!(a.ops.sent.is_empty());
    assert!(!a.state.build_in_progress());
}

#[test]
fn the_answer_to_a_finished_swing_does_not_restart_a_charge() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.state.set_desired_power(1.0);
    a.press(AttackHeight::MEDIUM);
    a.now = 0.6;
    a.tick();
    assert!(close(a.bar(), 0.6));
    a.done(0);
    a.now = 1.0;
    a.tick();
    assert!(close(a.bar(), 1.0));
}

#[test]
fn an_owner_named_release_over_a_busy_server_sends_nothing() {
    let mut a = Attack::new(CombatMode::MELEE);
    a.state.automation_controlled = true;
    a.state.set_desired_power(1.0);
    a.press(AttackHeight::MEDIUM);
    a.state.on_attack_commenced();
    assert!(a.state.attack_server_response_pending());
    a.now = 1.0;
    assert!(!a.release());
    assert!(!a.state.attack_request_in_progress());
    assert!(a.ops.sent.is_empty());
    a.done(0);
    a.now = 5.0;
    a.tick();
    assert!(a.ops.sent.is_empty());
}

// -- Target -------------------------------------------------------------------------

const MONSTER: u32 = 0x5000_0001;
const OTHER: u32 = 0x5000_0002;

/// The selection, the target state and the closest-monster substitute, as
/// the gameplay owner wires them: an emptied selection may be refilled.
struct Target {
    selection: Selection,
    state: CombatTargetState,
    auto_target: bool,
    mode: CombatMode,
    calls: u32,
    replacement: Option<u32>,
}

impl Target {
    fn new(mode: CombatMode, auto_target: bool) -> Self {
        Self {
            selection: Selection::default(),
            state: CombatTargetState::default(),
            auto_target,
            mode,
            calls: 0,
            replacement: None,
        }
    }
    fn run(&mut self, t: Option<SelectionTransition>) {
        let Some(t) = t else { return };
        if self
            .state
            .on_selection_changed(&t, self.auto_target, self.mode)
        {
            self.calls += 1;
            if let Some(next) = self.replacement {
                let t =
                    self.selection
                        .select(next, SelectionSource::System, SelectionReason::Selected);
                self.run(t);
            }
        }
    }
    fn select(&mut self, guid: u32) {
        let t = self
            .selection
            .select(guid, SelectionSource::Host, SelectionReason::Selected);
        self.run(t);
    }
    fn clear(&mut self, reason: SelectionReason) {
        let t = self.selection.clear(SelectionSource::Keyboard, reason);
        self.run(t);
    }
}

#[test]
fn a_dead_target_with_auto_target_picks_the_closest_replacement() {
    let mut t = Target::new(CombatMode::MELEE, true);
    t.select(MONSTER);
    t.replacement = Some(OTHER);
    t.clear(SelectionReason::CombatTargetDied);
    assert_eq!((t.calls, t.selection.selected()), (1, Some(OTHER)));
}

#[test]
fn a_dead_target_without_auto_target_leaves_the_selection_clear() {
    let mut t = Target::new(CombatMode::MELEE, false);
    t.select(MONSTER);
    t.clear(SelectionReason::CombatTargetDied);
    assert_eq!((t.calls, t.selection.selected()), (0, None));
}

#[test]
fn at_peace_a_dead_target_is_not_replaced() {
    let mut t = Target::new(CombatMode::NON_COMBAT, true);
    t.select(MONSTER);
    t.clear(SelectionReason::CombatTargetDied);
    assert_eq!((t.calls, t.selection.selected()), (0, None));
}

#[test]
fn a_session_reset_acquires_nothing() {
    let mut t = Target::new(CombatMode::MISSILE, true);
    t.select(OTHER);
    let reset = t.selection.reset();
    t.run(Some(reset));
    assert_eq!((t.calls, t.selection.selected()), (0, None));
}

#[test]
fn a_willing_deselect_stays_empty_and_consumes_the_mark_once() {
    let mut t = Target::new(CombatMode::MELEE, true);
    t.select(MONSTER);
    t.replacement = Some(MONSTER);
    t.state.notify_target_willingly_lost();
    t.clear(SelectionReason::Cleared);
    assert_eq!((t.calls, t.selection.selected()), (0, None));
    t.select(OTHER);
    t.replacement = None;
    t.clear(SelectionReason::Cleared);
    assert_eq!(t.calls, 1);
}

#[test]
fn a_removed_target_still_acquires_a_new_one() {
    let mut t = Target::new(CombatMode::MELEE, true);
    t.select(MONSTER);
    t.clear(SelectionReason::SelectedObjectRemoved);
    assert_eq!(t.calls, 1);
}

#[test]
fn a_session_reset_drops_a_pending_willing_mark() {
    let mut t = Target::new(CombatMode::MELEE, true);
    t.select(MONSTER);
    t.state.notify_target_willingly_lost();
    let reset = t.selection.reset();
    t.run(Some(reset));
    assert_eq!(t.calls, 0);
    t.select(OTHER);
    t.clear(SelectionReason::Cleared);
    assert_eq!(t.calls, 1);
}

// -- Deaths ---------------------------------------------------------------------------

/// The wire half of the death motion (RuntimeCreatureDeathStateTests).
const DEAD_WIRE: u16 = 0x0011;

fn motion(forward: Option<u16>, one_shot: Option<u16>) -> MovementData {
    MovementData {
        movement_type: 0,
        motion_flags: 0,
        current_style: 0x003D,
        movement: Movement::Interpreted {
            state: InterpretedMotionState {
                forward_command: forward,
                commands: one_shot
                    .map(|command| ac_proto::MotionItem {
                        command,
                        packed_sequence: 1,
                        speed: 1.0,
                    })
                    .into_iter()
                    .collect(),
                ..InterpretedMotionState::default()
            },
            sticky_object: None,
        },
    }
}

#[test]
fn the_dead_wire_command_is_the_death_motion() {
    assert_eq!(
        ac_motion::command::reconstruct_full_command(DEAD_WIRE),
        DEAD_MOTION
    );
}

#[test]
fn death_arrives_as_a_forward_command_or_a_one_shot() {
    let mut d = CreatureDeathState::default();
    assert!(d.observe_motion(MONSTER, 1, Some(&motion(Some(DEAD_WIRE), None))));
    assert!(d.observe_motion(OTHER, 1, Some(&motion(None, Some(DEAD_WIRE)))));
    assert!(d.is_dead_incarnation(MONSTER, 1) && d.is_dead_incarnation(OTHER, 1));
}

#[test]
fn a_repeated_death_is_announced_once() {
    let mut d = CreatureDeathState::default();
    let dead = motion(Some(DEAD_WIRE), None);
    assert!(d.observe_motion(MONSTER, 1, Some(&dead)));
    assert!(!d.observe_motion(MONSTER, 1, Some(&dead)));
}

#[test]
fn a_living_motion_says_nothing() {
    let mut d = CreatureDeathState::default();
    assert!(!d.observe_motion(MONSTER, 1, Some(&motion(Some(0x0007), None))));
    assert!(!d.is_dead(MONSTER));
    assert_eq!(d.count(), 0);
}

#[test]
fn a_fresh_creature_on_a_dead_id_is_alive() {
    let mut d = CreatureDeathState::default();
    d.observe_motion(MONSTER, 1, Some(&motion(Some(DEAD_WIRE), None)));
    assert!(d.is_dead_incarnation(MONSTER, 1));
    assert!(!d.is_dead_incarnation(MONSTER, 2));
}

#[test]
fn a_spawn_forgets_the_dead_and_reads_its_own_motion() {
    let mut d = CreatureDeathState::default();
    d.observe_motion(MONSTER, 1, Some(&motion(Some(DEAD_WIRE), None)));
    d.observe_spawn(MONSTER, 2, Some(&motion(Some(0x0007), None)));
    assert!(!d.is_dead(MONSTER));
    d.observe_spawn(MONSTER, 3, Some(&motion(Some(DEAD_WIRE), None)));
    assert!(d.is_dead(MONSTER));
}

// -- Health stream ----------------------------------------------------------------------

#[test]
fn selecting_a_creature_asks_for_its_health() {
    let mut q = HealthQuery::default();
    assert_eq!(q.on_selection_changed(Some(MONSTER), true), [MONSTER]);
    assert_eq!(q.streaming_object_id(), MONSTER);
}

#[test]
fn clearing_the_selection_stops_the_stream() {
    let mut q = HealthQuery::default();
    q.on_selection_changed(Some(MONSTER), true);
    assert_eq!(q.on_selection_changed(None, false), [0]);
    assert_eq!(q.streaming_object_id(), 0);
}

#[test]
fn moving_to_an_object_without_health_closes_the_stream() {
    let mut q = HealthQuery::default();
    q.on_selection_changed(Some(MONSTER), true);
    assert_eq!(q.on_selection_changed(Some(OTHER), false), [0]);
    assert_eq!(q.streaming_object_id(), 0);
}

#[test]
fn an_object_with_no_health_is_never_asked_about() {
    let mut q = HealthQuery::default();
    assert!(q.on_selection_changed(Some(OTHER), false).is_empty());
    assert_eq!(q.streaming_object_id(), 0);
}
