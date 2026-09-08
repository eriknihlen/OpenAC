using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.Core.Physics;
using AcDream.Core.Terrain;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class StreamingWorkBudgetTests
{
    [Fact]
    public void DefaultProfile_UsesElapsedTimeAsTheMicroOperationGuard()
    {
        Assert.Equal(
            4_096,
            StreamingWorkBudgetOptions.Default.MaxEntityOperations);
        Assert.Equal(
            2.0,
            StreamingWorkBudgetOptions.Default.MaxUpdateMilliseconds);
    }

    private static StreamingWorkBudget Budget(
        double milliseconds = 2,
        int completions = 4,
        long cpuBytes = 100,
        int entityOperations = 8,
        long gpuBytes = 100,
        int retireOperations = 4) => new(
            TimeSpan.FromMilliseconds(milliseconds),
            completions,
            cpuBytes,
            entityOperations,
            gpuBytes,
            retireOperations,
            0.75f);

    [Fact]
    public void MeterYieldsBeforeSecondOperationThatExceedsAnyDimension()
    {
        long now = 0;
        var meter = new StreamingWorkMeter(
            Budget(),
            () => now,
            timestampFrequency: 1_000);

        Assert.Equal(
            StreamingWorkAdmission.Admitted,
            meter.TryReserve(
                new StreamingWorkCost(
                    CompletionAdmissions: 2,
                    AdoptedCpuBytes: 60,
                    EntityOperations: 3,
                    GpuUploadBytes: 40,
                    GlRetireOperations: 1),
                "first"));
        meter.Complete();

        Assert.Equal(
            StreamingWorkAdmission.Yielded,
            meter.TryReserve(
                new StreamingWorkCost(AdoptedCpuBytes: 41),
                "second"));

        StreamingWorkMeterSnapshot snapshot = meter.Snapshot;
        Assert.Equal(1, snapshot.Operations);
        Assert.Equal(1, snapshot.CompletedOperations);
        Assert.Equal(1, snapshot.YieldCount);
        Assert.Equal(StreamingWorkLimit.AdoptedCpuBytes, snapshot.LastLimit);
        Assert.Equal("second", snapshot.LastStage);
    }

    [Fact]
    public void FirstOversizedOperationProgressesAndNamesTheOversize()
    {
        var meter = new StreamingWorkMeter(
            Budget(completions: 1),
            static () => 0,
            timestampFrequency: 1_000);

        Assert.Equal(
            StreamingWorkAdmission.OversizedProgress,
            meter.TryReserve(
                new StreamingWorkCost(CompletionAdmissions: 2),
                "oversized"));
        meter.Complete();

        StreamingWorkMeterSnapshot snapshot = meter.Snapshot;
        Assert.Equal(1, snapshot.OversizedProgressCount);
        Assert.Equal(
            StreamingWorkLimit.CompletionAdmissions,
            snapshot.LastLimit);
    }

    [Fact]
    public void EnsureProgressCanCrossTheLimitOnlyOncePerFrame()
    {
        var meter = new StreamingWorkMeter(
            Budget(cpuBytes: 1),
            static () => 0,
            timestampFrequency: 1_000);

        Assert.Equal(
            StreamingWorkAdmission.Admitted,
            meter.TryReserve(
                new StreamingWorkCost(CompletionAdmissions: 1),
                "admission"));
        meter.Complete();
        Assert.Equal(
            StreamingWorkAdmission.Admitted,
            meter.TryReserve(
                new StreamingWorkCost(AdoptedCpuBytes: 1),
                "first-execution",
                ensureProgress: true));
        meter.Complete();

        Assert.Equal(
            StreamingWorkAdmission.Yielded,
            meter.TryReserve(
                new StreamingWorkCost(AdoptedCpuBytes: 1),
                "second-execution",
                ensureProgress: true));
        Assert.Equal(1, meter.Snapshot.YieldCount);
        Assert.Equal(
            StreamingWorkLimit.AdoptedCpuBytes,
            meter.Snapshot.LastLimit);
    }

    [Fact]
    public void ActiveReveal_ProtectsDestinationShareAcrossEveryCountAndByteDimension()
    {
        var meter = new StreamingWorkMeter(
            Budget(
                milliseconds: 100,
                completions: 4,
                cpuBytes: 100,
                entityOperations: 8,
                gpuBytes: 100,
                retireOperations: 4),
            static () => 0,
            timestampFrequency: 1_000,
            destinationReservationActive: true);

        StreamingWorkCost unreservedShare = new(
            CompletionAdmissions: 1,
            AdoptedCpuBytes: 25,
            EntityOperations: 2,
            GpuUploadBytes: 25,
            GlRetireOperations: 1);
        Assert.Equal(
            StreamingWorkAdmission.Admitted,
            meter.TryReserve(unreservedShare, "ordinary"));
        meter.Complete();
        Assert.Equal(
            StreamingWorkAdmission.Yielded,
            meter.TryReserve(
                new StreamingWorkCost(CompletionAdmissions: 1),
                "ordinary-over-reserve"));

        using (meter.EnterLane(StreamingWorkLane.Destination))
        {
            Assert.Equal(
                StreamingWorkAdmission.Admitted,
                meter.TryReserve(
                    new StreamingWorkCost(
                        CompletionAdmissions: 3,
                        AdoptedCpuBytes: 75,
                        EntityOperations: 6,
                        GpuUploadBytes: 75,
                        GlRetireOperations: 3),
                    "destination"));
            meter.Complete();
        }

        StreamingWorkMeterSnapshot snapshot = meter.Snapshot;
        Assert.Equal(unreservedShare, snapshot.NonDestinationUsed);
        Assert.Equal(3, snapshot.DestinationUsed.CompletionAdmissions);
        Assert.Equal(100, snapshot.Used.AdoptedCpuBytes);
        Assert.Equal(100, snapshot.Used.GpuUploadBytes);
    }

    [Fact]
    public void InactiveReveal_DoesNotApplyDestinationReservation()
    {
        var meter = new StreamingWorkMeter(
            Budget(completions: 4),
            static () => 0,
            timestampFrequency: 1_000,
            destinationReservationActive: false);

        Assert.Equal(
            StreamingWorkAdmission.Admitted,
            meter.TryReserve(
                new StreamingWorkCost(CompletionAdmissions: 4),
                "ordinary"));
        meter.Complete();

        Assert.Equal(4, meter.Snapshot.NonDestinationUsed.CompletionAdmissions);
        Assert.Equal(0, meter.Snapshot.DestinationUsed.CompletionAdmissions);
    }

    [Fact]
    public void ActiveReveal_ProtectsDestinationWallClockAfterOrdinaryShareIsSpent()
    {
        long now = 0;
        var meter = new StreamingWorkMeter(
            Budget(milliseconds: 100),
            () => now,
            timestampFrequency: 1_000,
            destinationReservationActive: true);

        Assert.Equal(
            StreamingWorkAdmission.Admitted,
            meter.TryReserve(default, "ordinary"));
        now = 25;
        meter.Complete();
        Assert.Equal(
            StreamingWorkAdmission.Yielded,
            meter.TryReserve(default, "ordinary-after-25ms"));

        using (meter.EnterLane(StreamingWorkLane.Destination))
        {
            Assert.Equal(
                StreamingWorkAdmission.Admitted,
                meter.TryReserve(default, "destination"));
            now = 99;
            meter.Complete();
        }

        Assert.Equal(2, meter.Snapshot.CompletedOperations);
    }

    [Fact]
    public void WidenForDestinationHold_ScalesEveryDimensionAndReservesTheAuthoredNonDestinationShare()
    {
        StreamingWorkBudget widened = StreamingWorkBudgetOptions.Default
            .ToBudget()
            .WidenForDestinationHold(8.0);

        Assert.Equal(TimeSpan.FromMilliseconds(8), widened.MaxUpdateTime);
        Assert.Equal(256, widened.MaxCompletionAdmissions);
        Assert.Equal(
            32 * StreamingWorkBudgetOptions.MiB,
            widened.MaxAdoptedCpuBytes);
        Assert.Equal(16_384, widened.MaxEntityOperations);
        Assert.Equal(
            32 * StreamingWorkBudgetOptions.MiB,
            widened.MaxGpuUploadBytes);
        Assert.Equal(256, widened.MaxGlRetireOperations);
        Assert.Equal(0.9375f, widened.DestinationReserveFraction);
    }

    [Fact]
    public void WidenForDestinationHold_IsANoOpAtOrBelowTheCurrentCeiling()
    {
        StreamingWorkBudget authored =
            StreamingWorkBudgetOptions.Default.ToBudget();

        Assert.Equal(authored, authored.WidenForDestinationHold(2.0));
        Assert.Equal(authored, authored.WidenForDestinationHold(1.0));
        Assert.Equal(authored, authored.WidenForDestinationHold(0.0));
        Assert.Equal(authored, authored.WidenForDestinationHold(double.NaN));
    }

    [Fact]
    public void WidenForDestinationHold_DoesNotWidenTheNonDestinationLane()
    {
        StreamingWorkBudget authored =
            StreamingWorkBudgetOptions.Default.ToBudget();
        foreach (StreamingWorkBudget budget in new[]
        {
            authored,
            authored.WidenForDestinationHold(8.0),
        })
        {
            var meter = new StreamingWorkMeter(
                budget,
                static () => 0,
                timestampFrequency: 1_000,
                destinationReservationActive: true);

            Assert.Equal(
                StreamingWorkAdmission.Admitted,
                meter.TryReserve(
                    new StreamingWorkCost(CompletionAdmissions: 16),
                    "ordinary"));
            meter.Complete();
            Assert.Equal(
                StreamingWorkAdmission.Yielded,
                meter.TryReserve(
                    new StreamingWorkCost(CompletionAdmissions: 1),
                    "ordinary-over-share"));
            Assert.Equal(
                StreamingWorkLimit.CompletionAdmissions,
                meter.Snapshot.LastLimit);
        }

        // The destination lane of the widened profile owns the widened
        // remainder: 240 more admissions on top of the ordinary 16.
        var widenedMeter = new StreamingWorkMeter(
            authored.WidenForDestinationHold(8.0),
            static () => 0,
            timestampFrequency: 1_000,
            destinationReservationActive: true);
        Assert.Equal(
            StreamingWorkAdmission.Admitted,
            widenedMeter.TryReserve(
                new StreamingWorkCost(CompletionAdmissions: 16),
                "ordinary"));
        widenedMeter.Complete();
        using (widenedMeter.EnterLane(StreamingWorkLane.Destination))
        {
            Assert.Equal(
                StreamingWorkAdmission.Admitted,
                widenedMeter.TryReserve(
                    new StreamingWorkCost(CompletionAdmissions: 240),
                    "destination"));
            widenedMeter.Complete();
            Assert.Equal(
                StreamingWorkAdmission.Yielded,
                widenedMeter.TryReserve(
                    new StreamingWorkCost(CompletionAdmissions: 1),
                    "destination-over-widened-total"));
        }
    }

    [Fact]
    public void MeterUsesInjectedMonotonicClockAndRecordsOverrunAndFailure()
    {
        long now = 0;
        var meter = new StreamingWorkMeter(
            Budget(milliseconds: 2),
            () => now,
            timestampFrequency: 1_000);

        Assert.Equal(
            StreamingWorkAdmission.Admitted,
            meter.TryReserve(new StreamingWorkCost(), "slow"));
        now = 3;
        meter.Fail();

        StreamingWorkMeterSnapshot snapshot = meter.Snapshot;
        Assert.Equal(3, snapshot.ElapsedMilliseconds);
        Assert.Equal(1, snapshot.OverrunCount);
        Assert.Equal(1, snapshot.FailureCount);
        Assert.Equal(0, snapshot.CompletedOperations);
        Assert.Equal(3, snapshot.MaximumOperationMilliseconds);
        Assert.Equal("slow", snapshot.MaximumOperationStage);
        Assert.Equal("slow", snapshot.LastStage);
        Assert.Equal(StreamingWorkLimit.Time, snapshot.LastLimit);
    }

    [Fact]
    public void MeterRejectsReentrantReservationAndNegativeCost()
    {
        var meter = new StreamingWorkMeter(
            Budget(),
            static () => 0,
            timestampFrequency: 1_000);

        meter.TryReserve(new StreamingWorkCost(), "first");
        Assert.Throws<InvalidOperationException>(() =>
            meter.TryReserve(new StreamingWorkCost(), "reentrant"));
        meter.Complete();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            meter.TryReserve(
                new StreamingWorkCost(AdoptedCpuBytes: -1),
                "negative"));
        Assert.Throws<ArgumentException>(() =>
            new StreamingWorkMeter(
                default,
                static () => 0,
                timestampFrequency: 1_000));
    }

    [Theory]
    [InlineData(2, 1.0, 2, 50, 4, 50, 2)]
    [InlineData(4, 2.0, 4, 100, 8, 100, 4)]
    [InlineData(6, 3.0, 6, 150, 12, 150, 6)]
    public void LegacyCompletionSelectorScalesTheWholeProfile(
        int count,
        double milliseconds,
        int admissions,
        long cpuBytes,
        int entityOperations,
        long gpuBytes,
        int retireOperations)
    {
        var options = new StreamingWorkBudgetOptions(
            MaxUpdateMilliseconds: 2,
            MaxCompletionAdmissions: 4,
            MaxAdoptedCpuBytes: 100,
            MaxEntityOperations: 8,
            MaxGpuUploadBytes: 100,
            MaxGlRetireOperations: 4,
            DestinationReserveFraction: 0.75f);

        StreamingWorkBudgetOptions scaled =
            options.ScaleForLegacyCompletionCount(count);

        Assert.Equal(milliseconds, scaled.MaxUpdateMilliseconds);
        Assert.Equal(admissions, scaled.MaxCompletionAdmissions);
        Assert.Equal(cpuBytes, scaled.MaxAdoptedCpuBytes);
        Assert.Equal(entityOperations, scaled.MaxEntityOperations);
        Assert.Equal(gpuBytes, scaled.MaxGpuUploadBytes);
        Assert.Equal(retireOperations, scaled.MaxGlRetireOperations);
        Assert.Equal(0.75f, scaled.DestinationReserveFraction);
    }

    [Fact]
    public void LegacyCompletionSelectorRejectsZeroAndSaturatesHugeProfiles()
    {
        var options = new StreamingWorkBudgetOptions(
            MaxUpdateMilliseconds: 2,
            MaxCompletionAdmissions: int.MaxValue,
            MaxAdoptedCpuBytes: long.MaxValue,
            MaxEntityOperations: int.MaxValue,
            MaxGpuUploadBytes: long.MaxValue,
            MaxGlRetireOperations: int.MaxValue,
            DestinationReserveFraction: 0.75f);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            options.ScaleForLegacyCompletionCount(0));

        StreamingWorkBudgetOptions scaled =
            options.ScaleForLegacyCompletionCount(int.MaxValue);
        Assert.Equal(int.MaxValue, scaled.MaxCompletionAdmissions);
        Assert.Equal(long.MaxValue, scaled.MaxAdoptedCpuBytes);
        Assert.Equal(int.MaxValue, scaled.MaxEntityOperations);
        Assert.Equal(long.MaxValue, scaled.MaxGpuUploadBytes);
        Assert.Equal(int.MaxValue, scaled.MaxGlRetireOperations);
    }

    [Fact]
    public void CompletionCostChargesExactArraysAndDeterministicLogicalEntries()
    {
        const uint landblockId = 0xA9B4FFFFu;
        var first = Entity(1, meshRefs: 2);
        var second = Entity(2, meshRefs: 1);
        var cell = new LoadedCell
        {
            CellId = 0xA9B40100u,
            Portals =
            [
                new CellPortalInfo(1, 2, 3, 4),
                new CellPortalInfo(5, 6, 7, 8),
            ],
            ClipPlanes = [new PortalClipPlane(), new PortalClipPlane()],
            PortalPolygons =
            [
                new Vector3[3],
                new Vector3[4],
            ],
            VisibleCells = new uint[3],
        };
        var shell = new EnvCellShellPlacement(
            cell.CellId,
            1,
            2,
            3,
            ImmutableArray.Create<ushort>(4, 5, 6),
            Vector3.Zero,
            Quaternion.Identity,
            Matrix4x4.Identity,
            new WbBoundingBox(Vector3.Zero, Vector3.One),
            new WbBoundingBox(Vector3.Zero, Vector3.One));
        var physics = new PhysicsDatBundle(
            new LandBlockInfo(),
            new Dictionary<uint, EnvCell> { [1] = new EnvCell() },
            new Dictionary<uint, DatReaderWriter.DBObjs.Environment>
            {
                [2] = new DatReaderWriter.DBObjs.Environment(),
            },
            new Dictionary<uint, Setup> { [3] = new Setup() },
            new Dictionary<uint, GfxObj> { [4] = new GfxObj() });
        var build = new LandblockBuild(
            new LoadedLandblock(
                landblockId,
                new LandBlock(),
                [first, second],
                physics),
            new EnvCellLandblockBuild(landblockId, [cell], [shell]));
        var mesh = new LandblockMeshData(
            new TerrainVertex[2],
            new uint[3]);

        LandblockStreamCostEstimate estimate =
            LandblockStreamResultCost.Estimate(build, mesh);

        Assert.Equal(92, estimate.TerrainPayloadBytes);
        // MeshRef is 80 bytes on the supported x64 runtime: the Matrix4x4
        // retains its 16-byte alignment after the 4-byte GfxObj id.
        Assert.Equal(586, estimate.Work.AdoptedCpuBytes);
        Assert.Equal(1, estimate.Work.CompletionAdmissions);
        Assert.Equal(2, estimate.Work.EntityOperations);
        Assert.Equal(92, estimate.Work.GpuUploadBytes);
        Assert.Equal(3, estimate.MeshReferences);
        Assert.Equal(1, estimate.VisibilityCells);
        Assert.Equal(1, estimate.EnvCellShells);
        Assert.Equal(2, estimate.PortalConnections);
        Assert.Equal(7, estimate.PortalPolygonVertices);
        Assert.Equal(1, estimate.PhysicsEnvCells);
        Assert.Equal(1, estimate.PhysicsEnvironments);
        Assert.Equal(1, estimate.PhysicsSetups);
        Assert.Equal(1, estimate.PhysicsGfxObjects);
    }

    [Fact]
    public void NonPayloadCompletionCostsOnlyOneAdmission()
    {
        LandblockStreamCostEstimate estimate =
            LandblockStreamResultCost.Estimate(
                new LandblockStreamResult.Failed(0x1234FFFFu, "expected"));

        Assert.Equal(
            new StreamingWorkCost(CompletionAdmissions: 1),
            estimate.Work);
        Assert.Equal(0, estimate.TerrainPayloadBytes);
    }

    [Fact]
    public void LegacySchedulerPublishesShadowBudgetAndRetainedBacklogFacts()
    {
        uint center = StreamingRegion.EncodeLandblockIdForTest(32, 32);
        uint neighbor = StreamingRegion.EncodeLandblockIdForTest(32, 33);
        var outbox = new Queue<LandblockStreamResult>(
        [
            Loaded(center),
            Loaded(neighbor),
        ]);
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: max =>
            {
                var drained = new List<LandblockStreamResult>(max);
                while (drained.Count < max && outbox.TryDequeue(out var result))
                    drained.Add(result);
                return drained;
            },
            applyTerrain: static (_, _) => { },
            state: new GpuWorldState(),
            nearRadius: 1,
            farRadius: 1,
            workBudgetOptions: new StreamingWorkBudgetOptions(
                MaxUpdateMilliseconds: 100,
                MaxCompletionAdmissions: 2,
                MaxAdoptedCpuBytes: 1_000,
                MaxEntityOperations: 100,
                MaxGpuUploadBytes: 44,
                MaxGlRetireOperations: 100,
                DestinationReserveFraction: 0.75f));

        controller.Tick(32, 32);

        StreamingWorkDiagnostics first = controller.WorkDiagnostics;
        Assert.Equal(1, first.DeferredCompletions);
        Assert.Equal(44, first.DeferredAdoptedCpuBytes);
        Assert.True(first.OldestDeferredAgeMilliseconds >= 0);
        Assert.Equal(6, first.LastFrame.Operations);
        Assert.Equal(6, first.LastFrame.CompletedOperations);
        Assert.Equal(1, first.LastFrame.YieldCount);
        Assert.Equal(88, first.LastFrame.Used.AdoptedCpuBytes);

        controller.Tick(32, 32);

        StreamingWorkDiagnostics second = controller.WorkDiagnostics;
        Assert.Equal(0, second.DeferredCompletions);
        Assert.Equal(0, second.DeferredAdoptedCpuBytes);
        Assert.Equal(0, second.OldestDeferredAgeMilliseconds);
        Assert.Equal(4, second.LastFrame.CompletedOperations);
    }

    [Fact]
    public void ControllerPublishesLifetimeOversizeAndMaximumOperationEvidence()
    {
        uint center = StreamingRegion.EncodeLandblockIdForTest(32, 32);
        var source = new QueueCompletionSource(Loaded(center));
        StreamingController controller = Controller(
            source,
            static () => { },
            WorkOptions(
                admissions: 4,
                cpuBytes: 1_000,
                gpuBytes: 1));

        controller.Tick(32, 32);

        StreamingWorkDiagnostics diagnostics = controller.WorkDiagnostics;
        Assert.True(diagnostics.LifetimeOversizedProgressCount >= 1);
        Assert.True(diagnostics.MaximumOperationMilliseconds >= 0);
        Assert.NotNull(diagnostics.MaximumOperationStage);
        Assert.True(diagnostics.MaximumFrameMilliseconds >=
            diagnostics.MaximumOperationMilliseconds);
    }

    [Fact]
    public void ControllerBoundsProductionAdmissionByCountBeforeAdoption()
    {
        uint center = StreamingRegion.EncodeLandblockIdForTest(32, 32);
        var source = new QueueCompletionSource(
            Loaded(center),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(32, 33)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(32, 34)));
        int applied = 0;
        StreamingController controller = Controller(
            source,
            () => applied++,
            WorkOptions(admissions: 2, cpuBytes: 1_000));

        controller.Tick(32, 32);

        Assert.Equal(2, applied);
        Assert.Equal(1, source.BacklogCount);
        Assert.Equal(1, controller.WorkDiagnostics.WorkerCompletionBacklog);
        Assert.Equal(2, controller.WorkDiagnostics.LastFrame.Used.CompletionAdmissions);
        Assert.Equal(0, controller.WorkDiagnostics.DeferredCompletions);
    }

    [Fact]
    public void ControllerBoundsProductionAdmissionByRetainedCpuBytes()
    {
        uint center = StreamingRegion.EncodeLandblockIdForTest(32, 32);
        var source = new QueueCompletionSource(
            Loaded(center),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(32, 33)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(32, 34)));
        int applied = 0;
        StreamingController controller = Controller(
            source,
            () => applied++,
            WorkOptions(admissions: 64, cpuBytes: 44));

        controller.Tick(32, 32);

        Assert.Equal(1, applied);
        Assert.Equal(2, source.BacklogCount);
        Assert.Equal(44, controller.WorkDiagnostics.LastFrame.Used.AdoptedCpuBytes);
        Assert.Equal(
            StreamingWorkLimit.AdoptedCpuBytes,
            controller.WorkDiagnostics.LastFrame.LastLimit);
    }

    [Fact]
    public void DestinationAndEmptyUnloadPriorityNeverBypassPublicationBudget()
    {
        uint center = StreamingRegion.EncodeLandblockIdForTest(32, 32);
        uint ordinary = StreamingRegion.EncodeLandblockIdForTest(33, 33);
        var source = new QueueCompletionSource(
            Loaded(ordinary),
            Loaded(center),
            new LandblockStreamResult.Unloaded(0x1111FFFFu),
            new LandblockStreamResult.Unloaded(0x2222FFFFu));
        var applied = new List<uint>();
        StreamingController controller = Controller(
            source,
            id => applied.Add(id),
            WorkOptions(
                admissions: 4,
                cpuBytes: 1_000,
                gpuBytes: 44,
                retireOperations: 1));
        controller.BeginDestinationReservation(1, center, 0);

        controller.Tick(32, 32);

        Assert.Equal([center], applied);
        Assert.Equal(1, controller.WorkDiagnostics.DeferredCompletions);
        Assert.Equal(1, controller.WorkDiagnostics.LastFrame.YieldCount);
        Assert.Equal(44, controller.WorkDiagnostics.LastFrame.Used.GpuUploadBytes);
        Assert.Equal(0, controller.WorkDiagnostics.LastFrame.Used.GlRetireOperations);

        controller.Tick(32, 32);

        Assert.Equal([center], applied);
        Assert.Equal(1, controller.WorkDiagnostics.DeferredCompletions);
        Assert.Equal(0, controller.WorkDiagnostics.LastFrame.Used.GlRetireOperations);

        controller.EndDestinationReservation(1);
        for (int i = 0;
             i < 4
             && (controller.WorkDiagnostics.DeferredCompletions != 0
                 || source.BacklogCount != 0);
             i++)
        {
            controller.Tick(32, 32);
        }

        Assert.Equal(0, controller.WorkDiagnostics.DeferredCompletions);
        Assert.Equal(0, source.BacklogCount);
    }

    [Fact]
    public void ActiveHold_WidensTheDestinationLaneAndRevertsWhenTheReservationEnds()
    {
        uint center = StreamingRegion.EncodeLandblockIdForTest(32, 32);
        var source = new QueueCompletionSource(
            Loaded(center),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(32, 33)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(33, 32)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(33, 33)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(32, 31)));
        int applied = 0;
        StreamingController controller = Controller(
            source,
            () => applied++,
            WorkOptions(admissions: 2, cpuBytes: 1_000) with
            {
                HoldDestinationCeilingMilliseconds = 200,
            });
        controller.BeginDestinationReservation(1, center, 1);

        controller.Tick(32, 32);

        Assert.Equal(4, applied);
        Assert.Equal(
            4,
            controller.WorkDiagnostics.LastFrame.Used.CompletionAdmissions);
        Assert.Equal(1, source.BacklogCount);

        // The moment the reservation ends the authored profile is back:
        // the same backlog now admits 2 per frame, not 4.
        controller.EndDestinationReservation(1);
        source.Enqueue(
            Loaded(StreamingRegion.EncodeLandblockIdForTest(33, 31)));
        source.Enqueue(
            Loaded(StreamingRegion.EncodeLandblockIdForTest(31, 32)));

        controller.Tick(32, 32);

        Assert.Equal(6, applied);
        Assert.Equal(
            2,
            controller.WorkDiagnostics.LastFrame.Used.CompletionAdmissions);
        Assert.Equal(1, source.BacklogCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoldWidening_AppliesToLoginAndPortalRevealsAlikeAndRevertsAtEnd(
        bool portalKind)
    {
        uint center = StreamingRegion.EncodeLandblockIdForTest(32, 32);
        const uint destinationCell = 0x20200021u;
        var source = new QueueCompletionSource(
            Loaded(center),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(32, 33)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(33, 32)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(33, 33)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(32, 31)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(33, 31)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(31, 32)));
        int applied = 0;
        StreamingController controller = Controller(
            source,
            () => applied++,
            WorkOptions(admissions: 2, cpuBytes: 1_000) with
            {
                HoldDestinationCeilingMilliseconds = 200,
            });
        var transit = new RuntimeWorldTransitState(null);
        var coordinator = new WorldRevealCoordinator(
            transit,
            revealWindow: static () => new StreamingRevealWindow(1, 1),
            isRenderNeighborhoodReady: static (_, _, _) => false,
            isSpawnCellReady: static _ => false,
            isTerrainNeighborhoodReady: static (_, _) => false,
            areCompositeTexturesReady: static () => false,
            prepareCompositeTextures: static (_, _) => { },
            invalidateCompositeTextures: static () => { },
            isSpawnClaimUnhydratable: static _ => false,
            streaming: controller);

        if (portalKind)
        {
            Assert.True(transit.TryQueueTeleportStart(1));
            Assert.True(transit.ActivateQueuedTeleport());
            Assert.True(transit.OfferTeleportDestination(
                new RuntimeTeleportDestination(
                    EntityGuid: 0x50000001u,
                    InstanceSequence: 1,
                    PositionSequence: 1,
                    TeleportSequence: 1,
                    ForcePositionSequence: 1,
                    Position: new Position(
                        destinationCell,
                        Vector3.Zero,
                        Quaternion.Identity)),
                teleportTimestampAdvanced: true));
            Assert.True(coordinator.TryBeginPortal(
                1,
                destinationCell,
                out _));
            Assert.Equal(
                RuntimePortalKind.Portal,
                coordinator.Snapshot.Kind);
        }
        else
        {
            coordinator.BeginLogin(destinationCell);
            Assert.Equal(
                RuntimePortalKind.Login,
                coordinator.Snapshot.Kind);
        }

        controller.Tick(32, 32);

        Assert.Equal(4, applied);
        Assert.Equal(
            4,
            controller.WorkDiagnostics.LastFrame.Used.CompletionAdmissions);

        // Releasing the reveal releases the reservation; the very next frame
        // runs the authored profile again.
        coordinator.Cancel();
        controller.Tick(32, 32);

        Assert.Equal(6, applied);
        Assert.Equal(
            2,
            controller.WorkDiagnostics.LastFrame.Used.CompletionAdmissions);
    }

    [Fact]
    public void NoReservation_UsesTheAuthoredBudgetUnchanged()
    {
        uint center = StreamingRegion.EncodeLandblockIdForTest(32, 32);
        var source = new QueueCompletionSource(
            Loaded(center),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(32, 33)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(33, 32)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(33, 33)),
            Loaded(StreamingRegion.EncodeLandblockIdForTest(32, 31)));
        int applied = 0;
        StreamingController controller = Controller(
            source,
            () => applied++,
            WorkOptions(admissions: 2, cpuBytes: 1_000) with
            {
                HoldDestinationCeilingMilliseconds = 200,
            });

        controller.Tick(32, 32);

        Assert.Equal(2, applied);
        Assert.Equal(
            2,
            controller.WorkDiagnostics.LastFrame.Used.CompletionAdmissions);
        Assert.Equal(3, source.BacklogCount);
    }

    [Fact]
    public void IncompleteReveal_DoesNotStarveOrdinaryWorkBeforeDestinationArrives()
    {
        uint center = StreamingRegion.EncodeLandblockIdForTest(32, 32);
        uint ordinary = StreamingRegion.EncodeLandblockIdForTest(33, 33);
        var source = new QueueCompletionSource(Loaded(ordinary));
        var applied = new List<uint>();
        StreamingController controller = Controller(
            source,
            id => applied.Add(id),
            WorkOptions(admissions: 4, cpuBytes: 1_000));
        controller.BeginDestinationReservation(1, center, 0);

        controller.Tick(32, 32);

        Assert.Equal([ordinary], applied);
        Assert.Equal(0, controller.WorkDiagnostics.NearBacklog);

        source.Enqueue(Loaded(center));
        controller.Tick(32, 32);

        Assert.Equal([ordinary, center], applied);
        Assert.Equal(0, controller.WorkDiagnostics.NearBacklog);

        controller.EndDestinationReservation(1);
        controller.Tick(32, 32);

        Assert.Equal([ordinary, center], applied);
        Assert.Equal(0, controller.WorkDiagnostics.DeferredCompletions);
    }

    [Fact]
    public void StaleGenerationResultsDrainWithoutStarvingCurrentGeneration()
    {
        uint center = StreamingRegion.EncodeLandblockIdForTest(32, 32);
        LandblockStreamResult[] results =
        [
            .. Enumerable.Range(0, 625)
                .Select(index => Loaded(
                    StreamingRegion.EncodeLandblockIdForTest(
                        32 + index % 25,
                        32 + index / 25),
                    generation: 1)),
            Loaded(center),
        ];
        var source = new QueueCompletionSource(results);
        int applied = 0;
        StreamingController controller = Controller(
            source,
            () => applied++,
            WorkOptions(admissions: 2, cpuBytes: 44));

        controller.Tick(32, 32);

        Assert.Equal(1, applied);
        Assert.Equal(0, source.BacklogCount);
        Assert.Equal(0, controller.WorkDiagnostics.DeferredCompletions);
        Assert.Equal(0, controller.WorkDiagnostics.DeferredAdoptedCpuBytes);
        Assert.Equal(1, controller.WorkDiagnostics.LastFrame.Used.CompletionAdmissions);
        Assert.Equal(44, controller.WorkDiagnostics.LastFrame.Used.AdoptedCpuBytes);
    }

    private static StreamingController Controller(
        ILandblockCompletionSource source,
        Action applied,
        StreamingWorkBudgetOptions options) =>
        Controller(source, _ => applied(), options);

    private static StreamingController Controller(
        ILandblockCompletionSource source,
        Action<uint> applied,
        StreamingWorkBudgetOptions options)
    {
        var state = new GpuWorldState();
        var presentation = new LandblockPresentationPipeline(
            (build, _) => applied(build.Landblock.LandblockId),
            state);
        return new StreamingController(
            enqueueLoad: static (_, _, _) => { },
            enqueueUnload: static (_, _) => { },
            completionSource: source,
            state,
            nearRadius: 1,
            farRadius: 1,
            presentationPipeline: presentation,
            workTimestamp: static () => 0,
            workTimestampFrequency: 1_000,
            workBudgetOptions: options);
    }

    private static StreamingWorkBudgetOptions WorkOptions(
        int admissions,
        long cpuBytes,
        long gpuBytes = 1_000,
        int retireOperations = 100) => new(
            MaxUpdateMilliseconds: 100,
            MaxCompletionAdmissions: admissions,
            MaxAdoptedCpuBytes: cpuBytes,
            MaxEntityOperations: 100,
            MaxGpuUploadBytes: gpuBytes,
            MaxGlRetireOperations: retireOperations,
            DestinationReserveFraction: 0.75f);

    private static WorldEntity Entity(uint id, int meshRefs)
    {
        var refs = new MeshRef[meshRefs];
        for (int i = 0; i < refs.Length; i++)
            refs[i] = new MeshRef((uint)(0x01000000 + i), Matrix4x4.Identity);
        return new WorldEntity
        {
            Id = id,
            SourceGfxObjOrSetupId = 0x01000000u + id,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = refs,
        };
    }

    private static LandblockStreamResult.Loaded Loaded(
        uint landblockId,
        ulong generation = 0) => new(
        landblockId,
        LandblockStreamTier.Near,
        new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>()),
        new LandblockMeshData(
            new TerrainVertex[1],
            new uint[1]),
        generation);

    private sealed class QueueCompletionSource(
        params LandblockStreamResult[] results)
        : ILandblockCompletionSource
    {
        private readonly Queue<LandblockStreamResult> _results = new(results);

        public int BacklogCount => _results.Count;

        public void Enqueue(LandblockStreamResult result) =>
            _results.Enqueue(result);

        public bool TryPeek(out LandblockStreamResult? result)
        {
            if (_results.TryPeek(out LandblockStreamResult? peeked))
            {
                result = peeked;
                return true;
            }

            result = null;
            return false;
        }

        public bool TryRead(out LandblockStreamResult? result)
        {
            if (_results.TryDequeue(out LandblockStreamResult? consumed))
            {
                result = consumed;
                return true;
            }

            result = null;
            return false;
        }
    }
}
