using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public sealed class SortingSphereFloodMeasurementTests
{
    private const uint LandblockId = 0xA9B40000u;

    private const int SurfaceSampleDirections = 256; // >= 200 required by the task

    [Fact]
    public void InstalledSetups_ThirdBranchSortingSphereFloodCoverage_Measured()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        // Production physics-BSP predicate — same as
        // InstalledSetupBspPrimitiveDispatchTests / FlatCollisionAssetBuilder.cs:377-380.
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

        IReadOnlyList<Vector3> sampleDirections = BuildFibonacciSphereDirections(SurfaceSampleDirections);

        int totalSetups = 0;
        int thirdBranchPopulation = 0;
        int zeroRadiusSortingSphereCount = 0;
        int emptyFloodCount = 0;
        int failuresAt1mm = 0;
        int failuresAt1cm = 0;
        float worstShortfallMetres = 0f;
        uint worstShortfallSetupId = 0u;
        float maxOvershootMetres = 0f;
        uint maxOvershootSetupId = 0u;

        // Top-3 worst shortfalls for the report (task asks for the worst three).
        var worstThree = new List<(uint SetupId, float Shortfall)>();

        foreach (uint id in dats.GetAllIdsOfType<Setup>())
        {
            if (!dats.Portal.TryGet<Setup>(id, out Setup? setup) || setup is null)
                continue;
            totalSetups++;

            IReadOnlyList<ShadowShape> shapes = ShadowShapeBuilder.FromSetup(setup, 1f, HasPhysicsBsp);

            int bspCount = 0, cylCount = 0, sphCount = 0;
            foreach (ShadowShape shape in shapes)
            {
                switch (shape.CollisionType)
                {
                    case ShadowCollisionType.BSP: bspCount++; break;
                    case ShadowCollisionType.Cylinder: cylCount++; break;
                    case ShadowCollisionType.Sphere: sphCount++; break;
                }
            }
            if (bspCount != 0 || cylCount != 0 || sphCount == 0)
                continue; // not the third branch

            thirdBranchPopulation++;

            // Register through the PRODUCTION path, at world origin / identity
            // rotation / scale 1, then read the flood spheres back through the
            // same test-visible entry the MultiPart tests use.
            var reg = new ShadowObjectRegistry();
            const uint ownerId = 0xCAFEu;
            reg.RegisterMultiPart(
                ownerId, Vector3.Zero, Quaternion.Identity,
                shapes, 0u, EntityCollisionFlags.None,
                worldOffsetX: 0f, worldOffsetY: 0f, landblockId: LandblockId);

            var flood = new List<(Vector3 Centre, float Radius)>();
            foreach (ShadowEntry entry in reg.AllEntriesForDebug())
            {
                if (entry.EntityId != ownerId) continue;
                flood.Add((entry.Position, entry.Radius));
            }
            if (flood.Count == 0)
            {
                emptyFloodCount++;
                continue;
            }

            Vector3 sortingCentre = setup.SortingSphere.Origin;
            float sortingRadius = setup.SortingSphere.Radius;

            // Reverse direction (overshoot) — meaningful regardless of the
            // sorting sphere's radius, including zero.
            foreach ((Vector3 fc, float fr) in flood)
            {
                float overshoot = (Vector3.Distance(fc, sortingCentre) + fr) - sortingRadius;
                if (overshoot > maxOvershootMetres)
                {
                    maxOvershootMetres = overshoot;
                    maxOvershootSetupId = id;
                }
            }

            if (sortingRadius <= 0f)
            {
                zeroRadiusSortingSphereCount++;
                continue;
            }

            float worst = EvaluateWorstShortfall(sortingCentre, sortingRadius, flood, sampleDirections);

            bool fail1mm = worst > 0.001f;
            bool fail1cm = worst > 0.01f;
            if (fail1mm) failuresAt1mm++;
            if (fail1cm) failuresAt1cm++;

            if (fail1mm)
            {
                worstThree.Add((id, worst));
                worstThree.Sort((a, b) => b.Shortfall.CompareTo(a.Shortfall));
                if (worstThree.Count > 3) worstThree.RemoveRange(3, worstThree.Count - 3);
            }

            if (worst > worstShortfallMetres)
            {
                worstShortfallMetres = worst;
                worstShortfallSetupId = id;
            }
        }

        Console.WriteLine("===== AP-157 sorting-sphere flood measurement =====");
        Console.WriteLine($"Total installed Setups:                 {totalSetups}");
        Console.WriteLine($"Third-branch population (0 BSP, 0 Cyl, >=1 Sphere shape): {thirdBranchPopulation}");
        Console.WriteLine($"  of which SortingSphere.Radius == 0:   {zeroRadiusSortingSphereCount} (NOT failures — retail floods from a zero sphere there too)");
        Console.WriteLine($"  of which flood sphere list was empty: {emptyFloodCount} (structural anomaly, excluded from containment tallies)");
        int evaluated = thirdBranchPopulation - zeroRadiusSortingSphereCount - emptyFloodCount;
        Console.WriteLine($"Evaluated for containment (radius > 0, non-empty flood): {evaluated}");
        Console.WriteLine($"Containment failures at 1 mm tolerance: {failuresAt1mm}");
        Console.WriteLine($"Containment failures at 1 cm tolerance: {failuresAt1cm}");
        Console.WriteLine(
            $"Worst shortfall: {worstShortfallMetres.ToString("F4", CultureInfo.InvariantCulture)} m "
            + $"on Setup 0x{worstShortfallSetupId:X8}");
        Console.WriteLine("Worst three (setup id, shortfall metres):");
        foreach ((uint setupId, float shortfall) in worstThree)
        {
            Console.WriteLine(
                $"  0x{setupId:X8}: {shortfall.ToString("F4", CultureInfo.InvariantCulture)} m");
        }
        Console.WriteLine(
            $"Max overshoot (flood extends beyond sorting sphere): "
            + $"{maxOvershootMetres.ToString("F4", CultureInfo.InvariantCulture)} m "
            + $"on Setup 0x{maxOvershootSetupId:X8}");
        Console.WriteLine("====================================================");

        // Structural sanity only — this is a measurement, not a gate.
        Assert.True(totalSetups > 0, "Expected the installed DAT to enumerate at least one Setup.");
        Assert.True(thirdBranchPopulation > 0, "Expected at least one third-branch Setup in the installed DAT.");
    }

    private static float EvaluateWorstShortfall(
        Vector3 sortingCentre,
        float sortingRadius,
        IReadOnlyList<(Vector3 Centre, float Radius)> flood,
        IReadOnlyList<Vector3> sampleDirections)
    {
        float worst = float.NegativeInfinity;

        void Probe(Vector3 point)
        {
            float best = float.MaxValue;
            foreach ((Vector3 fc, float fr) in flood)
            {
                float need = (point - fc).Length() - fr;
                if (need < best) best = need;
            }
            if (best > worst) worst = best;
        }

        Probe(sortingCentre);
        foreach (Vector3 dir in sampleDirections)
            Probe(sortingCentre + dir * sortingRadius);

        return worst;
    }

    /// <summary>Fibonacci-sphere unit directions — a simple, deterministic,
    /// near-uniform surface sample set.</summary>
    private static IReadOnlyList<Vector3> BuildFibonacciSphereDirections(int count)
    {
        var dirs = new List<Vector3>(count);
        double goldenAngle = Math.PI * (3.0 - Math.Sqrt(5.0));
        for (int i = 0; i < count; i++)
        {
            double y = 1.0 - (i / (double)(count - 1)) * 2.0; // 1 .. -1
            double radiusAtY = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
            double theta = goldenAngle * i;
            double x = Math.Cos(theta) * radiusAtY;
            double z = Math.Sin(theta) * radiusAtY;
            dirs.Add(new Vector3((float)x, (float)y, (float)z));
        }
        return dirs;
    }
}
