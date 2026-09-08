namespace AcDream.App.Update;

using AcDream.App.Streaming;
using AcDream.Runtime;

internal readonly record struct UpdateFrameInput(double HostDeltaSeconds);

internal readonly record struct UpdateFrameTiming(
    double SimulationDeltaSeconds,
    float SimulationDeltaSecondsSingle,
    double ScriptTime);

internal sealed class UpdateFrameClock
    : IPhysicsScriptTimeSource,
      IGameRuntimeClock
{
    private readonly GameRuntimeClock _runtime;

    public UpdateFrameClock()
        : this(new GameRuntimeClock())
    {
    }

    public UpdateFrameClock(GameRuntimeClock runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public ulong FrameNumber => _runtime.FrameNumber;
    public double SimulationTimeSeconds => _runtime.SimulationTimeSeconds;
    public double CurrentScriptTime => _runtime.SimulationTimeSeconds;

    public UpdateFrameTiming Advance(
        UpdateFrameInput input,
        bool advanceScriptClock = true)
    {
        RuntimeFrameTime frame = _runtime.Advance(
            input.HostDeltaSeconds,
            advanceScriptClock);
        return new UpdateFrameTiming(
            frame.DeltaSeconds,
            (float)frame.DeltaSeconds,
            frame.SimulationTimeSeconds);
    }

    public static double NormalizeDeltaSeconds(double deltaSeconds) =>
        GameRuntimeClock.NormalizeDeltaSeconds(deltaSeconds);
}

internal interface IPhysicsScriptTimeSource
{
    double CurrentScriptTime { get; }
}

internal interface IUpdateFrameTeardownPhase
{
    void RetryPendingTeardowns();
}

internal interface IUpdateFrameFailureSink
{
    void ReportTeardownFailure(AggregateException error);
}

internal interface IUpdateFrameScriptClockPublisher
{
    void PublishTime(double scriptTime);
}

internal interface IStreamingFramePhase
{
    void Tick();
}

internal interface IGameplayInputFramePhase
{
    void Tick(UpdateFrameTiming timing);
}

internal interface IRetailLiveFramePhase
{
    void Tick(float deltaSeconds);
}

internal interface ILiveEntityLivenessFramePhase
{
    void Tick();
}

internal interface ILocalPlayerTeleportFramePhase
{
    void Tick(float deltaSeconds);
}

internal interface IPlayerModeAutoEntryFramePhase
{
    void TryEnter();
}

internal interface ICameraFramePhase
{
    void Tick(UpdateFrameTiming timing);
}

internal interface IUpdateFrameCommitPhase
{
    void Commit();
}

internal sealed class UpdateFrameOrchestrator : AcDream.App.Rendering.IGameUpdateFrameRoot
{
    private readonly IUpdateFrameTeardownPhase _teardown;
    private readonly IUpdateFrameFailureSink _failureSink;
    private readonly UpdateFrameClock _clock;
    private readonly IUpdateFrameScriptClockPublisher _scriptClockPublisher;
    private readonly IStreamingFramePhase _streaming;
    private readonly IGameplayInputFramePhase _input;
    private readonly IRetailLiveFramePhase _liveFrame;
    private readonly ILiveEntityLivenessFramePhase _liveness;
    private readonly ILocalPlayerTeleportFramePhase _teleport;
    private readonly IPlayerModeAutoEntryFramePhase _playerModeAutoEntry;
    private readonly ICameraFramePhase _camera;
    private readonly IUpdateFrameCommitPhase _commit;
    private readonly IWorldGenerationAvailability _availability;

    public UpdateFrameOrchestrator(
        IUpdateFrameTeardownPhase teardown,
        IUpdateFrameFailureSink failureSink,
        UpdateFrameClock clock,
        IUpdateFrameScriptClockPublisher scriptClockPublisher,
        IStreamingFramePhase streaming,
        IGameplayInputFramePhase input,
        IRetailLiveFramePhase liveFrame,
        ILiveEntityLivenessFramePhase liveness,
        ILocalPlayerTeleportFramePhase teleport,
        IPlayerModeAutoEntryFramePhase playerModeAutoEntry,
        ICameraFramePhase camera,
        IUpdateFrameCommitPhase commit,
        IWorldGenerationAvailability? availability = null)
    {
        _teardown = teardown ?? throw new ArgumentNullException(nameof(teardown));
        _failureSink = failureSink ?? throw new ArgumentNullException(nameof(failureSink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scriptClockPublisher = scriptClockPublisher
            ?? throw new ArgumentNullException(nameof(scriptClockPublisher));
        _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _liveFrame = liveFrame ?? throw new ArgumentNullException(nameof(liveFrame));
        _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
        _teleport = teleport ?? throw new ArgumentNullException(nameof(teleport));
        _playerModeAutoEntry = playerModeAutoEntry
            ?? throw new ArgumentNullException(nameof(playerModeAutoEntry));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _availability = availability ?? AlwaysAvailableWorldGeneration.Instance;
    }

    public void Tick(UpdateFrameInput input)
    {
        try
        {
            _teardown.RetryPendingTeardowns();
        }
        catch (AggregateException error)
        {
            _failureSink.ReportTeardownFailure(error);
        }

        UpdateFrameTiming timing = _clock.Advance(
            input,
            advanceScriptClock: _availability.IsWorldAvailable);
        _scriptClockPublisher.PublishTime(timing.ScriptTime);
        _streaming.Tick();
        _input.Tick(timing);
        _liveFrame.Tick(timing.SimulationDeltaSecondsSingle);
        if (_availability.IsWorldAvailable)
            _liveness.Tick();
        _teleport.Tick(timing.SimulationDeltaSecondsSingle);
        _playerModeAutoEntry.TryEnter();
        _camera.Tick(timing);
        _commit.Commit();
    }
}
