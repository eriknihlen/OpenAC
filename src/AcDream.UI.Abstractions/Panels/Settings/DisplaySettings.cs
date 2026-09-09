using System.Collections.Generic;
using AcDream.UI.Abstractions.Settings;

namespace AcDream.UI.Abstractions.Panels.Settings;

public enum ParticleRange
{
    Retail = 0,
    Extended = 1,
}

public sealed class RenderPackSettingOverrides :
    IReadOnlyDictionary<string, string>,
    IEquatable<RenderPackSettingOverrides>
{
    private readonly SortedDictionary<string, string> _values;

    public static RenderPackSettingOverrides Empty { get; } = new([]);

    public RenderPackSettingOverrides(
        IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in values)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(value);
            _values[key] = value;
        }
    }

    public int Count => _values.Count;

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<string> Values => _values.Values;

    public string this[string key] => _values[key];

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out string value) =>
        _values.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
        _values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();

    public RenderPackSettingOverrides Set(string settingId, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingId);
        ArgumentNullException.ThrowIfNull(value);
        var next = new SortedDictionary<string, string>(
            _values,
            StringComparer.OrdinalIgnoreCase)
        {
            [settingId] = value,
        };
        return new RenderPackSettingOverrides(next);
    }

    public bool Equals(RenderPackSettingOverrides? other) =>
        other is not null
        && _values.Count == other._values.Count
        && _values.All(pair => other._values.TryGetValue(pair.Key, out string? value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));

    public override bool Equals(object? obj) =>
        obj is RenderPackSettingOverrides other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach ((string key, string value) in _values)
        {
            hash.Add(key, StringComparer.OrdinalIgnoreCase);
            hash.Add(value, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}

public sealed record RenderPackSelectionSettings(
    string PackId,
    string? PackVersion,
    string PresetId)
{
    public const string RetailPackId = "retail";
    public const string RetailPresetId = "off";

    public static RenderPackSelectionSettings Retail { get; } = new(
        RetailPackId,
        PackVersion: null,
        RetailPresetId);

    /// <summary>
    /// User-authored values keyed by the selected pack's stable setting IDs.
    /// Empty by default so pre-render-pack settings files upgrade without a
    /// migration write.
    /// </summary>
    public RenderPackSettingOverrides SettingOverrides { get; init; } =
        RenderPackSettingOverrides.Empty;

    public bool IsRetail =>
        string.Equals(PackId, RetailPackId, StringComparison.OrdinalIgnoreCase);
}

public sealed record DisplaySettings(
    string Resolution,
    bool Fullscreen,
    bool VSync,
    float FieldOfView,
    float Gamma,
    bool ShowFps,
    QualityPreset Quality,
    ParticleRange ParticleRange,
    float ScreenBrightness = 0f,
    bool AutomaticDegrades = false,
    float GraphicsPerformance = 0f,
    float DegradeDistance = 50f,
    int LandscapeTextureDetail = 2,
    int EnvironmentTextureDetail = 1,
    int TextureFiltering = 1,
    int LandscapeDrawDistance = 8,
    bool BuildingDetailTextures = true,
    bool MultiPassAlpha = false)
{
    public RenderPackSelectionSettings RenderPack { get; init; } =
        RenderPackSelectionSettings.Retail;

    public static DisplaySettings Default { get; } = new(
        Resolution:   "1280x720",
        Fullscreen:   false,
        VSync:        true,
        FieldOfView:  90f,
        Gamma:        1.0f,
        ShowFps:      false,
        Quality:      QualityPreset.High,
        ParticleRange: ParticleRange.Extended);

    public static IReadOnlyList<string> AvailableResolutions { get; } = new[]
    {
        "1280x720",
        "1366x768",
        "1600x900",
        "1920x1080",
        "2560x1440",
        "3840x2160",
    };
}
