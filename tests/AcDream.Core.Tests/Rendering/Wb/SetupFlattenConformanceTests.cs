using System.Numerics;
using AcDream.Core.Meshing;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class SetupFlattenConformanceTests
{
    [Fact]
    public void Flatten_NoFrames_FallsBackToIdentity()
    {
        var setup = new Setup { Parts = { 0x01000001u } };
        // PlacementFrames deliberately empty — no DefaultScale entry either,
        // so scale defaults to Vector3.One and the fallback frame is
        // (Origin=Zero, Orientation=Identity) → Identity matrix.

        var refs = SetupMesh.Flatten(setup);

        Assert.Single(refs);
        Assert.Equal(0x01000001u, refs[0].GfxObjId);
        Assert.Equal(Matrix4x4.Identity, refs[0].PartTransform);
    }

    [Fact]
    public void Flatten_WithDefaultFrame_AppliesFrameOriginAndOrientation()
    {
        var setup = new Setup { Parts = { 0x01000001u } };
        setup.PlacementFrames[Placement.Default] = new AnimationFrame(1)
        {
            Frames =
            {
                new Frame
                {
                    Origin = new Vector3(10, 20, 30),
                    Orientation = Quaternion.Identity,
                },
            },
        };

        var refs = SetupMesh.Flatten(setup);

        Assert.Equal(new Vector3(10, 20, 30), refs[0].PartTransform.Translation);
    }

    [Fact]
    public void Flatten_WithRestingFrame_PrefersRestingOverDefault()
    {
        var setup = new Setup { Parts = { 0x01000001u } };
        setup.PlacementFrames[Placement.Default] = new AnimationFrame(1)
        {
            Frames = { new Frame { Origin = new Vector3(10, 20, 30), Orientation = Quaternion.Identity } },
        };
        setup.PlacementFrames[Placement.Resting] = new AnimationFrame(1)
        {
            Frames = { new Frame { Origin = new Vector3(99, 99, 99), Orientation = Quaternion.Identity } },
        };

        var refs = SetupMesh.Flatten(setup);

        Assert.Equal(new Vector3(99, 99, 99), refs[0].PartTransform.Translation);
    }

    [Fact]
    public void Flatten_WithMotionFrameOverride_PrefersOverrideOverResting()
    {
        var setup = new Setup { Parts = { 0x01000001u } };
        setup.PlacementFrames[Placement.Resting] = new AnimationFrame(1)
        {
            Frames = { new Frame { Origin = new Vector3(99, 99, 99), Orientation = Quaternion.Identity } },
        };

        var motionOverride = new AnimationFrame(1)
        {
            Frames = { new Frame { Origin = new Vector3(7, 7, 7), Orientation = Quaternion.Identity } },
        };

        var refs = SetupMesh.Flatten(setup, motionFrameOverride: motionOverride);

        Assert.Equal(new Vector3(7, 7, 7), refs[0].PartTransform.Translation);
    }

    [Fact]
    public void Flatten_DefaultScalePerPart_AppliedToTransform()
    {
        var setup = new Setup
        {
            Parts = { 0x01000001u, 0x01000002u },
            DefaultScale = { new Vector3(2, 2, 2), new Vector3(3, 3, 3) },
        };
        // No placement frames — fallback frame is identity pose; scale still applies.

        var refs = SetupMesh.Flatten(setup);

        Assert.Equal(2f, refs[0].PartTransform.M11);
        Assert.Equal(3f, refs[1].PartTransform.M11);
    }
}
