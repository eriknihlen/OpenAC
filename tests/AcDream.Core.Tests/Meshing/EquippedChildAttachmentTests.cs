using System.Numerics;
using AcDream.Core.Meshing;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Meshing;

public sealed class EquippedChildAttachmentTests
{
    [Fact]
    public void TryCompose_CombinesChildPlacementHoldingFrameAndAnimatedHandPart()
    {
        var parent = new Setup
        {
            HoldingLocations =
            {
                [ParentLocation.LeftHand] = new LocationType
                {
                    PartId = 1,
                    Frame = FrameAt(2, 0, 0),
                },
            },
        };
        var parentPose = new[]
        {
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(10, 0, 0),
        };
        var child = new Setup
        {
            Parts = { 0x01000001u },
            DefaultScale = { Vector3.One },
            PlacementFrames =
            {
                [Placement.LeftHand] = new AnimationFrame(1)
                {
                    Frames = { FrameAt(3, 0, 0) },
                },
            },
        };
        var template = new[] { new MeshRef(0x01000001u, Matrix4x4.Identity) };

        bool ok = EquippedChildAttachment.TryComposePose(
            parent, parentPose, child, ParentLocation.LeftHand,
            Placement.LeftHand, template, 1.0f, out EquippedChildPose pose);

        Assert.True(ok);
        Assert.Equal(12f, pose.RootLocal.Translation.X);
        Assert.Equal(3f, Assert.Single(pose.PartLocal).Translation.X);
        Assert.Equal(15f, Assert.Single(pose.AttachedParts).PartTransform.Translation.X);
    }

    [Fact]
    public void TryCompose_MissingRequestedPlacementFallsBackToDefault()
    {
        var parent = ParentWithRightHand(partId: -1, FrameAt(4, 0, 0));
        var child = new Setup
        {
            Parts = { 0x01000001u },
            PlacementFrames =
            {
                [Placement.Default] = new AnimationFrame(1)
                {
                    Frames = { FrameAt(6, 0, 0) },
                },
            },
        };

        bool ok = EquippedChildAttachment.TryCompose(
            parent, [], child, ParentLocation.RightHand,
            Placement.RightHandCombat,
            [new MeshRef(0x01000001u, Matrix4x4.Identity)],
            1.0f,
            out var result);

        Assert.True(ok);
        Assert.Equal(10f, Assert.Single(result).PartTransform.Translation.X);
    }

    [Fact]
    public void TryCompose_MissingHoldingLocationRejectsAttachment()
    {
        bool ok = EquippedChildAttachment.TryCompose(
            new Setup(), [], new Setup(), ParentLocation.RightHand,
            Placement.RightHandCombat, [], 1.0f, out var result);

        Assert.False(ok);
        Assert.Empty(result);
    }

    [Fact]
    public void TryComposePoseInto_UnavailableValidPartRejectsWithoutRootFallback()
    {
        var parent = ParentWithRightHand(partId: 1, FrameAt(4, 0, 0));
        var child = ChildWithOnePart();

        bool ok = EquippedChildAttachment.TryComposePoseInto(
            parent,
            [Matrix4x4.Identity, Matrix4x4.CreateTranslation(10, 0, 0)],
            [true, false],
            child,
            ParentLocation.RightHand,
            Placement.Default,
            [new MeshRef(0x01000001u, Matrix4x4.Identity)],
            1f,
            null,
            null,
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryComposePoseInto_OutOfRangePartUsesRetailRootAndReusesBuffers()
    {
        var parent = ParentWithRightHand(partId: 99, FrameAt(4, 0, 0));
        var child = ChildWithOnePart();
        var poseBuffer = new Matrix4x4[1];
        var meshBuffer = new MeshRef[1];

        bool ok = EquippedChildAttachment.TryComposePoseInto(
            parent,
            [Matrix4x4.CreateTranslation(10, 0, 0)],
            [true],
            child,
            ParentLocation.RightHand,
            Placement.Default,
            [new MeshRef(0x01000001u, Matrix4x4.Identity)],
            1f,
            poseBuffer,
            meshBuffer,
            out EquippedChildPose pose);

        Assert.True(ok);
        Assert.Same(poseBuffer, pose.PartLocal);
        Assert.Same(meshBuffer, pose.AttachedParts);
        Assert.Equal(4f, pose.RootLocal.Translation.X);
    }

    [Fact]
    public void NestedChildRootsComposeThroughImmediateParentWorldPose()
    {
        Matrix4x4 topLevelWorld = Matrix4x4.CreateTranslation(100, 0, 0);
        Matrix4x4 childRootLocal = Matrix4x4.CreateTranslation(10, 0, 0);
        Matrix4x4 grandchildRootLocal = Matrix4x4.CreateTranslation(2, 0, 0);

        Matrix4x4 childWorld = childRootLocal * topLevelWorld;
        Matrix4x4 grandchildWorld = grandchildRootLocal * childWorld;

        Assert.Equal(112f, grandchildWorld.Translation.X);
    }

    [Fact]
    public void ChildRigidPoseExcludesDefaultScaleAndScalesOnlyOrigin()
    {
        var parent = ParentWithRightHand(partId: -1, FrameAt(4, 0, 0));
        var child = new Setup
        {
            Parts = { 0x01000001u },
            DefaultScale = { new Vector3(5f, 6f, 7f) },
            PlacementFrames =
            {
                [Placement.Default] = new AnimationFrame(1)
                {
                    Frames = { FrameAt(3, 0, 0) },
                },
            },
        };

        Assert.True(EquippedChildAttachment.TryComposePose(
            parent,
            Array.Empty<Matrix4x4>(),
            child,
            ParentLocation.RightHand,
            Placement.Default,
            [new MeshRef(0x01000001u, Matrix4x4.Identity)],
            childScale: 2f,
            out EquippedChildPose pose));

        Matrix4x4 rigid = Assert.Single(pose.PartLocal);
        Assert.Equal(new Vector3(6f, 0f, 0f), rigid.Translation);
        Assert.Equal(Vector3.One, new Vector3(rigid.M11, rigid.M22, rigid.M33));
        Matrix4x4 visual = Assert.Single(pose.AttachedParts).PartTransform;
        Assert.Equal(new Vector3(10f, 12f, 14f), new Vector3(visual.M11, visual.M22, visual.M33));
    }

    private static Setup ChildWithOnePart() => new()
    {
        Parts = { 0x01000001u },
        DefaultScale = { Vector3.One },
        PlacementFrames =
        {
            [Placement.Default] = new AnimationFrame(1)
            {
                Frames = { FrameAt(1, 0, 0) },
            },
        },
    };

    private static Setup ParentWithRightHand(int partId, Frame frame) => new()
    {
        HoldingLocations =
        {
            [ParentLocation.RightHand] = new LocationType
            {
                PartId = partId,
                Frame = frame,
            },
        },
    };

    private static Frame FrameAt(float x, float y, float z) => new()
    {
        Origin = new Vector3(x, y, z),
        Orientation = Quaternion.Identity,
    };
}
