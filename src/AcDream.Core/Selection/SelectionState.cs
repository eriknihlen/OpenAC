using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Selection;

public enum SelectionChangeSource
{
    System,
    World,
    Radar,
    Inventory,
    ExternalContainer,
    Paperdoll,
    Toolbar,
    Keyboard,
    Plugin,
    Vendor,
    Social,
}

public enum SelectionChangeReason
{
    Selected,
    Cleared,
    SelectedObjectRemoved,
    CombatTargetDied,
    PreviousSelection,
    SessionReset,
}

public readonly record struct SelectionTransition(
    uint? PreviousObjectId,
    uint? SelectedObjectId,
    SelectionChangeSource Source,
    SelectionChangeReason Reason);

public sealed class SelectionState : ISelectionService
{
    private event Action<SelectionChangedEvent>? PluginChanged;

    public uint? SelectedObjectId { get; private set; }
    public uint? PreviousObjectId { get; private set; }
    public uint? PreviousValidObjectId { get; private set; }

    public event Action<SelectionTransition>? Changed;

    event Action<SelectionChangedEvent> ISelectionService.Changed
    {
        add => PluginChanged += value;
        remove => PluginChanged -= value;
    }

    public bool Select(
        uint objectId,
        SelectionChangeSource source,
        SelectionChangeReason reason = SelectionChangeReason.Selected)
        => objectId == 0
            ? Clear(source)
            : Set(objectId, source, reason);

    public bool Clear(
        SelectionChangeSource source,
        SelectionChangeReason reason = SelectionChangeReason.Cleared)
        => Set(null, source, reason);

    public bool SelectPrevious(SelectionChangeSource source = SelectionChangeSource.Keyboard)
        => PreviousObjectId is uint previous
            && Set(previous, source, SelectionChangeReason.PreviousSelection);

    public bool Reset(SelectionChangeSource source = SelectionChangeSource.System)
    {
        uint? old = SelectedObjectId;
        bool changed = old is not null
            || PreviousObjectId is not null
            || PreviousValidObjectId is not null;
        SelectedObjectId = null;
        PreviousObjectId = null;
        PreviousValidObjectId = null;

        var transition = new SelectionTransition(
            old,
            null,
            source,
            SelectionChangeReason.SessionReset);
        List<Exception>? failures = DispatchChanged(transition);
        DispatchPluginChanged(new SelectionChangedEvent(old, null));
        if (failures is not null)
            throw new AggregateException(
                "One or more selection reset observers failed.",
                failures);
        return changed;
    }

    bool ISelectionService.Select(uint objectId)
        => Select(objectId, SelectionChangeSource.Plugin);

    bool ISelectionService.Clear()
        => Clear(SelectionChangeSource.Plugin);

    private bool Set(
        uint? selectedObjectId,
        SelectionChangeSource source,
        SelectionChangeReason reason)
    {
        if (SelectedObjectId == selectedObjectId)
            return false;

        uint? old = SelectedObjectId;
        SelectedObjectId = selectedObjectId;
        PreviousObjectId = old;
        if (old is not null)
            PreviousValidObjectId = old;

        var transition = new SelectionTransition(old, selectedObjectId, source, reason);
        Changed?.Invoke(transition);
        DispatchPluginChanged(new SelectionChangedEvent(old, selectedObjectId));
        return true;
    }

    private List<Exception>? DispatchChanged(SelectionTransition transition)
    {
        Action<SelectionTransition>? listeners = Changed;
        if (listeners is null)
            return null;

        List<Exception>? failures = null;
        foreach (Action<SelectionTransition> listener in listeners.GetInvocationList())
        {
            try { listener(transition); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        return failures;
    }

    private void DispatchPluginChanged(SelectionChangedEvent change)
    {
        Action<SelectionChangedEvent>? listeners = PluginChanged;
        if (listeners is null) return;
        foreach (Action<SelectionChangedEvent> listener in listeners.GetInvocationList())
        {
            try { listener(change); }
            catch { }
        }
    }
}
