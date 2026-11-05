//! The character's walks to objects it is about to act on, as the
//! inventory's approach sees them ([`ApproachSource`]): planning, the
//! move-to that carries it out, the answers when it ends, and the small
//! body that lets the walk follow an object with no simulated body of its
//! own (a corpse, a chest, an item on the ground).
//!
//! Each placed body is one lifetime of walks; each walk begun is the next
//! generation within it. A move-to's end (arrival, call-off, failure) is
//! published for the current walk; a body that goes away publishes a
//! call-off for its last walk and nothing older survives it.

use std::collections::HashMap;

use ac_core::physics::PhysicsState;
use ac_physics::movement::{
    MoveToEvent, MovementEnv, MovementOps, MovementStruct, ObjectToken, PhysicsObjHost, TargetInfo,
    TargetManager, TargetMessage, TargetOps, WeenieError,
};
use ac_physics::{CellPosition, Quat, Vec3};

use super::Movement;
use super::remote::body_shape_of;
use crate::inventory::{ApproachCompletion, ApproachPlan, ApproachSource, ApproachToken};
use crate::world::World;

/// The walks of the character's bodies and their answers.
#[derive(Debug, Default)]
pub(super) struct Approaches {
    /// The current body's lifetime (0 before the first body).
    lifetime: u64,
    /// The last lifetime handed out.
    last_lifetime: u64,
    /// The current walk within it (0 before the first).
    generation: u64,
    completions: Vec<ApproachCompletion>,
    /// Bodies answering for walked-to objects the physics does not move.
    hosts: HashMap<u32, TableHost>,
}

impl Approaches {
    fn current(&self) -> Option<ApproachToken> {
        (self.lifetime != 0 && self.generation != 0).then_some(ApproachToken {
            lifetime: self.lifetime,
            generation: self.generation,
        })
    }

    fn publish(&mut self, natural: bool) {
        if let Some(token) = self.current() {
            self.completions.push(ApproachCompletion { token, natural });
        }
    }

    /// A new body: the old one's walk is called off and its answers go.
    pub(super) fn begin_lifetime(&mut self) {
        self.retire_lifetime();
        self.last_lifetime += 1;
        self.lifetime = self.last_lifetime;
        self.generation = 0;
    }

    /// The body went: answers it left are replaced by a call-off of its
    /// last walk.
    pub(super) fn retire_lifetime(&mut self) {
        if self.lifetime == 0 {
            return;
        }
        let lifetime = self.lifetime;
        self.completions.retain(|c| c.token.lifetime != lifetime);
        self.publish(false);
        self.lifetime = 0;
        self.generation = 0;
        self.hosts.clear();
    }

    fn publish_events(&mut self, events: Vec<MoveToEvent>) {
        for event in events {
            self.publish(event == MoveToEvent::Complete(WeenieError::NONE));
        }
    }
}

/// The least body an object needs to be walked to: where it is and that it
/// stands still. It answers a watcher at once with its place, and again
/// whenever it has been moved past the watcher's radius.
#[derive(Debug, Clone)]
struct TableHost {
    token: ObjectToken,
    position: CellPosition,
    radius: f32,
    now: f64,
    target: TargetManager,
    outbox: Vec<TargetMessage>,
}

impl PhysicsObjHost for TableHost {
    fn obj_token(&self) -> ObjectToken {
        self.token
    }
    fn obj_position(&self) -> CellPosition {
        self.position
    }
    fn obj_velocity(&self) -> Vec3 {
        Vec3::ZERO
    }
    fn obj_radius(&self) -> f32 {
        self.radius
    }
    fn obj_in_contact(&self) -> bool {
        true
    }
    fn obj_minterp_max_speed(&self) -> Option<f32> {
        None
    }
    fn obj_cur_time(&self) -> f64 {
        self.now
    }
    fn obj_physics_timer_time(&self) -> f64 {
        self.now
    }
    fn target_manager(&mut self) -> &mut TargetManager {
        &mut self.target
    }
    fn target_manager_ref(&self) -> &TargetManager {
        &self.target
    }
    fn obj_resolve(&self, _id: u32) -> Option<ObjectToken> {
        None
    }
    fn obj_position_of(&self, _token: ObjectToken) -> Option<CellPosition> {
        None
    }
    fn obj_post(&mut self, message: TargetMessage) {
        self.outbox.push(message);
    }
    fn obj_handle_update_target(&mut self, _info: TargetInfo) {}
    fn obj_interrupt_current_movement(&mut self) {}
}

impl Movement {
    /// Where an object is in the body's frame: its simulated body, else the
    /// server's last word carried into the frame; `None` when held by a
    /// parent, contained, or outside the resident landblocks.
    fn approach_position(&self, world: &World, guid: u32) -> Option<CellPosition> {
        if let Some(body) = self.remotes.values().find(|b| b.guid == guid) {
            return Some(body.driver.position());
        }
        let object = world.object(guid)?;
        if object.parent().is_some() {
            return None;
        }
        let p = object.position()?;
        let corner = self.residency.origin_of(p.cell_id)?;
        Some(CellPosition::new(
            p.cell_id,
            Vec3::new(p.origin.x, p.origin.y, p.origin.z) + corner,
            super::remote::wire_orientation(p),
        ))
    }

