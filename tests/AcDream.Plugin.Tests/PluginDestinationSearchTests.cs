using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests;

public sealed class PluginDestinationSearchTests
{
    [Fact]
    public void PrefixesAreSortedBeforeContainsMatchesAndCapped()
    {
        var places = Enumerable.Range(0, 5000)
            .Select(index => new PluginDestination($"Place {index:0000}", default))
            .Append(new PluginDestination("Old Place", default));
        IReadOnlyList<PluginInputSuggestion> results =
            PluginDestinationSearch.Suggest(places, "place", 3);
        Assert.Equal(3, results.Count);
        Assert.Equal("Place 0000", results[0].Value);
        Assert.Equal("Place 0001", results[1].Value);
        Assert.Equal("Place 0002", results[2].Value);
    }

    [Fact]
    public void RouteEditorRemovesAndReordersStableRows()
    {
        var editor = new PluginRouteEditorState();
        editor.SetLegs([
            new("a", "A", "walk"),
            new("b", "B", "portal"),
            new("c", "C", "walk")]);
        Assert.True(editor.Move("c", 0));
        Assert.True(editor.Remove("b"));
        Assert.Equal(["c", "a"], editor.Legs.Select(row => row.Key));
    }

    [Fact]
    public void EmptyOrInvalidLimitsAreSafe()
    {
        var place = new PluginDestination("Holtburg", default);
        Assert.Single(PluginDestinationSearch.Suggest([place], null));
        Assert.Empty(PluginDestinationSearch.Suggest([place], "holt", 0));
    }
}
