namespace AcDream.App.World;

internal sealed class RetailInboundEventDispatcher
{
    private interface IPendingOperation
    {
        void Invoke();
    }

    private sealed class PendingAction(Action operation) : IPendingOperation
    {
        public void Invoke() => operation();
    }

    private sealed class PendingStateOperation<TReceiver, TState>(
        TReceiver receiver,
        TState state,
        Action<TReceiver, TState> operation) : IPendingOperation
    {
        public void Invoke() => operation(receiver, state);
    }

    private readonly Queue<IPendingOperation> _pending = new();
    private bool _isDraining;

    internal int PendingCount => _pending.Count;
    internal bool IsDraining => _isDraining;

    internal void Run(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_isDraining)
        {
            _pending.Enqueue(new PendingAction(operation));
            return;
        }

        _isDraining = true;
        try
        {
            operation();
            DrainPending();
        }
        catch
        {
            _pending.Clear();
            throw;
        }
        finally
        {
            _isDraining = false;
        }
    }

    internal void Run<TReceiver, TState>(
        TReceiver receiver,
        TState state,
        Action<TReceiver, TState> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_isDraining)
        {
            _pending.Enqueue(
                new PendingStateOperation<TReceiver, TState>(
                    receiver,
                    state,
                    operation));
            return;
        }

        _isDraining = true;
        try
        {
            operation(receiver, state);
            DrainPending();
        }
        catch
        {
            _pending.Clear();
            throw;
        }
        finally
        {
            _isDraining = false;
        }
    }

    private void DrainPending()
    {
        while (_pending.TryDequeue(out IPendingOperation? next))
            next.Invoke();
    }

    internal void Clear()
    {
        if (_isDraining)
        {
            throw new InvalidOperationException(
                "Inbound event work cannot be cleared while its retail FIFO is active.");
        }
        _pending.Clear();
    }
}
