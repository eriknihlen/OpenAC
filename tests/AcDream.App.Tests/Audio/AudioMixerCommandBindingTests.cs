using System;
using System.Collections.Generic;
using AcDream.App.Audio;
using AcDream.Core.Audio;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;
using Silk.NET.OpenAL;

namespace AcDream.App.Tests.Audio;

// OpenAC #42 follow-up: /mixer has to change the mixer where it stands, not
// only the file — otherwise every experiment costs a restart.
public sealed class AudioMixerCommandBindingTests
{
    [Fact]
    public void TheCommandIsRegisteredUnderItsVerb_AndReportsTheCurrentSettings()
    {
        var harness = new Harness();

        Assert.True(harness.Commands.TryHandle("/mixer"));

        Assert.Equal(
            "mixer: retail mixer off, 32 voices, authored priority on, "
            + "at most 4 voices per sound",
            Assert.Single(harness.Said));
        Assert.Empty(harness.Saved);
    }

    [Fact]
    public void ChangingTheVoiceCount_ResizesTheLiveMixer_AndIsWrittenDown()
    {
        var harness = new Harness();

        Assert.True(harness.Commands.TryHandle("/mixer voices 40"));

        Assert.Equal(40, harness.Engine.MixerOptions.EffectiveVoiceCount);
        Assert.Equal(40, harness.Api.GeneratedSources.Count);
        Assert.Equal(40, Assert.Single(harness.Saved).VoiceCount);
    }

    [Fact]
    public void TurningTheRetailMixerOn_ReleasesTheVoicesItNoLongerHas()
    {
        var harness = new Harness();

        Assert.True(harness.Commands.TryHandle("/mixer retail on"));

        Assert.Equal(16, harness.Engine.MixerOptions.EffectiveVoiceCount);
        Assert.Equal(16, harness.Api.DeletedSources.Count);
        Assert.True(Assert.Single(harness.Saved).RetailMixer);

        Assert.True(harness.Commands.TryHandle("/mixer retail off"));
        Assert.Equal(32, harness.Engine.MixerOptions.EffectiveVoiceCount);
        Assert.Equal(48, harness.Api.GeneratedSources.Count);   // 32 + 16 again
    }

    [Fact]
    public void ASettingThatDoesNotChange_IsNeitherAppliedNorWrittenDown()
    {
        var harness = new Harness();

        Assert.True(harness.Commands.TryHandle("/mixer voices 32"));

        Assert.Empty(harness.Saved);
        Assert.Equal(32, harness.Api.GeneratedSources.Count);
    }

    [Fact]
    public void NonsenseIsAnsweredWithTheUsageLine_AndChangesNothing()
    {
        var harness = new Harness();

        Assert.True(harness.Commands.TryHandle("/mixer wibble"));

        Assert.Equal(AudioMixerCommand.Usage, Assert.Single(harness.Said));
        Assert.Empty(harness.Saved);
    }

    // A setting is written down BEFORE the running mixer is changed, so a save
    // that fails leaves both alone and says so. The other order would leave a
    // mixer running settings nothing remembers.
    [Fact]
    public void ASaveThatFails_LeavesTheRunningMixerAloneAndSaysSo()
    {
        var harness = new Harness(savesFail: true);

        Assert.True(harness.Commands.TryHandle("/mixer voices 40"));

        Assert.Equal(AudioMixerCommandBinding.SaveFailed, Assert.Single(harness.Said));
        Assert.Equal(32, harness.Engine.MixerOptions.EffectiveVoiceCount);
        Assert.Equal(32, harness.Api.GeneratedSources.Count);
        Assert.Empty(harness.Saved);
    }

