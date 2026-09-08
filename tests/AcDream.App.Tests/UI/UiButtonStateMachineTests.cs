using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class UiButtonStateMachineTests
{
    public static TheoryData<UiButtonVisualInput, uint> RequestedCases
        => new()
        {
            { new(false, false, false, false, false), UiButtonStateMachine.Normal },
            { new(false, false, true, false, true), UiButtonStateMachine.NormalRollover },
            { new(false, false, true, true, false), UiButtonStateMachine.NormalRollover },
            { new(false, false, true, true, true), UiButtonStateMachine.NormalPressed },
            { new(false, true, false, false, false), UiButtonStateMachine.Highlight },
            { new(false, true, true, false, true), UiButtonStateMachine.HighlightRollover },
            { new(false, true, true, true, false), UiButtonStateMachine.HighlightRollover },
            { new(false, true, true, true, true), UiButtonStateMachine.HighlightPressed },
            { new(true, false, true, true, true), UiButtonStateMachine.Ghosted },
            { new(true, true, true, true, true), UiButtonStateMachine.Ghosted },
        };

    [Theory]
    [MemberData(nameof(RequestedCases))]
    public void RequestedState_MatchesRetailPrecedence(UiButtonVisualInput input, uint expected)
        => Assert.Equal(expected, UiButtonStateMachine.RequestedState(input));

    [Fact]
    public void ResolveState_MissingRequestedState_PreservesCustomState()
    {
        var available = new HashSet<uint> { 0x10000053u };

        uint resolved = UiButtonStateMachine.ResolveState(
            0x10000053u,
            new UiButtonVisualInput(false, false, true, false, true),
            available);

        Assert.Equal(0x10000053u, resolved);
    }
}
