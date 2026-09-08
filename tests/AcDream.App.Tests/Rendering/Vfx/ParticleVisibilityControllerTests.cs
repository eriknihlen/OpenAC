using System.Numerics;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Vfx;

namespace AcDream.App.Tests.Rendering.Vfx;

public sealed class ParticleVisibilityControllerTests
{
    [Fact]
    public void CompletedRetailViewFeedsNextParticleUpdate()
    {
        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(1));
        int handle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000001u,
                Type = ParticleType.Still,
                MaxDegradeDistance = 100f,
                MaxParticles = 1,
                InitialParticles = 1,
                LifetimeMin = 10f,
                LifetimeMax = 10f,
            },
            Vector3.Zero,
            attachedObjectId: 99u);
        particles.UpdateEmitterOwnerCell(handle, 0x01010001u);
        var controller = new ParticleVisibilityController();

        controller.BeginFrame(Vector3.Zero);
        controller.UseWorldView();
        controller.MarkVisibleLandscapeCells(new HashSet<uint> { 0x01010001u });
        controller.CompleteFrame();
        controller.Apply(particles, 1f);

        Assert.True(Assert.Single(particles.EnumerateEmitters()).ViewEligible);
        Assert.Equal(handle, Assert.Single(particles.EnumerateEmitters()).Handle);
    }

    [Fact]
    public void UnresolvedFramePublishesEmptyWorldView()
    {
        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(1));
        int handle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000002u,
                Type = ParticleType.Still,
                MaxDegradeDistance = 100f,
                MaxParticles = 1,
            },
            Vector3.Zero,
            attachedObjectId: 42u);
        particles.UpdateEmitterOwnerCell(handle, 0x01010001u);
        var controller = new ParticleVisibilityController();

        controller.BeginFrame(Vector3.Zero);
        controller.UseWorldView();
        controller.CompleteFrame();
        controller.Apply(particles, 1f);
        Assert.False(Assert.Single(particles.EnumerateEmitters()).ViewEligible);

        controller.BeginFrame(Vector3.Zero);
        controller.CompleteFrame();
        controller.Apply(particles, 1f);
        Assert.False(Assert.Single(particles.EnumerateEmitters()).ViewEligible);
    }

    [Fact]
    public void AbortedFrame_PreservesTheLastCompletedWorldView()
    {
        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(1));
        int handle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000003u,
                Type = ParticleType.Still,
                MaxDegradeDistance = 100f,
                MaxParticles = 1,
            },
            Vector3.Zero,
            attachedObjectId: 42u);
        particles.UpdateEmitterOwnerCell(handle, 0x01010001u);
        var controller = new ParticleVisibilityController();

        controller.BeginFrame(Vector3.Zero);
        controller.UseWorldView();
        controller.MarkVisibleLandscapeCells([0x01010001u]);
        controller.CompleteFrame();

        controller.BeginFrame(new Vector3(1000f, 1000f, 0f));
        controller.UseWorldView();
        controller.MarkVisibleLandscapeCells([0x02020001u]);
        controller.AbortFrame();
        controller.Apply(particles, 1f);

        Assert.True(Assert.Single(particles.EnumerateEmitters()).ViewEligible);
    }

    [Fact]
    public void BorrowedLandscapeFrame_TracksOnlyCompletedTransactionsByReference()
    {
        var controller = new ParticleVisibilityController();
        RetailLandscapeVisibilityFrame first =
            controller.CaptureCompletedLandscapeVisibility();
        Assert.False(first.HasCompletedWorldView);
        Assert.Empty(first.CellIds);

        controller.BeginFrame(Vector3.Zero);
        controller.UseWorldView();
        controller.CompleteFrame();
        RetailLandscapeVisibilityFrame completedEmpty =
            controller.CaptureCompletedLandscapeVisibility();
        Assert.True(completedEmpty.HasCompletedWorldView);
        Assert.Empty(completedEmpty.CellIds);
        Assert.Same(first.CellIds, completedEmpty.CellIds);

        controller.BeginFrame(Vector3.One);
        controller.UseWorldView();
        controller.MarkVisibleLandscapeCells([0x12340001u]);
        RetailLandscapeVisibilityFrame whileBuilding =
            controller.CaptureCompletedLandscapeVisibility();
        Assert.Same(completedEmpty.CellIds, whileBuilding.CellIds);
        Assert.Empty(whileBuilding.CellIds);
        controller.AbortFrame();
        RetailLandscapeVisibilityFrame aborted =
            controller.CaptureCompletedLandscapeVisibility();
        Assert.Same(completedEmpty.CellIds, aborted.CellIds);
        Assert.True(aborted.HasCompletedWorldView);
        Assert.Empty(aborted.CellIds);

        controller.BeginFrame(Vector3.One);
        controller.UseWorldView();
        controller.MarkVisibleLandscapeCells([0x12340001u]);
        controller.CompleteFrame();
        RetailLandscapeVisibilityFrame replaced =
            controller.CaptureCompletedLandscapeVisibility();
        Assert.Same(completedEmpty.CellIds, replaced.CellIds);
        Assert.Equal([0x12340001u], replaced.CellIds);

        controller.Reset();
        RetailLandscapeVisibilityFrame reset =
            controller.CaptureCompletedLandscapeVisibility();
        Assert.Same(completedEmpty.CellIds, reset.CellIds);
        Assert.False(reset.HasCompletedWorldView);
        Assert.Empty(reset.CellIds);
    }

    [Fact]
    public void CompleteAbortAndResetPublishOnlyCompleteLandscapeTransactions()
    {
        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(1));
        int first = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000004u,
                Type = ParticleType.Still,
                MaxDegradeDistance = 100f,
                MaxParticles = 1,
            },
            Vector3.Zero,
            attachedObjectId: 43u);
        int second = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000005u,
                Type = ParticleType.Still,
                MaxDegradeDistance = 100f,
                MaxParticles = 1,
            },
            Vector3.Zero,
            attachedObjectId: 44u);
        particles.UpdateEmitterOwnerCell(first, 0x0101_0001u);
        particles.UpdateEmitterOwnerCell(second, 0x0101_0002u);
        var controller = new ParticleVisibilityController();

        controller.BeginFrame(Vector3.Zero);
        controller.UseWorldView();
        controller.MarkVisibleLandscapeCells([0x0101_0001u]);
        controller.CompleteFrame();

        controller.BeginFrame(new Vector3(1000f, 0f, 0f));
        controller.UseWorldView();
        controller.MarkVisibleLandscapeCells([0x0101_0002u]);
        controller.AbortFrame();
        controller.Apply(particles, 1f);
        Assert.True(particles.EnumerateEmitters().Single(e => e.Handle == first).ViewEligible);
        Assert.False(particles.EnumerateEmitters().Single(e => e.Handle == second).ViewEligible);

        controller.BeginFrame(Vector3.Zero);
        controller.UseWorldView();
        controller.MarkVisibleLandscapeCells([0x0101_0002u]);
        controller.CompleteFrame();
        controller.Apply(particles, 1f);
        Assert.False(particles.EnumerateEmitters().Single(e => e.Handle == first).ViewEligible);
        Assert.True(particles.EnumerateEmitters().Single(e => e.Handle == second).ViewEligible);

        controller.Reset();
        controller.Apply(particles, 1f);
        Assert.All(particles.EnumerateEmitters(), emitter => Assert.False(emitter.ViewEligible));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x0101_0100u)]
    public void MarkVisibleLandscapeCells_RejectsNonLandscapeIds(uint cellId)
    {
        var controller = new ParticleVisibilityController();
        controller.BeginFrame(Vector3.Zero);
        controller.UseWorldView();

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => controller.MarkVisibleLandscapeCells([cellId]));

        Assert.Contains($"0x{cellId:X8}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionApply_WarmedParticleViewPathDoesNotAllocate()
    {
        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(1));
        int outdoor = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000006u,
                Type = ParticleType.Still,
                MaxDegradeDistance = 100f,
                MaxParticles = 1,
            },
            Vector3.Zero,
            attachedObjectId: 45u);
        int environment = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000007u,
                Type = ParticleType.Still,
                MaxDegradeDistance = 100f,
                MaxParticles = 1,
            },
            Vector3.Zero,
            attachedObjectId: 46u);
        particles.UpdateEmitterOwnerCell(outdoor, 0x0101_0001u);
        particles.UpdateEmitterOwnerCell(environment, 0x0101_0100u);
        var controller = new ParticleVisibilityController();
        controller.BeginFrame(Vector3.Zero);
        controller.UseWorldView();
        controller.MarkVisibleLandscapeCells([0x0101_0001u]);
        controller.CompleteFrame();

        long allocated = ZeroAllocationProbe.MeasureWarmed(
            () => controller.Apply(particles, 1f),
            batchSize: 256,
            warmupBatches: 2,
            samples: 4);

        Assert.True(allocated == 0, $"Particle view allocated {allocated} bytes.");
    }
}
