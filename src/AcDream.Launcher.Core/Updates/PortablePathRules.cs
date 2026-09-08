namespace AcDream.Launcher.Core.Updates;

internal static class PortablePathRules
{
    public static bool IsWindowsDeviceName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        string stem = segment.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stem.Length != 4
            || (!stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                && !stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return stem[3] is >= '1' and <= '9'
            or '\u00b9'
            or '\u00b2'
            or '\u00b3';
    }
}
