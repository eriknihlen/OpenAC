namespace AcDream.Plugin.Abstractions;

/// <summary>A named destination offered by a navigation plugin.</summary>
public sealed record PluginDestination(string Name, PluginNavigationPosition Position);

/// <summary>A row in a plugin route editor.</summary>
public sealed record PluginRouteLegRow(
    string Key,
    string Destination,
    string LegType,
    string? Action = null,
    double? Distance = null);

/// <summary>Binding state for an editable, stable-keyed route list.</summary>
public sealed class PluginRouteEditorState : PluginPanelBinding
{
    private readonly List<PluginRouteLegRow> _legs = [];

    /// <summary>The current route rows in display order.</summary>
    public IReadOnlyList<PluginRouteLegRow> Legs => _legs;

    /// <summary>Replaces the route while preserving the supplied row keys.</summary>
    public void SetLegs(IEnumerable<PluginRouteLegRow> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);
        _legs.Clear();
        _legs.AddRange(legs);
        Invalidate(nameof(Legs));
    }

    /// <summary>Removes a row by stable key.</summary>
    public bool Remove(string key)
    {
        int index = _legs.FindIndex(row => StringComparer.Ordinal.Equals(row.Key, key));
        if (index < 0) return false;
        _legs.RemoveAt(index);
        Invalidate(nameof(Legs));
        return true;
    }

    /// <summary>Moves a row to a new display index.</summary>
    public bool Move(string key, int newIndex)
    {
        int oldIndex = _legs.FindIndex(row => StringComparer.Ordinal.Equals(row.Key, key));
        if (oldIndex < 0 || newIndex < 0 || newIndex >= _legs.Count) return false;
        PluginRouteLegRow row = _legs[oldIndex];
        _legs.RemoveAt(oldIndex);
        _legs.Insert(newIndex, row);
        Invalidate(nameof(Legs));
        return true;
    }
}

/// <summary>Small, deterministic destination search suitable for a text-input binding.</summary>
public static class PluginDestinationSearch
{
    /// <summary>Returns prefix matches first, then contains matches, capped by <paramref name="limit"/>.</summary>
    public static IReadOnlyList<PluginInputSuggestion> Suggest(
        IEnumerable<PluginDestination> destinations, string? query, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        if (limit <= 0) return Array.Empty<PluginInputSuggestion>();
        string needle = query?.Trim() ?? string.Empty;
        return destinations
            .Where(destination => needle.Length == 0
                || destination.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(destination => needle.Length != 0
                && !destination.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ThenBy(destination => destination.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(destination => new PluginInputSuggestion(destination.Name))
            .ToArray();
    }
}
