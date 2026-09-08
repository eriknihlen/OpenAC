using System.Numerics;

namespace AcDream.Core.Physics;

public static class RetailObjectActivityGate
{
    public const float MaxPhysicsDistance = 96f;

    public static RetailObjectActivityResult Evaluate(
        RetailObjectQuantumClock clock,
        PhysicsBody? body,
        bool lifecycleEligible,
        bool hasPartArray,
        bool isStatic,
        Vector3 objectPosition,
        Vector3? playerPosition,
        double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!lifecycleEligible)
        {
            SetActive(clock, body, active: false);
            return RetailObjectActivityResult.Suspended;
        }

        if (playerPosition is null)
        {
            if (clock.IsActive)
                return RetailObjectActivityResult.Active;
            clock.Advance(elapsedSeconds);
            return RetailObjectActivityResult.Inactive;
        }

        bool withinActiveBubble = !hasPartArray
            || Vector3.Distance(objectPosition, playerPosition.Value)
                <= MaxPhysicsDistance;
        if (!withinActiveBubble)
        {
            SetActive(clock, body, active: false);
            clock.Advance(elapsedSeconds);
            return RetailObjectActivityResult.Inactive;
        }

        bool reactivated = false;
        if (!isStatic)
        {
            reactivated = clock.Activate();
            if (body is not null)
                body.TransientState |= TransientStateFlags.Active;
        }

        if (!clock.IsActive)
        {
            clock.Advance(elapsedSeconds);
            return RetailObjectActivityResult.Inactive;
        }

        return reactivated
            ? RetailObjectActivityResult.Reactivated
            : RetailObjectActivityResult.Active;
    }

    private static void SetActive(
        RetailObjectQuantumClock clock,
        PhysicsBody? body,
        bool active)
    {
        if (active)
            clock.Activate();
        else
            clock.Deactivate();

        if (body is not null && !active)
            body.TransientState &= ~TransientStateFlags.Active;
    }
}

public enum RetailObjectActivityResult
{
    Suspended,
    Inactive,
    Reactivated,
    Active,
}
