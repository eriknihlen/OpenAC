using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using System.Numerics;

namespace AcDream.Runtime.Entities;

internal sealed class RuntimeEntityObjectViews
{
    public RuntimeEntityObjectViews(
        RuntimeEntityDirectory entities,
        ClientObjectTable objects)
    {
        Entities = new EntityView(
            entities ?? throw new ArgumentNullException(nameof(entities)));
        Inventory = new InventoryView(
            entities,
            objects ?? throw new ArgumentNullException(nameof(objects)));
    }

    public IRuntimeEntityView Entities { get; }
    public IRuntimeInventoryView Inventory { get; }

    internal static RuntimeEntitySnapshot Snapshot(RuntimeEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        uint localEntityId = record.LocalEntityId
            ?? throw new InvalidOperationException(
                $"Canonical entity 0x{record.ServerGuid:X8}/{record.Incarnation} has no Runtime identity.");
        return new RuntimeEntitySnapshot(
            new RuntimeEntityIdentity(
                record.ServerGuid,
                localEntityId,
                record.Incarnation),
            record.FullCellId,
            (uint)record.FinalPhysicsState,
            ConvertPosition(record.Snapshot.Position));
    }

    private static Position? ConvertPosition(
        CreateObject.ServerPosition? position) =>
        position is not { } value
            ? null
            : new Position(
                value.LandblockId,
                new Vector3(
                    value.PositionX,
                    value.PositionY,
                    value.PositionZ),
                new Quaternion(
                    value.RotationX,
                    value.RotationY,
                    value.RotationZ,
                    value.RotationW));

    internal static RuntimeInventoryItemSnapshot Snapshot(
        ClientObject item,
        RuntimeEntityDirectory entities,
        ushort? exactGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ushort incarnation = exactGeneration
            ?? (entities.TryGetActive(
                    item.ObjectId,
                    out RuntimeEntityRecord canonical)
                ? canonical.Incarnation
                : (ushort)0);
        return new RuntimeInventoryItemSnapshot(
            item.ObjectId,
            incarnation,
            item.Name,
            item.ContainerId,
            item.ContainerSlot,
            item.WielderId,
            (uint)item.CurrentlyEquippedLocation,
            item.StackSize,
            item.Value);
    }

    private sealed class EntityView(RuntimeEntityDirectory owner)
        : IRuntimeEntityView
    {
        public int Count => owner.Count;
        public int MaterializedCount => owner.ClaimedLocalIdCount;

        public bool TryGet(
            uint serverGuid,
            out RuntimeEntitySnapshot entity)
        {
            using RuntimeEntityDirectory.ActiveReadLease lease =
                owner.AcquireActiveRead();
            if (owner.TryGetActive(
                    serverGuid,
                    out RuntimeEntityRecord record))
            {
                entity = Snapshot(record);
                return true;
            }

            entity = default;
            return false;
        }

        public void Visit(IRuntimeEntityVisitor visitor)
        {
            ArgumentNullException.ThrowIfNull(visitor);
            using RuntimeEntityDirectory.ActiveReadLease lease =
                owner.AcquireActiveRead();
            foreach (RuntimeEntityRecord record in owner.ActiveRecords)
            {
                RuntimeEntitySnapshot entity = Snapshot(record);
                visitor.Visit(in entity);
            }
        }
    }

    private sealed class InventoryView(
        RuntimeEntityDirectory entities,
        ClientObjectTable owner)
        : IRuntimeInventoryView
    {
        public int ObjectCount => owner.ObjectCount;
        public int ContainerCount => owner.ContainerCount;

        public bool TryGet(
            uint objectId,
            out RuntimeInventoryItemSnapshot item)
        {
            ClientObject? found = owner.Get(objectId);
            if (found is not null)
            {
                item = Snapshot(found, entities);
                return true;
            }

            item = default;
            return false;
        }

        public void Visit(IRuntimeInventoryVisitor visitor)
        {
            ArgumentNullException.ThrowIfNull(visitor);
            foreach (ClientObject item in owner.Objects)
            {
                RuntimeInventoryItemSnapshot snapshot = Snapshot(
                    item,
                    entities);
                visitor.Visit(in snapshot);
            }
        }
    }
}
