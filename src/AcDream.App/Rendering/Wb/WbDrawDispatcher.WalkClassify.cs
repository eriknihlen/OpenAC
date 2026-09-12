using System.Numerics;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Selection;
using AcDream.App.Rendering.Walk;
using AcDream.Core.Lighting;
using AcDream.Core.Meshing;
using AcDream.Core.World;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering.Wb;

public sealed partial class WbDrawDispatcher
{
    private readonly HashSet<WalkDrawnPartKey> _walkDrawnParts = new();
    private bool _walkPartFrameActive;
    private readonly record struct WalkDrawnPartKey(
        RenderProjectionId ProjectionId,
        int PartIndex);

    internal void BeginWalkPartFrame()
    {
        if (_walkPartFrameActive)
        {
            throw new InvalidOperationException(
                "A walk part frame was opened before the previous scope closed.");
        }

        _missRequested.Clear();
        _walkDrawnParts.Clear();
        _walkPartFrameActive = true;
    }

    internal void AdvanceWalkPartPassStamp()
    {
        if (!_walkPartFrameActive)
        {
            throw new InvalidOperationException(
                "The walk part pass stamp cannot advance outside an active frame.");
        }

        _walkDrawnParts.Clear();
    }

    internal void EndWalkPartFrame()
    {
        _walkPartFrameActive = false;
        _walkDrawnParts.Clear();
    }

    private bool TryStampWalkPart(
        in RenderProjectionRecord projection,
        int partIndex) =>
        !_walkPartFrameActive
        || projection.EntityPayload.CasterIdentity
            == RenderCasterIdentityKind.LocalPlayer
        || _walkDrawnParts.Add(new WalkDrawnPartKey(projection.Id, partIndex));

    internal readonly record struct WalkClassifiedBatch(
        GroupKey Key,
        Matrix4x4 Transform,
        uint ClipSlot,
        InstanceLightSet Lights,
        uint IndoorFlag,
        float Alpha,
        Vector2 SelectionLighting,
        uint DetailCategory,
        bool IsOpaque,
        Vector3 LocalSortCenter,
        float SortDistanceSq = 0f);

    internal readonly record struct WalkClassifiedSelectionPart(
        uint ServerGuid,
        uint LocalEntityId,
        int PartIndex,
        uint GfxObjId,
        Matrix4x4 LocalToWorld);

    internal readonly record struct WalkCachedPart(
        ObjectRenderData RenderData,
        WalkClassifiedSelectionPart Selection,
        int BatchStart,
        int BatchCount);

    internal long WalkMeshAvailabilityVersion =>
        _meshAdapter.MeshManager?.RenderDataAvailabilityVersion ?? 0;

    internal bool WalkClassificationPending { get; private set; }

    /// <summary>The walk's building-shell residency question. A shell is
    /// drawable once its geometry is prepared. Two answers are permanent
    /// refusals rather than waits: a level authored blank (id zero), and an id
    /// the mesh layer classifies as a never-drawn placement marker, whose own
    /// ladder resolves to nothing at every viewing distance. Both are the same
    /// "this level has no object" state, and a building that selects one draws
    /// nothing at all — no shell and no look-in — so neither asks for
    /// preparation. Only a genuine miss does, exactly once per frame and by the
    /// same route classification uses, so a waiting building cannot starve the
    /// request that would let it in.</summary>
    public bool IsShellDrawable(uint gfxObjId)
    {
        if (gfxObjId == 0)
            return false;
        if (_meshAdapter.MeshManager is null)
            return true;   // no mesh source in this composition: nothing to wait for
        if (_meshAdapter.IsRuntimeHiddenMarker(gfxObjId))
            return false;  // never drawn at any distance: a refusal, not a wait
        if (_meshAdapter.TryGetRenderData(gfxObjId) is not null)
            return true;
        if (_missRequested.Add(gfxObjId))
            _meshAdapter.EnsureLoaded(gfxObjId);
        return false;
    }

