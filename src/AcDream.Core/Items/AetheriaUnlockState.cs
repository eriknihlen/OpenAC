using System;

namespace AcDream.Core.Items;

[Flags]
public enum AetheriaUnlockState : uint
{
    None   = 0x0,
    Blue   = 0x1,
    Yellow = 0x2,
    Red    = 0x4,
    All    = Blue | Yellow | Red,
}

public static class AetheriaUnlocks
{
    public const uint PropertyId = 0x142u;

    public static AetheriaUnlockState Read(ClientObject? player)
    {
        if (player?.Properties.Ints.TryGetValue(PropertyId, out int value) != true)
            return AetheriaUnlockState.None;
        return (AetheriaUnlockState)(uint)value;
    }
}
