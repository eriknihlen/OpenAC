//! Walking to an object before acting on it: a use of something out of
//! reach walks the character there first and goes out on arrival; a pickup
//! within reach turns to the item first, and one out of reach walks there
//! while the request goes out at once. One owner for every host, so a
//! headless use walks exactly as a windowed one.
//!
//! The walk itself is the character's move-to, run by the movement owner
//! ([`ApproachSource`]); this module arms what is to happen on arrival,
//! and once a frame ([`Inventory::drive_approaches`]) takes the walks that
//! ended: an arrival sends what was armed, a walk called off releases it,
//! and a walk that has stopped getting anywhere is given up on so the
//! one-request-at-a-time gate is not held for the rest of the session.

use ac_core::items::{ClientObject, InventoryRequestKind, ItemPolicyActionKind, UseReservation};
use ac_physics::CellPosition;
use ac_proto::inventory::{build_put_item_in_container, build_use};

use super::{Inventory, ItemContext, Notice};
use crate::world::World;

/// How many ticks in a row an armed walk may fail to get closer before it
/// is given up on (about five seconds of standstill at the physics rate).
pub const STALLED_APPROACH_GIVE_UP_TICKS: u32 = 150;
/// How close the character has to be to an object that never said, metres.
pub const DEFAULT_USE_RADIUS: f32 = 0.6;
/// Beyond this many metres the character may run at what it goes to.
pub const CHARGE_DISTANCE: f32 = 7.5;
/// What the player is told when a walk they asked for is given up on.
pub const APPROACH_NEVER_COMPLETED: &str = "Your approach never completed.";

/// Names one walk: the body it was walked by (a new body after a teleport
/// or re-entry is a new lifetime) and which walk of that body.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct ApproachToken {
    /// The body's lifetime.
    pub lifetime: u64,
    /// The walk within it.
    pub generation: u64,
}

/// How a walk ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ApproachCompletion {
    /// Which walk.
    pub token: ApproachToken,
    /// It arrived (rather than being called off or failing).
    pub natural: bool,
}

/// What getting within reach of an object takes.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ApproachPlan {
    /// The object.
    pub target_guid: u32,
    /// Where it is, in the body's frame.
    pub target_position: CellPosition,
    /// The cell the character stands in.
    pub player_cell_id: u32,
    /// How close the character has to be, metres.
    pub use_radius: f32,
    /// The character is already that close (horizontally).
    pub is_within_reach: bool,
    /// It is far enough off to run at.
    pub can_charge: bool,
    /// How wide the object is, metres.
    pub target_radius: f32,
    /// How tall, metres.
    pub target_height: f32,
}

impl ApproachPlan {
    /// The plan for a target at `target` seen from `player` (both in the
    /// body's frame): within reach and charge measured horizontally.
    #[must_use]
    pub fn new(
        target_guid: u32,
        target: CellPosition,
        player: CellPosition,
        use_radius: f32,
        shape: (f32, f32),
    ) -> Self {
        let dx = target.frame.origin.x - player.frame.origin.x;
        let dy = target.frame.origin.y - player.frame.origin.y;
        let distance_squared = dx * dx + dy * dy;
        Self {
            target_guid,
            target_position: target,
            player_cell_id: player.obj_cell_id,
            use_radius,
            is_within_reach: distance_squared <= use_radius * use_radius,
            can_charge: distance_squared >= CHARGE_DISTANCE * CHARGE_DISTANCE,
            target_radius: shape.0,
            target_height: shape.1,
        }
    }
}

/// The character's body as the approach sees it: planning a walk to an
/// object, starting it, and how it is going.
pub trait ApproachSource: std::fmt::Debug {
    /// The character has a body to walk.
    fn has_body(&self) -> bool;
    /// What reaching `guid` takes (`use_radius` as the object described
    /// it); `None` when it cannot be reached for (no body, no place).
    fn plan(&self, world: &World, guid: u32, use_radius: f32) -> Option<ApproachPlan>;
    /// Call off the body's walk, then start this one (a turn to the object
    /// when within reach, else a walk to it); its token, or `None` when the
    /// body refused.
    fn begin(&mut self, world: &World, plan: &ApproachPlan) -> Option<ApproachToken>;
    /// How many ticks in a row the walk in progress has not got closer;
    /// `None` when no walk is in progress.
    fn stalled_ticks(&self) -> Option<u32>;
    /// Call off the walk in progress.
    fn cancel(&mut self);
    /// Take the walks that ended, oldest first.
    fn take_completions(&mut self) -> Vec<ApproachCompletion>;
    /// The character is in the air (jumping or falling): nothing can be
    /// put on the ground then. A source without a body is never airborne.
    fn is_airborne(&self) -> bool {
        false
    }
}

