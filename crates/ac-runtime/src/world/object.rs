//! One object in the world, as the server last described its physics and
//! appearance, and the rules for applying a server update to it. The
//! object's description (name, item type, properties, containment) is kept
//! by the item table.

use ac_core::physics::{PhysicsTimestampGate, PhysicsTimestamps};
use ac_proto::{
    CreateObject, ModelData, MovementData, PhysicsAttachment, PhysicsDesc, PhysicsMovement,
    Position, Vector3,
};

use super::presentation::{MotionView, StepBlend, accepted_motion_state};

/// Stable identity of an object for presentation: a local id the client
/// gives the object on first sight, plus the incarnation (the server's
/// instance counter). A re-created object keeps its local id and gets a new
/// incarnation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct ObjectKey {
    /// Client-side id, unique for the session.
    pub local_id: u32,
    /// The server's instance counter for this object.
    pub incarnation: u16,
}

/// An object the client knows about.
#[derive(Debug, Clone)]
pub struct WorldObject {
    pub(super) key: ObjectKey,
    pub(super) guid: u32,
    pub(super) gate: PhysicsTimestampGate,
    pub(super) physics: PhysicsDesc,
    pub(super) model: ModelData,
    /// Where the session's physics has the object now (the local player's
    /// body, a remote object's simulated body), when it simulates it.
    pub(super) live: Option<Position>,
    /// What a window blends to draw the object between the fixed steps the
    /// session's physics moves it in (the local player); read only beside
    /// `live`.
    pub(super) step_blend: Option<StepBlend>,
    /// What the session's physics has the object doing now, when it
    /// simulates it (its motion interpreter's axes and ground contact).
    pub(super) live_motion: Option<MotionView>,
    /// How many motion records the server's updates have set (each
    /// accepted one counts, whether or not it changed anything).
    pub(super) motions_accepted: u32,
    /// Its entry into the world found no clear spot: the object is known
    /// but not in the world (not drawn) for the rest of this incarnation.
    pub(super) entry_refused: bool,
}

impl WorldObject {
    pub(super) fn new(key: ObjectKey, create: &CreateObject, gate: PhysicsTimestampGate) -> Self {
        Self {
            key,
            guid: create.guid,
            gate,
            physics: create.physics.clone(),
            model: create.model.clone(),
            live: None,
            step_blend: None,
            live_motion: None,
            motions_accepted: 0,
            entry_refused: false,
        }
    }

    /// Presentation identity.
    #[must_use]
    pub fn key(&self) -> ObjectKey {
        self.key
    }

    /// The server's object id.
    #[must_use]
    pub fn guid(&self) -> u32 {
        self.guid
    }

    /// Where the object is: its simulated body when the session's physics
    /// moves it, else the last position the server sent; `None` while it is
    /// held by a parent or in a container.
    #[must_use]
    pub fn position(&self) -> Option<&Position> {
        self.physics
            .position
            .as_ref()
            .and(self.live.as_ref())
            .or(self.physics.position.as_ref())
    }

    /// What a window blends to draw the object at its own frame's time,
    /// for a body the session's physics moves in fixed steps (the local
    /// player); `None` otherwise, and while the physics does not move it.
    #[must_use]
    pub fn step_blend(&self) -> Option<&StepBlend> {
        self.physics
            .position
            .as_ref()
            .and(self.live.as_ref())
            .and(self.step_blend.as_ref())
    }

    /// Whether its entry into the world found no clear spot (see
    /// [`World::refuse_entry`](super::World::refuse_entry)).
    #[must_use]
    pub fn entry_refused(&self) -> bool {
        self.entry_refused
    }

    /// The last position the server sent (`None` while held or contained).
    #[must_use]
    pub fn accepted_position(&self) -> Option<&Position> {
        self.physics.position.as_ref()
    }

    /// The parent holding this object, if any.
    #[must_use]
    pub fn parent(&self) -> Option<&PhysicsAttachment> {
        self.physics.parent.as_ref()
    }

    /// Physics fields as accepted so far.
    #[must_use]
    pub fn physics(&self) -> &PhysicsDesc {
        &self.physics
    }

    /// Appearance changes.
    #[must_use]
    pub fn model(&self) -> &ModelData {
        &self.model
    }

    /// The accepted update counters.
    #[must_use]
    pub fn timestamps(&self) -> PhysicsTimestamps {
        self.gate.stamps()
    }

    /// The current motion, when the server sent one.
    #[must_use]
    pub fn movement(&self) -> Option<&MovementData> {
        match &self.physics.movement {
            Some(PhysicsMovement::Present { data, .. }) => Some(data),
            _ => None,
        }
    }

    /// What the object is doing for presentation: the live interpreter's
    /// axes and contact when the session's physics moves it (with the
    /// server's actions), else the server's interpreted state on the
    /// ground; `None` when neither is known.
    #[must_use]
    pub fn motion_view(&self) -> Option<MotionView> {
        let accepted = self.movement().and_then(accepted_motion_state);
        match (&self.live_motion, accepted) {
            (Some(live), accepted) => {
                let mut view = live.clone();
                view.state.actions = accepted.and_then(|a| a.actions);
                Some(view)
            }
            (None, Some(state)) => Some(MotionView {
                state,
                serial: self.motions_accepted,
                grounded: true,
                standing_long_jump: self
                    .movement()
                    .is_some_and(MovementData::standing_long_jump),
                body: None,
            }),
            (None, None) => None,
        }
    }

    /// Whether the object has animations (a motion table).
    #[must_use]
    pub fn has_animations(&self) -> bool {
        self.physics.motion_table_id.is_some_and(|id| id != 0)
    }

    /// Apply an accepted appearance change.
    pub(super) fn apply_model(&mut self, model: &ModelData) {
        self.model = model.clone();
    }

    /// Apply an accepted position.
    pub(super) fn apply_position(
        &mut self,
        position: Position,
        velocity: Option<Vector3>,
        placement: Option<u32>,
        install_placement: bool,
        clear_parent: bool,
    ) {
        self.physics.position = Some(position);
        if install_placement {
            self.physics.animation_frame = placement;
        }
        self.physics.velocity = velocity;
        if clear_parent {
            self.physics.parent = None;
        }
    }

    /// The object left the world into a container: no position, no parent.
    pub(super) fn apply_pickup(&mut self) {
        self.physics.position = None;
        self.live = None;
        self.step_blend = None;
        self.live_motion = None;
        self.physics.parent = None;
    }

    /// The object is attached to a parent.
    pub(super) fn apply_parent(&mut self, parent_guid: u32, location: u32, placement: u32) {
        self.physics.position = None;
        self.live = None;
        self.step_blend = None;
        self.live_motion = None;
        self.physics.parent = Some(PhysicsAttachment {
            guid: parent_guid,
            location_id: location,
        });
        self.physics.animation_frame = Some(placement);
    }

    /// Apply accepted motion.
    pub(super) fn apply_movement(&mut self, data: &MovementData, is_autonomous: bool) {
        self.physics.movement = Some(PhysicsMovement::Present {
            raw: Vec::new(),
            data: data.clone(),
            is_autonomous,
        });
        self.motions_accepted = self.motions_accepted.wrapping_add(1);
    }
}
