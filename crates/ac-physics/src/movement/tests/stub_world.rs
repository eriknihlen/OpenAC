//! OpenAC `tests/AcDream.Core.Tests/Physics/Motion/PhysicsManagerHarness.cs`
//! (`PhysicsObjHostStub`): objects with target and position managers in a
//! shared world. Calls into one object run against a view of the others;
//! the messages they post are delivered right after, until none are left
//! (OpenAC calls across objects synchronously).

use glam::{Quat, Vec3};

use crate::body::CellPosition;
use crate::movement::{
    ObjectToken, PhysicsObjHost, PositionManager, PositionManagerHost, TargetInfo, TargetManager,
    TargetMessage, TargetOps,
};

#[derive(Debug, Clone)]
pub struct StubObject {
    pub token: ObjectToken,
    pub position: CellPosition,
    pub velocity: Vec3,
    pub radius: f32,
    pub in_contact: bool,
    pub minterp_max_speed: Option<f32>,
    pub cur_time: f64,
    pub physics_timer_time: f64,
    pub resolvable: bool,
    pub target_manager: TargetManager,
    pub target_manager_touched: bool,
    pub position_manager: PositionManager,
    pub handle_update_target_calls: Vec<TargetInfo>,
    pub interrupt_current_movement_calls: i32,
    /// Target reports reach this object's position manager (OpenAC's stub
    /// forwards only once its own `PositionManager` was used; the sticky and
    /// facade tests drive a separate manager and leave it unhooked).
    pub position_manager_hooked: bool,
    outbox: Vec<TargetMessage>,
}

impl StubObject {
    fn new(token: ObjectToken) -> Self {
        Self {
            token,
            position: CellPosition::new(1, Vec3::ZERO, Quat::IDENTITY),
            velocity: Vec3::ZERO,
            radius: 0.5,
            in_contact: true,
            minterp_max_speed: Some(1.0),
            cur_time: 0.0,
            physics_timer_time: 0.0,
            resolvable: true,
            target_manager: TargetManager::default(),
            target_manager_touched: false,
            position_manager: PositionManager::default(),
            handle_update_target_calls: Vec::new(),
            interrupt_current_movement_calls: 0,
            position_manager_hooked: false,
            outbox: Vec::new(),
        }
    }

    pub fn set_origin(&mut self, origin: Vec3) {
        self.position = CellPosition::new(
            self.position.obj_cell_id,
            origin,
            self.position.frame.orientation,
        );
    }

    pub fn advance_clocks(&mut self, seconds: f64) {
        self.cur_time += seconds;
        self.physics_timer_time += seconds;
    }
}

/// One object's view of the world during a call.
pub struct StubCx<'a> {
    obj: &'a mut StubObject,
    before: &'a [StubObject],
    after: &'a [StubObject],
}

impl StubCx<'_> {
    fn all(&self) -> impl Iterator<Item = &StubObject> {
        self.before
            .iter()
            .chain(std::iter::once(&*self.obj))
            .chain(self.after.iter())
    }
}

impl PhysicsObjHost for StubCx<'_> {
    fn obj_token(&self) -> ObjectToken {
        self.obj.token
    }
    fn obj_position(&self) -> CellPosition {
        self.obj.position
    }
    fn obj_velocity(&self) -> Vec3 {
        self.obj.velocity
    }
    fn obj_radius(&self) -> f32 {
        self.obj.radius
    }
    fn obj_in_contact(&self) -> bool {
        self.obj.in_contact
    }
    fn obj_minterp_max_speed(&self) -> Option<f32> {
        self.obj.minterp_max_speed
    }
    fn obj_cur_time(&self) -> f64 {
        self.obj.cur_time
    }
    fn obj_physics_timer_time(&self) -> f64 {
        self.obj.physics_timer_time
    }
    fn target_manager(&mut self) -> &mut TargetManager {
        self.obj.target_manager_touched = true;
        &mut self.obj.target_manager
    }
    fn target_manager_ref(&self) -> &TargetManager {
        &self.obj.target_manager
    }
    /// The newest object with `id` (a later one replaces an earlier one, as
    /// OpenAC's world dictionary does), when it can be resolved.
    fn obj_resolve(&self, id: u32) -> Option<ObjectToken> {
        self.all()
            .filter(|o| o.token.id == id)
            .last()
            .filter(|o| o.resolvable)
            .map(|o| o.token)
    }
    fn obj_position_of(&self, token: ObjectToken) -> Option<CellPosition> {
        self.all().find(|o| o.token == token).map(|o| o.position)
    }
    fn obj_post(&mut self, message: TargetMessage) {
        self.obj.outbox.push(message);
    }
    fn obj_handle_update_target(&mut self, info: TargetInfo) {
        self.obj.handle_update_target_calls.push(info);
        if info.context_id == 0 && self.obj.position_manager_hooked {
            crate::movement::PositionOps::position_handle_update_target(self, info);
        }
    }
    fn obj_interrupt_current_movement(&mut self) {
        self.obj.interrupt_current_movement_calls += 1;
    }
}

impl PositionManagerHost for StubCx<'_> {
    fn position_manager(&mut self) -> &mut PositionManager {
        &mut self.obj.position_manager
    }
    fn position_manager_ref(&self) -> &PositionManager {
        &self.obj.position_manager
    }
}

#[derive(Debug, Default)]
pub struct StubWorld {
    objects: Vec<StubObject>,
    next_incarnation: u64,
}

impl StubWorld {
    /// Add an object (a later one with the same id replaces it in lookups).
    pub fn add(&mut self, id: u32) -> ObjectToken {
        self.next_incarnation += 1;
        let token = ObjectToken {
            id,
            incarnation: self.next_incarnation,
        };
        self.objects.push(StubObject::new(token));
        token
    }

    pub fn get(&self, token: ObjectToken) -> &StubObject {
        self.objects.iter().find(|o| o.token == token).unwrap()
    }

    pub fn get_mut(&mut self, token: ObjectToken) -> &mut StubObject {
        self.objects.iter_mut().find(|o| o.token == token).unwrap()
    }

    /// Run `f` on `token`'s object, then deliver every message posted.
    pub fn with<R>(&mut self, token: ObjectToken, f: impl FnOnce(&mut StubCx<'_>) -> R) -> R {
        let r = self.call(token, f);
        self.pump();
        r
    }

    fn call<R>(&mut self, token: ObjectToken, f: impl FnOnce(&mut StubCx<'_>) -> R) -> R {
        let i = self.objects.iter().position(|o| o.token == token).unwrap();
        let (before, rest) = self.objects.split_at_mut(i);
        let (obj, after) = rest.split_first_mut().unwrap();
        let mut cx = StubCx { obj, before, after };
        f(&mut cx)
    }

    /// Deliver posted messages until none are left.
    pub fn pump(&mut self) {
        loop {
            let mut messages = Vec::new();
            for o in &mut self.objects {
                messages.append(&mut o.outbox);
            }
            if messages.is_empty() {
                return;
            }
            for m in messages {
                let to = m.addressee();
                if self.objects.iter().any(|o| o.token == to) {
                    self.call(to, |cx| cx.deliver_target_message(m));
                }
            }
        }
    }
}
