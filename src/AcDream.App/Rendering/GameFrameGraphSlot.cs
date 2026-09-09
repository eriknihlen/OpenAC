using AcDream.App.Update;

namespace AcDream.App.Rendering;

internal interface IGameUpdateFrameRoot
{
    void Tick(UpdateFrameInput input);
}

internal interface IGameRenderFrameRoot
{
    RenderFrameOutcome Render(RenderFrameInput input);
}

internal sealed class GameFrameGraphSlot
{
    private sealed record FrameGraphPair(
        IGameUpdateFrameRoot Update,
        IGameRenderFrameRoot Render);

    private FrameGraphPair? _pair;

    public bool IsPublished => Volatile.Read(ref _pair) is not null;

    public void Publish(IGameUpdateFrameRoot update, IGameRenderFrameRoot render)
    {
        _ = PublishCore(update, render);
    }

    public IDisposable PublishOwned(
        IGameUpdateFrameRoot update,
        IGameRenderFrameRoot render)
    {
        FrameGraphPair pair = PublishCore(update, render);
        return new Publication(this, pair);
    }

    private FrameGraphPair PublishCore(
        IGameUpdateFrameRoot update,
        IGameRenderFrameRoot render)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(render);
        var pair = new FrameGraphPair(update, render);
        if (Interlocked.CompareExchange(ref _pair, pair, null) is not null)
            throw new InvalidOperationException("A game frame graph is already published.");
        return pair;
    }

    public bool Tick(UpdateFrameInput input)
    {
        FrameGraphPair? pair = Volatile.Read(ref _pair);
        if (pair is null)
            return false;

        pair.Update.Tick(input);
        return true;
    }

    public bool Render(RenderFrameInput input, out RenderFrameOutcome outcome)
    {
        FrameGraphPair? pair = Volatile.Read(ref _pair);
        if (pair is null)
        {
            outcome = default;
            return false;
        }

        outcome = pair.Render.Render(input);
        return true;
    }

    public void Withdraw()
    {
        Interlocked.Exchange(ref _pair, null);
    }

    private void Withdraw(FrameGraphPair expected) =>
        Interlocked.CompareExchange(ref _pair, null, expected);

    private sealed class Publication : IDisposable
    {
        private GameFrameGraphSlot? _owner;
        private readonly FrameGraphPair _expected;

        public Publication(GameFrameGraphSlot owner, FrameGraphPair expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Withdraw(_expected);
    }
}
