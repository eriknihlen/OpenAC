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
}
