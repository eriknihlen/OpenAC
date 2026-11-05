//! API group F: combat (OpenAC `ICombatAutomation`) over the combat
//! owners: the mode request, the attack bar and the creatures nearby.

use ac_core::combat::{AttackHeight, CombatMode, is_hostile_monster, supports_targeted_attack};
use ac_core::items::ItemType;
use ac_core::physics::PhysicsState;
use ac_core::properties::PropertyInt;
use ac_plugin::abi::combat::{
    self as abi, AttackRequest, CombatResult, CombatSnapshot, CombatStatus, CombatTarget,
};

use super::{PluginBinding, Ran};
use crate::gameplay::combat::{CombatAttackCommand, InputActivation};
use crate::gameplay::{GameplayCommand, can_be_seen_and_is_alive, combat_object};
use crate::inventory::ObjectRef;
use crate::session::SessionCommand;
use crate::world::World;

fn project_mode(mode: CombatMode) -> abi::CombatMode {
    match mode {
        CombatMode::NON_COMBAT => abi::CombatMode::Peace,
        CombatMode::MELEE => abi::CombatMode::Melee,
        CombatMode::MISSILE => abi::CombatMode::Missile,
        CombatMode::MAGIC => abi::CombatMode::Magic,
        _ => abi::CombatMode::Unknown,
    }
}

fn project_height(height: AttackHeight) -> abi::AttackHeight {
    match height {
        AttackHeight::HIGH => abi::AttackHeight::High,
        AttackHeight::LOW => abi::AttackHeight::Low,
        _ => abi::AttackHeight::Medium,
    }
}

fn attack_key(height: abi::AttackHeight) -> CombatAttackCommand {
    match height {
        abi::AttackHeight::High => CombatAttackCommand::HighAttack,
        abi::AttackHeight::Medium => CombatAttackCommand::MediumAttack,
        abi::AttackHeight::Low => CombatAttackCommand::LowAttack,
    }
}

/// A place as one frame for every landblock: metres east and north of the
/// map's corner, and height.
pub(super) fn global(position: &ac_proto::Position) -> glam::Vec3 {
    let block_x = f32::from(u8::try_from((position.cell_id >> 24) & 0xFF).unwrap_or(0));
    let block_y = f32::from(u8::try_from((position.cell_id >> 16) & 0xFF).unwrap_or(0));
    glam::Vec3::new(
        block_x * 192.0 + position.origin.x,
        block_y * 192.0 + position.origin.y,
        position.origin.z,
    )
}

/// An angle as -180..180 degrees.
fn signed_degrees(angle: f32) -> f32 {
    let mut a = angle % 360.0;
    if a > 180.0 {
        a -= 360.0;
    } else if a <= -180.0 {
        a += 360.0;
    }
    a
}

/// Whether `target` is a creature a plugin may attack: a hostile monster,
/// placed in the world (OpenAC `HostileTargetScope.Classified`).
fn is_hostile(world: &World, target: u32) -> bool {
    let player = world.player_guid().unwrap_or(0);
    target != 0
        && target != player
        && world.object(target).and_then(|o| o.position()).is_some()
        && is_hostile_monster(
            player,
            combat_object(world.item(player)).as_ref(),
            combat_object(world.item(target)).as_ref(),
        )
}

