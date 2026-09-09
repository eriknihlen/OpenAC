using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal static class VtankProfileDirectory
{
    /// <summary>The real VTank "--" reserved-prefix marker (section 3).</summary>
    internal const string HiddenPrefix = "--";

    /// <summary>
    /// The nav-profile-only "~~" reserved prefix (section 3) — VTank's own
    /// producer/purpose was not determined by the KB research pass either;
    /// this only reproduces the filter.
    /// </summary>
    internal const string NavHiddenPrefix = "~~";

    internal const string ByCharacterLabel = "[By char]";
    internal const string DefaultLabel = "[Default]";
    internal const string NoneLabel = "[None]";

    internal const string MetaFolder = "metas";

    internal const string NavFolder = "navs";

    private const string LegacyNavMarker = "nav_";

    internal static bool IsLegacyFlatRouteFileName(string bareFileName) =>
        bareFileName.StartsWith(LegacyNavMarker, StringComparison.Ordinal)
        || (bareFileName.StartsWith(HiddenPrefix, StringComparison.Ordinal)
            && bareFileName[HiddenPrefix.Length..].StartsWith(
                LegacyNavMarker, StringComparison.Ordinal));

    internal static string StripLegacyNavMarker(string bareFileName)
    {
        bool isHidden = bareFileName.StartsWith(HiddenPrefix, StringComparison.Ordinal);
        string rest = isHidden ? bareFileName[HiddenPrefix.Length..] : bareFileName;
        rest = rest[LegacyNavMarker.Length..];
        return isHidden ? HiddenPrefix + rest : rest;
    }

    internal static IReadOnlyList<string> ListFlatAfFileNames(IPluginStorage storage) =>
        EnumerateFileNames(storage, ".af").ToList();

    public static string AutoCharacterFileName(
        string characterName,
        string server,
        string extension) =>
        $"{HiddenPrefix}{characterName}_{server}.{extension.TrimStart('.')}";

    public static string SubProfilePrefix(string characterName, string server) =>
        $"{HiddenPrefix}{characterName}_{server}_";

    public static bool IsHiddenFromOtherCharacters(
        string fileName,
        string characterName,
        string server) =>
        fileName.StartsWith(HiddenPrefix, StringComparison.Ordinal)
        && !fileName.StartsWith(
            SubProfilePrefix(characterName, server),
            StringComparison.Ordinal);

    public static string? TryDisplayName(
        string fileName,
        string characterName,
        string server)
    {
        string prefix = SubProfilePrefix(characterName, server);
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        string withoutPrefix = fileName[prefix.Length..];
        int dot = withoutPrefix.LastIndexOf('.');
        string suffix = dot >= 0 ? withoutPrefix[..dot] : withoutPrefix;
        return suffix.Length == 0 ? null : $"[Char] {suffix}";
    }

    /// <summary>
    /// One entry in a profile picker: the real on-disk file name, and the
    /// label VTank would show for it.
    /// </summary>
    public readonly record struct ProfileEntry(string FileName, string DisplayName);

    public static IReadOnlyList<ProfileEntry> ListSettingsProfiles(
        IPluginStorage storage,
        string characterName,
        string server,
        bool mineOnly,
        string? currentFileName = null)
    {
        var entries = new List<ProfileEntry>
        {
            new(string.Empty, DefaultLabel),
            new(string.Empty, ByCharacterLabel),
        };
        foreach (string fileName in EnumerateFileNames(storage, ".usd"))
        {
            string? subProfileDisplay = TryDisplayName(fileName, characterName, server);
            if (subProfileDisplay is not null)
            {
                entries.Add(new ProfileEntry(fileName, subProfileDisplay));
                continue;
            }
            if (fileName.StartsWith(HiddenPrefix, StringComparison.Ordinal))
                continue; // someone else's --Name_Server(.usd|_*) family.
            if (mineOnly
                && !fileName.Equals(currentFileName, StringComparison.Ordinal))
            {
                continue;
            }
            entries.Add(new ProfileEntry(fileName, fileName));
        }
        return entries;
    }

    public static IReadOnlyList<ProfileEntry> ListNavigationProfiles(IPluginStorage storage)
    {
        var entries = new List<ProfileEntry>
        {
            new(string.Empty, NoneLabel),
            new(string.Empty, ByCharacterLabel),
        };
        foreach (string bareName in EnumerateFolderFileNames(storage, NavFolder, ".af"))
        {
            if (bareName.StartsWith(HiddenPrefix, StringComparison.Ordinal)
                || bareName.StartsWith(NavHiddenPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            entries.Add(new ProfileEntry($"{NavFolder}/{bareName}", bareName));
        }
        return entries;
    }

    public static IReadOnlyList<ProfileEntry> ListMetaProfiles(IPluginStorage storage)
    {
        var entries = new List<ProfileEntry>
        {
            new(string.Empty, NoneLabel),
            new(string.Empty, ByCharacterLabel),
        };
        foreach (string bareName in EnumerateFolderFileNames(storage, MetaFolder, ".af"))
        {
            if (bareName.StartsWith(HiddenPrefix, StringComparison.Ordinal))
                continue;
            entries.Add(new ProfileEntry($"{MetaFolder}/{bareName}", bareName));
        }
        return entries;
    }

    public static IReadOnlyList<ProfileEntry> ListLootProfiles(IPluginStorage storage)
    {
        var entries = new List<ProfileEntry>
        {
            new(string.Empty, NoneLabel),
            new(string.Empty, ByCharacterLabel),
        };
        foreach (string fileName in EnumerateFileNames(storage, ".utl"))
        {
            if (fileName.StartsWith(HiddenPrefix, StringComparison.Ordinal))
                continue;
            entries.Add(new ProfileEntry(fileName, fileName));
        }
        return entries;
    }

    public static string AstFileName(string characterName, string server) =>
        $"{characterName}_{server}.ast";

    public static string CdfFileName(string characterName, string server) =>
        $"{server}_{characterName}.cdf";

    /// <summary>The literal version header a valid <c>.cdf</c> starts with (<c>da.cs:15</c>).</summary>
    internal const string CdfHeader = "uTank2 CDF 1.0";

    public readonly record struct VtankCharacterBinding(
        string SettingsFileName,
        string LootFileName,
        string NavFileName,
        string? MetaFileName);

    public static VtankCharacterBinding? TryReadCharacterBinding(
        IPluginStorage storage,
        string characterName,
        string server)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        if (!storage.IsAvailable)
            return null;
        string? text = storage.ReadText(CdfFileName(characterName, server));
        if (text is null)
            return null;
        string[] lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        if (lines.Length < 4 || !string.Equals(lines[0], CdfHeader, StringComparison.Ordinal))
            return null;
        string settings = lines[1].EndsWith(".uts", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(lines[1].AsSpan(0, lines[1].Length - 4), ".usd")
            : lines[1];
        string? meta = lines.Length >= 5 && lines[4].Length > 0 ? lines[4] : null;
        return new VtankCharacterBinding(settings, lines[2], lines[3], meta);
    }

    public static void WriteCharacterBinding(
        IPluginStorage storage,
        string characterName,
        string server,
        VtankCharacterBinding binding)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        if (!storage.IsAvailable)
            return;
        var lines = new List<string>
        {
            CdfHeader,
            binding.SettingsFileName,
            binding.LootFileName,
            binding.NavFileName,
        };
        if (!string.IsNullOrEmpty(binding.MetaFileName))
            lines.Add(binding.MetaFileName);
        storage.WriteText(
            CdfFileName(characterName, server),
            string.Join("\r\n", lines) + "\r\n");
    }

    private static IEnumerable<string> EnumerateFileNames(IPluginStorage storage, string extension)
    {
        if (!storage.IsAvailable)
            yield break;
        foreach (string key in storage.List(string.Empty)
            .Where(key => !key.Contains('/', StringComparison.Ordinal))
            .Where(key => key.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))
        {
            yield return key;
        }
    }

    private static IEnumerable<string> EnumerateFolderFileNames(
        IPluginStorage storage,
        string folder,
        string extension)
    {
        if (!storage.IsAvailable)
            yield break;
        string folderPrefix = folder + "/";
        foreach (string bareName in storage.List(folder)
            .Where(key => key.StartsWith(folderPrefix, StringComparison.Ordinal))
            .Select(key => key[folderPrefix.Length..])
            .Where(bareName => !bareName.Contains('/', StringComparison.Ordinal))
            .Where(bareName => bareName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static bareName => bareName, StringComparer.OrdinalIgnoreCase))
        {
            yield return bareName;
        }
    }
}
