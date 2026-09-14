using System.Text;
using System.Text.Json;

namespace AcDream.Core.Plugins;

/// <summary>Host facility an entry assembly declares in <c>plugin.json</c>.</summary>
public enum PluginKind
{
    Gameplay,
    RenderPack,
}

/// <summary>Runtime a manifest declares support for via <c>hosts</c> in <c>plugin.json</c>, distinct
/// from <see cref="PluginKind"/>: a mismatch here is a <see cref="PluginHostCompatibilityException"/>,
/// not the <see cref="PluginHostKindException"/> that a <see cref="PluginKind"/> mismatch reports.</summary>
public enum PluginHostKind
{
    Graphical,
    Headless,
}

public sealed record PluginManifest(
    string Id,
    string DisplayName,
    string Version,
    string EntryDll,
    int ApiVersion,
    IReadOnlyList<string> Dependencies)
{
    public IReadOnlyList<PluginKind> Kinds { get; init; } = [PluginKind.Gameplay];
    public PluginHostVersion? MinHostVersion { get; init; }
    public PluginHostVersion? MaxHostVersion { get; init; }
    public IReadOnlyList<PluginHostVersion> SkipHostVersions { get; init; } = [];
    public IReadOnlyList<PluginHostKind> Hosts { get; init; } =
        [PluginHostKind.Graphical, PluginHostKind.Headless];

    public PluginManifest(
        string Id,
        string DisplayName,
        string Version,
        string EntryDll,
        int ApiVersion,
        IReadOnlyList<string> Dependencies,
        IReadOnlyList<PluginKind> Kinds)
        : this(Id, DisplayName, Version, EntryDll, ApiVersion, Dependencies)
    {
        ArgumentNullException.ThrowIfNull(Kinds);
        if (Kinds.Count == 0)
            throw new ArgumentException("At least one plugin kind is required.", nameof(Kinds));
        this.Kinds = Kinds
            .Distinct()
            .ToArray();
    }

    public bool Declares(PluginKind kind) => Kinds.Contains(kind);

    public static PluginManifest Parse(string json)
    {
        PluginManifestDto? dto;
        try
        {
            RejectDuplicateProperties(json);
            dto = JsonSerializer.Deserialize<PluginManifestDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException($"invalid json: {ex.Message}", ex);
        }

        if (dto is null)
            throw new PluginManifestException("manifest is empty");

        Require(dto.Id, "id");
        Require(dto.DisplayName, "displayName");
        Require(dto.Version, "version");
        Require(dto.EntryDll, "entryDll");
        if (dto.ApiVersion <= 0)
            throw new PluginManifestException("apiVersion must be >= 1");

        IReadOnlyList<PluginKind> kinds = ParseKinds(dto.Kinds);
        PluginHostVersion? minHostVersion = ParseHostVersion(dto.MinHostVersion, "minHostVersion");
        PluginHostVersion? maxHostVersion = ParseHostVersion(dto.MaxHostVersion, "maxHostVersion");
        if (minHostVersion is { } min && maxHostVersion is { } max && min.CompareTo(max) > 0)
        {
            throw new PluginManifestException(
                $"minHostVersion ({min}) is greater than maxHostVersion ({max})");
        }
        IReadOnlyList<PluginHostVersion> skipHostVersions = ParseSkipHostVersions(dto.SkipHostVersions);
        IReadOnlyList<PluginHostKind> hosts = ParseHosts(dto.Hosts);

        return new PluginManifest(
            dto.Id!,
            dto.DisplayName!,
            dto.Version!,
            dto.EntryDll!,
            dto.ApiVersion,
            dto.Dependencies ?? Array.Empty<string>(),
            kinds)
        {
            MinHostVersion = minHostVersion,
            MaxHostVersion = maxHostVersion,
            SkipHostVersions = skipHostVersions,
            Hosts = hosts,
        };
    }

    private static IReadOnlyList<PluginKind> ParseKinds(IReadOnlyList<string>? values)
    {
        if (values is null)
            return [PluginKind.Gameplay];
        if (values.Count == 0)
            throw new PluginManifestException("kinds must contain at least one entry");

        var kinds = new List<PluginKind>(values.Count);
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !Enum.TryParse(value, ignoreCase: true, out PluginKind kind)
                || !Enum.IsDefined(kind))
            {
                throw new PluginManifestException(
                    $"unknown plugin kind: {value ?? "<null>"}");
            }

            if (!kinds.Contains(kind))
                kinds.Add(kind);
        }
        return kinds;
    }

    private static PluginHostVersion? ParseHostVersion(string? value, string jsonFieldName)
    {
        if (value is null)
            return null;
        if (!PluginHostVersion.TryParse(value, out PluginHostVersion version))
            throw new PluginManifestException($"malformed {jsonFieldName}: {value}");
        return version;
    }

    private static IReadOnlyList<PluginHostVersion> ParseSkipHostVersions(
        IReadOnlyList<string>? values)
    {
        if (values is null)
            return [];

        var skipVersions = new List<PluginHostVersion>(values.Count);
        foreach (string? value in values)
        {
            if (!PluginHostVersion.TryParse(value, out PluginHostVersion version))
            {
                throw new PluginManifestException(
                    $"malformed skipHostVersions entry: {value ?? "<null>"}");
            }
            skipVersions.Add(version);
        }
        return skipVersions;
    }

    private static IReadOnlyList<PluginHostKind> ParseHosts(IReadOnlyList<string>? values)
    {
        if (values is null)
            return [PluginHostKind.Graphical, PluginHostKind.Headless];
        if (values.Count == 0)
            throw new PluginManifestException("hosts must contain at least one entry");

        var hosts = new List<PluginHostKind>(values.Count);
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !Enum.TryParse(value, ignoreCase: true, out PluginHostKind host)
                || !Enum.IsDefined(host))
            {
                throw new PluginManifestException(
                    $"unknown host: {value ?? "<null>"}");
            }

            if (!hosts.Contains(host))
                hosts.Add(host);
        }
        return hosts;
    }

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
                        throw new PluginManifestException($"duplicate property: {name}");
                    break;
            }
        }
    }

    private static void Require(string? value, string jsonFieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new PluginManifestException($"missing required field: {jsonFieldName}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class PluginManifestDto
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Version { get; set; }
        public string? EntryDll { get; set; }
        public int ApiVersion { get; set; }
        public IReadOnlyList<string>? Dependencies { get; set; }
        public IReadOnlyList<string>? Kinds { get; set; }
        public string? MinHostVersion { get; set; }
        public string? MaxHostVersion { get; set; }
        public IReadOnlyList<string>? SkipHostVersions { get; set; }
        public IReadOnlyList<string>? Hosts { get; set; }
    }
}

public sealed class PluginApiVersionException : Exception
{
    public PluginApiVersionException(string message) : base(message) { }
}

public sealed class PluginManifestException : Exception
{
    public PluginManifestException(string message) : base(message) { }
    public PluginManifestException(string message, Exception inner) : base(message, inner) { }
}
