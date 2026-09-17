using System.Collections.Concurrent;

namespace AcDream.App.Plugins;

/// <summary>
/// Marshals a small unit of work from whatever thread a plugin called from
/// onto the window's own thread, blocking the caller until it has run.
///
/// GLFW's clipboard calls (what <see cref="WindowPluginClipboard"/> ends up
/// making through Silk.NET's <c>IKeyboard.ClipboardText</c>) are documented
/// as main-thread-only: calling them off that thread does not throw, it
/// just silently does nothing to the OS clipboard -- exactly how the bug
/// this queue exists to fix presented (TrySetText returned true, the
/// clipboard stayed empty). A plugin's own event handlers can run on
/// whatever thread the plugin chose (an async network continuation, its
/// own background thread), so the write has to be marshaled rather than
/// assumed to already be on the right thread.
/// </summary>
public sealed class MainThreadDispatchQueue
{
    /// <summary>
    /// One queued unit of work. Its own tiny state machine (Pending ->
    /// Completed or Abandoned) exists so a caller that times out can
    /// reliably stop a later Drain() from running the action at all --
    /// without it, a timed-out caller would report failure while the
    /// action ran anyway on the next frame, which is worse than either
    /// outcome alone -- and so the waiter's ManualResetEventSlim is never
    /// disposed while Run() might still call Set() on it.
    /// </summary>
    private sealed class WorkItem(Action action)
    {
        private const int Pending = 0;
        private const int Claimed = 1;

        private readonly Action _action = action;
        private readonly ManualResetEventSlim _done = new(initialState: false);
        private int _state;

        /// <summary>Called only from the owner thread, by Drain().</summary>
        public void Run()
        {
            if (Interlocked.CompareExchange(ref _state, Claimed, Pending) != Pending)
            {
                // WaitOrAbandon already claimed abandonment -- skip the
                // action entirely rather than running it after the caller
                // has already been told it failed.
                return;
            }
            try
            {
                _action();
            }
            finally
            {
                _done.Set();
            }
        }

        /// <summary>
        /// Waits up to <paramref name="timeout"/> for Run() to complete. On
        /// timeout, tries to claim abandonment so a Run() that hasn't
        /// started yet will skip the action; if Run() already claimed
        /// first, waits for the Set() it is about to call instead of
        /// disposing the event out from under it.
        /// </summary>
        public bool WaitOrAbandon(TimeSpan timeout)
        {
            if (_done.Wait(timeout))
            {
                _done.Dispose();
                return true;
            }
            if (Interlocked.CompareExchange(ref _state, Claimed, Pending) == Pending)
            {
                _done.Dispose();
                return false;
            }
            // Run() claimed this item concurrently with our timeout; it
            // will (or already did) call _done.Set(). Wait for that, again
            // bounded by the timeout, so the dispose cannot race Run()'s
            // finally block. If the action is still running past a second
            // timeout, report "not confirmed" and leave the event to the
            // garbage collector rather than block the caller indefinitely
            // or dispose under a Set() that is still to come.
            if (_done.Wait(timeout))
            {
                _done.Dispose();
                return true;
            }
            return false;
        }
    }

    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<WorkItem> _pending = new();

    /// <summary>True when called from the thread that constructed this queue.</summary>
    public bool IsOnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

    /// <summary>
    /// Runs every action queued so far. Must only be called from the owner
    /// thread (the window's update loop calls this once per frame). A
    /// throwing action is isolated -- logged to stderr rather than
    /// escaping into the frame loop -- and draining continues; the waiter
    /// is still released either way because WorkItem.Run()'s own finally
    /// block calls Set() before the exception reaches this catch.
    /// </summary>
    public void Drain()
    {
        if (!IsOnOwnerThread)
        {
            throw new InvalidOperationException(
                "MainThreadDispatchQueue.Drain must only be called from " +
                "the thread that constructed the queue -- that thread " +
                "affinity is the entire point of this queue.");
        }
        while (_pending.TryDequeue(out WorkItem? item))
        {
            try
            {
                item.Run();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"[MainThreadDispatchQueue] a queued action threw: {error}");
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the owner thread and waits for it
    /// to finish, up to <paramref name="timeout"/>. Runs inline, with no
    /// queueing or waiting, when already on the owner thread. Returns false
    /// if the timeout elapsed before a queued action ran -- in which case
    /// the action is guaranteed not to run at all, not merely "not yet".
    /// </summary>
    public bool InvokeAndWait(Action action, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsOnOwnerThread)
        {
            action();
            return true;
        }
        var item = new WorkItem(action);
        _pending.Enqueue(item);
        return item.WaitOrAbandon(timeout);
    }
}
