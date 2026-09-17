using System.Globalization;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Navigation;

/// <summary>
/// The <c>/nav</c> and <c>/motor</c> chat commands: walks the client plans, and moves the
/// client carries out, asked for by typing. Both are built on the plugin navigation API, so
/// they behave the same in every host, and each says in chat how what it started ended.
/// </summary>
internal sealed class NavigationChatCommands : IDisposable
{
    public const string NavVerb = "nav";
    public const string MotorVerb = "motor";

    /// <summary>How far away an object named in <c>/nav go to</c> may stand.</summary>
    internal const double NameSearchMeters = 1000d;

    internal const string NavUsage =
        "Usage: /nav go to [target | 0xOBJECTID | name | CELL [X Y Z] | [X Y Z] | 24.3N 101.1W] [within METERS], "
        + "/nav stand on [target | 0xOBJECTID | name] [within METERS], "
        + "/nav follow [target | 0xPLAYERID | player name] [BUFFER METERS], /nav stop, /nav status, /nav grid, /nav route [target | 0xOBJECTID | name], "
        + "/nav debug [on | off], /nav debug file PATH | off";

    /// <summary>What begins every line of a walk's narration written to chat.</summary>
    internal const string DebugPrefix = "[nav] ";

    internal const string MotorUsage =
        "Usage: /motor walk|run [forward|backward] [AMOUNT], /motor strafe left|right [AMOUNT], "
        + "/motor turn left|right [DEGREES], /motor turn to HEADING, /motor jump [POWER], "
        + "/motor stop [travel|strafe|turn], /motor status; join moves with +, such as "
        + "/motor run forward 20s + turn left 90. An AMOUNT is meters, degrees for a turn, or seconds written as 20s.";

    private readonly INavigationAutomation _navigation;
    private readonly Func<uint?> _selected;
    private readonly Action<string> _say;


    private readonly Func<bool>? _toggleGrid;
    private readonly Func<uint, bool>? _previewRoute;
    private readonly Action<Action<string>?>? _narrate;
    private bool _debugToChat;
    private string? _debugFile;
    private readonly List<IDisposable> _registrations = [];
    private readonly List<(PluginMoveChannel Channel, long Sequence, string Label)> _moves = [];
    private (long Sequence, string Label)? _walk;
    private (long Sequence, string Label)? _route;
    private IEvents? _events;
    private bool _disposed;

    /// <param name="navigation">The navigation API the commands ask.</param>
    /// <param name="selected">The object the player has selected, if any.</param>
    /// <param name="say">Where replies go, such as the chat log.</param>
    /// <param name="toggleGrid">Shows or hides the navigation grid and says whether it is now shown; null where nothing is drawn.</param>
    /// <param name="previewRoute">Plans and draws a route to an object without walking it; null where nothing is drawn.</param>
    /// <param name="narrate">Sets who hears the walks' full narration, or no one; null where walks are not narrated.</param>
    public NavigationChatCommands(
        INavigationAutomation navigation,
        Func<uint?> selected,
        Action<string> say,
        Func<bool>? toggleGrid = null,
        Func<uint, bool>? previewRoute = null,
        Action<Action<string>?>? narrate = null)
    {
        _narrate = narrate;
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _say = say ?? throw new ArgumentNullException(nameof(say));
        _toggleGrid = toggleGrid;
        _previewRoute = previewRoute;
    }

