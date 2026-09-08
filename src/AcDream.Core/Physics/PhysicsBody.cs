using System;
using System.Numerics;

namespace AcDream.Core.Physics;


[Flags]
public enum PhysicsStateFlags : uint
{
    None = 0x00000000,
    Static = 0x00000001,
    ReservedUnused1 = 0x00000002,
    Ethereal = 0x00000004,
    ReportCollisions = 0x00000008,
    IgnoreCollisions = 0x00000010,
    NoDraw = 0x00000020,
    Missile = 0x00000040,
    Pushable = 0x00000080,
    AlignPath = 0x00000100,
    PathClipped = 0x00000200,
    Gravity = 0x00000400,
    Lighting = 0x00000800,
    ParticleEmitter = 0x00001000,
    ReservedUnused2 = 0x00002000,
    Hidden = 0x00004000,
    ScriptedCollision = 0x00008000,
    HasPhysicsBsp = 0x00010000,
    Inelastic = 0x00020000,
    HasDefaultAnim = 0x00040000,
    HasDefaultScript = 0x00080000,
    Cloaked = 0x00100000,
    ReportAsEnvironment = 0x00200000,
    EdgeSlide = 0x00400000,
    Sledding = 0x00800000,
    Frozen = 0x01000000,
}

[Flags]
public enum TransientStateFlags : uint
{
    None = 0,
    Contact = 0x00000001,  // bit 0 — touching any surface
    OnWalkable = 0x00000002,  // bit 1 — standing on a walkable surface
    Sliding = 0x00000004,
    StationaryFall = 0x00000010,  // bit 4 — fsf == 1
    StationaryStop = 0x00000020,  // bit 5 — fsf == 2
    StationaryStuck = 0x00000040,  // bit 6 — fsf == 3
    Active = 0x00000080,  // bit 7 — object needs per-frame update
    WaterContact = 0x00000008,  // bit 3 — WATER_CONTACT_TS
    CheckEthereal = 0x00000100,
}

public sealed class PhysicsBody
{
    public const float MaxVelocity = 50.0f;
    public const float MaxVelocitySquared = MaxVelocity * MaxVelocity;
    public const float Gravity = -9.8f;
    public const float SmallVelocity = 0.25f;
    public const float SmallVelocitySquared = SmallVelocity * SmallVelocity;
    public const float DefaultFriction = 0.95f;
    public const float MinQuantum = 1.0f / 30.0f;   // ~0.0333 s
    public const float MaxQuantum = 0.2f;
    public const float HugeQuantum = 2.0f;            // discard stale dt


    private Vector3 _position;

    public Vector3 Position
    {
        get => _position;
        set
        {
            Vector3 delta = value - _position;
            _position = value;
            SyncCellPositionDelta(delta);
        }
    }

    public Position CellPosition { get; private set; }

    public bool InWorld { get; set; }

    public void SnapToCell(uint cellId, Vector3 worldPos, Vector3 cellLocal)
    {
        StageDormantCellFrame(cellId, worldPos, cellLocal);
        InWorld = true;
    }

    public void StageDormantCellFrame(
        uint cellId,
        Vector3 worldPos,
        Vector3 cellLocal)
    {
        _position = worldPos;
        uint cell = cellId;
        Vector3 local = cellLocal;
        if ((cellId & 0xFFFFu) is >= 1u and <= 0x40u)
            LandDefs.AdjustToOutside(ref cell, ref local);
        CellPosition = new Position(cell, new CellFrame(local, Orientation));
    }

    public void SetFrameInCurrentCell(Vector3 worldPosition, Quaternion orientation)
    {
        Vector3 delta = worldPosition - _position;
        _position = worldPosition;
        Orientation = orientation;

        if (CellPosition.ObjCellId != 0)
        {
            CellPosition = new Position(
                CellPosition.ObjCellId,
                new CellFrame(CellPosition.Frame.Origin + delta, orientation));
        }
    }

