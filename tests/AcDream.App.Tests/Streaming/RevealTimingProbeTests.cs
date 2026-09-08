using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.Runtime;

namespace AcDream.App.Tests.Streaming;

public sealed class RevealTimingProbeTests
{
    [Fact]
    public void ResourceSampling_IsLowFrequencyAndCoalescesSamePollEdges()
    {
        var resources = new CountingResourceSource();
        var probe = new RevealTimingProbe(
            loadedLandblockCount: static () => 4,
            renderResources: resources);
        probe.Begin(
            kind: "Login",
            generation: 2,
            destinationCell: 0x1234_0001u,
            window: new StreamingRevealWindow(NearRadius: 1, FarRadius: 2));

        probe.Observe(
            default,
            RuntimePortalSnapshot.Idle with { Generation = 2 });
        probe.Observe(
            new WorldRevealReadinessSnapshot(
                DestinationCell: 0x1234_0001u,
                IsIndoor: false,
                IsUnhydratable: false,
                RequiredRenderRadius: 2,
                RequiredNearRadius: 1,
                IsRenderNeighborhoodReady: true,
                AreCompositeTexturesReady: true,
                IsCollisionReady: true),
            RuntimePortalSnapshot.Idle with
            {
                Generation = 2,
                Materialized = true,
                WorldViewportObserved = true,
            });

        Assert.Equal(3, resources.CaptureCount);
    }

    [Fact]
    public void ResourceLineCarriesColdMeshTextureAndProcessAttribution()
    {
        MeshStreamResourceDiagnostics mesh =
            default(MeshStreamResourceDiagnostics) with
        {
            RenderData = 12,
            AtlasArrays = 3,
            EstimatedBytes = 4_000,
            GlobalUploadCount = 7,
            GlobalUploadedBytes = 8_000,
            FrameUploadCount = 2,
            FrameUploadBytes = 900,
            StagedUploadBacklog = 5,
            StagedUploadBytes = 6_000,
            GlobalCapacityBytes = 10_000,
            GlobalPhysicalCapacityBytes = 20_000,
            GlobalMigrationInProgress = true,
            PreparedReads = 21,
            PreparedLoaded = 20,
        };
        TextureStreamResourceDiagnostics textures =
            default(TextureStreamResourceDiagnostics) with
        {
            OwnedBindlessTextures = 17,
            TextureOwners = 15,
            CachedCompositeTextures = 9,
            CachedUnownedComposites = 2,
            CachedUnownedCompositeBytes = 700,
            CompositeAtlases = 2,
            CompositeAtlasBytes = 30_000,
            CompositeWarmupPending = 4,
            FrameCompositeUploadCount = 3,
            FrameCompositeUploadBytes = 1_200,
        };
        ProcessResourceDiagnostics process = new(
            ManagedBytes: 40_000,
            ManagedCommittedBytes: 50_000,
            TrackedGpuBytes: 60_000,
            TrackedGpuBuffers: 11,
            TrackedGpuTextures: 13);
        var snapshot = new RenderFrameResourceDiagnosticsSnapshot(
            default,
            default,
            mesh,
            textures,
            process);

        string line = RevealTimingProbe.FormatResourceLine(
            "render-ready",
            "Login",
            generation: 2,
            elapsedMilliseconds: 3456,
            snapshot);

        Assert.StartsWith(
            "[reveal-resource] checkpoint=render-ready kind=Login gen=2 elapsedMs=3456",
            line);
        Assert.Contains("meshData=12", line);
        Assert.Contains("globalUploads=7", line);
        Assert.Contains("staged=5", line);
        Assert.Contains("arenaMigrating=1", line);
        Assert.Contains("prepared=0/21/20/0/0", line);
        Assert.Contains("ownedTextures=17", line);
        Assert.Contains("unownedCompositeBytes=700", line);
        Assert.Contains("compositePending=4", line);
        Assert.Contains("frameCompositeUploadBytes=1200", line);
        Assert.Contains("managedBytes=40000", line);
        Assert.Contains("trackedGpuBytes=60000", line);
    }

    private sealed class CountingResourceSource :
        IRenderFrameResourceDiagnosticsSource
    {
        public int CaptureCount { get; private set; }

        public RenderFrameResourceDiagnosticsSnapshot Capture()
        {
            CaptureCount++;
            return default;
        }
    }
}
