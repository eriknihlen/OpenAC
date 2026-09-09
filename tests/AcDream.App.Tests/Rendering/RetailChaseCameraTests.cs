using System;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

[Collection(CameraDiagnosticsCollection.Name)]
public class RetailChaseCameraTests
{
    [Fact]
    public void ResetViewerToPlayer_TeleportReseedsViewerAndReextendsFromPlayer()
    {
        bool savedColl = CameraDiagnostics.CollideCamera;
        float savedT = CameraDiagnostics.TranslationStiffness;
        float savedR = CameraDiagnostics.RotationStiffness;
        try
        {
            CameraDiagnostics.CollideCamera = false;
            CameraDiagnostics.TranslationStiffness = 0.45f;
            CameraDiagnostics.RotationStiffness = 0.45f;

            var camera = new RetailChaseCamera();
            camera.Update(
                Vector3.Zero, 0f, Vector3.Zero, true, Vector3.UnitZ, 1f / 60f,
                cellId: 0x100u);

            var destination = new Vector3(200f, -300f, 12f);
            float yaw = MathF.PI / 2f;
            camera.ResetViewerToPlayer(destination, yaw);

            Assert.Equal(destination, camera.Position);
            Assert.Equal(0u, camera.ViewerCellId);

            camera.Update(
                destination, yaw, Vector3.Zero, true, Vector3.UnitZ, 1f / 60f,
                cellId: 0x12340100u);

            float firstStep = Vector3.Distance(camera.Position, destination);
            Assert.InRange(firstStep, 0.001f, 0.3f);
            Assert.Equal(0x12340100u, camera.ViewerCellId);
        }
        finally
        {
            CameraDiagnostics.CollideCamera = savedColl;
            CameraDiagnostics.TranslationStiffness = savedT;
            CameraDiagnostics.RotationStiffness = savedR;
        }
    }

    // ── Heading source ────────────────────────────────────────────────

    [Fact]
    public void Heading_StationaryWithSlopeAlign_FallsBackToYawVector()
    {
        var avgVel = Vector3.Zero;
        float yaw  = MathF.PI / 4f;  // 45°

        var h = RetailChaseCamera.ComputeHeading(
            avgVel, yaw,
            isOnGround: true, contactPlaneNormal: Vector3.UnitZ,
            alignToSlope: true);

        Assert.Equal(MathF.Cos(yaw), h.X, 5);
        Assert.Equal(MathF.Sin(yaw), h.Y, 5);
        Assert.Equal(0f,             h.Z, 5);
    }

    [Fact]
    public void Heading_MovingOnFlatGround_HeadingIsHorizontalFacing()
    {
        // Player moving forward (yaw=0 = +X), on flat ground. Heading
        // should be the yaw vector — the projection onto (0,0,1)-normal
        // plane is a no-op since the base is already horizontal.
        var avgVel = new Vector3(3f, 0f, 0f);
        var h = RetailChaseCamera.ComputeHeading(
            avgVel, yaw: 0f,
            isOnGround: true, contactPlaneNormal: Vector3.UnitZ,
            alignToSlope: true);
        Assert.Equal(1f, h.X, 5);
        Assert.Equal(0f, h.Y, 5);
        Assert.Equal(0f, h.Z, 5);
    }

    [Fact]
    public void Heading_OnUphillSlope_TiltsWithSlope()
    {
        // Player facing +Y (yaw=π/2), walking up a slope rising in +Y.
        // Slope normal tilts back-up: (0, -0.5, 0.866) (30° rise).
        // Projection of (0,1,0) onto plane perpendicular to (0,-0.5,0.866):
        //   dot = 1*(-0.5) = -0.5
        //   projected = (0,1,0) - (0,-0.5,0.866)*(-0.5) = (0, 0.75, 0.433)
        //   normalized → (0, 0.866, 0.5) — slope-aligned heading with +Z tilt.
        var avgVel = new Vector3(0f, 3f, 1.5f);   // moving up the slope
        var normal = new Vector3(0f, -0.5f, 0.866f);
        var h = RetailChaseCamera.ComputeHeading(
            avgVel, yaw: MathF.PI / 2f,
            isOnGround: true, contactPlaneNormal: normal,
            alignToSlope: true);
        Assert.True(h.Z > 0.4f, $"expected slope-aligned +Z tilt, got Z={h.Z}");
        Assert.Equal(1f, h.Length(), 4);
    }

    [Fact]
    public void Heading_AirborneJumpingStraightUp_StaysHorizontal()
    {
        var avgVel = new Vector3(0f, 0f, 5f);
        var h = RetailChaseCamera.ComputeHeading(
            avgVel, yaw: 0f,
            isOnGround: false, contactPlaneNormal: Vector3.Zero,
            alignToSlope: true);
        Assert.Equal(1f, h.X, 5);
        Assert.Equal(0f, h.Y, 5);
        Assert.Equal(0f, h.Z, 5);
    }

    [Fact]
    public void Heading_AirborneRunningJump_StaysHorizontal()
    {
        var avgVel = new Vector3(3f, 0f, 4f);
        var h = RetailChaseCamera.ComputeHeading(
            avgVel, yaw: 0f,
            isOnGround: false, contactPlaneNormal: Vector3.Zero,
            alignToSlope: true);
        Assert.Equal(1f, h.X, 5);
        Assert.Equal(0f, h.Y, 5);
        Assert.Equal(0f, h.Z, 5);
    }

