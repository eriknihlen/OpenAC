using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Player;
using AcDream.Core.World;
using DatReaderWriter;

namespace AcDream.App.Rendering;

internal interface IPaperdollDollRenderer
{
    void SetHeritage(uint heritageId);

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
    bool TryBuild(uint heritageId, out WorldEntity? doll);
}

/// <summary>The heritage the doll is posed and framed for.</summary>
internal interface IPaperdollHeritageSource
{
    uint HeritageGroup { get; }
}

internal interface IPaperdollEntityLookup
{
    /// <summary>
    /// The character as the world has it, together with the uniform scale its
    /// body wears. The scale is not on the projection itself, so it is read
    /// here and carried with the character rather than guessed downstream.
    /// </summary>
    bool TryGet(uint serverGuid, out WorldEntity player, out float objectScale);
}

internal interface IPaperdollPoseApplicator
{
    void Apply(WorldEntity doll, uint setupId, uint heritageId, float objectScale);
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
    private readonly IPaperdollHeritageSource _heritage;
    private WorldEntity? _doll;
    private bool _dirty = true;
    private uint _appliedHeritage;

    public PaperdollFramePresenter(
        IPaperdollDollRenderer renderer,
        IPaperdollFrameView view,
        IPaperdollDollFactory factory,
        IPaperdollHeritageSource heritage)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _heritage = heritage ?? throw new ArgumentNullException(nameof(heritage));
    }

    internal bool IsDirty => _dirty;

    public void MarkDirty() => _dirty = true;

    public void PrepareResources()
    {
        // The heritage arrives with the character description, which can land
        // after the doll is first built, so a change has to redress it: the
        // pose animation and the camera distance are both chosen by heritage.
        uint heritage = _heritage.HeritageGroup;
        if (heritage != _appliedHeritage)
        {
            _appliedHeritage = heritage;
            _renderer.SetHeritage(heritage);
            _dirty = true;
        }

        if (_dirty)
        {
            if (_factory.TryBuild(heritage, out WorldEntity? doll))
            {
                _renderer.SetDoll(doll);
                _doll = doll;
                _dirty = false;
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
        _renderer.SetHeritage(0u);
        _view.ClearTextureHandle();
        _doll = null;
        _dirty = true;
        _appliedHeritage = 0u;
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

    public bool TryGet(uint serverGuid, out WorldEntity player, out float objectScale)
    {
        if (_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
            && record.WorldEntity is { } found)
        {
            player = found;
            objectScale = LiveEntityObjectScale.Resolve(record, found);
            return true;
        }

        player = null!;
        objectScale = 1f;
        return false;
    }
}

/// <summary>
/// Reads the heritage the local character was made with. It comes in on the
/// character description, so it is absent until that lands.
/// </summary>
internal sealed class LivePaperdollHeritageSource : IPaperdollHeritageSource
{
    private const uint HeritageGroupPropertyId = 0xBCu;

    private readonly ClientObjectTable _objects;
    private readonly LocalPlayerState _localPlayer;
    private readonly ILocalPlayerIdentitySource _identity;

    public LivePaperdollHeritageSource(
        ClientObjectTable objects,
        LocalPlayerState localPlayer,
        ILocalPlayerIdentitySource identity)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _localPlayer = localPlayer ?? throw new ArgumentNullException(nameof(localPlayer));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public uint HeritageGroup
    {
        get
        {
            // The description writes the same properties to the player's object
            // and to the local-player state, but the object can exist first with
            // nothing on it, so an absent value there is not an answer.
            uint guid = _identity.ServerGuid;
            int heritage = guid != 0u && _objects.Get(guid) is { } player
                ? player.Properties.GetInt(HeritageGroupPropertyId)
                : 0;
            if (heritage <= 0)
                heritage = _localPlayer.Properties.GetInt(HeritageGroupPropertyId);
            return heritage > 0 ? (uint)heritage : 0u;
        }
    }
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

    public bool TryBuild(uint heritageId, out WorldEntity? doll)
    {
        doll = null;
        if (!_entities.TryGet(
                _identity.ServerGuid,
                out WorldEntity player,
                out float objectScale)
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
            partOverrides,
            objectScale);
        _pose.Apply(doll, player.SourceGfxObjOrSetupId, heritageId, objectScale);
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

    public void Apply(WorldEntity doll, uint setupId, uint heritageId, float objectScale)
    {
        DatReaderWriter.DBObjs.Animation? animation;
        DatReaderWriter.DBObjs.Setup? setup;
        lock (_datLock)
        {
            uint poseDid = RetailHeldPose.ResolvePoseDid(
                _dats,
                PaperdollHeritagePresentation.ResolvePoseEnum(heritageId));
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

            // The pose replaces every part transform outright, so the scale the
            // body wears has to be re-applied here: without it the doll is
            // drawn at the size of a default body no matter whose it is.
            Matrix4x4 transform = RetailHeldPose.ComposePartTransform(
                scale,
                origin,
                orientation,
                objectScale);
            MeshRef source = doll.MeshRefs[index];
            reposed.Add(new MeshRef(source.GfxObjId, transform)
            {
                SurfaceOverrides = source.SurfaceOverrides,
            });
        }

        doll.MeshRefs = reposed;
    }
}
