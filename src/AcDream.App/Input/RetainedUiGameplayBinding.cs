using AcDream.App.Rendering;
using AcDream.App.UI;

namespace AcDream.App.Input;

internal interface IRetainedUiDragReleaseSurface
{
    void AddReleasedOutside(Action<object, int, int> callback);

    void RemoveReleasedOutside(Action<object, int, int> callback);
}

internal sealed class UiRootDragReleaseSurface(UiRoot root)
    : IRetainedUiDragReleaseSurface
{
    private readonly UiRoot _root = root
        ?? throw new ArgumentNullException(nameof(root));

    public void AddReleasedOutside(Action<object, int, int> callback) =>
        _root.DragReleasedOutsideUi += callback;

    public void RemoveReleasedOutside(Action<object, int, int> callback) =>
        _root.DragReleasedOutsideUi -= callback;
}

internal sealed class RetainedUiGameplayBinding : IDisposable
{
    private readonly IRetainedUiDragReleaseSurface _surface;
    private readonly Action<ItemDragPayload, int, int> _placeDraggedItem;
    private readonly HostQuiescenceGate _quiescence;
    private readonly Action<object, int, int> _releasedOutside;
    private ResourceShutdownTransaction? _detach;
    private bool _attached;
    private bool _attachStarted;
    private int _disposeRequested;
    private int _active;

    public RetainedUiGameplayBinding(
        IRetainedUiDragReleaseSurface surface,
        Action<ItemDragPayload, int, int> placeDraggedItem,
        HostQuiescenceGate quiescence)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _placeDraggedItem = placeDraggedItem
            ?? throw new ArgumentNullException(nameof(placeDraggedItem));
        _quiescence = quiescence
            ?? throw new ArgumentNullException(nameof(quiescence));
        _releasedOutside = OnReleasedOutside;
    }

    public static RetainedUiGameplayBinding Create(
        UiRoot root,
        Action<ItemDragPayload, int, int> placeDraggedItem,
        HostQuiescenceGate quiescence) =>
        new(new UiRootDragReleaseSurface(root), placeDraggedItem, quiescence);

    public bool IsDisposalComplete => !_attached;

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);
        if (_attachStarted)
        {
            throw new InvalidOperationException(
                "Retained gameplay attachment has already started.");
        }

        _attachStarted = true;
        try
        {
            _attached = true;
            _surface.AddReleasedOutside(_releasedOutside);
            Volatile.Write(ref _active, 1);
        }
        catch (Exception attachError)
        {
            Deactivate();
            try
            {
                EnsureDetachTransaction().CompleteOrThrow();
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Retained gameplay registration and rollback both failed.",
                    new InvalidOperationException(
                        "Retained gameplay registration failed.", attachError),
                    rollbackError);
            }

            throw new InvalidOperationException(
                "Retained gameplay registration failed and was rolled back.",
                attachError);
        }
    }

    public void Deactivate() => Interlocked.Exchange(ref _active, 0);

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        Deactivate();
        EnsureDetachTransaction().CompleteOrThrow();
    }

    private void OnReleasedOutside(object payload, int x, int y) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0
                && payload is ItemDragPayload item)
            {
                _placeDraggedItem(item, x, y);
            }
        });

    private ResourceShutdownTransaction EnsureDetachTransaction() =>
        _detach ??= new ResourceShutdownTransaction(
            new ResourceShutdownStage("retained gameplay callbacks",
            [
                new("drag released outside UI", Remove),
            ]));

    private void Remove()
    {
        if (!_attached)
            return;

        _surface.RemoveReleasedOutside(_releasedOutside);
        _attached = false;
    }
}