    [Fact]
    public void Heading_SlopeAlignDisabled_IgnoresVelocityAndContactPlane()
    {
        var avgVel = new Vector3(0f, 0f, 1f);
        var tiltedNormal = new Vector3(0f, -0.5f, 0.866f);

        var h = RetailChaseCamera.ComputeHeading(
            avgVel, yaw: 0f,
            isOnGround: true, contactPlaneNormal: tiltedNormal,
            alignToSlope: false);

        Assert.Equal(1f, h.X, 5);   // (cos 0, sin 0, 0) = (1, 0, 0)
        Assert.Equal(0f, h.Y, 5);
        Assert.Equal(0f, h.Z, 5);
    }

    // ── Basis from heading ────────────────────────────────────────────

    [Fact]
    public void TrackedHeading_UsesPivotToRetailTargetOffsetPoint()
    {
        var pivot = new Vector3(0f, 0f, 1.5f);
        var targetPoint = new Vector3(10f, 0f, 0.5f);
        Vector3 expected = Vector3.Normalize(new Vector3(10f, 0f, -1f));

        Vector3 heading = RetailChaseCamera.ComputeTrackedHeading(pivot, targetPoint)!.Value;

        Assert.Equal(expected.X, heading.X, 5);
        Assert.Equal(expected.Y, heading.Y, 5);
        Assert.Equal(expected.Z, heading.Z, 5);
    }

    [Fact]
    public void TrackedHeading_MissingOrCoincidentTarget_FallsBack()
    {
        var pivot = new Vector3(1f, 2f, 3f);

        Assert.Null(RetailChaseCamera.ComputeTrackedHeading(pivot, null));
        Assert.Null(RetailChaseCamera.ComputeTrackedHeading(pivot, pivot));
    }

    [Fact]
    public void DesiredPose_TrackedHeadingWithNoOrbit_PlacesBoomBehindTargetDirection()
    {
        var pivot = new Vector3(10f, 20f, 1.5f);

        var (eye, forward) = RetailChaseCamera.ComputeDesiredPose(
            pivot, Vector3.UnitX, distance: 5f, pitch: 0f, viewerYawOffset: 0f);

        Assert.Equal(new Vector3(5f, 20f, 1.5f), eye);
        Assert.Equal(Vector3.UnitX, forward);
    }

    [Fact]
    public void DesiredPose_TrackedHeadingRetainsViewerOffsetOrbitAndLooksAtPivot()
    {
        var pivot = new Vector3(10f, 20f, 1.5f);

        var (eye, forward) = RetailChaseCamera.ComputeDesiredPose(
            pivot, Vector3.UnitX, distance: 5f, pitch: 0f,
            viewerYawOffset: MathF.PI / 2f);

        Assert.Equal(10f, eye.X, 5);
        Assert.Equal(15f, eye.Y, 5);
        Assert.Equal(1.5f, eye.Z, 5);
        Assert.Equal(Vector3.Normalize(pivot - eye), forward);
    }

    [Fact]
    public void MapMode_TargetDirectionTransformsViewerOffsetIntoOverheadPose()
    {
        var pivot = new Vector3(10f, 20f, 1.5f);
        float distance = MathF.Sqrt(450f * 450f + 0.75f * 0.75f);
        float pitch = MathF.Atan2(0.75f, 450f);

        var (eye, forward) = RetailChaseCamera.ComputeTargetDirectionPose(
            pivot,
            Vector3.UnitX,
            distance,
            pitch,
            new Vector3(0f, 0.5f, -1.8f));

        Assert.True(eye.Z > 430f, $"expected retail overhead eye, got Z={eye.Z}");
        Assert.InRange(Vector2.Distance(new Vector2(eye.X, eye.Y), new Vector2(pivot.X, pivot.Y)), 119f, 122f);
        Assert.True(forward.Z < -0.95f, $"expected steep downward view, got {forward}");
    }

    [Fact]
    public void Basis_HorizontalHeading_IsOrthonormalAndRightHanded()
    {
        var (forward, right, up) = RetailChaseCamera.BuildBasis(new Vector3(1f, 0f, 0f));

        Assert.Equal(1f, forward.Length(), 5);
        Assert.Equal(1f, right.Length(),   5);
        Assert.Equal(1f, up.Length(),      5);

        // Orthogonal
        Assert.Equal(0f, Vector3.Dot(forward, right), 5);
        Assert.Equal(0f, Vector3.Dot(forward, up),    5);
        Assert.Equal(0f, Vector3.Dot(right,   up),    5);

        Assert.Equal(0f, up.X, 5);
        Assert.Equal(0f, up.Y, 5);
        Assert.True(up.Z > 0f);
    }

    [Fact]
    public void Basis_NearVerticalHeading_UsesXFallbackForRight()
    {
        var (_, right, up) = RetailChaseCamera.BuildBasis(new Vector3(0f, 0f, 1f));

        Assert.Equal(1f, right.Length(), 5);
        Assert.Equal(1f, up.Length(),    5);
        Assert.Equal(0f, Vector3.Dot(right, up), 5);
    }

    // ── Velocity ring & averaging ────────────────────────────────────

