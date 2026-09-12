using System;
using AcDream.Core.Audio;
using DatReaderWriter.DBObjs;
using DRWSound = DatReaderWriter.Enums.Sound;

namespace AcDream.App.Audio;

public sealed class UiSoundController
{
    private readonly OpenAlAudioEngine _engine;
    private readonly DatSoundCache _cache;
    private readonly ISoundRandom _rng;
    private readonly uint _tableDid;
    private SoundTable? _table;
    private bool _tableMissing;

    public UiSoundController(
        OpenAlAudioEngine engine,
        DatSoundCache cache,
        uint tableDid,
        ISoundRandom? rng = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _tableDid = tableDid;
        _rng = rng ?? new SoundRandom();
    }

    public uint TableDid => _tableDid;

    /// <summary>
    /// Play one interface slot. Returns false when the bank is absent, the slot
    /// is unauthored, or the entry's probability gate says silence.
    /// </summary>
    public bool Play(SoundId sound)
    {
        if (!_engine.IsAvailable || _tableDid == 0 || _tableMissing)
            return false;

        if (_table is null)
        {
            _table = _cache.GetSoundTable(_tableDid);
            if (_table is null)
            {
                _tableMissing = true;
                return false;
            }
        }

        var entry = SoundCookbook.Select(_table, (DRWSound)sound, _rng);
        if (entry is null)
            return false;

        uint waveId = (uint)entry.Id;
        if (waveId == 0)
            return false;

        WaveData? wave = _cache.GetWave(waveId);
        if (wave is null)
            return false;

        // PlaySoundFromCenter takes no volume argument, so the authored entry
        // volume is the one that reaches the mixer. Interface cues are authored
        // high-priority, which is what keeps them audible in a crowd.
        return _engine.PlayUiWave(waveId, wave, entry.Volume, entry.Priority);
    }

    public bool PlayEnvironCue(uint changeType) =>
        EnvironSoundCueMap.TryGetSound(changeType, out SoundId sound) && Play(sound);
}
