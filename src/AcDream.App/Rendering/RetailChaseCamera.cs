using System;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;

namespace AcDream.App.Rendering;

public sealed class RetailChaseCamera : ICamera
{
    private const float RetailDefaultBack = 2.5f;
    private const float RetailDefaultUp = 0.75f;
    private const float RetailLookDownBack = 2f;
    private const float RetailMapBack = 450f;
    private const float RetailFirstPersonForward = 0.18f;
    private const float RetailLeaveHeadBack = 0.6f;
    private const float RetailLeaveHeadUp = 0.5f;
    private const float RetailInHeadDirectionStep = 0.200000003f;
    private const float RetailInHeadDirectionLimit = 0.800000012f;

    internal const float OffsetScalePerAdjustment = 0.200000003f;
    internal const float OffsetAngleRadians = 8f * MathF.PI / 180f;
    internal const float MinimumOffsetLength = 0.5f;
    internal const float MaximumHorizontalComponent = 10f;
    internal const float MaximumVerticalComponent = 450f;
    internal const float MinimumVerticalComponent = -1.8f;

    // Floor below which a velocity, normal or projection is treated as zero for tilt purposes.
    internal const float MinTiltComponent = 2e-4f;

    // Scales the normalized average velocity's Z for the airborne heading frame's forward axis.
    internal const float AirbornePlaneVerticalScale = 0.1f;

    // ICamera surface.
    public Vector3   Position   { get; private set; }

    public uint      ViewerCellId { get; private set; }
    public float     Aspect     { get; set; } = 16f / 9f;
    public float     FovY       { get; set; } = RetailFieldOfView.DefaultAppliedFovY;
    public Matrix4x4 View       { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 Projection =>
        Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, 0.1f, 5000f);

    // ── Public tunables (per-instance) ──────────────────────────────

    public float Distance { get; set; } = 2.61f;

    public float Pitch { get; set; } = 0.291f;

    public float YawOffset { get; set; } = 0f;

    public float PivotHeight { get; set; } = 1.5f;

    private bool _lookingDown;
    private bool _mapMode;
    private bool _inHead;
    private bool _savedInHead;
    private float _savedDistance;
    private float _savedPitch;
    private float _savedYawOffset;
    private Vector3? _targetDirectionLocal;
    private Vector3? _savedTargetDirectionLocal;

    public bool IsLookingDown => _lookingDown;
    public bool IsMapMode => _mapMode;
    public bool IsInHead => _inHead;

    public ICameraCollisionProbe? CollisionProbe { get; init; }

    public float PlayerTranslucency { get; private set; }

    private const float SnapEpsilon     = 0.000199999995f * 2f;
    private const float RotCloseEpsilon = 0.000199999995f;


    private readonly Vector3[] _velocityRing = new Vector3[5];
    private int     _velocityCount;
    private Vector3 _soughtEye;
    private Vector3 _publishedEye;
    private Vector3 _dampedForward = new(1f, 0f, 0f);
    private bool    _initialised;

    // Mouse-filter state — shared by FilterMouseDelta entrypoint.
    private float _lastMouseDeltaX;
    private float _lastMouseDeltaY;
    private float _lastFilterTimeSec;

    // ── Per-frame entry point ────────────────────────────────────────

