using System.Collections.Generic;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class UiMediaSequenceTests
{
    private static UiMediaStep Image(uint file)
        => new(UiMediaStepKind.Image, file, 1, 0f, 0f, 0u, 0f);

    private static UiMediaStep Pause(float seconds)
        => new(UiMediaStepKind.Pause, 0u, 0, seconds, seconds, 0u, 0f);

    private static UiMediaStep State(uint state, float probability = 1f)
        => new(UiMediaStepKind.State, 0u, 0, 0f, 0f, state, probability);

    private static UiMediaStep Jump(uint index, float probability = 1f)
        => new(UiMediaStepKind.Jump, 0u, 0, 0f, 0f, index, probability);

    private static IReadOnlyList<UiMediaStep> BlinkSequence() =>
    [
        Image(0x06005F0Eu), Pause(0.5f),
        Image(0x06005F0Fu), Pause(0.5f),
        Image(0x06005F0Eu), Pause(0.5f),
        Image(0x06005F0Fu), Pause(0.5f),
        Image(0x06005F0Eu), Pause(0.5f),
        Image(0x06005F0Fu), Pause(0.5f),
        State(13u),
    ];

    [Theory]
    [InlineData(0.0f, 0x06005F0Eu)]
    [InlineData(0.4f, 0x06005F0Eu)]
    [InlineData(0.5f, 0x06005F0Fu)]   // first flip, exactly on the boundary
    [InlineData(0.9f, 0x06005F0Fu)]
    [InlineData(1.0f, 0x06005F0Eu)]
    [InlineData(2.75f, 0x06005F0Fu)]
    public void TheFrameAlternatesEveryHalfSecond(float elapsed, uint expected)
    {
        (uint file, uint? transition) = UiMediaSequence.Sample(BlinkSequence(), elapsed);

        Assert.Equal(expected, file);
        Assert.Null(transition);
    }

    [Fact]
    public void AfterThreeSecondsItHandsOffToGhosted()
    {
        (uint _, uint? transition) = UiMediaSequence.Sample(BlinkSequence(), 3.0f);

        Assert.Equal(13u, transition);
    }

    [Fact]
    public void TheHandOffDoesNotFireEarly()
    {
        Assert.Null(UiMediaSequence.Sample(BlinkSequence(), 2.99f).TransitionState);
    }

    [Fact]
    public void ASingleImageIsNotAnAnimation()
    {
        // A still frame must keep the ordinary draw path rather than being
        // routed through a player that would only ever return the same file.
        Assert.False(UiMediaSequence.IsAnimated([Image(1u)]));
        Assert.False(UiMediaSequence.IsAnimated([]));
        Assert.False(UiMediaSequence.IsAnimated(null));

        Assert.True(UiMediaSequence.IsAnimated([Image(1u), Pause(1f)]));
        Assert.True(UiMediaSequence.IsAnimated([Image(1u), Image(2u)]));
    }

    [Fact]
    public void AJumpLoopsAndKeepsRunningForever()
    {
        IReadOnlyList<UiMediaStep> looping =
        [
            Image(0xAAu), Pause(1f),
            Image(0xBBu), Pause(1f),
            Jump(0u),
        ];

        Assert.Equal(0xAAu, UiMediaSequence.Sample(looping, 0.5f).File);
        Assert.Equal(0xBBu, UiMediaSequence.Sample(looping, 1.5f).File);
        Assert.Equal(0xAAu, UiMediaSequence.Sample(looping, 2.5f).File);   // looped
        Assert.Equal(0xBBu, UiMediaSequence.Sample(looping, 101.5f).File); // still going
    }

    [Fact]
    public void AZeroTimeJumpCycleTerminatesInsteadOfHanging()
    {
        IReadOnlyList<UiMediaStep> pathological = [Image(0xAAu), Jump(0u)];

        Assert.Equal(0xAAu, UiMediaSequence.Sample(pathological, 1f).File);
    }

    [Fact]
    public void AnUncertainBranchFallsThroughRatherThanLooping()
    {
        IReadOnlyList<UiMediaStep> maybe =
        [
            Image(0xAAu), Pause(1f), Jump(0u, probability: 0.5f), Image(0xBBu),
        ];

        Assert.Equal(0xBBu, UiMediaSequence.Sample(maybe, 1.5f).File);
    }

    [Fact]
    public void UnknownStepsAreSteppedOverWithoutBreakingJumpIndices()
    {
        // Sound, movie and message entries are kept as Other precisely so a
        // jump's index still lands on the authored entry.
        IReadOnlyList<UiMediaStep> withOther =
        [
            new(UiMediaStepKind.Other, 0u, 0, 0f, 0f, 0u, 0f),
            Image(0xCCu),
            Pause(1f),
            Jump(1u),
        ];

        Assert.Equal(0xCCu, UiMediaSequence.Sample(withOther, 0.5f).File);
        Assert.Equal(0xCCu, UiMediaSequence.Sample(withOther, 5f).File);
    }
}
