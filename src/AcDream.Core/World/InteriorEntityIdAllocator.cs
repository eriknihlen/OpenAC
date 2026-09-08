namespace AcDream.Core.World;

public static class InteriorEntityIdAllocator
{
    public const uint MaxCounter = 0xFFFu;

    public static uint Base(uint landblockX, uint landblockY)
        => 0x40000000u | ((landblockX & 0xFFu) << 20) | ((landblockY & 0xFFu) << 12);

    public static uint Allocate(uint landblockX, uint landblockY, ref uint counter)
    {
        if (counter > MaxCounter)
            throw new InvalidDataException(
                $"Landblock ({landblockX & 0xFFu:X2},{landblockY & 0xFFu:X2}) exceeds the 4096-entry interior entity id namespace.");
        return Base(landblockX, landblockY) + counter++;
    }
}
