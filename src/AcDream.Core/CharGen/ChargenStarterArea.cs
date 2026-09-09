using System.Numerics;

namespace AcDream.Core.CharGen;

public readonly record struct ChargenPosition(uint CellId, Vector3 Origin, Quaternion Orientation);

public sealed record ChargenStarterArea(
    int Index,
    string Name,
    IReadOnlyList<ChargenPosition> Locations);
