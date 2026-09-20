using System.ComponentModel;

namespace AcDream.Plugin.Abstractions;

/// <summary>A suggestion displayed by a plugin text input.</summary>
public sealed record PluginInputSuggestion(string Value, string? DisplayText = null);

/// <summary>A stable row for a virtualized plugin list.</summary>
public sealed record PluginListRow(string Key, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Optional binding contract for live plugin panels. Hosts subscribe to
/// changes and reevaluate only affected bindings.
/// </summary>
public interface IPluginPanelBinding : INotifyPropertyChanged
{
    /// <summary>Requests reevaluation of all bindings for this object.</summary>
    void Invalidate(string? propertyName = null);
}

/// <summary>Base binding implementation with safe invalidation semantics.</summary>
public abstract class PluginPanelBinding : IPluginPanelBinding
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public void Invalidate(string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
