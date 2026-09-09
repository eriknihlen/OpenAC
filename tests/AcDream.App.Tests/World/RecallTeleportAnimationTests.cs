using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Content.Vfx;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.World;

public sealed class RecallTeleportAnimationTests
{
    private const uint HumanSetup = 0x02000001u;
    private const uint HumanMotionTable = 0x09000001u;
    private const uint NonCombat = 0x8000003Du;
    private const uint Ready = 0x41000003u;
    private const uint LifestoneRecall = 0x10000153u;

    [Fact]
    public void LiveFrame_AdvancesObjectRuntimeBeforeInboundNetwork()
    {
        var calls = new List<string>();
        var frame = new RetailLiveFrameCoordinator(
            new TestLiveObjectFramePhase(_ => calls.Add("objects")),
            new GpuWorldState(),
            new TestLiveSessionFramePhase(() => calls.Add("network")),
            new TestPostNetworkCommandFramePhase(() => calls.Add("commands")),
            new TestLiveSpatialReconcilePhase(() => calls.Add("spatial")));

        frame.Tick(1f / 60f);

        Assert.Equal(["objects", "network", "commands", "spatial"], calls);
    }

    [Fact]
    public void LiveFrame_WhileCellsAreBlocked_DrainsNetworkAndCommandsWithoutWorldTicks()
    {
        var calls = new List<string>();
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        RuntimeWorldTransitTestDriver.BeginPortal(
            transit,
            0x11340021u);
        var frame = new RetailLiveFrameCoordinator(
            new TestLiveObjectFramePhase(_ => calls.Add("objects")),
            new GpuWorldState(availability: availability),
            new TestLiveSessionFramePhase(() => calls.Add("network")),
            new TestPostNetworkCommandFramePhase(() => calls.Add("commands")),
            new TestLiveSpatialReconcilePhase(() => calls.Add("spatial")),
            availability);

        frame.Tick(1f / 60f);

        Assert.Equal(["network", "commands"], calls);
    }

    [Fact]
    public void LiveFrame_WhileCellsAreBlocked_SynchronizesOnlyRenderProjection()
    {
        var calls = new List<string>();
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        RuntimeWorldTransitTestDriver.BeginPortal(
            transit,
            0x11340021u);
        var frame = new RetailLiveFrameCoordinator(
            new TestLiveObjectFramePhase(_ => calls.Add("objects")),
            new GpuWorldState(availability: availability),
            new TestLiveSessionFramePhase(() => calls.Add("network")),
            new TestPostNetworkCommandFramePhase(
                () => calls.Add("commands")),
            new TestLiveSpatialReconcilePhase(
                () => calls.Add("spatial")),
            availability,
            new TestRenderProjectionSyncPhase(
                () => calls.Add("render-projection")));

        frame.Tick(1f / 60f);

        Assert.Equal(
            ["network", "commands", "render-projection"],
            calls);
    }

    [Fact]
    public void LiveFrame_InvalidHostDeltaCannotBlockNetworkDispatch()
    {
        var deltas = new List<float>();
        var calls = new List<string>();
        var frame = new RetailLiveFrameCoordinator(
            new TestLiveObjectFramePhase(delta =>
            {
                deltas.Add(delta);
                calls.Add("objects");
            }),
            new GpuWorldState(),
            new TestLiveSessionFramePhase(() => calls.Add("network")),
            new TestPostNetworkCommandFramePhase(() => calls.Add("commands")),
            new TestLiveSpatialReconcilePhase(() => calls.Add("spatial")));

        frame.Tick(float.NaN);
        frame.Tick(float.PositiveInfinity);
        frame.Tick(-1f);

        Assert.Equal([0f, 0f, 0f], deltas);
        Assert.Equal(
            Enumerable.Repeat(new[] { "objects", "network", "commands", "spatial" }, 3)
                .SelectMany(static frameCalls => frameCalls),
            calls);
        Assert.Equal(0.0, UpdateFrameClock.NormalizeDeltaSeconds(double.NaN));
        Assert.Equal(0.0, UpdateFrameClock.NormalizeDeltaSeconds(double.NegativeInfinity));
        Assert.Equal(0.0, UpdateFrameClock.NormalizeDeltaSeconds(double.PositiveInfinity));
        Assert.Equal(0.0, UpdateFrameClock.NormalizeDeltaSeconds(double.MaxValue));
    }

