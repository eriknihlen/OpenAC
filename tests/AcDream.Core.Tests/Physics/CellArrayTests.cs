using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellArrayTests
{
    [Fact]
    public void Add_PreservesInsertionOrder()
    {
        var a = new CellArray();
        a.Add(0xA9B40170u);
        a.Add(0xA9B40031u);
        a.Add(0xA9B40171u);
        Assert.Equal(new[] { 0xA9B40170u, 0xA9B40031u, 0xA9B40171u }, a.OrderedIds.ToArray());
    }

    [Fact]
    public void Add_DedupsById_KeepingFirstPosition()
    {
        var a = new CellArray();
        a.Add(0xA9B40170u);
        a.Add(0xA9B40171u);
        a.Add(0xA9B40170u);
        Assert.Equal(2, a.Count);
        Assert.Equal(new[] { 0xA9B40170u, 0xA9B40171u }, a.OrderedIds.ToArray());
    }

    [Fact]
    public void Contains_TracksMembership()
    {
        var a = new CellArray();
        a.Add(0xA9B40170u);
        Assert.Contains(0xA9B40170u, a);
        Assert.DoesNotContain(0xA9B40171u, a);
    }

    [Fact]
    public void EnumeratesInInsertionOrder_AsICollection()
    {
        var a = new CellArray();
        a.Add(3u); a.Add(1u); a.Add(2u);
        ICollection<uint> c = a;               // helper-facing interface
        Assert.Equal(new[] { 3u, 1u, 2u }, c.ToArray());
    }

    [Fact]
    public void IsReadOnlyCollection_ForConsumers()
    {
        var a = new CellArray();
        a.Add(7u); a.Add(7u);
        IReadOnlyCollection<uint> ro = a;
        int count = ro.Count;
        Assert.Equal(1, count);
        Assert.Equal(new[] { 7u }, ro.ToArray());
    }
}
