using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.Core.Lighting;
using DatReaderWriter;
using DatReaderWriter.Options;
using DatLandBlockInfo = DatReaderWriter.DBObjs.LandBlockInfo;
using DatSetup = DatReaderWriter.DBObjs.Setup;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Conformance;

[Trait("Purpose", "Diagnostic")]
[Trait("Lane", "InstalledDat")]
public sealed class HoltburgTorchFalloffProbeTests
{
    private readonly ITestOutputHelper _out;
    public HoltburgTorchFalloffProbeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump_Holtburg_StaticLight_Falloffs()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        uint[] landblocks =
        {
            0xA9B3u, 0xA9B4u, 0xA9B2u, 0xA9B5u, 0xAAB3u, 0xAAB4u, 0xA8B3u, 0xA8B4u,
        };

        // Tally every distinct raw Falloff seen (the headline number).
        var falloffTally = new SortedDictionary<float, int>();
        int totalLights = 0;

        foreach (uint lb in landblocks)
        {
            uint infoId = (lb << 16) | 0xFFFEu;
            var info = dats.Get<DatLandBlockInfo>(infoId);
            if (info is null) { _out.WriteLine($"=== LB 0x{lb:X4}: LandBlockInfo NULL ==="); continue; }

            int buildings = info.Buildings?.Count ?? 0;
            int objects = info.Objects?.Count ?? 0;
            _out.WriteLine($"=== LB 0x{lb:X4}: Buildings={buildings} Objects={objects} ===");

            // Record building-shell origins so we can rank torches by proximity.
            var shells = new List<(uint model, Vector3 pos)>();
            if (info.Buildings is not null)
            {
                foreach (var b in info.Buildings)
                {
                    var o = b.Frame.Origin;
                    shells.Add((b.ModelId, new Vector3(o.X, o.Y, o.Z)));
                    _out.WriteLine($"  BUILDING shell model=0x{b.ModelId:X8} pos=({o.X:F1},{o.Y:F1},{o.Z:F1}) portals={b.Portals?.Count ?? 0}");
                }
            }

            if (info.Objects is null) continue;
            foreach (var stab in info.Objects)
            {
                if ((stab.Id & 0xFF000000u) != 0x02000000u) continue;
                var setup = dats.Get<DatSetup>(stab.Id);
                if (setup?.Lights is null || setup.Lights.Count == 0) continue;

                var loaded = LightInfoLoader.Load(
                    setup,
                    ownerId: 0,
                    entityPosition: new Vector3(stab.Frame.Origin.X, stab.Frame.Origin.Y, stab.Frame.Origin.Z),
                    entityRotation: new Quaternion(
                        stab.Frame.Orientation.X, stab.Frame.Orientation.Y,
                        stab.Frame.Orientation.Z, stab.Frame.Orientation.W));

                foreach (var (kvp, ls) in setup.Lights.Zip(loaded, (k, l) => (k, l)))
                {
                    float rawFalloff = kvp.Value.Falloff;
                    totalLights++;
                    falloffTally.TryGetValue(rawFalloff, out int c);
                    falloffTally[rawFalloff] = c + 1;

                    // Nearest building shell, for "is this an entrance torch on the hall?".
                    float nearest = float.MaxValue;
                    uint nearestModel = 0;
                    foreach (var (model, spos) in shells)
                    {
                        float dd = Vector3.Distance(ls.WorldPosition, spos);
                        if (dd < nearest) { nearest = dd; nearestModel = model; }
                    }

                    _out.WriteLine(
                        $"  LIGHT setup=0x{stab.Id:X8} kind={ls.Kind} " +
                        $"pos=({ls.WorldPosition.X:F1},{ls.WorldPosition.Y:F1},{ls.WorldPosition.Z:F1}) " +
                        $"color=({ls.ColorLinear.X:F3},{ls.ColorLinear.Y:F3},{ls.ColorLinear.Z:F3}) " +
                        $"intensity={ls.Intensity:F1} rawFalloff={rawFalloff:F3} Range={ls.Range:F3} " +
                        $"cone={ls.ConeAngle:F3} nearestShell=0x{nearestModel:X8}@{(nearest == float.MaxValue ? -1f : nearest):F1}m");
                }
            }
        }

        _out.WriteLine($"=== FALLOFF HISTOGRAM (raw dat values across {totalLights} static lights) ===");
        foreach (var kv in falloffTally)
            _out.WriteLine($"  rawFalloff={kv.Key:F3} -> Range(x1.3)={kv.Key * 1.3f:F3}m  count={kv.Value}");
    }
}
