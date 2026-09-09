using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering;

public sealed class ParticleEmitterRetirementTrackerTests
{
    [Fact]
    public void MeshFailureDoesNotBlockGfxOrTextureCleanupAndIsRetried()
    {
        int meshAttempts = 0;
        var removedGfx = new List<int>();
        var releasedTextures = new List<int>();
        var failures = new List<Exception>();
        var tracker = new ParticleEmitterRetirementTracker(
            _ =>
            {
                meshAttempts++;
                if (meshAttempts == 1)
                    throw new InvalidOperationException("mesh release failed");
            },
            removedGfx.Add,
            releasedTextures.Add,
            failures.Add);

        tracker.BeginRetirement(42);

        Assert.Equal([42], removedGfx);
        Assert.Equal([42], releasedTextures);
        Assert.Single(failures);
        Assert.Equal(1, tracker.PendingCount);

        tracker.RetryPending();

        Assert.Equal(2, meshAttempts);
        Assert.Equal([42], removedGfx);
        Assert.Equal([42], releasedTextures);
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void CommittedMeshFailureIsNotReplayed()
    {
        int meshAttempts = 0;
        var tracker = new ParticleEmitterRetirementTracker(
            _ =>
            {
                meshAttempts++;
                throw new MeshReferenceMutationException(
                    "committed",
                    mutationCommitted: true,
                    new InvalidOperationException());
            },
            _ => { },
            _ => { });

        tracker.BeginRetirement(42);
        tracker.RetryPending();

        Assert.Equal(1, meshAttempts);
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void IndependentFailuresRetainOnlyTheirOwnObligations()
    {
        int gfxAttempts = 0;
        int textureAttempts = 0;
        var tracker = new ParticleEmitterRetirementTracker(
            _ => { },
            _ =>
            {
                gfxAttempts++;
                if (gfxAttempts == 1)
                    throw new InvalidOperationException("gfx");
            },
            _ =>
            {
                textureAttempts++;
                if (textureAttempts == 1)
                    throw new InvalidOperationException("texture");
            });

        tracker.BeginRetirement(7);
        tracker.RetryPending();

        Assert.Equal(2, gfxAttempts);
        Assert.Equal(2, textureAttempts);
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void SameHandleReentryDoesNotRepeatActiveRetirementStage()
    {
        ParticleEmitterRetirementTracker? tracker = null;
        int meshReleases = 0;
        int gfxRemovals = 0;
        int textureReleases = 0;
        tracker = new ParticleEmitterRetirementTracker(
            handle =>
            {
                meshReleases++;
                tracker!.BeginRetirement(handle);
            },
            _ => gfxRemovals++,
            _ => textureReleases++);

        tracker.BeginRetirement(42);

        Assert.Equal(1, meshReleases);
        Assert.Equal(1, gfxRemovals);
        Assert.Equal(1, textureReleases);
        Assert.Equal(0, tracker.PendingCount);
    }
}
