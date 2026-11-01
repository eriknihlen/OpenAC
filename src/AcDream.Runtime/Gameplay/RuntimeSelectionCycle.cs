using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Selection;
using AcDream.Core.Ui;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Gameplay;

/// <summary>Which way round a cycle steps through its candidates.</summary>
public enum RuntimeSelectionCycleDirection
{
    /// <summary>The nearest candidate, wherever the selection is now.</summary>
    Closest,

    /// <summary>The candidate just inside the current one.</summary>
    Previous,

    /// <summary>The candidate just outside the current one.</summary>
    Next,
}

/// <summary>
/// Stepping the selection from one nearby character to the next.
/// </summary>
/// <remarks>
/// The order is the one the radar uses: candidates sorted by how far away
/// they are, measured in the character's own frame with height counting for a
/// little more than distance across the ground, and the object id breaking a
/// tie so the order never depends on which order the client heard about
/// things. "Previous" and "next" step in and out along that order and wrap
/// round at the ends. Who is in reach at all is decided on ground distance
/// alone, against the radar's range: height orders, it never excludes.
///
/// Everything it reads -- who is nearby, where they are, what the server
/// said about them -- is runtime state, so a client with a window and a bot
/// step through the same characters in the same order.
/// </remarks>
public sealed class RuntimeSelectionCycle
{
    private readonly GameRuntime _runtime;

    internal RuntimeSelectionCycle(GameRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>
    /// Goes back to whatever was selected before. Answers whether anything
    /// changed.
    /// </summary>
    public bool SelectPreviousSelection() =>
        _runtime.ActionOwner.Selection.SelectPrevious();

    /// <summary>
    /// Steps the selection to another nearby character. Answers whether one
    /// was found and selected.
    /// </summary>
    public bool SelectPlayer(RuntimeSelectionCycleDirection direction)
    {
        SelectionState selection = _runtime.ActionOwner.Selection;
        uint? anchor = selection.SelectedObjectId ?? selection.PreviousObjectId;
        if (FindPlayer(direction, anchor) is not { } found)
            return false;
        return selection.Select(found, SelectionChangeSource.Keyboard);
    }

    /// <summary>
    /// The character the given step lands on, or nothing when there is none
    /// within radar range.
    /// </summary>
    public uint? FindPlayer(
        RuntimeSelectionCycleDirection direction,
        uint? anchor)
    {
        uint playerGuid = _runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !_runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || !TryGetPose(playerRecord, out Vector3 playerPosition, out _))
        {
            return null;
        }

        Quaternion playerOrientation = LocalPlayerOrientation(playerRecord);
        float radarRadius = RetailRadar.GetRangeMeters(
            IsOutdoorCell(playerRecord.FullCellId));
        ClientObjectTable objects = _runtime.InventoryOwner.Objects;
        var candidates = new List<(uint Guid, float Order)>();
        foreach (RuntimeEntityRecord record
            in _runtime.EntityObjects.Entities.ActiveRecords)
        {
            uint guid = record.ServerGuid;
            if (guid == 0u
                || guid == playerGuid
                || objects.Get(guid) is not { } candidate
                || !TryGetPose(record, out Vector3 at, out _))
            {
                continue;
            }

            // Reach is ground distance alone; height only orders the
            // candidates, so someone on the floor below is still in the cycle.
            Vector3 offset = PlayerSpaceOffset(
                playerPosition, playerOrientation, at);
            if (!RetailRadar.IsWithinSelectionCycleRange(offset, radarRadius)
                || !IsSelectableCharacter(candidate, record.FinalPhysicsState))
            {
                continue;
            }
            candidates.Add((guid, RetailRadar.GetSelectionOrder(offset)));
        }

        if (candidates.Count == 0)
            return null;
        candidates.Sort(static (left, right) =>
            Compare(left.Order, left.Guid, right.Order, right.Guid));

        if (direction == RuntimeSelectionCycleDirection.Closest)
            return candidates[0].Guid;

        (float Order, uint Guid)? anchorKey = null;
        if (anchor is { } anchorGuid
            && _runtime.EntityObjects.Entities.TryGetActive(
                anchorGuid,
                out RuntimeEntityRecord anchorRecord)
            && TryGetPose(anchorRecord, out Vector3 anchorAt, out _))
        {
            anchorKey = (
                RetailRadar.GetSelectionOrder(PlayerSpaceOffset(
                    playerPosition, playerOrientation, anchorAt)),
                anchorGuid);
        }

        if (anchorKey is null)
        {
            return direction == RuntimeSelectionCycleDirection.Previous
                ? candidates[^1].Guid
                : candidates[0].Guid;
        }

        if (direction == RuntimeSelectionCycleDirection.Next)
        {
            foreach ((uint guid, float order) in candidates)
            {
                if (Compare(
                        order,
                        guid,
                        anchorKey.Value.Order,
                        anchorKey.Value.Guid) > 0)
                {
                    return guid;
                }
            }
            return candidates[0].Guid;
        }

        for (int index = candidates.Count - 1; index >= 0; index--)
        {
            (uint guid, float order) = candidates[index];
            if (Compare(
                    order,
                    guid,
                    anchorKey.Value.Order,
                    anchorKey.Value.Guid) < 0)
            {
                return guid;
            }
        }
        return candidates[^1].Guid;
    }

