using System.Diagnostics.CodeAnalysis;

namespace AcDream.Launcher.Core.Updates;

public sealed class LauncherVersion : IComparable<LauncherVersion>, IEquatable<LauncherVersion>
{
    private readonly string[] _core;
    private readonly string[] _preRelease;

    private LauncherVersion(
        string value,
        string[] core,
        string[] preRelease)
    {
        Value = value;
        _core = core;
        _preRelease = preRelease;
    }

    public string Value { get; }

    public bool IsPreRelease => _preRelease.Length != 0;

    public static LauncherVersion Parse(string value)
    {
        if (!TryParse(value, out LauncherVersion? version))
        {
            throw new FormatException($"'{value}' is not a strict SemVer 2.0 version.");
        }

        return version;
    }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out LauncherVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value)
            || value.Length > 128
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        string precedence = value;
        int plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            if (plus == value.Length - 1
                || value.IndexOf('+', plus + 1) >= 0
                || !ValidIdentifiers(value[(plus + 1)..], numericLeadingZeroRule: false))
            {
                return false;
            }

            precedence = value[..plus];
        }

        string coreText = precedence;
        string[] preRelease = [];
        int dash = precedence.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            if (dash == precedence.Length - 1
                || !ValidIdentifiers(precedence[(dash + 1)..], numericLeadingZeroRule: true))
            {
                return false;
            }

            coreText = precedence[..dash];
            preRelease = precedence[(dash + 1)..].Split('.');
        }

        string[] core = coreText.Split('.');
        if (core.Length != 3 || core.Any(part => !ValidCoreNumber(part)))
        {
            return false;
        }

        version = new LauncherVersion(value, core, preRelease);
        return true;
    }

    public int CompareTo(LauncherVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (int index = 0; index < _core.Length; index++)
        {
            int comparison = CompareNumeric(_core[index], other._core[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (_preRelease.Length == 0 || other._preRelease.Length == 0)
        {
            return _preRelease.Length == other._preRelease.Length
                ? 0
                : _preRelease.Length == 0 ? 1 : -1;
        }

        int shared = Math.Min(_preRelease.Length, other._preRelease.Length);
        for (int index = 0; index < shared; index++)
        {
            string left = _preRelease[index];
            string right = other._preRelease[index];
            bool leftNumeric = IsDigits(left);
            bool rightNumeric = IsDigits(right);
            int comparison = leftNumeric && rightNumeric
                ? CompareNumeric(left, right)
                : leftNumeric != rightNumeric
                    ? leftNumeric ? -1 : 1
                    : string.Compare(left, right, StringComparison.Ordinal);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    public bool Equals(LauncherVersion? other) =>
        other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as LauncherVersion);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (string part in _core)
        {
            hash.Add(part, StringComparer.Ordinal);
        }

        hash.Add(_preRelease.Length);
        foreach (string part in _preRelease)
        {
            hash.Add(part, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => Value;

    public static bool operator >(LauncherVersion left, LauncherVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator <(LauncherVersion left, LauncherVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator >=(LauncherVersion left, LauncherVersion right) =>
        left.CompareTo(right) >= 0;

    public static bool operator <=(LauncherVersion left, LauncherVersion right) =>
        left.CompareTo(right) <= 0;

    private static bool ValidCoreNumber(string value) =>
        IsDigits(value) && (value.Length == 1 || value[0] != '0');

    private static bool ValidIdentifiers(string value, bool numericLeadingZeroRule)
    {
        string[] identifiers = value.Split('.');
        return identifiers.All(identifier =>
            identifier.Length > 0
            && identifier.All(character =>
                character is >= '0' and <= '9'
                or >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or '-')
            && (!numericLeadingZeroRule
                || !IsDigits(identifier)
                || identifier.Length == 1
                || identifier[0] != '0'));
    }

    private static bool IsDigits(string value) =>
        value.Length > 0 && value.All(character => character is >= '0' and <= '9');

    private static int CompareNumeric(string left, string right)
    {
        int length = left.Length.CompareTo(right.Length);
        return length != 0
            ? length
            : string.Compare(left, right, StringComparison.Ordinal);
    }
}
