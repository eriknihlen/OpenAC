using System;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World.Cells;
using Xunit;

namespace AcDream.Core.Tests.World.Cells;

public class CellGraphFixtureTests
{
    private const uint CellarCellId = 0xA9B40147u;

    private static string FixtureDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "AcDream.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.NotNull(dir);
        return Path.Combine(dir!, "tests", "AcDream.Core.Tests", "Fixtures", "issue98");
    }

    private static EnvCell EnvFromFixture(uint cellId)
    {
        var path = Path.Combine(FixtureDir(), $"0x{cellId:X8}.json");
        Assert.True(File.Exists(path), $"missing fixture {path}");
        var cp = CellDumpSerializer.Hydrate(CellDumpSerializer.Read(path));

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var poly in cp.Resolved.Values)
            foreach (var v in poly.Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        if (min.X == float.MaxValue) { min = Vector3.Zero; max = Vector3.Zero; }

        return new EnvCell(cellId, cp.WorldTransform, cp.InverseWorldTransform, min, max,
            Array.Empty<CellPortal>(), Array.Empty<uint>(), false, cp.CellBSP);
    }

    [Fact]
    public void RealCottageCell_ResolvesViaGetVisible_AndIsEnv()
    {
        var g = new CellGraph();
        var env = EnvFromFixture(CellarCellId);
        g.Add(env);

        var resolved = g.GetVisible(CellarCellId);
        Assert.Same(env, resolved);
        Assert.True(resolved!.IsEnv);
        Assert.True(env.LocalBoundsMax.X > env.LocalBoundsMin.X, "non-degenerate bounds X");
        Assert.True(env.LocalBoundsMax.Y > env.LocalBoundsMin.Y, "non-degenerate bounds Y");
    }
}
