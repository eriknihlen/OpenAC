using System.Globalization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.UI.Abstractions.Settings;

namespace AcDream.UI.Abstractions.Panels.Settings;

public readonly record struct UiWindowPosition(float X, float Y);

public sealed class SettingsStore
{
    private const int CurrentSchemaVersion = 3;
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public DisplaySettings LoadDisplay()
    {
        if (!File.Exists(_path)) return DisplaySettings.Default;
        try
        {
            using var stream = File.OpenRead(_path);
            var doc  = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("display", out var disp)
                || disp.ValueKind != JsonValueKind.Object)
                return DisplaySettings.Default;

            var d = DisplaySettings.Default;
            float fieldOfView = ReadFloat(disp, "fieldOfView", d.FieldOfView);
            if (ReadSchemaVersion(root) < 3 && disp.TryGetProperty("fieldOfView", out _))
                fieldOfView = MigrateLegacyVerticalFovDegrees(fieldOfView);
            return new DisplaySettings(
                Resolution:  ReadString      (disp, "resolution",  d.Resolution),
                Fullscreen:  ReadBool        (disp, "fullscreen",  d.Fullscreen),
                VSync:       ReadBool        (disp, "vsync",       d.VSync),
                FieldOfView: fieldOfView,
                Gamma:       ReadFloat       (disp, "gamma",       d.Gamma),
                ShowFps:     ReadBool        (disp, "showFps",     d.ShowFps),
                Quality:     ReadQuality     (disp, "quality",     d.Quality),
                ParticleRange: ReadParticleRange(
                    disp, "particleRange", d.ParticleRange),
                ScreenBrightness:        ReadFloat(disp, "screenBrightness",        d.ScreenBrightness),
                AutomaticDegrades:       ReadBool (disp, "automaticDegrades",       d.AutomaticDegrades),
                GraphicsPerformance:     ReadFloat(disp, "graphicsPerformance",     d.GraphicsPerformance),
                DegradeDistance:         ReadFloat(disp, "degradeDistance",         d.DegradeDistance),
                LandscapeTextureDetail:  ReadInt  (disp, "landscapeTextureDetail",  d.LandscapeTextureDetail),
                EnvironmentTextureDetail:ReadInt  (disp, "environmentTextureDetail",d.EnvironmentTextureDetail),
                TextureFiltering:        ReadInt  (disp, "textureFiltering",        d.TextureFiltering),
                LandscapeDrawDistance:   ReadInt  (disp, "landscapeDrawDistance",   d.LandscapeDrawDistance),
                BuildingDetailTextures:  ReadBool (disp, "buildingDetailTextures",  d.BuildingDetailTextures),
                MultiPassAlpha:          ReadBool (disp, "multiPassAlpha",          d.MultiPassAlpha))
            {
                RenderPack = ReadRenderPackSelection(disp, d.RenderPack),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: failed to load {_path}: {ex.Message} — using defaults");
            return DisplaySettings.Default;
        }
    }

    private static int ReadSchemaVersion(JsonElement root) =>
        root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 1;

    internal static float MigrateLegacyVerticalFovDegrees(float legacyVerticalFovDegrees)
    {
        if (legacyVerticalFovDegrees == 60f)
            return 90f;
        return Math.Clamp(legacyVerticalFovDegrees * (16f / 9f - 0.1f), 10f, 160f);
    }

    public void SaveDisplay(DisplaySettings display)
        => SaveSection("display", BuildDisplayObject(display));