    /// Keep a table body for the object the character's walk seeks when
    /// nothing else simulates it (its place refreshed from the world), and
    /// none for anything else.
    pub(super) fn follow_sought_object(&mut self, world: &World) {
        let sought = self
            .player
            .as_ref()
            .filter(|p| p.controller.object().move_to.is_moving_to())
            .map_or(0, |p| p.controller.object().move_to.top_level_object_id);
        self.ensure_table_host(world, sought);
    }

    /// Where the minimal stand-in body of `guid` would stand: `None` for the
    /// character, for an object something else simulates, for a hidden
    /// object, and for one with no place in the body's frame.
    pub(super) fn stand_in_place(&self, world: &World, guid: u32) -> Option<CellPosition> {
        let player = self.player.as_ref().map_or(0, |p| p.guid);
        let simulated = guid == player || self.remotes.values().any(|b| b.guid == guid);
        let hidden = world
            .object(guid)
            .is_some_and(|o| o.physics().state & PhysicsState::HIDDEN.bits() != 0);
        if guid == 0 || simulated || hidden {
            return None;
        }
        self.approach_position(world, guid)
    }

    /// Make (or refresh) the stand-in body of `guid`, dropping any other.
    pub(super) fn ensure_table_host(&mut self, world: &World, guid: u32) {
        let place = self.stand_in_place(world, guid);
        let now = self
            .player
            .as_ref()
            .map_or(0.0, |p| f64::from(p.controller.sim_time_seconds()));
        let radius = self.body_shape(world, guid).map_or(0.0, |s| s.0);
        let hosts = &mut self.approaches.hosts;
        hosts.retain(|id, _| *id == guid);
        let Some(position) = place else {
            hosts.clear();
            return;
        };
        let Some(object) = world.object(guid) else {
            return;
        };
        let token = ObjectToken {
            id: guid,
            incarnation: u64::from(object.key().incarnation),
        };
        let host = hosts.entry(guid).or_insert_with(|| TableHost {
            token,
            position,
            radius,
            now,
            target: TargetManager::default(),
            outbox: Vec::new(),
        });
        if host.token != token {
            *host = TableHost {
                token,
                position,
                radius,
                now,
                target: TargetManager::default(),
                outbox: Vec::new(),
            };
        }
        host.position = position;
        host.now = now;
    }

    /// The table bodies' watchers hear of them (twice a second at most).
    pub(super) fn drive_table_hosts(&mut self) {
        for host in self.approaches.hosts.values_mut() {
            host.handle_targetting();
        }
        self.deliver_target_messages();
    }

    /// Insert the table bodies into a view of the bodies.
    pub(super) fn add_table_hosts(&self, view: &mut super::remote::BodiesView) {
        for host in self.approaches.hosts.values() {
            view.insert(host.token, host.position);
        }
    }

    /// Deliver a message to a table body; false when none has the token.
    pub(super) fn deliver_to_table_host(&mut self, message: TargetMessage) -> bool {
        let to = message.addressee();
        match self.approaches.hosts.get_mut(&to.id) {
            Some(host) if host.token == to => {
                host.deliver_target_message(message);
                true
            }
            _ => false,
        }
    }

    /// The table bodies' posted messages.
    pub(super) fn take_table_host_messages(&mut self) -> Vec<TargetMessage> {
        self.approaches
            .hosts
            .values_mut()
            .flat_map(|h| std::mem::take(&mut h.outbox))
            .collect()
    }

    /// The local player's move-to ended: answer the current walk.
    pub(super) fn publish_move_to_events(&mut self) {
        let Some(player) = self.player.as_mut() else {
            return;
        };
        let events = player.controller.object_mut().take_move_to_events();
        self.approaches.publish_events(events);
    }
}

impl ApproachSource for Movement {
    fn has_body(&self) -> bool {
        self.player.is_some() && self.content.is_some()
    }

    fn is_airborne(&self) -> bool {
        self.player
            .as_ref()
            .is_some_and(|player| player.controller.is_airborne())
    }

    fn plan(&self, world: &World, guid: u32, use_radius: f32) -> Option<ApproachPlan> {
        let player = self.player.as_ref()?;
        if guid == 0 || self.content.is_none() {
            return None;
        }
        let target = self.approach_position(world, guid)?;
        let here = player.controller.current_cell_position();
        let here = CellPosition::new(
            here.obj_cell_id,
            player.controller.position(),
            here.frame.orientation,
        );
        let shape = self
            .content
            .as_ref()
            .and_then(|content| body_shape_of(world, content, guid))
            .unwrap_or((0.0, 0.0));
        Some(ApproachPlan::new(guid, target, here, use_radius, shape))
    }

