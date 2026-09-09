using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.Tools.RenderPackValidator;

internal sealed record PackManifest(
    string Id,
    string DisplayName,
    string Version,
    string EntryDll,
    int ApiVersion,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Kinds)
{
    internal static ValidationOutcome Parse(string json)
    {
        ManifestDto? value;
        try
        {
            value = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions);
        }
        catch (JsonException error)
        {
            return ValidationOutcome.Invalid($"plugin.json is invalid JSON: {error.Message}");
        }

        if (value is null)
            return ValidationOutcome.Invalid("plugin.json is empty.");
        if (!StableId.IsValid(value.Id))
            return ValidationOutcome.Invalid("plugin.json id must be a stable lowercase logical id.");
        if (string.IsNullOrWhiteSpace(value.DisplayName))
            return ValidationOutcome.Invalid("plugin.json is missing displayName.");
        if (string.IsNullOrWhiteSpace(value.Version))
            return ValidationOutcome.Invalid("plugin.json is missing version.");
        if (!System.Version.TryParse(value.Version, out _))
            return ValidationOutcome.Invalid("plugin.json version must be a dotted numeric version.");
        if (!SafeRelativePath.IsValid(value.EntryDll, requireDll: true))
            return ValidationOutcome.Invalid("plugin.json entryDll must be a safe relative .dll path.");
        if (!PluginApi.IsSupported(value.ApiVersion))
        {
            return ValidationOutcome.Invalid(
                $"plugin.json apiVersion {value.ApiVersion} is unsupported; "
                + $"this SDK supports {PluginApi.MinimumSupported}..{PluginApi.Current}.");
        }

        string[] dependencies = value.Dependencies ?? [];
        if (dependencies.Any(static dependency => !StableId.IsValid(dependency)))
            return ValidationOutcome.Invalid("plugin.json contains an invalid dependency id.");
        string[] kinds = value.Kinds ?? ["gameplay"];
        if (kinds.Length == 0)
            return ValidationOutcome.Invalid("plugin.json kinds must contain at least one entry.");
        foreach (string? kind in kinds)
        {
            if (!string.Equals(kind, "gameplay", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(kind, "renderPack", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationOutcome.Invalid(
                    $"plugin.json contains unknown kind '{kind ?? "<null>"}'.");
            }
        }
        if (!kinds.Contains("renderPack", StringComparer.OrdinalIgnoreCase))
            return ValidationOutcome.Invalid("plugin.json does not declare the renderPack kind.");

        return ValidationOutcome.Valid(new PackManifest(
            value.Id!,
            value.DisplayName!,
            value.Version!,
            value.EntryDll!,
            value.ApiVersion,
            dependencies,
            kinds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
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
        public string[]? Dependencies { get; set; }
        public string[]? Kinds { get; set; }
    }
}

internal readonly record struct ValidationOutcome(
    bool Success,
    string? Reason,
    PackManifest? Manifest = null)
{
    internal static ValidationOutcome Valid(PackManifest manifest) =>
        new(true, null, manifest);

    internal static ValidationOutcome Invalid(string reason) =>
        new(false, reason);
}

internal static class StableId
{
    internal static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return false;
        if (value[0] is < 'a' or > 'z')
            return false;
        return value.All(static character =>
            character is >= 'a' and <= 'z'
            || character is >= '0' and <= '9'
            || character is '.' or '-' or '_');
    }
}

internal static class SafeRelativePath
{
    internal static bool IsValid(string? value, bool requireDll = false)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 512
            || Path.IsPathRooted(value)
            || value.Contains('\\'))
        {
            return false;
        }

        string[] segments = value.Split('/');
        if (segments.Any(static segment =>
            segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        return !requireDll
            || string.Equals(Path.GetExtension(value), ".dll", StringComparison.OrdinalIgnoreCase);
    }
}