/// A use armed for a walk's arrival.
#[derive(Debug)]
pub(super) struct PendingUse {
    guid: u32,
    owned: bool,
    useable: bool,
    reservation: UseReservation,
    token: ApproachToken,
    /// A person's own use: giving its walk up is said to the character.
    spoken_for: bool,
}

/// A pickup armed for a turn's end.
#[derive(Debug, Clone, Copy)]
pub(super) struct PendingPickup {
    item: u32,
    incarnation: Option<u16>,
    container: u32,
    placement: i32,
    token: ApproachToken,
    /// The placement's request token (the request it holds meanwhile).
    request: u64,
}

/// The armed use and pickup (at most one of each).
#[derive(Debug, Default)]
pub(super) struct Approaches {
    pending_use: Option<PendingUse>,
    pending_pickup: Option<PendingPickup>,
}

fn incarnation(world: &World, guid: u32) -> Option<u16> {
    world.object(guid).map(|o| o.key().incarnation)
}

impl Inventory {
    /// A use is armed for a walk's arrival.
    #[must_use]
    pub fn has_pending_use(&self) -> bool {
        self.approaches.pending_use.is_some()
    }

    /// A pickup is armed for a turn's end.
    #[must_use]
    pub fn has_pending_pickup(&self) -> bool {
        self.approaches.pending_pickup.is_some()
    }

    /// The use radius `guid` described, else the default.
    fn approach_use_radius(&self, guid: u32) -> f32 {
        self.use_radii
            .get(&guid)
            .copied()
            .filter(|r| *r > 0.0)
            .unwrap_or(DEFAULT_USE_RADIUS)
    }

    /// The newest request supersedes what was armed before it: the armed
    /// pickup is dropped (its placement called off) and the armed use gives
    /// its busy count back.
    pub(super) fn cancel_pending_approach(&mut self) {
        if let Some(pickup) = self.approaches.pending_pickup.take() {
            self.cancel_placement(pickup.request);
        }
        if let Some(mut pending) = self.approaches.pending_use.take() {
            let mut tx = Vec::new();
            self.transactions
                .cancel_use_before_dispatch(&mut pending.reservation, &mut tx);
        }
    }

    /// A person's use of an object: within reach (or with nothing to walk)
    /// the use goes out now; out of reach the character walks there first
    /// and the use goes out on arrival. Counted busy from here on either
    /// way. False when nothing was sent or armed.
    pub(super) fn request_use(&mut self, object: u32, ctx: &mut ItemContext<'_>) -> bool {
        self.request_use_for(object, true, ctx)
    }

    /// [`Inventory::request_use`] for a person (`spoken_for`: a walk given
    /// up is said to the character) or for a plugin. A plugin's use goes
    /// out at once wherever the object stands: the server walks the
    /// character there (to a portal's spot, else within the object's use
    /// radius) and uses it on arrival, which is what plugins expect. A walk
    /// of the client's own can stall short of an object it cannot get close
    /// enough to and is given up on without a word, so the same object
    /// would fail every time.
    pub(super) fn request_use_for(
        &mut self,
        object: u32,
        spoken_for: bool,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let mut tx = Vec::new();
        let mut reservation = self.transactions.begin_use_request_reservation(&mut tx);
        let owned = self.is_owned_by_player(object, ctx);
        let useable = owned
            || ctx
                .world
                .item(object)
                .and_then(|i| i.useability)
                .is_some_and(|u| u.is_useable());
        if !spoken_for {
            return self.dispatch_reserved_use(object, owned, useable, reservation, ctx);
        }
        self.cancel_pending_approach();
        let use_radius = self.approach_use_radius(object);
        if useable && let Some(source) = ctx.approach.as_deref_mut() {
            let plan = source.plan(ctx.world, object, use_radius);
            if let Some(plan) = plan.filter(|p| !p.is_within_reach) {
                let Some(token) = source.begin(ctx.world, &plan) else {
                    self.transactions
                        .cancel_use_before_dispatch(&mut reservation, &mut tx);
                    return false;
                };
                self.approaches.pending_use = Some(PendingUse {
                    guid: object,
                    owned,
                    useable,
                    reservation,
                    token,
                    spoken_for,
                });
                return true;
            }
        }
        self.dispatch_reserved_use(object, owned, useable, reservation, ctx)
    }

