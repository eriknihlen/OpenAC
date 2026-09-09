using System.Diagnostics.CodeAnalysis;

namespace AcDream.App.Rendering.Walk;

public sealed class WalkBuildingRegistry
{
    private readonly Dictionary<uint, IReadOnlyList<WalkBuildingFactory.Entry>> _byLandblock = new();
    private readonly Dictionary<WalkBuilding, WalkBuildingFactory.Entry> _byBuilding = new();

    public void Publish(uint landblockId, IReadOnlyList<WalkBuildingFactory.Entry> entries)
    {
        uint key = landblockId & 0xFFFF0000u;
        if (_byLandblock.TryGetValue(key, out IReadOnlyList<WalkBuildingFactory.Entry>? previous))
        {
            foreach (WalkBuildingFactory.Entry entry in previous)
                _byBuilding.Remove(entry.Building);
        }
        _byLandblock[key] = entries;
        foreach (WalkBuildingFactory.Entry entry in entries)
            _byBuilding[entry.Building] = entry;
    }

    public void Retire(uint landblockId)
    {
        uint key = landblockId & 0xFFFF0000u;
        if (_byLandblock.Remove(key, out IReadOnlyList<WalkBuildingFactory.Entry>? previous))
        {
            foreach (WalkBuildingFactory.Entry entry in previous)
                _byBuilding.Remove(entry.Building);
        }
    }

    /// <summary>The buildings of one landblock, or empty when none are
    /// published (unloaded, far-tier, or no <c>BuildingInfo</c> entries).</summary>
    public IReadOnlyList<WalkBuildingFactory.Entry> GetBuildings(uint landblockId) =>
        _byLandblock.TryGetValue(landblockId & 0xFFFF0000u, out IReadOnlyList<WalkBuildingFactory.Entry>? list)
            ? list
            : Array.Empty<WalkBuildingFactory.Entry>();

    public bool TryGetEntry(
        WalkBuilding building, [MaybeNullWhen(false)] out WalkBuildingFactory.Entry entry) =>
        _byBuilding.TryGetValue(building, out entry);

    public int LandblockCount => _byLandblock.Count;
}
