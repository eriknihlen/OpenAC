using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Host facility an entry assembly declares in <c>plugin.json</c>. Mirrors
/// <c>AcDream.Core.Plugins.PluginKind</c> (L-303).</summary>
public enum LauncherPluginKind
{
    Gameplay,
    RenderPack,
}

/// <summary>Runtime a manifest declares support for via <c>hosts</c>. Mirrors
/// <c>AcDream.Core.Plugins.PluginHostKind</c> (L-303).</summary>
public enum LauncherPluginHostKind
{
    Graphical,
    Headless,
}

/// <summary>The version core (<c>MAJOR.MINOR.PATCH</c>) a host field or the installed client names.
/// Mirrors <c>AcDream.Core.Plugins.PluginHostVersion</c> (L-303).</summary>
public readonly record struct LauncherPluginHostVersion(int Major, int Minor, int Patch)
    : IComparable<LauncherPluginHostVersion>
{
    public static bool TryParse(string? value, out LauncherPluginHostVersion version)
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

        version = new LauncherPluginHostVersion(major, minor, patch);
        return true;
    }

    /// <summary>Reduces a full launcher SemVer (which may carry pre-release or build metadata) to its
    /// version core, the same way the client reduces its own informational version.</summary>
    public static LauncherPluginHostVersion? FromLauncherVersion(LauncherVersion? version)
    {
        if (version is null)
            return null;

        string value = version.Value;
        int cut = value.IndexOfAny(['-', '+']);
        string core = cut < 0 ? value : value[..cut];
        return TryParse(core, out LauncherPluginHostVersion result) ? result : null;
    }

    public int CompareTo(LauncherPluginHostVersion other)
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

