using System.Diagnostics;
using AcDream.DrakBot.Behaviors;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote;

/// <summary>
/// The session figures the phone shows that nobody else keeps: rates per
/// hour, what changed since login, how long since the last kill, the last
/// thing the bot complained about. Fed once per status build on the plugin
/// tick.
/// </summary>
internal sealed class RemoteTelemetry
{
    // The character's own properties, as the server describes it.
    private const uint PropertyIntNumDeaths = 43u;
    private const uint PropertyInt64TotalExperience = 1u;
    private const uint PropertyInt64AvailableLuminance = 6u;

    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly Func<DateTime> _clock;
    private int _lastKills;
    private DateTime _lastKillAt;
    private DateTime _firstInWorldAt;
    private bool _baselined;
    private long _xpBaseline;
    private long _luminanceBaseline;
    private int _deathsBaseline;
    private long _lastLogSequence = -1;
    private BotLogEntry? _lastIssue;
    private int _ticksThisSecond;
    private int _ticksLastSecond = -1;
    private double _tickWindowStart;
    private readonly Stopwatch _tickClock = Stopwatch.StartNew();

    /// <param name="clock">Local time, as the bot's log stamps its entries.</param>
    public RemoteTelemetry(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (static () => DateTime.Now);
    }

    public double UptimeSeconds => _uptime.Elapsed.TotalSeconds;

    public int SessionKills { get; private set; }

    public double KillsPerHour { get; private set; }

    /// <summary>-1 until the first kill.</summary>
    public int SecondsSinceLastKill { get; private set; } = -1;

    public int Deaths { get; private set; }

    public int DeathsSession { get; private set; }

    public long XpSession { get; private set; }

    public double XpPerHour { get; private set; }

    public double LuminancePerHour { get; private set; }

    public BotLogEntry? LastIssue => _lastIssue;

    /// <summary>Seconds since the last warning or error; -1 with none.</summary>
    public long LastIssueAgeSeconds =>
        _lastIssue is { } issue ? Math.Max(0L, (long)(_clock() - issue.At).TotalSeconds) : -1L;

    /// <summary>Plugin ticks in the last whole second; until one has passed, the ticks so far.</summary>
    public int TicksPerSecond => _ticksLastSecond >= 0 ? _ticksLastSecond : _ticksThisSecond;

    /// <summary>Counts one plugin tick toward the tick rate.</summary>
    public void Tick()
    {
        double now = _tickClock.Elapsed.TotalSeconds;
        _ticksThisSecond++;
        if (now - _tickWindowStart >= 1d)
        {
            _ticksLastSecond = _ticksThisSecond;
            _ticksThisSecond = 0;
            _tickWindowStart = now;
        }
    }

    /// <summary>Folds in the bot's kill counter and the character's session properties.</summary>
    public void Update(BotController controller, IAutomationSurface surface, bool inWorld)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(surface);
        DateTime now = _clock();

        CombatBehavior? combat = controller.Engine.Behaviors.OfType<CombatBehavior>().FirstOrDefault();
        int kills = combat?.Kills ?? 0;
        if (kills < _lastKills)
            _lastKills = 0; // the counter was reset
        if (kills > _lastKills)
        {
            _lastKillAt = now;
            _lastKills = kills;
        }
        SessionKills = kills;
        SecondsSinceLastKill = _lastKillAt == default ? -1 : (int)Math.Max(0d, (now - _lastKillAt).TotalSeconds);

        if (!inWorld)
            return;
        if (_firstInWorldAt == default)
            _firstInWorldAt = now;
        double hours = Math.Max((now - _firstInWorldAt).TotalHours, 1d / 60d); // a minute at least, so a kill does not read as thousands an hour
        KillsPerHour = kills / hours;

        if (!surface.Objects.TryCaptureProperties(surface.Character.ObjectId, out PluginItemProperties properties))
            return;
        long xp = properties.Int64s.TryGetValue(PropertyInt64TotalExperience, out long totalXp) ? totalXp : 0L;
        long luminance = properties.Int64s.TryGetValue(PropertyInt64AvailableLuminance, out long lum) ? lum : 0L;
        int deaths = properties.Ints.TryGetValue(PropertyIntNumDeaths, out int numDeaths) ? numDeaths : 0;
        if (!_baselined)
        {
            _baselined = true;
            _xpBaseline = xp;
            _luminanceBaseline = luminance;
            _deathsBaseline = deaths;
        }
        Deaths = deaths;
        DeathsSession = Math.Max(0, deaths - _deathsBaseline);
        XpSession = Math.Max(0L, xp - _xpBaseline);
        XpPerHour = XpSession / hours;
        LuminancePerHour = Math.Max(0L, luminance - _luminanceBaseline) / hours;
    }

    /// <summary>Notes the newest warning or error in the bot's log, when the log has moved on.</summary>
    public void UpdateLastIssue(BotLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        long sequence = log.Sequence;
        if (sequence == _lastLogSequence)
            return;
        _lastLogSequence = sequence;
        IReadOnlyList<BotLogEntry> entries = log.Snapshot();
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            BotLogEntry entry = entries[index];
            if (entry.Level != BotLogLevel.Quiet)
                continue;
            if (_lastIssue is { } known && known.At == entry.At && known.Text == entry.Text)
                return;
            _lastIssue = entry;
            return;
        }
    }
}
