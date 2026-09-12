using System.Linq;
using AcDream.App.Audio;
using AcDream.Core.Audio;

namespace AcDream.App.Tests.Audio;

// OpenAC #42: many sounds at once collapsed to two or three, because a new
// sound could take a voice away from one that was still playing. Nothing may
// do that any more: once the sixteen voices are busy the next sound is
// dropped, and it does not matter where the sound came from.
public sealed class WorldVoicePoolTests
{
    private static WorldVoicePool FilledPool(out WorldVoicePool.Voice[] claimed)
    {
        var pool = new WorldVoicePool();
        for (int i = 0; i < pool.Count; i++)
            pool[i].SourceId = (uint)(i + 1);

        claimed = new WorldVoicePool.Voice[pool.Count];
        for (int i = 0; i < pool.Count; i++)
        {
            // A realistic mix: interface clicks land among world sounds.
            WorldVoicePool.Voice? voice = pool.Claim(
                Playing,
                ownerId: (uint)(i + 1),
                isInterface: i % 4 == 0);
            claimed[i] = Assert.IsType<WorldVoicePool.Voice>(voice);
        }

        return pool;
    }

    private static bool Playing(uint sourceId) => true;

    private static bool Finished(uint sourceId) => false;

    [Fact]
    public void SixteenVoices_ClaimInRingOrder()
    {
        WorldVoicePool pool = FilledPool(out WorldVoicePool.Voice[] claimed);

        for (int i = 0; i < pool.Count; i++)
        {
            Assert.Same(pool[i], claimed[i]);
            Assert.True(pool[i].InUse);
            Assert.Equal((uint)(i + 1), pool[i].OwnerId);
        }
    }

    [Fact]
    public void ASeventeenthSound_IsDropped_WithoutTakingAVoiceFromAPlayingOne()
    {
        WorldVoicePool pool = FilledPool(out _);

        Assert.Null(pool.Claim(Playing, ownerId: 0xDEADu, isInterface: false));

        for (int i = 0; i < pool.Count; i++)
        {
            Assert.True(pool[i].InUse);
            Assert.Equal((uint)(i + 1), pool[i].OwnerId);
        }
    }

    // There is no reserve of voices kept back for the interface, and no
    // last-resort voice it may take when the pool is full.
    [Fact]
    public void AnInterfaceSound_IsRefusedWhenAllSixteenAreBusy_AndTakesAFreeOneWhenThereIsOne()
    {
        WorldVoicePool pool = FilledPool(out _);

        Assert.Null(pool.Claim(Playing, ownerId: 0u, isInterface: true));
        for (int i = 0; i < pool.Count; i++)
            Assert.Equal((uint)(i + 1), pool[i].OwnerId);   // nothing was taken

        WorldVoicePool.Vacate(pool[3]);

        WorldVoicePool.Voice taken = Assert.IsType<WorldVoicePool.Voice>(
            pool.Claim(Playing, ownerId: 0u, isInterface: true));
        Assert.Same(pool[3], taken);
        Assert.True(taken.IsInterface);
    }

    // The cue that plays as you step into a portal is an interface sound, and
    // it starts at the very moment the world it is leaving is taken down.
    [Fact]
    public void AWorldChange_SilencesTheWorldVoicesOnly()
    {
        var pool = new WorldVoicePool();
        for (int i = 0; i < pool.Count; i++)
            pool[i].SourceId = (uint)(i + 1);

        WorldVoicePool.Voice world = Assert.IsType<WorldVoicePool.Voice>(
            pool.Claim(Playing, ownerId: 0x50000001u, isInterface: false));
        WorldVoicePool.Voice cue = Assert.IsType<WorldVoicePool.Voice>(
            pool.Claim(Playing, ownerId: 0u, isInterface: true));

        WorldVoicePool.Voice[] silenced = pool.SilencedByWorldChange().ToArray();

        Assert.Equal(pool.Count - 1, silenced.Length);
        Assert.Contains(world, silenced);
        Assert.DoesNotContain(cue, silenced);
    }

    [Fact]
    public void EveryVoiceRecordsTheOnePriority_SoNoneIsEverStrictlyLower()
    {
        WorldVoicePool pool = FilledPool(out _);

        for (int i = 0; i < pool.Count; i++)
            Assert.Equal(RetailVoicePool.VoicePriority, pool[i].Priority);
    }

    [Fact]
    public void AVoiceWhoseSoundHasFinished_IsClaimedAgain()
    {
        WorldVoicePool pool = FilledPool(out _);

        WorldVoicePool.Voice reclaimed = Assert.IsType<WorldVoicePool.Voice>(
            pool.Claim(Finished, ownerId: 0xBEEFu, isInterface: false));

        Assert.Equal(0xBEEFu, reclaimed.OwnerId);
        Assert.True(reclaimed.InUse);
        Assert.False(reclaimed.IsInterface);
    }

    [Fact]
    public void AVacatedVoice_IsFreeAgain()
    {
        WorldVoicePool pool = FilledPool(out WorldVoicePool.Voice[] claimed);

        WorldVoicePool.Vacate(claimed[5]);

        Assert.False(claimed[5].InUse);
        Assert.False(claimed[5].IsInterface);
        Assert.Equal(0u, claimed[5].OwnerId);
        Assert.Same(claimed[5], pool.Claim(Playing, ownerId: 0x1234u, isInterface: false));
    }
}
