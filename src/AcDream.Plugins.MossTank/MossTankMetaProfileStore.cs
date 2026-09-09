using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class MossTankMetaProfileStore
{
    public const string ByCharacter = "By char";

    private const string LegacyRosterKey = "profiles/meta/index.json";

    private readonly IPluginHost _host;
    private string _character = string.Empty;
    private string _selected = ByCharacter;
    private string? _pendingLegacyBareName;
    private bool _rosterSwept;
    private bool _flatFolderMigrationSwept;

    public MossTankMetaProfileStore(IPluginHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public string Selected => StripAf(_selected);
    public string? RecoveryNotice { get; private set; }
    public string? SaveNotice { get; private set; }

    private string Server => _host.Automation.Character.WorldName;
    private IPluginStorage VtankStorage => _host.VtankProfiles;
    private bool CanBindFiles => _character.Length > 0 && Server.Length > 0;

    private const string FolderPrefix = VtankProfileDirectory.MetaFolder + "/";

    private static string StripAf(string name)
    {
        if (name.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
            return name;
        string value = name.StartsWith(FolderPrefix, StringComparison.Ordinal)
            ? name[FolderPrefix.Length..]
            : name;
        return value.EndsWith(".af", StringComparison.OrdinalIgnoreCase)
            ? value[..^3]
            : value;
    }

    public IReadOnlyList<string> AvailableNames
    {
        get
        {
            var names = new List<string> { ByCharacter };
            foreach (VtankProfileDirectory.ProfileEntry entry in
                VtankProfileDirectory.ListMetaProfiles(VtankStorage))
            {
                if (entry.FileName.Length == 0)
                    continue;
                names.Add(StripAf(entry.FileName));
            }
            return names;
        }
    }

    public bool BindCharacter(string? characterName)
    {
        string normalized = string.IsNullOrWhiteSpace(characterName)
            ? string.Empty
            : characterName.Trim();
        if (normalized.Equals(_character, StringComparison.OrdinalIgnoreCase))
            return false;
        _character = normalized;
        _pendingLegacyBareName = null;
        VtankProfileDirectory.VtankCharacterBinding? binding = CanBindFiles
            ? VtankProfileDirectory.TryReadCharacterBinding(VtankStorage, _character, Server)
            : null;
        _selected = binding is { MetaFileName.Length: > 0 } bound
            ? bound.MetaFileName
            : ByCharacter;
        return true;
    }

    public MetaProfile LoadCurrent()
    {
        MigrateFlatFilesToMetasFolderIfNeeded();
        SweepLegacyRosterIfNeeded();
        MigrateLegacyIfNeeded();
        string fileName = CurrentFileName();
        string? text = VtankStorage.IsAvailable ? VtankStorage.ReadText(fileName) : null;
        if (text is null)
            return new MetaProfile();
        if (!MetafSerializer.TryLoadMeta(text, _host.Automation.Spells, out MetaProfile profile, out string error))
        {
            RecoveryNotice = MossTankProfileRecovery.Preserve(
                _host, "meta", fileName, text, new FormatException(error));
            _host.Log.Warn(RecoveryNotice);
            return new MetaProfile();
        }
        return profile;
    }

    public bool SaveCurrent(MetaProfile profile)
    {
        string fileName = CurrentFileName();
        string text;
        try
        {
            text = MetafSerializer.SaveMeta(profile);
        }
        catch (InvalidOperationException error)
        {
            SaveNotice = $"Meta profile '{fileName}' was NOT saved: {error.Message}";
            _host.Log.Warn(SaveNotice);
            return false;
        }
        if (!VtankStorage.IsAvailable)
            return false;
        try
        {
            VtankStorage.WriteText(fileName, text);
        }
        catch (Exception error)
        {
            SaveNotice = $"Meta profile '{fileName}' could not be saved: {error.Message}";
            _host.Log.Warn(SaveNotice);
            return false;
        }
        SaveNotice = null;
        return true;
    }

    public bool Select(string? name)
    {
        string normalized = Normalize(name);
        if (normalized.Length == 0)
            return false;
        if (normalized.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            _selected = ByCharacter;
            _pendingLegacyBareName = null;
            WriteBinding();
            return true;
        }

        string bare = normalized.EndsWith(".af", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".af";
        string plain = $"{VtankProfileDirectory.MetaFolder}/{bare}";
        if (VtankStorage.IsAvailable && VtankStorage.ReadText(plain) is not null)
        {
            _selected = plain;
            _pendingLegacyBareName = null;
            WriteBinding();
            return true;
        }

        if (_host.Storage.IsAvailable
            && _host.Storage.ReadText(LegacyNamedKey(normalized)) is not null)
        {
            _selected = plain;
            _pendingLegacyBareName = normalized;
            WriteBinding();
            return true;
        }
        return false;
    }

    public bool Exists(string? name)
    {
        string normalized = Normalize(name);
        if (normalized.Length == 0)
            return false;
        if (normalized.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
            return true;

        string bare = normalized.EndsWith(".af", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".af";
        string plain = $"{VtankProfileDirectory.MetaFolder}/{bare}";
        if (VtankStorage.IsAvailable && VtankStorage.ReadText(plain) is not null)
            return true;

        return _host.Storage.IsAvailable
            && _host.Storage.ReadText(LegacyNamedKey(normalized)) is not null;
    }

    public bool Create(
        string? name,
        bool copyCurrent,
        MetaProfile current,
        out string notice)
    {
        string normalized = Normalize(name);
        if (normalized.Length is < 1 or > 64
            || normalized.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            notice = "Enter a unique Meta profile name (1-64 characters).";
            return false;
        }
        string bare = normalized.EndsWith(".af", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".af";
        string fileName = $"{VtankProfileDirectory.MetaFolder}/{bare}";
        MetaProfile document = copyCurrent ? Clone(current) : new MetaProfile();
        if (!SaveTo(fileName, document, out notice))
            return false;
        _selected = fileName;
        _pendingLegacyBareName = null;
        WriteBinding();
        notice = copyCurrent
            ? $"Copied Meta profile to {bare}."
            : $"Created Meta profile {bare}.";
        return true;
    }

    public bool TryImportLegacy(
        string? name,
        out MetaProfile profile,
        out string notice)
    {
        string normalized = Normalize(name);
        if (!_host.Storage.IsAvailable || normalized.Length == 0)
        {
            profile = new MetaProfile();
            notice = "Legacy Meta storage is unavailable.";
            return false;
        }
        string? key = _host.Storage.List("imports")
            .Concat(_host.Storage.List("exports"))
            .FirstOrDefault(candidate =>
                candidate.EndsWith(".met", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(candidate).Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
        string? source = key is null ? null : _host.Storage.ReadText(key);
        if (string.IsNullOrWhiteSpace(source))
        {
            profile = new MetaProfile();
            notice = $"VTank Meta file '{normalized}.met' was not found in imports.";
            return false;
        }
        if (!VtankMetaProfileSerializer.TryLoad(
                source, _host.Automation.Spells, out profile, out string error))
        {
            notice = $"Could not import {Path.GetFileName(key)}: {error}";
            return false;
        }
        string bare = normalized.EndsWith(".af", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".af";
        string fileName = $"{VtankProfileDirectory.MetaFolder}/{bare}";
        if (!SaveTo(fileName, profile, out string saveNotice))
        {
            notice = saveNotice;
            return false;
        }
        _selected = fileName;
        _pendingLegacyBareName = null;
        WriteBinding();
        notice = $"Imported VTank Meta profile {bare}.";
        return true;
    }

    public bool Delete(out string notice)
    {
        if (_selected.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            notice = "'By char' is the built-in Meta profile and cannot be deleted.";
            return false;
        }
        string fileName = _selected;
        if (VtankStorage.IsAvailable)
            VtankStorage.Delete(fileName);
        _selected = ByCharacter;
        _pendingLegacyBareName = null;
        WriteBinding();
        notice = $"Deleted Meta profile {StripAf(fileName)}.";
        return true;
    }

    public MetaProfile ClearCurrent()
    {
        var empty = new MetaProfile();
        SaveCurrent(empty);
        return empty;
    }


    private void MigrateFlatFilesToMetasFolderIfNeeded()
    {
        if (_flatFolderMigrationSwept)
            return;
        _flatFolderMigrationSwept = true;
        if (!VtankStorage.IsAvailable)
            return;
        int migrated = 0;
        foreach (string bareName in VtankProfileDirectory.ListFlatAfFileNames(VtankStorage))
        {
            if (VtankProfileDirectory.IsLegacyFlatRouteFileName(bareName))
                continue; // MossTankRouteProfileStore's own sweep owns this one.
            string destination = $"{VtankProfileDirectory.MetaFolder}/{bareName}";
            if (VtankStorage.ReadText(destination) is not null)
            {
                _host.Log.Warn(
                    $"MossTank left flat Meta profile '{bareName}' in place: '{destination}' already exists.");
                continue;
            }
            string? content = VtankStorage.ReadText(bareName);
            if (content is null)
                continue; // listed but unreadable; skip defensively.
            VtankStorage.WriteText(destination, content);
            VtankStorage.Delete(bareName);
            migrated++;
        }
        if (migrated > 0)
        {
            _host.Log.Warn(
                $"Migrated {migrated} flat MossTank Meta profile(s) into {VtankProfileDirectory.MetaFolder}/.");
        }
    }

    // ------------------------------------------------------------------
    // Legacy JSON -> .af migration.
    // ------------------------------------------------------------------

    private void SweepLegacyRosterIfNeeded()
    {
        if (_rosterSwept)
            return;
        _rosterSwept = true;
        if (!_host.Storage.IsAvailable)
            return;
        List<string>? names = ReadRosterNames();
        if (names is not { Count: > 0 })
            return;

        var remaining = new List<string>();
        int migrated = 0;
        foreach (string rawName in names)
        {
            string name = (rawName ?? string.Empty).Trim();
            if (name.Length == 0 || name.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
                continue; // stale/invalid row; drop it rather than loop on it forever.

            string legacyKey = LegacyNamedKey(name);
            MetaProfile? legacy = ReadLegacyJson(legacyKey);
            if (legacy is null)
                continue;

            string bare = name.EndsWith(".af", StringComparison.OrdinalIgnoreCase)
                ? name
                : name + ".af";
            string fileName = $"{VtankProfileDirectory.MetaFolder}/{bare}";
            if (VtankStorage.IsAvailable && VtankStorage.ReadText(fileName) is null)
            {
                if (!SaveTo(fileName, legacy, out string notice))
                {
                    // Representational loss (a disabled rule) — same gate as
                    // MigrateLegacyIfNeeded: keep the row so this can be
                    // retried once the user resolves it.
                    _host.Log.Warn($"MossTank could not sweep legacy Meta profile '{name}': {notice}");
                    remaining.Add(name);
                    continue;
                }
            }
            _host.Storage.Delete(legacyKey);
            migrated++;
        }

        if (migrated == 0)
            return;

        if (remaining.Count > 0)
        {
            _host.Storage.WriteText(
                LegacyRosterKey,
                JsonSerializer.Serialize(new LegacyRosterDocument { Names = remaining }, JsonOptions));
        }
        else
        {
            _host.Storage.Delete(LegacyRosterKey);
        }
        _host.Log.Warn($"Migrated {migrated} legacy MossTank named Meta profile(s) from the old roster.");
    }

    private List<string>? ReadRosterNames()
    {
        string? json = null;
        try
        {
            json = _host.Storage.ReadText(LegacyRosterKey);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<LegacyRosterDocument>(json, JsonOptions)?.Names;
        }
        catch (Exception error)
        {
            RecoveryNotice = MossTankProfileRecovery.Preserve(
                _host, "meta", LegacyRosterKey, json, error);
            _host.Log.Warn(RecoveryNotice);
            return null;
        }
    }

    private void MigrateLegacyIfNeeded()
    {
        string fileName = CurrentFileName();
        if (!VtankStorage.IsAvailable || VtankStorage.ReadText(fileName) is not null)
        {
            _pendingLegacyBareName = null;
            return;
        }
        string legacyKey = _pendingLegacyBareName is { Length: > 0 } bareName
            ? LegacyNamedKey(bareName)
            : LegacyByCharacterKey();
        MetaProfile? legacy = ReadLegacyJson(legacyKey);
        if (legacy is null)
        {
            _pendingLegacyBareName = null;
            return;
        }
        if (!SaveTo(fileName, legacy, out string notice))
        {
            // Representational loss (a disabled rule) blocks the migration
            // outright rather than silently dropping it: the legacy JSON
            // stays put (still fully readable) until the user resolves it.
            _host.Log.Warn($"MossTank could not migrate legacy Meta profile: {notice}");
            _pendingLegacyBareName = null;
            return;
        }
        if (_host.Storage.IsAvailable)
            _host.Storage.Delete(legacyKey);
        _pendingLegacyBareName = null;
        _host.Log.Warn($"Migrated legacy MossTank Meta profile '{legacyKey}' to '{fileName}'.");
    }

    private MetaProfile? ReadLegacyJson(string key)
    {
        if (!_host.Storage.IsAvailable)
            return null;
        string? json = null;
        try
        {
            json = _host.Storage.ReadText(key);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<MetaProfile>(json, JsonOptions);
        }
        catch (Exception error)
        {
            RecoveryNotice = MossTankProfileRecovery.Preserve(_host, "meta", key, json, error);
            _host.Log.Error(RecoveryNotice, error);
            return null;
        }
    }

    private bool SaveTo(string fileName, MetaProfile profile, out string notice)
    {
        string text;
        try
        {
            text = MetafSerializer.SaveMeta(profile);
        }
        catch (InvalidOperationException error)
        {
            notice = $"Meta profile '{fileName}' was NOT saved: {error.Message}";
            SaveNotice = notice;
            _host.Log.Warn(notice);
            return false;
        }
        if (!VtankStorage.IsAvailable)
        {
            notice = "VTank Meta storage is unavailable.";
            return false;
        }
        try
        {
            VtankStorage.WriteText(fileName, text);
        }
        catch (Exception error)
        {
            notice = $"Meta profile '{fileName}' could not be saved: {error.Message}";
            SaveNotice = notice;
            _host.Log.Warn(notice);
            return false;
        }
        SaveNotice = null;
        notice = string.Empty;
        return true;
    }

    // ------------------------------------------------------------------
    // File naming, storage plumbing.
    // ------------------------------------------------------------------

    private string CurrentFileName() => _selected.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase)
        ? $"{VtankProfileDirectory.MetaFolder}/{VtankProfileDirectory.AutoCharacterFileName(_character, Server, "af")}"
        : _selected;

    private void WriteBinding()
    {
        if (!CanBindFiles || !VtankStorage.IsAvailable)
            return;
        VtankProfileDirectory.VtankCharacterBinding existing =
            VtankProfileDirectory.TryReadCharacterBinding(VtankStorage, _character, Server)
            ?? new VtankProfileDirectory.VtankCharacterBinding(
                string.Empty, string.Empty, string.Empty, CurrentFileName());
        VtankProfileDirectory.WriteCharacterBinding(
            VtankStorage,
            _character,
            Server,
            existing with { MetaFileName = CurrentFileName() });
    }

    private static MetaProfile Clone(MetaProfile profile) =>
        JsonSerializer.Deserialize<MetaProfile>(
            JsonSerializer.Serialize(profile, JsonOptions),
            JsonOptions) ?? new MetaProfile();

    private static string Normalize(string? name) => name?.Trim() ?? string.Empty;

    private string LegacyByCharacterKey() =>
        $"profiles/meta/by-character/{Hash(_character)}.json";

    private static string LegacyNamedKey(string name) =>
        $"profiles/meta/named/{Hash(name)}.json";

    private static string Hash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class LegacyRosterDocument
    {
        public List<string> Names { get; set; } = [];
    }
}
