using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.Content.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class InstalledStaticAnimatingOmegaTests
{
    [Fact]
    public void EverySetOmegaAnimationIsPureYawOnAMeshOffsetFromItsAxis()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var found = new List<(uint Setup, float Yaw, double Radius)>();

        foreach (uint id in dats.Portal.Tree.Select(e => e.Id).Where(i => (i >> 24) == 0x02))
        {
            if (!dats.Portal.TryGet<Setup>(id, out Setup? setup) || setup is null)
                continue;
            uint animId = setup.DefaultAnimation?.DataId ?? 0u;
            if (animId == 0
                || !dats.Portal.TryGet<Animation>(animId, out Animation? anim)
                || anim is null)
            {
                continue;
            }

            foreach (var frame in anim.PartFrames)
                foreach (var hook in frame.Hooks)
                {
                    if (hook is not SetOmegaHook set)
                        continue;

                    Assert.Equal(0f, set.Axis.X, 5);
                    Assert.Equal(0f, set.Axis.Y, 5);
                    Assert.NotEqual(0f, set.Axis.Z);

                    double radius = 0d;
                    foreach (var placement in setup.PlacementFrames.Values)
                        foreach (var af in placement.Frames)
                            radius = Math.Max(
                                radius,
                                Math.Sqrt((af.Origin.X * af.Origin.X)
                                          + (af.Origin.Y * af.Origin.Y)));

                    found.Add((id, set.Axis.Z, radius));
                }
        }

        Assert.NotEmpty(found);

        foreach ((uint setupId, _, double radius) in found)
        {
            Assert.True(
                radius > 1d,
                $"setup 0x{setupId:X8} spins about an axis only {radius:0.###}m "
                + "from its mesh; rotation would not read as flight.");
        }
    }
}
