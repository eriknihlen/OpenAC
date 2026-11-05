//! Target tracking between objects: a watcher subscribes to a target
//! ("voyeur"), the target sends the watcher its position whenever it has
//! drifted more than the watcher's radius from the last one sent (checked at
//! most twice a second), and lifecycle events (left the world, teleported).
//! Move-to and sticky follow use it to know where their target is.
//!
//! An object reaches other objects only through its host: it asks the world
//! which incarnation an id names and where an incarnation is now, and posts
//! [`TargetMessage`]s for the other object's manager, which the owner of all
//! objects delivers (OpenAC calls the other object directly).

use glam::Vec3;

use crate::body::CellPosition;
use crate::slot_map::SlotMap;
use ac_motion::move_to_math::normalize_check_small;

/// One incarnation of an object: the same guid reused later is a different
/// token.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct ObjectToken {
    /// Object id (guid).
    pub id: u32,
    /// Which incarnation.
    pub incarnation: u64,
}

/// What a target update reports.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
#[repr(u32)]
pub enum TargetStatus {
    /// Not yet heard from.
    #[default]
    Undefined = 0,
    /// Tracked normally.
    Ok = 1,
    /// Left the world.
    ExitWorld = 2,
    /// Teleported.
    Teleported = 3,
    /// Picked up into a container.
    Contained = 4,
    /// Attached to a parent (wielded, mounted).
    Parented = 5,
    /// No update arrived in time.
    TimedOut = 6,
}

/// A target's report to its watcher.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct TargetInfo {
    /// The target.
    pub object_id: u32,
    /// What happened.
    pub status: TargetStatus,
    /// Where the target is.
    pub target_position: CellPosition,
    /// Where it will be after the quantum (dead reckoned).
    pub interpolated_position: CellPosition,
    /// Request context (0 for movement).
    pub context_id: u32,
    /// Drift radius that triggers an update, metres.
    pub radius: f32,
    /// Dead-reckoning look-ahead, seconds.
    pub quantum: f64,
    /// Unit direction from the watcher to the interpolated position.
    pub interpolated_heading: Vec3,
    /// The target's velocity.
    pub velocity: Vec3,
    /// When the watcher last heard, seconds.
    pub last_update_time: f64,
}

impl TargetInfo {
    /// An update for `object_id` with `status` and positions; other fields 0.
    #[must_use]
    pub fn new(
        object_id: u32,
        status: TargetStatus,
        target_position: CellPosition,
        interpolated_position: CellPosition,
    ) -> Self {
        Self {
            object_id,
            status,
            target_position,
            interpolated_position,
            ..Self::default()
        }
    }
}

/// A subscription a target keeps for one watcher.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TargettedVoyeurInfo {
    /// The watcher (exact incarnation).
    pub watcher: ObjectToken,
    /// Dead-reckoning look-ahead, seconds.
    pub quantum: f64,
    /// Drift radius, metres.
    pub radius: f32,
    /// The position last sent.
    pub last_sent_position: CellPosition,
}

/// A request for another object's target manager.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum TargetMessage {
    /// `watcher` subscribes to `target`.
    AddVoyeur {
        /// Addressee.
        target: ObjectToken,
        /// Subscriber.
        watcher: ObjectToken,
        /// Drift radius, metres.
        radius: f32,
        /// Look-ahead, seconds.
        quantum: f64,
    },
    /// `watcher` unsubscribes from `target`.
    RemoveVoyeur {
        /// Addressee.
        target: ObjectToken,
        /// Subscriber.
        watcher: ObjectToken,
    },
    /// `sender` reports to its watcher.
    ReceiveTargetUpdate {
        /// Addressee.
        watcher: ObjectToken,
        /// The target reporting.
        sender: ObjectToken,
        /// The report.
        info: TargetInfo,
    },
}

impl TargetMessage {
    /// The object the message is for.
    #[must_use]
    pub fn addressee(&self) -> ObjectToken {
        match *self {
            Self::AddVoyeur { target, .. } | Self::RemoveVoyeur { target, .. } => target,
            Self::ReceiveTargetUpdate { watcher, .. } => watcher,
        }
    }
}

