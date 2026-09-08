using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.World;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class PrivateEntityViewportRendererDrawOrderTests
{
    private static WorldEntity Entity(uint id, IReadOnlyList<MeshRef> meshRefs) => new()
    {
        Id = id,
        ServerGuid = id,
        SourceGfxObjOrSetupId = 0x0200_0001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = meshRefs,
    };

    private static readonly MeshRef[] OneMesh = [new MeshRef(0x0100_0001u, Matrix4x4.Identity)];

    [Fact]
    public void NoBackdrop_ReturnsExactlyTheMainEntity()
    {
        WorldEntity main = Entity(1u, OneMesh);

        IReadOnlyList<WorldEntity> entities =
            PrivateEntityViewportRenderer.BuildDrawEntities(backdrop: null, main);

        Assert.Same(main, Assert.Single(entities));
    }

    [Fact]
    public void BackdropPresent_ReturnsBackdropFirstThenMain()
    {
        WorldEntity backdrop = Entity(ChargenPreviewEntityBuilder.PreviewBackdropRenderId, OneMesh);
        WorldEntity main = Entity(ChargenPreviewEntityBuilder.PreviewRenderId, OneMesh);

        IReadOnlyList<WorldEntity> entities =
            PrivateEntityViewportRenderer.BuildDrawEntities(backdrop, main);

        Assert.Equal(2, entities.Count);
        Assert.Same(backdrop, entities[0]);
        Assert.Same(main, entities[1]);
    }

    [Fact]
    public void BackdropWithNoDrawableMeshes_DegradesToExactlyTheMainEntity()
    {
        WorldEntity emptyBackdrop = Entity(
            ChargenPreviewEntityBuilder.PreviewBackdropRenderId, []);
        WorldEntity main = Entity(ChargenPreviewEntityBuilder.PreviewRenderId, OneMesh);

        IReadOnlyList<WorldEntity> entities =
            PrivateEntityViewportRenderer.BuildDrawEntities(emptyBackdrop, main);

        Assert.Same(main, Assert.Single(entities));
    }
}
