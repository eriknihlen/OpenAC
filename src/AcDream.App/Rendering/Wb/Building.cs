using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.Rendering.Wb;

public sealed class Building
{
    public required uint BuildingId { get; init; }

    public required HashSet<uint> EnvCellIds { get; init; }

    public required IReadOnlyList<Vector3[]> ExitPortalPolygons { get; init; }

    public bool HasPortalBounds { get; init; }

    public WbBoundingBox PortalBounds { get; init; }

    // -------------------------------------------------------------------------
    // Step 5 occlusion-query state (mutable, per-frame, RR9 scope).
    // -------------------------------------------------------------------------

    public uint QueryId;

    public bool QueryStarted;

    /// <summary>Previous-frame query result. When false, the building's interior
    /// render is skipped (Step 5 early-out in RR9 + RR11).</summary>
    public bool WasVisible;
}