    [Fact]
    public void VelocityRing_AveragesLastN()
    {
        var ring = new Vector3[5];
        int count = 0;

        ring = RetailChaseCamera.PushVelocity(ring, ref count, new Vector3(1, 0, 0));
        ring = RetailChaseCamera.PushVelocity(ring, ref count, new Vector3(1, 0, 0));
        ring = RetailChaseCamera.PushVelocity(ring, ref count, new Vector3(2, 0, 0));
        ring = RetailChaseCamera.PushVelocity(ring, ref count, new Vector3(2, 0, 0));
        ring = RetailChaseCamera.PushVelocity(ring, ref count, new Vector3(3, 0, 0));

        Assert.Equal(5, count);
        var avg = RetailChaseCamera.AverageVelocity(ring, count);
        Assert.Equal(1.8f, avg.X, 5);
        Assert.Equal(0f,   avg.Y, 5);
        Assert.Equal(0f,   avg.Z, 5);
    }

    [Fact]
    public void VelocityRing_FifoEvictsOldest()
    {
        var ring = new Vector3[5];
        int count = 0;

        // Push 6 entries; oldest (the first 1,0,0) should be evicted.
        for (int i = 0; i < 5; i++)
            ring = RetailChaseCamera.PushVelocity(ring, ref count, new Vector3(1, 0, 0));
        ring = RetailChaseCamera.PushVelocity(ring, ref count, new Vector3(10, 0, 0));

        Assert.Equal(5, count);
        // Sum of newest 5 entries: 4*(1,0,0) + (10,0,0) = (14,0,0), avg = 2.8
        var avg = RetailChaseCamera.AverageVelocity(ring, count);
        Assert.Equal(2.8f, avg.X, 5);
    }

    [Fact]
    public void VelocityRing_PartialFillUsesActualCount()
    {
        var ring = new Vector3[5];
        int count = 0;

        ring = RetailChaseCamera.PushVelocity(ring, ref count, new Vector3(2, 0, 0));
        ring = RetailChaseCamera.PushVelocity(ring, ref count, new Vector3(4, 0, 0));

        Assert.Equal(2, count);
        var avg = RetailChaseCamera.AverageVelocity(ring, count);
        Assert.Equal(3f, avg.X, 5);   // (2+4)/2, not (2+4)/5
    }

    // ── Damping alpha ────────────────────────────────────────────────

    [Fact]
    public void DampingAlpha_RetailDefault_ProducesSevenAndAHalfPercent()
    {
        // stiffness=0.45, dt=1/60 → 0.45 * (1/60) * 10 ≈ 0.075
        float alpha = RetailChaseCamera.ComputeDampingAlpha(stiffness: 0.45f, dt: 1f / 60f);
        Assert.Equal(0.075f, alpha, 4);
    }

    [Fact]
    public void DampingAlpha_LargeDtClampsToOne()
    {
        float alpha = RetailChaseCamera.ComputeDampingAlpha(stiffness: 0.45f, dt: 1f);
        Assert.Equal(1f, alpha);
    }

    [Fact]
    public void DampingAlpha_NegativeOrZero_ClampsToZero()
    {
        Assert.Equal(0f, RetailChaseCamera.ComputeDampingAlpha(stiffness: 0.45f, dt: 0f));
        Assert.Equal(0f, RetailChaseCamera.ComputeDampingAlpha(stiffness: 0.0f,  dt: 1f));
    }

    // ── Mouse low-pass ───────────────────────────────────────────────

    [Fact]
    public void MouseFilter_BeyondWindow_OutputsRaw()
    {
        float lastDelta = 5f;
        float lastTime  = 0f;
        float windowSec = 0.25f;

        float result = RetailChaseCamera.FilterMouseAxis(
            raw: 10f, weight: 0.5f, nowSec: 1.0f,
            ref lastDelta, ref lastTime, windowSec);

        // Beyond window, blended == raw, so out = raw * 0.5 + raw * 0.5 = raw.
        Assert.Equal(10f, result, 5);
    }

    [Fact]
    public void MouseFilter_WithinWindow_AveragesWithPrevious()
    {
        float lastDelta = 10f;
        float lastTime  = 0f;
        float windowSec = 0.25f;

        float result = RetailChaseCamera.FilterMouseAxis(
            raw: 20f, weight: 0.5f, nowSec: 0.1f,
            ref lastDelta, ref lastTime, windowSec);

        // Within window: avg = (10 + 20)/2 = 15.
        // Output: 20 * 0.5 + 15 * 0.5 = 17.5
        Assert.Equal(17.5f, result, 5);
    }

    [Fact]
    public void MouseFilter_WeightZero_OutputsRaw()
    {
        float lastDelta = 10f;
        float lastTime  = 0f;
        float windowSec = 0.25f;

        float result = RetailChaseCamera.FilterMouseAxis(
            raw: 20f, weight: 0f, nowSec: 0.1f,
            ref lastDelta, ref lastTime, windowSec);

        Assert.Equal(20f, result, 5);
    }

    [Fact]
    public void MouseFilter_WeightOne_OutputsAveraged()
    {
        float lastDelta = 10f;
        float lastTime  = 0f;
        float windowSec = 0.25f;

        float result = RetailChaseCamera.FilterMouseAxis(
            raw: 20f, weight: 1f, nowSec: 0.1f,
            ref lastDelta, ref lastTime, windowSec);

        // weight=1 → out = avg = 15
        Assert.Equal(15f, result, 5);
    }

    [Fact]
    public void MouseFilter_UpdatesLastDeltaAndTime()
    {
        float lastDelta = 10f;
        float lastTime  = 0f;
        float windowSec = 0.25f;

        float result = RetailChaseCamera.FilterMouseAxis(
            raw: 20f, weight: 0.5f, nowSec: 0.1f,
            ref lastDelta, ref lastTime, windowSec);

        Assert.Equal(result, lastDelta);   // last is updated to output
        Assert.Equal(0.1f,   lastTime, 5); // last time advances
    }

