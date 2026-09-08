
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.Rendering.Wb;

public struct EnvCellSceneryInstance
{
    /// <summary>GfxObj or Setup ID from DAT.</summary>
    public ulong ObjectId;

    /// <summary>Unique instance index within the landblock (WB used a 128-bit editor ID; we use uint).</summary>
    public uint InstanceId;

    /// <summary>True for multi-part Setup objects, false for simple GfxObj.</summary>
    public bool IsSetup;

    /// <summary>True if this instance is a building.</summary>
    public bool IsBuilding;

    public bool IsEntryCell;

    /// <summary>World-space position.</summary>
    public Vector3 WorldPosition;

    /// <summary>Local-space position (relative to landblock origin).</summary>
    public Vector3 LocalPosition;

    /// <summary>Rotation quaternion.</summary>
    public Quaternion Rotation;

    public uint CurrentPreviewCellId;

    /// <summary>Scale (typically uniform).</summary>
    public Vector3 Scale;

    public Matrix4x4 Transform;

    /// <summary>Local-space bounding box.</summary>
    public WbBoundingBox LocalBoundingBox;

    /// <summary>World-space bounding box.</summary>
    public WbBoundingBox BoundingBox;

    /// <summary>Rendering flags for this instance.</summary>
    public uint Flags;
}

public class EnvCellLandblock
{
    public int GridX { get; set; }

    public int GridY { get; set; }

    public object Lock { get; } = new();

    public List<EnvCellSceneryInstance> Instances { get; set; } = new();

    public Dictionary<uint, WbBoundingBox> EnvCellBounds { get; set; } = new();

    /// <summary>
    /// Set of EnvCell IDs in this landblock that have the SeenOutside flag.
    /// </summary>
    public HashSet<uint> SeenOutsideCells { get; set; } = new();

    /// <summary>
    /// Grouped transforms for each GfxObj part for static objects, for efficient instanced rendering.
    /// Key: GfxObjId, Value: List of transforms
    /// </summary>
    public Dictionary<ulong, List<InstanceData>> StaticPartGroups { get; set; } = new();

    /// <summary>
    /// Grouped transforms for each GfxObj part for buildings, for efficient instanced rendering.
    /// Key: GfxObjId, Value: List of transforms
    /// </summary>
    public Dictionary<ulong, List<InstanceData>> BuildingPartGroups { get; set; } = new();

    /// <summary>
    /// World-space bounding box of this landblock.
    /// </summary>
    public WbBoundingBox BoundingBox { get; set; }

    public WbBoundingBox TotalEnvCellBounds { get; set; }

    /// <summary>
    /// Whether instances (positions/bounding boxes) have been generated.
    /// </summary>
    public bool InstancesReady { get; set; }

    /// <summary>
    /// Whether mesh data for all instances has been prepared (CPU-side).
    /// </summary>
    public bool MeshDataReady { get; set; }

    /// <summary>
    /// Whether GPU resources have been uploaded.
    /// </summary>
    public bool GpuReady { get; set; }
}
