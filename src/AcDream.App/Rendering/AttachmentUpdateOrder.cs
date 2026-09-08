namespace AcDream.App.Rendering;

internal sealed class AttachmentUpdateOrder<TKey, TChild>
    where TKey : struct
{
    private readonly HashSet<TKey> _visiting = new();
    private readonly HashSet<TKey> _completed = new();
    private readonly HashSet<TKey> _failedSet = new();
    private readonly List<TKey> _failed = new();
    private readonly List<TKey> _subtree = new();
    private readonly Queue<TKey> _recoveryQueue = new();
    private readonly HashSet<TKey> _recoveryVisited = new();

    public IReadOnlyList<TKey> ForEachParentFirst(
        Dictionary<TKey, TChild> children,
        Func<TChild, TKey?> parentOf,
        Func<TKey, bool> update)
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(parentOf);
        ArgumentNullException.ThrowIfNull(update);

        _visiting.Clear();
        _completed.Clear();
        _failedSet.Clear();
        _failed.Clear();
        foreach (TKey childId in children.Keys)
            Visit(childId, children, parentOf, update);
        return _failed;
    }

    public IReadOnlyList<TKey> CollectSubtreePostOrder(
        Dictionary<TKey, TChild> children,
        TKey rootId,
        Func<TChild, TKey?> parentOf)
    {
        _subtree.Clear();
        _visiting.Clear();
        Collect(rootId, children, parentOf);
        return _subtree;
    }

    public void RealizeDescendants(
        TKey parentId,
        Func<TKey, IReadOnlyList<TKey>> childrenWaitingForParent,
        Func<TKey, bool> realize)
    {
        ArgumentNullException.ThrowIfNull(childrenWaitingForParent);
        ArgumentNullException.ThrowIfNull(realize);

        _recoveryQueue.Clear();
        _recoveryVisited.Clear();
        _recoveryQueue.Enqueue(parentId);
        _recoveryVisited.Add(parentId);
        while (_recoveryQueue.Count > 0)
        {
            TKey currentParent = _recoveryQueue.Dequeue();
            IReadOnlyList<TKey> waiting = childrenWaitingForParent(currentParent);
            for (int i = 0; i < waiting.Count; i++)
            {
                TKey childId = waiting[i];
                if (realize(childId) && _recoveryVisited.Add(childId))
                    _recoveryQueue.Enqueue(childId);
            }
        }
    }

    private void Collect(
        TKey rootId,
        Dictionary<TKey, TChild> children,
        Func<TChild, TKey?> parentOf)
    {
        if (!_visiting.Add(rootId))
            return;
        foreach ((TKey candidateId, TChild child) in children)
        {
            if (parentOf(child) is { } parent
                && EqualityComparer<TKey>.Default.Equals(parent, rootId))
                Collect(candidateId, children, parentOf);
        }
        _subtree.Add(rootId);
    }

    private void Visit(
        TKey childId,
        Dictionary<TKey, TChild> children,
        Func<TChild, TKey?> parentOf,
        Func<TKey, bool> update)
    {
        if (_completed.Contains(childId)
            || !children.TryGetValue(childId, out TChild? child))
        {
            return;
        }
        if (!_visiting.Add(childId))
            return;

        TKey? parentId = parentOf(child);
        if (parentId is { } attachedParentId && children.ContainsKey(attachedParentId))
            Visit(attachedParentId, children, parentOf, update);

        _visiting.Remove(childId);
        if (!_completed.Add(childId))
            return;

        bool succeeded = parentId is not { } parent
            || !_failedSet.Contains(parent);
        if (succeeded)
            succeeded = update(childId);
        if (!succeeded && _failedSet.Add(childId))
            _failed.Add(childId);
    }
}
