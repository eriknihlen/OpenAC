using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using AcDream.Core.Physics;
using AcDream.Core.Tests.Conformance;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public sealed class Issue337NeftetRockGeometryInspectionTests
{
    private const uint Landblock = 0x8766_0000u;
    private const uint GfxObjMask = 0x01000000u;
    private const uint SetupMask = 0x02000000u;
    private const uint TypeMask = 0xFF000000u;

    private static readonly Vector3 Wedge = new(134.313934f, 55.716248f, 50.010666f);

    private readonly ITestOutputHelper _out;

    public Issue337NeftetRockGeometryInspectionTests(ITestOutputHelper o) => _out = o;

    private sealed record Placed(
        uint EntityId,
        uint DatId,
        uint GfxObjId,
        Vector3 Position,
        Quaternion Rotation,
        List<Vector3[]> WorldPolygons,
        List<Vector3[]> LocalPolygons,
        FlatPhysicsBsp Bsp,
        Vector3 WorldMin,
        Vector3 WorldMax,
        int BspNodes,
        int BspPolys);

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpNeftetLandblockStaticsAndProbeTheWedgePoint()
    {
        System.Globalization.CultureInfo.CurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;

        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var info = dats.Get<LandBlockInfo>((Landblock & 0xFFFF0000u) | 0xFFFEu);
        Assert.NotNull(info);

        var sb = new StringBuilder();
        sb.AppendLine(Inv(
            $"landblock 0x{Landblock:X8}: Objects={info!.Objects.Count} Buildings={info.Buildings.Count}"));

        uint lbX = (Landblock >> 24) & 0xFFu;
        uint lbY = (Landblock >> 16) & 0xFFu;
        uint counter = 0;

        var cache = new PhysicsDataCache();
        var placed = new List<Placed>();

        foreach (var stab in info.Objects)
        {
            if (!IsSupported(stab.Id)) continue;
            uint entityId = LandblockStaticEntityIdAllocatorAllocate(lbX, lbY, ref counter);
            placed.AddRange(Place(dats, cache, entityId, stab.Id,
                stab.Frame.Origin, stab.Frame.Orientation));
        }

        foreach (var building in info.Buildings)
        {
            if (!IsSupported(building.ModelId)) continue;
            uint entityId = LandblockStaticEntityIdAllocatorAllocate(lbX, lbY, ref counter);
            placed.AddRange(Place(dats, cache, entityId, building.ModelId,
                building.Frame.Origin, building.Frame.Orientation));
        }

        sb.AppendLine($"placed collision parts: {placed.Count}");
        sb.AppendLine();
        sb.AppendLine("--- every part: world box, and the wedge point in ITS OWN local frame ---");
        foreach (var p in placed.OrderBy(p => p.EntityId).ThenBy(p => p.GfxObjId))
        {
            Vector3 local = Vector3.Transform(
                Wedge - p.Position, Quaternion.Inverse(p.Rotation));
            bool inWorldBox = Inside(p.WorldMin, p.WorldMax, Wedge);
            sb.AppendLine(Inv(
                $"  ent=0x{p.EntityId:X8} dat=0x{p.DatId:X8} gfx=0x{p.GfxObjId:X8} " +
                $"pos=({p.Position.X:F3},{p.Position.Y:F3},{p.Position.Z:F3}) " +
                $"rot=({p.Rotation.W:F4},{p.Rotation.X:F4},{p.Rotation.Y:F4},{p.Rotation.Z:F4}) " +
                $"yawDeg={YawDegrees(p.Rotation):F2} " +
                $"nodes={p.BspNodes} polys={p.BspPolys} " +
                $"worldMin=({p.WorldMin.X:F2},{p.WorldMin.Y:F2},{p.WorldMin.Z:F2}) " +
                $"worldMax=({p.WorldMax.X:F2},{p.WorldMax.Y:F2},{p.WorldMax.Z:F2}) " +
                $"wedgeLocal=({local.X:F2},{local.Y:F2},{local.Z:F2}) " +
                $"wedgeInWorldBox={inWorldBox}"));
        }

        sb.AppendLine();
        sb.AppendLine(Inv(
            $"--- vertical column through the wedge XY ({Wedge.X:F3},{Wedge.Y:F3}) ---"));
        sb.AppendLine("(every physics polygon whose WORLD XY projection contains that point,");
        sb.AppendLine(" with the Z of the polygon's plane at that XY — this is the surface");
        sb.AppendLine(" a falling body would land on and the ceiling it would be under)");

        var hits = new List<(uint Ent, uint Gfx, float Z, float Nz, int PolyIndex)>();
        foreach (var p in placed)
        {
            for (int i = 0; i < p.WorldPolygons.Count; i++)
            {
                Vector3[] poly = p.WorldPolygons[i];
                if (!XyContains(poly, Wedge.X, Wedge.Y)) continue;
                if (!TryPlaneZ(poly, Wedge.X, Wedge.Y, out float z, out float nz)) continue;
                hits.Add((p.EntityId, p.GfxObjId, z, nz, i));
            }
        }

        foreach (var h in hits.OrderByDescending(h => h.Z))
        {
            sb.AppendLine(Inv(
                $"  z={h.Z,9:F3}  normalZ={h.Nz,7:F4}  ent=0x{h.Ent:X8} gfx=0x{h.Gfx:X8} poly#{h.PolyIndex}"));
        }
        if (hits.Count == 0)
            sb.AppendLine("  (no physics polygon covers that XY at all)");

        sb.AppendLine();
        sb.AppendLine(Inv(
            $"player sphere centre is at z={Wedge.Z:F3}"));
        var above = hits.Where(h => h.Z > Wedge.Z).OrderBy(h => h.Z).ToList();
        var below = hits.Where(h => h.Z <= Wedge.Z).OrderByDescending(h => h.Z).ToList();
        sb.AppendLine(Inv(
            $"  nearest surface BELOW: {(below.Count == 0 ? "none" : $"z={below[0].Z:F3} ent=0x{below[0].Ent:X8} gfx=0x{below[0].Gfx:X8} (gap {Wedge.Z - below[0].Z:F3} m)")}"));
        sb.AppendLine(Inv(
            $"  nearest surface ABOVE: {(above.Count == 0 ? "none" : $"z={above[0].Z:F3} ent=0x{above[0].Ent:X8} gfx=0x{above[0].Gfx:X8} (gap {above[0].Z - Wedge.Z:F3} m)")}"));

        sb.AppendLine();
        sb.AppendLine("--- nearest physics polygon to the wedge point, per owner ---");
        foreach (var p in placed.OrderBy(p => p.EntityId))
        {
            float best = float.MaxValue;
            int bestIndex = -1;
            for (int i = 0; i < p.WorldPolygons.Count; i++)
            {
                float d = DistanceToPolygon(p.WorldPolygons[i], Wedge);
                if (d < best) { best = d; bestIndex = i; }
            }
            sb.AppendLine(Inv(
                $"  ent=0x{p.EntityId:X8} gfx=0x{p.GfxObjId:X8} nearestPolyDist={best:F3} m (poly#{bestIndex})"));
        }

        const float SphereRadius = 0.48f;   // the live mover's radius
        sb.AppendLine();
        sb.AppendLine(Inv(
            $"--- referee: BSP walk vs brute force, sphere r={SphereRadius:F3} ---"));

        foreach (var p in placed.OrderBy(p => p.EntityId))
        {
            int samples = 0, mismatch = 0, bothHit = 0;
            var mmMin = new Vector3(float.PositiveInfinity);
            var mmMax = new Vector3(float.NegativeInfinity);
            var firstMismatches = new List<string>();

            foreach (Vector3 probe in SurfaceProbes(p.LocalPolygons, SphereRadius))
            {
                samples++;
                bool walk = FlatBspQuery.SphereIntersectsPoly(
                    p.Bsp, probe, SphereRadius, out _, out _);
                bool brute = BruteHit(p.LocalPolygons, probe, SphereRadius);
                if (walk == brute) { if (walk) bothHit++; continue; }
                mismatch++;
                Vector3 world = p.Position + Vector3.Transform(probe, p.Rotation);
                mmMin = Vector3.Min(mmMin, world);
                mmMax = Vector3.Max(mmMax, world);
                if (firstMismatches.Count < 8)
                {
                    firstMismatches.Add(Inv(
                        $"      local=({probe.X:F2},{probe.Y:F2},{probe.Z:F2}) " +
                        $"world=({world.X:F2},{world.Y:F2},{world.Z:F2}) " +
                        $"walk={walk} brute={brute}"));
                }
            }

            sb.AppendLine(Inv(
                $"  ent=0x{p.EntityId:X8} gfx=0x{p.GfxObjId:X8} polys={p.BspPolys} " +
                $"samples={samples} bothHit={bothHit} MISMATCH={mismatch}"));
            if (mismatch > 0)
            {
                sb.AppendLine(Inv(
                    $"    mismatch world box=({mmMin.X:F2},{mmMin.Y:F2},{mmMin.Z:F2})..({mmMax.X:F2},{mmMax.Y:F2},{mmMax.Z:F2})"));
                foreach (string line in firstMismatches) sb.AppendLine(line);
            }
        }

        // ------------------------------------------------------------------
        // The exact live query, replayed. The wedge point, plus every point on
        // a 1 m lattice through the plateau, both answers side by side.
        // ------------------------------------------------------------------
        sb.AppendLine();
        sb.AppendLine("--- the live wedge query, replayed per owner ---");
        foreach (var p in placed.OrderBy(p => p.EntityId))
        {
            Vector3 local = Vector3.Transform(
                Wedge - p.Position, Quaternion.Inverse(p.Rotation));
            bool walk = FlatBspQuery.SphereIntersectsPoly(
                p.Bsp, local, SphereRadius, out ushort id, out _);
            bool brute = BruteHit(p.LocalPolygons, local, SphereRadius);
            float nearest = float.MaxValue;
            foreach (var poly in p.LocalPolygons)
                nearest = MathF.Min(nearest, DistanceToPolygon(poly, local));
            sb.AppendLine(Inv(
                $"  ent=0x{p.EntityId:X8} gfx=0x{p.GfxObjId:X8} " +
                $"walkHit={walk} (poly {id}) bruteHit={brute} nearestPoly={nearest:F3} m"));
        }

        sb.AppendLine();
        sb.AppendLine("--- 1 m lattice over the plateau (x 120..150, y 40..70, z 44..56) ---");
        foreach (var p in placed.OrderBy(p => p.EntityId))
        {
            int lat = 0, latWalk = 0, latBrute = 0, latMismatch = 0;
            var missMin = new Vector3(float.PositiveInfinity);
            var missMax = new Vector3(float.NegativeInfinity);
            for (float x = 120f; x <= 150f; x += 1f)
            for (float y = 40f; y <= 70f; y += 1f)
            for (float z = 44f; z <= 56f; z += 1f)
            {
                var world = new Vector3(x, y, z);
                Vector3 local = Vector3.Transform(
                    world - p.Position, Quaternion.Inverse(p.Rotation));
                lat++;
                bool walk = FlatBspQuery.SphereIntersectsPoly(
                    p.Bsp, local, SphereRadius, out _, out _);
                bool brute = BruteHit(p.LocalPolygons, local, SphereRadius);
                if (walk) latWalk++;
                if (brute) latBrute++;
                if (walk == brute) continue;
                latMismatch++;
                missMin = Vector3.Min(missMin, world);
                missMax = Vector3.Max(missMax, world);
            }
            sb.AppendLine(Inv(
                $"  ent=0x{p.EntityId:X8} gfx=0x{p.GfxObjId:X8} points={lat} " +
                $"walkHits={latWalk} bruteHits={latBrute} MISMATCH={latMismatch}" +
                (latMismatch > 0
                    ? $" box=({missMin.X:F1},{missMin.Y:F1},{missMin.Z:F1})..({missMax.X:F1},{missMax.Y:F1},{missMax.Z:F1})"
                    : string.Empty)));
        }

        sb.AppendLine();
        sb.AppendLine("--- recorded live positions vs the surface at their own XY ---");
        (string Tag, Vector3 P, float CpNz)[] track =
        [
            ("climb  cpNz=0.7401", new Vector3(185.53f, 617.52f - 576f, 3.839f), 0.7401f),
            ("climb  cpNz=0.7078", new Vector3(172.63f, 600.23f - 576f, 17.910f), 0.7078f),
            ("climb  cpNz=0.8640", new Vector3(154.04f, 596.93f - 576f, 32.543f), 0.8640f),
            ("climb  cpNz=0.8216", new Vector3(143.69f, 611.83f - 576f, 43.352f), 0.8216f),
            ("climb  cpNz=0.9532", new Vector3(138.38f, 621.53f - 576f, 48.567f), 0.9532f),
            ("STALL  cpNz=0.9805", new Vector3(130.81f, 625.88f - 576f, 51.097f), 0.9805f),
            ("stand  cpNz=0.9532", new Vector3(134.08f, 623.63f - 576f, 50.074f), 0.9532f),
            ("stand  cpNz=0.9532", new Vector3(135.77f, 625.86f - 576f, 49.994f), 0.9532f),
            ("stand  cpNz=1.0000", new Vector3(131.38f, 627.49f - 576f, 49.908f), 1.0000f),
            ("STALL  cpNz=0.9805", new Vector3(131.68f, 627.58f - 576f, 51.096f), 0.9805f),
            ("WEDGE  support=none", Wedge, float.NaN),
        ];

        foreach (var row in track)
        {
            var col = Column(placed, row.P.X, row.P.Y);
            var up = col.Where(c => c.Nz > 0f).OrderByDescending(c => c.Z).ToList();
            var upBelowHead = up.Where(c => c.Z <= row.P.Z + 0.05f).ToList();
            string surf = up.Count == 0
                ? "NO UPWARD SURFACE IN COLUMN"
                : Inv($"topUp z={up[0].Z:F3} (ent=0x{up[0].Ent:X8}) delta={row.P.Z - up[0].Z:+0.000;-0.000}");
            sb.AppendLine(Inv(
                $"  {row.Tag}  p=({row.P.X:F2},{row.P.Y:F2},{row.P.Z:F3})  " +
                $"colPolys={col.Count} upFacing={up.Count} atOrBelowFeet={upBelowHead.Count}  {surf}"));
        }

        sb.AppendLine();
        sb.AppendLine("--- hole map: upward-facing physics coverage over the plateau ---");
        sb.AppendLine("    (0.5 m XY grid, x 118..152, y 38..72; '.'=covered above z=35, "
                      + "'#'=NO upward surface, '*'=the wedge XY)");
        int covered = 0, holes = 0;
        var holeMin = new Vector2(float.PositiveInfinity);
        var holeMax = new Vector2(float.NegativeInfinity);
        for (float y = 72f; y >= 38f; y -= 0.5f)
        {
            var line = new StringBuilder(Inv($"  y={y,6:F1} "));
            for (float x = 118f; x <= 152f; x += 0.5f)
            {
                var col = Column(placed, x, y);
                bool hasUp = col.Any(c => c.Nz > 0f && c.Z > 35f);
                bool isWedge = MathF.Abs(x - Wedge.X) < 0.25f
                            && MathF.Abs(y - Wedge.Y) < 0.25f;
                if (hasUp) covered++;
                else
                {
                    holes++;
                    holeMin = Vector2.Min(holeMin, new Vector2(x, y));
                    holeMax = Vector2.Max(holeMax, new Vector2(x, y));
                }
                line.Append(isWedge ? '*' : hasUp ? '.' : '#');
            }
            sb.AppendLine(line.ToString());
        }
        sb.AppendLine(Inv(
            $"  covered={covered} holes={holes}" +
            (holes > 0
                ? $" holeBox=({holeMin.X:F1},{holeMin.Y:F1})..({holeMax.X:F1},{holeMax.Y:F1})"
                : string.Empty)));

        string outPath = Path.Combine(Path.GetTempPath(), "issue337-neftet-geometry.txt");
        File.WriteAllText(outPath, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"(also written to {outPath})");
    }

    // ---------------------------------------------------------------- helpers

    [Fact]
    public void ReplayTheDescentThatFellThroughTheSurface()
    {
        System.Globalization.CultureInfo.CurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;

        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var info = dats.Get<LandBlockInfo>((Landblock & 0xFFFF0000u) | 0xFFFEu);
        Assert.NotNull(info);

        uint lbX = (Landblock >> 24) & 0xFFu;
        uint lbY = (Landblock >> 16) & 0xFFu;
        uint counter = 0;
        var cache = new PhysicsDataCache();
        var placed = new List<Placed>();
        foreach (var stab in info!.Objects)
        {
            if (!IsSupported(stab.Id)) continue;
            uint entityId = LandblockStaticEntityIdAllocatorAllocate(lbX, lbY, ref counter);
            placed.AddRange(Place(dats, cache, entityId, stab.Id,
                stab.Frame.Origin, stab.Frame.Orientation));
        }

        var sb = new StringBuilder();

        var playerSetup = dats.Get<Setup>(0x0200_0001u);
        Assert.NotNull(playerSetup);
        FlatSetupCollision playerFlat =
            FlatCollisionAssetBuilder.FlattenSetup(playerSetup!);
        sb.AppendLine("--- player setup 0x02000001 collision volume ---");
        sb.AppendLine(Inv(
            $"  height={playerFlat.Height:F3} radius={playerFlat.Radius:F3} " +
            $"stepUp={playerFlat.StepUpHeight:F3} stepDown={playerFlat.StepDownHeight:F3} " +
            $"spheres={playerFlat.Spheres.Length} cylinders={playerFlat.Cylinders.Length}"));
        for (int i = 0; i < playerFlat.Spheres.Length; i++)
        {
            var s = playerFlat.Spheres[i];
            sb.AppendLine(Inv(
                $"    sphere[{i}] origin=({s.Origin.X:F3},{s.Origin.Y:F3},{s.Origin.Z:F3}) r={s.Radius:F3}"));
        }
        for (int i = 0; i < playerFlat.Cylinders.Length; i++)
        {
            var c = playerFlat.Cylinders[i];
            sb.AppendLine(Inv(
                $"    cylsphere[{i}] origin=({c.Origin.X:F3},{c.Origin.Y:F3},{c.Origin.Z:F3}) r={c.Radius:F3} h={c.Height:F3}"));
        }

        var rows = playerFlat.Spheres.Length > 0
            ? playerFlat.Spheres.ToArray()
            : playerFlat.Cylinders
                .Select(c => new FlatCollisionSphere(c.Origin, c.Radius))
                .ToArray();

        sb.AppendLine();
        sb.AppendLine("--- surface column at the fall XY ---");
        foreach ((string tag, float x, float y) in new[]
        {
            ("stand-before-jump", 131.683f, 627.581f - 576f),
            ("fall/landing     ", 131.13f, 627.61f - 576f),
            ("final wedge      ", 131.38f, 627.49f - 576f),
            ("login wedge      ", Wedge.X, Wedge.Y),
        })
        {
            var col = Column(placed, x, y).OrderByDescending(c => c.Z).ToList();
            sb.AppendLine(Inv($"  {tag} xy=({x:F3},{y:F3}) polys={col.Count}"));
            foreach (var c in col)
            {
                sb.AppendLine(Inv(
                    $"      z={c.Z,9:F3} nz={c.Nz,8:F4} ent=0x{c.Ent:X8} gfx=0x{c.Gfx:X8}"));
            }
        }

        (string Tag, Vector3 From, Vector3 To, bool ClientHit)[] steps =
        [
            ("CONTROL climb step", new Vector3(143.69f, 611.83f - 576f, 43.352f),
                                   new Vector3(143.69f, 611.83f - 576f, 42.852f), true),
            ("CONTROL stand step", new Vector3(135.77f, 625.86f - 576f, 49.994f),
                                   new Vector3(135.77f, 625.86f - 576f, 49.494f), true),
            ("FALL 1 51.389->51.269", new Vector3(131.13f, 627.61f - 576f, 51.389f),
                                      new Vector3(131.13f, 627.61f - 576f, 51.269f), false),
            ("FALL 2 51.269->50.961", new Vector3(131.13f, 627.61f - 576f, 51.269f),
                                      new Vector3(131.13f, 627.61f - 576f, 50.961f), false),
            ("FALL 3 50.961->50.670", new Vector3(131.13f, 627.61f - 576f, 50.961f),
                                      new Vector3(131.13f, 627.61f - 576f, 50.670f), false),
            ("FALL 4 50.670->50.335", new Vector3(131.13f, 627.61f - 576f, 50.670f),
                                      new Vector3(131.13f, 627.61f - 576f, 50.335f), false),
            ("FALL 5 50.335->50.021", new Vector3(131.13f, 627.61f - 576f, 50.335f),
                                      new Vector3(131.13f, 627.61f - 576f, 50.021f), false),
            ("FALL 6 50.021->49.656 (client BLOCKED here)",
                                      new Vector3(131.13f, 627.61f - 576f, 50.021f),
                                      new Vector3(131.13f, 627.61f - 576f, 49.656f), false),
        ];

        sb.AppendLine();
        sb.AppendLine("--- swept-sphere descent replay against every owner ---");
        foreach (var step in steps)
        {
            sb.AppendLine(Inv(
                $"  {step.Tag}   (client recorded a contact plane: {step.ClientHit})"));
            foreach (var p in placed.OrderBy(p => p.EntityId))
            {
                var invRot = Quaternion.Inverse(p.Rotation);
                for (int i = 0; i < rows.Length; i++)
                {
                    Vector3 worldFrom = step.From + rows[i].Origin;
                    Vector3 worldTo = step.To + rows[i].Origin;
                    Vector3 localTo = Vector3.Transform(worldTo - p.Position, invRot);
                    Vector3 localFrom = Vector3.Transform(worldFrom - p.Position, invRot);
                    Vector3 localMove = localTo - localFrom;

                    bool swept = FlatBspQuery.SphereIntersectsPolyWithTime(
                        p.Bsp, localTo, rows[i].Radius, localMove,
                        out ushort sweptId, out _, out float sweptTime);
                    bool stat = FlatBspQuery.SphereIntersectsPoly(
                        p.Bsp, localTo, rows[i].Radius, out ushort statId, out _);
                    float nearest = float.MaxValue;
                    foreach (var poly in p.LocalPolygons)
                        nearest = MathF.Min(nearest, DistanceToPolygon(poly, localTo));
                    if (!swept && !stat && nearest > 3f) continue;   // far away, silent
                    sb.AppendLine(Inv(
                        $"      ent=0x{p.EntityId:X8} sphere[{i}] r={rows[i].Radius:F3} " +
                        $"swept={swept}(poly {sweptId} t={sweptTime:F4}) " +
                        $"static={stat}(poly {statId}) nearestPoly={nearest:F3} m"));
                }
            }
        }

        string outPath = Path.Combine(Path.GetTempPath(), "issue337-descent-replay.txt");
        File.WriteAllText(outPath, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"(also written to {outPath})");
    }

    [Fact]
    public void TheBspQueryReturnsAHitAtThePositionTheClientFellThrough()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");   // CI without dats — the sibling dumps skip too.

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var info = dats.Get<LandBlockInfo>((Landblock & 0xFFFF0000u) | 0xFFFEu);
        Assert.NotNull(info);

        Placed rock = ResolveOwner(dats, info!, 0xC876_6009u);

        var feet = new Vector3(131.13f, 627.61f - 576f, 50.961f);
        var footSphereOffset = new Vector3(0f, 0f, 0.475f);
        const float SphereRadius = 0.48f;

        Vector3 local = Vector3.Transform(
            feet + footSphereOffset - rock.Position,
            Quaternion.Inverse(rock.Rotation));

        bool hit = FlatBspQuery.SphereIntersectsPoly(
            rock.Bsp, local, SphereRadius, out ushort polygonId, out _);

        Assert.True(
            hit,
            "The rock's physics BSP must report the plateau surface under the "
            + "mover at the position the live client fell through it. If this "
            + "fails, the defect is in the geometry or the traversal after all.");
        Assert.NotEqual(0, polygonId);
    }

    [Fact]
    public void TheOldBroadphaseMeasuredToTheOriginAndSoRejectedGeometryItStoodOn()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var info = dats.Get<LandBlockInfo>((Landblock & 0xFFFF0000u) | 0xFFFEu);
        Assert.NotNull(info);
        Placed rock = ResolveOwner(dats, info!, 0xC876_6009u);

        var feet = new Vector3(131.13f, 627.61f - 576f, 50.961f);
        Vector3 currPos = feet + new Vector3(0f, 0f, 0.475f);
        const float SphereRadius = 0.48f;
        const float Movement = 0.308f;   // the live step length

        var root = rock.Bsp.Nodes[rock.Bsp.RootIndex].BoundingSphere;
        float ownerRadius = root.Radius;

        float distToOrigin = (currPos - rock.Position).Length();
        float maxReach = SphereRadius + ownerRadius + Movement + 2f;

        Assert.True(
            distToOrigin > maxReach,
            $"the old filter is supposed to have REJECTED this candidate: "
            + $"distToOrigin={distToOrigin:F3} vs maxReach={maxReach:F3}.");

        Vector3 centre = rock.Position
            + Vector3.Transform(root.Origin, rock.Rotation);
        float distToCentre = (currPos - centre).Length();

        Assert.True(
            distToCentre <= ownerRadius,
            $"the mover must be inside the root bounding SPHERE it was standing "
            + $"on: distToCentre={distToCentre:F3} > radius={ownerRadius:F3}.");
    }

    private static Placed ResolveOwner(
        DatCollection dats, LandBlockInfo info, uint wantedEntityId)
    {
        uint lbX = (Landblock >> 24) & 0xFFu;
        uint lbY = (Landblock >> 16) & 0xFFu;
        uint counter = 0;
        var cache = new PhysicsDataCache();
        foreach (var stab in info.Objects)
        {
            if (!IsSupported(stab.Id)) continue;
            uint entityId = LandblockStaticEntityIdAllocatorAllocate(lbX, lbY, ref counter);
            if (entityId != wantedEntityId) continue;
            var parts = Place(dats, cache, entityId, stab.Id,
                stab.Frame.Origin, stab.Frame.Orientation).ToList();
            Assert.Single(parts);
            return parts[0];
        }
        throw new InvalidOperationException(
            $"landblock static 0x{wantedEntityId:X8} not found.");
    }

    private static string Inv(string s) => s;

    private static uint LandblockStaticEntityIdAllocatorAllocate(
        uint lbX, uint lbY, ref uint counter)
        => LandblockStaticEntityIdAllocator.Allocate(lbX, lbY, ref counter);

    private static bool IsSupported(uint id)
    {
        uint type = id & TypeMask;
        return type == GfxObjMask || type == SetupMask;
    }

    private static IEnumerable<Placed> Place(
        DatCollection dats,
        PhysicsDataCache cache,
        uint entityId,
        uint datId,
        Vector3 origin,
        Quaternion orientation)
    {
        var parts = new List<(uint GfxObjId, Vector3 LocalPos, Quaternion LocalRot)>();

        if ((datId & TypeMask) == GfxObjMask)
        {
            parts.Add((datId, Vector3.Zero, Quaternion.Identity));
        }
        else
        {
            var setup = dats.Get<Setup>(datId);
            if (setup is null) yield break;
            for (int i = 0; i < setup.Parts.Count; i++)
            {
                Vector3 lp = Vector3.Zero;
                Quaternion lr = Quaternion.Identity;
                if (!setup.PlacementFrames.TryGetValue(
                        DatReaderWriter.Enums.Placement.Resting, out var pf)
                    && !setup.PlacementFrames.TryGetValue(
                        DatReaderWriter.Enums.Placement.Default, out pf))
                {
                    pf = setup.PlacementFrames.Values.FirstOrDefault();
                }
                if (pf?.Frames is not null && i < pf.Frames.Count)
                {
                    lp = pf.Frames[i].Origin;
                    lr = pf.Frames[i].Orientation;
                }
                parts.Add((setup.Parts[i], lp, lr));
            }
        }

        foreach (var part in parts)
        {
            var gfx = dats.Get<GfxObj>(part.GfxObjId);
            if (gfx is null) continue;
            cache.CacheGfxObj(part.GfxObjId, gfx);
            FlatGfxObjCollisionAsset flat =
                FlatCollisionAssetBuilder.FlattenGfxObj(gfx);
            FlatPhysicsBsp bsp = flat.PhysicsBsp;
            if (bsp.RootIndex < 0 || bsp.Nodes.Length == 0) continue;

            Vector3 partWorldPos = origin + Vector3.Transform(part.LocalPos, orientation);
            Quaternion partWorldRot = orientation * part.LocalRot;

            var seen = new HashSet<int>();
            var worldPolys = new List<Vector3[]>();
            var localPolys = new List<Vector3[]>();
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);

            foreach (var node in bsp.Nodes)
            {
                var range = node.PolygonIndexRange;
                for (int i = range.Start; i < range.EndExclusive; i++)
                {
                    int pi = bsp.PolygonIndexStream[i];
                    if ((uint)pi >= (uint)bsp.PolygonTable.Polygons.Length) continue;
                    if (!seen.Add(pi)) continue;
                    var poly = bsp.PolygonTable.Polygons[pi];
                    var vr = poly.VertexRange;
                    var verts = new Vector3[vr.Count];
                    var lverts = new Vector3[vr.Count];
                    for (int v = 0; v < vr.Count; v++)
                    {
                        Vector3 local = bsp.PolygonTable.Vertices[vr.Start + v];
                        Vector3 world = partWorldPos
                            + Vector3.Transform(local, partWorldRot);
                        lverts[v] = local;
                        verts[v] = world;
                        min = Vector3.Min(min, world);
                        max = Vector3.Max(max, world);
                    }
                    worldPolys.Add(verts);
                    localPolys.Add(lverts);
                }
            }

            if (worldPolys.Count == 0) continue;

            yield return new Placed(
                entityId, datId, part.GfxObjId, partWorldPos, partWorldRot,
                worldPolys, localPolys, bsp, min, max,
                bsp.Nodes.Length, worldPolys.Count);
        }
    }

    private static IEnumerable<Vector3> SurfaceProbes(
        List<Vector3[]> polygons, float radius)
    {
        float nudge = radius * 0.5f;
        foreach (Vector3[] poly in polygons)
        {
            if (poly.Length < 3) continue;
            Vector3 n = Vector3.Cross(poly[1] - poly[0], poly[2] - poly[0]);
            float len = n.Length();
            if (len < 1e-9f) continue;
            n /= len;

            Vector3 centroid = Vector3.Zero;
            foreach (Vector3 v in poly) centroid += v;
            centroid /= poly.Length;

            yield return centroid + n * nudge;
            yield return centroid - n * nudge;
            foreach (Vector3 v in poly)
            {
                Vector3 inset = Vector3.Lerp(v, centroid, 0.15f);
                yield return inset + n * nudge;
                yield return inset - n * nudge;
            }
        }
    }

    private static bool BruteHit(List<Vector3[]> polygons, Vector3 centre, float radius)
    {
        foreach (Vector3[] poly in polygons)
        {
            if (DistanceToPolygon(poly, centre) < radius - 1e-4f)
                return true;
        }
        return false;
    }

    private static List<(uint Ent, uint Gfx, float Z, float Nz)> Column(
        List<Placed> placed, float x, float y)
    {
        var result = new List<(uint, uint, float, float)>();
        foreach (var p in placed)
        {
            if (x < p.WorldMin.X || x > p.WorldMax.X
                || y < p.WorldMin.Y || y > p.WorldMax.Y) continue;
            foreach (Vector3[] poly in p.WorldPolygons)
            {
                if (!XyContains(poly, x, y)) continue;
                if (!TryPlaneZ(poly, x, y, out float z, out float nz)) continue;
                result.Add((p.EntityId, p.GfxObjId, z, nz));
            }
        }
        return result;
    }

    private static bool Inside(Vector3 min, Vector3 max, Vector3 p)
        => p.X >= min.X && p.X <= max.X
        && p.Y >= min.Y && p.Y <= max.Y
        && p.Z >= min.Z && p.Z <= max.Z;

    private static float YawDegrees(Quaternion q)
    {
        Vector3 x = Vector3.Transform(Vector3.UnitX, q);
        return MathF.Atan2(x.Y, x.X) * 180f / MathF.PI;
    }

    private static bool XyContains(Vector3[] poly, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            float yi = poly[i].Y, yj = poly[j].Y;
            if ((yi > y) == (yj > y)) continue;
            float t = (y - yi) / (yj - yi);
            float xAt = poly[i].X + t * (poly[j].X - poly[i].X);
            if (x < xAt) inside = !inside;
        }
        return inside;
    }

    private static bool TryPlaneZ(Vector3[] poly, float x, float y, out float z, out float nz)
    {
        z = 0f; nz = 0f;
        if (poly.Length < 3) return false;
        Vector3 n = Vector3.Cross(poly[1] - poly[0], poly[2] - poly[0]);
        float len = n.Length();
        if (len < 1e-9f) return false;
        n /= len;
        if (MathF.Abs(n.Z) < 1e-6f) return false;
        nz = n.Z;
        // n · (P - poly0) = 0  ->  z = poly0.z - (n.x*(x-p0.x) + n.y*(y-p0.y)) / n.z
        z = poly[0].Z - (n.X * (x - poly[0].X) + n.Y * (y - poly[0].Y)) / n.Z;
        return true;
    }

    private static float DistanceToPolygon(Vector3[] poly, Vector3 p)
    {
        float best = float.MaxValue;
        for (int i = 2; i < poly.Length; i++)
            best = MathF.Min(best, DistanceToTriangle(poly[0], poly[i - 1], poly[i], p));
        if (poly.Length == 2)
            best = MathF.Min(best, DistanceToSegment(poly[0], poly[1], p));
        return best;
    }

    private static float DistanceToTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 p)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3.Distance(p, a);
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3.Distance(p, b);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
            return Vector3.Distance(p, a + ab * (d1 / (d1 - d3)));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3.Distance(p, c);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
            return Vector3.Distance(p, a + ac * (d2 / (d2 - d6)));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
            return Vector3.Distance(p, b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));
        float denom = 1f / (va + vb + vc);
        return Vector3.Distance(p, a + ab * (vb * denom) + ac * (vc * denom));
    }

    private static float DistanceToSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float t = Vector3.Dot(p - a, ab) / MathF.Max(1e-9f, Vector3.Dot(ab, ab));
        t = Math.Clamp(t, 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }
}