/// What the target, sticky, constraint and position managers reach through
/// their object.
pub trait PhysicsObjHost {
    /// This object.
    fn obj_token(&self) -> ObjectToken;
    /// Where it is (cell, world origin, orientation).
    fn obj_position(&self) -> CellPosition;
    /// Its velocity.
    fn obj_velocity(&self) -> Vec3;
    /// Its radius, metres.
    fn obj_radius(&self) -> f32;
    /// Standing on something.
    fn obj_in_contact(&self) -> bool;
    /// The interpreter's adjusted top speed, when it has an interpreter.
    fn obj_minterp_max_speed(&self) -> Option<f32>;
    /// Game time, seconds.
    fn obj_cur_time(&self) -> f64;
    /// Physics timer, seconds.
    fn obj_physics_timer_time(&self) -> f64;
    /// Its target manager.
    fn target_manager(&mut self) -> &mut TargetManager;
    /// Its target manager, read-only.
    fn target_manager_ref(&self) -> &TargetManager;
    /// The live incarnation `id` names, when it can be targeted.
    fn obj_resolve(&self, id: u32) -> Option<ObjectToken>;
    /// Where an exact incarnation is now.
    fn obj_position_of(&self, token: ObjectToken) -> Option<CellPosition>;
    /// Queue a request for another object.
    fn obj_post(&mut self, message: TargetMessage);
    /// A target report arrived (movement and sticky follow listen).
    fn obj_handle_update_target(&mut self, info: TargetInfo);
    /// Cancel the move-to in progress.
    fn obj_interrupt_current_movement(&mut self);
}

/// An object's target subscription (as watcher) and its watchers (as
/// target).
#[derive(Debug, Clone, Default)]
pub struct TargetManager {
    target_info: Option<TargetInfo>,
    target: Option<ObjectToken>,
    voyeur_table: Option<SlotMap<TargettedVoyeurInfo>>,
    last_update_time: f64,
}

impl TargetManager {
    /// Watchers are checked at most this often, seconds.
    pub const THROTTLE_SECONDS: f64 = 0.5;
    /// A target not heard from in this long times out, seconds.
    pub const STALENESS_SECONDS: f64 = 10.0;

    /// The subscription, when watching.
    #[must_use]
    pub fn target_info(&self) -> Option<&TargetInfo> {
        self.target_info.as_ref()
    }

    /// The watchers by watcher id, in subscription order.
    #[must_use]
    pub fn voyeurs(&self) -> Option<Vec<(u32, TargettedVoyeurInfo)>> {
        self.voyeur_table
            .as_ref()
            .map(|t| t.iter().map(|(k, v)| (k, *v)).collect())
    }

    /// Whether `watcher_id` watches this object.
    #[must_use]
    pub fn has_voyeur(&self, watcher_id: u32) -> bool {
        self.voyeur_table
            .as_ref()
            .is_some_and(|t| t.contains_key(watcher_id))
    }

    /// The look-ahead of the subscription (0 when not watching).
    #[must_use]
    pub fn get_target_quantum(&self) -> f64 {
        self.target_info.map_or(0.0, |i| i.quantum)
    }

    /// The exact incarnation watched as `object_id`.
    #[must_use]
    pub fn get_relationship_target(&self, object_id: u32) -> Option<ObjectToken> {
        match self.target_info {
            Some(info) if info.object_id == object_id => self.target,
            _ => None,
        }
    }
}

/// Target operations, on any [`PhysicsObjHost`].
pub trait TargetOps: PhysicsObjHost {
    /// Watch `object_id` (0 reports a time-out at once and watches nothing).
    fn set_target(&mut self, context_id: u32, object_id: u32, radius: f32, quantum: f64) {
        self.clear_target();
        if object_id == 0 {
            let cleared = TargetInfo {
                object_id: 0,
                status: TargetStatus::TimedOut,
                context_id,
                ..TargetInfo::default()
            };
            self.obj_handle_update_target(cleared);
            return;
        }
        let now = self.obj_cur_time();
        let target = self.obj_resolve(object_id);
        let me = self.obj_token();
        let tm = self.target_manager();
        tm.target_info = Some(TargetInfo {
            object_id,
            status: TargetStatus::Undefined,
            context_id,
            radius,
            quantum,
            last_update_time: now,
            ..TargetInfo::default()
        });
        tm.target = target;
        if let Some(target) = target {
            self.obj_post(TargetMessage::AddVoyeur {
                target,
                watcher: me,
                radius,
                quantum,
            });
        }
    }