impl PluginBinding<'_> {
    /// The combat state now (OpenAC `Combat.Snapshot`).
    pub(super) fn combat_snapshot(&self) -> CombatSnapshot {
        if !self.facts.in_world {
            return CombatSnapshot::default();
        }
        let gameplay = &self.state.gameplay;
        let view = gameplay.combat_view_at(
            &self.state.world,
            &self.state.options,
            Some(&self.state.movement),
            self.session_seconds(),
        );
        let attack = gameplay.attack();
        CombatSnapshot {
            selected_id: view.selected.unwrap_or(0),
            mode: project_mode(view.mode),
            attack_height: project_height(view.height),
            desired_power: view.desired_power,
            power_bar_level: view.power_bar,
            build_in_progress: attack.build_in_progress(),
            request_in_progress: view.request_in_progress,
            server_response_pending: view.response_pending,
            repeat_attack_in_progress: view.repeating,
            completion_revision: i64::try_from(attack.completion_revision()).unwrap_or(i64::MAX),
            completion_sequence: attack.completion_sequence(),
            completion_weenie_error: attack.completion_weenie_error(),
            qualified_self_motion_revision: 0,
            qualified_self_motion_age_seconds: 0.0,
            server_mode: view
                .server_mode
                .map_or(abi::CombatMode::Unknown, project_mode),
        }
    }

    /// The hostile creatures within `maximum` metres (straight line,
    /// height included), with where they stand relative to the facing
    /// (OpenAC `RuntimeHostileTargetQuery.Capture`, classified scope).
    pub(super) fn combat_hostile_targets(&self, maximum: f32) -> Vec<CombatTarget> {
        if !self.facts.in_world || maximum.is_nan() || maximum <= 0.0 {
            return Vec::new();
        }
        let world = &self.state.world;
        let Some(player) = world.player_guid() else {
            return Vec::new();
        };
        let Some(here) = world.object(player).and_then(|o| o.position()) else {
            return Vec::new();
        };
        let here_world = global(here);
        let o = here.orientation;
        let facing =
            ac_motion::move_to_math::get_heading(glam::Quat::from_xyzw(o.x, o.y, o.z, o.w));
        let combat = self.state.gameplay.combat();
        let deaths = self.state.gameplay.deaths();
        let names = self.state.gameplay.tables().creature_names();
        let unseen = PhysicsState::HIDDEN.bits() | PhysicsState::NO_DRAW.bits();
        let mut targets = Vec::new();
        for object in world.objects() {
            let guid = object.guid();
            let Some(position) = object.position() else {
                continue;
            };
            // One hidden or not drawn (a golem under the sand) cannot be
            // fought: every swing or spell at it would come back refused.
            if object.physics().state & unseen != 0 || !is_hostile(world, guid) {
                continue;
            }
            let there = global(position);
            let delta = there - here_world;
            if delta.length_squared() > maximum * maximum {
                continue;
            }
            let heading = ac_motion::move_to_math::position_heading(here_world, there);
            let item = world.item(guid);
            let incarnation = object.key().incarnation;
            let has_health = combat.has_health(guid);
            let species_id = item.map_or(0, |i| {
                i.properties
                    .ints
                    .get(&PropertyInt::CREATURE_TYPE.0)
                    .copied()
                    .unwrap_or(0)
            });
            targets.push(CombatTarget {
                object_id: guid,
                name: item.map(|i| i.name.clone()).unwrap_or_default(),
                weenie_class_id: item.map_or(0, |i| i.weenie_class_id),
                distance: delta.length(),
                relative_angle_degrees: signed_degrees(heading - facing),
                is_health_known: has_health,
                health_fraction: if has_health {
                    combat.health_percent(guid)
                } else {
                    1.0
                },
                species_id,
                species_name: names.resolve(species_id).to_owned(),
                // OpenAC's hostile-target capture reads 0 here too.
                maximum_health: 0,
                has_shield: world
                    .items()
                    .get_equipped_by(guid)
                    .iter()
                    .any(|i| i.item_type.intersects(ItemType::ARMOR)),
                incarnation,
                health_revision: 0,
                seconds_since_health_update: f64::INFINITY,
                height_difference: delta.z,
                is_dead: deaths.is_dead_incarnation(guid, incarnation),
                server_position: object.accepted_position().map(super::world::position_of),
            });
        }
        targets.sort_by_key(|t| t.object_id);
        targets
    }

    fn mode_command(&mut self, command: GameplayCommand) -> CombatResult {
        match self.run(SessionCommand::Gameplay(command)) {
            Ran::NoConnection => CombatResult::of(CombatStatus::Unavailable),
            Ran::Refused(why) => CombatResult {
                status: CombatStatus::Unavailable,
                notice: Some(why),
            },
            Ran::Done => {
                use crate::gameplay::combat::CombatModeRequestStatus as S;
                match self.state.gameplay.last_mode_request() {
                    Some(r) if matches!(r.status, S::Sent | S::Deferred) => {
                        CombatResult::of(CombatStatus::ModeChangeSent)
                    }
                    Some(r) if r.status == S::Rejected => CombatResult {
                        status: CombatStatus::Refused,
                        notice: r.notice.clone(),
                    },
                    r => CombatResult {
                        status: CombatStatus::Unavailable,
                        notice: r.and_then(|r| r.notice.clone()),
                    },
                }
            }
        }
    }

    /// Enter a mode (OpenAC `EnterMode`): already there is ready; a
    /// request parked behind a running motion counts as sent.
    pub(super) fn combat_enter_mode(&mut self, mode: abi::CombatMode) -> CombatResult {
        if !self.in_world() {
            return CombatResult::of(CombatStatus::Unavailable);
        }
        let requested = match mode {
            abi::CombatMode::Peace => CombatMode::NON_COMBAT,
            abi::CombatMode::Melee => CombatMode::MELEE,
            abi::CombatMode::Missile => CombatMode::MISSILE,
            abi::CombatMode::Magic => CombatMode::MAGIC,
            abi::CombatMode::Unknown => {
                return CombatResult {
                    status: CombatStatus::Refused,
                    notice: Some("Invalid combat mode.".to_owned()),
                };
            }
        };
        if self.state.gameplay.combat_mode() == requested {
            return CombatResult::of(CombatStatus::AlreadyReady);
        }
        self.mode_command(GameplayCommand::SetCombatMode(requested))
    }

    /// The combat key from peace (OpenAC `EnterDefaultMode`).
    pub(super) fn combat_enter_default_mode(&mut self) -> CombatResult {
        if !self.in_world() {
            return CombatResult::of(CombatStatus::Unavailable);
        }
        if self.state.gameplay.combat_mode() != CombatMode::NON_COMBAT {
            return CombatResult::of(CombatStatus::AlreadyReady);
        }
        self.mode_command(GameplayCommand::ToggleCombat)
    }

    /// Why a swing asked for never armed (OpenAC `DescribeAttackRefusal`).
    pub(super) fn describe_attack_refusal(&self, target: u32) -> String {
        if self.state.gameplay.selection().selected() != Some(target) {
            return "Something else took the target before the swing armed.".to_owned();
        }
        let gameplay = &self.state.gameplay;
        if !(is_hostile(&self.state.world, target)
            && can_be_seen_and_is_alive(
                &self.state.world,
                target,
                gameplay.deaths(),
                gameplay.combat(),
            ))
        {
            return "The target cannot be attacked: it is out of sight or out of play.".to_owned();
        }
        "The character is in no position to attack.".to_owned()
    }

    /// Start a swing at a creature (OpenAC `BeginPhysicalAttack`): a swing
    /// in flight at the same creature is busy; at another, it ends and the
    /// new one starts at once.
    pub(super) fn combat_begin_attack(&mut self, request: AttackRequest) -> CombatResult {
        if !self.in_world() {
            return CombatResult::of(CombatStatus::Unavailable);
        }
        let target = request.target_id;
        if !is_hostile(&self.state.world, target) {
            return CombatResult::of(CombatStatus::InvalidTarget);
        }
        if !supports_targeted_attack(self.state.gameplay.combat_mode()) {
            return CombatResult::of(CombatStatus::WrongMode);
        }
        let attack = self.state.gameplay.attack();
        let outstanding = attack.attack_request_in_progress()
            || attack.attack_server_response_pending()
            || attack.repeat_attack_in_progress();
        if outstanding {
            if self.state.plugin_services.armed_attack_target == target {
                return CombatResult::of(CombatStatus::Busy);
            }
            let abort = GameplayCommand::AttackInput(
                CombatAttackCommand::AbortForMovement,
                InputActivation::Press,
            );
            let _ = self.run(SessionCommand::Gameplay(abort));
        }
        let select = GameplayCommand::Select(ObjectRef::Guid(target));
        let _ = self.run(SessionCommand::Gameplay(select));
        let power = GameplayCommand::SetAttackPower(request.power.clamp(0.0, 1.0));
        let _ = self.run(SessionCommand::Gameplay(power));
        let press =
            GameplayCommand::AttackInput(attack_key(request.height), InputActivation::Press);
        let _ = self.run(SessionCommand::Gameplay(press));
        if !self.state.gameplay.attack().attack_request_in_progress() {
            self.state.plugin_services.armed_attack_target = 0;
            return CombatResult {
                status: CombatStatus::Refused,
                notice: Some(self.describe_attack_refusal(target)),
            };
        }
        self.state.plugin_services.armed_attack_target = target;
        CombatResult::of(CombatStatus::Started)
    }

    /// Release the swing (OpenAC `ReleasePhysicalAttack`): a swing that
    /// never left is busy while the server holds the last one, else refused
    /// with why.
    pub(super) fn combat_release_attack(&mut self) -> CombatResult {
        if !self.in_world() {
            return CombatResult::of(CombatStatus::Unavailable);
        }
        let attack = self.state.gameplay.attack();
        if !attack.attack_request_in_progress() {
            return CombatResult::of(CombatStatus::Refused);
        }
        let key = attack_key(project_height(attack.requested_height()));
        let release = GameplayCommand::AttackInput(key, InputActivation::Release);
        let _ = self.run(SessionCommand::Gameplay(release));
        let attack = self.state.gameplay.attack();
        if attack.last_release() == Some(true) {
            return CombatResult::of(CombatStatus::Released);
        }
        if attack.attack_server_response_pending() {
            return CombatResult::of(CombatStatus::Busy);
        }
        let armed = std::mem::take(&mut self.state.plugin_services.armed_attack_target);
        CombatResult {
            status: CombatStatus::Refused,
            notice: Some(self.describe_attack_refusal(armed)),
        }
    }

    /// Stop attacking (OpenAC `AbortPhysicalAttack`).
    pub(super) fn combat_abort_attack(&mut self) -> CombatResult {
        self.state.plugin_services.armed_attack_target = 0;
        let abort = GameplayCommand::AttackInput(
            CombatAttackCommand::AbortForMovement,
            InputActivation::Press,
        );
        match self.run(SessionCommand::Gameplay(abort)) {
            Ran::NoConnection => CombatResult::of(CombatStatus::Unavailable),
            _ => CombatResult::of(CombatStatus::Stopped),
        }
    }

    /// Let go of a creature the server stopped talking about (OpenAC
    /// `DismissGhostTarget`).
    pub(super) fn combat_dismiss_ghost(&mut self, target: u32) -> CombatResult {
        if !self.in_world() {
            return CombatResult::of(CombatStatus::Unavailable);
        }
        match self.run(SessionCommand::DismissGhost(target)) {
            Ran::Done => CombatResult::of(CombatStatus::Stopped),
            Ran::Refused(_) => CombatResult::of(CombatStatus::InvalidTarget),
            Ran::NoConnection => CombatResult::of(CombatStatus::Unavailable),
        }
    }

    /// Take combat over (OpenAC `AcquireCombatControl`) or hand it back.
    /// One plugin holds it at a time; it goes back when that plugin
    /// unloads.
    pub(super) fn combat_control(&mut self, plugin: &str, acquire: bool) -> bool {
        let services = &mut self.state.plugin_services;
        if acquire {
            if !self.facts.in_world {
                return false;
            }
            match &services.combat_control {
                Some(holder) if holder != plugin => return false,
                _ => services.combat_control = Some(plugin.to_owned()),
            }
            self.state.gameplay.set_automation_controlled(true);
            true
        } else {
            if services.combat_control.as_deref() == Some(plugin) {
                services.combat_control = None;
                self.state.gameplay.set_automation_controlled(false);
            }
            false
        }
    }
}
