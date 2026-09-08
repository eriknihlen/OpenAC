using System.Numerics;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class WorldGameStateTests
{
    private static WorldEntitySnapshot S(uint id, uint sourceId = 0x01000000u) =>
        new(id, sourceId, Vector3.Zero, Quaternion.Identity);

    [Fact]
    public void Add_ReplacesExistingProjectionById()
    {
        var state = new WorldGameState();
        state.Add(S(1));
        state.Add(S(1, 0x01000001u));

        WorldEntitySnapshot current = Assert.Single(state.Entities);
        Assert.Equal(0x01000001u, current.SourceId);
    }

    [Fact]
    public void RemoveById_CompactsIndexWithoutLosingOtherEntities()
    {
        var state = new WorldGameState();
        state.Add(S(1));
        state.Add(S(2));
        state.Add(S(3));

        Assert.True(state.RemoveById(2));
        state.Add(S(3, 0x01000003u));

        Assert.Equal(2, state.Entities.Count);
        Assert.DoesNotContain(state.Entities, entity => entity.Id == 2);
        Assert.Equal(0x01000003u, Assert.Single(state.Entities, entity => entity.Id == 3).SourceId);
    }

    [Fact]
    public void Clear_RemovesCurrentProjectionAndIndex()
    {
        var state = new WorldGameState();
        state.Add(S(1));
        state.Clear();
        state.Add(S(1, 0x01000001u));

        WorldEntitySnapshot current = Assert.Single(state.Entities);
        Assert.Equal(0x01000001u, current.SourceId);
    }
}
