using AcDream.App.Composition;
using AcDream.Runtime;

namespace AcDream.App.Tests.Composition;

public sealed class SessionStartCompositionTests
{
    [Fact]
    public void AWindowJoiningAPlayingSession_ComesToTheFront()
    {
        // A window opened onto a running session (the console's /window, the
        // launcher's switch) was not started by the player's click, so it
        // asks for the keyboard itself.
        int broughtToFront = 0;
        var phase = new SessionStartCompositionPhase(new SessionStartDependencies(
            static _ => { },
            JoinPlayingSession: static () => true,
            BringWindowToFront: () => broughtToFront++));

        phase.Start(new FrameRootResult(null!, null!, null!, null!, null!, null!));

        Assert.Equal(1, broughtToFront);
    }

    [Fact]
    public void DiagnosticsPreserveMissingCredentialAndFailureMessagesOnly()
    {
        var messages = new List<string>();

        SessionStartCompositionPhase.Report(
            new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.MissingCredentials,
                default),
            messages.Add);
        SessionStartCompositionPhase.Report(
            new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.Failed,
                default,
                Error: new InvalidOperationException("boom")),
            messages.Add);
        SessionStartCompositionPhase.Report(
            new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.Disabled,
                default),
            messages.Add);
        SessionStartCompositionPhase.Report(
            new RuntimeSessionStartResult(
                RuntimeSessionStartStatus.Connected,
                default),
            messages.Add);

        Assert.Equal(2, messages.Count);
        Assert.Equal(
            "live: ACDREAM_LIVE set but TEST_USER/TEST_PASS missing; skipping",
            messages[0]);
        Assert.StartsWith(
            "live: session failed: System.InvalidOperationException: boom",
            messages[1],
            StringComparison.Ordinal);
    }
}
