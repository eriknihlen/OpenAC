using System.Collections.Frozen;
using System.Collections.Generic;

namespace AcDream.Core.Audio;

public static class EnvironSoundCueMap
{
    private static readonly FrozenDictionary<uint, SoundId> Map =
        new Dictionary<uint, SoundId>
        {
            [0x65u] = SoundId.UI_Roar,
            [0x66u] = SoundId.UI_Bell,
            [0x67u] = SoundId.UI_Chant1,
            [0x68u] = SoundId.UI_Chant2,
            [0x69u] = SoundId.UI_DarkWhispers1,
            [0x6Au] = SoundId.UI_DarkWhispers2,
            [0x6Bu] = SoundId.UI_DarkLaugh,
            [0x6Cu] = SoundId.UI_DarkWind,
            [0x6Du] = SoundId.UI_DarkSpeech,
            [0x6Eu] = SoundId.UI_Drums,
            [0x6Fu] = SoundId.UI_GhostSpeak,
            [0x70u] = SoundId.UI_Breathing,
            [0x71u] = SoundId.UI_Howl,
            [0x72u] = SoundId.UI_LostSouls,
            [0x75u] = SoundId.UI_Squeal,
            [0x76u] = SoundId.UI_Thunder1,
            [0x77u] = SoundId.UI_Thunder2,
            [0x78u] = SoundId.UI_Thunder3,
            [0x79u] = SoundId.UI_Thunder4,
            [0x7Au] = SoundId.UI_Thunder5,
            [0x7Bu] = SoundId.UI_Thunder6,
        }.ToFrozenDictionary();

    public static bool TryGetSound(uint changeType, out SoundId sound) =>
        Map.TryGetValue(changeType, out sound);

    public static IReadOnlyCollection<uint> Codes => Map.Keys;
}
