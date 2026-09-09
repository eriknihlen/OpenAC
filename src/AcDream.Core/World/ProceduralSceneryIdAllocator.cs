namespace AcDream.Core.World;

public static class ProceduralSceneryIdAllocator
{
    public const uint MaxCounter = 0xFFFu;

    public static uint Base(uint landblockX, uint landblockY)
        => 0x80000000u
           | ((landblockX & 0xFFu) << 20)
           | ((landblockY & 0xFFu) << 12);

    public static uint Allocate(uint landblockX, uint landblockY, ref uint counter)
    {
        if (counter > MaxCounter)
            throw new InvalidDataException(
                $"Landblock ({landblockX & 0xFFu:X2},{landblockY & 0xFFu:X2}) exceeds the 4096-entry procedural scenery id namespace.");

        return Base(landblockX, landblockY) + counter++;
    }

    /// <summary>
    /// The full top-nibble test — <c>0x8...</c>, not bit 31 alone. Bit 31
    /// alone also matches <c>LandblockStaticEntityIdAllocator</c>'s
    /// <c>0xC...</c> ids (top nibble <c>1100</c> also has bit 31 set) and
    /// the synthetic render ids <c>0xDA11_D0xx</c> / <c>0xFFFF_FF01</c>.
    /// </summary>
    public static bool IsInNamespace(uint entityId) =>
        (entityId & 0xF0000000u) == 0x80000000u;
}
