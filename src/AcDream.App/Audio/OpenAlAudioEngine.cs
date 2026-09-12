using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Audio;
using Silk.NET.OpenAL;

namespace AcDream.App.Audio;

internal interface IWorldAudioQuiescence
{
    void SuspendWorldAudio();
    void ResumeWorldAudio();
}

public sealed unsafe class OpenAlAudioEngine : IAudioEngine, IWorldAudioQuiescence
{
    // ── Backends ─────────────────────────────────────────────────────────────
    private AL? _al;
    private OpenAlResourceLifetime? _resources;
    private bool _available;
    private bool _disposed;

    // ── Voices ───────────────────────────────────────────────────────────────
    private readonly WorldVoicePool _voices = new();
    private bool _worldAudioSuspended;

    private Func<uint, bool>? _isStillPlaying;

    private Vector3 _listenerPosition;
    private float _listenerHeadingDegrees;

    private const float MaxPanAzimuthDegrees = 30f;

    internal const long DefaultBufferByteBudget = 48L * 1024 * 1024; // 48 MiB
    private readonly Dictionary<uint, uint> _bufferByWaveId = new();
    private readonly AlBufferBudgetTracker _bufferBudget = new(DefaultBufferByteBudget);

    // ── Public volume knobs ──────────────────────────────────────────────────
    public float MasterVolume { get; set; } = 1f;

