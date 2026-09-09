using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class LandWalkOrderTests
{
    [Theory]
    [InlineData(5, 5, 11)]
    [InlineData(0, 0, 11)]
    [InlineData(10, 3, 11)]  // viewer at an edge
    [InlineData(1, 1, 3)]
    public void Block_order_covers_every_slot_exactly_once_viewer_first(
        int vx, int vy, int width)
    {
        Span<int> order = new int[width * width];

        int count = LandWalkOrder.GetBlockOrder(vx, vy, width, order);

        Assert.Equal(width * width, count);
        Assert.Equal(vx * width + vy, order[0]);
        var seen = new HashSet<int>();
        foreach (int slot in order)
            Assert.True(seen.Add(slot), $"slot {slot} emitted twice");
    }

    [Fact]
    public void Block_order_is_ring_monotone_near_to_far()
    {
        const int width = 11;
        const int vx = 5, vy = 5;
        Span<int> order = new int[width * width];
        LandWalkOrder.GetBlockOrder(vx, vy, width, order);

        int previousRing = 0;
        foreach (int slot in order)
        {
            int ring = Math.Max(Math.Abs(slot / width - vx), Math.Abs(slot % width - vy));
            Assert.True(ring >= previousRing, "a later entry moved to a NEARER ring");
            previousRing = ring;
        }
    }

    [Fact]
    public void Block_order_ring_one_matches_the_decoded_slot_pattern()
    {
        const int width = 3;
        Span<int> order = new int[9];
        LandWalkOrder.GetBlockOrder(1, 1, width, order);

        int[] expected =
        [
            1 * 3 + 1,   // viewer
            1 * 3 + 2,   // (0,+1)
            0 * 3 + 1,   // (-1,0)
            1 * 3 + 0,   // (0,-1)
            2 * 3 + 1,   // (+1,0)
            2 * 3 + 2,   // (+1,+1)
            0 * 3 + 2,   // (-1,+1)
            0 * 3 + 0,   // (-1,-1)
            2 * 3 + 0,   // (+1,-1)
        ];
        Assert.Equal(expected, order.ToArray());
    }

    [Theory]
    [InlineData(0, 0, 8)]
    [InlineData(7, 7, 8)]
    [InlineData(3, 5, 8)]
    [InlineData(0, 0, 1)]
    public void Cell_order_fills_exactly_and_ends_at_the_closest_cell(
        int cx, int cy, int side)
    {
        Span<int> order = new int[side * side];
        order.Fill(-1);

        LandWalkOrder.FillCellOrderFarToNear(cx, cy, side, order);

        Assert.Equal(cx * side + cy, order[^1]);
        var seen = new HashSet<int>();
        foreach (int slot in order)
        {
            Assert.InRange(slot, 0, side * side - 1);
            Assert.True(seen.Add(slot), $"slot {slot} written twice");
        }
    }

    [Fact]
    public void Cell_order_forward_walk_is_far_to_near()
    {
        const int side = 8;
        const int cx = 2, cy = 6;
        Span<int> order = new int[side * side];
        LandWalkOrder.FillCellOrderFarToNear(cx, cy, side, order);

        int previousRing = int.MaxValue;
        foreach (int slot in order)
        {
            int ring = Math.Max(Math.Abs(slot / side - cx), Math.Abs(slot % side - cy));
            Assert.True(ring <= previousRing, "a later entry moved to a FARTHER ring");
            previousRing = ring;
        }
    }

    [Theory]
    [InlineData(0, 0, LandDirection.InViewerBlock)]
    [InlineData(0, 3, LandDirection.North)]
    [InlineData(0, -1, LandDirection.South)]
    [InlineData(2, 0, LandDirection.East)]
    [InlineData(-4, 0, LandDirection.West)]
    [InlineData(-1, 1, LandDirection.NorthWest)]
    [InlineData(-2, -2, LandDirection.SouthWest)]
    [InlineData(3, 1, LandDirection.NorthEast)]
    [InlineData(1, -5, LandDirection.SouthEast)]
    public void Direction_mapping_matches_get_dir(int dx, int dy, LandDirection expected)
        => Assert.Equal(expected, LandWalkOrder.GetDirection(dx, dy));

    [Theory]
    [InlineData(LandDirection.InViewerBlock, 5, 3, 8, 5, 3)]
    [InlineData(LandDirection.North, 5, 3, 8, 5, 0)]      // block north: south edge faces viewer
    [InlineData(LandDirection.South, 5, 3, 8, 5, 7)]
    [InlineData(LandDirection.East, 5, 3, 8, 0, 3)]       // block east: west edge faces viewer
    [InlineData(LandDirection.West, 5, 3, 8, 7, 3)]
    [InlineData(LandDirection.NorthWest, 5, 3, 8, 7, 0)]
    [InlineData(LandDirection.SouthWest, 5, 3, 8, 7, 7)]
    [InlineData(LandDirection.NorthEast, 5, 3, 8, 0, 0)]
    [InlineData(LandDirection.SouthEast, 5, 3, 8, 0, 7)]
    public void Closest_cell_matches_the_direction_switch(
        LandDirection dir, int sqx, int sqy, int side, int expectedX, int expectedY)
        => Assert.Equal((expectedX, expectedY), LandWalkOrder.ClosestCell(dir, sqx, sqy, side));

    [Fact]
    public void Closest_cell_scales_the_viewer_coordinate_for_low_resolution_blocks()
    {
        Assert.Equal((2, 1), LandWalkOrder.ClosestCell(LandDirection.InViewerBlock, 5, 3, 4));
        Assert.Equal((0, 0), LandWalkOrder.ClosestCell(LandDirection.InViewerBlock, 7, 7, 1));
    }
}
