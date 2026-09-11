using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics.Motion;

namespace AcDream.Core.Physics;


/// <summary>Internal queue node. type=1 = Position waypoint (only kind we use).</summary>
internal sealed class InterpolationNode
{
    public uint CellId;
    public Vector3 TargetPosition;
    public Quaternion TargetOrientation = Quaternion.Identity;
}

public readonly record struct InterpolationRecoveryTarget(
    Vector3 Position,
    Quaternion Orientation,
    uint CellId,
    long Version);

public sealed class InterpolationManager
{

    /// <summary>Maximum waypoints held before oldest (head) is dropped.</summary>
    public const int QueueCap = 20;

    public const float MaxInterpolatedVelocityMod = 2.0f;

    public const float MaxInterpolatedVelocity = 7.5f;

    public const float MinDistanceToReachPosition = 0.20f;

    public const float DesiredDistance = 0.05f;

    public const int StallCheckFrameInterval = 5;

    public const float StallProgressMinFraction = 0.30f;

    public const int StallFailCountThreshold = 3;

    public const float AutonomyBlipDistance = 100.0f;

    public const float OriginalDistanceSentinel = 999999f;

    private const float FEpsilon = 0.0002f;


    private readonly LinkedList<InterpolationNode> _queue = new();   // position_queue

    private int   _frameCounter      = 0;                            // frame_counter
    private float _progressQuantum   = 0f;                           // progress_quantum (sum of dt)
    private float _originalDistance  = OriginalDistanceSentinel;     // original_distance
    private int   _failCount         = 0;                            // node_fail_counter
    private long _version;
    private InterpolationNode? _recoveryTarget;
    private Vector3 _lastBodyPosition;
    private bool  _keepHeading;                                      // keep_heading

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>True when the queue holds at least one waypoint.</summary>
    public bool IsActive => _queue.Count > 0;

    internal int Count => _queue.Count;

    public (int Depth, int FailCount) DiagnosticInterpolationState
        => (_queue.Count, _failCount);

    public void Clear()
    {
        _version++;
        _recoveryTarget = null;
        _queue.Clear();
        _frameCounter     = 0;
        _progressQuantum  = 0f;
        _originalDistance = OriginalDistanceSentinel;
        _failCount        = 0;
    }

