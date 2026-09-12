using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.Core.Lighting;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;
using AcDream.Core.Rendering;
using AcDream.Core.Terrain;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.App.Tests.Streaming;

public sealed class LandblockConcretePresentationPipelineTests
{
    private const uint LandblockId = 0xA9B4FFFFu;

    [Fact]
    public void Loaded_ConcreteOwnersPreserveRetailPrefixAndSpatialOrder()
    {
        var calls = new List<string>();
        ConcreteFixture fixture = Fixture(
            calls,
            commitEnvCells: _ => calls.Add("envcell"));
        fixture.Events.EntitySpawned += _ =>
        {
            Assert.Equal(1, fixture.Physics.Diagnostics.CompleteCount);
            Assert.False(fixture.State.IsLoaded(LandblockId));
            calls.Add("plugin");
        };
        var pipeline = new LandblockPresentationPipeline(
            fixture.Render,
            fixture.Physics,
            fixture.Static,
            fixture.State,
            fixture.RetirementOwner,
            onLandblockLoaded: _ => calls.Add("live-recovery"),
            ensureEnvCellMeshes: null);
        LandblockBuild build = Build(Entity(0x80A9B401u));

        pipeline.PublishLoaded(Result(build));

        Assert.Equal(
            ["terrain", "envcell", "plugin", "pin", "live-recovery"],
            calls);
        Assert.True(fixture.State.IsNearTier(LandblockId));
        Assert.Equal(1, fixture.Render.Diagnostics.BeginCount);
        Assert.Equal(1, fixture.Physics.Diagnostics.BeginCount);
        Assert.Equal(1, fixture.Static.Diagnostics.CompleteCount);
    }

    [Fact]
    public void MeteredLoaded_RetainsExactHeadAndPublishesSpatiallyOnlyAfterOwnerSuffix()
    {
        var calls = new List<string>();
        ConcreteFixture fixture = Fixture(
            calls,
            commitEnvCells: _ => calls.Add("envcell"));
        fixture.Events.EntitySpawned += _ => calls.Add("plugin");
        var pipeline = new LandblockPresentationPipeline(
            fixture.Render,
            fixture.Physics,
            fixture.Static,
            fixture.State,
            fixture.RetirementOwner,
            onLandblockLoaded: _ => calls.Add("live-recovery"));
        LandblockStreamResult.Loaded result =
            Result(Build(Entity(0x80A9B401u), Entity(0x80A9B402u)));
        LandblockStreamCostEstimate estimate =
            LandblockStreamResultCost.Estimate(result);
        var budget = new StreamingWorkBudget(
            TimeSpan.FromSeconds(1),
            maxCompletionAdmissions: 64,
            maxAdoptedCpuBytes: 1_000_000,
            maxEntityOperations: 1,
            maxGpuUploadBytes: 1_000_000,
            maxGlRetireOperations: 64,
            destinationReserveFraction: 0.75f);

        LandblockPublicationAdvance advance = default;
        for (int frame = 0; frame < 64; frame++)
        {
            var meter = new StreamingWorkMeter(budget);
            advance = frame == 0
                ? pipeline.PublishLoaded(
                    result,
                    estimate,
                    meter,
                    ensureProgress: false)
                : pipeline.ResumePublication(
                    result,
                    meter,
                    ensureProgress: false);
            meter.FinishFrame();

            Assert.True(
                meter.Snapshot.Used.EntityOperations <= 1,
                $"stage={meter.Snapshot.LastStage}, " +
                $"entities={meter.Snapshot.Used.EntityOperations}, " +
                $"oversized={meter.Snapshot.OversizedProgressCount}");
            if (fixture.Static.Diagnostics.CompleteCount == 0)
                Assert.False(fixture.State.IsLoaded(LandblockId));
            if (advance.Completed)
                break;
        }

        Assert.True(advance.Completed);
        Assert.False(pipeline.HasPendingPublication(result));
        Assert.True(fixture.State.IsNearTier(LandblockId));
        Assert.Equal(
            ["terrain", "envcell", "plugin", "plugin", "pin", "live-recovery"],
            calls);
        Assert.Equal(1, fixture.Render.Diagnostics.BeginCount);
        Assert.Equal(1, fixture.Physics.Diagnostics.BeginCount);
        Assert.Equal(1, fixture.Physics.Diagnostics.CompleteCount);
        Assert.Equal(1, fixture.Static.Diagnostics.CompleteCount);
    }

