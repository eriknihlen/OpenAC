using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class MossTankRouteProfileStore
{
    public const string ByCharacter = "By char";
    private const string FolderPrefix = VtankProfileDirectory.NavFolder + "/";
    private const string LegacyRosterKey = "profiles/route/index.json";

    private readonly IPluginHost _host;
    private string _characterName = string.Empty;
    private string _selected = ByCharacter;
    private string? _pendingLegacyBareName;
    private bool _rosterSwept;
    private bool _flatFolderMigrationSwept;

    public MossTankRouteProfileStore(IPluginHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public string Selected => Strip(_selected);
    public string? RecoveryNotice { get; private set; }

    private string Server => _host.Automation.Character.WorldName;
    private IPluginStorage VtankStorage => _host.VtankProfiles;
    private bool CanBindFiles => _characterName.Length > 0 && Server.Length > 0;

    private static string Strip(string fileName)
    {
        if (fileName.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
            return fileName;
        string value = fileName.StartsWith(FolderPrefix, StringComparison.Ordinal)
            ? fileName[FolderPrefix.Length..]
            : fileName;
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
                VtankProfileDirectory.ListNavigationProfiles(VtankStorage))
            {
                if (entry.FileName.Length == 0)
                    continue;
                names.Add(Strip(entry.FileName));
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
        VtankProfileDirectory.VtankCharacterBinding? binding = CanBindFiles
            ? VtankProfileDirectory.TryReadCharacterBinding(VtankStorage, _characterName, Server)
            : null;
        _selected = binding is { NavFileName.Length: > 0 } bound
            ? bound.NavFileName
            : ByCharacter;
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

        string candidate = ToFileName(normalized);
        if (VtankStorage.IsAvailable && VtankStorage.ReadText(candidate) is not null)
        {
            _selected = candidate;
            _pendingLegacyBareName = null;
            WriteBinding();
            return true;
        }
        if (_host.Storage.IsAvailable
            && _host.Storage.ReadText(LegacyProfileKey(normalized, byCharacter: false)) is not null)
        {
            _selected = candidate;
            _pendingLegacyBareName = normalized;
            WriteBinding();
            return true;
        }
        return false;
    }

    public bool Create(
        string? name,
        bool copyCurrent,
        NavigationSettings current,
        out string notice)
    {
        string normalized = Normalize(name);
        if (normalized.Length is < 1 or > 64)
        {
            notice = "Enter a route profile name (1-64 characters).";
            return false;
        }
        if (normalized.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            notice = "'By char' is the built-in route profile.";
            return false;
        }
        string fileName = ToFileName(normalized);
        NavigationSettings source = copyCurrent ? current : new NavigationSettings();
        WriteAf(fileName, MetafSerializer.SaveNav(source));
        _selected = fileName;
        _pendingLegacyBareName = null;
        WriteBinding();
        notice = copyCurrent
            ? $"Copied route to {Strip(fileName)}."
            : $"Created route profile {Strip(fileName)}.";
        return true;
    }

    public bool LoadCurrent(NavigationSettings target, ISpellCatalog spells)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spells);
        MigrateFlatFilesToNavsFolderIfNeeded();
        SweepLegacyRosterIfNeeded();
        MigrateLegacyIfNeeded(target, spells);
        string fileName = CurrentFileName();
        string? text = VtankStorage.IsAvailable ? VtankStorage.ReadText(fileName) : null;
        if (text is null)
            return false;
        if (!MetafSerializer.TryLoadNav(text, target, spells, out string error))
        {
            RecoveryNotice = MossTankProfileRecovery.Preserve(
                _host, "route", fileName, text, new FormatException(error));
            _host.Log.Warn(RecoveryNotice);
            return false;
        }
        return true;
    }

    public void SaveCurrent(NavigationSettings settings) =>
        WriteAf(CurrentFileName(), MetafSerializer.SaveNav(settings));

    public bool TryImportLegacy(
        string? name,
        NavigationSettings target,
        ISpellCatalog spells,
        out string notice)
    {
        string normalized = Normalize(name);
        if (!_host.Storage.IsAvailable || normalized.Length == 0)
        {
            notice = "Legacy navigation storage is unavailable.";
            return false;
        }
        string? key = _host.Storage.List("imports")
            .Concat(_host.Storage.List("exports"))
            .FirstOrDefault(candidate =>
                candidate.EndsWith(".nav", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(candidate).Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
        string? source = key is null ? null : _host.Storage.ReadText(key);
        if (string.IsNullOrWhiteSpace(source))
        {
            notice = $"VTank navigation file '{normalized}.nav' was not found in imports.";
            return false;
        }
        if (!VtankNavRouteSerializer.TryLoad(source, target, spells, out string error))
        {
            notice = $"Could not import {Path.GetFileName(key)}: {error}";
            return false;
        }
        string fileName = ToFileName(normalized);
        WriteAf(fileName, MetafSerializer.SaveNav(target));
        _selected = fileName;
        _pendingLegacyBareName = null;
        WriteBinding();
        notice = $"Imported VTank navigation profile {Strip(fileName)}.";
        return true;
    }

    public bool Delete(out string notice)
    {
        if (_selected.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase))
        {
            notice = "'By char' is the built-in route profile and cannot be deleted.";
            return false;
        }
        string fileName = _selected;
        if (VtankStorage.IsAvailable)
            VtankStorage.Delete(fileName);
        _selected = ByCharacter;
        _pendingLegacyBareName = null;
        WriteBinding();
        notice = $"Deleted route profile {Strip(fileName)}.";
        return true;
    }

    /// <summary>
    /// Resets only the fields this store owns (Mode/Waypoints/FollowTarget)
    /// — Enabled/Priority/MinimumDistanceMeters/FollowAroundCorners/
    /// OpenDoors/Door* belong to the Settings profile and are left alone.
    /// </summary>
    public void ClearCurrent(NavigationSettings target)
    {
        target.Mode = RouteMode.Circular;
        target.FollowTargetObjectId = 0u;
        target.FollowTargetName = string.Empty;
        target.Waypoints.Clear();
        SaveCurrent(target);
    }


    private void MigrateFlatFilesToNavsFolderIfNeeded()
    {
        if (_flatFolderMigrationSwept)
            return;
        _flatFolderMigrationSwept = true;
        if (!VtankStorage.IsAvailable)
            return;
        int migrated = 0;
        foreach (string bareName in VtankProfileDirectory.ListFlatAfFileNames(VtankStorage))
        {
            if (!VtankProfileDirectory.IsLegacyFlatRouteFileName(bareName))
                continue; // MossTankMetaProfileStore's own sweep owns this one.
            string strippedName = VtankProfileDirectory.StripLegacyNavMarker(bareName);
            string destination = $"{VtankProfileDirectory.NavFolder}/{strippedName}";
            if (VtankStorage.ReadText(destination) is not null)
            {
                _host.Log.Warn(
                    $"MossTank left flat route profile '{bareName}' in place: '{destination}' already exists.");
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
                $"Migrated {migrated} flat MossTank route profile(s) into {VtankProfileDirectory.NavFolder}/.");
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

            string legacyKey = LegacyProfileKey(name, byCharacter: false);
            LegacyRouteDocument? legacy = ReadLegacyJson(legacyKey);
            if (legacy is null)
                continue;

            string fileName = ToFileName(name);
            if (VtankStorage.IsAvailable && VtankStorage.ReadText(fileName) is null)
            {
                var scratch = new NavigationSettings();
                legacy.ApplyRouteOnly(scratch);
                WriteAf(fileName, MetafSerializer.SaveNav(scratch));
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
        _host.Log.Warn($"Migrated {migrated} legacy MossTank named route profile(s) from the old roster.");
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
            RecoveryNotice = MossTankProfileRecovery.Preserve(_host, "route", LegacyRosterKey, json, error);
            _host.Log.Warn(RecoveryNotice);
            return null;
        }
    }

    private void MigrateLegacyIfNeeded(NavigationSettings target, ISpellCatalog spells)
    {
        string fileName = CurrentFileName();
        if (!VtankStorage.IsAvailable || VtankStorage.ReadText(fileName) is not null)
        {
            _pendingLegacyBareName = null;
            return;
        }
        string legacyKey = _pendingLegacyBareName is { Length: > 0 } bareName
            ? LegacyProfileKey(bareName, byCharacter: false)
            : LegacyProfileKey(_characterName, byCharacter: true);
        LegacyRouteDocument? legacy = ReadLegacyJson(legacyKey);
        if (legacy is null)
        {
            _pendingLegacyBareName = null;
            return;
        }
        legacy.ApplyRouteOnly(target);
        WriteAf(fileName, MetafSerializer.SaveNav(target));
        if (_host.Storage.IsAvailable)
            _host.Storage.Delete(legacyKey);
        _pendingLegacyBareName = null;
        _host.Log.Warn($"Migrated legacy MossTank route profile '{legacyKey}' to '{fileName}'.");
    }

    private LegacyRouteDocument? ReadLegacyJson(string key)
    {
        if (!_host.Storage.IsAvailable)
            return null;
        string? json = null;
        try
        {
            json = _host.Storage.ReadText(key);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<LegacyRouteDocument>(json, JsonOptions);
        }
        catch (Exception error)
        {
            RecoveryNotice = MossTankProfileRecovery.Preserve(_host, "route", key, json, error);
            _host.Log.Warn(RecoveryNotice);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // File naming, storage plumbing.
    // ------------------------------------------------------------------

    private string CurrentFileName() => _selected.Equals(ByCharacter, StringComparison.OrdinalIgnoreCase)
        ? $"{VtankProfileDirectory.NavFolder}/{VtankProfileDirectory.AutoCharacterFileName(_characterName, Server, "af")}"
        : _selected;

    private static string ToFileName(string bareName) =>
        $"{VtankProfileDirectory.NavFolder}/{bareName}.af";

    private void WriteBinding()
    {
        if (!CanBindFiles || !VtankStorage.IsAvailable)
            return;
        VtankProfileDirectory.VtankCharacterBinding existing =
            VtankProfileDirectory.TryReadCharacterBinding(VtankStorage, _characterName, Server)
            ?? new VtankProfileDirectory.VtankCharacterBinding(
                string.Empty, string.Empty, CurrentFileName(), null);
        VtankProfileDirectory.WriteCharacterBinding(
            VtankStorage,
            _characterName,
            Server,
            existing with { NavFileName = CurrentFileName() });
    }

    private void WriteAf(string fileName, string text)
    {
        if (!VtankStorage.IsAvailable)
            return;
        try
        {
            VtankStorage.WriteText(fileName, text);
        }
        catch (Exception error)
        {
            _host.Log.Warn($"MossTank route profile could not be saved: {error.Message}");
        }
    }

    private static string Normalize(string? name) => name?.Trim() ?? string.Empty;

    private static string LegacyProfileKey(string value, bool byCharacter)
    {
        string identity = (byCharacter ? "char:" : "named:") + value.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"profiles/route/{hash}.json";
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

    private sealed class LegacyRouteDocument
    {
        public int Version { get; set; } = 1;
        public bool Enabled { get; set; }
        public bool Priority { get; set; }
        public RouteMode Mode { get; set; } = RouteMode.Circular;
        public double MinimumDistanceMeters { get; set; } = 2d;
        public uint FollowTargetObjectId { get; set; }
        public string FollowTargetName { get; set; } = string.Empty;
        public bool FollowAroundCorners { get; set; } = true;
        public bool OpenDoors { get; set; }
        public double DoorIdentifyRangeMeters { get; set; } = 20d;
        public double DoorOpenRangeMeters { get; set; } = 4d;
        public int DoorLockpickExcessThreshold { get; set; } = -50;
        public LegacyWaypointDocument[] Waypoints { get; set; } = [];

        public void ApplyRouteOnly(NavigationSettings value)
        {
            value.Mode = Enum.IsDefined(Mode) ? Mode : RouteMode.Circular;
            value.FollowTargetObjectId = FollowTargetObjectId;
            value.FollowTargetName = FollowTargetName ?? string.Empty;
            value.Waypoints.Clear();
            foreach (LegacyWaypointDocument waypoint in Waypoints ?? [])
                value.Waypoints.Add(waypoint.ToWaypoint());
        }
    }

    private sealed class LegacyWaypointDocument
    {
        public RouteWaypointType Type { get; set; }
        public uint CellId { get; set; }
        public double EastWest { get; set; }
        public double NorthSouth { get; set; }
        public double Elevation { get; set; }
        public float HeadingDegrees { get; set; }
        public bool IsOutdoor { get; set; }
        public uint ReferenceCellId { get; set; }
        public double ReferenceEastWest { get; set; }
        public double ReferenceNorthSouth { get; set; }
        public double ReferenceElevation { get; set; }
        public float ReferenceHeadingDegrees { get; set; }
        public bool ReferenceIsOutdoor { get; set; }
        public uint ObjectId { get; set; }
        public string ObjectName { get; set; } = string.Empty;
        public int LegacyObjectClass { get; set; }
        public bool LegacyReferenceValid { get; set; } = true;
        public string Text { get; set; } = string.Empty;
        public int DurationMilliseconds { get; set; } = 5000;
        public int Recall { get; set; }
        public uint RecallSpellId { get; set; }
        public string RecallSpellName { get; set; } = string.Empty;
        public float JumpHeadingDegrees { get; set; }
        public bool JumpRun { get; set; }
        public int JumpChargeMilliseconds { get; set; } = 1000;
        public RouteJumpDirection JumpDirection { get; set; }

        public RouteWaypoint ToWaypoint()
        {
            RouteRecallKind recall = MapLegacyRecall(Recall);
            return new RouteWaypoint
            {
                Type = Enum.IsDefined(Type) ? Type : RouteWaypointType.Point,
                Position = new PluginNavigationPosition(
                    CellId, EastWest, NorthSouth, Elevation, HeadingDegrees, IsOutdoor),
                ReferencePosition = new PluginNavigationPosition(
                    ReferenceCellId,
                    ReferenceEastWest,
                    ReferenceNorthSouth,
                    ReferenceElevation,
                    ReferenceHeadingDegrees,
                    ReferenceIsOutdoor),
                ObjectId = ObjectId,
                ObjectName = ObjectName ?? string.Empty,
                LegacyObjectClass = LegacyObjectClass,
                LegacyReferenceValid = LegacyReferenceValid,
                Text = Text ?? string.Empty,
                DurationMilliseconds = Math.Clamp(DurationMilliseconds, 0, 3_600_000),
                Recall = recall,
                RecallSpellId = RouteWaypoint.SpellIdForRecall(recall),
                RecallSpellName = RouteWaypoint.RecallDisplayName(recall),
                JumpHeadingDegrees = float.IsFinite(JumpHeadingDegrees) ? JumpHeadingDegrees : 0f,
                JumpRun = JumpRun,
                JumpChargeMilliseconds = JumpChargeMilliseconds,
                JumpDirection = Enum.IsDefined(JumpDirection) ? JumpDirection : RouteJumpDirection.Forward,
            };
        }

        private static RouteRecallKind MapLegacyRecall(int legacyOrdinal) => legacyOrdinal switch
        {
            0 => RouteRecallKind.LifestoneRecall,        // old Lifestone
            1 => RouteRecallKind.Marketplace,             // old Marketplace
            2 => RouteRecallKind.PrimaryPortalRecall,     // old PrimaryPortal
            3 => RouteRecallKind.SecondaryPortalRecall,   // old SecondaryPortal
            _ => RouteRecallKind.PrimaryPortalRecall,
        };
    }
}
