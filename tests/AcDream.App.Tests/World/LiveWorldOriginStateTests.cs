using System.Numerics;
using AcDream.App.World;

namespace AcDream.App.Tests.World;

public sealed class LiveWorldOriginStateTests
{
    [Fact]
    public void Placeholder_IsNotAuthoritativeUntilFirstInitialization()
    {
        var state = new LiveWorldOriginState();

        state.SetPlaceholder(42, 43);

        Assert.Equal(42, state.CenterX);
        Assert.Equal(43, state.CenterY);
        Assert.False(state.IsKnown);
        Assert.True(state.TryInitialize(10, 11));
        Assert.Equal(10, state.CenterX);
        Assert.Equal(11, state.CenterY);
        Assert.True(state.IsKnown);
        Assert.False(state.TryInitialize(20, 21));
        Assert.Equal(10, state.CenterX);
        Assert.Equal(11, state.CenterY);
    }

    [Fact]
    public void Recenter_ReplacesKnownOriginAndResetRetainsItAsPlaceholder()
    {
        var state = new LiveWorldOriginState();
        Assert.True(state.TryInitialize(1, 2));

        state.Recenter(3, 4);
        state.Reset();

        Assert.Equal(3, state.CenterX);
        Assert.Equal(4, state.CenterY);
        Assert.False(state.IsKnown);
        Assert.True(state.TryInitialize(5, 6));
    }

    [Fact]
    public void RecenterBeforeInitialization_PreservesUnknownAndResetIsIdempotent()
    {
        var state = new LiveWorldOriginState();
        state.SetPlaceholder(20, 21);

        state.Recenter(30, 31);
        state.Reset();
        state.Reset();

        Assert.Equal(30, state.CenterX);
        Assert.Equal(31, state.CenterY);
        Assert.False(state.IsKnown);

        Assert.True(state.TryInitialize(40, 41));
        state.Recenter(50, 51);

        Assert.True(state.IsKnown);
        Assert.Equal(50, state.CenterX);
        Assert.Equal(51, state.CenterY);
    }

    [Fact]
    public void CellLocalForSeed_UsesTheCurrentLandblockOrigin()
    {
        var state = new LiveWorldOriginState();
        state.SetPlaceholder(0x30, 0x32);

        Vector3 local = state.CellLocalForSeed(
            new Vector3(200f, -300f, 5f),
            0x3130_0001u);

        Assert.Equal(new Vector3(8f, 84f, 5f), local);
    }


    [Fact]
    public void AgreeingWorldFrameOwners_Pass()
    {
        var state = new LiveWorldOriginState();
        Assert.True(state.TryInitialize(0xA9, 0xB6));

        state.EnsureAgreesWithRuntimeFrame(0xA9B6FFFFu, 0xA9B60001u);
    }

    [Fact]
    public void BeforeEitherWorldFrameOwnerIsEstablished_ThereIsNothingToAgreeOn()
    {
        var state = new LiveWorldOriginState();

        state.EnsureAgreesWithRuntimeFrame(0xA9B6FFFFu, 0xA9B60001u);

        // An accepted origin with no Runtime frame yet is equally not a
        // disagreement.
        Assert.True(state.TryInitialize(0xA9, 0xB6));
        state.EnsureAgreesWithRuntimeFrame(0u, 0xA9B60001u);
    }

    [Theory]
    [InlineData(0xAAB6FFFFu, "192m")]
    [InlineData(0xA8B6FFFFu, "-192m")]
    [InlineData(0xA9B7FFFFu, "192m")]
    [InlineData(0xA9B5FFFFu, "-192m")]
    public void DisagreeingWorldFrameOwners_FailLoudlyWithTheOffsetInMetres(
        uint runtimeCenter,
        string expectedOffset)
    {
        var state = new LiveWorldOriginState();
        Assert.True(state.TryInitialize(0xA9, 0xB6));

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() =>
                state.EnsureAgreesWithRuntimeFrame(
                    runtimeCenter,
                    0xA9B60001u));

        Assert.Contains("World-frame owners disagree", error.Message);
        Assert.Contains(expectedOffset, error.Message);
        Assert.Contains("0xA9B60001", error.Message);
    }

    [Fact]
    public void ARuntimeRebaseAheadOfTheStreamedOrigin_IsCaught()
    {
        var state = new LiveWorldOriginState();
        Assert.True(state.TryInitialize(0x09, 0x04));

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() =>
                state.EnsureAgreesWithRuntimeFrame(
                    0xF682FFFFu,
                    0xF6820033u));

        Assert.Contains("45504m", error.Message);
    }

    [Fact]
    public void AfterBothRebaseToTheSameDestination_TheyAgreeAgain()
    {
        var state = new LiveWorldOriginState();
        Assert.True(state.TryInitialize(0x09, 0x04));

        state.Recenter(0xF6, 0x82);

        state.EnsureAgreesWithRuntimeFrame(0xF682FFFFu, 0xF6820033u);
    }


    [Fact]
    public void DisagreeingWorldFrameOwners_WhileTransitInFlight_DefersInsteadOfThrowing()
    {
        var state = new LiveWorldOriginState();
        Assert.True(state.TryInitialize(0x09, 0x04));

        bool agree = state.TryEnsureAgreesWithRuntimeFrame(
            0xF682FFFFu,
            0xF6820033u,
            transitInFlight: true);

        Assert.False(agree);
        // The disagreement did not mutate the streamed origin — only
        // Recenter (driven by StreamingOriginRecenterCoordinator) may do
        // that.
        Assert.Equal(0x09, state.CenterX);
        Assert.Equal(0x04, state.CenterY);
    }

    [Fact]
    public void DisagreeingWorldFrameOwners_WhileTransitInFlight_ThenAgreeing_RetrySucceeds()
    {
        var state = new LiveWorldOriginState();
        Assert.True(state.TryInitialize(0x09, 0x04));

        bool firstAttempt = state.TryEnsureAgreesWithRuntimeFrame(
            0xF682FFFFu,
            0xF6820033u,
            transitInFlight: true);
        Assert.False(firstAttempt);

        // Streaming recentres: old window fully retires, the streamed
        // origin adopts the destination Runtime already rebased to.
        state.Recenter(0xF6, 0x82);

        bool retry = state.TryEnsureAgreesWithRuntimeFrame(
            0xF682FFFFu,
            0xF6820033u,
            transitInFlight: false);
        Assert.True(retry);
    }

    [Fact]
    public void DisagreeingWorldFrameOwners_NotInTransit_StillThrowsWithTheOffsetInMetres()
    {
        var state = new LiveWorldOriginState();
        Assert.True(state.TryInitialize(0x09, 0x04));

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() =>
                state.TryEnsureAgreesWithRuntimeFrame(
                    0xF682FFFFu,
                    0xF6820033u,
                    transitInFlight: false));

        Assert.Contains("World-frame owners disagree", error.Message);
        Assert.Contains("45504m", error.Message);
        Assert.Contains("0xF6820033", error.Message);
    }

    [Fact]
    public void AgreeingWorldFrameOwners_TryVariantPassesRegardlessOfTransitFlag()
    {
        var state = new LiveWorldOriginState();
        Assert.True(state.TryInitialize(0xA9, 0xB6));

        Assert.True(
            state.TryEnsureAgreesWithRuntimeFrame(
                0xA9B6FFFFu,
                0xA9B60001u,
                transitInFlight: true));
        Assert.True(
            state.TryEnsureAgreesWithRuntimeFrame(
                0xA9B6FFFFu,
                0xA9B60001u,
                transitInFlight: false));
    }
}
