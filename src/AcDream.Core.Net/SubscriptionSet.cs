namespace AcDream.Core.Net;

/// <summary>
/// Owns a transactionally-built set of subscriptions and releases them in
/// reverse registration order. Disposal is idempotent and attempts every
/// release even when one fails.
/// </summary>
internal sealed class SubscriptionSet : IDisposable
{
    private readonly object _gate = new();
    private readonly List<RetryableSubscription> _subscriptions = [];
    private bool _disposeRequested;

    public void Add(IDisposable subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        AddRetained(new RetryableSubscription(subscription.Dispose));
    }

    public void Add(Action unsubscribe)
    {
        ArgumentNullException.ThrowIfNull(unsubscribe);
        AddRetained(new RetryableSubscription(unsubscribe));
    }

    private void AddRetained(RetryableSubscription retained)
    {
        bool disposeNow;
        lock (_gate)
        {
            disposeNow = _disposeRequested;
            _subscriptions.Add(retained);
        }

        if (!disposeNow)
            return;
        retained.Dispose();
        throw new ObjectDisposedException(nameof(SubscriptionSet));
    }

    public void Dispose()
    {
        RetryableSubscription[] subscriptions;
        lock (_gate)
        {
            _disposeRequested = true;
            subscriptions = _subscriptions.ToArray();
        }

        List<Exception>? errors = null;
        for (int i = subscriptions.Length - 1; i >= 0; i--)
        {
            try
            {
                subscriptions[i].Dispose();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        if (errors is not null)
            throw new AggregateException("one or more subscriptions failed to detach", errors);
    }

    private sealed class RetryableSubscription(Action dispose) : IDisposable
    {
        private readonly object _gate = new();
        private Action? _dispose = dispose;
        private bool _executing;
        private int _executingThreadId;

        public void Dispose()
        {
            Action? operation;
            int threadId = Environment.CurrentManagedThreadId;
            lock (_gate)
            {
                while (_executing)
                {
                    if (_executingThreadId == threadId)
                    {
                        throw new InvalidOperationException(
                            "Subscription cleanup cannot complete reentrantly.");
                    }
                    Monitor.Wait(_gate);
                }

                operation = _dispose;
                if (operation is null)
                    return;
                _executing = true;
                _executingThreadId = threadId;
            }

            try
            {
                operation();
            }
            catch
            {
                CompleteAttempt(succeeded: false);
                throw;
            }

            CompleteAttempt(succeeded: true);
        }

        private void CompleteAttempt(bool succeeded)
        {
            lock (_gate)
            {
                if (succeeded)
                    _dispose = null;
                _executing = false;
                _executingThreadId = 0;
                Monitor.PulseAll(_gate);
            }
        }
    }
}
