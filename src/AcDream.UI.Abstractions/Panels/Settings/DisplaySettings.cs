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
    int LandscapeTextureDetail = 0,
    int EnvironmentTextureDetail = 1,
    int TextureFiltering = 1,
    int LandscapeDrawDistance = 8,
    bool BuildingDetailTextures = true,
    bool MultiPassAlpha = false,
    bool KeepDistantBuildings = true,
    bool PotatoMode = false,
    bool UiOnly = false,
    bool UiOnlyWhenUnfocused = false,
    int UiScalePercent = DisplaySettings.DefaultUiScalePercent)
{
    /// <summary>The smallest interface scale, in percent.</summary>
    public const int MinimumUiScalePercent = 50;

    /// <summary>The largest interface scale, in percent.</summary>
    public const int MaximumUiScalePercent = 300;

    /// <summary>The interface scale moves in steps of this many percent.</summary>
    public const int UiScaleStepPercent = 10;

    /// <summary>The interface drawn at its authored size.</summary>
    public const int DefaultUiScalePercent = 100;

    /// <summary>
    /// A stored or requested interface scale made valid: held inside
    /// 50..300 and moved to the nearest 10 percent step; a value exactly
    /// between two steps goes up to the larger one.
    /// </summary>
    public static int NormalizeUiScalePercent(int percent)
    {
        int clamped = Math.Clamp(percent, MinimumUiScalePercent, MaximumUiScalePercent);
        double steps = Math.Round(
            (clamped - MinimumUiScalePercent) / (double)UiScaleStepPercent,
            MidpointRounding.AwayFromZero);
        return MinimumUiScalePercent + (int)steps * UiScaleStepPercent;
    }

    public RenderPackSelectionSettings RenderPack { get; init; } =
        RenderPackSelectionSettings.Retail;

    /// <summary>
    /// The settings the runtime runs with. Potato Mode is an overlay for
    /// running many clients on one machine: while it is on, every quality
    /// choice is forced to its cheapest value and the stored choices are left
    /// alone, so turning it off brings them back. Off returns this instance.
    /// </summary>
    public DisplaySettings Effective => PotatoMode
        ? this with
        {
            Quality = QualityPreset.Potato,
            ParticleRange = ParticleRange.Retail,
            AutomaticDegrades = false,
            LandscapeTextureDetail = 4,
            EnvironmentTextureDetail = 4,
            TextureFiltering = 0,
            LandscapeDrawDistance = 3,
            BuildingDetailTextures = false,
            MultiPassAlpha = false,
            RenderPack = RenderPackSelectionSettings.Retail,
        }
        : this;

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
