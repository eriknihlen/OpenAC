//! The small enums a collision query reads and returns.

/// Outcome of one collision test of a moving sphere.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum TransitionState {
    /// Not decided.
    #[default]
    Invalid = 0,
    /// No contact; the step may go ahead as it is.
    Ok = 1,
    /// Blocked.
    Collided = 2,
    /// The sphere was moved (pushed out, stepped down, landed); test again.
    Adjusted = 3,
    /// The sphere was redirected along a surface.
    Slid = 4,
}

/// Why a sphere is being inserted into the world.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum InsertType {
    /// An ordinary movement step.
    #[default]
    Transition = 0,
    /// Placing an object where it must not overlap anything.
    Placement = 1,
    /// The first placement of an object (login, teleport arrival).
    InitialPlacement = 2,
}

/// State bits of the object being moved.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct ObjectInfoState(pub u32);

impl ObjectInfoState {
    /// No bits.
    pub const NONE: u32 = 0x000;
    /// Standing on something.
    pub const CONTACT: u32 = 0x001;
    /// Standing on something walkable.
    pub const ON_WALKABLE: u32 = 0x002;
    /// The camera.
    pub const IS_VIEWER: u32 = 0x004;
    /// Stops at the first polygon it touches instead of sliding.
    pub const PATH_CLIPPED: u32 = 0x008;
    /// Turns freely.
    pub const FREE_ROTATE: u32 = 0x010;
    /// With [`Self::PATH_CLIPPED`]: stops exactly at the touching point.
    pub const PERFECT_CLIP: u32 = 0x040;
    /// Cannot be pushed through.
    pub const IS_IMPENETRABLE: u32 = 0x080;
    /// A player.
    pub const IS_PLAYER: u32 = 0x100;
    /// Slides along edges instead of falling off them.
    pub const EDGE_SLIDE: u32 = 0x200;
    /// Passes through creatures.
    pub const IGNORE_CREATURES: u32 = 0x400;
    /// A player killer.
    pub const IS_PK: u32 = 0x800;
    /// A player killer lite.
    pub const IS_PK_LITE: u32 = 0x1000;
    /// May enter restricted places.
    pub const CAN_BYPASS_MOVE_RESTRICTIONS: u32 = 0x2000;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u32) -> bool {
        self.0 & bits == bits
    }

    /// True when any bit of `bits` is set.
    #[must_use]
    pub fn intersects(self, bits: u32) -> bool {
        self.0 & bits != 0
    }

    /// Sets `bits`.
    pub fn insert(&mut self, bits: u32) {
        self.0 |= bits;
    }

    /// Clears `bits`.
    pub fn remove(&mut self, bits: u32) {
        self.0 &= !bits;
    }
}
