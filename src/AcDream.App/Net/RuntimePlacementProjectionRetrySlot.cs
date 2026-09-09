using AcDream.Runtime;

namespace AcDream.App.Net;

internal interface IRuntimePlacementProjectionRetryPhase
{
    void RetryPending();
}

internal sealed class RuntimePlacementProjectionRetrySlot
    : IRuntimePlacementProjectionRetryPhase
{
    private sealed record Binding(
        long Id,
        RuntimeGenerationToken Generation,
        Func<bool> Retry);

    private readonly Func<RuntimeGenerationToken> _currentGeneration;
    private Binding? _current;
    private long _nextBindingId;

    internal RuntimePlacementProjectionRetrySlot(
        Func<RuntimeGenerationToken> currentGeneration)
    {
        _currentGeneration = currentGeneration
            ?? throw new ArgumentNullException(nameof(currentGeneration));
    }

    internal int BindingCount => _current is null ? 0 : 1;

    internal IDisposable BindOwned(
        RuntimeGenerationToken generation,
        Func<bool> retry)
    {
        if (generation.Value == 0UL)
        {
            throw new ArgumentException(
                "A placement retry route requires a live Runtime generation.",
                nameof(generation));
        }
        ArgumentNullException.ThrowIfNull(retry);
        if (_current is not null)
        {
            throw new InvalidOperationException(
                "A graphical placement retry route is already bound.");
        }

        var binding = new Binding(
            checked(++_nextBindingId),
            generation,
            retry);
        _current = binding;
        return new DelegateDisposable(() => Unbind(binding));
    }

    public void RetryPending()
    {
        Binding? binding = _current;
        if (binding is null
            || binding.Generation != _currentGeneration())
        {
            return;
        }

        _ = binding.Retry();
    }

    private void Unbind(Binding binding)
    {
        if (ReferenceEquals(_current, binding))
            _current = null;
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose
            ?? throw new ArgumentNullException(nameof(dispose));

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
