namespace AcDream.Core.Physics;

public static class CollisionExemption
{
    private const uint ETHEREAL_PS         = 0x4u;
    private const uint IGNORE_COLLISIONS_PS = 0x10u;

    public static bool ShouldSkip(uint targetState, EntityCollisionFlags targetFlags,
                                   ObjectInfoState moverState)
    {
        if ((targetState & ETHEREAL_PS) != 0 && (targetState & IGNORE_COLLISIONS_PS) != 0)
            return true;

        bool moverIsViewer = (moverState & ObjectInfoState.IsViewer) != 0;
        bool targetIsCreature = (targetFlags & EntityCollisionFlags.IsCreature) != 0;
        if (moverIsViewer && targetIsCreature)
            return true;

        bool moverIgnoresCreatures = (moverState & ObjectInfoState.IgnoreCreatures) != 0;
        if (moverIgnoresCreatures && targetIsCreature)
            return true;

        bool moverIsPlayer = (moverState & ObjectInfoState.IsPlayer) != 0;
        bool targetIsPlayer = (targetFlags & EntityCollisionFlags.IsPlayer) != 0;
        if (moverIsPlayer && targetIsPlayer)
        {
            bool collide = false;

            if ((moverState & ObjectInfoState.IsImpenetrable) != 0)
                collide = true;
            if (!collide && (targetFlags & EntityCollisionFlags.IsImpenetrable) != 0)
                collide = true;

            if (!collide
                && (moverState & ObjectInfoState.IsPK) != 0
                && (targetFlags & EntityCollisionFlags.IsPK) != 0)
            {
                collide = true;
            }

            if (!collide
                && (moverState & ObjectInfoState.IsPKLite) != 0
                && (targetFlags & EntityCollisionFlags.IsPKLite) != 0)
            {
                collide = true;
            }

            if (!collide)
                return true; // exempt — non-PK pair walks through
        }

        return false; // proceed to broad-phase + shape dispatch
    }
}
