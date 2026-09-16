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
    private IOpenAlResourceApi? _api;
    private OpenAlResourceLifetime? _resources;
    private bool _available;
    private bool _disposed;

    // ── Voices ───────────────────────────────────────────────────────────────
    private readonly WorldVoicePool _voices;
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
            if (_available && _api is not null)
                _api.SetListenerGain(value ? 0f : 1f);
        }
    }

    private bool _focusMuted;

    /// <summary>
    /// "No Sound When Window Not Focused": refuses to start a new sound while
    /// true, the same point retail's own gate sits (SoundManager::
    /// PlaySoundInternal), ahead of a source being told to play rather than at
    /// the listener. Unlike retail, going true also cuts every sound already
    /// playing, so nothing keeps sounding once the window is backgrounded.
    /// That part is this fork's own choice, not the original client's. Going back to false starts nothing on its own;
    /// playback only resumes as new sounds are requested.
    /// </summary>
    public bool FocusMuted
    {
        get => _focusMuted;
        set
        {
            if (value && !_focusMuted)
                CutEveryPlayingVoice();
            _focusMuted = value;
        }
    }

    /// <summary>
    /// Every voice in flight, interface cues included. Backgrounding the
    /// window means silence, so unlike a world change there is nothing worth
    /// letting finish.
    /// </summary>
    private void CutEveryPlayingVoice()
    {
        for (int i = 0; i < _voices.Count; i++)
        {
            WorldVoicePool.Voice voice = _voices[i];
            if (voice.InUse)
                Silence(voice);
        }
    }

    /// <summary>"Disable Interface Sound", gating only the genuine interface path.</summary>
    public bool InterfaceEnabled { get; set; } = true;

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
        : this(new SilkOpenAlResourceApiFactory(), AudioMixerOptions.Default)
    {
    }

    public OpenAlAudioEngine(AudioMixerOptions mixer)
        : this(new SilkOpenAlResourceApiFactory(), mixer)
    {
    }

    internal OpenAlAudioEngine(
        IOpenAlResourceApiFactory apiFactory,
        AudioMixerOptions? mixer = null)
    {
        ArgumentNullException.ThrowIfNull(apiFactory);
        _voices = new WorldVoicePool(mixer ?? AudioMixerOptions.Default);
        IOpenAlResourceApi api;
        try
        {
            api = apiFactory.Create();
        }
        catch
        {
            return;
        }

        _api = api;
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
            // sounds all speak through these sources.
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

        _api = null;
    }

    /// <summary>The mixer settings in force.</summary>
    internal AudioMixerOptions MixerOptions => _voices.Options;

    /// <summary>
    /// Put new mixer settings in force without restarting: the pool grows or
    /// shrinks, and the sources it needs are made or released to match. A
    /// voice the pool no longer has room for stops whatever it was playing.
    /// </summary>
    /// <returns>
    /// False when there is no mixer running to change — an engine that never
    /// came up, or one already torn down. Its caller has to say so rather than
    /// report a change that did not happen; the settings themselves are the
    /// caller's to keep, and a later start reads them.
    /// </returns>
    internal bool ApplyMixerOptions(AudioMixerOptions mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        if (!_available || _resources is null)
            return false;

        _voices.ApplyOptions(mixer, RetireVoice, _resources.Create3DSource);
        return true;
    }

    private void RetireVoice(WorldVoicePool.Voice voice)
    {
        Silence(voice);
        if (voice.SourceId != 0)
            _resources!.ReleaseSource(voice.SourceId);
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
        float volume,
        float priority)
    {
        if (_worldAudioSuspended || FocusMuted || !_available || _api is null) return false;

        RetailVoiceMix mix = RetailSoundMixer.Mix(
            _listenerPosition,
            _listenerHeadingDegrees,
            position,
            volume,
            EffectMaster);
        if (!mix.Play) return false;

        uint buffer = EnsureBuffer(waveId, wave);
        if (buffer == 0) return false;

        WorldVoicePool.Voice? voice = ClaimWorldVoice(
            ownerId, waveId, priority, out bool tookPlayingVoice);
        if (voice is null)                  // every voice busy — drop the sound
        {
            ProbeVoices("dropped world", waveId, ownerId);
            return false;
        }

        if (tookPlayingVoice)
            ProbeVoices("stole for world", waveId, ownerId);

        Speak(voice, buffer, RetailSoundMixer.LinearGain(mix.Decibels), mix.Pan);
        return true;
    }

    private WorldVoicePool.Voice? ClaimWorldVoice(
        uint ownerId,
        uint waveId,
        float priority,
        out bool tookPlayingVoice) =>
        _voices.Claim(
            _isStillPlaying ??= IsStillPlaying,
            ownerId,
            isInterface: false,
            authoredPriority: priority,
            waveId: waveId,
            nowMs: Environment.TickCount64,
            out tookPlayingVoice);

    private WorldVoicePool.Voice? ClaimInterfaceVoice(
        uint waveId,
        float priority,
        out bool tookPlayingVoice) =>
        _voices.Claim(
            _isStillPlaying ??= IsStillPlaying,
            ownerId: 0,
            isInterface: true,
            authoredPriority: priority,
            waveId: waveId,
            nowMs: Environment.TickCount64,
            out tookPlayingVoice);

    /// <summary>
    /// Hand a claimed voice its sound and start it. This is the one place a
    /// voice is bound to a buffer, so it is the one place its level and its pan
    /// are set. It is also the teardown of whatever the voice held before,
    /// which is what a voice taken from a playing sound needs.
    /// </summary>
    private void Speak(WorldVoicePool.Voice voice, uint buffer, float gain, int pan)
    {
        _api!.StopSource(voice.SourceId);
        _api.AttachBuffer(voice.SourceId, 0);  // detach old
        _api.AttachBuffer(voice.SourceId, buffer);
        _api.SetSourceGain(voice.SourceId, gain);
        ApplyPan(voice.SourceId, pan);
        _api.SetSourceLooping(voice.SourceId, false);
        _api.PlaySource(voice.SourceId);
    }

    private long _lastProbeDumpMs;

    // Temporary probe (ACDREAM_PROBE_AUDIO_VOICES=1): what every voice holds
    // at the moment a sound is dropped or takes a voice from a playing one, at
    // most twice a second.
    private void ProbeVoices(string what, uint waveId, uint ownerId)
    {
        if (!AudioDiagnostics.ProbeVoicesEnabled || _api is null) return;
        long now = Environment.TickCount64;
        if (now - _lastProbeDumpMs < 500) return;
        _lastProbeDumpMs = now;

        var sb = new System.Text.StringBuilder();
        sb.Append(FormattableString.Invariant(
            $"[probe-voices] {what} wave=0x{waveId:X8} owner=0x{ownerId:X8}; voices:"));
        for (int i = 0; i < _voices.Count; i++)
        {
            WorldVoicePool.Voice v = _voices[i];
            string state = _api.IsSourcePlaying(v.SourceId) ? "Playing" : "Stopped";
            float offset = _api.SourceSecondsOffset(v.SourceId);
            sb.Append(FormattableString.Invariant(
                $" [{i}] wave=0x{v.WaveId:X8} owner=0x{v.OwnerId:X8}{(v.IsInterface ? " ui" : "")} prio={v.Priority:0.00} state={state} at={offset:0.00}s age={now - v.SpokeAtMs}ms"));
        }
        Console.WriteLine(sb.ToString());
    }

    private void ApplyPan(uint sourceId, int pan)
    {
        float position = RetailSoundMixer.StereoPositionFromPan(pan);
        float azimuth = position * MaxPanAzimuthDegrees * (MathF.PI / 180f);
        _api!.PlaceSourceRelative(
            sourceId,
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
    /// distance falloff, but sharing the same voices as everything else. When
    /// they are all busy the interface sound is dropped too. Retail attenuates
    /// this path with the same effect_sound_volume as everything else — there
    /// is no separate interface volume in force, only a separate on/off.
    /// </summary>
    /// <param name="isInterfaceSound">
    /// False for a non-positional sound that merely shares this path (the
    /// portal tunnel's own animation cues): "Disable Interface Sound" leaves
    /// those alone.
    /// </param>
    public bool PlayUiWave(
        uint waveId, WaveData wave, float volume, float priority, bool isInterfaceSound = true)
    {
        if (FocusMuted || !_available || _api is null) return false;
        if (isInterfaceSound && !InterfaceEnabled) return false;

        if (!RetailSoundMixer.TryGetAttenuation(0f, volume, EffectMaster, out int decibels))
            return false;

        uint buffer = EnsureBuffer(waveId, wave);
        if (buffer == 0) return false;

        WorldVoicePool.Voice? voice = ClaimInterfaceVoice(
            waveId, priority, out bool tookPlayingVoice);
        if (voice is null)
        {
            ProbeVoices("dropped ui", waveId, 0);
            return false;
        }

        if (tookPlayingVoice)
            ProbeVoices("stole for ui", waveId, 0);

        Speak(voice, buffer, RetailSoundMixer.LinearGain(decibels), pan: 0);
        return true;
    }


    public void PlayUi(SoundId id) { /* handled via AudioHookSink */ }

    public void Play3D(SoundId id, float x, float y, float z) { /* handled via AudioHookSink */ }

    public bool PlayAmbient3DWave(
        uint waveId,
        WaveData wave,
        Vector3 position,
        float volume,
        float priority)
    {
        if (_worldAudioSuspended || FocusMuted || !_available || _api is null) return false;

        RetailVoiceMix mix = RetailSoundMixer.Mix(
            _listenerPosition,
            _listenerHeadingDegrees,
            position,
            volume,
            AmbientMaster);
        if (!mix.Play) return false;

        uint buffer = EnsureBuffer(waveId, wave);
        if (buffer == 0) return false;

        WorldVoicePool.Voice? voice = ClaimWorldVoice(
            ownerId: 0, waveId, priority, out bool tookPlayingVoice);
        if (voice is null)
        {
            ProbeVoices("dropped ambient", waveId, 0);
            return false;
        }

        if (tookPlayingVoice)
            ProbeVoices("stole for ambient", waveId, 0);

        Speak(voice, buffer, RetailSoundMixer.LinearGain(mix.Decibels), mix.Pan);
        return true;
    }

    public bool PlayAmbientFromCenter(
        uint waveId,
        WaveData wave,
        float volume,
        float priority)
    {
        if (_worldAudioSuspended || FocusMuted || !_available || _api is null) return false;

        if (!RetailSoundMixer.TryGetAttenuation(0f, volume, AmbientMaster, out int decibels))
            return false;

        uint buffer = EnsureBuffer(waveId, wave);
        if (buffer == 0) return false;

        WorldVoicePool.Voice? voice = ClaimWorldVoice(
            ownerId: 0, waveId, priority, out bool tookPlayingVoice);
        if (voice is null)
        {
            ProbeVoices("dropped ambient-center", waveId, 0);
            return false;
        }

        if (tookPlayingVoice)
            ProbeVoices("stole for ambient-center", waveId, 0);

        Speak(voice, buffer, RetailSoundMixer.LinearGain(decibels), pan: 0);
        return true;
    }

    private float AmbientMaster => MasterVolume * AmbientVolume;


    // ── Private helpers ──────────────────────────────────────────────────────

    private uint EnsureBuffer(uint waveId, WaveData wave)
    {
        if (!_available || _api is null) return 0;
        if (_bufferByWaveId.TryGetValue(waveId, out var existing))
        {
            // Buffer id 0 is the "unsupported format" negative marker — no
            // payload, not tracked by the budget, nothing to touch.
            if (existing != 0)
                _bufferBudget.Touch(waveId);
            return existing;
        }

        uint buf = _api.GenerateBuffer();
        _resources!.OwnBuffer(buf);
        BufferFormat fmt = PickFormat(wave);
        if (fmt == 0)
        {
            _resources.ReleaseBuffer(buf);
            _bufferByWaveId[waveId] = 0;
            return 0;
        }

        _api.FillBuffer(buf, fmt, wave.PcmBytes, wave.SampleRate);

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
        if (_api is null) return false;

        for (int i = 0; i < _voices.Count; i++)
        {
            if (IsSourceBoundTo(_voices[i].SourceId, bufferId)) return true;
        }
        return false;
    }

    private bool IsSourceBoundTo(uint sourceId, uint bufferId)
    {
        return _api!.AttachedBuffer(sourceId) == bufferId;
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
        if (_api is null) return false;
        return _api.IsSourcePlaying(sourceId);
    }

    private void Silence(WorldVoicePool.Voice voice)
    {
        if (_available && _api is not null && voice.SourceId != 0)
        {
            _api.StopSource(voice.SourceId);
            _api.AttachBuffer(voice.SourceId, 0);
        }

        WorldVoicePool.Vacate(voice);
    }
}
