using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Audio;
using Xunit;

namespace AcDream.Core.Tests.Audio;

public sealed class AmbientSoundTests
{
    private sealed class ScriptedRandom(params float[] rolls) : ISoundRandom
    {
        private readonly Queue<float> _rolls = new(rolls);
        public float NextVariantRoll() => _rolls.Count > 0 ? _rolls.Dequeue() : 0f;
        public float NextProbabilityRoll() => NextVariantRoll();
    }

    private static AmbientSoundDescriptor Continuous(
        float volume = 1f, float minRate = 5f) =>
        new(SoundId.Ambient1, volume, BaseChance: 0f, minRate, MaxRate: 0f);

    private static AmbientSoundDescriptor Intermittent(
        float volume = 1f, float baseChance = 0.5f, float minRate = 4f, float maxRate = 10f) =>
        new(SoundId.Ambient2, volume, baseChance, minRate, maxRate);

    // ── The polarity that decides the whole model ──────────────────────────

    [Fact]
    public void BaseChanceZero_MeansContinuous_NotIntermittent()
    {
        Assert.True(Continuous().IsContinuous);
        Assert.False(Intermittent(baseChance: 0.5f).IsContinuous);
    }


    [Theory]
    [InlineData(0f, 1f)]        // on top of the listener
    [InlineData(19.9f, 1f)]     // inside the 20 m full-weight radius
    [InlineData(20f, 1f)]       // 400 == 400 is not > 400, and not < 400 -> 400/400
    [InlineData(40f, 0.25f)]    // 400/1600
    [InlineData(120f, 400f / 14400f)]
    [InlineData(120.1f, 0f)]
    [InlineData(500f, 0f)]
    public void CalcWeight_IsFullInsideTwentyMetres_ThenInverseSquare(float distance, float expected)
    {
        float weight = AmbientSoundConstants.CalcWeight(new Vector3(distance, 0f, 0f));
        Assert.Equal(expected, weight, 4);
    }


    [Fact]
    public void CalcDirection_InsideFourteenMetres_IsInViewerBlock()
    {
        // The threshold is min_dist_sq * 0.5 = 200 m^2 = 14.142 m — the ONLY
        // place the squared value is halved. Reading it as min_dist would widen
        // the omnidirectional zone from 14 m to 20 m.
        Assert.Equal(
            AmbientDirection.InViewerBlock,
            AmbientSoundConstants.CalcDirection(new Vector3(14.1f, 0f, 0f)));
        Assert.NotEqual(
            AmbientDirection.InViewerBlock,
            AmbientSoundConstants.CalcDirection(new Vector3(14.2f, 0f, 0f)));
    }

    [Theory]
    [InlineData(0f, 50f, AmbientDirection.North)]
    [InlineData(0f, -50f, AmbientDirection.South)]
    [InlineData(50f, 0f, AmbientDirection.East)]
    [InlineData(-50f, 0f, AmbientDirection.West)]
    [InlineData(40f, 40f, AmbientDirection.Northeast)]
    [InlineData(-40f, 40f, AmbientDirection.Northwest)]
    [InlineData(40f, -40f, AmbientDirection.Southeast)]
    [InlineData(-40f, -40f, AmbientDirection.Southwest)]
    public void CalcDirection_SectorsMatchRetail(float x, float y, AmbientDirection expected)
    {
        Assert.Equal(expected, AmbientSoundConstants.CalcDirection(new Vector3(x, y, 0f)));
    }

    [Fact]
    public void CalcDirection_DiagonalNeedsBothAxesWithinTwoTimes()
    {
        // Ratio gate is 2.0 both ways: 50/20 = 2.5 is NOT diagonal.
        Assert.Equal(
            AmbientDirection.North,
            AmbientSoundConstants.CalcDirection(new Vector3(20f, 50f, 0f)));
        // 40/30 = 1.33 IS diagonal.
        Assert.Equal(
            AmbientDirection.Northeast,
            AmbientSoundConstants.CalcDirection(new Vector3(30f, 40f, 0f)));
    }


