using AcDream.Core.Items;

namespace AcDream.App.World;

public sealed class ExternalContainerLifecycleController : IDisposable
{
    private readonly ExternalContainerState _state;
    private readonly ClientObjectTable _objects;
    private readonly Action<uint> _sendNoLongerViewingContents;
    private bool _disposed;

    public ExternalContainerLifecycleController(
        ExternalContainerState state,
        ClientObjectTable objects,
        Action<uint> sendNoLongerViewingContents)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _sendNoLongerViewingContents = sendNoLongerViewingContents
            ?? throw new ArgumentNullException(nameof(sendNoLongerViewingContents));
        _state.Changed += OnChanged;
    }

    private void OnChanged(ExternalContainerTransition transition)
    {
        if (transition.PreviousContainerId != 0u)
            _objects.StopViewingContentsTree(transition.PreviousContainerId);

        if (transition.Kind == ExternalContainerTransitionKind.ReplacementRequested
            && transition.PreviousContainerId != 0u)
        {
            _sendNoLongerViewingContents(transition.PreviousContainerId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Changed -= OnChanged;
    }
}
