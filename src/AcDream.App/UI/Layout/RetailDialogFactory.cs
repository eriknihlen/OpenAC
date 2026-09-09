namespace AcDream.App.UI.Layout;

public sealed class RetailDialogFactory : IDisposable
{
    public const uint DefaultQueueKey = 2u;
    public const uint NonQueuedKey = 1u;

    private sealed class DialogInfo
    {
        public required RetailDialogData Data { get; init; }
        public required uint Context { get; init; }
        public required uint QueueKey { get; init; }
        public required ulong Sequence { get; init; }
        public Action<RetailDialogData>? Callback { get; init; }
        public IRetailDialogView? View { get; set; }
    }

    private readonly UiRoot _host;
    private readonly Func<RetailDialogType, ImportedLayout?> _createLayout;
    private readonly Dictionary<uint, DialogInfo> _activeQueued = new();
    private readonly Dictionary<uint, DialogInfo> _activeNonQueued = new();
    private readonly Dictionary<uint, LinkedList<DialogInfo>> _pending = new();
    private readonly LinkedList<DialogInfo> _retryable = new();
    private readonly List<DialogInfo> _openOrder = new();
    private uint _globalContext;
    private ulong _globalSequence;
    private bool _resetting;
    private bool _disposed;

    public RetailDialogFactory(
        UiRoot host,
        Func<RetailDialogType, ImportedLayout?> createLayout)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _createLayout = createLayout ?? throw new ArgumentNullException(nameof(createLayout));
    }

    public event Action<uint, RetailDialogData>? DialogClosed;

    public event Action<uint>? DialogOpened;

    public bool IsOpen => _activeQueued.Count != 0 || _activeNonQueued.Count != 0;

    public int ActiveCount => _activeQueued.Count + _activeNonQueued.Count;

    public int PendingCount => _pending.Values.Sum(static queue => queue.Count);

    internal int RetryCount => _retryable.Count;

    public static uint RootElementId(RetailDialogType type)
        => type switch
        {
            RetailDialogType.Confirmation => 0x15u,
            RetailDialogType.Wait => 0x31u,
            RetailDialogType.Message => 0x24u,
            RetailDialogType.TextInput => 0x28u,
            RetailDialogType.ConfirmationTextInput => 0x2Cu,
            RetailDialogType.Menu => 0x1Bu,
            RetailDialogType.ConfirmationMenu => 0x1Fu,
            _ => 0u,
        };

    public uint MakeDialog(RetailDialogData data)
        => MakeDialog(data, callback: null);

    public uint MakeDialog(RetailDialogData data, Action<RetailDialogData>? callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);

        uint context = NextContext();
        RetailDialogData ownedData = data.Clone();
        uint queueKey = ownedData.GetUInt32(RetailDialogProperty.QueueKey, DefaultQueueKey);
        if (queueKey == 0u)
            queueKey = DefaultQueueKey;

        var info = new DialogInfo
        {
            Data = ownedData,
            Context = context,
            QueueKey = queueKey,
            Sequence = NextSequence(),
            Callback = callback,
        };

        if (queueKey == NonQueuedKey)
        {
            _activeNonQueued.Add(context, info);
            if (!TryCreateDialog(info))
            {
                _activeNonQueued.Remove(context);
                QueueRetry(info);
            }
            return context;
        }

        if (!_activeQueued.TryGetValue(queueKey, out DialogInfo? current))
        {
            if (HasRetry(queueKey) && !IsPriority(info))
            {
                PendingQueue(queueKey).AddLast(info);
                return context;
            }

            _activeQueued.Add(queueKey, info);
            if (!TryCreateDialog(info))
            {
                _activeQueued.Remove(queueKey);
                QueueRetry(info);
            }
            return context;
        }

        LinkedList<DialogInfo> queue = PendingQueue(queueKey);
        if (!IsPriority(info))
        {
            queue.AddLast(info);
            UpdatePendingDialogDisplays();
            return context;
        }

        Suspend(current);
        queue.AddFirst(current);
        _activeQueued[queueKey] = info;
        if (!TryCreateDialog(info))
        {
            _activeQueued.Remove(queueKey);
            queue.Remove(current);
            if (queue.Count == 0)
                _pending.Remove(queueKey);
            OpenSpecificDialog(current);
            QueueRetry(info);
        }
        return context;
    }

    public uint MakeConfirmation(
        string message,
        Action<RetailDialogData>? callback = null,
        uint queueKey = DefaultQueueKey,
        bool priority = false)
    {
        RetailDialogData data = RetailDialogData.Confirmation(message)
            .Set(RetailDialogProperty.QueueKey, queueKey);
        if (priority)
            data.Set(RetailDialogProperty.Priority, true);
        return MakeDialog(data, callback);
    }

    public uint MakeWait(
        string message,
        uint queueKey = DefaultQueueKey,
        bool priority = false)
    {
        RetailDialogData data = RetailDialogData.Wait(message)
            .Set(RetailDialogProperty.QueueKey, queueKey);
        if (priority)
            data.Set(RetailDialogProperty.Priority, true);
        return MakeDialog(data, callback: null);
    }

    public uint MakeMessage(
        string message,
        Action<RetailDialogData>? callback = null,
        uint queueKey = DefaultQueueKey,
        bool priority = false)
    {
        RetailDialogData data = RetailDialogData.Message(message)
            .Set(RetailDialogProperty.QueueKey, queueKey);
        if (priority)
            data.Set(RetailDialogProperty.Priority, true);
        return MakeDialog(data, callback);
    }

    public uint MakeConfirmationTextInput(
        string message,
        Action<RetailDialogData>? callback = null,
        uint queueKey = DefaultQueueKey)
    {
        RetailDialogData data = RetailDialogData.ConfirmationTextInput(message)
            .Set(RetailDialogProperty.QueueKey, queueKey);
        return MakeDialog(data, callback);
    }

    public uint MakeConfirmationMenu(
        IReadOnlyList<string> items,
        int selectedIndex,
        Action<RetailDialogData>? callback = null,
        uint queueKey = DefaultQueueKey)
    {
        RetailDialogData data = RetailDialogData.ConfirmationMenu(items, selectedIndex)
            .Set(RetailDialogProperty.QueueKey, queueKey);
        return MakeDialog(data, callback);
    }

    public bool CloseDialog(uint context)
    {
        if (context == 0u)
            return false;

        if (_activeNonQueued.Remove(context, out DialogInfo? nonQueued))
        {
            DialogDone(nonQueued);
            return true;
        }

        foreach ((uint queueKey, DialogInfo active) in _activeQueued.ToArray())
        {
            if (active.Context != context)
                continue;

            _activeQueued.Remove(queueKey);
            DialogDone(active);
            OpenNextDialog(queueKey);
            return true;
        }

        foreach ((uint queueKey, LinkedList<DialogInfo> queue) in _pending.ToArray())
        {
            LinkedListNode<DialogInfo>? node = queue.First;
            while (node is not null && node.Value.Context != context)
                node = node.Next;
            if (node is null)
                continue;

            DialogInfo pending = node.Value;
            queue.Remove(node);
            if (queue.Count == 0)
                _pending.Remove(queueKey);
            DialogDone(pending);
            UpdatePendingDialogDisplays();
            return true;
        }

        LinkedListNode<DialogInfo>? retry = _retryable.First;
        while (retry is not null && retry.Value.Context != context)
            retry = retry.Next;
        if (retry is not null)
        {
            DialogInfo failed = retry.Value;
            _retryable.Remove(retry);
            DialogDone(failed);
            if (failed.QueueKey != NonQueuedKey)
                OpenNextDialog(failed.QueueKey);
            return true;
        }

        return false;
    }

    public void Tick()
    {
        RetryFailedDialogs();
        foreach (DialogInfo info in _openOrder.ToArray())
        {
            if (info.View is { } view)
            {
                _host.BringToFront(view.Root);
                view.Tick();
            }
        }
    }

    public void Reset()
    {
        if (_resetting)
            return;

        _resetting = true;
        List<Exception>? failures = null;
        try
        {
            while (true)
            {
                DialogInfo[] infos = _activeNonQueued.Values
                    .Concat(_activeQueued.Values)
                    .Concat(_pending.Values.SelectMany(static queue => queue))
                    .Concat(_retryable)
                    .Distinct()
                    .ToArray();
                if (infos.Length == 0)
                    break;

                _activeNonQueued.Clear();
                _activeQueued.Clear();
                _pending.Clear();
                _retryable.Clear();
                foreach (DialogInfo info in infos)
                {
                    try { DialogDone(info); }
                    catch (Exception error) { (failures ??= []).Add(error); }
                }
            }
        }
        finally
        {
            _resetting = false;
            RefreshModal();
        }

        if (failures is not null)
            throw new AggregateException(
                "One or more dialogs failed while the factory reset.",
                failures);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }

    private uint NextContext()
    {
        _globalContext++;
        if (_globalContext == 0u)
            _globalContext++;
        return _globalContext;
    }

    private ulong NextSequence()
    {
        _globalSequence++;
        if (_globalSequence == 0uL)
            _globalSequence++;
        return _globalSequence;
    }

    private LinkedList<DialogInfo> PendingQueue(uint queueKey)
    {
        if (_pending.TryGetValue(queueKey, out LinkedList<DialogInfo>? queue))
            return queue;
        queue = new LinkedList<DialogInfo>();
        _pending.Add(queueKey, queue);
        return queue;
    }

    private bool TryCreateDialog(DialogInfo info)
    {
        RetailDialogType type = (RetailDialogType)info.Data.GetUInt32(
            RetailDialogProperty.Type);
        try
        {
            if (type is not (RetailDialogType.Confirmation
                or RetailDialogType.Wait
                or RetailDialogType.Message
                or RetailDialogType.ConfirmationTextInput
                or RetailDialogType.ConfirmationMenu))
            {
                throw new NotSupportedException(
                    $"Retail dialog type {(uint)type} does not have a ported presenter yet.");
            }

            ImportedLayout layout = _createLayout(type)
                ?? throw new InvalidOperationException(
                    $"Retail dialog catalog could not create type {(uint)type}.");
            IRetailDialogView view = type switch
            {
                RetailDialogType.Wait => new RetailWaitDialogView(
                    _host, layout, info.Data),
                RetailDialogType.Message => new RetailMessageDialogView(
                    _host, layout, info.Data, info.Context,
                    context => CloseDialog(context)),
                RetailDialogType.ConfirmationTextInput =>
                    new RetailConfirmationTextInputDialogView(
                        _host, layout, info.Data, info.Context,
                        context => CloseDialog(context)),
                RetailDialogType.ConfirmationMenu =>
                    new RetailConfirmationMenuDialogView(
                        _host, layout, info.Data, info.Context,
                        context => CloseDialog(context)),
                _ => new RetailConfirmationDialogView(
                    _host, layout, info.Data, info.Context,
                    context => CloseDialog(context)),
            };
            info.View = view;
            _host.AddChild(view.Root);
            _host.BringToFront(view.Root);
            _openOrder.Add(info);
            _host.Modal = view.Root;
            view.Tick();
            UpdatePendingDialogDisplays();
            DialogOpened?.Invoke(info.Context);
            return true;
        }
        catch (Exception error)
        {
            RemoveView(info);
            Console.WriteLine(
                $"[UI] retail dialog type {(uint)type} context {info.Context} "
                + $"will retry after catalog recovery: {error.Message}");
            return false;
        }
    }

    private void Suspend(DialogInfo info)
    {
        if (info.View is null)
            return;
        IRetailDialogView view = info.View;
        info.View = null;
        view.DetachHandlers();
        _openOrder.Remove(info);
        _host.RemoveChild(view.Root);
        RefreshModal();
    }

    private void DialogDone(DialogInfo info)
    {
        try
        {
            info.Callback?.Invoke(info.Data);
            DialogClosed?.Invoke(info.Context, info.Data);
        }
        finally
        {
            RemoveView(info);
        }
    }

    private void RemoveView(DialogInfo info)
    {
        if (info.View is { } view)
        {
            view.DetachHandlers();
            _host.RemoveChild(view.Root);
            info.View = null;
            _openOrder.Remove(info);
        }
        RefreshModal();
    }

    private void OpenNextDialog(uint queueKey)
    {
        if (_activeQueued.ContainsKey(queueKey))
            return;

        if (TryActivateRetry(queueKey))
            return;

        if (!_pending.TryGetValue(queueKey, out LinkedList<DialogInfo>? queue)
            || queue.First is null)
            return;

        DialogInfo next = queue.First.Value;
        queue.RemoveFirst();
        if (queue.Count == 0)
            _pending.Remove(queueKey);
        _activeQueued.Add(queueKey, next);
        if (!TryCreateDialog(next))
        {
            _activeQueued.Remove(queueKey);
            QueueRetry(next);
        }
    }

    private void OpenSpecificDialog(DialogInfo info)
    {
        _activeQueued.Add(info.QueueKey, info);
        if (!TryCreateDialog(info))
        {
            _activeQueued.Remove(info.QueueKey);
            QueueRetry(info);
        }
    }

    private void RetryFailedDialogs()
    {
        foreach (DialogInfo info in _retryable.ToArray())
        {
            if (info.QueueKey == NonQueuedKey)
            {
                _activeNonQueued.Add(info.Context, info);
                if (TryCreateDialog(info))
                    _retryable.Remove(info);
                else
                    _activeNonQueued.Remove(info.Context);
                continue;
            }

            if (!ReferenceEquals(FirstRetry(info.QueueKey), info))
                continue;

            if (!_activeQueued.TryGetValue(
                    info.QueueKey,
                    out DialogInfo? active))
                TryActivateRetry(info.QueueKey);
            else if (IsPriority(info)
                && (!IsPriority(active) || info.Sequence > active.Sequence))
                TryPreemptWithRetry(info, active);
        }
    }

    private void TryPreemptWithRetry(DialogInfo priority, DialogInfo current)
    {
        LinkedList<DialogInfo> queue = PendingQueue(priority.QueueKey);
        Suspend(current);
        queue.AddFirst(current);
        _activeQueued[priority.QueueKey] = priority;
        _retryable.Remove(priority);
        if (TryCreateDialog(priority))
            return;

        _activeQueued.Remove(priority.QueueKey);
        queue.Remove(current);
        if (queue.Count == 0)
            _pending.Remove(priority.QueueKey);
        OpenSpecificDialog(current);
        QueueRetry(priority);
    }

    private bool TryActivateRetry(uint queueKey)
    {
        DialogInfo? info = FirstRetry(queueKey);
        if (info is null)
            return false;

        _activeQueued.Add(queueKey, info);
        if (TryCreateDialog(info))
            _retryable.Remove(info);
        else
            _activeQueued.Remove(queueKey);
        return true;
    }

    private DialogInfo? FirstRetry(uint queueKey)
    {
        foreach (DialogInfo info in _retryable)
            if (info.QueueKey == queueKey)
                return info;
        return null;
    }

    private bool HasRetry(uint queueKey) => FirstRetry(queueKey) is not null;

    private static bool IsPriority(DialogInfo info) =>
        info.Data.GetBoolean(RetailDialogProperty.Priority);

    private void QueueRetry(DialogInfo info)
    {
        if (_retryable.Contains(info))
            return;
        if (!IsPriority(info))
        {
            _retryable.AddLast(info);
            return;
        }

        LinkedListNode<DialogInfo>? existing = _retryable.First;
        while (existing is not null
            && existing.Value.QueueKey != info.QueueKey)
        {
            existing = existing.Next;
        }
        if (existing is null)
            _retryable.AddLast(info);
        else
            _retryable.AddBefore(existing, info);
    }

    private void UpdatePendingDialogDisplays()
    {
        foreach ((uint queueKey, DialogInfo active) in _activeQueued)
        {
            int count = _pending.TryGetValue(queueKey, out LinkedList<DialogInfo>? queue)
                ? queue.Count
                : 0;
            active.View?.SetPendingCount(count);
        }
    }

    private void RefreshModal()
    {
        _host.Modal = _openOrder.Count == 0 ? null : _openOrder[^1].View?.Root;
    }
}
