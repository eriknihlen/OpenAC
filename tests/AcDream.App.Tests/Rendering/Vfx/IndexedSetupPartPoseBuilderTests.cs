using System.Numerics;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Rendering.Vfx;

public sealed class IndexedSetupPartPoseBuilderTests
{
    [Fact]
    public void Build_DoesNotCollapseMissingMiddleDrawable()
    {
        var setup = new Setup
        {
            Parts = { 0x01000001u, 0x01000002u, 0x01000003u },
        };
        var entity = new WorldEntity
        {
            Id = 7u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs =
            [
                new MeshRef(0x01000001u, Matrix4x4.CreateTranslation(1, 0, 0)),
                new MeshRef(0x01000003u, Matrix4x4.CreateTranslation(3, 0, 0)),
            ],
        };

        var indexed = IndexedSetupPartPoseBuilder.Build(setup, entity);

        Assert.Equal([true, false, true], indexed.Available);
        Assert.Equal(Matrix4x4.Identity, indexed.Poses[0]);
        Assert.Equal(Matrix4x4.Identity, indexed.Poses[2]);
    }
}