    [Fact]
    public void RenderSuffixFailure_ResumesConcreteReceiptsWithoutReplayingPrefixes()
    {
        var calls = new List<string>();
        bool failEnvOnce = true;
        ConcreteFixture fixture = Fixture(
            calls,
            commitEnvCells: _ =>
            {
                calls.Add("envcell");
                if (failEnvOnce)
                {
                    failEnvOnce = false;
                    throw new InvalidOperationException("injected envcell failure");
                }
            });
        fixture.Events.EntitySpawned += _ => calls.Add("plugin");
        var pipeline = new LandblockPresentationPipeline(
            fixture.Render,
            fixture.Physics,
            fixture.Static,
            fixture.State,
            fixture.RetirementOwner);
        LandblockBuild build = Build(Entity(0x80A9B401u));
        LandblockStreamResult.Loaded result = Result(build);

        Assert.Throws<InvalidOperationException>(() => pipeline.PublishLoaded(result));
        Assert.False(fixture.State.IsLoaded(LandblockId));
        Assert.True(pipeline.HasPendingPublication(result));
        Assert.Equal(1, fixture.Render.Diagnostics.BeginCount);
        Assert.Equal(1, fixture.Physics.Diagnostics.BeginCount);
        Assert.Equal(0, fixture.Physics.Diagnostics.CompleteCount);
        Assert.Equal(0, fixture.Static.Diagnostics.BeginCount);

        pipeline.ResumePublication(result);

        Assert.Equal(1, calls.Count(call => call == "terrain"));
        Assert.Equal(2, calls.Count(call => call == "envcell"));
        Assert.Equal(1, calls.Count(call => call == "plugin"));
        Assert.Equal(1, calls.Count(call => call == "pin"));
        Assert.Equal(1, fixture.Render.Diagnostics.BeginCount);
        Assert.Equal(1, fixture.Physics.Diagnostics.BeginCount);
        Assert.Equal(1, fixture.Physics.Diagnostics.CompleteCount);
        Assert.Equal(1, fixture.Static.Diagnostics.CompleteCount);
        Assert.False(pipeline.HasPendingPublication(result));
        Assert.True(fixture.State.IsLoaded(LandblockId));
    }

    [Fact]
    public void MeteredLoaded_NonterminalCommitWithoutDebt_CompletesInOneMeteredAdvance()
    {
        var calls = new List<string>();
        ConcreteFixture fixture = Fixture(
            calls,
            commitEnvCells: _ => calls.Add("envcell"));
        var pipeline = new LandblockPresentationPipeline(
            fixture.Render,
            fixture.Physics,
            fixture.Static,
            fixture.State,
            fixture.RetirementOwner,
            onLandblockLoaded: _ => calls.Add("live-recovery"));
        LandblockStreamResult.Loaded result =
            Result(Build(Entity(0x80A9B401u)));
        LandblockStreamCostEstimate estimate =
            LandblockStreamResultCost.Estimate(result);
        var budget = new StreamingWorkBudget(
            TimeSpan.FromSeconds(1),
            maxCompletionAdmissions: 64,
            maxAdoptedCpuBytes: 1_000_000,
            maxEntityOperations: 4_096,
            maxGpuUploadBytes: 1_000_000,
            maxGlRetireOperations: 64,
            destinationReserveFraction: 0.75f);
        var meter = new StreamingWorkMeter(
            budget,
            timestamp: static () => 0,
            timestampFrequency: 1);

        LandblockPublicationAdvance advance = pipeline.PublishLoaded(
            result,
            estimate,
            meter,
            ensureProgress: true);
        meter.FinishFrame();

        Assert.True(advance.Completed);
        Assert.False(pipeline.HasPendingPublication(result));
        Assert.True(fixture.State.IsNearTier(LandblockId));
        Assert.Equal(1, fixture.Physics.Diagnostics.CompleteCount);
        Assert.Equal(1, fixture.Static.Diagnostics.CompleteCount);
    }

    [Fact]
    public void CrossOwnerValidationFailure_BlocksBeforeAnyPresentationMutation()
    {
        var calls = new List<string>();
        ConcreteFixture fixture = Fixture(calls);
        var pipeline = new LandblockPresentationPipeline(
            fixture.Render,
            fixture.Physics,
            fixture.Static,
            fixture.State,
            fixture.RetirementOwner);
        WorldEntity first = Entity(0x80A9B401u);
        LandblockBuild build = Build(first, Entity(first.Id));
        LandblockStreamResult.Loaded result = Result(build);

        Assert.Throws<InvalidOperationException>(() => pipeline.PublishLoaded(result));

        Assert.Empty(calls);
        Assert.False(fixture.State.IsLoaded(LandblockId));
        Assert.Equal(0, fixture.Engine.LandblockCount);
        Assert.Equal(0, fixture.Render.Diagnostics.BeginCount);
        Assert.Equal(0, fixture.Physics.Diagnostics.BeginCount);
        Assert.Equal(0, fixture.Static.Diagnostics.BeginCount);
        Assert.True(pipeline.HasPendingPublication(result));
        Assert.Equal(1, pipeline.PendingPublicationCount);
    }

