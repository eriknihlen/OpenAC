using System.Collections.Generic;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;

namespace AcDream.Content.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class StaticSpherePopulationMeasurementTests
{
    [Fact]
    public void InstalledSetups_SphereOnlyStaticPopulation_Measured()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
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

        int totalSetups = 0;
        int sphereOnlyReachable = 0;
        var samples = new List<uint>();

        foreach (uint id in dats.GetAllIdsOfType<Setup>())
        {
            if (!dats.Portal.TryGet<Setup>(id, out Setup? setup) || setup is null)
                continue;
            totalSetups++;

            var shapes = ShadowShapeBuilder.FromSetup(setup, 1f, HasPhysicsBsp);
            if (shapes.Count == 0)
                continue;

            bool sphereOnly = true;
            foreach (var shape in shapes)
            {
                if (shape.CollisionType != ShadowCollisionType.Sphere)
                {
                    sphereOnly = false;
                    break;
                }
            }
            if (!sphereOnly)
                continue;

            sphereOnlyReachable++;
            if (samples.Count < 3)
                samples.Add(id);
        }

        Console.WriteLine("===== AP-155 static-sphere population measurement =====");
        Console.WriteLine($"Total installed Setups:                                {totalSetups}");
        Console.WriteLine($"Reach the static Sphere emission (post-fix, production dispatch): {sphereOnlyReachable}");
        Console.WriteLine("Three example object ids:");
        foreach (uint sample in samples)
            Console.WriteLine($"  0x{sample:X8}");
        Console.WriteLine("=========================================================");

        // Structural sanity only — this is a measurement, not a gate.
        Assert.True(totalSetups > 0, "Expected the installed DAT to enumerate at least one Setup.");
        Assert.True(
            sphereOnlyReachable > 0,
            "Expected at least one Setup to reach the static Sphere emission in the installed DAT.");
    }
}
