using System.Numerics;
using AcDream.Core.Meshing;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Meshing;

public class SetupPartTransformsTests
{
    [Fact]
    public void Compute_PrefersRestingPlacement_OverDefault()
    {
        var setup = new Setup
        {
            Parts = { 0x01000100u, 0x01000101u },
            DefaultScale = { Vector3.One, Vector3.One },
            PlacementFrames =
            {
                [Placement.Resting] = new AnimationFrame(2)
                {
                    Frames =
                    {
                        new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity },
                        new Frame { Origin = new Vector3(0, 0, 1f), Orientation = Quaternion.Identity },
                    },
                },
                [Placement.Default] = new AnimationFrame(2)
                {
                    Frames =
                    {
                        new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity },
                        new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity },
                    },
                },
            },
        };

        var transforms = SetupPartTransforms.Compute(setup);

        Assert.Equal(2, transforms.Count);
        var probe = Vector3.Transform(Vector3.Zero, transforms[1]);
        Assert.Equal(new Vector3(0, 0, 1f), probe);
    }

    [Fact]
    public void Compute_FallsBackToDefault_WhenRestingMissing()
    {
        var setup = new Setup
        {
            Parts = { 0x01000100u },
            DefaultScale = { Vector3.One },
            PlacementFrames =
            {
                [Placement.Default] = new AnimationFrame(1)
                {
                    Frames =
                    {
                        new Frame { Origin = new Vector3(2f, 0, 0), Orientation = Quaternion.Identity },
                    },
                },
            },
        };

        var transforms = SetupPartTransforms.Compute(setup);

        Assert.Single(transforms);
        var probe = Vector3.Transform(Vector3.Zero, transforms[0]);
        Assert.Equal(new Vector3(2f, 0, 0), probe);
    }

    [Fact]
    public void Compute_ReturnsEmpty_WhenNoPlacementFrames()
    {
        var setup = new Setup
        {
            Parts = { 0x01000100u, 0x01000101u },
        };

        var transforms = SetupPartTransforms.Compute(setup);

        Assert.Empty(transforms);
    }

    [Fact]
    public void Compute_ExcludesVisualDefaultScale_FromRigidPartFrame()
    {
        var setup = new Setup
        {
            Parts = { 0x01000100u },
            DefaultScale = { new Vector3(2f, 2f, 2f) },
            PlacementFrames =
            {
                [Placement.Resting] = new AnimationFrame(1)
                {
                    Frames =
                    {
                        new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity },
                    },
                },
            },
        };

        var transforms = SetupPartTransforms.Compute(setup);

        Assert.Single(transforms);
        var probe = Vector3.Transform(new Vector3(1f, 1f, 1f), transforms[0]);
        Assert.Equal(new Vector3(1f, 1f, 1f), probe);
    }

    [Fact]
    public void Compute_ObjectScaleMultipliesOriginButNotOrientationAxes()
    {
        var setup = new Setup
        {
            Parts = { 0x01000100u },
            DefaultScale = { new Vector3(7f, 8f, 9f) },
            PlacementFrames =
            {
                [Placement.Resting] = new AnimationFrame(1)
                {
                    Frames =
                    {
                        new Frame
                        {
                            Origin = new Vector3(2f, 0f, 0f),
                            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
                        },
                    },
                },
            },
        };

        Matrix4x4 rigid = Assert.Single(SetupPartTransforms.Compute(setup, objectScale: 3f));

        Assert.Equal(new Vector3(6f, 0f, 0f), rigid.Translation);
        Vector3 rotatedAxis = Vector3.TransformNormal(Vector3.UnitX, rigid);
        Assert.InRange(rotatedAxis.X, -0.0001f, 0.0001f);
        Assert.InRange(rotatedAxis.Y, 0.9999f, 1.0001f);
    }
}