    public void Update(
        Vector3 playerPosition,
        float playerYaw,
        Vector3 playerVelocity,
        bool inContact,
        Vector3 contactPlaneNormal,
        float dt,
        uint cellId = 0,
        uint selfEntityId = 0,
        Vector3? trackedTargetPoint = null,
        bool directOrbit = false)
    {
        Vector3 pivotWorld = playerPosition + new Vector3(0f, 0f, PivotHeight);
        Vector3? trackedHeading = directOrbit
            ? null : ComputeTrackedHeading(pivotWorld, trackedTargetPoint);

        // Look-down, map mode and a wide orbit see the flat facing; the head view always tilts.
        bool alignmentApplies = CameraDiagnostics.AlignToSlope
            && !directOrbit
            && !_lookingDown
            && !_mapMode
            && (_inHead
                || (trackedHeading is null
                    && IsOrbitWithinAlignmentThreshold(Distance, Pitch, YawOffset)));

        // The velocity ring only advances on updates that align the heading to the plane.
        if (alignmentApplies)
            PushVelocity(_velocityRing, ref _velocityCount, playerVelocity);
        Vector3 avgVel = AverageVelocity(_velocityRing, _velocityCount);

        // Look-down and map mode bake the orbit into the heading; their pose takes no yaw.
        float headingYaw = !_inHead && _targetDirectionLocal is not null
            ? playerYaw + YawOffset
            : playerYaw;

        Vector3 heading = directOrbit
            ? new Vector3(MathF.Cos(headingYaw), MathF.Sin(headingYaw), 0f)
            : trackedHeading
            ?? ComputeHeading(
                avgVel,
                headingYaw,
                inContact,
                contactPlaneNormal,
                alignmentApplies);

        (Vector3 targetEye, Vector3 targetForward) = _inHead
            ? ComputeInHeadPose(pivotWorld, heading, _targetDirectionLocal)
            : _targetDirectionLocal is { } localDirection
            ? ComputeTargetDirectionPose(
                pivotWorld, heading, Distance, Pitch, localDirection)
            : ComputeDesiredPose(
                pivotWorld, heading, Distance, Pitch, YawOffset);

        if (directOrbit || !_initialised)
        {
            _soughtEye     = targetEye;
            _publishedEye  = targetEye;
            _dampedForward = targetForward;
            _initialised   = true;
        }
        else
        {
            // Retail's Camera Stiffness sets rotation only in first person (CameraSet::SetStiffness).
            float tAlpha = _inHead ? 1f : ComputeDampingAlpha(CameraDiagnostics.TranslationStiffness, dt);
            float rAlpha = ComputeDampingAlpha(CameraDiagnostics.RotationStiffness, dt);
            Vector3 candidateEye     = Vector3.Lerp(_publishedEye, targetEye, tAlpha);
            Vector3 candidateForward = Vector3.Normalize(Vector3.Lerp(_dampedForward, targetForward, rAlpha));

            (_soughtEye, _dampedForward, _) =
                ApplyConvergenceSnap(_publishedEye, _dampedForward, candidateEye, candidateForward);
        }

        Vector3 publishedEye = _soughtEye;
        ViewerCellId = cellId;
        if (CameraDiagnostics.CollideCamera && CollisionProbe is not null)
        {
            var swept = CollisionProbe.SweepEye(pivotWorld, _soughtEye, cellId, selfEntityId, playerPosition);
            publishedEye = swept.Eye;
            ViewerCellId = swept.ViewerCellId;
            if (swept.ViewerCellId == 0)
            {
                _soughtEye = swept.Eye;
                ViewerCellId = cellId;
            }
        }
        _publishedEye = publishedEye;

        if (directOrbit && !_inHead && _targetDirectionLocal is null)
        {
            Vector3 toPivot = pivotWorld - publishedEye;
            if (toPivot.LengthSquared() > 0.000001f)
                _dampedForward = Vector3.Normalize(toPivot);
        }

        Position = publishedEye;
        View     = Matrix4x4.CreateLookAt(publishedEye, publishedEye + _dampedForward, new Vector3(0f, 0f, 1f));

        float d = Vector3.Distance(publishedEye, pivotWorld);
        PlayerTranslucency = ComputeTranslucency(d);
    }

    public void ResetViewerToPlayer(Vector3 playerPosition, float playerYaw)
    {
        Vector3 playerForward = new(MathF.Cos(playerYaw), MathF.Sin(playerYaw), 0f);

        _publishedEye = playerPosition;
        _soughtEye = playerPosition;
        _dampedForward = playerForward;
        _initialised = true;

        Array.Clear(_velocityRing, 0, _velocityRing.Length);
        _velocityCount = 0;

        Position = playerPosition;
        ViewerCellId = 0u;
        View = Matrix4x4.CreateLookAt(
            playerPosition,
            playerPosition + playerForward,
            Vector3.UnitZ);
        PlayerTranslucency = 0f;
    }

    public void AdjustDistance(float adjustment)
    {
        ExitLookDownForAdjustment();
        if (!float.IsFinite(adjustment) || adjustment == 0f)
            return;

        if (_inHead && adjustment > 0f)
        {
            _inHead = false;
            _targetDirectionLocal = null;
            SetViewerOffset(RetailLeaveHeadBack, RetailLeaveHeadUp);
            return;
        }

        float scale = 1f + adjustment * OffsetScalePerAdjustment;
        if (!(scale > 0f) || !float.IsFinite(scale))
            return;

        float candidateDistance = Distance * scale;
        if (adjustment < 0f && !(candidateDistance > MinimumOffsetLength))
            return;

        TryWriteViewerOffset(candidateDistance, Pitch, preserveHorizontalSign: false);
    }

