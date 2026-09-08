using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Entities;

internal static class RuntimeFirstEntryAcknowledgement
{
    internal static bool IsStillPending(
        RuntimeInitialCreateResidenceState residences,
        RuntimeSetPositionState setPosition,
        RuntimeEntityRecord record,
        in RuntimeInitialCreateResidenceToken residenceToken,
        in RuntimePlacementProjectionToken expected)
    {
        if (!residences.TryGetCurrent(
                record,
                out RuntimeInitialCreateResidenceLease lease)
            || lease.Token != residenceToken)
        {
            return false;
        }

        if (setPosition.TryPeekProjection(
                out RuntimePlacementProjectionSnapshot head)
            && head.Token.Entity == expected.Entity
            && (head.Kind is not RuntimePlacementProjectionKind.Place
                || head.Token != expected))
        {
            return false;
        }

        return true;
    }
}
