using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.Core.Items;
using AcDream.Core.Textures;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.App.UI;

/// <summary>
/// The interface's texture services as one plugin image table sees them.
/// Client art goes through the shared render-surface cache, so a surface
/// two plugins ask for is one upload; spell and object icons go through
/// the same composer the spell bar and the inventory draw from, so a
/// plugin's icon is the client's icon. Only the plugin's own art is
/// uploaded as releasable and given back when the plugin is done.
/// </summary>
internal sealed class RetailPluginImageBackend : IPluginImageBackend
{
    private readonly IDatReaderWriter _dats;
    private readonly object _datLock;
    private readonly TextureCache _textures;
    private readonly IconComposer _icons;
    private readonly ClientObjectTable _objects;

    internal RetailPluginImageBackend(
        IDatReaderWriter dats,
        object datLock,
        TextureCache textures,
        IconComposer icons,
        ClientObjectTable objects)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
    }

    public bool TryGetClientArt(uint surfaceId, out uint texture, out int width, out int height)
    {
        lock (_datLock)
        {
            // Checked first so a surface the client does not have is a
            // refusal rather than an upload of the placeholder.
            if (!_dats.Portal.TryGet<RenderSurface>(surfaceId, out _)
                && !_dats.HighRes.TryGet<RenderSurface>(surfaceId, out _))
            {
                texture = 0u;
                width = 0;
                height = 0;
                return false;
            }
            texture = _textures.GetOrUploadRenderSurface(surfaceId, out width, out height);
        }
        return texture != 0u;
    }

    public bool TryGetSpellIcon(uint spellId, out uint texture, out int width, out int height)
    {
        lock (_datLock)
            texture = _icons.GetSpellIcon(spellId);
        width = texture == 0u ? 0 : IconComposer.IconExtent;
        height = width;
        return texture != 0u;
    }

    public bool TryGetObjectIcon(uint objectId, out uint texture, out int width, out int height)
    {
        ClientObject? item = _objects.Get(objectId);
        if (item is null || item.IconId == 0u)
        {
            texture = 0u;
            width = 0;
            height = 0;
            return false;
        }
        lock (_datLock)
        {
            texture = _icons.GetIcon(
                item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects);
        }
        width = texture == 0u ? 0 : IconComposer.IconExtent;
        height = width;
        return texture != 0u;
    }

    public uint UploadOwned(byte[] rgba, int width, int height, string debugName) =>
        _textures.UploadReleasableRgba8(rgba, width, height, debugName);

    public uint UploadOwned(IReadOnlyList<DecodedTexture> mipLevels, string debugName) =>
        _textures.UploadReleasableRgba8(mipLevels, debugName);

    public bool ReleaseOwned(uint texture) => _textures.ReleaseUiTexture(texture);
}
