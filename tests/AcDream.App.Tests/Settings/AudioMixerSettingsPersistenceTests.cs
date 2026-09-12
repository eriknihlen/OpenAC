using System;
using System.IO;
using AcDream.Core.Audio;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.Settings;

// OpenAC #42 follow-up: the mixer settings are the player's, so they have to
// survive the session that set them — and they must not be at the mercy of the
// options panel, which writes the game's own sound section whole.
public sealed class AudioMixerSettingsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "acdream-audio-mixer-tests-" + Guid.NewGuid().ToString("N"));

    private string PathName => Path.Combine(_directory, "settings.json");

    [Fact]
    public void WithNoSettingsFile_TheDefaultsAreTheEnhancedMixer()
    {
        AudioMixerOptions loaded = new SettingsStore(PathName).LoadAudioMixer();

        Assert.False(loaded.RetailMixer);
        Assert.Equal(32, loaded.VoiceCount);
        Assert.True(loaded.UseAuthoredPriority);
        Assert.Equal(4, loaded.MaxVoicesPerWave);
    }

    [Fact]
    public void EverySettingSurvivesASaveAndLoad()
    {
        var store = new SettingsStore(PathName);
        var chosen = new AudioMixerOptions
        {
            RetailMixer = true,
            VoiceCount = 48,
            UseAuthoredPriority = false,
            MaxVoicesPerWave = 7,
        };

        store.SaveAudioMixer(chosen);

        Assert.Equal(chosen, new SettingsStore(PathName).LoadAudioMixer());
    }

    [Fact]
    public void AHandEditedValue_IsBroughtInsideItsRangeWhenItIsRead()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            PathName,
            """
            {
              "audioMixer": {
                "maxVoicesPerWave": 900,
                "retailMixer": false,
                "useAuthoredPriority": true,
                "voiceCount": 4096
              },
              "version": 3
            }
            """);

        AudioMixerOptions loaded = new SettingsStore(PathName).LoadAudioMixer();

        Assert.Equal(AudioMixerOptions.MaximumVoiceCount, loaded.VoiceCount);
        Assert.Equal(AudioMixerOptions.MaximumVoiceCount, loaded.MaxVoicesPerWave);
    }

    // The game's own sound options are written whole from the options panel.
    // Sharing a section with them would lose the mixer settings the first time
    // the player touched a volume slider.
    [Fact]
    public void SavingTheGameSoundOptions_LeavesTheMixerSettingsAlone()
    {
        var store = new SettingsStore(PathName);
        store.SaveAudioMixer(new AudioMixerOptions { VoiceCount = 64 });

        store.SaveAudio(AudioSettings.Default with { Master = 0.25f });

        Assert.Equal(64, store.LoadAudioMixer().VoiceCount);
        Assert.Equal(0.25f, store.LoadAudio().Master);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
