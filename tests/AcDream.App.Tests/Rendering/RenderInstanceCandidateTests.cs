using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering.Scene;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderInstanceCandidateTests
{
    [Fact]
    public void FromWorldEntity_CopiesDispatcherFactsWithoutRetainingEntity()
    {
        Quaternion rotation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            MathF.PI / 2f);
        var entity = new WorldEntity
        {
            Id = 41,
            ServerGuid = 0x8000_0041,
            SourceGfxObjOrSetupId = 0x0200_0041,
            Position = new Vector3(1f, 2f, 3f),
            Rotation = rotation,
            MeshRefs =
            [
                new MeshRef(0x0100_0001, Matrix4x4.Identity),
                new MeshRef(
                    0x0100_0002,
                    Matrix4x4.CreateTranslation(4f, 0f, 0f)),
            ],
            ParentCellId = 0xA9B4_0170,
            Scale = 1.25f,
            IsBuildingShell = true,
        };

        RenderInstanceCandidate candidate =
            RenderInstanceCandidate.FromWorldEntity(
                entity,
                animated: true,
                meshPartCount: 2,
                tupleLandblockId: 0x1122_FFFF);

        Assert.Equal(entity.Id, candidate.LocalEntityId);
        Assert.Equal(entity.ServerGuid, candidate.ServerGuid);
        Assert.Equal(entity.SourceGfxObjOrSetupId, candidate.SourceId);
        Assert.Equal(entity.ParentCellId, candidate.ParentCell);
        Assert.Equal(
            Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(entity.Position),
            candidate.RootWorld);
        Assert.Equal(entity.AabbMin, candidate.Bounds.Minimum);
        Assert.Equal(entity.AabbMax, candidate.Bounds.Maximum);
        Assert.Equal(1.25f, candidate.Scale);
        Assert.True(candidate.IsBuildingShell);
        Assert.True(candidate.Animated);
        Assert.Equal(2, candidate.MeshPartCount);
        Assert.Equal(0xA9B4_FFFFu, candidate.CacheLandblockId);
        Assert.DoesNotContain(
            typeof(RenderInstanceCandidate).GetFields(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic),
            field => field.FieldType == typeof(WorldEntity));
    }

    [Fact]
    public void OutdoorCandidate_KeepsTupleCacheHintSeparate()
    {
        var entity = new WorldEntity
        {
            Id = 42,
            ServerGuid = 0,
            SourceGfxObjOrSetupId = 0x0100_0042,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs =
            [
                new MeshRef(0x0100_0042, Matrix4x4.Identity),
            ],
        };

        RenderInstanceCandidate candidate =
            RenderInstanceCandidate.FromWorldEntity(
                entity,
                animated: false,
                meshPartCount: 1,
                tupleLandblockId: 0xA9B5_FFFF);

        Assert.Null(candidate.ParentCell);
        Assert.Equal(0xA9B5_FFFFu, candidate.CacheLandblockId);
    }
}
