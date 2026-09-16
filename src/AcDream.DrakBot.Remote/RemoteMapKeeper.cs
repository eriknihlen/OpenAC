using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote;

/// <summary>
/// Keeps the floor plans of the dungeons the character has been in this
/// session. On the tick it watches the character's landblock; a dungeon
/// not drawn yet has its geometry read from the host (on the tick, where
/// the data files are read) and is drawn on the pool, and the result is
/// taken on a later tick, so the game never waits for a drawing. All
/// state is the tick's; <see cref="Snapshot"/> hands out immutable sets.
/// </summary>
internal sealed class RemoteMapKeeper
{
    /// <summary>Dungeons kept at once; the least recently visited goes first.</summary>
    public const int MaxDungeons = 12;
    /// <summary>How often the character's landblock is looked at.</summary>
    public const double PollSeconds = 1d;
    /// <summary>A dungeon with nothing to draw (or one that failed) is not asked about again for this long.</summary>
    public const double RetrySeconds = 120d;

    private readonly Func<uint, RemoteDungeonGeometry?> _geometry;
    private readonly Func<RemoteDungeonGeometry, RemoteDungeonMaps?> _render;
    private readonly Func<DateTime> _utcNow;
    private readonly IPluginLogger? _log;
    private readonly Dictionary<uint, RemoteDungeonMaps> _maps = [];
    private readonly Dictionary<uint, double> _lastSeen = [];
    private readonly Dictionary<uint, double> _blank = [];
    private double _polledAt = double.NegativeInfinity;
    private uint _renderingLandblock;
    private Task<RemoteDungeonMaps?>? _rendering;

    public RemoteMapKeeper(
        Func<uint, RemoteDungeonGeometry?> geometry,
        Func<RemoteDungeonGeometry, RemoteDungeonMaps?> render,
        Func<DateTime> utcNow,
        IPluginLogger? log = null)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _log = log;
    }

    /// <summary>Set when the kept maps differ from what <see cref="Snapshot"/> last handed out.</summary>
    public bool Changed { get; private set; }

    public int Count => _maps.Count;

    /// <summary>A drawing is on the pool and not taken yet.</summary>
    public bool IsDrawing => _rendering is not null;

    /// <summary>
    /// The tick: take a finished drawing, look at where the character is
    /// once a second, and start drawing a dungeon not seen before.
    /// </summary>
    public void Tick(double now, in PluginNavigationSnapshot navigation)
    {
        TakeFinished();
        if (now - _polledAt < PollSeconds)
            return;
        _polledAt = now;
        if (!navigation.IsAvailable || navigation.IsPortalSpace || navigation.Position.IsOutdoor)
            return;
        uint landblock = navigation.Position.CellId & 0xFFFF0000u;
        if (landblock == 0u)
            return;
        _lastSeen[landblock] = now;
        if (_maps.ContainsKey(landblock) || _rendering is not null)
            return;
        if (_blank.TryGetValue(landblock, out double blankAt) && now - blankAt < RetrySeconds)
            return;
        Start(landblock, now);
    }

    private void Start(uint landblock, double now)
    {
        RemoteDungeonGeometry? geometry;
        try
        {
            geometry = _geometry(landblock);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log?.Warn($"DrakBot Remote: reading dungeon {landblock >> 16:X4} failed: {error.Message}");
            geometry = null;
        }
        if (geometry is null || geometry.Polygons.Count == 0)
        {
            _blank[landblock] = now;
            return;
        }
        DateTime builtUtc = _utcNow();
        _renderingLandblock = landblock;
        _rendering = Task.Run(() => _render(geometry) is { } maps ? maps with { BuiltUtc = builtUtc } : null);
    }

    private void TakeFinished()
    {
        Task<RemoteDungeonMaps?>? rendering = _rendering;
        if (rendering is null || !rendering.IsCompleted)
            return;
        _rendering = null;
        uint landblock = _renderingLandblock;
        if (rendering.IsFaulted)
        {
            _log?.Warn($"DrakBot Remote: drawing dungeon {landblock >> 16:X4} failed: {rendering.Exception?.GetBaseException().Message}");
            _blank[landblock] = _polledAt;
            return;
        }
        RemoteDungeonMaps? maps = rendering.Result;
        if (maps is null)
        {
            _blank[landblock] = _polledAt;
            return;
        }
        _maps[landblock] = maps;
        Changed = true;
        while (_maps.Count > MaxDungeons)
        {
            uint oldest = 0u;
            double seen = double.PositiveInfinity;
            foreach (uint kept in _maps.Keys)
            {
                double at = _lastSeen.GetValueOrDefault(kept, double.NegativeInfinity);
                if (at < seen)
                {
                    seen = at;
                    oldest = kept;
                }
            }
            _maps.Remove(oldest);
        }
    }

    /// <summary>The kept floor plans, the most recently visited dungeon first; clears <see cref="Changed"/>.</summary>
    public IReadOnlyList<RemoteDungeonMaps> Snapshot()
    {
        Changed = false;
        return _maps.Values
            .OrderByDescending(m => _lastSeen.GetValueOrDefault(m.LandblockId, double.NegativeInfinity))
            .ToArray();
    }
}
