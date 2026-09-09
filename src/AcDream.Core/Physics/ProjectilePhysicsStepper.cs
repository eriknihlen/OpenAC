using System.Numerics;

namespace AcDream.Core.Physics;

public readonly record struct ProjectileCollisionSphere(
    Vector3 SetupLocalOrigin,
    float SetupRadius,
    float ObjectScale = 1f)
{
    public Vector3 LocalOrigin => SetupLocalOrigin * ObjectScale;
    public float Radius => SetupRadius * ObjectScale;
    public bool IsValid
    {
        get
        {
            Vector3 origin = LocalOrigin;
            float radius = Radius;
            return float.IsFinite(SetupRadius) && SetupRadius > 0f
                && float.IsFinite(ObjectScale) && ObjectScale > 0f
                && float.IsFinite(radius) && radius > PhysicsGlobals.EPSILON
                && float.IsFinite(origin.X)
                && float.IsFinite(origin.Y)
                && float.IsFinite(origin.Z);
        }
    }
}

public readonly record struct ProjectileAdvanceResult(
    uint CellId,
    int QuantumCount,
    bool Simulated,
    bool CollisionNormalValid,
    Vector3 CollisionNormal,
    bool TransitionOk);

public readonly record struct ProjectileQuantumPreparation(
    uint CellId,
    float Quantum,
    bool Simulated,
    bool RequiresTransition,
    Vector3 BeginPosition,
    Quaternion BeginOrientation,
    Position BeginCellPosition,
    bool BeginInWorld,
    ProjectileQuantumDynamics BeginDynamics,
    Vector3 CandidatePosition,
    Quaternion CandidateOrientation,
    ProjectileQuantumDynamics CandidateDynamics,
    bool PreviousContact,
    bool PreviousOnWalkable);

public readonly record struct ProjectileQuantumDynamics(
    Vector3 Velocity,
    Vector3 CachedVelocity,
    Vector3 Acceleration,
    Vector3 Omega,
    TransientStateFlags TransientState,
    double LastUpdateTime);

public sealed class ProjectilePhysicsStepper
{
    private readonly PhysicsEngine _physics;

    public ProjectilePhysicsStepper(PhysicsEngine physics)
        => _physics = physics ?? throw new ArgumentNullException(nameof(physics));

