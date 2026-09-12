using AcDream.Core.Audio;
using Xunit;

namespace AcDream.Core.Tests.Audio;

// OpenAC #42 follow-up: the mixer settings are meant to be tried by ear, so
// /mixer has to report and change them from the chat line without a restart.
public sealed class AudioMixerCommandTests
{
    [Fact]
    public void TheDefaults_AreTheEnhancedMixer()
    {
        AudioMixerOptions defaults = AudioMixerOptions.Default;

        Assert.False(defaults.RetailMixer);
        Assert.Equal(32, defaults.EffectiveVoiceCount);
        Assert.True(defaults.EffectiveUseAuthoredPriority);
        Assert.Equal(4, defaults.EffectiveMaxVoicesPerWave);
    }

    [Fact]
    public void WithNoArguments_ItReportsWhatTheMixerIsDoing_AndChangesNothing()
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            AudioMixerOptions.Default,
            arguments: null);

        Assert.False(result.Changed);
        Assert.Equal(
            "mixer: retail mixer off, 32 voices, authored priority on, "
            + "at most 4 voices per sound",
            Assert.Single(result.Lines));
    }

    [Fact]
    public void RetailOn_PinsSixteenVoicesWithNoPriorityAndNoCap()
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            AudioMixerOptions.Default,
            "retail on");

        Assert.True(result.Changed);
        Assert.True(result.Options.RetailMixer);
        Assert.Equal(16, result.Options.EffectiveVoiceCount);
        Assert.False(result.Options.EffectiveUseAuthoredPriority);
        Assert.Equal(
            AudioMixerOptions.NoPerWaveCap,
            result.Options.EffectiveMaxVoicesPerWave);
        Assert.Equal(
            "mixer: retail mixer on, 16 voices, authored priority off, no cap per sound",
            Assert.Single(result.Lines));
    }

    // The knob overrides the other three rather than rewriting them, so
    // turning it off gives back exactly what was set before.
    [Fact]
    public void RetailOff_GivesBackTheSettingsItOverrode()
    {
        var chosen = new AudioMixerOptions
        {
            RetailMixer = true,
            VoiceCount = 48,
            UseAuthoredPriority = true,
            MaxVoicesPerWave = 6,
        };

        AudioMixerCommandResult result = AudioMixerCommand.Execute(chosen, "retail off");

        Assert.True(result.Changed);
        Assert.Equal(48, result.Options.EffectiveVoiceCount);
        Assert.True(result.Options.EffectiveUseAuthoredPriority);
        Assert.Equal(6, result.Options.EffectiveMaxVoicesPerWave);
    }

    [Theory]
    [InlineData("voices 48", 48)]
    [InlineData("voices 16", 16)]
    [InlineData("voices 64", 64)]
    [InlineData("voices 8", AudioMixerOptions.MinimumVoiceCount)]
    [InlineData("voices 4096", AudioMixerOptions.MaximumVoiceCount)]
    [InlineData("VOICES 40", 40)]
    public void Voices_SetsTheCountInsideItsRange(string arguments, int expected)
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            AudioMixerOptions.Default,
            arguments);

        Assert.Equal(expected, result.Options.EffectiveVoiceCount);
    }

    [Fact]
    public void PriorityOff_StopsAnImportantSoundFromTakingAVoice()
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            AudioMixerOptions.Default,
            "priority off");

        Assert.True(result.Changed);
        Assert.False(result.Options.EffectiveUseAuthoredPriority);
        Assert.Equal(
            "mixer: retail mixer off, 32 voices, authored priority off, "
            + "at most 4 voices per sound",
            Assert.Single(result.Lines));
    }

    [Theory]
    [InlineData("perwave 0", AudioMixerOptions.NoPerWaveCap)]
    [InlineData("perwave 8", 8)]
    [InlineData("perwave 999", 32)]     // never more than the voices there are
    public void PerWave_SetsTheCapInsideItsRange(string arguments, int expected)
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            AudioMixerOptions.Default,
            arguments);

        Assert.Equal(expected, result.Options.EffectiveMaxVoicesPerWave);
    }

    [Fact]
    public void PerWaveZero_IsReportedAsNoCap()
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            AudioMixerOptions.Default,
            "perwave 0");

        Assert.Equal(
            "mixer: retail mixer off, 32 voices, authored priority on, no cap per sound",
            Assert.Single(result.Lines));
    }

    [Theory]
    [InlineData("wibble")]
    [InlineData("voices")]
    [InlineData("voices lots")]
    [InlineData("voices 32 48")]
    [InlineData("retail")]
    [InlineData("retail maybe")]
    [InlineData("priority -1")]
    [InlineData("perwave four")]
    public void NonsenseChangesNothing_AndSaysHowToUseIt(string arguments)
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            AudioMixerOptions.Default,
            arguments);

        Assert.False(result.Changed);
        Assert.Equal(AudioMixerOptions.Default.Normalized(), result.Options);
        Assert.Equal(AudioMixerCommand.Usage, Assert.Single(result.Lines));
    }

    // Setting a value to what it already is is not a change, so nothing is
    // written down and nothing is rebuilt.
    [Fact]
    public void SettingAValueToWhatItAlreadyIs_IsNotAChange()
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            AudioMixerOptions.Default,
            "voices 32");

        Assert.False(result.Changed);
    }

    [Theory]
    [InlineData("retail 1", true)]
    [InlineData("retail true", true)]
    [InlineData("retail 0", false)]
    [InlineData("retail false", false)]
    public void OnAndOff_AlsoAnswerToTrueAndFalse(string arguments, bool expected)
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            AudioMixerOptions.Default,
            arguments);

        Assert.Equal(expected, result.Options.RetailMixer);
    }

    // A settings file edited by hand cannot put the mixer somewhere it could
    // not otherwise reach.
    [Fact]
    public void HandEditedValues_AreBroughtInsideTheirRanges()
    {
        AudioMixerOptions normalized = new AudioMixerOptions
        {
            VoiceCount = 4096,
            MaxVoicesPerWave = -3,
        }.Normalized();

        Assert.Equal(AudioMixerOptions.MaximumVoiceCount, normalized.VoiceCount);
        Assert.Equal(AudioMixerOptions.NoPerWaveCap, normalized.MaxVoicesPerWave);

        AudioMixerOptions tooManyPerWave = new AudioMixerOptions
        {
            VoiceCount = 16,
            MaxVoicesPerWave = 40,
        }.Normalized();

        Assert.Equal(16, tooManyPerWave.MaxVoicesPerWave);
    }
}