    // ── Auto-fade translucency ───────────────────────────────────────

    [Fact]
    public void Translucency_AtFarThreshold_IsZero()
    {
        Assert.Equal(0f, RetailChaseCamera.ComputeTranslucency(distance: 0.45f), 5);
        Assert.Equal(0f, RetailChaseCamera.ComputeTranslucency(distance: 1.00f), 5);
    }

    [Fact]
    public void Translucency_MidwayBetweenThresholds_IsHalf()
    {
        // Midpoint between 0.20 and 0.45 = 0.325
        // t = 1 - (0.20 - 0.325) / (0.20 - 0.45)
        //   = 1 - (-0.125) / (-0.25)
        //   = 1 - 0.5 = 0.5
        Assert.Equal(0.5f, RetailChaseCamera.ComputeTranslucency(distance: 0.325f), 4);
    }

    [Fact]
    public void Translucency_AtNearThreshold_IsOne()
    {
        Assert.Equal(1f, RetailChaseCamera.ComputeTranslucency(distance: 0.20f),  5);
        Assert.Equal(1f, RetailChaseCamera.ComputeTranslucency(distance: 0.10f),  5);
        Assert.Equal(1f, RetailChaseCamera.ComputeTranslucency(distance: 0.0f),   5);
    }

    // ── Update() integration ─────────────────────────────────────────

    [Fact]
    public void FirstUpdate_SnapsToTarget()
    {
        bool savedAlign = CameraDiagnostics.AlignToSlope;
        try
        {
            var cam = new RetailChaseCamera { Distance = 5f, Pitch = 0f };
            CameraDiagnostics.AlignToSlope = false;   // deterministic: heading = yaw vec

            cam.Update(
                playerPosition:      new Vector3(10f, 20f, 30f),
                playerYaw:           0f,             // forward = +X
                playerVelocity:      Vector3.Zero,
                isOnGround:          true,
                contactPlaneNormal:  Vector3.UnitZ,  // flat
                dt:                  1f / 60f);

            // Expected target eye:
            //   pivot          = (10, 20, 30+1.5=31.5)
            //   forward (yaw=0)= (1, 0, 0)
            //   right          = (0, -1, 0) since (1,0,0) × (0,0,1) = (0, -1, 0)
            //   up             = right × forward = (0,-1,0) × (1,0,0) = (0,0,1)
            //   viewer_offset  = (0, -5, 0)  (Distance=5, Pitch=0 → -Distance*cos = -5, sin = 0)
            //   eye = pivot + right*0 + forward*-5 + up*0
            //       = (10 - 5, 20, 31.5) = (5, 20, 31.5)
            Assert.Equal(5f,    cam.Position.X, 4);
            Assert.Equal(20f,   cam.Position.Y, 4);
            Assert.Equal(31.5f, cam.Position.Z, 4);
        }
        finally
        {
            CameraDiagnostics.AlignToSlope = savedAlign;
        }
    }

    [Fact]
    public void SecondUpdate_LerpsTowardTarget()
    {
        bool  savedAlign       = CameraDiagnostics.AlignToSlope;
        float savedTranslation = CameraDiagnostics.TranslationStiffness;
        float savedRotation    = CameraDiagnostics.RotationStiffness;
        try
        {
            var cam = new RetailChaseCamera { Distance = 5f, Pitch = 0f };
            CameraDiagnostics.AlignToSlope         = false;
            CameraDiagnostics.TranslationStiffness = 0.45f;
            CameraDiagnostics.RotationStiffness    = 0.45f;

            // First update at origin: dampedEye = (-5, 0, 1.5).
            cam.Update(Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
                isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f);
            var firstEye = cam.Position;

            // Teleport the player one frame later. Target eye now at (10-5, 0, 1.5) = (5, 0, 1.5).
            // alpha = 0.45 * (1/60) * 10 = 0.075.
            // New eye = firstEye + 0.075 * (target - firstEye)
            //         = (-5,0,1.5) + 0.075 * ((5,0,1.5) - (-5,0,1.5))
            //         = (-5,0,1.5) + 0.075 * (10,0,0)
            //         = (-4.25, 0, 1.5)
            cam.Update(new Vector3(10f, 0f, 0f), playerYaw: 0f, playerVelocity: Vector3.Zero,
                isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f);

            Assert.Equal(-4.25f, cam.Position.X, 3);
            Assert.Equal(0f,     cam.Position.Y, 4);
            Assert.Equal(1.5f,   cam.Position.Z, 4);
        }
        finally
        {
            CameraDiagnostics.AlignToSlope         = savedAlign;
            CameraDiagnostics.TranslationStiffness = savedTranslation;
            CameraDiagnostics.RotationStiffness    = savedRotation;
        }
    }

    [Fact]
    public void Translucency_PropertyReflectsCurrentDampedDistance()
    {
        bool savedAlign = CameraDiagnostics.AlignToSlope;
        try
        {
            var cam = new RetailChaseCamera { Distance = 5f, Pitch = 0f, PivotHeight = 1.5f };
            CameraDiagnostics.AlignToSlope = false;

            // Far from pivot — translucency should be 0.
            cam.Update(Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
                isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f);
            Assert.Equal(0f, cam.PlayerTranslucency, 5);
        }
        finally
        {
            CameraDiagnostics.AlignToSlope = savedAlign;
        }
    }

