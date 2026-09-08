using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;

namespace AcDream.Content.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class InstalledSetupCollisionReachabilityTests
{
    private const int ExpectedSetups = 5935;
    private const int ExpectedWithCylinder = 678;
    private const int ExpectedSphereOnlyNoCylinder = 3605;
    private const int ExpectedWithoutAnyPrimitive = 1652;
    private const int ExpectedWithSummaryRadius = 4282;

    [Fact]
    public void InstalledSetups_NeverReachTheDeletedRadiusFallback()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        int total = 0;
        int withCylinder = 0;
        int sphereOnly = 0;
        int withoutPrimitive = 0;
        int withRadius = 0;
        var fallbackReachable = new List<uint>();
        var fallbackReachableWideGuard = new List<uint>();

        foreach (uint id in dats.GetAllIdsOfType<Setup>())
        {
            if (!dats.Portal.TryGet<Setup>(id, out Setup? setup)
                || setup is null)
            {
                continue;
            }

            FlatSetupCollision flat =
                FlatCollisionAssetBuilder.FlattenSetup(setup);
            total++;

            bool hasCylinder = flat.Cylinders.Length > 0;
            bool hasSphere = flat.Spheres.Length > 0;
            if (hasCylinder)
                withCylinder++;
            else if (hasSphere)
                sphereOnly++;
            else
                withoutPrimitive++;

            if (flat.Radius > 0.0001f)
                withRadius++;

            if (!hasCylinder && !hasSphere && flat.Radius > 0.0001f)
                fallbackReachable.Add(id);
            if (!hasCylinder && !hasSphere && flat.Radius > 0f)
                fallbackReachableWideGuard.Add(id);
        }

        Assert.Equal(ExpectedSetups, total);
        Assert.Equal(ExpectedWithCylinder, withCylinder);
        Assert.Equal(ExpectedSphereOnlyNoCylinder, sphereOnly);
        Assert.Equal(ExpectedWithoutAnyPrimitive, withoutPrimitive);
        Assert.Equal(ExpectedWithSummaryRadius, withRadius);

        Assert.Empty(fallbackReachable);
        // The wider guard sites 2 and 3 actually used. Zero here is what makes
        // the headless-reachable deletion safe; the narrow guard above does
        // not evaluate it.
        Assert.Empty(fallbackReachableWideGuard);
    }
}
