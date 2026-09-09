using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellTransitAddAllOutsideCellsTests
{
    [Fact]
    public void SphereWellInsideCell_AddsOneCell()
    {
        var candidates = new HashSet<uint>();
        CellTransit.AddAllOutsideCells(
            worldSphereCenter: new Vector3(12f, 12f, 0f),
            sphereRadius: 0.5f,
            currentCellId: 0xA9B40001u,
            currentBlockOrigin: Vector3.Zero,
            candidates: candidates);

        Assert.Single(candidates);
        Assert.Contains(0xA9B40001u, candidates);
    }

    [Fact]
    public void SphereAtCellEastBoundary_AddsTwoCells()
    {
        var candidates = new HashSet<uint>();
        CellTransit.AddAllOutsideCells(
            worldSphereCenter: new Vector3(23.6f, 12f, 0f),
            sphereRadius: 0.5f,
            currentCellId: 0xA9B40001u,
            currentBlockOrigin: Vector3.Zero,
            candidates: candidates);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(0xA9B40001u, candidates);
        Assert.Contains(0xA9B40009u, candidates);
    }


    [Fact]
    public void SphereJustSouthOfBlockBoundary_AddsBothBlocks()
    {
        var candidates = new HashSet<uint>();
        CellTransit.AddAllOutsideCells(
            worldSphereCenter: new Vector3(150f, -0.2f, 0f),
            sphereRadius: 0.5f,
            currentCellId: 0xA9B40031u,
            currentBlockOrigin: Vector3.Zero,
            candidates: candidates);

        Assert.Contains(0xA9B30038u, candidates);
        Assert.Contains(0xA9B40031u, candidates);   // +Y neighbour (home block)
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void SphereDeepInNeighbourBlock_AddsNeighbourCellOnly()
    {
        var candidates = new HashSet<uint>();
        CellTransit.AddAllOutsideCells(
            worldSphereCenter: new Vector3(150f, -109.65f, 0f),
            sphereRadius: 0.5f,
            currentCellId: 0xA9B40031u,
            currentBlockOrigin: Vector3.Zero,
            candidates: candidates);

        Assert.Contains(0xA9B30034u, candidates);
        Assert.DoesNotContain(0xA9B40031u, candidates);
    }

    [Fact]
    public void NonAnchorBlockOrigin_ConvertsWorldFrame()
    {
        var candidates = new HashSet<uint>();
        CellTransit.AddAllOutsideCells(
            worldSphereCenter: new Vector3(150f, -0.2f, 0f),
            sphereRadius: 0.5f,
            currentCellId: 0xA9B30038u,
            currentBlockOrigin: new Vector3(0f, -192f, 0f),
            candidates: candidates);

        Assert.Contains(0xA9B30038u, candidates);
        Assert.Contains(0xA9B40031u, candidates);
    }
}
