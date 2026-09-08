using System.Text.Json;

namespace AcDream.Core.Plugins;

/// <summary>Host facility an entry assembly declares in <c>plugin.json</c>.</summary>
public enum PluginKind
{
    Gameplay,
    RenderPack,
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

        return new PluginManifest(
            dto.Id!,
            dto.DisplayName!,
            dto.Version!,
            dto.EntryDll!,
            dto.ApiVersion,
            dto.Dependencies ?? Array.Empty<string>(),
            kinds);
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