    [Fact]
    public void RetainedIdSourceChange_BlocksBeforeReplacementMutation()
    {
        var calls = new List<string>();
        ConcreteFixture fixture = Fixture(calls);
        var pipeline = new LandblockPresentationPipeline(
            fixture.Render,
            fixture.Physics,
            fixture.Static,
            fixture.State,
            fixture.RetirementOwner);
        WorldEntity first = Entity(0x80A9B401u);
        pipeline.PublishLoaded(Result(Build(first)));
        long renderBegins = fixture.Render.Diagnostics.BeginCount;
        long physicsBegins = fixture.Physics.Diagnostics.BeginCount;
        long staticBegins = fixture.Static.Diagnostics.BeginCount;
        calls.Clear();
        LandblockStreamResult.Loaded changed = Result(Build(
            Entity(first.Id, sourceId: 0x01000002u)));

        Assert.Throws<InvalidOperationException>(() =>
            pipeline.PublishLoaded(changed));

        Assert.Empty(calls);
        Assert.Equal(renderBegins, fixture.Render.Diagnostics.BeginCount);
        Assert.Equal(physicsBegins, fixture.Physics.Diagnostics.BeginCount);
        Assert.Equal(staticBegins, fixture.Static.Diagnostics.BeginCount);
        Assert.True(pipeline.HasPendingPublication(changed));
    }

    [Fact]
    public void ConcreteConstructor_RequiresMatchingRetirementOwner()
    {
        ConcreteFixture fixture = Fixture(new List<string>());

        Assert.Throws<ArgumentNullException>(() =>
            new LandblockPresentationPipeline(
                fixture.Render,
                fixture.Physics,
                fixture.Static,
                fixture.State,
                retirementOwner: null!));

        var foreignRender = new LandblockRenderPublisher(
            static (_, _, _) => { },
            static _ => { },
            new CellVisibility(),
            fixture.State);
        var foreignOwner = new LandblockPresentationRetirementOwner(
            foreignRender,
            fixture.Physics,
            fixture.Static,
            fixture.Lighting,
            fixture.Translucency);
        Assert.Throws<ArgumentException>(() =>
            new LandblockPresentationPipeline(
                fixture.Render,
                fixture.Physics,
                fixture.Static,
                fixture.State,
                foreignOwner));

        Assert.Throws<ArgumentException>(() =>
            new LandblockPresentationRetirementOwner(
                fixture.Render,
                fixture.Physics,
                fixture.Static,
                new LightingHookSink(
                    new LightManager(),
                    new NullPoseSource()),
                fixture.Translucency));
        Assert.Throws<ArgumentException>(() =>
            new LandblockPresentationRetirementOwner(
                fixture.Render,
                fixture.Physics,
                fixture.Static,
                fixture.Lighting,
                new TranslucencyFadeManager()));

        Assert.Throws<ArgumentException>(() =>
            new LandblockPresentationPipeline(
                fixture.Render,
                fixture.Physics,
                fixture.Static,
                new GpuWorldState(),
                fixture.RetirementOwner));
    }

    [Fact]
    public void SameIdReapply_RealActivatorRebindsWithoutDefaultReplay()
    {
        ConcreteFixture fixture = Fixture(new List<string>());
        var pipeline = new LandblockPresentationPipeline(
            fixture.Render,
            fixture.Physics,
            fixture.Static,
            fixture.State,
            fixture.RetirementOwner);
        WorldEntity first = Entity(0x80A9B401u);
        WorldEntity replacement = Entity(first.Id);

        pipeline.PublishLoaded(Result(Build(first)));
        Assert.Equal(1, fixture.Runner.ActiveOwnerCount);

        pipeline.PublishLoaded(Result(Build(replacement)));

        Assert.Equal(1, fixture.Runner.ActiveOwnerCount);
        Assert.True(fixture.Poses.TryGetRootPose(first.Id, out _));
    }

    [Fact]
    public void OmittedStaticReapply_RealActivatorBalancesScriptAndPoseOwners()
    {
        ConcreteFixture fixture = Fixture(new List<string>());
        var pipeline = new LandblockPresentationPipeline(
            fixture.Render,
            fixture.Physics,
            fixture.Static,
            fixture.State,
            fixture.RetirementOwner);
        WorldEntity entity = Entity(0x80A9B401u);
        pipeline.PublishLoaded(Result(Build(entity)));
        Assert.Equal(1, fixture.Runner.ActiveOwnerCount);
        Assert.True(fixture.Poses.TryGetRootPose(entity.Id, out _));

        pipeline.PublishLoaded(Result(Build()));

        Assert.Equal(0, fixture.Runner.ActiveOwnerCount);
        Assert.False(fixture.Poses.TryGetRootPose(entity.Id, out _));
    }

