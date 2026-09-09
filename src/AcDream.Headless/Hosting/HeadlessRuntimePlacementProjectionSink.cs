using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.World;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessRuntimePlacementProjectionSink
    : IRuntimePlacementProjectionSink
{
    private readonly GameRuntime _runtime;

    internal HeadlessRuntimePlacementProjectionSink(GameRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public bool TryApply(
        in RuntimePlacementProjectionSnapshot projection)
    {
        if (projection.Kind is RuntimePlacementProjectionKind.Discard)
        {
            return true;
        }

        if (projection.Kind is RuntimePlacementProjectionKind.ExecutorCompleted)
        {
            return true;
        }

        if (projection.Kind
            is RuntimePlacementProjectionKind.WithdrawalRestored)
        {
            return true;
        }

        RuntimePlacementProjectionToken token = projection.Token;
        RuntimeEntityDirectory directory = _runtime.EntityObjects.Entities;
        if (projection.Kind is RuntimePlacementProjectionKind.Place
            or RuntimePlacementProjectionKind.Withdraw
            && token.IsValid
            && directory.TryGetByLocalId(
                token.Entity.LocalEntityId,
                out RuntimeEntityRecord residenceCandidate)
            && directory.IsCurrent(residenceCandidate)
            && residenceCandidate.Key == token.Entity
            && _runtime.EntityObjects.TryGetInitialCreateResidence(
                residenceCandidate,
                out _))
        {
            return false;
        }
        if (!token.IsValid
            || token.SessionLifetimeVersion
                != directory.SessionLifetimeVersion
            || !directory.TryGetByLocalId(
                token.Entity.LocalEntityId,
                out RuntimeEntityRecord record)
            || !directory.IsCurrent(record)
            || record.Key != token.Entity
            || !HasValidPortalShape(token))
        {
            return false;
        }

        if (projection.Kind is RuntimePlacementProjectionKind.Withdraw)
            return true;
        if (projection.Kind is not RuntimePlacementProjectionKind.Place)
            return false;

        if (record.PositionAuthorityVersion != token.PositionAuthorityVersion
            || record.SpatialAuthorityVersion != token.SpatialAuthorityVersion
            || record.PlacementCommitVersion != token.PlacementCommitVersion
            || record.FullCellId != token.ExactCellId)
        {
            return false;
        }

        if (!_runtime.TransitOwner.IsCurrentPlacementAuthority(
                token.Portal,
                token.ExactCellId))
        {
            return true;
        }

        return true;
    }

    private static bool HasValidPortalShape(
        in RuntimePlacementProjectionToken token)
    {
        RuntimePortalPlacementAuthority portal = token.Portal;
        if (!portal.Present)
            return portal.IsEmpty;

        return portal.IsValid
            && portal.Projection.DestinationCell == token.ExactCellId;
    }
}
