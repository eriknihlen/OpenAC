//! How an accepted server position is carried out: set the body's position
//! outright (a teleport, a forced position, a far object), catch up to it by
//! interpolation (a near object), or nothing (the local player's ordinary
//! updates, an object in the air). Pure: the caller supplies the facts.
//!
//! The authority's structural checks are reduced to the object key: the
//! session generation and position-authority version OpenAC also checks are
//! its cross-thread staging tokens, which a single-owner runtime does not
//! have.

use ac_core::physics::{PositionDisposition, is_newer, is_valid_position};
use ac_physics::PhysicsSetPositionFlags;
use ac_proto::Position;

use crate::world::ObjectKey;

/// Whose position it is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PositionEntityKind {
    /// This client's character.
    LocalPlayer,
    /// Any other object.
    Remote,
    /// A missile.
    Projectile,
}

/// Where a created object is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CreateResidenceKind {
    /// In the world.
    TopLevel,
    /// Held by another object.
    Parented,
    /// In a container.
    PickedUp,
}

/// Where an accepted position came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AcceptedPositionSource {
    /// A position update.
    PositionEvent,
    /// A create of an object already known in this incarnation.
    SameIncarnationCreate,
}

/// What to do with a position.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PositionDispositionRoute {
    /// The update is not authoritative for this object.
    RejectedAuthority,
    /// The update's data is unusable.
    RejectedData,
    /// No position yet (held or contained).
    AwaitFreshPosition,
    /// Accepted, but the body is not moved.
    NoPositionOperation,
    /// Catch up by interpolation.
    Interpolate,
    /// Place the body with the collision sweep (and the teleport hook).
    SetPosition,
    /// Place the body outright.
    SetPositionSimple,
}

/// What kind of placement it is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SetPositionOperationKind {
    /// The character entering the world.
    InitialLogin,
    /// A server position for the character.
    LocalAuthoritative,
    /// A server position for another object.
    RemoteAuthoritative,
    /// A server position for a missile.
    ProjectileAuthoritative,
}

/// When the teleport hook runs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TeleportHookPhase {
    /// Never.
    None,
    /// Before the position is set.
    BeforePositionOperation,
    /// After it.
    AfterPositionOperation,
    /// After entering the world.
    AfterEnterWorld,
}

/// When the server's leash is re-armed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConstrainPhase {
    /// Never.
    None,
    /// Before the position operation.
    BeforePositionOperation,
    /// After it.
    AfterPositionOperation,
}

/// Who accepted the position: the object, the counters before and after.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PositionAuthority {
    /// The object.
    pub entity: ObjectKey,
    /// The teleport counter before this update.
    pub previous_teleport_sequence: u16,
    /// The teleport counter it carries.
    pub accepted_teleport_sequence: u16,
    /// How the update-counter gate accepted it.
    pub timestamp_disposition: PositionDisposition,
}

impl PositionAuthority {
    fn is_structurally_valid(&self) -> bool {
        self.entity.local_id != 0
            && matches!(
                self.timestamp_disposition,
                PositionDisposition::Apply | PositionDisposition::ForcePosition
            )
    }

    /// The teleport counter moved forward (wrap-safe).
    #[must_use]
    pub fn teleport_advanced(&self) -> bool {
        self.timestamp_disposition == PositionDisposition::Apply
            && is_newer(
                self.previous_teleport_sequence,
                self.accepted_teleport_sequence,
            )
    }

    /// The teleport counter went backwards.
    #[must_use]
    pub fn teleport_regressed(&self) -> bool {
        is_newer(
            self.accepted_teleport_sequence,
            self.previous_teleport_sequence,
        )
    }
}

/// A create's position to route.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CreatePositionRouteRequest {
    /// Who.
    pub authority: PositionAuthority,
    /// Whose.
    pub entity_kind: PositionEntityKind,
    /// Where the object is.
    pub residence: CreateResidenceKind,
    /// The position, when in the world.
    pub accepted_position: Option<Position>,
    /// The object's physics state bits.
    pub physics_state: u32,
}

