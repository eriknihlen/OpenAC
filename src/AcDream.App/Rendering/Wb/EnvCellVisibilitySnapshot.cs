using System.Collections.Generic;

namespace AcDream.App.Rendering.Wb;

public sealed class EnvCellVisibilitySnapshot
{
    /// <summary>Landblocks fully or partially inside the frustum at prepare time.</summary>
    public List<EnvCellLandblock> VisibleLandblocks { get; init; } = new();

    public Dictionary<uint, Dictionary<ulong, List<InstanceData>>> BatchedByCell { get; init; } = new();

    public int PostPreparePoolIndex { get; init; }

    public bool IsEmpty => VisibleLandblocks.Count == 0 && BatchedByCell.Count == 0;
}
