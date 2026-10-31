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

    /// <summary>The objects the creature holds, drawn with it.</summary>
    void SetAttachments(IReadOnlyList<WorldEntity> attachments) { }

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

    /// <summary>Adds the objects <paramref name="parent"/> holds right now.</summary>
    void CollectAttachments(WorldEntity parent, List<WorldEntity> into) { }
}

internal interface ICreatureAppraisalCloneFactory
{
    bool TrySynchronize(
        uint serverGuid,
        WorldEntity? currentClone,
        out WorldEntity? synchronizedClone,
        out Vector3 boundsMin,
        out Vector3 boundsMax);

    /// <summary>
    /// Copies of the objects the creature holds, placed on the copy the
    /// way they sit on the creature. Called after <see cref="TrySynchronize"/>.
    /// </summary>
    IReadOnlyList<WorldEntity> SynchronizeAttachments(uint serverGuid) => [];
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
            _renderer.SetAttachments([]);
            _view.SetTextureHandle(0u);
            return;
        }

        _clone = synchronized;
        _renderer.SetCreature(_clone, boundsMin, boundsMax);
        _renderer.SetAttachments(_factory.SynchronizeAttachments(serverGuid));
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
        (width, height) = _viewport.RenderTargetSize;
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
    private readonly EquippedChildRenderController? _equipped;

    public LiveCreatureAppraisalEntityLookup(
        LiveEntityRuntime liveEntities,
        EquippedChildRenderController? equipped = null)
    {
        _liveEntities = liveEntities
            ?? throw new ArgumentNullException(nameof(liveEntities));
        _equipped = equipped;
    }

    public bool TryGet(uint serverGuid, out WorldEntity entity) =>
        _liveEntities.TryGetWorldEntity(serverGuid, out entity);

    public void CollectAttachments(WorldEntity parent, List<WorldEntity> into) =>
        _equipped?.CollectAttachedChildren(parent.Id, into);
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

    private readonly WorldEntity?[] _attachmentClones =
        new WorldEntity?[CreatureAppraisalEntityBuilder.AttachmentRenderIds.Length];
    private readonly uint[] _attachmentSources =
        new uint[CreatureAppraisalEntityBuilder.AttachmentRenderIds.Length];
    private readonly uint[] _attachmentSourceGuids =
        new uint[CreatureAppraisalEntityBuilder.AttachmentRenderIds.Length];
    private readonly List<WorldEntity> _heldScratch = [];
    private readonly List<WorldEntity> _attachments = [];

    /// <summary>
    /// The real object a held-object copy stands in for, so values the
    /// object carries (how see-through it is) apply to its copy too.
    /// </summary>
    public bool TryGetHeldSource(uint copyServerGuid, out uint sourceServerGuid)
    {
        uint[] guids = CreatureAppraisalEntityBuilder.AttachmentServerGuids;
        for (int i = 0; i < guids.Length; i++)
        {
            if (guids[i] == copyServerGuid && _attachmentSourceGuids[i] != 0u)
            {
                sourceServerGuid = _attachmentSourceGuids[i];
                return true;
            }
        }
        sourceServerGuid = 0u;
        return false;
    }

    public IReadOnlyList<WorldEntity> SynchronizeAttachments(uint serverGuid)
    {
        _attachments.Clear();
        _heldScratch.Clear();
        if (_entities.TryGet(serverGuid, out WorldEntity source))
            _entities.CollectAttachments(source, _heldScratch);
        // A steady order keeps each held object in the same slot from frame
        // to frame, so its copy is reused rather than rebuilt.
        _heldScratch.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        int slot = 0;
        foreach (WorldEntity held in _heldScratch)
        {
            if (slot >= _attachmentClones.Length)
                break;
            if (held.MeshRefs.Count == 0)
                continue;

            WorldEntity? clone = _attachmentClones[slot];
            if (clone is null
                || _attachmentSources[slot] != held.Id
                || clone.SourceGfxObjOrSetupId != held.SourceGfxObjOrSetupId)
            {
                clone = CreatureAppraisalEntityBuilder.BuildAttachment(held, slot);
                _attachmentClones[slot] = clone;
                _attachmentSources[slot] = held.Id;
            }

            clone.ApplyAppearance(held.MeshRefs, held.PaletteOverride, held.PartOverrides);
            // Always drawn: in first person the held objects are hidden from
            // the world view, and the figure here is not in first person.
            clone.IsDrawVisible = true;
            clone.IsAncestorDrawVisible = true;
            _attachmentSourceGuids[slot] = held.ServerGuid;
            CreatureAppraisalEntityBuilder.PlaceAttachment(clone, held, source);
            _attachments.Add(clone);
            slot++;
        }

        for (int i = slot; i < _attachmentClones.Length; i++)
        {
            _attachmentClones[i] = null;
            _attachmentSources[i] = 0u;
            _attachmentSourceGuids[i] = 0u;
        }
        return _attachments;
    }
}

internal static class CreatureAppraisalEntityBuilder
{
    public const uint RenderId = 0xDA11_D022u;
    public const uint ServerGuid = 0xDA11_D021u;

    /// <summary>Render ids of the copies of what the creature holds, one per slot.</summary>
    public static readonly uint[] AttachmentRenderIds =
        [0xDA11_D023u, 0xDA11_D024u, 0xDA11_D025u, 0xDA11_D026u];

    public static readonly uint[] AttachmentServerGuids =
        [0xDA11_D027u, 0xDA11_D028u, 0xDA11_D029u, 0xDA11_D02Au];
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

    public static WorldEntity BuildAttachment(WorldEntity held, int slot)
    {
        ArgumentNullException.ThrowIfNull(held);
        var clone = new WorldEntity
        {
            Id = AttachmentRenderIds[slot],
            ServerGuid = AttachmentServerGuids[slot],
            SourceGfxObjOrSetupId = held.SourceGfxObjOrSetupId,
            Position = Vector3.Zero,
            Rotation = Heading,
            MeshRefs = held.MeshRefs,
            PaletteOverride = held.PaletteOverride,
            PartOverrides = held.PartOverrides,
            HiddenPartsMask = held.HiddenPartsMask,
            Scale = held.Scale,
            ParentCellId = null,
            EffectCellId = null,
        };
        if (held.HasLocalBounds)
            clone.SetLocalBounds(held.LocalBoundMin, held.LocalBoundMax);
        return clone;
    }

    /// <summary>
    /// Puts the copy of a held object where the object sits relative to the
    /// creature holding it, measured on the creature and carried over to the
    /// creature's copy (at the origin, turned to face the viewer).
    /// </summary>
    public static void PlaceAttachment(WorldEntity clone, WorldEntity held, WorldEntity holder)
    {
        Quaternion holderInverse = Quaternion.Inverse(holder.Rotation);
        Vector3 offset = Vector3.Transform(held.Position - holder.Position, holderInverse);
        Quaternion turn = Quaternion.Concatenate(held.Rotation, holderInverse);
        clone.SetPosition(Vector3.Transform(offset, Heading));
        clone.Rotation = Quaternion.Normalize(Quaternion.Concatenate(turn, Heading));
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
            "creature examination",
            attachmentRenderIds: CreatureAppraisalEntityBuilder.AttachmentRenderIds);
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

    public void SetAttachments(IReadOnlyList<WorldEntity> attachments) =>
        _renderer.SetAttachments(attachments);

    public uint Render(int width, int height) =>
        _renderer.Render(width, height);

    public void Dispose() => _renderer.Dispose();
}
