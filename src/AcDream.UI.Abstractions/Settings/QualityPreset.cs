using System.Globalization;
namespace AcDream.UI.Abstractions.Settings;

public enum QualityPreset { Low, Medium, High, Ultra }

public readonly record struct QualitySettings(
    int   NearRadius,
    int   FarRadius,
    int   MsaaSamples,        // 0 = off, 2, 4, 8
    int   AnisotropicLevel,   // 1 = off, 4, 8, 16
    bool  AlphaToCoverage,
    int   MaxCompletionsPerFrame)
{
    public static QualitySettings From(QualityPreset preset) => preset switch
    {
        QualityPreset.Low    => new(NearRadius: 2, FarRadius: 5,  MsaaSamples: 0, AnisotropicLevel: 4,  AlphaToCoverage: false, MaxCompletionsPerFrame: 2),
        QualityPreset.Medium => new(NearRadius: 3, FarRadius: 8,  MsaaSamples: 2, AnisotropicLevel: 8,  AlphaToCoverage: false, MaxCompletionsPerFrame: 3),
        QualityPreset.High   => new(NearRadius: 4, FarRadius: 12, MsaaSamples: 4, AnisotropicLevel: 16, AlphaToCoverage: true,  MaxCompletionsPerFrame: 4),
        QualityPreset.Ultra  => new(NearRadius: 5, FarRadius: 15, MsaaSamples: 4, AnisotropicLevel: 16, AlphaToCoverage: true,  MaxCompletionsPerFrame: 6),
        _ => From(QualityPreset.High),
    };

    /// <summary>
    /// Apply env-var overrides to a preset's resolved settings. Per-field
    /// env vars beat the preset (so devs can spot-test a single dimension).
    /// Unset or empty env vars leave the preset default unchanged.
    /// </summary>
    public static QualitySettings WithEnvOverrides(QualitySettings baseSettings)
    {
        int nearRadius = TryParseEnvInt("ACDREAM_NEAR_RADIUS",  baseSettings.NearRadius);
        int farRadius  = TryParseEnvInt("ACDREAM_FAR_RADIUS",   baseSettings.FarRadius);
        int msaa       = TryParseEnvInt("ACDREAM_MSAA_SAMPLES", baseSettings.MsaaSamples);
        int aniso      = TryParseEnvInt("ACDREAM_ANISOTROPIC",  baseSettings.AnisotropicLevel);
        // Bool override: any non-empty value other than "0"/"false" enables A2C.
        // Empty / unset → keep preset default.
        var a2cEnv = System.Environment.GetEnvironmentVariable("ACDREAM_A2C");
        bool a2c = a2cEnv switch
        {
            null or "" => baseSettings.AlphaToCoverage,
            "0" or "false" or "False" or "FALSE" => false,
            _ => true,
        };
        int completions = TryParseEnvInt("ACDREAM_MAX_COMPLETIONS_PER_FRAME", baseSettings.MaxCompletionsPerFrame);
        return new QualitySettings(nearRadius, farRadius, msaa, aniso, a2c, completions);
    }

    private static int TryParseEnvInt(string name, int defaultValue)
    {
        var s = System.Environment.GetEnvironmentVariable(name);
        return s is not null
            && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : defaultValue;
    }
}
