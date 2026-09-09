namespace AcDream.App.Composition;

internal interface IGameWindowPlatformPublication<in TGraphics, in TInput>
    where TGraphics : class
    where TInput : class
{
    void PublishGraphics(TGraphics graphics);
    void PublishInput(TInput input);
}

internal sealed record GameWindowPlatformResult<TGraphics, TInput>(
    TGraphics Graphics,
    TInput Input)
    where TGraphics : class
    where TInput : class;

internal enum GameWindowPlatformAcquisitionPoint
{
    GraphicsPublished,
    InputPublished,
}

internal static class GameWindowPlatformAcquisition
{
    public static GameWindowPlatformResult<TGraphics, TInput> Acquire<TGraphics, TInput>(
        Func<TGraphics> graphicsFactory,
        Action<TGraphics> releaseUnpublishedGraphics,
        Func<TInput> inputFactory,
        Action<TInput> releaseUnpublishedInput,
        IGameWindowPlatformPublication<TGraphics, TInput> publication,
        Action<GameWindowPlatformAcquisitionPoint>? faultInjection = null)
        where TGraphics : class
        where TInput : class
    {
        ArgumentNullException.ThrowIfNull(graphicsFactory);
        ArgumentNullException.ThrowIfNull(releaseUnpublishedGraphics);
        ArgumentNullException.ThrowIfNull(inputFactory);
        ArgumentNullException.ThrowIfNull(releaseUnpublishedInput);
        ArgumentNullException.ThrowIfNull(publication);

        var scope = new CompositionAcquisitionScope();
        try
        {
            var graphicsLease = scope.Acquire(
                "graphics API",
                graphicsFactory,
                releaseUnpublishedGraphics);
            TGraphics graphics = graphicsLease.Publish(publication.PublishGraphics);
            faultInjection?.Invoke(GameWindowPlatformAcquisitionPoint.GraphicsPublished);

            var inputLease = scope.Acquire(
                "input context",
                inputFactory,
                releaseUnpublishedInput);
            TInput input = inputLease.Publish(publication.PublishInput);
            faultInjection?.Invoke(GameWindowPlatformAcquisitionPoint.InputPublished);

            scope.Complete();
            return new GameWindowPlatformResult<TGraphics, TInput>(graphics, input);
        }
        catch (Exception failure)
        {
            scope.RollbackAndThrow(failure);
            throw new System.Diagnostics.UnreachableException();
        }
    }
}
