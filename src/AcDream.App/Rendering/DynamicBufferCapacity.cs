namespace AcDream.App.Rendering;

internal static class DynamicBufferCapacity
{
    internal const int DefaultAlignment = 4096;

    public static int Grow(int currentBytes, int requiredBytes, int alignment = DefaultAlignment)
    {
        if (currentBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(currentBytes));
        if (requiredBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        if (alignment <= 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        if (requiredBytes <= currentBytes)
            return currentBytes;

        long doubled = currentBytes == 0 ? alignment : (long)currentBytes * 2L;
        long target = Math.Max(requiredBytes, doubled);
        long aligned = ((target + alignment - 1L) / alignment) * alignment;
        if (aligned > int.MaxValue)
            throw new OverflowException("Dynamic GPU buffer capacity exceeds Int32.MaxValue.");
        return (int)aligned;
    }
}
