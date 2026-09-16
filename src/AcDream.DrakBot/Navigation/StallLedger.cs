using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Navigation;

/// <summary>
/// One place the walk has stood still against something while pressing
/// on: where, how many times, for how long in all and at the worst, which
/// step and heading, and what freed it last. The ledger of a dungeon's
/// remaining faults, for weeding them out.
/// </summary>
public sealed class StallSpot
{
    public required string Key { get; init; }
    public uint CellId { get; init; }
    public double EastWest { get; init; }
    public double NorthSouth { get; init; }
    public double Elevation { get; init; }
    public int Count { get; set; }
    public double Seconds { get; set; }
    public double LongestSeconds { get; set; }
    public int Step { get; set; }
    public float Heading { get; set; }
    public string FreedBy { get; set; } = string.Empty;
    public string LastAt { get; set; } = string.Empty;

    [JsonIgnore]
    public PluginNavigationPosition Position => new(CellId, EastWest, NorthSouth, Elevation, Heading, false);
}

/// <summary>
/// Watches the walk for standing still while pressing on - the stuck
/// detector's own judgement, distance covered over a window - and keeps
/// the ledger of where that happens, per landblock, in the plugin's
/// storage. A stall opens when the walk first fails to progress and
/// closes when it progresses again, the step changes, or the walk ends;
/// the log gets a line at each end with the seconds, and the spot (to the
/// metre) gathers the count and the time. `/drakbot nav stalls` reads it.
/// </summary>
public sealed class StallLedger(IPluginStorage storage)
{
    private const string Prefix = "stalls/";
    private readonly Dictionary<uint, Dictionary<string, StallSpot>> _cache = new();

    private bool _open;
    private double _openedAt;
    private PluginNavigationPosition _openedWhere;
    private int _openedStep;
    private float _openedHeading;
    private string _freedBy = string.Empty;

    /// <summary>The stall under way, if any: since when and where.</summary>
    public (double Since, PluginNavigationPosition Where)? Current => _open ? (_openedAt, _openedWhere) : null;

    /// <summary>What the walk was trying when the stall began, for the ledger; the last recovery or detour tried is what freed it.</summary>
    public void NoteAttempt(string what) => _freedBy = what;

    /// <summary>
    /// A walking tick: <paramref name="progressing"/> is the stuck
    /// detector's verdict for the window that just closed (null while the
    /// window is still open). Returns a line for the log when a stall
    /// opens or closes; null otherwise.
    /// </summary>
    public string? Observe(in PluginNavigationPosition position, double now, bool? progressing, int step, float heading)
    {
        if (progressing is null)
            return null;
        if (!progressing.Value)
        {
            if (_open)
                return null;
            _open = true;
            _openedAt = now;
            _openedWhere = position;
            _openedStep = step;
            _openedHeading = heading;
            _freedBy = string.Empty;
            return $"nav: stalled at {BotEngine.Describe(position)} on step {step + 1} heading {heading:0}";
        }
        if (!_open)
            return null;
        return Close(now, "moving again");
    }

    /// <summary>The walk moved on for another reason - a fight, a new route, the step skipped - and a stall under way is closed with it.</summary>
    public string? Interrupt(double now, string why) => _open ? Close(now, why) : null;

    private string Close(double now, string how)
    {
        _open = false;
        double seconds = now - _openedAt;
        StallSpot spot = Record(_openedWhere, seconds, _openedStep, _openedHeading, _freedBy.Length > 0 ? _freedBy : how);
        return $"nav: {how} after {seconds:0}s stalled at {BotEngine.Describe(_openedWhere)} on step {_openedStep + 1}"
            + $"{(_freedBy.Length > 0 ? $" (last tried {_freedBy})" : string.Empty)}; this spot {spot.Count} time(s), {spot.Seconds:0}s in all";
    }

    private StallSpot Record(in PluginNavigationPosition where, double seconds, int step, float heading, string freedBy)
    {
        uint landblock = where.CellId & 0xFFFF0000u;
        Dictionary<string, StallSpot> spots = Load(landblock);
        string key = KeyOf(where);
        if (!spots.TryGetValue(key, out StallSpot? spot))
        {
            spot = new StallSpot
            {
                Key = key,
                CellId = where.CellId,
                EastWest = where.EastWest,
                NorthSouth = where.NorthSouth,
                Elevation = where.Elevation,
            };
            spots[key] = spot;
        }
        spot.Count++;
        spot.Seconds += seconds;
        spot.LongestSeconds = Math.Max(spot.LongestSeconds, seconds);
        spot.Step = step + 1;
        spot.Heading = heading;
        spot.FreedBy = freedBy;
        spot.LastAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
        Save(landblock, spots);
        return spot;
    }

    /// <summary>The worst spots of a landblock, most time lost first.</summary>
    public IReadOnlyList<StallSpot> Worst(uint landblock, int count = 10) =>
        Load(landblock & 0xFFFF0000u).Values.OrderByDescending(s => s.Seconds).Take(count).ToArray();

    public void Clear(uint landblock)
    {
        landblock &= 0xFFFF0000u;
        _cache[landblock] = new Dictionary<string, StallSpot>(StringComparer.Ordinal);
        Save(landblock, _cache[landblock]);
    }

    /// <summary>To the metre, so a wall stalled against from either side of a corner is one spot, not twenty.</summary>
    private static string KeyOf(in PluginNavigationPosition position) =>
        $"{position.CellId:X8}:{Math.Round(position.EastWest * 240d):0}:{Math.Round(position.NorthSouth * 240d):0}";

    private Dictionary<string, StallSpot> Load(uint landblock)
    {
        if (_cache.TryGetValue(landblock, out Dictionary<string, StallSpot>? spots))
            return spots;
        spots = new Dictionary<string, StallSpot>(StringComparer.Ordinal);
        string? json = storage.IsAvailable ? storage.ReadText(Key(landblock)) : null;
        if (json is not null)
        {
            try
            {
                foreach (StallSpot spot in JsonSerializer.Deserialize<StallSpot[]>(json, BotProfile.JsonOptions) ?? [])
                    spots[spot.Key] = spot;
            }
            catch (JsonException)
            {
            }
        }
        _cache[landblock] = spots;
        return spots;
    }

    private void Save(uint landblock, Dictionary<string, StallSpot> spots)
    {
        if (storage.IsAvailable)
            storage.WriteText(Key(landblock), JsonSerializer.Serialize(spots.Values.OrderByDescending(s => s.Seconds).ToArray(), BotProfile.JsonOptions));
    }

    private static string Key(uint landblock) => $"{Prefix}{landblock >> 16:X4}.json";
}
