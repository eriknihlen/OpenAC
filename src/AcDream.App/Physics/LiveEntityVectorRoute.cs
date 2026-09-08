namespace AcDream.App.Physics;

internal enum LiveEntityVectorRoute
{
    Projectile,
    CanonicalBody,
    OrdinaryRemote,
}

internal static class LiveEntityVectorRouter
{
    internal static LiveEntityVectorRoute Route(
        Func<bool> tryProjectile,
        Func<bool> tryCanonicalBody,
        Action applyOrdinaryRemote)
    {
        ArgumentNullException.ThrowIfNull(tryProjectile);
        ArgumentNullException.ThrowIfNull(tryCanonicalBody);
        ArgumentNullException.ThrowIfNull(applyOrdinaryRemote);

        if (tryProjectile())
            return LiveEntityVectorRoute.Projectile;
        if (tryCanonicalBody())
            return LiveEntityVectorRoute.CanonicalBody;

        applyOrdinaryRemote();
        return LiveEntityVectorRoute.OrdinaryRemote;
    }
}
