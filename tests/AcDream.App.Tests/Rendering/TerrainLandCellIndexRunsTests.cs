using System.Collections.Generic;
using System.Linq;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class TerrainLandCellIndexRunsTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    [InlineData(2)]
    [InlineData(1)]
    public void EveryCellOfASide_CoversDisjointRunsTotalingTheWholeLandblock(int side)
    {
        var covered = new bool[384];
        int expectedTotalPerCell = (8 / side) * (8 / side) * 6;

        for (int cellIndex = 0; cellIndex < side * side; cellIndex++)
        {
            var runs = new List<(int Start, int Count)>();
            TerrainModernRenderer.AppendCellIndexRuns(side, cellIndex, runs);

            Assert.Equal(expectedTotalPerCell, runs.Sum(r => r.Count));
            foreach ((int start, int count) in runs)
            {
                Assert.InRange(start, 0, 383);
                Assert.InRange(start + count, 1, 384);
                for (int i = start; i < start + count; i++)
                {
                    Assert.False(
                        covered[i],
                        $"index {i} double-covered by side={side} cellIndex={cellIndex}");
                    covered[i] = true;
                }
            }
        }

        Assert.All(covered, c => Assert.True(c));
    }

    [Fact]
    public void Side1_EmitsOneRunOfTheWholeLandblock()
    {
        var runs = new List<(int Start, int Count)>();
        TerrainModernRenderer.AppendCellIndexRuns(1, 0, runs);

        Assert.Equal(new[] { (0, 384) }, runs);
    }

    [Fact]
    public void Side4_EmitsTwoRunsOfTwelve()
    {
        var runs = new List<(int Start, int Count)>();
        TerrainModernRenderer.AppendCellIndexRuns(4, 0, runs);

        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal(12, r.Count));
    }

    [Fact]
    public void Side2_EmitsFourRunsOfTwentyFour()
    {
        var runs = new List<(int Start, int Count)>();
        TerrainModernRenderer.AppendCellIndexRuns(2, 0, runs);

        Assert.Equal(4, runs.Count);
        Assert.All(runs, r => Assert.Equal(24, r.Count));
    }

    [Fact]
    public void Side8_EmitsOneSixIndexRunAtTheCellsExactOffset()
    {
        int cellIndex = 3 * 8 + 5;
        var runs = new List<(int Start, int Count)>();
        TerrainModernRenderer.AppendCellIndexRuns(8, cellIndex, runs);

        Assert.Equal(new[] { (258, 6) }, runs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void InvalidSideCellCount_Throws(int side)
    {
        var runs = new List<(int Start, int Count)>();
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => TerrainModernRenderer.AppendCellIndexRuns(side, 0, runs));
    }

    [Theory]
    [InlineData(8, 64)]
    [InlineData(8, -1)]
    [InlineData(4, 16)]
    [InlineData(1, 1)]
    public void OutOfRangeCellIndex_Throws(int side, int cellIndex)
    {
        var runs = new List<(int Start, int Count)>();
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => TerrainModernRenderer.AppendCellIndexRuns(side, cellIndex, runs));
    }
}
