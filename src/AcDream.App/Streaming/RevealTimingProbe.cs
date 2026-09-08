using System.Diagnostics;
using AcDream.App.Rendering;
using AcDream.Runtime;

namespace AcDream.App.Streaming;

internal sealed class RevealTimingProbe
{
    private readonly Func<int>? _loadedLandblockCount;
    private readonly IRenderFrameResourceDiagnosticsSource? _renderResources;
    private readonly Stopwatch _clock = new();
    private long _generation;
    private string _kind = "";
    private int _windowLandblocks;
    private bool _render;
    private bool _composites;
    private bool _collision;
    private bool _gateReady;
    private bool _materialized;
    private bool _summarized;
    private long _renderMs = -1;
    private long _compositesMs = -1;
    private long _collisionMs = -1;
    private long _gateReadyMs = -1;
    private long _materializedMs = -1;
    private long _lastProgressMs;
    private int _framesSinceProgress;

    public RevealTimingProbe(
        Func<int>? loadedLandblockCount,
        IRenderFrameResourceDiagnosticsSource? renderResources = null)
    {
        _loadedLandblockCount = loadedLandblockCount;
        _renderResources = renderResources;
    }

    public void Begin(
        string kind,
        long generation,
        uint destinationCell,
        in StreamingRevealWindow window)
    {
        _generation = generation;
        _kind = kind;
        int side = window.FarRadius * 2 + 1;
        _windowLandblocks = side * side;
        _render = false;
        _composites = false;
        _collision = false;
        _gateReady = false;
        _materialized = false;
        _summarized = false;
        _renderMs = -1;
        _compositesMs = -1;
        _collisionMs = -1;
        _gateReadyMs = -1;
        _materializedMs = -1;
        _lastProgressMs = 0;
        _clock.Restart();
        Console.WriteLine(
            $"[reveal-timing] event=begin kind={kind} gen={generation} "
            + $"cell=0x{destinationCell:X8} window={window.NearRadius}/"
            + $"{window.FarRadius} landblocks={_windowLandblocks} "
            + $"loaded={_loadedLandblockCount?.Invoke() ?? -1}");
        EmitResourceSnapshot("begin", elapsedMilliseconds: 0);
    }

    public void Observe(
        in WorldRevealReadinessSnapshot readiness,
        in RuntimePortalSnapshot portal)
    {
        if (_generation == 0 || portal.Generation != _generation)
            return;

        _framesSinceProgress++;
        long elapsed = _clock.ElapsedMilliseconds;
        string? resourceCheckpoint = null;
        if (!_render && readiness.IsRenderNeighborhoodReady)
        {
            _render = true;
            _renderMs = elapsed;
            Edge("render-ready", elapsed);
            resourceCheckpoint = AppendCheckpoint(
                resourceCheckpoint,
                "render-ready");
        }
        if (!_composites && readiness.AreCompositeTexturesReady)
        {
            _composites = true;
            _compositesMs = elapsed;
            Edge("composites-ready", elapsed);
            resourceCheckpoint = AppendCheckpoint(
                resourceCheckpoint,
                "composites-ready");
        }
        if (!_collision && readiness.IsCollisionReady)
        {
            _collision = true;
            _collisionMs = elapsed;
            Edge("collision-ready", elapsed);
            resourceCheckpoint = AppendCheckpoint(
                resourceCheckpoint,
                "collision-ready");
        }
        if (!_gateReady && readiness.IsReady)
        {
            _gateReady = true;
            _gateReadyMs = elapsed;
            Edge("gate-ready", elapsed);
            resourceCheckpoint = AppendCheckpoint(
                resourceCheckpoint,
                "gate-ready");
        }
        if (!_materialized && portal.Materialized)
        {
            _materialized = true;
            _materializedMs = elapsed;
            Edge("materialized", elapsed);
            resourceCheckpoint = AppendCheckpoint(
                resourceCheckpoint,
                "materialized");
        }

        if (resourceCheckpoint is not null)
            EmitResourceSnapshot(resourceCheckpoint, elapsed);

        if (!_summarized && portal.WorldViewportObserved)
        {
            _summarized = true;
            Console.WriteLine(
                $"[reveal-timing] SUMMARY kind={_kind} gen={_generation} "
                + $"totalMs={elapsed} renderMs={_renderMs} "
                + $"compositesMs={_compositesMs} collisionMs={_collisionMs} "
                + $"gateReadyMs={_gateReadyMs} "
                + $"materializedMs={_materializedMs} "
                + $"landblocks={_windowLandblocks}");
            EmitResourceSnapshot("summary", elapsed);
            return;
        }

        if (!_summarized && elapsed - _lastProgressMs >= 1000)
        {
            _lastProgressMs = elapsed;
            Console.WriteLine(
                $"[reveal-timing] elapsedMs={elapsed} "
                + $"render={(_render ? 1 : 0)} "
                + $"composites={(_composites ? 1 : 0)} "
                + $"collision={(_collision ? 1 : 0)} "
                + $"loaded={_loadedLandblockCount?.Invoke() ?? -1}"
                + $"/{_windowLandblocks} "
                + $"frames={_framesSinceProgress}");
            EmitResourceSnapshot("progress", elapsed);
            PublicationTimingProbe.EmitStreamingTickWindow();
            _framesSinceProgress = 0;
        }
    }