    public void CommitTransitionPosition(uint resolvedCellId, Vector3 worldPosition)
    {
        Position = worldPosition;

        if (CellPosition.ObjCellId == 0 || resolvedCellId == 0)
            return;

        uint cell = resolvedCellId;
        Vector3 local = CellPosition.Frame.Origin;
        if ((cell & 0xFFFFu) is >= 1u and <= 0x40u
            && !LandDefs.AdjustToOutside(ref cell, ref local))
        {
            return;
        }

        CellPosition = new Position(
            cell,
            new CellFrame(local, CellPosition.Frame.Orientation));
    }

    private void SyncCellPositionDelta(Vector3 delta)
    {
        uint cell = CellPosition.ObjCellId;
        if (cell == 0)
            return;

        Vector3 local = CellPosition.Frame.Origin + delta;

        if ((cell & 0xFFFFu) is not (>= 1u and <= 0x40u))
        {
            CellPosition = new Position(
                cell,
                new CellFrame(local, CellPosition.Frame.Orientation));
            return;
        }

        uint adjusted = cell;
        if (LandDefs.AdjustToOutside(ref adjusted, ref local))
            CellPosition = new Position(adjusted, new CellFrame(local, CellPosition.Frame.Orientation));
    }

    public Quaternion Orientation { get; set; } = Quaternion.Identity;

    public Vector3 Velocity { get; set; }

    public Vector3 CachedVelocity { get; set; }

    public int FramesStationaryFall { get; set; }

    /// <summary>World-space acceleration (+0xEC/F0/F4).</summary>
    public Vector3 Acceleration { get; set; }

    /// <summary>Angular velocity in radians/s (+0xF8/FC/100).</summary>
    public Vector3 Omega { get; set; }

    public Vector3 GroundNormal { get; set; } = Vector3.UnitZ;

    public Vector3 SlidingNormal { get; set; }


    public bool ContactPlaneValid { get; set; }

    public System.Numerics.Plane ContactPlane { get; set; }

    public uint ContactPlaneCellId { get; set; }

    public bool ContactPlaneIsWater { get; set; }

    /// <summary>Whether the previous walkable polygon is available for edge slide.</summary>
    public bool WalkablePolygonValid { get; set; }

    /// <summary>Most recent walkable polygon plane (world-space).</summary>
    public System.Numerics.Plane WalkablePlane { get; set; }

    /// <summary>Most recent walkable polygon vertices (world-space).</summary>
    public Vector3[]? WalkableVertices { get; set; }

    private Vector3[]? _walkableVertexStorage;

    internal void SetWalkableVerticesExact(ReadOnlySpan<Vector3> source)
    {
        if (_walkableVertexStorage is null
            || _walkableVertexStorage.Length != source.Length)
        {
            _walkableVertexStorage = new Vector3[source.Length];
        }

        source.CopyTo(_walkableVertexStorage);
        WalkableVertices = _walkableVertexStorage;
    }

    internal Vector3[]? RetainedWalkableVertexStorage
        => _walkableVertexStorage;

    /// <summary>Up vector used by the most recent walkable polygon probe.</summary>
    public Vector3 WalkableUp { get; set; } = Vector3.UnitZ;

    public float Elasticity { get; set; } = 0.05f;

    public float Friction { get; set; } = DefaultFriction;

    /// <summary>Physics state flags (+0xA8).</summary>
    public PhysicsStateFlags State { get; set; }
        = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions;

    public TransientStateFlags TransientState { get; set; }

    public double LastUpdateTime { get; set; }

    public bool IsFullyConstrained { get; set; }

    public bool LastMoveWasAutonomous { get; set; }


    public bool HasGravity => (State & PhysicsStateFlags.Gravity) != 0;
    public bool OnWalkable => (TransientState & TransientStateFlags.OnWalkable) != 0;
    public bool IsActive => (TransientState & TransientStateFlags.Active) != 0;
    public bool InContact => (TransientState & TransientStateFlags.Contact) != 0;
    public bool IsWaterContact => (TransientState & TransientStateFlags.WaterContact) != 0;


    public void calc_acceleration()
    {
        if ((TransientState & TransientStateFlags.Contact) != 0 &&
            (TransientState & TransientStateFlags.OnWalkable) != 0 &&
            (State & PhysicsStateFlags.Sledding) == 0)
        {
            Acceleration = Vector3.Zero;
            Omega = Vector3.Zero;
            return;
        }

        if ((State & PhysicsStateFlags.Gravity) != 0)
            Acceleration = new Vector3(0f, 0f, Gravity);
        else
            Acceleration = Vector3.Zero;
    }


