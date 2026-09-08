namespace AcDream.Core.World;

public static class LandblockStaticEntityIdAllocator
{
    public const uint MaxCounter = 0xFFFu;

    public static uint Base(uint landblockX, uint landblockY) =>
        0xC0000000u
        | ((landblockX & 0xFFu) << 20)
        | ((landblockY & 0xFFu) << 12);

    public static uint Allocate(uint landblockX, uint landblockY, ref uint counter)
    {
        if (counter > MaxCounter)
        {
            throw new InvalidDataException(
                $"Landblock ({landblockX & 0xFFu:X2},{landblockY & 0xFFu:X2}) exceeds the 4096-entry static entity id namespace.");
        }

        return Base(landblockX, landblockY) + counter++;
    }

    public static bool IsInNamespace(uint entityId) =>
        (entityId & 0xF0000000u) == 0xC0000000u;
}
