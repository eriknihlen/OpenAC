using System.Numerics;
using Xunit;

namespace AcDream.Core.Tests.Conformance;

public class RetailTraceTests
{
    [Fact]
    public void Parse_FindCellListLine_YieldsSeedPosAndPicked()
    {
        const string line =
            "[fcl] seed=0xA9B40170 px=141.5000 py=7.2200 pz=92.7400 picked=0xA9B40171";
        var rec = RetailTrace.ParseFindCellList(line);
        Assert.NotNull(rec);
        Assert.Equal(0xA9B40170u, rec!.SeedCellId);
        Assert.Equal(new Vector3(141.5f, 7.22f, 92.74f), rec.Position);
        Assert.Equal(0xA9B40171u, rec.PickedCellId);
    }

    [Fact]
    public void Parse_NegativeCoordinatesAndLowercaseHex_Ok()
    {
        const string line =
            "[fcl] seed=0xa9b40031 px=-12.5 py=0 pz=-3.25 picked=0xa9b40170";
        var rec = RetailTrace.ParseFindCellList(line);
        Assert.NotNull(rec);
        Assert.Equal(0xA9B40031u, rec!.SeedCellId);
        Assert.Equal(new Vector3(-12.5f, 0f, -3.25f), rec.Position);
        Assert.Equal(0xA9B40170u, rec.PickedCellId);
    }

    [Fact]
    public void Parse_NonMatchingLine_ReturnsNull()
    {
        Assert.Null(RetailTrace.ParseFindCellList("[BP4] find_collisions hit#10170 collide=0"));
        Assert.Null(RetailTrace.ParseFindCellList(""));
        Assert.Null(RetailTrace.ParseFindCellList("[fcl] seed=0xA9B40170 px=1 py=2")); // truncated
    }

    [Fact]
    public void ParseFile_SkipsNoiseAndYieldsOnlyPicks()
    {
        var lines = new[]
        {
            "armed; walk now",
            "[fcl] seed=0xA9B40031 px=160.0 py=10.0 pz=94.0 picked=0xA9B40170",
            "[BP] noise",
            "[fcl] seed=0xA9B40170 px=158.0 py=12.0 pz=95.0 picked=0xA9B40171",
        };
        var picks = RetailTrace.ParseAll(lines);
        Assert.Equal(2, picks.Count);
        Assert.Equal(0xA9B40170u, picks[0].PickedCellId);
        Assert.Equal(0xA9B40171u, picks[1].PickedCellId);
    }
}
