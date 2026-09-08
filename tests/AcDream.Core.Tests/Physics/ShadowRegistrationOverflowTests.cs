using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ShadowRegistrationOverflowTests
{
    private const uint EntityA = 0x40F68221u;
    private const uint EntityB = 0xC0F68221u;

    private const uint LbId = 0xF6820000u;
    private const float OffX = 0f, OffY = 0f;


    [Fact]
    public void OldPartIdScheme_OverflowsUint32_AndCollides()
    {
        // entity.Id * 256u == entity.Id << 8, truncated to 32 bits → the prefix
        // byte falls off the top and the two distinct entities alias.
        uint oldPartA = unchecked(EntityA * 256u);
        uint oldPartB = unchecked(EntityB * 256u);
        Assert.Equal(0xF6822100u, oldPartA);
        Assert.Equal(oldPartA, oldPartB);
        Assert.NotEqual(EntityA, EntityB);         // …yet the entities ARE distinct
    }

    // ── The bug: old per-part Register loses one registration ─────────────

    private static ShadowShape Cyl(Vector3 local) => ShadowShape.Cylinder(
        gfxObjId: 0u, localPosition: local, localRotation: Quaternion.Identity,
        scale: 1f, radius: 1f, cylHeight: 2f);

    [Fact]
    public void OldPerPartRegister_CollidingIds_SecondSilentlyOverwritesFirst()
    {
        var reg = new ShadowObjectRegistry();

        var posA = new Vector3(12f, 12f, 50f);
        var posB = new Vector3(42f, 12f, 50f);

        reg.Register(unchecked(EntityA * 256u), 0u, posA, Quaternion.Identity, 1f,
                     OffX, OffY, LbId, ShadowCollisionType.Cylinder, 2f);
        reg.Register(unchecked(EntityB * 256u), 0u, posB, Quaternion.Identity, 1f,
                     OffX, OffY, LbId, ShadowCollisionType.Cylinder, 2f);

        Assert.Empty(reg.GetObjectsInCell(LbId | 1u));
        Assert.NotEmpty(reg.GetObjectsInCell(LbId | 9u));
        Assert.Equal(1, reg.TotalRegistered);        // one silently lost
    }

    // ── The fix: RegisterMultiPart keys on the unique entity.Id ───────────

    [Fact]
    public void RegisterMultiPart_CollidingLowBitsIds_BothSurvive()
    {
        var reg = new ShadowObjectRegistry();

        var posA = new Vector3(12f, 12f, 50f);
        var posB = new Vector3(42f, 12f, 50f);

        reg.RegisterMultiPart(EntityA, posA, Quaternion.Identity,
                              new[] { Cyl(Vector3.Zero) }, 0u, EntityCollisionFlags.None,
                              OffX, OffY, LbId, isStatic: true);
        reg.RegisterMultiPart(EntityB, posB, Quaternion.Identity,
                              new[] { Cyl(Vector3.Zero) }, 0u, EntityCollisionFlags.None,
                              OffX, OffY, LbId, isStatic: true);

        Assert.Contains(reg.GetObjectsInCell(LbId | 1u), e => e.EntityId == EntityA);
        Assert.Contains(reg.GetObjectsInCell(LbId | 9u), e => e.EntityId == EntityB);
        Assert.Equal(2, reg.TotalRegistered);
    }

    // ── The builder: one BSP shape per BSP part; shells + no-BSP excluded ──

    private static GfxObjPhysics BspGfx(float radius, float centerZ = 0.75f)
    {
        var leaf = new PhysicsBSPNode { Type = BSPNodeType.Leaf };
        return new GfxObjPhysics
        {
            BSP             = new PhysicsBSPTree { Root = leaf },
            BoundingSphere  = new Sphere
                { Origin = new Vector3(0f, 0f, centerZ), Radius = radius },
            Resolved        = new Dictionary<ushort, ResolvedPolygon>(),
            PhysicsPolygons = new Dictionary<ushort, Polygon>(),
            Vertices        = new VertexArray(),
        };
    }

    /// <summary>Flat-form fixture — the production storage since I6/I7.</summary>
    private static GfxObjPhysics FlatBspGfx(float radius, float centerZ)
    {
        var node = new FlatPhysicsBspNode(
            BSPNodeType.Leaf, default, -1, -1, 0, 0,
            new FlatCollisionSphere(new Vector3(0f, 0f, centerZ), radius),
            new FlatIndexRange(0, 0));
        GfxObjPhysics phys = BspGfx(radius, centerZ);
        phys.FlatPhysicsBsp = new FlatPhysicsBsp(
            0,
            ImmutableArray.Create(node),
            ImmutableArray<int>.Empty,
            FlatPolygonTable.Empty);
        return phys;
    }

    [Fact]
    public void FromLandblockBspParts_OneShapePerBspPart_LocalTransformPreserved()
    {
        var meshRefs = new[]
        {
            new MeshRef(0x01000AC5u, Matrix4x4.CreateTranslation(0f, 0.5f, 0.4f)),
            new MeshRef(0x01000AC5u, Matrix4x4.CreateTranslation(0f, 1.0f, 0.8f)),
            new MeshRef(0x0BADBADu,  Matrix4x4.Identity),   // no physics BSP → skipped
        };

        var shapes = ShadowShapeBuilder.FromLandblockBspParts(
            meshRefs, isBuildingShell: false,
            getGfxObj: id => id == 0x01000AC5u ? BspGfx(1.05f) : null);

        Assert.Equal(2, shapes.Count);   // only the two BSP-bearing parts
        Assert.All(shapes, s => Assert.Equal(ShadowCollisionType.BSP, s.CollisionType));
        Assert.All(shapes, s => Assert.Equal(0x01000AC5u, s.GfxObjId));
        Assert.Contains(shapes, s => Vector3.Distance(s.LocalPosition, new Vector3(0f, 0.5f, 0.4f)) < 1e-4f);
        Assert.Contains(shapes, s => Vector3.Distance(s.LocalPosition, new Vector3(0f, 1.0f, 0.8f)) < 1e-4f);
        // Radius = local BoundingSphere radius × part scale (1.0).
        Assert.All(shapes, s => Assert.Equal(1.05f, s.Radius, 3));
    }

    [Fact]
    public void FromLandblockBspParts_CarriesTheScaledRootSphereCentre()
    {
        Matrix4x4 halfScale = Matrix4x4.CreateScale(0.5f)
            * Matrix4x4.CreateTranslation(0f, 2f, 0f);

        var flat = ShadowShapeBuilder.FromLandblockBspParts(
            [new MeshRef(0x01000AC5u, halfScale)],
            isBuildingShell: false,
            getGfxObj: _ => FlatBspGfx(4f, centerZ: 3f));
        ShadowShape flatShape = Assert.Single(flat);
        Assert.Equal(0.5f, flatShape.Scale, 3);
        Assert.Equal(2f, flatShape.Radius, 3);                       // 4 * 0.5
        Assert.Equal(new Vector3(0f, 0f, 1.5f), flatShape.BoundsCenter);  // 3 * 0.5

        // Graph fallback (fixtures without a flat BSP) takes the same path.
        var graph = ShadowShapeBuilder.FromLandblockBspParts(
            [new MeshRef(0x01000AC5u, halfScale)],
            isBuildingShell: false,
            getGfxObj: _ => BspGfx(4f, centerZ: 3f));
        ShadowShape graphShape = Assert.Single(graph);
        Assert.Equal(new Vector3(0f, 0f, 1.5f), graphShape.BoundsCenter);
    }

    [Fact]
    public void FromLandblockBspParts_BuildingShell_ReturnsEmpty()
    {
        var meshRefs = new[] { new MeshRef(0x01000AC5u, Matrix4x4.Identity) };
        var shapes = ShadowShapeBuilder.FromLandblockBspParts(
            meshRefs, isBuildingShell: true, getGfxObj: _ => BspGfx(1f));
        Assert.Empty(shapes);
    }
}