    // With no audio device there is no mixer to change. Reporting the new
    // settings as if they were in force would be a lie, so the reply says
    // where they actually took effect.
    [Fact]
    public void WithNoMixerRunning_TheSettingsAreSavedAndTheReplySaysSo()
    {
        var harness = new Harness(audioAvailable: false);

        Assert.True(harness.Commands.TryHandle("/mixer voices 40"));

        Assert.Equal(40, Assert.Single(harness.Saved).VoiceCount);
        Assert.Equal(
            [
                "mixer: retail mixer off, 40 voices, authored priority on, "
                + "at most 4 voices per sound",
                AudioMixerCommandBinding.NoMixerRunning,
            ],
            harness.Said);
        Assert.Empty(harness.Api.GeneratedSources);
    }

    // Reading the settings is not changing them, so it never claims a mixer
    // that is not there.
    [Fact]
    public void WithNoMixerRunning_ReadingTheSettingsSaysNothingExtra()
    {
        var harness = new Harness(audioAvailable: false);

        Assert.True(harness.Commands.TryHandle("/mixer"));

        Assert.Equal(
            "mixer: retail mixer off, 32 voices, authored priority on, "
            + "at most 4 voices per sound",
            Assert.Single(harness.Said));
    }

    [Fact]
    public void WithoutACommandLineOrAudio_ThereIsNothingToBind()
    {
        Assert.Null(AudioMixerCommandBinding.TryRegister(
            commands: null,
            engine: null,
            () => AudioMixerOptions.Default,
            _ => true,
            _ => { }));
    }

    [Fact]
    public void ADisposedBinding_LeavesTheVerbUnclaimed()
    {
        var harness = new Harness();

        harness.Binding.Dispose();

        Assert.False(harness.Commands.TryHandle("/mixer"));
        Assert.Empty(harness.Said);
    }

    private sealed class Harness
    {
        internal Harness(bool audioAvailable = true, bool savesFail = false)
        {
            Api.ContextResult = audioAvailable ? 202 : 0;
            Engine = new OpenAlAudioEngine(new Factory(Api), AudioMixerOptions.Default);
            Assert.Equal(audioAvailable, Engine.IsAvailable);
            Binding = Assert.IsType<AudioMixerCommandBinding>(
                AudioMixerCommandBinding.TryRegister(
                    Commands,
                    Engine,
                    () => _settings,
                    settings =>
                    {
                        if (savesFail)
                            return false;
                        _settings = settings;
                        Saved.Add(settings);
                        return true;
                    },
                    Said.Add));
        }

        private AudioMixerOptions _settings = AudioMixerOptions.Default;

        internal RecordingApi Api { get; } = new();

        internal PluginCommandRegistry Commands { get; } = new();

        internal OpenAlAudioEngine Engine { get; }

        internal AudioMixerCommandBinding Binding { get; }

        internal List<AudioMixerOptions> Saved { get; } = [];

        internal List<string> Said { get; } = [];
    }

    private sealed class Factory(IOpenAlResourceApi api) : IOpenAlResourceApiFactory
    {
        public IOpenAlResourceApi Create() => api;
    }

    private sealed class RecordingApi : IOpenAlResourceApi
    {
        private uint _nextSource = 1;

        public AL? AudioApi => null;
        public ALContext? ContextApi => null;
        public List<uint> GeneratedSources { get; } = [];
        public List<uint> DeletedSources { get; } = [];

        public nint ContextResult { get; set; } = 202;

        public nint OpenDevice() => 101;
        public bool SupportsOutputLimiterControl(nint device) => false;
        public nint CreateContext(nint device, int[]? attributes) => ContextResult;
        public int? ReadOutputLimiterState(nint device) => null;
        public bool MakeContextCurrent(nint context) => true;

        public uint GenerateSource()
        {
            uint source = _nextSource++;
            GeneratedSources.Add(source);
            return source;
        }

        public void Configure3DSource(uint source) { }
        public void DisableAlDistanceAttenuation() { }
        public void StopSource(uint source) { }
        public void DeleteSource(uint source) => DeletedSources.Add(source);
        public void DeleteBuffer(uint buffer) { }
        public void DestroyContext(nint context) { }
        public void CloseDevice(nint device) { }
    }
}
