using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;
using Placement = DatReaderWriter.Enums.Placement;

namespace AcDream.Core.Tests.Physics;

public class Issue175HubDoorPoseInspectionTests
{
    private readonly ITestOutputHelper _out;
    public Issue175HubDoorPoseInspectionTests(ITestOutputHelper output) => _out = output;

    private const uint HubDoorSetupId = 0x02000C9Du;

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void HubDoorSetup_PlacementVsMotionPose_DatInspection()
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

        var setup = dats.Get<Setup>(HubDoorSetupId);
        Assert.NotNull(setup);

        _out.WriteLine($"=== Setup 0x{HubDoorSetupId:X8} ===");
        _out.WriteLine($"  Flags            = {setup!.Flags} (0x{(uint)setup.Flags:X8})");
        _out.WriteLine($"  Parts            = {setup.Parts.Count}");
        for (int i = 0; i < setup.Parts.Count; i++)
            _out.WriteLine($"    [{i}] gfxObj=0x{setup.Parts[i]:X8}");
        _out.WriteLine($"  DefaultAnimation = 0x{setup.DefaultAnimation:X8}");
        _out.WriteLine($"  DefaultScript    = 0x{setup.DefaultScript:X8}");
        _out.WriteLine($"  DefaultMotionTable = 0x{setup.DefaultMotionTable:X8}");
        _out.WriteLine($"  CylSpheres={setup.CylSpheres.Count} Spheres={setup.Spheres.Count} Radius={setup.Radius:F3}");
        foreach (var c in setup.CylSpheres)
            _out.WriteLine($"    cyl r={c.Radius:F3} h={c.Height:F3} origin=({c.Origin.X:F3},{c.Origin.Y:F3},{c.Origin.Z:F3})");

        _out.WriteLine($"  PlacementFrames  = {setup.PlacementFrames.Count}");
        foreach (var kv in setup.PlacementFrames)
        {
            _out.WriteLine($"    [{kv.Key}] frames={kv.Value.Frames.Count}");
            for (int i = 0; i < kv.Value.Frames.Count; i++)
            {
                var f = kv.Value.Frames[i];
                _out.WriteLine(
                    $"      part[{i}] pos=({f.Origin.X:F3},{f.Origin.Y:F3},{f.Origin.Z:F3}) " +
                    $"rot=({f.Orientation.X:F3},{f.Orientation.Y:F3},{f.Orientation.Z:F3},{f.Orientation.W:F3})");
            }
        }

