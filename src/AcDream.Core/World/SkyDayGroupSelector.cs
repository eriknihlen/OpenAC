namespace AcDream.Core.World;

public static class SkyDayGroupSelector
{
    public static int SelectIndex(
        int dayGroupCount,
        int absoluteYear,
        int daysPerYear,
        int dayOfYear,
        int? forcedIndex = null)
    {
        if (dayGroupCount <= 0)
            return 0;

        if (forcedIndex is >= 0 && forcedIndex < dayGroupCount)
            return forcedIndex.Value;

        if (dayGroupCount == 1)
            return 0;

        int seed = unchecked(absoluteYear * daysPerYear + dayOfYear);
        int mixed = unchecked(
            seed * 0x6A42FDB2 + unchecked((int)0x8ABE1652));

        float hash = mixed;
        if (mixed < 0)
            hash += 4294967296.0f;

        const float inverseTwoTo32 = 1.0f / 4294967296.0f;
        int index = (int)MathF.Floor(
            (float)dayGroupCount * hash * inverseTwoTo32);

        return index < 0 || index >= dayGroupCount ? 0 : index;
    }
}