    public void set_velocity(Vector3 newVelocity)
    {
        Velocity = newVelocity;

        float mag2 = Velocity.LengthSquared();
        if (mag2 > MaxVelocitySquared)
        {
            Velocity = Vector3.Normalize(Velocity) * MaxVelocity;
        }

        // Set Active flag (bit 7 of TransientState, offset +0xAC).
        TransientState |= TransientStateFlags.Active;
    }


    public void set_local_velocity(Vector3 localVelocity, bool autonomous = false)
    {
        var worldVelocity = Vector3.Transform(localVelocity, Orientation);
        LastMoveWasAutonomous = autonomous;
        set_velocity(worldVelocity);
    }


    public void set_on_walkable(bool isOnWalkable)
    {
        if (isOnWalkable)
            TransientState |= TransientStateFlags.OnWalkable;
        else
            TransientState &= ~TransientStateFlags.OnWalkable;

        calc_acceleration();
    }


    public void calc_friction(float dt, float velocityMag2)
    {
        if ((TransientState & TransientStateFlags.OnWalkable) == 0)
            return;

        float angle = Vector3.Dot(Velocity, GroundNormal);
        if (angle >= 0.25f)
            return;

        Velocity -= angle * GroundNormal;

        float friction = Friction;

        if ((State & PhysicsStateFlags.Sledding) != 0)
        {
            if (velocityMag2 < 1.5625f)        // 1.25² — slow sled
                friction = 1.0f;
            else if (velocityMag2 >= 6.25f && GroundNormal.Z > 0.98480775f)
                friction = 0.2f;
        }

        // Exponential decay: vel *= (1 - friction)^dt
        float scalar = MathF.Pow(1.0f - friction, dt);
        Velocity *= scalar;
    }


    public void UpdatePhysicsInternal(float dt)
    {
        float velocityMag2 = Velocity.LengthSquared();

        if (velocityMag2 <= 0f)
        {
            if ((TransientState & TransientStateFlags.OnWalkable) != 0)
                TransientState &= ~TransientStateFlags.Active;
        }
        else
        {
            if (velocityMag2 > MaxVelocitySquared)
            {
                Velocity = Vector3.Normalize(Velocity) * MaxVelocity;
                velocityMag2 = MaxVelocitySquared;
            }

            calc_friction(dt, velocityMag2);

            if (velocityMag2 - SmallVelocitySquared < 0.0002f)
                Velocity = Vector3.Zero;

            // Euler integration: position += v*dt + 0.5*a*dt²
            Position += Velocity * dt + Acceleration * (0.5f * dt * dt);
        }

        Velocity += Acceleration * dt;

        Vector3 rotation = Omega * dt;
        float rotationLenSq = rotation.LengthSquared();
        if (rotationLenSq >= PhysicsGlobals.EpsilonSq)
        {
            float angle = MathF.Sqrt(rotationLenSq);
            Quaternion deltaRot = Quaternion.CreateFromAxisAngle(rotation / angle, angle);
            Orientation = Quaternion.Normalize(Quaternion.Multiply(deltaRot, Orientation));
        }
    }


    public void update_object(double currentTime)
    {
        double deltaTime = currentTime - LastUpdateTime;

        if (deltaTime <= PhysicsGlobals.EPSILON)
        {
            LastUpdateTime = currentTime;
            return;
        }

        if (deltaTime > HugeQuantum)
        {
            LastUpdateTime = currentTime;
            return;
        }

        double physicsTime = LastUpdateTime;

        while (deltaTime > MaxQuantum)
        {
            physicsTime += MaxQuantum;
            calc_acceleration();
            UpdatePhysicsInternal(MaxQuantum);
            deltaTime -= MaxQuantum;
        }

        // Simulate the remainder
        if (deltaTime > MinQuantum)
        {
            physicsTime += deltaTime;
            calc_acceleration();
            UpdatePhysicsInternal((float)deltaTime);
        }

        LastUpdateTime = physicsTime;
    }
}