    public void AdjustPitch(float adjustment)
    {
        ExitLookDownForAdjustment();
        if (!float.IsFinite(adjustment) || adjustment == 0f)
            return;

        if (_inHead)
        {
            Vector3 direction = _targetDirectionLocal ?? Vector3.UnitY;
            direction.Z = Math.Clamp(
                direction.Z + adjustment * RetailInHeadDirectionStep,
                -RetailInHeadDirectionLimit,
                RetailInHeadDirectionLimit);
            _targetDirectionLocal = direction;
            return;
        }

        float candidatePitch = Pitch + adjustment * OffsetAngleRadians;
        TryWriteViewerOffset(Distance, candidatePitch, preserveHorizontalSign: true);
    }

    public void AdjustYaw(float adjustment)
    {
        ExitLookDownForAdjustment();
        if (!float.IsFinite(adjustment) || adjustment == 0f)
            return;

        if (_inHead)
        {
            _inHead = false;
            SetViewerOffset(RetailLeaveHeadBack, RetailLeaveHeadUp);
        }

        YawOffset += adjustment * OffsetAngleRadians;
    }

    public void SetRetailDefaultView()
    {
        _lookingDown = false;
        _mapMode = false;
        _inHead = false;
        _targetDirectionLocal = null;
        YawOffset = 0f;
        PivotHeight = 1.5f;
        SetViewerOffset(RetailDefaultBack, RetailDefaultUp);
    }

    public void SetRetailFirstPersonView()
    {
        _lookingDown = false;
        _mapMode = false;
        _inHead = true;
        _targetDirectionLocal = null;
        YawOffset = 0f;
        Distance = RetailFirstPersonForward;
        Pitch = 0f;
        _initialised = false;
    }

    public void ToggleRetailLookDownView()
    {
        if (_lookingDown)
        {
            RestoreLookDownView();
            return;
        }
        SaveLookDownView();
        _lookingDown = true;
        _mapMode = false;
        _inHead = false;
        _targetDirectionLocal = new Vector3(0f, 0.5f, -1.8f);
        SetViewerOffset(RetailLookDownBack, RetailDefaultUp);
    }

    public void ToggleRetailMapModeView()
    {
        if (_mapMode)
        {
            RestoreLookDownView();
            return;
        }
        if (!_lookingDown)
            SaveLookDownView();
        _lookingDown = true;
        _mapMode = true;
        _inHead = false;
        _targetDirectionLocal = new Vector3(0f, 0.5f, -1.8f);
        SetViewerOffset(RetailMapBack, RetailDefaultUp);
    }

    private void SaveLookDownView()
    {
        _savedDistance = Distance;
        _savedPitch = Pitch;
        _savedYawOffset = YawOffset;
        _savedTargetDirectionLocal = _targetDirectionLocal;
        _savedInHead = _inHead;
    }

    private void RestoreLookDownView()
    {
        Distance = _savedDistance;
        Pitch = _savedPitch;
        YawOffset = _savedYawOffset;
        _targetDirectionLocal = _savedTargetDirectionLocal;
        _inHead = _savedInHead;
        _lookingDown = false;
        _mapMode = false;
    }

    private void ExitLookDownForAdjustment()
    {
        if (_lookingDown)
            RestoreLookDownView();
    }

    private void SetViewerOffset(float back, float up)
    {
        Distance = MathF.Sqrt(back * back + up * up);
        Pitch = MathF.Atan2(up, back);
    }

    private bool TryWriteViewerOffset(
        float candidateDistance,
        float candidatePitch,
        bool preserveHorizontalSign)
    {
        if (!float.IsFinite(candidateDistance)
            || !float.IsFinite(candidatePitch)
            || !(candidateDistance > 0f))
        {
            return false;
        }

        float currentHorizontal = Distance * MathF.Cos(Pitch);
        float candidateHorizontal = candidateDistance * MathF.Cos(candidatePitch);
        if (preserveHorizontalSign
            && ((currentHorizontal > 0f && candidateHorizontal < 0f)
                || (currentHorizontal < 0f && candidateHorizontal > 0f)))
        {
            return false;
        }

        float x = candidateHorizontal * MathF.Sin(YawOffset);
        float y = -candidateHorizontal * MathF.Cos(YawOffset);
        float z = candidateDistance * MathF.Sin(candidatePitch);
        if (!(MathF.Abs(x) < MaximumHorizontalComponent)
            || !(MathF.Abs(y) < MaximumHorizontalComponent)
            || !(z < MaximumVerticalComponent)
            || !(z > MinimumVerticalComponent))
        {
            return false;
        }

        Distance = candidateDistance;
        Pitch = candidatePitch;
        return true;
    }

