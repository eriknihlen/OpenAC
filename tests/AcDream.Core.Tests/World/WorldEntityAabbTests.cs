using System.Numerics;
using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.World;

public class WorldEntityAabbTests
{
    [Fact]
    public void Aabb_DefaultRadius_PositionPlusMinus5()
    {
        var entity = new WorldEntity
        {
            Id = 1,
            SourceGfxObjOrSetupId = 0,
            Position = new Vector3(10, 20, 30),
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>(),
        };
        entity.RefreshAabb();

        Assert.Equal(new Vector3(5, 15, 25), entity.AabbMin);
        Assert.Equal(new Vector3(15, 25, 35), entity.AabbMax);
    }

    [Fact]
    public void Aabb_MultiPartOffsets_CoverTheParts()
    {
        var entity = new WorldEntity
        {
            Id = 1,
            SourceGfxObjOrSetupId = 0x020003F2,
            Position = new Vector3(300, -132, 112),
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = new[]
            {
                new MeshRef(0x01000E2A, Matrix4x4.CreateTranslation(0f, 3f, 1.55f)),
                new MeshRef(0x01000E2D, Matrix4x4.CreateTranslation(-1.75f, 3f, 15.15f)),
            },
        };
        entity.RefreshAabb();

        // Top part sits at z = 112 + 15.15 = 127.15 — must be inside the box.
        Assert.True(entity.AabbMax.Z >= 127.15f,
            $"box top {entity.AabbMax.Z} must cover the highest part (z=127.15)");
        Assert.True(entity.AabbMin.Z <= 107f && entity.AabbMax.X >= 305f);
    }

    [Fact]
    public void Aabb_LocalBounds_TallSinglePart_Covered()
    {
        var entity = new WorldEntity
        {
            Id = 1,
            SourceGfxObjOrSetupId = 0x01000001,
            Position = new Vector3(100, 100, 50),
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = new[] { new MeshRef(0x01000001, Matrix4x4.Identity) },
        };
        entity.SetLocalBounds(new Vector3(-1, -1, 0), new Vector3(1, 1, 12));
        entity.RefreshAabb();

        // Mesh top = position.Z (50) + local 12 = 62; the old ±5 anchor box topped out at 55.
        Assert.True(entity.AabbMax.Z >= 62f, $"box top {entity.AabbMax.Z} must cover mesh top (z=62)");
        Assert.True(entity.AabbMin.Z <= 50f);
    }

    [Fact]
    public void Aabb_LocalBounds_FollowRotation()
    {
        // A mesh extending 12 m along +Y, entity rotated 90° about Z -> the extent
        // now points along -X (or +X depending on sign); the world box must follow.
        var entity = new WorldEntity
        {
            Id = 1,
            SourceGfxObjOrSetupId = 0x01000001,
            Position = Vector3.Zero,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, System.MathF.PI / 2f),
            MeshRefs = new[] { new MeshRef(0x01000001, Matrix4x4.Identity) },
        };
        entity.SetLocalBounds(new Vector3(-1, 0, -1), new Vector3(1, 12, 1));
        entity.RefreshAabb();

        // Rotating +Y by +90° about Z lands on -X: the box must extend ≥12 m on X
        // (one side), and no longer require 12 m on Y.
        bool coversX = entity.AabbMin.X <= -12f || entity.AabbMax.X >= 12f;
        Assert.True(coversX, $"rotated extent must show on X axis (box X [{entity.AabbMin.X},{entity.AabbMax.X}])");
        Assert.True(entity.AabbMax.Y < 12f, "the unrotated +Y extent must not persist after rotation");
    }

    [Fact]
    public void Aabb_LocalBoundsAccumulator_UnionsTransformedParts()
    {
        var acc = new AcDream.Core.Meshing.LocalBoundsAccumulator();
        Assert.False(acc.TryGet(out _, out _));

        acc.Add(Matrix4x4.Identity, (new Vector3(-0.5f), new Vector3(0.5f)));
        acc.Add(Matrix4x4.CreateTranslation(3, 3, 15), (new Vector3(-0.5f), new Vector3(0.5f)));

        Assert.True(acc.TryGet(out var min, out var max));
        Assert.Equal(-0.5f, min.Z, 3);
        Assert.Equal(15.5f, max.Z, 3);
        Assert.Equal(3.5f, max.X, 3);
    }

    [Fact]
    public void Aabb_DirtyFlag_SetByMutator_ClearedByRefresh()
    {
        var entity = new WorldEntity
        {
            Id = 1,
            SourceGfxObjOrSetupId = 0,
            Position = new Vector3(10, 20, 30),
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>(),
        };
        entity.RefreshAabb();
        Assert.False(entity.AabbDirty);

        entity.SetPosition(new Vector3(100, 200, 300));
        Assert.True(entity.AabbDirty);

        entity.RefreshAabb();
        Assert.False(entity.AabbDirty);
        Assert.Equal(new Vector3(95, 195, 295), entity.AabbMin);
    }
}