    [Fact]
    public void FullRetirement_DetachesAndCleansStaticsWithoutMutatingLiveOwners()
    {
        var calls = new List<string>();
        ConcreteFixture fixture = Fixture(calls);
        var pipeline = Pipeline(fixture);
        WorldEntity staticEntity = Entity(0x80A9B401u);
        WorldEntity liveEntity = Entity(
            7u,
            serverGuid: 0x80000007u);
        pipeline.PublishLoaded(Result(Build(staticEntity, liveEntity)));
        fixture.Lighting.RegisterOwnedLight(new LightSource
        {
            OwnerId = staticEntity.Id,
            Kind = LightKind.Point,
            RankingOrigin = Vector3.Zero,
        });
        fixture.Lighting.RegisterOwnedLight(new LightSource
        {
            OwnerId = liveEntity.Id,
            Kind = LightKind.Point,
            RankingOrigin = Vector3.Zero,
        });
        fixture.Translucency.StartPartFade(staticEntity.Id, 0u, 0f, 1f, 2f);
        fixture.Translucency.StartPartFade(liveEntity.Id, 0u, 0f, 1f, 2f);
        calls.Clear();

        pipeline.BeginFullRetirement(LandblockId);

        Assert.False(fixture.State.IsLoaded(LandblockId));
        Assert.Equal(0, pipeline.PendingRetirementCount);
        Assert.Equal(0, fixture.Runner.ActiveOwnerCount);
        Assert.False(fixture.Poses.TryGetRootPose(staticEntity.Id, out _));
        Assert.Single(fixture.Lighting.GetOwnedLights(liveEntity.Id)!);
        Assert.Null(fixture.Lighting.GetOwnedLights(staticEntity.Id));
        Assert.False(fixture.Translucency.TryGetCurrentValue(
            staticEntity.Id,
            0u,
            out _));
        Assert.True(fixture.Translucency.TryGetCurrentValue(
            liveEntity.Id,
            0u,
            out _));
        Assert.Empty(fixture.World.Entities);
        Assert.Equal(1, fixture.Physics.Diagnostics.FullRemovalCount);
        Assert.Equal(0, fixture.Physics.Diagnostics.DemotionCount);
        Assert.Equal(1, fixture.Render.Diagnostics.TerrainRemovalCount);
        Assert.Equal(1, fixture.Render.Diagnostics.CellVisibilityRemovalCount);
        Assert.Equal(1, fixture.Render.Diagnostics.BuildingRegistryRemovalCount);
        Assert.Equal(1, fixture.Render.Diagnostics.EnvCellRemovalCount);
        Assert.Contains("unpin", calls);
        Assert.Contains("terrain-remove", calls);
        Assert.Contains("envcell-remove", calls);
    }

    [Fact]
    public void NearRetirement_KeepsTerrainAndLivePresentationButRetiresNearOwners()
    {
        var calls = new List<string>();
        ConcreteFixture fixture = Fixture(calls);
        var pipeline = Pipeline(fixture);
        WorldEntity staticEntity = Entity(0x80A9B401u);
        WorldEntity liveEntity = Entity(
            7u,
            serverGuid: 0x80000007u);
        pipeline.PublishLoaded(Result(Build(staticEntity, liveEntity)));
        fixture.Lighting.RegisterOwnedLight(new LightSource
        {
            OwnerId = staticEntity.Id,
            Kind = LightKind.Point,
            RankingOrigin = Vector3.Zero,
        });
        fixture.Lighting.RegisterOwnedLight(new LightSource
        {
            OwnerId = liveEntity.Id,
            Kind = LightKind.Point,
            RankingOrigin = Vector3.Zero,
        });
        fixture.Translucency.StartPartFade(staticEntity.Id, 0u, 0f, 1f, 2f);
        fixture.Translucency.StartPartFade(liveEntity.Id, 0u, 0f, 1f, 2f);
        calls.Clear();

        pipeline.BeginNearLayerRetirement(LandblockId);

        Assert.True(fixture.State.IsLoaded(LandblockId));
        Assert.False(fixture.State.IsNearTier(LandblockId));
        Assert.Single(fixture.State.Entities);
        Assert.Same(liveEntity, fixture.State.Entities[0]);
        Assert.Equal(0, fixture.Runner.ActiveOwnerCount);
        Assert.False(fixture.Poses.TryGetRootPose(staticEntity.Id, out _));
        Assert.Single(fixture.Lighting.GetOwnedLights(liveEntity.Id)!);
        Assert.Null(fixture.Lighting.GetOwnedLights(staticEntity.Id));
        Assert.False(fixture.Translucency.TryGetCurrentValue(
            staticEntity.Id,
            0u,
            out _));
        Assert.True(fixture.Translucency.TryGetCurrentValue(
            liveEntity.Id,
            0u,
            out _));
        Assert.Empty(fixture.World.Entities);
        Assert.Equal(0, fixture.Physics.Diagnostics.FullRemovalCount);
        Assert.Equal(1, fixture.Physics.Diagnostics.DemotionCount);
        Assert.Equal(0, fixture.Render.Diagnostics.TerrainRemovalCount);
        Assert.Equal(1, fixture.Render.Diagnostics.CellVisibilityRemovalCount);
        Assert.Equal(1, fixture.Render.Diagnostics.BuildingRegistryRemovalCount);
        Assert.Equal(1, fixture.Render.Diagnostics.EnvCellRemovalCount);
        Assert.DoesNotContain("terrain-remove", calls);
    }

