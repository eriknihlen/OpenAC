using System;
using System.IO;
using System.Linq;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class DoorSetupGfxObjInspectionTests
{
    private readonly ITestOutputHelper _out;
    public DoorSetupGfxObjInspectionTests(ITestOutputHelper output) => _out = output;

    private const uint DoorSetupId = 0x020019FFu;

    [Fact]
    public void DoorSetup_AndParts_DatInspection()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var setup = dats.Get<Setup>(DoorSetupId);
        Assert.NotNull(setup);

        _out.WriteLine($"=== Setup 0x{DoorSetupId:X8} ===");
        _out.WriteLine($"  Flags        = {setup!.Flags} (0x{(uint)setup.Flags:X8})");
        _out.WriteLine($"  Radius       = {setup.Radius:F4}");
        _out.WriteLine($"  Height       = {setup.Height:F4}");
        _out.WriteLine($"  StepUp       = {setup.StepUpHeight:F4}");
        _out.WriteLine($"  StepDown     = {setup.StepDownHeight:F4}");
        _out.WriteLine($"  CylSpheres   = {setup.CylSpheres.Count}");
        for (int i = 0; i < setup.CylSpheres.Count; i++)
        {
            var c = setup.CylSpheres[i];
            _out.WriteLine($"    [{i}] r={c.Radius:F4} h={c.Height:F4} origin=({c.Origin.X:F3},{c.Origin.Y:F3},{c.Origin.Z:F3})");
        }
        _out.WriteLine($"  Spheres      = {setup.Spheres.Count}");
        for (int i = 0; i < setup.Spheres.Count; i++)
        {
            var s = setup.Spheres[i];
            _out.WriteLine($"    [{i}] r={s.Radius:F4} origin=({s.Origin.X:F3},{s.Origin.Y:F3},{s.Origin.Z:F3})");
        }
        _out.WriteLine($"  Parts        = {setup.Parts.Count}");
        for (int i = 0; i < setup.Parts.Count; i++)
        {
            _out.WriteLine($"    [{i}] gfxObj=0x{setup.Parts[i]:X8}");
        }
        _out.WriteLine($"  PlacementFrames = {setup.PlacementFrames.Count}");
        foreach (var kv in setup.PlacementFrames)
        {
            _out.WriteLine($"    [{kv.Key}] frameCount={kv.Value.Frames.Count}");
            for (int i = 0; i < kv.Value.Frames.Count; i++)
            {
                var f = kv.Value.Frames[i];
                _out.WriteLine($"      frame[{i}] pos=({f.Origin.X:F3},{f.Origin.Y:F3},{f.Origin.Z:F3}) " +
                               $"rot=({f.Orientation.X:F3},{f.Orientation.Y:F3},{f.Orientation.Z:F3},{f.Orientation.W:F3})");
            }
        }
        _out.WriteLine($"  HoldingLocations = {setup.HoldingLocations.Count}");

        // Inspect each unique part GfxObj.
        var uniqueGfxIds = setup.Parts.Distinct().ToList();
        foreach (uint gfxId in uniqueGfxIds)
        {
            _out.WriteLine("");
            InspectGfxObj(dats, gfxId);
        }

        _out.WriteLine("");
        _out.WriteLine("=== Cache predicate would skip a part if: ===");
        _out.WriteLine("  !Flags.HasFlag(HasPhysics) OR PhysicsBSP?.Root is null OR VertexArray is null");
        _out.WriteLine("(See src/AcDream.Core/Physics/PhysicsDataCache.cs:44-47)");
    }

    private void InspectGfxObj(DatCollection dats, uint gfxId)
    {
        _out.WriteLine($"=== GfxObj 0x{gfxId:X8} ===");
        var gfx = dats.Get<GfxObj>(gfxId);
        if (gfx is null)
        {
            _out.WriteLine($"  Get<GfxObj>(0x{gfxId:X8}) returned NULL — dat reader couldn't load");
            return;
        }

        _out.WriteLine($"  Flags        = {gfx.Flags} (0x{(uint)gfx.Flags:X8})");
        _out.WriteLine($"    HasPhysics      = {gfx.Flags.HasFlag(GfxObjFlags.HasPhysics)}");
        _out.WriteLine($"    HasDIDDegrade   = {gfx.Flags.HasFlag(GfxObjFlags.HasDIDDegrade)}");

        _out.WriteLine($"  VertexArray  = {(gfx.VertexArray is null ? "NULL" : $"non-null, {gfx.VertexArray.Vertices?.Count ?? 0} vertices")}");
        _out.WriteLine($"  PhysicsPolygons = {(gfx.PhysicsPolygons is null ? "NULL" : $"{gfx.PhysicsPolygons.Count} polys")}");
        _out.WriteLine($"  PhysicsBSP   = {(gfx.PhysicsBSP is null ? "NULL" : "non-null")}");
        if (gfx.PhysicsBSP is not null)
        {
            _out.WriteLine($"    PhysicsBSP.Root = {(gfx.PhysicsBSP.Root is null ? "NULL" : "non-null")}");
            if (gfx.PhysicsBSP.Root is { } root)
            {
                _out.WriteLine($"    Root.Type      = {root.Type}");
                _out.WriteLine($"    Root.Polygons  = {root.Polygons?.Count ?? 0}");
                _out.WriteLine($"    Root.PosNode   = {(root.PosNode is null ? "null" : "non-null")}");
                _out.WriteLine($"    Root.NegNode   = {(root.NegNode is null ? "null" : "non-null")}");
                var bs = root.BoundingSphere;
                if (bs is null)
                    _out.WriteLine("    Root.BoundingSphere = null");
                else
                    _out.WriteLine($"    Root.BoundingSphere = ({bs.Origin.X:F3},{bs.Origin.Y:F3},{bs.Origin.Z:F3}) r={bs.Radius:F3}");

                int totalPolys = CountTotalPolys(root);
                _out.WriteLine($"    BSP tree total polys (including children) = {totalPolys}");
            }
        }

        _out.WriteLine($"  Polygons (visual) = {(gfx.Polygons is null ? "NULL" : $"{gfx.Polygons.Count} polys")}");
        if (gfx.DrawingBSP is null)
        {
            _out.WriteLine($"  DrawingBSP   = NULL");
        }
        else
        {
            _out.WriteLine($"  DrawingBSP   = non-null");
            _out.WriteLine($"    DrawingBSP.Root = {(gfx.DrawingBSP.Root is null ? "NULL" : "non-null")}");
        }

        if (gfx.PhysicsPolygons is { Count: > 0 } polys
            && gfx.VertexArray?.Vertices is { } verts && verts.Count > 0)
        {
            _out.WriteLine($"  PhysicsPolygon AABB sweep (first 6 polys):");
            int dumped = 0;
            foreach (var (pid, p) in polys)
            {
                if (dumped++ >= 6) break;
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                int nVerts = p.VertexIds.Count;
                for (int i = 0; i < nVerts; i++)
                {
                    if (!verts.TryGetValue((ushort)p.VertexIds[i], out var sv)) { nVerts = -1; break; }
                    var v = sv.Origin;
                    if (v.X < minX) minX = v.X;
                    if (v.Y < minY) minY = v.Y;
                    if (v.Z < minZ) minZ = v.Z;
                    if (v.X > maxX) maxX = v.X;
                    if (v.Y > maxY) maxY = v.Y;
                    if (v.Z > maxZ) maxZ = v.Z;
                }
                if (nVerts < 0) { _out.WriteLine($"    [0x{pid:X4}] vertex lookup failed"); continue; }
                _out.WriteLine($"    [0x{pid:X4}] nVerts={nVerts} sides={p.SidesType} " +
                               $"min=({minX:F3},{minY:F3},{minZ:F3}) max=({maxX:F3},{maxY:F3},{maxZ:F3})");
            }
            if (polys.Count > 6)
                _out.WriteLine($"    ... ({polys.Count - 6} more)");

            // Total physics polygon AABB
            float gMinX = float.MaxValue, gMinY = float.MaxValue, gMinZ = float.MaxValue;
            float gMaxX = float.MinValue, gMaxY = float.MinValue, gMaxZ = float.MinValue;
            foreach (var (_, p) in polys)
            {
                int nVerts = p.VertexIds.Count;
                for (int i = 0; i < nVerts; i++)
                {
                    if (!verts.TryGetValue((ushort)p.VertexIds[i], out var sv)) continue;
                    var v = sv.Origin;
                    if (v.X < gMinX) gMinX = v.X;
                    if (v.Y < gMinY) gMinY = v.Y;
                    if (v.Z < gMinZ) gMinZ = v.Z;
                    if (v.X > gMaxX) gMaxX = v.X;
                    if (v.Y > gMaxY) gMaxY = v.Y;
                    if (v.Z > gMaxZ) gMaxZ = v.Z;
                }
            }
            _out.WriteLine($"  PhysicsPolygons combined AABB: min=({gMinX:F3},{gMinY:F3},{gMinZ:F3}) max=({gMaxX:F3},{gMaxY:F3},{gMaxZ:F3})");
            _out.WriteLine($"                                size=({gMaxX - gMinX:F3},{gMaxY - gMinY:F3},{gMaxZ - gMinZ:F3})");
        }
    }

    private static int CountTotalPolys(DatReaderWriter.Types.PhysicsBSPNode node)
    {
        int n = node.Polygons?.Count ?? 0;
        if (node.PosNode is not null) n += CountTotalPolys(node.PosNode);
        if (node.NegNode is not null) n += CountTotalPolys(node.NegNode);
        return n;
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void HoltburgCottage_CellPortals_DatInspection()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var cellIds = new uint[] { 0xA9B40150u, 0xA9B4013Fu, 0xA9B40029u };
        foreach (uint cellId in cellIds)
        {
            _out.WriteLine("");
            InspectCell(dats, cellId);
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void HoltburgLandblockStatics_DatInspection()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var lbInfo = dats.Get<DatReaderWriter.DBObjs.LandBlockInfo>(0xA9B4FFFEu);
        if (lbInfo is null)
        {
            _out.WriteLine("LandBlockInfo 0xA9B4FFFE: NULL");
            return;
        }

        _out.WriteLine($"=== LandBlockInfo 0xA9B4FFFE ===");
        _out.WriteLine($"  NumCells = {lbInfo.NumCells}");
        _out.WriteLine($"  Objects  = {lbInfo.Objects.Count} (landblock stabs)");
        _out.WriteLine($"  Buildings = {lbInfo.Buildings.Count}");

        const float SphereX = 133.655f;
        const float SphereY = 17.59f;
        const float Window  = 4f; // search window

        _out.WriteLine("");
        _out.WriteLine($"=== Stabs (Objects) within {Window} m of sphere XY ({SphereX:F2}, {SphereY:F2}) ===");
        int stabHits = 0;
        for (int i = 0; i < lbInfo.Objects.Count; i++)
        {
            var stab = lbInfo.Objects[i];
            float dx = stab.Frame.Origin.X - SphereX;
            float dy = stab.Frame.Origin.Y - SphereY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > Window) continue;
            stabHits++;
            _out.WriteLine($"  [{i}] id=0x{stab.Id:X8} pos=({stab.Frame.Origin.X:F3},{stab.Frame.Origin.Y:F3},{stab.Frame.Origin.Z:F3}) dist={dist:F3}");
        }
        _out.WriteLine($"  Total stab hits: {stabHits}");

        _out.WriteLine("");
        _out.WriteLine($"=== ALL Buildings (sorted by distance to sphere) ===");
        var buildings = lbInfo.Buildings
            .Select((b, i) => new {
                Idx = i, B = b,
                Dist = MathF.Sqrt(
                    (b.Frame.Origin.X - SphereX) * (b.Frame.Origin.X - SphereX) +
                    (b.Frame.Origin.Y - SphereY) * (b.Frame.Origin.Y - SphereY))
            })
            .OrderBy(x => x.Dist)
            .Take(6)
            .ToList();
        foreach (var x in buildings)
        {
            _out.WriteLine($"  [{x.Idx}] modelId=0x{x.B.ModelId:X8} pos=({x.B.Frame.Origin.X:F3},{x.B.Frame.Origin.Y:F3},{x.B.Frame.Origin.Z:F3}) dist={x.Dist:F3} portals={x.B.Portals.Count} numLeaves={x.B.NumLeaves}");
        }
    }

    private void InspectCell(DatCollection dats, uint cellId)
    {
        string typeLabel = (cellId & 0xFFFFu) >= 0x0100u ? "indoor" : "outdoor";
        _out.WriteLine($"=== Cell 0x{cellId:X8} (low=0x{cellId & 0xFFFFu:X4}, type={typeLabel}) ===");

        bool ok = dats.TryGet<EnvCell>(cellId, out var envCell);
        if (!ok || envCell is null)
        {
            _out.WriteLine($"  TryGet<EnvCell>(0x{cellId:X8}) returned false (not in dat or wrong type — outdoor cells use LandBlockInfo, not EnvCell)");
            return;
        }

        _out.WriteLine($"  Flags          = 0x{(uint)envCell.Flags:X8}");
        _out.WriteLine($"  Position.Origin= ({envCell.Position.Origin.X:F3},{envCell.Position.Origin.Y:F3},{envCell.Position.Origin.Z:F3})");
        var q = envCell.Position.Orientation;
        _out.WriteLine($"  Position.Rot   = ({q.X:F4},{q.Y:F4},{q.Z:F4},{q.W:F4})");
        _out.WriteLine($"  EnvironmentId  = 0x{envCell.EnvironmentId:X8}");
        _out.WriteLine($"  CellStructure  = 0x{envCell.CellStructure:X8}");
        _out.WriteLine($"  StaticObjects  = {envCell.StaticObjects.Count}");
        _out.WriteLine($"  VisibleCells   = {envCell.VisibleCells.Count}");
        _out.WriteLine($"  CellPortals    = {envCell.CellPortals.Count}");
        for (int i = 0; i < envCell.CellPortals.Count; i++)
        {
            var p = envCell.CellPortals[i];
            string otherIdStr = p.OtherCellId == 0xFFFFu
                ? "0xFFFF (EXIT-OUTSIDE)"
                : $"0x{p.OtherCellId:X4} (other-indoor)";
            _out.WriteLine($"    [{i}] otherCellId={otherIdStr} polyId=0x{p.PolygonId:X4} flags=0x{(uint)p.Flags:X4}");
        }

        bool envOk = dats.TryGet<DatReaderWriter.DBObjs.Environment>(
            0x0D000000u | envCell.EnvironmentId, out var environment);
        if (!envOk || environment is null)
        {
            _out.WriteLine($"  Environment 0x{envCell.EnvironmentId:X8} NOT loadable");
            return;
        }
        if (!environment.Cells.TryGetValue(envCell.CellStructure, out var cellStruct))
        {
            _out.WriteLine($"  Environment.Cells[0x{envCell.CellStructure:X8}] NOT present");
            return;
        }
        _out.WriteLine($"  CellStruct verts        = {cellStruct.VertexArray?.Vertices.Count ?? 0}");
        _out.WriteLine($"  CellStruct polygons     = {cellStruct.Polygons?.Count ?? 0} (visible)");
        _out.WriteLine($"  CellStruct physicsPolys = {cellStruct.PhysicsPolygons?.Count ?? 0}");

        var physicsPolygons = cellStruct.PhysicsPolygons
            ?? throw new InvalidDataException("CellStruct has no physics polygons collection.");
        var vertices = cellStruct.VertexArray?.Vertices
            ?? throw new InvalidDataException("CellStruct has no vertex array.");

        _out.WriteLine($"  CellStruct PHYSICS polys (world frame):");
        var cellOrigin = envCell.Position.Origin;
        var cellRot    = envCell.Position.Orientation;
        for (int pi = 0; pi < physicsPolygons.Count; pi++)
        {
            var (pid, poly) = (physicsPolygons.Keys.ElementAt(pi),
                               physicsPolygons.Values.ElementAt(pi));
            float wxMin = float.MaxValue, wxMax = float.MinValue;
            float wyMin = float.MaxValue, wyMax = float.MinValue;
            float wzMin = float.MaxValue, wzMax = float.MinValue;
            foreach (var vid in poly.VertexIds)
            {
                if (!vertices.TryGetValue((ushort)vid, out var sv)) continue;
                var rotated = System.Numerics.Vector3.Transform(sv.Origin, cellRot);
                var world   = cellOrigin + rotated;
                if (world.X < wxMin) wxMin = world.X; if (world.X > wxMax) wxMax = world.X;
                if (world.Y < wyMin) wyMin = world.Y; if (world.Y > wyMax) wyMax = world.Y;
                if (world.Z < wzMin) wzMin = world.Z; if (world.Z > wzMax) wzMax = world.Z;
            }
            _out.WriteLine($"    [{pi}=0x{pid:X4}] sides={poly.SidesType} " +
                $"X=[{wxMin:F3},{wxMax:F3}] Y=[{wyMin:F3},{wyMax:F3}] Z=[{wzMin:F3},{wzMax:F3}]");
        }

        for (int i = 0; i < envCell.CellPortals.Count; i++)
        {
            var p = envCell.CellPortals[i];
            if (cellStruct.Polygons is null
                || !cellStruct.Polygons.TryGetValue(p.PolygonId, out var poly))
            {
                _out.WriteLine($"  PortalPoly[{i}=0x{p.PolygonId:X4}] NOT present in CellStruct.Polygons");
                continue;
            }
            // Resolve vertex positions.
            var vs = poly.VertexIds.Select(vid => vertices[(ushort)vid].Origin).ToList();
            string vlist = string.Join(",", vs.Select(v => $"({v.X:F2},{v.Y:F2},{v.Z:F2})"));

            if (vs.Count >= 3)
            {
                var n = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(vs[1] - vs[0], vs[2] - vs[0]));
                float d = -System.Numerics.Vector3.Dot(n, vs[0]);
                _out.WriteLine($"  PortalPoly[{i}=0x{p.PolygonId:X4}] n_local=({n.X:F4},{n.Y:F4},{n.Z:F4}) d_local={d:F4} verts={vlist}");
            }
            else
            {
                _out.WriteLine($"  PortalPoly[{i}=0x{p.PolygonId:X4}] <3 verts, plane n/a verts={vlist}");
            }
        }
    }
}