    [Fact]
    public void AdjustDistance_UsesRetailMultiplicativeScale()
    {
        var cam = new RetailChaseCamera { Distance = 5f };
        cam.AdjustDistance(+1f);
        Assert.Equal(6f, cam.Distance, 5);

        cam.AdjustDistance(-1f);
        Assert.Equal(4.8f, cam.Distance, 5);
    }

    [Fact]
    public void AdjustDistance_RefusesWholeWriteAtRetailComponentAndNearLimits()
    {
        var far = new RetailChaseCamera
        {
            Distance = 9.9f,
            Pitch = 0f,
            YawOffset = 0f,
        };
        far.AdjustDistance(+1f);
        Assert.Equal(9.9f, far.Distance, 5);

        var near = new RetailChaseCamera
        {
            Distance = 0.55f,
            Pitch = 0f,
        };
        near.AdjustDistance(-1f);
        Assert.Equal(0.55f, near.Distance, 5);
    }

    [Fact]
    public void AdjustDistance_DiagonalOrbitUsesIndependentRetailXAndYLimits()
    {
        var cam = new RetailChaseCamera
        {
            Distance = 12f,
            Pitch = 0f,
            YawOffset = MathF.PI / 4f,
        };

        cam.AdjustDistance(+0.5f);

        Assert.Equal(13.2f, cam.Distance, 5);
    }

    [Fact]
    public void SetRetailFirstPersonView_PlacesEyeAheadAndLooksForward()
    {
        var cam = new RetailChaseCamera();
        cam.SetRetailFirstPersonView();

        cam.Update(
            playerPosition: Vector3.Zero,
            playerYaw: 0f,
            playerVelocity: Vector3.Zero,
            isOnGround: true,
            contactPlaneNormal: Vector3.UnitZ,
            dt: 1f / 60f);

        Assert.True(cam.IsInHead);
        Assert.Equal(new Vector3(0.18f, 0f, 1.5f), cam.Position);
        Assert.Equal(1f, cam.PlayerTranslucency, 5);
        var (_, forward) = RetailChaseCamera.ComputeInHeadPose(
            new Vector3(0f, 0f, 1.5f),
            Vector3.UnitX);
        Assert.Equal(Vector3.UnitX, forward);
    }

    [Fact]
    public void AdjustingZoomExitsRetailFirstPersonView()
    {
        var cam = new RetailChaseCamera();
        cam.SetRetailFirstPersonView();

        cam.AdjustDistance(1f);

        Assert.False(cam.IsInHead);
        Assert.Equal(MathF.Sqrt(0.6f * 0.6f + 0.5f * 0.5f), cam.Distance, 5);
    }

    [Fact]
    public void AdjustingCloserInRetailFirstPerson_RefusesNearWriteAndStaysInHead()
    {
        var cam = new RetailChaseCamera();
        cam.SetRetailFirstPersonView();

        cam.AdjustDistance(-1f);

        Assert.True(cam.IsInHead);
        Assert.Equal(0.18f, cam.Distance, 5);
    }

    [Fact]
    public void AdjustPitchInRetailFirstPerson_ChangesDirectionWithoutMovingEye()
    {
        var cam = new RetailChaseCamera();
        cam.SetRetailFirstPersonView();

        cam.AdjustPitch(+1f);
        cam.Update(
            playerPosition: Vector3.Zero,
            playerYaw: 0f,
            playerVelocity: Vector3.Zero,
            isOnGround: true,
            contactPlaneNormal: Vector3.UnitZ,
            dt: 1f / 60f);

        Assert.True(cam.IsInHead);
        Assert.Equal(new Vector3(0.18f, 0f, 1.5f), cam.Position);
        Vector3 forward = Vector3.Normalize(new Vector3(
            -cam.View.M13,
            -cam.View.M23,
            -cam.View.M33));
        Assert.True(forward.Z > 0f);

        for (int i = 0; i < 10; i++)
            cam.AdjustPitch(+1f);
        cam.Update(
            playerPosition: Vector3.Zero,
            playerYaw: 0f,
            playerVelocity: Vector3.Zero,
            isOnGround: true,
            contactPlaneNormal: Vector3.UnitZ,
            dt: 1f);
        forward = Vector3.Normalize(new Vector3(
            -cam.View.M13,
            -cam.View.M23,
            -cam.View.M33));
        Assert.Equal(
            0.8f / MathF.Sqrt(1f + 0.8f * 0.8f),
            forward.Z,
            5);
    }

    [Fact]
    public void AdjustPitch_UsesRetailEightDegreeRotationAndPreservesLength()
    {
        var cam = new RetailChaseCamera { Distance = 5f, Pitch = 0f };
        cam.AdjustPitch(+1f);

        Assert.Equal(RetailChaseCamera.OffsetAngleRadians, cam.Pitch, 6);
        Assert.Equal(5f, cam.Distance, 6);
    }

    [Fact]
    public void AdjustPitch_RefusesWholeWriteBelowRetailVerticalLimit()
    {
        var cam = new RetailChaseCamera { Distance = 3f, Pitch = -0.55f };
        float before = cam.Pitch;

        cam.AdjustPitch(-1f);

        Assert.Equal(before, cam.Pitch);
        Assert.Equal(3f, cam.Distance);
    }

