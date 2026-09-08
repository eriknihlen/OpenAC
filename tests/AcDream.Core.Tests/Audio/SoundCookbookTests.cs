using System;
using System.Collections.Generic;
using AcDream.Core.Audio;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DRWSoundEntry = DatReaderWriter.Types.SoundEntry;
using DRWSound = DatReaderWriter.Enums.Sound;
using Xunit;

namespace AcDream.Core.Tests.Audio;

public sealed class SoundCookbookTests
{
    private sealed class ScriptedRandom : ISoundRandom
    {
        private readonly Queue<float> _variant;
        private readonly Queue<float> _probability;

        public ScriptedRandom(float[]? variant = null, float[]? probability = null)
        {
            _variant = new Queue<float>(variant ?? Array.Empty<float>());
            _probability = new Queue<float>(probability ?? Array.Empty<float>());
        }

        public float NextVariantRoll() => _variant.Dequeue();
        public float NextProbabilityRoll() => _probability.Dequeue();
    }

    private static DRWSoundEntry Entry(uint id, float probability = 1f, float volume = 1f, float priority = 1f) =>
        new() { Id = id, Probability = probability, Volume = volume, Priority = priority };


    [Fact]
    public void PickVariant_EmptyList_ReturnsNull()
    {
        Assert.Null(SoundCookbook.PickVariant(new List<DRWSoundEntry>(), new ScriptedRandom(new[] { 0f })));
    }

    [Fact]
    public void PickVariant_SingleEntry_AlwaysIndexZero()
    {
        var entries = new List<DRWSoundEntry> { Entry(0x0A000001) };
        foreach (float roll in new[] { 0f, 0.5f, SoundRandom.MaxVariantRoll })
        {
            var picked = SoundCookbook.PickVariant(entries, new ScriptedRandom(new[] { roll }));
            Assert.Equal(0x0A000001u, picked!.Id.DataId);
        }
    }

    [Fact]
    public void PickVariant_TwoEntries_LastIsUnreachable()
    {
        var entries = new List<DRWSoundEntry> { Entry(0x0A000001), Entry(0x0A00051E) };
        foreach (float roll in new[] { 0f, 0.25f, 0.5f, 0.75f, 0.999f, SoundRandom.MaxVariantRoll })
        {
            var picked = SoundCookbook.PickVariant(entries, new ScriptedRandom(new[] { roll }));
            Assert.Equal(0x0A000001u, picked!.Id.DataId);
        }
    }

    [Theory]
    // n == 3 → idx = (int)(roll * 2): halves of the roll range map to 0 and 1.
    [InlineData(0f, 0)]
    [InlineData(0.49f, 0)]
    [InlineData(0.5f, 1)]
    [InlineData(SoundRandom.MaxVariantRoll, 1)]
    public void PickVariant_ThreeEntries_TruncatesTowardZero(float roll, int expectedIndex)
    {
        var entries = new List<DRWSoundEntry>
        {
            Entry(0x0A000001), Entry(0x0A000002), Entry(0x0A000003),
        };
        var picked = SoundCookbook.PickVariant(entries, new ScriptedRandom(new[] { roll }));
        Assert.Equal(entries[expectedIndex].Id.DataId, picked!.Id.DataId);
    }

    [Fact]
    public void PickVariant_IgnoresProbabilityEntirely()
    {
        var entries = new List<DRWSoundEntry>
        {
            Entry(0x0A000001, probability: 0f),
            Entry(0x0A000002, probability: 1f),
        };
        var picked = SoundCookbook.PickVariant(entries, new ScriptedRandom(new[] { 0.9f }));
        Assert.Equal(0x0A000001u, picked!.Id.DataId);
    }

    // ── PlayProbability: the Bernoulli gate ────────────────────────────────

    [Theory]
    [InlineData(0.5f, 0.49f, true)]
    [InlineData(0.5f, 0.5f, false)]   // strict <
    [InlineData(0.5f, 0.51f, false)]
    [InlineData(0f, 0f, false)]       // probability 0 never plays
    [InlineData(1f, 0.99997f, true)]
    [InlineData(1f, 1f, false)]       // rand() == RAND_MAX → skipped even at p=1
    public void PlayProbability_IsStrictLessThan(float probability, float roll, bool expected)
    {
        Assert.Equal(
            expected,
            SoundCookbook.PlayProbability(probability, new ScriptedRandom(probability: new[] { roll })));
    }