/// An accepted position update to route.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AcceptedPositionRouteRequest {
    /// Who.
    pub authority: PositionAuthority,
    /// Whose.
    pub entity_kind: PositionEntityKind,
    /// Where it came from.
    pub source: AcceptedPositionSource,
    /// The position.
    pub accepted_position: Position,
    /// Its placement frame.
    pub placement_frame: Option<u32>,
    /// The body's committed cell (`None` or 0: not placed).
    pub committed_cell_id: Option<u32>,
    /// The update says the object is on the ground.
    pub has_contact: bool,
    /// Distance from the local player, metres.
    pub player_distance: f32,
    /// The client follows the server's positions for its own character.
    pub use_position_from_server: bool,
    /// The object has animations.
    pub has_animations: bool,
    /// The object's physics state bits.
    pub physics_state: u32,
}

/// The decision.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PositionRoute {
    /// Who.
    pub authority: PositionAuthority,
    /// What to do.
    pub disposition: PositionDispositionRoute,
    /// What kind of placement.
    pub operation_kind: SetPositionOperationKind,
    /// Placement flags.
    pub set_position_flags: PhysicsSetPositionFlags,
    /// Placement frame.
    pub placement_frame: u32,
    /// Detach from a parent first.
    pub unparent_before_routing: bool,
    /// Apply the placement frame first.
    pub apply_placement_frame_before_routing: bool,
    /// Leave the world.
    pub leave_world: bool,
    /// When the teleport hook runs.
    pub teleport_hook_phase: TeleportHookPhase,
    /// Stop interpolating.
    pub stop_interpolating: bool,
    /// When the leash is re-armed.
    pub constrain_phase: ConstrainPhase,
    /// Keep the body's heading.
    pub preserve_heading: bool,
    /// Zero the velocity.
    pub zero_velocity: bool,
    /// Send a position at once.
    pub send_position_immediately: bool,
    /// The body takes part in collision reporting (not hidden).
    pub collision_batch_eligible: bool,
}

impl PositionRoute {
    /// Not rejected.
    #[must_use]
    pub fn accepted(&self) -> bool {
        !matches!(
            self.disposition,
            PositionDispositionRoute::RejectedAuthority | PositionDispositionRoute::RejectedData
        )
    }

    /// Sets the body's position.
    #[must_use]
    pub fn performs_set_position(&self) -> bool {
        matches!(
            self.disposition,
            PositionDispositionRoute::SetPosition | PositionDispositionRoute::SetPositionSimple
        )
    }

    /// Runs the teleport hook.
    #[must_use]
    pub fn runs_teleport_hook(&self) -> bool {
        self.teleport_hook_phase != TeleportHookPhase::None
    }

    /// Re-arms the leash before the position operation.
    #[must_use]
    pub fn constrain_before_routing(&self) -> bool {
        self.constrain_phase == ConstrainPhase::BeforePositionOperation
    }

    /// Re-arms the leash after the position operation.
    #[must_use]
    pub fn constrain_after_routing(&self) -> bool {
        self.constrain_phase == ConstrainPhase::AfterPositionOperation
    }

    fn plain(
        authority: PositionAuthority,
        disposition: PositionDispositionRoute,
        operation_kind: SetPositionOperationKind,
        reporting: bool,
    ) -> Self {
        Self {
            authority,
            disposition,
            operation_kind,
            set_position_flags: PhysicsSetPositionFlags(PhysicsSetPositionFlags::NONE),
            placement_frame: 0,
            unparent_before_routing: false,
            apply_placement_frame_before_routing: false,
            leave_world: false,
            teleport_hook_phase: TeleportHookPhase::None,
            stop_interpolating: false,
            constrain_phase: ConstrainPhase::None,
            preserve_heading: false,
            zero_velocity: false,
            send_position_immediately: false,
            collision_batch_eligible: reporting,
        }
    }
}

