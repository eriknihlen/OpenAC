namespace AcDream.Content;

/// <summary>
/// The key a string table files a piece of text under, worked out from the
/// text's name.
/// </summary>
public static class RetailStringHash
{
    /// <summary>The key for <paramref name="value"/>.</summary>
    public static uint Compute(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        uint result = 0u;
        foreach (char c in value)
        {
            result = unchecked((result << 4) + (byte)c);
            uint high = result & 0xF0000000u;
            if (high != 0u)
                result = ((high >> 24) ^ result) & 0x0FFFFFFFu;
        }

        return result == uint.MaxValue ? uint.MaxValue - 1u : result;
    }
}
