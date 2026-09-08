using System.Numerics;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.World;
using AcDream.Core.Lighting;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal interface ICreatureAppraisalRenderer
{
    void SetCreature(
        WorldEntity? creature,
        Vector3 boundsMin,
        Vector3 boundsMax);

    uint Render(int width, int height);
}

internal interface ICreatureAppraisalFrameView
{
    bool TryGetVisibleTarget(
        out uint serverGuid,
        out int width,
        out int height);

    void SetTextureHandle(uint textureHandle);
}

internal interface ICreatureAppraisalEntityLookup
{
    bool TryGet(uint serverGuid, out WorldEntity entity);
}

internal interface ICreatureAppraisalCloneFactory
{
    bool TrySynchronize(
        uint serverGuid,
        WorldEntity? currentClone,
        out WorldEntity? synchronizedClone,
        out Vector3 boundsMin,
        out Vector3 boundsMax);
}

internal sealed class CreatureAppraisalFramePresenter :
    IPrivateEntityViewportFrame
{
    private readonly ICreatureAppraisalRenderer _renderer;
    private readonly ICreatureAppraisalFrameView _view;
    private readonly ICreatureAppraisalCloneFactory _factory;
    private WorldEntity? _clone;
    private uint _serverGuid;

    public CreatureAppraisalFramePresenter(
        ICreatureAppraisalRenderer renderer,
        ICreatureAppraisalFrameView view,
        ICreatureAppraisalCloneFactory factory)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public void Render()
    {
        if (!_view.TryGetVisibleTarget(
                out uint serverGuid,
                out int width,
                out int height))
        {
            return;
        }

        if (_serverGuid != serverGuid)
        {
            _serverGuid = serverGuid;
            _clone = null;
        }

        if (!_factory.TrySynchronize(
                serverGuid,
                _clone,
                out WorldEntity? synchronized,
                out Vector3 boundsMin,
                out Vector3 boundsMax))
        {
            _clone = null;
            _renderer.SetCreature(null, Vector3.Zero, Vector3.Zero);
            _view.SetTextureHandle(0u);
            return;
        }

        _clone = synchronized;
        _renderer.SetCreature(_clone, boundsMin, boundsMax);
        _view.SetTextureHandle(_renderer.Render(width, height));
    }
}

internal sealed class RetailCreatureAppraisalFrameView :
    ICreatureAppraisalFrameView
{
    private readonly UiViewport _viewport;
    private readonly UiElement _windowFrame;
    private readonly AppraisalUiController _controller;

    public RetailCreatureAppraisalFrameView(
        UiViewport viewport,
        UiElement windowFrame,
        AppraisalUiController controller)
    {
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _windowFrame = windowFrame ?? throw new ArgumentNullException(nameof(windowFrame));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public bool TryGetVisibleTarget(
        out uint serverGuid,
        out int width,
        out int height)
    {
        serverGuid = 0u;
        width = 0;
        height = 0;
        if (_controller.ActiveView is not (
                AppraisalView.Creature or AppraisalView.Character))
        {
            return false;
        }
        if (!IsEffectivelyVisible(_windowFrame))
            return false;
        if (!IsEffectivelyVisible(_viewport))
            return false;
        if (_controller.CurrentObjectId == 0u)
            return false;

        serverGuid = _controller.CurrentObjectId;
        width = (int)_viewport.Width;
        height = (int)_viewport.Height;
        return width > 0 && height > 0;
    }

    public void SetTextureHandle(uint textureHandle) =>
        _viewport.TextureSlot = UiTextureTableHandle.ToSlot(textureHandle);

    private static bool IsEffectivelyVisible(UiElement element)
    {
        for (UiElement? current = element; current is not null; current = current.Parent)
            if (!current.Visible)
                return false;
        return true;
    }
}

internal sealed class LiveCreatureAppraisalEntityLookup :
    ICreatureAppraisalEntityLookup
{
    private readonly LiveEntityRuntime _liveEntities;

    public LiveCreatureAppraisalEntityLookup(LiveEntityRuntime liveEntities)
    {
        _liveEntities = liveEntities
            ?? throw new ArgumentNullException(nameof(liveEntities));
    }

    public bool TryGet(uint serverGuid, out WorldEntity entity) =>
        _liveEntities.TryGetWorldEntity(serverGuid, out entity);
}

internal sealed class RetailCreatureAppraisalCloneFactory :
    ICreatureAppraisalCloneFactory
{
    private readonly ICreatureAppraisalEntityLookup _entities;

    public RetailCreatureAppraisalCloneFactory(
        ICreatureAppraisalEntityLookup entities)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
    }

    public bool TrySynchronize(
        uint serverGuid,
        WorldEntity? currentClone,
        out WorldEntity? synchronizedClone,
        out Vector3 boundsMin,
        out Vector3 boundsMax)
    {
        synchronizedClone = null;
        boundsMin = Vector3.Zero;
        boundsMax = Vector3.Zero;
        if (!_entities.TryGet(serverGuid, out WorldEntity source))
            return false;
        if (source.MeshRefs.Count == 0)
            return false;

        WorldEntity clone = currentClone is not null
            && currentClone.SourceGfxObjOrSetupId == source.SourceGfxObjOrSetupId
                ? currentClone
                : CreatureAppraisalEntityBuilder.Build(source);

        clone.ApplyAppearance(
            source.MeshRefs,
            source.PaletteOverride,
            source.PartOverrides);
        clone.IsDrawVisible = source.IsDrawVisible;
        clone.IsAncestorDrawVisible = source.IsAncestorDrawVisible;
        if (source.HasLocalBounds)
            clone.SetLocalBounds(source.LocalBoundMin, source.LocalBoundMax);

        (boundsMin, boundsMax) =
            CreatureAppraisalEntityBuilder.RotatedBounds(source);
        synchronizedClone = clone;
        return true;
    }
}