    internal bool AdmitCachedWalkPart(
        in RenderProjectionRecord record,
        in WalkCachedPart part,
        IWalkLookInViewSource views,
        int routeIndex) =>
        ResolvePartVisible(views, routeIndex, part.RenderData,
            part.Selection.LocalToWorld, out _, out _, out _)
        && TryStampWalkPart(in record, part.Selection.PartIndex);

    internal void ResolveCachedWalkLighting(
        in RenderProjectionRecord record,
        uint tupleLandblockId,
        out InstanceLightSet lights,
        out uint indoorFlag,
        out Vector2 selection)
    {
        RenderInstanceCandidate entity = RenderInstanceCandidate.FromProjection(
            in record, tupleLandblockId, animated: false);
        ResolveWalkLightSet(in entity, out lights, out bool indoor);
        indoorFlag = indoor ? 1u : 0u;
        selection = _selectionLighting?.TryGetLighting(
            entity.ServerGuid, entity.LocalEntityId, out RetailSelectionLighting lighting) == true
            ? new Vector2(lighting.Luminosity, lighting.Diffuse)
            : new Vector2(0f, 1f);
    }

    private bool TryClassifyBatch(
        ObjectRenderData renderData,
        int batchIndex,
        in RenderInstanceCandidate entity,
        MeshRef meshRef,
        PaletteCompositeIdentity paletteIdentity,
        float opacityMultiplier,
        bool entityHasCutoutSubset,
        out GroupKey key,
        out bool compositePending)
    {
        key = default;
        compositePending = false;
        ObjectRenderBatch batch = renderData.Batches[batchIndex];

        if (!RetailUntexturedSubsetPolicy.Draws(entity.IsBuildingShell, batch.Key.IsSolid))
            return false;

        TranslucencyKind translucency = batch.Translucency;

        if (opacityMultiplier < 1.0f && IsOpaque(translucency))
            translucency = TranslucencyKind.AlphaBlend;

        ResolvedTexture texture = ResolveTexture(
            in entity, meshRef, batch, paletteIdentity, out compositePending);
        if (!texture.Slot.IsAssigned)
            return false;

        uint foliageFlags = FoliageWindClassification.Classify(
            entity.LocalEntityId,
            FoliageWindExclusions.Contains(meshRef.GfxObjId),
            batch.Translucency,
            entityHasCutoutSubset);
        key = new GroupKey(
            batch.FirstIndex, (int)batch.BaseVertex,
            batch.IndexCount, texture.Slot, texture.Layer, translucency,
            MaterialState: batch.MaterialState,
            FoliageFlags: foliageFlags,
            SurfaceOpacity: batch.SurfaceOpacity,
            CullMode: batch.CullMode);
        return true;
    }

    internal void ClassifyEntityForWalk(
        in RenderProjectionRecord projection,
        uint tupleLandblockId,
        List<WalkClassifiedBatch> batches,
        List<WalkClassifiedSelectionPart> selectionParts,
        bool liveDynamic = false,
        IWalkLookInViewSource? lookInViews = null,
        int lookInRouteIndex = -1,
        uint lookInCellId = 0,
        WalkBuildingSelection? buildingSelection = null,
        Matrix4x4 buildingPartTransform = default,
        List<WalkCachedPart>? retainedParts = null)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(selectionParts);
        WalkClassificationPending = false;

        if ((projection.Flags & RenderProjectionFlags.Draw) == 0)
            return;

        RenderInstanceCandidate entity =
            RenderInstanceCandidate.FromProjection(
                in projection,
                tupleLandblockId,
                animated: liveDynamic);

        (uint slot, bool culled) = ResolveSlotForFrame();
        if (culled)
            return;

        ResolveWalkLightSet(in entity, out InstanceLightSet lights, out bool indoor);
        Vector2 selectionLighting =
            _selectionLighting?.TryGetLighting(
                entity.ServerGuid, entity.LocalEntityId, out RetailSelectionLighting lighting) == true
                ? new Vector2(lighting.Luminosity, lighting.Diffuse)
                : new Vector2(0f, 1f);
        uint detailCategory = entity.IsBuildingShell ? 1u : 0u;