    public (float outX, float outY) FilterMouseDelta(float rawX, float rawY, float weight, float nowSec)
    {
        // X first — advances the shared timestamp.
        float x = FilterMouseAxis(rawX, weight, nowSec,
            ref _lastMouseDeltaX, ref _lastFilterTimeSec, CameraDiagnostics.MouseLowPassWindowSec);
        float yTimeShadow = _lastFilterTimeSec - 1f;  // force within-window path for the Y axis
        float y = FilterMouseAxis(rawY, weight, nowSec,
            ref _lastMouseDeltaY, ref yTimeShadow, CameraDiagnostics.MouseLowPassWindowSec);
        return (x, y);
    }

    // Math primitives — pure, internal-static for unit-testability.

    internal static Vector3 ComputeHeading(
        Vector3 avgVelocity,
        float yaw,
        bool inContact,
        Vector3 contactPlaneNormal,
        bool alignToSlope)
    {
        // Base heading: player's facing direction in world XY plane.
        Vector3 baseHeading = new(MathF.Cos(yaw), MathF.Sin(yaw), 0f);

        if (!alignToSlope) return baseHeading;

        float avgLength = avgVelocity.Length();
        if (avgLength < MinTiltComponent) return baseHeading;

        Vector3 normAvg = avgVelocity / avgLength;
        if (MathF.Abs(normAvg.X) < MinTiltComponent || MathF.Abs(normAvg.Y) < MinTiltComponent)
            return baseHeading;

        Vector3 normal;
        if (inContact)
        {
            normal = contactPlaneNormal.Length() >= MinTiltComponent
                ? Vector3.Normalize(contactPlaneNormal)
                : contactPlaneNormal;
        }
        else
        {
            Vector3 t = new(normAvg.X, normAvg.Y, AirbornePlaneVerticalScale * normAvg.Z);
            normal = t.Length() >= MinTiltComponent
                ? Vector3.Transform(Vector3.UnitZ, RetailFrameMath.SetVectorHeading(Quaternion.Identity, t))
                : new Vector3(0f, 0f, 1f);
        }

        float   dot       = Vector3.Dot(baseHeading, normal);
        Vector3 projected = baseHeading - normal * dot;

        // Degenerate: facing parallel to normal falls back to the base heading.
        if (projected.Length() < MinTiltComponent) return baseHeading;
        return Vector3.Normalize(projected);
    }

    // True while the orbited boom stays behind the player with at most one unit of side offset.
    internal static bool IsOrbitWithinAlignmentThreshold(float distance, float pitch, float yawOffset)
    {
        float horizontal = distance * MathF.Cos(pitch);
        return horizontal * MathF.Abs(MathF.Sin(yawOffset)) <= 1f && MathF.Cos(yawOffset) >= 0f;
    }

    internal static Vector3? ComputeTrackedHeading(Vector3 pivotWorld, Vector3? trackedTargetPoint)
    {
        if (trackedTargetPoint is not Vector3 targetPoint)
            return null;

        Vector3 towardTarget = targetPoint - pivotWorld;
        return towardTarget.LengthSquared() < 1e-8f
            ? null
            : Vector3.Normalize(towardTarget);
    }

    internal static (Vector3 eye, Vector3 forward) ComputeDesiredPose(
        Vector3 pivotWorld,
        Vector3 heading,
        float distance,
        float pitch,
        float viewerYawOffset)
    {
        var (frameForward, frameRight, frameUp) = BuildBasis(heading);

        // AC's frame-local +Y points opposite our screen-right basis. Rotating
        // viewer_offset.x/y by +yaw therefore produces this boom direction.
        Vector3 boomForward = Vector3.Normalize(
            frameForward * MathF.Cos(viewerYawOffset)
            - frameRight * MathF.Sin(viewerYawOffset));

        float horizontal = distance * MathF.Cos(pitch);
        float vertical = distance * MathF.Sin(pitch);
        Vector3 eye = pivotWorld - boomForward * horizontal + frameUp * vertical;
        Vector3 forward = Vector3.Normalize(pivotWorld - eye);
        return (eye, forward);
    }

