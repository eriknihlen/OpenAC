using AcDream.App.Interaction;
using AcDream.Core.Physics;

namespace AcDream.App.Tests.Interaction;

public sealed class PlayerApproachCompletionStateTests
{
    [Fact]
    public void CompletionMailbox_PreservesPublicationOrder()
    {
        var state = new PlayerApproachCompletionState();
        IPlayerApproachCompletionSink lifetime = state.BeginControllerLifetime();

        Assert.True(state.TryBeginApproach(out PlayerApproachToken token));
        lifetime.PublishNaturalCompletion();
        lifetime.PublishCancellation(WeenieError.NoObject);

        Assert.True(state.TryTake(out PlayerApproachCompletion natural));
        Assert.Equal(token, natural.Token);
        Assert.True(natural.IsNatural);
        Assert.Equal(WeenieError.None, natural.Error);
        Assert.True(state.TryTake(out PlayerApproachCompletion cancellation));
        Assert.False(cancellation.IsNatural);
        Assert.Equal(WeenieError.NoObject, cancellation.Error);
        Assert.False(state.TryTake(out _));
    }

    [Fact]
    public void Clear_DiscardsEveryPendingCompletion()
    {
        var state = new PlayerApproachCompletionState();
        IPlayerApproachCompletionSink lifetime = state.BeginControllerLifetime();
        Assert.True(state.TryBeginApproach(out _));
        lifetime.PublishNaturalCompletion();
        lifetime.PublishCancellation(WeenieError.ActionCancelled);

        state.Clear();

        Assert.False(state.TryTake(out _));
    }

    [Fact]
    public void RetiredLifetime_CannotPublishIntoReplacementLifetime()
    {
        var state = new PlayerApproachCompletionState();
        IPlayerApproachCompletionSink stale = state.BeginControllerLifetime();
        Assert.True(state.TryBeginApproach(out PlayerApproachToken staleToken));
        state.RetireControllerLifetime(stale);
        Assert.True(state.TryTake(out PlayerApproachCompletion retirement));
        Assert.Equal(staleToken, retirement.Token);
        Assert.False(retirement.IsNatural);
        IPlayerApproachCompletionSink current = state.BeginControllerLifetime();
        Assert.True(state.TryBeginApproach(out PlayerApproachToken currentToken));

        stale.PublishNaturalCompletion();
        current.PublishNaturalCompletion();

        Assert.True(state.TryTake(out PlayerApproachCompletion completion));
        Assert.Equal(currentToken, completion.Token);
        Assert.False(state.TryTake(out _));
    }

    [Fact]
    public void Retirement_ReplacesQueuedSuccessWithMatchingCancellation()
    {
        var state = new PlayerApproachCompletionState();
        IPlayerApproachCompletionSink lifetime = state.BeginControllerLifetime();
        Assert.True(state.TryBeginApproach(out PlayerApproachToken token));
        lifetime.PublishNaturalCompletion();

        state.RetireControllerLifetime(lifetime);

        Assert.True(state.TryTake(out PlayerApproachCompletion completion));
        Assert.Equal(token, completion.Token);
        Assert.False(completion.IsNatural);
        Assert.Equal(WeenieError.ActionCancelled, completion.Error);
        Assert.False(state.TryTake(out _));
    }
}
