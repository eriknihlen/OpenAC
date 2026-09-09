using System.Linq;
using AcDream.Core.Audio;
using Xunit;

namespace AcDream.Core.Tests.Audio;

public sealed class EnvironSoundCueMapTests
{
    [Theory]
    [InlineData(0x65u, SoundId.UI_Roar)]
    [InlineData(0x66u, SoundId.UI_Bell)]
    [InlineData(0x67u, SoundId.UI_Chant1)]
    [InlineData(0x68u, SoundId.UI_Chant2)]
    [InlineData(0x69u, SoundId.UI_DarkWhispers1)]
    [InlineData(0x6Au, SoundId.UI_DarkWhispers2)]
    [InlineData(0x6Bu, SoundId.UI_DarkLaugh)]
    [InlineData(0x6Cu, SoundId.UI_DarkWind)]
    [InlineData(0x6Du, SoundId.UI_DarkSpeech)]
    [InlineData(0x6Eu, SoundId.UI_Drums)]
    [InlineData(0x6Fu, SoundId.UI_GhostSpeak)]
    [InlineData(0x70u, SoundId.UI_Breathing)]
    [InlineData(0x71u, SoundId.UI_Howl)]
    [InlineData(0x72u, SoundId.UI_LostSouls)]
    [InlineData(0x75u, SoundId.UI_Squeal)]
    [InlineData(0x76u, SoundId.UI_Thunder1)]
    [InlineData(0x77u, SoundId.UI_Thunder2)]
    [InlineData(0x78u, SoundId.UI_Thunder3)]
    [InlineData(0x79u, SoundId.UI_Thunder4)]
    [InlineData(0x7Au, SoundId.UI_Thunder5)]
    [InlineData(0x7Bu, SoundId.UI_Thunder6)]
    public void EveryRetailCase_MapsToItsSound(uint code, SoundId expected)
    {
        Assert.True(EnvironSoundCueMap.TryGetSound(code, out SoundId sound));
        Assert.Equal(expected, sound);
    }

    [Theory]
    [InlineData(0x73u)]
    [InlineData(0x74u)]
    public void GapCodes_HaveNoCase(uint code)
    {
        Assert.False(EnvironSoundCueMap.TryGetSound(code, out _));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(6u)]
    [InlineData(0x64u)]
    [InlineData(0x7Cu)]
    [InlineData(0xFFu)]
    public void CodesOutsideTheRun_HaveNoCase(uint code)
    {
        Assert.False(EnvironSoundCueMap.TryGetSound(code, out _));
    }

    [Fact]
    public void TheTableIsNotAConstantOffset()
    {
        Assert.True(EnvironSoundCueMap.TryGetSound(0x72u, out SoundId lostSouls));
        Assert.True(EnvironSoundCueMap.TryGetSound(0x75u, out SoundId squeal));
        Assert.Equal(0x11u, (uint)lostSouls - 0x72u);
        Assert.Equal(0x0Fu, (uint)squeal - 0x75u);
    }

    [Fact]
    public void CoversExactlyTheTwentyOneInterfaceStingers()
    {
        Assert.Equal(21, EnvironSoundCueMap.Codes.Count);

        var sounds = EnvironSoundCueMap.Codes
            .Select(code =>
            {
                EnvironSoundCueMap.TryGetSound(code, out SoundId sound);
                return (uint)sound;
            })
            .OrderBy(value => value)
            .ToList();

        Assert.Equal(Enumerable.Range(0x76, 21).Select(v => (uint)v), sounds);
    }
}
