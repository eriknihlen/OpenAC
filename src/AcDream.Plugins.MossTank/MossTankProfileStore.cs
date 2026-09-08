using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class MossTankProfileStore
{
    public const string ByCharacter = "By char";

    private const string OldSingleProfileKey = "profile.json";
    private const string LegacyRosterKey = "profiles/index.json";
    private const string PreferencesKey = "profiles/macro/preferences.json";
    private const string SideCarDirectory = "profiles/macro/sidecar/";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IPluginHost _host;
    private PreferencesDocument _preferences;
    private string _characterName = string.Empty;
    private string _selected = ByCharacter;
    private string? _pendingLegacyBareName;
    private VtankDatabase? _currentDatabase;
    private string? _currentDatabaseFileName;
    private bool _rosterSwept;

    public MossTankProfileStore(IPluginHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _preferences = ReadJson<PreferencesDocument>(PreferencesKey)
            ?? ReadJson<PreferencesDocument>(LegacyRosterKey)
            ?? new PreferencesDocument();
    }

    public string Selected => _selected;
    public bool MineOnly => _preferences.MineOnly;
    public string? RecoveryNotice { get; private set; }

    private string Server => _host.Automation.Character.WorldName;
    private IPluginStorage VtankStorage => _host.VtankProfiles;

    public IReadOnlyList<string> AvailableNames
    {
        get
        {
            var names = new List<string> { ByCharacter };
            foreach (VtankProfileDirectory.ProfileEntry entry in VtankProfileDirectory.ListSettingsProfiles(
                VtankStorage, _characterName, Server, MineOnly, CurrentFileName()))
            {
                if (entry.FileName.Length == 0)
                    continue;
                names.Add(entry.FileName);
            }
            return names;
        }
    }

    public bool BindCharacter(string? characterName)
    {
        string normalized = string.IsNullOrWhiteSpace(characterName)
            ? string.Empty
            : characterName.Trim();
        if (string.Equals(normalized, _characterName, StringComparison.OrdinalIgnoreCase))
            return false;

        _characterName = normalized;
        _pendingLegacyBareName = null;
        _currentDatabase = null;
        _currentDatabaseFileName = null;
        VtankProfileDirectory.VtankCharacterBinding? binding = CanBindFiles
            ? VtankProfileDirectory.TryReadCharacterBinding(VtankStorage, _characterName, Server)
            : null;
        _selected = binding is { SettingsFileName.Length: > 0 } bound
            ? bound.SettingsFileName
            : ByCharacter;
        return true;
    }

    private bool CanBindFiles => _characterName.Length > 0 && Server.Length > 0;

    public void SetMineOnly(bool value)
    {
        if (_preferences.MineOnly == value)
            return;
        _preferences.MineOnly = value;
        if (!AvailableNames.Contains(_selected, StringComparer.Ordinal))
            _selected = ByCharacter;
        SavePreferences();
    }

    public bool Select(string? name)
    {
        string normalized = NormalizeName(name);
        if (normalized.Length == 0)
            return false;
        if (normalized.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            _selected = ByCharacter;
            _pendingLegacyBareName = null;
            WriteBinding();
            return true;
        }

        string? existing = AvailableNames.FirstOrDefault(candidate =>
                candidate.Equals(normalized, StringComparison.Ordinal))
            ?? AvailableNames.FirstOrDefault(candidate =>
                candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _selected = existing;
            _pendingLegacyBareName = null;
            WriteBinding();
            return true;
        }

        string subProfile = VtankProfileDirectory.SubProfilePrefix(_characterName, Server)
            + normalized + ".usd";
        if (VtankStorage.IsAvailable && VtankStorage.ReadText(subProfile) is not null)
        {
            _selected = subProfile;
            _pendingLegacyBareName = null;
            WriteBinding();
            return true;
        }

        if (_host.Storage.IsAvailable
            && _host.Storage.ReadText(LegacyProfileKey(normalized, byCharacter: false)) is not null)
        {
            // Not on disk as a .usd yet: LoadCurrent's migration materializes
            // it at this exact sub-profile file name.
            _selected = subProfile;
            _pendingLegacyBareName = normalized;
            WriteBinding();
            return true;
        }

        return false;
    }

    public bool Create(
        string? name,
        bool copyCurrent,
        VtankSettingsProfileSerializer.AllSettings settings,
        ISet<string> noBuffItemNames,
        out string notice)
    {
        string normalized = NormalizeName(name);
        if (!ValidNamedProfile(normalized, out notice))
            return false;

        string fileName = VtankProfileDirectory.SubProfilePrefix(_characterName, Server)
            + normalized + ".usd";
        VtankDatabase database;
        SideCarDocument sidecar;
        if (copyCurrent)
        {
            database = VtankSettingsProfileSerializer.CreateNew(settings);
            sidecar = SideCarDocument.Capture(settings, noBuffItemNames);
        }
        else
        {
            database = VtankDefaultSettingsDatabase.Parse();
            ApplyFromDatabase(database, settings);
            sidecar = SideCarDocument.CreateDefaults();
            sidecar.Apply(settings, noBuffItemNames, _host.Log);
        }
        WriteUsdText(fileName, database.Render());
        WriteJson(SideCarKey(fileName), sidecar);
        _currentDatabase = database;
        _currentDatabaseFileName = fileName;
        _selected = fileName;
        _pendingLegacyBareName = null;
        WriteBinding();
        notice = copyCurrent
            ? $"Copied current settings to {fileName}."
            : $"Created profile {fileName}.";
        return true;
    }

    public bool Delete(out string notice)
    {
        if (_selected.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            notice = "'By char' is the built-in character profile and cannot be deleted.";
            return false;
        }
        string fileName = _selected;
        if (VtankStorage.IsAvailable)
            VtankStorage.Delete(fileName);
        if (_host.Storage.IsAvailable)
            _host.Storage.Delete(SideCarKey(fileName));
        _selected = ByCharacter;
        _pendingLegacyBareName = null;
        _currentDatabase = null;
        _currentDatabaseFileName = null;
        WriteBinding();
        notice = $"Deleted profile {fileName}.";
        return true;
    }

    public void LoadCurrent(
        VtankSettingsProfileSerializer.AllSettings settings,
        ISet<string> noBuffItemNames)
    {
        SweepLegacyRosterIfNeeded();
        MigrateLegacyIfNeeded(settings, noBuffItemNames);
        string fileName = CurrentFileName();
        string? text = ReadUsdText(fileName);
        if (text is null)
        {
            // VTank's own behavior for a missing file: seed from the shipped
            // template/defaults rather than leaving the live settings alone.
            VtankDatabase fresh = VtankDefaultSettingsDatabase.Parse();
            ApplyFromDatabase(fresh, settings);
            SideCarDocument.CreateDefaults().Apply(settings, noBuffItemNames, _host.Log);
            _currentDatabase = fresh;
            _currentDatabaseFileName = fileName;
            return;
        }

        VtankDatabase database;
        try
        {
            database = VtankSettingsProfileSerializer.Load(text, settings);
        }
        catch (FormatException error)
        {
            RecoveryNotice = MossTankProfileRecovery.Preserve(
                _host, "macro", fileName, text, error);
            _host.Log.Warn(RecoveryNotice);
            VtankDatabase fresh = VtankDefaultSettingsDatabase.Parse();
            ApplyFromDatabase(fresh, settings);
            SideCarDocument.CreateDefaults().Apply(settings, noBuffItemNames, _host.Log);
            _currentDatabase = fresh;
            _currentDatabaseFileName = fileName;
            return;
        }
        _currentDatabase = database;
        _currentDatabaseFileName = fileName;
        (ReadJson<SideCarDocument>(SideCarKey(fileName)) ?? SideCarDocument.CreateDefaults())
            .Apply(settings, noBuffItemNames, _host.Log);
    }

    public void SaveCurrent(
        VtankSettingsProfileSerializer.AllSettings settings,
        ISet<string> noBuffItemNames)
    {
        string fileName = CurrentFileName();
        VtankDatabase database = fileName.Equals(_currentDatabaseFileName, StringComparison.Ordinal)
            && _currentDatabase is not null
                ? _currentDatabase
                : VtankSettingsProfileSerializer.CreateNew(settings);
        string text = VtankSettingsProfileSerializer.Save(database, settings);
        WriteUsdText(fileName, text);
        WriteJson(SideCarKey(fileName), SideCarDocument.Capture(settings, noBuffItemNames));
        _currentDatabase = database;
        _currentDatabaseFileName = fileName;
    }

    public void ClearCurrent(
        VtankSettingsProfileSerializer.AllSettings settings,
        ISet<string> noBuffItemNames)
    {
        VtankDatabase database = VtankDefaultSettingsDatabase.Parse();
        ApplyFromDatabase(database, settings);
        SideCarDocument.CreateDefaults().Apply(settings, noBuffItemNames, _host.Log);
        string fileName = CurrentFileName();
        WriteUsdText(fileName, database.Render());
        WriteJson(SideCarKey(fileName), SideCarDocument.CreateDefaults());
        _currentDatabase = database;
        _currentDatabaseFileName = fileName;
    }

    public int SetOptionInAll(string name, VtankSettingsProfileSerializer.AllSettings current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!VtankOptionCatalog.IsKnown(name) || !VtankStorage.IsAvailable)
            return 0;
        string canonical = VtankOptionCatalog.Canonical(name);
        VtankCell? captured = VtankSettingsProfileSerializer.Capture(canonical, current);
        if (captured is null)
            return 0;

        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in VtankStorage.List(string.Empty))
        {
            if (!key.Contains('/', StringComparison.Ordinal)
                && key.EndsWith(".usd", StringComparison.OrdinalIgnoreCase))
            {
                fileNames.Add(key);
            }
        }
        fileNames.Add(CurrentFileName());

        DynamicSettingDocument? dynamicDocument = current.Combat.DynamicSettings.TryGetValue(
            canonical, out MonsterValue liveDynamicValue)
                ? DynamicSettingDocument.From(liveDynamicValue)
                : null;

        foreach (string fileName in fileNames)
        {
            string? text = ReadUsdText(fileName);
            VtankDatabase database;
            try
            {
                database = string.IsNullOrEmpty(text)
                    ? VtankDefaultSettingsDatabase.Parse()
                    : VtankDatabase.Parse(text);
            }
            catch (FormatException error)
            {
                _host.Log.Warn($"MossTank could not update '{fileName}' for setinall: {error.Message}");
                continue;
            }
            VtankTable? table = database.Find("Settings");
            int nameColumn = table?.ColumnIndex("Setting") ?? -1;
            int valueColumn = table?.ColumnIndex("Value") ?? -1;
            if (table is null || nameColumn < 0 || valueColumn < 0)
                continue;
            VtankRow? row = table.Rows.FirstOrDefault(candidate =>
                candidate.Cells[nameColumn].AsString().Equals(
                    canonical, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new VtankRow();
                for (int column = 0; column < table.ColumnNames.Count; column++)
                    row.Cells.Add(new VtankCell { Tag = "0" });
                row.Cells[nameColumn] = VtankCell.String(canonical);
                table.Rows.Add(row);
            }
            row.Cells[valueColumn] = captured;
            WriteUsdText(fileName, database.Render());
            if (fileName.Equals(_currentDatabaseFileName, StringComparison.Ordinal))
                _currentDatabase = database;
            if (dynamicDocument is not null)
            {
                SideCarDocument sidecar = ReadJson<SideCarDocument>(SideCarKey(fileName))
                    ?? SideCarDocument.CreateDefaults();
                sidecar.CombatDynamicSettings[canonical] = dynamicDocument;
                WriteJson(SideCarKey(fileName), sidecar);
            }
        }
        return fileNames.Count;
    }

    // ------------------------------------------------------------------
    // Legacy JSON -> .usd/.json side-car migration.
    // ------------------------------------------------------------------

    private void SweepLegacyRosterIfNeeded()
    {
        if (_rosterSwept)
            return;
        _rosterSwept = true;
        LegacyRosterDocument? roster = ReadJson<LegacyRosterDocument>(LegacyRosterKey);
        if (roster?.Profiles is not { Count: > 0 } profiles)
            return;

        var remaining = new List<LegacyRosterEntry>();
        int migrated = 0;
        foreach (LegacyRosterEntry entry in profiles)
        {
            string name = (entry.Name ?? string.Empty).Trim();
            if (name.Length == 0 || name.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
                continue; // stale/invalid row; drop it rather than loop on it forever.

            bool ownedByCurrentCharacter = string.IsNullOrWhiteSpace(entry.Owner)
                || entry.Owner.Trim().Equals(_characterName, StringComparison.OrdinalIgnoreCase);
            if (!ownedByCurrentCharacter)
            {
                remaining.Add(entry);
                continue;
            }

            string legacyKey = LegacyProfileKey(name, byCharacter: false);
            LegacyProfileDocument? legacy = ReadJson<LegacyProfileDocument>(legacyKey);
            if (legacy is null)
                continue;

            string fileName = VtankProfileDirectory.SubProfilePrefix(_characterName, Server)
                + name + ".usd";
            if (ReadUsdText(fileName) is null)
            {
                var settings = new VtankSettingsProfileSerializer.AllSettings
                {
                    Combat = new CombatSettings(),
                    Buffs = new BuffSettings(),
                    Vitals = new VitalSettings(),
                    Inventory = new InventorySettings(),
                    Navigation = new NavigationSettings(),
                };
                var noBuffItemNames = new HashSet<string>(StringComparer.Ordinal);
                legacy.Apply(
                    settings.Combat, settings.Buffs, settings.Vitals, settings.Inventory,
                    noBuffItemNames, _host.Log);
                VtankDatabase database = VtankSettingsProfileSerializer.CreateNew(settings);
                WriteUsdText(fileName, database.Render());
                WriteJson(SideCarKey(fileName), SideCarDocument.Capture(settings, noBuffItemNames));
            }
            if (_host.Storage.IsAvailable)
                _host.Storage.Delete(legacyKey);
            migrated++;
        }

        if (migrated == 0)
            return;

        if (remaining.Count > 0)
            WriteJson(LegacyRosterKey, new LegacyRosterDocument { Profiles = remaining });
        else if (_host.Storage.IsAvailable)
            _host.Storage.Delete(LegacyRosterKey);
        WriteJson(PreferencesKey, _preferences);
        _host.Log.Warn(
            $"Migrated {migrated} legacy MossTank named settings profile(s) from the old roster.");
    }

    /// <summary>
    /// Runs once per store (per selection) when the resolved <c>.usd</c>
    /// file does not exist yet but the equivalent old JSON profile document
    /// does: applies the JSON onto the live settings, writes the real
    /// <c>.usd</c> + side-car pair, and deletes the JSON key. Never
    /// overwrites an existing <c>.usd</c> file, and is a no-op (not an
    /// error) when neither exists — a brand-new profile.
    /// </summary>
    private void MigrateLegacyIfNeeded(
        VtankSettingsProfileSerializer.AllSettings settings,
        ISet<string> noBuffItemNames)
    {
        string fileName = CurrentFileName();
        if (ReadUsdText(fileName) is not null)
        {
            _pendingLegacyBareName = null;
            return;
        }

        string? legacyKey = _pendingLegacyBareName is { Length: > 0 } bareName
            ? LegacyProfileKey(bareName, byCharacter: false)
            : _selected.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase)
                ? LegacyProfileKey(_characterName, byCharacter: true)
                : null;
        LegacyProfileDocument? legacy = legacyKey is null ? null : ReadJson<LegacyProfileDocument>(legacyKey);
        legacyKey ??= OldSingleProfileKey;
        if (legacy is null
            && _selected.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            legacy = ReadJson<LegacyProfileDocument>(OldSingleProfileKey);
            legacyKey = OldSingleProfileKey;
        }
        if (legacy is null)
        {
            _pendingLegacyBareName = null;
            return;
        }

        legacy.Apply(settings.Combat, settings.Buffs, settings.Vitals, settings.Inventory, noBuffItemNames, _host.Log);
        VtankDatabase database = VtankSettingsProfileSerializer.CreateNew(settings);
        WriteUsdText(fileName, database.Render());
        WriteJson(SideCarKey(fileName), SideCarDocument.Capture(settings, noBuffItemNames));
        _currentDatabase = database;
        _currentDatabaseFileName = fileName;
        if (_host.Storage.IsAvailable)
            _host.Storage.Delete(legacyKey);
        _pendingLegacyBareName = null;
        _host.Log.Warn(
            $"Migrated legacy MossTank settings profile '{legacyKey}' to '{fileName}'.");
    }

    // ------------------------------------------------------------------
    // File naming, storage plumbing.
    // ------------------------------------------------------------------

    private string CurrentFileName() => _selected.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase)
        ? VtankProfileDirectory.AutoCharacterFileName(_characterName, Server, "usd")
        : _selected;

    private void WriteBinding()
    {
        if (!CanBindFiles || !VtankStorage.IsAvailable)
            return;
        VtankProfileDirectory.VtankCharacterBinding existing =
            VtankProfileDirectory.TryReadCharacterBinding(VtankStorage, _characterName, Server)
            ?? new VtankProfileDirectory.VtankCharacterBinding(
                CurrentFileName(), string.Empty, string.Empty, null);
        VtankProfileDirectory.WriteCharacterBinding(
            VtankStorage,
            _characterName,
            Server,
            existing with { SettingsFileName = CurrentFileName() });
    }

    private static void ApplyFromDatabase(
        VtankDatabase database,
        VtankSettingsProfileSerializer.AllSettings target)
    {
        VtankTable? settings = database.Find("Settings");
        int nameColumn = settings?.ColumnIndex("Setting") ?? -1;
        int valueColumn = settings?.ColumnIndex("Value") ?? -1;
        if (settings is null || nameColumn < 0 || valueColumn < 0)
            return;
        foreach (VtankRow row in settings.Rows)
        {
            VtankSettingsProfileSerializer.Apply(
                row.Cells[nameColumn].AsString(), row.Cells[valueColumn], target);
        }
    }

    private static bool ValidNamedProfile(string name, out string notice)
    {
        if (name.Length is < 1 or > 64)
        {
            notice = "Enter a profile name (1-64 characters).";
            return false;
        }
        if (name.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            notice = "'By char' is the built-in character profile.";
            return false;
        }
        notice = string.Empty;
        return true;
    }

    private static string LegacyProfileKey(string value, bool byCharacter)
    {
        string identity = (byCharacter ? "char:" : "named:")
            + value.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity)));
        return $"profiles/macro/{hash}.json";
    }

    private static string SideCarKey(string fileName) =>
        SideCarDirectory + Sanitize(fileName) + ".json";

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var result = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
            result.Append(invalid.Contains(c) ? '_' : c);
        return result.ToString();
    }

    private static string NormalizeName(string? name) => name?.Trim() ?? string.Empty;

    private string? ReadUsdText(string fileName) =>
        VtankStorage.IsAvailable ? VtankStorage.ReadText(fileName) : null;

    private void WriteUsdText(string fileName, string text)
    {
        if (!VtankStorage.IsAvailable)
            return;
        try
        {
            VtankStorage.WriteText(fileName, text);
        }
        catch (Exception error)
        {
            _host.Log.Warn($"MossTank could not save settings profile '{fileName}': {error.Message}");
        }
    }

    private T? ReadJson<T>(string key) where T : class
    {
        if (!_host.Storage.IsAvailable)
            return null;
        string? json = null;
        try
        {
            json = _host.Storage.ReadText(key);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception error)
        {
            RecoveryNotice = MossTankProfileRecovery.Preserve(
                _host, "macro", key, json, error);
            _host.Log.Warn(RecoveryNotice);
            return null;
        }
    }

    private void WriteJson<T>(string key, T document)
    {
        if (!_host.Storage.IsAvailable)
            return;
        try
        {
            _host.Storage.WriteText(key, JsonSerializer.Serialize(document, Options));
        }
        catch (Exception error)
        {
            _host.Log.Warn($"MossTank profile could not be saved: {error.Message}");
        }
    }

    private void SavePreferences() => WriteJson(PreferencesKey, _preferences);

    private sealed class PreferencesDocument
    {
        public bool MineOnly { get; set; } = true;
    }


    private sealed class LegacyRosterDocument
    {
        public List<LegacyRosterEntry> Profiles { get; set; } = [];
    }

    private sealed class LegacyRosterEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
    }


    private sealed class SideCarDocument
    {
        public int Version { get; set; } = 1;
        public string[] ItemNames { get; set; } = [];
        public string[] ConsumableNames { get; set; } = [];
        public Dictionary<string, ConsumableCategory> ConsumableCategories { get; set; } =
            new(StringComparer.Ordinal);
        public string[] NoBuffItemNames { get; set; } = [];
        public float CombatAttackPower { get; set; } = 0.5f;
        public double CombatScanIntervalSeconds { get; set; } = 0.25d;
        public string CombatMetaState { get; set; } = "Default";
        public Dictionary<string, DynamicSettingDocument> CombatDynamicSettings { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public MonsterRuleDocument[] CombatRules { get; set; } =
            [MonsterRuleDocument.From(new MonsterRule("DEFAULT", 0))];
        public bool BuffAttributes { get; set; } = true;
        public bool BuffProtections { get; set; } = true;
        public bool BuffAuras { get; set; } = true;
        public bool BuffBanes { get; set; } = true;
        public bool BuffRegeneration { get; set; } = true;
        public bool BuffOther { get; set; }
        public bool BuffTrainedSkillsOnly { get; set; } = true;
        public string[] BuffExtraSpellNames { get; set; } = [];
        public string[] BuffBlacklistedFamilyNames { get; set; } = [];
        public bool VitalsEnabled { get; set; } = true;
        public double InventoryScanIntervalSeconds { get; set; } = 0.25d;
        public string InventoryLootClassifierId { get; set; } = string.Empty;
        public double InventoryLootScanIntervalSeconds { get; set; } = 0.25d;
        public LootRuleDocument[] InventoryLootRules { get; set; } = [];

        public static SideCarDocument Capture(
            VtankSettingsProfileSerializer.AllSettings settings,
            ISet<string> noBuffItemNames) => new()
        {
            ItemNames = Sorted(settings.Combat.CombatItemNames),
            ConsumableNames = Sorted(settings.Combat.ConsumableNames),
            ConsumableCategories = settings.Combat.ConsumableCategories.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal),
            NoBuffItemNames = Sorted(noBuffItemNames),
            CombatAttackPower = settings.Combat.AttackPower,
            CombatScanIntervalSeconds = settings.Combat.ScanIntervalSeconds,
            CombatMetaState = settings.Combat.MetaState,
            CombatDynamicSettings = settings.Combat.DynamicSettings.ToDictionary(
                static pair => pair.Key,
                static pair => DynamicSettingDocument.From(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            CombatRules = settings.Combat.Rules.Select(MonsterRuleDocument.From).ToArray(),
            BuffAttributes = settings.Buffs.BuffAttributes,
            BuffProtections = settings.Buffs.BuffProtections,
            BuffAuras = settings.Buffs.BuffAuras,
            BuffBanes = settings.Buffs.BuffBanes,
            BuffRegeneration = settings.Buffs.BuffRegeneration,
            BuffOther = settings.Buffs.BuffOther,
            BuffTrainedSkillsOnly = settings.Buffs.BuffTrainedSkillsOnly,
            BuffExtraSpellNames = Sorted(settings.Buffs.ExtraBuffSpellNames),
            BuffBlacklistedFamilyNames = Sorted(settings.Buffs.BlacklistedBuffFamilyNames),
            VitalsEnabled = settings.Vitals.Enabled,
            InventoryScanIntervalSeconds = settings.Inventory.ScanIntervalSeconds,
            InventoryLootClassifierId = settings.Inventory.Loot.ExternalClassifierId,
            InventoryLootScanIntervalSeconds = settings.Inventory.Loot.ScanIntervalSeconds,
            InventoryLootRules = settings.Inventory.Loot.Rules
                .Select(LootRuleDocument.From)
                .ToArray(),
        };

        public static SideCarDocument CreateDefaults() => Capture(
            new VtankSettingsProfileSerializer.AllSettings
            {
                Combat = new CombatSettings(),
                Buffs = new BuffSettings(),
                Vitals = new VitalSettings(),
                Inventory = new InventorySettings(),
                Navigation = new NavigationSettings(),
            },
            new HashSet<string>(StringComparer.Ordinal));

        public void Apply(
            VtankSettingsProfileSerializer.AllSettings settings,
            ISet<string> noBuffItemNames,
            IPluginLogger? logger = null)
        {
            Replace(settings.Combat.CombatItemNames, ItemNames);
            settings.Combat.CombatItemObjectIds.Clear();
            Replace(settings.Combat.ConsumableNames, ConsumableNames);
            settings.Combat.ConsumableCategories.Clear();
            foreach ((string itemName, ConsumableCategory category) in
                ConsumableCategories ?? new Dictionary<string, ConsumableCategory>())
            {
                if (settings.Combat.ConsumableNames.Contains(itemName))
                    settings.Combat.ConsumableCategories[itemName] = category;
            }
            Replace(noBuffItemNames, NoBuffItemNames);
            settings.Combat.AttackPower = Math.Clamp(CombatAttackPower, 0f, 1f);
            settings.Combat.ScanIntervalSeconds = Math.Clamp(CombatScanIntervalSeconds, 0.05d, 5d);
            settings.Combat.MetaState = string.IsNullOrWhiteSpace(CombatMetaState)
                ? "Default"
                : CombatMetaState;
            settings.Combat.DynamicSettings.Clear();
            foreach ((string optionName, DynamicSettingDocument setting) in
                CombatDynamicSettings ?? new Dictionary<string, DynamicSettingDocument>())
            {
                settings.Combat.DynamicSettings[optionName] = setting.ToValue();
            }
            settings.Combat.Rules.Clear();
            foreach (MonsterRuleDocument rule in CombatRules ?? [])
            {
                try { settings.Combat.Rules.Add(rule.ToRule()); }
                catch (FormatException error)
                {
                    logger?.Warn($"MossTank could not restore a monster rule: {error.Message}");
                }
            }
            if (!settings.Combat.Rules.Any(static rule => rule.IsDefault))
                settings.Combat.Rules.Add(new MonsterRule("DEFAULT", 0));

            settings.Buffs.BuffAttributes = BuffAttributes;
            settings.Buffs.BuffProtections = BuffProtections;
            settings.Buffs.BuffAuras = BuffAuras;
            settings.Buffs.BuffBanes = BuffBanes;
            settings.Buffs.BuffRegeneration = BuffRegeneration;
            settings.Buffs.BuffOther = BuffOther;
            settings.Buffs.BuffTrainedSkillsOnly = BuffTrainedSkillsOnly;
            Replace(settings.Buffs.ExtraBuffSpellNames, BuffExtraSpellNames);
            Replace(settings.Buffs.BlacklistedBuffFamilyNames, BuffBlacklistedFamilyNames);

            settings.Vitals.Enabled = VitalsEnabled;

            settings.Inventory.ScanIntervalSeconds = Math.Clamp(
                InventoryScanIntervalSeconds, 0.05d, 10d);
            settings.Inventory.Loot.ExternalClassifierId = InventoryLootClassifierId?.Trim()
                ?? string.Empty;
            settings.Inventory.Loot.ScanIntervalSeconds = Math.Clamp(
                InventoryLootScanIntervalSeconds, 0.05d, 5d);
            settings.Inventory.Loot.Rules.Clear();
            foreach (LootRuleDocument rule in InventoryLootRules ?? [])
                settings.Inventory.Loot.Rules.Add(rule.ToRule());
        }
    }

    private static string[] Sorted(IEnumerable<string> values) => values
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static value => value, StringComparer.Ordinal)
        .ToArray();

    private static void Replace(ISet<string> target, IEnumerable<string>? values)
    {
        target.Clear();
        if (values is null)
            return;
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                target.Add(value);
        }
    }

    private sealed class DynamicSettingDocument
    {
        public MonsterValueKind Kind { get; set; }
        public double Number { get; set; }
        public string Text { get; set; } = string.Empty;
        public bool Boolean { get; set; }

        public static DynamicSettingDocument From(MonsterValue value) => new()
        {
            Kind = value.Kind,
            Number = value.Number,
            Text = value.Text,
            Boolean = value.Boolean,
        };

        public MonsterValue ToValue() => Kind switch
        {
            MonsterValueKind.Number => MonsterValue.FromNumber(Number),
            MonsterValueKind.Boolean => MonsterValue.FromBoolean(Boolean),
            _ => MonsterValue.FromText(Text ?? string.Empty),
        };
    }

    private sealed class LootRuleDocument
    {
        public string Name { get; set; } = "Rule";
        public string Expression { get; set; } = "*";
        public LootAction Action { get; set; } = LootAction.Keep;
        public int KeepCount { get; set; } = 1;
        public int Priority { get; set; }

        public static LootRuleDocument From(LootRule rule) => new()
        {
            Name = rule.Name,
            Expression = rule.Expression,
            Action = rule.Action,
            KeepCount = rule.KeepCount,
            Priority = rule.Priority,
        };

        public LootRule ToRule() => new()
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "Rule" : Name.Trim(),
            Expression = string.IsNullOrWhiteSpace(Expression) ? "*" : Expression.Trim(),
            Action = Action,
            KeepCount = Math.Clamp(KeepCount, 0, 100000),
            Priority = Math.Clamp(Priority, -1000, 1000),
        };
    }

    private sealed class MonsterRuleDocument
    {
        public string Expression { get; set; } = "DEFAULT";
        public MonsterActionFlags Flags { get; set; } = MonsterActionFlags.Attack;
        public int Priority { get; set; }
        public MonsterDamageType DamageType { get; set; } = MonsterDamageType.Auto;
        public MonsterDamageType ExtraVulnerability { get; set; } = MonsterDamageType.Auto;
        public uint WeaponObjectId { get; set; }
        public uint OffhandObjectId { get; set; }
        public string WeaponName { get; set; } = string.Empty;
        public string OffhandName { get; set; } = string.Empty;
        public MonsterDamageType PetDamageType { get; set; } = MonsterDamageType.PlayerAuto;

        public static MonsterRuleDocument From(MonsterRule rule) => new()
        {
            Expression = rule.Expression,
            Flags = rule.Actions.Flags,
            Priority = rule.Actions.Priority,
            DamageType = rule.Actions.DamageType,
            ExtraVulnerability = rule.Actions.ExtraVulnerability,
            WeaponObjectId = rule.Actions.WeaponObjectId,
            OffhandObjectId = rule.Actions.OffhandObjectId,
            WeaponName = rule.Actions.WeaponName,
            OffhandName = rule.Actions.OffhandName,
            PetDamageType = rule.Actions.PetDamageType,
        };

        public MonsterRule ToRule() => new(Expression, new MonsterRuleActions
        {
            Flags = Flags,
            Priority = Math.Clamp(Priority, -1, 4),
            DamageType = DamageType,
            ExtraVulnerability = ExtraVulnerability,
            WeaponObjectId = WeaponObjectId,
            OffhandObjectId = OffhandObjectId,
            WeaponName = WeaponName ?? string.Empty,
            OffhandName = OffhandName ?? string.Empty,
            PetDamageType = PetDamageType,
        });
    }


    private sealed class LegacyProfileDocument
    {
        public int Version { get; set; } = 6;
        public string[] ItemNames { get; set; } = [];
        public string[] ConsumableNames { get; set; } = [];
        public Dictionary<string, ConsumableCategory> ConsumableCategories { get; set; } =
            new(StringComparer.Ordinal);
        public string[] NoBuffItemNames { get; set; } = [];
        public LegacyCombatProfileDocument? Combat { get; set; }
        public LegacyBuffProfileDocument? Buffs { get; set; }
        public LegacyVitalProfileDocument? Vitals { get; set; }
        public LegacyInventoryProfileDocument? Inventory { get; set; }

        public void Apply(
            CombatSettings combat,
            BuffSettings buffs,
            VitalSettings vitals,
            InventorySettings inventory,
            ISet<string> noBuffItemNames,
            IPluginLogger? logger = null)
        {
            (Combat ?? LegacyCombatProfileDocument.Capture(new CombatSettings())).Apply(combat, logger);
            (Buffs ?? LegacyBuffProfileDocument.Capture(new BuffSettings())).Apply(buffs);
            (Vitals ?? LegacyVitalProfileDocument.Capture(new VitalSettings())).Apply(vitals);
            (Inventory ?? LegacyInventoryProfileDocument.Capture(new InventorySettings())).Apply(inventory);
            Replace(combat.CombatItemNames, ItemNames);
            combat.CombatItemObjectIds.Clear();
            Replace(combat.ConsumableNames, ConsumableNames);
            combat.ConsumableCategories.Clear();
            foreach ((string itemName, ConsumableCategory category) in
                ConsumableCategories ?? new Dictionary<string, ConsumableCategory>())
            {
                if (combat.ConsumableNames.Contains(itemName))
                    combat.ConsumableCategories[itemName] = category;
            }
            Replace(noBuffItemNames, NoBuffItemNames);
        }
    }

    private sealed class LegacyInventoryProfileDocument
    {
        public bool ManaChargesWhenOff { get; set; } = true;
        public bool AutoStack { get; set; } = true;
        public bool AutoCram { get; set; }
        public bool AutoCraftItems { get; set; } = true;
        public bool SplitPeas { get; set; } = true;
        public int CriticalComponentMinimum { get; set; } = 4;
        public int NormalComponentMinimum { get; set; } = 20;
        public int IdleComponentMinimum { get; set; } = 20;
        public int IdleHealthKitCount { get; set; } = 2;
        public int IdleStaminaKitCount { get; set; } = 2;
        public int IdleManaKitCount { get; set; } = 2;
        public int IdleHealthFoodCount { get; set; } = 15;
        public int IdleStaminaFoodCount { get; set; } = 15;
        public int IdleManaFoodCount { get; set; } = 15;
        public bool RefillWornMana { get; set; } = true;
        public int RefillWornManaPercent { get; set; } = 33;
        public double ScanIntervalSeconds { get; set; } = 0.25d;
        public bool EnableLooting { get; set; }
        public string LootClassifierId { get; set; } = string.Empty;
        public bool LootPriorityBoost { get; set; }
        public bool LootAllCorpses { get; set; }
        public bool LootFellowCorpses { get; set; }
        public bool LootOnlyRareCorpses { get; set; }
        public bool ReadUnknownScrolls { get; set; } = true;
        public bool CombineSalvage { get; set; } = true;
        public int ManaStoneLootCount { get; set; } = 4;
        public int ManaTankMinimumMana { get; set; } = 1000;
        public double CorpseApproachRange { get; set; } = 40d;
        public double CorpseOpenTimeoutSeconds { get; set; } = 1.5d;
        public int BlacklistCorpseOpenAttemptCount { get; set; } = 30;
        public double BlacklistCorpseOpenTimeoutSeconds { get; set; } = 200d;
        public double CorpseCacheTimeoutMinutes { get; set; } = 60d;
        public int CorpseLootItemMaxAttempts { get; set; } = 20;
        public double LootScanIntervalSeconds { get; set; } = 0.25d;
        public LootRuleDocument[] LootRules { get; set; } = [];

        public static LegacyInventoryProfileDocument Capture(InventorySettings settings) => new()
        {
            ManaChargesWhenOff = settings.ManaChargesWhenOff,
            AutoStack = settings.AutoStack,
            AutoCram = settings.AutoCram,
            AutoCraftItems = settings.AutoCraftItems,
            SplitPeas = settings.SplitPeas,
            CriticalComponentMinimum = settings.CriticalComponentMinimum,
            NormalComponentMinimum = settings.NormalComponentMinimum,
            IdleComponentMinimum = settings.IdleComponentMinimum,
            IdleHealthKitCount = settings.IdleHealthKitCount,
            IdleStaminaKitCount = settings.IdleStaminaKitCount,
            IdleManaKitCount = settings.IdleManaKitCount,
            IdleHealthFoodCount = settings.IdleHealthFoodCount,
            IdleStaminaFoodCount = settings.IdleStaminaFoodCount,
            IdleManaFoodCount = settings.IdleManaFoodCount,
            RefillWornMana = settings.RefillWornMana,
            RefillWornManaPercent = settings.RefillWornManaPercent,
            ScanIntervalSeconds = settings.ScanIntervalSeconds,
            EnableLooting = settings.Loot.Enabled,
            LootClassifierId = settings.Loot.ExternalClassifierId,
            LootPriorityBoost = settings.Loot.PriorityBoost,
            LootAllCorpses = settings.Loot.LootAllCorpses,
            LootFellowCorpses = settings.Loot.LootFellowCorpses,
            LootOnlyRareCorpses = settings.Loot.LootOnlyRareCorpses,
            ReadUnknownScrolls = settings.Loot.ReadUnknownScrolls,
            CombineSalvage = settings.Loot.CombineSalvage,
            ManaStoneLootCount = settings.Loot.ManaStoneLootCount,
            ManaTankMinimumMana = settings.Loot.ManaTankMinimumMana,
            CorpseApproachRange = settings.Loot.CorpseApproachRange,
            CorpseOpenTimeoutSeconds = settings.Loot.CorpseOpenTimeoutSeconds,
            BlacklistCorpseOpenAttemptCount = settings.Loot.BlacklistCorpseOpenAttemptCount,
            BlacklistCorpseOpenTimeoutSeconds = settings.Loot.BlacklistCorpseOpenTimeoutSeconds,
            CorpseCacheTimeoutMinutes = settings.Loot.CorpseCacheTimeoutMinutes,
            CorpseLootItemMaxAttempts = settings.Loot.CorpseLootItemMaxAttempts,
            LootScanIntervalSeconds = settings.Loot.ScanIntervalSeconds,
            LootRules = settings.Loot.Rules.Select(LootRuleDocument.From).ToArray(),
        };

        public void Apply(InventorySettings settings)
        {
            settings.ManaChargesWhenOff = ManaChargesWhenOff;
            settings.AutoStack = AutoStack;
            settings.AutoCram = AutoCram;
            settings.AutoCraftItems = AutoCraftItems;
            settings.SplitPeas = SplitPeas;
            settings.CriticalComponentMinimum = Math.Clamp(CriticalComponentMinimum, 0, 1000);
            settings.NormalComponentMinimum = Math.Clamp(NormalComponentMinimum, 0, 1000);
            settings.IdleComponentMinimum = Math.Clamp(IdleComponentMinimum, 0, 1000);
            settings.IdleHealthKitCount = Math.Clamp(IdleHealthKitCount, 0, 1000);
            settings.IdleStaminaKitCount = Math.Clamp(IdleStaminaKitCount, 0, 1000);
            settings.IdleManaKitCount = Math.Clamp(IdleManaKitCount, 0, 1000);
            settings.IdleHealthFoodCount = Math.Clamp(IdleHealthFoodCount, 0, 1000);
            settings.IdleStaminaFoodCount = Math.Clamp(IdleStaminaFoodCount, 0, 1000);
            settings.IdleManaFoodCount = Math.Clamp(IdleManaFoodCount, 0, 1000);
            settings.RefillWornMana = RefillWornMana;
            settings.RefillWornManaPercent = Math.Clamp(RefillWornManaPercent, 0, 99);
            settings.ScanIntervalSeconds = Math.Clamp(ScanIntervalSeconds, 0.05d, 10d);
            settings.Loot.Enabled = EnableLooting;
            settings.Loot.ExternalClassifierId = LootClassifierId?.Trim() ?? string.Empty;
            settings.Loot.PriorityBoost = LootPriorityBoost;
            settings.Loot.LootAllCorpses = LootAllCorpses;
            settings.Loot.LootFellowCorpses = LootFellowCorpses;
            settings.Loot.LootOnlyRareCorpses = LootOnlyRareCorpses;
            settings.Loot.ReadUnknownScrolls = ReadUnknownScrolls;
            settings.Loot.CombineSalvage = CombineSalvage;
            settings.Loot.ManaStoneLootCount = Math.Clamp(ManaStoneLootCount, 0, 100);
            settings.Loot.ManaTankMinimumMana = Math.Clamp(ManaTankMinimumMana, 1, int.MaxValue);
            settings.Loot.CorpseApproachRange = Math.Clamp(CorpseApproachRange, 2f, 100f);
            settings.Loot.CorpseOpenTimeoutSeconds = Math.Clamp(CorpseOpenTimeoutSeconds, 0.25d, 30d);
            settings.Loot.BlacklistCorpseOpenAttemptCount = Math.Clamp(
                BlacklistCorpseOpenAttemptCount, 1, 1000);
            settings.Loot.BlacklistCorpseOpenTimeoutSeconds = Math.Clamp(
                BlacklistCorpseOpenTimeoutSeconds, 1d, 3600d);
            settings.Loot.CorpseCacheTimeoutMinutes = Math.Clamp(CorpseCacheTimeoutMinutes, 1d, 1440d);
            settings.Loot.CorpseLootItemMaxAttempts = Math.Clamp(CorpseLootItemMaxAttempts, 1, 100);
            settings.Loot.ScanIntervalSeconds = Math.Clamp(LootScanIntervalSeconds, 0.05d, 5d);
            settings.Loot.Rules.Clear();
            foreach (LootRuleDocument rule in LootRules ?? [])
                settings.Loot.Rules.Add(rule.ToRule());
        }
    }

    private sealed class LegacyCombatProfileDocument
    {
        public bool Enabled { get; set; } = true;
        public double MaximumRange { get; set; } = 5d;
        public double ApproachDistance { get; set; }
        public bool IdlePeaceMode { get; set; }
        public TargetSelectionMethod SelectionMethod { get; set; } = TargetSelectionMethod.Both;
        public double TargetSelectAngleRange { get; set; } = 5d;
        public bool TargetLock { get; set; }
        public PluginAttackHeight AttackHeight { get; set; } = PluginAttackHeight.Medium;
        public float AttackPower { get; set; } = 0.5f;
        public bool AutoAttackPower { get; set; } = true;
        public bool UseRecklessness { get; set; } = true;
        public double ScanIntervalSeconds { get; set; } = 0.25;
        public DebuffEachFirst DebuffEachFirst { get; set; } = DebuffEachFirst.One;
        public DebuffSelectionMethod DebuffSelectionMethod { get; set; } = DebuffSelectionMethod.Skill;
        public double DebuffPrecastSeconds { get; set; } = 5d;
        public bool SwitchWandsToDebuff { get; set; }
        public UseArcsMode UseArcs { get; set; } = UseArcsMode.AtRange;
        public double ArcRange { get; set; } = 5d;
        public double RingDistance { get; set; } = 5d;
        public int MinimumRingTargets { get; set; } = 4;
        public bool DeleteGhostMonsters { get; set; } = true;
        public int GhostMonsterSpellAttemptCount { get; set; } = 200;
        public int BlacklistMonsterAttemptCount { get; set; } = 4;
        public double BlacklistMonsterTimeoutSeconds { get; set; } = 120d;
        public bool DeleteGhostMonstersByHealthTracker { get; set; } = true;
        public double GhostDeleteHealthTrackerSeconds { get; set; } = 30d;
        public bool SummonPets { get; set; } = true;
        public PetRangeMode PetRangeMode { get; set; } = PetRangeMode.AttackDistance;
        public double PetCustomRange { get; set; } = 5d;
        public int PetMonsterDensity { get; set; } = 1;
        public int PetRefillCountIdle { get; set; } = 3;
        public int PetRefillCountNormal { get; set; } = 1;
        public string MetaState { get; set; } = "Default";
        public Dictionary<string, DynamicSettingDocument> DynamicSettings { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public MonsterRuleDocument[] Rules { get; set; } =
            [MonsterRuleDocument.From(new MonsterRule("DEFAULT", 0))];

        public static LegacyCombatProfileDocument Capture(CombatSettings value) => new()
        {
            Enabled = value.Enabled,
            MaximumRange = value.MaximumRange,
            ApproachDistance = value.ApproachDistance,
            IdlePeaceMode = value.IdlePeaceMode,
            SelectionMethod = value.SelectionMethod,
            TargetSelectAngleRange = value.TargetSelectAngleRange,
            TargetLock = value.TargetLock,
            AttackHeight = value.AttackHeight,
            AttackPower = value.AttackPower,
            AutoAttackPower = value.AutoAttackPower,
            UseRecklessness = value.UseRecklessness,
            ScanIntervalSeconds = value.ScanIntervalSeconds,
            DebuffEachFirst = value.DebuffEachFirst,
            DebuffSelectionMethod = value.DebuffSelectionMethod,
            DebuffPrecastSeconds = value.DebuffPrecastSeconds,
            SwitchWandsToDebuff = value.SwitchWandsToDebuff,
            UseArcs = value.UseArcs,
            ArcRange = value.ArcRange,
            RingDistance = value.RingDistance,
            MinimumRingTargets = value.MinimumRingTargets,
            DeleteGhostMonsters = value.DeleteGhostMonsters,
            GhostMonsterSpellAttemptCount = value.GhostMonsterSpellAttemptCount,
            BlacklistMonsterAttemptCount = value.BlacklistMonsterAttemptCount,
            BlacklistMonsterTimeoutSeconds = value.BlacklistMonsterTimeoutSeconds,
            DeleteGhostMonstersByHealthTracker = value.DeleteGhostMonstersByHealthTracker,
            GhostDeleteHealthTrackerSeconds = value.GhostDeleteHealthTrackerSeconds,
            SummonPets = value.SummonPets,
            PetRangeMode = value.PetRangeMode,
            PetCustomRange = value.PetCustomRange,
            PetMonsterDensity = value.PetMonsterDensity,
            PetRefillCountIdle = value.PetRefillCountIdle,
            PetRefillCountNormal = value.PetRefillCountNormal,
            MetaState = value.MetaState,
            DynamicSettings = value.DynamicSettings.ToDictionary(
                static pair => pair.Key,
                static pair => DynamicSettingDocument.From(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            Rules = value.Rules.Select(MonsterRuleDocument.From).ToArray(),
        };

        public void Apply(CombatSettings value, IPluginLogger? logger = null)
        {
            value.Enabled = Enabled;
            value.MaximumRange = Math.Clamp(MaximumRange, 2f, 100f);
            value.ApproachDistance = Math.Clamp(ApproachDistance, 0f, 100f);
            value.IdlePeaceMode = IdlePeaceMode;
            value.SelectionMethod = SelectionMethod;
            value.TargetSelectAngleRange = Math.Clamp(TargetSelectAngleRange, 2f, value.MaximumRange);
            value.TargetLock = TargetLock;
            value.AttackHeight = AttackHeight;
            value.AttackPower = Math.Clamp(AttackPower, 0f, 1f);
            value.AutoAttackPower = AutoAttackPower;
            value.UseRecklessness = UseRecklessness;
            value.ScanIntervalSeconds = Math.Clamp(ScanIntervalSeconds, 0.05, 5d);
            value.DebuffEachFirst = DebuffEachFirst;
            value.DebuffSelectionMethod = DebuffSelectionMethod;
            value.DebuffPrecastSeconds = Math.Clamp(DebuffPrecastSeconds, 0d, 60d);
            value.SwitchWandsToDebuff = SwitchWandsToDebuff;
            value.UseArcs = UseArcs;
            value.ArcRange = Math.Clamp(ArcRange, 1f, 100f);
            value.RingDistance = Math.Clamp(RingDistance, 1f, 100f);
            value.MinimumRingTargets = Math.Clamp(MinimumRingTargets, 1, 25);
            value.DeleteGhostMonsters = DeleteGhostMonsters;
            value.GhostMonsterSpellAttemptCount = Math.Clamp(GhostMonsterSpellAttemptCount, 1, 1000);
            value.BlacklistMonsterAttemptCount = Math.Clamp(BlacklistMonsterAttemptCount, 1, 20);
            value.BlacklistMonsterTimeoutSeconds = Math.Clamp(BlacklistMonsterTimeoutSeconds, 1d, 3600d);
            value.DeleteGhostMonstersByHealthTracker = DeleteGhostMonstersByHealthTracker;
            value.GhostDeleteHealthTrackerSeconds = Math.Clamp(GhostDeleteHealthTrackerSeconds, 1d, 300d);
            value.SummonPets = SummonPets;
            value.PetRangeMode = PetRangeMode;
            value.PetCustomRange = Math.Clamp(PetCustomRange, 1f, 100f);
            value.PetMonsterDensity = Math.Clamp(PetMonsterDensity, 1, 25);
            value.PetRefillCountIdle = Math.Clamp(PetRefillCountIdle, 0, 3);
            value.PetRefillCountNormal = Math.Clamp(PetRefillCountNormal, 0, 3);
            value.MetaState = string.IsNullOrWhiteSpace(MetaState) ? "Default" : MetaState;
            value.DynamicSettings.Clear();
            foreach ((string settingName, DynamicSettingDocument setting) in
                DynamicSettings ?? new Dictionary<string, DynamicSettingDocument>())
            {
                value.DynamicSettings[settingName] = setting.ToValue();
            }
            value.Rules.Clear();
            foreach (MonsterRuleDocument rule in Rules ?? [])
            {
                try { value.Rules.Add(rule.ToRule()); }
                catch (FormatException error)
                {
                    logger?.Warn($"MossTank could not restore a legacy monster rule: {error.Message}");
                }
            }
            if (!value.Rules.Any(static rule => rule.IsDefault))
                value.Rules.Add(new MonsterRule("DEFAULT", 0));
        }
    }

    private sealed class LegacyBuffProfileDocument
    {
        public bool Enabled { get; set; } = true;
        public bool IdleBuffTopoff { get; set; }
        public double IdleBuffTopoffSeconds { get; set; } = 1200d;
        public double RebuffWhenUnderSeconds { get; set; } = 300d;
        public int SkillExcessOverDifficulty { get; set; } = 5;
        public bool BuffAttributes { get; set; } = true;
        public bool BuffProtections { get; set; } = true;
        public bool BuffAuras { get; set; } = true;
        public bool BuffBanes { get; set; } = true;
        public bool BuffRegeneration { get; set; } = true;
        public bool BuffOther { get; set; }
        public bool BuffTrainedSkillsOnly { get; set; } = true;
        public double BuffCastRecastSeconds { get; set; } = 30d;
        public double BuffCastRecastResetSeconds { get; set; } = 30d;
        public bool FastCastBuffs { get; set; }
        public bool RandomHelperBuffs { get; set; }
        public double RandomHelperIntervalSeconds { get; set; } = 5d;
        public string BlacklistedSpellComponents { get; set; } = string.Empty;
        public string ProtectionElements { get; set; } = "ALFCBPS";
        public int ProtectionProfileMode { get; set; } = 2;
        public string BaneElements { get; set; } = "ALFCBPS";
        public int BaneProfileMode { get; set; } = 2;

        public static LegacyBuffProfileDocument Capture(BuffSettings value) => new()
        {
            Enabled = value.Enabled,
            IdleBuffTopoff = value.IdleBuffTopoff,
            IdleBuffTopoffSeconds = value.IdleBuffTopoffSeconds,
            RebuffWhenUnderSeconds = value.RebuffWhenUnderSeconds,
            SkillExcessOverDifficulty = value.SkillExcessOverDifficulty,
            BuffAttributes = value.BuffAttributes,
            BuffProtections = value.BuffProtections,
            BuffAuras = value.BuffAuras,
            BuffBanes = value.BuffBanes,
            BuffRegeneration = value.BuffRegeneration,
            BuffOther = value.BuffOther,
            BuffTrainedSkillsOnly = value.BuffTrainedSkillsOnly,
            BuffCastRecastSeconds = value.BuffCastRecastSeconds,
            BuffCastRecastResetSeconds = value.BuffCastRecastResetSeconds,
            FastCastBuffs = value.FastCastBuffs,
            RandomHelperBuffs = value.RandomHelperBuffs,
            RandomHelperIntervalSeconds = value.RandomHelperIntervalSeconds,
            BlacklistedSpellComponents = value.BlacklistedSpellComponents,
            ProtectionElements = value.ProtectionElements,
            ProtectionProfileMode = value.ProtectionProfileMode,
            BaneElements = value.BaneElements,
            BaneProfileMode = value.BaneProfileMode,
        };

        public void Apply(BuffSettings value)
        {
            value.Enabled = Enabled;
            value.IdleBuffTopoff = IdleBuffTopoff;
            value.IdleBuffTopoffSeconds = Math.Clamp(IdleBuffTopoffSeconds, 30d, 7200d);
            value.RebuffWhenUnderSeconds = Math.Clamp(RebuffWhenUnderSeconds, 30d, 1800d);
            value.SkillExcessOverDifficulty = Math.Clamp(SkillExcessOverDifficulty, -100, 100);
            value.BuffAttributes = BuffAttributes;
            value.BuffProtections = BuffProtections;
            value.BuffAuras = BuffAuras;
            value.BuffBanes = BuffBanes;
            value.BuffRegeneration = BuffRegeneration;
            value.BuffOther = BuffOther;
            value.BuffTrainedSkillsOnly = BuffTrainedSkillsOnly;
            value.BuffCastRecastSeconds = Math.Clamp(BuffCastRecastSeconds, 0d, 3600d);
            value.BuffCastRecastResetSeconds = Math.Clamp(BuffCastRecastResetSeconds, 0d, 3600d);
            value.FastCastBuffs = FastCastBuffs;
            value.RandomHelperBuffs = RandomHelperBuffs;
            value.RandomHelperIntervalSeconds = Math.Clamp(RandomHelperIntervalSeconds, 0.25d, 3600d);
            value.BlacklistedSpellComponents = BlacklistedSpellComponents ?? string.Empty;
            value.ProtectionElements = ProtectionElements ?? "ALFCBPS";
            value.ProtectionProfileMode = Math.Clamp(ProtectionProfileMode, 1, 8);
            value.BaneElements = BaneElements ?? "ALFCBPS";
            value.BaneProfileMode = Math.Clamp(BaneProfileMode, 1, 8);
        }
    }

    private sealed class LegacyVitalProfileDocument
    {
        public bool Enabled { get; set; } = true;
        public double NormalHealth { get; set; } = 0.75;
        public double NormalStamina { get; set; } = 0.50;
        public double NormalMana { get; set; } = 0.50;
        public double NoTargetHealth { get; set; } = 0.01;
        public double NoTargetStamina { get; set; } = 0.01;
        public double NoTargetMana { get; set; } = 0.01;
        public double HelperHealth { get; set; } = 0.20;
        public double HelperStamina { get; set; } = 0.01;
        public double HelperMana { get; set; } = 0.01;
        public bool HelpOthers { get; set; } = true;

        public static LegacyVitalProfileDocument Capture(VitalSettings value) => new()
        {
            Enabled = value.Enabled,
            NormalHealth = value.NormalHealth,
            NormalStamina = value.NormalStamina,
            NormalMana = value.NormalMana,
            NoTargetHealth = value.NoTargetHealth,
            NoTargetStamina = value.NoTargetStamina,
            NoTargetMana = value.NoTargetMana,
            HelperHealth = value.HelperHealth,
            HelperStamina = value.HelperStamina,
            HelperMana = value.HelperMana,
            HelpOthers = value.HelpOthers,
        };

        public void Apply(VitalSettings value)
        {
            value.Enabled = Enabled;
            value.NormalHealth = Clamp(NormalHealth);
            value.NormalStamina = Clamp(NormalStamina);
            value.NormalMana = Clamp(NormalMana);
            value.NoTargetHealth = Clamp(NoTargetHealth);
            value.NoTargetStamina = Clamp(NoTargetStamina);
            value.NoTargetMana = Clamp(NoTargetMana);
            value.HelperHealth = Clamp(HelperHealth);
            value.HelperStamina = Clamp(HelperStamina);
            value.HelperMana = Clamp(HelperMana);
            value.HelpOthers = HelpOthers;
        }

        private static double Clamp(double value) => Math.Clamp(value, 0d, 1d);
    }
}
