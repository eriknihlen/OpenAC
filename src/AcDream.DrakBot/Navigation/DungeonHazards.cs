using System.Text.Json;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Navigation;

/// <summary>
/// Cells the planner must route around - acid pools, lava, a pit - marked
/// by the operator from where the character stands and kept per landblock
/// in the plugin's storage, so a dungeon learned once stays learned.
/// </summary>
public sealed class DungeonHazards(IPluginStorage storage)
{
    private const string Prefix = "hazards/";
    private readonly Dictionary<uint, HashSet<uint>> _cache = new();

    public IReadOnlySet<uint> For(uint landblock) => Load(landblock & 0xFFFF0000u);

    /// <summary>Marks a cell; true when it was not already marked.</summary>
    public bool Add(uint cellId)
    {
        uint landblock = cellId & 0xFFFF0000u;
        HashSet<uint> cells = Load(landblock);
        if (!cells.Add(cellId))
            return false;
        Save(landblock, cells);
        return true;
    }

    public bool Remove(uint cellId)
    {
        uint landblock = cellId & 0xFFFF0000u;
        HashSet<uint> cells = Load(landblock);
        if (!cells.Remove(cellId))
            return false;
        Save(landblock, cells);
        return true;
    }

    public void Clear(uint landblock)
    {
        landblock &= 0xFFFF0000u;
        _cache[landblock] = new HashSet<uint>();
        if (storage.IsAvailable)
            storage.Delete(Key(landblock));
    }

    private HashSet<uint> Load(uint landblock)
    {
        if (_cache.TryGetValue(landblock, out HashSet<uint>? cells))
            return cells;
        cells = new HashSet<uint>();
        string? json = storage.IsAvailable ? storage.ReadText(Key(landblock)) : null;
        if (json is not null)
        {
            try
            {
                foreach (uint id in JsonSerializer.Deserialize<uint[]>(json, BotProfile.JsonOptions) ?? [])
                    cells.Add(id);
            }
            catch (JsonException)
            {
                // A damaged file is an empty list; the next mark rewrites it.
            }
        }
        _cache[landblock] = cells;
        return cells;
    }

    private void Save(uint landblock, HashSet<uint> cells)
    {
        if (storage.IsAvailable)
            storage.WriteText(Key(landblock), JsonSerializer.Serialize(cells.OrderBy(id => id).ToArray(), BotProfile.JsonOptions));
    }

    private static string Key(uint landblock) => $"{Prefix}{landblock >> 16:X4}.json";

    // ── points given up on ──────────────────────────────────────────────
    // A route point the walk could not reach from a cell - the room above
    // a hole, a ledge - is remembered with that cell, per landblock, so the
    // next session skips it at once instead of six detours into the wall.

    private const string GivenUpPrefix = "givenup/";
    private readonly Dictionary<uint, HashSet<string>> _givenUp = new();

    public IReadOnlySet<string> GivenUpFor(uint landblock) => LoadGivenUp(landblock & 0xFFFF0000u);

    /// <summary>Remembers a point given up on from a cell; true when it is new.</summary>
    public bool AddGivenUp(uint cellId, string key)
    {
        uint landblock = cellId & 0xFFFF0000u;
        HashSet<string> keys = LoadGivenUp(landblock);
        if (!keys.Add(key))
            return false;
        if (storage.IsAvailable)
            storage.WriteText(GivenUpKey(landblock), JsonSerializer.Serialize(keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(), BotProfile.JsonOptions));
        return true;
    }

    private HashSet<string> LoadGivenUp(uint landblock)
    {
        if (_givenUp.TryGetValue(landblock, out HashSet<string>? keys))
            return keys;
        keys = new HashSet<string>(StringComparer.Ordinal);
        string? json = storage.IsAvailable ? storage.ReadText(GivenUpKey(landblock)) : null;
        if (json is not null)
        {
            try
            {
                foreach (string key in JsonSerializer.Deserialize<string[]>(json, BotProfile.JsonOptions) ?? [])
                    keys.Add(key);
            }
            catch (JsonException)
            {
            }
        }
        _givenUp[landblock] = keys;
        return keys;
    }

    private static string GivenUpKey(uint landblock) => $"{GivenUpPrefix}{landblock >> 16:X4}.json";
}
