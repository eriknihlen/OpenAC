using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>A blocked plugin id, and the versions it applies to ("*" for all).</summary>
public sealed record PluginBlock(string Id, IReadOnlyList<string> Versions, string Reason)
{
    public bool Matches(string version) =>
        Versions.Contains("*") || Versions.Contains(version);
}

public sealed record PluginCatalogEntry(
    string Id,
    string Name,
    string Author,
    string Description,
    string Repo);

/// <summary>The launcher's published plugin list (<c>plugins.json</c>, L-308). Strict parsing
/// mirrors <see cref="ReleaseManifestClient"/>: unknown members and duplicate properties (compared
/// case-insensitively, as in <c>plugin.json</c>, L-311) are rejected.</summary>
public sealed record PluginCatalog(
    int SchemaVersion,
    IReadOnlyList<PluginCatalogEntry> Plugins,
    IReadOnlyList<PluginBlock> Blocked)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The proof-of-concept list release (L-308). One constant, so moving it to Erik's
    /// account later is a one-line change.</summary>
    public static Uri ProductionListUri { get; } =
        GitHubReleaseLocator.LatestAsset("shaneedwards/openac-plugins", "plugins.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public bool IsBlocked(string id, LauncherVersion? version) => BlockReason(id, version) is not null;

    /// <summary>The reason a release is blocked, or null if it is not.</summary>
    public string? BlockReason(string id, LauncherVersion? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Blocked.FirstOrDefault(block =>
                string.Equals(block.Id, id, StringComparison.OrdinalIgnoreCase)
                && (version is null || block.Matches(version.Value)))
            ?.Reason;
    }

    /// <summary>True only for a block covering every version ("*"); a version-specific block needs
    /// the latest version fetched before it can be judged (L-314).</summary>
    public bool IsBlockedForAllVersions(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Blocked.Any(block =>
            string.Equals(block.Id, id, StringComparison.OrdinalIgnoreCase)
            && block.Versions.Contains("*"));
    }

    public static PluginCatalog Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            RejectDuplicateProperties(document.RootElement, "$");
            CatalogDocument? value = document.RootElement.Deserialize<CatalogDocument>(
                SerializerOptions);
            return Validate(value);
        }
        catch (LauncherUpdateException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
                                   or FormatException
                                   or InvalidOperationException)
        {
            throw new LauncherUpdateException(
                $"The plugin list is invalid: {ex.Message}",
                ex);
        }
    }

    private static PluginCatalog Validate(CatalogDocument? document)
    {
        if (document is null)
        {
            throw new LauncherUpdateException("The plugin list is empty.");
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new LauncherUpdateException(
                $"Plugin list schema version {document.SchemaVersion} is not supported.");
        }

        IReadOnlyList<PluginCatalogEntry> plugins = ValidateEntries(document.Plugins);
        IReadOnlyList<PluginBlock> blocked = ValidateBlocks(document.Blocked);
        return new PluginCatalog(document.SchemaVersion, plugins, blocked);
    }

    private static IReadOnlyList<PluginCatalogEntry> ValidateEntries(
        IReadOnlyList<EntryDocument>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            throw new LauncherUpdateException("The plugin list has no plugins.");
        }

        var result = new List<PluginCatalogEntry>(entries.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EntryDocument entry in entries)
        {
            RequireField(entry.Id, "plugins[].id");
            RequireField(entry.Name, "plugins[].name");
            RequireField(entry.Author, "plugins[].author");
            RequireField(entry.Description, "plugins[].description");
            RequireField(entry.Repo, "plugins[].repo");
            if (!GitHubReleaseLocator.IsValidRepo(entry.Repo!))
            {
                throw new LauncherUpdateException(
                    $"Plugin list entry '{entry.Id}' has an invalid repo '{entry.Repo}'.");
            }

            if (!ids.Add(entry.Id!))
            {
                throw new LauncherUpdateException(
                    $"Plugin list contains a duplicate id '{entry.Id}'.");
            }

            result.Add(new PluginCatalogEntry(
                entry.Id!,
                entry.Name!,
                entry.Author!,
                entry.Description!,
                entry.Repo!));
        }

        return result;
    }

    private static IReadOnlyList<PluginBlock> ValidateBlocks(IReadOnlyList<BlockDocument>? blocks)
    {
        if (blocks is null)
            return [];

        var result = new List<PluginBlock>(blocks.Count);
        foreach (BlockDocument block in blocks)
        {
            RequireField(block.Id, "blocked[].id");
            RequireField(block.Reason, "blocked[].reason");
            if (block.Versions is null || block.Versions.Count == 0)
            {
                throw new LauncherUpdateException(
                    $"Blocked entry '{block.Id}' has no versions.");
            }

            if (block.Versions.Any(string.IsNullOrWhiteSpace))
            {
                throw new LauncherUpdateException(
                    $"Blocked entry '{block.Id}' has a blank version.");
            }

            result.Add(new PluginBlock(block.Id!, block.Versions, block.Reason!));
        }

        return result;
    }

    private static void RequireField(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new LauncherUpdateException($"The plugin list is missing '{fieldName}'.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new LauncherUpdateException(
                        $"Duplicate JSON property '{path}.{property.Name}' is not allowed.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }

    private sealed class CatalogDocument
    {
        public int SchemaVersion { get; init; }

        public List<EntryDocument>? Plugins { get; init; }

        public List<BlockDocument>? Blocked { get; init; }
    }

    private sealed class EntryDocument
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Author { get; init; }

        public string? Description { get; init; }

        public string? Repo { get; init; }
    }

    private sealed class BlockDocument
    {
        public string? Id { get; init; }

        public List<string>? Versions { get; init; }

        public string? Reason { get; init; }
    }
}