    [Fact]
    public void ContinuousVolume_IsItsShareOfTheTotalWeight()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom(0f));
        scheduler.BeginRebuild();
        AmbientSoundInstance grass = scheduler.Track(Continuous(volume: 1f), 0x20000001u);
        AmbientSoundInstance shore = scheduler.Track(Continuous(volume: 1f), 0x20000002u);

        scheduler.Contribute(grass, new Vector3(1f, 0f, 0f));
        scheduler.Contribute(grass, new Vector3(2f, 0f, 0f));
        scheduler.Contribute(grass, new Vector3(3f, 0f, 0f));
        scheduler.Contribute(shore, new Vector3(4f, 0f, 0f));
        scheduler.EndRebuild(now: 0);

        Assert.Equal(4f, scheduler.TotalSoundCount, 4);
        Assert.Equal(0.75f, grass.CurrentVolume, 4);
        Assert.Equal(0.25f, shore.CurrentVolume, 4);
    }

    [Fact]
    public void ContinuousBed_BelowTheAudibilityFloor_CannotBeHeard()
    {
        // ambient_sound_min_vol = 0.03 linear (about -30.5 dB).
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom(0f));
        scheduler.BeginRebuild();
        AmbientSoundInstance faint = scheduler.Track(Continuous(volume: 1f), 0x20000001u);
        AmbientSoundInstance loud = scheduler.Track(Continuous(volume: 1f), 0x20000002u);

        scheduler.Contribute(faint, new Vector3(119f, 0f, 0f));   // tiny weight
        for (int i = 0; i < 5; i++)
            scheduler.Contribute(loud, new Vector3(i, 0f, 0f));   // weight 1 each
        scheduler.EndRebuild(now: 0);

        Assert.True(faint.CurrentVolume < AmbientSoundConstants.MinVolume);
        Assert.False(faint.CanHear());
        Assert.True(loud.CanHear());
    }

    [Fact]
    public void IntermittentPlayChance_ScalesWithItsShare_AndVolumeStaysAuthored()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom(0f));
        scheduler.BeginRebuild();
        AmbientSoundInstance a = scheduler.Track(Intermittent(volume: 0.8f, baseChance: 0.6f), 1u);
        AmbientSoundInstance b = scheduler.Track(Intermittent(volume: 0.8f, baseChance: 0.6f), 2u);
        scheduler.Contribute(a, new Vector3(1f, 0f, 0f));
        scheduler.Contribute(b, new Vector3(2f, 0f, 0f));
        scheduler.EndRebuild(now: 0);

        Assert.Equal(0.3f, a.PlayChance, 4);
        Assert.Equal(0.8f, a.GetVolume(), 4);
    }

    [Fact]
    public void IntermittentUpdate_WithZeroWeight_LeavesPlayChanceAlone()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom(0f));
        scheduler.BeginRebuild();
        AmbientSoundInstance sound = scheduler.Track(Intermittent(baseChance: 0.6f), 1u);
        scheduler.Contribute(sound, new Vector3(1f, 0f, 0f));
        scheduler.EndRebuild(now: 0);
        Assert.Equal(0.6f, sound.PlayChance, 4);

        scheduler.BeginRebuild();
        scheduler.EndRebuild(now: 0);
        Assert.Equal(0f, sound.PlayChance);
        Assert.False(sound.CanHear());
    }

    [Fact]
    public void Rebuild_ResetsStaleBearings()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom(0f));
        scheduler.BeginRebuild();
        AmbientSoundInstance sound = scheduler.Track(Intermittent(), 1u);
        scheduler.Contribute(sound, new Vector3(0f, 60f, 0f));   // north
        scheduler.EndRebuild(now: 0);
        Assert.Contains(
            sound.Directions,
            shell => shell.Direction == AmbientDirection.North);

        scheduler.BeginRebuild();
        scheduler.Contribute(sound, new Vector3(0f, -60f, 0f));  // south only
        scheduler.EndRebuild(now: 0);
        Assert.DoesNotContain(
            sound.Directions,
            shell => shell.Direction == AmbientDirection.North);
    }

    [Fact]
    public void InViewerBlockContribution_SpreadsAcrossAllEightDirections()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom(0f));
        scheduler.BeginRebuild();
        AmbientSoundInstance sound = scheduler.Track(Intermittent(), 1u);
        scheduler.Contribute(sound, new Vector3(3f, 0f, 0f));   // inside 14.14 m
        scheduler.EndRebuild(now: 0);

        Assert.Equal(8, sound.Directions.Count);
        Assert.All(sound.Directions, shell =>
        {
            Assert.Equal(AmbientSoundConstants.InBlockNearDistance, shell.MinDistance, 3);
            Assert.Equal(AmbientSoundConstants.ShellHalfThickness, shell.MaxDistance, 3);
        });
    }

    // ── PlayNow / positions / intervals ────────────────────────────────────

    [Fact]
    public void ContinuousPlayNow_IsAlwaysTrue()
    {
        AmbientSoundInstance sound = new(Continuous(), 1u);
        Assert.True(sound.PlayNow(new ScriptedRandom(0.99f)));
    }

    [Theory]
    [InlineData(0.4f, true)]
    [InlineData(0.5f, true)]    // inclusive
    [InlineData(0.6f, false)]
    public void IntermittentPlayNow_RollsAgainstPlayChance(float roll, bool expected)
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom(0f));
        scheduler.BeginRebuild();
        AmbientSoundInstance sound = scheduler.Track(Intermittent(baseChance: 0.5f), 1u);
        scheduler.Contribute(sound, new Vector3(1f, 0f, 0f));
        scheduler.EndRebuild(now: 0);
        Assert.Equal(0.5f, sound.PlayChance, 4);

        Assert.Equal(expected, sound.PlayNow(new ScriptedRandom(roll)));
    }

    [Fact]
    public void ContinuousBed_HasNoPosition()
    {
        AmbientSoundInstance sound = new(Continuous(), 1u);
        Assert.False(sound.TryGetSoundPosition(
            new Vector3(100f, 200f, 10f), new ScriptedRandom(0f), out _));
    }

    [Fact]
    public void IntermittentPosition_OffsetsTheListenerAndKeepsTheirZ()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom(0f));
        scheduler.BeginRebuild();
        AmbientSoundInstance sound = scheduler.Track(Intermittent(), 1u);
        scheduler.Contribute(sound, new Vector3(0f, 60f, 0f));   // north shell
        scheduler.EndRebuild(now: 0);

        var listener = new Vector3(100f, 200f, 37f);
        // rolls: shell index, bearing jitter (mid), distance t
        Assert.True(sound.TryGetSoundPosition(
            listener, new ScriptedRandom(0f, 0.5f, 1f), out Vector3 position));

        Assert.Equal(37f, position.Z);                 // listener Z preserved
        Assert.True(position.Y > listener.Y);          // to the north
        Assert.Equal(100f, position.X, 1);
    }

    [Fact]
    public void IntermittentDistance_IsQuadraticallyBiasedTowardMin()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom(0f));
        scheduler.BeginRebuild();
        AmbientSoundInstance sound = scheduler.Track(Intermittent(), 1u);
        scheduler.Contribute(sound, new Vector3(0f, 60f, 0f));
        scheduler.EndRebuild(now: 0);
        AmbientDirectionShell shell = sound.Directions[0];

        var listener = Vector3.Zero;
        // t = 0.5 -> min + (max-min)*0.25, NOT the linear midpoint.
        sound.TryGetSoundPosition(
            listener, new ScriptedRandom(0f, 0.5f, 0.5f), out Vector3 position);

        float distance = position.Length();
        float quadratic = shell.MinDistance + ((shell.MaxDistance - shell.MinDistance) * 0.25f);
        float linear = shell.MinDistance + ((shell.MaxDistance - shell.MinDistance) * 0.5f);
        Assert.Equal(quadratic, distance, 1);
        Assert.NotEqual(linear, distance, 1);
    }

    [Fact]
    public void ContinuousInterval_UsesMinRateOnly()
    {
        AmbientSoundInstance sound = new(Continuous(minRate: 7f), 1u);
        Assert.Equal(7f, sound.GetPlayInterval(new ScriptedRandom(0.9f)));
    }

    [Fact]
    public void IntermittentInterval_RollsBetweenTheAuthoredRates()
    {
        AmbientSoundInstance sound = new(Intermittent(minRate: 4f, maxRate: 10f), 1u);
        Assert.Equal(7f, sound.GetPlayInterval(new ScriptedRandom(0.5f)), 3);
    }

    [Fact]
    public void RollDice_SwapsAnInvertedRange()
    {
        Assert.Equal(
            7f,
            AmbientSoundInstance.RollDice(10f, 4f, new ScriptedRandom(0.5f)),
            3);
    }

    // ── The scheduler ──────────────────────────────────────────────────────

    [Fact]
    public void Scheduler_FiresAtTheDeadlineAndReArms()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        var firings = new List<AmbientSoundFiring>();
        scheduler.BeginRebuild();
        AmbientSoundInstance bed = scheduler.Track(Continuous(minRate: 5f), 1u);
        scheduler.Contribute(bed, Vector3.Zero);
        scheduler.EndRebuild(now: 0, firings, Vector3.Zero);

        firings.Clear();

        scheduler.Tick(now: 4.9, firings, Vector3.Zero);
        Assert.Empty(firings);

        scheduler.Tick(now: 5.0, firings, Vector3.Zero);
        Assert.Empty(firings);

        scheduler.Tick(now: 5.01, firings, Vector3.Zero);
        Assert.Single(firings);
        Assert.True(bed.OnQueue);
        Assert.Equal(1, scheduler.QueuedCount);

        firings.Clear();
        scheduler.Tick(now: 10.1, firings, Vector3.Zero);
        Assert.Single(firings);
    }

    [Fact]
    public void Scheduler_ArmingPlaysImmediately_WithNoInitialDelay()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        var firings = new List<AmbientSoundFiring>();

        scheduler.BeginRebuild();
        AmbientSoundInstance bed = scheduler.Track(Continuous(minRate: 30f), 1u);
        scheduler.Contribute(bed, Vector3.Zero);
        scheduler.EndRebuild(now: 100.0, firings, Vector3.Zero);

        Assert.Single(firings);
        Assert.True(bed.OnQueue);
    }

    [Fact]
    public void Scheduler_ZeroPlayInterval_DoesNotSpinForever()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        scheduler.BeginRebuild();
        AmbientSoundInstance bed = scheduler.Track(Continuous(minRate: 0f), 1u);
        scheduler.Contribute(bed, Vector3.Zero);
        scheduler.EndRebuild(now: 0);

        var firings = new List<AmbientSoundFiring>();
        scheduler.Tick(now: 1.0, firings, Vector3.Zero);

        // One pop, then the re-armed deadline equals `now` and the loop stops.
        Assert.Single(firings);
    }

    [Fact]
    public void Scheduler_DoesNotReArmAnAlreadyQueuedInstanceOnRebuild()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        scheduler.BeginRebuild();
        AmbientSoundInstance bed = scheduler.Track(Continuous(minRate: 5f), 1u);
        scheduler.Contribute(bed, Vector3.Zero);
        scheduler.EndRebuild(now: 0);
        Assert.Equal(1, scheduler.QueuedCount);

        for (int i = 0; i < 5; i++)
        {
            scheduler.BeginRebuild();
            scheduler.Contribute(bed, Vector3.Zero);
            scheduler.EndRebuild(now: 0);
        }

        Assert.Equal(1, scheduler.QueuedCount);
    }

    [Fact]
    public void Scheduler_ArmsAnInstanceThatBecomesAudible()
    {
        // The other half of the guard: a newly audible ambient must not stay
        // silent until some later event.
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        scheduler.BeginRebuild();
        AmbientSoundInstance bed = scheduler.Track(Continuous(minRate: 5f), 1u);
        scheduler.EndRebuild(now: 0);
        Assert.Equal(0, scheduler.QueuedCount);

        scheduler.BeginRebuild();
        scheduler.Contribute(bed, Vector3.Zero);
        scheduler.EndRebuild(now: 0);
        Assert.Equal(1, scheduler.QueuedCount);
    }

    [Fact]
    public void Scheduler_DropsAnInstanceThatWentInaudible()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        scheduler.BeginRebuild();
        AmbientSoundInstance bed = scheduler.Track(Continuous(minRate: 5f), 1u);
        scheduler.Contribute(bed, Vector3.Zero);
        scheduler.EndRebuild(now: 0);

        scheduler.BeginRebuild();
        scheduler.EndRebuild(now: 0);

        var firings = new List<AmbientSoundFiring>();
        scheduler.Tick(now: 99.0, firings, Vector3.Zero);

        Assert.Empty(firings);
        Assert.Equal(0, scheduler.QueuedCount);
        Assert.False(bed.OnQueue);
    }

    [Fact]
    public void Scheduler_ContinuousFiringHasNoPosition_IntermittentDoes()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        scheduler.BeginRebuild();
        AmbientSoundInstance bed = scheduler.Track(Continuous(minRate: 1f), 1u);
        AmbientSoundInstance chirp = scheduler.Track(
            Intermittent(baseChance: 1f, minRate: 1f, maxRate: 1f), 2u);
        scheduler.Contribute(bed, Vector3.Zero);
        scheduler.Contribute(chirp, new Vector3(0f, 60f, 0f));
        scheduler.EndRebuild(now: 0);

        var firings = new List<AmbientSoundFiring>();
        scheduler.Tick(now: 1.01, firings, Vector3.Zero);

        Assert.Equal(2, firings.Count);
        Assert.Null(firings.Find(f => f.Instance == bed).Position);
        Assert.NotNull(firings.Find(f => f.Instance == chirp).Position);
    }

    [Fact]
    public void Scheduler_Clear_DropsEverything()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        scheduler.BeginRebuild();
        AmbientSoundInstance bed = scheduler.Track(Continuous(), 1u);
        scheduler.Contribute(bed, Vector3.Zero);
        scheduler.EndRebuild(now: 0);

        scheduler.Clear();

        Assert.Equal(0, scheduler.QueuedCount);
        Assert.Empty(scheduler.Instances);
        Assert.False(bed.OnQueue);
        Assert.Equal(0f, scheduler.TotalSoundCount);
    }

    // ── The gatherer: the region walk and the shared denominator ───────────

    private static DatReaderWriter.DBObjs.Region SyntheticRegion(
        int ambientEntriesPerTable)
    {
        // terrainType 0 -> sceneType index 0 -> scene 0 -> STB 0.
        var region = new DatReaderWriter.DBObjs.Region
        {
            TerrainInfo = new DatReaderWriter.Types.TerrainDesc(),
            SceneInfo = new DatReaderWriter.Types.SceneDesc(),
            SoundInfo = new DatReaderWriter.Types.SoundDesc(),
        };

        var terrain = new DatReaderWriter.Types.TerrainType();
        terrain.SceneTypes.Add(0u);
        region.TerrainInfo.TerrainTypes.Add(terrain);

        var scene = new DatReaderWriter.Types.SceneType { StbIndex = 0u };
        region.SceneInfo.SceneTypes.Add(scene);

        var stb = new DatReaderWriter.Types.AmbientSTBDesc { STBId = 0x20000001u };
        for (int i = 0; i < ambientEntriesPerTable; i++)
        {
            stb.AmbientSounds.Add(new DatReaderWriter.Types.AmbientSoundDesc
            {
                SType = (DatReaderWriter.Enums.Sound)((uint)DatReaderWriter.Enums.Sound.Ambient1 + i),
                Volume = 1f,
                BaseChance = 0f,
                MinRate = 5f,
                MaxRate = 0f,
            });
        }
        region.SoundInfo.STBDesc.Add(stb);
        return region;
    }

    /// <summary>A landblock whose every terrain word selects terrain 0 / scene 0.</summary>
    private static ushort[] UniformTerrain() => new ushort[81];

    [Fact]
    public void Gatherer_ListenerInsideTheBlock_AccumulatesWeight()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        var gatherer = new AmbientSoundGatherer(scheduler);

        gatherer.Rebuild(
            SyntheticRegion(ambientEntriesPerTable: 1),
            viewerLandblockId: 0xA9B4FFFFu,
            listenerLocalPosition: new Vector3(96f, 96f, 0f),   // mid-landblock
            landblocks: _ => UniformTerrain(),
            now: 0);

        Assert.True(scheduler.TotalSoundCount > 0f);
        AmbientSoundInstance instance = Assert.Single(scheduler.Instances);
        Assert.True(instance.CanHear());
    }

    [Fact]
    public void Gatherer_TotalWeightCountsCellsOnce_NotOncePerAmbientEntry()
    {
        var single = new AmbientSoundScheduler(new ScriptedRandom());
        new AmbientSoundGatherer(single).Rebuild(
            SyntheticRegion(1), 0xA9B4FFFFu, new Vector3(96f, 96f, 0f),
            _ => UniformTerrain(), now: 0);

        var triple = new AmbientSoundScheduler(new ScriptedRandom());
        new AmbientSoundGatherer(triple).Rebuild(
            SyntheticRegion(3), 0xA9B4FFFFu, new Vector3(96f, 96f, 0f),
            _ => UniformTerrain(), now: 0);

        Assert.Equal(single.TotalSoundCount, triple.TotalSoundCount, 3);
        Assert.Equal(3, triple.Instances.Count);

        // And each of the three beds keeps a full share, not a third of one.
        AmbientSoundInstance one = Assert.Single(single.Instances);
        Assert.All(
            triple.Instances,
            bed => Assert.Equal(one.CurrentVolume, bed.CurrentVolume, 3));
    }

    [Fact]
    public void Gatherer_MissingNeighbours_ContributeNothing()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        var gatherer = new AmbientSoundGatherer(scheduler);

        gatherer.Rebuild(
            SyntheticRegion(1),
            0xA9B4FFFFu,
            new Vector3(96f, 96f, 0f),
            // Only the viewer's own landblock is loaded.
            id => id == 0xA9B4FFFFu ? UniformTerrain() : null,
            now: 0);

        Assert.True(scheduler.TotalSoundCount > 0f);
    }

    [Fact]
    public void Gatherer_UnauthoredTerrain_ProducesNoAmbients()
    {
        var scheduler = new AmbientSoundScheduler(new ScriptedRandom());
        var region = SyntheticRegion(1);
        region.SceneInfo.SceneTypes[0].StbIndex = 0xFFFFFFFFu;

        new AmbientSoundGatherer(scheduler).Rebuild(
            region, 0xA9B4FFFFu, new Vector3(96f, 96f, 0f),
            _ => UniformTerrain(), now: 0);

        Assert.Empty(scheduler.Instances);
        Assert.Equal(0f, scheduler.TotalSoundCount);
    }
}
