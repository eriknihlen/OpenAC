using AcDream.Runtime.Session;

namespace AcDream.Runtime.Chat;

public readonly record struct LoginCommandFailure(
    int CommandIndex,
    string Command,
    string Error);

public sealed class LoginCommandSequence
{
    private readonly string[] _commands;
    private readonly TimeSpan _delay;
    private readonly TimeProvider _timeProvider;
    private readonly IChatCommandFeedback _feedback;
    private readonly ICommandBus _bus;
    private readonly Action<LoginCommandFailure> _onFailure;
    private RuntimeGenerationToken _generation;
    private RuntimeGenerationToken? _lastStartedGeneration;
    private long _nextDeadline;
    private int _nextIndex;
    private bool _active;

    public LoginCommandSequence(
        IEnumerable<string?>? commands,
        TimeSpan delay,
        IChatCommandFeedback feedback,
        ICommandBus bus,
        Action<LoginCommandFailure>? onFailure = null,
        TimeProvider? timeProvider = null)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(bus);

        _commands = commands?.Select(static command => command ?? string.Empty)
            .ToArray() ?? [];
        _delay = delay;
        _feedback = feedback;
        _bus = bus;
        _onFailure = onFailure ?? (static _ => { });
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int CommandCount => _commands.Length;
    public bool IsActive => _active;
    public int NextCommandIndex => _nextIndex;

    public void EnteredWorld(RuntimeGenerationToken generation)
    {
        if (_lastStartedGeneration == generation)
            return;

        _lastStartedGeneration = generation;
        _generation = generation;
        _nextIndex = 0;
        _active = _commands.Length > 0;
        _nextDeadline = _timeProvider.GetTimestamp();
        DrainDue(generation, isInWorld: true);
    }

    public void Tick(
        RuntimeGenerationToken generation,
        bool isInWorld)
    {
        DrainDue(generation, isInWorld);
    }

    public void Cancel(RuntimeGenerationToken generation)
    {
        if (_active && _generation == generation)
            _active = false;
    }

    private void DrainDue(
        RuntimeGenerationToken generation,
        bool isInWorld)
    {
        if (!_active || !isInWorld || generation != _generation)
            return;

        long now = _timeProvider.GetTimestamp();
        while (_active
            && generation == _generation
            && _nextIndex < _commands.Length
            && now >= _nextDeadline)
        {
            int commandIndex = _nextIndex;
            string command = _commands[commandIndex];
            try
            {
                SubmitOutcome outcome = ChatCommandRouter.Submit(
                    command,
                    _feedback,
                    _bus,
                    ChatChannelKind.Say);
                if (outcome is SubmitOutcome.UnknownCommand
                    or SubmitOutcome.Dropped)
                {
                    ReportFailure(new LoginCommandFailure(
                        commandIndex,
                        command,
                        $"Chat command routing returned {outcome}."));
                }
            }
            catch (Exception error)
            {
                ReportFailure(new LoginCommandFailure(
                    commandIndex,
                    command,
                    error.GetBaseException().Message));
            }

            if (!_active || generation != _generation)
                return;

            _nextIndex++;
            if (_nextIndex >= _commands.Length)
            {
                _active = false;
                return;
            }

            now = _timeProvider.GetTimestamp();
            _nextDeadline = Add(_timeProvider, now, _delay);
        }
    }

    private void ReportFailure(LoginCommandFailure failure)
    {
        try
        {
            _onFailure(failure);
        }
        catch (Exception)
        {
        }
    }

    private static long Add(
        TimeProvider provider,
        long timestamp,
        TimeSpan duration)
    {
        double delta = duration.TotalSeconds * provider.TimestampFrequency;
        if (delta >= long.MaxValue - timestamp)
            return long.MaxValue;
        return checked(timestamp + (long)Math.Ceiling(delta));
    }
}
