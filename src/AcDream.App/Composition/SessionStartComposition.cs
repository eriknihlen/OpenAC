using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.Composition;

internal sealed record SessionStartDependencies(
    Action<string> Log);

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
