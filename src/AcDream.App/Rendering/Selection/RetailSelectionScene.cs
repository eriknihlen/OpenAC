using System.Numerics;
using AcDream.App.Rendering.Scene;
using AcDream.Core.Selection;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Selection;

internal interface IWorldSceneSelectionFrame
{
    void BeginFrame(FrustumPlanes? preparedViewFrustum = null);

    void CompleteFrame();

    void AbortFrame();
}

internal sealed class RetailSelectionScene :
    IRetailSelectionRenderSink,
    IRetailSelectionRenderOracle,
    IRetailSelectionLightingSource,
    IWorldSceneSelectionFrame
{
    private readonly IRetailSelectionGeometrySource _geometry;
    private readonly RetailSelectionLightingPulse _lightingPulse;
    private List<RetailSelectionPart> _building = new();
    private List<RetailSelectionPart> _published = new();
    private readonly HashSet<PartKey> _buildingKeys = new();
    private FrustumPlanes? _viewFrustum;
    private ICurrentRenderSelectionObserver? _currentRenderSceneObserver;
    private bool _frameOpen;

    private readonly record struct PartKey(uint LocalEntityId, int PartIndex, uint GfxObjId);

    public RetailSelectionScene(
        IRetailSelectionGeometrySource geometry,
        RetailSelectionLightingPulse? lightingPulse = null)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _lightingPulse = lightingPulse ?? new RetailSelectionLightingPulse();
    }

    public void BeginLightingPulse(uint serverGuid, uint localEntityId)
        => _lightingPulse.Start(serverGuid, localEntityId);

    public void TickLighting()
        => _lightingPulse.Tick();

    public bool TryGetLighting(
        uint serverGuid,
        uint localEntityId,
        out RetailSelectionLighting lighting)
        => _lightingPulse.TryGet(serverGuid, localEntityId, out lighting);

    internal void SetCurrentRenderSceneObserver(
        ICurrentRenderSelectionObserver? observer) =>
        _currentRenderSceneObserver = observer;

    public void Reset()
    {
        _currentRenderSceneObserver?.AbortSelectionFrame();
        _frameOpen = false;
        _building.Clear();
        _published.Clear();
        _buildingKeys.Clear();
        _viewFrustum = null;
        _lightingPulse.Clear();
    }

    public void BeginFrame(FrustumPlanes? preparedViewFrustum = null)
    {
        if (_frameOpen)
        {
            throw new InvalidOperationException(
                "The retail selection scene cannot begin a second frame before completing or aborting the first.");
        }

        _frameOpen = true;
        _building.Clear();
        _buildingKeys.Clear();
        _viewFrustum = preparedViewFrustum;
        _currentRenderSceneObserver?.BeginSelectionFrame();
    }

    public void SetViewFrustum(FrustumPlanes viewFrustum)
        => _viewFrustum = viewFrustum;

    public void AddVisiblePart(
        WorldEntity entity,
        int partIndex,
        uint gfxObjId,
        Matrix4x4 partWorld)
    {
        ArgumentNullException.ThrowIfNull(entity);
        AddVisiblePart(
            entity.ServerGuid,
            entity.Id,
            partIndex,
            gfxObjId,
            partWorld);
    }

    public void AddVisiblePart(
        uint serverGuid,
        uint localEntityId,
        int partIndex,
        uint gfxObjId,
        Matrix4x4 partWorld)
    {
        if (!_frameOpen)
            return;
        if (serverGuid == 0u)
            return;
        if (!_buildingKeys.Add(new PartKey(
                localEntityId,
                partIndex,
                gfxObjId)))
            return;
        if (!TryCreateVisiblePart(
                serverGuid,
                localEntityId,
                partIndex,
                gfxObjId,
                partWorld,
                out RetailSelectionPart part))
        {
            return;
        }

        _building.Add(part);
        _currentRenderSceneObserver?.ObserveSelectionPart(
            serverGuid,
            localEntityId,
            partIndex,
            gfxObjId,
            partWorld,
            part.Mesh);
    }

    public bool TryCreateVisiblePart(
        uint serverGuid,
        uint localEntityId,
        int partIndex,
        uint gfxObjId,
        Matrix4x4 partWorld,
        out RetailSelectionPart part)
    {
        if (serverGuid == 0u)
        {
            part = default;
            return false;
        }

        RetailSelectionMesh? mesh = _geometry.Resolve(gfxObjId);
        if (mesh is null
            || _viewFrustum is not { } frustum
            || !DrawingSphereIntersectsFrustum(
                mesh,
                partWorld,
                frustum))
        {
            part = default;
            return false;
        }

        part = new RetailSelectionPart(
            serverGuid,
            localEntityId,
            partIndex,
            partWorld,
            mesh);
        return true;
    }

    public void CompleteFrame()
    {
        if (!_frameOpen)
        {
            throw new InvalidOperationException(
                "The retail selection scene cannot complete without an open frame.");
        }

        _frameOpen = false;
        (_published, _building) = (_building, _published);
        _currentRenderSceneObserver?.CompleteSelectionFrame();
    }

    public void AbortFrame()
    {
        if (!_frameOpen)
            return;

        _frameOpen = false;
        _currentRenderSceneObserver?.AbortSelectionFrame();
        _building.Clear();
        _buildingKeys.Clear();
        _viewFrustum = null;
    }

    public RetailSelectionHit? Pick(
        float mouseX,
        float mouseY,
        Vector2 viewport,
        Matrix4x4 view,
        Matrix4x4 projection,
        uint skipServerGuid)
    {
        if (viewport.X <= 0f || viewport.Y <= 0f)
            return null;
        var ray = WorldPicker.BuildRay(
            mouseX, mouseY, viewport.X, viewport.Y, view, projection);
        return RetailWorldPicker.Pick(
            ray.Origin, ray.Direction, _published, skipServerGuid);
    }

    internal static bool DrawingSphereIntersectsFrustum(
        RetailSelectionMesh mesh,
        Matrix4x4 localToWorld,
        FrustumPlanes frustum)
    {
        Vector3 center = Vector3.Transform(mesh.SphereCenter, localToWorld);
        float scaleX = new Vector3(localToWorld.M11, localToWorld.M12, localToWorld.M13).Length();
        float scaleY = new Vector3(localToWorld.M21, localToWorld.M22, localToWorld.M23).Length();
        float scaleZ = new Vector3(localToWorld.M31, localToWorld.M32, localToWorld.M33).Length();
        float radius = mesh.SphereRadius * MathF.Max(scaleX, MathF.Max(scaleY, scaleZ));
        return TestPlane(frustum.Left, center, radius)
            && TestPlane(frustum.Right, center, radius)
            && TestPlane(frustum.Bottom, center, radius)
            && TestPlane(frustum.Top, center, radius)
            && TestPlane(frustum.Near, center, radius)
            && TestPlane(frustum.Far, center, radius);
    }

    private static bool TestPlane(Vector4 plane, Vector3 center, float radius)
        => plane.X * center.X + plane.Y * center.Y + plane.Z * center.Z + plane.W >= -radius;
}
