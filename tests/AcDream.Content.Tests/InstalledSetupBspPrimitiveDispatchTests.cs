using System.Globalization;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.Content.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class InstalledSetupBspPrimitiveDispatchTests
{
    private const int ExpectedSetups = 5935;
    private const int ExpectedWithCylinder = 678;
    private const int ExpectedSphereOnlyNoCylinder = 3605;
    private const int ExpectedWithoutAnyPrimitive = 1652;

    private const int ExpectedAffected = 172;
    private const int ExpectedAffectedCylinderBearing = 73;
    private const int ExpectedAffectedSphereBearing = 99;
    private const int ExpectedWithPhysicsBspPart = 530;

    [Fact]
    public void InstalledSetups_WithBothAPrimitiveAndAPhysicsBspPart_EmitOnlyBspShapes()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        // Production physics-BSP predicate, FlatCollisionAssetBuilder.cs:377-380.
        var physicsBspCache = new Dictionary<uint, bool>();
        bool HasPhysicsBsp(uint gfxObjId)
        {
            if (physicsBspCache.TryGetValue(gfxObjId, out bool cached))
                return cached;
            bool result =
                dats.Portal.TryGet<GfxObj>(gfxObjId, out GfxObj? gfx)
                && gfx is not null
                && gfx.Flags.HasFlag(GfxObjFlags.HasPhysics)
                && gfx.PhysicsBSP?.Root is not null
                && gfx.VertexArray is not null;
            physicsBspCache[gfxObjId] = result;
            return result;
        }

        int total = 0;
        int withCylinder = 0;
        int sphereOnly = 0;
        int withoutPrimitive = 0;
        int withPhysicsBspPart = 0;
        int affected = 0;
        int affectedCylinderBearing = 0;
        int affectedSphereBearing = 0;
        var affectedThatStillEmitAPrimitive = new List<uint>();

        foreach (uint id in dats.GetAllIdsOfType<Setup>())
        {
            if (!dats.Portal.TryGet<Setup>(id, out Setup? setup) || setup is null)
                continue;
            total++;

            bool hasCylinder = false;
            foreach (var cyl in setup.CylSpheres)
            {
                if (cyl.Radius > 0f) { hasCylinder = true; break; }
            }
            bool hasSphere = false;
            foreach (var sph in setup.Spheres)
            {
                if (sph.Radius > 0f) { hasSphere = true; break; }
            }
            bool emitsSphere = setup.CylSpheres.Count == 0 && hasSphere;

            if (hasCylinder) withCylinder++;
            else if (emitsSphere) sphereOnly++;
            else withoutPrimitive++;

            bool hasBspPart = false;
            foreach (uint partId in setup.Parts)
            {
                if (HasPhysicsBsp(partId)) { hasBspPart = true; break; }
            }
            if (hasBspPart) withPhysicsBspPart++;

            if (!hasBspPart || !(hasCylinder || emitsSphere))
                continue;

            affected++;
            if (hasCylinder) affectedCylinderBearing++;
            else affectedSphereBearing++;

            // The behaviour: for every affected Setup the production builder
            // must emit BSP shapes only.
            IReadOnlyList<ShadowShape> shapes =
                ShadowShapeBuilder.FromSetup(setup, 1f, HasPhysicsBsp);
            bool clean = shapes.Count > 0;
            foreach (ShadowShape shape in shapes)
            {
                if (shape.CollisionType != ShadowCollisionType.BSP)
                {
                    clean = false;
                    break;
                }
            }
            if (!clean)
                affectedThatStillEmitAPrimitive.Add(id);
        }

        Assert.Equal(ExpectedSetups, total);
        Assert.Equal(ExpectedWithCylinder, withCylinder);
        Assert.Equal(ExpectedSphereOnlyNoCylinder, sphereOnly);
        Assert.Equal(ExpectedWithoutAnyPrimitive, withoutPrimitive);
        Assert.Equal(ExpectedWithPhysicsBspPart, withPhysicsBspPart);

        Assert.Equal(ExpectedAffected, affected);
        Assert.Equal(ExpectedAffectedCylinderBearing, affectedCylinderBearing);
        Assert.Equal(ExpectedAffectedSphereBearing, affectedSphereBearing);

        Assert.Empty(affectedThatStillEmitAPrimitive);
    }

    private const int ExpectedPhysicsBspParts = 973;
    private const int ExpectedBspBearingSetups = 530;
    private const int ExpectedOffCentreParts = 376;      // |origin| > radius/2
    private const int ExpectedPhysicsVertices = 91689;
    private const int ExpectedWouldFailIfOriginDiscarded = 428;   // of 530
    private const int ExpectedDeepestBspPartArray = 49;

    [Fact]
    public void InstalledSetups_BspFloodSpheres_ContainTheirOwnPhysicsPolygons()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var boundsCache = new Dictionary<uint, FlatCollisionSphere?>();
        var vertexCache = new Dictionary<uint, Vector3[]>();
        FlatCollisionSphere? Bounds(uint gfxObjId)
        {
            if (boundsCache.TryGetValue(gfxObjId, out FlatCollisionSphere? cached))
                return cached;
            FlatCollisionSphere? result = null;
            Vector3[] vertices = [];
            if (dats.Portal.TryGet<GfxObj>(gfxObjId, out GfxObj? gfx)
                && gfx is not null
                && gfx.Flags.HasFlag(GfxObjFlags.HasPhysics)
                && gfx.PhysicsBSP?.Root is not null
                && gfx.VertexArray is not null
                && gfx.PhysicsBSP.Root.BoundingSphere is { } bs)
            {
                result = new FlatCollisionSphere(bs.Origin, bs.Radius);
                var collected = new List<Vector3>();
                foreach (var polygon in gfx.PhysicsPolygons.Values)
                {
                    foreach (var vertexId in polygon.VertexIds)
                    {
                        if (gfx.VertexArray.Vertices.TryGetValue(
                                (ushort)vertexId, out var vertex))
                        {
                            collected.Add(vertex.Origin);
                        }
                    }
                }
                vertices = collected.ToArray();
            }
            boundsCache[gfxObjId] = result;
            vertexCache[gfxObjId] = vertices;
            return result;
        }

        const float EntScale = 1.75f;   // not 1: a dropped scale must show up
        const float Tolerance = 1e-3f;
        int bspParts = 0;
        int bspBearingSetups = 0;
        int offCentreParts = 0;
        int physicsVertices = 0;
        int wouldFailIfOriginDiscarded = 0;
        float worstShortfall = 0f;
        uint worstShortfallSetup = 0u;
        int mostBspShapesOnOneSetup = 0;
        var uncontained = new List<uint>();

        foreach (uint id in dats.GetAllIdsOfType<Setup>())
        {
            if (!dats.Portal.TryGet<Setup>(id, out Setup? setup) || setup is null)
                continue;

            AnimationFrame? placement = null;
            if (setup.PlacementFrames.TryGetValue(Placement.Resting, out var resting))
                placement = resting;
            else if (setup.PlacementFrames.TryGetValue(Placement.Default, out var def))
                placement = def;
            else foreach (var kvp in setup.PlacementFrames) { placement = kvp.Value; break; }

            var truth = new List<Vector3>();
            for (int i = 0; i < setup.Parts.Count; i++)
            {
                uint partGfxObjId = (uint)setup.Parts[i];
                FlatCollisionSphere? b = Bounds(partGfxObjId);
                if (b is null) continue;
                bspParts++;
                if (b.Value.Origin.Length() > b.Value.Radius / 2f)
                    offCentreParts++;

                Vector3 partOrigin = Vector3.Zero;
                Quaternion partRot = Quaternion.Identity;
                if (placement is not null && i < placement.Frames.Count)
                {
                    partOrigin = placement.Frames[i].Origin;
                    partRot = placement.Frames[i].Orientation;
                }
                foreach (Vector3 vertex in vertexCache[partGfxObjId])
                {
                    truth.Add(
                        (partOrigin + Vector3.Transform(vertex, partRot)) * EntScale);
                }
            }
            if (truth.Count == 0) continue;
            bspBearingSetups++;
            physicsVertices += truth.Count;

            // Production emission, through the production bounds seam.
            IReadOnlyList<ShadowShape> shapes = ShadowShapeBuilder.FromSetup(
                setup,
                EntScale,
                id => Bounds(id) is not null,
                physicsBspBounds: id => Bounds(id) is { } sphere
                    ? ShadowPartGeometry.Create(sphere, null)
                    : (ShadowPartGeometry?)null);

            var flood = new List<(Vector3 Centre, float Radius)>();
            var floodIfOriginDiscarded = new List<(Vector3 Centre, float Radius)>();
            foreach (ShadowShape shape in shapes)
            {
                if (shape.CollisionType != ShadowCollisionType.BSP) continue;
                flood.Add((
                    shape.LocalPosition
                        + Vector3.Transform(shape.BoundsCenter, shape.LocalRotation),
                    shape.Radius));
                floodIfOriginDiscarded.Add((shape.LocalPosition, shape.Radius));
            }
            if (flood.Count > mostBspShapesOnOneSetup)
                mostBspShapesOnOneSetup = flood.Count;

            float Shortfall(List<(Vector3 Centre, float Radius)> spheres)
            {
                float worst = 0f;
                foreach (Vector3 point in truth)
                {
                    float best = float.MaxValue;
                    foreach ((Vector3 fc, float fr) in spheres)
                    {
                        float need = (point - fc).Length() - fr;
                        if (need < best) best = need;
                    }
                    if (best > worst) worst = best;
                }
                return worst;
            }

            float shortfall = Shortfall(flood);
            if (shortfall > Tolerance)
            {
                uncontained.Add(id);
                if (shortfall > worstShortfall)
                {
                    worstShortfall = shortfall;
                    worstShortfallSetup = id;
                }
            }
            if (Shortfall(floodIfOriginDiscarded) > Tolerance)
                wouldFailIfOriginDiscarded++;
        }

        Assert.Equal(ExpectedPhysicsBspParts, bspParts);
        Assert.Equal(ExpectedBspBearingSetups, bspBearingSetups);
        Assert.Equal(ExpectedOffCentreParts, offCentreParts);
        Assert.Equal(ExpectedPhysicsVertices, physicsVertices);
        Assert.Equal(ExpectedWouldFailIfOriginDiscarded, wouldFailIfOriginDiscarded);
        Assert.Equal(ExpectedDeepestBspPartArray, mostBspShapesOnOneSetup);

        // The fact.
        Assert.True(
            uncontained.Count == 0,
            $"{uncontained.Count} Setups flood from spheres that do not contain "
            + $"their own physics-polygon geometry; worst shortfall "
            + $"{worstShortfall.ToString("F3", CultureInfo.InvariantCulture)} m on "
            + $"Setup 0x{worstShortfallSetup:X8}.");
    }
}
