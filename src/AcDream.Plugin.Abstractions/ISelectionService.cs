namespace AcDream.Plugin.Abstractions;

public readonly record struct SelectionChangedEvent(
    uint? PreviousObjectId,
    uint? SelectedObjectId);

public interface ISelectionService
{
    uint? SelectedObjectId { get; }
    uint? PreviousObjectId { get; }
    event Action<SelectionChangedEvent> Changed;
    bool Select(uint objectId);
    bool Clear();
}