    fn begin(&mut self, world: &World, plan: &ApproachPlan) -> Option<ApproachToken> {
        if self.player.is_none() || self.content.is_none() {
            return None;
        }
        // The walk in progress is called off first, and answered as the
        // walk it was.
        {
            let view = self.bodies_view();
            let player = self.player.as_mut()?;
            player
                .controller
                .object_mut()
                .cx(&view)
                .movement_cancel_move_to(WeenieError::ACTION_CANCELLED);
        }
        self.publish_move_to_events();
        self.approaches.generation += 1;
        let token = self.approaches.current()?;
        self.ensure_table_host(world, plan.target_guid);
        let request = MovementStruct {
            kind: if plan.is_within_reach {
                ac_motion::MovementType::TurnToObject
            } else {
                ac_motion::MovementType::MoveToObject
            },
            object_id: plan.target_guid,
            top_level_id: plan.target_guid,
            pos: CellPosition::new(
                plan.player_cell_id,
                plan.target_position.frame.origin,
                Quat::IDENTITY,
            ),
            params: Some(ac_motion::MovementParameters {
                distance_to_object: plan.use_radius,
                can_charge: plan.can_charge,
                ..ac_motion::MovementParameters::default()
            }),
            radius: plan.target_radius,
            height: plan.target_height,
            ..MovementStruct::default()
        };
        let view = self.bodies_view();
        let player = self.player.as_mut()?;
        let c = &mut player.controller;
        c.set_last_move_was_autonomous(false);
        let mut env = MovementEnv::new(&mut self.engine, &view);
        let result = c.perform_movement(&mut env, &request);
        self.deliver_target_messages();
        result.is_none().then_some(token)
    }

    fn stalled_ticks(&self) -> Option<u32> {
        let move_to = &self.player.as_ref()?.controller.object().move_to;
        move_to
            .is_moving_to()
            .then_some(move_to.fail_progress_count)
    }

    fn cancel(&mut self) {
        let view = self.bodies_view();
        if let Some(player) = self.player.as_mut() {
            player
                .controller
                .object_mut()
                .cx(&view)
                .movement_cancel_move_to(WeenieError::ACTION_CANCELLED);
        }
        self.publish_move_to_events();
    }

    fn take_completions(&mut self) -> Vec<ApproachCompletion> {
        std::mem::take(&mut self.approaches.completions)
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Runtime.Tests/Gameplay/RuntimeApproachCompletionStateTests.cs`.

    use super::*;

    fn begin_walk(a: &mut Approaches) -> ApproachToken {
        a.generation += 1;
        a.current().unwrap()
    }

    #[test]
    fn answers_keep_their_order() {
        // CompletionMailbox_PreservesPublicationOrder.
        let mut a = Approaches::default();
        a.begin_lifetime();
        let token = begin_walk(&mut a);
        a.publish_events(vec![
            MoveToEvent::Complete(WeenieError::NONE),
            MoveToEvent::Cancelled(WeenieError::NO_OBJECT),
        ]);
        assert_eq!(
            a.completions,
            [
                ApproachCompletion {
                    token,
                    natural: true
                },
                ApproachCompletion {
                    token,
                    natural: false
                }
            ]
        );
    }

    #[test]
    fn a_retired_body_cannot_answer_for_its_replacement() {
        // RetiredLifetime_CannotPublishIntoReplacementLifetime.
        let mut a = Approaches::default();
        a.begin_lifetime();
        let stale = begin_walk(&mut a);
        a.retire_lifetime();
        assert_eq!(
            std::mem::take(&mut a.completions),
            [ApproachCompletion {
                token: stale,
                natural: false
            }]
        );
        a.begin_lifetime();
        // No walk of the new body yet: nothing is answered.
        a.publish_events(vec![MoveToEvent::Complete(WeenieError::NONE)]);
        assert!(a.completions.is_empty());
        let current = begin_walk(&mut a);
        assert_ne!(current, stale);
        a.publish_events(vec![MoveToEvent::Complete(WeenieError::NONE)]);
        assert_eq!(a.completions.len(), 1);
        assert_eq!(a.completions[0].token, current);
    }

    #[test]
    fn retiring_replaces_a_queued_arrival_with_a_call_off() {
        // Retirement_ReplacesQueuedSuccessWithMatchingCancellation.
        let mut a = Approaches::default();
        a.begin_lifetime();
        let token = begin_walk(&mut a);
        a.publish_events(vec![MoveToEvent::Complete(WeenieError::NONE)]);
        a.retire_lifetime();
        assert_eq!(
            a.completions,
            [ApproachCompletion {
                token,
                natural: false
            }]
        );
    }

    #[test]
    fn a_failed_arrival_is_not_an_arrival() {
        let mut a = Approaches::default();
        a.begin_lifetime();
        begin_walk(&mut a);
        a.publish_events(vec![MoveToEvent::Complete(WeenieError::NO_OBJECT)]);
        assert!(!a.completions[0].natural);
    }
}
