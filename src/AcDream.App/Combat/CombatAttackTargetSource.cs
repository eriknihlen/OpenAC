using System.Numerics;
using AcDream.App.Input;
using AcDream.App.World;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.World;

namespace AcDream.App.Combat;

internal sealed class CombatAttackTargetSource : ICombatAttackTargetSource
{
    private readonly SelectionState _selection;
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ClientObjectTable _objects;
    private readonly ILocalPlayerIdentitySource _player;

    public CombatAttackTargetSource(
        SelectionState selection,
        LiveEntityRuntime liveEntities,
        ClientObjectTable objects,
        ILocalPlayerIdentitySource player)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _liveEntities = liveEntities
            ?? throw new ArgumentNullException(nameof(liveEntities));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _player = player ?? throw new ArgumentNullException(nameof(player));
    }

    public uint? SelectedObjectId => _selection.SelectedObjectId;

    public uint? GetSelectedOrClosestCombatTarget(bool autoTarget)
    {
        if (_selection.SelectedObjectId is { } selected
            && IsAttackableExplicitTarget(selected))
        {
            return selected;
        }

        if (!autoTarget)
            return null;

        (uint Guid, float DistanceSquared)? closest = FindClosestHostileMonster();
        if (closest is not { } best)
        {
            _selection.Clear(SelectionChangeSource.Keyboard);
            return null;
        }

        _selection.Select(best.Guid, SelectionChangeSource.Keyboard);
        string? name = _objects.Get(best.Guid)?.Name;
        string label = string.IsNullOrWhiteSpace(name)
            ? $"0x{best.Guid:X8}"
            : name;
        Console.WriteLine(
            $"combat: selected target 0x{best.Guid:X8} {label} dist={MathF.Sqrt(best.DistanceSquared):F1}");
        return best.Guid;
    }

    private (uint Guid, float DistanceSquared)? FindClosestHostileMonster()
    {
        if (!_liveEntities.TryGetWorldEntity(
                _player.ServerGuid,
                out WorldEntity playerEntity))
        {
            return null;
        }

        (uint Guid, float DistanceSquared)? best = null;
        foreach (LiveEntityRecord record in _liveEntities.VisibleRecords)
        {
            uint guid = record.ServerGuid;
            WorldEntity entity = record.WorldEntity!;
            if (!IsHostileMonster(guid))
                continue;

            float distanceSquared = Vector3.DistanceSquared(
                entity.Position,
                playerEntity.Position);
            if (best is null || distanceSquared < best.Value.DistanceSquared)
                best = (guid, distanceSquared);
        }
        return best;
    }

    private bool IsHostileMonster(uint serverGuid)
    {
        if (!TryGetLiveCombatCandidate(serverGuid, out ClientObject? candidate))
            return false;

        uint playerGuid = _player.ServerGuid;
        return (candidate.Type & ItemType.Creature) != 0
            && CombatTargetPolicy.IsHostileMonster(
                playerGuid,
                _objects.Get(playerGuid),
                candidate);
    }

    private bool IsAttackableExplicitTarget(uint serverGuid)
    {
        if (!TryGetLiveCombatCandidate(serverGuid, out ClientObject? candidate))
            return false;

        uint playerGuid = _player.ServerGuid;
        return SelectedObjectHealthPolicy.ObjectIsAttackable(
            playerGuid,
            _objects.Get(playerGuid),
            serverGuid,
            candidate);
    }

    private bool TryGetLiveCombatCandidate(
        uint serverGuid,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ClientObject? candidate)
    {
        candidate = null;
        uint playerGuid = _player.ServerGuid;
        if (serverGuid == playerGuid
            || !_liveEntities.TryGetInteractionEligibleRecord(
                serverGuid,
                out LiveEntityRecord record)
            || record.WorldEntity is not { } entity)
        {
            return false;
        }

        if (_liveEntities.TryGetAnimationRuntime(entity.Id, out var animation)
            && animation.CurrentMotion == MotionCommand.Dead)
        {
            return false;
        }

        candidate = _objects.Get(serverGuid);
        return candidate is not null;
    }
}
