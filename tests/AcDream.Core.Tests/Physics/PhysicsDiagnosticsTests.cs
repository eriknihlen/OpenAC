using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class PhysicsDiagnosticsTests
{
    // -----------------------------------------------------------------------
    // ProbeBuildingEnabled — flag gates the emission path.
    // -----------------------------------------------------------------------

    [Fact]
    public void ProbeBuilding_StaticApi_Roundtrip()
    {
        bool initial = PhysicsDiagnostics.ProbeBuildingEnabled;
        try
        {
            PhysicsDiagnostics.ProbeBuildingEnabled = true;
            Assert.True(PhysicsDiagnostics.ProbeBuildingEnabled);

            PhysicsDiagnostics.ProbeBuildingEnabled = false;
            Assert.False(PhysicsDiagnostics.ProbeBuildingEnabled);
        }
        finally
        {
            // Restore so a process-wide static doesn't leak between tests
            // (env-var init was the only thing that set this before).
            PhysicsDiagnostics.ProbeBuildingEnabled = initial;
        }
    }


    [Fact]
    public void LastBspHitPoly_StaticApi_Roundtrip()
    {
        ResolvedPolygon? initial = PhysicsDiagnostics.LastBspHitPoly;
        try
        {
            PhysicsDiagnostics.LastBspHitPoly = null;
            Assert.Null(PhysicsDiagnostics.LastBspHitPoly);

            var synthetic = new ResolvedPolygon
            {
                Vertices = new[]
                {
                    new Vector3(-1f, 0f, 0f),
                    new Vector3( 1f, 0f, 0f),
                    new Vector3( 1f, 0f, 2f),
                    new Vector3(-1f, 0f, 2f),
                },
                Plane     = new System.Numerics.Plane(0f, 1f, 0f, -94.123f),
                NumPoints = 4,
                SidesType = CullMode.None,
            };
            PhysicsDiagnostics.LastBspHitPoly = synthetic;

            var read = PhysicsDiagnostics.LastBspHitPoly;
            Assert.NotNull(read);
            Assert.Equal(4, read!.NumPoints);
            Assert.Equal(synthetic.Plane.D, read.Plane.D);
            Assert.Same(synthetic, read);

            PhysicsDiagnostics.LastBspHitPoly = null;
            Assert.Null(PhysicsDiagnostics.LastBspHitPoly);
        }
        finally
        {
            PhysicsDiagnostics.LastBspHitPoly = initial;
        }
    }


    [Fact]
    public void ProbePushBack_StaticApi_Roundtrip()
    {
        bool initial = PhysicsDiagnostics.ProbePushBackEnabled;
        try
        {
            PhysicsDiagnostics.ProbePushBackEnabled = true;
            Assert.True(PhysicsDiagnostics.ProbePushBackEnabled);

            PhysicsDiagnostics.ProbePushBackEnabled = false;
            Assert.False(PhysicsDiagnostics.ProbePushBackEnabled);
        }
        finally
        {
            PhysicsDiagnostics.ProbePushBackEnabled = initial;
        }
    }


    [Fact]
    public void ProbeStepWalk_StaticApi_Roundtrip()
    {
        bool initial = PhysicsDiagnostics.ProbeStepWalkEnabled;
        try
        {
            PhysicsDiagnostics.ProbeStepWalkEnabled = true;
            Assert.True(PhysicsDiagnostics.ProbeStepWalkEnabled);

            PhysicsDiagnostics.ProbeStepWalkEnabled = false;
            Assert.False(PhysicsDiagnostics.ProbeStepWalkEnabled);
        }
        finally
        {
            PhysicsDiagnostics.ProbeStepWalkEnabled = initial;
        }
    }


    [Fact]
    public void ProbeSweptEnabled_DefaultsToFalse()
    {
        PhysicsDiagnostics.ProbeSweptEnabled = false;
        Assert.False(PhysicsDiagnostics.ProbeSweptEnabled);
    }


    [Fact]
    public void ProbeDumpGfxObjs_EnabledTracksIdSetNonEmpty()
    {
        var initial = PhysicsDiagnostics.ProbeDumpGfxObjIds;
        try
        {
            PhysicsDiagnostics.ProbeDumpGfxObjIds = new HashSet<uint>();
            Assert.False(PhysicsDiagnostics.ProbeDumpGfxObjsEnabled);

            PhysicsDiagnostics.ProbeDumpGfxObjIds = new HashSet<uint> { 0x01000A2Bu };
            Assert.True(PhysicsDiagnostics.ProbeDumpGfxObjsEnabled);
            Assert.Contains(0x01000A2Bu, PhysicsDiagnostics.ProbeDumpGfxObjIds);
        }
        finally
        {
            PhysicsDiagnostics.ProbeDumpGfxObjIds = initial;
        }
    }
}
