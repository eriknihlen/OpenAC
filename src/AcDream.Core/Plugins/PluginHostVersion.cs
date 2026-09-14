namespace AcDream.Core.Plugins;

/// <summary>The version core (<c>MAJOR.MINOR.PATCH</c>) a client build or a manifest field names.</summary>
public readonly record struct PluginHostVersion(int Major, int Minor, int Patch)
    : IComparable<PluginHostVersion>
{
    public static bool TryParse(string? value, out PluginHostVersion version)
    {
        version = default;
        if (value is null)
            return false;

        string[] parts = value.Split('.');
        if (parts.Length != 3)
            return false;

        if (!TryParseComponent(parts[0], out int major)
            || !TryParseComponent(parts[1], out int minor)
            || !TryParseComponent(parts[2], out int patch))
        {
            return false;
        }

        version = new PluginHostVersion(major, minor, patch);
        return true;
    }

    public static PluginHostVersion? FromInformationalVersion(string? informationalVersion)
    {
        if (informationalVersion is null)
            return null;

        int cut = informationalVersion.IndexOfAny(['-', '+']);
        string core = cut < 0 ? informationalVersion : informationalVersion[..cut];
        return TryParse(core, out PluginHostVersion version) ? version : null;
    }

    public int CompareTo(PluginHostVersion other)
    {
        int major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;
        int minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    private static bool TryParseComponent(string value, out int component)
    {
        component = 0;
        if (value.Length == 0 || !value.All(char.IsAsciiDigit))
            return false;
        if (value.Length > 1 && value[0] == '0')
            return false;
        return int.TryParse(value, out component);
    }
}