    public ProjectileAdvanceResult Advance(
        PhysicsBody body,
        double currentTime,
        uint cellId,
        ProjectileCollisionSphere sphere,
        uint movingEntityId,
        uint designatedTargetId = 0,
        bool isParented = false)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!double.IsFinite(currentTime))
            throw new ArgumentOutOfRangeException(
                nameof(currentTime), currentTime, "The game clock must be finite.");
        if (!double.IsFinite(body.LastUpdateTime))
            throw new InvalidOperationException(
                "The projectile's prior update timestamp must be finite.");

        if (!sphere.IsValid)
            return new ProjectileAdvanceResult(cellId, 0, false, false, default, false);

        if (isParented || !body.InWorld
            || body.State.HasFlag(PhysicsStateFlags.Frozen)
            || body.State.HasFlag(PhysicsStateFlags.Static)
            || cellId == 0)
        {
            body.TransientState &= ~TransientStateFlags.Active;
            return new ProjectileAdvanceResult(cellId, 0, false, false, default, true);
        }

        if (!body.IsActive)
            return new ProjectileAdvanceResult(cellId, 0, false, false, default, true);

        double elapsed = currentTime - body.LastUpdateTime;
        if (elapsed <= PhysicsGlobals.EPSILON || elapsed > PhysicsBody.HugeQuantum)
        {
            body.LastUpdateTime = currentTime;
            return new ProjectileAdvanceResult(cellId, 0, false, false, default, true);
        }

        int quantumCount = 0;
        bool collisionNormalValid = false;
        Vector3 collisionNormal = default;
        bool transitionOk = true;
        double physicsTime = body.LastUpdateTime;

        while (elapsed > PhysicsBody.MaxQuantum)
        {
            physicsTime += PhysicsBody.MaxQuantum;
            StepQuantum(
                body, PhysicsBody.MaxQuantum, ref cellId, sphere,
                movingEntityId, designatedTargetId,
                ref collisionNormalValid, ref collisionNormal, ref transitionOk);
            quantumCount++;
            elapsed -= PhysicsBody.MaxQuantum;
        }

        if (elapsed > PhysicsBody.MinQuantum)
        {
            float quantum = (float)elapsed;
            physicsTime += elapsed;
            StepQuantum(
                body, quantum, ref cellId, sphere,
                movingEntityId, designatedTargetId,
                ref collisionNormalValid, ref collisionNormal, ref transitionOk);
            quantumCount++;
        }

        body.LastUpdateTime = physicsTime;

        return new ProjectileAdvanceResult(
            cellId,
            quantumCount,
            quantumCount != 0,
            collisionNormalValid,
            collisionNormal,
            transitionOk);
    }

    public ProjectileAdvanceResult AdvanceQuantum(
        PhysicsBody body,
        float quantum,
        uint cellId,
        ProjectileCollisionSphere sphere,
        uint movingEntityId,
        uint designatedTargetId = 0,
        bool isParented = false)
    {
        ProjectileQuantumPreparation preparation = BeginQuantum(
            body,
            quantum,
            cellId,
            sphere,
            isParented);
        return CompleteQuantum(
            body,
            preparation,
            sphere,
            movingEntityId,
            designatedTargetId);
    }

    public ProjectileQuantumPreparation BeginQuantum(
        PhysicsBody body,
        float quantum,
        uint cellId,
        ProjectileCollisionSphere sphere,
        bool isParented = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!float.IsFinite(quantum)
            || quantum <= PhysicsGlobals.EPSILON
            || quantum > PhysicsBody.MaxQuantum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantum),
                quantum,
                "An admitted object quantum must be finite and no larger than MaxQuantum.");
        }

        if (!sphere.IsValid)
            return NotSimulated(cellId, quantum);

        if (isParented || !body.InWorld
            || body.State.HasFlag(PhysicsStateFlags.Frozen)
            || body.State.HasFlag(PhysicsStateFlags.Static)
            || cellId == 0)
        {
            body.TransientState &= ~TransientStateFlags.Active;
            return NotSimulated(cellId, quantum);
        }

        if (!body.IsActive)
            return NotSimulated(cellId, quantum);

        Vector3 beginPosition = body.Position;
        Quaternion beginOrientation = body.Orientation;
        Position beginCellPosition = body.CellPosition;
        bool beginInWorld = body.InWorld;
        ProjectileQuantumDynamics beginDynamics = CaptureDynamics(body);
        bool previousContact = body.InContact;
        bool previousOnWalkable = body.OnWalkable;

        body.calc_acceleration();
        body.UpdatePhysicsInternal(quantum);

        Vector3 candidatePosition = body.Position;
        Quaternion candidateOrientation = body.Orientation;
        Vector3 displacement = candidatePosition - beginPosition;
        bool candidateMoved = displacement != Vector3.Zero;
        if (candidateMoved && body.State.HasFlag(PhysicsStateFlags.AlignPath))
        {
            candidateOrientation = RetailFrameMath.SetVectorHeading(
                candidateOrientation,
                displacement);
        }

        if (!candidateMoved)
        {
            body.CachedVelocity = Vector3.Zero;
            body.Orientation = candidateOrientation;
            body.LastUpdateTime += quantum;
        }
        else
        {
            body.Position = beginPosition;
            body.Orientation = beginOrientation;
        }

        ProjectileQuantumDynamics candidateDynamics = CaptureDynamics(body);
        RestoreBeginFrame(
            body,
            beginPosition,
            beginOrientation,
            beginCellPosition,
            beginInWorld);
        ApplyDynamics(body, beginDynamics);

        return new ProjectileQuantumPreparation(
            cellId,
            quantum,
            Simulated: true,
            RequiresTransition: candidateMoved,
            beginPosition,
            beginOrientation,
            beginCellPosition,
            beginInWorld,
            beginDynamics,
            candidatePosition,
            candidateOrientation,
            candidateDynamics,
            previousContact,
            previousOnWalkable);
    }

    public ProjectileAdvanceResult CompleteQuantum(
        PhysicsBody body,
        in ProjectileQuantumPreparation preparation,
        ProjectileCollisionSphere sphere,
        uint movingEntityId,
        uint designatedTargetId = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!preparation.Simulated)
            return new ProjectileAdvanceResult(preparation.CellId, 0, false, false, default, true);
        ApplyDynamics(body, preparation.CandidateDynamics);
        if (!preparation.RequiresTransition)
        {
            body.SetFrameInCurrentCell(
                preparation.CandidatePosition,
                preparation.CandidateOrientation);
            return new ProjectileAdvanceResult(preparation.CellId, 1, true, false, default, true);
        }

        uint cellId = preparation.CellId;
        ObjectInfoState moverFlags = body.State.HasFlag(PhysicsStateFlags.PathClipped)
            ? ObjectInfoState.PathClipped
            : ObjectInfoState.None;
        var resolved = _physics.ResolveWithTransition(
            preparation.BeginPosition,
            preparation.CandidatePosition,
            cellId,
            sphereRadius: sphere.Radius,
            sphereHeight: 0f,
            stepUpHeight: PhysicsGlobals.DefaultStepHeight,
            stepDownHeight: 0f,
            isOnGround: preparation.PreviousOnWalkable,
            body: body,
            moverFlags: moverFlags,
            movingEntityId: movingEntityId,
            localSphereOrigin: sphere.LocalOrigin,
            beginOrientation: preparation.BeginOrientation,
            endOrientation: preparation.CandidateOrientation,
            designatedTargetId: designatedTargetId);

        bool collisionNormalValid = false;
        Vector3 collisionNormal = default;
        bool transitionOk = resolved.Ok;
        if (!resolved.Ok)
        {
            body.SetFrameInCurrentCell(
                preparation.CandidatePosition,
                preparation.CandidateOrientation);
            body.CachedVelocity = Vector3.Zero;
        }
        else
        {
            body.CachedVelocity = (resolved.Position - preparation.BeginPosition)
                / preparation.Quantum;
            body.Position = resolved.Position;
            body.Orientation = resolved.Orientation == default
                ? preparation.CandidateOrientation
                : resolved.Orientation;
            if (resolved.CellId != 0)
                cellId = resolved.CellId;

            PhysicsObjUpdate.ApplySetPositionContact(
                body,
                resolved.InContact,
                resolved.OnWalkable);
            PhysicsObjUpdate.HandleAllCollisions(
                body,
                resolved.CollisionNormalValid,
                resolved.CollisionNormal,
                preparation.PreviousContact,
                preparation.PreviousOnWalkable,
                nowOnWalkable: body.OnWalkable);
            if (resolved.CollisionNormalValid)
            {
                collisionNormalValid = true;
                collisionNormal = resolved.CollisionNormal;
            }
        }

        body.LastUpdateTime += preparation.Quantum;
        return new ProjectileAdvanceResult(
            cellId,
            1,
            true,
            collisionNormalValid,
            collisionNormal,
            transitionOk);
    }

    private static ProjectileQuantumPreparation NotSimulated(
        uint cellId,
        float quantum) => new(
        CellId: cellId,
        Quantum: quantum,
        Simulated: false,
        RequiresTransition: false,
        BeginPosition: default,
        BeginOrientation: default,
        BeginCellPosition: default,
        BeginInWorld: false,
        BeginDynamics: default,
        CandidatePosition: default,
        CandidateOrientation: default,
        CandidateDynamics: default,
        PreviousContact: false,
        PreviousOnWalkable: false);

    private static ProjectileQuantumDynamics CaptureDynamics(PhysicsBody body) => new(
        body.Velocity,
        body.CachedVelocity,
        body.Acceleration,
        body.Omega,
        body.TransientState,
        body.LastUpdateTime);

    private static void ApplyDynamics(
        PhysicsBody body,
        in ProjectileQuantumDynamics dynamics)
    {
        body.Velocity = dynamics.Velocity;
        body.CachedVelocity = dynamics.CachedVelocity;
        body.Acceleration = dynamics.Acceleration;
        body.Omega = dynamics.Omega;
        body.TransientState = dynamics.TransientState;
        body.LastUpdateTime = dynamics.LastUpdateTime;
    }

    private static void RestoreBeginFrame(
        PhysicsBody body,
        Vector3 beginPosition,
        Quaternion beginOrientation,
        in Position beginCellPosition,
        bool beginInWorld)
    {
        body.Orientation = beginOrientation;
        if (beginCellPosition.ObjCellId != 0)
        {
            body.SnapToCell(
                beginCellPosition.ObjCellId,
                beginPosition,
                beginCellPosition.Frame.Origin);
        }
        else
        {
            body.SetFrameInCurrentCell(beginPosition, beginOrientation);
        }
        body.InWorld = beginInWorld;
    }

    private void StepQuantum(
        PhysicsBody body,
        float dt,
        ref uint cellId,
        ProjectileCollisionSphere sphere,
        uint movingEntityId,
        uint designatedTargetId,
        ref bool collisionNormalValid,
        ref Vector3 collisionNormal,
        ref bool transitionOk)
    {
        ProjectileQuantumPreparation preparation = BeginQuantum(
            body,
            dt,
            cellId,
            sphere);
        ProjectileAdvanceResult result = CompleteQuantum(
            body,
            preparation,
            sphere,
            movingEntityId,
            designatedTargetId);
        if (result.CellId != 0)
            cellId = result.CellId;
        collisionNormalValid |= result.CollisionNormalValid;
        if (result.CollisionNormalValid)
            collisionNormal = result.CollisionNormal;
        transitionOk &= result.TransitionOk;
    }
}
