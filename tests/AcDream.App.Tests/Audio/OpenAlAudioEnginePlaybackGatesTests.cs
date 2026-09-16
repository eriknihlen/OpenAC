using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Audio;
using AcDream.Core.Audio;
using Silk.NET.OpenAL;

namespace AcDream.App.Tests.Audio;

// PR #107 review (Erik): three claims settled against the retail decompile.
// Every gate under test is driven through a recording backend that stands in
// for the device, so these run the same on a runner with no OpenAL at all.
public sealed class OpenAlAudioEnginePlaybackGatesTests
{
    // SoundManager::GetAttenuation (0x550020) multiplies by effect_sound_volume
    // whenever it is not the ambient case — the same path PlaySoundFromCenter
    // (0x550950, 0x5509e0) takes for interface sounds. SoundManager::
    // interface_sound_volume (0x81f078) is written by the preference and read
    // nowhere.
    [Fact]
    public void InterfaceSounds_FollowTheEffectVolume()
    {
        var backend = new PlaybackBackend();
        using var engine = new OpenAlAudioEngine(new Factory(backend));
        Assert.True(engine.IsAvailable);

        engine.SfxVolume = 0f;
        Assert.False(engine.PlayUiWave(1, Tone(), volume: 1f, priority: 1f));
        Assert.Empty(backend.Playing);

        engine.SfxVolume = 1f;
        Assert.True(engine.PlayUiWave(2, Tone(), volume: 1f, priority: 1f));
        Assert.Single(backend.Playing);
    }

    // SoundManager::PlaySoundInternal (0x54fec0, 0x550170) is the only place
    // s_bPlaySoundOnlyWhenActive is read, ahead of the source being told to
    // play. Nothing there touches listener gain.
    [Fact]
    public void FocusMuted_RefusesNewSounds_WithoutTouchingListenerGain()
    {
        var backend = new PlaybackBackend();
        using var engine = new OpenAlAudioEngine(new Factory(backend));

        engine.FocusMuted = true;
        Assert.False(engine.Play3DWave(1, 1, Tone(), Vector3.Zero, volume: 1f, priority: 1f));
        Assert.False(engine.PlayUiWave(2, Tone(), volume: 1f, priority: 1f));
        Assert.False(engine.PlayAmbientFromCenter(3, Tone(), volume: 1f, priority: 1f));
        Assert.Empty(backend.Playing);
        Assert.Null(backend.ListenerGain);

        engine.FocusMuted = false;
        Assert.True(engine.Play3DWave(4, 4, Tone(), Vector3.Zero, volume: 1f, priority: 1f));
        Assert.Single(backend.Playing);
        Assert.Null(backend.ListenerGain);
    }

    // Shane's own divergence from #107: losing focus doesn't just refuse new
    // sounds, it cuts whatever world sound is already playing, the same way
    // SuspendWorldAudio does. The per-sound voice cap forces this across
    // thirty-two distinct waves so the pool is genuinely full of sounds still
    // sounding, not one sound holding several voices.
    [Fact]
    public void FocusMuted_CutsSoundsAlreadyPlaying()
    {
        var backend = new PlaybackBackend();
        using var engine = new OpenAlAudioEngine(new Factory(backend), new AudioMixerOptions
        {
            UseAuthoredPriority = false,
            MaxVoicesPerWave = AudioMixerOptions.NoPerWaveCap,
        });

        WaveData tone = Tone();
        for (uint i = 0; i < 32; i++)
            Assert.True(engine.Play3DWave(i + 1, i + 1, tone, Vector3.Zero, volume: 1f, priority: 1f));
        Assert.Equal(32, backend.Playing.Count);

        // Every voice is genuinely still playing: a thirty-third distinct wave
        // is dropped rather than stealing one of them.
        Assert.False(engine.Play3DWave(900, 900, tone, Vector3.Zero, volume: 1f, priority: 1f));

        int stopsBefore = backend.Stops;
        engine.FocusMuted = true;
        Assert.Empty(backend.Playing);
        int stopsAfterCut = backend.Stops;
        Assert.True(stopsAfterCut > stopsBefore);

        engine.FocusMuted = true;   // twice in a row does the work once
        Assert.Equal(stopsAfterCut, backend.Stops);

        engine.FocusMuted = false;  // starts nothing on its own
        Assert.Empty(backend.Playing);

        for (uint i = 0; i < 32; i++)
            Assert.True(engine.Play3DWave(1000 + i, 1000 + i, tone, Vector3.Zero, volume: 1f, priority: 1f));
        Assert.Equal(32, backend.Playing.Count);
    }

