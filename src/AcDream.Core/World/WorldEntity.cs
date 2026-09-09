using System.Numerics;

namespace AcDream.Core.World;

public sealed class WorldEntity
{
    private PaletteOverride? _paletteOverride;
    private IReadOnlyList<PartOverride> _partOverrides = Array.Empty<PartOverride>();

    public required uint Id { get; init; }
    public uint ServerGuid { get; init; }
    public required uint SourceGfxObjOrSetupId { get; init; }
    public required Vector3 Position { get; set; }
    public required Quaternion Rotation { get; set; }
    public required IReadOnlyList<MeshRef> MeshRefs { get; set; }

    public bool IsDrawVisible { get; set; } = true;

    public bool IsAncestorDrawVisible { get; set; } = true;

    public IReadOnlyList<Matrix4x4> IndexedPartTransforms { get; private set; } =
        Array.Empty<Matrix4x4>();

    public IReadOnlyList<bool> IndexedPartAvailable { get; private set; } =
        Array.Empty<bool>();

    public void SetIndexedPartPoses(
        IReadOnlyList<Matrix4x4> transforms,
        IReadOnlyList<bool> available)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        ArgumentNullException.ThrowIfNull(available);
        if (transforms.Count != available.Count)
            throw new ArgumentException("Indexed part pose and availability counts must match.");
        IndexedPartTransforms = transforms;
        IndexedPartAvailable = available;
    }

    public PaletteOverride? PaletteOverride
    {
        get => _paletteOverride;
        init => _paletteOverride = value;
    }

    public uint? ParentCellId { get; set; }

    public uint? EffectCellId { get; set; }

    public uint? VisibilityCellId => ParentCellId ?? EffectCellId;

    public bool IsBuildingShell { get; init; }

    public uint? BuildingShellAnchorCellId { get; init; }

    public float Scale { get; init; } = 1.0f;

    public IReadOnlyList<PartOverride> PartOverrides
    {
        get => _partOverrides;
        init => _partOverrides = value;
    }

    public void ApplyAppearance(
        IReadOnlyList<MeshRef> meshRefs,
        PaletteOverride? paletteOverride,
        IReadOnlyList<PartOverride> partOverrides)
    {
        ArgumentNullException.ThrowIfNull(meshRefs);
        ArgumentNullException.ThrowIfNull(partOverrides);

        MeshRefs = meshRefs;
        _paletteOverride = paletteOverride;
        _partOverrides = partOverrides;
    }

    public ulong HiddenPartsMask { get; init; }

    public Vector3 AabbMin { get; private set; }
    public Vector3 AabbMax { get; private set; }
    public bool AabbDirty { get; private set; } = true;

    public Vector3 LocalBoundMin { get; private set; }
    public Vector3 LocalBoundMax { get; private set; }
    public bool HasLocalBounds { get; private set; }

    public void SetLocalBounds(Vector3 min, Vector3 max)
    {
        LocalBoundMin = min;
        LocalBoundMax = max;
        HasLocalBounds = true;
        AabbDirty = true;
    }

    private const float DefaultAabbRadius = 5.0f;

    public void RefreshAabb()
    {
        var p = Position;

        if (HasLocalBounds)
        {
            Vector3 lo = LocalBoundMin, hi = LocalBoundMax;
            var rot = Rotation;
            Vector3 min = default, max = default;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? lo.X : hi.X,
                    (c & 2) == 0 ? lo.Y : hi.Y,
                    (c & 4) == 0 ? lo.Z : hi.Z);
                var t = Vector3.Transform(corner, rot);
                if (c == 0) { min = max = t; }
                else { min = Vector3.Min(min, t); max = Vector3.Max(max, t); }
            }
            AabbMin = p + min - new Vector3(DefaultAabbRadius);
            AabbMax = p + max + new Vector3(DefaultAabbRadius);
            AabbDirty = false;
            return;
        }

        float radius = DefaultAabbRadius;
        var refs = MeshRefs;
        if (refs is not null)
        {
            float maxOffset = 0f;
            for (int i = 0; i < refs.Count; i++)
            {
                float len = refs[i].PartTransform.Translation.Length();
                if (len > maxOffset) maxOffset = len;
            }
            radius += maxOffset;
        }

        AabbMin = new Vector3(p.X - radius, p.Y - radius, p.Z - radius);
        AabbMax = new Vector3(p.X + radius, p.Y + radius, p.Z + radius);
        AabbDirty = false;
    }

    public void SetPosition(Vector3 pos)
    {
        Position = pos;
        AabbDirty = true;
    }
}

public readonly record struct PartOverride(byte PartIndex, uint GfxObjId);