/// Objects further than this from the player are placed, not interpolated,
/// metres.
pub const MAX_PHYSICS_DISTANCE: f32 = 96.0;
const HIDDEN: u32 = 0x0000_4000;
const INITIAL_CREATE_FLAGS: u32 =
    PhysicsSetPositionFlags::PLACEMENT | PhysicsSetPositionFlags::SLIDE;
const AUTHORITATIVE_TELEPORT_FLAGS: u32 = PhysicsSetPositionFlags::TELEPORT
    | PhysicsSetPositionFlags::SLIDE
    | PhysicsSetPositionFlags::SEND_POSITION_EVENT;

fn reporting(physics_state: u32) -> bool {
    physics_state & HIDDEN == 0
}

pub(crate) fn valid_position(p: &Position) -> bool {
    let o = p.origin;
    let q = p.orientation;
    [o.x, o.y, o.z, q.w, q.x, q.y, q.z]
        .iter()
        .all(|v| v.is_finite())
        && is_valid_position(p.cell_id, [o.x, o.y, o.z], [q.w, q.x, q.y, q.z])
}

fn operation_kind(kind: PositionEntityKind, initial_create: bool) -> SetPositionOperationKind {
    match kind {
        PositionEntityKind::LocalPlayer if initial_create => SetPositionOperationKind::InitialLogin,
        PositionEntityKind::LocalPlayer => SetPositionOperationKind::LocalAuthoritative,
        PositionEntityKind::Projectile => SetPositionOperationKind::ProjectileAuthoritative,
        PositionEntityKind::Remote => SetPositionOperationKind::RemoteAuthoritative,
    }
}

/// Whether to unparent and apply the placement frame before routing: an
/// ordinary update unparents, and an object without animations takes its
/// placement frame; a forced position does neither.
#[must_use]
pub fn pre_placement_flags(disposition: PositionDisposition, has_animations: bool) -> (bool, bool) {
    match disposition {
        PositionDisposition::Apply => (true, !has_animations),
        PositionDisposition::ForcePosition | PositionDisposition::Rejected => (false, false),
    }
}

/// Route a create's position.
#[must_use]
pub fn classify_create(request: &CreatePositionRouteRequest) -> PositionRoute {
    let authority = request.authority;
    let operation = operation_kind(request.entity_kind, true);
    let report = reporting(request.physics_state);
    let valid_authority = authority.is_structurally_valid()
        && authority.timestamp_disposition == PositionDisposition::Apply
        && authority.previous_teleport_sequence == authority.accepted_teleport_sequence;
    if !valid_authority {
        return PositionRoute::plain(
            authority,
            PositionDispositionRoute::RejectedAuthority,
            SetPositionOperationKind::RemoteAuthoritative,
            false,
        );
    }
    if matches!(
        request.residence,
        CreateResidenceKind::Parented | CreateResidenceKind::PickedUp
    ) {
        return PositionRoute::plain(
            authority,
            PositionDispositionRoute::AwaitFreshPosition,
            operation,
            report,
        );
    }
    if !request
        .accepted_position
        .is_some_and(|p| valid_position(&p))
    {
        return PositionRoute::plain(
            authority,
            PositionDispositionRoute::RejectedData,
            operation,
            report,
        );
    }
    PositionRoute {
        set_position_flags: PhysicsSetPositionFlags(INITIAL_CREATE_FLAGS),
        teleport_hook_phase: if request.entity_kind == PositionEntityKind::LocalPlayer {
            TeleportHookPhase::AfterEnterWorld
        } else {
            TeleportHookPhase::None
        },
        ..PositionRoute::plain(
            authority,
            PositionDispositionRoute::SetPosition,
            operation,
            report,
        )
    }
}

