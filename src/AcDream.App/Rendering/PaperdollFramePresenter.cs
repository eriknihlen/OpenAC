using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter;

namespace AcDream.App.Rendering;

internal interface IPaperdollDollRenderer
{
    void SetDoll(WorldEntity? doll);

    void Prepare();

    uint Render(int width, int height);
}

internal interface IPaperdollFrameView
{
    bool TryGetVisibleSize(out int width, out int height);

    void SetTextureHandle(uint textureHandle);

    void ClearTextureHandle();
}

internal interface IPaperdollInventoryVisibility
{
    bool IsVisible { get; }
}

internal interface IPaperdollDollFactory
{
    bool TryBuild(out WorldEntity? doll);
}

internal interface IPaperdollEntityLookup
{
    bool TryGet(uint serverGuid, out WorldEntity player);
}

internal interface IPaperdollPoseApplicator
{
    void Apply(WorldEntity doll, uint setupId);
}

/// <summary>
/// Owns paperdoll dirty/rebuild state and the private render-target
/// presentation edge. The renderer remains a borrowed resource disposed by
/// the existing window shutdown transaction.
/// </summary>
internal sealed class PaperdollFramePresenter :
    IPrivateEntityViewportFrame,
    IPrivateEntityViewportResourcePreparation
{
    private readonly IPaperdollDollRenderer _renderer;
    private readonly IPaperdollFrameView _view;
    private readonly IPaperdollDollFactory _factory;
    private WorldEntity? _doll;
    private bool _dirty = true;

    public PaperdollFramePresenter(
        IPaperdollDollRenderer renderer,
        IPaperdollFrameView view,
        IPaperdollDollFactory factory)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    internal bool IsDirty => _dirty;

    public void MarkDirty() => _dirty = true;

    public void PrepareResources()
    {
        if (_dirty)
        {
            if (_factory.TryBuild(out WorldEntity? doll))
            {
                _renderer.SetDoll(doll);
                _doll = doll;
                _dirty = false;
            }
            else
            {
            }
        }

        _renderer.Prepare();
    }

    public void Render()
    {
        if (!_view.TryGetVisibleSize(out int width, out int height))
            return;

        uint textureHandle = _renderer.Render(width, height);
        if (textureHandle != 0u)
            _view.SetTextureHandle(textureHandle);
    }

    public void ResetSession()
    {
        _renderer.SetDoll(null);
        _view.ClearTextureHandle();
        _doll = null;
        _dirty = true;
    }
}

/// <summary>Retained-UI visibility and texture publication for the doll view.</summary>
internal sealed class RetailPaperdollFrameView : IPaperdollFrameView
{
    private readonly UiViewport _viewport;
    private readonly IPaperdollInventoryVisibility _inventory;

    public RetailPaperdollFrameView(
        UiViewport viewport,
        IPaperdollInventoryVisibility inventory)
    {
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
    }

    public bool TryGetVisibleSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!_viewport.Visible || !_inventory.IsVisible)
        {
            return false;
        }

        width = (int)_viewport.Width;
        height = (int)_viewport.Height;
        return true;
    }

    public void SetTextureHandle(uint textureHandle) =>
        _viewport.TextureSlot = UiTextureTableHandle.ToSlot(textureHandle);

    public void ClearTextureHandle() =>
        _viewport.TextureSlot = GpuTextureSlot.Unassigned;
}

/// <summary>Narrow visibility adapter for the paperdoll's inventory host.</summary>
internal sealed class PaperdollInventoryVisibility : IPaperdollInventoryVisibility
{
    private readonly UiElement _inventoryFrame;

    public PaperdollInventoryVisibility(UiElement inventoryFrame)
    {
        _inventoryFrame = inventoryFrame
            ?? throw new ArgumentNullException(nameof(inventoryFrame));
    }

    public bool IsVisible => _inventoryFrame.Visible;
}

internal sealed class LivePaperdollEntityLookup : IPaperdollEntityLookup
{
    private readonly LiveEntityRuntime _liveEntities;

