using System;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Rendering;

[Collection(CameraDiagnosticsCollection.Name)]
public class Issue181CameraParkStabilityTests
{
    private readonly ITestOutputHelper _out;
    public Issue181CameraParkStabilityTests(ITestOutputHelper output) => _out = output;

    private sealed class PassthroughProbe : ICameraCollisionProbe
    {
        public CameraSweepResult SweepEye(Vector3 pivot, Vector3 desiredEye, uint cellId, uint selfEntityId, Vector3 playerPos)
            => new(desiredEye, cellId);
    }

    [Fact]
    public void ParkedCamera_StaticInputs_ReachesBitStableFixedPoint()
    {
        bool  savedAlign = CameraDiagnostics.AlignToSlope;
        bool  savedColl  = CameraDiagnostics.CollideCamera;
        float savedT     = CameraDiagnostics.TranslationStiffness;
        float savedR     = CameraDiagnostics.RotationStiffness;
        try
        {
            CameraDiagnostics.AlignToSlope         = true;   // production default
            CameraDiagnostics.CollideCamera        = true;
            CameraDiagnostics.TranslationStiffness = 0.45f;
            CameraDiagnostics.RotationStiffness    = 0.45f;

            var cam = new RetailChaseCamera { CollisionProbe = new PassthroughProbe() };

            var playerPos = new Vector3(49.5f, -39.9f, -5.9f);
            float yaw     = 1.83f;
            float dt      = 1f / 1500f;

            void Step() => cam.Update(
                playerPosition: playerPos,
                playerYaw: yaw,
                playerVelocity: Vector3.Zero,
                isOnGround: true,
                contactPlaneNormal: Vector3.UnitZ,
                dt: dt,
                cellId: 0x8A020142u,
                selfEntityId: 0x5);

            for (int i = 0; i < 20000; i++) Step();

            Vector3 a = cam.Position;
            var fwdA = cam.View;   // full view matrix — includes the forward half

            float maxDelta = 0f;
            Vector3 prev = a;
            bool viewChanged = false;
            for (int i = 0; i < 2000; i++)
            {
                Step();
                maxDelta = MathF.Max(maxDelta, Vector3.Distance(cam.Position, prev));
                prev = cam.Position;
                if (cam.View != fwdA) viewChanged = true;
            }

            _out.WriteLine(FormattableString.Invariant(
                $"post-convergence maxConsecDelta={maxDelta * 1e6f:F2}um viewChanged={viewChanged} pos=({cam.Position.X:F7},{cam.Position.Y:F7},{cam.Position.Z:F7})"));

            Assert.True(maxDelta == 0f,
                $"parked camera must be a bit-exact fixed point, wandered up to {maxDelta * 1e6f:F1}um/frame");
            Assert.False(viewChanged, "view matrix must be frozen at park");
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