    // AudioHookSink routes the portal tunnel's own animation cues through the
    // same PlayUiWave as a genuine interface sound (UiPresentationHookSink);
    // only the latter is meant to hear "Disable Interface Sound".
    [Fact]
    public void DisablingInterfaceSound_LeavesTheNonInterfaceCallerAlone()
    {
        var backend = new PlaybackBackend();
        using var engine = new OpenAlAudioEngine(new Factory(backend));

        engine.InterfaceEnabled = false;

        Assert.False(engine.PlayUiWave(1, Tone(), volume: 1f, priority: 1f));
        Assert.Empty(backend.Playing);
        Assert.True(engine.PlayUiWave(2, Tone(), volume: 1f, priority: 1f, isInterfaceSound: false));
        Assert.Single(backend.Playing);
    }

    private static WaveData Tone() => new()
    {
        ChannelCount = 1,
        SampleRate = 8000,
        BitsPerSample = 8,
        PcmBytes = new byte[] { 128, 160, 192, 160, 128, 96, 64, 96 },
    };

    private sealed class Factory(IOpenAlResourceApi api) : IOpenAlResourceApiFactory
    {
        public IOpenAlResourceApi Create() => api;
    }

    /// <summary>
    /// A device that never finishes a sound: a source stays playing from
    /// PlaySource until StopSource, so a full pool stays full and a cut is
    /// visible as the set emptying.
    /// </summary>
    private sealed class PlaybackBackend : IOpenAlResourceApi
    {
        private uint _nextSource = 1;
        private uint _nextBuffer = 1;
        private readonly Dictionary<uint, uint> _attached = new();

        public HashSet<uint> Playing { get; } = new();
        public int Stops { get; private set; }
        public float? ListenerGain { get; private set; }

        public nint OpenDevice() => 101;
        public bool SupportsOutputLimiterControl(nint device) => false;
        public nint CreateContext(nint device, int[]? attributes) => 202;
        public int? ReadOutputLimiterState(nint device) => null;
        public bool MakeContextCurrent(nint context) => true;
        public uint GenerateSource() => _nextSource++;
        public void Configure3DSource(uint source) { }
        public void DisableAlDistanceAttenuation() { }

        public void StopSource(uint source)
        {
            if (Playing.Remove(source))
                Stops++;
        }

        public void DeleteSource(uint source) => Playing.Remove(source);
        public void DeleteBuffer(uint buffer) { }
        public void DestroyContext(nint context) { }
        public void CloseDevice(nint device) { }

        public void SetListenerGain(float gain) => ListenerGain = gain;
        public uint GenerateBuffer() => _nextBuffer++;
        public void FillBuffer(uint buffer, BufferFormat format, ReadOnlySpan<byte> pcm, int sampleRate) { }
        public void AttachBuffer(uint source, uint buffer) => _attached[source] = buffer;
        public uint AttachedBuffer(uint source) => _attached.TryGetValue(source, out uint b) ? b : 0u;
        public void SetSourceGain(uint source, float gain) { }
        public void SetSourceLooping(uint source, bool looping) { }
        public void PlaceSourceRelative(uint source, float x, float y, float z) { }
        public void PlaySource(uint source) => Playing.Add(source);
        public bool IsSourcePlaying(uint source) => Playing.Contains(source);
        public float SourceSecondsOffset(uint source) => 0f;
    }
}
