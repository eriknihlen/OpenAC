namespace AcDream.Core.Physics;

public sealed class RemoteWeenie : IWeenieObject
{
    public bool InqRunRate(out float rate)
    {
        rate = 0f;
        return false;
    }

    public bool InqJumpVelocity(float extent, out float vz)
    {
        vz = 0f;
        return false;
    }

    /// <summary>Remotes never locally initiate jumps.</summary>
    public bool CanJump(float extent) => true;
}