/// Route an accepted position update.
#[must_use]
pub fn classify_accepted_position(request: &AcceptedPositionRouteRequest) -> PositionRoute {
    let authority = request.authority;
    let operation = operation_kind(request.entity_kind, false);
    let report = reporting(request.physics_state);
    let valid_authority = authority.is_structurally_valid()
        && !authority.teleport_regressed()
        && match authority.timestamp_disposition {
            PositionDisposition::Apply => true,
            PositionDisposition::ForcePosition => {
                request.entity_kind == PositionEntityKind::LocalPlayer
            }
            PositionDisposition::Rejected => false,
        };
    if !valid_authority {
        return PositionRoute::plain(
            authority,
            PositionDispositionRoute::RejectedAuthority,
            operation,
            report,
        );
    }
    if !valid_position(&request.accepted_position) {
        return PositionRoute::plain(
            authority,
            PositionDispositionRoute::RejectedData,
            operation,
            report,
        );
    }
    let (unparent, apply_placement) =
        pre_placement_flags(authority.timestamp_disposition, request.has_animations);
    let placement = request.placement_frame.unwrap_or(0);
    let base = |disposition| PositionRoute {
        placement_frame: placement,
        unparent_before_routing: unparent,
        apply_placement_frame_before_routing: apply_placement,
        ..PositionRoute::plain(authority, disposition, operation, report)
    };

    if authority.timestamp_disposition == PositionDisposition::ForcePosition {
        // The forced-position branch comes before unparenting and the
        // placement frame.
        return PositionRoute {
            set_position_flags: PhysicsSetPositionFlags(AUTHORITATIVE_TELEPORT_FLAGS),
            preserve_heading: true,
            send_position_immediately: true,
            ..base(PositionDispositionRoute::SetPositionSimple)
        };
    }

    if request.entity_kind == PositionEntityKind::LocalPlayer {
        if authority.teleport_advanced() {
            return PositionRoute {
                set_position_flags: PhysicsSetPositionFlags(AUTHORITATIVE_TELEPORT_FLAGS),
                teleport_hook_phase: TeleportHookPhase::AfterPositionOperation,
                constrain_phase: ConstrainPhase::AfterPositionOperation,
                zero_velocity: true,
                ..base(PositionDispositionRoute::SetPositionSimple)
            };
        }
        let interpolate = request.use_position_from_server && request.has_contact;
        return PositionRoute {
            constrain_phase: ConstrainPhase::BeforePositionOperation,
            ..base(if interpolate {
                PositionDispositionRoute::Interpolate
            } else {
                PositionDispositionRoute::NoPositionOperation
            })
        };
    }

    let cellless = request.committed_cell_id.is_none_or(|c| c == 0);
    if authority.teleport_advanced() || cellless {
        return PositionRoute {
            set_position_flags: PhysicsSetPositionFlags(AUTHORITATIVE_TELEPORT_FLAGS),
            teleport_hook_phase: TeleportHookPhase::BeforePositionOperation,
            constrain_phase: ConstrainPhase::AfterPositionOperation,
            ..base(PositionDispositionRoute::SetPosition)
        };
    }

    let effective_contact =
        request.source == AcceptedPositionSource::SameIncarnationCreate || request.has_contact;
    if !effective_contact {
        return base(PositionDispositionRoute::NoPositionOperation);
    }
    if !request.player_distance.is_finite() || request.player_distance < 0.0 {
        return PositionRoute::plain(
            authority,
            PositionDispositionRoute::RejectedData,
            operation,
            report,
        );
    }
    let nearby = request.player_distance < MAX_PHYSICS_DISTANCE;
    PositionRoute {
        set_position_flags: PhysicsSetPositionFlags(if nearby {
            PhysicsSetPositionFlags::NONE
        } else {
            AUTHORITATIVE_TELEPORT_FLAGS
        }),
        stop_interpolating: !nearby,
        constrain_phase: ConstrainPhase::AfterPositionOperation,
        ..base(if nearby {
            PositionDispositionRoute::Interpolate
        } else {
            PositionDispositionRoute::SetPositionSimple
        })
    }
}

#[cfg(test)]
#[path = "route_tests.rs"]
mod tests;