    private void Edge(string name, long elapsed) =>
        Console.WriteLine(
            $"[reveal-timing] event={name} kind={_kind} gen={_generation} "
            + $"elapsedMs={elapsed} "
            + $"loaded={_loadedLandblockCount?.Invoke() ?? -1}"
            + $"/{_windowLandblocks}");

    private void EmitResourceSnapshot(string checkpoint, long elapsedMilliseconds)
    {
        if (_renderResources is null)
            return;

        RenderFrameResourceDiagnosticsSnapshot snapshot =
            _renderResources.Capture();
        Console.WriteLine(FormatResourceLine(
            checkpoint,
            _kind,
            _generation,
            elapsedMilliseconds,
            snapshot));
    }

    private static string AppendCheckpoint(string? current, string next) =>
        current is null ? next : current + "+" + next;

    internal static string FormatResourceLine(
        string checkpoint,
        string kind,
        long generation,
        long elapsedMilliseconds,
        in RenderFrameResourceDiagnosticsSnapshot snapshot)
    {
        MeshStreamResourceDiagnostics mesh = snapshot.Mesh;
        TextureStreamResourceDiagnostics textures = snapshot.Textures;
        ProcessResourceDiagnostics process = snapshot.Process;
        return $"[reveal-resource] checkpoint={checkpoint} kind={kind} "
            + $"gen={generation} elapsedMs={elapsedMilliseconds} "
            + $"meshData={mesh.RenderData} meshAtlases={mesh.AtlasArrays} "
            + $"meshUnusedLru={mesh.UnusedLru} meshBytes={mesh.EstimatedBytes} "
            + $"globalUploads={mesh.GlobalUploadCount} "
            + $"globalUploadBytes={mesh.GlobalUploadedBytes} "
            + $"frameUploads={mesh.FrameUploadCount} "
            + $"frameUploadBytes={mesh.FrameUploadBytes} "
            + $"frameArrayBytes={mesh.FrameArrayAllocationBytes} "
            + $"frameMipmapBytes={mesh.FrameMipmapBytes} "
            + $"frameBufferUploadBytes={mesh.FrameBufferUploadBytes} "
            + $"frameBufferAllocationBytes={mesh.FrameBufferAllocationBytes} "
            + $"frameBufferCopyBytes={mesh.FrameBufferCopyBytes} "
            + $"frameNewArrays={mesh.FrameNewArrayCount} "
            + $"frameNewBuffers={mesh.FrameNewBufferCount} "
            + $"frameStaleDiscards={mesh.FrameStaleDiscardCount} "
            + $"frameMipmapArrays={mesh.FrameMipmapArrayCount} "
            + $"staged={mesh.StagedUploadBacklog} "
            + $"stagedBytes={mesh.StagedUploadBytes} "
            + $"stagingHighWater={(mesh.StagingAtHighWater ? 1 : 0)} "
            + $"cpuMeshCache={mesh.CpuMeshCacheCount} "
            + $"cpuMeshCacheBytes={mesh.CpuMeshCacheBytes} "
            + $"arenaCapacityBytes={mesh.GlobalCapacityBytes} "
            + $"arenaPhysicalBytes={mesh.GlobalPhysicalCapacityBytes} "
            + $"arenaMigrating={(mesh.GlobalMigrationInProgress ? 1 : 0)} "
            + $"prepared={mesh.PreparedProbes}/{mesh.PreparedReads}/"
            + $"{mesh.PreparedLoaded}/{mesh.PreparedMissing}/"
            + $"{mesh.PreparedCorrupt} "
            + $"ownedTextures={textures.OwnedBindlessTextures} "
            + $"textureOwners={textures.TextureOwners} "
            + $"composites={textures.CachedCompositeTextures} "
            + $"unownedComposites={textures.CachedUnownedComposites} "
            + $"unownedCompositeBytes={textures.CachedUnownedCompositeBytes} "
            + $"compositeAtlases={textures.CompositeAtlases} "
            + $"compositeAtlasBytes={textures.CompositeAtlasBytes} "
            + $"compositePending={textures.CompositeWarmupPending} "
            + $"frameCompositeUploads={textures.FrameCompositeUploadCount} "
            + $"frameCompositeUploadBytes={textures.FrameCompositeUploadBytes} "
            + $"managedBytes={process.ManagedBytes} "
            + $"managedCommittedBytes={process.ManagedCommittedBytes} "
            + $"trackedGpuBytes={process.TrackedGpuBytes} "
            + $"trackedGpuBuffers={process.TrackedGpuBuffers} "
            + $"trackedGpuTextures={process.TrackedGpuTextures}";
    }
}