    [Fact]
    public void LiveFrame_CommitsTheInboundMutationBatchBeforeCommandsAndReconcile()
    {
        const uint landblock = 0x0101FFFFu;
        var calls = new List<string>();
        var world = new GpuWorldState();
        long before = world.VisibilityCommitCount;
        var frame = new RetailLiveFrameCoordinator(
            new TestLiveObjectFramePhase(_ => calls.Add("objects")),
            world,
            new TestLiveSessionFramePhase(() =>
            {
                calls.Add("network");
                world.PlaceLiveEntityProjection(landblock, Entity(1));
                Assert.Equal(before, world.VisibilityCommitCount);
            }),
            new TestPostNetworkCommandFramePhase(() =>
            {
                calls.Add("commands");
                Assert.Equal(before + 1, world.VisibilityCommitCount);
            }),
            new TestLiveSpatialReconcilePhase(() =>
            {
                calls.Add("spatial");
                Assert.Equal(before + 1, world.VisibilityCommitCount);
            }));

        frame.Tick(1f / 60f);

        Assert.Equal(["objects", "network", "commands", "spatial"], calls);
        Assert.Equal(before + 1, world.VisibilityCommitCount);
        Assert.Equal(1, world.PendingLiveEntityCount);
    }

    [Fact]
    public void LiveFrame_SessionFailureDisposesMutationBatchAndSuppressesLaterPhases()
    {
        const uint landblock = 0x0101FFFFu;
        var calls = new List<string>();
        var world = new GpuWorldState();
        long before = world.VisibilityCommitCount;
        var frame = new RetailLiveFrameCoordinator(
            new TestLiveObjectFramePhase(_ => calls.Add("objects")),
            world,
            new TestLiveSessionFramePhase(() =>
            {
                calls.Add("network");
                world.PlaceLiveEntityProjection(landblock, Entity(2));
                Assert.Equal(before, world.VisibilityCommitCount);
                throw new InvalidOperationException("inbound failure");
            }),
            new TestPostNetworkCommandFramePhase(() => calls.Add("commands")),
            new TestLiveSpatialReconcilePhase(() => calls.Add("spatial")));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => frame.Tick(1f / 60f));

        Assert.Equal("inbound failure", error.Message);
        Assert.Equal(["objects", "network"], calls);
        Assert.Equal(before + 1, world.VisibilityCommitCount);
        Assert.Equal(1, world.PendingLiveEntityCount);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledRecall_HiddenEnterWorldPublishesFinishedCyclicPoseWithoutAdvancingTime()
    {
        string datDir = @"C:\Turbine\Asheron's Call";
        if (!File.Exists(Path.Combine(datDir, "client_portal.dat")))
            throw new InvalidOperationException(
                "Lane=InstalledDat requires installed retail DATs; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        Setup setup = Assert.IsType<Setup>(dats.Get<Setup>(HumanSetup));
        MotionTable table = Assert.IsType<MotionTable>(dats.Get<MotionTable>(HumanMotionTable));
        var loader = new RetailAnimationLoader(dats);
        var sequencer = new AnimationSequencer(setup, table, loader);
        sequencer.SetCycle(NonCombat, Ready);
        sequencer.PlayAction(LifestoneRecall);

        int readyKey = unchecked((int)((NonCombat << 16) | (Ready & 0xFFFFFFu)));
        MotionData action = table.Links[readyKey].MotionData[unchecked((int)LifestoneRecall)];
        double aceDuration = 0.0;
        foreach (var anim in action.Anims)
        {
            Animation loaded = Assert.IsType<Animation>(loader.LoadAnimation((uint)anim.AnimId));
            int high = anim.HighFrame == -1 ? loaded.PartFrames.Count : anim.HighFrame;
            aceDuration += (high - anim.LowFrame) / Math.Abs(anim.Framerate);
        }

        // Advance returns a reusable scratch buffer; snapshot the authored tail
        // before HandleEnterWorld/SampleCurrentPose overwrites that buffer.
        PartTransform[] recallTail = sequencer.Advance((float)aceDuration).ToArray();
        Assert.False(
            sequencer.CurrentNodeDiag.IsLooping,
            "At ACE's exact teleport boundary the authored recall link is still the current node.");

        sequencer.Manager.HandleEnterWorld();
        IReadOnlyList<PartTransform> finishedPose = sequencer.SampleCurrentPose();

        Assert.True(sequencer.CurrentNodeDiag.IsLooping);
        Assert.Equal(recallTail.Length, finishedPose.Count);
        Assert.Contains(
            Enumerable.Range(0, finishedPose.Count),
            index => Vector3.DistanceSquared(
                         recallTail[index].Origin,
                         finishedPose[index].Origin) > 1e-6f
                     || MathF.Abs(Quaternion.Dot(
                         Quaternion.Normalize(recallTail[index].Orientation),
                         Quaternion.Normalize(finishedPose[index].Orientation))) < 0.999999f);
    }

    private static WorldEntity Entity(uint id) => new()
    {
        Id = id,
        ServerGuid = 0x70000000u + id,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };
}
