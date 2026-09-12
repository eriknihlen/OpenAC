using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Audio;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DRWSound = DatReaderWriter.Enums.Sound;

namespace AcDream.App.Audio;

public sealed class AudioHookSink : IAnimationHookSink
{
    private readonly OpenAlAudioEngine _engine;
    private readonly DatSoundCache _cache;
    private readonly IEntitySoundTable _entitySoundTables;
    private readonly ISoundRandom _rng;

    public AudioHookSink(
        OpenAlAudioEngine engine,
        DatSoundCache cache,
        IEntitySoundTable entitySoundTables,
        ISoundRandom? rng = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _entitySoundTables = entitySoundTables ?? throw new ArgumentNullException(nameof(entitySoundTables));
        _rng = rng ?? new SoundRandom();
    }

    public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook)
    {
        if (!_engine.IsAvailable) return;

        switch (hook)
        {
            case SoundHook s:
                Play(entityId, entityWorldPosition, (uint)s.Id, volume: 1f);
                break;

            case SoundTableHook st:
                PlayFromSoundTable(entityId, entityWorldPosition, st.SoundType);
                break;

            case SoundTweakedHook stw:
                // A tweaked hook carries its own odds of making a sound at all.
                // Thunder is authored this way: the hook comes round on its
                // cadence and usually loses the roll.
                if (TweakedSoundHooks.TryRoll(stw, _rng, out uint tweakedWave, out float tweakedVolume))
                    Play(entityId, entityWorldPosition, tweakedWave, tweakedVolume);
                break;

            // All the visual-only hooks (Scale, Luminous, Diffuse, …)
            // are for other sinks to handle.
        }
    }

    public void PlayServerSound(
        uint entityId,
        Vector3 worldPosition,
        uint soundType,
        float wireVolume)
    {
        if (!_engine.IsAvailable) return;

        uint tableId = _entitySoundTables.GetSoundTableId(entityId);
        if (tableId == 0)
        {
            WireProbe(entityId, soundType, "no-sound-table");
            return;
        }

        SoundTable? table = _cache.GetSoundTable(tableId);
        if (table is null)
        {
            WireProbe(entityId, soundType, $"table-0x{tableId:X8}-unloadable");
            return;
        }

        var entry = SoundCookbook.Select(table, (DRWSound)soundType, _rng);
        if (entry is null)
        {
            WireProbe(entityId, soundType, "slot-missing-or-gate-silence");
            return;
        }

        WireProbe(entityId, soundType,
            $"play wave=0x{(uint)entry.Id:X8} pos=({worldPosition.X:F0},{worldPosition.Y:F0},{worldPosition.Z:F0})");
        Play(
            entityId, worldPosition,
            waveId: (uint)entry.Id,
            volume: wireVolume);
    }

    private static void WireProbe(uint entityId, uint soundType, string outcome)
    {
        if (!AudioDiagnostics.ProbeWireSoundsEnabled) return;
        Console.WriteLine(FormattableString.Invariant(
            $"[sound-wire] local=0x{entityId:X8} slot=0x{soundType:X2} {outcome}"));
    }

    public void OnUiHook(uint entityId, AnimationHook hook)
    {
        if (!_engine.IsAvailable) return;

        switch (hook)
        {
            case SoundHook s:
                PlayUi((uint)s.Id, volume: 1f);
                break;

            case SoundTableHook st:
                uint tableId = _entitySoundTables.GetSoundTableId(entityId);
                if (tableId == 0) return;
                SoundTable? table = _cache.GetSoundTable(tableId);
                if (table is null) return;
                var entry = SoundCookbook.Select(table, st.SoundType, _rng);
                if (entry is null) return;
                PlayUi((uint)entry.Id, entry.Volume);
                break;

            case SoundTweakedHook stw:
                if (TweakedSoundHooks.TryRoll(stw, _rng, out uint tweakedWave, out float tweakedVolume))
                    PlayUi(tweakedWave, tweakedVolume);
                break;
        }
    }

    private void PlayUi(uint waveId, float volume)
    {
        if (waveId == 0) return;
        WaveData? wave = _cache.GetWave(waveId);
        if (wave is null) return;
        _engine.PlayUiWave(waveId, wave, volume);
    }

    private void PlayFromSoundTable(
        uint entityId, Vector3 worldPos, DRWSound sound,
        float volumeMult = 1f)
    {
        uint tableId = _entitySoundTables.GetSoundTableId(entityId);
        if (tableId == 0) return;

        SoundTable? table = _cache.GetSoundTable(tableId);
        if (table is null) return;

        var entry = SoundCookbook.Select(table, sound, _rng);
        if (entry is null) return;

        Play(
            entityId, worldPos,
            waveId: (uint)entry.Id,
            volume: entry.Volume * volumeMult);
    }

    private void Play(uint entityId, Vector3 worldPos, uint waveId, float volume)
    {
        if (waveId == 0) return;
        WaveData? wave = _cache.GetWave(waveId);
        if (wave is null) return;
        _engine.Play3DWave(
            entityId,
            waveId,
            wave,
            worldPos,
            volume);
    }
}

public interface IEntitySoundTable
{
    /// <summary>
    /// Return the SoundTable dat id (0x20xxxxxx) for <paramref name="entityId"/>,
    /// or 0 if no table is known (e.g. a static prop without audio).
    /// </summary>
    uint GetSoundTableId(uint entityId);
}

public sealed class DictionaryEntitySoundTable : IEntitySoundTable
{
    private readonly Dictionary<uint, uint> _table = new();

    public void Set(uint entityId, uint soundTableId) => _table[entityId] = soundTableId;
    public void Remove(uint entityId) => _table.Remove(entityId);

    public uint GetSoundTableId(uint entityId) =>
        _table.TryGetValue(entityId, out var id) ? id : 0;
}