    /// <summary>
    /// Another person, shown on the radar, out in the world rather than in
    /// somebody's pack, and not hidden (cloaked, or hidden from the UI).
    /// </summary>
    private static bool IsSelectableCharacter(
        ClientObject candidate,
        PhysicsStateFlags physicsState)
    {
        var flags = (PublicWeenieFlags)(candidate.PublicWeenieBitfield ?? 0u);
        if (candidate.ContainerId != 0u
            || SelectionEligibility.IsHiddenFromSelection(
                physicsState, candidate.PublicWeenieBitfield))
        {
            return false;
        }

        return candidate.RadarBehavior is { } behavior
            && RetailRadar.IsShowable(
                (RadarBehavior)behavior, hasPhysicsObject: true)
            && (flags & PublicWeenieFlags.Player) != 0;
    }

    /// <summary>
    /// Where something stands relative to the character, in the character's
    /// own frame, in metres.
    /// </summary>
    private static Vector3 PlayerSpaceOffset(
        Vector3 playerPosition,
        Quaternion playerOrientation,
        Vector3 targetPosition)
        => Vector3.Transform(
            targetPosition - playerPosition,
            Quaternion.Inverse(playerOrientation));

    private static int Compare(
        float leftOrder,
        uint leftGuid,
        float rightOrder,
        uint rightGuid)
    {
        int order = leftOrder.CompareTo(rightOrder);
        return order != 0 ? order : leftGuid.CompareTo(rightGuid);
    }

    /// <summary>
    /// An outdoor cell: the radar reaches three times as far outside as it
    /// does inside a building.
    /// </summary>
    private static bool IsOutdoorCell(uint cellId)
        => cellId == 0u || (cellId & 0xFFFFu) < 0x100u;

    /// <summary>Where something stands, in metres.</summary>
    private static bool TryGetPose(
        RuntimeEntityRecord record,
        out Vector3 position,
        out Quaternion orientation)
    {
        orientation = Quaternion.Identity;
        position = default;
        if (record.Snapshot.Position is not { } snapshot
            || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                record, out position))
        {
            return false;
        }

        orientation = new Quaternion(
            snapshot.RotationX,
            snapshot.RotationY,
            snapshot.RotationZ,
            snapshot.RotationW);
        return true;
    }

    /// <summary>
    /// Which way the character faces. Whoever is driving its body knows
    /// better than the last thing the server said, and every client reads it
    /// from the same place.
    /// </summary>
    private Quaternion LocalPlayerOrientation(RuntimeEntityRecord record)
    {
        RuntimeMovementSnapshot movement = _runtime.Movement.Snapshot;
        if (movement.HasController && movement.Position.ObjCellId != 0u)
            return movement.Position.Frame.Orientation;
        return TryGetPose(record, out _, out Quaternion orientation)
            ? orientation
            : Quaternion.Identity;
    }
}