    /// Change the look-ahead and re-subscribe with it.
    fn set_target_quantum(&mut self, quantum: f64) {
        let Some(info) = self.target_manager_ref().target_info else {
            return;
        };
        let target = self
            .target_manager_ref()
            .target
            .or_else(|| self.obj_resolve(info.object_id));
        let me = self.obj_token();
        let tm = self.target_manager();
        tm.target_info = Some(TargetInfo { quantum, ..info });
        tm.target = target;
        if let Some(target) = target {
            self.obj_post(TargetMessage::AddVoyeur {
                target,
                watcher: me,
                radius: info.radius,
                quantum,
            });
        }
    }

    /// Stop watching.
    fn clear_target(&mut self) {
        let Some(info) = self.target_manager_ref().target_info else {
            return;
        };
        let target = self
            .target_manager_ref()
            .target
            .or_else(|| self.obj_resolve(info.object_id));
        if let Some(target) = target {
            let me = self.obj_token();
            self.obj_post(TargetMessage::RemoveVoyeur {
                target,
                watcher: me,
            });
        }
        let tm = self.target_manager();
        tm.target_info = None;
        tm.target = None;
    }

    /// The watched target reported: accept it only from the exact incarnation
    /// watched, record it with the direction to it, pass it on, and stop
    /// watching a target that left the world.
    fn receive_update(&mut self, update: TargetInfo, sender: ObjectToken) {
        let tm = self.target_manager_ref();
        let Some(info) = tm.target_info else {
            return;
        };
        if info.object_id != update.object_id || tm.target != Some(sender) {
            return;
        }
        let mut heading =
            update.interpolated_position.frame.origin - self.obj_position().frame.origin;
        if normalize_check_small(&mut heading) {
            heading = Vec3::Z;
        }
        let updated = TargetInfo {
            radius: update.radius,
            quantum: update.quantum,
            target_position: update.target_position,
            interpolated_position: update.interpolated_position,
            velocity: update.velocity,
            status: update.status,
            interpolated_heading: heading,
            last_update_time: self.obj_cur_time(),
            ..info
        };
        self.target_manager().target_info = Some(updated);
        self.obj_handle_update_target(updated);
        if update.status == TargetStatus::ExitWorld {
            self.clear_target();
        }
    }

    /// `watcher` subscribes: an existing subscription by the same incarnation
    /// is updated in place (nothing sent); otherwise it replaces any older
    /// incarnation's and the watcher is sent the current position at once.
    fn add_voyeur(&mut self, watcher: ObjectToken, radius: f32, quantum: f64) {
        let tm = self.target_manager();
        let table = tm.voyeur_table.get_or_insert_with(SlotMap::default);
        if let Some(existing) = table.get_mut(watcher.id) {
            if existing.watcher == watcher {
                existing.radius = radius;
                existing.quantum = quantum;
                return;
            }
            table.remove(watcher.id);
        }
        let voyeur = TargettedVoyeurInfo {
            watcher,
            quantum,
            radius,
            last_sent_position: CellPosition::default(),
        };
        table.insert(watcher.id, voyeur);
        let pos = self.obj_position();
        self.send_voyeur_update(watcher.id, pos, TargetStatus::Ok);
    }

    /// `watcher_id` unsubscribes, when `expected` is the incarnation that
    /// subscribed. Returns whether it was removed.
    fn remove_voyeur(&mut self, watcher_id: u32, expected: ObjectToken) -> bool {
        let tm = self.target_manager();
        let Some(table) = tm.voyeur_table.as_mut() else {
            return false;
        };
        match table.get(watcher_id) {
            Some(v) if v.watcher == expected => table.remove(watcher_id).is_some(),
            _ => false,
        }
    }

