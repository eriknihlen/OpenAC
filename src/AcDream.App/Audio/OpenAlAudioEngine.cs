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

    // ── Pools ────────────────────────────────────────────────────────────────
    private const int PoolSize3D = 16;
    private const int PoolSizeUi = 4;

    private sealed class Slot3D
    {
        public uint SourceId;
        public uint OwnerId;
        public bool InUse;
        public float Priority;
    }
    private readonly Slot3D[] _pool3D = CreateWorldSlots();
    private int _pool3DCursor; // round-robin start
    private bool _worldAudioSuspended;

    private readonly uint[] _poolUi = new uint[PoolSizeUi];

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

            // Initialise 3D source pool.
            for (int i = 0; i < PoolSize3D; i++)
            {
                uint src = _resources.Create3DSource();
                _pool3D[i].SourceId = src;
            }

            // UI sources are source-relative (attached to listener) so they
            // ignore 3D position.
            for (int i = 0; i < PoolSizeUi; i++)
            {
                uint src = _resources.CreateUiSource();
                _poolUi[i] = src;
            }

            api.DisableAlDistanceAttenuation();

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
        float volume,
        float priority)
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

        int slotIdx = AcquireWorldSlot(priority);
        if (slotIdx < 0) return false;    // nothing lower-priority — drop

        float gain = RetailSoundMixer.LinearGain(mix.Decibels);
        var slot = _pool3D[slotIdx];
        _al.SourceStop(slot.SourceId);
        _al.SetSourceProperty(slot.SourceId, SourceInteger.Buffer, 0);  // detach old
        _al.SetSourceProperty(slot.SourceId, SourceInteger.Buffer, (int)buffer);
        _al.SetSourceProperty(slot.SourceId, SourceFloat.Gain, gain);
        ApplyPan(slot.SourceId, mix.Pan);
        _al.SetSourceProperty(slot.SourceId, SourceBoolean.Looping, false);
        _al.SourcePlay(slot.SourceId);

        slot.InUse = true;
        slot.OwnerId = ownerId;
        slot.Priority = priority;
        _pool3DCursor = RetailVoicePool.AdvanceCursor(slotIdx, PoolSize3D);
        return true;
    }

    private int AcquireWorldSlot(float priority)
    {
        Span<VoiceSlotState> slots = stackalloc VoiceSlotState[PoolSize3D];
        for (int i = 0; i < PoolSize3D; i++)
        {
            Slot3D s = _pool3D[i];
            slots[i] = new VoiceSlotState(
                Occupied: s.InUse,
                StillPlaying: s.InUse && IsStillPlaying(s.SourceId),
                Priority: s.Priority);
        }

        return RetailVoicePool.Acquire(slots, _pool3DCursor, priority);
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
        for (int i = 0; i < _pool3D.Length; i++)
            StopWorldSlot(_pool3D[i]);
    }

    public void ResumeWorldAudio() => _worldAudioSuspended = false;

    internal void StopAllForOwner(uint ownerId)
    {
        if (ownerId == 0)
            return;

        for (int i = 0; i < _pool3D.Length; i++)
        {
            Slot3D slot = _pool3D[i];
            if (slot.InUse && slot.OwnerId == ownerId)
                StopWorldSlot(slot);
        }
    }

    /// <summary>
    /// Play a raw WaveData blob as a 2D UI sound (no falloff, ignores
    /// listener position).
    /// </summary>
    public bool PlayUiWave(uint waveId, WaveData wave, float volume = 1f)
    {
        if (!_available || _al is null) return false;

        uint buffer = EnsureBuffer(waveId, wave);
        if (buffer == 0) return false;

        // UI pool: find a free source (first not-playing), else round-robin.
        int slotIdx = -1;
        for (int i = 0; i < PoolSizeUi; i++)
        {
            if (!IsStillPlaying(_poolUi[i])) { slotIdx = i; break; }
        }
        if (slotIdx < 0) slotIdx = 0; // always replace slot 0 as a last resort

        if (!RetailSoundMixer.TryGetAttenuation(0f, volume, EffectMaster, out int decibels))
            return false;

        uint src = _poolUi[slotIdx];
        _al.SourceStop(src);
        _al.SetSourceProperty(src, SourceInteger.Buffer, 0);
        _al.SetSourceProperty(src, SourceInteger.Buffer, (int)buffer);
        _al.SetSourceProperty(src, SourceFloat.Gain, RetailSoundMixer.LinearGain(decibels));
        _al.SourcePlay(src);
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

        int slotIdx = AcquireWorldSlot(priority);
        if (slotIdx < 0) return false;

        Slot3D slot = _pool3D[slotIdx];
        _al.SourceStop(slot.SourceId);
        _al.SetSourceProperty(slot.SourceId, SourceInteger.Buffer, 0);
        _al.SetSourceProperty(slot.SourceId, SourceInteger.Buffer, (int)buffer);
        _al.SetSourceProperty(
            slot.SourceId,
            SourceFloat.Gain,
            RetailSoundMixer.LinearGain(mix.Decibels));
        ApplyPan(slot.SourceId, mix.Pan);
        _al.SetSourceProperty(slot.SourceId, SourceBoolean.Looping, false);
        _al.SourcePlay(slot.SourceId);

        slot.InUse = true;
        slot.OwnerId = 0;
        slot.Priority = priority;
        _pool3DCursor = RetailVoicePool.AdvanceCursor(slotIdx, PoolSize3D);
        return true;
    }

    public bool PlayAmbientFromCenter(
        uint waveId,
        WaveData wave,
        float volume,
        float priority)
    {
        if (_worldAudioSuspended || !_available || _al is null) return false;

        if (!RetailSoundMixer.TryGetAttenuation(0f, volume, AmbientMaster, out int decibels))
            return false;

        uint buffer = EnsureBuffer(waveId, wave);
        if (buffer == 0) return false;

        int slotIdx = AcquireWorldSlot(priority);
        if (slotIdx < 0) return false;

        Slot3D slot = _pool3D[slotIdx];
        _al.SourceStop(slot.SourceId);
        _al.SetSourceProperty(slot.SourceId, SourceInteger.Buffer, 0);
        _al.SetSourceProperty(slot.SourceId, SourceInteger.Buffer, (int)buffer);
        _al.SetSourceProperty(
            slot.SourceId,
            SourceFloat.Gain,
            RetailSoundMixer.LinearGain(decibels));
        ApplyPan(slot.SourceId, 0);
        _al.SetSourceProperty(slot.SourceId, SourceBoolean.Looping, false);
        _al.SourcePlay(slot.SourceId);

        slot.InUse = true;
        slot.OwnerId = 0;
        slot.Priority = priority;
        _pool3DCursor = RetailVoicePool.AdvanceCursor(slotIdx, PoolSize3D);
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

        for (int i = 0; i < PoolSize3D; i++)
        {
            if (IsSourceBoundTo(_pool3D[i].SourceId, bufferId)) return true;
        }
        for (int i = 0; i < PoolSizeUi; i++)
        {
            if (IsSourceBoundTo(_poolUi[i], bufferId)) return true;
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

    private void StopWorldSlot(Slot3D slot)
    {
        if (_available && _al is not null && slot.SourceId != 0)
        {
            _al.SourceStop(slot.SourceId);
            _al.SetSourceProperty(slot.SourceId, SourceInteger.Buffer, 0);
        }

        slot.OwnerId = 0;
        slot.Priority = 0f;
        slot.InUse = false;
    }

    private static Slot3D[] CreateWorldSlots()
    {
        var slots = new Slot3D[PoolSize3D];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = new Slot3D();
        return slots;
    }
}