    /// Send a use whose busy count `reservation` holds: refused (and the
    /// count given back) outside the world or for what is neither the
    /// player's nor useable.
    fn dispatch_reserved_use(
        &mut self,
        object: u32,
        owned: bool,
        useable: bool,
        mut reservation: UseReservation,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let mut tx = Vec::new();
        if object == 0 || (!owned && !useable) || !ctx.send(|seq| build_use(seq, object)) {
            self.transactions
                .cancel_use_before_dispatch(&mut reservation, &mut tx);
            return false;
        }
        self.transactions.mark_use_dispatched(&mut reservation);
        self.interaction.note_use_dispatched(object, 0);
        self.arm_landscape_container_request(object, ctx);
        true
    }

    /// The use went out: when it is the kind that opens a container on the
    /// ground (a chest, a corpse), the client waits for that one.
    fn arm_landscape_container_request(&mut self, object: u32, ctx: &mut ItemContext<'_>) {
        let Some(item) = ctx.world.item(object).cloned() else {
            return;
        };
        let mut input = self.use_input(&item, ctx, false, None);
        // Asked as the use goes out: the request it is part of holds the
        // gate, so the gate is not what decides this.
        input.ready_for_inventory_request = true;
        input.confirm_volatile_rare_uses = true;
        let decision = ac_core::items::interaction::decide_use(&input);
        if decision
            .actions
            .iter()
            .any(|a| a.kind == ItemPolicyActionKind::SetGroundObject && a.object_id == object)
        {
            // The click that sent this use has already said why a locked
            // container stays unviewed.
            self.attempt_set_ground_object(object, false, ctx);
        }
    }

    /// A person's pickup of a loose item into `container`, its placement
    /// reserved as `token`: one in the open ground container (or worn by
    /// the player) goes now; anything else that may not be picked up
    /// ([`super::pickup::refusal`]) is refused with the reason shown; one
    /// in the world within reach waits for the
    /// character to turn to it (the placement holding the request
    /// meanwhile); one out of reach goes now while the character walks
    /// there. Whatever does not go out or get armed calls the placement
    /// off.
    pub(super) fn send_pickup(
        &mut self,
        item: u32,
        container: u32,
        placement: i32,
        token: u64,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        let armed = self.arm_or_send_pickup(item, container, placement, token, ctx);
        if !armed {
            self.cancel_placement(token);
        }
        armed
    }

    fn arm_or_send_pickup(
        &mut self,
        item: u32,
        container: u32,
        placement: i32,
        request_token: u64,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        self.cancel_pending_approach();
        if !ctx.active {
            return false;
        }
        let contained = self.is_contained_pickup(item, ctx);
        // Anything else is checked before the character moves for it: a
        // creature, something stuck or another's, is refused in the game's
        // words and nothing is sent.
        if !contained && self.refuse_pickup(item, ctx) {
            return false;
        }
        let body = ctx
            .approach
            .as_deref()
            .is_some_and(ApproachSource::has_body);
        if contained || !body {
            return self.dispatch_pickup(item, container, placement, request_token, ctx);
        }
        let use_radius = self.approach_use_radius(item);
        let Some(source) = ctx.approach.as_deref_mut() else {
            return false;
        };
        let Some(plan) = source.plan(ctx.world, item, use_radius) else {
            return false;
        };
        let token = source.begin(ctx.world, &plan);
        if plan.is_within_reach {
            let Some(token) = token else {
                return false;
            };
            self.approaches.pending_pickup = Some(PendingPickup {
                item,
                incarnation: incarnation(ctx.world, item),
                container,
                placement,
                token,
                request: request_token,
            });
            return true;
        }
        self.dispatch_pickup(item, container, placement, request_token, ctx)
    }

    /// What lies in the open ground container or is worn by the player:
    /// its pickup goes out unchecked.
    fn is_contained_pickup(&self, item: u32, ctx: &ItemContext<'_>) -> bool {
        let player = ctx.player();
        let ground = self.external.current_container_id();
        ctx.world.item(item).is_some_and(|i: &ClientObject| {
            (ground != 0 && i.container_id == ground) || i.wielder_id == player
        })
    }

    /// Whether `item` may not be picked up ([`super::pickup::refusal`]);
    /// when so, the player is told why.
    fn refuse_pickup(&mut self, item: u32, ctx: &ItemContext<'_>) -> bool {
        let owned = self.is_owned_by_player(item, ctx);
        let Some(refusal) = super::pickup::refusal(ctx.world, item, owned) else {
            return false;
        };
        self.notices.push(Notice::Interface(refusal));
        true
    }