    internal static (Vector3 eye, Vector3 forward) ComputeInHeadPose(
        Vector3 pivotWorld,
        Vector3 heading,
        Vector3? targetDirectionLocal = null)
    {
        Vector3 headingForward = Vector3.Normalize(heading);
        Vector3 targetForward = headingForward;
        if (targetDirectionLocal is { } local)
        {
            var (frameForward, frameRight, frameUp) = BuildBasis(headingForward);
            targetForward = Vector3.Normalize(
                frameForward * local.Y
                - frameRight * local.X
                + frameUp * local.Z);
        }
        return (
            pivotWorld + headingForward * RetailFirstPersonForward,
            targetForward);
    }

    internal static (Vector3 eye, Vector3 forward) ComputeTargetDirectionPose(
        Vector3 pivotWorld,
        Vector3 heading,
        float distance,
        float pitch,
        Vector3 targetDirectionLocal)
    {
        var (frameForward, frameRight, frameUp) = BuildBasis(heading);
        Vector3 targetForward = Vector3.Normalize(
            frameForward * targetDirectionLocal.Y
            - frameRight * targetDirectionLocal.X
            + frameUp * targetDirectionLocal.Z);
        var (_, _, targetUp) = BuildBasis(targetForward);

        float back = distance * MathF.Cos(pitch);
        float up = distance * MathF.Sin(pitch);
        Vector3 eye = pivotWorld - targetForward * back + targetUp * up;
        return (eye, targetForward);
    }

    internal static (Vector3 forward, Vector3 right, Vector3 up) BuildBasis(Vector3 heading)
    {
        Vector3 forward = Vector3.Normalize(heading);
        Vector3 worldUp = new(0f, 0f, 1f);

        Vector3 right;
        if (MathF.Abs(forward.Z) > 0.99f)
        {
            // Near-vertical forward — use world +X as the secondary axis.
            right = Vector3.Normalize(Vector3.Cross(forward, new Vector3(1f, 0f, 0f)));
        }
        else
        {
            right = Vector3.Normalize(Vector3.Cross(forward, worldUp));
        }
        Vector3 up = Vector3.Cross(right, forward);  // already unit (forward + right orthonormal)
        return (forward, right, up);
    }

    internal static Vector3[] PushVelocity(Vector3[] ring, ref int count, Vector3 sample)
    {
        if (ring.Length != 5)
            throw new ArgumentException("velocity ring must have 5 entries", nameof(ring));

        // Shift left by 1 (oldest is overwritten), append new sample at the tail.
        for (int i = 0; i < 4; i++) ring[i] = ring[i + 1];
        ring[4] = sample;
        if (count < 5) count++;
        return ring;
    }

    internal static Vector3 AverageVelocity(Vector3[] ring, int count)
    {
        if (count == 0) return Vector3.Zero;
        Vector3 sum = Vector3.Zero;
        int start = ring.Length - count;
        for (int i = start; i < ring.Length; i++) sum += ring[i];
        return sum / count;
    }

    internal static float ComputeDampingAlpha(float stiffness, float dt)
    {
        float a = stiffness * dt * 10f;
        if (a <= 0f) return 0f;
        if (a >= 1f) return 1f;
        return a;
    }

    internal static (Vector3 eye, Vector3 forward, bool frozen) ApplyConvergenceSnap(
        Vector3 viewerEye, Vector3 viewerForward, Vector3 candidateEye, Vector3 candidateForward)
    {
        bool translationConverged = Vector3.Distance(candidateEye,     viewerEye)     < SnapEpsilon;
        bool rotationConverged    = Vector3.Distance(candidateForward, viewerForward) < RotCloseEpsilon;
        if (translationConverged && rotationConverged)
            return (viewerEye, viewerForward, true);   // park: exact fixed point on the viewer
        return (candidateEye, candidateForward, false);
    }

    internal static float FilterMouseAxis(
        float raw,
        float weight,
        float nowSec,
        ref float lastDelta,
        ref float lastTimeSec,
        float windowSec)
    {
        float avg;
        if (nowSec - lastTimeSec < windowSec)
            avg = (lastDelta + raw) * 0.5f;
        else
            avg = raw;

        float output    = raw * (1f - weight) + avg * weight;
        lastDelta       = output;
        lastTimeSec     = nowSec;
        return output;
    }

    internal static float ComputeTranslucency(float distance)
    {
        const float Far  = 0.45f;
        const float Near = 0.20f;

        if (distance >= Far)  return 0f;
        if (distance <= Near) return 1f;
        // Linear: t = 1 - (Near - distance) / (Near - Far)
        return 1f - (Near - distance) / (Near - Far);
    }
}