    [Fact]
    public void AdjustYaw_UsesRetailEightDegreeRotationUnit()
    {
        var cam = new RetailChaseCamera();
        cam.AdjustYaw(+1f);
        Assert.Equal(RetailChaseCamera.OffsetAngleRadians, cam.YawOffset, 6);
    }


    private sealed class FakeProbe : ICameraCollisionProbe
    {
        public int Calls;
        public Vector3 ReturnEye;
        public uint ReturnCell;
        public CameraSweepResult SweepEye(Vector3 pivot, Vector3 desiredEye, uint cellId, uint selfEntityId, Vector3 playerPos)
        {
            Calls++;
            return new CameraSweepResult(ReturnEye, ReturnCell);
        }
    }

    [Fact]
    public void Update_WithProbeAndFlagOn_PublishesCollidedEye()
    {
        CameraDiagnostics.CollideCamera = true;
        var collided = new Vector3(1f, 2f, 3f);
        var probe = new FakeProbe { ReturnEye = collided };
        var cam = new RetailChaseCamera { CollisionProbe = probe };

        cam.Update(
            playerPosition: Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
            isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f,
            cellId: 0x100, selfEntityId: 0x5);

        Assert.Equal(1, probe.Calls);
        Assert.Equal(collided, cam.Position);
    }

    [Fact]
    public void Update_WithProbeAndFlagOn_ExposesSweptViewerCell()
    {
        CameraDiagnostics.CollideCamera = true;
        var probe = new FakeProbe { ReturnEye = new Vector3(1f, 2f, 3f), ReturnCell = 0xA9B40170u };
        var cam = new RetailChaseCamera { CollisionProbe = probe };

        cam.Update(
            playerPosition: Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
            isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f,
            cellId: 0xA9B40171u, selfEntityId: 0x5);

        Assert.Equal(0xA9B40170u, cam.ViewerCellId);
    }

    [Fact]
    public void Update_FlagOff_ViewerCellFallsBackToPlayerCell()
    {
        CameraDiagnostics.CollideCamera = false;
        try
        {
            var cam = new RetailChaseCamera { CollisionProbe = new FakeProbe { ReturnCell = 0xDEADu } };
            cam.Update(
                playerPosition: Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
                isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f,
                cellId: 0xA9B40171u, selfEntityId: 0x5);

            Assert.Equal(0xA9B40171u, cam.ViewerCellId);
        }
        finally { CameraDiagnostics.CollideCamera = true; }
    }

    [Fact]
    public void Update_FlagOff_DoesNotConsultProbe()
    {
        CameraDiagnostics.CollideCamera = false;
        try
        {
            var probe = new FakeProbe { ReturnEye = new Vector3(99f, 99f, 99f) };
            var cam = new RetailChaseCamera { CollisionProbe = probe };

            cam.Update(
                playerPosition: Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
                isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f,
                cellId: 0x100, selfEntityId: 0x5);

            Assert.Equal(0, probe.Calls);
            Assert.NotEqual(new Vector3(99f, 99f, 99f), cam.Position);
        }
        finally
        {
            CameraDiagnostics.CollideCamera = true; // reset even if an assert throws
        }
    }

    [Fact]
    public void Update_NullProbe_DoesNotThrow()
    {
        CameraDiagnostics.CollideCamera = true;
        var cam = new RetailChaseCamera { CollisionProbe = null };

        cam.Update(
            playerPosition: Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
            isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f,
            cellId: 0x100, selfEntityId: 0x5);

        Assert.NotEqual(default, cam.View);
    }

    [Fact]
    public void Update_ProbePullsEyeInClose_FullyFadesPlayer()
    {
        CameraDiagnostics.CollideCamera = true;
        var pulledIn = new Vector3(0f, 0f, 1.6f);
        var cam = new RetailChaseCamera { CollisionProbe = new FakeProbe { ReturnEye = pulledIn } };

        cam.Update(
            playerPosition: Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
            isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f,
            cellId: 0x100, selfEntityId: 0x5);

        Assert.Equal(pulledIn, cam.Position);
        Assert.Equal(1f, cam.PlayerTranslucency, 3);
    }

    private sealed class ClampThenReleaseProbe : ICameraCollisionProbe
    {
        public int Calls;
        public Vector3 ClampEye;
        public CameraSweepResult SweepEye(Vector3 pivot, Vector3 desiredEye, uint cellId, uint selfEntityId, Vector3 playerPos)
        {
            Calls++;
            return new CameraSweepResult(Calls == 1 ? ClampEye : desiredEye, cellId);
        }
    }

    [Fact]
    public void Update_AfterClampReleases_EyeReExtendsGradually()
    {
        bool  savedColl = CameraDiagnostics.CollideCamera;
        float savedT    = CameraDiagnostics.TranslationStiffness;
        float savedR    = CameraDiagnostics.RotationStiffness;
        try
        {
            CameraDiagnostics.CollideCamera        = true;
            CameraDiagnostics.TranslationStiffness = 0.45f;
            CameraDiagnostics.RotationStiffness    = 0.45f;

            var probe = new ClampThenReleaseProbe { ClampEye = new Vector3(0f, 0f, 2f) };
            var cam = new RetailChaseCamera { CollisionProbe = probe };

            void Step() => cam.Update(
                playerPosition: Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
                isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f,
                cellId: 0x100, selfEntityId: 0x5);

            Step();
            Step();  // frame 2: releases

            Assert.True(cam.Position.X > -0.5f,
                $"eye must re-extend gradually from the contact, got {cam.Position}");

            for (int i = 0; i < 200; i++) Step();
            Assert.True(cam.Position.X < -2.4f,
                $"eye should converge to the target after release, got {cam.Position}");
        }
        finally
        {
            CameraDiagnostics.CollideCamera        = savedColl;
            CameraDiagnostics.TranslationStiffness = savedT;
            CameraDiagnostics.RotationStiffness    = savedR;
        }
    }