    public AudioSettings LoadAudio()
    {
        if (!File.Exists(_path)) return AudioSettings.Default;
        try
        {
            using var stream = File.OpenRead(_path);
            var doc  = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("audio", out var audio)
                || audio.ValueKind != JsonValueKind.Object)
                return AudioSettings.Default;

            var d = AudioSettings.Default;
            return new AudioSettings(
                Master:  ReadFloat(audio, "master",  d.Master),
                Sfx:     ReadFloat(audio, "sfx",     d.Sfx),
                Ambient: ReadFloat(audio, "ambient", d.Ambient),
                SoundFeatures:           ReadInt  (audio, "soundFeatures",           d.SoundFeatures),
                SfxEnabled:              ReadBool (audio, "sfxEnabled",              d.SfxEnabled),
                AmbientEnabled:          ReadBool (audio, "ambientEnabled",          d.AmbientEnabled),
                InterfaceEnabled:        ReadBool (audio, "interfaceEnabled",        d.InterfaceEnabled),
                InterfaceVolume:         ReadFloat(audio, "interfaceVolume",         d.InterfaceVolume),
                PlaySoundOnlyWhenActive: ReadBool (audio, "playSoundOnlyWhenActive", d.PlaySoundOnlyWhenActive));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: failed to load {_path}: {ex.Message} — using defaults");
            return AudioSettings.Default;
        }
    }

    public void SaveAudio(AudioSettings audio)
        => SaveSection("audio", BuildAudioObject(audio));

    public ChatSettings LoadChat()
    {
        if (!File.Exists(_path)) return ChatSettings.Default;
        try
        {
            using var stream = File.OpenRead(_path);
            var doc  = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("chat", out var chat)
                || chat.ValueKind != JsonValueKind.Object)
                return ChatSettings.Default;

            var d = ChatSettings.Default;
            return new ChatSettings(
                HearGeneralChat:  ReadBool (chat, "hearGeneralChat",  d.HearGeneralChat),
                HearTradeChat:    ReadBool (chat, "hearTradeChat",    d.HearTradeChat),
                HearLFGChat:      ReadBool (chat, "hearLFGChat",      d.HearLFGChat),
                HearRoleplayChat: ReadBool (chat, "hearRoleplayChat", d.HearRoleplayChat),
                HearSocietyChat:  ReadBool (chat, "hearSocietyChat",  d.HearSocietyChat),
                AppearOffline:    ReadBool (chat, "appearOffline",    d.AppearOffline),
                ShowTimestamps:   ReadBool (chat, "showTimestamps",   d.ShowTimestamps),
                FilterProfanity:  ReadBool (chat, "filterProfanity",  d.FilterProfanity),
                FontSize:         ReadFloat(chat, "fontSize",         d.FontSize),
                ChatWindow1Filter: ReadULong(chat, "chatWindow1Filter", d.ChatWindow1Filter),
                ChatWindow2Filter: ReadULong(chat, "chatWindow2Filter", d.ChatWindow2Filter),
                ChatWindow3Filter: ReadULong(chat, "chatWindow3Filter", d.ChatWindow3Filter),
                ChatWindow4Filter: ReadULong(chat, "chatWindow4Filter", d.ChatWindow4Filter),
                ChatWindowMainFilter: ReadULong(chat, "chatWindowMainFilter", d.ChatWindowMainFilter),
                DefaultOpacity:   ReadFloat(chat, "defaultOpacity",   d.DefaultOpacity),
                ActiveOpacity:    ReadFloat(chat, "activeOpacity",    d.ActiveOpacity),
                ChatFontFace:      ReadInt(chat, "chatFontFace",      d.ChatFontFace),
                ChatFontSizeIndex: ReadInt(chat, "chatFontSizeIndex", d.ChatFontSizeIndex));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: failed to load {_path}: {ex.Message} — using defaults");
            return ChatSettings.Default;
        }
    }

    public void SaveChat(ChatSettings chat)
        => SaveSection("chat", BuildChatObject(chat));

    public CameraTurningSettings LoadCameraTurning()
    {
        if (!File.Exists(_path)) return CameraTurningSettings.Default;
        try
        {
            using var stream = File.OpenRead(_path);
            var doc  = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("cameraTurning", out var ct)
                || ct.ValueKind != JsonValueKind.Object)
                return CameraTurningSettings.Default;

            var d = CameraTurningSettings.Default;
            return new CameraTurningSettings(
                Stiffness:             ReadFloat(ct, "stiffness",             d.Stiffness),
                AdjustmentSpeed:       ReadFloat(ct, "adjustmentSpeed",       d.AdjustmentSpeed),
                MouseLookSensitivity:  ReadFloat(ct, "mouseLookSensitivity",  d.MouseLookSensitivity),
                AlignToSlope:          ReadBool (ct, "alignToSlope",          d.AlignToSlope),
                InvertMouseLookYAxis:  ReadBool (ct, "invertMouseLookYAxis",  d.InvertMouseLookYAxis),
                UseMouseTurning:       ReadBool (ct, "useMouseTurning",       d.UseMouseTurning));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: failed to load {_path}: {ex.Message} — using defaults");
            return CameraTurningSettings.Default;
        }
    }

    public void SaveCameraTurning(CameraTurningSettings cameraTurning)
        => SaveSection("cameraTurning", BuildCameraTurningObject(cameraTurning));

    public MiscSettings LoadMisc()
    {
        if (!File.Exists(_path)) return MiscSettings.Default;
        try
        {
            using var stream = File.OpenRead(_path);
            var doc  = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("misc", out var misc)
                || misc.ValueKind != JsonValueKind.Object)
                return MiscSettings.Default;

            var d = MiscSettings.Default;
            return new MiscSettings(
                TooltipEnable:       ReadBool (misc, "tooltipEnable",       d.TooltipEnable),
                TooltipDelaySeconds: ReadFloat(misc, "tooltipDelaySeconds", d.TooltipDelaySeconds));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: failed to load {_path}: {ex.Message} — using defaults");
            return MiscSettings.Default;
        }
    }

    /// <summary>Save the tooltip preferences, preserving all other top-level keys.</summary>
    public void SaveMisc(MiscSettings misc)
        => SaveSection("misc", BuildMiscObject(misc));

    public CharacterSettings LoadCharacter(string toonKey)
    {
        if (toonKey is null) throw new ArgumentNullException(nameof(toonKey));
        if (!File.Exists(_path)) return CharacterSettings.Default;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            var toon = root?["character"]?[toonKey] as JsonObject;
            if (toon is null) return CharacterSettings.Default;

            var d = CharacterSettings.Default;
            return new CharacterSettings(
                DefaultChatChannel: toon["defaultChatChannel"]?.GetValue<string>() ?? d.DefaultChatChannel,
                AutoAttack:         toon["autoAttack"]?.GetValue<bool>()           ?? d.AutoAttack,
                ConfirmSalvage:     toon["confirmSalvage"]?.GetValue<bool>()       ?? d.ConfirmSalvage,
                ShowPickupMessages: toon["showPickupMessages"]?.GetValue<bool>()   ?? d.ShowPickupMessages);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: failed to load {_path}: {ex.Message} — using defaults");
            return CharacterSettings.Default;
        }
    }

    public void SaveCharacter(string toonKey, CharacterSettings settings)
    {
        if (toonKey is null) throw new ArgumentNullException(nameof(toonKey));
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        JsonObject root = LoadMutableRoot();

        // Build the toon's payload.
        var toonObj = new JsonObject
        {
            ["autoAttack"]         = settings.AutoAttack,
            ["confirmSalvage"]     = settings.ConfirmSalvage,
            ["defaultChatChannel"] = settings.DefaultChatChannel,
            ["showPickupMessages"] = settings.ShowPickupMessages,
        };

        JsonObject characterMap = GetOrCreateObject(root, "character");
        characterMap[toonKey] = toonObj;
        root["version"] = CurrentSchemaVersion;
        WriteMutableRoot(root);
    }

    public UiWindowPosition? LoadWindowPosition(string toonKey, string windowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toonKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);
        if (!File.Exists(_path)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            var node = root?["windowPositions"]?[toonKey]?[windowName] as JsonObject;
            if (node is null) return null;
            float? x = node["x"]?.GetValue<float>();
            float? y = node["y"]?.GetValue<float>();
            return x is not null && y is not null ? new UiWindowPosition(x.Value, y.Value) : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: failed to load window position from {_path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Save a named retained-window position under
    /// <c>windowPositions[toonKey][windowName]</c>, preserving all settings sections.
    /// </summary>
    public void SaveWindowPosition(string toonKey, string windowName, UiWindowPosition position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toonKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);

        JsonObject root = LoadMutableRoot();
        JsonObject positions = GetOrCreateObject(root, "windowPositions");
        JsonObject toon = GetOrCreateObject(positions, toonKey);
        toon[windowName] = new JsonObject
        {
            ["x"] = position.X,
            ["y"] = position.Y,
        };
        root["version"] = CurrentSchemaVersion;
        WriteMutableRoot(root);
    }

    public UiWindowLayout? LoadWindowLayout(
        string toonKey,
        string resolutionKey,
        string windowName,
        UiWindowLayout fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toonKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolutionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);
        if (!File.Exists(_path)) return null;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            JsonObject? node = root?["windowLayouts"]?[toonKey]?[resolutionKey]?[windowName] as JsonObject;
            if (node is null
                && root?["windowLayouts"]?[toonKey] is JsonObject resolutions
                && TryParseResolution(resolutionKey, out int requestedWidth, out int requestedHeight))
            {
                long bestDistance = long.MaxValue;
                foreach ((string key, JsonNode? value) in resolutions)
                {
                    if (value?[windowName] is not JsonObject candidate
                        || !TryParseResolution(key, out int width, out int height))
                        continue;
                    long dx = width - requestedWidth;
                    long dy = height - requestedHeight;
                    long distance = dx * dx + dy * dy;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    node = candidate;
                }
            }

            if (node is not null)
            {
                return DeserializeWindowLayout(node, fallback);
            }

            // Version-1 migration: radar stored only X/Y and had no resolution key.
            if (root?["windowPositions"]?[toonKey]?[windowName] is JsonObject legacy)
            {
                return fallback with
                {
                    X = ReadNodeFloat(legacy, "x", fallback.X),
                    Y = ReadNodeFloat(legacy, "y", fallback.Y),
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: failed to load window layout from {_path}: {ex.Message}");
            return null;
        }
    }

    public UiWindowPlacement? LoadWindowPlacement(
        string toonKey,
        string resolutionKey,
        string windowName,
        UiWindowLayout fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toonKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolutionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);
        if (!TryParseResolution(resolutionKey, out int requestedWidth, out int requestedHeight)
            || !File.Exists(_path)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            var placements = (root?["windowPlacements"] as JsonObject)?[toonKey] as JsonObject;
            if (placements?[windowName] is JsonObject latest
                && ReadNodeInt(latest, "screenWidth", 0) is > 0 and var width
                && ReadNodeInt(latest, "screenHeight", 0) is > 0 and var height
                && latest["layout"] is JsonObject layout)
                return new UiWindowPlacement(DeserializeWindowLayout(layout, fallback), width, height);

            var resolutions = (root?["windowLayouts"] as JsonObject)?[toonKey] as JsonObject;
            if (resolutions is not null)
            {
                JsonObject? selected = null;
                int sourceWidth = requestedWidth, sourceHeight = requestedHeight;
                long bestDistance = long.MaxValue;
                foreach ((string key, JsonNode? value) in resolutions)
                {
                    if (value is not JsonObject windows || windows[windowName] is not JsonObject candidate
                        || !TryParseResolution(key, out int candidateWidth, out int candidateHeight)) continue;
                    long dx = (long)candidateWidth - requestedWidth;
                    long dy = (long)candidateHeight - requestedHeight;
                    long distance = dx * dx + dy * dy;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    selected = candidate;
                    sourceWidth = candidateWidth;
                    sourceHeight = candidateHeight;
                }
                if (selected is not null)
                    return new UiWindowPlacement(DeserializeWindowLayout(selected, fallback), sourceWidth, sourceHeight);
            }
            var positions = (root?["windowPositions"] as JsonObject)?[toonKey] as JsonObject;
            if (positions?[windowName] is JsonObject position)
                return new UiWindowPlacement(fallback with
                {
                    X = ReadNodeFloat(position, "x", fallback.X),
                    Y = ReadNodeFloat(position, "y", fallback.Y),
                }, requestedWidth, requestedHeight);
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: failed to load window placement from {_path}: {ex.Message}");
            return null;
        }
    }

    public void SaveWindowPlacement(string toonKey, string windowName, UiWindowPlacement placement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toonKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(placement.ScreenWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(placement.ScreenHeight);
        JsonObject root = LoadMutableRoot();
        JsonObject toon = GetOrCreateObject(GetOrCreateObject(root, "windowPlacements"), toonKey);
        toon[windowName] = new JsonObject
        {
            ["screenWidth"] = placement.ScreenWidth,
            ["screenHeight"] = placement.ScreenHeight,
            ["layout"] = SerializeWindowLayout(placement.Layout),
        };
        string resolutionKey = string.Create(CultureInfo.InvariantCulture, $"{placement.ScreenWidth}x{placement.ScreenHeight}");
        JsonObject resolution = GetOrCreateObject(
            GetOrCreateObject(GetOrCreateObject(root, "windowLayouts"), toonKey), resolutionKey);
        resolution[windowName] = SerializeWindowLayout(placement.Layout);
        root["version"] = CurrentSchemaVersion;
        WriteMutableRoot(root);
    }

    private static UiWindowLayout DeserializeWindowLayout(JsonObject node, UiWindowLayout fallback) => new(
        ReadNodeFloat(node, "x", fallback.X),
        ReadNodeFloat(node, "y", fallback.Y),
        ReadNodeFloat(node, "width", fallback.Width),
        ReadNodeFloat(node, "height", fallback.Height),
        ReadNodeBool(node, "visible", fallback.Visible),
        ReadNodeBool(node, "collapsed", fallback.Collapsed),
        ReadNodeBool(node, "maximized", fallback.Maximized),
        ReadNodeInt(node, "authoredGeometryRevision", 0));

    public void SaveWindowLayout(
        string toonKey,
        string resolutionKey,
        string windowName,
        UiWindowLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toonKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolutionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);

        JsonObject root = LoadMutableRoot();
        JsonObject layouts = GetOrCreateObject(root, "windowLayouts");
        JsonObject toon = GetOrCreateObject(layouts, toonKey);
        JsonObject resolution = GetOrCreateObject(toon, resolutionKey);
        resolution[windowName] = new JsonObject
        {
            ["x"] = layout.X,
            ["y"] = layout.Y,
            ["width"] = layout.Width,
            ["height"] = layout.Height,
            ["visible"] = layout.Visible,
            ["collapsed"] = layout.Collapsed,
            ["maximized"] = layout.Maximized,
            ["authoredGeometryRevision"] = layout.AuthoredGeometryRevision,
        };
        root["version"] = CurrentSchemaVersion;
        WriteMutableRoot(root);
    }

    public UiWindowLayout? LoadNamedWindowLayout(
        string profileName,
        string windowName,
        UiWindowLayout fallback)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);
        if (!File.Exists(_path)) return null;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            JsonObject? node = root?["namedWindowLayouts"]?[profileName]?[windowName] as JsonObject;
            return node is null
                ? null
                : DeserializeWindowLayout(node, fallback);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: failed to load named window layout from {_path}: {ex.Message}");
            return null;
        }
    }

    public void SaveNamedWindowLayout(
        string profileName,
        string windowName,
        UiWindowLayout layout)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);

        JsonObject root = LoadMutableRoot();
        JsonObject profiles = GetOrCreateObject(root, "namedWindowLayouts");
        JsonObject profile = GetOrCreateObject(profiles, profileName);
        profile[windowName] = SerializeWindowLayout(layout);
        root["version"] = CurrentSchemaVersion;
        WriteMutableRoot(root);
    }

    private static JsonObject SerializeWindowLayout(UiWindowLayout layout) => new()
    {
        ["x"] = layout.X,
        ["y"] = layout.Y,
        ["width"] = layout.Width,
        ["height"] = layout.Height,
        ["visible"] = layout.Visible,
        ["collapsed"] = layout.Collapsed,
        ["maximized"] = layout.Maximized,
        ["authoredGeometryRevision"] = layout.AuthoredGeometryRevision,
    };

    private JsonObject LoadMutableRoot()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(_path)) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private void WriteMutableRoot(JsonObject root)
        => File.WriteAllText(_path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static float ReadNodeFloat(JsonObject node, string key, float fallback)
    {
        try { return node[key]?.GetValue<float>() ?? fallback; }
        catch { return fallback; }
    }

    private static bool ReadNodeBool(JsonObject node, string key, bool fallback)
    {
        try { return node[key]?.GetValue<bool>() ?? fallback; }
        catch { return fallback; }
    }

    private static int ReadNodeInt(JsonObject node, string key, int fallback)
    {
        try { return node[key]?.GetValue<int>() ?? fallback; }
        catch { return fallback; }
    }

    private static bool TryParseResolution(string key, out int width, out int height)
    {
        width = 0;
        height = 0;
        int separator = key.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        return separator > 0
            && int.TryParse(
                key.AsSpan(0, separator),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out width)
            && int.TryParse(
                key.AsSpan(separator + 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out height)
            && width > 0
            && height > 0;
    }

    private static SortedDictionary<string, object> BuildChatObject(ChatSettings c)
        => new(StringComparer.Ordinal)
        {
            ["activeOpacity"]     = c.ActiveOpacity,
            ["appearOffline"]     = c.AppearOffline,
            ["chatFontFace"]      = c.ChatFontFace,
            ["chatFontSizeIndex"] = c.ChatFontSizeIndex,
            ["chatWindow1Filter"] = c.ChatWindow1Filter,
            ["chatWindow2Filter"] = c.ChatWindow2Filter,
            ["chatWindow3Filter"] = c.ChatWindow3Filter,
            ["chatWindow4Filter"] = c.ChatWindow4Filter,
            ["chatWindowMainFilter"] = c.ChatWindowMainFilter,
            ["defaultOpacity"]   = c.DefaultOpacity,
            ["filterProfanity"]  = c.FilterProfanity,
            ["fontSize"]         = c.FontSize,
            ["hearGeneralChat"]  = c.HearGeneralChat,
            ["hearLFGChat"]      = c.HearLFGChat,
            ["hearRoleplayChat"] = c.HearRoleplayChat,
            ["hearSocietyChat"]  = c.HearSocietyChat,
            ["hearTradeChat"]    = c.HearTradeChat,
            ["showTimestamps"]   = c.ShowTimestamps,
        };

    private static SortedDictionary<string, object> BuildDisplayObject(DisplaySettings d)
        => new(StringComparer.Ordinal)
        {
            ["automaticDegrades"]        = d.AutomaticDegrades,
            ["buildingDetailTextures"]   = d.BuildingDetailTextures,
            ["degradeDistance"]          = d.DegradeDistance,
            ["environmentTextureDetail"] = d.EnvironmentTextureDetail,
            ["fieldOfView"] = d.FieldOfView,
            ["fullscreen"]  = d.Fullscreen,
            ["gamma"]       = d.Gamma,
            ["graphicsPerformance"]      = d.GraphicsPerformance,
            ["landscapeDrawDistance"]    = d.LandscapeDrawDistance,
            ["landscapeTextureDetail"]   = d.LandscapeTextureDetail,
            ["multiPassAlpha"]           = d.MultiPassAlpha,
            ["particleRange"] = d.ParticleRange.ToString(),
            ["quality"]     = d.Quality.ToString(),
            ["renderPack"]  = BuildRenderPackObject(d.RenderPack),
            ["resolution"]  = d.Resolution,
            ["screenBrightness"]         = d.ScreenBrightness,
            ["showFps"]     = d.ShowFps,
            ["textureFiltering"]         = d.TextureFiltering,
            ["vsync"]       = d.VSync,
        };

    private static SortedDictionary<string, object?> BuildRenderPackObject(
        RenderPackSelectionSettings selection)
    {
        var overrides = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach ((string key, string value) in selection.SettingOverrides)
            overrides[key] = value;
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["packId"] = selection.PackId,
            ["packVersion"] = selection.PackVersion,
            ["presetId"] = selection.PresetId,
            ["settingOverrides"] = overrides,
        };
    }

    private static SortedDictionary<string, object> BuildMiscObject(MiscSettings m)
        => new(StringComparer.Ordinal)
        {
            ["tooltipDelaySeconds"] = m.TooltipDelaySeconds,
            ["tooltipEnable"]       = m.TooltipEnable,
        };

    private static SortedDictionary<string, object> BuildCameraTurningObject(CameraTurningSettings c)
        => new(StringComparer.Ordinal)
        {
            ["adjustmentSpeed"]      = c.AdjustmentSpeed,
            ["alignToSlope"]         = c.AlignToSlope,
            ["invertMouseLookYAxis"] = c.InvertMouseLookYAxis,
            ["mouseLookSensitivity"] = c.MouseLookSensitivity,
            ["stiffness"]            = c.Stiffness,
            ["useMouseTurning"]      = c.UseMouseTurning,
        };

    private static SortedDictionary<string, object> BuildAudioObject(AudioSettings a)
        => new(StringComparer.Ordinal)
        {
            ["ambient"]                 = a.Ambient,
            ["ambientEnabled"]          = a.AmbientEnabled,
            ["interfaceEnabled"]        = a.InterfaceEnabled,
            ["interfaceVolume"]         = a.InterfaceVolume,
            ["master"]                  = a.Master,
            ["playSoundOnlyWhenActive"] = a.PlaySoundOnlyWhenActive,
            ["sfx"]                     = a.Sfx,
            ["sfxEnabled"]              = a.SfxEnabled,
            ["soundFeatures"]           = a.SoundFeatures,
        };

    /// <summary>
    /// Generic atomic-section save: writes the named section and preserves
    /// all other top-level keys from the existing file, replacing only the
    /// version + the targeted section. Avoids duplication between the
    /// per-section Save methods.
    /// </summary>
    private void SaveSection(string sectionName, SortedDictionary<string, object> sectionPayload)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Preserve any non-target top-level keys from the existing file.
        var preservedKeys = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(_path))
        {
            try
            {
                using var stream = File.OpenRead(_path);
                var doc  = JsonDocument.Parse(stream);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name == sectionName || prop.Name == "version") continue;
                    preservedKeys[prop.Name] = prop.Value.GetRawText();
                }
            }
            catch
            {
                preservedKeys.Clear();
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append('{').AppendLine();
        foreach (var kv in preservedKeys)
        {
            sb.Append("  \"").Append(kv.Key).Append("\": ")
              .Append(kv.Value).Append(',').AppendLine();
        }
        sb.Append("  \"").Append(sectionName).Append("\": ")
          .Append(JsonSerializer.Serialize(sectionPayload, new JsonSerializerOptions { WriteIndented = true })
                                 .Replace("\n", "\n  "))
          .Append(',').AppendLine();
        sb.Append("  \"version\": ").Append(CurrentSchemaVersion).AppendLine();
        sb.Append('}').AppendLine();

        File.WriteAllText(_path, sb.ToString());
    }

    private static RenderPackSelectionSettings ReadRenderPackSelection(
        JsonElement display,
        RenderPackSelectionSettings fallback)
    {
        if (!display.TryGetProperty("renderPack", out JsonElement value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        string packId = ReadString(value, "packId", string.Empty);
        string presetId = ReadString(value, "presetId", string.Empty);
        if (string.IsNullOrWhiteSpace(packId) || string.IsNullOrWhiteSpace(presetId))
            return fallback;
        string? version = null;
        if (value.TryGetProperty("packVersion", out JsonElement versionElement)
            && versionElement.ValueKind == JsonValueKind.String)
        {
            version = versionElement.GetString();
        }

        var overrides = new List<KeyValuePair<string, string>>();
        if (value.TryGetProperty("settingOverrides", out JsonElement overrideElement))
        {
            if (overrideElement.ValueKind != JsonValueKind.Object)
                return fallback;
            foreach (JsonProperty property in overrideElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String
                    || property.Value.GetString() is not { } settingValue)
                {
                    return fallback;
                }
                overrides.Add(new KeyValuePair<string, string>(property.Name, settingValue));
            }
        }

        return new RenderPackSelectionSettings(packId, version, presetId)
        {
            SettingOverrides = new RenderPackSettingOverrides(overrides),
        };
    }

    private static string ReadString(JsonElement obj, string name, string fallback)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? fallback) : fallback;

    private static bool ReadBool(JsonElement obj, string name, bool fallback)
        => obj.TryGetProperty(name, out var el)
           && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
                ? el.GetBoolean() : fallback;

    private static float ReadFloat(JsonElement obj, string name, float fallback)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetSingle() : fallback;

    private static int ReadInt(JsonElement obj, string name, int fallback)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32() : fallback;

    private static ulong ReadULong(JsonElement obj, string name, ulong fallback)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return fallback;
        return el.TryGetUInt64(out ulong value) ? value : fallback;
    }

    private static QualityPreset ReadQuality(JsonElement obj, string name, QualityPreset fallback)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return fallback;
        var s = el.GetString();
        return Enum.TryParse<QualityPreset>(s, ignoreCase: true, out var v) ? v : fallback;
    }

    private static ParticleRange ReadParticleRange(
        JsonElement obj,
        string name,
        ParticleRange fallback)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return fallback;
        var value = el.GetString();
        return Enum.TryParse(value, ignoreCase: true, out ParticleRange parsed)
               && Enum.IsDefined(parsed)
            ? parsed
            : fallback;
    }
}