    [Fact]
    public void FullRetirementFailure_RetriesOnlyUnfinishedConcreteStage()
    {
        int terrainAttempts = 0;
        var calls = new List<string>();
        ConcreteFixture fixture = Fixture(
            calls,
            removeTerrain: _ =>
            {
                terrainAttempts++;
                if (terrainAttempts == 1)
                    throw new InvalidOperationException("injected terrain removal failure");
            });
        var pipeline = Pipeline(fixture);
        pipeline.PublishLoaded(Result(Build(Entity(0x80A9B401u))));

        pipeline.BeginFullRetirement(LandblockId);

        Assert.False(fixture.State.IsLoaded(LandblockId));
        Assert.Equal(0, pipeline.PendingRetirementCount);
        Assert.Equal(2, terrainAttempts);
        Assert.Equal(1, fixture.Render.Diagnostics.TerrainRemovalCount);
        Assert.Equal(1, fixture.Physics.Diagnostics.FullRemovalCount);
        Assert.Equal(1, fixture.Render.Diagnostics.EnvCellRemovalCount);

        pipeline.AdvanceRetirements();

        Assert.Equal(0, pipeline.PendingRetirementCount);
        Assert.Equal(2, terrainAttempts);
        Assert.Equal(1, fixture.Render.Diagnostics.TerrainRemovalCount);
        Assert.Equal(1, fixture.Physics.Diagnostics.FullRemovalCount);
        Assert.Equal(1, fixture.Render.Diagnostics.EnvCellRemovalCount);
    }

