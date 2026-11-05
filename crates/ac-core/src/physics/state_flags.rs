//! The physics state bits an object carries.

use crate::wire_enum::wire_enum;

wire_enum! {
    flags
    /// The persistent physics state of an object: how it collides, whether
    /// it is drawn, whether gravity applies. Sent in object descriptions and
    /// state updates.
    PhysicsState {
        NONE = 0x0000_0000 => "None",
        STATIC = 0x0000_0001 => "Static",
        /// Reserved, never set.
        RESERVED_UNUSED1 = 0x0000_0002 => "ReservedUnused1",
        ETHEREAL = 0x0000_0004 => "Ethereal",
        REPORT_COLLISIONS = 0x0000_0008 => "ReportCollisions",
        IGNORE_COLLISIONS = 0x0000_0010 => "IgnoreCollisions",
        NO_DRAW = 0x0000_0020 => "NoDraw",
        MISSILE = 0x0000_0040 => "Missile",
        PUSHABLE = 0x0000_0080 => "Pushable",
        ALIGN_PATH = 0x0000_0100 => "AlignPath",
        PATH_CLIPPED = 0x0000_0200 => "PathClipped",
        GRAVITY = 0x0000_0400 => "Gravity",
        LIGHTING = 0x0000_0800 => "Lighting",
        PARTICLE_EMITTER = 0x0000_1000 => "ParticleEmitter",
        /// Reserved, never set.
        RESERVED_UNUSED2 = 0x0000_2000 => "ReservedUnused2",
        HIDDEN = 0x0000_4000 => "Hidden",
        SCRIPTED_COLLISION = 0x0000_8000 => "ScriptedCollision",
        /// The object collides against its setup's own collision mesh.
        HAS_PHYSICS_BSP = 0x0001_0000 => "HasPhysicsBsp",
        /// Hitting a wall stops the object instead of bouncing it.
        INELASTIC = 0x0002_0000 => "Inelastic",
        HAS_DEFAULT_ANIM = 0x0004_0000 => "HasDefaultAnim",
        HAS_DEFAULT_SCRIPT = 0x0008_0000 => "HasDefaultScript",
        CLOAKED = 0x0010_0000 => "Cloaked",
        REPORT_AS_ENVIRONMENT = 0x0020_0000 => "ReportAsEnvironment",
        EDGE_SLIDE = 0x0040_0000 => "EdgeSlide",
        SLEDDING = 0x0080_0000 => "Sledding",
        FROZEN = 0x0100_0000 => "Frozen",
    }
}

wire_enum! {
    flags
    /// Per-frame contact state the physics step recomputes; never sent or
    /// saved.
    TransientState {
        NONE = 0x0000_0000 => "None",
        /// Touching any surface.
        CONTACT = 0x0000_0001 => "Contact",
        /// Standing on a walkable surface.
        ON_WALKABLE = 0x0000_0002 => "OnWalkable",
        SLIDING = 0x0000_0004 => "Sliding",
        WATER_CONTACT = 0x0000_0008 => "WaterContact",
        STATIONARY_FALL = 0x0000_0010 => "StationaryFall",
        STATIONARY_STOP = 0x0000_0020 => "StationaryStop",
        STATIONARY_STUCK = 0x0000_0040 => "StationaryStuck",
        /// The object needs a per-frame update.
        ACTIVE = 0x0000_0080 => "Active",
        CHECK_ETHEREAL = 0x0000_0100 => "CheckEthereal",
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Core.Tests/Properties/RetailEnumConformanceTests.cs`
    //! (`TransientStateFlagsMatchesRetail`, `PhysicsStateFlagsMatchesRetail`).

    use super::*;

    const RETAIL_TRANSIENT_STATE: &[(&str, u32)] = &[
        ("Contact", 0x1),
        ("OnWalkable", 0x2),
        ("Sliding", 0x4),
        ("WaterContact", 0x8),
        ("StationaryFall", 0x10),
        ("StationaryStop", 0x20),
        ("StationaryStuck", 0x40),
        ("Active", 0x80),
        ("CheckEthereal", 0x100),
    ];

    const RETAIL_PHYSICS_STATE: &[(&str, u32)] = &[
        ("Static", 0x1),
        ("ReservedUnused1", 0x2),
        ("Ethereal", 0x4),
        ("ReportCollisions", 0x8),
        ("IgnoreCollisions", 0x10),
        ("NoDraw", 0x20),
        ("Missile", 0x40),
        ("Pushable", 0x80),
        ("AlignPath", 0x100),
        ("PathClipped", 0x200),
        ("Gravity", 0x400),
        ("Lighting", 0x800),
        ("ParticleEmitter", 0x1000),
        ("ReservedUnused2", 0x2000),
        ("Hidden", 0x4000),
        ("ScriptedCollision", 0x8000),
        ("HasPhysicsBsp", 0x10000),
        ("Inelastic", 0x20000),
        ("HasDefaultAnim", 0x40000),
        ("HasDefaultScript", 0x80000),
        ("Cloaked", 0x100000),
        ("ReportAsEnvironment", 0x200000),
        ("EdgeSlide", 0x400000),
        ("Sledding", 0x800000),
        ("Frozen", 0x1000000),
    ];

    #[test]
    fn transient_state_matches_retail() {
        for &(name, value) in RETAIL_TRANSIENT_STATE {
            assert_eq!(
                TransientState::from_name(name),
                Some(TransientState(value)),
                "{name}"
            );
        }
    }

    #[test]
    fn physics_state_matches_retail() {
        for &(name, value) in RETAIL_PHYSICS_STATE {
            assert_eq!(
                PhysicsState::from_name(name),
                Some(PhysicsState(value)),
                "{name}"
            );
        }
    }

    #[test]
    fn flag_operations() {
        let mut state = PhysicsState::GRAVITY | PhysicsState::ETHEREAL;
        assert!(state.contains(PhysicsState::GRAVITY));
        assert!(!state.contains(PhysicsState::GRAVITY | PhysicsState::HIDDEN));
        assert!(state.intersects(PhysicsState::GRAVITY | PhysicsState::HIDDEN));
        state.remove(PhysicsState::GRAVITY);
        assert_eq!(state, PhysicsState::ETHEREAL);
        state.set(PhysicsState::HIDDEN, true);
        assert_eq!(state.bits(), 0x4004);
        assert!((state & PhysicsState::NO_DRAW).is_empty());
        // Bits nobody names are kept.
        let unknown = PhysicsState(0x8000_0000) | PhysicsState::STATIC;
        assert_eq!(unknown.bits(), 0x8000_0001);
        assert_eq!(format!("{unknown:?}"), "PhysicsState(0x80000001)");
        assert_eq!(
            format!("{:?}", PhysicsState::FROZEN),
            "PhysicsState::Frozen"
        );
    }
}
