using System;

namespace AcDream.Core.Items;

public enum ExternalContainerTransitionKind
{
    Opened,
    ReplacementRequested,
    Closed,
    Reset,
}

public readonly record struct ExternalContainerTransition(
    ExternalContainerTransitionKind Kind,
    uint PreviousContainerId,
    uint ContainerId);

public sealed class ExternalContainerState
{
    private readonly HashSet<uint> _openedCorpses = [];

    public uint RequestedContainerId { get; private set; }
    public uint CurrentContainerId { get; private set; }
    public int OpenedCorpseCount => _openedCorpses.Count;

    public event Action<ExternalContainerTransition>? Changed;

    public bool RequestOpen(uint containerId, bool isCorpse = false)
    {
        if (containerId == 0u)
            return false;

        if (isCorpse)
            _openedCorpses.Add(containerId);

        if (RequestedContainerId == containerId)
            return false;

        uint previous = CurrentContainerId;
        RequestedContainerId = containerId;

        if (previous != 0u && previous != containerId)
        {
            CurrentContainerId = 0u;
            Changed?.Invoke(new ExternalContainerTransition(
                ExternalContainerTransitionKind.ReplacementRequested,
                previous,
                containerId));
        }
        return true;
    }

    public bool HasCorpseBeenOpened(uint objectId)
        => objectId != 0u && _openedCorpses.Contains(objectId);

    public bool SetCorpseDeleted(uint objectId)
        => objectId != 0u && _openedCorpses.Remove(objectId);

    public bool ApplyViewContents(uint containerId)
    {
        if (containerId == 0u || containerId != RequestedContainerId)
            return false;

        uint previous = CurrentContainerId;
        CurrentContainerId = containerId;
        RequestedContainerId = containerId;
        if (previous == containerId)
            return false;

        Changed?.Invoke(new ExternalContainerTransition(
            ExternalContainerTransitionKind.Opened,
            previous,
            containerId));
        return true;
    }

    public bool ApplyClose(uint containerId)
    {
        if (containerId == 0u || containerId != CurrentContainerId)
            return false;

        uint previous = CurrentContainerId;
        CurrentContainerId = 0u;
        RequestedContainerId = 0u;
        Changed?.Invoke(new ExternalContainerTransition(
            ExternalContainerTransitionKind.Closed,
            previous,
            0u));
        return true;
    }

    public bool ApplyUseDone(uint weenieError)
    {
        if (weenieError == 0u || RequestedContainerId == CurrentContainerId)
            return false;
        RequestedContainerId = CurrentContainerId;
        return true;
    }

    public bool Reset()
    {
        uint previous = CurrentContainerId;
        bool changed = previous != 0u
            || RequestedContainerId != 0u
            || _openedCorpses.Count != 0;
        CurrentContainerId = 0u;
        RequestedContainerId = 0u;
        _openedCorpses.Clear();

        var transition = new ExternalContainerTransition(
            ExternalContainerTransitionKind.Reset,
            previous,
            0u);
        Action<ExternalContainerTransition>? listeners = Changed;
        if (listeners is not null)
        {
            List<Exception>? failures = null;
            foreach (Action<ExternalContainerTransition> listener in listeners.GetInvocationList())
            {
                try { listener(transition); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null)
                throw new AggregateException(
                    "One or more external-container reset observers failed.",
                    failures);
        }
        return changed;
    }
}