    [Fact]
    public async Task FarLoaded_WorkerAndRealWalkPreserveTerrainBeyondNearRadiusWithoutNearPopulation()
    {
        ConcreteFixture fixture = Fixture(new List<string>());
        LandblockBuild far = Build() with
        {
            EnvCells = null,
            Origin = new LandblockBuildOrigin(0xA3, 0xB4),
            TerrainBounds = new(240.5f, 4.25f),
        };

        using var streamer = LandblockStreamer.CreateForRequests(
            loadLandblock: _ => far,
            buildMeshOrNull: (_, _) => EmptyMesh(),
            workerCount: 1);
        streamer.EnqueueLoad(new LandblockBuildRequest(
            LandblockId, LandblockStreamJobKind.LoadFar, Generation: 483, far.Origin));
        streamer.Start();
        LandblockStreamResult? completion = null;
        for (int attempt = 0; attempt < 200 && completion is null; attempt++)
        {
            IReadOnlyList<LandblockStreamResult> drained = streamer.DrainCompletions();
            if (drained.Count > 0)
                completion = Assert.Single(drained);
            else
                await Task.Delay(10);
        }
        var loaded = Assert.IsType<LandblockStreamResult.Loaded>(completion);
        Assert.Equal(483ul, loaded.Generation);
        Assert.Equal(far.Origin, loaded.Build.Origin);
        Assert.Equal(far.TerrainBounds, loaded.Build.TerrainBounds);
        Assert.Null(loaded.Build.EnvCells);
        Assert.Same(PhysicsDatBundle.Empty, loaded.Landblock.PhysicsDats);

        Pipeline(fixture).PublishLoaded(loaded);

        AssertFarTerrain(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedNearAsFar_BothOverloadsPreserveTerrainAndStripNearPopulation(bool metered)
    {
        var calls = new List<string>();
        ConcreteFixture fixture = Fixture(calls);
        var pipeline = Pipeline(fixture);
        LandblockBuild near = BuildWithWalkBuilding() with
        {
            Origin = new LandblockBuildOrigin(0xA3, 0xB4),
        };
        LandblockStreamResult.Loaded accepted = Result(near);

        if (metered)
        {
            // One mutation per frame forces the real retained publication path.
            var budget = new StreamingWorkBudget(
                TimeSpan.FromSeconds(1),
                maxCompletionAdmissions: 64,
                maxAdoptedCpuBytes: 1_000_000,
                maxEntityOperations: 1,
                maxGpuUploadBytes: 1_000_000,
                maxGlRetireOperations: 64,
                destinationReserveFraction: 0.75f);
            LandblockPublicationAdvance advance = default;
            for (int frame = 0; frame < 64 && !advance.Completed; frame++)
            {
                var meter = new StreamingWorkMeter(budget);
                advance = pipeline.PublishAsFar(
                    accepted, near, accepted.MeshData, meter, ensureProgress: false);
                meter.FinishFrame();
                Assert.True(meter.Snapshot.Used.EntityOperations <= 1);
            }
            Assert.True(advance.Completed);
        }
        else
        {
            pipeline.PublishAsFar(accepted, near, accepted.MeshData);
        }

        Assert.False(pipeline.HasPendingPublication(accepted));
        Assert.Equal(1, calls.Count(call => call == "terrain"));
        Assert.Equal(1, fixture.Render.Diagnostics.CompleteCount);
        AssertFarTerrain(fixture);
    }

    [Fact]
    public void ConcreteRetirement_DemotionKeepsTerrain_PromotionAndRevisitRestoreBuildings()
    {
        ConcreteFixture fixture = Fixture(new List<string>());
        var pipeline = Pipeline(fixture);
        LandblockBuild near = BuildWithWalkBuilding();
        WalkBuilding building = Assert.Single(near.EnvCells!.WalkBuildings).Building;

        pipeline.PublishLoaded(Result(near));
        AssertNearTerrain(fixture, building);

        pipeline.BeginNearLayerRetirement(LandblockId);
        pipeline.BeginNearLayerRetirement(LandblockId);
        Assert.Equal(0, pipeline.PendingRetirementCount);
        Assert.True(fixture.State.IsLoaded(LandblockId));
        Assert.False(fixture.State.IsNearTier(LandblockId));
        Assert.Equal(0, fixture.Render.Diagnostics.TerrainRemovalCount);
        fixture.Render.RemoveBuildingRegistry(LandblockId);
        AssertTerrainBounds(fixture.Render.WalkLandscape);
        Assert.NotEmpty(DrawTerrain(fixture, farViewer: false).TerrainTurns);
        Assert.Empty(fixture.Render.WalkBuildings.GetBuildings(LandblockId));
        Assert.False(fixture.Render.WalkBuildings.TryGetEntry(building, out _));
        Assert.All(PublishedBlock(fixture.Render.WalkLandscape).CellBuildings,
            value => Assert.Null(value));

        pipeline.PublishPromoted(
            new LandblockStreamResult.Promoted(LandblockId, near, EmptyMesh()),
            mergeIntoExistingLandblock: true);
        AssertNearTerrain(fixture, building);

        pipeline.BeginFullRetirement(LandblockId);
        pipeline.BeginFullRetirement(LandblockId);
        Assert.Equal(0, pipeline.PendingRetirementCount);
        Assert.False(fixture.State.IsLoaded(LandblockId));
        fixture.Render.RemoveTerrain(LandblockId);
        fixture.Render.RemoveBuildingRegistry(LandblockId); // late stage after terrain removal
        Assert.Empty(DrawTerrain(fixture, farViewer: false).TerrainTurns);
        Assert.All(fixture.Render.WalkLandscape.Landscape.Blocks, value => Assert.Null(value));
        Assert.Empty(fixture.Render.WalkBuildings.GetBuildings(LandblockId));

        pipeline.PublishLoaded(Result(near));
        AssertNearTerrain(fixture, building);
    }

    private static LandblockBuild BuildWithWalkBuilding()
    {
        var building = new WalkBuilding { PositionCellId = 0xA9B40009u };
        var entry = new WalkBuildingFactory.Entry(
            building, Matrix4x4.Identity, Matrix4x4.Identity);
        LandblockBuild build = Build(Entity(0x80A9B401u));
        return build with
        {
            EnvCells = new EnvCellLandblockBuild(
                LandblockId, [], [], [entry], walkMaxZ: 240.5f, walkMinZ: 4.25f),
            TerrainBounds = new(240.5f, 4.25f),
        };
    }

    private static void AssertFarTerrain(ConcreteFixture fixture)
    {
        TerrainTurnRecorder turns = DrawTerrain(fixture, farViewer: true);
        Assert.Equal((LandblockId & 0xFFFF0000u, 1, 0), Assert.Single(turns.TerrainTurns));
        AssertTerrainBounds(fixture.Render.WalkLandscape);
        Assert.Empty(turns.Buildings);
        Assert.Empty(fixture.Render.WalkBuildings.GetBuildings(LandblockId));
        Assert.All(PublishedBlock(fixture.Render.WalkLandscape).CellBuildings,
            value => Assert.Null(value));
        Assert.True(fixture.State.IsLoaded(LandblockId));
        Assert.False(fixture.State.IsNearTier(LandblockId));
        Assert.Empty(fixture.State.Entities);
        Assert.Empty(fixture.World.Entities);
    }

    private static void AssertNearTerrain(ConcreteFixture fixture, WalkBuilding building)
    {
        TerrainTurnRecorder turns = DrawTerrain(fixture, farViewer: false);
        Assert.NotEmpty(turns.TerrainTurns);
        Assert.Contains(building.PositionCellId, turns.Buildings);
        AssertTerrainBounds(fixture.Render.WalkLandscape);
        Assert.Same(building, PublishedBlock(fixture.Render.WalkLandscape).CellBuildings[8]);
        Assert.Same(building, Assert.Single(
            fixture.Render.WalkBuildings.GetBuildings(LandblockId)).Building);
        Assert.True(fixture.State.IsNearTier(LandblockId));
    }

    private static void AssertTerrainBounds(WalkLandscapeAssembler assembler)
    {
        WalkLandBlock block = PublishedBlock(assembler);
        Assert.Equal(240.5f, block.MaxZ);
        Assert.Equal(4.25f, block.MinZ);
    }

    private static WalkLandBlock PublishedBlock(WalkLandscapeAssembler assembler) =>
        Assert.Single(assembler.Landscape.Blocks.OfType<WalkLandBlock>());

    private static TerrainTurnRecorder DrawTerrain(ConcreteFixture fixture, bool farViewer)
    {
        // The far-only block is six blocks east of the viewer, beyond NearRadius4.
        uint cameraCell = farViewer ? 0xA3B40001u : 0xA9B40001u;
        var eye = new Vector3(12f, 12f, 100f);
        var target = new Vector3(farViewer ? 6 * 192f + 96f : 144f, 12f, 20f);
        Vector3 forward = Vector3.Normalize(target - eye);
        Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitZ)
            * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 16f / 9f, 0.1f, 10_000f);
        fixture.Render.WalkLandscape.SetViewer(cameraCell, eye);
        var context = new WalkProductionFrameContext(
            new CellVisibility(), fixture.Render.WalkBuildings,
            eye, forward, viewProjection, 1600f, 900f, cameraCell);
        var recorder = new TerrainTurnRecorder();
        new RetailFrameWalk().WalkFrame(
            cameraCell, null, fixture.Render.WalkLandscape.Landscape, context, recorder);
        return recorder;
    }

