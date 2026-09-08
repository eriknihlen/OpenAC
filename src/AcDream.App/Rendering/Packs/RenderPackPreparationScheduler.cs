namespace AcDream.App.Rendering.Packs;

internal interface IRenderPackPreparationScheduler
{
    Task Schedule(Action preparation);
}

internal sealed class ThreadPoolRenderPackPreparationScheduler :
    IRenderPackPreparationScheduler
{
    internal static ThreadPoolRenderPackPreparationScheduler Instance { get; } = new();

    private ThreadPoolRenderPackPreparationScheduler()
    {
    }

    public Task Schedule(Action preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return Task.Run(preparation);
    }
}

internal sealed class InlineRenderPackPreparationScheduler :
    IRenderPackPreparationScheduler
{
    internal static InlineRenderPackPreparationScheduler Instance { get; } = new();

    private InlineRenderPackPreparationScheduler()
    {
    }

    public Task Schedule(Action preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        preparation();
        return Task.CompletedTask;
    }
}
