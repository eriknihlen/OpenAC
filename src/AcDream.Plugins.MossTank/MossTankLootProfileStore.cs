using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class MossTankLootProfileStore
{
    public const string ByCharacter = "By char";

    private const string LegacyRosterKey = "profiles/loot/index.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IPluginHost _host;
    private string _characterName = string.Empty;
    private string _selected = ByCharacter;
    private bool _rosterSwept;

    public MossTankLootProfileStore(IPluginHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public string Selected => StripUtl(_selected);
    public string? RecoveryNotice { get; private set; }

    private static string StripUtl(string name) => name.Equals(
        ByCharacter, StringComparison.OrdinalIgnoreCase)
            ? name
            : name.EndsWith(".utl", StringComparison.OrdinalIgnoreCase)
                ? name[..^4]
                : name;

    private string Server => _host.Automation.Character.WorldName;
    private IPluginStorage VtankStorage => _host.VtankProfiles;
    private bool CanBindFiles => _characterName.Length > 0 && Server.Length > 0;

    public IReadOnlyList<string> AvailableNames
    {
        get
        {
            var names = new List<string> { ByCharacter };
            foreach (VtankProfileDirectory.ProfileEntry entry in
                VtankProfileDirectory.ListLootProfiles(VtankStorage))
            {
                if (entry.FileName.Length == 0)
                    continue;
                names.Add(StripUtl(entry.FileName));
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
        VtankProfileDirectory.VtankCharacterBinding? binding = CanBindFiles
            ? VtankProfileDirectory.TryReadCharacterBinding(VtankStorage, _characterName, Server)
            : null;
        _selected = binding is { LootFileName.Length: > 0 } bound
            ? bound.LootFileName
            : ByCharacter;
        return true;
    }

    public bool Select(string? name)
    {
        string normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return false;
        if (normalized.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            _selected = ByCharacter;
            WriteBinding();
            return true;
        }

        string candidate = ToFileName(normalized);
        if (VtankStorage.IsAvailable && VtankStorage.ReadText(candidate) is not null)
        {
            _selected = candidate;
            WriteBinding();
            return true;
        }
        return false;
    }

    public bool Exists(string? name)
    {
        string normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return false;
        if (normalized.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
            return true;

        string candidate = ToFileName(normalized);
        return VtankStorage.IsAvailable && VtankStorage.ReadText(candidate) is not null;
    }

    public bool Create(
        string? name,
        bool copyCurrent,
        IReadOnlyList<LootRule> current,
        out string notice,
        LootSettings? settings = null)
    {
        string normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 64)
        {
            notice = "Enter a loot profile name (1-64 characters).";
            return false;
        }
        if (normalized.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            notice = "'By char' is the built-in loot profile.";
            return false;
        }

        string fileName = ToFileName(normalized);
        var profile = new VtankLootProfile
        {
            Rules = copyCurrent ? current.ToList() : [],
            SalvageCombine = copyCurrent
                ? settings?.SalvageCombine.Clone() ?? new VtankSalvageCombineSettings()
                : new VtankSalvageCombineSettings(),
        };
        WriteUtl(fileName, profile);
        _selected = fileName;
        WriteBinding();
        notice = copyCurrent
            ? $"Copied loot rules to {_selected}."
            : $"Created loot profile {_selected}.";
        return true;
    }

    /// <summary>Returns false when no document exists (legacy migration seam).</summary>
    public bool LoadCurrent(List<LootRule> target, LootSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        SweepLegacyRosterIfNeeded();
        string fileName = CurrentFileName();
        string? text = VtankStorage.IsAvailable ? VtankStorage.ReadText(fileName) : null;
        if (text is null)
            return false;
        if (!VtankLootProfileSerializer.TryRead(text, out VtankLootProfile profile, out string error))
        {
            RecoveryNotice = MossTankProfileRecovery.Preserve(
                _host, "loot", fileName, text, new FormatException(error));
            _host.Log.Warn(RecoveryNotice);
            return false;
        }
        ApplyMossTankExpressions(profile);
        target.Clear();
        target.AddRange(profile.Rules);
        if (settings is not null)
            settings.SalvageCombine = profile.SalvageCombine.Clone();
        return true;
    }

    public bool TryLoadNamed(string? name, List<LootRule> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string normalized = name?.Trim() ?? string.Empty;
        if (normalized.EndsWith(".utl", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        if (normalized.Length == 0)
            return false;

        string fileName = normalized.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase)
            ? CurrentFileName()
            : ToFileName(normalized);
        string? text = VtankStorage.IsAvailable ? VtankStorage.ReadText(fileName) : null;
        if (text is null || !VtankLootProfileSerializer.TryRead(
            text, out VtankLootProfile profile, out _))
        {
            return false;
        }

        ApplyMossTankExpressions(profile);
        target.Clear();
        target.AddRange(profile.Rules);
        return true;
    }

    public void SaveCurrent(
        IReadOnlyList<LootRule> rules,
        LootSettings? settings = null)
    {
        string fileName = CurrentFileName();
        var profile = new VtankLootProfile
        {
            Rules = rules.ToList(),
            SalvageCombine = settings?.SalvageCombine.Clone() ?? new VtankSalvageCombineSettings(),
        };
        string? existingText = VtankStorage.IsAvailable ? VtankStorage.ReadText(fileName) : null;
        if (existingText is not null
            && VtankLootProfileSerializer.TryRead(existingText, out VtankLootProfile existing, out _))
        {
            profile.UnknownBlocks = existing.UnknownBlocks;
        }
        WriteUtl(fileName, profile);
    }

    public bool Delete(out string notice)
    {
        if (_selected.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            notice = "'By char' is the built-in loot profile and cannot be deleted.";
            return false;
        }
        string fileName = _selected;
        if (VtankStorage.IsAvailable)
            VtankStorage.Delete(fileName);
        _selected = ByCharacter;
        WriteBinding();
        notice = $"Deleted loot profile {StripUtl(fileName)}.";
        return true;
    }

    public void ClearCurrent(List<LootRule> target, LootSettings? settings = null)
    {
        target.Clear();
        if (settings is not null)
            settings.SalvageCombine = new VtankSalvageCombineSettings();
        SaveCurrent(target, settings);
    }

    public bool TryImportLegacy(
        string? name,
        List<LootRule> target,
        LootSettings? settings,
        out string notice)
    {
        ArgumentNullException.ThrowIfNull(target);
        string normalized = name?.Trim() ?? string.Empty;
        if (normalized.EndsWith(".utl", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        if (!_host.Storage.IsAvailable || normalized.Length == 0)
        {
            notice = "Legacy loot-profile storage is unavailable.";
            return false;
        }
        string? key = _host.Storage.List("imports")
            .Concat(_host.Storage.List("exports"))
            .FirstOrDefault(candidate =>
                candidate.EndsWith(".utl", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(candidate).Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
        string? source = key is null ? null : _host.Storage.ReadText(key);
        if (string.IsNullOrWhiteSpace(source))
        {
            notice = $"VTClassic loot file '{normalized}.utl' was not found in imports.";
            return false;
        }
        if (!VtankLootProfileSerializer.TryRead(
            source,
            out VtankLootProfile imported,
            out string error))
        {
            notice = $"Could not import {Path.GetFileName(key)}: {error}";
            return false;
        }

        string fileName = ToFileName(normalized);
        WriteUtl(fileName, imported);
        _selected = fileName;
        WriteBinding();
        target.Clear();
        target.AddRange(imported.Rules);
        if (settings is not null)
            settings.SalvageCombine = imported.SalvageCombine.Clone();
        notice = $"Imported VTClassic loot profile {_selected}.";
        return true;
    }


    private void SweepLegacyRosterIfNeeded()
    {
        if (_rosterSwept)
            return;
        _rosterSwept = true;
        if (!_host.Storage.IsAvailable)
            return;

        string byCharacterFileName = CurrentFileName();
        if (VtankStorage.ReadText(byCharacterFileName) is null)
        {
            LootProfileDocument? byCharacter = ReadLegacyJson(
                ProfileKey(_characterName, byCharacter: true));
            if (byCharacter is not null)
            {
                WriteUtl(byCharacterFileName, byCharacter.ToVtankProfile());
                _host.Storage.Delete(ProfileKey(_characterName, byCharacter: true));
            }
        }

        LegacyRosterDocument? roster = ReadLegacyRoster();
        if (roster?.Names is not { Count: > 0 } names)
            return;

        var remaining = new List<string>();
        int migrated = 0;
        foreach (string rawName in names)
        {
            string name = (rawName ?? string.Empty).Trim();
            if (name.Length == 0 || name.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
                continue; // stale/invalid row; drop it rather than loop on it forever.

            string legacyKey = ProfileKey(name, byCharacter: false);
            LootProfileDocument? legacy = ReadLegacyJson(legacyKey);
            if (legacy is null)
                continue;

            string fileName = ToFileName(name);
            if (VtankStorage.ReadText(fileName) is null)
                WriteUtl(fileName, legacy.ToVtankProfile());
            _host.Storage.Delete(legacyKey);
            migrated++;
        }

        if (migrated == 0)
            return;
        if (remaining.Count > 0)
        {
            _host.Storage.WriteText(
                LegacyRosterKey,
                JsonSerializer.Serialize(new LegacyRosterDocument { Names = remaining }, Options));
        }
        else
        {
            _host.Storage.Delete(LegacyRosterKey);
        }
        _host.Log.Warn($"Migrated {migrated} legacy MossTank named loot profile(s) from the old roster.");
    }

    private LegacyRosterDocument? ReadLegacyRoster()
    {
        string? json = null;
        try
        {
            json = _host.Storage.ReadText(LegacyRosterKey);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<LegacyRosterDocument>(json, Options);
        }
        catch (Exception error)
        {
            RecoveryNotice = MossTankProfileRecovery.Preserve(
                _host, "loot", LegacyRosterKey, json, error);
            _host.Log.Warn(RecoveryNotice);
            return null;
        }
    }

    private LootProfileDocument? ReadLegacyJson(string key)
    {
        if (!_host.Storage.IsAvailable)
            return null;
        string? json = null;
        try
        {
            json = _host.Storage.ReadText(key);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<LootProfileDocument>(json, Options);
        }
        catch (Exception error)
        {
            RecoveryNotice = MossTankProfileRecovery.Preserve(_host, "loot", key, json, error);
            _host.Log.Warn(RecoveryNotice);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // File naming, storage plumbing.
    // ------------------------------------------------------------------

    private string CurrentFileName() => _selected.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase)
        ? VtankProfileDirectory.AutoCharacterFileName(_characterName, Server, "utl")
        : _selected;

    private static string ToFileName(string bareName) =>
        bareName.EndsWith(".utl", StringComparison.OrdinalIgnoreCase)
            ? bareName
            : bareName + ".utl";

    private void WriteBinding()
    {
        if (!CanBindFiles || !VtankStorage.IsAvailable)
            return;
        VtankProfileDirectory.VtankCharacterBinding existing =
            VtankProfileDirectory.TryReadCharacterBinding(VtankStorage, _characterName, Server)
            ?? new VtankProfileDirectory.VtankCharacterBinding(
                string.Empty, CurrentFileName(), string.Empty, null);
        VtankProfileDirectory.WriteCharacterBinding(
            VtankStorage,
            _characterName,
            Server,
            existing with { LootFileName = CurrentFileName() });
    }

    private void WriteUtl(string fileName, VtankLootProfile profile)
    {
        if (!VtankStorage.IsAvailable)
            return;
        AttachMossTankExpressions(profile);
        try
        {
            VtankStorage.WriteText(fileName, VtankLootProfileSerializer.Write(profile));
        }
        catch (Exception error)
        {
            _host.Log.Warn($"MossTank loot profile could not be saved: {error.Message}");
        }
    }


    private const string MossTankRulesBlockType = "MossTankRuleExpressions";

    private static void AttachMossTankExpressions(VtankLootProfile profile)
    {
        profile.UnknownBlocks.RemoveAll(static block =>
            string.Equals(block.Type, MossTankRulesBlockType, StringComparison.Ordinal));

        var payload = new StringBuilder();
        payload.Append(profile.Rules.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (LootRule rule in profile.Rules)
        {
            // A rule with real VtankRequirements (imported from a genuine
            // VTClassic file, never touched by MossTank's own editor) has
            // nothing of ours to preserve — record an empty slot so load
            // leaves its VtankRequirements-derived state alone.
            string expression = rule.VtankRequirements.Count > 0 ? string.Empty : rule.Expression;
            payload.Append(expression.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            payload.Append(expression);
        }
        profile.UnknownBlocks.Add(new VtankLootExtraBlock
        {
            Type = MossTankRulesBlockType,
            Payload = payload.ToString(),
        });
    }

    private static void ApplyMossTankExpressions(VtankLootProfile profile)
    {
        VtankLootExtraBlock? block = profile.UnknownBlocks.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, MossTankRulesBlockType, StringComparison.Ordinal));
        if (block is null)
            return;
        profile.UnknownBlocks.Remove(block);

        string payload = block.Payload ?? string.Empty;
        int position = 0;
        if (!TryReadPayloadLine(payload, ref position, out string countText)
            || !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
        {
            return;
        }
        int limit = Math.Min(count, profile.Rules.Count);
        for (int index = 0; index < limit; index++)
        {
            if (!TryReadPayloadLine(payload, ref position, out string lengthText)
                || !int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length)
                || length < 0
                || length > payload.Length - position)
            {
                return;
            }
            string expression = payload.Substring(position, length);
            position += length;
            if (expression.Length == 0)
                continue;
            profile.Rules[index].Expression = expression;
            profile.Rules[index].VtankRequirements.Clear();
        }
    }

    private static bool TryReadPayloadLine(string text, ref int position, out string line)
    {
        if (position > text.Length)
        {
            line = string.Empty;
            return false;
        }
        int start = position;
        while (position < text.Length && text[position] is not ('\r' or '\n'))
            position++;
        line = text[start..position];
        if (position < text.Length && text[position] == '\r')
            position++;
        if (position < text.Length && text[position] == '\n')
            position++;
        return true;
    }

    private static string ProfileKey(string value, bool byCharacter)
    {
        string identity = (byCharacter ? "char:" : "named:")
            + value.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"profiles/loot/{hash}.json";
    }

    // ------------------------------------------------------------------
    // Migration-only shapes: the OLD JSON roster and per-profile document,
    // kept solely so SweepLegacyRosterIfNeeded can recover them once.
    // Nothing else in this file writes either shape again.
    // ------------------------------------------------------------------

    private sealed class LegacyRosterDocument
    {
        public int Version { get; set; } = 1;
        public List<string> Names { get; set; } = [];
        public Dictionary<string, string> SelectedByCharacter { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LootProfileDocument
    {
        public int Version { get; set; } = 2;
        public LootRuleDocument[] Rules { get; set; } = [];
        public VtankSalvageCombineSettings? SalvageCombine { get; set; } = new();
        public VtankLootExtraBlockDocument[] UnknownBlocks { get; set; } = [];

        public VtankLootProfile ToVtankProfile() => new()
        {
            Rules = (Rules ?? []).Select(static rule => rule.ToRule()).ToList(),
            SalvageCombine = SalvageCombine?.Clone()
                ?? new VtankSalvageCombineSettings(),
            UnknownBlocks = (UnknownBlocks ?? [])
                .Select(static block => block.ToBlock())
                .ToList(),
        };
    }

    private sealed class LootRuleDocument
    {
        public string Name { get; set; } = "Rule";
        public string Expression { get; set; } = "*";
        public LootAction Action { get; set; } = LootAction.Keep;
        public int KeepCount { get; set; } = 1;
        public int Priority { get; set; }
        public string CustomExpression { get; set; } = string.Empty;
        public VtankLootRequirementDocument[] Requirements { get; set; } = [];

        public static LootRuleDocument From(LootRule rule) => new()
        {
            Name = rule.Name,
            Expression = rule.Expression,
            Action = rule.Action,
            KeepCount = rule.KeepCount,
            Priority = rule.Priority,
            CustomExpression = rule.CustomExpression,
            Requirements = rule.VtankRequirements.Select(
                VtankLootRequirementDocument.From).ToArray(),
        };

        public LootRule ToRule() => new()
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "Rule" : Name.Trim(),
            Expression = string.IsNullOrWhiteSpace(Expression)
                ? "*"
                : Expression.Trim(),
            Action = Action,
            KeepCount = Math.Clamp(KeepCount, 0, 100000),
            Priority = Math.Clamp(Priority, -1000, 1000),
            CustomExpression = CustomExpression ?? string.Empty,
            VtankRequirements = (Requirements ?? [])
                .Select(static requirement => requirement.ToRequirement())
                .ToList(),
        };
    }

    private sealed class VtankLootRequirementDocument
    {
        public int Type { get; set; }
        public string Payload { get; set; } = string.Empty;

        public static VtankLootRequirementDocument From(
            VtankLootRequirement requirement) => new()
        {
            Type = requirement.Type,
            Payload = requirement.Payload,
        };

        public VtankLootRequirement ToRequirement() => new()
        {
            Type = Type,
            Payload = Payload ?? string.Empty,
        };
    }

    private sealed class VtankLootExtraBlockDocument
    {
        public string Type { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;

        public static VtankLootExtraBlockDocument From(
            VtankLootExtraBlock block) => new()
        {
            Type = block.Type,
            Payload = block.Payload,
        };

        public VtankLootExtraBlock ToBlock() => new()
        {
            Type = Type ?? string.Empty,
            Payload = Payload ?? string.Empty,
        };
    }
}
