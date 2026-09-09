using System.Text.Json;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Residency;
using AcDream.App.Rendering.Scene;
using AcDream.App.Streaming;
using AcDream.App.UI.Testing;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.World;

namespace AcDream.App.Diagnostics;

internal static class AutomationArtifactName
{
    public static bool TryValidate(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
        {
            error = "artifact name must contain 1-80 characters";
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
            {
                error = $"artifact name '{name}' may contain only letters, digits, '-' and '_'";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}

internal sealed record WorldLifecycleResourceSnapshot(
    int LoadedLandblocks,
    int WorldEntities,
    int AnimatedEntities,
    int VisibleLandblocks,
    int TotalLandblocks,
    int LiveEntities,
    int MaterializedLiveEntities,
    CurrentRenderSceneOracleSnapshot RenderSceneOracle,
    RenderSceneShadowComparisonSnapshot RenderSceneShadow,
    RenderFrameProductComparisonSnapshot RenderFrameProduct,
    int PendingLiveTeardowns,
    int PendingLandblockRetirements,
    int ParticleEmitters,
    int Particles,
    int ParticleBindings,
    int ParticleOwners,
    int EffectOwners,
    int LightOwners,
    int ScriptOwners,
    int ActiveScripts,
    int MeshRenderData,
    int MeshAtlasArrays,
    long MeshEstimatedBytes,
    int StagedMeshUploads,
    long StagedMeshBytes,
    long TrackedGpuBytes,
    long ResidencyGpuBytes,
    long GpuTrackerMinusResidencyBytes,
    int TrackedGpuBuffers,
    int TrackedGpuTextures,
    int OwnedCompositeTextures,
    int CompositeTextureOwners,
    int ActiveParticleTextures,
    int ParticleTextureOwners,
    int CompositeWarmupPending,
    long ManagedBytes,
    long ManagedCommittedBytes,
    long LohSizeBytes,
    long LohFragmentationBytes,
    long ProcessTotalAllocatedBytes,
    long CpuMeshCacheHits,
    long CpuMeshCacheMisses,
    long CpuMeshCacheEvictions,
    long DecodedTextureCacheHits,
    long DecodedTextureCacheMisses,
    long DecodedTextureCacheEvictions,
    long DatObjectCacheHits,
    long DatObjectCacheMisses,
    long DatObjectCacheEvictions,
    int PhysicsGraphGfxObjs,
    int PhysicsGraphSetups,
    int PhysicsGraphCells,
    int PhysicsFlatGfxObjs,
    int PhysicsFlatSetups,
    int PhysicsFlatCells,
    int PhysicsFlatEnvCells,
    CollisionShadowStats CollisionShadow,
    StreamingWorkDiagnostics StreamingWork,
    ResidencySnapshot Residency,
    double Fps,
    double FrameMilliseconds,
    string? LastFrameProfile);

internal sealed record WorldLifecycleCheckpoint(
    int Sequence,
    string Name,
    DateTime TimestampUtc,
    int ProcessId,
    RuntimePortalSnapshot Reveal,
    RuntimeWorldEnvironmentOwnershipSnapshot EnvironmentOwnership,
    RuntimeWorldTransitOwnershipSnapshot TransitOwnership,
    RenderFrameOutcome Render,
    WorldLifecycleResourceSnapshot Resources);

internal sealed class WorldLifecycleCheckpointRequest :
    IRetailUiAutomationCheckpoint
{
    private readonly object _sync = new();
    private RetailUiAutomationCheckpointStatus _status =
        RetailUiAutomationCheckpointStatus.Pending;
    private string? _error;

    public WorldLifecycleCheckpointRequest(object owner, int sequence, string name)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Sequence = sequence;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    internal object Owner { get; }
    public int Sequence { get; }
    public string Name { get; }

    public RetailUiAutomationCheckpointStatus Status
    {
        get
        {
            lock (_sync)
                return _status;
        }
    }

    public string? Error
    {
        get
        {
            lock (_sync)
                return _error;
        }
    }

    internal bool TrySetTerminal(
        RetailUiAutomationCheckpointStatus status,
        string? error)
    {
        if (status == RetailUiAutomationCheckpointStatus.Pending)
            throw new ArgumentOutOfRangeException(nameof(status));

        lock (_sync)
        {
            if (_status != RetailUiAutomationCheckpointStatus.Pending)
                return false;
            _status = status;
            _error = error;
            return true;
        }
    }
}

internal sealed class WorldRevealFactsAutomationRuntime
    : IRetailUiAutomationRuntime
{
    private readonly Func<RuntimePortalSnapshot> _getReveal;
    private readonly Func<int> _getPortalMaterializationCount;

    public WorldRevealFactsAutomationRuntime(
        Func<RuntimePortalSnapshot> getReveal,
        Func<int> getPortalMaterializationCount)
    {
        _getReveal = getReveal
            ?? throw new ArgumentNullException(nameof(getReveal));
        _getPortalMaterializationCount = getPortalMaterializationCount
            ?? throw new ArgumentNullException(
                nameof(getPortalMaterializationCount));
    }

    public bool IsWorldReady => _getReveal().IsReady;
    public bool IsWorldViewportVisible => _getReveal().WorldViewportObserved;
    public int PortalMaterializationCount => _getPortalMaterializationCount();

    public bool TryRequestCheckpoint(
        string name,
        out IRetailUiAutomationCheckpoint? checkpoint,
        out string error)
    {
        checkpoint = null;
        error = "checkpoints require ACDREAM_AUTOMATION_ARTIFACT_DIR";
        return false;
    }

    public void CancelCheckpoint(IRetailUiAutomationCheckpoint checkpoint)
    {
    }

    public bool TryRequestScreenshot(string name, out string error)
    {
        error = "screenshots require ACDREAM_AUTOMATION_ARTIFACT_DIR";
        return false;
    }

    public bool IsScreenshotComplete(string name) => false;
}

internal sealed class WorldLifecycleAutomationController :
    IRetailUiAutomationRuntime,
    IRenderFramePostDiagnosticsPhase,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Func<RuntimePortalSnapshot> _getReveal;
    private readonly Func<RuntimeWorldEnvironmentOwnershipSnapshot>
        _getEnvironmentOwnership;
    private readonly Func<RuntimeWorldTransitOwnershipSnapshot>
        _getTransitOwnership;
    private readonly Func<int> _getPortalMaterializationCount;
    private readonly Func<int> _getRenderPackPerformanceSampleCount;
    private readonly Func<bool> _getRenderPackFailedToRetail;
    private readonly Func<RetailUiAutomationRenderPackStatus>
        _getRenderPackStatus;
    private readonly Func<string, (bool Succeeded, string Error)>
        _selectRenderPack;
    private readonly Func<(bool Succeeded, string Error)>?
        _disableRenderPack;
    private readonly Func<(bool Succeeded, string Error)>?
        _reenableRenderPack;
    private readonly Func<(int Width, int Height)> _getFramebufferSize;
    private readonly Func<int, int, (bool Succeeded, string Error)>
        _resizeFramebuffer;
    private readonly Func<(bool Succeeded, string Error)>
        _resetRenderPackPerformance;
    private readonly Action? _requestClientClose;
    private readonly Func<RenderFrameOutcome, WorldLifecycleResourceSnapshot>
        _captureResources;
    private readonly FrameScreenshotController _screenshots;
    private readonly string _artifactDirectory;
    private readonly Action<string> _log;
    private readonly object _requestOwner = new();
    private readonly object _sync = new();
    private readonly Queue<WorldLifecycleCheckpointRequest> _requests = [];
    private string? _lastEnabledRenderPackPreset;
    private int _sequence;
    private bool _disposed;

    public WorldLifecycleAutomationController(
        Func<RuntimePortalSnapshot> getReveal,
        Func<RuntimeWorldEnvironmentOwnershipSnapshot>
            getEnvironmentOwnership,
        Func<RuntimeWorldTransitOwnershipSnapshot> getTransitOwnership,
        Func<int> getPortalMaterializationCount,
        Func<RenderFrameOutcome, WorldLifecycleResourceSnapshot> captureResources,
        FrameScreenshotController screenshots,
        string artifactDirectory,
        Action<string>? log = null,
        Func<int>? getRenderPackPerformanceSampleCount = null,
        Func<(bool Succeeded, string Error)>? resetRenderPackPerformance = null,
        Func<bool>? getRenderPackFailedToRetail = null,
        Func<RetailUiAutomationRenderPackStatus>? getRenderPackStatus = null,
        Func<string, (bool Succeeded, string Error)>? selectRenderPack = null,
        Func<(bool Succeeded, string Error)>? disableRenderPack = null,
        Func<(bool Succeeded, string Error)>? reenableRenderPack = null,
        Func<(int Width, int Height)>? getFramebufferSize = null,
        Func<int, int, (bool Succeeded, string Error)>? resizeFramebuffer = null,
        Action? requestClientClose = null)
    {
        _getReveal = getReveal ?? throw new ArgumentNullException(nameof(getReveal));
        _getEnvironmentOwnership = getEnvironmentOwnership
            ?? throw new ArgumentNullException(
                nameof(getEnvironmentOwnership));
        _getTransitOwnership = getTransitOwnership
            ?? throw new ArgumentNullException(nameof(getTransitOwnership));
        _getPortalMaterializationCount = getPortalMaterializationCount
            ?? throw new ArgumentNullException(nameof(getPortalMaterializationCount));
        _getRenderPackPerformanceSampleCount =
            getRenderPackPerformanceSampleCount ?? (() => 0);
        _getRenderPackFailedToRetail = getRenderPackFailedToRetail ?? (() => false);
        _getRenderPackStatus = getRenderPackStatus
            ?? (() => RetailUiAutomationRenderPackStatus.Retail);
        _selectRenderPack = selectRenderPack
            ?? (_ => (false, "render-pack selection automation is unavailable"));
        _disableRenderPack = disableRenderPack;
        _reenableRenderPack = reenableRenderPack;
        _getFramebufferSize = getFramebufferSize ?? (() => (0, 0));
        _resizeFramebuffer = resizeFramebuffer
            ?? ((_, _) => (false, "framebuffer resize automation is unavailable"));
        _resetRenderPackPerformance = resetRenderPackPerformance
            ?? (() => (false, "render-pack performance automation is unavailable"));
        _requestClientClose = requestClientClose;
        _captureResources = captureResources ?? throw new ArgumentNullException(nameof(captureResources));
        _screenshots = screenshots ?? throw new ArgumentNullException(nameof(screenshots));
        _artifactDirectory = string.IsNullOrWhiteSpace(artifactDirectory)
            ? throw new ArgumentException("An automation artifact directory is required.", nameof(artifactDirectory))
            : Path.GetFullPath(artifactDirectory);
        _log = log ?? (_ => { });
    }

    public bool IsWorldReady => _getReveal().IsReady;
    public bool IsWorldViewportVisible => _getReveal().WorldViewportObserved;
    public int PortalMaterializationCount => _getPortalMaterializationCount();
    public int RenderPackPerformanceSampleCount =>
        _getRenderPackPerformanceSampleCount();
    public bool RenderPackFailedToRetail => _getRenderPackFailedToRetail();
    public RetailUiAutomationRenderPackStatus RenderPackStatus =>
        _getRenderPackStatus();
    public int FramebufferWidth => _getFramebufferSize().Width;
    public int FramebufferHeight => _getFramebufferSize().Height;

    public bool TrySelectRenderPack(string presetId, out string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        string normalized = presetId.ToLowerInvariant();
        if (normalized == "off")
            normalized = "retail";
        if (normalized is not ("retail" or "low" or "medium" or "high" or "auto"))
        {
            error = $"unknown render-pack preset '{presetId}'";
            return false;
        }

        (bool succeeded, string selectionError) = _selectRenderPack(normalized);
        if (succeeded && normalized != "retail")
            _lastEnabledRenderPackPreset = normalized;
        error = selectionError;
        return succeeded;
    }

    public bool TryDisableRenderPack(out string error)
    {
        if (_disableRenderPack is not null)
        {
            (bool succeeded, string disableError) = _disableRenderPack();
            error = disableError;
            return succeeded;
        }
        RetailUiAutomationRenderPackStatus current = RenderPackStatus;
        if (current.State == RetailUiAutomationRenderPackState.Active
            && !string.Equals(current.PackId, "retail", StringComparison.OrdinalIgnoreCase))
        {
            _lastEnabledRenderPackPreset = current.PresetId;
        }
        return TrySelectRenderPack("retail", out error);
    }

    public bool TryReenableRenderPack(out string error)
    {
        if (_reenableRenderPack is not null)
        {
            (bool succeeded, string reenableError) = _reenableRenderPack();
            error = reenableError;
            return succeeded;
        }
        if (string.IsNullOrWhiteSpace(_lastEnabledRenderPackPreset))
        {
            error = "render-pack re-enable requires a prior active enhanced selection";
            return false;
        }
        return TrySelectRenderPack(_lastEnabledRenderPackPreset, out error);
    }

    public bool TryResizeFramebuffer(int width, int height, out string error)
    {
        if (width < 320 || height < 240 || width > 8192 || height > 8192)
        {
            error = "automation framebuffer size must be within 320x240 and 8192x8192";
            return false;
        }
        (bool succeeded, string resizeError) = _resizeFramebuffer(width, height);
        error = resizeError;
        return succeeded;
    }

    public bool TryResetRenderPackPerformance(out string error)
    {
        if (RenderPackFailedToRetail)
        {
            error = string.Empty;
            return true;
        }
        (bool succeeded, string resetError) = _resetRenderPackPerformance();
        error = resetError;
        return succeeded;
    }

    public bool TryRequestClientClose(out string error)
    {
        if (_requestClientClose is null)
        {
            error = "client-close automation is unavailable";
            return false;
        }

        try
        {
            _requestClientClose();
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"client-close automation failed: {exception.Message}";
            return false;
        }
    }

    public bool TryRequestCheckpoint(
        string name,
        out IRetailUiAutomationCheckpoint? checkpoint,
        out string error)
    {
        checkpoint = null;
        if (!AutomationArtifactName.TryValidate(name, out error))
            return false;

        lock (_sync)
        {
            if (_disposed)
            {
                error = $"checkpoint '{name}' was rejected because world lifecycle automation is shutting down";
                return false;
            }

            var request = new WorldLifecycleCheckpointRequest(
                _requestOwner,
                checked(++_sequence),
                name);
            _requests.Enqueue(request);
            checkpoint = request;
            error = string.Empty;
            return true;
        }
    }

    public void CancelCheckpoint(IRetailUiAutomationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint is not WorldLifecycleCheckpointRequest request
            || !ReferenceEquals(request.Owner, _requestOwner))
        {
            throw new ArgumentException(
                "The checkpoint acknowledgement is not owned by this automation runtime.",
                nameof(checkpoint));
        }

        lock (_sync)
        {
            request.TrySetTerminal(
                RetailUiAutomationCheckpointStatus.Cancelled,
                $"checkpoint '{request.Name}' was cancelled before render-frame capture");
        }
    }

    public void Process(RenderFrameInput input, RenderFrameOutcome outcome)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            while (_requests.Count != 0)
            {
                WorldLifecycleCheckpointRequest request = _requests.Dequeue();
                if (request.Status != RetailUiAutomationCheckpointStatus.Pending)
                    continue;
                WriteCheckpoint(request, outcome);
            }
        }
    }

    private void WriteCheckpoint(
        WorldLifecycleCheckpointRequest request,
        RenderFrameOutcome outcome)
    {
        try
        {
            Directory.CreateDirectory(_artifactDirectory);
            var checkpoint = new WorldLifecycleCheckpoint(
                Sequence: request.Sequence,
                Name: request.Name,
                TimestampUtc: DateTime.UtcNow,
                ProcessId: Environment.ProcessId,
                Reveal: _getReveal(),
                EnvironmentOwnership: _getEnvironmentOwnership(),
                TransitOwnership: _getTransitOwnership(),
                Render: outcome,
                Resources: _captureResources(outcome));
            string json = JsonSerializer.Serialize(checkpoint, JsonOptions);
            string timelinePath = Path.Combine(
                _artifactDirectory,
                "world-lifecycle.checkpoints.jsonl");
            File.WriteAllText(
                Path.Combine(
                    _artifactDirectory,
                    $"checkpoint-{request.Name}.json"),
                json + Environment.NewLine);
            File.AppendAllText(timelinePath, json + Environment.NewLine);
            request.TrySetTerminal(
                RetailUiAutomationCheckpointStatus.Succeeded,
                error: null);
            _log(
                $"[world-gate] checkpoint name={request.Name} "
                + $"sequence={request.Sequence} path={timelinePath}");
        }
        catch (Exception exception)
        {
            request.TrySetTerminal(
                RetailUiAutomationCheckpointStatus.Failed,
                $"checkpoint '{request.Name}' sequence {request.Sequence} failed: "
                + exception.Message);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_requests.Count != 0)
            {
                WorldLifecycleCheckpointRequest request = _requests.Dequeue();
                request.TrySetTerminal(
                    RetailUiAutomationCheckpointStatus.Cancelled,
                    $"checkpoint '{request.Name}' was cancelled during world lifecycle automation shutdown");
            }
        }
    }

    public bool TryRequestScreenshot(string name, out string error) =>
        _screenshots.TryRequest(name, out error);

    public bool IsScreenshotComplete(string name) => _screenshots.IsComplete(name);

    public bool TryIsAutomationSignalPublished(
        string name,
        out bool published,
        out string error)
    {
        published = false;
        if (!AutomationArtifactName.TryValidate(name, out error))
            return false;

        published = File.Exists(Path.Combine(
            _artifactDirectory,
            "signals",
            name + ".signal"));
        error = string.Empty;
        return true;
    }
}
