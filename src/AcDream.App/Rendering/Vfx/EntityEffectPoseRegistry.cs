using System.Numerics;
using AcDream.Core.Vfx;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Vfx;

public sealed class EntityEffectPoseRegistry :
    IEntityEffectPoseSource,
    IEntityEffectCellSource,
    IEntityEffectPoseChangeSource,
    IEntityEffectPoseLifetimeSource
{
    private sealed class PoseRecord
    {
        public Matrix4x4 RootWorld;
        public Matrix4x4[] PartLocal = Array.Empty<Matrix4x4>();
        public bool[] PartAvailable = Array.Empty<bool>();
        public uint CellId;
        public ulong LifetimeVersion;
        public ulong ChangeVersion;
    }

    private readonly Dictionary<uint, PoseRecord> _poses = new();
    private ulong _nextLifetimeVersion;

    public event Action<uint>? EffectPoseChanged;

    public int Count => _poses.Count;

    public void Publish(WorldEntity entity, IReadOnlyList<Matrix4x4> partLocal)
    {
        Publish(entity, partLocal, availability: null);
    }

    public void Publish(
        WorldEntity entity,
        IReadOnlyList<Matrix4x4> partLocal,
        IReadOnlyList<bool>? availability)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Publish(
            entity.Id,
            Matrix4x4.CreateFromQuaternion(entity.Rotation)
                * Matrix4x4.CreateTranslation(entity.Position),
            partLocal,
            entity.VisibilityCellId ?? 0u,
            availability);
    }

    /// <summary>
    /// Publish the exact indexed part transforms already installed on an
    /// entity. Appearance replacement uses this overload so a PhysicsScript
    /// delivered later in the same update observes the new parts immediately.
    /// </summary>
    public void PublishMeshRefs(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Matrix4x4 rootWorld = Matrix4x4.CreateFromQuaternion(entity.Rotation)
            * Matrix4x4.CreateTranslation(entity.Position);

        bool changed;
        if (!_poses.TryGetValue(entity.Id, out PoseRecord? record))
        {
            record = new PoseRecord { LifetimeVersion = NextLifetimeVersion() };
            _poses.Add(entity.Id, record);
            changed = true;
        }
        else
        {
            changed = record.RootWorld != rootWorld
                || record.CellId != (entity.VisibilityCellId ?? 0u);
        }

        record.RootWorld = rootWorld;
        record.CellId = entity.VisibilityCellId ?? 0u;
        if (entity.IndexedPartTransforms.Count > 0)
        {
            changed |= CopyParts(
                record,
                entity.IndexedPartTransforms,
                entity.IndexedPartAvailable);
        }
        else
        {
            if (record.PartLocal.Length != entity.MeshRefs.Count)
            {
                record.PartLocal = new Matrix4x4[entity.MeshRefs.Count];
                changed = true;
            }
            if (record.PartAvailable.Length != entity.MeshRefs.Count)
            {
                record.PartAvailable = new bool[entity.MeshRefs.Count];
                changed = true;
            }
            for (int i = 0; i < entity.MeshRefs.Count; i++)
            {
                changed |= record.PartLocal[i] != entity.MeshRefs[i].PartTransform
                    || !record.PartAvailable[i];
                record.PartLocal[i] = entity.MeshRefs[i].PartTransform;
                record.PartAvailable[i] = true;
            }
        }

        if (changed)
        {
            record.ChangeVersion++;
            EffectPoseChanged?.Invoke(entity.Id);
        }
    }

    public void Publish(
        uint localEntityId,
        Matrix4x4 rootWorld,
        IReadOnlyList<Matrix4x4> partLocal,
        uint cellId,
        IReadOnlyList<bool>? availability = null)
    {
        if (localEntityId == 0)
            return;
        ArgumentNullException.ThrowIfNull(partLocal);

        bool changed;
        if (!_poses.TryGetValue(localEntityId, out PoseRecord? record))
        {
            record = new PoseRecord { LifetimeVersion = NextLifetimeVersion() };
            _poses.Add(localEntityId, record);
            changed = true;
        }
        else
        {
            changed = record.RootWorld != rootWorld || record.CellId != cellId;
        }

        record.RootWorld = rootWorld;
        record.CellId = cellId;
        changed |= CopyParts(record, partLocal, availability);
        if (changed)
        {
            record.ChangeVersion++;
            EffectPoseChanged?.Invoke(localEntityId);
        }
    }

    public bool UpdateRoot(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!_poses.TryGetValue(entity.Id, out PoseRecord? record))
            return false;
        Matrix4x4 rootWorld = Matrix4x4.CreateFromQuaternion(entity.Rotation)
            * Matrix4x4.CreateTranslation(entity.Position);
        uint cellId = entity.VisibilityCellId ?? 0u;
        if (record.RootWorld == rootWorld && record.CellId == cellId)
            return true;

        record.RootWorld = rootWorld;
        record.CellId = cellId;
        record.ChangeVersion++;
        EffectPoseChanged?.Invoke(entity.Id);
        return true;
    }

    public bool Remove(uint localEntityId)
    {
        if (!_poses.Remove(localEntityId))
            return false;
        EffectPoseChanged?.Invoke(localEntityId);
        return true;
    }

    public void Clear()
    {
        if (_poses.Count == 0)
            return;

        uint[] removedOwners = _poses.Keys.ToArray();
        _poses.Clear();
        foreach (uint owner in removedOwners)
        {
            EffectPoseChanged?.Invoke(owner);
        }
    }

    public bool TryGetRootPose(uint localEntityId, out Matrix4x4 rootWorld)
    {
        if (_poses.TryGetValue(localEntityId, out PoseRecord? record))
        {
            rootWorld = record.RootWorld;
            return true;
        }
        rootWorld = default;
        return false;
    }

    public bool TryGetPartPose(uint localEntityId, int partIndex, out Matrix4x4 partLocal)
    {
        if (partIndex >= 0
            && _poses.TryGetValue(localEntityId, out PoseRecord? record)
            && partIndex < record.PartLocal.Length
            && record.PartAvailable[partIndex])
        {
            partLocal = record.PartLocal[partIndex];
            return true;
        }
        partLocal = default;
        return false;
    }

    public bool TryGetPartPoses(
        uint localEntityId,
        out IReadOnlyList<Matrix4x4> partLocal)
    {
        if (_poses.TryGetValue(localEntityId, out PoseRecord? record))
        {
            partLocal = record.PartLocal;
            return true;
        }
        partLocal = Array.Empty<Matrix4x4>();
        return false;
    }

    public bool TryGetPartPoseSnapshot(
        uint localEntityId,
        out IReadOnlyList<Matrix4x4> partLocal,
        out IReadOnlyList<bool> availability)
    {
        if (_poses.TryGetValue(localEntityId, out PoseRecord? record))
        {
            partLocal = record.PartLocal;
            availability = record.PartAvailable;
            return true;
        }
        partLocal = Array.Empty<Matrix4x4>();
        availability = Array.Empty<bool>();
        return false;
    }

    public bool TryGetCellId(uint localEntityId, out uint cellId)
    {
        if (_poses.TryGetValue(localEntityId, out PoseRecord? record))
        {
            cellId = record.CellId;
            return true;
        }
        cellId = 0;
        return false;
    }

    public ulong GetPoseOwnerLifetimeVersion(uint localEntityId) =>
        _poses.TryGetValue(localEntityId, out PoseRecord? record)
            ? record.LifetimeVersion
            : 0UL;

    public ulong GetPoseChangeVersion(uint localEntityId) =>
        _poses.TryGetValue(localEntityId, out PoseRecord? record)
            ? record.ChangeVersion
            : 0UL;

    private ulong NextLifetimeVersion()
    {
        _nextLifetimeVersion++;
        if (_nextLifetimeVersion == 0UL)
            _nextLifetimeVersion++;
        return _nextLifetimeVersion;
    }

    private static bool CopyParts(
        PoseRecord record,
        IReadOnlyList<Matrix4x4> partLocal,
        IReadOnlyList<bool>? availability)
    {
        if (availability is not null && availability.Count != partLocal.Count)
            throw new ArgumentException("Part pose and availability counts must match.");
        bool changed = false;
        if (record.PartLocal.Length != partLocal.Count)
        {
            record.PartLocal = new Matrix4x4[partLocal.Count];
            changed = true;
        }
        if (record.PartAvailable.Length != partLocal.Count)
        {
            record.PartAvailable = new bool[partLocal.Count];
            changed = true;
        }
        for (int i = 0; i < partLocal.Count; i++)
        {
            bool partAvailable = availability is null || availability[i];
            changed |= record.PartLocal[i] != partLocal[i]
                || record.PartAvailable[i] != partAvailable;
            record.PartLocal[i] = partLocal[i];
            record.PartAvailable[i] = partAvailable;
        }
        return changed;
    }
}

public interface IEntityEffectPoseLifetimeSource
{
    ulong GetPoseOwnerLifetimeVersion(uint localEntityId);
}
