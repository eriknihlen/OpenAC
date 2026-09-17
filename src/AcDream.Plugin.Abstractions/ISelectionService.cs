namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Reports that the client's selected object changed.
/// </summary>
/// <param name="PreviousObjectId">
/// The object that was selected before the change, or null if nothing was.
/// </param>
/// <param name="SelectedObjectId">
/// The object selected after the change, or null if the selection was
/// cleared.
/// </param>
public readonly record struct SelectionChangedEvent(
    uint? PreviousObjectId,
    uint? SelectedObjectId);

/// <summary>
/// Read/write access to the client's single selected object -- the target
/// highlighted in the world and used by the client's own targeted actions.
/// </summary>
public interface ISelectionService
{
    /// <summary>
    /// The currently selected object, or null when nothing is selected.
    /// </summary>
    uint? SelectedObjectId { get; }

    /// <summary>
    /// The object that was selected immediately before the current one, or
    /// null if there is no such earlier selection.
    /// </summary>
    uint? PreviousObjectId { get; }

    /// <summary>
    /// Raised whenever the selection changes, including when it is cleared.
    /// </summary>
    event Action<SelectionChangedEvent> Changed;

    /// <summary>
    /// Selects an object. Passing zero clears the selection instead.
    /// Returns false when that object was already the selected one, so
    /// nothing changed.
    /// </summary>
    bool Select(uint objectId);

    /// <summary>
    /// Clears the selection. Returns false when nothing was selected.
    /// </summary>
    bool Clear();
}
