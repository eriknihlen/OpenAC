using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.Composition;

/// <param name="Log">Where the start's outcome is reported.</param>
/// <param name="JoinPlayingSession">
/// Asked first: true when the window joined a session that had already
/// started without it, which then is not started again.
/// </param>
/// <param name="BringWindowToFront">
/// Asked when the window joined a playing session: it was opened by a
/// command (the console's or the launcher's), not by the player's click, so
/// it asks for the keyboard itself.
/// </param>
internal sealed record SessionStartDependencies(
    Action<string> Log,
    Func<bool>? JoinPlayingSession = null,
    Action? BringWindowToFront = null);

internal sealed class SessionStartCompositionPhase
    : ISessionStartCompositionPhase<FrameRootResult>
{
    private readonly SessionStartDependencies _dependencies;

    public SessionStartCompositionPhase(SessionStartDependencies dependencies) =>
        _dependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies));

    public void Start(FrameRootResult frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_dependencies.JoinPlayingSession?.Invoke() == true)
        {
            _dependencies.BringWindowToFront?.Invoke();
            return;
        }
        RuntimeSessionStartResult result =
            frame.GameRuntime.Session.Start(frame.GameRuntime.Generation);
        Report(result, _dependencies.Log);
    }

    internal static void Report(
        RuntimeSessionStartResult result,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        switch (result.Status)
        {
            case RuntimeSessionStartStatus.MissingCredentials:
                log(
                    "live: ACDREAM_LIVE set but TEST_USER/TEST_PASS missing; skipping");
                break;
            case RuntimeSessionStartStatus.Failed:
                log($"live: session failed: {result.Error}");
                break;
        }
    }
}