    private sealed class TerrainTurnRecorder : IWalkEventSink
    {
        public List<(uint LandblockId, int SideCellCount, int CellIndex)> TerrainTurns { get; } = [];
        public List<uint> Buildings { get; } = [];

        public void Emit(in WalkEvent walkEvent)
        {
            if (walkEvent.Kind == WalkEventKind.Building)
                Buildings.Add(walkEvent.CellId);
        }

        public void OnLandCellTurn(uint landblockId, int sideCellCount, int cellIndex) =>
            TerrainTurns.Add((landblockId, sideCellCount, cellIndex));
    }

    private static LandblockPresentationPipeline Pipeline(
        ConcreteFixture fixture) => new(
        fixture.Render,
        fixture.Physics,
        fixture.Static,
        fixture.State,
        fixture.RetirementOwner);

    private static ConcreteFixture Fixture(
        List<string> calls,
        Action<EnvCellLandblockBuild>? commitEnvCells = null,
        Action<uint>? removeTerrain = null,
        Action<uint>? removeEnvCells = null)
    {
        var poses = new EntityEffectPoseRegistry();
        var particleSink = new ParticleHookSink(
            new ParticleSystem(new EmitterDescRegistry()),
            poses);
        var script = new DatPhysicsScript();
        script.ScriptData.Add(new PhysicsScriptData
        {
            StartTime = 10.0,
            Hook = new CreateParticleHook { EmitterInfoId = 100u },
        });
        var runner = new PhysicsScriptRunner(
            id => id == 0xAAu ? script : null,
            new NullHookSink());
        var activator = new EntityScriptActivator(
            runner,
            particleSink,
            poses,
            _ => new ScriptActivationInfo(0xAAu, Array.Empty<Matrix4x4>()));
        var state = new GpuWorldState(
            new LandblockSpawnAdapter(new RecordingMeshAdapter(calls)),
            entityScriptActivator: activator);
        var entityObjects =
            new AcDream.Runtime.Entities.RuntimeEntityObjectLifetime(
                new PhysicsDataCache());
        PhysicsDataCache cache = entityObjects.Physics.DataCache;
        PhysicsEngine engine = entityObjects.Physics.Engine;
        var render = new LandblockRenderPublisher(
            (_, _, _) => calls.Add("terrain"),
            removeTerrain ?? (_ => calls.Add("terrain-remove")),
            new CellVisibility(),
            state,
            commitEnvCells: commitEnvCells,
            removeEnvCells: removeEnvCells ?? (_ => calls.Add("envcell-remove")));
        var physics = new LandblockPhysicsPublisher(
            entityObjects.Physics,
            new float[256]);
        var lights = new LightManager();
        var lighting = new LightingHookSink(
            lights,
            new NullPoseSource());
        var translucency = new TranslucencyFadeManager();
        var world = new WorldGameState();
        var events = new WorldEvents();
        var staticPresentation = new LandblockStaticPresentationPublisher(
            lighting,
            translucency,
            world,
            events);
        var retirementOwner = new LandblockPresentationRetirementOwner(
            render,
            physics,
            staticPresentation,
            lighting,
            translucency);
        return new ConcreteFixture(
            state,
            engine,
            render,
            physics,
            staticPresentation,
            events,
            retirementOwner,
            runner,
            poses,
            lights,
            lighting,
            translucency,
            world);
    }