    private bool _muted;

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_available && _al is not null)
                _al.SetListenerProperty(ListenerFloat.Gain, value ? 0f : 1f);
        }
    }
    public float SfxVolume    { get; set; } = 1f;
    public float AmbientVolume{ get; set; } = 0.8f;
    public bool  IsAvailable => _available;

    /// <summary>
    /// The one startup line describing what the output limiter was doing and
    /// what it is doing now, or null when the engine never came up.
    /// </summary>
    internal string? OutputLimiterReport { get; private set; }

    public long ResidentBufferBytes => _bufferBudget.ResidentBytes;

    public int ResidentBufferCount => _bufferBudget.Count;

    public OpenAlAudioEngine()
        : this(new SilkOpenAlResourceApiFactory())
    {
    }

    internal OpenAlAudioEngine(IOpenAlResourceApiFactory apiFactory)
    {
        ArgumentNullException.ThrowIfNull(apiFactory);
        IOpenAlResourceApi api;
        try
        {
            api = apiFactory.Create();
        }
        catch
        {
            return;
        }

        _al = api.AudioApi;
        _resources = new OpenAlResourceLifetime(api);
        try
        {
            if (!_resources.TryOpenDevice())
            {
                return;
            }
            if (!_resources.TryCreateContext())
            {
                DisableAfterInitializationFailure(
                    new InvalidOperationException("OpenAL could not create a context."));
                return;
            }
            if (!_resources.TryMakeCurrent())
            {
                DisableAfterInitializationFailure(
                    new InvalidOperationException("OpenAL could not activate its context."));
                return;
            }

            // One pool for everything: world sounds, ambients and interface
            // sounds all speak through these sixteen sources.
            for (int i = 0; i < _voices.Count; i++)
                _voices[i].SourceId = _resources.Create3DSource();

            api.DisableAlDistanceAttenuation();

            OutputLimiterReport = _resources.DescribeOutputLimiter();
            Console.WriteLine(OutputLimiterReport);

            _available = true;
        }
        catch (OpenAlInitializationException)
        {
            throw;
        }
        catch (Exception failure)
        {
            DisableAfterInitializationFailure(failure);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _available = false;
        _resources?.RetryCleanup();
        _disposed = _resources is null || _resources.IsCleanupComplete;
    }

    internal bool IsDisposalComplete =>
        _disposed || _resources is null || _resources.IsCleanupComplete;

    private void DisableAfterInitializationFailure(Exception failure)
    {
        _available = false;
        if (_resources is null)
            return;

        try
        {
            _resources.RetryCleanup();
        }
        catch (AggregateException cleanupFailure)
        {
            throw new OpenAlInitializationException(
                failure,
                _resources,
                cleanupFailure);
        }

        _al = null;
    }

    // ── IAudioEngine ─────────────────────────────────────────────────────────

    public void SetListener(float posX, float posY, float posZ, float headingDegrees)
    {
        _listenerPosition = new Vector3(posX, posY, posZ);
        _listenerHeadingDegrees = headingDegrees;
    }

    private float EffectMaster => MasterVolume * SfxVolume;

    public bool Play3DWave(
        uint ownerId,
        uint waveId,
        WaveData wave,
        Vector3 position,
        float volume)
    {
        if (_worldAudioSuspended || !_available || _al is null) return false;

        RetailVoiceMix mix = RetailSoundMixer.Mix(
            _listenerPosition,
            _listenerHeadingDegrees,
            position,
            volume,
            EffectMaster);
        if (!mix.Play) return false;

        uint buffer = EnsureBuffer(waveId, wave);
        if (buffer == 0) return false;

        WorldVoicePool.Voice? voice = ClaimWorldVoice(ownerId);
        if (voice is null) return false;    // every voice busy — drop the sound

        Speak(voice, buffer, RetailSoundMixer.LinearGain(mix.Decibels), mix.Pan);
        return true;
    }

    private WorldVoicePool.Voice? ClaimWorldVoice(uint ownerId) =>
        _voices.Claim(_isStillPlaying ??= IsStillPlaying, ownerId, isInterface: false);

    private WorldVoicePool.Voice? ClaimInterfaceVoice() =>
        _voices.Claim(_isStillPlaying ??= IsStillPlaying, ownerId: 0, isInterface: true);

    /// <summary>
    /// Hand a claimed voice its sound and start it. This is the one place a
    /// voice is bound to a buffer, so it is the one place its level, its pan
    /// and its recorded priority are set.
    /// </summary>
    private void Speak(WorldVoicePool.Voice voice, uint buffer, float gain, int pan)
    {
        _al!.SourceStop(voice.SourceId);
        _al.SetSourceProperty(voice.SourceId, SourceInteger.Buffer, 0);  // detach old
        _al.SetSourceProperty(voice.SourceId, SourceInteger.Buffer, (int)buffer);
        _al.SetSourceProperty(voice.SourceId, SourceFloat.Gain, gain);
        ApplyPan(voice.SourceId, pan);
        _al.SetSourceProperty(voice.SourceId, SourceBoolean.Looping, false);
        _al.SourcePlay(voice.SourceId);
    }

    private void ApplyPan(uint sourceId, int pan)
    {
        float position = RetailSoundMixer.StereoPositionFromPan(pan);
        float azimuth = position * MaxPanAzimuthDegrees * (MathF.PI / 180f);
        _al!.SetSourceProperty(sourceId, SourceBoolean.SourceRelative, true);
        _al.SetSourceProperty(
            sourceId,
            SourceVector3.Position,
            MathF.Sin(azimuth),
            0f,
            -MathF.Cos(azimuth));
    }

    public void SuspendWorldAudio()
    {
        _worldAudioSuspended = true;
        // Only the world goes quiet. An interface cue is not part of the world
        // being taken down — the portal-enter sound plays at exactly this
        // moment and has to be allowed to finish.
        foreach (WorldVoicePool.Voice voice in _voices.SilencedByWorldChange())
            Silence(voice);
    }

    public void ResumeWorldAudio() => _worldAudioSuspended = false;

    internal void StopAllForOwner(uint ownerId)
    {
        if (ownerId == 0)
            return;

        for (int i = 0; i < _voices.Count; i++)
        {
            WorldVoicePool.Voice voice = _voices[i];
            if (voice.InUse && voice.OwnerId == ownerId)
                Silence(voice);
        }
    }

    /// <summary>
    /// Play a raw WaveData blob as an interface sound: centred, with no
    /// distance falloff, but sharing the same sixteen voices as everything
    /// else. When they are all busy the interface sound is dropped too.
    /// </summary>
    public bool PlayUiWave(uint waveId, WaveData wave, float volume = 1f)
    {
        if (!_available || _al is null) return false;

        if (!RetailSoundMixer.TryGetAttenuation(0f, volume, EffectMaster, out int decibels))
            return false;

        uint buffer = EnsureBuffer(waveId, wave);
        if (buffer == 0) return false;

        WorldVoicePool.Voice? voice = ClaimInterfaceVoice();
        if (voice is null) return false;

        Speak(voice, buffer, RetailSoundMixer.LinearGain(decibels), pan: 0);
        return true;
    }


    public void PlayUi(SoundId id) { /* handled via AudioHookSink */ }

    public void Play3D(SoundId id, float x, float y, float z) { /* handled via AudioHookSink */ }

    public bool PlayAmbient3DWave(
        uint waveId,
        WaveData wave,
        Vector3 position,
        float volume)
    {
        if (_worldAudioSuspended || !_available || _al is null) return false;

        RetailVoiceMix mix = RetailSoundMixer.Mix(
            _listenerPosition,
            _listenerHeadingDegrees,
            position,
            volume,
            AmbientMaster);
        if (!mix.Play) return false;

        uint buffer = EnsureBuffer(waveId, wave);
        if (buffer == 0) return false;

        WorldVoicePool.Voice? voice = ClaimWorldVoice(ownerId: 0);
        if (voice is null) return false;

        Speak(voice, buffer, RetailSoundMixer.LinearGain(mix.Decibels), mix.Pan);
        return true;
    }

    public bool PlayAmbientFromCenter(
        uint waveId,
        WaveData wave,
        float volume)
    {
        if (_worldAudioSuspended || !_available || _al is null) return false;

        if (!RetailSoundMixer.TryGetAttenuation(0f, volume, AmbientMaster, out int decibels))
            return false;

        uint buffer = EnsureBuffer(waveId, wave);
        if (buffer == 0) return false;

        WorldVoicePool.Voice? voice = ClaimWorldVoice(ownerId: 0);
        if (voice is null) return false;

        Speak(voice, buffer, RetailSoundMixer.LinearGain(decibels), pan: 0);
        return true;
    }

    private float AmbientMaster => MasterVolume * AmbientVolume;


    // ── Private helpers ──────────────────────────────────────────────────────

    private uint EnsureBuffer(uint waveId, WaveData wave)
    {
        if (!_available || _al is null) return 0;
        if (_bufferByWaveId.TryGetValue(waveId, out var existing))
        {
            // Buffer id 0 is the "unsupported format" negative marker — no
            // payload, not tracked by the budget, nothing to touch.
            if (existing != 0)
                _bufferBudget.Touch(waveId);
            return existing;
        }

        uint buf = _al.GenBuffer();
        _resources!.OwnBuffer(buf);
        BufferFormat fmt = PickFormat(wave);
        if (fmt == 0)
        {
            _resources.ReleaseBuffer(buf);
            _bufferByWaveId[waveId] = 0;
            return 0;
        }

        fixed (byte* p = wave.PcmBytes)
            _al.BufferData(buf, fmt, p, wave.PcmBytes.Length, wave.SampleRate);

        _bufferByWaveId[waveId] = buf;
        _bufferBudget.RecordCreated(waveId, buf, wave.PcmBytes.Length);
        EvictBuffersOverBudget(protectedBufferId: buf);
        return buf;
    }

    private void EvictBuffersOverBudget(uint protectedBufferId)
    {
        while (_bufferBudget.ResidentBytes > _bufferBudget.MaxBytes)
        {
            bool IsProtected(uint bufferId) =>
                bufferId == protectedBufferId || IsBufferAttachedToAnySource(bufferId);

            if (!_bufferBudget.TryEvictOldestUnprotected(
                    IsProtected, out uint evictedWaveId, out uint evictedBufferId))
            {
                break;
            }

            _bufferByWaveId.Remove(evictedWaveId);
            _resources!.ReleaseBuffer(evictedBufferId);
        }
    }

    private bool IsBufferAttachedToAnySource(uint bufferId)
    {
        if (_al is null) return false;

        for (int i = 0; i < _voices.Count; i++)
        {
            if (IsSourceBoundTo(_voices[i].SourceId, bufferId)) return true;
        }
        return false;
    }

    private bool IsSourceBoundTo(uint sourceId, uint bufferId)
    {
        _al!.GetSourceProperty(sourceId, GetSourceInteger.Buffer, out int attached);
        return (uint)attached == bufferId;
    }

    private static BufferFormat PickFormat(WaveData w)
    {
        return (w.ChannelCount, w.BitsPerSample) switch
        {
            (1, 8)  => BufferFormat.Mono8,
            (1, 16) => BufferFormat.Mono16,
            (2, 8)  => BufferFormat.Stereo8,
            (2, 16) => BufferFormat.Stereo16,
            _       => 0,
        };
    }

    private bool IsStillPlaying(uint sourceId)
    {
        if (_al is null) return false;
        _al.GetSourceProperty(sourceId, GetSourceInteger.SourceState, out int state);
        return state == (int)SourceState.Playing;
    }

    private void Silence(WorldVoicePool.Voice voice)
    {
        if (_available && _al is not null && voice.SourceId != 0)
        {
            _al.SourceStop(voice.SourceId);
            _al.SetSourceProperty(voice.SourceId, SourceInteger.Buffer, 0);
        }

        WorldVoicePool.Vacate(voice);
    }
}
