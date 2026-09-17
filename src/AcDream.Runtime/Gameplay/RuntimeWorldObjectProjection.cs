using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// Shared plugin <see cref="PluginWorldObject"/> projection for the object
/// table + entity directory, used verbatim by both the graphical and
/// headless hosts so classification, ownership, and navigation math cannot
/// drift between them.
/// </summary>
/// <remarks>
/// The active-enchantment list is the one piece of state a host may not
/// have: the graphical host tracks a live active-enchantment list on its
/// own automation surface and supplies it via <paramref name="activeSpellIdsForPlayer"/>
/// in <see cref="Project"/>; a caller that omits the supplier (the headless
/// host, today) gets an empty list for that field only -- every other
/// field (name, classification, ownership, position, appraisal data,
/// capacities, stack size, door-open, icon) comes from the same
/// object table and entity record both hosts already have, so it is
/// populated identically on both.
/// </remarks>
public static class RuntimeWorldObjectProjection
{
    public static PluginWorldObject Project(
        RuntimeEntityRecord? record,
        ClientObject? item,
        uint playerId,
        ClientObjectTable objects,
        Func<uint, IReadOnlyList<uint>>? activeSpellIdsForPlayer = null,
        bool remoteBodiesUnsimulated = false)
    {
        uint objectId = record?.ServerGuid ?? item!.ObjectId;
        // A host that never moves a remote entity's physics body reads that
        // entity's position from its latest snapshot instead.
        Position? source = record is null
            ? null
            : remoteBodiesUnsimulated && record.ServerGuid != playerId
                ? ConvertPosition(record.Snapshot.Position)
                : record.PhysicsBody?.CellPosition
                    ?? ConvertPosition(record.Snapshot.Position);
        bool owned = item is not null && IsPlayerOwned(item, playerId, objects);
        IReadOnlyList<uint> activeSpells = objectId == playerId
            ? activeSpellIdsForPlayer?.Invoke(playerId) ?? Array.Empty<uint>()
            : Array.Empty<uint>();
        uint publicFlags = item?.PublicWeenieBitfield ?? 0u;
        return new PluginWorldObject(
            objectId,
            item?.WeenieClassId ?? 0u,
            item?.Name ?? record?.Snapshot.Name ?? $"0x{objectId:X8}",
            ClassifyObject(item),
            (uint)(item?.Type ?? ItemType.None),
            item?.ContainerId ?? 0u,
            item?.WielderId ?? 0u)
        {
            IsOwned = owned,
            IsLandscape = source is not null
                && !owned
                && (item?.ContainerId ?? 0u) == 0u
                && (item?.WielderId ?? 0u) == 0u,
            HasPosition = source is not null,
            Position = source is { } position
                ? ProjectNavigationPosition(position)
                : default,
            HasAppraisalData = item is not null && HasPropertyData(item.Properties),
            LastIdTime = item?.LastAppraisalTimeMs ?? 0,
            // An open door stops colliding; its Open property comes with an appraisal and
            // does not follow the door opening and closing afterwards.
            IsDoorOpen = (publicFlags & (uint)PublicWeenieFlags.Door) != 0u
                && (record is not null
                    ? record.FinalPhysicsState.HasFlag(PhysicsStateFlags.Ethereal)
                    : item?.Properties.GetBool((uint)AcDream.Core.Properties.PropertyBool.Open) ?? false),
            StackSize = Math.Max(1, item?.StackSize ?? 1),
            ItemsCapacity = item?.ItemsCapacity ?? 0,
            ContainersCapacity = item?.ContainersCapacity ?? 0,
            SpellIds = item?.AppraisedSpellIds.Count > 0
                ? item.AppraisedSpellIds.ToArray()
                : Array.Empty<uint>(),
            ActiveSpellIds = activeSpells,
            IconId = item?.IconId ?? 0u,
        };
    }

    public static bool IsPlayerOwned(
        ClientObject item,
        uint playerId,
        ClientObjectTable objects)
    {
        if (item.WielderId == playerId || item.ContainerId == playerId)
            return true;
        uint parentId = item.ContainerId;
        for (int depth = 0; parentId != 0u && depth < 4; depth++)
        {
            ClientObject? parent = objects.Get(parentId);
            if (parent is null)
                return false;
            if (parent.WielderId == playerId || parent.ContainerId == playerId)
                return true;
            parentId = parent.ContainerId;
        }
        return false;
    }

    public static bool HasPropertyData(PropertyBundle properties) =>
        properties.Ints.Count != 0
        || properties.Int64s.Count != 0
        || properties.Bools.Count != 0
        || properties.Floats.Count != 0
        || properties.Strings.Count != 0
        || properties.DataIds.Count != 0
        || properties.InstanceIds.Count != 0;

    /// <summary>
    /// Retail item-type/public-weenie-bitfield classification, plus the
    /// appraised-spell-id refinement the shared classifier has no notion
    /// of: a written (Type Writable) object that carries an appraised
    /// spell id is a scroll, not a book or a plain writable.
    /// </summary>
    public static PluginObjectClass ClassifyObject(ClientObject? item)
    {
        if (item is null)
            return PluginObjectClass.Unknown;
        uint type = (uint)item.Type;
        uint flags = item.PublicWeenieBitfield ?? 0u;
        PluginObjectClass result = PluginObjectClassifier.Classify(type, flags);
        if ((type & 0x00002000u) != 0u && item.SpellId is > 0u)
            result = PluginObjectClass.Scroll;
        return result;
    }

    public static PluginNavigationPosition ProjectNavigationPosition(
        Position position)
    {
        uint cellId = position.ObjCellId;
        uint blockX = (cellId >> 24) & 0xFFu;
        uint blockY = (cellId >> 16) & 0xFFu;
        System.Numerics.Vector3 local = position.Frame.Origin;
        return new PluginNavigationPosition(
            cellId,
            (((double)blockX - 127d) * 192d + local.X - 84d) / 240d,
            (((double)blockY - 127d) * 192d + local.Y - 84d) / 240d,
            local.Z / 240d,
            MoveToMath.GetHeading(position.Frame.Orientation),
            (cellId & 0xFFFFu) is >= 1u and <= 0x40u);
    }

    public static Position? ConvertPosition(
        AcDream.Core.Net.Messages.CreateObject.ServerPosition? position) =>
        position is not { } value
            ? null
            : new Position(
                value.LandblockId,
                new CellFrame(
                    new System.Numerics.Vector3(
                        value.PositionX,
                        value.PositionY,
                        value.PositionZ),
                    new System.Numerics.Quaternion(
                        value.RotationX,
                        value.RotationY,
                        value.RotationZ,
                        value.RotationW)));
}