    private static LandblockStreamResult.Loaded Result(LandblockBuild build) =>
        new(
            LandblockId,
            LandblockStreamTier.Near,
            build,
            EmptyMesh());

    private static LandblockBuild Build(params WorldEntity[] entities)
    {
        uint cellId = (LandblockId & 0xFFFF0000u) | 0x0100u;
        var shell = new EnvCellShellPlacement(
            cellId,
            0x2_0000_0001UL,
            0x0D000001u,
            1,
            ImmutableArray<ushort>.Empty,
            Vector3.Zero,
            Quaternion.Identity,
            Matrix4x4.Identity,
            new WbBoundingBox(Vector3.Zero, Vector3.One),
            new WbBoundingBox(Vector3.Zero, Vector3.One));
        var envCells = new EnvCellLandblockBuild(
            LandblockId,
            Array.Empty<LoadedCell>(),
            [shell]);
        return new LandblockBuild(
            new LoadedLandblock(
                LandblockId,
                new LandBlock
                {
                    Terrain = new TerrainInfo[81],
                    Height = new byte[81],
                },
                entities,
                PhysicsDatBundle.Empty),
            envCells,
            new LandblockBuildOrigin(0xA9, 0xB4));
    }

    private static WorldEntity Entity(
        uint id,
        uint sourceId = 0x01000001u,
        uint serverGuid = 0u) => new()
    {
        Id = id,
        ServerGuid = serverGuid,
        SourceGfxObjOrSetupId = sourceId,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static LandblockMeshData EmptyMesh() => new(
        Array.Empty<TerrainVertex>(),
        Array.Empty<uint>());

    private sealed record ConcreteFixture(
        GpuWorldState State,
        PhysicsEngine Engine,
        LandblockRenderPublisher Render,
        LandblockPhysicsPublisher Physics,
        LandblockStaticPresentationPublisher Static,
        WorldEvents Events,
        LandblockPresentationRetirementOwner RetirementOwner,
        PhysicsScriptRunner Runner,
        EntityEffectPoseRegistry Poses,
        LightManager Lights,
        LightingHookSink Lighting,
        TranslucencyFadeManager Translucency,
        WorldGameState World);

    private sealed class NullHookSink : IAnimationHookSink
    {
        public void OnHook(uint entityId, Vector3 worldPosition, AnimationHook hook)
        {
        }
    }

    private sealed class RecordingMeshAdapter(List<string> calls) : IWbMeshAdapter
    {
        public void IncrementRefCount(ulong id)
        {
        }

        public void PinPreparedRenderData(ulong id) => calls.Add("pin");

        public void DecrementRefCount(ulong id) => calls.Add("unpin");
    }

    private sealed class NullPoseSource : IEntityEffectPoseSource
    {
        public bool TryGetRootPose(uint localEntityId, out Matrix4x4 rootWorld)
        {
            rootWorld = default;
            return false;
        }

        public bool TryGetPartPose(
            uint localEntityId,
            int partIndex,
            out Matrix4x4 partLocal)
        {
            partLocal = default;
            return false;
        }
    }
}