    /// The pick-up key on `item`: what may not be picked up is refused
    /// before a pack is chosen for it, so a creature is refused as one
    /// whatever room the packs have; anything else is placed in the pack
    /// (whose pickup asks again, as the key's placement does).
    pub(super) fn pick_up(&mut self, item: u32, ctx: &mut ItemContext<'_>) -> bool {
        if item != 0 && !self.is_contained_pickup(item, ctx) && self.refuse_pickup(item, ctx) {
            return false;
        }
        self.place_world_item_in_backpack(item, ctx)
    }

    /// Send the pickup whose placement reserved `token`.
    fn dispatch_pickup(
        &mut self,
        item: u32,
        container: u32,
        placement: i32,
        token: u64,
        ctx: &mut ItemContext<'_>,
    ) -> bool {
        self.send_placement(token, InventoryRequestKind::Pickup, item, ctx, |ctx| {
            ctx.send(|seq| build_put_item_in_container(seq, item, container, placement))
        })
    }

    /// Once a frame, after the character's step: the walks that ended send
    /// or release what was armed for them, and a walk that has stopped
    /// getting anywhere is given up on.
    pub fn drive_approaches(&mut self, ctx: &mut ItemContext<'_>) {
        let Some(source) = ctx.approach.as_deref_mut() else {
            return;
        };
        let completions = source.take_completions();
        for completion in completions {
            self.resolve_completion(completion, ctx);
        }
        self.expire_stalled_approach(ctx);
    }

    fn resolve_completion(&mut self, completion: ApproachCompletion, ctx: &mut ItemContext<'_>) {
        if let Some(pickup) = self
            .approaches
            .pending_pickup
            .filter(|p| p.token == completion.token)
        {
            self.approaches.pending_pickup = None;
            let current = ctx.world.item(pickup.item).is_some()
                && incarnation(ctx.world, pickup.item) == pickup.incarnation;
            if completion.natural && current {
                self.dispatch_pickup(
                    pickup.item,
                    pickup.container,
                    pickup.placement,
                    pickup.request,
                    ctx,
                );
            } else {
                self.cancel_placement(pickup.request);
            }
            return;
        }
        let Some(mut pending) = self
            .approaches
            .pending_use
            .take_if(|p| p.token == completion.token)
        else {
            return;
        };
        if !completion.natural {
            let mut tx = Vec::new();
            self.transactions
                .cancel_use_before_dispatch(&mut pending.reservation, &mut tx);
            return;
        }
        // Range is not checked again: the walk's own arrival rule allows
        // for how wide the bodies are, a centre distance would not.
        if !self.dispatch_reserved_use(
            pending.guid,
            pending.owned,
            pending.useable,
            pending.reservation,
            ctx,
        ) && !ctx.active
        {
            self.notices
                .push(Notice::Interface("Not in world".to_owned()));
        }
    }

    fn expire_stalled_approach(&mut self, ctx: &mut ItemContext<'_>) {
        let Some(source) = ctx.approach.as_deref_mut() else {
            return;
        };
        let Some(stalled) = source.stalled_ticks() else {
            return;
        };
        if stalled < STALLED_APPROACH_GIVE_UP_TICKS {
            return;
        }
        if let Some(mut pending) = self.approaches.pending_use.take() {
            source.cancel();
            let mut tx = Vec::new();
            self.transactions
                .cancel_use_before_dispatch(&mut pending.reservation, &mut tx);
            // A plugin's use is answered with its code and says nothing.
            if pending.spoken_for {
                self.notices
                    .push(Notice::Interface(APPROACH_NEVER_COMPLETED.to_owned()));
            }
            return;
        }
        if let Some(pickup) = self.approaches.pending_pickup.take() {
            source.cancel();
            self.cancel_placement(pickup.request);
            self.notices
                .push(Notice::Interface(APPROACH_NEVER_COMPLETED.to_owned()));
        }
    }

    /// An object left the world: what was armed for it is dropped.
    pub(super) fn forget_approach_target(&mut self, guid: u32) {
        if let Some(pickup) = self.approaches.pending_pickup.take_if(|p| p.item == guid) {
            self.cancel_placement(pickup.request);
        }
        if let Some(mut pending) = self.approaches.pending_use.take_if(|p| p.guid == guid) {
            let mut tx = Vec::new();
            self.transactions
                .cancel_use_before_dispatch(&mut pending.reservation, &mut tx);
        }
    }

    /// Session end: nothing is armed.
    pub(super) fn reset_approaches(&mut self) {
        self.approaches = Approaches::default();
    }
}
