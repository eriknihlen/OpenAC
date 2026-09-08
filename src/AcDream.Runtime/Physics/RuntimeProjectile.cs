using AcDream.Core.Physics;

namespace AcDream.Runtime.Physics;

public interface IRuntimeProjectile
{
    PhysicsBody Body { get; }
    ProjectileCollisionSphere CollisionSphere { get; }
    ulong PredictionAuthorityVersion { get; }
}

internal sealed class RuntimeProjectile : IRuntimeProjectile
{
    internal RuntimeProjectile(
        PhysicsBody body,
        ProjectileCollisionSphere collisionSphere)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
        if (!collisionSphere.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(collisionSphere),
                "A Runtime projectile requires one valid prepared Setup sphere.");
        }

        CollisionSphere = collisionSphere;
    }

    public PhysicsBody Body { get; }
    public ProjectileCollisionSphere CollisionSphere { get; }
    public ulong PredictionAuthorityVersion { get; private set; }

    internal void InvalidatePrediction() =>
        PredictionAuthorityVersion++;
}