/// <summary>The launcher's own reader for <c>plugin.json</c> (L-303). Fields and defaults match
/// <c>AcDream.Core.Plugins.PluginManifest</c>; a parity test pins the two readers together.</summary>
public sealed record LauncherPluginManifest(
    string Id,
    string DisplayName,
    string Version,
    string EntryDll,
    int ApiVersion,
    IReadOnlyList<LauncherPluginKind> Kinds,
    LauncherPluginHostVersion? MinHostVersion,
    LauncherPluginHostVersion? MaxHostVersion,
    IReadOnlyList<LauncherPluginHostVersion> SkipHostVersions,
    IReadOnlyList<LauncherPluginHostKind>? Hosts)
{
    private static readonly Regex IdPattern = new(
        @"\A[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+\z",
        RegexOptions.Compiled);

    public static LauncherPluginManifest Parse(string json)
    {
        ManifestDto? dto;
        try
        {
            RejectDuplicateProperties(json);
            dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new LauncherPluginManifestException($"invalid json: {ex.Message}", ex);
        }

        if (dto is null)
            throw new LauncherPluginManifestException("manifest is empty");

        Require(dto.Id, "id");
        Require(dto.DisplayName, "displayName");
        Require(dto.Version, "version");
        Require(dto.EntryDll, "entryDll");
        if (dto.ApiVersion <= 0)
            throw new LauncherPluginManifestException("apiVersion must be >= 1");

        IReadOnlyList<LauncherPluginKind> kinds = ParseKinds(dto.Kinds);
        LauncherPluginHostVersion? minHostVersion = ParseHostVersion(dto.MinHostVersion, "minHostVersion");
        LauncherPluginHostVersion? maxHostVersion = ParseHostVersion(dto.MaxHostVersion, "maxHostVersion");
        if (minHostVersion is { } min && maxHostVersion is { } max && min.CompareTo(max) > 0)
        {
            throw new LauncherPluginManifestException(
                $"minHostVersion ({min}) is greater than maxHostVersion ({max})");
        }
        IReadOnlyList<LauncherPluginHostVersion> skipHostVersions =
            ParseSkipHostVersions(dto.SkipHostVersions);
        IReadOnlyList<LauncherPluginHostKind>? hosts = dto.Hosts is null
            ? null
            : ParseHosts(dto.Hosts);

        return new LauncherPluginManifest(
            dto.Id!,
            dto.DisplayName!,
            dto.Version!,
            dto.EntryDll!,
            dto.ApiVersion,
            kinds,
            minHostVersion,
            maxHostVersion,
            skipHostVersions,
            hosts);
    }

    /// <summary>The install-only rules a downloaded manifest must additionally satisfy: a namespaced
    /// id, a strict SemVer version, and a declared <see cref="MinHostVersion"/> and
    /// <see cref="Hosts"/> (L-310).</summary>
    public void ValidateForInstall()
    {
        if (!IdPattern.IsMatch(Id))
        {
            throw new LauncherPluginManifestException(
                $"id '{Id}' does not match the required namespaced pattern");
        }

        if (!LauncherVersion.TryParse(Version, out _))
        {
            throw new LauncherPluginManifestException(
                $"version '{Version}' is not a valid SemVer 2.0 version");
        }

        if (MinHostVersion is null)
            throw new LauncherPluginManifestException("minHostVersion is required for launcher installs");

        if (Hosts is not { Count: > 0 })
            throw new LauncherPluginManifestException("hosts is required for launcher installs");

        if (ApiVersion < LauncherPluginApiRange.Minimum || ApiVersion > LauncherPluginApiRange.Current)
        {
            throw new LauncherPluginManifestException(
                $"apiVersion {ApiVersion} is not supported by this launcher");
        }
    }

    /// <summary>Whether a release tag names this manifest's own version, so "latest" can't drift
    /// between the manifest fetch and the asset downloads that follow it.</summary>
    public bool MatchesTag(string tag) =>
        string.Equals(tag, "v" + Version, StringComparison.Ordinal);

    private static IReadOnlyList<LauncherPluginKind> ParseKinds(IReadOnlyList<string>? values)
    {
        if (values is null)
            return [LauncherPluginKind.Gameplay];
        if (values.Count == 0)
            throw new LauncherPluginManifestException("kinds must contain at least one entry");

        var kinds = new List<LauncherPluginKind>(values.Count);
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !Enum.TryParse(value, ignoreCase: true, out LauncherPluginKind kind)
                || !Enum.IsDefined(kind))
            {
                throw new LauncherPluginManifestException(
                    $"unknown plugin kind: {value ?? "<null>"}");
            }

            if (!kinds.Contains(kind))
                kinds.Add(kind);
        }
        return kinds;
    }

    private static LauncherPluginHostVersion? ParseHostVersion(string? value, string jsonFieldName)
    {
        if (value is null)
            return null;
        if (!LauncherPluginHostVersion.TryParse(value, out LauncherPluginHostVersion version))
            throw new LauncherPluginManifestException($"malformed {jsonFieldName}: {value}");
        return version;
    }

    private static IReadOnlyList<LauncherPluginHostVersion> ParseSkipHostVersions(
        IReadOnlyList<string>? values)
    {
        if (values is null)
            return [];

        var skipVersions = new List<LauncherPluginHostVersion>(values.Count);
        foreach (string? value in values)
        {
            if (!LauncherPluginHostVersion.TryParse(value, out LauncherPluginHostVersion version))
            {
                throw new LauncherPluginManifestException(
                    $"malformed skipHostVersions entry: {value ?? "<null>"}");
            }
            skipVersions.Add(version);
        }
        return skipVersions;
    }

    private static IReadOnlyList<LauncherPluginHostKind> ParseHosts(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            throw new LauncherPluginManifestException("hosts must contain at least one entry");

        var hosts = new List<LauncherPluginHostKind>(values.Count);
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !Enum.TryParse(value, ignoreCase: true, out LauncherPluginHostKind host)
                || !Enum.IsDefined(host))
            {
                throw new LauncherPluginManifestException(
                    $"unknown host: {value ?? "<null>"}");
            }

            if (!hosts.Contains(host))
                hosts.Add(host);
        }
        return hosts;
    }

    private static void Require(string? value, string jsonFieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new LauncherPluginManifestException($"missing required field: {jsonFieldName}");
    }

    /// <summary>Rejects a manifest that repeats a property name anywhere in the document, matching
    /// <c>AcDream.Core.Plugins.PluginManifest</c>'s own reader (L-311): an install decision should
    /// never hinge on which of two conflicting property spellings a JSON writer happened to put
    /// last.</summary>
    private static void RejectDuplicateProperties(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        var scopes = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    break;
                case JsonTokenType.EndObject:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    string name = reader.GetString()!;
                    if (!scopes.Peek().Add(name))
                        throw new LauncherPluginManifestException($"duplicate property: {name}");
                    break;
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class ManifestDto
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Version { get; set; }
        public string? EntryDll { get; set; }
        public int ApiVersion { get; set; }
        public IReadOnlyList<string>? Kinds { get; set; }
        public string? MinHostVersion { get; set; }
        public string? MaxHostVersion { get; set; }
        public IReadOnlyList<string>? SkipHostVersions { get; set; }
        public IReadOnlyList<string>? Hosts { get; set; }
    }
}

public sealed class LauncherPluginManifestException : Exception
{
    public LauncherPluginManifestException(string message) : base(message) { }
    public LauncherPluginManifestException(string message, Exception inner) : base(message, inner) { }
}
