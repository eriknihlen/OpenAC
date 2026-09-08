using AcDream.UI.Abstractions.Settings;
using Xunit;

namespace AcDream.UI.Abstractions.Tests.Panels.Settings;

public class QualityPresetTests
{
    [Theory]
    [InlineData(QualityPreset.Low,    2, 5,  0)]
    [InlineData(QualityPreset.Medium, 3, 8,  2)]
    [InlineData(QualityPreset.High,   4, 12, 4)]
    [InlineData(QualityPreset.Ultra,  5, 15, 4)]
    public void From_Preset_ProducesExpectedRadiiAndMsaa(
        QualityPreset preset, int n1, int n2, int msaa)
    {
        var s = QualitySettings.From(preset);
        Assert.Equal(n1,   s.NearRadius);
        Assert.Equal(n2,   s.FarRadius);
        Assert.Equal(msaa, s.MsaaSamples);
    }

    [Theory]
    [InlineData(QualityPreset.Low,    4,  false)]
    [InlineData(QualityPreset.Medium, 8,  false)]
    [InlineData(QualityPreset.High,   16, true)]
    [InlineData(QualityPreset.Ultra,  16, true)]
    public void From_Preset_ProducesExpectedAnisoAndA2C(
        QualityPreset preset, int aniso, bool a2c)
    {
        var s = QualitySettings.From(preset);
        Assert.Equal(aniso, s.AnisotropicLevel);
        Assert.Equal(a2c,   s.AlphaToCoverage);
    }

    [Theory]
    [InlineData(QualityPreset.Low,    2)]
    [InlineData(QualityPreset.Medium, 3)]
    [InlineData(QualityPreset.High,   4)]
    [InlineData(QualityPreset.Ultra,  6)]
    public void From_Preset_ProducesExpectedMaxCompletions(
        QualityPreset preset, int expected)
    {
        var s = QualitySettings.From(preset);
        Assert.Equal(expected, s.MaxCompletionsPerFrame);
    }

    [Fact]
    public void EnvVar_NearRadius_OverridesPreset()
    {
        System.Environment.SetEnvironmentVariable("ACDREAM_NEAR_RADIUS", "2");
        try
        {
            var s = QualitySettings.From(QualityPreset.High);  // High = NearRadius=4 normally
            var resolved = QualitySettings.WithEnvOverrides(s);
            Assert.Equal(2,  resolved.NearRadius);
            Assert.Equal(12, resolved.FarRadius);   // FarRadius unaffected
        }
        finally { System.Environment.SetEnvironmentVariable("ACDREAM_NEAR_RADIUS", null); }
    }

    [Fact]
    public void EnvVar_FarRadius_OverridesPreset()
    {
        System.Environment.SetEnvironmentVariable("ACDREAM_FAR_RADIUS", "20");
        try
        {
            var s        = QualitySettings.From(QualityPreset.High);
            var resolved = QualitySettings.WithEnvOverrides(s);
            Assert.Equal(4,  resolved.NearRadius);  // NearRadius unaffected
            Assert.Equal(20, resolved.FarRadius);
        }
        finally { System.Environment.SetEnvironmentVariable("ACDREAM_FAR_RADIUS", null); }
    }

    [Fact]
    public void EnvVar_AlphaToCoverage_BooleanParsing()
    {
        // Ensure "0" and "false" disable; other values enable.
        System.Environment.SetEnvironmentVariable("ACDREAM_A2C", "0");
        try
        {
            var s        = QualitySettings.From(QualityPreset.High);  // High has A2C=true
            var resolved = QualitySettings.WithEnvOverrides(s);
            Assert.False(resolved.AlphaToCoverage);
        }
        finally { System.Environment.SetEnvironmentVariable("ACDREAM_A2C", null); }
    }

    [Fact]
    public void EnvVar_AlphaToCoverage_FalseString_Disables()
    {
        System.Environment.SetEnvironmentVariable("ACDREAM_A2C", "false");
        try
        {
            var s        = QualitySettings.From(QualityPreset.High);
            var resolved = QualitySettings.WithEnvOverrides(s);
            Assert.False(resolved.AlphaToCoverage);
        }
        finally { System.Environment.SetEnvironmentVariable("ACDREAM_A2C", null); }
    }

    [Fact]
    public void EnvVar_AlphaToCoverage_NonZeroEnables()
    {
        System.Environment.SetEnvironmentVariable("ACDREAM_A2C", "1");
        try
        {
            var s        = QualitySettings.From(QualityPreset.Low);  // Low has A2C=false
            var resolved = QualitySettings.WithEnvOverrides(s);
            Assert.True(resolved.AlphaToCoverage);
        }
        finally { System.Environment.SetEnvironmentVariable("ACDREAM_A2C", null); }
    }

    [Fact]
    public void EnvVar_Unset_LeavesPresetDefault()
    {
        // Ensure no env vars are set for this test's fields.
        System.Environment.SetEnvironmentVariable("ACDREAM_NEAR_RADIUS", null);
        System.Environment.SetEnvironmentVariable("ACDREAM_FAR_RADIUS",  null);
        System.Environment.SetEnvironmentVariable("ACDREAM_A2C",         null);

        var s        = QualitySettings.From(QualityPreset.High);
        var resolved = QualitySettings.WithEnvOverrides(s);
        Assert.Equal(s, resolved);
    }

    [Fact]
    public void From_UndefinedPreset_FallsBackToHigh()
    {
        var s = QualitySettings.From((QualityPreset)99);
        Assert.Equal(4,  s.NearRadius);   // High default
        Assert.Equal(12, s.FarRadius);
        Assert.Equal(4,  s.MsaaSamples);
        Assert.True(s.AlphaToCoverage);
    }

    [Fact]
    public void EnvVar_MaxCompletionsPerFrame_OverridesPreset()
    {
        System.Environment.SetEnvironmentVariable("ACDREAM_MAX_COMPLETIONS_PER_FRAME", "8");
        try
        {
            var s        = QualitySettings.From(QualityPreset.High);  // High = 4
            var resolved = QualitySettings.WithEnvOverrides(s);
            Assert.Equal(8, resolved.MaxCompletionsPerFrame);
        }
        finally { System.Environment.SetEnvironmentVariable("ACDREAM_MAX_COMPLETIONS_PER_FRAME", null); }
    }

    [Fact]
    public void EnvVar_MsaaSamples_OverridesPreset()
    {
        System.Environment.SetEnvironmentVariable("ACDREAM_MSAA_SAMPLES", "8");
        try
        {
            var s        = QualitySettings.From(QualityPreset.High);  // High = 4
            var resolved = QualitySettings.WithEnvOverrides(s);
            Assert.Equal(8, resolved.MsaaSamples);
        }
        finally { System.Environment.SetEnvironmentVariable("ACDREAM_MSAA_SAMPLES", null); }
    }

    [Fact]
    public void EnvVar_Anisotropic_OverridesPreset()
    {
        System.Environment.SetEnvironmentVariable("ACDREAM_ANISOTROPIC", "4");
        try
        {
            var s        = QualitySettings.From(QualityPreset.High);  // High = 16
            var resolved = QualitySettings.WithEnvOverrides(s);
            Assert.Equal(4, resolved.AnisotropicLevel);
        }
        finally { System.Environment.SetEnvironmentVariable("ACDREAM_ANISOTROPIC", null); }
    }
}