    private sealed class RecordingClampProbe : ICameraCollisionProbe
    {
        public readonly System.Collections.Generic.List<Vector3> Requests = new();
        public Vector3 ClampEye;
        public CameraSweepResult SweepEye(Vector3 pivot, Vector3 desiredEye, uint cellId, uint selfEntityId, Vector3 playerPos)
        {
            Requests.Add(desiredEye);
            return new CameraSweepResult(ClampEye, cellId);
        }
    }

    [Fact]
    public void Update_SweepTargetConvergesOntoContact_NotTheFullBoom()
    {
        bool  savedColl = CameraDiagnostics.CollideCamera;
        float savedT    = CameraDiagnostics.TranslationStiffness;
        float savedR    = CameraDiagnostics.RotationStiffness;
        try
        {
            CameraDiagnostics.CollideCamera        = true;
            CameraDiagnostics.TranslationStiffness = 0.45f;
            CameraDiagnostics.RotationStiffness    = 0.45f;

            var wall  = new Vector3(0f, 0f, 2f);
            var probe = new RecordingClampProbe { ClampEye = wall };
            var cam   = new RetailChaseCamera { CollisionProbe = probe };

            void Step() => cam.Update(
                playerPosition: Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
                isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f,
                cellId: 0x100, selfEntityId: 0x5);

            Step();
            Step();

            Assert.True(Vector3.Distance(probe.Requests[0], wall) > 2f,
                $"frame-1 init request should be the full boom, got {probe.Requests[0]}");

            // Frame 2's request = one 7.5% lerp step off the wall (~0.19 m) — the
            // knife-edge full-length ray is never re-rolled.
            float reach = Vector3.Distance(probe.Requests[1], wall);
            Assert.True(reach < 0.3f,
                $"frame-2 sweep target must sit one step past the contact, got {reach:F3} m past it");
        }
        finally
        {
            CameraDiagnostics.CollideCamera        = savedColl;
            CameraDiagnostics.TranslationStiffness = savedT;
            CameraDiagnostics.RotationStiffness    = savedR;
        }
    }

    private sealed class FallbackThenReleaseProbe : ICameraCollisionProbe
    {
        public readonly System.Collections.Generic.List<Vector3> Requests = new();
        public int Calls;
        public CameraSweepResult SweepEye(Vector3 pivot, Vector3 desiredEye, uint cellId, uint selfEntityId, Vector3 playerPos)
        {
            Calls++;
            Requests.Add(desiredEye);
            return Calls == 1
                ? new CameraSweepResult(playerPos, 0u)          // total fallback (pc:92886)
                : new CameraSweepResult(desiredEye, cellId);
        }
    }

    [Fact]
    public void Update_TotalFallback_ReExtendsFromThePlayer()
    {
        bool  savedColl = CameraDiagnostics.CollideCamera;
        float savedT    = CameraDiagnostics.TranslationStiffness;
        float savedR    = CameraDiagnostics.RotationStiffness;
        try
        {
            CameraDiagnostics.CollideCamera        = true;
            CameraDiagnostics.TranslationStiffness = 0.45f;
            CameraDiagnostics.RotationStiffness    = 0.45f;

            var probe = new FallbackThenReleaseProbe();
            var cam   = new RetailChaseCamera { CollisionProbe = probe };

            void Step() => cam.Update(
                playerPosition: Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
                isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f,
                cellId: 0x100, selfEntityId: 0x5);

            Step();  // frame 1: total fallback — viewer snaps to the player
            Assert.Equal(Vector3.Zero, cam.Position);
            Assert.Equal(0x100u, cam.ViewerCellId);

            Step();  // frame 2: the sweep target re-extends FROM the player
            float reach = Vector3.Distance(probe.Requests[1], Vector3.Zero);
            Assert.True(reach < 0.3f,
                $"post-fallback sweep target must re-extend from the player, got {reach:F3} m out");
        }
        finally
        {
            CameraDiagnostics.CollideCamera        = savedColl;
            CameraDiagnostics.TranslationStiffness = savedT;
            CameraDiagnostics.RotationStiffness    = savedR;
        }
    }

    private sealed class WallPlaneProbe : ICameraCollisionProbe
    {
        public float WallX = -1f;
        public CameraSweepResult SweepEye(Vector3 pivot, Vector3 desiredEye, uint cellId, uint selfEntityId, Vector3 playerPos)
        {
            if (desiredEye.X >= WallX || desiredEye.X - pivot.X >= -1e-6f)
                return new CameraSweepResult(desiredEye, cellId);
            float t = (WallX - pivot.X) / (desiredEye.X - pivot.X);
            return new CameraSweepResult(pivot + (desiredEye - pivot) * t, cellId);
        }
    }