        PaletteCompositeIdentity paletteIdentity = default;
        if (entity.PaletteOverride is not null)
            paletteIdentity = TextureCache.GetPaletteIdentity(entity.PaletteOverride);

        IReadOnlyList<MeshRef>? meshRefs = projection.EntityPayload.MeshRefs;
        if (buildingSelection is null && meshRefs is null)
            return;

        IReadOnlyDictionary<uint, uint>? buildingSurfaceOverrides =
            buildingSelection is not null && meshRefs is { Count: > 0 }
                ? meshRefs[0].SurfaceOverrides
                : null;
        int meshRefCount = buildingSelection is null ? meshRefs!.Count : 1;
        for (int partIndex = 0; partIndex < meshRefCount; partIndex++)
        {
            MeshRef meshRef = buildingSelection is WalkBuildingSelection selected
                ? new MeshRef(selected.GfxObjId, buildingPartTransform)
                {
                    SurfaceOverrides = buildingSurfaceOverrides,
                }
                : meshRefs![partIndex];
            if (_meshAdapter.IsRuntimeHiddenMarker(meshRef.GfxObjId))
                continue;

            ObjectRenderData? renderData = _meshAdapter.TryGetRenderData(meshRef.GfxObjId);
            if (renderData is null)
            {
                WalkClassificationPending = true;
                if (_missRequested.Add(meshRef.GfxObjId))
                    _meshAdapter.EnsureLoaded(meshRef.GfxObjId);
                continue;
            }

            if (buildingSelection is null
                && renderData.IsSetup
                && renderData.SetupParts.Count > 0)
            {
                bool entityHasCutoutSubset = FoliageWindClassification.ComputeEntityHasCutoutSubset(
                    renderData.SetupParts,
                    _meshAdapter,
                    static (adapter, part) => adapter.TryGetRenderData(part.GfxObjId)
                        is { HasCutoutSubset: true });

                for (int setupPartIndex = 0; setupPartIndex < renderData.SetupParts.Count; setupPartIndex++)
                {
                    (ulong gfxObjId, Matrix4x4 partTransform) = renderData.SetupParts[setupPartIndex];
                    if (_meshAdapter.IsRuntimeHiddenMarker((uint)gfxObjId))
                        continue;

                    ObjectRenderData? partData = _meshAdapter.TryGetRenderData(gfxObjId);
                    if (partData is null)
                    {
                        WalkClassificationPending = true;
                        if (_missRequested.Add(gfxObjId))
                            _meshAdapter.EnsureLoaded(gfxObjId);
                        continue;
                    }

                    float opacity = liveDynamic
                        ? WalkPartOpacity(
                            entity.ServerGuid,
                            entity.LocalEntityId,
                            (uint)setupPartIndex)
                        : 1f;
                    if (opacity <= 0f)
                    {
                        continue;
                    }

                    Matrix4x4 restPose = partTransform * meshRef.PartTransform;
                    Matrix4x4 model = restPose * entity.RootWorld;
                    int selectionPartIndex = unchecked((partIndex << 16) | (setupPartIndex & 0xFFFF));

                    bool visible = ResolvePartVisible(
                        lookInViews,
                        lookInRouteIndex,
                        partData,
                        model,
                        out _,
                        out _,
                        out _);
                    bool firstAdmission = visible
                        && (retainedParts is not null || TryStampWalkPart(in projection, selectionPartIndex));
                    if (!firstAdmission)
                        continue;
                    int batchStart = batches.Count;
                    EmitClassifiedBatches(
                        partData, model, in entity, meshRef, paletteIdentity,
                        entityHasCutoutSubset, slot, lights, indoor,
                        selectionLighting, detailCategory, opacity,
                        batches);
                    selectionParts.Add(new WalkClassifiedSelectionPart(
                        entity.ServerGuid, entity.LocalEntityId, selectionPartIndex,
                        (uint)gfxObjId, model));
                    retainedParts?.Add(new WalkCachedPart(
                        partData, selectionParts[^1], batchStart, batches.Count - batchStart));
                }
            }
            else
            {
                float opacity = liveDynamic
                    ? WalkPartOpacity(
                        entity.ServerGuid,
                        entity.LocalEntityId,
                        (uint)partIndex)
                    : 1f;
                if (opacity <= 0f)
                {
                    continue;
                }

                Matrix4x4 model = meshRef.PartTransform * entity.RootWorld;
                bool visible = ResolvePartVisible(
                    lookInViews,
                    lookInRouteIndex,
                    renderData,
                    model,
                    out _,
                    out _,
                    out _);
                bool firstAdmission = visible
                    && (retainedParts is not null || TryStampWalkPart(in projection, partIndex));
                if (!firstAdmission)
                    continue;
                int batchStart = batches.Count;
                EmitClassifiedBatches(
                    renderData, model, in entity, meshRef, paletteIdentity,
                    entityHasCutoutSubsetOverride: null, slot, lights, indoor,
                    selectionLighting, detailCategory, opacity,
                    batches);
                selectionParts.Add(new WalkClassifiedSelectionPart(
                    entity.ServerGuid, entity.LocalEntityId, partIndex,
                    (uint)meshRef.GfxObjId, model));
                retainedParts?.Add(new WalkCachedPart(
                    renderData, selectionParts[^1], batchStart, batches.Count - batchStart));
            }
        }
    }

    private float WalkPartOpacity(
        uint serverGuid,
        uint localEntityId,
        uint setupPartIndex)
    {
        float opacity = EntityOpacity(serverGuid);
        if (opacity <= 0f)
            return 0f;
        if (!_translucencyFades.TryGetCurrentValue(
                localEntityId,
                setupPartIndex,
                out float translucency))
        {
            return opacity;
        }

        return translucency >= 1f
            ? 0f
            : opacity * (1f - translucency);
    }

    private void ResolveWalkLightSet(
        in RenderInstanceCandidate entity,
        out InstanceLightSet lights,
        out bool indoor)
    {
        indoor = IndoorObjectReceivesTorches(entity.ParentCell);
        lights = InstanceLightSet.Disabled;
        IReadOnlyList<LightSource>? snapshot = _pointSnapshot;
        if (!indoor || snapshot is null || snapshot.Count == 0)
            return;

        Vector3 center =
            (entity.Bounds.Minimum + entity.Bounds.Maximum) * 0.5f;
        float radius =
            (entity.Bounds.Maximum - entity.Bounds.Minimum)
            .Length() * 0.5f;
        Span<int> selected =
            stackalloc int[LightManager.MaxLightsPerObject];
        selected.Fill(-1);
        LightManager.SelectForObject(
            snapshot,
            center,
            radius,
            selected);
        lights = InstanceLightSet.From(selected);
    }

    private static bool ResolvePartVisible(
        IWalkLookInViewSource? lookInViews,
        int routeIndex,
        ObjectRenderData renderData,
        Matrix4x4 localToWorld,
        out Vector3 sphereCenter,
        out float sphereRadius,
        out bool hasSphere)
    {
        sphereCenter = default;
        sphereRadius = 0f;
        hasSphere = false;
        if (lookInViews is null)
            return true;

        if (renderData.SelectionSphere is not { Radius: > 0f } sphere)
        {
            return lookInViews.SphereVisibleInLookInTurn(
                routeIndex, Vector3.Zero, radius: 0f, testSphere: false);
        }

        hasSphere = true;
        TransformDrawingSphere(sphere, localToWorld, out sphereCenter, out sphereRadius);
        return lookInViews.SphereVisibleInLookInTurn(routeIndex, in sphereCenter, sphereRadius);
    }

    internal static bool LookInDrawingSphereVisible(
        IWalkLookInViewSource lookInViews,
        int routeIndex,
        DatReaderWriter.Types.Sphere sphere,
        Matrix4x4 localToWorld,
        out Vector3 center,
        out float radius)
    {
        ArgumentNullException.ThrowIfNull(lookInViews);
        ArgumentNullException.ThrowIfNull(sphere);

        TransformDrawingSphere(sphere, localToWorld, out center, out radius);
        return lookInViews.SphereVisibleInLookInTurn(
            routeIndex,
            in center,
            radius);
    }

    private static void TransformDrawingSphere(
        DatReaderWriter.Types.Sphere sphere,
        Matrix4x4 localToWorld,
        out Vector3 center,
        out float radius)
    {
        center = Vector3.Transform(sphere.Origin, localToWorld);
        float scaleX = new Vector3(
            localToWorld.M11,
            localToWorld.M12,
            localToWorld.M13).Length();
        float scaleY = new Vector3(
            localToWorld.M21,
            localToWorld.M22,
            localToWorld.M23).Length();
        float scaleZ = new Vector3(
            localToWorld.M31,
            localToWorld.M32,
            localToWorld.M33).Length();
        radius = sphere.Radius
            * MathF.Max(scaleX, MathF.Max(scaleY, scaleZ));
    }

    private void EmitClassifiedBatches(
        ObjectRenderData renderData,
        Matrix4x4 model,
        in RenderInstanceCandidate entity,
        MeshRef meshRef,
        PaletteCompositeIdentity paletteIdentity,
        bool? entityHasCutoutSubsetOverride,
        uint slot,
        InstanceLightSet lights,
        bool indoor,
        Vector2 selectionLighting,
        uint detailCategory,
        float opacity,
        List<WalkClassifiedBatch> sink)
    {
        bool entityHasCutoutSubset = entityHasCutoutSubsetOverride ?? renderData.HasCutoutSubset;
        for (int batchIdx = 0; batchIdx < renderData.Batches.Count; batchIdx++)
        {
            bool survives = TryClassifyBatch(
                renderData, batchIdx, in entity, meshRef, paletteIdentity,
                opacityMultiplier: opacity, entityHasCutoutSubset,
                out GroupKey key, out bool compositePending);
            WalkClassificationPending |= compositePending;
            if (!survives)
                continue;

            sink.Add(new WalkClassifiedBatch(
                key, model, slot, lights, indoor ? 1u : 0u, Alpha: opacity,
                selectionLighting, detailCategory, IsOpaque: IsOpaque(key.Translucency),
                LocalSortCenter: renderData.SortCenter));
        }
    }

    internal void PublishWalkSelectionPart(in WalkClassifiedSelectionPart part) =>
        _selectionSink?.AddVisiblePart(
            part.ServerGuid, part.LocalEntityId, part.PartIndex, part.GfxObjId, part.LocalToWorld);

    internal void SubmitWalkAlphaInstance(
        in WalkClassifiedBatch batch,
        Matrix4x4 viewProjection)
    {
        RetailAlphaQueue queue = _alphaQueue
            ?? throw new InvalidOperationException(
                "SubmitWalkAlphaInstance requires an active RetailAlphaQueue.");

        if (_deferredAlpha.Count == 0)
            _deferredAlphaViewProjection = viewProjection;
        else if (_deferredAlphaViewProjection != viewProjection)
            throw new InvalidOperationException(
                "One retail alpha scope cannot combine different view-projection matrices.");

        var candidate = new DeferredAlphaInstance(
            batch.Key, batch.Transform, batch.ClipSlot, batch.Lights,
            batch.IndoorFlag, batch.DetailCategory, batch.Alpha, batch.SelectionLighting);
        SubmitToAlphaQueue(
            queue, batch.Key.Translucency, in candidate, batch.DetailCategory == 1u, viewProjection);
    }
}
