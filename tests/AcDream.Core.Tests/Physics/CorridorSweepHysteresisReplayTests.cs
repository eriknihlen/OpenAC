using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class CorridorSweepHysteresisReplayTests
{
    private readonly ITestOutputHelper _out;
    public CorridorSweepHysteresisReplayTests(ITestOutputHelper output) => _out = output;

    private const float ViewerSphereRadius = 0.3f;

    private const uint FacilityHubLandblock = 0x8A020000u;
    private const uint CorridorCell         = 0x8A020164u;

    // [flap-cam] player=(70.58,-40.16,-5.90) (parked spawn) + PivotHeight 1.5.
    private static readonly Vector3 Pivot = new(70.58f, -40.16f, -4.40f);

    private static readonly Vector3 THit = new(70.366051f, -38.628315f, -3.935829f);

    private static (PhysicsEngine, PhysicsDataCache) BuildCorridorEngine(DatCollection dats)
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        for (uint low = 0x0100u; low <= 0x01FFu; low++)
        {
            uint id = FacilityHubLandblock | low;
            try { ConformanceDats.LoadEnvCell(dats, cache, id); }
            catch (InvalidOperationException) { }
        }

        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(FacilityHubLandblock, new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);
        return (engine, cache);
    }

    private static ResolveResult SweepViewer(PhysicsEngine engine, Vector3 pivot, Vector3 desiredEye, uint cellId)
    {
        uint startCell = cellId;
        if ((cellId & 0xFFFFu) >= 0x0100u)
        {
            var (pivotCell, found) = engine.AdjustPosition(cellId, pivot);
            if (found) startCell = pivotCell;
        }

        Vector3 begin = pivot      - new Vector3(0f, 0f, ViewerSphereRadius);
        Vector3 end   = desiredEye - new Vector3(0f, 0f, ViewerSphereRadius);

        return engine.ResolveWithTransition(
            currentPos:     begin,
            targetPos:      end,
            cellId:         startCell,
            sphereRadius:   ViewerSphereRadius,
            sphereHeight:   0f,
            stepUpHeight:   0f,
            stepDownHeight: 0f,
            isOnGround:     false,
            body:           null,
            moverFlags:     ObjectInfoState.IsViewer | ObjectInfoState.PathClipped
                          | ObjectInfoState.FreeRotate | ObjectInfoState.PerfectClip,
            movingEntityId: 0);
    }

    [Fact]
    public void ClippedStop_IsTheContactPoint_NotAStepBoundary()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var (engine, _) = BuildCorridorEngine(dats);

        Vector3 dir = Vector3.Normalize(THit - Pivot);

        var clippedEyeBacks = new List<float>();
        for (float s = 1.20f; s <= 1.75f; s += 0.025f)
        {
            Vector3 target = Pivot + dir * s;
            var r = SweepViewer(engine, Pivot, target, CorridorCell);
            Vector3 eye = r.Position + new Vector3(0f, 0f, ViewerSphereRadius);
            float eyeBack = Vector3.Distance(Pivot, eye);
            _out.WriteLine(FormattableString.Invariant(
                $"s={s:F3} eyeBack={eyeBack:F3} collNorm={r.CollisionNormalValid}"));

            if (s <= 1.55f)
            {
                // (1) Short of first touch (~1.61 m): the sweep must reach the target.
                Assert.False(r.CollisionNormalValid,
                    $"s={s:F3}: no wall within reach, sweep must not clip");
                Assert.True(MathF.Abs(eyeBack - s) < 0.01f,
                    $"s={s:F3}: unclipped sweep must reach the target, got eyeBack={eyeBack:F3}");
            }
            else if (s >= 1.65f)
            {
                Assert.True(r.CollisionNormalValid,
                    $"s={s:F3}: target past the wall must clip");
                clippedEyeBacks.Add(eyeBack);
            }
        }

        Assert.True(clippedEyeBacks.Count >= 4, "expected several clipped samples");
        float min = float.MaxValue, max = float.MinValue;
        foreach (var e in clippedEyeBacks) { min = MathF.Min(min, e); max = MathF.Max(max, e); }
        Assert.True(max - min < 0.03f,
            $"clipped stops must be one contact point, got spread {max - min:F3} m ({min:F3}..{max:F3})");
    }
}