    /// <summary>
    /// Registers <c>/nav</c> and <c>/motor</c> and follows what they start on every tick of
    /// <paramref name="events"/>. Dispose the result to unregister both.
    /// </summary>
    public NavigationChatCommands Register(IPluginCommandRegistry commands, IEvents? events)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _registrations.Add(commands.Register(NavVerb, command => Nav(command.Arguments)));
        _registrations.Add(commands.Register(MotorVerb, command => Motor(command.Arguments)));
        if (events is not null)
        {
            events.Tick += OnTick;
            _events = events;
        }
        return this;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_events is not null)
            _events.Tick -= OnTick;
        if (_debugToChat || _debugFile is not null)
            _narrate?.Invoke(null);
        foreach (IDisposable registration in _registrations)
            registration.Dispose();
        _registrations.Clear();
    }

    private void OnTick(double elapsedSeconds) => Tick();

    /// <summary>Answers a command in chat: its status, usage, a problem with it, or a toggle.</summary>
    private void Reply(string line) => _say(line);

    /// <summary>
    /// Says how a walk, route or move started and ended, only while debug narration is on,
    /// to chat and to the debug file, so walks stay quiet in chat otherwise.
    /// </summary>
    private void Announce(string line)
    {
        if (_debugToChat || _debugFile is not null)
            Narrate(line);
    }

    /// <summary>Says how each walk and move these commands started ended, once it has.</summary>
    internal void Tick()
    {
        if (_route is { } route)
        {
            PluginGoToReport report = _navigation.GoToReport;
            if (report.Sequence != route.Sequence)
            {
                _route = null;
            }
            else if (report.State != PluginGoToState.Planning)
            {
                // A route asked for alone that was found reports no walk state of its own.
                _route = null;
                Announce(report.State == PluginGoToState.None
                    ? $"{route.Label}: {(string.IsNullOrWhiteSpace(report.Reason) ? "a route was found" : report.Reason)}"
                    : $"{route.Label}: {Describe(report)}");
            }
        }

        if (_walk is { } walk)
        {
            PluginGoToReport report = _navigation.GoToReport;
            if (report.Sequence != walk.Sequence)
            {
                _walk = null;
            }
            else if (IsFinished(report.State))
            {
                _walk = null;
                Announce($"{walk.Label}: {Describe(report)}");
            }
        }

        for (int index = _moves.Count - 1; index >= 0; index--)
        {
            (PluginMoveChannel channel, long sequence, string label) = _moves[index];
            PluginMoveProgress progress = _navigation.MoveReport[channel];
            if (progress.Sequence != sequence)
            {
                _moves.RemoveAt(index);
                continue;
            }
            if (progress.State is PluginMoveState.None or PluginMoveState.Moving)
                continue;
            _moves.RemoveAt(index);
            Announce($"{label}: {Describe(progress)}");
        }
    }

    // ── /nav ──────────────────────────────────────────────────────────────

    internal void Nav(string arguments)
    {
        string[] words = Words(arguments);
        if (words.Length == 0 || Is(words[0], "help"))
        {
            Reply(NavUsage);
            return;
        }

        if (Is(words[0], "go") && words.Length >= 2 && Is(words[1], "to"))
        {
            GoTo(words[2..]);
            return;
        }
        if (Is(words[0], "goto"))
        {
            GoTo(words[1..]);
            return;
        }
        if (Is(words[0], "follow"))
        {
            Follow(words[1..]);
            return;
        }
        if (Is(words[0], "stand") && words.Length >= 2 && Is(words[1], "on"))
        {
            StandOn(words[2..]);
            return;
        }
        if (Is(words[0], "stop") && words.Length == 1)
        {
            Reply(_navigation.StopGoTo() switch
            {
                PluginNavigationCommandStatus.Accepted => "Navigation: stopped the walk",
                PluginNavigationCommandStatus.Rejected => "Navigation: no walk is under way",
                _ => "Navigation: the character is not in the world",
            });
            return;
        }
        if (Is(words[0], "status") && words.Length == 1)
        {
            PluginGoToReport report = _navigation.GoToReport;
            Reply(report.Sequence == 0
                ? "Navigation: no walk has been asked for"
                : $"Navigation: {Describe(report)}");
            return;
        }
        if (Is(words[0], "grid") && words.Length == 1)
        {
            Reply(_toggleGrid is null
                ? "Navigation: this client has nothing to draw the grid on"
                : _toggleGrid() ? "Navigation: grid shown" : "Navigation: grid hidden");
            return;
        }
        if (Is(words[0], "route"))
        {
            Route(words[1..]);
            return;
        }
        if (Is(words[0], "debug"))
        {
            Debug(words[1..], arguments);
            return;
        }
        Reply(NavUsage);
    }

    /// <summary>
    /// Turns the walks' full narration on or off, in chat and in a file: where each walk starts
    /// and heads, the grid it builds, the route it finds leg by leg and leap by leap, and what
    /// happens along the way.
    /// </summary>
    private void Debug(string[] words, string arguments)
    {
        if (_narrate is null)
        {
            Reply("Navigation: this client does not narrate its walks");
            return;
        }
        if (words.Length >= 1 && Is(words[0], "file"))
        {
            if (words.Length == 2 && Is(words[1], "off"))
            {
                _debugFile = null;
                Reply("Navigation: debug narration no longer goes to a file");
            }
            else if (words.Length >= 2)
            {
                string path = arguments.Trim();
                path = path[(path.IndexOf("file", StringComparison.OrdinalIgnoreCase) + 4)..].Trim();
                if (!TryOpenDebugFile(path, out string? full, out string? problem))
                {
                    Reply($"Navigation: cannot write debug narration to {path}: {problem}");
                    return;
                }
                _debugFile = full;
                Reply($"Navigation: debug narration goes to {full}");
            }
            else
            {
                Reply(NavUsage);
                return;
            }
        }
        else if (words.Length == 0)
        {
            _debugToChat = !_debugToChat;
            Reply(_debugToChat ? "Navigation: debug narration on" : "Navigation: debug narration off");
        }
        else if (words.Length == 1 && (Is(words[0], "on") || Is(words[0], "off")))
        {
            _debugToChat = Is(words[0], "on");
            Reply(_debugToChat ? "Navigation: debug narration on" : "Navigation: debug narration off");
        }
        else
        {
            Reply(NavUsage);
            return;
        }
        _narrate(_debugToChat || _debugFile is not null ? Narrate : null);
    }

    private void Narrate(string line)
    {
        if (_debugToChat)
            Reply(DebugPrefix + line);
        if (_debugFile is not { } file)
            return;
        try
        {
            File.AppendAllText(
                file,
                string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {line}{Environment.NewLine}"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _debugFile = null;
            Reply($"Navigation: stopped writing debug narration to {file}: {error.Message}");
            if (!_debugToChat)
                _narrate?.Invoke(null);
        }
    }

    /// <summary>Resolves a debug file's path, a leading ~ meaning the home folder, and makes sure it can be written.</summary>
    private static bool TryOpenDebugFile(string path, out string? full, out string? problem)
    {
        full = null;
        problem = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            problem = "no path was given";
            return false;
        }
        try
        {
            string expanded = path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
                : path;
            full = Path.GetFullPath(expanded);
            if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
            File.AppendAllText(full, string.Empty);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = error.Message;
            full = null;
            return false;
        }
    }

    private void GoTo(string[] words)
    {
        if (!TrySplitWithin(words, out string[] goalWords, out float arrival, out string? problem))
        {
            Reply($"Navigation: {problem}");
            return;
        }

        if (TryParsePlace(goalWords, out PluginNavigationPosition place, out string placeLabel))
        {
            PluginNavigationCommandStatus status = _navigation.GoTo(place, arrival);
            Started(status, $"Walk to {placeLabel}");
            return;
        }
        if (!TryResolveObject(goalWords, out uint objectId, out string label, out problem))
        {
            Reply($"Navigation: {problem}");
            return;
        }
        Started(_navigation.GoTo(objectId, arrival), $"Walk to {label}");
    }

    /// <summary>
    /// Follows a player: the selected one, one by id, or one by name, with the buffer to keep
    /// behind them as a last number or after within.
    /// </summary>
    private void Follow(string[] words)
    {
        if (!TrySplitWithin(words, out string[] goalWords, out float buffer, out string? problem))
        {
            Reply($"Navigation: {problem}");
            return;
        }
        if (goalWords.Length == words.Length)
        {
            buffer = RuntimeNavigationAutomation.DefaultFollowMeters;
            if (goalWords.Length > 0
                && !goalWords[^1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && TryParseFloat(goalWords[^1], out float trailing))
            {
                buffer = trailing;
                goalWords = goalWords[..^1];
            }
        }
        if (!TryResolveObject(goalWords, out uint playerId, out string label, out problem))
        {
            Reply($"Navigation: {problem}");
            return;
        }
        Started(_navigation.Follow(playerId, buffer), $"Follow {label}");
    }

    private void StandOn(string[] words)
    {
        if (!TrySplitWithin(words, out string[] goalWords, out float arrival, out string? problem)
            || !TryResolveObject(goalWords, out uint objectId, out string label, out problem))
        {
            Reply($"Navigation: {problem}");
            return;
        }
        Started(_navigation.StandOn(objectId, arrival), $"Stand on {label}");
    }

    private void Started(PluginNavigationCommandStatus status, string label)
    {
        switch (status)
        {
            case PluginNavigationCommandStatus.Accepted:
                _walk = (_navigation.GoToReport.Sequence, label);
                Announce($"{label}: planning");
                break;
            case PluginNavigationCommandStatus.Rejected:
                Reply($"{label}: refused; the arrival distance must be above 0 and at most {RuntimeNavigationAutomation.MaximumGoToArrivalMeters:0} m");
                break;
            default:
                Reply($"{label}: this client cannot walk now");
                break;
        }
    }

    private void Route(string[] words)
    {
        if (_previewRoute is null)
        {
            Reply("Navigation: this client has nothing to draw a route on");
            return;
        }
        if (!TryResolveObject(words, out uint objectId, out string label, out string? problem))
        {
            Reply($"Navigation: {problem}");
            return;
        }
        if (!_previewRoute(objectId))
        {
            Reply("Navigation: the character is not in the world");
            return;
        }
        _route = (_navigation.GoToReport.Sequence, $"Route to {label}");
        Announce($"Navigation: planning a route to {label}");
    }

    /// <summary>
    /// A place: a cell and a point in its landblock's own frame, as /loc prints them, with or
    /// without 0x, brackets, the "Your location is:" before them and the heading after them; a
    /// bracketed point alone, in the character's own cell; or map coordinates with compass
    /// letters, which stand on the ground there.
    /// </summary>
    internal bool TryParsePlace(string[] words, out PluginNavigationPosition place, out string label)
    {
        place = default;
        label = string.Empty;
        string joined = string.Join(' ', words);
        bool bracketed = joined.Contains('[', StringComparison.Ordinal);
        if (joined.StartsWith("Your location is:", StringComparison.OrdinalIgnoreCase))
            joined = joined["Your location is:".Length..];
        string[] parts = joined.Replace('[', ' ').Replace(']', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if ((parts.Length == 4 || (bracketed && parts.Length == 8))
            && TryParseHex(parts[0], requirePrefix: false, out uint cellId)
            && TryParseFloat(parts[1], out float x)
            && TryParseFloat(parts[2], out float y)
            && TryParseFloat(parts[3], out float z)
            && parts[4..].All(static part => TryParseFloat(part, out _)))
        {
            place = RuntimeNavigationProjection.Position(
                new Position(cellId, new CellFrame(new Vector3(x, y, z), Quaternion.Identity)));
            label = $"0x{cellId:X8} [{x:0.###} {y:0.###} {z:0.###}]";
            return true;
        }
        if (bracketed
            && parts.Length == 3
            && TryParseFloat(parts[0], out float localX)
            && TryParseFloat(parts[1], out float localY)
            && TryParseFloat(parts[2], out float localZ)
            && _navigation.Snapshot is { IsAvailable: true } standing)
        {
            uint hereCell = standing.Position.CellId;
            place = RuntimeNavigationProjection.Position(
                new Position(hereCell, new CellFrame(new Vector3(localX, localY, localZ), Quaternion.Identity)));
            label = $"0x{hereCell:X8} [{localX:0.###} {localY:0.###} {localZ:0.###}]";
            return true;
        }

        if (words.Length == 2
            && TryParseCompass(words[0], 'N', 'S', out double northSouth)
            && TryParseCompass(words[1], 'E', 'W', out double eastWest))
        {
            place = new PluginNavigationPosition(
                CellId: 0u,
                EastWest: eastWest,
                NorthSouth: northSouth,
                Elevation: double.NaN,
                HeadingDegrees: 0f,
                IsOutdoor: true);
            label = $"{Math.Abs(northSouth):0.###}{(northSouth < 0 ? 'S' : 'N')} {Math.Abs(eastWest):0.###}{(eastWest < 0 ? 'W' : 'E')}";
            return true;
        }
        return false;
    }

    /// <summary>The selected object when no words or target is given, an object id written 0x..., or an object named nearby.</summary>
    internal bool TryResolveObject(string[] words, out uint objectId, out string label, out string? problem)
    {
        objectId = 0u;
        label = string.Empty;
        problem = null;
        if (words.Length == 0 || (words.Length == 1 && (Is(words[0], "target") || Is(words[0], "selected") || Is(words[0], "selection"))))
        {
            if (_selected() is not { } selected || selected == 0u)
            {
                problem = "select something first";
                return false;
            }
            objectId = selected;
            label = LabelOf(selected);
            return true;
        }
        if (words.Length == 1 && TryParseHex(words[0], requirePrefix: true, out uint id))
        {
            objectId = id;
            label = LabelOf(id);
            return true;
        }

        string name = string.Join(' ', words);
        PluginNavigationSnapshot here = _navigation.Snapshot;
        if (!here.IsAvailable)
        {
            problem = "the character is not in the world";
            return false;
        }
        if (!_navigation.TryFindObject(name, here.Position, NameSearchMeters, out PluginNavigationObject found))
        {
            problem = $"nothing named \"{name}\" is within {NameSearchMeters:0} m";
            return false;
        }
        objectId = found.ObjectId;
        label = $"{found.Name} (0x{found.ObjectId:X8})";
        return true;
    }

    private string LabelOf(uint objectId) =>
        _navigation.TryGetObject(objectId, out PluginNavigationObject known) && known.Name.Length > 0
            ? $"{known.Name} (0x{objectId:X8})"
            : $"0x{objectId:X8}";

    private static bool TrySplitWithin(string[] words, out string[] goal, out float arrival, out string? problem)
    {
        goal = words;
        arrival = 2.5f;
        problem = null;
        int within = Array.FindLastIndex(words, word => Is(word, "within"));
        if (within < 0)
            return true;
        if (within != words.Length - 2 || !TryParseFloat(words[^1], out arrival))
        {
            problem = "within takes a distance in meters, such as within 3";
            return false;
        }
        goal = words[..within];
        return true;
    }

    // ── /motor ────────────────────────────────────────────────────────────

    /// <summary>
    /// A move to start: the channel it occupies, if any, so two on one channel are refused;
    /// whether it is a scripted move whose end the move report says; and how to start it.
    /// </summary>
    private readonly record struct MotorMove(
        PluginMoveChannel? Channel,
        bool Reported,
        string Label,
        Func<PluginNavigationCommandStatus> Start);

    internal void Motor(string arguments)
    {
        string[] parts = arguments.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.All(static part => part.Length == 0) || Is(arguments.Trim(), "help"))
        {
            Reply(MotorUsage);
            return;
        }

        string[] first = Words(parts[0]);
        if (parts.Length == 1 && first.Length >= 1 && Is(first[0], "status"))
        {
            PluginMoveReport report = _navigation.MoveReport;
            Reply($"Motor: travel {Describe(report.Travel)}; strafe {Describe(report.Strafe)}; turn {Describe(report.Turn)}");
            return;
        }
        if (parts.Length == 1 && first.Length >= 1 && Is(first[0], "stop"))
        {
            Stop(first[1..]);
            return;
        }

        var moves = new List<MotorMove>(parts.Length);
        var channels = new HashSet<PluginMoveChannel>();
        foreach (string part in parts)
        {
            if (!TryParseMove(Words(part), out MotorMove move, out string problem))
            {
                Reply($"Motor: {problem}. {MotorUsage}");
                return;
            }
            if (move.Channel is { } channel && !channels.Add(channel))
            {
                Reply($"Motor: two moves on the {channel.ToString().ToLowerInvariant()} channel cannot run together");
                return;
            }
            moves.Add(move);
        }

        foreach (MotorMove move in moves)
        {
            PluginNavigationCommandStatus status = move.Start();
            if (status != PluginNavigationCommandStatus.Accepted)
            {
                Reply(status == PluginNavigationCommandStatus.Rejected
                    ? $"{move.Label}: refused; it is past what the client carries out"
                    : $"{move.Label}: this client cannot move the character now");
                continue;
            }
            if (move.Reported && move.Channel is { } channel)
                _moves.Add((channel, _navigation.MoveReport[channel].Sequence, move.Label));
            else
                Announce($"{move.Label}: sent");
        }
    }

    private void Stop(string[] words)
    {
        PluginNavigationCommandStatus status;
        if (words.Length == 0)
        {
            status = _navigation.StopMoving();
        }
        else if (words.Length == 1 && ParseChannel(words[0]) is { } channel)
        {
            status = _navigation.StopMoving(channel);
        }
        else
        {
            Reply($"Motor: stop takes travel, strafe or turn. {MotorUsage}");
            return;
        }
        Reply(status switch
        {
            PluginNavigationCommandStatus.Accepted => "Motor: stopped",
            PluginNavigationCommandStatus.Rejected => "Motor: nothing to stop",
            _ => "Motor: the character is not in the world",
        });
    }

    private bool TryParseMove(string[] words, out MotorMove move, out string problem)
    {
        move = default;
        problem = "a move was not understood";
        if (words.Length == 0)
        {
            problem = "an empty move";
            return false;
        }

        string verb = words[0].ToLowerInvariant();
        switch (verb)
        {
            case "walk":
            case "run":
            {
                PluginMovePace pace = verb == "walk" ? PluginMovePace.Walk : PluginMovePace.Run;
                PluginMoveDirection direction = PluginMoveDirection.Forward;
                int next = 1;
                if (words.Length > 1 && Is(words[1], "backward"))
                {
                    direction = PluginMoveDirection.Backward;
                    next = 2;
                }
                else if (words.Length > 1 && Is(words[1], "forward"))
                {
                    next = 2;
                }
                if (!TryParseAmount(words[next..], out float amount, out PluginMoveUnit unit))
                {
                    problem = $"{verb} takes an amount such as 10 or 20s";
                    return false;
                }
                string label = $"{Capitalize(verb)} {direction.ToString().ToLowerInvariant()}{AmountLabel(amount, unit, "m")}";
                move = new MotorMove(PluginMoveChannel.Travel, true, label, () => _navigation.Move(direction, pace, amount, unit));
                return true;
            }
            case "strafe":
            {
                if (words.Length < 2 || !(Is(words[1], "left") || Is(words[1], "right")))
                {
                    problem = "strafe takes left or right";
                    return false;
                }
                PluginMoveDirection direction = Is(words[1], "left") ? PluginMoveDirection.StrafeLeft : PluginMoveDirection.StrafeRight;
                if (!TryParseAmount(words[2..], out float amount, out PluginMoveUnit unit))
                {
                    problem = "strafe takes an amount such as 2 or 3s";
                    return false;
                }
                string label = $"Strafe {words[1].ToLowerInvariant()}{AmountLabel(amount, unit, "m")}";
                move = new MotorMove(PluginMoveChannel.Strafe, true, label, () => _navigation.Move(direction, PluginMovePace.Run, amount, unit));
                return true;
            }
            case "turn":
            {
                if (words.Length == 3 && Is(words[1], "to") && TryParseFloat(words[2], out float heading))
                {
                    move = new MotorMove(PluginMoveChannel.Turn, false, $"Turn to {heading:0.#}°", () => _navigation.FaceHeading(heading));
                    return true;
                }
                if (words.Length < 2 || !(Is(words[1], "left") || Is(words[1], "right")))
                {
                    problem = "turn takes left, right, or to a heading";
                    return false;
                }
                PluginMoveDirection direction = Is(words[1], "left") ? PluginMoveDirection.TurnLeft : PluginMoveDirection.TurnRight;
                if (!TryParseAmount(words[2..], out float amount, out PluginMoveUnit unit))
                {
                    problem = "turn takes degrees such as 90, or seconds such as 2s";
                    return false;
                }
                string label = $"Turn {words[1].ToLowerInvariant()}{AmountLabel(amount, unit, "°")}";
                move = new MotorMove(PluginMoveChannel.Turn, true, label, () => _navigation.Move(direction, PluginMovePace.Run, amount, unit));
                return true;
            }
            case "jump":
            {
                float power = 1f;
                if (words.Length > 2 || (words.Length == 2 && !TryParseFloat(words[1], out power)))
                {
                    problem = "jump takes a power from above 0 to 1";
                    return false;
                }
                move = new MotorMove(null, false, $"Jump at {power:0.##}", () => _navigation.Jump(power));
                return true;
            }
            default:
                problem = $"{verb} is not a move";
                return false;
        }
    }

    private static bool TryParseAmount(string[] words, out float amount, out PluginMoveUnit unit)
    {
        amount = 0f;
        unit = PluginMoveUnit.MetersOrDegrees;
        if (words.Length == 0)
            return true;
        if (words.Length != 1)
            return false;
        string word = words[0];
        if (word.EndsWith('s') || word.EndsWith('S'))
        {
            unit = PluginMoveUnit.Seconds;
            word = word[..^1];
        }
        return TryParseFloat(word, out amount);
    }

    private static string AmountLabel(float amount, PluginMoveUnit unit, string distanceUnit) =>
        amount == 0f
            ? " until stopped"
            : unit == PluginMoveUnit.Seconds
                ? $" for {amount:0.##} s"
                : $" {amount:0.##} {distanceUnit}";

    private static PluginMoveChannel? ParseChannel(string word) =>
        Is(word, "travel") ? PluginMoveChannel.Travel
        : Is(word, "strafe") ? PluginMoveChannel.Strafe
        : Is(word, "turn") ? PluginMoveChannel.Turn
        : null;

    // ── Reports ───────────────────────────────────────────────────────────

    private static bool IsFinished(PluginGoToState state) =>
        state is not (PluginGoToState.None or PluginGoToState.Planning or PluginGoToState.Walking or PluginGoToState.Waiting);

    internal static string Describe(in PluginGoToReport report)
    {
        string state = report.State switch
        {
            PluginGoToState.Planning => "planning",
            PluginGoToState.Walking => "walking",
            PluginGoToState.Waiting => "waiting",
            PluginGoToState.Arrived => "arrived",
            PluginGoToState.ArrivedWithoutSight => "arrived without a line of sight",
            PluginGoToState.NoRoute => "no route",
            PluginGoToState.Blocked => "blocked",
            PluginGoToState.Stopped => "stopped",
            PluginGoToState.Interrupted => "interrupted",
            PluginGoToState.Lost => "lost",
            _ => "idle",
        };
        string text = string.IsNullOrWhiteSpace(report.Reason) || report.Reason == state
            ? state
            : $"{state} ({report.Reason})";
        if (report.BlockedByObjectId != 0u)
            text += $", blocked by 0x{report.BlockedByObjectId:X8}";
        if (float.IsFinite(report.RemainingMeters))
            text += $", {report.RemainingMeters:0.0} m left";
        return text;
    }

    internal static string Describe(in PluginMoveProgress progress)
    {
        if (progress.Sequence == 0)
            return "none";
        string state = progress.State switch
        {
            PluginMoveState.Moving => "moving",
            PluginMoveState.Completed => "completed",
            PluginMoveState.Stopped => "stopped",
            PluginMoveState.TimeLimit => "ran out of time",
            PluginMoveState.Blocked => "blocked",
            PluginMoveState.Interrupted => "interrupted by the player",
            PluginMoveState.Lost => "lost",
            _ => "none",
        };
        string unit = progress.Direction is PluginMoveDirection.TurnLeft or PluginMoveDirection.TurnRight ? "°" : " m";
        return $"{state} after {progress.Covered:0.##}{unit} in {progress.ElapsedSeconds:0.#} s";
    }

    // ── Words ─────────────────────────────────────────────────────────────

    private static string[] Words(string text) =>
        text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Is(string word, string expected) =>
        string.Equals(word, expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseFloat(string word, out float value) =>
        float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private static bool TryParseHex(string word, bool requirePrefix, out uint value)
    {
        value = 0u;
        string digits = word;
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            digits = digits[2..];
        else if (requirePrefix)
            return false;
        return digits.Length is > 0 and <= 8
            && uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseCompass(string word, char positive, char negative, out double value)
    {
        value = 0d;
        if (word.Length < 2)
            return false;
        char letter = char.ToUpperInvariant(word[^1]);
        if (letter != positive && letter != negative)
            return false;
        if (!double.TryParse(word[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double magnitude)
            || !double.IsFinite(magnitude)
            || magnitude < 0d)
        {
            return false;
        }
        value = letter == positive ? magnitude : -magnitude;
        return true;
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