    /// Per-update check (throttled to twice a second): time out a target
    /// never heard from in 10 s, and send watchers the position when it has
    /// drifted past their radius.
    fn handle_targetting(&mut self) {
        if self.obj_physics_timer_time() - self.target_manager_ref().last_update_time
            < TargetManager::THROTTLE_SECONDS
        {
            return;
        }
        if let Some(info) = self.target_manager_ref().target_info
            && info.status == TargetStatus::Undefined
            && info.last_update_time + TargetManager::STALENESS_SECONDS < self.obj_cur_time()
        {
            let timed_out = TargetInfo {
                status: TargetStatus::TimedOut,
                ..info
            };
            self.target_manager().target_info = Some(timed_out);
            self.obj_handle_update_target(timed_out);
        }
        if let Some(ids) = self
            .target_manager_ref()
            .voyeur_table
            .as_ref()
            .map(|t| t.keys().collect::<Vec<_>>())
        {
            for id in ids {
                self.check_and_update_voyeur(id);
            }
        }
        let now = self.obj_physics_timer_time();
        self.target_manager().last_update_time = now;
    }

    /// Send watcher `watcher_id` the dead-reckoned position when it has
    /// drifted past the watcher's radius from the last one sent.
    fn check_and_update_voyeur(&mut self, watcher_id: u32) {
        let Some(voyeur) = self
            .target_manager_ref()
            .voyeur_table
            .as_ref()
            .and_then(|t| t.get(watcher_id).copied())
        else {
            return;
        };
        let new_pos = self.get_interpolated_position(voyeur.quantum);
        let drift = new_pos
            .frame
            .origin
            .distance(voyeur.last_sent_position.frame.origin);
        if drift > voyeur.radius {
            self.send_voyeur_update(watcher_id, new_pos, TargetStatus::Ok);
        }
    }

    /// Where this object will be `quantum` seconds ahead at its velocity.
    fn get_interpolated_position(&self, quantum: f64) -> CellPosition {
        let pos = self.obj_position();
        let origin = pos.frame.origin + self.obj_velocity() * quantum as f32;
        CellPosition::new(pos.obj_cell_id, origin, pos.frame.orientation)
    }

    /// Send watcher `watcher_id` this object's position, `pos` as the
    /// dead-reckoned one, and `status`.
    fn send_voyeur_update(&mut self, watcher_id: u32, pos: CellPosition, status: TargetStatus) {
        let me = self.obj_token();
        let here = self.obj_position();
        let velocity = self.obj_velocity();
        let tm = self.target_manager();
        let Some(voyeur) = tm.voyeur_table.as_mut().and_then(|t| t.get_mut(watcher_id)) else {
            return;
        };
        voyeur.last_sent_position = pos;
        let info = TargetInfo {
            object_id: me.id,
            status,
            target_position: here,
            interpolated_position: pos,
            context_id: 0,
            radius: voyeur.radius,
            quantum: voyeur.quantum,
            velocity,
            ..TargetInfo::default()
        };
        let watcher = voyeur.watcher;
        self.obj_post(TargetMessage::ReceiveTargetUpdate {
            watcher,
            sender: me,
            info,
        });
    }

    /// Tell every watcher about an event (no drift check).
    fn notify_voyeur_of_event(&mut self, status: TargetStatus) {
        let Some(ids) = self
            .target_manager_ref()
            .voyeur_table
            .as_ref()
            .map(|t| t.keys().collect::<Vec<_>>())
        else {
            return;
        };
        let pos = self.obj_position();
        for id in ids {
            self.send_voyeur_update(id, pos, status);
        }
    }

    /// Tell every watcher about an event, then drop them all.
    fn notify_voyeur_of_event_and_clear(&mut self, status: TargetStatus) {
        if self.target_manager_ref().voyeur_table.is_none() {
            return;
        }
        self.notify_voyeur_of_event(status);
        if let Some(t) = self.target_manager().voyeur_table.as_mut() {
            *t = SlotMap::default();
        }
    }

    /// Deliver a message addressed to this object.
    fn deliver_target_message(&mut self, message: TargetMessage) {
        match message {
            TargetMessage::AddVoyeur {
                watcher,
                radius,
                quantum,
                ..
            } => self.add_voyeur(watcher, radius, quantum),
            TargetMessage::RemoveVoyeur { watcher, .. } => {
                self.remove_voyeur(watcher.id, watcher);
            }
            TargetMessage::ReceiveTargetUpdate { sender, info, .. } => {
                self.receive_update(info, sender);
            }
        }
    }
}

impl<T: PhysicsObjHost + ?Sized> TargetOps for T {}
