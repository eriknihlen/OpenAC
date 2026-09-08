using System.Globalization;

namespace AcDream.Launcher.Core.Profiles;

public static class CharacterIdFormat
{
    public static string ToHexString(uint id) =>
        "0x" + id.ToString("X8", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (!span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;

        span = span[2..];

        return uint.TryParse(
            span,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out id);
    }
}
