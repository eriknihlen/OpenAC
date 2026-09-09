using AcDream.App.Physics;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;

namespace AcDream.App.World;

internal sealed class LiveEntityProjectionWithdrawalController
{
    private readonly LiveEntityRuntime _runtime;
    private readonly ProjectileController _projectiles;
    private readonly WorldGameState _worldState;
    private readonly WorldEvents _worldEvents;
    private readonly ShadowObjectRegistry _shadows;
    private readonly EntityEffectPoseRegistry _effectPoses;
    private readonly LocalPlayerShadowState _localPlayerShadow;

    public LiveEntityProjectionWithdrawalController(
        LiveEntityRuntime runtime,
        ProjectileController projectiles,
        WorldGameState worldState,
        WorldEvents worldEvents,
        ShadowObjectRegistry shadows,
        EntityEffectPoseRegistry effectPoses,
        LocalPlayerShadowState localPlayerShadow)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _projectiles = projectiles ?? throw new ArgumentNullException(nameof(projectiles));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _worldEvents = worldEvents ?? throw new ArgumentNullException(nameof(worldEvents));
        _shadows = shadows ?? throw new ArgumentNullException(nameof(shadows));
        _effectPoses = effectPoses ?? throw new ArgumentNullException(nameof(effectPoses));
        _localPlayerShadow = localPlayerShadow
            ?? throw new ArgumentNullException(nameof(localPlayerShadow));
    }

    public bool Withdraw(uint serverGuid, uint localPlayerGuid)
    {
        if (!_runtime.TryGetRecord(serverGuid, out LiveEntityRecord record)
            || record.WorldEntity is null)
        {
            return false;
        }

        return Withdraw(record, localPlayerGuid);
    }

    public bool Withdraw(LiveEntityRecord record, uint localPlayerGuid)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_runtime.IsCurrentRecord(record) || record.WorldEntity is null)
            return false;

        return Withdraw(
            record,
            record.PositionAuthorityVersion,
            record.ProjectionMutationVersion,
            localPlayerGuid);
    }

    public bool Withdraw(
        LiveEntityRecord record,
        ulong positionAuthorityVersion,
        ulong projectionMutationVersion,
        uint localPlayerGuid)
    {
        ExactProjectionWithdrawalOutcome outcome = WithdrawExact(
            record,
            positionAuthorityVersion,
            projectionMutationVersion,
            localPlayerGuid);
        if (outcome.Failure is not null)
            throw outcome.Failure;
        return outcome.Disposition is ExactProjectionWithdrawalDisposition.Completed;
    }

    public ExactProjectionWithdrawalOutcome WithdrawExact(
        LiveEntityRecord record,
        ulong positionAuthorityVersion,
        ulong projectionMutationVersion,
        uint localPlayerGuid)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_runtime.IsCurrentRecord(record)
            || record.WorldEntity is null
            || record.PositionAuthorityVersion != positionAuthorityVersion
            || record.ProjectionMutationVersion != projectionMutationVersion)
        {
            return new(
                ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        }

        try
        {
            LeaveWorld(record, localPlayerGuid);
            bool completed = _runtime.WithdrawLiveEntityProjection(
                record,
                positionAuthorityVersion,
                projectionMutationVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        }
        catch (Exception error)
        {
            bool current = _runtime.IsCurrentRecord(record);
            ExactProjectionWithdrawalDisposition disposition =
                current
                && record.PositionAuthorityVersion == positionAuthorityVersion
                && record.ProjectionMutationVersion == projectionMutationVersion
                && record.IsSpatiallyProjected
                    ? ExactProjectionWithdrawalDisposition.Pending
                    : current
                        && record.PositionAuthorityVersion == positionAuthorityVersion
                        && !record.IsSpatiallyProjected
                            ? ExactProjectionWithdrawalDisposition.Completed
                            : ExactProjectionWithdrawalDisposition.Superseded;
            return new(disposition, error);
        }
    }

    public void LeaveWorld(LiveEntityRecord record, uint localPlayerGuid)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.WorldEntity is not { } entity)
            return;

        bool retainedProjectileShadow = _projectiles.LeaveWorld(record);
        if (!retainedProjectileShadow)
            _shadows.Suspend(entity.Id);
        _worldState.RemoveById(entity.Id);
        _worldEvents.ForgetEntity(entity.Id);
        if (record.ServerGuid == localPlayerGuid
            && (!_runtime.TryGetRecord(record.ServerGuid, out LiveEntityRecord current)
                || ReferenceEquals(current, record)))
        {
            _localPlayerShadow.Clear();
        }

        _effectPoses.Remove(entity.Id);
    }
}

public enum ExactProjectionWithdrawalDisposition
{
    Completed,
    Pending,
    Superseded,
}

public readonly record struct ExactProjectionWithdrawalOutcome(
    ExactProjectionWithdrawalDisposition Disposition,
    Exception? Failure);
