using System;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics.Motion;

public class CSequencePhysicsTests
{
    private sealed class NullLoader : IAnimationLoader
    {
        public DatReaderWriter.DBObjs.Animation? LoadAnimation(uint id) => null;
    }

    private static void AssertVec(Vector3 expected, Vector3 actual, float tol = 1e-5f)
    {
        Assert.True((expected - actual).Length() < tol,
            $"expected {expected}, got {actual}");
    }

    [Fact]
    public void ApplyPhysics_PositiveSign_AddsVelocityTimesQuantum()
    {
        var seq = new CSequence(new NullLoader());
        seq.SetVelocity(new Vector3(2, 0, 0));
        var frame = new Frame { Origin = new Vector3(1, 1, 1), Orientation = Quaternion.Identity };

        seq.ApplyPhysics(frame, quantum: 0.5, signSource: 1.0);

        AssertVec(new Vector3(2, 1, 1), frame.Origin);
    }

    [Fact]
    public void ApplyPhysics_CopySign_MagnitudeFromQuantum_SignFromSource()
    {
        var seq = new CSequence(new NullLoader());
        seq.SetVelocity(new Vector3(2, 0, 0));

        var f1 = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };
        seq.ApplyPhysics(f1, quantum: -0.5, signSource: 1.0);
        AssertVec(new Vector3(1, 0, 0), f1.Origin);

        var f2 = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };
        seq.ApplyPhysics(f2, quantum: 0.5, signSource: -1.0);
        AssertVec(new Vector3(-1, 0, 0), f2.Origin);
    }

    [Fact]
    public void ApplyPhysics_OmegaRotatesFrame_GlobalZ()
    {
        // omega = (0,0,π) for 0.5s → 90° about global Z: local +X maps to +Y.
        var seq = new CSequence(new NullLoader());
        seq.SetOmega(new Vector3(0, 0, MathF.PI));
        var frame = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };

        seq.ApplyPhysics(frame, quantum: 0.5, signSource: 1.0);

        AssertVec(new Vector3(0, 1, 0), Vector3.Transform(Vector3.UnitX, frame.Orientation));
    }

    [Fact]
    public void GRotate_ComposesInGlobalSpace()
    {
        // Frame already rotated 90° about Z; grotate 90° about GLOBAL X.
        // local +X: q maps it to +Y; global X-rot then maps +Y to +Z.
        var frame = new Frame
        {
            Origin = Vector3.Zero,
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
        };

        FrameOps.GRotate(frame, new Vector3(MathF.PI / 2f, 0, 0));

        AssertVec(new Vector3(0, 0, 1), Vector3.Transform(Vector3.UnitX, frame.Orientation));
    }

    [Fact]
    public void Rotate_LocalVector_MappedThroughOrientation()
    {
        // Frame rotated 90° about Z: a LOCAL X-axis rotation is a GLOBAL
        // Y-axis rotation. rotate(local πX/2) on this frame must equal
        // grotate(global πY/2).
        var q0 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var viaLocal = new Frame { Origin = Vector3.Zero, Orientation = q0 };
        FrameOps.Rotate(viaLocal, new Vector3(MathF.PI / 2f, 0, 0));

        var viaGlobal = new Frame { Origin = Vector3.Zero, Orientation = q0 };
        FrameOps.GRotate(viaGlobal, new Vector3(0, MathF.PI / 2f, 0));

        AssertVec(
            Vector3.Transform(Vector3.UnitX, viaGlobal.Orientation),
            Vector3.Transform(Vector3.UnitX, viaLocal.Orientation));
        AssertVec(
            Vector3.Transform(Vector3.UnitZ, viaGlobal.Orientation),
            Vector3.Transform(Vector3.UnitZ, viaLocal.Orientation));
    }

    [Fact]
    public void GRotate_TinyRotation_Skipped()
    {
        var frame = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };
        FrameOps.GRotate(frame, new Vector3(1e-5f, 0, 0));
        Assert.Equal(Quaternion.Identity, frame.Orientation);
    }

    [Fact]
    public void ApplyPhysics_ZeroPhysics_NoChange()
    {
        var seq = new CSequence(new NullLoader());
        var frame = new Frame
        {
            Origin = new Vector3(3, 4, 5),
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f),
        };
        var beforeO = frame.Origin;
        var beforeQ = frame.Orientation;

        seq.ApplyPhysics(frame, 1.0, 1.0);

        Assert.Equal(beforeO, frame.Origin);
        Assert.Equal(beforeQ, frame.Orientation);
    }
}
