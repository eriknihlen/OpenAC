using System;
using System.Threading;

namespace AcDream.Core.Physics;

internal sealed class TransitionScratchArena
{
    internal const int Capacity = 10;

    private readonly Transition?[] _records = new Transition?[Capacity];
    private int _depth;
    private int _ownerThreadId;

    internal int ActiveDepth => _depth;

    internal Transition Rent()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (_depth != 0 && _ownerThreadId != threadId)
        {
            throw new InvalidOperationException(
                "Physics transition scratch cannot be shared across threads.");
        }

        if (_depth >= Capacity)
        {
            throw new InvalidOperationException(
                $"Physics transition nesting exceeds retail's {Capacity}-record scratch arena.");
        }

        if (_depth == 0)
            _ownerThreadId = threadId;

        Transition transition = _records[_depth] ??= new Transition();
        _depth++;
        transition.ResetForReuse();
        return transition;
    }

    internal void Return(Transition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        int threadId = Environment.CurrentManagedThreadId;
        int index = _depth - 1;
        if (index < 0
            || _ownerThreadId != threadId
            || !ReferenceEquals(_records[index], transition))
        {
            throw new InvalidOperationException(
                "Physics transition scratch must be returned in LIFO order on its owning thread.");
        }

        _depth = index;
        if (_depth == 0)
            _ownerThreadId = 0;
    }
}
