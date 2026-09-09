using System.Diagnostics;

namespace AcDream.App.Rendering;

internal sealed class FramePacingController : IDisposable
{
    private readonly IFramePacingClock _clock;
    private readonly IFramePacingWaiter _waiter;
    private readonly bool _ownsWaiter;

    private FramePacingPolicy _policy;
    private long _periodTicks;
    private long _nextDeadline;
    private bool _hasDeadline;
    private bool _disposed;

    public FramePacingController()
        : this(PlatformFramePacingWaiterFactory.ForCurrentProcess())
    {
    }

    internal FramePacingController(
        IFramePacingWaiterFactory waiterFactory)
        : this(
            StopwatchFramePacingClock.Instance,
            (waiterFactory
                ?? throw new ArgumentNullException(nameof(waiterFactory)))
                .Create(),
            ownsWaiter: true)
    {
    }

    internal FramePacingController(
        IFramePacingClock clock,
        IFramePacingWaiter waiter)
        : this(clock, waiter, ownsWaiter: false)
    {
    }

    internal FramePacingController(
        IFramePacingClock clock,
        IFramePacingWaiter waiter,
        bool ownsWaiter)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _waiter = waiter ?? throw new ArgumentNullException(nameof(waiter));
        _ownsWaiter = ownsWaiter;
        if (_clock.Frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(clock), "Clock frequency must be positive.");
    }

    internal FramePacingPolicy Policy => _policy;

    public void Apply(FramePacingPolicy policy)
    {
        if (_policy == policy)
            return;

        _policy = policy;
        if (policy.UseVSync
            || policy.SoftwareLimitHz is not { } limitHz
            || !double.IsFinite(limitHz)
            || limitHz <= 0d)
        {
            _periodTicks = 0;
            _nextDeadline = 0;
            _hasDeadline = false;
            return;
        }

        _periodTicks = Math.Max(
            1L,
            checked((long)Math.Round(_clock.Frequency / limitHz)));
        _nextDeadline = AddSaturating(_clock.GetTimestamp(), _periodTicks);
        _hasDeadline = true;
    }

    public void CompleteFrame()
    {
        if (!_hasDeadline || _periodTicks <= 0)
            return;

        long deadline = _nextDeadline;
        long now = _clock.GetTimestamp();

        if (now < deadline)
        {
            do
            {
                long remaining = deadline - now;
                _waiter.Wait(remaining, _clock.Frequency);
                now = _clock.GetTimestamp();
            }
            while (now < deadline);

            long followingDeadline = AddSaturating(deadline, _periodTicks);
            _nextDeadline = followingDeadline > now
                ? followingDeadline
                : AddSaturating(now, _periodTicks);
            return;
        }

        _nextDeadline = AddSaturating(now, _periodTicks);
    }

    private static long AddSaturating(long value, long increment)
        => value > long.MaxValue - increment
            ? long.MaxValue
            : value + increment;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_ownsWaiter && _waiter is IDisposable disposable)
            disposable.Dispose();
    }
}

internal interface IFramePacingClock
{
    long Frequency { get; }

    long GetTimestamp();
}

internal interface IFramePacingWaiter
{
    void Wait(long durationTicks, long clockFrequency);
}

internal sealed class StopwatchFramePacingClock : IFramePacingClock
{
    public static StopwatchFramePacingClock Instance { get; } = new();

    private StopwatchFramePacingClock()
    {
    }

    public long Frequency => Stopwatch.Frequency;

    public long GetTimestamp() => Stopwatch.GetTimestamp();
}