    [Fact]
    public void Update_PressedAgainstWall_EyeGlidesStably()
    {
        bool  savedColl = CameraDiagnostics.CollideCamera;
        float savedT    = CameraDiagnostics.TranslationStiffness;
        float savedR    = CameraDiagnostics.RotationStiffness;
        try
        {
            CameraDiagnostics.CollideCamera        = true;
            CameraDiagnostics.TranslationStiffness = 0.45f;
            CameraDiagnostics.RotationStiffness    = 0.45f;

            var cam = new RetailChaseCamera { CollisionProbe = new WallPlaneProbe { WallX = -1f } };

            void Step() => cam.Update(
                playerPosition: Vector3.Zero, playerYaw: 0f, playerVelocity: Vector3.Zero,
                isOnGround: true, contactPlaneNormal: Vector3.UnitZ, dt: 1f / 60f,
                cellId: 0x100, selfEntityId: 0x5);

            // Settle a few frames, then watch 30 frames for per-frame jumps.
            for (int i = 0; i < 5; i++) Step();
            Vector3 prev = cam.Position;
            float maxDelta = 0f;
            for (int i = 0; i < 30; i++)
            {
                Step();
                maxDelta = MathF.Max(maxDelta, Vector3.Distance(cam.Position, prev));
                prev = cam.Position;
            }

            Assert.True(MathF.Abs(cam.Position.X - (-1f)) < 1e-3f,
                $"eye should sit on the wall plane, got {cam.Position}");
            Assert.True(maxDelta < 1e-3f,
                $"pressed against a wall the eye must not jump frame-to-frame, max delta {maxDelta:F5} m");
        }
        finally
        {
            CameraDiagnostics.CollideCamera        = savedColl;
            CameraDiagnostics.TranslationStiffness = savedT;
            CameraDiagnostics.RotationStiffness    = savedR;
        }
    }


    [Fact]
    public void ConvergenceSnap_StepBelowEpsilon_FreezesAtCurrent()
    {
        var damped    = new Vector3(5f, 6f, 7f);
        var forward   = Vector3.Normalize(new Vector3(1f, 0f, 0f));
        var candidate = damped + new Vector3(0.0001f, 0f, 0f);        // 0.1 mm step < 0.4 mm
        var candFwd   = forward;                                       // no rotation step

        var (eye, fwd, frozen) = RetailChaseCamera.ApplyConvergenceSnap(damped, forward, candidate, candFwd);

        Assert.True(frozen);
        Assert.Equal(damped, eye);       // exact — returns the input, freezing the drift
        Assert.Equal(forward, fwd);
    }

    [Fact]
    public void ConvergenceSnap_TranslationStepAboveEpsilon_ReturnsCandidate()
    {
        var damped    = new Vector3(5f, 6f, 7f);
        var forward   = Vector3.Normalize(new Vector3(1f, 0f, 0f));
        var candidate = damped + new Vector3(0.01f, 0f, 0f);          // 1 cm step ≫ 0.4 mm
        var candFwd   = forward;

        var (eye, fwd, frozen) = RetailChaseCamera.ApplyConvergenceSnap(damped, forward, candidate, candFwd);

        Assert.False(frozen);
        Assert.Equal(candidate, eye);
        Assert.Equal(candFwd, fwd);
    }

    [Fact]
    public void ConvergenceSnap_RotationStepAboveEpsilon_ReturnsCandidate()
    {
        var damped    = new Vector3(5f, 6f, 7f);
        var forward   = Vector3.Normalize(new Vector3(1f, 0f, 0f));
        var candidate = damped + new Vector3(0.0001f, 0f, 0f);
        var candFwd   = Vector3.Normalize(new Vector3(1f, 0.05f, 0f)); // ~0.05 rad turn ≫ 0.0002

        var (_, _, frozen) = RetailChaseCamera.ApplyConvergenceSnap(damped, forward, candidate, candFwd);

        Assert.False(frozen);
    }

    [Fact]
    public void Update_AtRestAfterConvergence_BoomFreezesAtExactFixedPoint()
    {
        bool  savedAlign = CameraDiagnostics.AlignToSlope;
        bool  savedColl  = CameraDiagnostics.CollideCamera;
        float savedT     = CameraDiagnostics.TranslationStiffness;
        float savedR     = CameraDiagnostics.RotationStiffness;
        try
        {
            CameraDiagnostics.AlignToSlope         = false;   // deterministic heading
            CameraDiagnostics.CollideCamera        = false;   // Position == _dampedEye
            CameraDiagnostics.TranslationStiffness = 0.45f;
            CameraDiagnostics.RotationStiffness    = 0.45f;

            var cam = new RetailChaseCamera { Distance = 2.61f, Pitch = 0.291f };

            // Frame 1 at pose A: init snaps the damped eye to A's target.
            cam.Update(Vector3.Zero, 0.5f, Vector3.Zero, true, Vector3.UnitZ, 1f / 60f);

            // Hold pose B for many frames → the boom lerps A's target → B's target.
            var posB = new Vector3(5f, 5f, 0f);
            for (int i = 0; i < 120; i++)
                cam.Update(posB, 0.5f, Vector3.Zero, true, Vector3.UnitZ, 1f / 60f);

            Vector3 a = cam.Position;
            cam.Update(posB, 0.5f, Vector3.Zero, true, Vector3.UnitZ, 1f / 60f);
            Vector3 b = cam.Position;

            Assert.Equal(a, b);   // exact — frozen, not dithering
        }
        finally
        {
            CameraDiagnostics.AlignToSlope         = savedAlign;
            CameraDiagnostics.CollideCamera        = savedColl;
            CameraDiagnostics.TranslationStiffness = savedT;
            CameraDiagnostics.RotationStiffness    = savedR;
        }
    }
}
