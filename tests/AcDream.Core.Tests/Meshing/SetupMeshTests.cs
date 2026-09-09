using System.Numerics;
using AcDream.Core.Meshing;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Meshing;

public class SetupMeshTests
{
    [Fact]
    public void Flatten_SinglePartSetup_YieldsOneMeshRef()
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
                        new Frame
                        {
                            Origin = new Vector3(0, 0, 0),
                            Orientation = Quaternion.Identity,
                        },
                    },
                },
            },
        };

        var refs = SetupMesh.Flatten(setup);

        var single = Assert.Single(refs);
        Assert.Equal(0x01000100u, single.GfxObjId);
        // Identity-ish transform
        Assert.Equal(Matrix4x4.Identity, single.PartTransform);
    }

    [Fact]
    public void Flatten_TwoPartSetup_YieldsTwoMeshRefs()
    {
        var setup = new Setup
        {
            Parts = { 0x01000100u, 0x01000200u },
            DefaultScale = { Vector3.One, Vector3.One },
            PlacementFrames =
            {
                [Placement.Default] = new AnimationFrame(2)
                {
                    Frames =
                    {
                        new Frame { Origin = new(0, 0, 0), Orientation = Quaternion.Identity },
                        new Frame { Origin = new(10, 0, 0), Orientation = Quaternion.Identity },
                    },
                },
            },
        };

        var refs = SetupMesh.Flatten(setup);

        Assert.Equal(2, refs.Count);
        Assert.Equal(0x01000100u, refs[0].GfxObjId);
        Assert.Equal(0x01000200u, refs[1].GfxObjId);
        Assert.Equal(10f, refs[1].PartTransform.Translation.X);
    }

    [Fact]
    public void Flatten_PartScale_IsAppliedToTransform()
    {
        var setup = new Setup
        {
            Parts = { 0x01000100u },
            DefaultScale = { new Vector3(2, 3, 4) },
            PlacementFrames =
            {
                [Placement.Default] = new AnimationFrame(1)
                {
                    Frames = { new Frame { Orientation = Quaternion.Identity } },
                },
            },
        };

        var refs = SetupMesh.Flatten(setup);

        // The transform's M11 = 2 (scale X), M22 = 3, M33 = 4
        Assert.Equal(2f, refs[0].PartTransform.M11);
        Assert.Equal(3f, refs[0].PartTransform.M22);
        Assert.Equal(4f, refs[0].PartTransform.M33);
    }

    [Fact]
    public void Flatten_MissingPlacementFrame_UsesIdentity()
    {
        var setup = new Setup
        {
            Parts = { 0x01000100u },
            DefaultScale = { Vector3.One },
            // PlacementFrames deliberately empty
        };

        var refs = SetupMesh.Flatten(setup);

        Assert.Single(refs);
        Assert.Equal(Matrix4x4.Identity, refs[0].PartTransform);
    }
}
