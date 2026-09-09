using System.Numerics;

namespace AcDream.Core.Selection;

public sealed record RetailSelectionMesh(
    Vector3 SphereCenter,
    float SphereRadius,
    IReadOnlyList<RetailSelectionPolygon> Polygons);

/// <summary>One visual polygon. Vertex order and one/two-sidedness are DAT-authored.</summary>
public sealed record RetailSelectionPolygon(
    IReadOnlyList<Vector3> Vertices,
    bool SingleSided);

/// <summary>One part which survived the normal world-render visibility traversal.</summary>
public readonly record struct RetailSelectionPart(
    uint ServerGuid,
    uint LocalEntityId,
    int PartIndex,
    Matrix4x4 LocalToWorld,
    RetailSelectionMesh Mesh);

public readonly record struct RetailSelectionHit(
    uint ServerGuid,
    uint LocalEntityId,
    int PartIndex,
    double Distance,
    bool PolygonHit);
