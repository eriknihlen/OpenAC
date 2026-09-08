using AcDream.App.Rendering;
using AcDream.Content;
using AcDream.Core.Items;
using DatReaderWriter.DBObjs;

namespace AcDream.App.UI;

public interface IMarkupIconResolver
{
    (uint tex, int w, int h) ResolveDid(uint did);

    (uint tex, int w, int h) ResolveSpell(uint spellId);

    (uint tex, int w, int h) ResolveItem(uint objectId);
}

public sealed class RetailMarkupIconResolver : IMarkupIconResolver
{
    private readonly IDatReaderWriter _dats;
    private readonly IconComposer _icons;
    private readonly ClientObjectTable _objects;

    private const int MaxCachedMisses = 256;

    private readonly Dictionary<uint, (uint tex, int w, int h)> _resolvedDidCache = new();

    private readonly Queue<uint> _missOrder = new();

    public RetailMarkupIconResolver(
        IDatReaderWriter dats,
        IconComposer icons,
        ClientObjectTable objects)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
    }

    public (uint tex, int w, int h) ResolveDid(uint did)
    {
        if (did == 0u)
            return (0u, 0, 0);
        if (_resolvedDidCache.TryGetValue(did, out (uint tex, int w, int h) cached))
            return cached;

        (uint tex, int w, int h) result;
        bool isMiss;
        if (!_dats.Portal.TryGet<RenderSurface>(did, out _)
            && !_dats.HighRes.TryGet<RenderSurface>(did, out _))
        {
            result = (0u, 0, 0);
            isMiss = true;
        }
        else
        {
            result = _icons.GetKeyedIcon(did);
            isMiss = result.tex == 0u;
        }

        _resolvedDidCache[did] = result;
        if (isMiss)
        {
            _missOrder.Enqueue(did);
            if (_missOrder.Count > MaxCachedMisses)
                _resolvedDidCache.Remove(_missOrder.Dequeue());
        }
        return result;
    }

    public (uint tex, int w, int h) ResolveSpell(uint spellId)
    {
        if (spellId == 0u) return (0u, 0, 0);
        uint tex = _icons.GetSpellIcon(spellId);
        return tex == 0u ? (0u, 0, 0) : (tex, 32, 32);
    }

    public (uint tex, int w, int h) ResolveItem(uint objectId)
    {
        if (objectId == 0u) return (0u, 0, 0);
        ClientObject? item = _objects.Get(objectId);
        if (item is null || item.IconId == 0u) return (0u, 0, 0);
        uint tex = _icons.GetIcon(
            item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects);
        return tex == 0u ? (0u, 0, 0) : (tex, 32, 32);
    }
}