        foreach (uint gfxId in setup.Parts.Distinct())
        {
            var gfx = dats.Get<GfxObj>(gfxId);
            _out.WriteLine($"=== GfxObj 0x{gfxId:X8} ===");
            if (gfx is null) { _out.WriteLine("  NULL"); continue; }
            var root = gfx.PhysicsBSP?.Root;
            _out.WriteLine($"  PhysicsBSP.Root = {(root is null ? "NULL" : "non-null")}");
            if (root?.BoundingSphere is { } bs)
                _out.WriteLine($"  BSP bounds = ({bs.Origin.X:F3},{bs.Origin.Y:F3},{bs.Origin.Z:F3}) r={bs.Radius:F3}");
            if (gfx.PhysicsPolygons is { } pp && gfx.VertexArray?.Vertices is { } verts)
            {
                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var poly in pp.Values)
                foreach (var vid in poly.VertexIds)
                {
                    if (!verts.TryGetValue((ushort)vid, out var sv)) continue;
                    minX = Math.Min(minX, sv.Origin.X); maxX = Math.Max(maxX, sv.Origin.X);
                    minY = Math.Min(minY, sv.Origin.Y); maxY = Math.Max(maxY, sv.Origin.Y);
                    minZ = Math.Min(minZ, sv.Origin.Z); maxZ = Math.Max(maxZ, sv.Origin.Z);
                }
                _out.WriteLine($"  Physics AABB (part-local) = X[{minX:F3},{maxX:F3}] Y[{minY:F3},{maxY:F3}] Z[{minZ:F3},{maxZ:F3}]");
            }
        }

        if (setup.DefaultMotionTable != 0)
        {
            var mt = dats.Get<MotionTable>(setup.DefaultMotionTable);
            _out.WriteLine($"=== MotionTable 0x{setup.DefaultMotionTable:X8} ===");
            if (mt is null) { _out.WriteLine("  NULL"); return; }
            _out.WriteLine($"  DefaultStyle = 0x{(uint)mt.DefaultStyle:X8}");
            if (mt.Cycles.TryGetValue((int)mt.DefaultStyle, out var defCycle)
                && defCycle.Anims.Count > 0)
            {
                var animRef = defCycle.Anims[0];
                _out.WriteLine($"  default cycle anim[0] id=0x{animRef.AnimId:X8} lo={animRef.LowFrame} hi={animRef.HighFrame}");
                var anim = dats.Get<Animation>(animRef.AnimId);
                if (anim is not null && anim.PartFrames.Count > 0)
                {
                    var f0 = anim.PartFrames[Math.Clamp((int)animRef.LowFrame, 0, anim.PartFrames.Count - 1)];
                    for (int i = 0; i < f0.Frames.Count; i++)
                    {
                        var f = f0.Frames[i];
                        _out.WriteLine(
                            $"  anim frame0 part[{i}] pos=({f.Origin.X:F3},{f.Origin.Y:F3},{f.Origin.Z:F3}) " +
                            $"rot=({f.Orientation.X:F3},{f.Orientation.Y:F3},{f.Orientation.Z:F3},{f.Orientation.W:F3})");
                    }
                }
                else
                {
                    _out.WriteLine("  anim NULL or no PartFrames");
                }
            }
            else
            {
                _out.WriteLine("  no default-style cycle");
            }
        }
        else
        {
            _out.WriteLine("=== no DefaultMotionTable on the setup ===");
        }
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void MotionTablePose_DefaultState_ResolvesOnRealTable()
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
        var mt = dats.Get<MotionTable>(0x09000001u);
        Assert.NotNull(mt);

        var pose = AcDream.Core.Physics.Motion.MotionTablePose.DefaultStatePartFrames(
            mt!, id => dats.Get<Animation>(id));

        Assert.NotNull(pose);
        _out.WriteLine($"human MT default pose parts={pose!.Count} " +
                       $"part0=({pose[0].Origin.X:F3},{pose[0].Origin.Y:F3},{pose[0].Origin.Z:F3})");
        Assert.True(pose.Count >= 1);
    }


    private static Setup MakeTwoPartSetup()
    {
        var setup = new Setup();
        setup.Parts.Add(0x01000001u);
        setup.Parts.Add(0x01000002u);
        var placement = new AnimationFrame(2);
        placement.Frames.Clear();
        placement.Frames.Add(new Frame { Origin = new Vector3(0.88f, -0.44f, 1.37f),
                                         Orientation = new Quaternion(0f, 0f, -0.966f, 0.259f) });
        placement.Frames.Add(new Frame { Origin = new Vector3(-0.88f, -0.44f, 1.37f),
                                         Orientation = new Quaternion(0f, 0f, -0.259f, 0.966f) });
        setup.PlacementFrames[Placement.Default] = placement;
        return setup;
    }

    [Fact]
    public void FromSetup_PartPoseOverride_ReplacesPlacementFrames()
    {
        var setup = MakeTwoPartSetup();
        var closed = new[]
        {
            new Frame { Origin = new Vector3(0.85f, 0f, 1.37f), Orientation = Quaternion.Identity },
            new Frame { Origin = new Vector3(-0.85f, 0f, 1.37f), Orientation = Quaternion.Identity },
        };

        var shapes = ShadowShapeBuilder.FromSetup(
            setup, entScale: 1f, hasPhysicsBsp: _ => true, partPoseOverride: closed);

        Assert.Equal(2, shapes.Count);
        Assert.Equal(new Vector3(0.85f, 0f, 1.37f), shapes[0].LocalPosition);
        Assert.Equal(Quaternion.Identity, shapes[0].LocalRotation);
        Assert.Equal(new Vector3(-0.85f, 0f, 1.37f), shapes[1].LocalPosition);
    }

    [Fact]
    public void FromSetup_NoOverride_KeepsPlacementFrames()
    {
        var setup = MakeTwoPartSetup();

        var shapes = ShadowShapeBuilder.FromSetup(
            setup, entScale: 1f, hasPhysicsBsp: _ => true);

        Assert.Equal(2, shapes.Count);
        Assert.Equal(new Vector3(0.88f, -0.44f, 1.37f), shapes[0].LocalPosition);
        Assert.Equal(new Quaternion(0f, 0f, -0.966f, 0.259f), shapes[0].LocalRotation);
    }

    [Fact]
    public void FromSetup_ShortOverride_FallsBackPerPart()
    {
        var setup = MakeTwoPartSetup();
        var shortOverride = new[]
        {
            new Frame { Origin = new Vector3(0.85f, 0f, 1.37f), Orientation = Quaternion.Identity },
        };

        var shapes = ShadowShapeBuilder.FromSetup(
            setup, entScale: 1f, hasPhysicsBsp: _ => true, partPoseOverride: shortOverride);

        Assert.Equal(2, shapes.Count);
        Assert.Equal(new Vector3(0.85f, 0f, 1.37f), shapes[0].LocalPosition);   // override
        Assert.Equal(new Vector3(-0.88f, -0.44f, 1.37f), shapes[1].LocalPosition); // placement fallback
    }
}
