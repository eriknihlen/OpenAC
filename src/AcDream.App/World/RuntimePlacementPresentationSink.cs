using AcDream.Runtime.Physics;
using AcDream.Runtime.World;
using AcDream.App.Physics;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Plugins;
using AcDream.Core.World;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.World;

internal sealed class RuntimePlacementPresentationSink
    : IRuntimePlacementProjectionSink
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly RuntimeWorldTransitState _transit;
    private readonly WorldGameState _worldState;
    private readonly WorldEvents _worldEvents;
    private readonly EntityEffectPoseRegistry _effectPoses;
    private readonly LocalPlayerShadowSynchronizer _localPlayerShadowSync;
    private readonly Func<uint> _localPlayerGuid;
    private readonly Action<uint> _clearSelectionForUnavailableEntity;
    private readonly Action<LiveEntityRecord, bool>[] _visibilitySinks;

    public RuntimePlacementPresentationSink(
        LiveEntityRuntime liveEntities,
        RuntimeWorldTransitState transit,
        WorldGameState worldState,
        WorldEvents worldEvents,
        EntityEffectPoseRegistry effectPoses,
        LocalPlayerShadowSynchronizer localPlayerShadowSync,
        Func<uint> localPlayerGuid,
        Action<uint> clearSelectionForUnavailableEntity,
        IEnumerable<Action<LiveEntityRecord, bool>>? visibilitySinks = null)
    {
        _liveEntities = liveEntities
            ?? throw new ArgumentNullException(nameof(liveEntities));
        _transit = transit ?? throw new ArgumentNullException(nameof(transit));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _worldEvents = worldEvents ?? throw new ArgumentNullException(nameof(worldEvents));
        _effectPoses = effectPoses
            ?? throw new ArgumentNullException(nameof(effectPoses));
        _localPlayerShadowSync = localPlayerShadowSync
            ?? throw new ArgumentNullException(nameof(localPlayerShadowSync));
        _localPlayerGuid = localPlayerGuid
            ?? throw new ArgumentNullException(nameof(localPlayerGuid));
        _clearSelectionForUnavailableEntity = clearSelectionForUnavailableEntity
            ?? throw new ArgumentNullException(
                nameof(clearSelectionForUnavailableEntity));
        _visibilitySinks = visibilitySinks?.ToArray()
            ?? Array.Empty<Action<LiveEntityRecord, bool>>();
        if (_visibilitySinks.Any(static sink => sink is null))
            throw new ArgumentException(
                "Presentation visibility sinks cannot contain null.",
                nameof(visibilitySinks));
    }

    public bool TryApply(in RuntimePlacementProjectionSnapshot projection)
    {
        if (projection.Kind is RuntimePlacementProjectionKind.ExecutorCompleted)
        {
            return TryApplyInitialCreateCompletion(in projection);
        }

        if (projection.Kind
            is RuntimePlacementProjectionKind.WithdrawalRestored)
        {
            return TryApplyWithdrawalRestoration(in projection);
        }

        if (projection.Kind is RuntimePlacementProjectionKind.Place
            or RuntimePlacementProjectionKind.Withdraw
            && _liveEntities.HasActiveInitialCreateResidence(
                projection.Token.Entity))
        {
            return false;
        }

        if (projection.Kind is RuntimePlacementProjectionKind.Place
            && !_transit.IsCurrentPlacementAuthority(
                projection.Token.Portal,
                projection.Token.ExactCellId))
        {
            return true;
        }

        if (!_liveEntities.TryApplyRuntimePlacementProjection(in projection))
            return false;
        if (projection.Kind is RuntimePlacementProjectionKind.Discard)
        {
            return true;
        }
        if (!_liveEntities.TryGetRecord(
                projection.Token.Entity,
                out LiveEntityRecord record)
            || record.WorldEntity is not { } entity)
        {
            return false;
        }

        return projection.Kind switch
        {
            RuntimePlacementProjectionKind.Place =>
                TryPublishPlace(record, entity),
            RuntimePlacementProjectionKind.Withdraw =>
                TryPublishWithdrawal(record, entity),
            _ => false,
        };
    }

    private bool TryApplyInitialCreateCompletion(
        in RuntimePlacementProjectionSnapshot projection)
    {
        if (projection.Token.ExactCellId == 0u)
            return true;
        if (!_liveEntities.TryApplyInitialCreateCompletionPresentation(
                in projection))
        {
            return false;
        }
        if (!_liveEntities.TryGetRecord(
                projection.Token.Entity,
                out LiveEntityRecord record)
            || record.WorldEntity is not { } entity)
        {
            return true;
        }
        return TryPublishPlace(record, entity);
    }

    private bool TryApplyWithdrawalRestoration(
        in RuntimePlacementProjectionSnapshot projection)
    {
        if (_liveEntities.TryApplyRuntimePlacementProjection(in projection)
            && _liveEntities.TryGetRecord(
                projection.Token.Entity,
                out LiveEntityRecord record)
            && record.WorldEntity is { } entity)
        {
            _ = TryPublishPlace(record, entity);
        }
        return true;
    }

    private bool TryPublishPlace(LiveEntityRecord record, WorldEntity entity)
    {
        if (!IsCurrent(record, entity))
            return false;

        WorldEntitySnapshot snapshot = Snapshot(entity);
        _worldState.Add(snapshot);
        if (!IsCurrent(record, entity))
            return false;
        _worldEvents.UpsertCurrent(snapshot);
        if (!IsCurrent(record, entity))
            return false;
        _effectPoses.PublishMeshRefs(entity);
        if (!IsCurrent(record, entity))
            return false;

        if (record.ServerGuid == _localPlayerGuid())
        {
            _localPlayerShadowSync.SyncPose(
                entity,
                entity.Position,
                entity.Rotation,
                record.FullCellId,
                force: true);
        }

        for (int i = 0; i < _visibilitySinks.Length; i++)
        {
            _visibilitySinks[i](record, true);
            if (!IsCurrent(record, entity))
                return false;
        }
        return true;
    }

    private bool TryPublishWithdrawal(
        LiveEntityRecord record,
        WorldEntity entity)
    {
        if (!IsCurrent(record, entity))
            return false;

        for (int i = 0; i < _visibilitySinks.Length; i++)
        {
            _visibilitySinks[i](record, false);
            if (!IsCurrent(record, entity))
                return false;
        }

        _worldState.RemoveById(entity.Id);
        if (!IsCurrent(record, entity))
            return false;
        _worldEvents.ForgetEntity(entity.Id);
        if (!IsCurrent(record, entity))
            return false;
        _effectPoses.Remove(entity.Id);
        if (!IsCurrent(record, entity))
            return false;
        if (record.ServerGuid == _localPlayerGuid())
        {
            _localPlayerShadowSync.Suspend(entity);
        }
        if (!IsCurrent(record, entity))
            return false;
        _clearSelectionForUnavailableEntity(record.ServerGuid);
        return IsCurrent(record, entity);
    }

    private bool IsCurrent(LiveEntityRecord record, WorldEntity entity) =>
        _liveEntities.TryGetRecord(
            record.ProjectionKey!.Value,
            out LiveEntityRecord current)
        && ReferenceEquals(current, record)
        && ReferenceEquals(current.WorldEntity, entity);

    private static WorldEntitySnapshot Snapshot(WorldEntity entity) => new(
        entity.Id,
        entity.SourceGfxObjOrSetupId,
        entity.Position,
        entity.Rotation);
}
