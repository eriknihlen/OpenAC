namespace AcDream.App.World;

internal interface ILiveEntityRuntimeSource
{
    LiveEntityRuntime? Current { get; }
}

internal sealed class LiveEntityRuntimeSlot : ILiveEntityRuntimeSource
{
    public LiveEntityRuntime? Current { get; private set; }

    public void Bind(LiveEntityRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (Current is not null)
            throw new InvalidOperationException("The live entity runtime is already bound.");
        Current = runtime;
    }

    public IDisposable BindOwned(LiveEntityRuntime runtime)
    {
        Bind(runtime);
        return new Binding(this, runtime);
    }

    private void Unbind(LiveEntityRuntime expected)
    {
        if (ReferenceEquals(Current, expected))
            Current = null;
    }

    private sealed class Binding : IDisposable
    {
        private LiveEntityRuntimeSlot? _owner;
        private readonly LiveEntityRuntime _expected;

        public Binding(LiveEntityRuntimeSlot owner, LiveEntityRuntime expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}