    public Quaternion? Enqueue(
        Vector3 targetPosition,
        float heading,
        bool isMovingTo,
        Vector3? currentBodyPosition = null,
        uint targetCellId = 0,
        float autonomyBlipDistance = AutonomyBlipDistance)
        => Enqueue(
            targetPosition,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, heading),
            isMovingTo,
            currentBodyPosition,
            currentBodyOrientation: null,
            targetCellId,
            autonomyBlipDistance);

    public Quaternion? Enqueue(
        Vector3 targetPosition,
        Quaternion targetOrientation,
        bool isMovingTo,
        Vector3? currentBodyPosition = null,
        Quaternion? currentBodyOrientation = null,
        uint targetCellId = 0,
        float autonomyBlipDistance = AutonomyBlipDistance)
    {
        _version++;
        Vector3 reference;
        bool haveTail = _queue.Last is { } tail;
        if (haveTail)
        {
            reference = _queue.Last!.Value.TargetPosition;
        }
        else if (currentBodyPosition.HasValue)
        {
            reference = currentBodyPosition.Value;
        }
        else
        {
            reference = targetPosition; // dist = 0 → near branch
        }

        float dist = Vector3.Distance(reference, targetPosition);

        if (dist > autonomyBlipDistance)
        {
            // The far branch does not assign keep_heading from arg3. It uses
            // the manager's existing flag when storing this Position.
            EnqueueRaw(
                targetPosition,
                StoreTargetOrientation(
                    targetOrientation,
                    currentBodyOrientation,
                    _keepHeading),
                targetCellId);
            _failCount = StallFailCountThreshold + 1;
            return null;
        }

        if (currentBodyPosition.HasValue)
        {
            float bodyDist = Vector3.Distance(currentBodyPosition.Value, targetPosition);
            if (bodyDist <= DesiredDistance)
            {
                Clear();
                return isMovingTo
                    ? null
                    : MoveToMath.SetHeading(
                        targetOrientation,
                        MoveToMath.GetHeading(targetOrientation));
            }
        }

        while (_queue.Last is { } stale &&
               Vector3.Distance(stale.Value.TargetPosition, targetPosition) < DesiredDistance)
        {
            _queue.RemoveLast();
        }

        while (_queue.Count >= QueueCap)
            _queue.RemoveFirst();

        // 3. Append.
        _keepHeading = isMovingTo;
        EnqueueRaw(
            targetPosition,
            StoreTargetOrientation(
                targetOrientation,
                currentBodyOrientation,
                _keepHeading),
            targetCellId);
        return null;
    }

    private void EnqueueRaw(
        Vector3 target,
        Quaternion targetOrientation,
        uint targetCellId)
    {
        _queue.AddLast(new InterpolationNode
        {
            CellId = targetCellId,
            TargetPosition = target,
            TargetOrientation = targetOrientation,
        });
    }

    private static Quaternion StoreTargetOrientation(
        Quaternion targetOrientation,
        Quaternion? currentBodyOrientation,
        bool keepHeading)
    {
        if (!keepHeading || currentBodyOrientation is not { } current)
            return targetOrientation;

        return MoveToMath.SetHeading(
            targetOrientation,
            MoveToMath.GetHeading(current));
    }

    public Vector3 AdjustOffset(
        double dt,
        Vector3 currentBodyPosition,
        float maxSpeedFromMinterp,
        bool inContact = true,
        bool isSticky = false)
    {
        if (!inContact)
            return Vector3.Zero;

        InterpolationStep step = ComputeStep(
            dt,
            currentBodyPosition,
            maxSpeedFromMinterp,
            isSticky);
        return step.Overwrites ? step.WorldOrigin : Vector3.Zero;
    }

    public bool AdjustOffset(
        double dt,
        Vector3 currentBodyPosition,
        Quaternion currentBodyOrientation,
        float maxSpeedFromMinterp,
        MotionDeltaFrame offset,
        bool inContact = true,
        bool isSticky = false)
    {
        ArgumentNullException.ThrowIfNull(offset);
        if (!inContact)
            return false;

        InterpolationStep step = ComputeStep(
            dt,
            currentBodyPosition,
            maxSpeedFromMinterp,
            isSticky);
        if (!step.Overwrites)
            return false;

        offset.Origin = MoveToMath.GlobalToLocalVec(
            currentBodyOrientation,
            step.WorldOrigin);
        offset.Orientation = _keepHeading
            ? Quaternion.Identity
            : FrameOps.SetRotate(
                offset.Origin,
                Quaternion.Identity,
                Quaternion.Inverse(currentBodyOrientation)
                * step.TargetOrientation);
        return true;
    }

    public bool TryGetRecoveryTarget(out InterpolationRecoveryTarget target)
    {
        target = default;
        if (_failCount <= StallFailCountThreshold
            && (_queue.Count != 0 || _failCount == 0))
            return false;

        InterpolationNode? node = _queue.Last?.Value ?? _recoveryTarget;
        if (node is null)
            return false;

        target = new(node.TargetPosition, node.TargetOrientation, node.CellId, _version);
        PhysicsDiagnostics.LogRemoteSlideStallSnap(
            _failCount, StallFailCountThreshold, _queue.Count,
            _lastBodyPosition, node.TargetPosition,
            Vector3.Distance(_lastBodyPosition,
                _queue.First?.Value.TargetPosition ?? node.TargetPosition));
        return true;
    }

    public bool CompleteRecovery(InterpolationRecoveryTarget target)
    {
        if (target.Version != _version)
            return false;
        Clear();
        return true;
    }

    private InterpolationStep ComputeStep(
        double dt,
        Vector3 currentBodyPosition,
        float maxSpeedFromMinterp,
        bool isSticky)
    {
        // Invalid time must never poison the body's position.
        if (dt <= 0 || double.IsNaN(dt))
            return default;

        _lastBodyPosition = currentBodyPosition;
        if (_queue.First is null)
            return default;

        var head = _queue.First.Value;
        float dist = Vector3.Distance(head.TargetPosition, currentBodyPosition);
        if (dist < DesiredDistance)
        {
            NodeCompleted(succeeded: true, currentBodyPosition);
            return default;
        }

        float scaled = maxSpeedFromMinterp * MaxInterpolatedVelocityMod;
        float catchUp = scaled < FEpsilon ? MaxInterpolatedVelocity : scaled;
        _progressQuantum += (float)dt;
        _frameCounter++;

        if (_frameCounter >= StallCheckFrameInterval)
        {
            float progress = _originalDistance - dist;
            if (!isSticky && !(progress >= FEpsilon
                && progress / _progressQuantum / catchUp >= StallProgressMinFraction))
            {
                bool reached = dist < MinDistanceToReachPosition;
                if (!reached)
                    _failCount++;
                NodeCompleted(reached, currentBodyPosition);
                return default;
            }

            _frameCounter = 0;
            _progressQuantum = 0f;
            _originalDistance = dist;
        }

        float step = Math.Min(catchUp * (float)dt, dist);
        Vector3 delta = ((head.TargetPosition - currentBodyPosition) / dist) * step;
        return new InterpolationStep(true, delta, head.TargetOrientation);
    }
    private readonly record struct InterpolationStep(
        bool Overwrites,
        Vector3 WorldOrigin,
        Quaternion TargetOrientation);

    private void NodeCompleted(bool succeeded, Vector3 currentBodyPosition)
    {
        _version++;
        _frameCounter = 0;
        _progressQuantum = 0f;
        InterpolationNode? completed = _queue.First?.Value;
        if (completed is not null)
            _queue.RemoveFirst();

        if (_queue.First is { } next)
        {
            _originalDistance = Vector3.Distance(next.Value.TargetPosition, currentBodyPosition);
        }
        else
        {
            _originalDistance = OriginalDistanceSentinel;
            if (succeeded)
                Clear();
            else
                _recoveryTarget = completed;
        }
    }
}