    public LivePaperdollEntityLookup(LiveEntityRuntime liveEntities)
    {
        _liveEntities = liveEntities
            ?? throw new ArgumentNullException(nameof(liveEntities));
    }

    public bool TryGet(uint serverGuid, out WorldEntity player) =>
        _liveEntities.TryGetWorldEntity(serverGuid, out player);
}

internal sealed class RetailPaperdollDollFactory : IPaperdollDollFactory
{
    private readonly IPaperdollEntityLookup _entities;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly IPaperdollPoseApplicator _pose;

    public RetailPaperdollDollFactory(
        IPaperdollEntityLookup entities,
        ILocalPlayerIdentitySource identity,
        IPaperdollPoseApplicator pose)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _pose = pose ?? throw new ArgumentNullException(nameof(pose));
    }

    public bool TryBuild(out WorldEntity? doll)
    {
        doll = null;
        if (!_entities.TryGet(_identity.ServerGuid, out WorldEntity player)
            || player.MeshRefs.Count == 0)
        {
            return false;
        }

        uint? basePalette = null;
        List<(uint, byte, byte)>? subPalettes = null;
        if (player.PaletteOverride is { } palette)
        {
            basePalette = palette.BasePaletteId;
            subPalettes = new List<(uint, byte, byte)>();
            foreach (var range in palette.SubPalettes)
            {
                subPalettes.Add((
                    range.SubPaletteId,
                    range.Offset,
                    range.Length));
            }
        }

        List<(byte, uint)>? partOverrides = null;
        if (player.PartOverrides.Count > 0)
        {
            partOverrides = new List<(byte, uint)>(player.PartOverrides.Count);
            foreach (var part in player.PartOverrides)
                partOverrides.Add((part.PartIndex, part.GfxObjId));
        }

        doll = DollEntityBuilder.Build(
            player.SourceGfxObjOrSetupId,
            new List<MeshRef>(player.MeshRefs),
            basePalette,
            subPalettes,
            partOverrides);
        _pose.Apply(doll, player.SourceGfxObjOrSetupId);
        return true;
    }
}

internal sealed class RetailPaperdollPoseApplicator : IPaperdollPoseApplicator
{
    private readonly IDatReaderWriter _dats;
    private readonly IAnimationLoader _animations;
    private readonly object _datLock;

    public RetailPaperdollPoseApplicator(
        IDatReaderWriter dats,
        IAnimationLoader animations,
        object datLock)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
    }

    private uint ResolvePoseDid() => RetailHeldPose.ResolvePoseDid(_dats, 0x10000005u);

    public void Apply(WorldEntity doll, uint setupId)
    {
        DatReaderWriter.DBObjs.Animation? animation;
        DatReaderWriter.DBObjs.Setup? setup;
        lock (_datLock)
        {
            uint poseDid = ResolvePoseDid();
            if ((poseDid >> 24) != 0x03u)
                return;

            animation = _animations.LoadAnimation(poseDid);
            setup = _dats.Get<DatReaderWriter.DBObjs.Setup>(setupId);
        }
        if (animation is null || setup is null || animation.PartFrames.Count == 0)
            return;

        var frame = animation.PartFrames[^1];
        var reposed = new List<MeshRef>(doll.MeshRefs.Count);
        for (int index = 0; index < doll.MeshRefs.Count; index++)
        {
            Vector3 scale = index < setup.DefaultScale.Count
                ? setup.DefaultScale[index]
                : Vector3.One;
            Vector3 origin = Vector3.Zero;
            Quaternion orientation = Quaternion.Identity;
            if (index < frame.Frames.Count)
            {
                origin = frame.Frames[index].Origin;
                orientation = frame.Frames[index].Orientation;
            }

            Matrix4x4 transform = RetailHeldPose.ComposePartTransform(scale, origin, orientation);
            MeshRef source = doll.MeshRefs[index];
            reposed.Add(new MeshRef(source.GfxObjId, transform)
            {
                SurfaceOverrides = source.SurfaceOverrides,
            });
        }

        doll.MeshRefs = reposed;
    }
}
