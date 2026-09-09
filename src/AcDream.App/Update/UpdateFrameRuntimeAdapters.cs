using AcDream.App.Input;
using AcDream.App.World;
using AcDream.Core.Vfx;

namespace AcDream.App.Update;

internal sealed class LiveEntityTeardownFramePhase : IUpdateFrameTeardownPhase
{
    private readonly LiveEntityRuntime _runtime;

    public LiveEntityTeardownFramePhase(LiveEntityRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public void RetryPendingTeardowns() => _runtime.RetryPendingTeardowns();
}

internal sealed class ConsoleUpdateFrameFailureSink : IUpdateFrameFailureSink
{
    public void ReportTeardownFailure(AggregateException error) =>
        Console.Error.WriteLine($"[live-entity-teardown] {error}");
}

internal sealed class PhysicsScriptClockPublisher : IUpdateFrameScriptClockPublisher
{
    private readonly PhysicsScriptRunner _runner;

    public PhysicsScriptClockPublisher(PhysicsScriptRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public void PublishTime(double scriptTime) => _runner.PublishTime(scriptTime);
}

internal interface IClientMonotonicTimeSource
{
    double Now { get; }
}

internal sealed class StopwatchClientMonotonicTimeSource
    : IClientMonotonicTimeSource
{
    public double Now =>
        System.Diagnostics.Stopwatch.GetTimestamp()
        / (double)System.Diagnostics.Stopwatch.Frequency;
}

internal sealed class LiveEntityLivenessFramePhase
    : ILiveEntityLivenessFramePhase
{
    private readonly LiveEntityLivenessController _liveness;
    private readonly IClientMonotonicTimeSource _clock;

    public LiveEntityLivenessFramePhase(
        LiveEntityLivenessController liveness,
        IClientMonotonicTimeSource clock)
    {
        _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void Tick() => _liveness.Tick(_clock.Now);
}

internal sealed class PlayerModeAutoEntryFramePhase
    : IPlayerModeAutoEntryFramePhase
{
    private readonly PlayerModeAutoEntry _autoEntry;

    public PlayerModeAutoEntryFramePhase(PlayerModeAutoEntry autoEntry) =>
        _autoEntry = autoEntry ?? throw new ArgumentNullException(nameof(autoEntry));

    public void TryEnter() => _autoEntry.TryEnter();
}