internal static class CreatureAppraisalEntityBuilder
{
    public const uint RenderId = 0xDA11_D022u;
    public const uint ServerGuid = 0xDA11_D021u;
    private const float HeadingDegrees = 191.367905f;
    private static readonly Quaternion Heading = Quaternion.CreateFromAxisAngle(
        Vector3.UnitZ,
        -HeadingDegrees * (MathF.PI / 180f));

    public static WorldEntity Build(WorldEntity source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = new WorldEntity
        {
            Id = RenderId,
            ServerGuid = ServerGuid,
            SourceGfxObjOrSetupId = source.SourceGfxObjOrSetupId,
            Position = Vector3.Zero,
            Rotation = Heading,
            MeshRefs = source.MeshRefs,
            PaletteOverride = source.PaletteOverride,
            PartOverrides = source.PartOverrides,
            HiddenPartsMask = source.HiddenPartsMask,
            Scale = source.Scale,
            ParentCellId = null,
            EffectCellId = null,
        };
        if (source.HasLocalBounds)
            clone.SetLocalBounds(source.LocalBoundMin, source.LocalBoundMax);
        return clone;
    }

    public static (Vector3 Min, Vector3 Max) RotatedBounds(
        WorldEntity source)
    {
        Vector3 min;
        Vector3 max;
        if (source.HasLocalBounds)
        {
            min = source.LocalBoundMin;
            max = source.LocalBoundMax;
        }
        else
        {
            min = new Vector3(-0.5f, -0.5f, 0f);
            max = new Vector3(0.5f, 0.5f, 2f);
            foreach (MeshRef mesh in source.MeshRefs)
            {
                Vector3 p = mesh.PartTransform.Translation;
                min = Vector3.Min(min, p - new Vector3(0.5f));
                max = Vector3.Max(max, p + new Vector3(0.5f));
            }
        }

        Vector3 rotatedMin = default;
        Vector3 rotatedMax = default;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = new(
                (corner & 1) == 0 ? min.X : max.X,
                (corner & 2) == 0 ? min.Y : max.Y,
                (corner & 4) == 0 ? min.Z : max.Z);
            point = Vector3.Transform(point, Heading);
            if (corner == 0)
                rotatedMin = rotatedMax = point;
            else
            {
                rotatedMin = Vector3.Min(rotatedMin, point);
                rotatedMax = Vector3.Max(rotatedMax, point);
            }
        }
        return (rotatedMin, rotatedMax);
    }
}

internal sealed class CreatureAppraisalCamera :
    IPrivateEntityViewportCamera
{
    private const float FitDistanceFactor = 1.20710683f;
    private Vector3 _boundsMin = new(-0.5f, -0.5f, 0f);
    private Vector3 _boundsMax = new(0.5f, 0.5f, 2f);
    private float _aspect = 1f;
    private Vector3 _eye;

    public CreatureAppraisalCamera() => Recalculate();

    public float Aspect
    {
        get => _aspect;
        set
        {
            _aspect = float.IsFinite(value) && value > 0f ? value : 1f;
            Recalculate();
        }
    }

    public Vector3 Eye => _eye;
    public float FovRadians { get; set; } = MathF.PI / 4f;
    public float Near { get; set; } = 0.1f;
    public float Far { get; set; } = 2048f;

    public Matrix4x4 View =>
        Matrix4x4.CreateLookAt(_eye, _eye + Vector3.UnitY, Vector3.UnitZ);

    public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(
        FovRadians,
        _aspect,
        Near,
        Far);

    public void Fit(Vector3 min, Vector3 max)
    {
        _boundsMin = Vector3.Min(min, max);
        _boundsMax = Vector3.Max(min, max);
        Recalculate();
    }

    private void Recalculate()
    {
        Vector3 span = Vector3.Max(
            _boundsMax - _boundsMin,
            new Vector3(0.001f));
        Vector3 center = (_boundsMin + _boundsMax) * 0.5f;
        float fittedVertical = MathF.Max(span.Z, span.X / _aspect);
        float distance = fittedVertical * FitDistanceFactor + span.Y * 0.5f;
        _eye = new Vector3(center.X, center.Y - distance, center.Z);
    }
}

/// <summary>Examination-specific facade over the shared private renderer.</summary>
internal sealed class CreatureAppraisalViewportRenderer :
    IUiViewportRenderer,
    ICreatureAppraisalRenderer,
    IDisposable
{
    private readonly CreatureAppraisalCamera _camera = new();
    private readonly PrivateEntityViewportRenderer _renderer;

    public CreatureAppraisalViewportRenderer(
        IWorldPassScope scope,
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        ICurrentGpuFrameSource frames,
        WbDrawDispatcher dispatcher,
        SceneLightingUboBinding lightUbo,
        IEntityTextureLifetime textureLifetime,
        IWbMeshAdapter meshAdapter)
    {
        _renderer = new PrivateEntityViewportRenderer(
            scope,
            device,
            frames,
            dispatcher,
            lightUbo,
            textureLifetime,
            meshAdapter,
            CreatureAppraisalEntityBuilder.RenderId,
            _camera,
            "creature examination");
    }

    public bool TextureIsBottomUp => _renderer.TextureIsBottomUp;

    public void SetCreature(
        WorldEntity? creature,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        if (creature is not null)
            _camera.Fit(boundsMin, boundsMax);
        _renderer.SetEntity(creature);
    }

    public uint Render(int width, int height) =>
        _renderer.Render(width, height);

    public void Dispose() => _renderer.Dispose();
}
