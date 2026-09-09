using System;
using System.Collections.Generic;

namespace AcDream.App.Rendering.Wb;

public sealed class BuildingRegistry
{
    private readonly Dictionary<uint, List<Building>> _byCellId = new();

    // Index 2: building-id → Building.
    private readonly Dictionary<uint, Building> _byBuildingId = new();

    public void Add(Building b)
    {
        if (_byBuildingId.TryGetValue(b.BuildingId, out var existing) && ReferenceEquals(existing, b))
            return;
        _byBuildingId[b.BuildingId] = b;
        foreach (var cellId in b.EnvCellIds)
        {
            if (!_byCellId.TryGetValue(cellId, out var list))
            {
                list = new List<Building>();
                _byCellId[cellId] = list;
            }
            if (!list.Contains(b)) list.Add(b);
        }
    }

    public IReadOnlyList<Building> GetBuildingsContainingCell(uint cellId) =>
        _byCellId.TryGetValue(cellId, out var list) ? list : Array.Empty<Building>();

    public Building? GetById(uint buildingId) =>
        _byBuildingId.TryGetValue(buildingId, out var b) ? b : null;

    /// <summary>Enumerates every registered building in unspecified order.</summary>
    public IEnumerable<Building> All() => _byBuildingId.Values;

    /// <summary>Number of registered buildings.</summary>
    public int Count => _byBuildingId.Count;
}
