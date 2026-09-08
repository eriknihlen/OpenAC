namespace AcDream.Core.Net.Transport;

internal static class SequenceMath
{
    /// <summary>True when <paramref name="a"/> is strictly newer than
    /// <paramref name="b"/> in wrap-safe sequence order.</summary>
    public static bool IsNewer(uint a, uint b) => unchecked((int)(a - b)) > 0;

    /// <summary>The wrap-safe newer of two sequence values (either one when
    /// they are equal).</summary>
    public static uint Max(uint a, uint b) => IsNewer(a, b) ? a : b;
}