    [Fact]
    public void PlayProbability_ZeroProbability_NeverPlaysAcrossTheWholeGrid()
    {
        var rng = new SoundRandom(new Random(1));
        for (int i = 0; i < 20_000; i++)
            Assert.False(SoundCookbook.PlayProbability(0f, rng));
    }

    [Fact]
    public void PlayProbability_FivePercent_MatchesRetailRate()
    {
        var rng = new SoundRandom(new Random(20260808));
        int played = 0;
        for (int i = 0; i < 100_000; i++)
            if (SoundCookbook.PlayProbability(0.05f, rng)) played++;

        Assert.InRange(played, 4_600, 5_400);   // 5% ± 0.4pp
    }

    [Fact]
    public void ProbabilityRoll_ReachesExactlyOne_AndNeverExceedsIt()
    {
        // The gate's grid is rand()/32767 with rand() ∈ [0, 32767], so 1.0 is
        // attainable — that attainability is what makes p=1.0 skip 1-in-32768.
        var rng = new SoundRandom(new Random(7));
        bool sawOne = false;
        for (int i = 0; i < 500_000; i++)
        {
            float roll = rng.NextProbabilityRoll();
            Assert.InRange(roll, 0f, 1f);
            if (roll == 1f) sawOne = true;
        }
        Assert.True(sawOne, "rand()/32767 must be able to return exactly 1.0");
    }

    [Fact]
    public void VariantRoll_NeverReachesOne()
    {
        var rng = new SoundRandom(new Random(11));
        for (int i = 0; i < 500_000; i++)
        {
            float roll = rng.NextVariantRoll();
            Assert.InRange(roll, 0f, SoundRandom.MaxVariantRoll);
            Assert.True(roll < 1f);
        }
    }


    [Fact]
    public void Select_GateFailure_ReturnsNull()
    {
        var entries = new List<DRWSoundEntry> { Entry(0x0A000001, probability: 0.05f) };
        var rng = new ScriptedRandom(variant: new[] { 0f }, probability: new[] { 0.9f });
        Assert.Null(SoundCookbook.Select(entries, rng));
    }

    [Fact]
    public void Select_GatePass_ReturnsPickedEntry()
    {
        var entries = new List<DRWSoundEntry> { Entry(0x0A000001, probability: 0.05f) };
        var rng = new ScriptedRandom(variant: new[] { 0f }, probability: new[] { 0.01f });
        Assert.Equal(0x0A000001u, SoundCookbook.Select(entries, rng)!.Id.DataId);
    }

    [Fact]
    public void Select_SingleEntryBelowOne_IsGated_NotShortCircuited()
    {
        var entries = new List<DRWSoundEntry> { Entry(0x0A000001, probability: 0.05f) };
        var rng = new SoundRandom(new Random(99));
        int played = 0;
        for (int i = 0; i < 20_000; i++)
            if (SoundCookbook.Select(entries, rng) is not null) played++;

        Assert.InRange(played, 800, 1_200);   // ~5% of 20k, not 20k
    }

    [Fact]
    public void Select_WithSoundTable_LooksUpBySound()
    {
        var table = new SoundTable();
        table.Sounds[DRWSound.Footstep1] = new SoundData();
        table.Sounds[DRWSound.Footstep1].Entries.Add(Entry(0x0A000123, volume: 0.7f));

        var rng = new ScriptedRandom(variant: new[] { 0f }, probability: new[] { 0f });
        var picked = SoundCookbook.Select(table, DRWSound.Footstep1, rng);
        Assert.Equal(0x0A000123u, picked!.Id.DataId);
        Assert.Equal(0.7f, picked.Volume);
    }

    [Fact]
    public void Select_WithSoundTable_MissingSound_ReturnsNull()
    {
        var table = new SoundTable();
        var rng = new ScriptedRandom(variant: new[] { 0f }, probability: new[] { 0f });
        Assert.Null(SoundCookbook.Select(table, DRWSound.Attack1, rng));
    }

    [Fact]
    public void Select_EmptyEntryList_DoesNotConsumeAProbabilityRoll()
    {
        var table = new SoundTable();
        table.Sounds[DRWSound.Attack1] = new SoundData();
        var rng = new ScriptedRandom(variant: new[] { 0f });
        Assert.Null(SoundCookbook.Select(table, DRWSound.Attack1, rng));
    }
}
